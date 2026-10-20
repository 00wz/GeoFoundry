using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Geometry.Projection
{
    /// <summary>
    /// Sutherland-Hodgman clipping of a triangle against a triangle (the only use case is
    /// <see cref="NativeGroundProjector"/>). Takes explicit <c>float2</c> parameters (subject and clip always have
    /// exactly 3 points) and uses local <see cref="FixedList128Bytes{T}"/> buffers: no allocation and no static
    /// state. The intersection of two triangles has at most 6 vertices after 3 clip edges, well within capacity.
    /// </summary>
    internal static class NativePolygonClip
    {
        /// <summary>
        /// Intersects the subject triangle (s0, s1, s2) with the clip triangle (c0, c1, c2) using Sutherland-Hodgman.
        /// Both triangles must be CCW. <paramref name="output"/> receives the intersection polygon (up to 9 vertices,
        /// usually ≤ 6); it is empty if the triangles do not overlap.
        /// </summary>
        public static void ClipTriangleAgainstTriangle(
            float2 s0, float2 s1, float2 s2, float2 c0, float2 c1, float2 c2,
            ref FixedList128Bytes<float2> output)
        {
            output.Clear();

            FixedList128Bytes<float2> bufA = default;
            FixedList128Bytes<float2> bufB = default;
            bufA.Add(s0); bufA.Add(s1); bufA.Add(s2);

            // Ping-pong between bufA and bufB: the current input/output role switches after each clip edge.
            // FixedList is a struct, so the swap uses a flag rather than reassigning a ref/var, which would copy
            // the buffer instead of sharing it.
            bool inIsA = true;

            for (int e = 0; e < 3; e++)
            {
                float2 edgeA = e == 0 ? c0 : (e == 1 ? c1 : c2);
                float2 edgeB = e == 0 ? c1 : (e == 1 ? c2 : c0);

                if (inIsA) ClipAgainstEdge(in bufA, edgeA, edgeB, ref bufB);
                else ClipAgainstEdge(in bufB, edgeA, edgeB, ref bufA);
                inIsA = !inIsA;

                if (inIsA && bufA.Length == 0) { output.Clear(); return; }
                if (!inIsA && bufB.Length == 0) { output.Clear(); return; }
            }

            var final = inIsA ? bufA : bufB;
            for (int i = 0; i < final.Length; i++) output.Add(final[i]);
        }

        private static void ClipAgainstEdge(
            in FixedList128Bytes<float2> inList, float2 edgeA, float2 edgeB, ref FixedList128Bytes<float2> outList)
        {
            outList.Clear();
            if (inList.Length == 0) return;

            float2 prev = inList[inList.Length - 1];
            bool prevInside = IsInsideEdge(prev, edgeA, edgeB);

            for (int i = 0; i < inList.Length; i++)
            {
                float2 cur = inList[i];
                bool curInside = IsInsideEdge(cur, edgeA, edgeB);

                if (curInside)
                {
                    if (!prevInside) outList.Add(LineIntersection(prev, cur, edgeA, edgeB));
                    outList.Add(cur);
                }
                else if (prevInside)
                {
                    outList.Add(LineIntersection(prev, cur, edgeA, edgeB));
                }

                prev = cur;
                prevInside = curInside;
            }
        }

        /// <summary>
        /// Returns <c>true</c> if <paramref name="p"/> is on the inside of the edge <c>(edgeA → edgeB)</c>. For a CCW
        /// clip polygon, inside means to the left of the edge direction.
        /// </summary>
        private static bool IsInsideEdge(float2 p, float2 edgeA, float2 edgeB)
        {
            float cross = (edgeB.x - edgeA.x) * (p.y - edgeA.y)
                        - (edgeB.y - edgeA.y) * (p.x - edgeA.x);
            return cross >= 0f;
        }

        /// <summary>Intersection point of the segment (p1, p2) with the line through (eA, eB).</summary>
        private static float2 LineIntersection(float2 p1, float2 p2, float2 eA, float2 eB)
        {
            float2 d1 = p2 - p1;
            float2 d2 = eB - eA;
            float denom = d1.x * d2.y - d1.y * d2.x;
            if (math.abs(denom) < 1e-9f) return p1; // parallel — degenerate
            float t = ((eA.x - p1.x) * d2.y - (eA.y - p1.y) * d2.x) / denom;
            return p1 + d1 * t;
        }
    }
}
