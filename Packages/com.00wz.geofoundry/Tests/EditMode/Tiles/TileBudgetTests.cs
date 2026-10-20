using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.TileSelection;
using GeoFoundry.Tiles.Budget;
using GeoFoundry.Tiles.Diagnostics;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>
    /// Tests <see cref="TileBudget"/>: budget state and per-frame decisions (tile cost by state, build cancellation,
    /// eviction and load selection) and their frame-by-frame interplay with a fake executor
    /// (<see cref="FakeLoader"/>) standing in for <c>TileLifecycleManager</c>. Snapshot-based admission and eviction
    /// are covered by <see cref="TileBudgetPolicyTests"/>.
    /// </summary>
    public sealed class TileBudgetTests
    {
        private static readonly TileMemoryUsage Unlimited = new TileMemoryUsage(long.MaxValue, long.MaxValue);

        private static TileCoord T(int i) => new TileCoord(10, i, 0);

        private static TileMemoryUsage Gpu(long gpu, long heap = 0) => new TileMemoryUsage(gpu, heap);

        /// <summary>Limit on the GPU pool only.</summary>
        private static TileMemoryUsage GpuLimit(long gpu) => new TileMemoryUsage(gpu, long.MaxValue);

        /// <summary>
        /// Builds a selector result: <paramref name="byImportance"/> is the importance order, <paramref name="toLoad"/>
        /// the load requests by priority (all tiles in importance order by default). Tiles without a request go to
        /// <c>TilesToKeep</c>, so <c>ByImportance</c> is exactly Render ∪ Load ∪ Keep.
        /// </summary>
        private static TileSelectionResult Selection(IEnumerable<TileCoord> byImportance, IEnumerable<TileCoord> toLoad = null)
        {
            var selection = new TileSelectionResult();
            selection.ByImportance.AddRange(byImportance);
            selection.TilesToLoad.AddRange(toLoad ?? selection.ByImportance);
            selection.TilesToKeep.UnionWith(selection.ByImportance.Except(selection.TilesToLoad));
            return selection;
        }

        /// <summary>Loads a tile outside the frame phases: start followed immediately by completion.</summary>
        private static void Load(TileBudget budget, TileCoord coord, TileMemoryUsage cost)
        {
            budget.OnLoadStarted(coord, () => cost);
            budget.OnLoadCompleted(coord);
        }

        /// <summary>
        /// Executes <see cref="TileBudget"/> decisions in place of <c>TileLifecycleManager</c>: each frame runs the
        /// three budget phases and carries them out. A load takes <see cref="LoadFrames"/> frames, the memory of an
        /// in-flight tile grows to its actual value (<see cref="Actual"/>), and loads complete and cancellations
        /// finish between frames (see <see cref="CancelFrames"/>).
        /// </summary>
        private sealed class FakeLoader
        {
            public readonly TileBudget Budget;
            public readonly Dictionary<TileCoord, TileMemoryUsage> Actual;
            public int LoadFrames = 1;

            /// <summary>
            /// Number of frames until a cancellation finishes: 0 means immediately, like a build aborted inside
            /// <c>Cancel</c>; 1 means by the next frame, before its <see cref="TileBudget.Admit"/>; more means the
            /// tile goes through admission while still cancelling. Until then it holds its memory and slot.
            /// </summary>
            public int CancelFrames;

            /// <summary>Current memory of resident tiles, as read by the budget.</summary>
            public readonly Dictionary<TileCoord, TileMemoryUsage> Memory = new();

            /// <summary>Number of times each tile started loading.</summary>
            public readonly Dictionary<TileCoord, int> LoadCount = new();

            /// <summary>Decisions of the last frame.</summary>
            public readonly List<TileCoord> Cancelled = new(), Evicted = new(), Started = new();

            /// <summary>In-flight tiles → frames spent building; cancelled tiles do not advance here.</summary>
            private readonly Dictionary<TileCoord, int> _building = new();

            /// <summary>Cancelling tiles → frames until the cancellation finishes.</summary>
            private readonly Dictionary<TileCoord, int> _cancelling = new();

            public FakeLoader(TileBudget budget, Dictionary<TileCoord, TileMemoryUsage> actual)
            {
                Budget = budget;
                Actual = actual;
            }

            public void Step(TileSelectionResult selection)
            {
                // Between frames: cancellations finish, builds advance and complete.
                foreach (var coord in _cancelling.Keys.ToList())
                {
                    if (--_cancelling[coord] > 0) continue;
                    _cancelling.Remove(coord);
                    Abort(coord);
                }
                foreach (var coord in _building.Keys.ToList())
                {
                    int frames = ++_building[coord];
                    var actual = Actual[coord];
                    Memory[coord] = new TileMemoryUsage(
                        actual.GpuBytes * frames / LoadFrames, actual.HeapBytes * frames / LoadFrames);
                    if (frames < LoadFrames) continue;

                    _building.Remove(coord);
                    Budget.OnLoadCompleted(coord);
                }

                Budget.Admit(selection, Cancelled);
                foreach (var coord in Cancelled)
                {
                    _building.Remove(coord);
                    if (CancelFrames == 0) Abort(coord);
                    else _cancelling[coord] = CancelFrames;
                }

                Budget.Evict(Evicted);
                foreach (var coord in Evicted) Memory.Remove(coord);

                Budget.SelectStarts(Started);
                foreach (var coord in Started)
                {
                    Memory[coord] = TileMemoryUsage.Zero;
                    _building[coord] = 0;
                    LoadCount[coord] = LoadCount.TryGetValue(coord, out int count) ? count + 1 : 1;
                    Budget.OnLoadStarted(coord, () => Memory[coord]);
                }
            }

            private void Abort(TileCoord coord)
            {
                Budget.OnLoadAborted(coord);
                Memory.Remove(coord);
            }
        }

        // ---- CostOf ------------------------------------------------------------

        [Test]
        public void CostOf_Loaded_IsActual()
        {
            var budget = new TileBudget(Unlimited, 4);
            Load(budget, T(1), Gpu(100, 100));
            Load(budget, T(0), Gpu(7, 2));

            Assert.That(budget.CostOf(T(0)), Is.EqualTo(Gpu(7, 2)), "estimate is ignored when the actual cost is known");
        }

        [Test]
        public void CostOf_InFlight_IsAtLeastExpected_PerPool()
        {
            var budget = new TileBudget(Unlimited, 4);
            Load(budget, T(1), Gpu(10, 4)); // the estimate for unknown tiles is the average: (10, 4)
            budget.OnLoadStarted(T(0), () => Gpu(3, 9));

            Assert.That(budget.CostOf(T(0)), Is.EqualTo(Gpu(10, 9)),
                "expected cost while accumulated is lower, otherwise accumulated; per pool");
        }

        [Test]
        public void CostOf_NotResident_LoadRequested_IsEstimate()
        {
            var budget = new TileBudget(Unlimited, 4);
            Load(budget, T(1), Gpu(5, 1));
            budget.Admit(Selection(new[] { T(0) }), new List<TileCoord>());

            Assert.That(budget.CostOf(T(0)), Is.EqualTo(Gpu(5, 1)));
        }

        [Test]
        public void CostOf_NotResident_NotRequested_IsZero()
        {
            // E.g. a failed load that the selector keeps as an empty slot and does not retry.
            var budget = new TileBudget(Unlimited, 4);
            Load(budget, T(1), Gpu(5, 1));
            budget.Admit(Selection(new[] { T(0) }, toLoad: new TileCoord[0]), new List<TileCoord>());

            Assert.That(budget.CostOf(T(0)), Is.EqualTo(TileMemoryUsage.Zero), "admission will not trigger a load, so the tile takes no memory");
        }

        [Test]
        public void OnLoadCompleted_TeachesEstimate_ByMemoryAtCompletion()
        {
            var budget = new TileBudget(Unlimited, 4);
            var memory = Gpu(1);
            budget.OnLoadStarted(T(0), () => memory);
            budget.Admit(Selection(new[] { T(0), T(1) }), new List<TileCoord>());
            Assert.That(budget.CostOf(T(1)), Is.EqualTo(TileMemoryUsage.Zero), "optimistic while nothing has loaded");

            memory = Gpu(9);
            budget.OnLoadCompleted(T(0));
            Assert.That(budget.CostOf(T(1)), Is.EqualTo(Gpu(9)), "unknown tile uses the actual cost of the completed one");
        }

        // ---- Admit: cancellation -----------------------------------------------

        [Test]
        public void Admit_CancelsInFlightTilesThatLostAdmission_Once()
        {
            // Each tile is estimated at 3 with a limit of 5: T(0) fits, T(1) is the boundary, T(2) is denied.
            var budget = new TileBudget(GpuLimit(5), 4);
            Load(budget, T(9), Gpu(3));
            foreach (var coord in new[] { T(0), T(1), T(2) }) budget.OnLoadStarted(coord, () => TileMemoryUsage.Zero);
            var toCancel = new List<TileCoord>();

            budget.Admit(Selection(new[] { T(0), T(1), T(2) }), toCancel);
            Assert.That(toCancel, Is.EqualTo(new[] { T(2) }), "denied by the budget");
            Assert.That(budget.ResidencyOf(T(2)), Is.EqualTo(TileResidency.Cancelling));

            budget.Admit(Selection(new[] { T(0) }), toCancel);
            Assert.That(toCancel, Is.EqualTo(new[] { T(1) }),
                "no longer wanted; an already cancelling tile is not re-cancelled, a loaded one is not in flight");
            Assert.That(budget.ResidencyOf(T(0)), Is.EqualTo(TileResidency.Loading), "admitted tile keeps building");
        }

        // ---- SelectStarts --------------------------------------------------------

        [Test]
        public void SelectStarts_InLoadOrder_OnlyAdmittedAndNotResident_UpToFreeSlots()
        {
            // Each tile costs 2 with a limit of 5: T(0), T(1) and boundary T(2) are admitted, T(3), T(4) are denied.
            var budget = new TileBudget(GpuLimit(5), maxConcurrentLoads: 2);
            Load(budget, T(0), Gpu(2));
            var importance = new[] { T(0), T(1), T(2), T(3), T(4) };
            var toStart = new List<TileCoord>();

            budget.Admit(Selection(importance, toLoad: new[] { T(0), T(4), T(3), T(2), T(1) }), new List<TileCoord>());
            budget.SelectStarts(toStart);

            Assert.That(toStart, Is.EqualTo(new[] { T(2), T(1) }),
                "request order, not importance; loaded and denied tiles are skipped");
        }

        [Test]
        public void SelectStarts_CancellingTileHoldsSlot_AndIsNotRestartedUntilAborted()
        {
            var budget = new TileBudget(Unlimited, maxConcurrentLoads: 2);
            var all = new[] { T(0), T(1), T(2) };
            var toCancel = new List<TileCoord>();
            var toStart = new List<TileCoord>();

            budget.Admit(Selection(all), toCancel);
            budget.SelectStarts(toStart);
            Assert.That(toStart, Is.EqualTo(new[] { T(0), T(1) }));
            foreach (var coord in toStart) budget.OnLoadStarted(coord, () => TileMemoryUsage.Zero);

            // T(0) is no longer needed: its build is cancelled but holds a slot until the cancellation finishes.
            budget.Admit(Selection(new[] { T(1), T(2) }), toCancel);
            Assert.That(toCancel, Is.EqualTo(new[] { T(0) }));
            budget.SelectStarts(toStart);
            Assert.That(toStart, Is.Empty, "both slots are taken, one by the cancelling tile");

            // T(0) is needed again while the cancellation is still pending: it is not reloaded.
            budget.Admit(Selection(all), toCancel);
            Assert.That(toCancel, Is.Empty);
            Assert.That(budget.IsAdmitted(T(0)), Is.True);
            budget.SelectStarts(toStart);
            Assert.That(toStart, Is.Empty);

            budget.OnLoadAborted(T(0));
            budget.Admit(Selection(all), toCancel);
            budget.SelectStarts(toStart);
            Assert.That(toStart, Is.EqualTo(new[] { T(0) }), "cancellation finished: the slot is free and the tile reloads");
        }

        [Test]
        public void SelectStarts_DuplicateRequest_StartedOnce()
        {
            var budget = new TileBudget(Unlimited, maxConcurrentLoads: 4);
            var toStart = new List<TileCoord>();

            budget.Admit(Selection(new[] { T(0), T(1) }, toLoad: new[] { T(0), T(0), T(1) }), new List<TileCoord>());
            budget.SelectStarts(toStart);

            Assert.That(toStart, Is.EqualTo(new[] { T(0), T(1) }));
        }

        // ---- Evict ---------------------------------------------------------------

        [Test]
        public void Evict_CountsMemoryOfLoadsInFlight()
        {
            // Warm T(0) and T(1) cost 3 each, the building T(2) already holds 4, limit 8: without it nothing would be
            // evicted.
            var budget = new TileBudget(GpuLimit(8), 4);
            Load(budget, T(0), Gpu(3));
            Load(budget, T(1), Gpu(3));
            budget.OnLoadStarted(T(2), () => Gpu(4));
            var evicted = new List<TileCoord>();

            budget.Admit(Selection(new[] { T(2) }), new List<TileCoord>());
            budget.Evict(evicted);

            Assert.That(evicted, Is.EqualTo(new[] { T(0) }), "memory for the in-flight tile is freed by evicting loaded ones");
            Assert.That(budget.LoadedTiles, Is.EquivalentTo(new[] { T(1) }), "evicted tile is forgotten immediately");
        }

        /// <summary>
        /// Checks the recency and importance order tracked by the budget itself: the warm cache is evicted by
        /// admission recency, then denied tiles from least important, even though they were used earliest.
        /// </summary>
        [Test]
        public void Evict_WarmCacheByRecency_ThenDeniedFromLeastImportant()
        {
            // Each tile costs 2 with a limit of 3: T(2) is admitted, T(3) is the boundary, T(4) and T(5) are denied.
            var budget = new TileBudget(GpuLimit(3), 4);
            for (int i = 0; i < 6; i++) Load(budget, T(i), Gpu(2));
            var none = new List<TileCoord>();
            var evicted = new List<TileCoord>();

            budget.Admit(Selection(new[] { T(1) }), none); // T(1) admitted in frame 1
            budget.Admit(Selection(new[] { T(0) }), none); // T(0) in frame 2
            budget.Admit(Selection(new[] { T(2), T(3), T(4), T(5) }), none);
            budget.Evict(evicted);

            Assert.That(evicted, Is.EqualTo(new[] { T(1), T(0), T(5), T(4) }));
            Assert.That(budget.Boundary, Is.EqualTo(T(3)));
            Assert.That(budget.LoadedMemoryExceptBoundary(), Is.EqualTo(Gpu(2)), "loaded tiles except the boundary fit");
        }

        // ---- Load contract ---------------------------------------------------------

        [Test]
        public void Loads_ContractViolations()
        {
            var budget = new TileBudget(Unlimited, 4);
            budget.OnLoadStarted(T(0), () => Gpu(1));
            Load(budget, T(1), Gpu(1));

            Assert.Throws<InvalidOperationException>(() => budget.OnLoadStarted(T(0), () => Gpu(1)), "already loading");
            Assert.Throws<InvalidOperationException>(() => budget.OnLoadStarted(T(1), () => Gpu(1)), "already loaded");
            Assert.Throws<InvalidOperationException>(() => budget.OnLoadCompleted(T(2)), "not loading");

            budget.Admit(Selection(new TileCoord[0]), new List<TileCoord>()); // T(0) is no longer needed and is cancelled
            Assert.Throws<InvalidOperationException>(() => budget.OnLoadCompleted(T(0)), "cancelled build cannot complete");

            budget.OnLoadAborted(T(0));
            budget.OnLoadAborted(T(0));
            budget.OnLoadAborted(T(2));
            budget.OnLoadAborted(T(1));
            Assert.That(budget.ResidencyOf(T(0)), Is.EqualTo(TileResidency.None), "repeated call and unknown tile are no-ops");
            Assert.That(budget.ResidencyOf(T(1)), Is.EqualTo(TileResidency.Loaded), "aborted load does not affect a loaded tile");
        }

        // ---- Frame by frame ------------------------------------------------------

        /// <summary>
        /// In a steady state at the budget limit, a loaded tile leaves the wanted set. A zero cost for unloaded
        /// tiles would admit them all at once, only to deny and evict them right after loading. With the estimate,
        /// only what fits into the freed space is admitted and no load is wasted.
        /// </summary>
        [Test]
        public void Simulation_LoadedTileLeavesWantedSet_AdmitsOnlyWhatFits_NoWastedLoads()
        {
            var order = Enumerable.Range(0, 12).Select(T).ToList();
            var loader = new FakeLoader(new TileBudget(GpuLimit(10), int.MaxValue), order.ToDictionary(t => t, _ => Gpu(3)));

            for (int i = 0; i < 10; i++) loader.Step(Selection(order));
            Assume.That(loader.Budget.LoadedTiles, Is.EquivalentTo(order.Take(4)), "steady state: 3 + 1 boundary tile at 3 each with a limit of 10");
            Assume.That(loader.Budget.Denied, Has.Count.EqualTo(8));

            // The most important loaded tile leaves the wanted set, freeing room for exactly one tile.
            order.RemoveAt(0);
            var loadsBefore = new Dictionary<TileCoord, int>(loader.LoadCount);
            var evictedAfter = new HashSet<TileCoord>();
            for (int i = 0; i < 10; i++)
            {
                loader.Step(Selection(order));
                evictedAfter.UnionWith(loader.Evicted);
            }

            var loadedAfter = loader.LoadCount.Where(kv => kv.Value > (loadsBefore.TryGetValue(kv.Key, out int n) ? n : 0))
                .Select(kv => kv.Key).ToArray();
            Assert.That(loadedAfter, Is.EqualTo(new[] { T(4) }), "exactly one new tile is admitted and loaded");
            Assert.That(loadedAfter.Where(evictedAfter.Contains), Is.Empty, "no loads are wasted");
            Assert.That(loader.Budget.Denied, Has.Count.EqualTo(order.Count - 4), "the rest stay denied");
        }

        /// <summary>
        /// Random costs and limits with a fixed importance order, where a loaded tile occasionally leaves the wanted
        /// set, as when the camera moves. Loads take one frame, so no cancellation happens. The budget converges,
        /// loaded tiles except the boundary fit the limits after eviction, and wasted loads (loaded, then evicted)
        /// are rare: they occur only while the estimate for unknown tiles (the average) differs from the actual cost.
        /// </summary>
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(int.MaxValue)]
        public void Simulation_RandomCostsAndWantedSetShrinks_ConvergesWithFewWastedLoads(int maxConcurrentLoads)
        {
            const int tileCount = 20;
            var rng = new System.Random(20261009 + maxConcurrentLoads);
            int totalLoads = 0, wastedLoads = 0;

            for (int iter = 0; iter < 50; iter++)
            {
                var order = Enumerable.Range(0, tileCount).Select(T).ToList();
                var actual = order.ToDictionary(t => t, _ => new TileMemoryUsage(rng.Next(1, 6), rng.Next(0, 3)));
                var limits = new TileMemoryUsage(rng.Next(5, 40), rng.Next(3, 30));
                var loader = new FakeLoader(new TileBudget(limits, maxConcurrentLoads), actual);
                var budget = loader.Budget;
                var startedAtFrame = new Dictionary<TileCoord, int>();

                for (int frame = 1; frame <= 6 * tileCount; frame++)
                {
                    string ctx = $"maxConcurrentLoads={maxConcurrentLoads}, iter={iter}, frame={frame}, limits={limits}";

                    // Occasionally a loaded admitted tile leaves the wanted set; the last frames have no drops so loads
                    // can converge.
                    if (frame % 15 == 0 && frame <= 4 * tileCount && order.Count > 4)
                    {
                        var candidates = order.Where(c => budget.ResidencyOf(c) == TileResidency.Loaded && budget.IsAdmitted(c)).ToList();
                        if (candidates.Count > 0) order.Remove(candidates[rng.Next(candidates.Count)]);
                    }

                    loader.Step(Selection(order));
                    foreach (var c in loader.Evicted)
                        if (startedAtFrame.TryGetValue(c, out int f) && f > 1 && order.Contains(c)) wastedLoads++;
                    foreach (var c in loader.Started) startedAtFrame[c] = frame;

                    Assert.That(TileBudgetPolicy.Fits(budget.LoadedMemoryExceptBoundary(), limits), Is.True,
                        ctx + ": after eviction, loaded tiles except the boundary fit");
                }

                string final = $"maxConcurrentLoads={maxConcurrentLoads}, iter={iter}, limits={limits}";
                Assert.That(budget.InFlightCount, Is.Zero, final + ": loads converged");
                Assert.That(budget.Admitted.All(c => budget.ResidencyOf(c) == TileResidency.Loaded), Is.True,
                    final + ": admitted tiles are loaded");
                totalLoads += loader.LoadCount.Values.Sum();
            }

            // A wasted load is a tile that started loading after the first frame (when no estimates exist yet) and was
            // evicted while still wanted by the selector. Without the estimate (zero cost) every dropped tile admits
            // the whole tail of denied tiles, wasting about a third of loads with 3 concurrent loads and over half
            // with no limit. With the estimate only misses of the average for never-loaded tiles remain: an expensive
            // tile shifts the boundary and tiles loaded alongside it become surplus (about 6% with 3 concurrent
            // loads, 0 with one or no limit).
            Assert.That(wastedLoads, Is.LessThanOrEqualTo(totalLoads / 10), $"{wastedLoads} of {totalLoads} loads wasted");
        }

        /// <summary>
        /// Loads span several frames and in-flight memory grows; load order differs from importance order; builds
        /// that lose admission are cancelled, and the cancellation finishes immediately or after several frames,
        /// holding memory and a slot meanwhile and possibly being readmitted; one tile is wanted without a load
        /// request (an empty slot after a failed load). Tiles leave and rejoin the wanted set, and a building tile
        /// also disappears for one frame, as with camera jitter. Every frame, loaded tiles except the boundary fit
        /// the limits after eviction and in-flight tiles do not exceed the slots; once the set stops changing the
        /// budget converges: admitted tiles with a request are loaded and nothing more is loaded, cancelled or
        /// evicted.
        /// </summary>
        [TestCase(1, 1, 0)]
        [TestCase(3, 2, 0)]
        [TestCase(3, 3, 2)]
        [TestCase(5, 4, 4)]
        public void Simulation_MultiFrameLoadsWithCancellation_KeepsInvariantsAndConverges(
            int loadFrames, int maxConcurrentLoads, int cancelFrames)
        {
            const int tileCount = 24;
            const int churnFrames = 8 * tileCount;
            int settleFrames = 6 * tileCount * (loadFrames + cancelFrames);
            var rng = new System.Random(20261010 + 100 * loadFrames + 10 * maxConcurrentLoads + cancelFrames);

            for (int iter = 0; iter < 30; iter++)
            {
                var tiles = Enumerable.Range(0, tileCount).Select(T).ToList();
                var actual = tiles.ToDictionary(t => t, _ => new TileMemoryUsage(rng.Next(1, 6), rng.Next(0, 3)));
                var limits = new TileMemoryUsage(rng.Next(5, 40), rng.Next(3, 30));
                var loadKey = tiles.ToDictionary(t => t, _ => rng.Next());
                var noLoad = tiles[rng.Next(tileCount)];
                var loader = new FakeLoader(new TileBudget(limits, maxConcurrentLoads), actual)
                {
                    LoadFrames = loadFrames,
                    CancelFrames = cancelFrames
                };
                var budget = loader.Budget;
                var order = new List<TileCoord>(tiles);
                var dropped = new List<TileCoord>();
                TileCoord? flickered = null;
                int flickeredAt = 0;

                TileSelectionResult Select() =>
                    Selection(order, toLoad: order.Where(t => t != noLoad).OrderBy(t => loadKey[t]));

                for (int frame = 1; frame <= churnFrames + settleFrames; frame++)
                {
                    string ctx = $"loadFrames={loadFrames}, slots={maxConcurrentLoads}, cancelFrames={cancelFrames}, " +
                                 $"iter={iter}, frame={frame}, limits={limits}";

                    // A building tile disappears from the wanted set for one frame.
                    if (flickered.HasValue)
                    {
                        order.Insert(Math.Min(flickeredAt, order.Count), flickered.Value);
                        flickered = null;
                    }
                    else if (frame <= churnFrames && frame % 5 == 0)
                    {
                        var building = order.Where(c => budget.ResidencyOf(c) == TileResidency.Loading).ToList();
                        if (building.Count > 0)
                        {
                            flickered = building[rng.Next(building.Count)];
                            flickeredAt = order.IndexOf(flickered.Value);
                            order.RemoveAt(flickeredAt);
                        }
                    }

                    // During the churn phase a tile occasionally leaves the wanted set or returns to it.
                    if (frame <= churnFrames && frame % 7 == 0)
                    {
                        if (dropped.Count > 0 && (order.Count <= 4 || rng.Next(2) == 0))
                        {
                            var back = dropped[rng.Next(dropped.Count)];
                            dropped.Remove(back);
                            order.Insert(rng.Next(order.Count + 1), back);
                        }
                        else if (order.Count > 4)
                        {
                            var drop = order[rng.Next(order.Count)];
                            order.Remove(drop);
                            dropped.Add(drop);
                        }
                    }

                    loader.Step(Select());

                    Assert.That(TileBudgetPolicy.Fits(budget.LoadedMemoryExceptBoundary(), limits), Is.True,
                        ctx + ": after eviction, loaded tiles except the boundary fit");
                    Assert.That(budget.InFlightCount, Is.LessThanOrEqualTo(maxConcurrentLoads), ctx + ": slots");
                }

                string final = $"loadFrames={loadFrames}, slots={maxConcurrentLoads}, cancelFrames={cancelFrames}, " +
                               $"iter={iter}, limits={limits}";
                Assert.That(budget.InFlightCount, Is.Zero, final + ": loads converged");
                Assert.That(budget.Admitted.Where(c => c != noLoad).All(c => budget.ResidencyOf(c) == TileResidency.Loaded),
                    Is.True, final + ": admitted tiles with a request are loaded");
                Assert.That(budget.ResidencyOf(noLoad), Is.EqualTo(TileResidency.None), final + ": tile without a request is not loaded");

                for (int i = 0; i < 10; i++)
                {
                    loader.Step(Select());
                    Assert.That(loader.Started.Concat(loader.Cancelled).Concat(loader.Evicted), Is.Empty,
                        final + ": steady state");
                }
            }
        }
    }
}
