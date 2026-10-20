using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.Ground;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.TileIO
{
    /// <summary>
    /// Cancellation of <see cref="DemTileCache"/> loads: a load nobody waits for anymore is aborted (down to the source's
    /// HTTP request), while it continues as long as at least one caller waits. A new caller does not inherit an aborted
    /// load, even if the source has not yet reacted to the cancellation.
    /// </summary>
    public sealed class DemTileCacheCancellationTests
    {
        private const int DemSize = 16;

        private static readonly TileCoord Coord = new TileCoord(12, 2477, 1280);

        /// <summary>
        /// DEM arrives when the test delivers it (<see cref="Calls"/>, one entry per call). With
        /// <see cref="HonorCancellation"/>, token cancellation aborts the load, as with a real source.
        /// </summary>
        private sealed class DeferredDemSource : IDemTileSource
        {
            public bool HonorCancellation = true;
            public readonly List<AwaitableCompletionSource<NativeArray<float>>> Calls = new();
            public int CancelledCount;

            public int TileSizePixels => DemSize;

            public Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
            {
                var completion = new AwaitableCompletionSource<NativeArray<float>>();
                Calls.Add(completion);
                if (HonorCancellation)
                    ct.Register(() => { if (completion.TrySetCanceled()) CancelledCount++; });
                return completion.Awaitable;
            }
        }

        private DemTileCache _cache;

        [TearDown]
        public void TearDown()
        {
            _cache?.Dispose();
            _cache = null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, string what, int maxFrames = 60)
        {
            for (int i = 0; i < maxFrames && !condition(); i++) yield return null;
            Assert.That(condition(), Is.True, $"timed out waiting for: {what}");
        }

        private static NativeArray<float> MakeHeights(float value)
        {
            var heights = new NativeArray<float>(DemSize * DemSize, Allocator.Persistent);
            for (int i = 0; i < heights.Length; i++) heights[i] = value;
            return heights;
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
        public IEnumerator Acquire_OnlyWaiterCancelled_AbortsSourceLoad()
        {
            var dem = new DeferredDemSource();
            _cache = new DemTileCache(dem);
            using var cts = new CancellationTokenSource();

            var acquire = _cache.AcquireAsync(Coord, cts.Token);
            yield return WaitFor(() => dem.Calls.Count == 1, "DEM request");

            cts.Cancel();
            yield return WaitFor(() => acquire.GetAwaiter().IsCompleted, "AcquireAsync completion");

            Assert.Catch<OperationCanceledException>(() => acquire.GetAwaiter().GetResult());
            Assert.That(dem.CancelledCount, Is.EqualTo(1), "cancellation aborts a load nobody waits for");
            Assert.That(_cache.MemoryBytes, Is.Zero);
        }

        [UnityTest]
        public IEnumerator Acquire_OneOfTwoWaitersCancelled_LoadContinuesForTheOther()
        {
            var dem = new DeferredDemSource();
            _cache = new DemTileCache(dem);
            using var cts = new CancellationTokenSource();

            var cancelled = _cache.AcquireAsync(Coord, cts.Token);
            var waiting = _cache.AcquireAsync(Coord, CancellationToken.None);
            yield return WaitFor(() => dem.Calls.Count == 1, "one shared DEM request");

            cts.Cancel();
            yield return WaitFor(() => cancelled.GetAwaiter().IsCompleted, "cancelled AcquireAsync completion");
            Assert.Catch<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());
            Assert.That(dem.CancelledCount, Is.Zero, "the load is still awaited, so it is not aborted");

            dem.Calls[0].SetResult(MakeHeights(42f));
            yield return WaitFor(() => waiting.GetAwaiter().IsCompleted, "second AcquireAsync completion");

            var handle = waiting.GetAwaiter().GetResult();
            Assert.That(handle.Heights[0], Is.EqualTo(42f));
            handle.Dispose();
            Assert.That(_cache.MemoryBytes, Is.Zero);
        }

        [UnityTest]
        public IEnumerator Acquire_AfterAbortedLoadIgnoringCancellation_StartsFreshLoad()
        {
            var dem = new DeferredDemSource { HonorCancellation = false };
            _cache = new DemTileCache(dem);
            using var cts = new CancellationTokenSource();

            var cancelled = _cache.AcquireAsync(Coord, cts.Token);
            yield return WaitFor(() => dem.Calls.Count == 1, "DEM request");
            cts.Cancel();
            yield return WaitFor(() => cancelled.GetAwaiter().IsCompleted, "cancelled AcquireAsync completion");
            Assert.Catch<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());

            // The source ignores cancellation, so the aborted load is still running. A new caller does not inherit it.
            var fresh = _cache.AcquireAsync(Coord, CancellationToken.None);
            Assert.That(dem.Calls.Count, Is.EqualTo(2), "a new caller starts a new load");

            var stale = MakeHeights(1f);
            dem.Calls[0].SetResult(stale);
            dem.Calls[1].SetResult(MakeHeights(2f));
            yield return WaitFor(() => fresh.GetAwaiter().IsCompleted, "new AcquireAsync completion");

            var handle = fresh.GetAwaiter().GetResult();
            Assert.That(handle.Heights[0], Is.EqualTo(2f), "heights from the new load, not the aborted one");
            handle.Dispose();
            Assert.That(_cache.MemoryBytes, Is.Zero);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            bool leaked = !IsDisposed(stale);
            if (leaked) stale.Dispose();
            Assert.That(leaked, Is.False, "heights from the aborted load are freed");
#endif
        }
    }
}
