using System;
using System.Globalization;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Parses CSS colors as written in the MapLibre style of OpenMapTiles (<c>hsl(…)</c>, <c>hsla(…)</c>,
    /// <c>rgb(…)</c>, <c>rgba(…)</c>, <c>#rgb</c>, <c>#rrggbb</c>), so default style tables can be copied from
    /// <c>layers/*/style.json</c> verbatim. Used when building defaults, not per tile.
    /// </summary>
    public static class OmtColor
    {
        /// <summary>Parses a CSS color string; throws <see cref="FormatException"/> on unsupported input.</summary>
        public static Color Parse(string css)
        {
            if (string.IsNullOrWhiteSpace(css)) throw new FormatException("Empty color.");
            string s = css.Trim().ToLowerInvariant();

            if (s[0] == '#')
            {
                if (ColorUtility.TryParseHtmlString(s, out var c)) return c;
                throw new FormatException($"Invalid hex color '{css}'.");
            }

            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open < 0 || close < open) throw new FormatException($"Invalid color '{css}'.");
            string fn = s.Substring(0, open).Trim();
            string[] parts = s.Substring(open + 1, close - open - 1).Split(',');

            float Num(int i) => float.Parse(parts[i].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture);
            float alpha = parts.Length > 3 ? Num(3) : 1f;

            switch (fn)
            {
                case "hsl":
                case "hsla":
                {
                    var c = HslToRgb(Num(0) / 360f, Num(1) / 100f, Num(2) / 100f);
                    c.a = alpha;
                    return c;
                }
                case "rgb":
                case "rgba":
                    return new Color(Num(0) / 255f, Num(1) / 255f, Num(2) / 255f, alpha);
                default:
                    throw new FormatException($"Unsupported color function '{fn}'.");
            }
        }

        /// <summary>Converts HSL (all components in [0,1]) to an sRGB color.</summary>
        public static Color HslToRgb(float h, float s, float l)
        {
            h -= Mathf.Floor(h);
            float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
            float p = 2f * l - q;
            return new Color(Hue(p, q, h + 1f / 3f), Hue(p, q, h), Hue(p, q, h - 1f / 3f), 1f);
        }

        private static float Hue(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }

        /// <summary>
        /// Converts an sRGB color (as authored in the inspector or the style) to a vertex color. Vertex colors are
        /// not color-space converted by Unity, so in a Linear color space project they are linearized here; the
        /// GeoFoundry shaders multiply albedo by the vertex color as is.
        /// </summary>
        public static Color32 ToVertex(Color color)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear;
            return color;
        }
    }
}
