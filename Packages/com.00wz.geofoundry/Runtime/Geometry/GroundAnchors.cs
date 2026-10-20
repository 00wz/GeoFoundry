using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using Unity.Mathematics;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Burst-compatible placement of point-like objects (labels, icons, markers) on the tile's ground: converts
    /// tile-local MVT coordinates to the tile's local frame with the terrain height under the point. Usable both on
    /// the main thread and inside jobs of other assemblies, since it only takes blittable inputs.
    /// </summary>
    public static class GroundAnchors
    {
        /// <summary>
        /// Projects a tile-local MVT point onto the ground.
        /// </summary>
        /// <remarks>
        /// Points outside <c>[0, extent)</c> are rejected: they lie in the tile buffer and belong to a neighboring
        /// tile, which contains the same point inside its own extent. Accepting them would duplicate every label
        /// near tile borders (OpenMapTiles uses a 256-pixel buffer for <c>place</c> and <c>water_name</c>).
        /// </remarks>
        /// <param name="local">Point in tile-local coordinates (Y down).</param>
        /// <param name="coord">Tile coordinate.</param>
        /// <param name="extent">Layer extent.</param>
        /// <param name="grid">The tile's ground grid (must be valid).</param>
        /// <param name="frame">The tile's local frame.</param>
        /// <param name="position">Position relative to the tile root, on the ground.</param>
        /// <param name="tileUv">Position in tile UV space ([0,1], V up).</param>
        /// <returns><c>false</c> if the point lies outside the tile extent.</returns>
        public static bool TryProjectPoint(
            int2 local, TileCoord coord, uint extent, in GroundGrid grid, MercatorPivot frame,
            out float3 position, out float2 tileUv)
        {
            position = default;
            tileUv = default;
            if (local.x < 0 || local.y < 0 || local.x >= extent || local.y >= extent) return false;

            double4 bounds = coord.BoundsMeters();
            double2 m = MvtTileCoords.ToMercator(local, bounds, extent);
            tileUv = ToTileUv(m, bounds);
            position = grid.SampleWorld(tileUv, frame);
            return true;
        }

        /// <summary>
        /// Returns the ground position (relative to the tile root) at a tile UV, without bounds rejection; UVs are
        /// clamped to the tile by <see cref="GroundGrid.SampleWorld"/>.
        /// </summary>
        public static float3 SampleGround(float2 tileUv, in GroundGrid grid, MercatorPivot frame) =>
            grid.SampleWorld(math.saturate(tileUv), frame);

        /// <summary>Converts Mercator meters to tile UV space ([0,1] across the tile bounds).</summary>
        public static float2 ToTileUv(double2 mercator, double4 tileBoundsMeters)
        {
            double w = tileBoundsMeters.z - tileBoundsMeters.x;
            double h = tileBoundsMeters.w - tileBoundsMeters.y;
            return new float2((float)((mercator.x - tileBoundsMeters.x) / w), (float)((mercator.y - tileBoundsMeters.y) / h));
        }
    }
}
