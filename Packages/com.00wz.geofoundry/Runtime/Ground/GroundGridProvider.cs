using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using UnityEngine;

namespace GeoFoundry.Ground
{
    /// <summary>
    /// Source of per-tile <see cref="GroundGrid"/>s and the single place that decides what the ground under a tile
    /// is: DEM elevation (<see cref="DemGroundGridProvider"/>) or a plane (<see cref="FlatGroundGridProvider"/>).
    /// Consumers (<c>TileBuilder</c>, terrain and layer handlers, <see cref="GroundGridHeightProvider"/>) always get
    /// a valid grid from it and never check whether terrain elevation is enabled.
    ///
    /// The set of implementations is closed (the constructor is <c>internal</c>): grid building is internal to the
    /// package. Custom elevation is plugged in through a height source, <see cref="GeoFoundry.Core.Sources.IDemTileSource"/>.
    /// </summary>
    public abstract class GroundGridProvider : IDisposable
    {
        internal GroundGridProvider() { }

        /// <summary>
        /// Asynchronously acquires the grid of tile <paramref name="coord"/> and holds it until the returned handle is
        /// disposed (the caller must dispose it). Missing elevation data is not an error. Throws only on cancellation
        /// of <paramref name="ct"/>, after the provider's <see cref="IDisposable.Dispose"/> or on an unexpected
        /// internal error, and always when the result is awaited, never synchronously at the call.
        /// </summary>
        public abstract Awaitable<GroundGridHandle> AcquireAsync(TileCoord coord, CancellationToken ct);

        /// <summary>
        /// Gets an already available grid without holding it, for external queries such as camera height.
        /// Returns <c>false</c> if the tile's grid is not available right now.
        /// </summary>
        public abstract bool TryGet(TileCoord coord, out GroundGrid grid);

        /// <summary>
        /// Gets the memory the provider currently holds for grids (and their source data), for statistics.
        /// Not counted in the tile budget: grids are shared by neighboring tiles.
        /// </summary>
        public abstract long MemoryBytes { get; }

        /// <summary>Releases the provider; grids under live handles are freed by their last Dispose.</summary>
        public abstract void Dispose();
    }

    /// <summary>
    /// Owner of grids issued through <see cref="GroundGridHandle"/>; the handle notifies it on release.
    /// Not public: a grid can be released only through its handle.
    /// </summary>
    internal interface IGroundGridOwner
    {
        void Release(TileCoord coord);
    }

    /// <summary>
    /// Ref-counted handle to one tile's <see cref="GroundGrid"/>. <see cref="Dispose"/> decrements the ref count in
    /// the issuing <see cref="GroundGridProvider"/>, which frees the native arrays once all holders have disposed.
    /// </summary>
    public readonly struct GroundGridHandle : IDisposable
    {
        private readonly IGroundGridOwner _owner;
        private readonly TileCoord _coord;

        /// <summary>The held grid; valid until this handle is disposed.</summary>
        public readonly GroundGrid Grid;

        internal GroundGridHandle(IGroundGridOwner owner, TileCoord coord, GroundGrid grid)
        {
            _owner = owner;
            _coord = coord;
            Grid = grid;
        }

        public void Dispose() => _owner?.Release(_coord);
    }
}
