using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GeoFoundry.Core.Handlers;
using GeoFoundry.OpenMapTiles;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode.OpenMapTiles
{
    /// <summary>Unit tests of the OpenMapTiles helpers and the coverage of the schema by the built-in handlers.</summary>
    public sealed class OmtCommonTests
    {
        /// <summary>Layers of the OpenMapTiles 3.16 schema (openmaptiles.yaml).</summary>
        private static readonly string[] SchemaLayers =
        {
            "water", "waterway", "landcover", "landuse", "mountain_peak", "park", "boundary", "aeroway",
            "transportation", "building", "water_name", "transportation_name", "place", "housenumber", "poi",
            "aerodrome_label",
        };

        [Test]
        public void EverySchemaLayer_HasExactlyOneBuiltInHandler()
        {
            var types = typeof(OmtLayerHandler).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(OmtLayerHandler).IsAssignableFrom(t)).ToList();
            var layers = new List<string>();
            foreach (var type in types)
            {
                var handler = (LayerHandlerSO)ScriptableObject.CreateInstance(type);
                layers.Add(handler.LayerName);
                UnityEngine.Object.DestroyImmediate(handler);
            }
            Assert.That(layers, Is.EquivalentTo(SchemaLayers));
        }

        [Test]
        public void EveryHandler_HasCreateAssetMenu_AndOwnFile()
        {
            foreach (var type in typeof(OmtLayerHandler).Assembly.GetTypes()
                         .Where(t => !t.IsAbstract && typeof(ScriptableObject).IsAssignableFrom(t)))
            {
                Assert.That(type.GetCustomAttribute<CreateAssetMenuAttribute>(), Is.Not.Null, type.Name);
            }
        }

        [TestCase("hsl(194, 45%, 77%)", 0.6665f, 0.8252f, 0.8735f, 1f)]
        [TestCase("hsla(0, 0%, 100%, 0.8)", 1f, 1f, 1f, 0.8f)]
        [TestCase("rgb(255, 0, 128)", 1f, 0f, 0.502f, 1f)]
        public void OmtColor_ParsesStyleColors(string css, float r, float g, float b, float a)
        {
            var c = OmtColor.Parse(css);
            Assert.That(c.r, Is.EqualTo(r).Within(2e-3f));
            Assert.That(c.g, Is.EqualTo(g).Within(2e-3f));
            Assert.That(c.b, Is.EqualTo(b).Within(2e-3f));
            Assert.That(c.a, Is.EqualTo(a).Within(1e-4f));
        }

        [Test]
        public void StyleDefaults_KeepTheStyleOpacityAsAlpha()
        {
            Assert.That(OmtStyleDefaults.ParkFills()[0].Color.a, Is.EqualTo(0.15f).Within(1e-4f), "the park tint is see-through");
            Assert.That(OmtStyleDefaults.WaterIntermittent.a, Is.EqualTo(0.85f).Within(1e-4f));
            Assert.That(OmtStyleDefaults.LandcoverFills()[0].Color.a, Is.EqualTo(1f), "opaque style fills stay opaque");
            Assert.That(OmtColor.ToVertex(new Color(1f, 0f, 0f, 0.15f)).a, Is.EqualTo(38), "alpha reaches the vertex color");
        }

        [Test]
        public void DrawOrder_PutsLanduseAndAerowayBelowLandcover_AsTheStyle()
        {
            Assert.That(OmtDrawOrder.Landuse, Is.LessThan(OmtDrawOrder.Landcover));
            Assert.That(OmtDrawOrder.AerowayFill, Is.LessThan(OmtDrawOrder.Landcover));
            Assert.That(OmtDrawOrder.Terrain, Is.LessThan(OmtDrawOrder.Landuse));
            // Style order: waterway 47–51, water 52–53, park lines 55–56, aeroway lines 57–58, land use outlines
            // 60–61, transportation 63+, boundaries 203+.
            int[] styleOrder =
            {
                OmtDrawOrder.Landcover, OmtDrawOrder.Waterway, OmtDrawOrder.Water, OmtDrawOrder.ParkOutline,
                OmtDrawOrder.AerowayLines, OmtDrawOrder.LanduseOutline, OmtDrawOrder.TransportationAreas,
                OmtDrawOrder.Transportation, OmtDrawOrder.Boundary, OmtDrawOrder.Building,
            };
            Assert.That(styleOrder, Is.Ordered.And.Unique);
        }

        [Test]
        public void OmtColor_RejectsUnknownFunctions() =>
            Assert.Throws<FormatException>(() => OmtColor.Parse("cmyk(0,0,0,0)"));

        [Test]
        public void OmtZoom_MetersPerPixel_MatchesMapLibreConvention()
        {
            Assert.That(OmtZoom.MetersPerPixel(0), Is.EqualTo(78271.517f).Within(0.01f));
            Assert.That(OmtZoom.MetersPerPixel(14), Is.EqualTo(4.7773f).Within(1e-3f));
            var width = OmtZoom.LineWidth(10f, 2f, 1.5f);
            Assert.That(width, Is.EqualTo(new GeoFoundry.Geometry.LineWidth(15f, 2f)), "real meters scaled to Mercator");
            Assert.That(width.Evaluate(4f), Is.EqualTo(15f), "the real width up close");
            Assert.That(width.Evaluate(10f), Is.EqualTo(20f), "at least 2 px from afar");
        }

        [TestCase(133f, -4, -1)]
        [TestCase(1024f, -1, 1)]
        [TestCase(1500f, -1, 2)]
        [TestCase(2048f, 0, 2)]
        public void OmtZoom_LabelTierRange_CoversTheShownSizesWithMargin(float size, int first, int last)
        {
            var tiers = OmtZoom.LabelTierRange(size);
            Assert.That(tiers.x, Is.EqualTo(first));
            Assert.That(tiers.y, Is.EqualTo(last));
        }

        [TestCase("12", 12.0)]
        [TestCase("-3.5", -3.5)]
        [TestCase("2,25", 2.25)]
        [TestCase(" 7", 7.0)]
        public void OmtProps_ParsesNumericStrings(string text, double expected)
        {
            FixedString128Bytes s = text;
            Assert.That(OmtProps.TryParseNumber(s, out double v), Is.True);
            Assert.That(v, Is.EqualTo(expected).Within(1e-9));
        }

        [Test]
        public void OmtProps_RejectsNonNumbers()
        {
            FixedString128Bytes s = "yes";
            Assert.That(OmtProps.TryParseNumber(s, out _), Is.False);
        }

        [Test]
        public void OmtText_UppercaseCoversLatinCyrillicGreek()
        {
            Assert.That(OmtText.Transform('a', OmtTextTransform.Uppercase), Is.EqualTo('A'));
            Assert.That(OmtText.Transform('я', OmtTextTransform.Uppercase), Is.EqualTo('Я'));
            Assert.That(OmtText.Transform('ё', OmtTextTransform.Uppercase), Is.EqualTo('Ё'));
            Assert.That(OmtText.Transform('λ', OmtTextTransform.Uppercase), Is.EqualTo('Λ'));
            Assert.That(OmtText.Transform('1', OmtTextTransform.Uppercase), Is.EqualTo('1'));
            Assert.That(OmtText.Transform('a', OmtTextTransform.None), Is.EqualTo('a'));
        }

        [Test]
        public void OmtText_DecodesUtf8()
        {
            FixedString128Bytes s = "Мир 1";
            var cps = new NativeList<int>(8, Allocator.Temp);
            OmtText.Decode(s, OmtTextTransform.None, ref cps);
            Assert.That(cps.Length, Is.EqualTo(5));
            Assert.That(cps[0], Is.EqualTo('М'));
            Assert.That(cps[3], Is.EqualTo(' '));
            cps.Dispose();
        }

        [Test]
        public void OmtIcons_ResolvesTemplates()
        {
            var ids = new NativeHashMap<FixedString64Bytes, int>(4, Allocator.Temp);
            ids.TryAdd("cafe", 3);
            ids.TryAdd("road_motorway", 7);
            FixedString128Bytes cls = "motorway", sub = "cafe";
            Assert.That(OmtIcons.Resolve(OmtRules.Fixed("{subclass}"), cls, sub, ids), Is.EqualTo(3));
            Assert.That(OmtIcons.Resolve(OmtRules.Fixed("road_{class}"), cls, sub, ids), Is.EqualTo(7));
            Assert.That(OmtIcons.Resolve(OmtRules.Fixed("{class}"), cls, sub, ids), Is.EqualTo(-1));
            Assert.That(OmtIcons.Resolve(default, cls, sub, ids), Is.EqualTo(-1));
            ids.Dispose();
        }

        [Test]
        public void OmtIconAtlas_ParsesIndex_WithFlippedV()
        {
            var atlas = ScriptableObject.CreateInstance<OmtIconAtlas>();
            var texture = new Texture2D(64, 32);
            var index = new TextAsset(
                "{\"width\":64,\"height\":32,\"icons\":[{\"name\":\"peak\",\"x\":0,\"y\":0,\"w\":16,\"h\":8,\"pixelRatio\":2}]}");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(OmtIconAtlas).GetField("_texture", flags).SetValue(atlas, texture);
            typeof(OmtIconAtlas).GetField("_index", flags).SetValue(atlas, index);
            try
            {
                Assert.That(atlas.Count, Is.EqualTo(1));
                Assert.That(atlas.Contains("peak"), Is.True);
                var info = atlas.Infos[0];
                Assert.That(info.Size.x, Is.EqualTo(8f));
                Assert.That(info.Size.y, Is.EqualTo(4f));
                Assert.That(info.Uv.y, Is.EqualTo(1f - 8f / 32f).Within(1e-5f), "top rows map to the top of the texture");
                Assert.That(info.Uv.w, Is.EqualTo(1f).Within(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(atlas);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(index);
            }
        }

        [Test]
        public void StyleDefaults_AreValid()
        {
            // Building the defaults parses every color of the transcribed style.
            Assert.That(OmtStyleDefaults.TransportationLines().Count, Is.GreaterThan(20));
            Assert.That(OmtStyleDefaults.LandcoverFills().Count, Is.GreaterThan(30));
            Assert.That(OmtLabelDefaults.Poi().Count, Is.GreaterThan(40));
            Assert.That(OmtLabelDefaults.Place().Any(r => r.Class == "city" && r.Subclass == "2"), Is.True);
        }

        // ---- schema zooms -----------------------------------------------------

        /// <summary>
        /// Tile zoom from which the OpenMapTiles schema (reference SQL, <c>layers/*/*.sql</c>) puts a class into the
        /// tiles, per label rule list; a class not listed has data at the rule's minimum zoom or earlier.
        /// </summary>
        private static readonly (string List, Func<List<OmtLabelRule>> Rules, Func<OmtLabelRule, float> SchemaZoom)[]
            SchemaZooms =
            {
                ("place", OmtLabelDefaults.Place, r => r.Class switch
                {
                    "town" => 7, "village" => 8, "hamlet" or "borough" or "suburb" => 11,
                    "quarter" or "neighbourhood" or "isolated_dwelling" => 14, "island" => 8, "aboriginal_lands" => 6,
                    "state" or "province" => 2, "city" => 2, _ => 0,
                }),
                ("poi", OmtLabelDefaults.Poi, r => r.Class switch
                {
                    "railway" => r.Subclass is "tram_stop" or "subway" ? 14 : 12,
                    "ferry_terminal" => 12, "college" => 10, _ => 14,
                }),
                ("aerodrome_label", OmtLabelDefaults.AerodromeLabel, r => r.Class == "international" ? 8 : 10),
                ("water_name", OmtLabelDefaults.WaterName, r => r.Class == "bay" ? 13 : 0),
                ("water_name lines", OmtLabelDefaults.WaterNameLines, r => 3),
                ("waterway", OmtLabelDefaults.Waterway, r => r.Class switch
                {
                    "river" => 3, "canal" => 12, _ => 13,
                }),
                ("transportation_name", OmtLabelDefaults.TransportationNames, r => r.Class switch
                {
                    "motorway" => 6, "trunk" => 8, "path" => 13, _ => 12,
                }),
                ("transportation_name shields", OmtLabelDefaults.TransportationShields, r => r.Class switch
                {
                    "motorway" => 6, "trunk" => 8, _ => 12,
                }),
                ("transportation_name junctions", OmtLabelDefaults.TransportationJunctions, r => 10),
                ("oneway arrows", OmtLabelDefaults.OnewayArrows, r => 12),
                ("mountain_peak", OmtLabelDefaults.MountainPeak, r => r.Class is "ridge" or "arete" or "cliff" ? 13 : 7),
                ("housenumber", OmtLabelDefaults.Housenumber, r => 14),
                ("park", OmtLabelDefaults.Park, r => 5),
                ("aeroway", OmtLabelDefaults.Aeroway, r => r.Class == "gate" ? 14 : 10),
            };

        /// <summary>Schema zooms of the fill rule lists (tile zooms, see <see cref="SchemaZooms"/>).</summary>
        private static readonly (string List, Func<List<OmtFillRule>> Rules, Func<OmtFillRule, float> SchemaZoom)[]
            FillSchemaZooms =
            {
                ("water", OmtStyleDefaults.WaterFills, r => 0),
                ("landcover", OmtStyleDefaults.LandcoverFills, r => r.Class == "ice" ? 0 : 7),
                ("landuse", OmtStyleDefaults.LanduseFills, r => r.Class switch
                {
                    "residential" => 4, "suburb" or "quarter" or "neighbourhood" => 6, _ => 9,
                }),
                ("park", OmtStyleDefaults.ParkFills, r => 4),
                ("aeroway", OmtStyleDefaults.AerowayFills, r => 10),
                ("transportation areas", OmtStyleDefaults.TransportationAreas, r => 13),
            };

        /// <summary>Schema zooms of the line rule lists (see <see cref="SchemaZooms"/>).</summary>
        private static readonly (string List, Func<List<OmtLineRule>> Rules, Func<OmtLineRule, float> SchemaZoom)[]
            LineSchemaZooms =
            {
                ("waterway", OmtStyleDefaults.WaterwayLines, r => r.Class switch
                {
                    "river" => 3, "canal" => 12, _ => 13,
                }),
                ("landcover outlines", OmtStyleDefaults.LandcoverOutlines, r => r.Class == "ice" ? 0 : 7),
                ("landuse outlines", OmtStyleDefaults.LanduseOutlines, r => r.Class == "residential" ? 4 : 9),
                ("park outlines", OmtStyleDefaults.ParkOutlines, r => 4),
                ("boundary", OmtStyleDefaults.BoundaryLines, r => r.Class switch
                {
                    "2" => 0, "3" => 5, "4" => 1, "5" or "6" => 10, "7" or "8" => 12, _ => 13,
                }),
                ("boundary areas", OmtStyleDefaults.BoundaryAreaOutlines, r => 4),
                ("aeroway", OmtStyleDefaults.AerowayLines, r => 10),
                ("mountain_peak", OmtStyleDefaults.MountainLines, r => 13),
                ("transportation", OmtStyleDefaults.TransportationLines, r => TransportationZoom(r.Class, r.Subclass)),
            };

        private static float TransportationZoom(string cls, string subclass)
        {
            switch (cls)
            {
                case "motorway": case "trunk": case "ferry": return 4;
                case "primary": case "rail": return 8;
                case "secondary": return 9;
                case "tertiary": case "busway": case "bus_guideway": return 11;
                case "minor": case "raceway": case "track": case "path": case "aerialway": return 12;
                case "service": case "pier": return 13;
                case "transit": return subclass is "subway" or "tram" or "monorail" ? 14 : 11;
            }
            if (cls.EndsWith("_construction"))
            {
                string road = cls.Substring(0, cls.Length - "_construction".Length);
                return road is "minor" or "service" ? 13 : road is "track" or "path" ? 14 : TransportationZoom(road, "");
            }
            throw new ArgumentException($"no schema zoom for transportation class '{cls}'");
        }

        [Test]
        public void LabelDefaults_AreNeverShownBeforeTheSchemaPutsTheirDataIntoTiles()
        {
            foreach (var (list, rules, schemaZoom) in SchemaZooms)
            foreach (var rule in rules())
            {
                Assert.That(rule.MinZoom, Is.GreaterThanOrEqualTo(schemaZoom(rule)),
                    $"{list} rule '{rule.Class}/{rule.Subclass}' fades in before its data exists");
            }
        }

        [Test]
        public void FillAndLineDefaults_AreNeverShownBeforeTheSchemaPutsTheirDataIntoTiles()
        {
            foreach (var (list, rules, schemaZoom) in FillSchemaZooms)
            foreach (var rule in rules())
            {
                Assert.That(rule.MinZoom, Is.GreaterThanOrEqualTo(schemaZoom(rule)),
                    $"{list} fill rule '{rule.Class}/{rule.Subclass}' starts before its data exists");
            }
            foreach (var (list, rules, schemaZoom) in LineSchemaZooms)
            foreach (var rule in rules())
            {
                Assert.That(rule.MinZoom, Is.GreaterThanOrEqualTo(schemaZoom(rule)),
                    $"{list} line rule '{rule.Class}/{rule.Subclass}' starts before its data exists");
                Assert.That(Mathf.Max(rule.MinZoom, rule.CasingMinZoom), Is.GreaterThanOrEqualTo(schemaZoom(rule)));
            }
        }

        // ---- schema vocabulary ------------------------------------------------

        /// <summary>
        /// Values of the class (and subclass) keys the OpenMapTiles 3.16 schema writes, per rule list
        /// (<c>layers/*/*.yaml</c> values, <c>mapping.yaml</c> and the SQL). Every value must be matched by a rule (a
        /// specific one or a deliberate fallback), and every value named by a rule must be one the schema writes.
        /// Subclasses are listed as <c>class/subclass</c>; a null subclass list means the key is not matched.
        /// </summary>
        private sealed class Vocabulary
        {
            public string List;
            public Func<IEnumerable<(string Class, string Subclass, bool Visible)>> Rules;
            public string[] Classes;
            public string[] Pairs;
            public string[] Subclasses;
            /// <summary>Rule classes may be values the vocabulary does not close (e.g. protection titles of parks).</summary>
            public bool OpenClasses;
            /// <summary>The rules style only some values (outlines, arrows): only the reverse check applies.</summary>
            public bool Partial;
        }

        private static Func<IEnumerable<(string, string, bool)>> Fills(Func<List<OmtFillRule>> rules) =>
            () => rules().Select(r => (r.Class, r.Subclass, r.Visible));

        private static Func<IEnumerable<(string, string, bool)>> Lines(params Func<List<OmtLineRule>>[] lists) =>
            () => lists.SelectMany(l => l()).Select(r => (r.Class, r.Subclass, r.Visible));

        private static Func<IEnumerable<(string, string, bool)>> Labels(params Func<List<OmtLabelRule>>[] lists) =>
            () => lists.SelectMany(l => l()).Select(r => (r.Class, r.Subclass, r.Visible));

        private static readonly string[] LandcoverPairs =
        {
            "farmland/farmland", "farmland/farm", "farmland/orchard", "farmland/vineyard", "farmland/plant_nursery",
            "ice/glacier", "ice/ice_shelf", "wood/wood", "wood/forest", "rock/bare_rock", "rock/scree",
            "grass/fell", "grass/flowerbed", "grass/grassland", "grass/heath", "grass/scrub", "grass/shrubbery",
            "grass/tundra", "grass/grass", "grass/meadow", "grass/allotments", "grass/park", "grass/village_green",
            "grass/recreation_ground", "grass/garden", "grass/golf_course",
            "wetland/wetland", "wetland/bog", "wetland/swamp", "wetland/wet_meadow", "wetland/marsh", "wetland/reedbed",
            "wetland/saltern", "wetland/tidalflat", "wetland/saltmarsh", "wetland/mangrove",
            "sand/beach", "sand/sand", "sand/dune",
        };

        private static readonly string[] Roads =
        {
            "motorway", "trunk", "primary", "secondary", "tertiary", "minor", "service", "track", "path", "raceway",
        };

        private static readonly string[] TransportationClasses = Roads
            .Concat(Roads.Select(r => r + "_construction"))
            .Concat(new[] { "busway", "bus_guideway", "ferry", "rail", "transit", "aerialway", "pier", "bridge" })
            .ToArray();

        private static readonly string[] TransportationSubclasses =
        {
            "rail", "narrow_gauge", "preserved", "funicular", "subway", "light_rail", "monorail", "tram", "pedestrian",
            "path", "footway", "cycleway", "steps", "bridleway", "corridor", "platform",
        };

        /// <summary>Classes of the poi layer: the class of <c>poi.yaml</c> for mapped subclasses, else the subclass.</summary>
        private static readonly string[] PoiClasses =
        {
            "aerialway", "alcohol_shop", "aquarium", "archery", "art_gallery", "athletics", "atm", "attraction",
            "australian_football", "badminton", "bakery", "bank", "bar", "baseball", "basin", "basketball",
            "beachvolleyball", "beer", "bicycle", "bicycle_parking", "bicycle_rental", "billiards", "bmx", "bollard",
            "border_control", "boules", "bowls", "boxing", "brownfield", "bus", "butcher", "cafe", "campsite",
            "canadian_football", "canoe", "car", "castle", "cemetery", "chess", "cinema", "climbing",
            "climbing_adventure", "clothing_store", "college", "cricket", "cricket_nets", "croquet", "curling",
            "cycle_barrier", "cycling", "dentist", "disc_golf", "diving", "doctors", "dog_park", "dog_racing",
            "drinking_water", "entrance", "equestrian", "escape_game", "fast_food", "fatsal", "ferry_terminal",
            "field_hockey", "fire_station", "free_flying", "fuel", "gaelic_games", "garden", "gate", "golf", "grocery",
            "gymnastics", "hackerspace", "hairdresser", "handball", "harbor", "hockey", "horse_racing", "horseshoes",
            "hospital", "ice_cream", "ice_hockey", "ice_rink", "ice_stock", "information", "judo", "karting", "korfball",
            "laundry", "library", "lift_gate", "lodging", "long_jump", "model_aerodrome", "monument", "motocross",
            "motor", "motorcycle_parking", "multi", "museum", "music", "netball", "office", "orienteering",
            "paddle_tennis", "paintball", "paragliding", "park", "parking", "pelota", "pharmacy", "picnic_site", "pitch",
            "place_of_worship", "playground", "police", "post", "prison", "racquet", "railway", "rc_car", "recycling",
            "reservoir", "restaurant", "rowing", "rugby", "rugby_league", "rugby_union", "running", "sailing",
            "sally_port", "school", "scuba_diving", "shelter", "shooting", "shooting_range", "shop", "skateboard",
            "skating", "skiing", "sports_centre", "stadium", "stile", "surfing", "swimming", "swimming_pool",
            "table_soccer", "table_tennis", "team_handball", "telephone", "tennis", "theatre", "theme_park", "toboggan",
            "toilets", "toll_booth", "town_hall", "veterinary", "volleyball", "waste_basket", "water_park", "water_ski",
            "winter_sports", "yoga", "zoo",
        };

        /// <summary>Subclasses of the poi layer (<c>layers/poi/mapping.yaml</c>, plus <c>subway</c> stations).</summary>
        private static readonly string[] PoiSubclasses =
        {
            "accessories", "accountant", "advertising_agency", "alcohol", "alpine_hut", "american_football", "antiques",
            "aquarium", "archery", "architect", "art", "arts_centre", "artwork", "association", "athletics", "atm",
            "attraction", "australian_football", "aviary", "badminton", "bag", "bail_bond_agent", "bakery", "bank",
            "bar", "baseball", "basin", "basketball", "bbq", "beachvolleyball", "beauty", "bed", "bed_and_breakfast",
            "beverages", "bicycle", "bicycle_parking", "bicycle_rental", "biergarten", "billiards", "birds", "bmx",
            "bollard", "books", "border_control", "boules", "boutique", "bowls", "boxing", "brownfield", "bus_station",
            "bus_stop", "butcher", "butterfly", "cafe", "camera", "camp_site", "canadian_football", "canoe", "car",
            "car_parts", "car_repair", "caravan_site", "carpet", "castle", "cemetery", "chalet", "charging_station",
            "charity", "chemist", "chess", "chocolate", "cinema", "climbing", "climbing_adventure", "clinic", "clothes",
            "coffee", "college", "community_centre", "company", "computer", "confectionery", "construction_company",
            "consulting", "convenience", "cooperative", "copyshop", "cosmetics", "courier", "courthouse", "coworking",
            "cricket", "cricket_nets", "croquet", "curling", "cycle_barrier", "cycling", "deli", "delicatessen",
            "dentist", "department_store", "diplomatic", "disc_golf", "diving", "dock", "doctors", "dog_park",
            "dog_racing", "doityourself", "dormitory", "drinking_water", "dry_cleaning", "educational_institution",
            "electronics", "employment_agency", "enclosure", "energy_supplier", "engineer", "equestrian", "erotic",
            "escape_game", "estate_agent", "fabric", "falconry", "farm", "fast_food", "fatsal", "ferry_terminal",
            "field_hockey", "financial", "financial_advisor", "fire_station", "florist", "food_court", "forestry",
            "foundation", "free_flying", "frozen_food", "fuel", "furniture", "gaelic_games", "gallery", "garden",
            "garden_centre", "gate", "general", "geodesist", "gift", "golf", "golf_course", "government",
            "graphic_design", "grave_yard", "greengrocer", "guest_house", "guide", "gymnastics", "hackerspace",
            "hairdresser", "halt", "handball", "harbour_master", "hardware", "health_insurance", "hearing_aids", "hifi",
            "hockey", "horse_racing", "horseshoes", "hospital", "hostel", "hotel", "ice_cream", "ice_hockey", "ice_rink",
            "ice_stock", "information", "insurance", "interior_decoration", "interior_design", "it", "jewelry", "judo",
            "karting", "kindergarten", "kiosk", "korfball", "lamps", "laundry", "lawyer", "library", "lift_gate",
            "locksmith", "logistics", "long_jump", "mall", "marina", "marketing", "marketplace", "massage",
            "miniature_golf", "mobile_phone", "model_aerodrome", "monument", "motel", "motocross", "motor", "motorcycle",
            "motorcycle_parking", "moving_company", "multi", "museum", "music", "musical_instrument", "netball",
            "newsagent", "newspaper", "ngo", "nightclub", "notary", "nursing_home", "optician", "orienteering",
            "outdoor", "paddle_tennis", "paint", "paintball", "paragliding", "parcel_locker", "park", "parking",
            "pelota", "perfume", "perfumery", "pet", "petting_zoo", "pharmacy", "photo", "physician", "picnic_site",
            "pitch", "place_of_worship", "playground", "police", "political_party", "post_box", "post_office", "prison",
            "private_investigator", "property_management", "pub", "public_building", "publisher", "quango", "racquet",
            "rc_car", "recycling", "religion", "reptile", "research", "reservoir", "restaurant", "rowing", "rugby",
            "rugby_league", "rugby_union", "ruins", "running", "safari_park", "sailing", "sally_port", "school",
            "scuba_diving", "second_hand", "security", "shelter", "shoes", "shooting", "shooting_range", "skateboard",
            "skating", "skiing", "soccer", "sports", "sports_centre", "stadium", "station", "stationery", "stile",
            "subway", "subway_entrance", "supermarket", "surfing", "surveyor", "swimming", "swimming_area",
            "swimming_pool", "table_soccer", "table_tennis", "tailor", "tattoo", "tax_advisor", "taxi", "team_handball",
            "telecommunication", "telephone", "tennis", "terrarium", "theatre", "theme_park", "therapist", "ticket",
            "tobacco", "toboggan", "toilets", "toll_booth", "townhall", "toys", "train_station_entrance", "tram_stop",
            "translator", "travel_agency", "travel_agent", "tutoring", "union", "university", "veterinary", "video",
            "video_games", "viewpoint", "volleyball", "waste_basket", "watches", "water_park", "water_ski",
            "water_utility", "weapons", "web_design", "wedding_planner", "wholesale", "wildlife_park", "wine",
            "winter_sports", "yoga", "zoo",
        };

        private static readonly Vocabulary[] Vocabularies =
        {
            new Vocabulary
            {
                List = "water", Rules = Fills(OmtStyleDefaults.WaterFills),
                Classes = new[] { "dock", "river", "pond", "lake", "ocean", "swimming_pool" },
            },
            new Vocabulary
            {
                List = "waterway", Rules = Lines(OmtStyleDefaults.WaterwayLines),
                Classes = new[] { "stream", "river", "canal", "drain", "ditch" },
            },
            new Vocabulary
            {
                List = "waterway labels", Rules = Labels(OmtLabelDefaults.Waterway),
                Classes = new[] { "stream", "river", "canal", "drain", "ditch" },
            },
            new Vocabulary { List = "landcover", Rules = Fills(OmtStyleDefaults.LandcoverFills), Pairs = LandcoverPairs },
            new Vocabulary
            {
                List = "landcover outlines", Rules = Lines(OmtStyleDefaults.LandcoverOutlines), Pairs = LandcoverPairs,
                Partial = true,
            },
            new Vocabulary
            {
                List = "landuse", Rules = Fills(OmtStyleDefaults.LanduseFills),
                Classes = new[]
                {
                    "railway", "cemetery", "military", "residential", "commercial", "industrial", "garages", "retail",
                    "bus_station", "school", "university", "kindergarten", "college", "library", "hospital", "stadium",
                    "pitch", "playground", "track", "theme_park", "zoo", "suburb", "quarter", "neighbourhood", "dam",
                    "quarry", "parking",
                },
            },
            new Vocabulary
            {
                List = "mountain_peak", Rules = Labels(OmtLabelDefaults.MountainPeak),
                Classes = new[] { "peak", "volcano", "saddle", "ridge", "cliff", "arete" },
            },
            new Vocabulary
            {
                List = "mountain_peak lines", Rules = Lines(OmtStyleDefaults.MountainLines),
                Classes = new[] { "ridge", "cliff", "arete" },
            },
            new Vocabulary
            {
                List = "park", Rules = Lines(OmtStyleDefaults.ParkOutlines), OpenClasses = true,
                Classes = new[] { "national_park", "nature_reserve", "protected_area" },
            },
            new Vocabulary
            {
                List = "boundary", Rules = Lines(OmtStyleDefaults.BoundaryLines),
                Classes = new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10" },
            },
            new Vocabulary
            {
                List = "boundary areas", Rules = Lines(OmtStyleDefaults.BoundaryAreaOutlines),
                Classes = new[] { "aboriginal_lands" },
            },
            new Vocabulary
            {
                List = "aeroway", Rules = Fills(OmtStyleDefaults.AerowayFills),
                Classes = new[] { "aerodrome", "heliport", "runway", "helipad", "taxiway", "apron" },
            },
            new Vocabulary
            {
                List = "aeroway lines", Rules = Lines(OmtStyleDefaults.AerowayLines), Classes = new[] { "runway", "taxiway" },
            },
            new Vocabulary
            {
                List = "aeroway labels", Rules = Labels(OmtLabelDefaults.Aeroway),
                Classes = new[] { "gate", "runway", "taxiway" },
            },
            new Vocabulary
            {
                List = "transportation", Rules = Lines(OmtStyleDefaults.TransportationLines),
                Classes = TransportationClasses.Where(c => c != "bridge").ToArray(), Subclasses = TransportationSubclasses,
            },
            new Vocabulary
            {
                List = "transportation areas", Rules = Fills(OmtStyleDefaults.TransportationAreas),
                Classes = new[] { "path", "pier", "bridge", "minor", "service" }, Subclasses = TransportationSubclasses,
            },
            new Vocabulary
            {
                List = "transportation_name", Rules = Labels(OmtLabelDefaults.TransportationNames),
                Classes = TransportationClasses.Where(c => c is not ("busway" or "bus_guideway" or "pier" or "bridge"))
                    .ToArray(),
            },
            new Vocabulary
            {
                List = "transportation_name shields", Rules = Labels(OmtLabelDefaults.TransportationShields),
                Classes = new[] { "motorway", "trunk", "primary", "secondary", "tertiary" }, Partial = true,
            },
            new Vocabulary
            {
                List = "transportation_name junctions", Rules = Labels(OmtLabelDefaults.TransportationJunctions),
                Classes = new[] { "motorway", "trunk", "primary", "secondary", "tertiary" },
                Subclasses = new[] { "junction" }, Partial = true,
            },
            new Vocabulary
            {
                List = "oneway arrows", Rules = Labels(OmtLabelDefaults.OnewayArrows), Classes = TransportationClasses,
                Partial = true,
            },
            new Vocabulary
            {
                List = "water_name", Rules = Labels(OmtLabelDefaults.WaterName, OmtLabelDefaults.WaterNameLines),
                Classes = new[] { "lake", "bay", "strait", "sea", "ocean" },
            },
            new Vocabulary
            {
                List = "place", Rules = Labels(OmtLabelDefaults.Place),
                Classes = new[]
                {
                    "continent", "country", "state", "province", "city", "town", "village", "hamlet", "borough",
                    "suburb", "quarter", "neighbourhood", "isolated_dwelling", "island", "aboriginal_lands",
                },
                Subclasses = new[] { "2", "3", "4", "5", "6" },
            },
            new Vocabulary
            {
                List = "poi", Rules = Labels(OmtLabelDefaults.Poi), Classes = PoiClasses, Subclasses = PoiSubclasses,
            },
            new Vocabulary
            {
                List = "aerodrome_label", Rules = Labels(OmtLabelDefaults.AerodromeLabel),
                Classes = new[] { "international", "public", "regional", "military", "private", "other" },
            },
        };

        private static bool Matches(string rule, string value) => rule.Length == 0 || rule == value;

        [Test]
        public void Defaults_CoverEverySchemaValue_AndNameNoValueTheSchemaDoesNotWrite()
        {
            foreach (var v in Vocabularies)
            {
                var rules = v.Rules().ToList();
                var pairs = (v.Pairs ?? new string[0]).Select(p => (p.Split('/')[0], p.Split('/')[1])).ToList();
                var classes = (v.Classes ?? pairs.Select(p => p.Item1).ToArray()).Distinct().ToList();

                if (!v.Partial && pairs.Count > 0)
                {
                    foreach (var (cls, sub) in pairs)
                        Assert.That(rules.Any(r => Matches(r.Class, cls) && Matches(r.Subclass, sub)), Is.True,
                            $"{v.List}: '{cls}/{sub}' matches no rule");
                }
                else if (!v.Partial)
                {
                    // Without a subclass rule of its own, a class needs a rule matching any subclass.
                    foreach (var cls in classes)
                        Assert.That(rules.Any(r => Matches(r.Class, cls) && r.Subclass.Length == 0), Is.True,
                            $"{v.List}: class '{cls}' matches no rule");
                }

                foreach (var r in rules)
                {
                    if (r.Class.Length > 0 && !v.OpenClasses)
                        Assert.That(classes, Does.Contain(r.Class), $"{v.List}: rule class '{r.Class}' is not a schema value");
                    if (r.Subclass.Length > 0)
                    {
                        bool known = pairs.Any(p => p.Item2 == r.Subclass) ||
                                     (v.Subclasses != null && v.Subclasses.Contains(r.Subclass));
                        Assert.That(known, Is.True, $"{v.List}: rule subclass '{r.Subclass}' is not a schema value");
                    }
                }
            }
        }

        /// <summary>
        /// The preset handlers keep their rule lists unserialized, so the defaults of the code apply: a list saved
        /// into an asset (e.g. after editing it in the inspector) silently overrides later changes of the defaults.
        /// </summary>
        [Test]
        public void Presets_DoNotSerializeRuleLists_SoTheCodeDefaultsApply()
        {
            string[] lists =
            {
                "_fills", "_lines", "_outlines", "_areas", "_labels", "_points", "_names", "_shields", "_junctions",
                "_onewayArrows",
            };
            foreach (var path in System.IO.Directory.GetFiles("Packages/com.00wz.geofoundry/Presets/OpenMapTiles", "Omt*.asset"))
            {
                var yaml = System.IO.File.ReadAllLines(path);
                foreach (var list in lists)
                    Assert.That(yaml.Any(l => l.StartsWith("  " + list + ":")), Is.False,
                        $"{System.IO.Path.GetFileName(path)} serializes {list}; revert the asset to use the code defaults");
            }
        }

        [Test]
        public void Materials_HaveTheSortingPriorityOfTheDrawOrder()
        {
            var expected = new Dictionary<string, int>
            {
                ["DefaultTerrain"] = OmtDrawOrder.Terrain, ["OmtLanduse"] = OmtDrawOrder.Landuse,
                ["OmtAerowayFill"] = OmtDrawOrder.AerowayFill, ["OmtLandcover"] = OmtDrawOrder.Landcover,
                ["OmtLandcoverOutline"] = OmtDrawOrder.LandcoverOutline, ["OmtWaterway"] = OmtDrawOrder.Waterway,
                ["OmtParkFill"] = OmtDrawOrder.ParkFill, ["OmtWater"] = OmtDrawOrder.Water,
                ["OmtParkOutline"] = OmtDrawOrder.ParkOutline, ["OmtAerowayLines"] = OmtDrawOrder.AerowayLines,
                ["OmtLanduseOutline"] = OmtDrawOrder.LanduseOutline,
                ["OmtTransportationAreas"] = OmtDrawOrder.TransportationAreas,
                ["OmtTransportation"] = OmtDrawOrder.Transportation,
                ["OmtWaterwayBridges"] = OmtDrawOrder.WaterwayBridges, ["OmtMountainLines"] = OmtDrawOrder.MountainLines,
                ["OmtBoundary"] = OmtDrawOrder.Boundary, ["OmtBuilding"] = OmtDrawOrder.Building,
            };
            foreach (var (name, priority) in expected)
            {
                var material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                    $"Packages/com.00wz.geofoundry/Presets/OpenMapTiles/Materials/{name}.mat");
                Assert.That(material, Is.Not.Null, name);
                Assert.That(Mathf.RoundToInt(material.GetFloat("_QueueOffset")), Is.EqualTo(priority), name);
                if (priority != OmtDrawOrder.Building)
                    Assert.That(material.renderQueue, Is.EqualTo(2000 + priority), name + " render queue");
            }
        }
    }
}
