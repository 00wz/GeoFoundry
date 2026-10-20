using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Mvt;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Burst- and Job System-compatible projection of Polygon geometry from tile-local coordinates to Mercator via
    /// the blittable <see cref="MvtTileCoords.ToMercator(int2, double4, uint)"/>, with MultiPolygon splitting by
    /// the sign of the signed area. Works on the raw slices (<see cref="NativeMvtLayer.FeatureRingRangeSlice"/>,
    /// <see cref="NativeMvtLayer.Rings"/>, <see cref="NativeMvtLayer.RingPoints"/>) instead of
    /// <see cref="NativeMvtFeature"/>: <see cref="NativeMvtTile"/> keeps its data in internal fields accessible only
    /// to the <c>GeoFoundry.Core</c> assembly, so jobs in <c>GeoFoundry.Geometry</c> need the bare
    /// <c>NativeArray</c>s directly.
    ///
    /// The output is a 3-level CSR (polygon → ring → point), the same start + count pattern as
    /// <c>NativeMvtTile.Rings</c>/<c>RingPoints</c>.
    /// </summary>
    internal static class NativeMvtPolygonHelpers
    {
        /// <summary>
        /// Splits the rings of ONE feature (<paramref name="featureRingRange"/> is (start, count) in
        /// <paramref name="rings"/>) into separate polygons (exterior + holes) by the sign of the signed area. The
        /// output lists are cleared first, so each call is self-contained for one feature.
        /// </summary>
        /// <param name="outPoints">All points of all kept polygon rings, concatenated, in Mercator meters.</param>
        /// <param name="outRingRanges">(pointStart, pointCount) in <paramref name="outPoints"/> for each kept
        /// ring.</param>
        /// <param name="outPolygonRingRanges">(ringStart, ringCount) in <paramref name="outRingRanges"/> for each
        /// polygon; element [0] of each is the exterior.</param>
        public static void SplitMultiPolygon(
            int2 featureRingRange, NativeArray<int2> rings, NativeArray<int2> ringPoints,
            TileCoord tile, uint extent,
            ref NativeList<double2> outPoints,
            ref NativeList<int2> outRingRanges,
            ref NativeList<int2> outPolygonRingRanges)
        {
            outPoints.Clear();
            outRingRanges.Clear();
            outPolygonRingRanges.Clear();
            if (featureRingRange.y == 0) return;

            double4 bounds = tile.BoundsMeters();
            int currentPolygonRingStart = -1;

            for (int r = 0; r < featureRingRange.y; r++)
            {
                int2 ringRange = rings[featureRingRange.x + r];
                if (ringRange.y < 3) continue;

                int pointStart = outPoints.Length;
                for (int i = 0; i < ringRange.y; i++)
                {
                    int2 local = ringPoints[ringRange.x + i];
                    outPoints.Add(MvtTileCoords.ToMercator(local, bounds, extent));
                }
                int pointCount = outPoints.Length - pointStart;

                double area = SignedArea(outPoints, pointStart, pointCount);

                if (area < 0)
                {
                    // A new exterior closes the previous polygon, if one is open.
                    if (currentPolygonRingStart >= 0)
                        outPolygonRingRanges.Add(new int2(currentPolygonRingStart, outRingRanges.Length - currentPolygonRingStart));

                    currentPolygonRingStart = outRingRanges.Length;
                    outRingRanges.Add(new int2(pointStart, pointCount));
                }
                else if (area > 0 && currentPolygonRingStart >= 0)
                {
                    outRingRanges.Add(new int2(pointStart, pointCount));
                    // An orphan hole (currentPolygonRingStart < 0) is not registered; its points stay as an unused
                    // tail in outPoints (harmless: the scratch lists are cleared for the next feature).
                }
                // area == 0: degenerate, skipped.
            }

            if (currentPolygonRingStart >= 0)
                outPolygonRingRanges.Add(new int2(currentPolygonRingStart, outRingRanges.Length - currentPolygonRingStart));
        }

        /// <summary>Signed area by the surveyor's formula.</summary>
        private static double SignedArea(NativeList<double2> points, int start, int count)
        {
            double sum = 0;
            for (int k = 0; k < count; k++)
            {
                double2 a = points[start + k];
                double2 b = points[start + (k + 1) % count];
                sum += (a.x * b.y) - (b.x * a.y);
            }
            return sum * 0.5;
        }
    }
}
