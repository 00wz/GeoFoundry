using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.TileIO.Net;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.TileIO.Sources
{
    /// <summary>
    /// <see cref="IVectorTileSource"/> over HTTP using <see cref="TileDownloader"/>.
    /// Takes a URL template with the <c>{z}/{x}/{y}</c> placeholders.
    /// </summary>
    public sealed class HttpVectorTileSource : IVectorTileSource, IDisposable
    {
        private readonly string _urlTemplate;
        private readonly TileDownloader _downloader;
        private readonly bool _ownsDownloader;
        private bool _disposed;

        /// <summary>Creates a source with its own <see cref="TileDownloader"/>, disposed together with the source.</summary>
        public HttpVectorTileSource(string urlTemplate, int maxConcurrent = 6)
        {
            if (string.IsNullOrEmpty(urlTemplate))
                throw new ArgumentException("URL template must not be empty.", nameof(urlTemplate));
            _urlTemplate    = urlTemplate;
            _downloader     = new TileDownloader(maxConcurrent);
            _ownsDownloader = true;
        }

        /// <summary>Creates a source that uses an external <see cref="TileDownloader"/> without owning it.</summary>
        public HttpVectorTileSource(string urlTemplate, TileDownloader downloader)
        {
            if (string.IsNullOrEmpty(urlTemplate))
                throw new ArgumentException("URL template must not be empty.", nameof(urlTemplate));
            _urlTemplate    = urlTemplate;
            _downloader     = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _ownsDownloader = false;
        }

        public Awaitable<NativeArray<byte>> GetTileAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpVectorTileSource));
            return _downloader.GetBytesAsync(ResolveUrl(coord), allocator, ct);
        }

        private string ResolveUrl(TileCoord coord) =>
            _urlTemplate
                .Replace("{z}", coord.Z.ToString())
                .Replace("{x}", coord.X.ToString())
                .Replace("{y}", coord.Y.ToString());

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsDownloader) _downloader.Dispose();
        }
    }
}
