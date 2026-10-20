using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Geometry.Projection
{
    /// <summary>
    /// Projects geometry onto the tile's ground mesh: walks the ground cell triangles within the input geometry's
    /// AABB, clips with <see cref="NativePolygonClip"/> and samples the blittable <see cref="GroundGrid"/>. Input
    /// coordinates are in tile UV space ([0,1]); geometry outside the tile is clipped away. Output goes to
    /// <c>ref NativeList</c> arguments with no static shared buffers (each call uses its own local
    /// <see cref="FixedList128Bytes{T}"/>/<see cref="NativeList{T}"/>), so it is safe to use from concurrent jobs.
    /// </summary>
    internal static class NativeGroundProjector
    {
        /// <summary>
        /// Projects a triangle onto the ground. Output sub-triangles are appended in triples with per-vertex world
        /// positions, normals and tile-local UVs (the sub-vertex position in tile UV space [0,1]).
        /// </summary>
        public static void ProjectTriangle(
            float2 v0, float2 v1, float2 v2,
            in GroundGrid grid,
            MercatorPivot frame,
            ref NativeList<float3> outPositions,
            ref NativeList<float3> outNormals,
            ref NativeList<float2> outTileUvs)
        {
            if (NativeTriangleMath.IsDegenerate(v0, v1, v2)) return;

            EnsureCCW(ref v0, ref v1, ref v2);

            float2 aabbMin = math.min(math.min(v0, v1), v2);
            float2 aabbMax = math.max(math.max(v0, v1), v2);
            int N = grid.SegmentsPerSide;
            int xMin = math.max(0, (int)math.floor(aabbMin.x * N));
            int yMin = math.max(0, (int)math.floor(aabbMin.y * N));
            int xMax = math.min(N - 1, (int)math.floor(aabbMax.x * N));
            int yMax = math.min(N - 1, (int)math.floor(aabbMax.y * N));

            FixedList128Bytes<float2> clipResult = default;

            for (int qy = yMin; qy <= yMax; qy++)
            for (int qx = xMin; qx <= xMax; qx++)
            {
                for (int triIdx = 0; triIdx < 2; triIdx++)
                {
                    grid.GetCellTriangle(qx, qy, triIdx, out var g0, out var g1, out var g2);
                    EnsureCCW(ref g0, ref g1, ref g2);

                    NativePolygonClip.ClipTriangleAgainstTriangle(v0, v1, v2, g0, g1, g2, ref clipResult);
                    if (clipResult.Length < 3) continue;

                    for (int k = 2; k < clipResult.Length; k++)
                    {
                        float2 p0 = clipResult[0];
                        float2 p1 = clipResult[k];
                        float2 p2 = clipResult[k - 1];

                        outPositions.Add((float3)grid.SampleWorld(p0, frame));
                        outPositions.Add((float3)grid.SampleWorld(p1, frame));
                        outPositions.Add((float3)grid.SampleWorld(p2, frame));

                        outNormals.Add((float3)grid.SampleVertexNormal(p0, frame));
                        outNormals.Add((float3)grid.SampleVertexNormal(p1, frame));
                        outNormals.Add((float3)grid.SampleVertexNormal(p2, frame));

                        outTileUvs.Add(p0);
                        outTileUvs.Add(p1);
                        outTileUvs.Add(p2);
                    }
                }
            }
        }

        /// <summary>
        /// Projects a line segment onto the ground: appends the segment's endpoints that lie inside the tile plus
        /// upsampled points where the segment crosses ground cell boundaries, each with its normal, in order along
        /// the segment.
        /// </summary>
        public static void ProjectLineSegment(
            float2 a, float2 b,
            in GroundGrid grid,
            MercatorPivot frame,
            ref NativeList<float3> outPositions,
            ref NativeList<float3> outNormals)
        {
            float2 dir = b - a;
            float lenSq = math.dot(dir, dir);
            if (lenSq < 1e-12f) return;

            var progresses = new NativeList<float>(16, Allocator.Temp);
            try
            {
                if (a.x >= 0f && a.x <= 1f && a.y >= 0f && a.y <= 1f) progresses.Add(0f);
                if (b.x >= 0f && b.x <= 1f && b.y >= 0f && b.y <= 1f) progresses.Add(1f);

                int N = grid.SegmentsPerSide;
                int xMin = math.max(0, (int)math.floor(math.min(a.x, b.x) * N));
                int yMin = math.max(0, (int)math.floor(math.min(a.y, b.y) * N));
                int xMax = math.min(N - 1, (int)math.floor(math.max(a.x, b.x) * N));
                int yMax = math.min(N - 1, (int)math.floor(math.max(a.y, b.y) * N));

                FixedList32Bytes<float2> hits = default;

                for (int qy = yMin; qy <= yMax; qy++)
                for (int qx = xMin; qx <= xMax; qx++)
                {
                    for (int triIdx = 0; triIdx < 2; triIdx++)
                    {
                        grid.GetCellTriangle(qx, qy, triIdx, out var g0, out var g1, out var g2);
                        NativeTriangleMath.LineTriangleIntersections(a, b, g0, g1, g2, ref hits);
                        for (int h = 0; h < hits.Length; h++)
                        {
                            float2 hp = hits[h];
                            float t = math.dot(hp - a, dir) / lenSq;
                            if (t > 0f && t < 1f) progresses.Add(t);
                        }
                    }
                }

                if (progresses.Length < 2) return;
                progresses.Sort();

                const float eps = 1e-5f;
                float prevT = -1f;
                for (int i = 0; i < progresses.Length; i++)
                {
                    float t = progresses[i];
                    if (t - prevT < eps) continue;
                    prevT = t;
                    float2 p = math.lerp(a, b, t);
                    outPositions.Add((float3)grid.SampleWorld(p, frame));
                    outNormals.Add((float3)grid.SampleVertexNormal(p, frame));
                }
            }
            finally
            {
                progresses.Dispose();
            }
        }

        /// <summary>If the triangle is CW in (X right, Y up), swaps b and c to make it CCW.</summary>
        private static void EnsureCCW(ref float2 a, ref float2 b, ref float2 c)
        {
            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (cross < 0f) { (b, c) = (c, b); }
        }
    }
}
