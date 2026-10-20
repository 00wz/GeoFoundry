using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Geometry;
using GeoFoundry.Ground;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Ground
{
    /// <summary>
    /// <see cref="FlatGroundGridProvider"/> provides ground without terrain elevation: a 1×1 grid at the fallback
    /// height over the tile bounds, returned synchronously. The shared arrays are freed by the last handle, even
    /// after the provider is disposed.
    /// </summary>
    public sealed class FlatGroundGridProviderTests
    {
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);
        private static readonly MercatorPivot Pivot = MercatorPivot.FromLonLat(37, 55);

        [Test]
        public void TryGet_FlatQuadOverTileBounds()
        {
            using var ground = new FlatGroundGridProvider();

            Assert.That(ground.TryGet(Coord, out var grid), Is.True, "flat ground exists everywhere");
            Assert.That(grid.SegmentsPerSide, Is.EqualTo(1));
            Assert.That(grid.BoundsMeters, Is.EqualTo(Coord.BoundsMeters()));
            Assert.That(grid.Heights.Length, Is.EqualTo(4));
            Assert.That(grid.Normals.Length, Is.EqualTo(4),
                "per-vertex normals are required: SampleVertexNormal indexes the created array");

            foreach (var uv in new[] { new float2(0, 0), new float2(1, 1), new float2(0.3f, 0.7f), new float2(0.9f, 0.1f) })
            {
                Assert.That(grid.SampleHeightAtUv(uv), Is.EqualTo(GroundHeightDefaults.FallbackHeightMeters));
                Assert.That(grid.SampleVertexNormal(uv, Pivot).y, Is.EqualTo(1f).Within(1e-5f), "normal is +Y");
            }
        }

        [Test]
        public void TryGet_DifferentTiles_ShareHeightsButNotBounds()
        {
            using var ground = new FlatGroundGridProvider();
            var other = Coord.Offset(1, 0);

            ground.TryGet(Coord, out var a);
            ground.TryGet(other, out var b);

            Assert.That(b.BoundsMeters, Is.EqualTo(other.BoundsMeters()));
            Assert.That(a.BoundsMeters, Is.Not.EqualTo(b.BoundsMeters));
            Assert.That(ground.MemoryBytes, Is.EqualTo(4 * sizeof(float) + 4 * 3 * sizeof(float)),
                "one pair of arrays per provider, no per-tile allocations");
        }

        [Test]
        public void AcquireAsync_CompletesSynchronously()
        {
            using var ground = new FlatGroundGridProvider();

            var task = ground.AcquireAsync(Coord, CancellationToken.None);
            Assert.That(task.GetAwaiter().IsCompleted, Is.True);

            var handle = task.GetAwaiter().GetResult();
            Assert.That(handle.Grid.BoundsMeters, Is.EqualTo(Coord.BoundsMeters()));
            handle.Dispose();
        }

        [Test]
        public void Dispose_WithLiveHandle_KeepsArraysUntilLastRelease()
        {
            var ground = new FlatGroundGridProvider();
            var handle = ground.AcquireAsync(Coord, CancellationToken.None).GetAwaiter().GetResult();

            ground.Dispose();
            Assert.That(ground.MemoryBytes, Is.GreaterThan(0), "a live handle keeps the arrays");
            Assert.That(handle.Grid.Heights[0], Is.EqualTo(GroundHeightDefaults.FallbackHeightMeters),
                "a live handle's grid is readable after the provider is disposed");
            Assert.That(ground.TryGet(Coord, out _), Is.False, "no new grids after Dispose");

            handle.Dispose();
            Assert.That(ground.MemoryBytes, Is.Zero, "the last handle freed the arrays");
        }

        [Test]
        public void AcquireAsync_AfterDispose_ThrowsOnAwait()
        {
            var ground = new FlatGroundGridProvider();
            ground.Dispose();

            var task = ground.AcquireAsync(Coord, CancellationToken.None);
            Assert.Throws<ObjectDisposedException>(() => task.GetAwaiter().GetResult());
        }

        [Test]
        public void AcquireAsync_Cancelled_ThrowsOnAwait_AndHoldsNothing()
        {
            var ground = new FlatGroundGridProvider();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = ground.AcquireAsync(Coord, cts.Token);
            Assert.Catch<OperationCanceledException>(() => task.GetAwaiter().GetResult());

            ground.Dispose();
            Assert.That(ground.MemoryBytes, Is.Zero, "a cancelled acquire holds no handle");
        }

        /// <summary>Terrain without elevation is a single quad: 4 vertices, 2 triangles, at the fallback height.</summary>
        [UnityTest]
        public IEnumerator BuildTerrainMeshAsync_FlatGrid_ProducesSingleQuad() => Run(async ct =>
        {
            using var ground = new FlatGroundGridProvider();
            ground.TryGet(Coord, out var grid);

            var mesh = await AsyncGeometryWorker.BuildTerrainMeshAsync(grid, Coord, Pivot, ct);
            try
            {
                Assert.That(mesh, Is.Not.Null);
                Assert.That(mesh.vertexCount, Is.EqualTo(4));
                Assert.That(mesh.triangles.Length, Is.EqualTo(6));
                foreach (var v in mesh.vertices)
                    Assert.That(v.y, Is.EqualTo(GroundHeightDefaults.FallbackHeightMeters).Within(1e-4f));
            }
            finally
            {
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            }
        });

        private static IEnumerator Run(Func<CancellationToken, Awaitable> body, int timeoutSeconds = 30)
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
