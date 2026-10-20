using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Sources;
using GeoFoundry.TileIO.Net;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.TileIO
{
    /// <summary>
    /// Retries of <see cref="TileDownloader"/> against a local server (<see cref="LoopbackHttpServer"/>), without
    /// external network access: a slot is held for one attempt rather than the whole series, so during backoff it is
    /// free for other downloads. Otherwise every failing URL would waste a slot for the entire series.
    /// </summary>
    public sealed class TileDownloaderRetryTests
    {
        /// <summary>Waits by time rather than frames: a retry series with backoff takes seconds.</summary>
        private static IEnumerator WaitFor(Func<bool> condition, string what, float timeoutSeconds = 10f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(condition(), Is.True, $"timed out waiting for: {what}");
        }

        [UnityTest]
        public IEnumerator Retry_SlotIsFreeDuringBackoff_QueuedDownloadCompletesFirst()
        {
            const int maxRetries = 3;
            using var failing = new LoopbackHttpServer(Array.Empty<byte>(), statusCode: 503);
            using var healthy = new LoopbackHttpServer(new byte[] { 1, 2, 3 });
            using var downloader = new TileDownloader(maxConcurrent: 1, maxRetries: maxRetries);

            var retried = downloader.GetBytesAsync(failing.Url, Allocator.Persistent, CancellationToken.None);
            yield return WaitFor(() => failing.RequestCount == 1, "first attempt reaching the server");

            // The only slot is taken by the attempt, so the download is queued.
            var queued = downloader.GetBytesAsync(healthy.Url, Allocator.Persistent, CancellationToken.None);
            yield return WaitFor(() => queued.GetAwaiter().IsCompleted, "queued download");

            Assert.That(retried.GetAwaiter().IsCompleted, Is.False,
                "the queued download completed during backoff, not after the whole retry series");
            var bytes = queued.GetAwaiter().GetResult();
            try { Assert.That(bytes.Length, Is.EqualTo(3)); }
            finally { bytes.Dispose(); }

            yield return WaitFor(() => retried.GetAwaiter().IsCompleted, "end of the retry series");
            var ex = Assert.Catch<TileDownloadException>(() => retried.GetAwaiter().GetResult());
            Assert.That(ex.HttpStatus, Is.EqualTo(503));
            Assert.That(failing.RequestCount, Is.EqualTo(1 + maxRetries));
        }

        [UnityTest]
        public IEnumerator Retry_CancelledDuringBackoff_ThrowsCancellation_NoFurtherAttempts()
        {
            using var failing = new LoopbackHttpServer(Array.Empty<byte>(), statusCode: 503);
            using var downloader = new TileDownloader(maxConcurrent: 1, maxRetries: 3);
            using var cts = new CancellationTokenSource();

            var retried = downloader.GetBytesAsync(failing.Url, Allocator.Persistent, cts.Token);
            yield return WaitFor(() => failing.RequestCount == 1, "first attempt reaching the server");
            // The 503 response reaches the downloader within a couple of frames; after that it is most likely in
            // backoff (0.3 s). Cancelling mid-attempt is fine too: no further attempts must follow either way.
            for (int i = 0; i < 5; i++) yield return null;

            cts.Cancel();
            yield return WaitFor(() => retried.GetAwaiter().IsCompleted, "retry series cancellation");
            Assert.Catch<OperationCanceledException>(() => retried.GetAwaiter().GetResult());

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(failing.RequestCount, Is.EqualTo(1), "no further attempts after cancellation");
        }

        [UnityTest]
        public IEnumerator NotFound_404_ThrowsTileDownloadException_WithoutRetry()
        {
            using var missing = new LoopbackHttpServer(Array.Empty<byte>(), statusCode: 404);
            using var downloader = new TileDownloader(maxConcurrent: 1, maxRetries: 3);

            var download = downloader.GetBytesAsync(missing.Url, Allocator.Persistent, CancellationToken.None);
            yield return WaitFor(() => download.GetAwaiter().IsCompleted, "404 response");

            var ex = Assert.Catch<TileDownloadException>(() => download.GetAwaiter().GetResult());
            Assert.That(ex.HttpStatus, Is.EqualTo(404));
            Assert.That(missing.RequestCount, Is.EqualTo(1), "4xx is not retried");
        }
    }
}
