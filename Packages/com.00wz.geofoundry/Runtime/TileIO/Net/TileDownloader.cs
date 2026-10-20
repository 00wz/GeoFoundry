using System;
using System.Threading;
using GeoFoundry.Core.Sources;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace GeoFoundry.TileIO.Net
{
    /// <summary>
    /// Low-level tile downloader built on <see cref="UnityWebRequest"/>.
    ///
    /// - Concurrency is limited by an own slot counter polled with <see cref="Awaitable.NextFrameAsync()"/>
    ///   (see <see cref="AcquireSlotAsync"/>), not by <see cref="SemaphoreSlim"/>. Browsers usually allow
    ///   6 concurrent connections per host (HTTP/1.1), so more slots only add waiting.
    ///   <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> wakes waiters through a thread-pool continuation
    ///   on <c>Release()</c>, and the single-threaded WebGL/IL2CPP runtime does not pump that thread pool, so
    ///   queued callers would never wake up.
    /// - Network errors and HTTP 5xx are retried with exponential backoff; 4xx (404, etc.) throws immediately.
    ///   A slot is held for one attempt, not the whole series: it is free during backoff and the retry queues
    ///   again, so a failing URL does not hold a slot idle, and a server that is down does not stall the queue.
    /// - Cancellation: the downloader owns its requests; both cancellation of the caller's token and
    ///   <see cref="Dispose"/> abort them. The wait wakes synchronously inside <c>Cancel()</c>/<c>Dispose()</c>:
    ///   the request is aborted and released right there, the slot is freed, and the result's Unity objects are
    ///   not touched after cancellation (see <see cref="SendAsync"/>).
    ///
    /// Safe for concurrent use from several async chains, but not thread-safe outside the Unity player loop.
    /// </summary>
    public sealed class TileDownloader : IDisposable
    {
        private const int DefaultMaxConcurrent = 6;
        private const int DefaultTimeoutSeconds = 30;
        private const int DefaultMaxRetries = 3;
        private const int InitialBackoffMs = 300;

        private readonly int _maxConcurrent;
        private readonly int _maxRetries;
        private readonly int _timeoutSeconds;
        private int _activeCount;
        private bool _disposed;

        /// <summary>
        /// Aborts all of the downloader's requests on <see cref="Dispose"/>. Never disposed itself: operation
        /// tokens linked to it unwind right inside <c>Cancel()</c>, and a late call gets
        /// <see cref="ObjectDisposedException"/> from <see cref="ThrowIfDisposed"/>.
        /// </summary>
        private readonly CancellationTokenSource _disposeCts = new();

        /// <summary>Gets the total number of bytes downloaded over the instance's lifetime.</summary>
        public long TotalBytesDownloaded { get; private set; }

        /// <summary>Gets the number of successfully completed requests.</summary>
        public int TotalRequestsCompleted { get; private set; }

        /// <summary>Creates a downloader.</summary>
        /// <param name="maxConcurrent">Maximum number of concurrent requests; values below 1 are treated as 1.</param>
        /// <param name="maxRetries">Maximum number of retries for network errors and HTTP 5xx.</param>
        /// <param name="timeoutSeconds">Timeout of a single request attempt, in seconds.</param>
        public TileDownloader(int maxConcurrent = DefaultMaxConcurrent,
                              int maxRetries = DefaultMaxRetries,
                              int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (maxConcurrent < 1) maxConcurrent = 1;
            _maxConcurrent = maxConcurrent;
            _maxRetries = maxRetries;
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>
        /// Downloads a URL as binary data and copies it into a <see cref="NativeArray{T}"/> with the given allocator.
        /// The caller owns the returned array and must dispose it.
        /// </summary>
        /// <exception cref="TileDownloadException">The request failed with a non-retryable error or ran out of retries.</exception>
        public async Awaitable<NativeArray<byte>> GetBytesAsync(string url, Allocator allocator, CancellationToken ct)
        {
            ThrowIfDisposed();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            return await DownloadBytesWithRetryAsync(url, allocator, linked.Token);
        }

        /// <summary>
        /// Downloads a URL as PNG/JPG and returns the decoded <see cref="Texture2D"/>. On the Web, decoding uses the
        /// browser's native image decoder, which is much faster and lighter than LoadImage from managed bytes.
        /// The caller owns the returned texture and must destroy it.
        /// </summary>
        /// <exception cref="TileDownloadException">The request failed with a non-retryable error or ran out of retries.</exception>
        public async Awaitable<Texture2D> GetTextureAsync(string url, bool nonReadable, CancellationToken ct)
        {
            ThrowIfDisposed();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            return await DownloadTextureWithRetryAsync(url, nonReadable, linked.Token);
        }

        /// <summary>
        /// Waits for a free slot without blocking by polling every frame instead of using <see cref="SemaphoreSlim"/>
        /// (see the class remarks for why). Slots are not granted in FIFO order (all waiters recheck every frame),
        /// which does not matter for tile loading.
        /// </summary>
        private async Awaitable AcquireSlotAsync(CancellationToken ct)
        {
            while (_activeCount >= _maxConcurrent)
            {
                ct.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(ct);
            }
            _activeCount++;
        }

        private void ReleaseSlot() => _activeCount--;

        private async Awaitable<NativeArray<byte>> DownloadBytesWithRetryAsync(string url, Allocator allocator, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                // The request is created only once a slot is held, so queued waiters hold no native UnityWebRequest.
                await AcquireSlotAsync(ct);
                try
                {
                    using var request = UnityWebRequest.Get(url);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = _timeoutSeconds;

                    await SendAsync(request, ct);
                    ct.ThrowIfCancellationRequested();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        var bytes = request.downloadHandler.data ?? Array.Empty<byte>();
                        var native = new NativeArray<byte>(bytes.Length, allocator, NativeArrayOptions.UninitializedMemory);
                        NativeArray<byte>.Copy(bytes, native, bytes.Length);
                        TotalBytesDownloaded += bytes.Length;
                        TotalRequestsCompleted++;
                        return native;
                    }

                    if (!ShouldRetry(request, attempt))
                        throw new TileDownloadException(url, request.responseCode,
                            $"Failed to download {url}: HTTP {request.responseCode} — {request.error}");

                    attempt++;
                    Debug.LogWarning($"[GeoFoundry] Retry {attempt}/{_maxRetries} for {url}: {request.error}");
                }
                finally
                {
                    ReleaseSlot();
                }

                // Backoff happens outside the slot (see the class remarks).
                await DelayWithBackoffAsync(attempt, ct);
            }
        }

        private async Awaitable<Texture2D> DownloadTextureWithRetryAsync(string url, bool nonReadable, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                // The request is created only once a slot is held, so queued waiters hold no native UnityWebRequest.
                await AcquireSlotAsync(ct);
                try
                {
                    using var request = UnityWebRequestTexture.GetTexture(url, nonReadable);
                    request.timeout = _timeoutSeconds;

                    await SendAsync(request, ct);
                    // A cancelled request may have finished downloading; do not create the texture then: the
                    // DownloadHandler frees it with the request (see SendAsync for why this matters).
                    ct.ThrowIfCancellationRequested();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        var tex = DownloadHandlerTexture.GetContent(request);
                        TotalBytesDownloaded += request.downloadedBytes >= 0 ? (long)request.downloadedBytes : 0;
                        TotalRequestsCompleted++;
                        return tex;
                    }

                    if (!ShouldRetry(request, attempt))
                        throw new TileDownloadException(url, request.responseCode,
                            $"Failed to download {url}: HTTP {request.responseCode} — {request.error}");

                    attempt++;
                    Debug.LogWarning($"[GeoFoundry] Retry {attempt}/{_maxRetries} for {url}: {request.error}");
                }
                finally
                {
                    ReleaseSlot();
                }

                // Backoff happens outside the slot (see the class remarks).
                await DelayWithBackoffAsync(attempt, ct);
            }
        }

        /// <summary>
        /// Sends the request and waits for completion. Cancelling <paramref name="ct"/> wakes the wait synchronously,
        /// inside <c>Cancel()</c>: the request is aborted and the caller (<c>using var request</c>) releases it right
        /// there. Waiting for Unity to deliver completion of an aborted request is unsafe: on exiting Play Mode it
        /// does so in <c>PreloadManager::WaitForAllAsyncOperationsToComplete</c>, in the middle of scene teardown,
        /// and a continuation touching the result (<c>Texture.width</c>) crashes the editor natively. A late
        /// <c>completed</c> of a cancelled request touches only the completion source.
        /// </summary>
        private static async Awaitable SendAsync(UnityWebRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var completion = new AwaitableCompletionSource();
            request.SendWebRequest().completed += _ => completion.TrySetResult();

            using var registration = ct.Register(static state =>
            {
                var (req, source) = ((UnityWebRequest, AwaitableCompletionSource))state;
                req.Abort();
                source.TrySetCanceled();
            }, (request, completion));

            await completion.Awaitable;
        }

        private bool ShouldRetry(UnityWebRequest request, int attempt)
        {
            if (attempt >= _maxRetries) return false;
            if (request.result == UnityWebRequest.Result.ConnectionError) return true;
            if (request.result == UnityWebRequest.Result.ProtocolError)
            {
                // 5xx is a server error; retrying makes sense.
                return request.responseCode >= 500 && request.responseCode < 600;
            }
            return false;
        }

        private static async Awaitable DelayWithBackoffAsync(int attempt, CancellationToken ct)
        {
            float seconds = InitialBackoffMs * (float)System.Math.Pow(2, attempt - 1) / 1000f;
            await Awaitable.WaitForSecondsAsync(seconds, ct);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TileDownloader));
        }

        /// <summary>Aborts all of the downloader's in-flight requests synchronously; see <see cref="SendAsync"/>.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
        }
    }
}
