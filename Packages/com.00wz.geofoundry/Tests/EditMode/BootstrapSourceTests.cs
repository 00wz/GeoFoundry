using System;
using System.IO;
using System.Threading.Tasks;
using GeoFoundry.Bootstrap;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.TileIO.PmTiles;
using GeoFoundry.TileIO.Sources;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode
{
    public sealed class BootstrapSourceTests
    {
        GeoFoundryConfig _config;
        string _archive;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<GeoFoundryConfig>();
            _archive = Path.Combine(Application.temporaryCachePath, "geofoundry-source-" + Guid.NewGuid() + ".pmtiles");
            // Source construction is lazy: an archive is parsed only when a consumer requests a tile.
            File.WriteAllBytes(_archive, new byte[127]);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_config);
            File.Delete(_archive);
        }

        [Test]
        public void Defaults_SelectLocalArchives()
        {
            Assert.That(_config.SourceMode, Is.EqualTo(TileSourceMode.LocalPmTiles));
            Assert.That(_config.VectorArchivePath, Is.EqualTo("Maps/vector.pmtiles"));
            Assert.That(_config.DemArchivePath, Is.EqualTo("Maps/dem.pmtiles"));
            Assert.That(ConfiguredTileSources.ResolveArchivePath("Maps/vector.pmtiles"),
                Is.EqualTo(PmTilesByteSources.StreamingAsset("Maps/vector.pmtiles")));
        }

        [Test]
        public void LocalMode_CreatesBothPmTilesSourcesAndIgnoresRemoteFields()
        {
            _config.VectorArchivePath = _config.DemArchivePath = _archive;
            _config.VectorTilesEndpointTemplate = _config.DemTilesEndpointTemplate = "invalid unused URL";
            _config.MaxConcurrentHttpRequests = 0; // Irrelevant in local mode.
            using var sources = ConfiguredTileSources.Create(_config);
            Assert.That(sources.Vector, Is.InstanceOf<PmTilesVectorTileSource>());
            Assert.That(sources.Dem, Is.InstanceOf<PmTilesDemTileSource>());
        }

        void ConfigureRemote()
        {
            _config.SourceMode = TileSourceMode.RemoteHttp;
            _config.VectorTilesEndpointTemplate = "https://example.invalid/vector/{z}/{x}/{y}";
            _config.DemTilesEndpointTemplate = "https://example.invalid/dem/{z}/{x}/{y}.png";
            _config.VectorArchivePath = _config.DemArchivePath = "missing-unused.pmtiles";
        }

        [Test]
        public void RemoteMode_CreatesBothHttpSourcesAndIgnoresArchivePaths()
        {
            ConfigureRemote();
            using var sources = ConfiguredTileSources.Create(_config);
            Assert.That(sources.Vector, Is.InstanceOf<HttpVectorTileSource>());
            Assert.That(sources.Dem, Is.InstanceOf<HttpDemTileSource>());
        }

        [TestCase(TileSourceMode.LocalPmTiles, true)]
        [TestCase(TileSourceMode.LocalPmTiles, false)]
        [TestCase(TileSourceMode.RemoteHttp, true)]
        [TestCase(TileSourceMode.RemoteHttp, false)]
        public void FlatGround_DoesNotCreateDem(TileSourceMode mode, bool emptyPath)
        {
            if (mode == TileSourceMode.RemoteHttp) ConfigureRemote();
            else _config.VectorArchivePath = _config.DemArchivePath = _archive;
            _config.EnableRelief = emptyPath;
            if (emptyPath) _config.DemArchivePath = _config.DemTilesEndpointTemplate = "  ";
            else _config.DemArchivePath = "missing-unused.pmtiles";
            using var sources = ConfiguredTileSources.Create(_config);
            Assert.That(sources.Vector, Is.Not.Null);
            Assert.That(sources.Dem, Is.Null);
        }

        [TestCase(TileSourceMode.LocalPmTiles)]
        [TestCase(TileSourceMode.RemoteHttp)]
        public void MissingVector_ReportsTheSelectedMode(TileSourceMode mode)
        {
            _config.SourceMode = mode;
            _config.VectorArchivePath = _config.VectorTilesEndpointTemplate = "  ";
            var error = Assert.Throws<ArgumentException>(() => ConfiguredTileSources.Create(_config));
            Assert.That(error.Message, Does.Contain(mode == TileSourceMode.LocalPmTiles ? "VectorArchivePath" : "VectorTilesEndpointTemplate"));
        }

        [TestCase(TileSourceMode.LocalPmTiles)]
        [TestCase(TileSourceMode.RemoteHttp)]
        public async Task Dispose_ReleasesBothOwnedSources(TileSourceMode mode)
        {
            if (mode == TileSourceMode.RemoteHttp) ConfigureRemote();
            else _config.VectorArchivePath = _config.DemArchivePath = _archive;
            var sources = ConfiguredTileSources.Create(_config);
            sources.Dispose(); sources.Dispose();
            try { var bytes = await sources.Vector.GetTileAsync(new TileCoord(0, 0, 0), Allocator.Persistent, default); if (bytes.IsCreated) bytes.Dispose(); Assert.Fail(); }
            catch (ObjectDisposedException) { }
            try { var heights = await sources.Dem.GetHeightsAsync(new TileCoord(0, 0, 0), Allocator.Persistent, default); if (heights.IsCreated) heights.Dispose(); Assert.Fail(); }
            catch (ObjectDisposedException) { }
        }

        [Test]
        public void ArchivePaths_PreserveAbsoluteLocations()
        {
            Assert.That(ConfiguredTileSources.ResolveArchivePath(_archive), Is.EqualTo(_archive));
            const string url = "https://example.invalid/maps/region.pmtiles";
            Assert.That(ConfiguredTileSources.ResolveArchivePath(url), Is.EqualTo(url));
            const string jar = "jar:file:///data/app.apk!/assets/maps/region.pmtiles";
            Assert.That(ConfiguredTileSources.ResolveArchivePath(jar), Is.EqualTo(jar));
        }

        [Test]
        public void InvalidLocalDem_RejectsIncompletePair()
        {
            _config.VectorArchivePath = _archive;
            _config.DemArchivePath = _archive + ".missing";
            Assert.Throws<PmTilesException>(() => ConfiguredTileSources.Create(_config));
            // The same config can be corrected and built again after a failed attempt.
            _config.DemArchivePath = _archive;
            using var sources = ConfiguredTileSources.Create(_config);
            Assert.That(sources.Dem, Is.InstanceOf<PmTilesDemTileSource>());
        }
    }
}
