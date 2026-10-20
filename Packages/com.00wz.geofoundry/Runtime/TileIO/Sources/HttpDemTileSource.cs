using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.TileIO.Dem;
using GeoFoundry.TileIO.Net;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.TileIO.Sources
{
    /// <summary>
    /// <see cref="IDemTileSource"/> that downloads Terrarium PNG tiles with <see cref="TileDownloader"/> and decodes
    /// them with <see cref="TerrariumDecoder"/>. The URL template uses the <c>{z}/{x}/{y}</c> placeholders.
    /// </summary>
    public sealed class HttpDemTileSource : IDemTileSource, IDisposable
    {
        private readonly string _urlTemplate;
        private readonly TileDownloader _downloader;
        private readonly bool _ownsDownloader;
        private bool _disposed;

        public int TileSizePixels { get; }

        /// <summary>
        /// Creates a source with its own <see cref="TileDownloader"/>, disposed together with the source.
        /// </summary>
        /// <param name="urlTemplate">URL template with <c>{z}</c>, <c>{x}</c>, <c>{y}</c> placeholders.</param>
        /// <param name="maxConcurrent">Maximum number of concurrent requests of the own downloader.</param>
        /// <param name="tileSizePixels">
        /// DEM tile side in pixels; defaults to <see cref="TerrariumDecoder.StandardTileSize"/> (256).
        /// Must match exactly what the server returns, otherwise <c>PaddedHeightSampler</c> reads the DEM with a
        /// wrong stride.
        /// </param>
        public HttpDemTileSource(string urlTemplate, int maxConcurrent = 6,
                                 int tileSizePixels = TerrariumDecoder.StandardTileSize)
        {
            if (string.IsNullOrEmpty(urlTemplate))
                throw new ArgumentException("URL template must not be empty.", nameof(urlTemplate));
            if (tileSizePixels < 2)
                throw new ArgumentOutOfRangeException(nameof(tileSizePixels), tileSizePixels, "tileSizePixels >= 2.");
            _urlTemplate    = urlTemplate;
            _downloader     = new TileDownloader(maxConcurrent);
            _ownsDownloader = true;
            TileSizePixels  = tileSizePixels;
        }

        /// <summary>
        /// Creates a source that uses an external <see cref="TileDownloader"/> without owning it.
        /// </summary>
        public HttpDemTileSource(string urlTemplate, TileDownloader downloader,
                                 int tileSizePixels = TerrariumDecoder.StandardTileSize)
        {
            if (string.IsNullOrEmpty(urlTemplate))
                throw new ArgumentException("URL template must not be empty.", nameof(urlTemplate));
            if (tileSizePixels < 2)
                throw new ArgumentOutOfRangeException(nameof(tileSizePixels), tileSizePixels, "tileSizePixels >= 2.");
            _urlTemplate    = urlTemplate;
            _downloader     = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _ownsDownloader = false;
            TileSizePixels  = tileSizePixels;
        }

        public async Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpDemTileSource));
            var tex = await _downloader.GetTextureAsync(ResolveUrl(coord), nonReadable: false, ct);
            try
            {
                // A texture received by the time of cancellation is not decoded, and does not leak either (see finally).
                ct.ThrowIfCancellationRequested();
                return TerrariumDecoder.Decode(tex, allocator);
            }
            finally
            {
                if (tex != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
                    else UnityEngine.Object.DestroyImmediate(tex);
                }
            }
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
