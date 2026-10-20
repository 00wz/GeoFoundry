using System;
using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Tiles.Budget
{
    /// <summary>A loaded tile in the snapshot passed to <see cref="TileBudgetPolicy.SelectEvictions"/>.</summary>
    internal readonly struct LoadedTileUsage
    {
        public readonly TileCoord Coord;

        /// <summary>Actual memory of the tile.</summary>
        public readonly TileMemoryUsage Cost;

        /// <summary>Frame in which the budget last admitted the tile.</summary>
        public readonly int LastUsedFrame;

        /// <summary>
        /// Position of the tile in the current frame's importance order (0 is the most important);
        /// <see cref="NotWanted"/> if the tile is outside the selector's desired set.
        /// </summary>
        public readonly int ImportanceRank;

        public const int NotWanted = -1;

        public LoadedTileUsage(TileCoord coord, TileMemoryUsage cost, int lastUsedFrame, int importanceRank = NotWanted)
        {
            Coord = coord;
            Cost = cost;
            LastUsedFrame = lastUsedFrame;
            ImportanceRank = importanceRank;
        }
    }

    /// <summary>
    /// Tile budget decisions as pure functions without Unity dependencies: which tiles to admit
    /// (<see cref="Admit"/>) and which loaded tiles to evict (<see cref="SelectEvictions"/>). The budget is given
    /// as limits in both <see cref="TileMemoryUsage"/> pools; a tile fits only if it fits both.
    /// <see cref="TileBudget"/> builds the snapshot of its tiles for them and stores the decisions.
    /// </summary>
    internal static class TileBudgetPolicy
    {
        /// <summary>
        /// Eviction order: tiles outside the desired set first, least recently used first, then desired (denied)
        /// tiles, least important first.
        /// </summary>
        private static readonly Comparison<LoadedTileUsage> CompareEvictionOrder = (a, b) =>
        {
            bool aWanted = a.ImportanceRank != LoadedTileUsage.NotWanted;
            bool bWanted = b.ImportanceRank != LoadedTileUsage.NotWanted;
            if (aWanted != bWanted) return aWanted ? 1 : -1;
            if (aWanted) return b.ImportanceRank.CompareTo(a.ImportanceRank);

            int byFrame = a.LastUsedFrame.CompareTo(b.LastUsedFrame);
            if (byFrame != 0) return byFrame;
            // Deterministic tie-break: the order must not depend on dictionary enumeration order.
            int byZ = a.Coord.Z.CompareTo(b.Coord.Z);
            if (byZ != 0) return byZ;
            int byX = a.Coord.X.CompareTo(b.Coord.X);
            return byX != 0 ? byX : a.Coord.Y.CompareTo(b.Coord.Y);
        };

        /// <summary>Returns whether <paramref name="usage"/> fits both limits.</summary>
        public static bool Fits(TileMemoryUsage usage, TileMemoryUsage limits) =>
            usage.GpuBytes <= limits.GpuBytes && usage.HeapBytes <= limits.HeapBytes;

        /// <summary>
        /// Admits by importance: the prefix of <paramref name="byImportance"/> whose total cost fits
        /// <paramref name="limits"/>, plus the first tile that does not fit (a one-tile overshoot). All less important
        /// tiles after it are denied, even if some would fit individually; otherwise the kept area would stop being
        /// contiguous (cheap distant tiles around a hole where an expensive near tile should be). The cost of a tile
        /// not yet in memory is an estimate (see <see cref="TileBudget.CostOf"/>); once loaded, its actual cost is used.
        ///
        /// The one-tile overshoot breaks the load-evict cycle at the budget edge: a tile admitted by estimate may turn
        /// out more expensive after loading and stop fitting. With a strict prefix it would be denied, evicted,
        /// admitted again by estimate, and so on. With the overshoot it stays admitted and the tiles after it are
        /// denied. Admitted tiles exceed the limits by at most one tile. It also keeps the screen from staying empty
        /// when the most important tile alone exceeds the budget.
        /// </summary>
        /// <param name="costOf">The tile's budget cost; see <see cref="TileBudget.CostOf"/>.</param>
        /// <param name="admitted">Filled with admitted tiles (cleared first).</param>
        /// <param name="denied">Filled with denied tiles (cleared first).</param>
        /// <returns>
        /// The boundary tile — the first one that did not fit, admitted beyond the limits; <c>null</c> if everything
        /// fit. Admitted tiles without it always fit <paramref name="limits"/>.
        /// </returns>
        /// <remarks>
        /// The caller chooses the output collection types: both are filled in importance order, so an ordered
        /// collection (<see cref="List{T}"/>) preserves it, while <see cref="HashSet{T}"/> gives fast membership checks.
        /// </remarks>
        public static TileCoord? Admit(
            IReadOnlyList<TileCoord> byImportance,
            Func<TileCoord, TileMemoryUsage> costOf,
            TileMemoryUsage limits,
            ICollection<TileCoord> admitted,
            ICollection<TileCoord> denied)
        {
            admitted.Clear();
            denied.Clear();

            var total = TileMemoryUsage.Zero;
            TileCoord? boundary = null;
            for (int i = 0; i < byImportance.Count; i++)
            {
                var coord = byImportance[i];
                if (boundary.HasValue)
                {
                    denied.Add(coord);
                    continue;
                }

                total += costOf(coord);
                admitted.Add(coord);
                // The tile that does not fit is admitted too (one-tile overshoot); only denials follow.
                if (!Fits(total, limits)) boundary = coord;
            }

            return boundary;
        }

        /// <summary>
        /// Selects evictions: while resident tiles — loaded <paramref name="loaded"/> and loading
        /// (<paramref name="inFlightUsage"/>) — do not fit <paramref name="limits"/>, loaded tiles outside
        /// <paramref name="admitted"/> are evicted: first tiles outside the desired set (warm cache), least recently
        /// used first, then tiles denied by the budget, least important first. Admitted tiles are never evicted;
        /// in-flight tiles are counted but not evicted (they can only be cancelled). If nothing is left to evict,
        /// resident tiles stay above the limits — the budget is soft.
        ///
        /// Eviction uses actual memory while admission uses estimates (see <see cref="TileBudget.CostOf"/>), so a
        /// denial is possible while actual memory still fits: room is reserved for admitted tiles that have not loaded
        /// yet. A denied loaded tile stays in memory while there is room and is evicted after the warm cache: the
        /// selector wants it, and if the budget admits it again, it shows without a load.
        /// </summary>
        /// <param name="loaded">
        /// Loaded tiles. A caller-owned buffer that is sorted in place when the limits are exceeded.
        /// </param>
        /// <param name="evictions">Filled with tiles to evict, in order (cleared first).</param>
        public static void SelectEvictions(
            List<LoadedTileUsage> loaded,
            TileMemoryUsage inFlightUsage,
            ISet<TileCoord> admitted,
            TileMemoryUsage limits,
            List<TileCoord> evictions)
        {
            evictions.Clear();

            var resident = inFlightUsage;
            foreach (var tile in loaded) resident += tile.Cost;
            if (Fits(resident, limits)) return;

            loaded.Sort(CompareEvictionOrder);
            foreach (var tile in loaded)
            {
                if (admitted.Contains(tile.Coord)) continue;

                evictions.Add(tile.Coord);
                resident -= tile.Cost;
                if (Fits(resident, limits)) return;
            }
        }
    }
}
