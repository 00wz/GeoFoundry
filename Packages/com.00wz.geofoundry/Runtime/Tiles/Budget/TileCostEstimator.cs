using System;
using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Tiles.Budget
{
    /// <summary>
    /// Estimates the memory a tile will occupy once loaded, for admitting tiles not yet in memory
    /// (see <see cref="TileBudget.CostOf"/>). Remembers the actual cost of loaded tiles: a tile that has loaded
    /// before is estimated by its past cost, an unknown one by the mean of remembered costs. Until anything is
    /// remembered (at startup), the estimate is zero and tiles are admitted optimistically.
    ///
    /// Remembers at most <see cref="Capacity"/> tiles; beyond that the earliest remembered are forgotten (the mean
    /// covers the remaining ones). Has no Unity dependencies.
    /// </summary>
    internal sealed class TileCostEstimator
    {
        public const int DefaultCapacity = 4096;

        private readonly Dictionary<TileCoord, TileMemoryUsage> _costs = new();

        /// <summary>Order in which tiles were remembered, for forgetting the oldest; each tile appears once.</summary>
        private readonly Queue<TileCoord> _order = new();

        private TileMemoryUsage _total;

        public TileCostEstimator(int capacity = DefaultCapacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
            Capacity = capacity;
        }

        /// <summary>Gets the maximum number of remembered tiles.</summary>
        public int Capacity { get; }

        /// <summary>Gets the number of currently remembered tiles.</summary>
        public int Count => _costs.Count;

        /// <summary>Gets the mean remembered cost; <see cref="TileMemoryUsage.Zero"/> if nothing is remembered.</summary>
        public TileMemoryUsage Mean => _costs.Count == 0
            ? TileMemoryUsage.Zero
            : new TileMemoryUsage(_total.GpuBytes / _costs.Count, _total.HeapBytes / _costs.Count);

        /// <summary>Remembers the actual cost of a fully loaded tile; a repeated call replaces the previous cost.</summary>
        public void Remember(TileCoord coord, TileMemoryUsage cost)
        {
            if (_costs.TryGetValue(coord, out var previous))
            {
                // The tile is already queued: it keeps its place, and no duplicate is added.
                _total += cost - previous;
                _costs[coord] = cost;
                return;
            }

            _costs.Add(coord, cost);
            _order.Enqueue(coord);
            _total += cost;

            while (_costs.Count > Capacity)
            {
                var oldest = _order.Dequeue();
                if (_costs.Remove(oldest, out var forgotten)) _total -= forgotten;
            }
        }

        /// <summary>Returns the tile's past cost, or <see cref="Mean"/> if it is unknown.</summary>
        public TileMemoryUsage Estimate(TileCoord coord) =>
            _costs.TryGetValue(coord, out var cost) ? cost : Mean;
    }
}
