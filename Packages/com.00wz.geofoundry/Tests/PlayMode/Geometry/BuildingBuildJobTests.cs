using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Geometry
{
    /// <summary>
    /// Schedules <see cref="BuildingBuildJob"/> via <c>Schedule()</c> and awaits it with
    /// <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>, like <see cref="SurfaceBuildJobTests"/>/
    /// <see cref="RoadBuildJobTests"/>. The checks are structural rather than differential: the extrusion math has
    /// no standalone native helper to compare against. The job is geometry-only, so Hidden/TopMeters/BottomMeters
    /// are supplied directly (in production they come from <see cref="BuildingPropertiesJob"/>, which has its own
    /// tests); rendering properties such as <c>colour</c> never enter the job, only
    /// <see cref="BuildingBuildJob.RangeFeatureIndices"/> comes out.
    /// </summary>
    public sealed class BuildingBuildJobTests
    {
        // Square footprint: signed area in Mercator < 0 (exterior); see NativeMvtPolygonHelpersTests.
        private static readonly int2[] Square =
        {
            new(1000, 1000), new(3000, 1000), new(3000, 3000), new(1000, 3000),
        };

        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);

        [UnityTest]
        public IEnumerator ScheduledJob_NoFloor_ProducesRoofPlusWalls() => Run(async ct =>
        {
            var (job, buffers) = MakeJob(top: 10f, bottom: 0f, hidden: false, terrainBase: null);
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                Assert.That(job.Ranges.Length, Is.EqualTo(1), "one feature gives one MeshRange");
                Assert.That(job.RangeFeatureIndices[0], Is.EqualTo(0));

                var range = job.Ranges[0];
                // Square: 2 roof triangles (4 verts, 6 indices), no floor, plus
                // 4 walls of 4 verts/6 indices each.
                Assert.That(range.VertexCount, Is.EqualTo(4 + 4 * 4), "roof(4) + 4 walls×4");
                Assert.That(range.TriCount, Is.EqualTo(6 + 4 * 6), "roof(6) + 4 walls×6");

                bool anyRoofY = false, anyWallBottomY = false;
                for (int i = 0; i < job.Positions.Length; i++)
                {
                    float y = job.Positions[i].y;
                    Assert.That(y, Is.EqualTo(0f).Within(1e-3f).Or.EqualTo(10f).Within(1e-3f),
                        "all vertices are at ground(0) or roof(10) level; no floor is built since bottom=0");
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
        public IEnumerator ScheduledJob_WithFloorAndTerrainBase_OffsetsAllHeights() => Run(async ct =>
        {
            const float terrainBase = 5f;
            const float bottom = 3f;
            const float top = 10f;

            var (job, buffers) = MakeJob(top, bottom, hidden: false, terrainBase: terrainBase);
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                var range = job.Ranges[0];
                // roof(4,6) + floor(4,6) + 4 walls×(4,6).
                Assert.That(range.VertexCount, Is.EqualTo(4 + 4 + 4 * 4));
                Assert.That(range.TriCount, Is.EqualTo(6 + 6 + 4 * 6));

                float floorY = terrainBase + bottom;
                float roofY = terrainBase + top;
                for (int i = 0; i < job.Positions.Length; i++)
                {
                    float y = job.Positions[i].y;
                    Assert.That(y, Is.EqualTo(floorY).Within(1e-3f).Or.EqualTo(roofY).Within(1e-3f),
                        $"all vertices are at floor({floorY}) or roof({roofY}) level");
                }
            }
            finally
            {
                buffers.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ScheduledJob_Hidden_ProducesNoGeometry() => Run(async ct =>
        {
            var (job, buffers) = MakeJob(top: 10f, bottom: 0f, hidden: true, terrainBase: null);
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
        public IEnumerator ScheduledJob_NoFeatureColor_BroadcastsOpaqueWhite() => Run(async ct =>
        {
            var (job, buffers) = MakeJob(top: 10f, bottom: 0f, hidden: false, terrainBase: null);
            try
            {
                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                Assert.That(job.Colors.Length, Is.EqualTo(job.Positions.Length));
                for (int i = 0; i < job.Colors.Length; i++)
                    Assert.That(job.Colors[i], Is.EqualTo(new Color32(255, 255, 255, 255)));
            }
            finally
            {
                buffers.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ScheduledJob_WithFeatureColor_BroadcastsToAllVertices() => Run(async ct =>
        {
            var expected = new Color32(10, 20, 30, 255);
            var (job, buffers) = MakeJob(top: 10f, bottom: 0f, hidden: false, terrainBase: null, color: expected);
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

        // ---- helpers --------------------------------------------------------

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
            private readonly NativeArray<float> _emptyHeights;
            private readonly NativeArray<float> _gridHeights;
            private readonly NativeList<float3> _positions;
            private readonly NativeList<float3> _normals;
            private readonly NativeList<float2> _uvs;
            private readonly NativeList<Color32> _colors;
            private readonly NativeList<int> _triangles;
            private readonly NativeList<MeshRange> _ranges;
            private readonly NativeList<int> _rangeFeatureIndices;

            public JobBuffers(
                NativeArray<MvtGeomType> featureGeomTypes, NativeArray<int2> featureRingRange,
                NativeArray<bool> hidden, NativeArray<float> topMeters,
                NativeArray<float> bottomMeters, NativeArray<Color32> featureColors, NativeArray<int2> rings, NativeArray<int2> ringPoints,
                NativeArray<float> emptyHeights, NativeArray<float> gridHeights, NativeList<float3> positions,
                NativeList<float3> normals, NativeList<float2> uvs, NativeList<Color32> colors, NativeList<int> triangles,
                NativeList<MeshRange> ranges, NativeList<int> rangeFeatureIndices)
            {
                _featureGeomTypes = featureGeomTypes;
                _featureRingRange = featureRingRange;
                _hidden = hidden;
                _topMeters = topMeters;
                _bottomMeters = bottomMeters;
                _featureColors = featureColors;
                _rings = rings;
                _ringPoints = ringPoints;
                _emptyHeights = emptyHeights;
                _gridHeights = gridHeights;
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
                _emptyHeights.Dispose();
                if (_gridHeights.IsCreated) _gridHeights.Dispose();
                _positions.Dispose();
                _normals.Dispose();
                _uvs.Dispose();
                _colors.Dispose();
                _triangles.Dispose();
                _ranges.Dispose();
                _rangeFeatureIndices.Dispose();
            }
        }

        /// <summary>
        /// When <paramref name="terrainBase"/> is null, the job samples without a grid (all 9 slots are empty and the
        /// result is 0). Otherwise a real 2×2 grid with the same height at every corner covers the whole tile
        /// (Bounds = tile.BoundsMeters()), so barycentric sampling always returns <paramref name="terrainBase"/>.
        /// </summary>
        private static (BuildingBuildJob job, JobBuffers buffers) MakeJob(
            float top, float bottom, bool hidden, float? terrainBase, Color32? color = null)
        {
            var featureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.Polygon }, Allocator.TempJob);
            var featureRingRange = new NativeArray<int2>(new[] { new int2(0, 1) }, Allocator.TempJob);
            var hiddenArr = new NativeArray<bool>(new[] { hidden }, Allocator.TempJob);
            var topMeters = new NativeArray<float>(new[] { top }, Allocator.TempJob);
            var bottomMeters = new NativeArray<float>(new[] { bottom }, Allocator.TempJob);
            // color == null gives an empty FeatureColors (the "no per-feature color" sentinel; the job
            // broadcasts opaque white). Otherwise the single feature gets that color.
            var featureColors = color.HasValue
                ? new NativeArray<Color32>(new[] { color.Value }, Allocator.TempJob)
                : new NativeArray<Color32>(0, Allocator.TempJob);
            var rings = new NativeArray<int2>(new[] { new int2(0, Square.Length) }, Allocator.TempJob);
            var ringPoints = new NativeArray<int2>(Square, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normals = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var colors = new NativeList<Color32>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var ranges = new NativeList<MeshRange>(Allocator.TempJob);
            var rangeFeatureIndices = new NativeList<int>(Allocator.TempJob);

            var emptyHeights = new NativeArray<float>(0, Allocator.TempJob);
            var emptyGrid = new BuildingBuildJob.SamplerGridView { Heights = emptyHeights, Bounds = default, VertsPerSide = 1 };

            NativeArray<float> gridHeights = default;
            var gridCenter = emptyGrid;
            if (terrainBase.HasValue)
            {
                gridHeights = new NativeArray<float>(new[] { terrainBase.Value, terrainBase.Value, terrainBase.Value, terrainBase.Value }, Allocator.TempJob);
                gridCenter = new BuildingBuildJob.SamplerGridView
                {
                    Heights = gridHeights,
                    Bounds = Coord.BoundsMeters(),
                    VertsPerSide = 2,
                };
            }

            var job = new BuildingBuildJob
            {
                FeatureGeomTypes = featureGeomTypes,
                FeatureRingRange = featureRingRange,
                Hidden = hiddenArr,
                TopMeters = topMeters,
                BottomMeters = bottomMeters,
                FeatureColors = featureColors,
                Rings = rings,
                RingPoints = ringPoints,
                GridCenter = gridCenter,
                GridNorth = emptyGrid,
                GridSouth = emptyGrid,
                GridEast = emptyGrid,
                GridWest = emptyGrid,
                GridNorthEast = emptyGrid,
                GridNorthWest = emptyGrid,
                GridSouthEast = emptyGrid,
                GridSouthWest = emptyGrid,
                Coord = Coord,
                Extent = 4096,
                LocalFrame = MercatorPivot.FromLonLat(37, 55),
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
                rings, ringPoints, emptyHeights, gridHeights, positions, normals, uvs, colors, triangles, ranges,
                rangeFeatureIndices);

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
