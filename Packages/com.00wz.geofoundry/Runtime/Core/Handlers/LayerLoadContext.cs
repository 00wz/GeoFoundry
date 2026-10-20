using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using UnityEngine;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Arguments of <see cref="ILayerHandler.OnLayerLoadedAsync"/>: the parsed layer of one tile, its coordinates,
    /// ground grids, scene parent and cancellation token. Materials are not passed; each handler owns its own.
    /// </summary>
    /// <remarks>
    /// Immutable. <see cref="GeoFoundry.Tiles.VectorTileBuilder"/> creates a new context for each handler call.
    /// The native data it references (<see cref="Layer"/>, <see cref="Grids"/>) is borrowed: it is valid only
    /// until the handler call returns and must not be disposed by the handler.
    /// </remarks>
    public sealed class LayerLoadContext
    {
        /// <summary>Gets the parsed MVT layer of the tile. Borrowed; valid until the handler call returns.</summary>
        public NativeMvtLayer Layer { get; }

        /// <summary>Gets the tile coordinate.</summary>
        public TileCoord TileCoord { get; }

        /// <summary>Gets the parent transform for all GameObjects the handler creates.</summary>
        public Transform TileRoot { get; }

        /// <summary>
        /// Gets the local frame of the tile, centered on
        /// <see cref="GeoFoundry.Core.Coordinates.TileCoord.CenterMeters"/>, for converting Mercator coordinates to
        /// vertex positions relative to <see cref="TileRoot"/>. It is independent of the global floating-origin
        /// pivot and does not change on rebase; <see cref="GeoFoundry.Tiles.TileData.ApplyPivot"/> positions the
        /// tile root relative to the global pivot separately.
        /// </summary>
        public MercatorPivot LocalFrame { get; }

        /// <summary>
        /// Gets the ground grids of the tile and its 8 neighbors; build jobs read <c>Heights</c> and
        /// <c>Normals</c> directly (see <c>BuildingBuildJob.SamplerGridView</c>). <see cref="TileGroundGrids.Center"/>
        /// is always set; neighbors only when <c>GeoFoundryConfig.LoadNeighborGrids</c> is enabled. The handler
        /// checks the slots it reads with <see cref="GeoFoundry.Core.Ground.GroundGrid.IsValid"/>; see
        /// <see cref="TileGroundGrids"/>. Borrowed; valid until the handler call returns.
        /// </summary>
        public TileGroundGrids Grids { get; }

        /// <summary>
        /// Gets the cancellation token of the tile load, passed from
        /// <see cref="GeoFoundry.Tiles.TileBuilder.PopulateAsync"/>. Handlers pass it to
        /// <see cref="GeoFoundry.Core.JobHandleAwaitExtensions.WaitNonBlockingAsync"/>: cancellation means the
        /// result is not needed, but the job is still awaited to completion (without blocking the thread) because
        /// it reads <see cref="Layer"/> and <see cref="Grids"/>, which are released right after the handler returns
        /// (see <see cref="ILayerHandler.OnLayerLoadedAsync"/>).
        /// </summary>
        public CancellationToken Ct { get; }

        /// <summary>Initializes a new context; normally created by the package for each handler call.</summary>
        /// <exception cref="ArgumentException"><paramref name="layer"/> is <c>default(NativeMvtLayer)</c>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="tileRoot"/> is null.</exception>
        public LayerLoadContext(
            NativeMvtLayer layer,
            TileCoord tileCoord,
            Transform tileRoot,
            MercatorPivot localFrame,
            TileGroundGrids grids,
            CancellationToken ct)
        {
            if (!layer.IsValid) throw new ArgumentException("Layer is invalid (default(NativeMvtLayer)).", nameof(layer));
            Layer = layer;
            TileCoord = tileCoord;
            TileRoot = tileRoot != null ? tileRoot : throw new ArgumentNullException(nameof(tileRoot));
            LocalFrame = localFrame;
            Grids = grids;
            Ct = ct;
        }
    }
}
