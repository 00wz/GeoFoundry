using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using Unity.Mathematics;

namespace GeoFoundry.Ground
{
    /// <summary>
    /// <see cref="IGroundHeightProvider"/> over ground grids already loaded by a <see cref="GroundGridProvider"/>.
    /// Read-only: does not change tile ref counts (it uses <see cref="GroundGridProvider.TryGet"/>).
    ///
    /// Tiles, and therefore their grids, are loaded at different zoom levels, so the height comes from the finest
    /// loaded grid containing the point, found by iterating zoom from <see cref="TileCoord.MaxZoom"/> down to 0
    /// (grids are keyed by tile, with no spatial index). The finest grid in memory is not necessarily rendered
    /// (e.g. a loading child under a rendered parent), but it samples the same DEM surface in more detail.
    /// </summary>
    public sealed class GroundGridHeightProvider : IGroundHeightProvider
    {
        private readonly GroundGridProvider _gridProvider;

        /// <param name="gridProvider">Provider of the tiles' ground grids.</param>
        public GroundGridHeightProvider(GroundGridProvider gridProvider)
        {
            _gridProvider = gridProvider;
        }

        public bool TryGetHeight(double2 mercatorMeters, out float heightMeters)
        {
            if (!TryFindFinestGrid(mercatorMeters, out var grid))
            {
                heightMeters = GroundHeightDefaults.FallbackHeightMeters;
                return false;
            }

            var bounds = grid.BoundsMeters;
            var u = (float)((mercatorMeters.x - bounds.x) / (bounds.z - bounds.x));
            var v = (float)((mercatorMeters.y - bounds.y) / (bounds.w - bounds.y));
            heightMeters = grid.SampleHeightAtUv(new float2(u, v));
            return true;
        }

        private bool TryFindFinestGrid(double2 mercatorMeters, out GroundGrid grid)
        {
            for (int zoom = TileCoord.MaxZoom; zoom >= 0; zoom--)
            {
                var tileXy = MercatorProjection.MercatorToTile(mercatorMeters, zoom);
                if (_gridProvider.TryGet(new TileCoord(zoom, tileXy.x, tileXy.y), out grid)) return true;
            }

            grid = default;
            return false;
        }
    }
}
