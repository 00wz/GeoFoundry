using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Fill style of the polygons matching <see cref="Class"/>/<see cref="Subclass"/> (empty matches any value).
    /// Rules are matched in list order and the first match wins, so put specific rules (with a subclass) before
    /// generic ones.
    /// </summary>
    [Serializable]
    public sealed class OmtFillRule
    {
        [Tooltip("Value of the class attribute; empty matches any class.")]
        public string Class = "";

        [Tooltip("Value of the subclass attribute; empty matches any subclass.")]
        public string Subclass = "";

        [Tooltip("Fill color (sRGB); alpha is the fill opacity (the layers below show through).")]
        public Color Color = Color.white;

        [Tooltip("Minimum tile zoom at which the polygons are drawn.")]
        [Range(0, 22)] public int MinZoom;

        [Tooltip("Draw order inside the layer: lower values are drawn first and covered by higher ones.")]
        public int Order;

        [Tooltip("Whether matching polygons are drawn. A hidden rule still matches and suppresses later rules.")]
        public bool Visible = true;

        public OmtFillRule() { }

        public OmtFillRule(string cls, string subclass, Color color, int order, int minZoom = 0)
        {
            Class = cls ?? "";
            Subclass = subclass ?? "";
            Color = color;
            Order = order;
            MinZoom = minZoom;
        }
    }

    /// <summary>
    /// Line style of the features matching <see cref="Class"/>/<see cref="Subclass"/>: a fill ribbon with an optional
    /// casing below and an optional dashed overlay above (rail hatching, construction dashes). Widths combine a real
    /// size with a minimum on-screen size, see <see cref="OmtZoom.LineWidth"/>. First match wins.
    /// </summary>
    [Serializable]
    public sealed class OmtLineRule
    {
        [Tooltip("Value of the class attribute (numbers are compared as text, e.g. admin_level \"2\"); empty matches any.")]
        public string Class = "";

        [Tooltip("Value of the subclass attribute; empty matches any.")]
        public string Subclass = "";

        [Tooltip("Fill color (sRGB); alpha is the line opacity.")]
        public Color Color = Color.white;

        [Tooltip("Real width of the line in ground meters.")]
        [Min(0)] public float WidthMeters = 4f;

        [Tooltip("Minimum width in screen pixels at the camera's zoom, so the line stays visible from afar.")]
        [Min(0)] public float MinWidthPixels = 1f;

        [Tooltip("Dash pattern in multiples of the line width (dash, gap); zero means a solid line.")]
        public Vector2 Dash;

        [Tooltip("Casing (outline) color drawn below all fills; alpha 0 disables the casing.")]
        public Color CasingColor = new Color(0, 0, 0, 0);

        [Tooltip("Minimum casing width added on each side, in screen pixels at the camera's zoom.")]
        [Min(0)] public float CasingPixels = 0.75f;

        [Tooltip("Minimum casing width added on each side, in ground meters.")]
        [Min(0)] public float CasingMeters = 0.75f;

        [Tooltip("Overlay color drawn above the fill (e.g. white rail hatching); alpha 0 disables the overlay.")]
        public Color OverlayColor = new Color(0, 0, 0, 0);

        [Tooltip("Overlay width as a fraction of the line width (above 1 the overlay is wider than the line).")]
        [Range(0, 2)] public float OverlayWidth = 0.5f;

        [Tooltip("Overlay dash pattern in multiples of the line width (dash, gap); zero means solid.")]
        public Vector2 OverlayDash;

        [Tooltip("Minimum zoom at which the line is drawn: tiles of lower zoom skip it, and it fades in as the camera zoom reaches it.")]
        [Range(0, 22)] public int MinZoom;

        [Tooltip("Draw order inside the layer: lower values are drawn first.")]
        public int Order;

        [Tooltip("Whether matching lines are drawn. A hidden rule still matches and suppresses later rules.")]
        public bool Visible = true;

        public OmtLineRule() { }

        public OmtLineRule(string cls, string subclass, Color color, float widthMeters, float minWidthPixels, int order,
            int minZoom = 0)
        {
            Class = cls ?? "";
            Subclass = subclass ?? "";
            Color = color;
            WidthMeters = widthMeters;
            MinWidthPixels = minWidthPixels;
            Order = order;
            MinZoom = minZoom;
        }

        public OmtLineRule WithCasing(Color color, float pixels = 0.75f, float meters = 0.75f)
        {
            CasingColor = color;
            CasingPixels = pixels;
            CasingMeters = meters;
            return this;
        }

        public OmtLineRule WithDash(float dash, float gap)
        {
            Dash = new Vector2(dash, gap);
            return this;
        }

        public OmtLineRule WithOverlay(Color color, float widthFraction, float dash = 0f, float gap = 0f)
        {
            OverlayColor = color;
            OverlayWidth = widthFraction;
            OverlayDash = new Vector2(dash, gap);
            return this;
        }
    }

    /// <summary>Blittable form of <see cref="OmtFillRule"/> for jobs.</summary>
    internal struct OmtFillRuleData
    {
        public FixedString64Bytes Class;
        public FixedString64Bytes Subclass;
        public Color32 Color;
        public int MinZoom;
        public int Order;
        public bool Visible;
    }

    /// <summary>Blittable form of <see cref="OmtLineRule"/> for jobs; colors are vertex colors.</summary>
    internal struct OmtLineRuleData
    {
        public FixedString64Bytes Class;
        public FixedString64Bytes Subclass;
        public Color32 Color;
        public float WidthMeters;
        public float MinWidthPixels;
        public float2 Dash;
        public Color32 CasingColor;
        public bool HasCasing;
        public float CasingPixels;
        public float CasingMeters;
        public Color32 OverlayColor;
        public bool HasOverlay;
        public float OverlayWidth;
        public float2 OverlayDash;
        public int MinZoom;
        public int Order;
        public bool Visible;
    }

    /// <summary>Conversion of the inspector rules to job data and Burst-compatible matching.</summary>
    internal static class OmtRules
    {
        public static NativeArray<OmtFillRuleData> ToNative(IReadOnlyList<OmtFillRule> rules, Allocator allocator)
        {
            int n = rules?.Count ?? 0;
            var result = new NativeArray<OmtFillRuleData>(n, allocator);
            for (int i = 0; i < n; i++)
            {
                var r = rules[i];
                if (r == null) continue;
                result[i] = new OmtFillRuleData
                {
                    Class = Fixed(r.Class),
                    Subclass = Fixed(r.Subclass),
                    Color = OmtColor.ToVertex(r.Color),
                    MinZoom = r.MinZoom,
                    Order = r.Order,
                    Visible = r.Visible,
                };
            }
            return result;
        }

        public static NativeArray<OmtLineRuleData> ToNative(IReadOnlyList<OmtLineRule> rules, Allocator allocator)
        {
            int n = rules?.Count ?? 0;
            var result = new NativeArray<OmtLineRuleData>(n, allocator);
            for (int i = 0; i < n; i++)
            {
                var r = rules[i];
                if (r == null) continue;
                result[i] = new OmtLineRuleData
                {
                    Class = Fixed(r.Class),
                    Subclass = Fixed(r.Subclass),
                    Color = OmtColor.ToVertex(r.Color),
                    WidthMeters = r.WidthMeters,
                    MinWidthPixels = r.MinWidthPixels,
                    Dash = new float2(r.Dash.x, r.Dash.y),
                    CasingColor = OmtColor.ToVertex(r.CasingColor),
                    HasCasing = r.CasingColor.a > 0f,
                    CasingPixels = r.CasingPixels,
                    CasingMeters = r.CasingMeters,
                    OverlayColor = OmtColor.ToVertex(r.OverlayColor),
                    HasOverlay = r.OverlayColor.a > 0f,
                    OverlayWidth = r.OverlayWidth,
                    OverlayDash = new float2(r.OverlayDash.x, r.OverlayDash.y),
                    MinZoom = r.MinZoom,
                    Order = r.Order,
                    Visible = r.Visible,
                };
            }
            return result;
        }

        /// <summary>Converts a managed string to a fixed string, truncating it if needed; null gives empty.</summary>
        public static FixedString64Bytes Fixed(string s)
        {
            FixedString64Bytes fs = default;
            if (!string.IsNullOrEmpty(s)) fs.CopyFromTruncated(s);
            return fs;
        }

        /// <summary>Creates a native array of fixed strings (e.g. a name key preference list).</summary>
        public static NativeArray<FixedString64Bytes> ToNative(IReadOnlyList<string> strings, Allocator allocator)
        {
            int n = strings?.Count ?? 0;
            var result = new NativeArray<FixedString64Bytes>(n, allocator);
            for (int i = 0; i < n; i++) result[i] = Fixed(strings[i]);
            return result;
        }

        public static bool Matches(in FixedString64Bytes rule, in FixedString128Bytes value, bool hasValue) =>
            rule.IsEmpty || (hasValue && value.Equals(rule));

        /// <summary>Returns the index of the first fill rule matching the class/subclass, or -1.</summary>
        public static int Match(NativeArray<OmtFillRuleData> rules,
            in FixedString128Bytes cls, bool hasClass, in FixedString128Bytes sub, bool hasSub)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                var r = rules[i];
                if (Matches(r.Class, cls, hasClass) && Matches(r.Subclass, sub, hasSub)) return i;
            }
            return -1;
        }

        /// <summary>Returns the index of the first line rule matching the class/subclass, or -1.</summary>
        public static int Match(NativeArray<OmtLineRuleData> rules,
            in FixedString128Bytes cls, bool hasClass, in FixedString128Bytes sub, bool hasSub)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                var r = rules[i];
                if (Matches(r.Class, cls, hasClass) && Matches(r.Subclass, sub, hasSub)) return i;
            }
            return -1;
        }
    }
}
