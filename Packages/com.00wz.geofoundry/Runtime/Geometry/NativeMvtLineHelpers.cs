using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Mvt;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Burst- and Job System-compatible projection of LineString geometry from tile-local coordinates to Mercator
    /// via the blittable <see cref="MvtTileCoords.ToMercator(int2, double4, uint)"/>, working on the raw slices
    /// (<see cref="NativeMvtLayer.FeatureRingRangeSlice"/>, <see cref="NativeMvtLayer.Rings"/>,
    /// <see cref="NativeMvtLayer.RingPoints"/>) instead of <see cref="NativeMvtFeature"/>; see
    /// <see cref="NativeMvtPolygonHelpers"/> for the same approach with polygons.
    ///
    /// Simpler than polygons: LineString/MultiLineString needs no grouping by signed area. The MVT parser stores
    /// lines in the same ring storage (<c>NativeMvtTile.Rings</c>/<c>RingPoints</c>) one to one (each MoveTo in the
    /// wire format starts a new line), so every ring of a feature is simply a separate line.
    /// </summary>
    internal static class NativeMvtLineHelpers
    {
        /// <summary>
        /// Projects the rings of ONE feature (<paramref name="featureRingRange"/> is (start, count) in
        /// <paramref name="rings"/>) into Mercator lines; rings with fewer than 2 points are skipped. The output lists
        /// are cleared first, so each call is self-contained for one feature.
        /// </summary>
        /// <param name="outPoints">All points of all kept lines, concatenated, in Mercator meters.</param>
        /// <param name="outLineRanges">(pointStart, pointCount) in <paramref name="outPoints"/> for each line.</param>
        /// <param name="outLineRings">Absolute ring index (in <paramref name="rings"/>) of each line, parallel to
        /// <paramref name="outLineRanges"/>.</param>
        public static void ProjectLines(
            int2 featureRingRange, NativeArray<int2> rings, NativeArray<int2> ringPoints,
            TileCoord tile, uint extent,
            ref NativeList<double2> outPoints,
            ref NativeList<int2> outLineRanges,
            ref NativeList<int> outLineRings)
        {
            outPoints.Clear();
            outLineRanges.Clear();
            outLineRings.Clear();
            if (featureRingRange.y == 0) return;

            double4 bounds = tile.BoundsMeters();

            for (int r = 0; r < featureRingRange.y; r++)
            {
                int2 ringRange = rings[featureRingRange.x + r];
                if (ringRange.y < 2) continue;

                int pointStart = outPoints.Length;
                for (int i = 0; i < ringRange.y; i++)
                {
                    int2 local = ringPoints[ringRange.x + i];
                    outPoints.Add(MvtTileCoords.ToMercator(local, bounds, extent));
                }
                outLineRanges.Add(new int2(pointStart, ringRange.y));
                outLineRings.Add(featureRingRange.x + r);
            }
        }
    }
}
