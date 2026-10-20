using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Core.Mvt;
using UnityEngine;

namespace GeoFoundry.Tiles
{
    /// <summary>
    /// Builds the vector geometry of one tile: passes the grids to handlers via <see cref="LayerLoadContext.Grids"/>,
    /// iterates the MVT layers and attaches each successfully built instance to the tile as soon as it is ready
    /// (<see cref="TileData.AttachLayerInstance"/> immediately synchronizes it with the current floating-origin
    /// pivot, closing the race window between layers of one tile).
    ///
    /// Handlers are called only through <see cref="ILayerHandler.OnLayerLoadedAsync"/> and build geometry with
    /// Jobs/Burst without blocking the main thread.
    ///
    /// A failure of one handler is logged and does not affect other layers. Cancellation of <c>ct</c> is not an
    /// error: it stops the build (<see cref="OperationCanceledException"/> before the next handler, or the handler's
    /// own exception). If the last handler returns <c>null</c> on cancellation, the method completes normally; the
    /// caller checks the token after the build (see <see cref="TileBuilder.PopulateAsync"/>).
    /// </summary>
    internal static class VectorTileBuilder
    {
        public static async Awaitable BuildAsync(
            NativeMvtTile tile,
            TileCoord coord,
            TileData tileData,
            MercatorPivot localFrame,
            TileGroundGrids grids,
            LayerHandlerRegistry handlerRegistry,
            CancellationToken ct)
        {
            for (int li = 0; li < tile.Layers.Count; li++)
            {
                var layer = tile.Layers[li];
                string layerName = layer.Name.ToString();
                var handlers = handlerRegistry.Get(layerName);
                if (handlers.Count == 0) continue;

                foreach (var handler in handlers)
                {
                    // After cancellation the remaining layers are not built: the tile will be released anyway.
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var ctx = new LayerLoadContext(
                            layer, coord, tileData.Root, localFrame,
                            grids, ct);
                        var inst = await handler.OnLayerLoadedAsync(ctx);
                        if (inst != null) tileData.AttachLayerInstance(inst);
                    }
                    // An exception during cancellation is not a handler failure: it stops the tile build.
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Debug.LogError(
                            $"[GeoFoundry] Handler {handler.GetType().Name} on '{layerName}' " +
                            $"failed for {coord}: {ex}");
                    }
                }
            }
        }
    }
}
