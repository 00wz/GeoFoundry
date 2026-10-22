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
                    "quarter" or "neighbourhood" or "isolated_dwelling" => 14, "island" => 8, _ => 0,
                }),
                ("poi", OmtLabelDefaults.Poi, r => r.Class is "railway" or "ferry_terminal" or "college" or "university"
                    ? 12 : 14),
                ("aerodrome_label", OmtLabelDefaults.AerodromeLabel, r => r.Class == "international" ? 8 : 10),
                ("water_name", OmtLabelDefaults.WaterName, r => r.Class == "bay" ? 13 : 0),
                ("water_name lines", OmtLabelDefaults.WaterNameLines, r => 3),
                ("waterway", OmtLabelDefaults.Waterway, r => r.Class switch
                {
                    "river" => 3, "canal" => 12, _ => 13,
                }),
                ("transportation_name", OmtLabelDefaults.TransportationNames, r => r.Class switch
                {
                    "minor" or "tertiary" or "ferry" => 12, "path" or "service" or "track" => 13, _ => 0,
                }),
                ("mountain_peak", OmtLabelDefaults.MountainPeak, r => r.Class is "ridge" or "arete" or "cliff" ? 13 : 7),
                ("housenumber", OmtLabelDefaults.Housenumber, r => 14),
            };

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
    }
}
