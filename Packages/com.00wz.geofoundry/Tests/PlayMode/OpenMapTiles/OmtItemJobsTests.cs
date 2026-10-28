using System.Collections.Generic;
using GeoFoundry.Geometry;
using GeoFoundry.OpenMapTiles;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tests.PlayMode.OpenMapTiles
{
    /// <summary>
    /// Rule matching, modifiers and draw ordering of <see cref="OmtFillItemsJob"/>, <see cref="OmtLineItemsJob"/> and
    /// <see cref="OmtTransportationItemsJob"/>, run synchronously on synthetic layers.
    /// </summary>
    public sealed class OmtItemJobsTests
    {
        private static readonly int2[] Square = OmtTestTile.Square(100, 100, 400, 400);
        private static readonly int2[] Segment = { new int2(100, 100), new int2(900, 100) };

        [Test]
        public void FillItems_SubclassRuleWinsOverClass_AndItemsAreSortedByOrder()
        {
            using var tile = new OmtTestTile("landcover")
                .Polygon(Square, ("class", "grass"), ("subclass", "park"))
                .Polygon(Square, ("class", "wood"), ("subclass", "forest"))
                .Polygon(Square, ("class", "grass"), ("subclass", "meadow"))
                .Build();
            var rules = new List<OmtFillRule>
            {
                new OmtFillRule("", "park", Color.red, 30),
                new OmtFillRule("grass", "", Color.green, 20),
                new OmtFillRule("wood", "", Color.blue, 10),
            };

            var items = RunFill(tile, rules, "class", "subclass", 14, new List<OmtModifier>());
            try
            {
                Assert.That(items.Length, Is.EqualTo(3));
                Assert.That(items[0].FeatureIndex, Is.EqualTo(1), "wood (order 10) first");
                Assert.That(items[1].FeatureIndex, Is.EqualTo(2), "meadow falls back to the grass rule (order 20)");
                Assert.That(items[2].FeatureIndex, Is.EqualTo(0), "park subclass rule (order 30) last");
                Assert.That(items[2].Color, Is.EqualTo(OmtColor.ToVertex(Color.red)));
            }
            finally
            {
                items.Dispose();
            }
        }

        [Test]
        public void FillItems_RuleBelowItsMinZoom_FallsThroughToTheNextRule()
        {
            using var tile = new OmtTestTile("landcover")
                .Polygon(Square, ("class", "grass"), ("subclass", "park"))
                .Build();
            var rules = new List<OmtFillRule>
            {
                new OmtFillRule("", "park", Color.red, 20, 13),
                new OmtFillRule("grass", "", Color.green, 15, 7),
            };

            var z12 = RunFill(tile, rules, "class", "subclass", 12, new List<OmtModifier>());
            var z13 = RunFill(tile, rules, "class", "subclass", 13, new List<OmtModifier>());
            var z6 = RunFill(tile, rules, "class", "subclass", 6, new List<OmtModifier>());
            try
            {
                Assert.That(z12.Length, Is.EqualTo(1));
                Assert.That(z12[0].Color, Is.EqualTo(OmtColor.ToVertex(Color.green)), "the class rule below z13");
                Assert.That(z13[0].Color, Is.EqualTo(OmtColor.ToVertex(Color.red)), "the subclass rule from z13");
                Assert.That(z6.Length, Is.EqualTo(0), "no rule is active below z7");
            }
            finally
            {
                z12.Dispose();
                z13.Dispose();
                z6.Dispose();
            }
        }

        [Test]
        public void FillItems_MinZoomHiddenRulesAndSkipModifiers_FilterFeatures()
        {
            using var tile = new OmtTestTile("water")
                .Polygon(Square, ("class", "lake"))
                .Polygon(Square, ("class", "lake"), ("brunnel", "tunnel"))
                .Polygon(Square, ("class", "ocean"))
                .Polygon(Square, ("class", "pond"))
                .Build();
            var rules = new List<OmtFillRule>
            {
                new OmtFillRule("ocean", "", Color.blue, 0, 15),
                new OmtFillRule("pond", "", Color.blue, 0) { Visible = false },
                new OmtFillRule("", "", Color.cyan, 0),
            };
            var mods = new List<OmtModifier> { OmtModifier.Create("brunnel", "tunnel", OmtModifierAction.Skip) };

            var items = RunFill(tile, rules, "class", "", 14, mods);
            try
            {
                Assert.That(items.Length, Is.EqualTo(2), "tunnel skipped, pond hidden");
                Assert.That(items[0].FeatureIndex, Is.EqualTo(0));
                Assert.That(items[1].FeatureIndex, Is.EqualTo(2), "ocean below its rule's min zoom uses the generic rule");
                Assert.That(items[1].Color, Is.EqualTo(OmtColor.ToVertex(Color.cyan)));
            }
            finally
            {
                items.Dispose();
            }
        }

        [Test]
        public void LineItems_EmitCasingsThenFillsThenOverlays_WithShaderWidths()
        {
            using var tile = new OmtTestTile("transportation")
                .Line(Segment, ("class", "rail"))
                .Line(Segment, ("class", "road"))
                .Build();
            var rules = new List<OmtLineRule>
            {
                new OmtLineRule("rail", "", Color.gray, 4f, 0f, 2).WithOverlay(Color.white, 0.5f, 2f, 2f),
                new OmtLineRule("road", "", Color.white, 10f, 0f, 1).WithCasing(Color.black, 0f, 1f).WithDash(1f, 1f),
            };

            var (primary, secondary) = RunLines(tile, new OmtLinePass { Rules = rules }, new List<OmtModifier>());
            try
            {
                Assert.That(secondary.Length, Is.EqualTo(0));
                Assert.That(primary.Length, Is.EqualTo(4), "road casing, road fill, rail fill, rail overlay");
                Assert.That(primary[0].FeatureIndex, Is.EqualTo(1));
                Assert.That(primary[0].Width.Evaluate(0.1f), Is.EqualTo(12f).Within(1e-3f), "casing adds 1 m per side");
                Assert.That(primary[0].Width.ExtraMeters, Is.EqualTo(1f), "the border is evaluated by the shader");
                Assert.That(primary[1].FeatureIndex, Is.EqualTo(1), "fills ordered by rule order: road first");
                Assert.That(primary[1].Dash, Is.EqualTo(new float2(1f, 1f)), "dashes in core widths");
                Assert.That(primary[1].MinZoom, Is.EqualTo(0f), "the rule's min zoom");
                Assert.That(primary[2].FeatureIndex, Is.EqualTo(0));
                Assert.That(primary[3].Width.Evaluate(0.1f), Is.EqualTo(2f).Within(1e-3f), "overlay is half the rail width");
                Assert.That(primary[3].Dash, Is.EqualTo(new float2(4f, 4f)), "2 rail widths = 4 overlay widths");
            }
            finally
            {
                primary.Dispose();
                secondary.Dispose();
            }
        }

        [Test]
        public void LineItems_SecondaryModifierAndPolygonOutlines()
        {
            using var tile = new OmtTestTile("waterway")
                .Line(Segment, ("class", "river"))
                .Line(Segment, ("class", "river"), ("brunnel", "bridge"))
                .Polygon(Square, ("class", "river"))
                .Build();
            var rules = new List<OmtLineRule> { new OmtLineRule("river", "", Color.blue, 5f, 0f, 0) };
            var mods = new List<OmtModifier> { OmtModifier.Create("brunnel", "bridge", OmtModifierAction.Secondary) };

            var (primary, secondary) = RunLines(tile, new OmtLinePass { Rules = rules, Outlines = true }, mods);
            try
            {
                Assert.That(primary.Length, Is.EqualTo(2), "regular line + polygon outline");
                Assert.That(primary[1].Flags, Is.EqualTo(LineDrawFlags.PolygonOutline));
                Assert.That(secondary.Length, Is.EqualTo(1));
                Assert.That(secondary[0].FeatureIndex, Is.EqualTo(1));
            }
            finally
            {
                primary.Dispose();
                secondary.Dispose();
            }
        }

        [Test]
        public void LineItems_AreNotFilteredByTileZoom_AndCarryTheirMinZooms()
        {
            using var tile = new OmtTestTile("transportation")
                .Line(Segment, ("class", "path"))
                .Build();
            var rules = new List<OmtLineRule>
            {
                new OmtLineRule("path", "", Color.red, 0f, 1f, 0, 16).WithCasing(Color.white, 1f, 0f).WithCasingMinZoom(17),
            };

            // The job knows no tile zoom: a rule deeper than any source zoom is built and faded in by the shader.
            var (primary, secondary) = RunLines(tile, new OmtLinePass { Rules = rules }, new List<OmtModifier>());
            try
            {
                Assert.That(primary.Length, Is.EqualTo(2));
                Assert.That(primary[0].MinZoom, Is.EqualTo(17f), "the casing from its own min zoom");
                Assert.That(primary[1].MinZoom, Is.EqualTo(16f), "the fill from the rule's min zoom");
            }
            finally
            {
                primary.Dispose();
                secondary.Dispose();
            }
        }

        [Test]
        public void Modifier_SecondConditionAndMinZoomAction()
        {
            using var tile = new OmtTestTile("waterway")
                .Line(Segment, ("class", "river"), ("intermittent", 1))
                .Line(Segment, ("class", "stream"), ("intermittent", 1))
                .Line(Segment, ("class", "stream"), ("brunnel", "tunnel"))
                .Line(Segment, ("class", "stream"))
                .Build();
            var rules = new List<OmtLineRule> { new OmtLineRule("", "", Color.blue, 2f, 0f, 0, 13) };
            var other = OmtModifier.Create("intermittent", "", OmtModifierAction.Dash);
            other.Dash = new float2(4, 3);
            var river = OmtModifier.Create("intermittent", "", OmtModifierAction.Dash).And("class", "river");
            river.Dash = new float2(3, 2);
            var tunnel = OmtModifier.Create("brunnel", "tunnel", OmtModifierAction.MinZoom);
            tunnel.MinZoom = 14f;

            var (primary, secondary) = RunLines(tile, new OmtLinePass { Rules = rules },
                new List<OmtModifier> { other, river, tunnel });
            try
            {
                Assert.That(primary.Length, Is.EqualTo(4));
                Assert.That(primary[0].Dash, Is.EqualTo(new float2(3, 2)), "both conditions hold: the later modifier wins");
                Assert.That(primary[1].Dash, Is.EqualTo(new float2(4, 3)), "the second condition fails for streams");
                Assert.That(primary[2].MinZoom, Is.EqualTo(14f), "the tunnel's minimum zoom is raised");
                Assert.That(primary[3].MinZoom, Is.EqualTo(13f));
                Assert.That(primary[3].Dash, Is.EqualTo(float2.zero));
            }
            finally
            {
                primary.Dispose();
                secondary.Dispose();
            }
        }

        [Test]
        public void Modifier_NegatedValue_MatchesPresentDifferentValuesOnly()
        {
            using var tile = new OmtTestTile("boundary")
                .Line(Segment, ("admin_level", 2))
                .Line(Segment, ("admin_level", 2), ("claimed_by", "IN"))
                .Line(Segment, ("admin_level", 2), ("claimed_by", "PK"))
                .Build();
            var rules = new List<OmtLineRule> { new OmtLineRule("2", "", Color.gray, 0f, 1f, 0) };
            var mod = OmtModifier.Create("claimed_by", "IN", OmtModifierAction.Skip);
            mod.Negate = true;

            var (primary, secondary) = RunLines(tile, new OmtLinePass { Rules = rules, ClassKey = "admin_level" },
                new List<OmtModifier> { mod });
            try
            {
                Assert.That(primary.Length, Is.EqualTo(2), "numeric admin_level matches rule \"2\"; PK claim skipped");
                Assert.That(primary[0].FeatureIndex, Is.EqualTo(0));
                Assert.That(primary[1].FeatureIndex, Is.EqualTo(1));
            }
            finally
            {
                primary.Dispose();
                secondary.Dispose();
            }
        }

        [Test]
        public void Transportation_DrawsTunnelsThenGroundThenBridges_AndMapsDesignatedPaths()
        {
            using var tile = new OmtTestTile("transportation")
                .Line(Segment, ("class", "primary"), ("brunnel", "bridge"))
                .Line(Segment, ("class", "primary"))
                .Line(Segment, ("class", "primary"), ("brunnel", "tunnel"))
                .Line(Segment, ("class", "path"), ("subclass", "footway"), ("bicycle", "designated"))
                .Line(Segment, ("class", "primary"), ("ramp", 1))
                .Build();
            var rules = new List<OmtLineRule>
            {
                new OmtLineRule("primary", "", Color.yellow, 10f, 0f, 2).WithCasing(Color.black, 0f, 1f),
                new OmtLineRule("path", "cycleway", Color.blue, 2f, 0f, 1),
                new OmtLineRule("path", "", Color.red, 2f, 0f, 1),
            };

            var nativeRules = OmtRules.ToNative(rules, Allocator.TempJob);
            var output = new NativeList<LineDrawItem>(16, Allocator.TempJob);
            try
            {
                var layer = tile.GetLayer(0);
                new OmtTransportationItemsJob
                {
                    GeomTypes = layer.FeatureGeomTypesSlice,
                    Props = OmtProps.From(layer),
                    Rules = nativeRules,
                    MercatorScale = 1f,
                    Tunnels = OmtTunnelMode.Dim,
                    BridgeCasing = new Color32(0, 0, 0, 255),
                    White = new Color32(255, 255, 255, 255),
                    RampWidth = 0.5f,
                    MinorServiceWidth = 1f,
                    RailServiceOpacity = 1f,
                    Output = output,
                }.Run();

                var black = new Color32(0, 0, 0, 255);
                var fills = new List<int>();
                foreach (var item in output)
                    if (!item.Color.Equals(black)) fills.Add(item.FeatureIndex);
                Assert.That(fills, Is.EqualTo(new[] { 2, 3, 1, 4, 0 }),
                    "tunnel, then ground (path before primaries by order, then feature index), then bridge");

                bool cyclewayColor = false;
                foreach (var item in output)
                    if (item.FeatureIndex == 3) cyclewayColor = item.Color.Equals(OmtColor.ToVertex(Color.blue));
                Assert.That(cyclewayColor, Is.True, "bicycle=designated path uses the cycleway rule");

                foreach (var item in output)
                    if (item.FeatureIndex == 4 && item.Color.Equals(OmtColor.ToVertex(Color.yellow)))
                        Assert.That(item.Width.Evaluate(0.01f), Is.EqualTo(5f).Within(1e-3f), "ramp halves the width");
            }
            finally
            {
                nativeRules.Dispose();
                output.Dispose();
            }
        }

        [Test]
        public void Transportation_LinksRailServiceBridgeAndTunnelCasings()
        {
            using var tile = new OmtTestTile("transportation")
                .Line(Segment, ("class", "primary"), ("ramp", 1))
                .Line(Segment, ("class", "motorway"), ("ramp", 1))
                .Line(Segment, ("class", "rail"), ("service", "yard"))
                .Line(Segment, ("class", "trunk"), ("brunnel", "bridge"))
                .Line(Segment, ("class", "primary"), ("brunnel", "bridge"))
                .Line(Segment, ("class", "primary"), ("brunnel", "tunnel"))
                .Build();
            var trunkCasing = new Color(1f, 0f, 0f);
            var rules = new List<OmtLineRule>
            {
                new OmtLineRule("primary", "", Color.yellow, 10f, 0f, 2, 8).WithCasing(Color.gray, 0f, 1f),
                new OmtLineRule("motorway", "", Color.magenta, 10f, 0f, 3, 5).WithCasing(Color.gray, 0f, 1f),
                new OmtLineRule("trunk", "", Color.cyan, 10f, 0f, 1, 5).WithCasing(trunkCasing, 0f, 1f)
                    .WithBridgeCasing(trunkCasing),
                new OmtLineRule("rail", "", Color.gray, 3f, 0f, 0, 8),
            };

            var nativeRules = OmtRules.ToNative(rules, Allocator.TempJob);
            var output = new NativeList<LineDrawItem>(16, Allocator.TempJob);
            try
            {
                var layer = tile.GetLayer(0);
                var black = new Color32(0, 0, 0, 255);
                new OmtTransportationItemsJob
                {
                    GeomTypes = layer.FeatureGeomTypesSlice,
                    Props = OmtProps.From(layer),
                    Rules = nativeRules,
                    MercatorScale = 1f,
                    Tunnels = OmtTunnelMode.Dim,
                    BridgeCasing = black,
                    White = new Color32(255, 255, 255, 255),
                    RampWidth = 0.75f,
                    LinkMinZoom = 11f,
                    RailServiceOpacity = 0.4f,
                    MinorServiceWidth = 1f,
                    Output = output,
                }.Run();

                LineDrawItem Fill(int fi)
                {
                    foreach (var item in output)
                        if (item.FeatureIndex == fi && item.Width.ExtraMeters == 0f) return item;
                    throw new AssertionException($"no fill of feature {fi}");
                }
                LineDrawItem Casing(int fi)
                {
                    foreach (var item in output)
                        if (item.FeatureIndex == fi && item.Width.ExtraMeters > 0f) return item;
                    throw new AssertionException($"no casing of feature {fi}");
                }

                Assert.That(Fill(0).MinZoom, Is.EqualTo(11f), "links other than motorway links from the link zoom");
                Assert.That(Fill(0).Width.Meters, Is.EqualTo(7.5f).Within(1e-4f));
                Assert.That(Fill(1).MinZoom, Is.EqualTo(5f), "motorway links keep the road's zoom");
                Assert.That(Fill(2).Color.a, Is.EqualTo(102), "rail service tracks at 40 % opacity");
                Assert.That(Casing(3).Color, Is.EqualTo(OmtColor.ToVertex(trunkCasing)), "the rule's bridge casing");
                Assert.That(Casing(4).Color, Is.EqualTo(black), "the handler's bridge casing");
                Assert.That(Casing(5).Dash, Is.EqualTo(new float2(0.5f, 0.25f)), "dashed tunnel casing");
            }
            finally
            {
                nativeRules.Dispose();
                output.Dispose();
            }
        }

        // ---- helpers --------------------------------------------------------

        private static NativeList<SurfaceDrawItem> RunFill(
            GeoFoundry.Core.Mvt.NativeMvtTile tile, List<OmtFillRule> rules, string classKey, string subKey, int zoom, List<OmtModifier> mods)
        {
            var layer = tile.GetLayer(0);
            var nativeRules = OmtRules.ToNative(rules, Allocator.TempJob);
            var nativeMods = OmtModifiers.ToNative(mods, Allocator.TempJob);
            var items = new NativeList<SurfaceDrawItem>(8, Allocator.TempJob);
            new OmtFillItemsJob
            {
                GeomTypes = layer.FeatureGeomTypesSlice,
                Props = OmtProps.From(layer),
                Rules = nativeRules,
                Modifiers = nativeMods,
                ClassKey = OmtRules.Fixed(classKey),
                SubclassKey = OmtRules.Fixed(subKey),
                Zoom = zoom,
                Output = items,
            }.Run();
            nativeRules.Dispose();
            nativeMods.Dispose();
            return items;
        }

        private static (NativeList<LineDrawItem>, NativeList<LineDrawItem>) RunLines(
            GeoFoundry.Core.Mvt.NativeMvtTile tile, OmtLinePass pass, List<OmtModifier> mods)
        {
            var layer = tile.GetLayer(0);
            var nativeRules = OmtRules.ToNative(pass.Rules, Allocator.TempJob);
            var nativeMods = OmtModifiers.ToNative(mods, Allocator.TempJob);
            var primary = new NativeList<LineDrawItem>(8, Allocator.TempJob);
            var secondary = new NativeList<LineDrawItem>(8, Allocator.TempJob);
            new OmtLineItemsJob
            {
                GeomTypes = layer.FeatureGeomTypesSlice,
                Props = OmtProps.From(layer),
                Rules = nativeRules,
                Modifiers = nativeMods,
                ClassKey = OmtRules.Fixed(pass.ClassKey),
                SubclassKey = OmtRules.Fixed(pass.SubclassKey),
                MercatorScale = 1f,
                IncludeLines = pass.Lines,
                OutlinePolygons = pass.Outlines,
                Primary = primary,
                Secondary = secondary,
            }.Run();
            nativeRules.Dispose();
            nativeMods.Dispose();
            return (primary, secondary);
        }
    }
}
