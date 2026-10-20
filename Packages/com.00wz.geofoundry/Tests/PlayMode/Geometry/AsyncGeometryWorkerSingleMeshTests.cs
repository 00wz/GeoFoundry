using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using GeoFoundry.Ground;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Geometry
{
    /// <summary>
    /// Integration tests for the "one Mesh per tile layer" methods
    /// (<see cref="AsyncGeometryWorker.BuildSurfaceMeshAsync"/>/<see cref="AsyncGeometryWorker.BuildRoadMeshAsync"/>/
    /// <see cref="AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/>). Unlike the *BuildJobTests, which schedule
    /// jobs directly on raw buffers, these go through the full public API with a real
    /// <see cref="NativeMvtLayer"/>, which also covers the Advanced Mesh API assembly of the final
    /// <see cref="Mesh"/> (<c>FillMeshDataJob</c> inside <c>MeshAssembler.BuildSingleMeshAsync</c>).
    /// <para>
    /// Differential check: the merged mesh must contain exactly the same total geometry as the per-feature path
    /// (<c>*MeshesAsync</c>) on the same input. Both jobs are deterministic and differ only in the indexing mode
    /// (<c>EmitGlobalIndices</c>), so the vertex and index counts must match.
    /// </para>
    /// </summary>
    public sealed class AsyncGeometryWorkerSingleMeshTests
    {
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);
        private const uint Extent = 4096;
        private static readonly MercatorPivot Pivot = MercatorPivot.FromLonLat(37, 55);

        private static readonly int2[] SquareA = { new(500, 500), new(2500, 500), new(2500, 2500), new(500, 2500) };
        private static readonly int2[] SquareB = { new(3000, 500), new(5000, 500), new(5000, 2500), new(3000, 2500) };
        private static readonly int2[] LineA = { new(500, 500), new(2500, 700) };
        private static readonly int2[] LineB = { new(500, 2500), new(2500, 2700) };

        [UnityTest]
        public IEnumerator BuildSurfaceMeshAsync_TwoFeatures_MatchesSumOfPerFeatureMeshes() => Run(async ct =>
        {
            using var tile = MakeTile(MvtGeomType.Polygon, SquareA, SquareB);
            using var ground = new FlatGroundGridProvider();
            var layer = tile.GetLayer(0);
            var grid = GroundTestUtils.FlatGrid(ground, Coord);

            var mesh = await AsyncGeometryWorker.BuildSurfaceMeshAsync(
                layer, Coord, grid, Pivot, 0.05f, default(NativeArray<Color32>), ct);
            var perFeature = await AsyncGeometryWorker.BuildSurfaceMeshesAsync(
                layer, Coord, grid, Pivot, 0.05f, default(NativeArray<Color32>), ct);

            AssertMergedMatchesSum(mesh, perFeature);
        });

        [UnityTest]
        public IEnumerator BuildRoadMeshAsync_TwoFeatures_MatchesSumOfPerFeatureMeshes() => Run(async ct =>
        {
            using var tile = MakeTile(MvtGeomType.LineString, LineA, LineB);
            using var ground = new FlatGroundGridProvider();
            var layer = tile.GetLayer(0);
            var grid = GroundTestUtils.FlatGrid(ground, Coord);

            var mesh = await AsyncGeometryWorker.BuildRoadMeshAsync(
                layer, _ => 6f, Coord, grid, Pivot, default(NativeArray<Color32>), ct);
            var perFeature = await AsyncGeometryWorker.BuildRoadMeshesAsync(
                layer, _ => 6f, Coord, grid, Pivot, default(NativeArray<Color32>), ct);

            AssertMergedMatchesSum(mesh, perFeature);
        });

        [UnityTest]
        public IEnumerator BuildBuildingTerrainMeshAsync_TwoFeatures_MatchesSumOfPerFeatureMeshes() => Run(async ct =>
        {
            using var tile = MakeTile(MvtGeomType.Polygon, SquareA, SquareB);
            using var ground = new FlatGroundGridProvider();
            var layer = tile.GetLayer(0);
            var grid = GroundTestUtils.FlatGrid(ground, Coord);

            var hidden = new NativeArray<bool>(new[] { false, false }, Allocator.Persistent);
            var topMeters = new NativeArray<float>(new[] { 10f, 10f }, Allocator.Persistent);
            var bottomMeters = new NativeArray<float>(new[] { 0f, 0f }, Allocator.Persistent);
            try
            {
                var mesh = await AsyncGeometryWorker.BuildBuildingTerrainMeshAsync(
                    layer, hidden, topMeters, bottomMeters, default(NativeArray<Color32>), grid, Coord, Pivot, ct);
                var perFeature = await AsyncGeometryWorker.BuildBuildingTerrainMeshesAsync(
                    layer, hidden, topMeters, bottomMeters, default(NativeArray<Color32>), grid, Coord, Pivot, ct);

                AssertMergedMatchesSum(mesh, perFeature);
            }
            finally
            {
                hidden.Dispose();
                topMeters.Dispose();
                bottomMeters.Dispose();
            }
        });

        // ---- flat ground: clipping to the tile, grid is required -------------------

        /// <summary>
        /// <see cref="SquareB"/> extends past the east edge of the tile (x up to 5000 with extent 4096): on flat
        /// ground the surface is clipped to the tile boundary the same way as on terrain elevation.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildSurfaceMeshAsync_FlatGround_PolygonBeyondTileEdge_ClippedToTile() => Run(async ct =>
        {
            using var tile = MakeTile(MvtGeomType.Polygon, SquareA, SquareB);
            using var ground = new FlatGroundGridProvider();
            var mesh = await AsyncGeometryWorker.BuildSurfaceMeshAsync(
                tile.GetLayer(0), Coord, GroundTestUtils.FlatGrid(ground, Coord), Pivot, 0.05f,
                default(NativeArray<Color32>), ct);
            try
            {
                Assert.That(mesh, Is.Not.Null);
                GroundTestUtils.AssertInsideTile(mesh.vertices, Coord, Pivot);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        });

        /// <summary>
        /// A horizontal line crossing the east edge of the tile: the ribbon is clipped to the boundary (its width
        /// extends across the line, i.e. along the edge, so it does not cross the edge).
        /// </summary>
        [UnityTest]
        public IEnumerator BuildRoadMeshAsync_FlatGround_LineBeyondTileEdge_ClippedToTile() => Run(async ct =>
        {
            var beyondEdge = new[] { new int2(3000, 700), new int2(5000, 700) };
            using var tile = MakeTile(MvtGeomType.LineString, LineA, beyondEdge);
            using var ground = new FlatGroundGridProvider();
            var mesh = await AsyncGeometryWorker.BuildRoadMeshAsync(
                tile.GetLayer(0), _ => 6f, Coord, GroundTestUtils.FlatGrid(ground, Coord), Pivot,
                default(NativeArray<Color32>), ct);
            try
            {
                Assert.That(mesh, Is.Not.Null);
                GroundTestUtils.AssertInsideTile(mesh.vertices, Coord, Pivot);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        });

        /// <summary>The tile grid is required: without it the worker throws instead of building geometry without ground.</summary>
        [UnityTest]
        public IEnumerator BuildSurfaceMeshAsync_DefaultGrid_Throws() => Run(async ct =>
        {
            using var tile = MakeTile(MvtGeomType.Polygon, SquareA, SquareB);
            try
            {
                await AsyncGeometryWorker.BuildSurfaceMeshAsync(
                    tile.GetLayer(0), Coord, default(GroundGrid), Pivot, 0.05f, default(NativeArray<Color32>), ct);
                Assert.Fail("ArgumentException expected");
            }
            catch (System.ArgumentException ex)
            {
                Assert.That(ex.ParamName, Is.EqualTo("grid"));
            }
        });

        /// <summary>
        /// The 9-grid building path reads all neighbors: if any slot is empty it throws instead of passing an
        /// uncreated NativeArray to the job. Covers both no neighbors at all (<c>LoadNeighborGrids</c> disabled)
        /// and partially filled neighbors (North present, East missing).
        /// </summary>
        [UnityTest]
        public IEnumerator BuildBuildingMeshesAsync_WithoutNeighbors_Throws() =>
            AssertBuildBuildingMeshesThrows(northValid: false, expectedSlot: "North");

        [UnityTest]
        public IEnumerator BuildBuildingMeshesAsync_PartialNeighbors_Throws() =>
            AssertBuildBuildingMeshesThrows(northValid: true, expectedSlot: "East");

        private IEnumerator AssertBuildBuildingMeshesThrows(bool northValid, string expectedSlot) => Run(async ct =>
        {
            using var tile = MakeTile(MvtGeomType.Polygon, SquareA, SquareB);
            using var ground = new FlatGroundGridProvider();
            var grids = new TileGroundGrids(
                GroundTestUtils.FlatGrid(ground, Coord),
                northValid ? GroundTestUtils.FlatGrid(ground, Coord.Offset(0, -1)) : default,
                GroundTestUtils.FlatGrid(ground, Coord.Offset(0, +1)),
                default, // East
                GroundTestUtils.FlatGrid(ground, Coord.Offset(-1, 0)),
                GroundTestUtils.FlatGrid(ground, Coord.Offset(+1, -1)),
                GroundTestUtils.FlatGrid(ground, Coord.Offset(-1, -1)),
                GroundTestUtils.FlatGrid(ground, Coord.Offset(+1, +1)),
                GroundTestUtils.FlatGrid(ground, Coord.Offset(-1, +1)));

            var hidden = new NativeArray<bool>(new[] { false, false }, Allocator.Persistent);
            var topMeters = new NativeArray<float>(new[] { 10f, 10f }, Allocator.Persistent);
            var bottomMeters = new NativeArray<float>(new[] { 0f, 0f }, Allocator.Persistent);
            try
            {
                await AsyncGeometryWorker.BuildBuildingMeshesAsync(
                    tile.GetLayer(0), hidden, topMeters, bottomMeters, default(NativeArray<Color32>),
                    grids, Coord, Pivot, ct);
                Assert.Fail("ArgumentException expected");
            }
            catch (System.ArgumentException ex)
            {
                Assert.That(ex.ParamName, Is.EqualTo("grids"));
                Assert.That(ex.Message, Does.Contain(expectedSlot), "the message names the first empty slot");
            }
            finally
            {
                hidden.Dispose();
                topMeters.Dispose();
                bottomMeters.Dispose();
            }
        });

        // ---- compact layout (SNorm8 normals, adaptive index format) ------------

        [UnityTest]
        public IEnumerator BuildSurfaceMeshAsync_SmallLayer_UsesCompactLayout() => Run(async ct =>
        {
            using var tile = MakeTile(MvtGeomType.Polygon, SquareA, SquareB);
            using var ground = new FlatGroundGridProvider();
            var mesh = await AsyncGeometryWorker.BuildSurfaceMeshAsync(
                tile.GetLayer(0), Coord, GroundTestUtils.FlatGrid(ground, Coord), Pivot, 0.05f,
                default(NativeArray<Color32>), ct);
            try
            {
                Assert.That(mesh, Is.Not.Null);
                Assert.That(mesh.indexFormat, Is.EqualTo(UnityEngine.Rendering.IndexFormat.UInt16));
                AssertCompactNormal(mesh);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        });

        [TestCase(65535, UnityEngine.Rendering.IndexFormat.UInt16)]
        [TestCase(65536, UnityEngine.Rendering.IndexFormat.UInt32)]
        public void BuildMeshes_IndexFormatBoundary_AndNormalsRoundTrip(int vertCount, UnityEngine.Rendering.IndexFormat expected)
        {
            int indexCount = (vertCount + 2) / 3 * 3;
            var positions = new NativeArray<float3>(vertCount, Allocator.TempJob);
            var normals = new NativeArray<float3>(vertCount, Allocator.TempJob);
            var uvs = new NativeArray<float2>(vertCount, Allocator.TempJob);
            var triangles = new NativeArray<int>(indexCount, Allocator.TempJob);
            var ranges = new NativeArray<MeshRange>(1, Allocator.TempJob);
            Mesh[] meshes = null;
            try
            {
                for (int i = 0; i < vertCount; i++)
                {
                    positions[i] = new float3(i % 256, 0f, i / 256);
                    normals[i] = math.normalize(new float3(math.sin(i * 0.1f), 1f, math.cos(i * 0.1f)));
                }
                // The last index is vertCount-1: checks that the maximum index survives without truncation.
                for (int i = 0; i < indexCount; i++) triangles[i] = math.min(i, vertCount - 1);
                ranges[0] = new MeshRange(0, vertCount, 0, indexCount);

                meshes = MeshAssembler.BuildMeshes(positions, normals, uvs, triangles, ranges);
                var mesh = meshes[0];

                Assert.That(mesh.indexFormat, Is.EqualTo(expected));
                AssertCompactNormal(mesh);

                var outIndices = mesh.triangles;
                Assert.That(outIndices.Length, Is.EqualTo(indexCount));
                for (int i = 0; i < indexCount; i++)
                    if (outIndices[i] != triangles[i])
                        Assert.Fail($"index[{i}]: expected {triangles[i]}, got {outIndices[i]}");

                const float tolerance = 1f / 127f + 1e-4f;
                var outNormals = mesh.normals;
                for (int i = 0; i < vertCount; i++)
                {
                    Vector3 d = outNormals[i] - (Vector3)normals[i];
                    if (Mathf.Abs(d.x) > tolerance || Mathf.Abs(d.y) > tolerance || Mathf.Abs(d.z) > tolerance)
                        Assert.Fail($"normal[{i}]: expected {normals[i]}, got {outNormals[i]}");
                }
            }
            finally
            {
                if (meshes != null)
                    foreach (var m in meshes)
                        Object.DestroyImmediate(m);
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                triangles.Dispose();
                ranges.Dispose();
            }
        }

        // ---- helpers --------------------------------------------------------

        private static void AssertCompactNormal(Mesh mesh)
        {
            Assert.That(mesh.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.Normal),
                Is.EqualTo(UnityEngine.Rendering.VertexAttributeFormat.SNorm8));
            Assert.That(mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.Normal), Is.EqualTo(4));
        }

        private static void AssertMergedMatchesSum(Mesh mesh, GeometryResult? perFeature)
        {
            try
            {
                Assert.That(mesh, Is.Not.Null, "merged mesh must not be null");
                Assert.That(mesh.subMeshCount, Is.EqualTo(1), "one submesh: all geometry is merged");
                Assert.That(perFeature, Is.Not.Null);
                Assert.That(perFeature.Value.Meshes.Length, Is.EqualTo(2), "two features give two per-feature meshes (ground truth)");

                int expectedVerts = perFeature.Value.Meshes[0].vertexCount + perFeature.Value.Meshes[1].vertexCount;
                int expectedTris = perFeature.Value.Meshes[0].triangles.Length + perFeature.Value.Meshes[1].triangles.Length;

                Assert.That(mesh.vertexCount, Is.EqualTo(expectedVerts),
                    "merged mesh vertex count equals the sum over both features");
                Assert.That(mesh.triangles.Length, Is.EqualTo(expectedTris),
                    "merged mesh index count equals the sum over both features");
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
                if (perFeature != null)
                    foreach (var m in perFeature.Value.Meshes)
                        Object.DestroyImmediate(m);
            }
        }

        /// <summary>
        /// Builds one layer "test" with two features of the given geometry type and no tags. Both Polygon (footprint)
        /// and LineString (points along the line) use the same CSR layout (Rings/RingPoints); see
        /// <see cref="RoadBuildJob"/>/<see cref="SurfaceBuildJob"/>.
        /// </summary>
        private static NativeMvtTile MakeTile(MvtGeomType geomType, int2[] featureA, int2[] featureB)
        {
            var ringPointsArr = new int2[featureA.Length + featureB.Length];
            featureA.CopyTo(ringPointsArr, 0);
            featureB.CopyTo(ringPointsArr, featureA.Length);

            return new NativeMvtTile
            {
                LayerNames = new NativeArray<FixedString64Bytes>(new[] { (FixedString64Bytes)"test" }, Allocator.Persistent),
                LayerVersions = new NativeArray<uint>(new uint[] { 2 }, Allocator.Persistent),
                LayerExtents = new NativeArray<uint>(new[] { Extent }, Allocator.Persistent),
                LayerFeatureRange = new NativeArray<int2>(new[] { new int2(0, 2) }, Allocator.Persistent),

                FeatureIds = new NativeArray<ulong>(new ulong[] { 1, 2 }, Allocator.Persistent),
                FeatureGeomTypes = new NativeArray<MvtGeomType>(new[] { geomType, geomType }, Allocator.Persistent),
                FeatureTagRange = new NativeArray<int2>(new[] { new int2(0, 0), new int2(0, 0) }, Allocator.Persistent),
                FeatureRingRange = new NativeArray<int2>(new[] { new int2(0, 1), new int2(1, 1) }, Allocator.Persistent),

                Tags = new NativeArray<int2>(0, Allocator.Persistent),
                Keys = new NativeArray<FixedString64Bytes>(0, Allocator.Persistent),
                Values = new NativeArray<MvtValue>(0, Allocator.Persistent),

                Rings = new NativeArray<int2>(new[] { new int2(0, featureA.Length), new int2(featureA.Length, featureB.Length) }, Allocator.Persistent),
                RingPoints = new NativeArray<int2>(ringPointsArr, Allocator.Persistent),
            };
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
