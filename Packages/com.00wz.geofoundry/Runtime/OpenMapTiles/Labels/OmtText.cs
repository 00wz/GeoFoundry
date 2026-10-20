using Unity.Collections;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>Burst-compatible text helpers shared by the label jobs.</summary>
    internal static class OmtText
    {
        /// <summary>Glyph map key: the font style in the high bits, the code point in the low 21 bits.</summary>
        public static int GlyphKey(OmtFontStyle style, int codepoint) => ((int)style << 21) | (codepoint & 0x1FFFFF);

        public static OmtFontStyle KeyStyle(int key) => (OmtFontStyle)(key >> 21);

        public static int KeyCodepoint(int key) => key & 0x1FFFFF;

        /// <summary>Applies the case transform to a code point (Latin, Latin-1, Greek and Cyrillic).</summary>
        public static int Transform(int cp, OmtTextTransform transform)
        {
            if (transform != OmtTextTransform.Uppercase) return cp;
            if (cp >= 'a' && cp <= 'z') return cp - 32;
            if (cp >= 0xE0 && cp <= 0xFE && cp != 0xF7) return cp - 32;      // Latin-1
            if (cp >= 0x3B1 && cp <= 0x3C9 && cp != 0x3C2) return cp - 32;   // Greek
            if (cp >= 0x430 && cp <= 0x44F) return cp - 32;                  // Cyrillic а-я
            if (cp >= 0x450 && cp <= 0x45F) return cp - 80;                  // Cyrillic ѐ-џ
            return cp;
        }

        /// <summary>Decodes UTF-8 text into transformed code points (appended to <paramref name="output"/>).</summary>
        public static void Decode(in FixedString128Bytes text, OmtTextTransform transform, ref NativeList<int> output)
        {
            var copy = text;
            int offset = 0;
            while (offset < copy.Length)
            {
                int before = offset;
                Unicode.Rune rune = copy.Read(ref offset);
                if (offset <= before) break;
                output.Add(Transform(rune.value, transform));
            }
        }

        /// <summary>Appends <paramref name="value"/> to <paramref name="text"/> without exceeding its capacity.</summary>
        public static void AppendSafe(ref FixedString128Bytes text, in FixedString128Bytes value)
        {
            if (text.Length + value.Length <= text.Capacity) text.Append(value);
        }
    }
}
