using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Tiles.Budget;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>
    /// Tests <see cref="TileBudgetPolicy"/>: admission of tiles by importance and eviction of loaded tiles under two
    /// limits. Tile cost accounting and frame-by-frame interplay of admission and eviction are covered by
    /// <see cref="TileBudgetTests"/>.
    /// </summary>
    public sealed class TileBudgetPolicyTests
    {
        private sealed class FixedCosts
        {
            private readonly Dictionary<TileCoord, TileMemoryUsage> _costs = new();

            public FixedCosts Set(TileCoord coord, long gpu, long heap = 0)
            {
                _costs[coord] = new TileMemoryUsage(gpu, heap);
                return this;
            }

            public TileMemoryUsage CostOf(TileCoord coord) =>
                _costs.TryGetValue(coord, out var cost) ? cost : TileMemoryUsage.Zero;
        }

        private static TileCoord T(int i) => new TileCoord(10, i, 0);

        private static (HashSet<TileCoord> Admitted, List<TileCoord> Denied) Admit(
            IReadOnlyList<TileCoord> byImportance, FixedCosts costs, long gpuLimit, long heapLimit = long.MaxValue)
        {
            var admitted = new HashSet<TileCoord> { T(999) }; // stale entry from the previous frame; must be cleared
            var denied = new List<TileCoord> { T(998) };
            TileBudgetPolicy.Admit(byImportance, costs.CostOf, new TileMemoryUsage(gpuLimit, heapLimit), admitted, denied);
            return (admitted, denied);
        }

        private static LoadedTileUsage Loaded(TileCoord coord, long gpu, int lastUsedFrame, long heap = 0) =>
            new LoadedTileUsage(coord, new TileMemoryUsage(gpu, heap), lastUsedFrame);

        /// <summary>A loaded tile from the wanted set at position <paramref name="rank"/> in importance order.</summary>
        private static LoadedTileUsage Wanted(TileCoord coord, long gpu, int lastUsedFrame, int rank) =>
            new LoadedTileUsage(coord, new TileMemoryUsage(gpu, 0), lastUsedFrame, rank);

        private static List<TileCoord> SelectEvictions(
            IEnumerable<LoadedTileUsage> loaded, IEnumerable<TileCoord> admitted, long gpuLimit,
            long heapLimit = long.MaxValue, long inFlightGpu = 0)
        {
            var evictions = new List<TileCoord> { T(997) }; // stale entry from the previous frame; must be cleared
            TileBudgetPolicy.SelectEvictions(
                loaded.ToList(), new TileMemoryUsage(inFlightGpu, 0), new HashSet<TileCoord>(admitted),
                new TileMemoryUsage(gpuLimit, heapLimit), evictions);
            return evictions;
        }

        [Test]
        public void Fits_RequiresBothLimits()
        {
            var limits = new TileMemoryUsage(10, 5);

            Assert.That(TileBudgetPolicy.Fits(new TileMemoryUsage(10, 5), limits), Is.True, "limit is inclusive");
            Assert.That(TileBudgetPolicy.Fits(new TileMemoryUsage(11, 0), limits), Is.False);
            Assert.That(TileBudgetPolicy.Fits(new TileMemoryUsage(0, 6), limits), Is.False);
        }

        // ---- Admit -------------------------------------------------------------

        [Test]
        public void Admit_EverythingFits_NothingDenied()
        {
            var order = new[] { T(0), T(1), T(2) };
            var costs = new FixedCosts().Set(T(0), 3).Set(T(1), 3).Set(T(2), 3);

            var (admitted, denied) = Admit(order, costs, gpuLimit: 9);

            Assert.That(admitted, Is.EquivalentTo(order));
            Assert.That(denied, Is.Empty);
        }

        [Test]
        public void Admit_PrefixPlusFirstMiss_LaterCheapTilesDenied()
        {
            var order = new[] { T(0), T(1), T(2), T(3) };
            var costs = new FixedCosts().Set(T(0), 4).Set(T(1), 5).Set(T(2), 1).Set(T(3), 1);

            var (admitted, denied) = Admit(order, costs, gpuLimit: 6);

            Assert.That(admitted, Is.EquivalentTo(new[] { T(0), T(1) }), "the first tile that does not fit is admitted: overshoot by one tile");
            Assert.That(denied, Is.EqualTo(new[] { T(2), T(3) }),
                "cheaper less important tiles are denied so the kept area stays contiguous; importance order is kept");
        }

        [Test]
        public void Admit_ExactFit_NextTileIsTheOverflow()
        {
            var order = new[] { T(0), T(1), T(2), T(3) };
            var costs = new FixedCosts().Set(T(0), 3).Set(T(1), 3).Set(T(2), 3).Set(T(3), 3);

            var (admitted, denied) = Admit(order, costs, gpuLimit: 6);

            Assert.That(admitted, Is.EquivalentTo(new[] { T(0), T(1), T(2) }), "limit is inclusive; the next tile is the overshoot");
            Assert.That(denied, Is.EqualTo(new[] { T(3) }));
        }

        [Test]
        public void Admit_HeapLimitAlsoBinds()
        {
            var order = new[] { T(0), T(1), T(2) };
            var costs = new FixedCosts().Set(T(0), 1, heap: 3).Set(T(1), 1, heap: 3).Set(T(2), 1, heap: 3);

            var (admitted, denied) = Admit(order, costs, gpuLimit: 100, heapLimit: 5);

            Assert.That(admitted, Is.EquivalentTo(new[] { T(0), T(1) }));
            Assert.That(denied, Is.EqualTo(new[] { T(2) }));
        }

        [Test]
        public void Admit_UnknownCost_IsOptimisticZero()
        {
            var order = new[] { T(0), T(1), T(2), T(3) };
            var costs = new FixedCosts().Set(T(0), 10).Set(T(2), 1).Set(T(3), 1); // T(1) is unknown

            var (admitted, denied) = Admit(order, costs, gpuLimit: 10);

            Assert.That(admitted, Is.EquivalentTo(new[] { T(0), T(1), T(2) }),
                "unknown cost does not block admission; T(2) is the one-tile overshoot");
            Assert.That(denied, Is.EqualTo(new[] { T(3) }));
        }

        [Test]
        public void Admit_FirstTileAboveLimit_AdmittedAlone()
        {
            var order = new[] { T(0), T(1) };
            var costs = new FixedCosts().Set(T(0), 11).Set(T(1), 1);

            var (admitted, denied) = Admit(order, costs, gpuLimit: 10);

            Assert.That(admitted, Is.EquivalentTo(new[] { T(0) }), "the most important tile above the budget does not leave the screen empty");
            Assert.That(denied, Is.EqualTo(new[] { T(1) }));
        }

        /// <summary>
        /// Checks the load/evict cycle at the budget boundary: a tile admitted with unknown cost no longer fits after
        /// loading but stays admitted; the tiles after it are denied, and evicted ones (unknown again) are not
        /// readmitted until the order changes.
        /// </summary>
        [Test]
        public void Admit_BoundaryTile_StaysAdmittedAfterLoad_FollowersStayDenied()
        {
            var order = new[] { T(0), T(1), T(2) };
            var costs = new FixedCosts().Set(T(0), 6);

            // Frame 1: T(1) and T(2) are not loaded yet and are admitted optimistically.
            var (admitted, denied) = Admit(order, costs, gpuLimit: 10);
            Assert.That(admitted, Is.EquivalentTo(order));
            Assert.That(denied, Is.Empty);

            // Frame 2: T(1) and T(2) have loaded; the total exceeds the limit at T(1).
            costs.Set(T(1), 6).Set(T(2), 6);
            (admitted, denied) = Admit(order, costs, gpuLimit: 10);
            Assert.That(admitted, Is.EquivalentTo(new[] { T(0), T(1) }), "boundary tile stays admitted");
            Assert.That(denied, Is.EqualTo(new[] { T(2) }));

            // Frame 3: the denied T(2) is evicted; its cost is unknown again, but the denial persists.
            costs = new FixedCosts().Set(T(0), 6).Set(T(1), 6);
            (admitted, denied) = Admit(order, costs, gpuLimit: 10);
            Assert.That(admitted, Is.EquivalentTo(new[] { T(0), T(1) }));
            Assert.That(denied, Is.EqualTo(new[] { T(2) }), "evicted tile is not readmitted");
        }

        [Test]
        public void Admit_ReturnsBoundaryTile()
        {
            TileCoord? Boundary(TileCoord[] order, FixedCosts costs, long gpuLimit) =>
                TileBudgetPolicy.Admit(order, costs.CostOf, new TileMemoryUsage(gpuLimit, long.MaxValue),
                    new HashSet<TileCoord>(), new List<TileCoord>());

            var order = new[] { T(0), T(1), T(2), T(3) };
            var costs = new FixedCosts().Set(T(0), 3).Set(T(1), 3).Set(T(2), 3).Set(T(3), 3);

            Assert.That(Boundary(order, costs, gpuLimit: 12), Is.Null, "everything fits: no boundary tile");
            Assert.That(Boundary(order, costs, gpuLimit: 6), Is.EqualTo(T(2)), "first tile that does not fit");
            Assert.That(Boundary(order, costs, gpuLimit: 10), Is.EqualTo(T(3)), "the last tile can be the boundary");
            Assert.That(Boundary(order, costs, gpuLimit: 2), Is.EqualTo(T(0)), "most important tile exceeds the budget");
            Assert.That(Boundary(new TileCoord[0], costs, gpuLimit: 0), Is.Null);
        }

        [Test]
        public void Admit_EmptyOrder_ClearsOutputs()
        {
            var (admitted, denied) = Admit(new TileCoord[0], new FixedCosts(), gpuLimit: 10);

            Assert.That(admitted, Is.Empty);
            Assert.That(denied, Is.Empty);
        }

        [Test]
        public void Admit_UnitCosts_AdmitsFirstNPlusOne()
        {
            var order = Enumerable.Range(0, 10).Select(T).ToArray();
            var costs = new FixedCosts();
            foreach (var t in order) costs.Set(t, 1);

            var (admitted, denied) = Admit(order, costs, gpuLimit: 4);

            Assert.That(admitted, Is.EquivalentTo(order.Take(5)), "budget in tile counts: the first N + 1 by importance");
            Assert.That(denied, Is.EqualTo(order.Skip(5)));
        }

        // ---- SelectEvictions ---------------------------------------------------

        [Test]
        public void SelectEvictions_WithinLimits_EvictsNothing()
        {
            var loaded = new[] { Loaded(T(0), 3, lastUsedFrame: 1), Loaded(T(1), 3, lastUsedFrame: 2) };

            var evictions = SelectEvictions(loaded, admitted: new TileCoord[0], gpuLimit: 8, inFlightGpu: 2);

            Assert.That(evictions, Is.Empty, "resident tiles exactly at the limit: limit is inclusive");
        }

        [Test]
        public void SelectEvictions_LeastRecentlyUsedFirst_UntilFits()
        {
            var loaded = new[]
            {
                Loaded(T(0), 4, lastUsedFrame: 5),
                Loaded(T(1), 4, lastUsedFrame: 2),
                Loaded(T(2), 4, lastUsedFrame: 9),
                Loaded(T(3), 4, lastUsedFrame: 3)
            };

            var evictions = SelectEvictions(loaded, admitted: new TileCoord[0], gpuLimit: 8);

            Assert.That(evictions, Is.EqualTo(new[] { T(1), T(3) }), "least recently used first, and no more than needed");
        }

        [Test]
        public void SelectEvictions_AdmittedNeverEvicted_EvenOverLimits()
        {
            var loaded = new[]
            {
                Loaded(T(0), 10, lastUsedFrame: 1),
                Loaded(T(1), 5, lastUsedFrame: 2),
                Loaded(T(2), 5, lastUsedFrame: 3)
            };

            var evictions = SelectEvictions(loaded, admitted: new[] { T(0) }, gpuLimit: 8);

            Assert.That(evictions, Is.EqualTo(new[] { T(1), T(2) }),
                "all non-admitted tiles are evicted; the admitted one stays even though limits are still exceeded");
        }

        [Test]
        public void SelectEvictions_InFlightCountedButNotEvicted()
        {
            var loaded = new[] { Loaded(T(0), 3, lastUsedFrame: 1), Loaded(T(1), 3, lastUsedFrame: 2) };

            var evictions = SelectEvictions(loaded, admitted: new TileCoord[0], gpuLimit: 8, inFlightGpu: 6);

            Assert.That(evictions, Is.EqualTo(new[] { T(0), T(1) }), "memory for in-flight tiles is freed by evicting loaded ones");
        }

        [Test]
        public void SelectEvictions_HeapLimitAlsoBinds()
        {
            var loaded = new[]
            {
                Loaded(T(0), 1, lastUsedFrame: 1, heap: 5),
                Loaded(T(1), 1, lastUsedFrame: 2, heap: 5)
            };

            var evictions = SelectEvictions(loaded, admitted: new TileCoord[0], gpuLimit: 100, heapLimit: 6);

            Assert.That(evictions, Is.EqualTo(new[] { T(0) }));
        }

        [Test]
        public void SelectEvictions_EqualRecency_DeterministicTiebreak()
        {
            var tiles = new[]
            {
                Loaded(new TileCoord(11, 5, 5), 1, lastUsedFrame: 7),
                Loaded(new TileCoord(10, 7, 0), 1, lastUsedFrame: 7),
                Loaded(new TileCoord(10, 3, 9), 1, lastUsedFrame: 7),
                Loaded(new TileCoord(10, 3, 2), 1, lastUsedFrame: 7)
            };
            var expected = new[] { new TileCoord(10, 3, 2), new TileCoord(10, 3, 9), new TileCoord(10, 7, 0) };

            Assert.That(SelectEvictions(tiles, admitted: new TileCoord[0], gpuLimit: 1), Is.EqualTo(expected),
                "equal recency is ordered by Z, X, Y");
            Assert.That(SelectEvictions(tiles.Reverse(), admitted: new TileCoord[0], gpuLimit: 1), Is.EqualTo(expected),
                "result does not depend on input order");
        }

        [Test]
        public void SelectEvictions_NotWantedBeforeDenied_DeniedFromLeastImportant()
        {
            var loaded = new[]
            {
                Wanted(T(0), 2, lastUsedFrame: 1, rank: 4),  // denied, more important
                Wanted(T(1), 2, lastUsedFrame: 1, rank: 9),  // denied, least important
                Loaded(T(2), 2, lastUsedFrame: 8),           // warm, used recently
                Loaded(T(3), 2, lastUsedFrame: 5),           // warm, used long ago
                Wanted(T(4), 2, lastUsedFrame: 1, rank: 0)   // admitted
            };

            var all = SelectEvictions(loaded, admitted: new[] { T(4) }, gpuLimit: 0);
            Assert.That(all, Is.EqualTo(new[] { T(3), T(2), T(1), T(0) }),
                "warm cache by recency, then denied tiles from least important, even though they were used earliest");

            var partial = SelectEvictions(loaded, admitted: new[] { T(4) }, gpuLimit: 5);
            Assert.That(partial, Is.EqualTo(new[] { T(3), T(2), T(1) }), "the more important denied tile stays if there is room");
        }
    }
}
