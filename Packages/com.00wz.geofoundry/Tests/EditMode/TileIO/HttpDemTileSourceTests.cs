using NUnit.Framework;
using GeoFoundry.TileIO.Dem;
using GeoFoundry.TileIO.Net;
using GeoFoundry.TileIO.Sources;

namespace GeoFoundry.Tests.EditMode.TileIO
{
    public sealed class HttpDemTileSourceTests
    {
        [Test]
        public void Constructor_DefaultTileSize_Is256()
        {
            using var dl = new TileDownloader();
            var src = new HttpDemTileSource("http://x/{z}/{x}/{y}.png", dl);
            Assert.That(src.TileSizePixels, Is.EqualTo(TerrariumDecoder.StandardTileSize));
            Assert.That(src.TileSizePixels, Is.EqualTo(256));
        }

        [Test]
        public void Constructor_CustomTileSize_512_Respected()
        {
            using var dl = new TileDownloader();
            var src = new HttpDemTileSource("http://x/{z}/{x}/{y}.png", dl, tileSizePixels: 512);
            Assert.That(src.TileSizePixels, Is.EqualTo(512));
        }

        [Test]
        public void Constructor_InvalidTileSize_Throws()
        {
            using var dl = new TileDownloader();
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new HttpDemTileSource("http://x/{z}/{x}/{y}.png", dl, tileSizePixels: 0));
        }

        [Test]
        public void Constructor_ConvenienceOverload_AlsoAcceptsTileSize()
        {
            using var src = new HttpDemTileSource("http://x/{z}/{x}/{y}.png",
                maxConcurrent: 2, tileSizePixels: 1024);
            Assert.That(src.TileSizePixels, Is.EqualTo(1024));
        }
    }
}
