using System.Threading;
using GeoFoundry.Core.Coordinates;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Core.Sources
{
    /// <summary>
    /// Source of terrain elevation (DEM) tiles as float heightmaps. Hides the encoding (Terrarium PNG, raw float32,
    /// Mapbox RGB and so on) and the transport.
    ///
    /// Missing data is a regular result, not an error: sources often have no tiles where there is nothing to
    /// describe (oceans, areas outside the coverage). The source reports such a tile with an array that is not
    /// created rather than an exception, and the consumer uses the fallback height for it without logging. How
    /// missing data is recognized is the source's own business: an HTTP status, a missing file, a gap in an archive.
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
        /// dispose it. An array that is not created (<c>default</c>, <c>IsCreated == false</c>) if the source has no
        /// data for the tile (see the interface remarks).
        /// </returns>
        /// <exception cref="TileSourceException">The source cannot provide the tile (retries exhausted, a refused
        /// request, corrupt data); a source throws its own subclass with the details of its transport.</exception>
        Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct);
    }
}
