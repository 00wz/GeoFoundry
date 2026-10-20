using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Tiles.Diagnostics;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>Tests <see cref="TileDebugSummary"/>: aggregates over a snapshot of tile states.</summary>
    public sealed class TileDebugSummaryTests
    {
        private static int _next;

        private static TileDebugInfo Tile(
            TileResidency residency, TileBudgetVerdict verdict,
            bool rendered = false, bool loadRequested = false, long gpu = 0, long heap = 0) =>
            new TileDebugInfo(new TileCoord(10, _next++, 0), residency, verdict, rendered, loadRequested,
                new TileMemoryUsage(gpu, heap), new TileMemoryUsage(gpu, heap));

        [Test]
        public void From_Empty_IsZero()
        {
            var summary = TileDebugSummary.From(new TileDebugInfo[0]);

            Assert.That(summary.Loaded + summary.Loading + summary.Cancelling + summary.Queued, Is.Zero);
            Assert.That(summary.BudgetExceeded, Is.False);
            Assert.That(summary.ResidentMemory, Is.EqualTo(TileMemoryUsage.Zero));
        }

        [Test]
        public void From_CountsAndMemoryByState()
        {
            var tiles = new[]
            {
                Tile(TileResidency.Loaded, TileBudgetVerdict.Admitted, rendered: true, gpu: 10, heap: 1),
                Tile(TileResidency.Loaded, TileBudgetVerdict.Boundary, rendered: true, gpu: 20, heap: 2),
                Tile(TileResidency.Loaded, TileBudgetVerdict.Admitted, gpu: 5),                  // admitted, off screen
                Tile(TileResidency.Loaded, TileBudgetVerdict.NotWanted, gpu: 7, heap: 3),        // warm cache
                Tile(TileResidency.Loaded, TileBudgetVerdict.Denied, gpu: 4),                    // denied, not yet evicted
                Tile(TileResidency.Loading, TileBudgetVerdict.Admitted, rendered: true, loadRequested: true, gpu: 2),
                Tile(TileResidency.Cancelling, TileBudgetVerdict.NotWanted, gpu: 3),
                Tile(TileResidency.None, TileBudgetVerdict.Admitted, loadRequested: true),        // waiting for a slot
                Tile(TileResidency.None, TileBudgetVerdict.Admitted),                            // empty: load failed
                Tile(TileResidency.None, TileBudgetVerdict.Denied, loadRequested: true)          // denied: not waiting
            };

            var summary = TileDebugSummary.From(tiles);

            Assert.That(summary.Rendered, Is.EqualTo(3));
            Assert.That(summary.Loaded, Is.EqualTo(5));
            Assert.That(summary.Warm, Is.EqualTo(1), "warm cache counts only loaded tiles outside the wanted set");
            Assert.That(summary.Loading, Is.EqualTo(1));
            Assert.That(summary.Cancelling, Is.EqualTo(1));
            Assert.That(summary.Queued, Is.EqualTo(1), "queued: admitted, requests a load and is not building");
            Assert.That(summary.Empty, Is.EqualTo(1), "empty: admitted, not resident and not requesting a load");
            Assert.That(summary.Admitted, Is.EqualTo(6), "including the boundary tile");
            Assert.That(summary.Denied, Is.EqualTo(2));
            Assert.That(summary.BudgetExceeded, Is.True);

            Assert.That(summary.LoadedMemory, Is.EqualTo(new TileMemoryUsage(46, 6)));
            Assert.That(summary.InFlightMemory, Is.EqualTo(new TileMemoryUsage(5, 0)), "building and cancelling tiles");
            Assert.That(summary.WarmMemory, Is.EqualTo(new TileMemoryUsage(7, 3)));
            Assert.That(summary.ResidentMemory, Is.EqualTo(new TileMemoryUsage(51, 6)));
        }

        [Test]
        public void IsEmpty_OnlyAdmittedWithoutContentAndWithoutLoadRequest()
        {
            Assert.That(Tile(TileResidency.None, TileBudgetVerdict.Admitted).IsEmpty, Is.True);
            Assert.That(Tile(TileResidency.None, TileBudgetVerdict.Boundary).IsEmpty, Is.True);
            Assert.That(Tile(TileResidency.None, TileBudgetVerdict.Admitted, loadRequested: true).IsEmpty, Is.False,
                "requesting a load means queued, not empty");
            Assert.That(Tile(TileResidency.Loaded, TileBudgetVerdict.Admitted).IsEmpty, Is.False);
            Assert.That(Tile(TileResidency.Cancelling, TileBudgetVerdict.Admitted).IsEmpty, Is.False);
            Assert.That(Tile(TileResidency.None, TileBudgetVerdict.Denied).IsEmpty, Is.False, "denied is a separate state");
            Assert.That(Tile(TileResidency.None, TileBudgetVerdict.NotWanted).IsEmpty, Is.False);
        }

        [Test]
        public void From_NoBoundary_BudgetNotExceeded()
        {
            var summary = TileDebugSummary.From(new[]
            {
                Tile(TileResidency.Loaded, TileBudgetVerdict.Admitted, rendered: true, gpu: 1),
                Tile(TileResidency.Loaded, TileBudgetVerdict.NotWanted, gpu: 1)
            });

            Assert.That(summary.BudgetExceeded, Is.False);
            Assert.That(summary.Denied, Is.Zero);
        }
    }
}
