using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Geometry;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Geometry
{
    /// <summary>
    /// Schedules <see cref="TerrainBuildJob"/> via <c>Schedule()</c> and awaits it with
    /// <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>, like
    /// <see cref="SurfaceBuildJobTests"/>/<see cref="BuildingBuildJobTests"/>. Uses a small synthetic 2×2-segment
    /// <see cref="GroundGrid"/> with known heights/normals and compares the job output with direct calls to
    /// <see cref="GroundGrid.WorldFromGridCorner"/> (the same math the job uses).
    /// </summary>
    public sealed class TerrainBuildJobTests
    {
        private const int N = 2; // 2×2 segments: 3×3 = 9 vertices, 2×2×6 = 24 indices.
        private static readonly MercatorPivot Pivot = MercatorPivot.FromLonLat(37, 55);

        [UnityTest]
        public IEnumerator ScheduledJob_ProducesExpectedGridMeshAndNormals() => Run(async ct =>
        {
            int vps = N + 1;
            var heights = new NativeArray<float>(vps * vps, Allocator.TempJob);
            var normals = new NativeArray<Vector3>(vps * vps, Allocator.TempJob);
            for (int j = 0; j < vps; j++)
            for (int i = 0; i < vps; i++)
            {
                int idx = j * vps + i;
                heights[idx] = i + j * 10f;
                // Varied (not all +Y) normals to check a real pass-through rather than a constant.
                normals[idx] = new Vector3(0.1f * i, 1f, 0.1f * j).normalized;
            }

            var bounds = new double4(0, 0, 100, 100);
            var grid = new GroundGrid(N, heights, normals, bounds);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normalsOut = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            try
            {
                var job = new TerrainBuildJob
                {
                    Grid = grid,
                    LocalFrame = Pivot,
                    Positions = positions,
                    Normals = normalsOut,
                    Uvs = uvs,
                    Triangles = triangles,
                };

                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                Assert.That(positions.Length, Is.EqualTo(vps * vps));
                Assert.That(normalsOut.Length, Is.EqualTo(vps * vps));
                Assert.That(uvs.Length, Is.EqualTo(vps * vps));
                Assert.That(triangles.Length, Is.EqualTo(N * N * 6));

                for (int j = 0; j < vps; j++)
                for (int i = 0; i < vps; i++)
                {
                    int idx = j * vps + i;
                    Vector3 expectedPos = grid.WorldFromGridCorner(i, j, Pivot);
                    Assert.That(positions[idx].x, Is.EqualTo(expectedPos.x).Within(1e-3f));
                    Assert.That(positions[idx].y, Is.EqualTo(expectedPos.y).Within(1e-3f));
                    Assert.That(positions[idx].z, Is.EqualTo(expectedPos.z).Within(1e-3f));

                    Vector3 expectedNormal = normals[idx];
                    Assert.That(normalsOut[idx].x, Is.EqualTo(expectedNormal.x).Within(1e-5f));
                    Assert.That(normalsOut[idx].y, Is.EqualTo(expectedNormal.y).Within(1e-5f));
                    Assert.That(normalsOut[idx].z, Is.EqualTo(expectedNormal.z).Within(1e-5f));

                    Assert.That(uvs[idx].x, Is.EqualTo((float)i / N).Within(1e-5f));
                    Assert.That(uvs[idx].y, Is.EqualTo((float)j / N).Within(1e-5f));
                }

                for (int i = 0; i < triangles.Length; i++)
                    Assert.That(triangles[i], Is.InRange(0, vps * vps - 1));

                // Full async pipeline: a single MeshRange over the whole buffer (see BuildTerrainMeshAsync).
                var ranges = new NativeArray<MeshRange>(new[] { new MeshRange(0, positions.Length, 0, triangles.Length) }, Allocator.Temp);
                var meshes = MeshAssembler.BuildMeshes(
                    positions.AsArray(), normalsOut.AsArray(), uvs.AsArray(), triangles.AsArray(), ranges);
                ranges.Dispose();

                Assert.That(meshes.Length, Is.EqualTo(1));
                Assert.That(meshes[0].vertexCount, Is.EqualTo(vps * vps));
                Assert.That(meshes[0].triangles.Length, Is.EqualTo(N * N * 6));
                Object.DestroyImmediate(meshes[0]);
            }
            finally
            {
                heights.Dispose();
                normals.Dispose();
                positions.Dispose();
                normalsOut.Dispose();
                uvs.Dispose();
                triangles.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ScheduledJob_NoNormalsCreated_FallsBackToUp() => Run(async ct =>
        {
            int vps = N + 1;
            var heights = new NativeArray<float>(vps * vps, Allocator.TempJob);
            var bounds = new double4(0, 0, 100, 100);
            // "No normals" for the job is a valid zero-length array, not default: the Job System requires all
            // NativeContainer fields (including those nested in GroundGrid) to be constructed at Schedule() time;
            // default(NativeArray<Vector3>) in a job field throws InvalidOperationException at Schedule() instead
            // of reaching the fallback branch in Execute().
            var emptyNormals = new NativeArray<Vector3>(0, Allocator.TempJob);
            var grid = new GroundGrid(N, heights, emptyNormals, bounds);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var normalsOut = new NativeList<float3>(Allocator.TempJob);
            var uvs = new NativeList<float2>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            try
            {
                var job = new TerrainBuildJob
                {
                    Grid = grid,
                    LocalFrame = Pivot,
                    Positions = positions,
                    Normals = normalsOut,
                    Uvs = uvs,
                    Triangles = triangles,
                };

                JobHandle handle = job.Schedule();
                bool completed = await handle.WaitNonBlockingAsync(ct);
                Assert.That(completed, Is.True);

                for (int i = 0; i < normalsOut.Length; i++)
                {
                    Assert.That(normalsOut[i].x, Is.EqualTo(0f).Within(1e-6f));
                    Assert.That(normalsOut[i].y, Is.EqualTo(1f).Within(1e-6f));
                    Assert.That(normalsOut[i].z, Is.EqualTo(0f).Within(1e-6f));
                }
            }
            finally
            {
                heights.Dispose();
                emptyNormals.Dispose();
                positions.Dispose();
                normalsOut.Dispose();
                uvs.Dispose();
                triangles.Dispose();
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
