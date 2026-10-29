using System;
using System.Collections.Generic;
using System.Linq;
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
    ///
    /// Responses with a missing-data status (<see cref="DefaultNoDataStatuses"/> unless set otherwise) are tiles
    /// without data, as <see cref="IDemTileSource"/> defines them, rather than errors: DEM servers usually answer
    /// 404 or 204 for tiles over oceans.
    /// </summary>
    public sealed class HttpDemTileSource : IDemTileSource, IDisposable
    {
        /// <summary>The default missing-data statuses: 204 No Content and 404 Not Found.</summary>
        public static readonly IReadOnlyList<long> DefaultNoDataStatuses = new long[] { 204, 404 };

        private readonly string _urlTemplate;
        private readonly TileDownloader _downloader;
        private readonly bool _ownsDownloader;
        private readonly long[] _noDataStatuses;
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
        /// <param name="noDataStatuses">
        /// HTTP statuses that mean the server has no data for the tile; <c>null</c> means
        /// <see cref="DefaultNoDataStatuses"/>, an empty list makes every such status an error.
        /// </param>
        public HttpDemTileSource(string urlTemplate, int maxConcurrent = 6,
                                 int tileSizePixels = TerrariumDecoder.StandardTileSize,
                                 IReadOnlyList<long> noDataStatuses = null)
        {
            if (string.IsNullOrEmpty(urlTemplate))
                throw new ArgumentException("URL template must not be empty.", nameof(urlTemplate));
            if (tileSizePixels < 2)
                throw new ArgumentOutOfRangeException(nameof(tileSizePixels), tileSizePixels, "tileSizePixels >= 2.");
            _urlTemplate    = urlTemplate;
            _downloader     = new TileDownloader(maxConcurrent);
            _ownsDownloader = true;
            _noDataStatuses = (noDataStatuses ?? DefaultNoDataStatuses).ToArray();
            TileSizePixels  = tileSizePixels;
        }

        /// <summary>
        /// Creates a source that uses an external <see cref="TileDownloader"/> without owning it.
        /// </summary>
        public HttpDemTileSource(string urlTemplate, TileDownloader downloader,
                                 int tileSizePixels = TerrariumDecoder.StandardTileSize,
                                 IReadOnlyList<long> noDataStatuses = null)
        {
            if (string.IsNullOrEmpty(urlTemplate))
                throw new ArgumentException("URL template must not be empty.", nameof(urlTemplate));
            if (tileSizePixels < 2)
                throw new ArgumentOutOfRangeException(nameof(tileSizePixels), tileSizePixels, "tileSizePixels >= 2.");
            _urlTemplate    = urlTemplate;
            _downloader     = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _ownsDownloader = false;
            _noDataStatuses = (noDataStatuses ?? DefaultNoDataStatuses).ToArray();
            TileSizePixels  = tileSizePixels;
        }

        /// <summary>
        /// Downloads and decodes the tile. A missing-data status (see the class remarks) returns an array that is
        /// not created.
        /// </summary>
        public async Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpDemTileSource));
            var tex = await _downloader.GetTextureAsync(ResolveUrl(coord), nonReadable: false, _noDataStatuses, ct);
            if (tex == null) return default;
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
