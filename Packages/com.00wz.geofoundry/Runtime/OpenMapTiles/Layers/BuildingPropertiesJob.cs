using GeoFoundry.Core.Mvt;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Interprets the OMT building layer schema (<c>hide_3d</c>, <c>render_height</c>, <c>render_min_height</c>,
    /// <c>colour</c>) on the Job System. Fully standalone: it knows nothing about geometry, polygons or terrain.
    /// <c>colour</c> is the raw OSM <c>building:colour</c> value (or a hex color derived from <c>building:material</c>):
    /// hex colors and CSS color names (<see cref="OmtNamedColours"/>) are accepted, and of a list (<c>beige;brown</c>)
    /// the first value is used.
    /// Reads properties directly from the raw slices (<see cref="NativeMvtLayer.Tags"/>,
    /// <see cref="NativeMvtLayer.Keys"/>, <see cref="NativeMvtLayer.Values"/>): the internal fields of
    /// <see cref="NativeMvtTile"/> are not accessible outside <c>GeoFoundry.Core</c>.
    ///
    /// Outputs are per-feature arrays (non-compacted, length = number of features in the layer). They are passed
    /// as plain input to <see cref="GeoFoundry.Geometry.AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/> (terrain-conforming), which
    /// uses <see cref="TopMeters"/> and <see cref="BottomMeters"/> as offsets above the terrain at every point rather
    /// than as a single height. This job only interprets the schema and does not choose the geometry strategy.
    /// </summary>
    [BurstCompile]
    internal struct BuildingPropertiesJob : IJob
    {
        /// <summary>Height used when <c>render_height</c> is missing.</summary>
        public float DefaultTopMeters;
        /// <summary>Base height used when <c>render_min_height</c> is missing.</summary>
        public float DefaultBottomMeters;
        /// <summary>Multiplier of both heights (exaggeration).</summary>
        public float HeightScale;
        /// <summary>Minimum height of a building above its base, so that flat parts stay visible.</summary>
        public float MinHeightMeters;
        /// <summary>Vertex color of buildings without <c>colour</c>; opaque white means no tint.</summary>
        public Color32 DefaultColor;
        /// <summary>Converts <c>colour</c> values from sRGB to linear (Linear color space projects).</summary>
        public bool LinearizeColours;
        /// <summary>Whether the <c>colour</c> attribute is read; otherwise every feature gets <see cref="DefaultColor"/>.</summary>
        public bool UseColour;

        [ReadOnly] public NativeArray<int2> FeatureTagRange;
        [ReadOnly] public NativeArray<int2> Tags;
        [ReadOnly] public NativeArray<FixedString64Bytes> Keys;
        [ReadOnly] public NativeArray<MvtValue> Values;
        /// <summary>CSS color names (lowercase) to sRGB colors; optional.</summary>
        [ReadOnly] public NativeHashMap<FixedString32Bytes, Color32> NamedColours;

        public NativeArray<bool> Hidden;
        public NativeArray<float> TopMeters;
        public NativeArray<float> BottomMeters;
        /// <summary>
        /// Per-feature color that the geometry job assigns to every vertex of the feature
        /// (<c>albedo *= vertexColor</c> in the shader). Opaque white (255,255,255,255) means no color override
        /// (<c>colour</c> is optional) and is the identity for the multiplication.
        /// </summary>
        public NativeArray<Color32> Colors;

        public void Execute()
        {
            FixedString64Bytes keyHide3d = "hide_3d";
            FixedString64Bytes keyRenderHeight = "render_height";
            FixedString64Bytes keyRenderMinHeight = "render_min_height";
            FixedString64Bytes keyColour = "colour";

            for (int fi = 0; fi < FeatureTagRange.Length; fi++)
            {
                int2 tagRange = FeatureTagRange[fi];

                // AsLong()/AsDouble() do not touch managed strings, unlike AsBool() for Kind.String, which calls
                // FixedString.ToString() and is not allowed in Burst.
                bool hidden = TryGetValue(tagRange, keyHide3d, out var hv) && hv.AsLong() != 0;
                float top = TryGetValue(tagRange, keyRenderHeight, out var tv) ? (float)tv.AsDouble() : DefaultTopMeters;
                float bottom = TryGetValue(tagRange, keyRenderMinHeight, out var bv) ? (float)bv.AsDouble() : DefaultBottomMeters;
                top *= HeightScale;
                bottom *= HeightScale;
                top = math.max(top, bottom + MinHeightMeters);

                Color32 color = DefaultColor;
                if (UseColour && TryGetValue(tagRange, keyColour, out var cv) &&
                    cv.TryGetRawString(out var raw) && TryParseColour(raw, out var rgba))
                {
                    color = LinearizeColours ? ToLinear(rgba) : rgba;
                }

                Hidden[fi] = hidden;
                TopMeters[fi] = top;
                BottomMeters[fi] = bottom;
                Colors[fi] = color;
            }
        }

        /// <summary>Linear scan over the feature's tag range; equivalent to the managed
        /// <see cref="NativeMvtPropertyBag.TryGetValue"/>, but on the raw slices.</summary>
        private bool TryGetValue(int2 tagRange, FixedString64Bytes key, out MvtValue value)
        {
            for (int i = 0; i < tagRange.y; i++)
            {
                int2 tag = Tags[tagRange.x + i];
                if (Keys[tag.x].Equals(key))
                {
                    value = Values[tag.y];
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static Color32 ToLinear(Color32 c) => new Color32(Lin(c.r), Lin(c.g), Lin(c.b), c.a);

        private static byte Lin(byte v)
        {
            float s = v / 255f;
            float l = s <= 0.04045f ? s / 12.92f : math.pow((s + 0.055f) / 1.055f, 2.4f);
            return (byte)math.round(math.saturate(l) * 255f);
        }

        /// <summary>
        /// Parses the first value of a <c>;</c>-separated list, trimmed: a hex color or a CSS color name (any case).
        /// </summary>
        private bool TryParseColour(in FixedString128Bytes raw, out Color32 rgba)
        {
            rgba = default;
            int start = 0, end = 0;
            while (end < raw.Length && raw[end] != ';') end++;
            while (start < end && raw[start] == ' ') start++;
            while (end > start && raw[end - 1] == ' ') end--;
            if (end == start) return false;

            FixedString128Bytes value = default;
            FixedString32Bytes name = default;
            bool fitsName = end - start <= name.Capacity;
            for (int i = start; i < end; i++)
            {
                byte c = raw[i];
                value.Add(c);
                if (fitsName) name.Add(c >= 'A' && c <= 'Z' ? (byte)(c + 32) : c);
            }
            if (TryParseHexColor(value, out rgba)) return true;
            return fitsName && NamedColours.IsCreated && NamedColours.TryGetValue(name, out rgba);
        }

        // ---- Hex color parser (#rgb, #rrggbb, #rrggbbaa; leading '#' optional) on FixedString128Bytes ----

        private static bool TryParseHexColor(FixedString128Bytes raw, out Color32 rgba)
        {
            rgba = default;
            int length = raw.Length;
            if (length == 0) return false;
            int start = raw[0] == '#' ? 1 : 0;
            int len = length - start;

            if (len == 3)
            {
                if (!TryHexDigit(raw[start], out int r) || !TryHexDigit(raw[start + 1], out int g) || !TryHexDigit(raw[start + 2], out int b)) return false;
                rgba = new Color32((byte)(r * 17), (byte)(g * 17), (byte)(b * 17), 255);
                return true;
            }
            if (len == 6)
            {
                if (!TryHexByte(raw, start, out byte r) || !TryHexByte(raw, start + 2, out byte g) || !TryHexByte(raw, start + 4, out byte b)) return false;
                rgba = new Color32(r, g, b, 255);
                return true;
            }
            if (len == 8)
            {
                if (!TryHexByte(raw, start, out byte r) || !TryHexByte(raw, start + 2, out byte g) || !TryHexByte(raw, start + 4, out byte b) || !TryHexByte(raw, start + 6, out byte a)) return false;
                rgba = new Color32(r, g, b, a);
                return true;
            }
            return false;
        }

        private static bool TryHexDigit(byte c, out int v)
        {
            if (c >= '0' && c <= '9') { v = c - '0'; return true; }
            if (c >= 'a' && c <= 'f') { v = 10 + c - 'a'; return true; }
            if (c >= 'A' && c <= 'F') { v = 10 + c - 'A'; return true; }
            v = 0; return false;
        }

        private static bool TryHexByte(FixedString128Bytes s, int offset, out byte v)
        {
            if (!TryHexDigit(s[offset], out int hi) || !TryHexDigit(s[offset + 1], out int lo)) { v = 0; return false; }
            v = (byte)((hi << 4) | lo);
            return true;
        }
    }

    /// <summary>The CSS named colors (CSS Color Module Level 4), accepted in the building <c>colour</c> attribute.</summary>
    internal static class OmtNamedColours
    {
        private static readonly (string Name, int Rgb)[] Table =
        {
            ("aliceblue", 0xf0f8ff), ("antiquewhite", 0xfaebd7), ("aqua", 0x00ffff), ("aquamarine", 0x7fffd4),
            ("azure", 0xf0ffff), ("beige", 0xf5f5dc), ("bisque", 0xffe4c4), ("black", 0x000000),
            ("blanchedalmond", 0xffebcd), ("blue", 0x0000ff), ("blueviolet", 0x8a2be2), ("brown", 0xa52a2a),
            ("burlywood", 0xdeb887), ("cadetblue", 0x5f9ea0), ("chartreuse", 0x7fff00), ("chocolate", 0xd2691e),
            ("coral", 0xff7f50), ("cornflowerblue", 0x6495ed), ("cornsilk", 0xfff8dc), ("crimson", 0xdc143c),
            ("cyan", 0x00ffff), ("darkblue", 0x00008b), ("darkcyan", 0x008b8b), ("darkgoldenrod", 0xb8860b),
            ("darkgray", 0xa9a9a9), ("darkgreen", 0x006400), ("darkgrey", 0xa9a9a9), ("darkkhaki", 0xbdb76b),
            ("darkmagenta", 0x8b008b), ("darkolivegreen", 0x556b2f), ("darkorange", 0xff8c00), ("darkorchid", 0x9932cc),
            ("darkred", 0x8b0000), ("darksalmon", 0xe9967a), ("darkseagreen", 0x8fbc8f), ("darkslateblue", 0x483d8b),
            ("darkslategray", 0x2f4f4f), ("darkslategrey", 0x2f4f4f), ("darkturquoise", 0x00ced1), ("darkviolet", 0x9400d3),
            ("deeppink", 0xff1493), ("deepskyblue", 0x00bfff), ("dimgray", 0x696969), ("dimgrey", 0x696969),
            ("dodgerblue", 0x1e90ff), ("firebrick", 0xb22222), ("floralwhite", 0xfffaf0), ("forestgreen", 0x228b22),
            ("fuchsia", 0xff00ff), ("gainsboro", 0xdcdcdc), ("ghostwhite", 0xf8f8ff), ("gold", 0xffd700),
            ("goldenrod", 0xdaa520), ("gray", 0x808080), ("green", 0x008000), ("greenyellow", 0xadff2f),
            ("grey", 0x808080), ("honeydew", 0xf0fff0), ("hotpink", 0xff69b4), ("indianred", 0xcd5c5c),
            ("indigo", 0x4b0082), ("ivory", 0xfffff0), ("khaki", 0xf0e68c), ("lavender", 0xe6e6fa),
            ("lavenderblush", 0xfff0f5), ("lawngreen", 0x7cfc00), ("lemonchiffon", 0xfffacd), ("lightblue", 0xadd8e6),
            ("lightcoral", 0xf08080), ("lightcyan", 0xe0ffff), ("lightgoldenrodyellow", 0xfafad2), ("lightgray", 0xd3d3d3),
            ("lightgreen", 0x90ee90), ("lightgrey", 0xd3d3d3), ("lightpink", 0xffb6c1), ("lightsalmon", 0xffa07a),
            ("lightseagreen", 0x20b2aa), ("lightskyblue", 0x87cefa), ("lightslategray", 0x778899), ("lightslategrey", 0x778899),
            ("lightsteelblue", 0xb0c4de), ("lightyellow", 0xffffe0), ("lime", 0x00ff00), ("limegreen", 0x32cd32),
            ("linen", 0xfaf0e6), ("magenta", 0xff00ff), ("maroon", 0x800000), ("mediumaquamarine", 0x66cdaa),
            ("mediumblue", 0x0000cd), ("mediumorchid", 0xba55d3), ("mediumpurple", 0x9370db), ("mediumseagreen", 0x3cb371),
            ("mediumslateblue", 0x7b68ee), ("mediumspringgreen", 0x00fa9a), ("mediumturquoise", 0x48d1cc),
            ("mediumvioletred", 0xc71585), ("midnightblue", 0x191970), ("mintcream", 0xf5fffa), ("mistyrose", 0xffe4e1),
            ("moccasin", 0xffe4b5), ("navajowhite", 0xffdead), ("navy", 0x000080), ("oldlace", 0xfdf5e6),
            ("olive", 0x808000), ("olivedrab", 0x6b8e23), ("orange", 0xffa500), ("orangered", 0xff4500),
            ("orchid", 0xda70d6), ("palegoldenrod", 0xeee8aa), ("palegreen", 0x98fb98), ("paleturquoise", 0xafeeee),
            ("palevioletred", 0xdb7093), ("papayawhip", 0xffefd5), ("peachpuff", 0xffdab9), ("peru", 0xcd853f),
            ("pink", 0xffc0cb), ("plum", 0xdda0dd), ("powderblue", 0xb0e0e6), ("purple", 0x800080),
            ("rebeccapurple", 0x663399), ("red", 0xff0000), ("rosybrown", 0xbc8f8f), ("royalblue", 0x4169e1),
            ("saddlebrown", 0x8b4513), ("salmon", 0xfa8072), ("sandybrown", 0xf4a460), ("seagreen", 0x2e8b57),
            ("seashell", 0xfff5ee), ("sienna", 0xa0522d), ("silver", 0xc0c0c0), ("skyblue", 0x87ceeb),
            ("slateblue", 0x6a5acd), ("slategray", 0x708090), ("slategrey", 0x708090), ("snow", 0xfffafa),
            ("springgreen", 0x00ff7f), ("steelblue", 0x4682b4), ("tan", 0xd2b48c), ("teal", 0x008080),
            ("thistle", 0xd8bfd8), ("tomato", 0xff6347), ("turquoise", 0x40e0d0), ("violet", 0xee82ee),
            ("wheat", 0xf5deb3), ("white", 0xffffff), ("whitesmoke", 0xf5f5f5), ("yellow", 0xffff00),
            ("yellowgreen", 0x9acd32),
        };

        /// <summary>Number of names in the table.</summary>
        public static int Count => Table.Length;

        /// <summary>Creates the name → color map for <see cref="BuildingPropertiesJob.NamedColours"/>; the caller disposes it.</summary>
        public static NativeHashMap<FixedString32Bytes, Color32> Create(Allocator allocator)
        {
            var map = new NativeHashMap<FixedString32Bytes, Color32>(Table.Length, allocator);
            foreach (var (name, rgb) in Table)
                map.TryAdd(name, new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255));
            return map;
        }
    }
}
