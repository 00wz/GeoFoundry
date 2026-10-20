using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
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
    /// Schedules <see cref="BuildingTerrainBuildJob"/> via <c>Schedule()</c> and awaits it with
    /// <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>, like
    /// <see cref="BuildingBuildJobTests"/>/<see cref="SurfaceBuildJobTests"/>.
    /// <para>
    /// The key test is <see cref="SharedEdge_TwoFeatures_OnSlopedGrid_MatchExactlyAtCorners"/>: terrain-conforming
    /// extrusion must keep multi-feature buildings on a slope from falling apart (see
    /// <see cref="BuildingTerrainBuildJob"/>), so two features sharing an edge on a non-flat grid get bit-identical
    /// heights at their shared corners.
    /// </para>
    /// </summary>
    public sealed class BuildingTerrainBuildJobTests
    {
        // Square footprint (as in BuildingBuildJobTests): signed area in Mercator < 0 (exterior).
        // It lies entirely on one side of the diagonal of the flat 1×1 grid (tile uv: v > u), so the projection
        // splits neither the roof nor the walls and the vertex count is exact.
        private static readonly int2[] Square =
        {
            new(500, 500), new(1500, 500), new(1500, 1500), new(500, 1500),
        };

        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);
        private const uint Extent = 4096;
        private static readonly MercatorPivot Pivot = MercatorPivot.FromLonLat(37, 55);

        [UnityTest]
        public IEnumerator FlatGround_NoFloor_ProducesRoofPlusWalls() => Run(async ct =>
        {
            using var ground = new FlatGroundGridProvider();
            var (job, buffers) = MakeSingleFeatureJob(Square, top: 10f, bottom: 0f, hidden: false, FlatGrid(ground));
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                Assert.That(job.Ranges.Length, Is.EqualTo(1), "one feature gives one MeshRange");
                Assert.That(job.RangeFeatureIndices[0], Is.EqualTo(0));

                var range = job.Ranges[0];
                // Square: 2 earcut roof triangles, each with its own 3 vertices (no shared indexing, as in
                // SurfaceBuildJob.AppendTriangle; required for per-triangle ground projection). No floor
                // (bottom=0), plus 4 walls of 4 verts/6 indices each.
                Assert.That(range.VertexCount, Is.EqualTo(2 * 3 + 4 * 4), "roof(2×3) + 4 walls×4");
                Assert.That(range.TriCount, Is.EqualTo(2 * 3 + 4 * 6), "roof(2×3) + 4 walls×6");

                bool anyRoofY = false, anyWallBottomY = false;
                for (int i = 0; i < job.Positions.Length; i++)
                {
                    float y = job.Positions[i].y;
                    Assert.That(y, Is.EqualTo(0f).Within(1e-3f).Or.EqualTo(10f).Within(1e-3f),
                        "all vertices are at ground(0) or roof(10) level; flat ground, no floor is built");
                    if (y > 5f) anyRoofY = true; else anyWallBottomY = true;
                }
                Assert.That(anyRoofY, Is.True);
                Assert.That(anyWallBottomY, Is.True);

                var meshes = MeshAssembler.BuildMeshes(
                    job.Positions.AsArray(), job.Normals.AsArray(), job.Uvs.AsArray(),
                    job.Triangles.AsArray(), job.Ranges.AsArray());
                Assert.That(meshes.Length, Is.EqualTo(1));
                Assert.That(meshes[0].vertexCount, Is.EqualTo(range.VertexCount));
                Object.DestroyImmediate(meshes[0]);
            }
            finally
            {
                buffers.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator FlatGround_WithFloor_AddsFloorCap() => Run(async ct =>
        {
            using var ground = new FlatGroundGridProvider();
            var (job, buffers) = MakeSingleFeatureJob(Square, top: 10f, bottom: 3f, hidden: false, FlatGrid(ground));
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                var range = job.Ranges[0];
                // roof(2×3) + floor(2×3) + 4 walls×(4,6).
                Assert.That(range.VertexCount, Is.EqualTo(2 * 3 + 2 * 3 + 4 * 4));
                Assert.That(range.TriCount, Is.EqualTo(2 * 3 + 2 * 3 + 4 * 6));

                for (int i = 0; i < job.Positions.Length; i++)
                {
                    float y = job.Positions[i].y;
                    Assert.That(y, Is.EqualTo(3f).Within(1e-3f).Or.EqualTo(10f).Within(1e-3f),
                        "all vertices are at floor(3) or roof(10) level");
                }
            }
            finally
            {
                buffers.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator Hidden_ProducesNoGeometry() => Run(async ct =>
        {
            using var ground = new FlatGroundGridProvider();
            var (job, buffers) = MakeSingleFeatureJob(Square, top: 10f, bottom: 0f, hidden: true, FlatGrid(ground));
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);
                Assert.That(job.Ranges.Length, Is.EqualTo(0));
                Assert.That(job.Positions.Length, Is.EqualTo(0));
            }
            finally
            {
                buffers.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator WithFeatureColor_BroadcastsToAllVertices() => Run(async ct =>
        {
            var expected = new Color32(10, 20, 30, 255);
            using var ground = new FlatGroundGridProvider();
            var (job, buffers) = MakeSingleFeatureJob(Square, top: 10f, bottom: 0f, hidden: false, FlatGrid(ground), color: expected);
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                Assert.That(job.Colors.Length, Is.EqualTo(job.Positions.Length));
                for (int i = 0; i < job.Colors.Length; i++)
                    Assert.That(job.Colors[i], Is.EqualTo(expected));
            }
            finally
            {
                buffers.Dispose();
            }
        });

        /// <summary>
        /// A footprint extending past the tile edge (MVT buffer): on flat ground the building is clipped to the tile
        /// boundary the same way as on terrain elevation, so no vertex leaves the tile bounds.
        /// </summary>
        [UnityTest]
        public IEnumerator FlatGround_FootprintBeyondTileEdge_ClippedToTile() => Run(async ct =>
        {
            var beyondEdge = new[] { new int2(3000, 500), new int2(5000, 500), new int2(5000, 1500), new int2(3000, 1500) };

            using var ground = new FlatGroundGridProvider();
            var (job, buffers) = MakeSingleFeatureJob(beyondEdge, top: 10f, bottom: 0f, hidden: false, FlatGrid(ground));
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);
                Assert.That(job.Positions.Length, Is.GreaterThan(0), "the part inside the tile is built");

                GroundTestUtils.AssertInsideTile(job.Positions, Coord, Pivot);
            }
            finally
            {
                buffers.Dispose();
            }
        });

        /// <summary>
        /// Two features of one building share an edge (tile-local corners (2500,500)/(2500,2500)) on a non-flat grid
        /// whose height grows along both X and Y. Both features sample terrain elevation at the shared corners through
        /// the same deterministic <see cref="GroundGrid"/>, so the minimum (wall bottom = terrain+bottom) and maximum
        /// (wall top/roof = terrain+top) heights at each shared corner must match bit for bit. With
        /// <see cref="BuildingBuildJob"/> (per-feature <c>SampleTerrainBase</c> = minimum over the whole footprint)
        /// they differ on a slope.
        /// </summary>
        [UnityTest]
        public IEnumerator SharedEdge_TwoFeatures_OnSlopedGrid_MatchExactlyAtCorners() => Run(async ct =>
        {
            // A: (500,500)-(2500,500)-(2500,2500)-(500,2500). B shares the edge (2500,500)-(2500,2500) with A (reversed).
            var footprintA = new[] { new int2(500, 500), new int2(2500, 500), new int2(2500, 2500), new int2(500, 2500) };
            var footprintB = new[] { new int2(2500, 2500), new int2(2500, 500), new int2(4000, 500), new int2(4000, 2500) };

            const float top = 10f;
            const float bottom = 0f;

            var (job, buffers) = MakeTwoFeatureJob(footprintA, footprintB, top, bottom, MakeSlopedGrid());
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);
                Assert.That(job.Ranges.Length, Is.EqualTo(2), "both features produce geometry");

                var rangeA = job.Ranges[0];
                var rangeB = job.Ranges[1];

                double2 mercCorner1 = MvtTileCoords.ToMercator(new int2(2500, 500), Coord, Extent);
                double2 mercCorner2 = MvtTileCoords.ToMercator(new int2(2500, 2500), Coord, Extent);
                float3 corner1 = Pivot.WorldFromMercator(mercCorner1, 0f);
                float3 corner2 = Pivot.WorldFromMercator(mercCorner2, 0f);

                AssertMinMaxYMatch(job.Positions, rangeA, rangeB, corner1, "corner (2500,500)");
                AssertMinMaxYMatch(job.Positions, rangeA, rangeB, corner2, "corner (2500,2500)");
            }
            finally
            {
                buffers.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ScheduledJob_EmitGlobalIndices_TwoFeatures_IndicesSpanWholeBuffer() => Run(async ct =>
        {
            var footprintA = new[] { new int2(500, 500), new int2(2500, 500), new int2(2500, 2500), new int2(500, 2500) };
            var footprintB = new[] { new int2(2500, 2500), new int2(2500, 500), new int2(4000, 500), new int2(4000, 2500) };

            var (job, buffers) = MakeTwoFeatureJob(footprintA, footprintB, top: 10f, bottom: 0f,
                grid: MakeSlopedGrid(), emitGlobalIndices: true);
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);
                Assert.That(job.Ranges.Length, Is.EqualTo(2));

                var rangeB = job.Ranges[1];
                Assert.That(rangeB.VertexStart, Is.GreaterThan(0));

                // Global indices: the second feature's triangles reference its own vertex range in the shared
                // buffer (>= VertexStart) rather than restarting at [0, VertexCount); same pattern as in
                // SurfaceBuildJobTests/RoadBuildJobTests.
                for (int i = rangeB.TriStart; i < rangeB.TriStart + rangeB.TriCount; i++)
                {
                    Assert.That(job.Triangles[i],
                        Is.InRange(rangeB.VertexStart, rangeB.VertexStart + rangeB.VertexCount - 1));
                }

                for (int i = 0; i < job.Triangles.Length; i++)
                    Assert.That(job.Triangles[i], Is.InRange(0, job.Positions.Length - 1));
            }
            finally
            {
                buffers.Dispose();
            }
        });

        // ---- helpers --------------------------------------------------------

        private static GroundGrid FlatGrid(FlatGroundGridProvider ground) => GroundTestUtils.FlatGrid(ground, Coord);

        private static void AssertMinMaxYMatch(
            NativeList<float3> positions, MeshRange rangeA, MeshRange rangeB, float3 cornerXZ, string label)
        {
            const float xzEps = 0.05f;

            (float min, float max, int count) statsA = CollectYStats(positions, rangeA, cornerXZ, xzEps);
            (float min, float max, int count) statsB = CollectYStats(positions, rangeB, cornerXZ, xzEps);

            Assert.That(statsA.count, Is.GreaterThan(0), $"feature A has vertices at {label}");
            Assert.That(statsB.count, Is.GreaterThan(0), $"feature B has vertices at {label}");

            Assert.That(statsA.min, Is.EqualTo(statsB.min).Within(1e-4f),
                $"wall bottom (terrain+bottom) at {label} matches between features A and B");
            Assert.That(statsA.max, Is.EqualTo(statsB.max).Within(1e-4f),
                $"wall top/roof (terrain+top) at {label} matches between features A and B");
        }

        private static (float min, float max, int count) CollectYStats(
            NativeList<float3> positions, MeshRange range, float3 cornerXZ, float xzEps)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            int count = 0;
            for (int i = range.VertexStart; i < range.VertexStart + range.VertexCount; i++)
            {
                float3 p = positions[i];
                float dx = p.x - cornerXZ.x;
                float dz = p.z - cornerXZ.z;
                if (dx * dx + dz * dz > xzEps * xzEps) continue;

                count++;
                if (p.y < min) min = p.y;
                if (p.y > max) max = p.y;
            }
            return (min, max, count);
        }

        /// <summary>Grid with SegmentsPerSide=4 whose height grows along both X and Y: a real (non-degenerate) slope.</summary>
        private static GroundGrid MakeSlopedGrid()
        {
            const int segmentsPerSide = 4;
            int vps = segmentsPerSide + 1;
            var heights = new float[vps * vps];
            var vertNormals = new Vector3[vps * vps];
            for (int j = 0; j < vps; j++)
            for (int i = 0; i < vps; i++)
            {
                heights[j * vps + i] = i * 10f + j * 4f;
                vertNormals[j * vps + i] = Vector3.up;
            }

            // GroundGrid.SampleVertexNormal falls back to a flat normal only when Normals is not created
            // (IsCreated == false); a zero-length array is created and indexing it would fail. A full per-vertex
            // array is needed, as in production (TerrainMeshBuilder).
            return new GroundGrid(segmentsPerSide, new NativeArray<float>(heights, Allocator.TempJob),
                new NativeArray<Vector3>(vertNormals, Allocator.TempJob), Coord.BoundsMeters());
        }

        private readonly struct JobBuffers
        {
            private readonly NativeArray<MvtGeomType> _featureGeomTypes;
            private readonly NativeArray<int2> _featureRingRange;
            private readonly NativeArray<bool> _hidden;
            private readonly NativeArray<float> _topMeters;
            private readonly NativeArray<float> _bottomMeters;
            private readonly NativeArray<Color32> _featureColors;
            private readonly NativeArray<int2> _rings;
            private readonly NativeArray<int2> _ringPoints;
            private readonly NativeArray<float> _gridHeights;
            private readonly NativeArray<Vector3> _gridNormals;
            private readonly NativeList<float3> _positions;
            private readonly NativeList<float3> _normals;
            private readonly NativeList<float2> _uvs;
            private readonly NativeList<Color32> _colors;
            private readonly NativeList<int> _triangles;
            private readonly NativeList<MeshRange> _ranges;
            private readonly NativeList<int> _rangeFeatureIndices;

            public JobBuffers(
                NativeArray<MvtGeomType> featureGeomTypes, NativeArray<int2> featureRingRange,
                NativeArray<bool> hidden, NativeArray<float> topMeters, NativeArray<float> bottomMeters,
                NativeArray<Color32> featureColors, NativeArray<int2> rings, NativeArray<int2> ringPoints,
                NativeArray<float> gridHeights, NativeArray<Vector3> gridNormals,
                NativeList<float3> positions, NativeList<float3> normals, NativeList<float2> uvs,
                NativeList<Color32> colors, NativeList<int> triangles, NativeList<MeshRange> ranges,
                NativeList<int> rangeFeatureIndices)
            {
                _featureGeomTypes = featureGeomTypes;
                _featureRingRange = featureRingRange;
                _hidden = hidden;
                _topMeters = topMeters;
                _bottomMeters = bottomMeters;
                _featureColors = featureColors;
                _rings = rings;
                _ringPoints = ringPoints;
                _gridHeights = gridHeights;
                _gridNormals = gridNormals;
                _positions = positions;
                _normals = normals;
                _uvs = uvs;
                _colors = colors;
                _triangles = triangles;
                _ranges = ranges;
                _rangeFeatureIndices = rangeFeatureIndices;
            }

            public void Dispose()
            {
                _featureGeomTypes.Dispose();
                _featureRingRange.Dispose();
                _hidden.Dispose();
                _topMeters.Dispose();
                _bottomMeters.Dispose();
                _featureColors.Dispose();
                _rings.Dispose();
                _ringPoints.Dispose();
                if (_gridHeights.IsCreated) _gridHeights.Dispose();
                if (_gridNormals.IsCreated) _gridNormals.Dispose();
                _positions.Dispose();
                _normals.Dispose();
                _uvs.Dispose();
                _colors.Dispose();
                _triangles.Dispose();
                _ranges.Dispose();
                _rangeFeatureIndices.Dispose();
            }
        }

        /// <summary><paramref name="grid"/> is borrowed; the job buffers do not dispose it.</summary>
        private static (BuildingTerrainBuildJob job, JobBuffers buffers) MakeSingleFeatureJob(
            int2[] footprint, float top, float bottom, bool hidden, GroundGrid grid, Color32? color = null)
        {
            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.Polygon }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1) }, Allocator.TempJob);
            var hiddenArr = new NativeArray<bool>(new[] { hidden }, Allocator.TempJob);
            var topMeters = new NativeArray<float>(new[] { top }, Allocator.TempJob);
            var bottomMeters = new NativeArray<float>(new[] { bottom }, Allocator.TempJob);
            var featureColors = color.HasValue
                ? new NativeArray<Color32>(new[] { color.Value }, Allocator.TempJob)
                : new NativeArray<Color32>(0, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, footprint.Length) }, Allocator.TempJob);
            var ringPoints = new NativeArray<int2>(footprint, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normals = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var colors = new NativeList<Color32>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var ranges = new NativeList<MeshRange>(Allocator.TempJob);
            var rangeFeatureIndices = new NativeList<int>(Allocator.TempJob);

            var job = new BuildingTerrainBuildJob
            {
                FeatureGeomTypes = featureGeomTypes,
                FeatureRingRange = featureRingRange,
                Hidden = hiddenArr,
                TopMeters = topMeters,
                BottomMeters = bottomMeters,
                FeatureColors = featureColors,
                Rings = rings,
                RingPoints = ringPoints,
                Coord = Coord,
                Extent = Extent,
                UvScalePerMeter = 0.05f,
                Grid = grid,
                LocalFrame = Pivot,
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Colors = colors,
                Triangles = triangles,
                Ranges = ranges,
                RangeFeatureIndices = rangeFeatureIndices,
            };

            var buffers = new JobBuffers(
                featureGeomTypes, featureRingRange, hiddenArr, topMeters, bottomMeters, featureColors,
                rings, ringPoints, default, default, positions, normals, uvs, colors,
                triangles, ranges, rangeFeatureIndices);

            return (job, buffers);
        }

        private static (BuildingTerrainBuildJob job, JobBuffers buffers) MakeTwoFeatureJob(
            int2[] footprintA, int2[] footprintB, float top, float bottom, GroundGrid grid,
            bool emitGlobalIndices = false)
        {
            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.Polygon, MvtGeomType.Polygon }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1), new int2(1, 1) }, Allocator.TempJob);
            var hiddenArr = new NativeArray<bool>(new[] { false, false }, Allocator.TempJob);
            var topMeters = new NativeArray<float>(new[] { top, top }, Allocator.TempJob);
            var bottomMeters = new NativeArray<float>(new[] { bottom, bottom }, Allocator.TempJob);
            var featureColors = new NativeArray<Color32>(0, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, footprintA.Length), new int2(footprintA.Length, footprintB.Length) }, Allocator.TempJob);
            var ringPointsArr = new int2[footprintA.Length + footprintB.Length];
            footprintA.CopyTo(ringPointsArr, 0);
            footprintB.CopyTo(ringPointsArr, footprintA.Length);
            var ringPoints = new NativeArray<int2>(ringPointsArr, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normals = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var colors = new NativeList<Color32>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var ranges = new NativeList<MeshRange>(Allocator.TempJob);
            var rangeFeatureIndices = new NativeList<int>(Allocator.TempJob);

            var job = new BuildingTerrainBuildJob
            {
                FeatureGeomTypes = featureGeomTypes,
                FeatureRingRange = featureRingRange,
                Hidden = hiddenArr,
                TopMeters = topMeters,
                BottomMeters = bottomMeters,
                FeatureColors = featureColors,
                Rings = rings,
                RingPoints = ringPoints,
                Coord = Coord,
                Extent = Extent,
                UvScalePerMeter = 0.05f,
                Grid = grid,
                EmitGlobalIndices = emitGlobalIndices,
                LocalFrame = Pivot,
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Colors = colors,
                Triangles = triangles,
                Ranges = ranges,
                RangeFeatureIndices = rangeFeatureIndices,
            };

            var buffers = new JobBuffers(
                featureGeomTypes, featureRingRange, hiddenArr, topMeters, bottomMeters, featureColors,
                rings, ringPoints, grid.Heights, grid.Normals, positions, normals, uvs, colors,
                triangles, ranges, rangeFeatureIndices);

            return (job, buffers);
        }

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
