using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using GeoFoundry.Ground;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Geometry
{
    /// <summary>
    /// Schedules <see cref="SurfaceBuildJob"/> via <c>Schedule()</c> and awaits it with
    /// <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/> rather than calling <c>Complete()</c> directly.
    /// This catches Burst-incompatible constructs and confirms that the whole async pipeline
    /// (job → <see cref="MeshAssembler.BuildMeshes"/>) produces valid Meshes.
    /// </summary>
    public sealed class SurfaceBuildJobTests
    {
        // Exterior ring: signed area in Mercator < 0 (tile-local Y-down flips to Mercator Y-up during projection;
        // see NativeMvtPolygonHelpersTests).
        private static readonly int2[] ExteriorRing =
        {
            new(1000, 1000), new(3000, 1000), new(3000, 3000), new(1000, 3000),
        };

        [UnityTest]
        public IEnumerator ScheduledJob_SquarePolygon_WaitNonBlockingAsync_ProducesGeometry() => Run(async ct =>
        {
            var coord = new TileCoord(14, 9903, 5145);
            const uint extent = 4096;

            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.Polygon }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1) }, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, ExteriorRing.Length) }, Allocator.TempJob);
            var ringPoints = new NativeArray<int2>(ExteriorRing, Allocator.TempJob);
            // Empty is the "no per-feature color" sentinel (see SurfaceBuildJob.FeatureColors):
            // the job broadcasts opaque white to all vertices.
            var featureColors = new NativeArray<Color32>(0, Allocator.TempJob);
            var drawItems = new NativeArray<SurfaceDrawItem>(0, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normals = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var colors = new NativeList<Color32>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var ranges = new NativeList<MeshRange>(Allocator.TempJob);
            var rangeFeatureIndices = new NativeList<int>(Allocator.TempJob);

            // Ground without terrain elevation: a flat 1×1 grid (as in production with elevation disabled).
            var ground = new FlatGroundGridProvider();
            try
            {
                var job = new SurfaceBuildJob
                {
                    FeatureGeomTypes = featureGeomTypes,
                    FeatureRingRange = featureRingRange,
                    Rings = rings,
                    RingPoints = ringPoints,
                    FeatureColors = featureColors,
                    DrawItems = drawItems,
                    Coord = coord,
                    Extent = extent,
                    UvScalePerMeter = 0.05f,
                    Grid = GroundTestUtils.FlatGrid(ground, coord),
                    LocalFrame = MercatorPivot.FromLonLat(37, 55),
                    Positions = positions,
                    Normals = normals,
                    Uvs = uvs,
                    Colors = colors,
                    Triangles = triangles,
                    Ranges = ranges,
                    RangeFeatureIndices = rangeFeatureIndices,
                };

                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True, "the job completes within the test timeout");

                Assert.That(ranges.Length, Is.EqualTo(1), "one polygon gives one MeshRange");
                Assert.That(rangeFeatureIndices.Length, Is.EqualTo(1));
                Assert.That(rangeFeatureIndices[0], Is.EqualTo(0));

                var range = ranges[0];
                Assert.That(range.VertexCount, Is.GreaterThan(0));
                Assert.That(range.TriCount, Is.GreaterThan(0));
                Assert.That(range.TriCount % 3, Is.EqualTo(0));
                Assert.That(positions.Length, Is.EqualTo(normals.Length));
                Assert.That(positions.Length, Is.EqualTo(uvs.Length));
                Assert.That(positions.Length, Is.EqualTo(colors.Length));

                // Flat ground: all position Y coordinates are 0 (FallbackHeightMeters).
                for (int i = 0; i < positions.Length; i++)
                    Assert.That(positions[i].y, Is.EqualTo(0f).Within(1e-4f));

                // No per-feature color (empty FeatureColors) gives opaque white on all vertices.
                for (int i = 0; i < colors.Length; i++)
                    Assert.That(colors[i], Is.EqualTo(new Color32(255, 255, 255, 255)));

                // Triangles hold valid feature-local indices (0-based within range.VertexCount).
                for (int i = range.TriStart; i < range.TriStart + range.TriCount; i++)
                    Assert.That(triangles[i], Is.InRange(0, range.VertexCount - 1));

                // Full async pipeline: the worker builds real Meshes from the job's native buffers.
                var meshes = MeshAssembler.BuildMeshes(
                    positions.AsArray(), normals.AsArray(), uvs.AsArray(), triangles.AsArray(), ranges.AsArray(), colors.AsArray());

                Assert.That(meshes.Length, Is.EqualTo(1));
                Assert.That(meshes[0].vertexCount, Is.EqualTo(range.VertexCount));
                Assert.That(meshes[0].triangles.Length, Is.EqualTo(range.TriCount));

                Object.DestroyImmediate(meshes[0]);
            }
            finally
            {
                featureGeomTypes.Dispose();
                featureRingRange.Dispose();
                rings.Dispose();
                ringPoints.Dispose();
                featureColors.Dispose();
                drawItems.Dispose();
                ground.Dispose();
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                colors.Dispose();
                triangles.Dispose();
                ranges.Dispose();
                rangeFeatureIndices.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ScheduledJob_WithFeatureColor_BroadcastsToAllVertices() => Run(async ct =>
        {
            var coord = new TileCoord(14, 9903, 5145);
            const uint extent = 4096;
            var expected = new Color32(10, 20, 30, 255);

            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.Polygon }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1) }, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, ExteriorRing.Length) }, Allocator.TempJob);
            var ringPoints = new NativeArray<int2>(ExteriorRing, Allocator.TempJob);
            var featureColors = new NativeArray<Color32>(new[] { expected }, Allocator.TempJob);
            var drawItems = new NativeArray<SurfaceDrawItem>(0, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normals = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var colors = new NativeList<Color32>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var ranges = new NativeList<MeshRange>(Allocator.TempJob);
            var rangeFeatureIndices = new NativeList<int>(Allocator.TempJob);
            var ground = new FlatGroundGridProvider();
            try
            {
                var job = new SurfaceBuildJob
                {
                    FeatureGeomTypes = featureGeomTypes,
                    FeatureRingRange = featureRingRange,
                    Rings = rings,
                    RingPoints = ringPoints,
                    FeatureColors = featureColors,
                    DrawItems = drawItems,
                    Coord = coord,
                    Extent = extent,
                    UvScalePerMeter = 0.05f,
                    Grid = GroundTestUtils.FlatGrid(ground, coord),
                    LocalFrame = MercatorPivot.FromLonLat(37, 55),
                    Positions = positions,
                    Normals = normals,
                    Uvs = uvs,
                    Colors = colors,
                    Triangles = triangles,
                    Ranges = ranges,
                    RangeFeatureIndices = rangeFeatureIndices,
                };

                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                Assert.That(colors.Length, Is.EqualTo(positions.Length));
                for (int i = 0; i < colors.Length; i++)
                    Assert.That(colors[i], Is.EqualTo(expected));
            }
            finally
            {
                featureGeomTypes.Dispose();
                featureRingRange.Dispose();
                rings.Dispose();
                ringPoints.Dispose();
                featureColors.Dispose();
                drawItems.Dispose();
                ground.Dispose();
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                colors.Dispose();
                triangles.Dispose();
                ranges.Dispose();
                rangeFeatureIndices.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ScheduledJob_EmitGlobalIndices_TwoFeatures_IndicesSpanWholeBuffer() => Run(async ct =>
        {
            var coord = new TileCoord(14, 9903, 5145);
            const uint extent = 4096;

            // Two independent (non-adjacent) squares; the second is offset along X so it does not intersect the first.
            var squareB = new[]
            {
                new int2(3500, 1000), new int2(3500 + 2000, 1000), new int2(3500 + 2000, 3000), new int2(3500, 3000),
            };

            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.Polygon, MvtGeomType.Polygon }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1), new int2(1, 1) }, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, ExteriorRing.Length), new int2(ExteriorRing.Length, squareB.Length) }, Allocator.TempJob);
            var ringPointsArr = new int2[ExteriorRing.Length + squareB.Length];
            ExteriorRing.CopyTo(ringPointsArr, 0);
            squareB.CopyTo(ringPointsArr, ExteriorRing.Length);
            var ringPoints = new NativeArray<int2>(ringPointsArr, Allocator.TempJob);
            var featureColors = new NativeArray<Color32>(0, Allocator.TempJob);
            var drawItems = new NativeArray<SurfaceDrawItem>(0, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normals = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var colors = new NativeList<Color32>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var ranges = new NativeList<MeshRange>(Allocator.TempJob);
            var rangeFeatureIndices = new NativeList<int>(Allocator.TempJob);
            var ground = new FlatGroundGridProvider();
            try
            {
                var job = new SurfaceBuildJob
                {
                    FeatureGeomTypes = featureGeomTypes,
                    FeatureRingRange = featureRingRange,
                    Rings = rings,
                    RingPoints = ringPoints,
                    FeatureColors = featureColors,
                    DrawItems = drawItems,
                    Coord = coord,
                    Extent = extent,
                    UvScalePerMeter = 0.05f,
                    Grid = GroundTestUtils.FlatGrid(ground, coord),
                    EmitGlobalIndices = true,
                    LocalFrame = MercatorPivot.FromLonLat(37, 55),
                    Positions = positions,
                    Normals = normals,
                    Uvs = uvs,
                    Colors = colors,
                    Triangles = triangles,
                    Ranges = ranges,
                    RangeFeatureIndices = rangeFeatureIndices,
                };

                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                Assert.That(ranges.Length, Is.EqualTo(2), "two separate features give two MeshRanges");
                var rangeB = ranges[1];
                Assert.That(rangeB.VertexStart, Is.GreaterThan(0), "the second feature does not start at the buffer start");

                // Global indices: the second feature's triangle indices point into its own vertex range in the
                // shared buffer (>= VertexStart) instead of restarting at [0, VertexCount) as feature-local
                // indexing would, which would mean EmitGlobalIndices=true had no effect.
                for (int i = rangeB.TriStart; i < rangeB.TriStart + rangeB.TriCount; i++)
                {
                    Assert.That(triangles[i], Is.InRange(rangeB.VertexStart, rangeB.VertexStart + rangeB.VertexCount - 1));
                }

                // The indices also stay valid for the whole buffer, which is what building a single Mesh without
                // splitting into ranges requires.
                for (int i = 0; i < triangles.Length; i++)
                    Assert.That(triangles[i], Is.InRange(0, positions.Length - 1));
            }
            finally
            {
                featureGeomTypes.Dispose();
                featureRingRange.Dispose();
                rings.Dispose();
                ringPoints.Dispose();
                featureColors.Dispose();
                drawItems.Dispose();
                ground.Dispose();
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                colors.Dispose();
                triangles.Dispose();
                ranges.Dispose();
                rangeFeatureIndices.Dispose();
            }
        });

        // ---- helpers --------------------------------------------------------

        private static IEnumerator Run(System.Func<CancellationToken, Awaitable> body, int timeoutSeconds = 30)
        {
            using var cts = new CancellationTokenSource(timeoutSeconds * 1000);
            var task = body(cts.Token);
            while (!task.GetAwaiter().IsCompleted)
            {
                yield return null;
                if (cts.Token.IsCancellationRequested) Assert.Fail($"Test timed out after {timeoutSeconds}s");
            }
            task.GetAwaiter().GetResult();
        }
    }
}
