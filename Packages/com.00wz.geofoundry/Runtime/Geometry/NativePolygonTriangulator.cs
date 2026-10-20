using GeoFoundry.Geometry.Earcut;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Takes polygon rings (exterior + holes) as CSR arrays (the same start + count pattern as
    /// <c>NativeMvtTile.Rings</c>/<c>RingPoints</c>) and returns a flat vertex list plus triangle indices via
    /// <see cref="NativeEarcut"/>.
    ///
    /// <c>ringRanges</c>[0] is the exterior ring, [1..n] are holes (MVT/GeoJSON convention). Double
    /// precision is kept at the API boundary; coordinates are converted to float only inside the triangulator.
    /// </summary>
    internal static class NativePolygonTriangulator
    {
        /// <summary>
        /// Triangulates a polygon. Returns <c>true</c> if at least one triangle was produced.
        /// </summary>
        /// <param name="ringPoints">All points of all rings in sequence (exterior, then holes).</param>
        /// <param name="ringRanges">(start, count) in <paramref name="ringPoints"/> for each ring.</param>
        /// <param name="vertices">Output: points in the same order as <paramref name="ringPoints"/>.</param>
        /// <param name="triangles">Output: indices into <paramref name="vertices"/>, three per triangle.</param>
        public static bool Triangulate(
            NativeArray<double2> ringPoints, NativeArray<int2> ringRanges,
            NativeList<double2> vertices, NativeList<int> triangles)
        {
            vertices.Clear();
            triangles.Clear();
            if (ringRanges.Length == 0 || ringRanges[0].y < 3) return false;

            int totalPoints = 0;
            for (int r = 0; r < ringRanges.Length; r++) totalPoints += ringRanges[r].y;

            var flat = new NativeArray<float2>(totalPoints, Allocator.Temp);
            var holeIndices = new NativeArray<int>(math.max(0, ringRanges.Length - 1), Allocator.Temp);

            int cursor = 0;
            for (int r = 0; r < ringRanges.Length; r++)
            {
                if (r > 0) holeIndices[r - 1] = cursor;

                int2 range = ringRanges[r];
                for (int i = 0; i < range.y; i++)
                {
                    double2 p = ringPoints[range.x + i];
                    flat[cursor] = (float2)p;
                    vertices.Add(p);
                    cursor++;
                }
            }

            NativeEarcut.Triangulate(flat, holeIndices, triangles);

            flat.Dispose();
            holeIndices.Dispose();

            return triangles.Length > 0;
        }
    }
}
