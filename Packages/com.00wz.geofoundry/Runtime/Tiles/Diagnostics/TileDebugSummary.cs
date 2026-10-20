using System.Collections.Generic;
using GeoFoundry.Core;

namespace GeoFoundry.Tiles.Diagnostics
{
    /// <summary>
    /// Aggregates over a <see cref="TileLifecycleManager.GetDebugSnapshot"/> snapshot: tile counts and memory by
    /// state. Computed by the consumer (overlay, HUD) rather than the manager, so the manager accumulates nothing
    /// for debugging.
    /// </summary>
    public struct TileDebugSummary
    {
        /// <summary>Gets the number of rendered tiles (loaded and building).</summary>
        public int Rendered { get; private set; }

        /// <summary>Gets the number of loaded tiles.</summary>
        public int Loaded { get; private set; }

        /// <summary>Gets the warm cache size: loaded tiles the selector does not want. Empty while the budget is at its limits.</summary>
        public int Warm { get; private set; }

        /// <summary>Gets the number of building tiles.</summary>
        public int Loading { get; private set; }

        /// <summary>Gets the number of cancelling tiles; they hold memory and a concurrent load slot until the cancellation completes.</summary>
        public int Cancelling { get; private set; }

        /// <summary>Gets the number of tiles waiting for a concurrent load slot (see <see cref="TileDebugInfo.IsQueued"/>).</summary>
        public int Queued { get; private set; }

        /// <summary>
        /// Gets the number of empty tiles: admitted but without content (see <see cref="TileDebugInfo.IsEmpty"/>),
        /// usually failed loads.
        /// </summary>
        public int Empty { get; private set; }

        /// <summary>Gets the number of tiles admitted by the budget, including the boundary tile.</summary>
        public int Admitted { get; private set; }

        /// <summary>Gets the number of tiles denied by the budget; they leave holes.</summary>
        public int Denied { get; private set; }

        /// <summary>Gets whether there is a boundary tile, i.e. the selector's desired set did not fit the budget.</summary>
        public bool BudgetExceeded { get; private set; }

        /// <summary>Gets the memory of loaded tiles.</summary>
        public TileMemoryUsage LoadedMemory { get; private set; }

        /// <summary>Gets the memory accumulated so far by building and cancelling tiles.</summary>
        public TileMemoryUsage InFlightMemory { get; private set; }

        /// <summary>Gets the warm cache memory, a part of <see cref="LoadedMemory"/>.</summary>
        public TileMemoryUsage WarmMemory { get; private set; }

        /// <summary>Gets the resident memory, which the budget compares with the limits.</summary>
        public TileMemoryUsage ResidentMemory => LoadedMemory + InFlightMemory;

        /// <summary>Computes the summary of a debug snapshot.</summary>

        public static TileDebugSummary From(IReadOnlyList<TileDebugInfo> tiles)
        {
            var summary = new TileDebugSummary();
            for (int i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];

                if (tile.IsRendered) summary.Rendered++;
                if (tile.IsQueued) summary.Queued++;
                if (tile.IsEmpty) summary.Empty++;
                if (tile.IsAdmitted) summary.Admitted++;
                if (tile.Verdict == TileBudgetVerdict.Denied) summary.Denied++;
                if (tile.Verdict == TileBudgetVerdict.Boundary) summary.BudgetExceeded = true;

                switch (tile.Residency)
                {
                    case TileResidency.Loaded:
                        summary.Loaded++;
                        summary.LoadedMemory += tile.Memory;
                        if (tile.Verdict == TileBudgetVerdict.NotWanted)
                        {
                            summary.Warm++;
                            summary.WarmMemory += tile.Memory;
                        }
                        break;

                    case TileResidency.Loading:
                        summary.Loading++;
                        summary.InFlightMemory += tile.Memory;
                        break;

                    case TileResidency.Cancelling:
                        summary.Cancelling++;
                        summary.InFlightMemory += tile.Memory;
                        break;
                }
            }

            return summary;
        }
    }
}
