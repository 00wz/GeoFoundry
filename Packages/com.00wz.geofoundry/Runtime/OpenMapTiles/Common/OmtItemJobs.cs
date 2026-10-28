using System.Collections.Generic;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>What an <see cref="OmtModifier"/> does to a matching feature.</summary>
    internal enum OmtModifierAction : byte
    {
        /// <summary>The feature is not drawn.</summary>
        Skip,
        /// <summary>The fill color is replaced by <see cref="OmtModifier.Color"/>.</summary>
        Recolor,
        /// <summary>The dash pattern is replaced by <see cref="OmtModifier.Dash"/> (multiples of the width).</summary>
        Dash,
        /// <summary>The width is multiplied by <see cref="OmtModifier.Scale"/>.</summary>
        WidthScale,
        /// <summary>The casing is not drawn.</summary>
        NoCasing,
        /// <summary>The feature goes to the secondary output (e.g. a bridge mesh with its own draw order).</summary>
        Secondary,
        /// <summary>The fill color is mixed toward <see cref="OmtModifier.Color"/> by <see cref="OmtModifier.Scale"/>.</summary>
        Tint,
        /// <summary>
        /// The minimum zoom is raised to <see cref="OmtModifier.MinZoom"/>: the display zoom from which a line is drawn
        /// (all its items), or the tile zoom from which a polygon is filled.
        /// </summary>
        MinZoom,
    }

    /// <summary>
    /// Attribute-based adjustment applied on top of the matched rule: when the feature's <see cref="Key"/> equals
    /// <see cref="Value"/> (or is truthy if <see cref="Value"/> is empty), <see cref="Action"/> is applied. With
    /// <see cref="Negate"/> the condition becomes "the key is present and differs from <see cref="Value"/>" (or, with
    /// an empty <see cref="Value"/>, "the key is absent or falsy"). An optional second condition (<see cref="Key2"/>,
    /// <see cref="Value2"/>, <see cref="Negate2"/>, same semantics, see <see cref="And"/>) must hold as well. Several
    /// modifiers may apply to one feature, in array order.
    /// </summary>
    internal struct OmtModifier
    {
        public FixedString64Bytes Key;
        public FixedString64Bytes Value;
        public bool Negate;
        public FixedString64Bytes Key2;
        public FixedString64Bytes Value2;
        public bool Negate2;
        public OmtModifierAction Action;
        public Color32 Color;
        public float2 Dash;
        public float Scale;
        public float MinZoom;

        public static OmtModifier Create(string key, string value, OmtModifierAction action) => new OmtModifier
        {
            Key = OmtRules.Fixed(key),
            Value = OmtRules.Fixed(value),
            Action = action,
            Scale = 1f,
        };

        /// <summary>Returns this modifier with a second condition that must hold as well.</summary>
        public OmtModifier And(string key, string value, bool negate = false)
        {
            Key2 = OmtRules.Fixed(key);
            Value2 = OmtRules.Fixed(value);
            Negate2 = negate;
            return this;
        }

        public bool Applies(ref OmtProps props, int fi) =>
            Test(ref props, fi, Key, Value, Negate) && (Key2.IsEmpty || Test(ref props, fi, Key2, Value2, Negate2));

        private static bool Test(ref OmtProps props, int fi, in FixedString64Bytes key, in FixedString64Bytes value,
            bool negate)
        {
            if (value.IsEmpty) return props.IsTrue(fi, key) != negate;
            if (!negate) return props.Is(fi, key, value);
            return props.TryGetNonEmpty(fi, key, out var text) && !text.Equals(value);
        }
    }

    internal static class OmtModifiers
    {
        public static NativeArray<OmtModifier> ToNative(List<OmtModifier> list, Allocator allocator)
        {
            var result = new NativeArray<OmtModifier>(list.Count, allocator);
            for (int i = 0; i < list.Count; i++) result[i] = list[i];
            return result;
        }

        /// <summary>Linear mix of two colors (Burst-compatible).</summary>
        public static Color32 Mix(Color32 a, Color32 b, float t)
        {
            t = math.saturate(t);
            return new Color32(
                (byte)math.round(math.lerp(a.r, b.r, t)),
                (byte)math.round(math.lerp(a.g, b.g, t)),
                (byte)math.round(math.lerp(a.b, b.b, t)),
                (byte)math.round(math.lerp(a.a, b.a, t)));
        }
    }

    /// <summary>Sort key of a draw entry: the rule's order, then the feature index (stable, deterministic).</summary>
    internal struct OmtOrderComparer : IComparer<int4>
    {
        // x = order, y = feature index, z/w = payload
        public int Compare(int4 a, int4 b)
        {
            int c = a.x.CompareTo(b.x);
            return c != 0 ? c : a.y.CompareTo(b.y);
        }
    }

    /// <summary>
    /// Builds the ordered <see cref="SurfaceDrawItem"/> list of a polygon layer: matches every Polygon feature
    /// against the fill rules active at the tile zoom (see <see cref="OmtFillRule"/>) by
    /// <see cref="ClassKey"/>/<see cref="SubclassKey"/>, applies the modifiers, and sorts the result by rule order.
    /// The surface shader has no zoom fade, so fills are filtered by the tile zoom.
    /// </summary>
    [BurstCompile]
    internal struct OmtFillItemsJob : IJob
    {
        [ReadOnly] public NativeArray<MvtGeomType> GeomTypes;
        public OmtProps Props;
        [ReadOnly] public NativeArray<OmtFillRuleData> Rules;
        [ReadOnly] public NativeArray<OmtModifier> Modifiers;
        public FixedString64Bytes ClassKey;
        public FixedString64Bytes SubclassKey;
        public int Zoom;

        public NativeList<SurfaceDrawItem> Output;

        public void Execute()
        {
            var entries = new NativeList<int4>(GeomTypes.Length, Allocator.Temp);
            var colors = new NativeList<Color32>(GeomTypes.Length, Allocator.Temp);

            for (int fi = 0; fi < GeomTypes.Length; fi++)
            {
                if (GeomTypes[fi] != MvtGeomType.Polygon) continue;

                FixedString128Bytes cls = default, sub = default;
                bool hasClass = !ClassKey.IsEmpty && Props.TryGetText(fi, ClassKey, out cls);
                bool hasSub = !SubclassKey.IsEmpty && Props.TryGetText(fi, SubclassKey, out sub);
                int ri = OmtRules.Match(Rules, cls, hasClass, sub, hasSub, Zoom);
                if (ri < 0) continue;
                var rule = Rules[ri];
                if (!rule.Visible) continue;

                Color32 color = rule.Color;
                bool skip = false;
                for (int m = 0; m < Modifiers.Length && !skip; m++)
                {
                    var mod = Modifiers[m];
                    if (!mod.Applies(ref Props, fi)) continue;
                    switch (mod.Action)
                    {
                        case OmtModifierAction.Skip: skip = true; break;
                        case OmtModifierAction.Recolor: color = mod.Color; break;
                        case OmtModifierAction.Tint: color = OmtModifiers.Mix(color, mod.Color, mod.Scale); break;
                        case OmtModifierAction.MinZoom: skip = Zoom < mod.MinZoom; break;
                    }
                }
                if (skip) continue;

                entries.Add(new int4(rule.Order, fi, colors.Length, 0));
                colors.Add(color);
            }

            entries.Sort(new OmtOrderComparer());
            for (int i = 0; i < entries.Length; i++)
                Output.Add(new SurfaceDrawItem(entries[i].y, colors[entries[i].z]));

            entries.Dispose();
            colors.Dispose();
        }
    }

    /// <summary>
    /// Builds the ordered <see cref="LineDrawItem"/> lists of a line layer: matches LineString features (and, with
    /// <see cref="OutlinePolygons"/>, Polygon outlines) against the line rules, applies the modifiers, sorts by rule
    /// order and emits three passes per output: all casings, all fills, all overlays. Features with the
    /// <see cref="OmtModifierAction.Secondary"/> modifier go to <see cref="Secondary"/>. Lines are not filtered by
    /// the tile zoom: every item carries its minimum display zoom, by which the line shader hides and fades it, so a
    /// rule deeper than the source's maximum zoom still shows on that source's tiles viewed up close.
    /// </summary>
    [BurstCompile]
    internal struct OmtLineItemsJob : IJob
    {
        [ReadOnly] public NativeArray<MvtGeomType> GeomTypes;
        public OmtProps Props;
        [ReadOnly] public NativeArray<OmtLineRuleData> Rules;
        [ReadOnly] public NativeArray<OmtModifier> Modifiers;
        public FixedString64Bytes ClassKey;
        public FixedString64Bytes SubclassKey;
        public float MercatorScale;
        public bool IncludeLines;
        public bool OutlinePolygons;

        public NativeList<LineDrawItem> Primary;
        public NativeList<LineDrawItem> Secondary;

        private struct Resolved
        {
            public int Fi;
            public int Rule;
            public Color32 Color;
            public LineWidth Width;
            public float2 Dash;
            public bool Casing;
            public bool Outline;
            public float MinZoom;
        }

        public void Execute()
        {
            var primary = new NativeList<int4>(GeomTypes.Length, Allocator.Temp);
            var secondary = new NativeList<int4>(16, Allocator.Temp);
            var resolved = new NativeList<Resolved>(GeomTypes.Length, Allocator.Temp);

            for (int fi = 0; fi < GeomTypes.Length; fi++)
            {
                var geom = GeomTypes[fi];
                bool outline = geom == MvtGeomType.Polygon;
                if (outline ? !OutlinePolygons : (geom != MvtGeomType.LineString || !IncludeLines)) continue;

                FixedString128Bytes cls = default, sub = default;
                bool hasClass = !ClassKey.IsEmpty && Props.TryGetText(fi, ClassKey, out cls);
                bool hasSub = !SubclassKey.IsEmpty && Props.TryGetText(fi, SubclassKey, out sub);
                int ri = OmtRules.Match(Rules, cls, hasClass, sub, hasSub);
                if (ri < 0) continue;
                var rule = Rules[ri];
                if (!rule.Visible) continue;

                var r = new Resolved
                {
                    Fi = fi,
                    Rule = ri,
                    Color = rule.Color,
                    Width = OmtZoom.LineWidth(rule.WidthMeters, rule.MinWidthPixels, MercatorScale),
                    Dash = rule.Dash,
                    Casing = rule.HasCasing,
                    Outline = outline,
                    MinZoom = rule.MinZoom,
                };
                bool skip = false, toSecondary = false;
                for (int m = 0; m < Modifiers.Length && !skip; m++)
                {
                    var mod = Modifiers[m];
                    if (!mod.Applies(ref Props, fi)) continue;
                    switch (mod.Action)
                    {
                        case OmtModifierAction.Skip: skip = true; break;
                        case OmtModifierAction.Recolor: r.Color = mod.Color; break;
                        case OmtModifierAction.Tint: r.Color = OmtModifiers.Mix(r.Color, mod.Color, mod.Scale); break;
                        case OmtModifierAction.Dash: r.Dash = mod.Dash; break;
                        case OmtModifierAction.WidthScale: r.Width = r.Width.ScaleCore(mod.Scale); break;
                        case OmtModifierAction.NoCasing: r.Casing = false; break;
                        case OmtModifierAction.Secondary: toSecondary = true; break;
                        case OmtModifierAction.MinZoom: r.MinZoom = math.max(r.MinZoom, mod.MinZoom); break;
                    }
                }
                if (skip || (r.Width.Meters <= 0f && r.Width.Pixels <= 0f)) continue;

                var key = new int4(rule.Order, fi, resolved.Length, 0);
                resolved.Add(r);
                if (toSecondary) secondary.Add(key);
                else primary.Add(key);
            }

            var comparer = new OmtOrderComparer();
            primary.Sort(comparer);
            secondary.Sort(comparer);
            Emit(primary, resolved, ref Primary);
            Emit(secondary, resolved, ref Secondary);

            primary.Dispose();
            secondary.Dispose();
            resolved.Dispose();
        }

        private void Emit(NativeList<int4> keys, NativeList<Resolved> resolved, ref NativeList<LineDrawItem> output)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    var r = resolved[keys[i].z];
                    var rule = Rules[r.Rule];
                    var flags = r.Outline ? LineDrawFlags.PolygonOutline : LineDrawFlags.None;
                    switch (pass)
                    {
                        case 0:
                            if (!r.Casing) break;
                            output.Add(new LineDrawItem(r.Fi,
                                r.Width.WithBorder(rule.CasingMeters * MercatorScale, rule.CasingPixels),
                                rule.CasingColor, default, flags, math.max(r.MinZoom, rule.CasingMinZoom)));
                            break;
                        case 1:
                            output.Add(new LineDrawItem(r.Fi, r.Width, r.Color, r.Dash, flags, r.MinZoom));
                            break;
                        case 2:
                            if (!rule.HasOverlay || rule.OverlayWidth <= 0f) break;
                            // The overlay dash is relative to the fill width; the item's dash to its own core.
                            output.Add(new LineDrawItem(r.Fi, r.Width.ScaleCore(rule.OverlayWidth), rule.OverlayColor,
                                rule.OverlayDash / rule.OverlayWidth, flags, r.MinZoom));
                            break;
                    }
                }
            }
        }
    }
}
