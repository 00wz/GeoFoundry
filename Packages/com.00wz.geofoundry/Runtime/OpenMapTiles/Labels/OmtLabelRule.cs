using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>Font face of a label (see <see cref="OmtTextSettings"/>).</summary>
    public enum OmtFontStyle : byte
    {
        Regular = 0,
        Bold = 1,
        Italic = 2,
    }

    /// <summary>Case transform of a label text.</summary>
    public enum OmtTextTransform : byte
    {
        None = 0,
        Uppercase = 1,
    }

    /// <summary>Position of a point label's text relative to its anchor (and icon).</summary>
    public enum OmtTextAnchor : byte
    {
        /// <summary>Text centered on the anchor.</summary>
        Center = 0,
        /// <summary>Text below the anchor/icon (the text's top edge at the anchor).</summary>
        Top = 1,
        /// <summary>Text above the anchor/icon (the text's bottom edge at the anchor).</summary>
        Bottom = 2,
    }

    /// <summary>
    /// Label style of the features matching <see cref="Class"/>/<see cref="Subclass"/> (empty matches any value). Sizes
    /// are screen pixels; zooms are display zooms (the zoom the map is viewed at, which can exceed the tile zoom when
    /// the data's maximum zoom tiles are viewed up close). First match wins.
    /// </summary>
    [Serializable]
    public sealed class OmtLabelRule
    {
        [Tooltip("Value of the class attribute; empty matches any.")]
        public string Class = "";

        [Tooltip("Value of the subclass attribute; empty matches any.")]
        public string Subclass = "";

        public OmtFontStyle Font = OmtFontStyle.Regular;

        [Tooltip("Text size in screen pixels.")]
        [Min(1)] public float Size = 12f;

        public Color Color = Color.black;

        public Color HaloColor = new Color(1, 1, 1, 0.8f);

        [Tooltip("Halo width in screen pixels.")]
        [Range(0, 3)] public float HaloWidth = 1f;

        [Tooltip("Maximum line width in ems before wrapping; 0 disables wrapping.")]
        [Min(0)] public float MaxWidth = 8f;

        public OmtTextTransform Transform = OmtTextTransform.None;

        [Tooltip("Extra letter spacing in ems.")]
        public float LetterSpacing;

        public OmtTextAnchor Anchor = OmtTextAnchor.Center;

        [Tooltip("Gap between the icon and the text, in ems.")]
        [Min(0)] public float TextOffset = 0.2f;

        [Tooltip("Icon name in the icon atlas; {class} and {subclass} are replaced by the feature's values. Empty means no icon.")]
        public string Icon = "";

        [Tooltip("Icon size multiplier.")]
        [Min(0)] public float IconScale = 1f;

        [Tooltip("Minimum display zoom at which the label appears.")]
        [Range(0, 24)] public float MinZoom;

        [Tooltip("Display zoom above which the label disappears.")]
        [Range(0, 24)] public float MaxZoom = 24f;

        [Tooltip("Placement priority: lower values are placed first and win collisions.")]
        public int Priority;

        [Tooltip("Features with a higher rank (the layer's rank attribute) are not labeled; 0 labels all.")]
        [Min(0)] public int MaxRank;

        [Tooltip("Whether matching features are labeled. A hidden rule still matches and suppresses later rules.")]
        public bool Visible = true;

        public OmtLabelRule() { }

        public OmtLabelRule(string cls, string subclass, float size, Color color, float minZoom = 0f, int priority = 0)
        {
            Class = cls ?? "";
            Subclass = subclass ?? "";
            Size = size;
            Color = color;
            MinZoom = minZoom;
            Priority = priority;
        }

        public OmtLabelRule WithHalo(Color color, float width)
        {
            HaloColor = color;
            HaloWidth = width;
            return this;
        }

        public OmtLabelRule WithFont(OmtFontStyle font, OmtTextTransform transform = OmtTextTransform.None)
        {
            Font = font;
            Transform = transform;
            return this;
        }

        public OmtLabelRule WithIcon(string icon, OmtTextAnchor anchor = OmtTextAnchor.Top, float scale = 1f)
        {
            Icon = icon ?? "";
            Anchor = anchor;
            IconScale = scale;
            return this;
        }

        public OmtLabelRule WithWrap(float maxWidthEms)
        {
            MaxWidth = maxWidthEms;
            return this;
        }

        public OmtLabelRule WithMaxZoom(float maxZoom)
        {
            MaxZoom = maxZoom;
            return this;
        }

        public OmtLabelRule WithMaxRank(int maxRank)
        {
            MaxRank = maxRank;
            return this;
        }
    }

    /// <summary>Blittable form of <see cref="OmtLabelRule"/>.</summary>
    internal struct OmtLabelRuleData
    {
        public FixedString64Bytes Class;
        public FixedString64Bytes Subclass;
        public OmtFontStyle Font;
        public float Size;
        public Color32 Color;
        public Color32 HaloColor;
        public float HaloWidth;
        public float MaxWidth;
        public OmtTextTransform Transform;
        public float LetterSpacing;
        public OmtTextAnchor Anchor;
        public float TextOffset;
        public FixedString64Bytes Icon;
        public float IconScale;
        public float MinZoom;
        public float MaxZoom;
        public int Priority;
        public int MaxRank;
        public bool Visible;
    }

    internal static class OmtLabelRules
    {
        public static NativeArray<OmtLabelRuleData> ToNative(IReadOnlyList<OmtLabelRule> rules, float scale, Allocator allocator)
        {
            int n = rules?.Count ?? 0;
            var result = new NativeArray<OmtLabelRuleData>(n, allocator);
            for (int i = 0; i < n; i++)
            {
                var r = rules[i];
                if (r == null) continue;
                result[i] = new OmtLabelRuleData
                {
                    Class = OmtRules.Fixed(r.Class),
                    Subclass = OmtRules.Fixed(r.Subclass),
                    Font = r.Font,
                    Size = r.Size * scale,
                    Color = OmtColor.ToVertex(r.Color),
                    HaloColor = OmtColor.ToVertex(r.HaloColor),
                    HaloWidth = r.HaloWidth * scale,
                    MaxWidth = r.MaxWidth,
                    Transform = r.Transform,
                    LetterSpacing = r.LetterSpacing,
                    Anchor = r.Anchor,
                    TextOffset = r.TextOffset,
                    Icon = OmtRules.Fixed(r.Icon),
                    IconScale = r.IconScale * scale,
                    MinZoom = r.MinZoom,
                    MaxZoom = r.MaxZoom,
                    Priority = r.Priority,
                    MaxRank = r.MaxRank,
                    Visible = r.Visible,
                };
            }
            return result;
        }

        public static int Match(NativeArray<OmtLabelRuleData> rules,
            in FixedString128Bytes cls, bool hasClass, in FixedString128Bytes sub, bool hasSub)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                var r = rules[i];
                if (OmtRules.Matches(r.Class, cls, hasClass) && OmtRules.Matches(r.Subclass, sub, hasSub)) return i;
            }
            return -1;
        }
    }
}
