using System;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>Geometry a label is placed on.</summary>
    internal enum OmtLabelKind : byte
    {
        /// <summary>At the feature's point(s); Point features only.</summary>
        Point = 0,
        /// <summary>Along the feature's line(s), rotated to follow the line on screen.</summary>
        Line = 1,
    }

    /// <summary>How the icon of a label is combined with its text.</summary>
    internal enum OmtIconMode : byte
    {
        None = 0,
        /// <summary>Icon at the anchor, text beside it per <see cref="OmtLabelRule.Anchor"/>.</summary>
        Marker = 1,
        /// <summary>Icon stretched behind the text with padding (road shields).</summary>
        Shield = 2,
    }

    [Flags]
    internal enum OmtLabelFlags : byte
    {
        None = 0,
        /// <summary>Placed without collision tests and without blocking other labels (oneway arrows).</summary>
        AllowOverlap = 1,
        /// <summary>Line labels keep the line direction on screen instead of flipping to stay upright.</summary>
        KeepDirection = 2,
        /// <summary>Line labels run against the line direction (oneway=-1 arrows).</summary>
        Reverse = 4,
    }

    /// <summary>
    /// One label to place, produced by a layer-specific request job and consumed by
    /// <see cref="OmtLabelLayoutJob"/>: what text/icon, on which feature, with which style rule and priority.
    /// </summary>
    internal struct OmtLabelRequest
    {
        public int FeatureIndex;
        public OmtLabelKind Kind;
        public OmtIconMode IconMode;
        public OmtLabelFlags Flags;
        /// <summary>Index of the style rule in the rule array passed to the layout job.</summary>
        public int Rule;
        /// <summary>Text in UTF-8; may be empty for icon-only labels. '\n' forces a line break.</summary>
        public FixedString128Bytes Text;
        /// <summary>Index into the icon info array, or -1.</summary>
        public int Icon;
        /// <summary>Placement priority: lower values are placed first (rank, class importance).</summary>
        public float Priority;
        /// <summary>Display zoom range; combined with the rule's range.</summary>
        public float MinZoom;
        public float MaxZoom;
        /// <summary>Distance between repeated line labels in screen pixels (line labels only).</summary>
        public float Spacing;
        /// <summary>Text color override (alpha 0 = use the rule's color), e.g. a route colour.</summary>
        public UnityEngine.Color32 ColorOverride;

        public static OmtLabelRequest Create(int fi, OmtLabelKind kind, int rule, in FixedString128Bytes text, float priority) =>
            new OmtLabelRequest
            {
                FeatureIndex = fi,
                Kind = kind,
                Rule = rule,
                Text = text,
                Icon = -1,
                Priority = priority,
                MinZoom = 0f,
                MaxZoom = 24f,
                Spacing = 250f,
            };
    }

    /// <summary>Pixel rectangle of an icon in the atlas, as UVs plus its logical size.</summary>
    internal struct OmtIconInfo
    {
        /// <summary>UV rectangle (u0, v0, u1, v1).</summary>
        public float4 Uv;
        /// <summary>Logical size in screen pixels at scale 1.</summary>
        public float2 Size;
    }

    /// <summary>Glyph data for layout: UVs, quad in ems relative to the pen position on the baseline, advance.</summary>
    internal struct OmtGlyphInfo
    {
        public float4 Uv;
        /// <summary>(x0, y0, x1, y1) in ems, y up from the baseline, including the SDF padding.</summary>
        public float4 Quad;
        public float Advance;
        /// <summary>Texture slot of the glyph's atlas (≥ 1; slot 0 is the icon atlas).</summary>
        public int Slot;
    }

    /// <summary>
    /// Interleaved float attributes of a label vertex (mesh stream 1): TexCoord0 = (u, v, mode, halo px),
    /// TexCoord1 = (offset px, min zoom, max zoom), TexCoord2 = (tangent, rotation mode).
    /// </summary>
    internal struct OmtLabelVertex
    {
        public float4 Uv0;
        public float4 Uv1;
        public float4 Uv2;
    }

    /// <summary>Interleaved colors of a label vertex (mesh stream 2): Color = text color, TexCoord3 = halo color.</summary>
    internal struct OmtLabelColors
    {
        public UnityEngine.Color32 Color;
        public UnityEngine.Color32 Halo;
    }

    /// <summary>Vertical metrics of a font face in ems.</summary>
    internal struct OmtFontMetrics
    {
        public float LineHeight;
        public float Ascent;
        public float Descent;
        public float SpaceAdvance;
    }
}
