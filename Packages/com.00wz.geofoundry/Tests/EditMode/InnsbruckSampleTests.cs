using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.TileIO.Sources;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode
{
    /// <summary>Validates the shipped sample template, never the user's imported or customized copies.</summary>
    public sealed class InnsbruckSampleTests
    {
        const string Package = "Packages/com.00wz.geofoundry";
        static string Sample => Directory.Exists(Package + "/Samples~/Innsbruck")
            ? Package + "/Samples~/Innsbruck" : Package + "/Samples/Innsbruck";
        static string GuidOf(string path) => File.ReadLines(path + ".meta").Single(l => l.StartsWith("guid: ")).Substring(6).Trim();
        [Serializable] sealed class Manifest { public Entry[] samples; }
        [Serializable] sealed class Entry { public string displayName; public string path; }

        [Test]
        public void Sample_IsDeclaredForPackageManagerImport()
        {
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(Package + "/package.json"));
            Assert.That(manifest.samples.Any(s => s.displayName == "Innsbruck" && s.path == "Samples~/Innsbruck"), Is.True);
        }

        [Test]
        public void Sample_HasSceneConfigAndArchives()
        {
            foreach (string relative in new[] { "Scenes/Innsbruck.unity", "Configs/InnsbruckConfig.asset",
                "Configs/InnsbruckCamera.asset", "Settings/InnsbruckURP.asset", "README.md",
                "Data/Maps/vector.pmtiles", "Data/Maps/dem.pmtiles" })
                Assert.That(File.Exists(Sample + "/" + relative), Is.True, relative);
            string config = File.ReadAllText(Sample + "/Configs/InnsbruckConfig.asset");
            Assert.That(config, Does.Contain("GeoFoundry/Innsbruck/vector.pmtiles"));
            Assert.That(config, Does.Contain("GeoFoundry/Innsbruck/dem.pmtiles"));
            Assert.That(config, Does.Contain("TileSizePixels: 128"));
            Assert.That(config, Does.Not.Contain("192.168."), "No development endpoint should ship in a sample");
        }

        [Test]
        public void Sample_PresetsHaveOneTemplateAndIsolatedTestImports()
        {
            Assert.That(Directory.GetFiles(Package + "/Presets/OpenMapTiles", "*", SearchOption.AllDirectories)
                .Any(p => p.EndsWith(".asset") || p.EndsWith(".mat")), Is.False,
                "Mutable presets belong only in the sample template");
            var baseline = Directory.GetFiles(SamplePresetAssets.Template, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".asset") || p.EndsWith(".mat")).ToArray();
            Assert.That(baseline.Length, Is.GreaterThan(20));
            Assert.That(baseline.Select(GuidOf).Distinct().Count(), Is.EqualTo(baseline.Length));
            string testRoot;
            using (var presets = new SamplePresetAssets())
            {
                testRoot = presets.Root;
                foreach (string file in baseline)
                {
                    string copy = testRoot + file.Substring(SamplePresetAssets.Template.Length);
                    Assert.That(GuidOf(copy), Is.Not.EqualTo(GuidOf(file)), "Tests must not load the user's imported copy");
                    foreach (string guid in baseline.Select(GuidOf))
                        Assert.That(File.ReadAllText(copy), Does.Not.Contain("guid: " + guid));
                }
            }
            Assert.That(Directory.Exists(testRoot), Is.False, "Test imports must be removed");
        }

        [Test]
        public async Task Sample_ArchivesSupplyRealInnsbruckMvtAnd128PixelTerrarium()
        {
            var coord = TileCoord.FromLonLat(11.4041, 47.2692, 14);
            using var vectors = new PmTilesVectorTileSource(Path.GetFullPath(Sample + "/Data/Maps/vector.pmtiles"));
            using var dem = new PmTilesDemTileSource(Path.GetFullPath(Sample + "/Data/Maps/dem.pmtiles"), 128);
            using var bytes = await vectors.GetTileAsync(coord, Allocator.Persistent, default);
            Assert.That(bytes.Length, Is.GreaterThan(0), "The demo's start location must have vector data");
            using var heights = await dem.GetHeightsAsync(coord, Allocator.Persistent, default);
            Assert.That(heights.Length, Is.EqualTo(128 * 128));
            Assert.That(heights[64 * 128 + 64], Is.InRange(400f, 4000f), "Plausible elevation near Innsbruck");
        }
    }
}
