using System;
using System.Collections.Generic;
using System.Threading;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.Core.TileSelection;
using GeoFoundry.Tiles.Budget;
using GeoFoundry.Tiles.Diagnostics;
using UnityEngine;

namespace GeoFoundry.Tiles
{
    /// <summary>
    /// Manages the tile lifecycle. Each frame it passes the frame input (<see cref="TileFrameInput"/>: loaded tiles,
    /// tiles denied by the budget in the previous frame, failed loads) to the <see cref="ITileSelector"/> and receives
    /// a <see cref="TileSelectionResult"/>: what to show, what to load, what to keep in memory, and in what order of
    /// importance. From this selection the budget (<see cref="TileBudget"/>) decides which tiles to admit, which
    /// builds to cancel, which loaded tiles to evict and which loads to start; the manager executes these decisions:
    /// cancels builds, releases tiles, builds new ones (<see cref="TileBuilder"/>, throttled by
    /// <see cref="MaxConcurrentLoads"/>) and synchronizes tile activation. A tile is rendered when the selector asks to
    /// show it and the budget admitted it. The budget rules, its softness and invariants are described in
    /// <see cref="TileBudget"/>; an invariant violation is logged. Use <see cref="GetDebugSnapshot"/> to inspect tile
    /// states.
    ///
    /// The manager does not store failed loads; it only passes them to the selector in the next frame's input
    /// (<see cref="TileFrameInput.FailedSinceLastFrame"/>), and whether to retry such a tile is entirely up to the
    /// selector. External code is notified of each failure by the <see cref="TileLoadFailed"/> event.
    /// Ticks itself after <see cref="Start"/>, without a MonoBehaviour.
    /// </summary>
    public sealed class TileLifecycleManager : IDisposable 
    {
        private readonly TileBuilder _tileBuilder;
        private readonly ITileSelector _selector;
        private readonly IFloatingOriginController _originController;
        private readonly Transform _mapRoot;

        /// <summary>The budget: in-memory tile state and per-frame decisions.</summary>
        private readonly TileBudget _budget;

        /// <summary>Result of the last selector call.</summary>
        private readonly TileSelectionResult _selection = new();

        /// <summary>
        /// Tiles in memory: loaded (active and hidden) and building, including cancelling ones; their budget state is
        /// in <see cref="_budget"/>. A tile is added together with reporting the load start to the budget
        /// (<see cref="LoadTileAsync"/>) and removed on eviction (<see cref="EvictTiles"/>) or an aborted load.
        /// </summary>
        private readonly Dictionary<TileCoord, TileData> _tiles = new();

        /// <summary>Cancellation of in-flight builds (see <see cref="LoadTileAsync"/>); linked to the shared <see cref="_cts"/>.</summary>
        private readonly Dictionary<TileCoord, CancellationTokenSource> _loadCts = new();

        /// <summary>Failed loads since the last selector call (see <see cref="TileFrameInput.FailedSinceLastFrame"/>).</summary>
        private readonly List<TileCoord> _failedSinceLastFrame = new();

        /// <summary>Scratch: in-flight tiles whose builds must be cancelled.</summary>
        private readonly List<TileCoord> _toCancel = new();

        /// <summary>Scratch: evicted tiles.</summary>
        private readonly List<TileCoord> _evicted = new(64);

        /// <summary>Scratch: tiles whose loads must be started.</summary>
        private readonly List<TileCoord> _toStart = new();

        private readonly HashSet<TileCoord> _validationUnionScratch = new();
        private readonly HashSet<TileCoord> _validationOrderScratch = new();

        private readonly CancellationTokenSource _cts = new();
        private bool _started;
        private bool _disposed;

        /// <summary>Creates a manager. Call <see cref="Start"/> to begin loading tiles.</summary>
        /// <param name="tileBuilder">Builds tile content.</param>
        /// <param name="selector">Chooses which tiles to show, load and keep each frame.</param>
        /// <param name="originController">Floating-origin controller that tiles subscribe to for rebases.</param>
        /// <param name="mapRoot">
        /// Parent of tile objects. Tiles are positioned locally relative to it, while tile selection and floating
        /// origin work in world coordinates, so the root must sit at the origin with no rotation or scale.
        /// </param>
        /// <param name="budget">
        /// Memory limits for resident tiles, in bytes, in both pools. They are exceeded by design (see
        /// <see cref="TileBudget"/>), so leave headroom.
        /// </param>
        /// <param name="maxConcurrentLoads">How many tiles may build at once, including cancelling ones; at least 1.</param>
        public TileLifecycleManager(
            TileBuilder tileBuilder,
            ITileSelector selector,
            IFloatingOriginController originController,
            Transform mapRoot,
            TileMemoryUsage budget,
            int maxConcurrentLoads = 4)
        {
            _tileBuilder     = tileBuilder      ?? throw new ArgumentNullException(nameof(tileBuilder));
            _selector         = selector         ?? throw new ArgumentNullException(nameof(selector));
            _originController = originController ?? throw new ArgumentNullException(nameof(originController));
            _mapRoot          = mapRoot;
            _budget           = new TileBudget(budget, maxConcurrentLoads);
        }

        // ---- Public API -------------------------------------------------------

        /// <summary>
        /// Starts the self-driven update loop based on <see cref="Awaitable.NextFrameAsync"/>. Repeated calls are
        /// ignored.
        /// </summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            _ = RunLoop(_cts.Token);
        }

        /// <summary>
        /// Raised when a tile load fails with an error (not a cancellation). Source failures arrive as
        /// <see cref="TileSourceException"/>, in the source's own subclass with the transport details (an HTTP source
        /// throws <c>GeoFoundry.TileIO.Net.TileDownloadException</c> with the HTTP status); other errors arrive as is. Raised on the main thread after
        /// the error is logged. Notification only: the manager does not store the error, and the selector decides
        /// whether to retry the tile (<see cref="TileFrameInput.FailedSinceLastFrame"/>). Exceptions thrown by
        /// subscribers are logged and do not affect the manager.
        /// </summary>
        public event Action<TileCoord, Exception> TileLoadFailed;

        /// <summary>Gets the budget limits: memory of resident tiles in both pools.</summary>
        public TileMemoryUsage Budget => _budget.Limits;

        /// <summary>Gets how many tiles may build at once, including cancelling ones.</summary>
        public int MaxConcurrentLoads => _budget.MaxConcurrentLoads;

        // ---- Diagnostics -----------------------------------------------------

        /// <summary>
        /// Fills <paramref name="tiles"/> (cleared first) with the debug state of every tile the manager knows about:
        /// wanted by the selector, loaded or building. Budget decisions and the selection are from the last frame;
        /// residency and memory are current. The manager accumulates nothing for debugging: the call is O(tiles) and
        /// costs nothing when not called. Use <see cref="TileDebugSummary.From"/> for aggregates and
        /// <see cref="TileDebugOverlay"/> for drawing.
        /// </summary>
        /// <param name="tiles">Receives one entry per tile.</param>
        public void GetDebugSnapshot(List<TileDebugInfo> tiles)
        {
            tiles.Clear();

            // The budget splits the desired set into admitted and denied, so an in-memory tile in neither is outside
            // the desired set: each tile appears in the snapshot exactly once.
            foreach (var coord in _selection.ByImportance) tiles.Add(DescribeForDebug(coord));
            foreach (var coord in _tiles.Keys)
                if (!_budget.IsAdmitted(coord) && !_budget.IsDenied(coord)) tiles.Add(DescribeForDebug(coord));
        }

        private TileDebugInfo DescribeForDebug(TileCoord coord)
        {
            var residency = _budget.ResidencyOf(coord);
            var verdict = coord == _budget.Boundary ? TileBudgetVerdict.Boundary
                : _budget.IsAdmitted(coord) ? TileBudgetVerdict.Admitted
                : _budget.IsDenied(coord) ? TileBudgetVerdict.Denied
                : TileBudgetVerdict.NotWanted;

            bool rendered = residency != TileResidency.None && IsRendered(coord);
            return new TileDebugInfo(
                coord, residency, verdict, rendered, _budget.IsLoadRequested(coord), _budget.MemoryOf(coord), _budget.CostOf(coord));
        }

        // ---- Loop ------------------------------------------------------------

        private async Awaitable RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Awaitable.NextFrameAsync(ct); }
                catch (OperationCanceledException) { break; }

                // An error in one frame must not stop tile loading forever: the loop is not awaited, so an escaping
                // exception would end it silently.
                try { DoUpdate(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        /// <summary>
        /// One frame: the selector's choice, then the budget phases, each followed by executing its decision (see
        /// <see cref="TileBudget"/>). If the selector has nothing to select, the whole frame is skipped.
        /// </summary>
        private void DoUpdate()
        {
            var input = new TileFrameInput(_budget.LoadedTiles, _budget.Denied, _failedSinceLastFrame);
            if (!_selector.SelectTiles(in input, _selection)) return;
            _failedSinceLastFrame.Clear();
            ValidateSelection();

            _budget.Admit(_selection, _toCancel);
            CancelLoads();
            ReconcileActivation();
            EvictTiles();
            AssertLoadedWithinBudget();
            StartLoads();
        }

        // ---- Internal --------------------------------------------------------

        /// <summary>
        /// Whether a tile is rendered: the selector asks to show it, the budget admitted it, and its build is not
        /// cancelling — a cancelling tile is incomplete and will be released even if admitted again.
        /// </summary>
        private bool IsRendered(TileCoord coord) =>
            _selection.TilesToRender.Contains(coord) && _budget.IsAdmitted(coord) &&
            _budget.ResidencyOf(coord) != TileResidency.Cancelling;

        /// <summary>
        /// Checks the <see cref="TileSelectionResult.ByImportance"/> contract: every tile of
        /// <c>TilesToRender ∪ TilesToLoad ∪ TilesToKeep</c> appears exactly once. Otherwise a tile the selector left out
        /// of the importance list would be denied silently.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private void ValidateSelection()
        {
            _validationUnionScratch.Clear();
            _validationUnionScratch.UnionWith(_selection.TilesToRender);
            _validationUnionScratch.UnionWith(_selection.TilesToLoad);
            _validationUnionScratch.UnionWith(_selection.TilesToKeep);

            _validationOrderScratch.Clear();
            _validationOrderScratch.UnionWith(_selection.ByImportance);

            Debug.Assert(
                _validationOrderScratch.Count == _selection.ByImportance.Count &&
                _validationOrderScratch.SetEquals(_validationUnionScratch),
                "[GeoFoundry] TileSelectionResult.ByImportance must contain every tile of " +
                "TilesToRender ∪ TilesToLoad ∪ TilesToKeep exactly once.");
        }

        /// <summary>
        /// Cancels builds of tiles the budget no longer admits (see <see cref="TileBudget.Admit"/>). <c>Cancel</c> runs
        /// token callbacks synchronously, and the build may finish inside it, removing the tile from
        /// <see cref="_tiles"/> and <see cref="_loadCts"/>; that is why the loop iterates the budget's list rather than
        /// the dictionaries. Otherwise the cancelled tile stays resident until the cancellation reaches
        /// <see cref="LoadTileAsync"/>, which releases it.
        /// </summary>
        private void CancelLoads()
        {
            foreach (var coord in _toCancel)
                if (_loadCts.TryGetValue(coord, out var cts)) cts.Cancel();
        }

        /// <summary>
        /// Synchronizes activation of in-memory tiles with <see cref="IsRendered"/>. In-flight tiles are synchronized
        /// every frame too, not once at load completion; otherwise a tile no longer needed mid-build would keep
        /// rendering until its load finished. <see cref="TileData.SetActive"/> is a no-op if the state is unchanged.
        /// </summary>
        private void ReconcileActivation()
        {
            foreach (var kv in _tiles) kv.Value.SetActive(IsRendered(kv.Key));
        }

        /// <summary>
        /// Releases loaded tiles evicted by the budget (<see cref="TileBudget.Evict"/>). Called after cancellation: a
        /// cancellation that completed synchronously has already freed its tile's memory. A failure to release one
        /// tile is logged and does not affect the others; the budget has already forgotten the tile, so it is removed
        /// from <see cref="_tiles"/> regardless.
        /// </summary>
        private void EvictTiles()
        {
            _budget.Evict(_evicted);
            foreach (var coord in _evicted)
            {
                try { _tiles[coord].Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }
                _tiles.Remove(coord);
            }
        }

        /// <summary>
        /// Starts the loads selected by the budget (<see cref="TileBudget.SelectStarts"/>): admitted tiles from
        /// <see cref="TileSelectionResult.TilesToLoad"/> in priority order, up to the free slots.
        /// <see cref="LoadTileAsync"/> registers the tile synchronously.
        /// </summary>
        private void StartLoads()
        {
            _budget.SelectStarts(_toStart);
            foreach (var coord in _toStart) _ = LoadTileAsync(coord, startInactive: !IsRendered(coord));
        }

        /// <summary>
        /// Checks the budget invariant right after eviction: loaded tiles other than the boundary one fit the limits
        /// (see <see cref="TileBudget.LoadedMemoryExceptBoundary"/>). A violation is a memory accounting or eviction
        /// bug, not a by-design overshoot.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private void AssertLoadedWithinBudget()
        {
            var loaded = _budget.LoadedMemoryExceptBoundary();
            if (!TileBudgetPolicy.Fits(loaded, _budget.Limits))
                Debug.LogAssertion(
                    $"[GeoFoundry] Loaded tiles except the boundary one exceed the budget after eviction: " +
                    $"{loaded} > {_budget.Limits}. Tile memory accounting or eviction is broken.");
        }

        private void RaiseTileLoadFailed(TileCoord coord, Exception error)
        {
            try { TileLoadFailed?.Invoke(coord, error); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private async Awaitable LoadTileAsync(TileCoord coord, bool startInactive)
        {
            var tileRoot = new GameObject($"Tile_{coord}");
            tileRoot.transform.SetParent(_mapRoot, worldPositionStays: false);
            // Subscribes to PivotChanged and positions itself immediately, before the build starts. Otherwise a
            // floating-origin rebase during the asynchronous build would be missed and the tile would be offset by
            // the pivot difference. With the subscription, a rebase at any stage of TileBuilder.PopulateAsync is
            // handled right away.
            var tileData = new TileData(coord, tileRoot, _originController);

            // A tile that is not rendered right now builds under a disabled hierarchy so it does not overlap
            // rendered tiles until it becomes rendered (checked every frame, see ReconcileActivation).
            if (startInactive) tileData.SetActive(false);

            // Register as loading synchronously, before the first await. Otherwise ReconcileActivation could not
            // control the tile during the build, the budget could not tell "loading" from "not requested", would not
            // count the tile against concurrent loads or see its accumulated memory, and CancelLoads could not cancel
            // the build.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _tiles[coord] = tileData;
            _loadCts[coord] = cts;
            _budget.OnLoadStarted(coord, () => tileData.MemoryUsage);

            bool loaded = false;
            try
            {
                await _tileBuilder.PopulateAsync(tileData, coord, cts.Token);

                // The build may complete without an exception even after cancellation (layer handlers return null
                // rather than throw), leaving the tile likely incomplete. Such a tile is not cached; it is released
                // like any cancellation.
                cts.Token.ThrowIfCancellationRequested();

                // The selection and budget decision may have changed during the asynchronous load; the final
                // activation follows them rather than startInactive.
                tileData.SetActive(IsRendered(coord));
                _budget.OnLoadCompleted(coord);
                loaded = true;
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                // Budget denial or manager shutdown. Not a failure: the tile may be loaded again, even if the
                // cancellation surfaced as something other than OperationCanceledException.
                tileData.Dispose();
            }
            catch (OperationCanceledException)
            {
                tileData.Dispose();
            }
            catch (TileSourceException ex)
            {
                tileData.Dispose();
                Debug.LogError($"[GeoFoundry] Failed {coord}: {ex.Message}");
                _failedSinceLastFrame.Add(coord);
                RaiseTileLoadFailed(coord, ex);
            }
            catch (Exception ex)
            {
                tileData.Dispose();
                Debug.LogError($"[GeoFoundry] Unexpected error loading {coord}: {ex}");
                _failedSinceLastFrame.Add(coord);
                RaiseTileLoadFailed(coord, ex);
            }
            finally
            {
                _loadCts.Remove(coord);
                if (!loaded)
                {
                    _budget.OnLoadAborted(coord);
                    _tiles.Remove(coord);
                }
                cts.Dispose();
            }
        }

        // ---- IDisposable -----------------------------------------------------

        /// <summary>
        /// Stops the manager: cancels builds and releases loaded tiles. A failure to release one tile is logged and
        /// does not affect the others. Repeated calls are ignored.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // In-flight tiles are released by their LoadTileAsync once the cancellation arrives: those waiting for
            // downloads or ground grids right inside Cancel(), those waiting for a job when it completes (the job
            // reads build data, see JobHandleAwaitExtensions.WaitNonBlockingAsync). Cancel() aggregates callback
            // exceptions into an AggregateException after all callbacks have run.
            try { _cts.Cancel(); }
            catch (Exception ex) { Debug.LogException(ex); }
            _cts.Dispose();

            foreach (var kv in _tiles)
            {
                if (_budget.ResidencyOf(kv.Key) != TileResidency.Loaded) continue;
                try { kv.Value.Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            _tiles.Clear();
            _loadCts.Clear();
            _budget.Clear();

            _selection.Clear();
            _failedSinceLastFrame.Clear();
        }
    }
}
