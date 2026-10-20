using System;
using GeoFoundry.Core.Coordinates;
using UnityEngine;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Builds content for one MVT layer. Registered in <see cref="LayerHandlerRegistry"/> and called for every
    /// tile that contains a layer named <see cref="LayerName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler fully owns the interpretation of the layer data: it reads feature properties itself and decides
    /// what geometry to build. This makes the package schema-agnostic: to switch from the OpenMapTiles schema to
    /// Shortbread or a custom one, register a different set of handlers.
    /// </para>
    /// <para>
    /// Several handlers can be registered for the same layer (for example, building → mesh + label + collider);
    /// they are called in registration order.
    /// </para>
    /// </remarks>
    public interface ILayerHandler
    {
        /// <summary>Gets the name of the MVT layer this handler processes, for example "building".</summary>
        string LayerName { get; }

        /// <summary>
        /// Builds the content of the layer for one tile. Called on the main thread for every tile that contains
        /// the layer <see cref="LayerName"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Heavy work is expected to run in jobs (Burst) without blocking the main thread; await job handles
        /// with <see cref="GeoFoundry.Core.JobHandleAwaitExtensions.WaitNonBlockingAsync"/>. The handler creates
        /// its GameObjects under <see cref="LayerLoadContext.TileRoot"/> and returns an instance whose resources
        /// are disposed when the tile is unloaded.
        /// </para>
        /// <para>
        /// Context data (<see cref="LayerLoadContext.Layer"/>, <see cref="LayerLoadContext.Grids"/>) is borrowed
        /// and released immediately after this method returns, including on cancellation. The handler must wait
        /// for every job it schedules over that data before returning, even after cancellation;
        /// <see cref="GeoFoundry.Core.JobHandleAwaitExtensions.WaitNonBlockingAsync"/> and
        /// <c>AsyncGeometryWorker</c> already do so.
        /// </para>
        /// <para>
        /// On cancellation (<see cref="LayerLoadContext.Ct"/>) the handler returns <c>null</c> or throws
        /// <see cref="OperationCanceledException"/> without leaving anything created. Any other exception is
        /// logged and does not prevent the remaining layers of the tile from being built.
        /// </para>
        /// </remarks>
        /// <param name="context">The tile, its parsed layer, ground grids and the cancellation token.</param>
        /// <returns>
        /// The instance that owns the created resources, or <c>null</c> if there is nothing to keep.
        /// </returns>
        Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext context);
    }

    /// <summary>
    /// The content created by <see cref="ILayerHandler.OnLayerLoadedAsync"/> for one tile. The tile owns the
    /// instance and calls <see cref="IDisposable.Dispose"/> when it is unloaded; the instance then releases its
    /// meshes, GameObjects and native allocations.
    /// </summary>
    /// <remarks>All members are called on the main thread.</remarks>
    public interface ILayerHandlerInstance : IDisposable
    {
        /// <summary>
        /// Called by <see cref="GeoFoundry.Tiles.TileData.ApplyPivot"/> when the tile is first placed and on every
        /// floating-origin rebase.
        /// </summary>
        /// <remarks>
        /// GameObject-based implementations (the typical case, see <see cref="MeshHierarchyInstance"/>) do not
        /// need to override this method: their position follows the tile root transform, which
        /// <c>TileData.ApplyPivot</c> updates separately. Override it only if the instance keeps its own absolute
        /// coordinates outside the transform hierarchy (for example, renders with <c>Graphics.DrawMesh</c> or
        /// <c>RenderMeshIndirect</c>); recompute them from scratch with <c>newPivot.WorldFromMercator(...)</c>
        /// rather than accumulating deltas.
        /// </remarks>
        /// <param name="newPivot">The current global floating-origin pivot.</param>
        void OnOriginShifted(MercatorPivot newPivot) { }

        /// <summary>
        /// Called by <see cref="GeoFoundry.Tiles.TileData.SetActive"/> when the tile is shown or hidden, and
        /// synchronously when the instance is attached to a tile that is already active or inactive.
        /// </summary>
        /// <remarks>
        /// A tile is hidden while its data must stay in memory, for example a lower-zoom tile covered by more
        /// detailed children. GameObject-based implementations (see <see cref="MeshHierarchyInstance"/>) do not
        /// need to override this method: their visibility follows <c>SetActive</c> of the tile root. Override it
        /// only if the instance renders outside the tile transform hierarchy (see <see cref="OnOriginShifted"/>).
        /// </remarks>
        /// <param name="active">Whether the tile should be rendered.</param>
        void OnActiveChanged(bool active) { }

        /// <summary>
        /// Gets the memory held by the instance (meshes, native data), counted against the tile memory budget.
        /// </summary>
        /// <remarks>
        /// Read once when the instance is attached to the tile (see
        /// <see cref="GeoFoundry.Tiles.TileData.MemoryUsage"/>), so the value must not change afterwards. Use
        /// <see cref="MeshMemory.Measure"/> for meshes; <see cref="MeshHierarchyInstance"/> computes it itself.
        /// Implementing it correctly is mandatory: an instance that understates its memory silently bypasses the
        /// tile budget. If the instance really holds nothing, return <see cref="TileMemoryUsage.Zero"/>
        /// explicitly.
        /// </remarks>
        TileMemoryUsage MemoryUsage { get; }
    }
}
