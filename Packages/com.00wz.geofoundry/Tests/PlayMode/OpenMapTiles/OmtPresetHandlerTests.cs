#if UNITY_EDITOR
using System.Collections;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Ground;
using GeoFoundry.OpenMapTiles;
using GeoFoundry.Tests.PlayMode.Geometry;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.OpenMapTiles
{
    /// <summary>
    /// End-to-end runs of the shipped preset handlers (<c>Presets/OpenMapTiles</c>) on synthetic tiles: the assets,
    /// their material/font/atlas references, the runtime glyph atlases and the mesh layouts are exercised together.
    /// </summary>
    public sealed class OmtPresetHandlerTests
    {
        private const string Presets = "Packages/com.00wz.geofoundry/Presets/OpenMapTiles/";
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);
        private static readonly MercatorPivot Frame = MercatorPivot.FromLonLat(37.6, 55.75);

        private static readonly int2[] Road = { new int2(100, 2000), new int2(2000, 2100), new int2(4000, 2000) };
        private static readonly int2[] Block = OmtTestTile.Square(500, 500, 1500, 1500);

        [UnityTest]
        public IEnumerator Transportation_BuildsLinesAreasAndOnewayArrows() => RunHandler("OmtTransportation",
            new OmtTestTile("transportation")
                .Line(Road, ("class", "primary"), ("oneway", 1))
                .Line(Road, ("class", "rail"), ("brunnel", "bridge"))
                .Line(Road, ("class", "minor"), ("brunnel", "tunnel"))
                .Polygon(Block, ("class", "path"), ("subclass", "pedestrian")),
            expectMeshes: 3);

        [UnityTest]
        public IEnumerator Landcover_BuildsFillsAndOutlines() => RunHandler("OmtLandcover",
            new OmtTestTile("landcover")
                .Polygon(Block, ("class", "wood"), ("subclass", "forest"))
                .Polygon(Block, ("class", "farmland"), ("subclass", "farmland")),
            expectMeshes: 2);

        [Test]
        public void TextSettings_DeclutterLabelsForTheTileScreenSize()
        {
            var settings = AssetDatabase.LoadAssetAtPath<OmtTextSettings>(Presets + "OmtTextSettings.asset");
            Assert.That(settings, Is.Not.Null);
            Assert.That(settings.TileScreenSizePixels, Is.GreaterThanOrEqualTo(1f));
            var tiers = OmtZoom.LabelTierRange(settings.TileScreenSizePixels);
            Assert.That(tiers.x, Is.LessThanOrEqualTo(tiers.y));
        }

        [UnityTest]
        public IEnumerator Building_Extrudes() => RunHandler("OmtBuilding",
            new OmtTestTile("building").Polygon(Block, ("render_height", 20.0), ("colour", "#aabbcc")),
            expectMeshes: 1);

        [UnityTest]
        public IEnumerator Building_ReadsNamedAndListedColours_AndTheSchemaDefaultHeight() => OmtTestTile.Run(async ct =>
        {
            using var tile = new OmtTestTile("building")
                .Polygon(Block, ("colour", "beige;brown"))
                .Polygon(Block, ("colour", " Firebrick "))
                .Polygon(Block, ("colour", "#aabbcc"))
                .Polygon(Block, ("colour", "browm"))
                .Polygon(Block)
                .Build();
            var props = await AsyncBuildingPropertyReader.ReadAsync(tile.GetLayer(0), BuildingPropertyOptions.Default, ct);
            Assert.That(props, Is.Not.Null);
            try
            {
                var colors = props.Value.Colors;
                Assert.That(colors[0], Is.EqualTo(new Color32(245, 245, 220, 255)), "the first value of a list");
                Assert.That(colors[1], Is.EqualTo(new Color32(178, 34, 34, 255)), "names in any case, trimmed");
                Assert.That(colors[2], Is.EqualTo(new Color32(0xaa, 0xbb, 0xcc, 255)));
                Assert.That(colors[3], Is.EqualTo(new Color32(255, 255, 255, 255)), "unknown values keep the default");
                Assert.That(props.Value.TopMeters[4], Is.EqualTo(5f), "the schema's 5 m without render_height");
            }
            finally
            {
                props.Value.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator PeakWithoutEle_AndAirportWithoutIata_AreNotLabeled() => OmtTestTile.Run(async ct =>
        {
            await RunEmpty("OmtMountainPeak", new OmtTestTile("mountain_peak")
                .Point(2048, 2048, ("class", "peak"), ("name", "Hill"), ("rank", 1)), ct);
            await RunEmpty("OmtAerodromeLabel", new OmtTestTile("aerodrome_label")
                .Point(2048, 2048, ("class", "military"), ("name", "Airfield"), ("icao", "UUMB")), ct);
        });

        [UnityTest]
        public IEnumerator Place_BuildsTextLabelsWithRuntimeFontAtlas() => RunHandler("OmtPlace",
            new OmtTestTile("place")
                .Point(2048, 2048, ("class", "city"), ("name", "Москва"), ("name:en", "Moscow"), ("capital", 2), ("rank", 1))
                .Point(1000, 3000, ("class", "suburb"), ("name", "Arbat"), ("rank", 5)),
            expectMeshes: 1, expectSubmeshesAtLeast: 2);

        [UnityTest]
        public IEnumerator Poi_BuildsIconsAndNames() => RunHandler("OmtPoi",
            new OmtTestTile("poi").Point(2048, 2048, ("class", "cafe"), ("subclass", "cafe"), ("name", "Coffee"), ("rank", 1)),
            expectMeshes: 1, expectSubmeshesAtLeast: 2);

        [UnityTest]
        public IEnumerator TransportationName_BuildsNamesAndShields() => RunHandler("OmtTransportationName",
            new OmtTestTile("transportation_name")
                .Line(Road, ("class", "motorway"), ("name", "Ring Road"), ("ref", "M1")),
            expectMeshes: 1, expectSubmeshesAtLeast: 2);

        [UnityTest]
        public IEnumerator Boundary_Water_Waterway_Park_Aeroway_Landuse_Build() => OmtTestTile.Run(async ct =>
        {
            await RunOnce("OmtBoundary", new OmtTestTile("boundary").Line(Road, ("admin_level", 2), ("disputed", 1)), 1, 0, ct);
            await RunOnce("OmtWater", new OmtTestTile("water").Polygon(Block, ("class", "lake")), 1, 0, ct);
            await RunOnce("OmtWaterway", new OmtTestTile("waterway")
                .Line(Road, ("class", "river"), ("name", "Moskva"))
                .Line(Road, ("class", "canal"), ("brunnel", "bridge")), 3, 0, ct);
            // Fill and outline; park names end at zoom 12, below the labels' zooms on this z14 tile.
            await RunOnce("OmtPark", new OmtTestTile("park")
                .Polygon(Block, ("class", "national_park"))
                .Point(1000, 1000, ("class", "national_park"), ("name", "Losiny Ostrov"), ("rank", 1)), 2, 0, ct);
            await RunOnce("OmtAeroway", new OmtTestTile("aeroway")
                .Polygon(Block, ("class", "aerodrome"))
                .Line(Road, ("class", "runway"), ("ref", "06/24")), 3, 0, ct);
            await RunOnce("OmtLanduse", new OmtTestTile("landuse").Polygon(Block, ("class", "residential")), 2, 0, ct);
        });

        [UnityTest]
        public IEnumerator Labels_Housenumber_Peak_Aerodrome_WaterName_Build() => OmtTestTile.Run(async ct =>
        {
            await RunOnce("OmtHousenumber", new OmtTestTile("housenumber").Point(2048, 2048, ("housenumber", "12/3")), 1, 1, ct);
            await RunOnce("OmtMountainPeak", new OmtTestTile("mountain_peak")
                .Point(2048, 2048, ("class", "peak"), ("name", "Elbrus"), ("ele", 5642), ("rank", 1)), 1, 2, ct);
            await RunOnce("OmtAerodromeLabel", new OmtTestTile("aerodrome_label")
                .Point(2048, 2048, ("class", "international"), ("name", "Sheremetyevo"), ("iata", "SVO")), 1, 2, ct);
            await RunOnce("OmtWaterName", new OmtTestTile("water_name")
                .Point(2048, 2048, ("class", "lake"), ("name", "Lake"))
                .Line(Road, ("class", "lake"), ("name", "Lake line")), 1, 1, ct);
        });

        // ---- helpers --------------------------------------------------------

        private static IEnumerator RunHandler(string asset, OmtTestTile tile, int expectMeshes, int expectSubmeshesAtLeast = 0) =>
            OmtTestTile.Run(ct => RunOnce(asset, tile, expectMeshes, expectSubmeshesAtLeast, ct));

        private static async Awaitable RunEmpty(string asset, OmtTestTile tileBuilder, CancellationToken ct)
        {
            var handler = AssetDatabase.LoadAssetAtPath<OmtLayerHandler>(Presets + asset + ".asset");
            Assert.That(handler, Is.Not.Null, $"preset {asset} loads");

            using var tile = tileBuilder.Build();
            using var ground = new FlatGroundGridProvider();
            var root = new GameObject("TileRoot");
            ILayerHandlerInstance instance = null;
            try
            {
                var grid = GroundTestUtils.FlatGrid(ground, Coord);
                var grids = new TileGroundGrids(grid, default, default, default, default, default, default, default, default);
                instance = await handler.OnLayerLoadedAsync(new LayerLoadContext(tile.GetLayer(0), Coord, root.transform, Frame, grids, ct));
                Assert.That(instance, Is.Null, $"{asset} builds nothing");
            }
            finally
            {
                instance?.Dispose();
                Object.DestroyImmediate(root);
            }
        }

        private static async Awaitable RunOnce(string asset, OmtTestTile tileBuilder, int expectMeshes,
            int expectSubmeshesAtLeast, CancellationToken ct)
        {
            var handler = AssetDatabase.LoadAssetAtPath<OmtLayerHandler>(Presets + asset + ".asset");
            Assert.That(handler, Is.Not.Null, $"preset {asset} loads");

            using var tile = tileBuilder.Build();
            using var ground = new FlatGroundGridProvider();
            var root = new GameObject("TileRoot");
            ILayerHandlerInstance instance = null;
            try
            {
                var grid = GroundTestUtils.FlatGrid(ground, Coord);
                var grids = new TileGroundGrids(grid, default, default, default, default, default, default, default, default);
                var ctx = new LayerLoadContext(tile.GetLayer(0), Coord, root.transform, Frame, grids, ct);
                instance = await handler.OnLayerLoadedAsync(ctx);

                Assert.That(instance, Is.Not.Null, $"{asset} builds content");
                var renderers = root.GetComponentsInChildren<MeshRenderer>();
                Assert.That(renderers.Length, Is.EqualTo(expectMeshes), $"{asset} mesh count");
                foreach (var r in renderers)
                {
                    var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                    Assert.That(mesh.vertexCount, Is.GreaterThan(0), r.name);
                    Assert.That(r.sharedMaterials.Length, Is.EqualTo(mesh.subMeshCount), r.name);
                    foreach (var m in r.sharedMaterials) Assert.That(m, Is.Not.Null, r.name);
                }
                if (expectSubmeshesAtLeast > 0)
                {
                    int max = 0;
                    foreach (var r in renderers) max = Mathf.Max(max, r.GetComponent<MeshFilter>().sharedMesh.subMeshCount);
                    Assert.That(max, Is.GreaterThanOrEqualTo(expectSubmeshesAtLeast), $"{asset}: icon and glyph submeshes");
                }
                Assert.That(instance.MemoryUsage.Equals(GeoFoundry.Core.TileMemoryUsage.Zero), Is.False, "memory is accounted");
            }
            finally
            {
                instance?.Dispose();
                Object.DestroyImmediate(root);
            }
        }
    }
}
#endif
