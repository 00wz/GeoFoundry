using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.Ground;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Ground
{
    /// <summary>
    /// Shutdown of <see cref="DemGroundGridProvider"/>: <see cref="DemGroundGridProvider.Dispose"/> does not free a grid
    /// under a live handle (the holder's job may be reading it); the last handle does. Loads are aborted, and nothing
    /// that arrives after shutdown stays in memory.
    /// </summary>
    public sealed class GroundGridProviderShutdownTests
    {
        private const int DemSize = 16;

        private static readonly TileCoord Coord = new TileCoord(12, 2477, 1280);

        /// <summary>Flat DEM that arrives immediately.</summary>
        private sealed class ImmediateDemSource : IDemTileSource
        {
            public int TileSizePixels => DemSize;

            public Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
            {
                var heights = new NativeArray<float>(DemSize * DemSize, allocator);
                for (int i = 0; i < heights.Length; i++) heights[i] = 150f;
                var completion = new AwaitableCompletionSource<NativeArray<float>>();
                completion.SetResult(heights);
                return completion.Awaitable;
            }
        }

        /// <summary>
        /// DEM arrives when the test delivers it (<see cref="Pending"/>). With <see cref="HonorCancellation"/>, token
        /// cancellation aborts the load, as with a real source.
        /// </summary>
        private sealed class DeferredDemSource : IDemTileSource
        {
            public bool HonorCancellation = true;
            public readonly Dictionary<TileCoord, AwaitableCompletionSource<NativeArray<float>>> Pending = new();
            public readonly HashSet<TileCoord> Cancelled = new();

            public int TileSizePixels => DemSize;

            public Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
            {
                var completion = new AwaitableCompletionSource<NativeArray<float>>();
                Pending[coord] = completion;
                if (HonorCancellation)
                    ct.Register(() => { if (completion.TrySetCanceled()) Cancelled.Add(coord); });
                return completion.Awaitable;
            }
        }

        /// <summary>Grid reader, like the geometry jobs that hold the grid through a tile handle.</summary>
        private struct ReadHeightsJob : IJob
        {
            [ReadOnly] public NativeArray<float> Heights;
            public NativeArray<float> Sum;

            public void Execute()
            {
                float sum = 0f;
                for (int i = 0; i < Heights.Length; i++) sum += Heights[i];
                Sum[0] = sum;
            }
        }

        private DemGroundGridProvider _grids;

        [TearDown]
        public void TearDown()
        {
            _grids?.Dispose();
            _grids = null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, string what, int maxFrames = 60)
        {
            for (int i = 0; i < maxFrames && !condition(); i++) yield return null;
            Assert.That(condition(), Is.True, $"timed out waiting for: {what}");
        }

        /// <summary>
        /// Returns whether the array has been freed. Accessing a freed <see cref="NativeArray{T}"/> throws
        /// <see cref="ObjectDisposedException"/> (derived from <see cref="InvalidOperationException"/>) only when
        /// collection checks are enabled.
        /// </summary>
        private static bool IsDisposed(NativeArray<float> array)
        {
            try
            {
                _ = array[0];
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        [UnityTest]
        public IEnumerator Dispose_WhileJobReadsBorrowedGrid_DoesNotThrow_GridFreedByLastHandle()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Ignore("A job reading the grid and the grid being freed are observable only with collection checks.");
#endif
            _grids = new DemGroundGridProvider(new ImmediateDemSource(), gridResolution: 4);
            var acquire = _grids.AcquireAsync(Coord, CancellationToken.None);
            yield return WaitFor(() => acquire.GetAwaiter().IsCompleted, "grid load");
            var handle = acquire.GetAwaiter().GetResult();
            var heights = handle.Grid.Heights;

            var sum = new NativeArray<float>(1, Allocator.Persistent);
            // No Complete(): to the safety system the job is still reading the grid even if it has finished, which is
            // exactly the state of a tile awaiting its geometry job at OnDisable.
            var job = new ReadHeightsJob { Heights = heights, Sum = sum }.Schedule();
            try
            {
                Assert.DoesNotThrow(() => _grids.Dispose(), "the provider does not free a grid under a live handle");
                Assert.That(IsDisposed(heights), Is.False, "the handle keeps the grid");

                job.Complete();
                handle.Dispose();
                Assert.That(IsDisposed(heights), Is.True, "the last handle frees the grid even after the provider is disposed");
                Assert.That(_grids.MemoryBytes, Is.Zero);
            }
            finally
            {
                job.Complete();
                sum.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Dispose_WhileGridLoading_CancelsAcquireAndDemLoads()
        {
            var dem = new DeferredDemSource();
            _grids = new DemGroundGridProvider(dem, gridResolution: 4);
            var acquire = _grids.AcquireAsync(Coord, CancellationToken.None);
            yield return WaitFor(() => dem.Pending.Count == 9, "DEM requests for the tile and its 8 neighbors");

            _grids.Dispose();
            yield return WaitFor(() => acquire.GetAwaiter().IsCompleted, "AcquireAsync completion");

            Assert.Catch<OperationCanceledException>(() => acquire.GetAwaiter().GetResult());
            Assert.That(dem.Cancelled, Is.EquivalentTo(dem.Pending.Keys), "DEM loads are aborted");
            Assert.That(_grids.MemoryBytes, Is.Zero);
        }

        [UnityTest]
        public IEnumerator Dispose_DemArrivingAfterDispose_IsReleased()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Ignore("Freeing a NativeArray is observable only with collection checks.");
#endif
            var dem = new DeferredDemSource { HonorCancellation = false };
            _grids = new DemGroundGridProvider(dem, gridResolution: 4);
            var acquire = _grids.AcquireAsync(Coord, CancellationToken.None);
            yield return WaitFor(() => dem.Pending.Count == 9, "DEM requests for the tile and its 8 neighbors");

            _grids.Dispose();

            // The source ignores cancellation: heights arrive at an already stopped provider.
            var delivered = new List<NativeArray<float>>();
            foreach (var completion in dem.Pending.Values)
            {
                var heights = new NativeArray<float>(DemSize * DemSize, Allocator.Persistent);
                delivered.Add(heights);
                completion.SetResult(heights);
            }
            yield return WaitFor(() => acquire.GetAwaiter().IsCompleted, "AcquireAsync completion");
            for (int i = 0; i < 3; i++) yield return null;

            Assert.Catch<OperationCanceledException>(() => acquire.GetAwaiter().GetResult());
            int leaked = 0;
            foreach (var heights in delivered)
            {
                if (IsDisposed(heights)) continue;
                leaked++;
                heights.Dispose();
            }
            Assert.That(leaked, Is.Zero, "DEM arriving after Dispose is freed");
            Assert.That(_grids.MemoryBytes, Is.Zero);
        }
    }
}
