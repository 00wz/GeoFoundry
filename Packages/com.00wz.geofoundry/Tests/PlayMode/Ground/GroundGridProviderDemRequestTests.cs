using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Sources;
using GeoFoundry.Ground;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Ground
{
    /// <summary>
    /// Which DEM tiles <see cref="DemGroundGridProvider"/> requests: at the world edges it skips nonexistent tiles
    /// beyond the poles and wraps X across longitude; a grid load nobody waits for aborts its DEM loads; DEM tiles
    /// without data give the fallback height silently.
    /// </summary>
    public sealed class GroundGridProviderDemRequestTests
    {
        private const int DemSize = 16;

        /// <summary>DEM source that never completes unless cancelled; records requests and cancellations.</summary>
        private sealed class DeferredDemSource : IDemTileSource
        {
            public readonly Dictionary<TileCoord, AwaitableCompletionSource<NativeArray<float>>> Pending = new();
            public readonly HashSet<TileCoord> Cancelled = new();

            public int TileSizePixels => DemSize;

            public Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
            {
                var completion = new AwaitableCompletionSource<NativeArray<float>>();
                Pending[coord] = completion;
                ct.Register(() => { if (completion.TrySetCanceled()) Cancelled.Add(coord); });
                return completion.Awaitable;
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

        [UnityTest]
        public IEnumerator Acquire_OnlyWaiterCancelled_AbortsDemLoads()
        {
            var dem = new DeferredDemSource();
            _grids = new DemGroundGridProvider(dem, gridResolution: 4);
            using var cts = new CancellationTokenSource();

            var acquire = _grids.AcquireAsync(new TileCoord(12, 2477, 1280), cts.Token);
            yield return WaitFor(() => dem.Pending.Count == 9, "DEM requests for the tile and its 8 neighbors");

            cts.Cancel();
            yield return WaitFor(() => acquire.GetAwaiter().IsCompleted, "AcquireAsync completion");

            Assert.Catch<OperationCanceledException>(() => acquire.GetAwaiter().GetResult());
            Assert.That(dem.Cancelled, Is.EquivalentTo(dem.Pending.Keys),
                "nobody waits for the grid load, so its DEM loads are aborted");
            Assert.That(_grids.MemoryBytes, Is.Zero);
        }

        /// <summary>
        /// Missing data is a regular result of the source (see <see cref="IDemTileSource"/>): the grid is built at the
        /// fallback height, and nothing is logged.
        /// </summary>
        [UnityTest]
        public IEnumerator Acquire_SourceHasNoData_FlatGridAtFallbackHeight_NothingLogged()
        {
            var logs = new List<string>();
            Application.LogCallback onLog = (message, _, type) => logs.Add($"[{type}] {message}");
            Application.logMessageReceived += onLog;
            try
            {
                var dem = new DeferredDemSource();
                _grids = new DemGroundGridProvider(dem, gridResolution: 4);

                var acquire = _grids.AcquireAsync(new TileCoord(12, 2477, 1280), CancellationToken.None);
                Assert.That(dem.Pending.Count, Is.EqualTo(9), "DEM requests for the tile and its 8 neighbors");
                foreach (var completion in dem.Pending.Values) completion.SetResult(default);
                yield return WaitFor(() => acquire.GetAwaiter().IsCompleted, "AcquireAsync completion");

                using var handle = acquire.GetAwaiter().GetResult();
                foreach (float height in handle.Grid.Heights)
                    Assert.That(height, Is.EqualTo(GroundHeightDefaults.FallbackHeightMeters));
            }
            finally
            {
                Application.logMessageReceived -= onLog;
            }
            Assert.That(logs, Is.Empty, "missing data is not logged");
        }

        [Test]
        public void Acquire_Root_RequestsOnlyRootDem()
        {
            var dem = new DeferredDemSource();
            _grids = new DemGroundGridProvider(dem, gridResolution: 4);

            // No wait needed: the load requests DEM synchronously, before the first await.
            _ = _grids.AcquireAsync(new TileCoord(0, 0, 0), CancellationToken.None);

            Assert.That(dem.Pending.Keys, Is.EquivalentTo(new[] { new TileCoord(0, 0, 0) }),
                "there are no tiles beyond the poles, and the longitude neighbors at z=0 are the root itself");
        }

        [Test]
        public void Acquire_TileAtWorldCorner_SkipsBeyondPoleAndWrapsX()
        {
            var dem = new DeferredDemSource();
            _grids = new DemGroundGridProvider(dem, gridResolution: 4);

            _ = _grids.AcquireAsync(new TileCoord(2, 0, 0), CancellationToken.None);

            var expected = new List<TileCoord>();
            foreach (int x in new[] { 3, 0, 1 })
            foreach (int y in new[] { 0, 1 })
                expected.Add(new TileCoord(2, x, y));
            Assert.That(dem.Pending.Keys, Is.EquivalentTo(expected));
        }

        /// <summary>
        /// A grid beyond the pole (a neighbor of an edge-row tile with <c>LoadNeighborGrids</c>) has no DEM of its own:
        /// only the world's edge row is requested, which the tile's own grid needs anyway.
        /// </summary>
        [Test]
        public void Acquire_BeyondPole_RequestsOnlyInWorldDem()
        {
            var dem = new DeferredDemSource();
            _grids = new DemGroundGridProvider(dem, gridResolution: 4);

            _ = _grids.AcquireAsync(new TileCoord(2, 0, -1), CancellationToken.None);

            var expected = new List<TileCoord>();
            foreach (int x in new[] { 3, 0, 1 })
                expected.Add(new TileCoord(2, x, 0));
            Assert.That(dem.Pending.Keys, Is.EquivalentTo(expected));
        }
    }
}
