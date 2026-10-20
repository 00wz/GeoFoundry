using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using UnityEngine;

namespace GeoFoundry.Core.Handlers
{
    /// <summary>
    /// Arguments of <see cref="ITerrainHandler.OnTerrainLoadedAsync"/>; the terrain counterpart of
    /// <see cref="GeoFoundry.Core.Handlers.LayerLoadContext"/>. The material is not passed; the handler owns it.
    /// </summary>
    public sealed class TerrainLoadContext
    {
        /// <summary>
        /// Gets the ground grid of the tile. Borrowed: valid until the handler call returns and must not be
        /// disposed by the handler.
        /// </summary>
        public GroundGrid Grid { get; }

        /// <summary>Gets the tile coordinate.</summary>
        public TileCoord TileCoord { get; }

        /// <summary>
        /// Gets the local frame of the tile (see <see cref="GeoFoundry.Core.Handlers.LayerLoadContext.LocalFrame"/>),
        /// not the global floating-origin pivot.
        /// </summary>
        public MercatorPivot LocalFrame { get; }

        /// <summary>
        /// Gets the parent transform for terrain GameObjects. May be null; handlers that render directly
        /// (<c>Graphics.RenderMesh</c> and the like) do not need GameObjects and ignore it.
        /// </summary>
        public Transform Parent { get; }

        /// <summary>
        /// Gets the cancellation token of the tile load, passed from
        /// <see cref="GeoFoundry.Tiles.TileBuilder.PopulateAsync"/>. Handlers pass it to
        /// <see cref="GeoFoundry.Core.JobHandleAwaitExtensions.WaitNonBlockingAsync"/>; the job is awaited to
        /// completion even after cancellation because it reads the borrowed <see cref="Grid"/>.
        /// </summary>
        public CancellationToken Ct { get; }

        /// <summary>Initializes a new context; normally created by the package for each handler call.</summary>
        public TerrainLoadContext(
            GroundGrid grid, TileCoord tileCoord, MercatorPivot localFrame, Transform parent, CancellationToken ct)
        {
            Grid = grid;
            TileCoord = tileCoord;
            LocalFrame = localFrame;
            Parent = parent;
            Ct = ct;
        }
    }
}
