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
                Assert.That(items.Length, Is.EqualTo(1), "tunnel skipped, ocean below its min zoom, pond hidden");
                Assert.That(items[0].FeatureIndex, Is.EqualTo(0));
            }
            finally
            {
                items.Dispose();
            }
        }

        [Test]
        public void LineItems_EmitCasingsThenFillsThenOverlays_WithDashInMeters()
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
                Assert.That(primary[0].WidthMeters, Is.EqualTo(12f).Within(1e-3f), "casing adds 1 m per side");
                Assert.That(primary[1].FeatureIndex, Is.EqualTo(1), "fills ordered by rule order: road first");
                Assert.That(primary[1].Dash.x, Is.EqualTo(10f).Within(1e-3f), "dash 1 width = 10 m");
                Assert.That(primary[2].FeatureIndex, Is.EqualTo(0));
                Assert.That(primary[3].WidthMeters, Is.EqualTo(2f).Within(1e-3f), "overlay is half the rail width");
                Assert.That(primary[3].Dash, Is.EqualTo(new float2(8f, 8f)));
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
                    Zoom = 14,
                    MercatorScale = 1f,
                    MetersPerPixel = 1f,
                    Tunnels = OmtTunnelMode.Dim,
                    BridgeCasing = new Color32(0, 0, 0, 255),
                    White = new Color32(255, 255, 255, 255),
                    Black = new Color32(0, 0, 0, 255),
                    RampWidth = 0.5f,
                    MinorServiceWidth = 1f,
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
                        Assert.That(item.WidthMeters, Is.EqualTo(5f).Within(1e-3f), "ramp halves the width");
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
                Zoom = 14,
                MercatorScale = 1f,
                MetersPerPixel = 1f,
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
