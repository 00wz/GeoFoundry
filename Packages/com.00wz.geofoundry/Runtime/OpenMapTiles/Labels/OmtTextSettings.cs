using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Shared label settings of the OpenMapTiles handlers: source fonts, the name language preference, label materials,
    /// the icon atlas and placement parameters. One asset is referenced by every label-producing handler, so they
    /// share one set of glyph atlases.
    /// </summary>
    /// <remarks>
    /// Glyphs are rasterized at runtime into dynamic SDF atlases (the engine's TextCore font engine) from the source fonts,
    /// so any script covered by the fonts (Latin, Cyrillic, Greek with the default Noto Sans) is supported. Complex
    /// shaping (Arabic joining, Indic scripts) and right-to-left reordering are not performed.
    /// </remarks>
    [CreateAssetMenu(menuName = "GeoFoundry/OpenMapTiles/Text Settings", fileName = "OmtTextSettings")]
    public sealed class OmtTextSettings : ScriptableObject
    {
        [Header("Fonts")]
        [Tooltip("Regular face (TTF/OTF with font data included).")]
        [SerializeField] private Font _regularFont;

        [Tooltip("Bold face; the regular face is used if empty.")]
        [SerializeField] private Font _boldFont;

        [Tooltip("Italic face; the regular face is used if empty.")]
        [SerializeField] private Font _italicFont;

        [Tooltip("Rasterization size of the SDF glyphs in points.")]
        [SerializeField, Range(16, 96)] private int _samplingSize = 42;

        [Tooltip("SDF padding in atlas pixels; limits the maximum halo width relative to the text size.")]
        [SerializeField, Range(2, 16)] private int _padding = 8;

        [Tooltip("Size of each glyph atlas texture.")]
        [SerializeField] private int _atlasSize = 2048;

        [Header("Names")]
        [Tooltip("Name attributes in order of preference, e.g. name:ru, name:en, name. The first non-empty one is shown.")]
        [SerializeField] private List<string> _nameKeys = new List<string> { "name:en", "name" };

        [Header("Rendering")]
        [Tooltip("Material for text (GeoFoundry/Label). Instances per glyph atlas are created at runtime.")]
        [SerializeField] private Material _textMaterial;

        [Tooltip("Material for icons (GeoFoundry/Label with the icon atlas as its texture).")]
        [SerializeField] private Material _iconMaterial;

        [Tooltip("Icon atlas.")]
        [SerializeField] private OmtIconAtlas _icons;

        [Tooltip("Global multiplier of label sizes.")]
        [SerializeField, Range(0.25f, 4f)] private float _scale = 1f;

        [Tooltip("Height of label anchors above the ground in meters.")]
        [SerializeField, Min(0)] private float _anchorLift = 3f;

        [Header("Placement")]
        [Tooltip("Maximum zoom of the vector tile source (14 for OpenMapTiles). Tiles of this zoom are viewed up close, " +
                 "so their labels are decluttered for several deeper display zooms.")]
        [SerializeField, Range(0, 22)] private int _sourceMaxZoom = 14;

        [Tooltip("Number of display zooms beyond the source maximum zoom for which labels are decluttered.")]
        [SerializeField, Range(0, 8)] private int _overzoomLevels = 5;

        [Tooltip("Padding around labels in collision tests, in screen pixels.")]
        [SerializeField, Range(0, 16)] private float _collisionPadding = 2f;

        [Tooltip("Must equal GeoFoundryConfig → Lod → Max Tile Screen Size Pixels: a tile is shown while its screen " +
                 "size is between half of this value and this value, and its labels are decluttered for that range.")]
        [SerializeField, Min(1f)] private float _tileScreenSizePixels = 1500f;

        private OmtGlyphCache _glyphs;
        private readonly List<Material> _textMaterials = new List<Material>();
        private readonly Dictionary<Transform, NativeParallelMultiHashMap<int, float4>> _occupancy =
            new Dictionary<Transform, NativeParallelMultiHashMap<int, float4>>();

        public IReadOnlyList<string> NameKeys => _nameKeys;
        public OmtIconAtlas Icons => _icons;
        public float Scale => _scale;
        public float AnchorLift => _anchorLift;
        public int SourceMaxZoom => _sourceMaxZoom;
        public int OverzoomLevels => _overzoomLevels;
        public float CollisionPadding => _collisionPadding;

        /// <summary>
        /// Gets or sets the maximum screen size of a tile in pixels, which sets the label declutter tiers (see
        /// <see cref="OmtZoom.LabelTierRange"/>). Handlers cannot query the tile selection, so it must be kept equal to
        /// <c>GeoFoundryConfig.Lod.MaxTileScreenSizePixels</c>: with a lower value tiles show fewer labels than would
        /// fit; with a value more than about twice as high, labels fade in only shortly before their tile is replaced
        /// (they flicker as the camera approaches).
        /// </summary>
        public float TileScreenSizePixels
        {
            get => _tileScreenSizePixels;
            set => _tileScreenSizePixels = Mathf.Max(1f, value);
        }

        /// <summary>Gets whether text can be rendered (a regular font and a text material are assigned).</summary>
        public bool CanRenderText => _regularFont != null && _textMaterial != null;

        /// <summary>Gets whether icons can be rendered.</summary>
        public bool CanRenderIcons => _icons != null && _icons.Texture != null && _iconMaterial != null;

        internal OmtGlyphCache Glyphs
        {
            get
            {
                if (_glyphs == null && _regularFont != null)
                    _glyphs = new OmtGlyphCache(_regularFont, _boldFont, _italicFont, _samplingSize, _padding, _atlasSize);
                return _glyphs;
            }
        }

        /// <summary>
        /// Returns the material of a texture slot: slot 0 is the icon material, slot k ≥ 1 a runtime instance of the
        /// text material bound to glyph atlas k - 1.
        /// </summary>
        internal Material GetSlotMaterial(int slot)
        {
            if (slot == 0) return _iconMaterial;
            var glyphs = Glyphs;
            if (glyphs == null || _textMaterial == null || slot - 1 >= glyphs.Textures.Count) return null;
            while (_textMaterials.Count < slot) _textMaterials.Add(null);
            var material = _textMaterials[slot - 1];
            if (material == null)
            {
                material = new Material(_textMaterial) { name = $"{_textMaterial.name} (atlas {slot - 1})" };
                material.mainTexture = glyphs.Textures[slot - 1];
                material.hideFlags = HideFlags.DontSave;
                _textMaterials[slot - 1] = material;
            }
            return material;
        }

        /// <summary>
        /// Returns the label occupancy of one tile load (keyed by its root transform), shared by all label layers of the
        /// tile so that they avoid each other. Entries of unloaded tiles (destroyed roots) are released here.
        /// </summary>
        internal NativeParallelMultiHashMap<int, float4> GetOccupancy(Transform tileRoot)
        {
            if (_occupancy.TryGetValue(tileRoot, out var existing)) return existing;

            // A destroyed root compares equal to null; its key object stays usable for removal.
            var stale = new List<Transform>();
            foreach (var kv in _occupancy)
                if (kv.Key == null) stale.Add(kv.Key);
            foreach (var k in stale)
            {
                _occupancy[k].Dispose();
                _occupancy.Remove(k);
            }

            var boxes = new NativeParallelMultiHashMap<int, float4>(256, Allocator.Persistent);
            _occupancy[tileRoot] = boxes;
            return boxes;
        }

        // Not released in OnValidate: the atlases are referenced by the label meshes of loaded tiles.
        private void OnDisable() => Release();

        private void Release()
        {
            foreach (var m in _textMaterials)
            {
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m);
                else DestroyImmediate(m);
            }
            _textMaterials.Clear();
            _glyphs?.Dispose();
            _glyphs = null;
            foreach (var kv in _occupancy) kv.Value.Dispose();
            _occupancy.Clear();
        }
    }
}
