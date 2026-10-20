using System;
using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.TileSelection;
using GeoFoundry.Tiles.Diagnostics;

namespace GeoFoundry.Tiles.Budget
{
    /// <summary>
    /// Tile memory budget: tracks resident tiles and makes the per-frame decisions — which tiles of the desired set
    /// to admit, which builds to cancel, which loaded tiles to evict and which loads to start.
    /// It never releases, cancels or loads anything itself: the caller (<see cref="TileLifecycleManager"/>) carries
    /// out the decisions and reports loads back (<see cref="OnLoadStarted"/>, <see cref="OnLoadCompleted"/>,
    /// <see cref="OnLoadAborted"/>). Has no Unity dependencies; not thread-safe.
    ///
    /// A frame consists of three phases in strict order; between phases the caller executes the previous decision:
    /// <list type="number">
    /// <item><see cref="Admit"/> — admission from the selector's choice and cancellation of non-admitted builds;</item>
    /// <item><see cref="Evict"/> — eviction. Only after the cancellations are executed: a cancellation may complete
    ///   synchronously inside <c>Cancel</c> and free its tile's memory;</item>
    /// <item><see cref="SelectStarts"/> — which loads to start.</item>
    /// </list>
    ///
    /// <b>Admission.</b> From the selector's desired set, the prefix of <see cref="TileSelectionResult.ByImportance"/>
    /// whose total cost fits the limits is admitted, plus the first tile that does not fit — the boundary tile
    /// (<see cref="TileBudgetPolicy.Admit"/>); the rest are denied. Admitted tiles are rendered, protected from
    /// eviction and loaded; denied tiles are not, and are passed to the selector in the next frame
    /// (<see cref="Denied"/> → <see cref="TileFrameInput.DeniedLastFrame"/>). The budget is memory in two
    /// <see cref="TileMemoryUsage"/> pools (GPU and heap); a tile is admitted only if it fits both. A tile's
    /// admission cost is the memory it will occupy if admitted (<see cref="CostOf"/>). Shared DEM ground grids are
    /// not counted.
    ///
    /// <b>Cancellation.</b> An in-flight tile keeps building only while it is admitted: the build of a tile denied by
    /// the budget or no longer wanted by the selector is cancelled in the same frame, so the warm cache holds only
    /// loaded tiles. Otherwise an unneeded tile would hold a concurrency slot and memory while admitted tiles wait,
    /// and a denied one would hold memory needed by more important tiles. There is no delay: flicker at the budget
    /// edge is damped by importance hysteresis in the selector, and leaving the desired set is a real change in what
    /// the camera needs. A cancelling tile counts as resident and holds its slot until the cancellation reaches the
    /// end of the build (<see cref="OnLoadAborted"/>). Similar to <c>Cesium3DTileset.cancelOutOfViewRequests</c>, but
    /// Cesium cancels only the network phase; here the build is cancelled too, since it is expensive (terrain
    /// subdivision) and can be interrupted between handlers.
    ///
    /// <b>Eviction.</b> Resident tiles are loaded and loading tiles (a loading tile allocates memory during the
    /// build). When resident tiles exceed the limits, loaded tiles not admitted this frame are evicted: first tiles
    /// outside the desired set (warm cache), least recently used first, then denied tiles, least important first
    /// (<see cref="TileBudgetPolicy.SelectEvictions"/>); admitted tiles never. Eviction uses actual memory.
    ///
    /// <b>The budget is soft</b>: resident tiles exceed the limits by design, but boundedly —
    /// <c>resident ≤ limits + boundary tile + all in-flight tiles
    /// ≤ limits + (MaxConcurrentLoads + 1) × heaviest tile</c>. In-flight tiles exceed the limits in two ways:
    /// admitted ones may turn out more expensive than the estimate they were admitted with, and cancelling ones hold
    /// memory until the cancellation completes; together there are at most <see cref="MaxConcurrentLoads"/> of them.
    /// Loads of admitted tiles must start even when nothing is left to evict: room for an admitted tile is freed by
    /// eviction only once it occupies actual memory — otherwise an important unloaded tile would wait for room,
    /// and the denied tiles after it would not be evicted until it loads — a deadlock. Right after eviction, loaded
    /// tiles other than the boundary one always fit the limits (<see cref="LoadedMemoryExceptBoundary"/>): a
    /// violation is a memory accounting or eviction bug, not a by-design overshoot.
    /// </summary>
    /// <remarks>
    /// Possible improvement: cancellation hysteresis — cancel an in-flight tile only after it stays unadmitted for
    /// N consecutive frames, if measurements show frequent restarts of the same tiles.
    /// </remarks>
    internal sealed class TileBudget
    {
        private sealed class LoadedTile
        {
            public Func<TileMemoryUsage> Memory;

            /// <summary>Frame in which the tile was last admitted (or finished loading).</summary>
            public int LastUsedFrame;
        }

        private sealed class InFlightTile
        {
            /// <summary>Accumulated memory; grows as the build progresses.</summary>
            public Func<TileMemoryUsage> Memory;

            /// <summary>
            /// Cost estimate at start: while accumulated memory is lower, the budget uses this value
            /// (see <see cref="CostOf"/>).
            /// </summary>
            public TileMemoryUsage ExpectedCost;

            /// <summary>The build is cancelled, but the cancellation has not completed yet (<see cref="OnLoadAborted"/>).</summary>
            public bool Cancelling;
        }

        private readonly Dictionary<TileCoord, LoadedTile> _loaded = new();

        /// <summary>Tiles being built, including cancelling ones.</summary>
        private readonly Dictionary<TileCoord, InFlightTile> _inFlight = new();

        /// <summary>Estimates the cost of tiles not yet in memory from the actual cost of loaded ones.</summary>
        private readonly TileCostEstimator _estimator = new();

        /// <summary><see cref="CostOf"/> as a delegate for <see cref="TileBudgetPolicy.Admit"/>, without a per-frame allocation.</summary>
        private readonly Func<TileCoord, TileMemoryUsage> _costOf;

        /// <summary>Tiles admitted by the last <see cref="Admit"/>; exactly these are protected from eviction.</summary>
        private readonly HashSet<TileCoord> _admitted = new();

        /// <summary>Tiles denied by the last <see cref="Admit"/> (see <see cref="Denied"/>).</summary>
        private readonly HashSet<TileCoord> _denied = new();

        /// <summary>Position of each desired tile in the importance order of the last <see cref="Admit"/>.</summary>
        private readonly Dictionary<TileCoord, int> _importanceRank = new();

        /// <summary><see cref="TileSelectionResult.TilesToLoad"/> of the last <see cref="Admit"/>; the start order.</summary>
        private readonly List<TileCoord> _loadOrder = new(64);

        /// <summary>Tiles of <see cref="_loadOrder"/>, for membership checks.</summary>
        private readonly HashSet<TileCoord> _loadRequests = new();

        /// <summary><see cref="Evict"/> scratch: snapshot of loaded tiles for <see cref="TileBudgetPolicy.SelectEvictions"/>.</summary>
        private readonly List<LoadedTileUsage> _loadedScratch = new(64);

        private TileCoord? _boundary;
        private int _frame;

        /// <param name="limits">
        /// Memory limits for resident tiles, in bytes, in both pools. They are exceeded by design (see the class
        /// docs), so leave headroom.
        /// </param>
        /// <param name="maxConcurrentLoads">How many tiles may build at once, including cancelling ones; at least 1.</param>
        public TileBudget(TileMemoryUsage limits, int maxConcurrentLoads)
        {
            if (limits.GpuBytes < 0 || limits.HeapBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(limits), limits, "Budget limits cannot be negative.");

            Limits = limits;
            MaxConcurrentLoads = Math.Max(1, maxConcurrentLoads);
            _costOf = CostOf;
        }

        /// <summary>Gets the memory limits for resident (loaded and loading) tiles in both pools.</summary>
        public TileMemoryUsage Limits { get; }

        /// <summary>Gets how many tiles may build at once, including cancelling ones.</summary>
        public int MaxConcurrentLoads { get; }

        /// <summary>Gets the loaded tiles. This is a live view that changes on loads and evictions.</summary>
        public IReadOnlyCollection<TileCoord> LoadedTiles => _loaded.Keys;

        /// <summary>Gets the number of tiles currently building, including cancelling ones.</summary>
        public int InFlightCount => _inFlight.Count;

        /// <summary>Gets the tiles admitted by the last <see cref="Admit"/>, including the boundary tile.</summary>
        public IReadOnlyCollection<TileCoord> Admitted => _admitted;

        /// <summary>
        /// Gets the tiles denied by the last <see cref="Admit"/>. This is a live view that the next <c>Admit</c>
        /// refills, so a selector receiving it as <see cref="TileFrameInput.DeniedLastFrame"/> copies it
        /// synchronously inside <see cref="ITileSelector.SelectTiles"/>.
        /// </summary>
        public IReadOnlyCollection<TileCoord> Denied => _denied;

        /// <summary>Gets the boundary tile of the last <see cref="Admit"/> (admitted beyond the limits); <c>null</c> if everything fit.</summary>
        public TileCoord? Boundary => _boundary;

        public bool IsAdmitted(TileCoord coord) => _admitted.Contains(coord);

        public bool IsDenied(TileCoord coord) => _denied.Contains(coord);

        /// <summary>Returns whether the selector requests loading the tile (<see cref="TileSelectionResult.TilesToLoad"/> of the last <see cref="Admit"/>).</summary>
        public bool IsLoadRequested(TileCoord coord) => _loadRequests.Contains(coord);

        /// <summary>Returns whether the tile is absent, loading, cancelling or loaded.</summary>
        public TileResidency ResidencyOf(TileCoord coord)
        {
            if (_loaded.ContainsKey(coord)) return TileResidency.Loaded;
            if (_inFlight.TryGetValue(coord, out var inFlight))
                return inFlight.Cancelling ? TileResidency.Cancelling : TileResidency.Loading;
            return TileResidency.None;
        }

        /// <summary>
        /// Returns the tile's admission cost — the memory it will occupy if admitted:
        /// <list type="bullet">
        /// <item>loaded — actual memory;</item>
        /// <item>building — accumulated memory, but no less than the estimate at start, since actual memory grows
        ///   during the build;</item>
        /// <item>not in memory but requested by the selector — the <see cref="TileCostEstimator"/> estimate: the
        ///   tile's past cost or the average over loaded tiles;</item>
        /// <item>not in memory and not requested (e.g. a failed load the selector does not retry) — zero: admission
        ///   will not cause a load.</item>
        /// </list>
        /// A zero cost for a tile that will be loaded would break admission: denials hold only while admitted tiles
        /// exceed the limits, so as soon as the total drops below them (e.g. a loaded tile leaves the desired set),
        /// all zero-cost tiles would be admitted at once, however many, then denied and evicted after loading. With
        /// an estimate, freed room is taken by roughly as many tiles as fit in it. For the same reason a building
        /// tile costs no less than expected; otherwise its cost would drop almost to zero right after start.
        /// </summary>
        public TileMemoryUsage CostOf(TileCoord coord)
        {
            if (_loaded.TryGetValue(coord, out var loaded)) return loaded.Memory();
            if (_inFlight.TryGetValue(coord, out var inFlight)) return TileMemoryUsage.Max(inFlight.Memory(), inFlight.ExpectedCost);
            return _loadRequests.Contains(coord) ? _estimator.Estimate(coord) : TileMemoryUsage.Zero;
        }

        /// <summary>Returns the actual memory of a loaded or building tile; zero if the tile is not in memory.</summary>
        public TileMemoryUsage MemoryOf(TileCoord coord)
        {
            if (_loaded.TryGetValue(coord, out var loaded)) return loaded.Memory();
            if (_inFlight.TryGetValue(coord, out var inFlight)) return inFlight.Memory();
            return TileMemoryUsage.Zero;
        }

        /// <summary>
        /// Returns the memory of loaded tiles other than the boundary tile. Right after <see cref="Evict"/> it always
        /// fits <see cref="Limits"/>: either eviction stopped because resident (and hence loaded) tiles fit, or
        /// nothing was left to evict — then only admitted tiles are loaded, and admitted tiles without the boundary
        /// one fit by construction of <see cref="TileBudgetPolicy.Admit"/>. Relies on no loads completing between
        /// <see cref="Admit"/> and <see cref="Evict"/>. In-flight tiles are excluded; their overshoot is by design
        /// (see the class docs).
        /// </summary>
        public TileMemoryUsage LoadedMemoryExceptBoundary()
        {
            var total = TileMemoryUsage.Zero;
            foreach (var kv in _loaded)
                if (kv.Key != _boundary) total += kv.Value.Memory();
            return total;
        }

        // ---- Frame -------------------------------------------------------------

        /// <summary>
        /// Frame phase 1: admission from the selector's choice (see the class docs). Everything needed until the
        /// next <c>Admit</c> — the importance order and load requests — is copied from <paramref name="selection"/>,
        /// so the budget does not keep it. Also refreshes the recency of admitted loaded tiles and marks
        /// non-admitted in-flight tiles as cancelling; the caller must cancel their builds.
        /// </summary>
        /// <param name="toCancel">Filled with tiles whose builds must be cancelled (cleared first).</param>
        public void Admit(TileSelectionResult selection, List<TileCoord> toCancel)
        {
            _frame++;

            var byImportance = selection.ByImportance;
            _importanceRank.Clear();
            for (int i = 0; i < byImportance.Count; i++) _importanceRank[byImportance[i]] = i;

            // Requests go before admission: the cost of unloaded tiles depends on them.
            _loadOrder.Clear();
            _loadOrder.AddRange(selection.TilesToLoad);
            _loadRequests.Clear();
            _loadRequests.UnionWith(_loadOrder);

            _boundary = TileBudgetPolicy.Admit(byImportance, _costOf, Limits, _admitted, _denied);

            foreach (var coord in _admitted)
                if (_loaded.TryGetValue(coord, out var loaded)) loaded.LastUsedFrame = _frame;

            toCancel.Clear();
            foreach (var kv in _inFlight)
            {
                if (kv.Value.Cancelling || _admitted.Contains(kv.Key)) continue;
                kv.Value.Cancelling = true;
                toCancel.Add(kv.Key);
            }
        }

        /// <summary>
        /// Frame phase 2: trims overshoot of the limits (e.g. after loads complete) by evicting non-admitted loaded
        /// tiles, as decided by <see cref="TileBudgetPolicy.SelectEvictions"/> (see the class docs). Called after the
        /// caller has cancelled the builds from <see cref="Admit"/>: a cancellation that completed synchronously has
        /// already freed its tile's memory, and cancelling tiles still pending count as resident. The budget forgets
        /// evicted tiles immediately; the caller releases them.
        /// </summary>
        /// <param name="evicted">Filled with evicted tiles in eviction order (cleared first).</param>
        public void Evict(List<TileCoord> evicted)
        {
            _loadedScratch.Clear();
            foreach (var kv in _loaded)
            {
                int rank = _importanceRank.TryGetValue(kv.Key, out int r) ? r : LoadedTileUsage.NotWanted;
                _loadedScratch.Add(new LoadedTileUsage(kv.Key, kv.Value.Memory(), kv.Value.LastUsedFrame, rank));
            }

            var inFlightUsage = TileMemoryUsage.Zero;
            foreach (var tile in _inFlight.Values) inFlightUsage += tile.Memory();

            TileBudgetPolicy.SelectEvictions(_loadedScratch, inFlightUsage, _admitted, Limits, evicted);
            foreach (var coord in evicted) _loaded.Remove(coord);
        }

        /// <summary>
        /// Frame phase 3: selects loads to start — admitted tiles from <see cref="TileSelectionResult.TilesToLoad"/>
        /// in its order (the selector's load priority, not importance), up to the free
        /// <see cref="MaxConcurrentLoads"/> slots. No room is freed in advance: admission already reserved it by
        /// estimate, and eviction frees actual memory once the tile occupies it. Loads are selected even if resident
        /// tiles no longer fit the limits; otherwise the boundary tile would deadlock (see the class docs).
        ///
        /// Tiles already loaded or loading are skipped, including cancelling ones: such a tile starts again once the
        /// cancellation completes and holds its slot until then. A tile whose load failed is not skipped: if the
        /// selector requests it again, it is retried. The caller reports each start with <see cref="OnLoadStarted"/>.
        /// </summary>
        /// <param name="toStart">Filled with tiles to load, in order (cleared first).</param>
        public void SelectStarts(List<TileCoord> toStart)
        {
            toStart.Clear();
            int freeSlots = MaxConcurrentLoads - _inFlight.Count;
            foreach (var coord in _loadOrder)
            {
                if (toStart.Count >= freeSlots) break;
                if (!_admitted.Contains(coord) || _loaded.ContainsKey(coord) || _inFlight.ContainsKey(coord)) continue;
                if (!toStart.Contains(coord)) toStart.Add(coord); // a duplicate request loads once
            }
        }

        // ---- Loads -------------------------------------------------------------

        /// <summary>
        /// Reports that a tile load has started. <paramref name="memory"/> returns the tile's current memory; the
        /// budget reads it when deciding, and it grows during the build. The tile's expected cost is the estimate at
        /// start (see <see cref="CostOf"/>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The tile is already loaded or loading.</exception>
        public void OnLoadStarted(TileCoord coord, Func<TileMemoryUsage> memory)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            if (_loaded.ContainsKey(coord) || _inFlight.ContainsKey(coord))
                throw new InvalidOperationException($"Tile {coord} is already in memory.");

            _inFlight.Add(coord, new InFlightTile { Memory = memory, ExpectedCost = _estimator.Estimate(coord) });
        }

        /// <summary>
        /// Reports that a tile build has completed. Its actual cost is remembered for estimates
        /// (<see cref="TileCostEstimator"/>). Loads must not complete between <see cref="Admit"/> and
        /// <see cref="Evict"/> (see <see cref="LoadedMemoryExceptBoundary"/>).
        /// </summary>
        /// <exception cref="InvalidOperationException">The tile is not loading, or its build was cancelled.</exception>
        public void OnLoadCompleted(TileCoord coord)
        {
            if (!_inFlight.TryGetValue(coord, out var inFlight))
                throw new InvalidOperationException($"Tile {coord} is not loading.");
            if (inFlight.Cancelling)
                throw new InvalidOperationException($"Build of tile {coord} was cancelled; it cannot complete.");

            _inFlight.Remove(coord);
            _loaded.Add(coord, new LoadedTile { Memory = inFlight.Memory, LastUsedFrame = _frame });
            _estimator.Remember(coord, inFlight.Memory());
        }

        /// <summary>
        /// Reports that a tile load was aborted by cancellation or error and the tile is no longer in memory.
        /// A no-op for a tile that is not loading (e.g. after <see cref="Clear"/>).
        /// </summary>
        public void OnLoadAborted(TileCoord coord) => _inFlight.Remove(coord);

        /// <summary>Forgets all tiles and decisions when the caller stops. Cost estimates are kept.</summary>
        public void Clear()
        {
            _loaded.Clear();
            _inFlight.Clear();
            _admitted.Clear();
            _denied.Clear();
            _importanceRank.Clear();
            _loadOrder.Clear();
            _loadRequests.Clear();
            _boundary = null;
        }
    }
}
