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
        /// <exception cref="TileDownloadException">The tile cannot be downloaded: a 4xx or another non-retryable
        /// error, or retries are exhausted.</exception>
        Awaitable<NativeArray<byte>> GetTileAsync(TileCoord coord, Allocator allocator, CancellationToken ct);
    }
}
