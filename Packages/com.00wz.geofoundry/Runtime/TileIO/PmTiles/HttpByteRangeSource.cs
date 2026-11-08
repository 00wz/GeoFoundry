using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace GeoFoundry.TileIO.PmTiles
{
    /// <summary>Strict HTTP byte ranges: requires 206 and identity encoding. Never accepts a whole-file 200.</summary>
    /// <remarks>WebGL uses a small JS fetch adapter to reject 200 at headers, before Unity's loader can allocate
    /// the complete archive. Other platforms use UnityWebRequest. Main-thread API; dispose cancels active reads.</remarks>
    public sealed class HttpByteRangeSource : IByteRangeSource
    {
        readonly string _url;
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        bool _disposed;
        public long Length { get; private set; } = -1;

        public HttpByteRangeSource(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("An absolute HTTP(S) archive URL is required.", nameof(url));
            _url = url;
        }

        public async Awaitable<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpByteRangeSource));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            ct = linked.Token;
            ct.ThrowIfCancellationRequested();
            if (offset < 0 || length < 0 || length > 16 * 1024 * 1024 || offset > 9007199254740991L - length)
                throw new PmTilesException("Invalid HTTP range (maximum 16 MiB, offsets must be exact JavaScript integers).");
            if (length == 0) return Array.Empty<byte>();
#if UNITY_WEBGL && !UNITY_EDITOR
            int id = GF_PM_Begin(_url, offset.ToString(CultureInfo.InvariantCulture), length);
            try
            {
                while (GF_PM_State(id) == 0) await Awaitable.NextFrameAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (GF_PM_State(id) != 1)
                {
                    var error = new byte[1024];
                    int count = GF_PM_Error(id, error, error.Length);
                    throw new PmTilesException("PMTiles HTTP range failed: " + Encoding.UTF8.GetString(error, 0, count));
                }
                SetLength((long)GF_PM_Length(id));
                var bytes = new byte[length];
                GF_PM_Copy(id, bytes, length);
                ct.ThrowIfCancellationRequested();
                return bytes;
            }
            finally { GF_PM_Release(id); }
#else
            using var handler = new RangeDownload(length);
            using var request = new UnityWebRequest(_url, "GET", handler, null);
            request.disposeDownloadHandlerOnDispose = false;
            request.SetRequestHeader("Range", $"bytes={offset}-{offset + length - 1}");
            request.SetRequestHeader("Accept-Encoding", "identity");
            request.timeout = 30;
            await PmTilesByteSources.SendAsync(request, ct);
            ct.ThrowIfCancellationRequested();
            if (request.responseCode != 206 || request.result != UnityWebRequest.Result.Success)
                throw new PmTilesException($"PMTiles server must support HTTP Range (206); received {request.responseCode}: {request.error}. Use the supplied range server for local Web builds.");
            if (!string.IsNullOrEmpty(request.GetResponseHeader("Content-Encoding")) && request.GetResponseHeader("Content-Encoding") != "identity")
                throw new PmTilesException("PMTiles HTTP response must not use Content-Encoding.");
            string contentRange = request.GetResponseHeader("Content-Range");
            string prefix = $"bytes {offset}-{offset + length - 1}/";
            if (contentRange == null || !contentRange.StartsWith(prefix, StringComparison.Ordinal) ||
                !long.TryParse(contentRange.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long total) ||
                total < offset + length)
                throw new PmTilesException("Invalid or inaccessible PMTiles Content-Range header.");
            SetLength(total);
            if (handler.Count != length) throw new PmTilesException("Truncated PMTiles HTTP range.");
            return handler.Bytes;
#endif
        }

        void SetLength(long length)
        {
            if (length < 0 || (Length >= 0 && Length != length))
                throw new PmTilesException("PMTiles archive length changed during reading; reopen the source.");
            Length = length;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int GF_PM_Begin(string url, string offset, int length);
        [DllImport("__Internal")] static extern int GF_PM_State(int id);
        [DllImport("__Internal")] static extern double GF_PM_Length(int id);
        [DllImport("__Internal")] static extern int GF_PM_Error(int id, [Out] byte[] bytes, int capacity);
        [DllImport("__Internal")] static extern void GF_PM_Copy(int id, [Out] byte[] bytes, int length);
        [DllImport("__Internal")] static extern void GF_PM_Release(int id);
#else
        sealed class RangeDownload : DownloadHandlerScript
        {
            public byte[] Bytes { get; }
            public int Count { get; private set; }
            public RangeDownload(int size) : base(new byte[16384]) { Bytes = new byte[size]; }
            protected override bool ReceiveData(byte[] data, int length)
            {
                if (length > Bytes.Length - Count) return false;
                Buffer.BlockCopy(data, 0, Bytes, Count, length); Count += length; return true;
            }
        }
#endif
    }
}
