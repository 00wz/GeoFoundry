using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Converts MVT tile-local coordinates (int2 in [0, extent]) to Web Mercator meters. Tile-local coordinates
    /// have their origin at the top-left corner with Y pointing down; Mercator Y points up, so Y is flipped
    /// within the tile bounds.
    /// </summary>
    public static class MvtTileCoords
    {
        /// <summary>Converts a tile-local point of <paramref name="tile"/> to Mercator meters.</summary>
        /// <param name="local">Tile-local point.</param>
        /// <param name="tile">The tile the point belongs to.</param>
        /// <param name="extent">Layer extent (<see cref="NativeMvtLayer.Extent"/>), usually 4096.</param>
        public static double2 ToMercator(int2 local, TileCoord tile, uint extent)
        {
            var b = tile.BoundsMeters(); // (minX, minY, maxX, maxY)
            double sx = (b.z - b.x) / extent;
            double sy = (b.w - b.y) / extent;
            double x = b.x + local.x * sx;
            double y = b.w - local.y * sy;
            return new double2(x, y);
        }

        /// <summary>
        /// Converts a tile-local point to Mercator meters given precomputed tile bounds (minX, minY, maxX, maxY);
        /// avoids recomputing the bounds for every point.
        /// </summary>
        public static double2 ToMercator(int2 local, double4 tileBoundsMeters, uint extent)
        {
            double sx = (tileBoundsMeters.z - tileBoundsMeters.x) / extent;
            double sy = (tileBoundsMeters.w - tileBoundsMeters.y) / extent;
            return new double2(
                tileBoundsMeters.x + local.x * sx,
                tileBoundsMeters.w - local.y * sy);
        }
    }
}
