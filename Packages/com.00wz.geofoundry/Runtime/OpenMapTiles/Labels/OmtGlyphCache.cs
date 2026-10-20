using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using Glyph = UnityEngine.TextCore.Glyph;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Glyph source of the labels: runtime dynamic font assets of the engine's TextCore text module (SDF,
    /// multi-atlas) created from the source fonts of <see cref="OmtTextSettings"/>, plus a managed cache of glyph
    /// layout data. TextCore is used only as the font engine and atlas packer (it needs no imported resources, unlike
    /// TextMeshPro); the labels are rendered by the GeoFoundry label shader.
    /// </summary>
    /// <remarks>
    /// Main thread only. Jobs never read the cache: <see cref="Prepare"/> copies the glyphs a tile needs into a
    /// separate native map. Dynamic atlas data is never cleared, so UVs of already built tiles stay valid; atlases
    /// only grow (new atlas textures get new texture slots).
    /// </remarks>
    internal sealed class OmtGlyphCache
    {
        private readonly FontAsset[] _fonts = new FontAsset[3];
        private readonly Dictionary<int, OmtGlyphInfo> _glyphs = new Dictionary<int, OmtGlyphInfo>(512);
        private readonly HashSet<int> _missing = new HashSet<int>();
        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private readonly Dictionary<Texture2D, int> _slots = new Dictionary<Texture2D, int>();

        /// <summary>Gets the atlas textures by slot - 1 (slot 0 is the icon atlas).</summary>
        public IReadOnlyList<Texture2D> Textures => _textures;

        public OmtGlyphCache(Font regular, Font bold, Font italic, int samplingSize, int padding, int atlasSize)
        {
            _fonts[0] = Create(regular, samplingSize, padding, atlasSize);
            _fonts[1] = bold != null && bold != regular ? Create(bold, samplingSize, padding, atlasSize) : _fonts[0];
            _fonts[2] = italic != null && italic != regular ? Create(italic, samplingSize, padding, atlasSize) : _fonts[0];
        }

        public bool IsValid => _fonts[0] != null;

        private static FontAsset Create(Font font, int samplingSize, int padding, int atlasSize)
        {
            if (font == null) return null;
            var asset = FontAsset.CreateFontAsset(font, samplingSize, padding, GlyphRenderMode.SDFAA,
                atlasSize, atlasSize, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);
            if (asset != null) asset.name = $"{font.name} (GeoFoundry runtime SDF)";
            return asset;
        }

        /// <summary>Gets the vertical metrics of each font style, indexed by <see cref="OmtFontStyle"/>.</summary>
        public NativeArray<OmtFontMetrics> CreateMetrics(Allocator allocator)
        {
            var result = new NativeArray<OmtFontMetrics>(3, allocator);
            for (int i = 0; i < 3; i++)
            {
                var font = _fonts[i];
                if (font == null) continue;
                var face = font.faceInfo;
                float ps = math.max(1f, face.pointSize);
                float space = 0.25f;
                if (TryGetGlyph(font, ' ', out var g)) space = g.metrics.horizontalAdvance / ps;
                result[i] = new OmtFontMetrics
                {
                    LineHeight = face.lineHeight / ps,
                    Ascent = face.ascentLine / ps,
                    Descent = face.descentLine / ps,
                    SpaceAdvance = space,
                };
            }
            return result;
        }

        /// <summary>
        /// Ensures the glyphs of <paramref name="keys"/> (see <see cref="OmtText.GlyphKey"/>) are in the atlases and
        /// returns their layout data as a new native map owned by the caller. Glyphs missing from the font (and its
        /// fallbacks) are omitted; the layout skips them.
        /// </summary>
        public NativeHashMap<int, OmtGlyphInfo> Prepare(NativeHashSet<int> keys, Allocator allocator)
        {
            var result = new NativeHashMap<int, OmtGlyphInfo>(math.max(16, keys.Count), allocator);
            if (!IsValid) return result;

            // Batch the code points still unknown, per style, into one TryAddCharacters call each.
            var pending = new List<uint>[3];
            using (var it = keys.ToNativeArray(Allocator.Temp))
            {
                foreach (int key in it)
                {
                    if (_glyphs.ContainsKey(key) || _missing.Contains(key)) continue;
                    int style = (int)OmtText.KeyStyle(key);
                    (pending[style] ??= new List<uint>()).Add((uint)OmtText.KeyCodepoint(key));
                }

                for (int style = 0; style < 3; style++)
                {
                    if (pending[style] == null) continue;
                    var font = _fonts[style] ?? _fonts[0];
                    font.TryAddCharacters(pending[style].ToArray(), out _);
                    foreach (uint cp in pending[style])
                    {
                        int key = OmtText.GlyphKey((OmtFontStyle)style, (int)cp);
                        if (TryResolve(font, cp, out var info)) _glyphs[key] = info;
                        else _missing.Add(key);
                    }
                }

                foreach (int key in it)
                    if (_glyphs.TryGetValue(key, out var info)) result.TryAdd(key, info);
            }
            return result;
        }

        private bool TryResolve(FontAsset font, uint cp, out OmtGlyphInfo info)
        {
            if (TryBuild(font, cp, out info)) return true;
            var fallbacks = font.fallbackFontAssetTable;
            if (fallbacks != null)
            {
                foreach (var fb in fallbacks)
                {
                    if (fb == null) continue;
                    fb.TryAddCharacters(new[] { cp }, out _);
                    if (TryBuild(fb, cp, out info)) return true;
                }
            }
            return false;
        }

        private static bool TryGetGlyph(FontAsset font, uint cp, out Glyph glyph)
        {
            glyph = null;
            if (font.characterLookupTable.TryGetValue(cp, out var ch) && ch.glyph != null)
            {
                glyph = ch.glyph;
                return true;
            }
            return false;
        }

        private bool TryBuild(FontAsset font, uint cp, out OmtGlyphInfo info)
        {
            info = default;
            if (!TryGetGlyph(font, cp, out var glyph)) return false;

            float ps = math.max(1f, font.faceInfo.pointSize);
            float pad = font.atlasPadding;
            var m = glyph.metrics;
            var r = glyph.glyphRect;
            int slot = 0;
            if (r.width > 0 && r.height > 0)
            {
                var textures = font.atlasTextures;
                if (glyph.atlasIndex < 0 || textures == null || glyph.atlasIndex >= textures.Length) return false;
                slot = SlotOf(textures[glyph.atlasIndex]);
                float aw = font.atlasWidth, ah = font.atlasHeight;
                info.Uv = new float4((r.x - pad) / aw, (r.y - pad) / ah, (r.x + r.width + pad) / aw, (r.y + r.height + pad) / ah);
                info.Quad = new float4(
                    (m.horizontalBearingX - pad) / ps,
                    (m.horizontalBearingY - m.height - pad) / ps,
                    (m.horizontalBearingX + m.width + pad) / ps,
                    (m.horizontalBearingY + pad) / ps);
            }
            info.Advance = m.horizontalAdvance / ps;
            info.Slot = slot;
            return true;
        }

        private int SlotOf(Texture2D texture)
        {
            if (_slots.TryGetValue(texture, out int slot)) return slot;
            _textures.Add(texture);
            slot = _textures.Count; // slot 0 is reserved for icons
            _slots[texture] = slot;
            return slot;
        }

        /// <summary>Destroys the runtime font assets and their atlases.</summary>
        public void Dispose()
        {
            var destroyed = new HashSet<Object>();
            foreach (var font in _fonts)
            {
                if (font == null || !destroyed.Add(font)) continue;
                if (font.atlasTextures != null)
                    foreach (var tex in font.atlasTextures) DestroyObject(tex);
                DestroyObject(font.material);
                DestroyObject(font);
            }
            _glyphs.Clear();
            _missing.Clear();
            _textures.Clear();
            _slots.Clear();
        }

        private static void DestroyObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }
    }
}
