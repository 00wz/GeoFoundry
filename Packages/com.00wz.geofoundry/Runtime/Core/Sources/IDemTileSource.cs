using System.Threading;
using GeoFoundry.Core.Coordinates;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Core.Sources
{
    /// <summary>
    /// Source of terrain elevation (DEM) tiles as float heightmaps. Hides the encoding (Terrarium PNG, raw float32,
    /// Mapbox RGB and so on) and the transport.
    /// </summary>
    public interface IDemTileSource
    {
        /// <summary>Gets the side of a tile heightmap in pixels, usually 256.</summary>
        int TileSizePixels { get; }

        /// <summary>
        /// Loads the heightmap of a tile. Called on the main thread.
        /// </summary>
        /// <param name="coord">The tile to load.</param>
        /// <param name="allocator">The allocator for the returned array.</param>
        /// <param name="ct">Cancels the load; the source then throws <see cref="System.OperationCanceledException"/>
        /// without leaking any allocation.</param>
        /// <returns>
        /// <c>TileSizePixels × TileSizePixels</c> heights in meters, row-major. The caller owns the array and must
        /// dispose it.
        /// </returns>
        /// <exception cref="TileDownloadException">The tile cannot be downloaded (retries exhausted or a
        /// non-retryable HTTP status).</exception>
        Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct);
    }
}
