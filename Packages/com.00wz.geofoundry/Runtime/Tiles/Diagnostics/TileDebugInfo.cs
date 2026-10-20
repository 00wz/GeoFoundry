using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Tiles.Diagnostics
{
    /// <summary>
    /// Where a tile is in the <see cref="TileLifecycleManager"/> lifecycle (see <see cref="Budget.TileBudget.ResidencyOf"/>).
    /// </summary>
    public enum TileResidency : byte
    {
        /// <summary>Not in memory: not loaded yet, evicted, or its load failed.</summary>
        None,

        /// <summary>Being built.</summary>
        Loading,

        /// <summary>
        /// The build is cancelled, but the cancellation has not completed yet: the tile holds memory and a
        /// concurrent load slot.
        /// </summary>
        Cancelling,

        /// <summary>Loaded (rendered or hidden).</summary>
        Loaded
    }

    /// <summary>The budget's (<see cref="Budget.TileBudget"/>) verdict on a tile in the last <see cref="TileLifecycleManager"/> frame.</summary>
    public enum TileBudgetVerdict : byte
    {
        /// <summary>Not in the selector's desired set: warm cache or a cancelling build.</summary>
        NotWanted,

        /// <summary>Admitted: protected from eviction, loaded and rendered if the selector requests it.</summary>
        Admitted,

        /// <summary>
        /// Boundary tile: admitted beyond the limits as the first tile that did not fit the budget
        /// (see <see cref="Budget.TileBudgetPolicy.Admit"/>). Its presence means the budget has reached its limits.
        /// </summary>
        Boundary,

        /// <summary>Denied by the budget: not rendered, not loaded, and evicted.</summary>
        Denied
    }

    /// <summary>
    /// Debug state of a single tile; see <see cref="TileLifecycleManager.GetDebugSnapshot"/>.
    /// <see cref="TileDebugSummary"/> computes aggregates over a snapshot.
    /// </summary>
    public readonly struct TileDebugInfo
    {
        /// <summary>The tile.</summary>
        public readonly TileCoord Coord;

        /// <summary>Where the tile is in the lifecycle.</summary>
        public readonly TileResidency Residency;

        /// <summary>The budget's verdict in the last frame.</summary>
        public readonly TileBudgetVerdict Verdict;

        /// <summary>
        /// Whether the tile is rendered: it is in memory (loaded, or building and not cancelling), the selector asks
        /// to show it, and the budget admitted it. A tile not in memory is not rendered even if the selector asks to
        /// show it; neither is a cancelling one, since it is incomplete and will be released.
        /// </summary>
        public readonly bool IsRendered;

        /// <summary>Whether the selector requests loading the tile (<c>TilesToLoad</c>).</summary>
        public readonly bool IsLoadRequested;

        /// <summary>
        /// Tile memory: actual for a loaded tile, accumulated so far for a building one, zero for a tile not in
        /// memory.
        /// </summary>
        public readonly TileMemoryUsage Memory;

        /// <summary>
        /// The tile's budget cost at snapshot time — the memory it will occupy if admitted
        /// (see <see cref="Budget.TileBudget.CostOf"/>): for a tile not in memory, an estimate if the selector
        /// requests its load and zero otherwise; for a building tile, no less than the estimate at start.
        /// </summary>
        public readonly TileMemoryUsage BudgetCost;

        public TileDebugInfo(
            TileCoord coord, TileResidency residency, TileBudgetVerdict verdict,
            bool isRendered, bool isLoadRequested, TileMemoryUsage memory, TileMemoryUsage budgetCost)
        {
            Coord = coord;
            Residency = residency;
            Verdict = verdict;
            IsRendered = isRendered;
            IsLoadRequested = isLoadRequested;
            Memory = memory;
            BudgetCost = budgetCost;
        }

        /// <summary>Whether the budget admitted the tile, including as the boundary tile.</summary>
        public bool IsAdmitted => Verdict == TileBudgetVerdict.Admitted || Verdict == TileBudgetVerdict.Boundary;

        /// <summary>
        /// Whether the tile waits for a concurrent load slot: admitted and requested by the selector, but not
        /// building yet.
        /// </summary>
        public bool IsQueued => IsAdmitted && IsLoadRequested && Residency == TileResidency.None;

        /// <summary>
        /// Whether the tile is empty: admitted, but not in memory and not requested by the selector, so the selector
        /// treats it as ready although it has no content and leaves a hole. With <c>TileQuadtreeTraversal</c> this is
        /// always a tile whose load failed (it is not retried while needed for coverage); the cause is reported by
        /// <see cref="TileLifecycleManager.TileLoadFailed"/>.
        /// </summary>
        public bool IsEmpty => IsAdmitted && !IsLoadRequested && Residency == TileResidency.None;
    }
}
