using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;

namespace GeoFoundry.Tests.EditMode.Coordinates
{
    /// <summary>
    /// Round-trip LonLat ↔ Mercator ↔ Tile and tile geometry checks around Moscow.
    /// </summary>
    public sealed class MercatorProjectionTests
    {
        // Central Moscow (Red Square); the reference point for all tests.
        private const double MoscowLon = 37.6175;
        private const double MoscowLat = 55.7520;

        // Round-trip tolerance. Lon/lat are decimal degrees; 1e-9° ≈ 0.1 mm at the equator.
        private const double LonLatTolerance = 1e-9;
        private const double MetersTolerance = 1e-3;

        [Test]
        public void LonLat_RoundTrip_MoscowCenter()
        {
            var meters = MercatorProjection.LonLatToMeters(MoscowLon, MoscowLat);
            var back = MercatorProjection.MetersToLonLat(meters);

            Assert.That(back.x, Is.EqualTo(MoscowLon).Within(LonLatTolerance), "lon round-trip");
            Assert.That(back.y, Is.EqualTo(MoscowLat).Within(LonLatTolerance), "lat round-trip");
        }

        [Test]
        public void LonLat_Origin_GivesZeroMeters()
        {
            var meters = MercatorProjection.LonLatToMeters(0.0, 0.0);
            Assert.That(meters.x, Is.EqualTo(0.0).Within(MetersTolerance));
            Assert.That(meters.y, Is.EqualTo(0.0).Within(MetersTolerance));
        }

        [Test]
        public void LonLat_PolesClamped_DoNotProduceInfinity()
        {
            var northPole = MercatorProjection.LonLatToMeters(0.0, 90.0);
            var southPole = MercatorProjection.LonLatToMeters(0.0, -90.0);

            Assert.That(double.IsFinite(northPole.y), Is.True, "North pole should be clamped, not Inf");
            Assert.That(double.IsFinite(southPole.y), Is.True, "South pole should be clamped, not Inf");
            Assert.That(math.abs(northPole.y), Is.GreaterThan(MercatorProjection.HalfCircumference * 0.99));
        }

        [Test]
        public void EarthCircumference_MatchesWGS84()
        {
            // Exact value: 2π × 6378137 ≈ 40075016.685578.
            Assert.That(MercatorProjection.EarthCircumference, Is.EqualTo(40075016.685578).Within(1e-3));
        }

        [Test]
        public void Tile_ContainsLonLat_ForMoscowCenter()
        {
            var xy = MercatorProjection.LonLatToTile(MoscowLon, MoscowLat, 16);
            // The zoom-16 tile containing central Moscow; verified through its bbox below.
            var nw = MercatorProjection.TileToLonLat(xy.x, xy.y, 16);
            var se = MercatorProjection.TileToLonLat(xy.x + 1, xy.y + 1, 16);

            Assert.That(MoscowLon, Is.InRange(nw.x, se.x), "tile contains the longitude of central Moscow");
            // The SE corner lies south of NW (latitude decreases southward).
            Assert.That(MoscowLat, Is.InRange(se.y, nw.y), "tile contains the latitude of central Moscow");
        }

        [Test]
        public void Tile_BoundsMeters_ContainsTileCenter()
        {
            var xy = MercatorProjection.LonLatToTile(MoscowLon, MoscowLat, 16);
            var bounds = MercatorProjection.TileBoundsMeters(xy.x, xy.y, 16);

            double minX = bounds.x, minY = bounds.y, maxX = bounds.z, maxY = bounds.w;
            Assert.That(minX, Is.LessThan(maxX));
            Assert.That(minY, Is.LessThan(maxY));

            // A zoom-16 tile side is about 612 meters (40075016.685 / 2^16 ≈ 611.5).
            double size = maxX - minX;
            Assert.That(size, Is.EqualTo(611.4962).Within(1e-3));
            Assert.That(maxY - minY, Is.EqualTo(size).Within(MetersTolerance), "tile is square in the projection");
        }

        [Test]
        public void Tile_AdjacentTilesTouch_NoGapsOrOverlaps()
        {
            // Adjacent tiles must meet exactly: maxX of tile (x,y) equals minX of tile (x+1,y).
            var a = MercatorProjection.TileBoundsMeters(39614, 20580, 16);
            var b = MercatorProjection.TileBoundsMeters(39615, 20580, 16);
            var c = MercatorProjection.TileBoundsMeters(39614, 20581, 16);

            Assert.That(b.x, Is.EqualTo(a.z).Within(MetersTolerance), "right neighbor meets along X");
            Assert.That(c.w, Is.EqualTo(a.y).Within(MetersTolerance), "down neighbor meets along Y");
        }

        [Test]
        public void TileSize_Halves_OnEachZoomLevel()
        {
            // Each next zoom level halves the tile size.
            for (int z = 0; z < 20; z++)
            {
                double s0 = MercatorProjection.TileSizeMeters(z);
                double s1 = MercatorProjection.TileSizeMeters(z + 1);
                Assert.That(s0 / s1, Is.EqualTo(2.0).Within(1e-9), $"zoom {z} → {z+1}");
            }
        }

        [Test]
        public void MercatorToTile_RoundTripWithLonLatToTile_ProducesSameResult()
        {
            // On any zoom MercatorToTile(LonLatToMeters(lon,lat)) must match LonLatToTile.
            foreach (int z in new[] { 0, 5, 10, 14, 16 })
            {
                var direct = MercatorProjection.LonLatToTile(MoscowLon, MoscowLat, z);
                var viaMercator = MercatorProjection.MercatorToTile(
                    MercatorProjection.LonLatToMeters(MoscowLon, MoscowLat), z);
                Assert.That(viaMercator.x, Is.EqualTo(direct.x), $"x mismatch on zoom {z}");
                Assert.That(viaMercator.y, Is.EqualTo(direct.y), $"y mismatch on zoom {z}");
            }
        }

        [Test]
        public void MercatorToTile_OnTileCenter_ReturnsSameTile()
        {
            var tile = new TileCoord(14, 9903, 5145);
            var center = tile.CenterMeters();
            var recovered = MercatorProjection.MercatorToTile(center, 14);
            Assert.That(recovered.x, Is.EqualTo(tile.X));
            Assert.That(recovered.y, Is.EqualTo(tile.Y));
        }
    }
}
