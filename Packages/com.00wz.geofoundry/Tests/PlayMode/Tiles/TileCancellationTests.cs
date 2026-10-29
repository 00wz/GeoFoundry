using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Core.Sources;
using GeoFoundry.Core.TileSelection;
using GeoFoundry.Geometry;
using GeoFoundry.Ground;
using GeoFoundry.TileIO.Net;
using GeoFoundry.Tiles;
using GeoFoundry.Tiles.Diagnostics;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Tiles
{
    /// <summary>
    /// Tile build cancellation: <see cref="TileBuilder"/> does not leak MVT bytes when interrupted before taking them,
    /// and completes a cancellation with an exception even if the layer handler returned <c>null</c> for it;
    /// <see cref="TileLifecycleManager"/> cancels in-flight tiles that are no longer admitted (denied by the budget or
    /// no longer wanted by the selector), and only those, and a cancelled tile does not enter the cache.
    /// <para>
    /// The MVT source is fake: bytes arrive when the test delivers them. The budget is real: the memory of an
    /// in-flight tile comes from a fake terrain (<see cref="FakeTerrainHandler"/>) attached before MVT is awaited.
    /// There is no DEM for it, so <see cref="DemGroundGridProvider"/> builds a flat grid and logs every DEM failure as
    /// an error (see <see cref="ExpectDemFailureLogs"/>). The manager's debug snapshot
    /// (<see cref="TileLifecycleManager.GetDebugSnapshot"/>) is checked in the same states: loading, queued for a slot,
    /// cancelling.
    /// </para>
    /// </summary>
    public sealed class TileCancellationTests
    {
        /// <summary>
        /// Number of tiles the budget admits in the denial test. Each tile with terrain weighs one unit in the GPU pool
        /// (<see cref="FakeTerrainInstance"/>), and the limit is <c>Budget − 1</c>: <c>Admit</c> allows exceeding it by
        /// one tile.
        /// </summary>
        private const int Budget = 10;

        /// <summary>A non-limiting budget for tests without denials.</summary>
        private static readonly TileMemoryUsage Unlimited = new TileMemoryUsage(long.MaxValue, long.MaxValue);

        private static readonly MercatorPivot Pivot = MercatorPivot.FromLonLat(37.6175, 55.7520);

        private readonly List<UnityEngine.Object> _created = new();
        private TileLifecycleManager _manager;
        private GroundGridProvider _gridProvider;

        [TearDown]
        public void TearDown()
        {
            _manager?.Dispose();
            _manager = null;
            _gridProvider?.Dispose();
            _gridProvider = null;

            foreach (var obj in _created)
                if (obj != null) UnityEngine.Object.Destroy(obj);
            _created.Clear();

            if (_unexpectedErrors != null)
            {
                Application.logMessageReceived -= OnLogMessage;
                var unexpected = _unexpectedErrors;
                _unexpectedErrors = null;
                Assert.That(unexpected, Is.Empty, "errors in the log other than the expected DEM failures");
            }
        }

        /// <summary>
        /// The DEM cache logs a DEM failure as an error, one per failed load. A failed tile is not cached, so grids
        /// loaded later fetch it again, and the count depends on load timing: <see cref="LogAssert.Expect(LogType, Regex)"/>
        /// (one message per call) does not fit. Instead, logged errors do not fail the test by themselves, and
        /// <see cref="TearDown"/> checks that none of them is anything but a DEM failure.
        /// </summary>
        private void ExpectDemFailureLogs()
        {
            if (_unexpectedErrors != null) return;
            _unexpectedErrors = new List<string>();
            Application.logMessageReceived += OnLogMessage;
            LogAssert.ignoreFailingMessages = true;
        }

        private static readonly Regex DemFailureLog = new Regex(@"^\[GeoFoundry\] Unexpected error loading DEM tile ");

        private List<string> _unexpectedErrors;

        private void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if ((type is LogType.Error or LogType.Exception or LogType.Assert) && !DemFailureLog.IsMatch(message))
                _unexpectedErrors.Add($"[{type}] {message}");
        }

        private T Track<T>(T obj) where T : UnityEngine.Object
        {
            _created.Add(obj);
            return obj;
        }

        private static TileCoord Tile(int i) => new TileCoord(14, 9903 + i, 5145);

        private static List<TileCoord> Tiles(int count)
        {
            var tiles = new List<TileCoord>(count);
            for (int i = 0; i < count; i++) tiles.Add(Tile(i));
            return tiles;
        }

        // ---- Fakes -------------------------------------------------------------

        private sealed class FakeFloatingOriginController : IFloatingOriginController
        {
            public MercatorPivot Current { get; }
            public event Action<PivotChangedEventArgs> PivotChanged { add { } remove { } }

            public FakeFloatingOriginController(MercatorPivot initial) => Current = initial;
        }

        /// <summary>
        /// Returns MVT bytes only when the test completes the load through <see cref="Pending"/>. With
        /// <see cref="HonorCancellation"/>, token cancellation aborts the load, as with a real source.
        /// </summary>
        private sealed class DeferredVectorTileSource : IVectorTileSource
        {
            public bool HonorCancellation = true;
            public readonly Dictionary<TileCoord, AwaitableCompletionSource<NativeArray<byte>>> Pending = new();
            public readonly HashSet<TileCoord> Cancelled = new();
            public readonly Dictionary<TileCoord, int> RequestCount = new();

            public Awaitable<NativeArray<byte>> GetTileAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
            {
                var completion = new AwaitableCompletionSource<NativeArray<byte>>();
                Pending[coord] = completion;
                RequestCount[coord] = RequestCount.TryGetValue(coord, out int count) ? count + 1 : 1;
                if (HonorCancellation)
                    ct.Register(() => { if (completion.TrySetCanceled()) Cancelled.Add(coord); });
                return completion.Awaitable;
            }
        }

        /// <summary>
        /// DEM is unavailable: the DEM cache logs the failure as an error (see <see cref="ExpectDemFailureLogs"/>), and
        /// <see cref="DemGroundGridProvider"/> fills the grid with the default height.
        /// </summary>
        private sealed class FailingDemTileSource : IDemTileSource
        {
            public int TileSizePixels => 256;

            public Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct) =>
                throw new InvalidOperationException("DEM unavailable");
        }

        /// <summary>
        /// Grids from <c>inner</c> except for one tile, whose acquisition fails: an unexpected provider failure (not DEM
        /// unavailability, which the provider survives on its own).
        /// </summary>
        private sealed class FailingGridProvider : GroundGridProvider
        {
            private readonly GroundGridProvider _inner;
            private readonly TileCoord _failing;

            public FailingGridProvider(GroundGridProvider inner, TileCoord failing)
            {
                _inner = inner;
                _failing = failing;
            }

            public override Awaitable<GroundGridHandle> AcquireAsync(TileCoord coord, CancellationToken ct)
            {
                if (!coord.Equals(_failing)) return _inner.AcquireAsync(coord, ct);
                var failed = new AwaitableCompletionSource<GroundGridHandle>();
                failed.SetException(new InvalidOperationException($"grid {coord} is broken"));
                return failed.Awaitable;
            }

            public override bool TryGet(TileCoord coord, out GroundGrid grid) => _inner.TryGet(coord, out grid);
            public override long MemoryBytes => _inner.MemoryBytes;
            public override void Dispose() { }
        }

        /// <summary>Grids from <c>inner</c>; records which tiles they were requested for.</summary>
        private sealed class RecordingGridProvider : GroundGridProvider
        {
            private readonly GroundGridProvider _inner;
            public readonly List<TileCoord> Requested = new();

            public RecordingGridProvider(GroundGridProvider inner) => _inner = inner;

            public override Awaitable<GroundGridHandle> AcquireAsync(TileCoord coord, CancellationToken ct)
            {
                Requested.Add(coord);
                return _inner.AcquireAsync(coord, ct);
            }

            public override bool TryGet(TileCoord coord, out GroundGrid grid) => _inner.TryGet(coord, out grid);
            public override long MemoryBytes => _inner.MemoryBytes;
            public override void Dispose() { }
        }

        /// <summary>Terrain weighing one GPU pool unit, which gives an in-flight tile a known cost.</summary>
        private sealed class FakeTerrainInstance : ITerrainHandlerInstance
        {
            public TileMemoryUsage MemoryUsage => new TileMemoryUsage(1, 0);

            public void Dispose() { }
        }

        /// <summary>Builds a <see cref="FakeTerrainInstance"/> within a frame and records which tiles it built for.</summary>
        private sealed class FakeTerrainHandler : ITerrainHandler
        {
            public readonly HashSet<TileCoord> Built = new();

            public async Awaitable<ITerrainHandlerInstance> OnTerrainLoadedAsync(TerrainLoadContext ctx)
            {
                await Awaitable.NextFrameAsync();
                Built.Add(ctx.TileCoord);
                return new FakeTerrainInstance();
            }
        }

        /// <summary>
        /// Builds terrain with the real <see cref="AsyncGeometryWorker"/> from the tile's shared grid and calls
        /// <see cref="OnJobScheduled"/> right after scheduling the job, while it is reading the grid.
        /// </summary>
        private sealed class JobTerrainHandler : ITerrainHandler
        {
            public Action OnJobScheduled;
            public readonly HashSet<TileCoord> Finished = new();

            public async Awaitable<ITerrainHandlerInstance> OnTerrainLoadedAsync(TerrainLoadContext ctx)
            {
                // The job is scheduled synchronously, before the first await inside the worker.
                var build = AsyncGeometryWorker.BuildTerrainMeshAsync(ctx.Grid, ctx.TileCoord, ctx.LocalFrame, ctx.Ct);
                var onJobScheduled = OnJobScheduled;
                OnJobScheduled = null;
                onJobScheduled?.Invoke();

                var mesh = await build;
                Finished.Add(ctx.TileCoord);
                if (mesh == null) return null;
                UnityEngine.Object.Destroy(mesh);
                return new FakeTerrainInstance();
            }
        }

        /// <summary>
        /// Handler for the <see cref="MinimalMvt"/> layer that keeps building until the build is cancelled and then,
        /// like the built-in handlers (see <c>AsyncGeometryWorker</c>), returns <c>null</c> instead of throwing.
        /// </summary>
        private sealed class BlockingLayerHandler : ILayerHandler
        {
            public readonly HashSet<TileCoord> Started = new();
            public readonly HashSet<TileCoord> SawCancellation = new();

            public string LayerName => TestLayerName;

            public async Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext context)
            {
                Started.Add(context.TileCoord);
                while (!context.Ct.IsCancellationRequested) await Awaitable.NextFrameAsync();
                SawCancellation.Add(context.TileCoord);
                return null;
            }
        }

        /// <summary>Handler for the <see cref="MinimalMvt"/> layer that records the tile's grids and builds nothing.</summary>
        private sealed class GridsRecordingLayerHandler : ILayerHandler
        {
            public TileGroundGrids? Grids;

            public string LayerName => TestLayerName;

            public Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext context)
            {
                Grids = context.Grids;
                var done = new AwaitableCompletionSource<ILayerHandlerInstance>();
                done.SetResult(null);
                return done.Awaitable;
            }
        }

        /// <summary>Selector that wants to show <see cref="Wanted"/> every frame, in order of importance.</summary>
        private sealed class FixedSelector : ITileSelector
        {
            public readonly List<TileCoord> Wanted = new();
            public readonly List<TileCoord> Failed = new();

            /// <summary>
            /// Tiles from <see cref="Wanted"/> to render but not load, like failed loads that
            /// <c>TileQuadtreeTraversal</c> keeps as empty space and does not retry.
            /// </summary>
            public readonly HashSet<TileCoord> NoLoad = new();

            /// <summary>All tiles the manager has ever reported as loaded.</summary>
            public readonly HashSet<TileCoord> EverLoaded = new();

            public bool SelectTiles(in TileFrameInput input, TileSelectionResult result)
            {
                Failed.AddRange(input.FailedSinceLastFrame);
                EverLoaded.UnionWith(input.LoadedTiles);

                result.Clear();
                foreach (var coord in Wanted)
                {
                    result.TilesToRender.Add(coord);
                    if (!NoLoad.Contains(coord)) result.TilesToLoad.Add(coord);
                    result.ByImportance.Add(coord);
                }
                return true;
            }
        }

        private TileData MakeTileData(TileCoord coord)
        {
            var root = Track(new GameObject($"Tile_{coord}"));
            return new TileData(coord, root, new FakeFloatingOriginController(Pivot));
        }

        private const string TestLayerName = "test";

        /// <summary>
        /// MVT with a single empty layer <see cref="TestLayerName"/>: <c>Tile.layers</c> (field 3) →
        /// <c>Layer.name</c> (field 1); version and extent use the parser defaults. Ownership of the array passes to
        /// <see cref="TileBuilder"/>.
        /// </summary>
        private static NativeArray<byte> MinimalMvt() =>
            new NativeArray<byte>(new byte[] { 0x1A, 0x06, 0x0A, 0x04, (byte)'t', (byte)'e', (byte)'s', (byte)'t' },
                Allocator.Persistent);

        private static LayerHandlerRegistry Registry(ILayerHandler layerHandler)
        {
            var registry = new LayerHandlerRegistry();
            if (layerHandler != null) registry.Register(layerHandler);
            return registry;
        }

        /// <param name="budget">Budget limits; <see cref="Unlimited"/> by default.</param>
        /// <param name="terrain">
        /// If set, tiles build terrain (on a grid without DEM: flat, but N×N as in production with elevation);
        /// otherwise the ground is a flat 1×1 grid without elevation or terrain.
        /// </param>
        /// <param name="gridResolution">Terrain grid segments per side.</param>
        private (FixedSelector Selector, DeferredVectorTileSource Source, Transform MapRoot) StartManager(
            TileMemoryUsage? budget = null, ILayerHandler layerHandler = null, ITerrainHandler terrain = null,
            int maxConcurrentLoads = 2 * Budget, int gridResolution = 4)
        {
            var source = new DeferredVectorTileSource();
            if (terrain != null) ExpectDemFailureLogs();
            _gridProvider = terrain != null
                ? new DemGroundGridProvider(new FailingDemTileSource(), gridResolution)
                : new FlatGroundGridProvider();
            var builder = new TileBuilder(_gridProvider, source, terrain, Registry(layerHandler), loadNeighborGrids: false);
            var selector = new FixedSelector();
            var mapRoot = Track(new GameObject("MapRoot")).transform;

            _manager = new TileLifecycleManager(
                builder, selector, new FakeFloatingOriginController(Pivot), mapRoot, budget ?? Unlimited, maxConcurrentLoads);
            _manager.Start();
            return (selector, source, mapRoot);
        }

        /// <summary>
        /// Returns the tile root under <paramref name="mapRoot"/>. Not <see cref="Transform.Find"/>: the name contains
        /// <c>TileCoord.ToString()</c> in the form <c>z/x/y</c>, and <c>Find</c> treats <c>/</c> as a path separator.
        /// </summary>
        private static Transform FindTileRoot(Transform mapRoot, TileCoord coord)
        {
            string name = $"Tile_{coord}";
            foreach (Transform child in mapRoot)
                if (child.name == name) return child;
            return null;
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
        private static bool IsDisposed(NativeArray<byte> bytes)
        {
            try
            {
                _ = bytes[0];
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        // ---- TileBuilder -------------------------------------------------------

        [UnityTest]
        public IEnumerator PopulateAsync_CancelledBeforeMvtConsumed_ReleasesMvtBytesWhenTheyArrive()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Ignore("Freeing a NativeArray is observable only with collection checks.");
#endif
            var coord = Tile(0);
            var source = new DeferredVectorTileSource { HonorCancellation = false };
            _gridProvider = new FlatGroundGridProvider();
            var builder = new TileBuilder(_gridProvider, source, null, new LayerHandlerRegistry(), loadNeighborGrids: false);
            var tileData = MakeTileData(coord);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var populate = builder.PopulateAsync(tileData, coord, cts.Token);
            yield return WaitFor(() => populate.GetAwaiter().IsCompleted, "PopulateAsync completion");
            Assert.Catch<OperationCanceledException>(() => populate.GetAwaiter().GetResult());
            tileData.Dispose();

            // The load started before cancellation completes after the build has been interrupted.
            var bytes = new NativeArray<byte>(16, Allocator.Persistent);
            source.Pending[coord].SetResult(bytes);
            for (int i = 0; i < 10 && !IsDisposed(bytes); i++) yield return null;

            bool released = IsDisposed(bytes);
            if (!released) bytes.Dispose();
            Assert.That(released, Is.True, "MVT bytes not taken by the interrupted build are freed");
        }

        [UnityTest]
        public IEnumerator PopulateAsync_CancelledDuringLayerBuild_ThrowsOperationCanceled()
        {
            var coord = Tile(0);
            var source = new DeferredVectorTileSource();
            var handler = new BlockingLayerHandler();
            _gridProvider = new FlatGroundGridProvider();
            var builder = new TileBuilder(_gridProvider, source, null, Registry(handler), loadNeighborGrids: false);
            var tileData = MakeTileData(coord);
            using var cts = new CancellationTokenSource();

            var populate = builder.PopulateAsync(tileData, coord, cts.Token);
            yield return WaitFor(() => source.Pending.ContainsKey(coord), "MVT request");
            source.Pending[coord].SetResult(MinimalMvt());
            yield return WaitFor(() => handler.Started.Contains(coord), "layer build start");

            cts.Cancel();
            yield return WaitFor(() => populate.GetAwaiter().IsCompleted, "PopulateAsync completion");
            Assert.That(handler.SawCancellation, Does.Contain(coord));
            Assert.Catch<OperationCanceledException>(() => populate.GetAwaiter().GetResult(),
                "the handler returned null on cancellation, but a cancelled build must not look completed");
            tileData.Dispose();
        }

        /// <summary>
        /// A failure to acquire a neighbor grid fails the tile build (no silent default in the slot), and grids already
        /// acquired or still being acquired are released, so the ref count does not leak.
        /// </summary>
        [UnityTest]
        public IEnumerator PopulateAsync_NeighborGridFails_ThrowsAndReleasesAcquiredGrids()
        {
            var coord = Tile(0);
            var flat = new FlatGroundGridProvider();
            _gridProvider = flat;
            var source = new DeferredVectorTileSource();
            var builder = new TileBuilder(
                new FailingGridProvider(flat, coord.Offset(+1, 0)), source, null, new LayerHandlerRegistry(),
                loadNeighborGrids: true);
            var tileData = MakeTileData(coord);

            var populate = builder.PopulateAsync(tileData, coord, CancellationToken.None);
            yield return WaitFor(() => populate.GetAwaiter().IsCompleted, "PopulateAsync completion");
            Assert.Catch<InvalidOperationException>(() => populate.GetAwaiter().GetResult());
            tileData.Dispose();
            source.Pending[coord].SetResult(MinimalMvt()); // TileBuilder itself frees the untaken bytes

            flat.Dispose();
            Assert.That(flat.MemoryBytes, Is.Zero, "the center and remaining neighbor grids are released");
        }

        [UnityTest]
        public IEnumerator PopulateAsync_NeighborGridsOff_OnlyCenterAcquired()
        {
            var coord = Tile(0);
            var flat = new FlatGroundGridProvider();
            _gridProvider = flat;
            var grids = new RecordingGridProvider(flat);
            var source = new DeferredVectorTileSource();
            var handler = new GridsRecordingLayerHandler();
            var builder = new TileBuilder(grids, source, null, Registry(handler), loadNeighborGrids: false);
            var tileData = MakeTileData(coord);

            var populate = builder.PopulateAsync(tileData, coord, CancellationToken.None);
            yield return WaitFor(() => source.Pending.ContainsKey(coord), "MVT request");
            source.Pending[coord].SetResult(MinimalMvt());
            yield return WaitFor(() => populate.GetAwaiter().IsCompleted, "PopulateAsync completion");
            populate.GetAwaiter().GetResult();

            Assert.That(grids.Requested, Is.EquivalentTo(new[] { coord }), "without the option only the center grid is acquired");
            Assert.That(handler.Grids, Is.Not.Null, "the layer handler was called");
            var seen = handler.Grids.Value;
            Assert.That(seen.Center.IsValid, Is.True);
            foreach (var neighbor in Neighbors(seen))
                Assert.That(neighbor.IsValid, Is.False, "neighbor slots are empty");
            tileData.Dispose();
        }

        /// <summary>
        /// With the option all 9 grids are valid, even for an edge-row tile: its northern neighbors lie beyond the pole,
        /// where there are no tiles, but a grid still exists.
        /// </summary>
        [UnityTest]
        public IEnumerator PopulateAsync_NeighborGridsOn_TileAtPole_AllNineValid()
        {
            var coord = new TileCoord(14, 9903, 0);
            var flat = new FlatGroundGridProvider();
            _gridProvider = flat;
            var grids = new RecordingGridProvider(flat);
            var source = new DeferredVectorTileSource();
            var handler = new GridsRecordingLayerHandler();
            var builder = new TileBuilder(grids, source, null, Registry(handler), loadNeighborGrids: true);
            var tileData = MakeTileData(coord);

            var populate = builder.PopulateAsync(tileData, coord, CancellationToken.None);
            yield return WaitFor(() => source.Pending.ContainsKey(coord), "MVT request");
            source.Pending[coord].SetResult(MinimalMvt());
            yield return WaitFor(() => populate.GetAwaiter().IsCompleted, "PopulateAsync completion");
            populate.GetAwaiter().GetResult();

            Assert.That(grids.Requested.Count, Is.EqualTo(9));
            Assert.That(grids.Requested, Does.Contain(coord.Offset(0, -1)), "the neighbor beyond the pole is requested");
            Assert.That(handler.Grids, Is.Not.Null, "the layer handler was called");
            var seen = handler.Grids.Value;
            Assert.That(seen.Center.IsValid, Is.True);
            foreach (var neighbor in Neighbors(seen))
                Assert.That(neighbor.IsValid, Is.True, "all neighbors are valid, including the one beyond the pole");
            Assert.That(seen.North.BoundsMeters, Is.EqualTo(coord.Offset(0, -1).BoundsMeters()),
                "the grid beyond the pole lies next to the tile");
            tileData.Dispose();
        }

        private static GroundGrid[] Neighbors(TileGroundGrids g) =>
            new[] { g.North, g.South, g.East, g.West, g.NorthEast, g.NorthWest, g.SouthEast, g.SouthWest };

        // ---- TileLifecycleManager ---------------------------------------------

        [UnityTest]
        public IEnumerator Manager_DeniedInFlightTile_IsCancelledAndReleased_NotReportedFailed()
        {
            var terrain = new FakeTerrainHandler();
            var (selector, source, mapRoot) = StartManager(
                budget: new TileMemoryUsage(Budget - 1, long.MaxValue), terrain: terrain);
            var tiles = Tiles(Budget);
            selector.Wanted.AddRange(tiles);
            // A tile's cost becomes known when its terrain is attached, while the tile stays in flight (MVT never
            // arrives). The total is Budget with a limit of Budget − 1, so the least important tile is the boundary
            // tile, admitted over the limit.
            yield return WaitFor(() => terrain.Built.Count == Budget, "terrain of all budget tiles");

            var leastImportant = tiles[Budget - 1];
            var leastImportantRoot = FindTileRoot(mapRoot, leastImportant);
            Assert.That(leastImportantRoot, Is.Not.Null);
            Assert.That(source.Cancelled, Is.Empty, "no denials while all but the boundary tile fit");

            // The new most important tile is admitted optimistically (its cost is still unknown); once its terrain is
            // attached, the second-to-last tile becomes the boundary tile and the least important one, still in
            // flight, is denied.
            selector.Wanted.Insert(0, Tile(Budget));
            yield return WaitFor(() => source.Cancelled.Contains(leastImportant), "cancellation of the denied tile");
            Assert.That(terrain.Built, Does.Contain(Tile(Budget)), "denial happens only once the new tile's cost is known");
            yield return null; // Destroy of the tile root takes effect at the end of the frame

            Assert.That(source.Cancelled, Is.EquivalentTo(new[] { leastImportant }), "only the denied tile is cancelled");
            Assert.That(leastImportantRoot == null, Is.True, "the cancelled tile is released");
            Assert.That(selector.Failed, Is.Empty, "cancellation is not a failed load");
            Assert.That(source.Pending.ContainsKey(Tile(Budget)), Is.True, "the admitted tile starts loading");
        }

        [UnityTest]
        public IEnumerator Manager_InFlightTileNoLongerSelected_IsCancelledAndReleased_NotReportedFailed()
        {
            var (selector, source, mapRoot) = StartManager();
            var tiles = Tiles(Budget);
            selector.Wanted.AddRange(tiles);
            yield return WaitFor(() => source.Pending.Count == Budget, "load start of all budget tiles");

            // The budget did not deny the tile; the selector stopped requesting it.
            var dropped = tiles[Budget / 2];
            var droppedRoot = FindTileRoot(mapRoot, dropped);
            Assert.That(droppedRoot, Is.Not.Null);

            selector.Wanted.Remove(dropped);
            yield return WaitFor(() => source.Cancelled.Contains(dropped), "cancellation of the unwanted tile");
            yield return null; // Destroy of the tile root takes effect at the end of the frame

            Assert.That(source.Cancelled, Is.EquivalentTo(new[] { dropped }), "admitted tiles are not cancelled");
            Assert.That(droppedRoot == null, Is.True, "the cancelled tile is released rather than moved to the warm cache");
            Assert.That(selector.Failed, Is.Empty, "cancellation is not a failed load");
        }

        [UnityTest]
        public IEnumerator Manager_CancelledTile_RequestedAgain_IsReloaded()
        {
            var (selector, source, _) = StartManager();
            var tiles = Tiles(Budget);
            selector.Wanted.AddRange(tiles);
            yield return WaitFor(() => source.Pending.Count == Budget, "load start of all budget tiles");

            var dropped = tiles[Budget / 2];
            selector.Wanted.Remove(dropped);
            yield return WaitFor(() => source.Cancelled.Contains(dropped), "cancellation of the unwanted tile");

            selector.Wanted.Insert(Budget / 2, dropped);
            yield return WaitFor(() => source.RequestCount[dropped] == 2, "repeated tile request");
            Assert.That(selector.Failed, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Manager_TileCancelledDuringLayerBuild_IsReleased_NotLoaded()
        {
            var handler = new BlockingLayerHandler();
            var (selector, source, mapRoot) = StartManager(layerHandler: handler);
            var coord = Tile(0);
            selector.Wanted.Add(coord);
            yield return WaitFor(() => source.Pending.ContainsKey(coord), "MVT request");

            source.Pending[coord].SetResult(MinimalMvt());
            yield return WaitFor(() => handler.Started.Contains(coord), "layer build start");
            var root = FindTileRoot(mapRoot, coord);
            Assert.That(root, Is.Not.Null);

            // The tile is no longer wanted in the middle of the layer build: the handler returns null on cancellation.
            selector.Wanted.Remove(coord);
            yield return WaitFor(() => handler.SawCancellation.Contains(coord), "layer build cancellation");
            yield return WaitFor(() => root == null, "release of the cancelled tile");
            for (int i = 0; i < 3; i++) yield return null;

            Assert.That(selector.EverLoaded, Has.No.Member(coord), "an unfinished tile does not enter the cache as loaded");
            Assert.That(selector.Failed, Is.Empty, "cancellation is not a failed load");
        }

        // ---- Diagnostics ---------------------------------------------------------

        [UnityTest]
        public IEnumerator Manager_FailedLoad_RaisesTileLoadFailedWithCause_CancellationDoesNot()
        {
            var (selector, source, _) = StartManager();
            var failures = new List<(TileCoord Coord, Exception Error)>();
            _manager.TileLoadFailed += (coord, error) => failures.Add((coord, error));
            // A subscriber exception must not disrupt the manager.
            _manager.TileLoadFailed += (_, _) => throw new InvalidOperationException("subscriber is broken");

            var failed = Tile(0);
            var dropped = Tile(1);
            selector.Wanted.AddRange(new[] { failed, dropped });
            yield return WaitFor(() => source.Pending.Count == 2, "load start of both tiles");

            selector.Wanted.Remove(dropped);
            yield return WaitFor(() => source.Cancelled.Contains(dropped), "cancellation of the unwanted tile");

            // LogAssert matches expectations in order: the manager logs first, then raises the event.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"Failed .*Not Found"));
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("subscriber is broken"));
            source.Pending[failed].SetException(new TileDownloadException("http://test", 404, "Not Found"));
            yield return WaitFor(() => failures.Count > 0, "failure event");
            yield return WaitFor(() => selector.Failed.Contains(failed), "failure passed to the selector");

            Assert.That(failures.Select(f => f.Coord), Is.EqualTo(new[] { failed }), "cancellation is not a failure");
            // The manager knows only TileSourceException; the source's subclass reaches subscribers as is.
            Assert.That(failures[0].Error, Is.InstanceOf<TileDownloadException>());
            Assert.That(((TileDownloadException)failures[0].Error).HttpStatus, Is.EqualTo(404));
        }

        [UnityTest]
        public IEnumerator Manager_TileWithoutLoadRequest_DoesNotConsumeBudget()
        {
            // Each tile weighs one unit (terrain); the limit is 3 units.
            var terrain = new FakeTerrainHandler();
            var (selector, source, _) = StartManager(budget: new TileMemoryUsage(3, long.MaxValue), terrain: terrain);

            // One tile loads completely, which gives the budget an estimate for unknown tiles: one unit.
            var loaded = Tile(0);
            selector.Wanted.Add(loaded);
            yield return WaitFor(() => source.Pending.ContainsKey(loaded), "MVT request");
            source.Pending[loaded].SetResult(MinimalMvt());
            yield return WaitFor(() => selector.EverLoaded.Contains(loaded), "tile load");

            // An empty tile (the selector does not request its load) is second in importance.
            var empty = Tile(1);
            var requested = new[] { Tile(2), Tile(3), Tile(4) };
            selector.NoLoad.Add(empty);
            selector.Wanted.Add(empty);
            selector.Wanted.AddRange(requested);
            yield return WaitFor(() => requested.All(source.Pending.ContainsKey), "load start of the requested tiles");

            var snapshot = new List<TileDebugInfo>();
            _manager.GetDebugSnapshot(snapshot);
            TileDebugInfo Info(TileCoord coord) => snapshot.Single(t => t.Coord.Equals(coord));

            Assert.That(Info(empty).BudgetCost, Is.EqualTo(TileMemoryUsage.Zero), "admitting the empty tile does not lead to a load");
            Assert.That(Info(empty).IsEmpty, Is.True);
            Assert.That(Info(requested[0]).BudgetCost.GpuBytes, Is.EqualTo(1), "the requested tile is estimated from loaded tiles");
            // 1 (loaded) + 0 (empty) + 1 + 1 + 1 (boundary): had the empty tile an estimate, the last one would be denied.
            Assert.That(snapshot.Where(t => t.Verdict == TileBudgetVerdict.Denied), Is.Empty);
            Assert.That(Info(requested[2]).Verdict, Is.EqualTo(TileBudgetVerdict.Boundary));
        }

        [UnityTest]
        public IEnumerator Manager_DebugSnapshot_ShowsLoadingQueuedAndCancellingTiles()
        {
            var (selector, source, _) = StartManager(maxConcurrentLoads: 2);
            source.HonorCancellation = false; // the cancelled build hangs until MVT arrives
            var tiles = Tiles(3);
            selector.Wanted.AddRange(tiles);
            yield return WaitFor(() => source.Pending.Count == 2, "load start in both slots");

            var snapshot = new List<TileDebugInfo>();
            TileDebugInfo Info(TileCoord coord) => snapshot.Single(t => t.Coord.Equals(coord));

            _manager.GetDebugSnapshot(snapshot);
            Assert.That(snapshot.Select(t => t.Coord), Is.EquivalentTo(tiles), "each tile exactly once");
            foreach (var coord in tiles.Take(2))
            {
                Assert.That(Info(coord).Residency, Is.EqualTo(TileResidency.Loading));
                Assert.That(Info(coord).Verdict, Is.EqualTo(TileBudgetVerdict.Admitted));
                Assert.That(Info(coord).IsRendered, Is.True, "a loading tile is shown as it builds");
            }
            Assert.That(Info(tiles[2]).IsQueued, Is.True, "the third tile got no slot");
            Assert.That(Info(tiles[2]).IsRendered, Is.False, "the tile is not in memory, so there is nothing to render");

            // The second tile is no longer wanted: its build is cancelled but hangs and holds the slot.
            selector.Wanted.Remove(tiles[1]);
            yield return WaitFor(() =>
            {
                _manager.GetDebugSnapshot(snapshot);
                return snapshot.Any(t => t.Coord.Equals(tiles[1]) && t.Residency == TileResidency.Cancelling);
            }, "cancellation of the unwanted tile");

            Assert.That(Info(tiles[1]).Verdict, Is.EqualTo(TileBudgetVerdict.NotWanted));
            Assert.That(Info(tiles[1]).IsLoadRequested, Is.False);
            Assert.That(Info(tiles[2]).IsQueued, Is.True, "the cancelling tile occupies a slot");

            var summary = TileDebugSummary.From(snapshot);
            Assert.That(summary.Loading, Is.EqualTo(1));
            Assert.That(summary.Cancelling, Is.EqualTo(1));
            Assert.That(summary.Queued, Is.EqualTo(1));
            Assert.That(summary.Admitted, Is.EqualTo(2));
            Assert.That(summary.Rendered, Is.EqualTo(1));
            Assert.That(summary.BudgetExceeded, Is.False);
        }

        [UnityTest]
        public IEnumerator Manager_CancellingTile_AdmittedAgain_StaysHiddenUntilReloaded()
        {
            var (selector, source, mapRoot) = StartManager();
            source.HonorCancellation = false; // the cancelled build hangs until MVT arrives
            var coord = Tile(0);
            selector.Wanted.Add(coord);
            yield return WaitFor(() => source.Pending.ContainsKey(coord), "MVT request");

            var root = FindTileRoot(mapRoot, coord);
            Assert.That(root.gameObject.activeSelf, Is.True, "a loading tile is shown as it builds");

            var snapshot = new List<TileDebugInfo>();
            TileDebugInfo Info()
            {
                _manager.GetDebugSnapshot(snapshot);
                return snapshot.Single(t => t.Coord.Equals(coord));
            }

            // The tile is no longer wanted: its build is cancelled but hangs.
            selector.Wanted.Remove(coord);
            yield return WaitFor(() => Info().Residency == TileResidency.Cancelling, "cancellation of the unwanted tile");
            Assert.That(root.gameObject.activeSelf, Is.False);

            // The tile is wanted again before the cancellation completes: it is admitted but unfinished and will be released.
            selector.Wanted.Add(coord);
            yield return WaitFor(() => Info().Verdict == TileBudgetVerdict.Admitted, "repeated admission");
            yield return null; // active state is synced every frame; one more frame to avoid depending on order

            var info = Info();
            Assert.That(info.Residency, Is.EqualTo(TileResidency.Cancelling));
            Assert.That(info.IsRendered, Is.False, "a cancelling tile is not rendered");
            Assert.That(root.gameObject.activeSelf, Is.False);

            // The cancellation has completed; the tile loads again.
            source.Pending[coord].SetResult(MinimalMvt());
            yield return WaitFor(() => source.RequestCount[coord] == 2, "repeated tile request");
            Assert.That(selector.Failed, Is.Empty, "cancellation is not a failed load");
        }

        // ---- Shutdown -----------------------------------------------------------

        [UnityTest]
        public IEnumerator Shutdown_WhileGeometryJobReadsSharedGrid_NoErrors_GridsReleased()
        {
            var terrain = new JobTerrainHandler();
            // A larger grid so the terrain job cannot finish before shutdown.
            var (selector, _, mapRoot) = StartManager(terrain: terrain, gridResolution: 128);
            var coord = Tile(0);

            // Shutdown in GeoFoundryBootstrapper.OnDisable order (manager, then grids) while the tile's terrain job
            // is reading its shared grid.
            Exception shutdownError = null;
            Transform root = null;
            terrain.OnJobScheduled = () =>
            {
                root = FindTileRoot(mapRoot, coord);
                try
                {
                    _manager.Dispose();
                    _gridProvider.Dispose();
                }
                catch (Exception ex)
                {
                    shutdownError = ex;
                }
            };

            selector.Wanted.Add(coord);
            yield return WaitFor(() => terrain.OnJobScheduled == null, "terrain job scheduling");
            Assert.That(shutdownError, Is.Null, $"shutdown during the job: {shutdownError}");
            Assert.That(root, Is.Not.Null);

            yield return WaitFor(() => terrain.Finished.Contains(coord), "terrain job completion");
            yield return WaitFor(() => root == null, "tile release");
            Assert.That(_gridProvider.MemoryBytes, Is.Zero, "the tile grids are freed by the last handle");
        }
    }
}
