using System;
using System.Threading;
using UnityEngine;

namespace GeoFoundry.TileIO.PmTiles
{
    /// <summary>Exact asynchronous byte ranges. Main-thread API; supports overlapping calls and cancellation.</summary>
    public interface IByteRangeSource : IDisposable
    {
        /// <summary>Archive length, or -1 until the first successful read establishes it.</summary>
        long Length { get; }

        /// <summary>Returns exactly length bytes or throws; returned managed buffer belongs to the caller.</summary>
        Awaitable<byte[]> ReadAsync(long offset, int length, CancellationToken ct);
    }
}
