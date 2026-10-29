using System.Threading;
using GeoFoundry.Core.Coordinates;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Core.Sources
{
    /// <summary>
    /// Source of raw MVT tile bytes. Hides the transport: HTTP, S3, local storage, an in-memory mock for tests.
    /// </summary>
    public interface IVectorTileSource
    {
        /// <summary>
        /// Loads the bytes of a vector tile. Called on the main thread.
        /// </summary>
        /// <param name="coord">The tile to load.</param>
        /// <param name="allocator">The allocator for the returned array.</param>
        /// <param name="ct">Cancels the load; the source then throws <see cref="System.OperationCanceledException"/>
        /// without leaking any allocation.</param>
        /// <returns>The tile bytes, allocated with <paramref name="allocator"/>. The caller owns the array and must
        /// dispose it.</returns>
        /// <exception cref="TileSourceException">The source cannot provide the tile (retries exhausted, a refused
        /// request, corrupt data); a source throws its own subclass with the details of its transport.</exception>
        Awaitable<NativeArray<byte>> GetTileAsync(TileCoord coord, Allocator allocator, CancellationToken ct);
    }
}
