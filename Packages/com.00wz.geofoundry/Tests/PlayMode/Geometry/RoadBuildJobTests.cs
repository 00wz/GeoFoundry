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
    /// Schedules <see cref="RoadBuildJob"/> via <c>Schedule()</c> and awaits it with
    /// <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>, like <see cref="SurfaceBuildJobTests"/>.
    /// </summary>
    public sealed class RoadBuildJobTests
    {
        // 3-point line, 2 segments.
        private static readonly int2[] Line =
        {
            new(1000, 1000), new(2000, 1200), new(3000, 1000),
        };

        [UnityTest]
        public IEnumerator ScheduledJob_SingleLine_WaitNonBlockingAsync_ProducesRibbonGeometry() => Run(async ct =>
        {
            var coord = new TileCoord(14, 9903, 5145);
            const uint extent = 4096;

            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.LineString }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1) }, Allocator.TempJob);
            var widths = new NativeArray<float>(new[] { 6f }, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, Line.Length) }, Allocator.TempJob);
            var ringPoints = new NativeArray<int2>(Line, Allocator.TempJob);
            var featureColors = new NativeArray<Color32>(0, Allocator.TempJob);

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
                var job = new RoadBuildJob
                {
                    FeatureGeomTypes = featureGeomTypes,
                    FeatureRingRange = featureRingRange,
                    Widths = widths,
                    Rings = rings,
                    RingPoints = ringPoints,
                    FeatureColors = featureColors,
                    Coord = coord,
                    Extent = extent,
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

                Assert.That(ranges.Length, Is.EqualTo(1), "one feature gives one MeshRange");
                Assert.That(rangeFeatureIndices.Length, Is.EqualTo(1));
                Assert.That(rangeFeatureIndices[0], Is.EqualTo(0));

                var range = ranges[0];
                // The ribbon is split along grid cells (ProjectTriangle) into a non-indexed triangle soup: one vertex
                // per index; the exact count depends on the split.
                Assert.That(range.VertexCount, Is.GreaterThan(0));
                Assert.That(range.TriCount, Is.EqualTo(range.VertexCount));
                Assert.That(range.TriCount % 3, Is.EqualTo(0));
                Assert.That(positions.Length, Is.EqualTo(normals.Length));
                Assert.That(positions.Length, Is.EqualTo(uvs.Length));
                Assert.That(positions.Length, Is.EqualTo(colors.Length));

                // Flat ground: all position Y coordinates are 0.
                for (int i = 0; i < positions.Length; i++)
                    Assert.That(positions[i].y, Is.EqualTo(0f).Within(1e-4f));

                // No per-feature color (empty FeatureColors) gives opaque white on all vertices.
                for (int i = 0; i < colors.Length; i++)
                    Assert.That(colors[i], Is.EqualTo(new Color32(255, 255, 255, 255)));

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
                widths.Dispose();
                rings.Dispose();
                ringPoints.Dispose();
                featureColors.Dispose();
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
        public IEnumerator NonPositiveWidth_FeatureSkipped_NoGeometryProduced() => Run(async ct =>
        {
            var coord = new TileCoord(14, 9903, 5145);
            const uint extent = 4096;

            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.LineString }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1) }, Allocator.TempJob);
            var widths = new NativeArray<float>(new[] { -1f }, Allocator.TempJob); // do not render (unknown class)
            var rings = new NativeArray<int2>(new[] { new int2(0, Line.Length) }, Allocator.TempJob);
            var ringPoints = new NativeArray<int2>(Line, Allocator.TempJob);
            var featureColors = new NativeArray<Color32>(0, Allocator.TempJob);

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
                var job = new RoadBuildJob
                {
                    FeatureGeomTypes = featureGeomTypes,
                    FeatureRingRange = featureRingRange,
                    Widths = widths,
                    Rings = rings,
                    RingPoints = ringPoints,
                    FeatureColors = featureColors,
                    Coord = coord,
                    Extent = extent,
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
                Assert.That(ranges.Length, Is.EqualTo(0));
                Assert.That(positions.Length, Is.EqualTo(0));
            }
            finally
            {
                featureGeomTypes.Dispose();
                featureRingRange.Dispose();
                widths.Dispose();
                rings.Dispose();
                ringPoints.Dispose();
                featureColors.Dispose();
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

            // The second line is offset along Y so it does not intersect the first.
            var lineB = new[] { new int2(1000, 3000), new int2(2000, 3200), new int2(3000, 3000) };

            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.LineString, MvtGeomType.LineString }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1), new int2(1, 1) }, Allocator.TempJob);
            var widths = new NativeArray<float>(new[] { 6f, 6f }, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, Line.Length), new int2(Line.Length, lineB.Length) }, Allocator.TempJob);
            var ringPointsArr = new int2[Line.Length + lineB.Length];
            Line.CopyTo(ringPointsArr, 0);
            lineB.CopyTo(ringPointsArr, Line.Length);
            var ringPoints = new NativeArray<int2>(ringPointsArr, Allocator.TempJob);
            var featureColors = new NativeArray<Color32>(0, Allocator.TempJob);

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
                var job = new RoadBuildJob
                {
                    FeatureGeomTypes = featureGeomTypes,
                    FeatureRingRange = featureRingRange,
                    Widths = widths,
                    Rings = rings,
                    RingPoints = ringPoints,
                    FeatureColors = featureColors,
                    Coord = coord,
                    Extent = extent,
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

                Assert.That(ranges.Length, Is.EqualTo(2));
                var rangeB = ranges[1];
                Assert.That(rangeB.VertexStart, Is.GreaterThan(0));

                for (int i = rangeB.TriStart; i < rangeB.TriStart + rangeB.TriCount; i++)
                    Assert.That(triangles[i], Is.InRange(rangeB.VertexStart, rangeB.VertexStart + rangeB.VertexCount - 1));

                for (int i = 0; i < triangles.Length; i++)
                    Assert.That(triangles[i], Is.InRange(0, positions.Length - 1));
            }
            finally
            {
                featureGeomTypes.Dispose();
                featureRingRange.Dispose();
                widths.Dispose();
                rings.Dispose();
                ringPoints.Dispose();
                featureColors.Dispose();
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

        // 90° turn at Corner: A → Corner → B.
        private static readonly int2 BendA = new(1000, 1000);
        private static readonly int2 Corner = new(2000, 1000);
        private static readonly int2 BendB = new(2000, 2000);

        [UnityTest]
        public IEnumerator ScheduledJob_RightAngleBend_NoGapOnOuterSide() => Run(async ct =>
        {
            // A single line: independent per-segment quads would leave a wedge-shaped gap on the outer side of the
            // bend; the join must close it.
            await AssertBendCovered(
                new[] { BendA, Corner, BendB },
                new[] { new int2(0, 3) },
                new[] { new int2(0, 1) },
                ct);
        });

        [UnityTest]
        public IEnumerator ScheduledJob_BendSplitIntoTwoFeatures_NoGapAtSharedEnd() => Run(async ct =>
        {
            // The same road split at the turn into two features (typical for MVT: a split OSM way or parts of a
            // MultiLineString); the ends must be stitched.
            await AssertBendCovered(
                new[] { BendA, Corner, Corner, BendB },
                new[] { new int2(0, 2), new int2(2, 2) },
                new[] { new int2(0, 1), new int2(1, 1) },
                ct);
        });

        [UnityTest]
        public IEnumerator ScheduledJob_BendSplitWithOppositeDirections_NoGapAtSharedEnd() => Run(async ct =>
        {
            // The second line runs in the opposite direction (both end at the turn).
            await AssertBendCovered(
                new[] { BendA, Corner, BendB, Corner },
                new[] { new int2(0, 2), new int2(2, 2) },
                new[] { new int2(0, 1), new int2(1, 1) },
                ct);
        });

        // ---- helpers --------------------------------------------------------

        /// <summary>
        /// Builds lines 20 meters wide and asserts that both sides of the bend at <see cref="Corner"/> are covered by
        /// geometry (over the union of all MeshRanges).
        /// </summary>
        private static async Awaitable AssertBendCovered(int2[] points, int2[] ringArr, int2[] featureRings, CancellationToken ct)
        {
            var coord = new TileCoord(14, 9903, 5145);
            const uint extent = 4096;
            const float width = 20f;
            int featureCount = featureRings.Length;

            var geomTypesArr = new MvtGeomType[featureCount];
            var widthsArr = new float[featureCount];
            for (int i = 0; i < featureCount; i++) { geomTypesArr[i] = MvtGeomType.LineString; widthsArr[i] = width; }

            var featureGeomTypes = new NativeArray<MvtGeomType>(geomTypesArr, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(featureRings, Allocator.TempJob);
            var widths = new NativeArray<float>(widthsArr, Allocator.TempJob);
            var rings = new NativeArray<int2>(ringArr, Allocator.TempJob);
            var ringPoints = new NativeArray<int2>(points, Allocator.TempJob);
            var featureColors = new NativeArray<Color32>(0, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normals = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var colors = new NativeList<Color32>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var ranges = new NativeList<MeshRange>(Allocator.TempJob);
            var rangeFeatureIndices = new NativeList<int>(Allocator.TempJob);
            var ground = new FlatGroundGridProvider();
            var frame = MercatorPivot.FromLonLat(37, 55);

            try
            {
                var job = new RoadBuildJob
                {
                    FeatureGeomTypes = featureGeomTypes,
                    FeatureRingRange = featureRingRange,
                    Widths = widths,
                    Rings = rings,
                    RingPoints = ringPoints,
                    FeatureColors = featureColors,
                    Coord = coord,
                    Extent = extent,
                    Grid = GroundTestUtils.FlatGrid(ground, coord),
                    LocalFrame = frame,
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
                Assert.That(ranges.Length, Is.EqualTo(featureCount));

                float2 p0 = ToWorldXZ(BendA, coord, extent, frame);
                float2 p1 = ToWorldXZ(Corner, coord, extent, frame);
                float2 p2 = ToWorldXZ(BendB, coord, extent, frame);
                float2 leftA = Left(math.normalize(p1 - p0));
                float2 leftB = Left(math.normalize(p2 - p1));
                float2 bisector = math.normalize(leftA + leftB);

                // At 90° the outer miter point is halfW·√2 from the centerline, and segment ends stop halfW short of
                // the corner, so a point 0.6·halfW along the bisector lies inside the join but outside both
                // segment quads.
                float d = width * 0.5f * 0.6f;
                Assert.That(IsCovered(p1 + bisector * d, ranges, positions, triangles), Is.True, "the +bisector side of the bend is covered");
                Assert.That(IsCovered(p1 - bisector * d, ranges, positions, triangles), Is.True, "the −bisector side of the bend is covered");
            }
            finally
            {
                featureGeomTypes.Dispose();
                featureRingRange.Dispose();
                widths.Dispose();
                rings.Dispose();
                ringPoints.Dispose();
                featureColors.Dispose();
                ground.Dispose();
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                colors.Dispose();
                triangles.Dispose();
                ranges.Dispose();
                rangeFeatureIndices.Dispose();
            }
        }

        private static float2 ToWorldXZ(int2 local, TileCoord coord, uint extent, MercatorPivot frame) =>
            frame.WorldFromMercator(MvtTileCoords.ToMercator(local, coord, extent)).xz;

        private static float2 Left(float2 v) => new float2(-v.y, v.x);

        private static bool IsCovered(float2 p, NativeList<MeshRange> ranges, NativeList<float3> positions, NativeList<int> triangles)
        {
            foreach (var range in ranges)
            {
                for (int i = range.TriStart; i < range.TriStart + range.TriCount; i += 3)
                {
                    float2 a = positions[range.VertexStart + triangles[i]].xz;
                    float2 b = positions[range.VertexStart + triangles[i + 1]].xz;
                    float2 c = positions[range.VertexStart + triangles[i + 2]].xz;
                    float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
                    bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
                    bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
                    if (!(hasNeg && hasPos)) return true;
                }
            }
            return false;
        }

        private static float Cross(float2 u, float2 v) => u.x * v.y - u.y * v.x;

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
