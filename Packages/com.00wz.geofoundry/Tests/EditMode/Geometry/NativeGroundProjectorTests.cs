using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Geometry.Projection;
using GeoFoundry.Ground;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Tests.EditMode.Geometry
{
    /// <summary>
    /// Structural tests of the Burst/Job-compatible ground projection geometry
    /// (<see cref="NativePolygonClip"/>/<see cref="NativeTriangleMath"/>/<see cref="NativeGroundProjector"/>)
    /// using hardcoded expected values and FixedList/NativeList containers.
    /// </summary>
    public sealed class NativeGroundProjectorTests
    {
        [Test]
        public void PolygonClip_OverlappingTriangles_ProducesIntersection()
        {
            // Subject: (0,0)-(10,0)-(0,10). Clip: (5,5)-(15,5)-(5,15). The intersection is a small triangle
            // near (5,5).
            FixedList128Bytes<float2> output = default;
            NativePolygonClip.ClipTriangleAgainstTriangle(
                new float2(0, 0), new float2(10, 0), new float2(0, 10),
                new float2(5, 5), new float2(15, 5), new float2(5, 15),
                ref output);

            Assert.That(output.Length, Is.GreaterThanOrEqualTo(3), "result is a convex polygon");
            for (int i = 0; i < output.Length; i++)
            {
                Assert.That(output[i].x, Is.GreaterThanOrEqualTo(5f - 0.01f));
                Assert.That(output[i].y, Is.GreaterThanOrEqualTo(5f - 0.01f));
            }
        }

        [Test]
        public void PolygonClip_DisjointTriangles_ReturnsEmpty()
        {
            FixedList128Bytes<float2> output = default;
            NativePolygonClip.ClipTriangleAgainstTriangle(
                new float2(0, 0), new float2(1, 0), new float2(0, 1),
                new float2(100, 100), new float2(101, 100), new float2(100, 101),
                ref output);
            Assert.That(output.Length, Is.LessThan(3));
        }

        [Test]
        public void TriangleMath_IsDegenerate_OnColinearPoints()
        {
            Assert.That(NativeTriangleMath.IsDegenerate(new float2(0, 0), new float2(5, 0), new float2(10, 0)), Is.True);
            Assert.That(NativeTriangleMath.IsDegenerate(new float2(0, 0), new float2(5, 0), new float2(0, 5)), Is.False);
        }

        [Test]
        public void TriangleMath_Barycentric_AtVertices()
        {
            var a = new float2(0, 0); var b = new float2(10, 0); var c = new float2(0, 10);
            var bA = NativeTriangleMath.Barycentric(a, a, b, c);
            Assert.That(bA.x, Is.EqualTo(1f).Within(0.001f));
            var bMid = NativeTriangleMath.Barycentric(new float2(5, 5), a, b, c);
            Assert.That(bMid.x, Is.EqualTo(0f).Within(0.001f));
            Assert.That(bMid.y, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(bMid.z, Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void GroundProjector_TileUvOutput_StableUnderPivotRebase()
        {
            int seg = 2;
            int vps = seg + 1;
            var heights = new NativeArray<float>(vps * vps, Allocator.Temp);
            var posA = new NativeList<float3>(16, Allocator.Temp);
            var normA = new NativeList<float3>(16, Allocator.Temp);
            var uvA = new NativeList<float2>(16, Allocator.Temp);
            var posB = new NativeList<float3>(16, Allocator.Temp);
            var normB = new NativeList<float3>(16, Allocator.Temp);
            var uvB = new NativeList<float2>(16, Allocator.Temp);
            try
            {
                for (int i = 0; i < heights.Length; i++) heights[i] = 0f;

                var pivotA = MercatorPivot.FromLonLat(37, 55);
                var pivotB = MercatorPivot.FromLonLat(40, 56);
                var bounds = new double4(0, 0, 100, 100);

                var grid = new GroundGrid(seg, heights, bounds);

                var v0 = new float2(0.25f, 0.25f);
                var v1 = new float2(0.75f, 0.25f);
                var v2 = new float2(0.5f, 0.75f);
                NativeGroundProjector.ProjectTriangle(v0, v1, v2, grid, pivotA, ref posA, ref normA, ref uvA);
                NativeGroundProjector.ProjectTriangle(v0, v1, v2, grid, pivotB, ref posB, ref normB, ref uvB);

                Assert.That(uvA.Length, Is.EqualTo(uvB.Length));
                for (int i = 0; i < uvA.Length; i++)
                {
                    Assert.That(uvA[i].x, Is.EqualTo(uvB[i].x).Within(1e-5f), "tile UV is invariant to the pivot");
                    Assert.That(uvA[i].y, Is.EqualTo(uvB[i].y).Within(1e-5f));
                }
                Assert.That(posA.Length, Is.GreaterThan(0));
                Assert.That(posA[0], Is.Not.EqualTo(posB[0]));
            }
            finally
            {
                heights.Dispose();
                posA.Dispose(); normA.Dispose(); uvA.Dispose();
                posB.Dispose(); normB.Dispose(); uvB.Dispose();
            }
        }

        [Test]
        public void GroundProjector_ProjectTriangleOnFlat_ProducesOutputOnPlane()
        {
            int seg = 2;
            int vps = seg + 1;
            var heights = new NativeArray<float>(vps * vps, Allocator.Temp);
            var positions = new NativeList<float3>(16, Allocator.Temp);
            var normals = new NativeList<float3>(16, Allocator.Temp);
            var tileUvs = new NativeList<float2>(16, Allocator.Temp);
            try
            {
                for (int i = 0; i < heights.Length; i++) heights[i] = 50f;
                var pivot = MercatorPivot.FromLonLat(37, 55);
                var grid = new GroundGrid(seg, heights, new double4(0, 0, 100, 100));

                NativeGroundProjector.ProjectTriangle(
                    new float2(0.2f, 0.2f), new float2(0.8f, 0.2f), new float2(0.5f, 0.8f),
                    grid, pivot, ref positions, ref normals, ref tileUvs);

                Assert.That(positions.Length, Is.GreaterThan(0), "output triangles are produced");
                Assert.That(positions.Length % 3, Is.EqualTo(0), "vertex count is a multiple of 3");
                Assert.That(normals.Length, Is.EqualTo(positions.Length));
                Assert.That(tileUvs.Length, Is.EqualTo(positions.Length));
                for (int i = 0; i < positions.Length; i++)
                    Assert.That(positions[i].y, Is.EqualTo(50f).Within(0.01f), "all vertices lie on the flat 50 m plane");
                for (int i = 0; i < normals.Length; i++)
                    Assert.That(normals[i].y, Is.GreaterThan(0.99f), "flat ground gives normal ≈ +Y");
                for (int i = 0; i < tileUvs.Length; i++)
                {
                    Assert.That(tileUvs[i].x, Is.InRange(0f, 1f), "tile UV is within [0,1]");
                    Assert.That(tileUvs[i].y, Is.InRange(0f, 1f));
                }
            }
            finally
            {
                heights.Dispose();
                positions.Dispose(); normals.Dispose(); tileUvs.Dispose();
            }
        }

        /// <summary>
        /// Without terrain elevation the ground is a flat 1×1 grid (<see cref="FlatGroundGridProvider"/>): a triangle
        /// extending past the tile edge is clipped at the boundary just like on a terrain grid, and the resulting
        /// area equals the area of its part inside the tile.
        /// </summary>
        [Test]
        public void GroundProjector_FlatGrid1x1_ClipsTriangleToTile()
        {
            var positions = new NativeList<float3>(16, Allocator.Temp);
            var normals = new NativeList<float3>(16, Allocator.Temp);
            var tileUvs = new NativeList<float2>(16, Allocator.Temp);
            using var ground = new FlatGroundGridProvider();
            try
            {
                var coord = new TileCoord(14, 9903, 5145);
                Assert.That(ground.TryGet(coord, out var grid), Is.True);
                Assert.That(grid.SegmentsPerSide, Is.EqualTo(1));

                // Right triangle (0.5,0.2)-(1.5,0.2)-(0.5,0.6): the part inside the tile is a trapezoid
                // u∈[0.5,1] with area 0.5 * (0.4 + 0.2) * 0.5 = 0.15 (full area is 0.2).
                NativeGroundProjector.ProjectTriangle(
                    new float2(0.5f, 0.2f), new float2(1.5f, 0.2f), new float2(0.5f, 0.6f),
                    grid, MercatorPivot.FromLonLat(37, 55), ref positions, ref normals, ref tileUvs);

                Assert.That(positions.Length, Is.GreaterThan(0));
                Assert.That(positions.Length % 3, Is.EqualTo(0));

                float area = 0f;
                for (int i = 0; i < tileUvs.Length; i += 3)
                {
                    float2 a = tileUvs[i], b = tileUvs[i + 1], c = tileUvs[i + 2];
                    area += math.abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5f;
                }
                Assert.That(area, Is.EqualTo(0.15f).Within(1e-4f), "area of the part inside the tile");

                for (int i = 0; i < tileUvs.Length; i++)
                {
                    Assert.That(tileUvs[i].x, Is.InRange(0f, 1f + 1e-5f), "clipped at the tile edge");
                    Assert.That(tileUvs[i].y, Is.InRange(0f, 1f));
                    Assert.That(positions[i].y, Is.EqualTo(GroundHeightDefaults.FallbackHeightMeters).Within(1e-4f));
                    Assert.That(normals[i].y, Is.EqualTo(1f).Within(1e-4f));
                }
            }
            finally
            {
                positions.Dispose(); normals.Dispose(); tileUvs.Dispose();
            }
        }
    }
}
