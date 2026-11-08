using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace GeoFoundry.TileIO.PmTiles
{
    /// <summary>Platform selection and Android StreamingAssets extraction. No user file picker.</summary>
    public static class PmTilesByteSources
    {
        /// <summary>Open an absolute filesystem path, HTTP(S) URL, or Android jar URL.</summary>
        public static IByteRangeSource Open(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("An archive path is required.", nameof(path));
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new HttpByteRangeSource(path);
            if (path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)) return new ExtractedSource(path);
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PmTilesException("WebGL PMTiles sources require an HTTP(S) URL and a server supporting Range (206).");
#else
            return new FileByteRangeSource(path);
#endif
        }

        /// <summary>Build a platform-specific path for a relative archive name inside StreamingAssets.</summary>
        public static string StreamingAsset(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath.StartsWith("/") || relativePath.Contains("..") || relativePath.Contains(":"))
                throw new ArgumentException("A relative StreamingAssets archive path is required.", nameof(relativePath));
            string relative = relativePath.Replace('\\', '/');
            string root = Application.streamingAssetsPath.TrimEnd('/', '\\');
            if (root.Contains("://"))
            {
                string[] parts = relative.Split('/');
                for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
                relative = string.Join("/", parts);
            }
            return root + "/" + relative;
        }

        internal static async Awaitable SendAsync(UnityWebRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var completion = new AwaitableCompletionSource();
            request.SendWebRequest().completed += _ => completion.TrySetResult();
            using var registration = ct.Register(() => { request.Abort(); completion.TrySetCanceled(); });
            await completion.Awaitable;
            ct.ThrowIfCancellationRequested();
        }

        // Extract once per source instance. A unique destination avoids stale caches and concurrent source races.
        // Files are deleted at Dispose; no full managed copy and no dependency on APK ZIP compression method.
        sealed class ExtractedSource : IByteRangeSource
        {
            readonly string _url;
            readonly string _path;
            readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
            FileByteRangeSource _file;
            bool _extracting, _disposed;
            int _activeReads;
            public long Length => _file?.Length ?? -1;
            public ExtractedSource(string url)
            {
                _url = url;
                _path = Path.Combine(Application.persistentDataPath, "geofoundry-" + Guid.NewGuid().ToString("N") + ".pmtiles");
            }
            public async Awaitable<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ExtractedSource));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                ct = linked.Token;
                while (_extracting) await Awaitable.NextFrameAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (_file == null)
                {
                    _extracting = true;
                    try
                    {
                        using (var request = new UnityWebRequest(_url, "GET"))
                        {
                            request.downloadHandler = new DownloadHandlerFile(_path) { removeFileOnAbort = true };
                            await SendAsync(request, ct);
                            if (request.result != UnityWebRequest.Result.Success)
                                throw new PmTilesException($"Cannot extract Android PMTiles StreamingAsset: {request.error}");
                        }
                        ct.ThrowIfCancellationRequested();
                        _file = new FileByteRangeSource(_path);
                    }
                    finally
                    {
                        _extracting = false;
                        if (_file == null) Delete();
                    }
                }
                _activeReads++;
                try { return await _file.ReadAsync(offset, length, ct); }
                finally { if (--_activeReads == 0 && _disposed) Delete(); }
            }
            void Delete() { if (File.Exists(_path)) File.Delete(_path); }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true; _lifetime.Cancel(); _file?.Dispose();
                if (!_extracting && _activeReads == 0) Delete();
            }
        }
    }
}
