using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;

namespace GeoFoundry.Tests.EditMode.Coordinates
{
    public sealed class MercatorPivotTests
    {
        // Moscow; used as the coordinate system center in all pivot tests.
        private const double MoscowLon = 37.6175;
        private const double MoscowLat = 55.7520;

        [Test]
        public void WorldFromMercator_AtPivot_IsZero()
        {
            var pivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var world = pivot.WorldFromMercator(pivot.PivotMeters);

            Assert.That(world.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(world.z, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(world.y, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void WorldFromMercator_PreservesHeight()
        {
            var pivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var world = pivot.WorldFromMercator(pivot.PivotMeters, height: 42.5f);
            Assert.That(world.y, Is.EqualTo(42.5f).Within(1e-4f));
        }

        [Test]
        public void RoundTrip_World_To_Mercator_To_World()
        {
            var pivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var original = new float3(123.4f, 17f, -456.7f);

            var mercator = pivot.MercatorFromWorld(original);
            var back = pivot.WorldFromMercator(mercator, height: original.y);

            Assert.That(back.x, Is.EqualTo(original.x).Within(1e-3f));
            Assert.That(back.y, Is.EqualTo(original.y).Within(1e-3f));
            Assert.That(back.z, Is.EqualTo(original.z).Within(1e-3f));
        }

        [Test]
        public void ShouldRebase_TargetFarFromPivot_ReturnsTrue()
        {
            var pivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var farTarget = pivot.PivotMeters + new double2(2000.0, 0.0);
            Assert.That(pivot.ShouldRebase(farTarget, thresholdMeters: 1000.0), Is.True);
        }

        [Test]
        public void ShouldRebase_TargetClose_ReturnsFalse()
        {
            var pivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var closeTarget = pivot.PivotMeters + new double2(500.0, 500.0);
            // sqrt(500² + 500²) ≈ 707 < 1000
            Assert.That(pivot.ShouldRebase(closeTarget, thresholdMeters: 1000.0), Is.False);
        }

        [Test]
        public void Rebase_ShiftsCoordinatesByDelta()
        {
            var pivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var pointMeters = pivot.PivotMeters + new double2(100.0, 200.0);

            var worldBefore = pivot.WorldFromMercator(pointMeters);
            Assert.That(worldBefore.x, Is.EqualTo(100f).Within(1e-3f));
            Assert.That(worldBefore.z, Is.EqualTo(200f).Within(1e-3f));

            pivot.Rebase(pointMeters);

            var worldAfter = pivot.WorldFromMercator(pointMeters);
            Assert.That(worldAfter.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(worldAfter.z, Is.EqualTo(0f).Within(1e-3f));
        }
    }
}
