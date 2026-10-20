using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Tiles.Budget;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>Tests <see cref="TileCostEstimator"/>: cost estimation for tiles that are not resident.</summary>
    public sealed class TileCostEstimatorTests
    {
        private static TileCoord T(int i) => new TileCoord(10, i, 0);

        private static TileMemoryUsage Gpu(long gpu, long heap = 0) => new TileMemoryUsage(gpu, heap);

        [Test]
        public void Empty_EstimatesZero()
        {
            var estimator = new TileCostEstimator();

            Assert.That(estimator.Estimate(T(0)), Is.EqualTo(TileMemoryUsage.Zero), "optimistic at startup");
            Assert.That(estimator.Mean, Is.EqualTo(TileMemoryUsage.Zero));
        }

        [Test]
        public void RememberedTile_EstimatedByItsOwnCost_UnknownByMean()
        {
            var estimator = new TileCostEstimator();
            estimator.Remember(T(0), Gpu(10, 4));
            estimator.Remember(T(1), Gpu(20, 2));

            Assert.That(estimator.Estimate(T(0)), Is.EqualTo(Gpu(10, 4)), "tile's previous cost");
            Assert.That(estimator.Estimate(T(5)), Is.EqualTo(Gpu(15, 3)), "unknown tile gets the per-pool average");
        }

        [Test]
        public void RememberAgain_ReplacesCostAndMean()
        {
            var estimator = new TileCostEstimator();
            estimator.Remember(T(0), Gpu(10));
            estimator.Remember(T(1), Gpu(20));
            estimator.Remember(T(0), Gpu(30));

            Assert.That(estimator.Count, Is.EqualTo(2));
            Assert.That(estimator.Estimate(T(0)), Is.EqualTo(Gpu(30)));
            Assert.That(estimator.Mean, Is.EqualTo(Gpu(25)));
        }

        [Test]
        public void OverCapacity_ForgetsOldest()
        {
            var estimator = new TileCostEstimator(capacity: 2);
            estimator.Remember(T(0), Gpu(10));
            estimator.Remember(T(1), Gpu(20));
            estimator.Remember(T(0), Gpu(12)); // repeating does not refresh: T(0) is still the oldest
            estimator.Remember(T(2), Gpu(40));

            Assert.That(estimator.Count, Is.EqualTo(2));
            Assert.That(estimator.Mean, Is.EqualTo(Gpu(30)), "average over the remaining tiles");
            Assert.That(estimator.Estimate(T(0)), Is.EqualTo(Gpu(30)), "forgotten tile is estimated by the average");
            Assert.That(estimator.Estimate(T(1)), Is.EqualTo(Gpu(20)));
        }
    }
}
