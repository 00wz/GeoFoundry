using System;
using GeoFoundry.Core.Coordinates;
using UnityEngine;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Builds the terrain representation of a tile. The terrain counterpart of
    /// <see cref="GeoFoundry.Core.Handlers.ILayerHandler"/>.
    /// </summary>
    public interface ITerrainHandler
    {
        /// <summary>
        /// Builds the terrain geometry of one tile on the main thread, running heavy work in jobs without blocking
        /// it.
        /// </summary>
        /// <remarks>
        /// Follows the same contract as <see cref="GeoFoundry.Core.Handlers.ILayerHandler.OnLayerLoadedAsync"/>:
        /// <see cref="TerrainLoadContext.Grid"/> is borrowed and valid only until the method returns, so the
        /// handler waits for its jobs over it before returning, including after cancellation. On cancellation
        /// it returns <c>null</c> or throws <see cref="OperationCanceledException"/>.
        /// </remarks>
        /// <param name="ctx">The tile, its ground grid and the cancellation token.</param>
        /// <returns>The instance that owns the created resources, or <c>null</c>.</returns>
        Awaitable<ITerrainHandlerInstance> OnTerrainLoadedAsync(TerrainLoadContext ctx);
    }

    /// <summary>
    /// The terrain of one tile, created by <see cref="ITerrainHandler.OnTerrainLoadedAsync"/>. The tile owns it;
    /// <see cref="IDisposable.Dispose"/> releases all associated resources.
    /// </summary>
    public interface ITerrainHandlerInstance : IDisposable
    {
        /// <summary>
        /// Called when the tile is first placed and on every floating-origin rebase. Same contract as
        /// <see cref="GeoFoundry.Core.Handlers.ILayerHandlerInstance.OnOriginShifted"/>.
        /// </summary>
        void OnOriginShifted(MercatorPivot newPivot) { }

        /// <summary>
        /// Called when the tile is shown or hidden. Same contract as
        /// <see cref="GeoFoundry.Core.Handlers.ILayerHandlerInstance.OnActiveChanged"/>.
        /// </summary>
        void OnActiveChanged(bool active) { }

        /// <summary>
        /// Gets the memory held by the instance, counted against the tile budget. Same contract as
        /// <see cref="GeoFoundry.Core.Handlers.ILayerHandlerInstance.MemoryUsage"/>.
        /// </summary>
        TileMemoryUsage MemoryUsage { get; }
    }
}
