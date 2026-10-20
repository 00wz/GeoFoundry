using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Geometry.Projection
{
    /// <summary>
    /// Burst- and Job System-compatible triangle/segment geometry: degeneracy check and line-triangle
    /// intersections. Output goes to a <see cref="FixedList32Bytes{T}"/> (inline, no allocation; enough capacity,
    /// since a segment crosses a triangle boundary in at most 2 distinct points).
    /// </summary>
    internal static class NativeTriangleMath
    {
        public static bool IsDegenerate(float2 a, float2 b, float2 c, float eps = 1e-9f)
        {
            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            return math.abs(cross) < eps;
        }

        public static float3 Barycentric(float2 p, float2 a, float2 b, float2 c)
        {
            float2 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = math.dot(v0, v0);
            float d01 = math.dot(v0, v1);
            float d11 = math.dot(v1, v1);
            float d20 = math.dot(v2, v0);
            float d21 = math.dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (math.abs(denom) < 1e-12f) return new float3(1, 0, 0);
            float v = (d11 * d20 - d01 * d21) / denom;
            float w = (d00 * d21 - d01 * d20) / denom;
            float u = 1f - v - w;
            return new float3(u, v, w);
        }

        /// <summary>
        /// Writes to <paramref name="output"/> all intersection points of the segment (p1, p2) with the three edges
        /// of the triangle (t0, t1, t2): 0–2 distinct points (a hit exactly at a vertex may be reported twice).
        /// </summary>
        public static void LineTriangleIntersections(
            float2 p1, float2 p2, float2 t0, float2 t1, float2 t2, ref FixedList32Bytes<float2> output)
        {
            output.Clear();
            if (TrySegmentSegmentIntersect(p1, p2, t0, t1, out var hit0)) output.Add(hit0);
            if (TrySegmentSegmentIntersect(p1, p2, t1, t2, out var hit1)) output.Add(hit1);
            if (TrySegmentSegmentIntersect(p1, p2, t2, t0, out var hit2)) output.Add(hit2);
        }

        /// <summary>Intersects two segments, endpoints included. Returns <c>false</c> if they are parallel or do not
        /// intersect.</summary>
        public static bool TrySegmentSegmentIntersect(float2 a1, float2 a2, float2 b1, float2 b2, out float2 hit)
        {
            float2 r = a2 - a1;
            float2 s = b2 - b1;
            float denom = r.x * s.y - r.y * s.x;
            if (math.abs(denom) < 1e-9f) { hit = default; return false; }
            float t = ((b1.x - a1.x) * s.y - (b1.y - a1.y) * s.x) / denom;
            float u = ((b1.x - a1.x) * r.y - (b1.y - a1.y) * r.x) / denom;
            if (t < 0f || t > 1f || u < 0f || u > 1f) { hit = default; return false; }
            hit = a1 + r * t;
            return true;
        }
    }
}
