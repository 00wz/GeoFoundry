using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Icon atlas of the OpenMapTiles labels: a texture with the icons of the style (POI, peaks, aerodromes, place
    /// markers, road shields, oneway arrows) and a JSON index of their pixel rectangles, as produced by
    /// <c>Tools~/omt-icons/build_atlas.py</c>. Icons are looked up by name, e.g. <c>restaurant</c>, <c>peak</c>,
    /// <c>road_motorway</c>.
    /// </summary>
    /// <remarks>
    /// Index format: <c>{"width":W,"height":H,"icons":[{"name":"cafe","x":0,"y":0,"w":30,"h":30,"pixelRatio":2}]}</c>,
    /// with pixel coordinates measured from the top-left corner of the texture.
    /// </remarks>
    [CreateAssetMenu(menuName = "GeoFoundry/OpenMapTiles/Icon Atlas", fileName = "OmtIconAtlas")]
    public sealed class OmtIconAtlas : ScriptableObject
    {
        [Tooltip("Atlas texture (RGBA, sRGB, no mipmaps recommended).")]
        [SerializeField] private Texture2D _texture;

        [Tooltip("JSON index of the icon rectangles.")]
        [SerializeField] private TextAsset _index;

#pragma warning disable 0649 // assigned by JsonUtility
        [Serializable]
        private sealed class IndexJson
        {
            public int width;
            public int height;
            public IconJson[] icons;
        }

        [Serializable]
        private sealed class IconJson
        {
            public string name;
            public int x, y, w, h;
            public float pixelRatio = 1f;
        }
#pragma warning restore 0649

        private Dictionary<string, int> _byName;
        private NativeArray<OmtIconInfo> _infos;
        private NativeHashMap<FixedString64Bytes, int> _nativeByName;

        /// <summary>Gets the atlas texture.</summary>
        public Texture2D Texture => _texture;

        /// <summary>Gets the number of icons in the index.</summary>
        public int Count
        {
            get
            {
                EnsureLoaded();
                return _infos.IsCreated ? _infos.Length : 0;
            }
        }

        /// <summary>Returns whether the atlas contains an icon named <paramref name="name"/>.</summary>
        public bool Contains(string name)
        {
            EnsureLoaded();
            return name != null && _byName != null && _byName.ContainsKey(name);
        }

        /// <summary>
        /// Gets the icon infos indexed by icon id. Persistent, owned by the atlas; read-only for jobs and valid until
        /// the atlas is disabled.
        /// </summary>
        internal NativeArray<OmtIconInfo> Infos
        {
            get
            {
                EnsureLoaded();
                return _infos;
            }
        }

        /// <summary>Gets the icon id by name for jobs. Persistent, owned by the atlas; read-only for jobs.</summary>
        internal NativeHashMap<FixedString64Bytes, int> Ids
        {
            get
            {
                EnsureLoaded();
                return _nativeByName;
            }
        }

        private void EnsureLoaded()
        {
            if (_infos.IsCreated) return;

            _byName = new Dictionary<string, int>();
            var list = new List<OmtIconInfo>();
            if (_index != null && _texture != null)
            {
                var json = JsonUtility.FromJson<IndexJson>(_index.text);
                float tw = json.width > 0 ? json.width : _texture.width;
                float th = json.height > 0 ? json.height : _texture.height;
                foreach (var icon in json.icons ?? Array.Empty<IconJson>())
                {
                    if (string.IsNullOrEmpty(icon.name) || _byName.ContainsKey(icon.name)) continue;
                    float ratio = icon.pixelRatio > 0f ? icon.pixelRatio : 1f;
                    _byName[icon.name] = list.Count;
                    list.Add(new OmtIconInfo
                    {
                        // Texture V runs bottom-up, the index rows top-down.
                        Uv = new float4(icon.x / tw, 1f - (icon.y + icon.h) / th, (icon.x + icon.w) / tw, 1f - icon.y / th),
                        Size = new float2(icon.w / ratio, icon.h / ratio),
                    });
                }
            }

            _infos = new NativeArray<OmtIconInfo>(math.max(1, list.Count), Allocator.Persistent);
            for (int i = 0; i < list.Count; i++) _infos[i] = list[i];
            _nativeByName = new NativeHashMap<FixedString64Bytes, int>(math.max(1, list.Count), Allocator.Persistent);
            foreach (var kv in _byName) _nativeByName.TryAdd(OmtRules.Fixed(kv.Key), kv.Value);
        }

        private void OnDisable() => Release();

        private void OnValidate() => Release();

        private void Release()
        {
            // Jobs only read the arrays while a handler awaits them, and handlers never outlive the map, which is
            // disabled before its assets.
            if (_infos.IsCreated) _infos.Dispose();
            if (_nativeByName.IsCreated) _nativeByName.Dispose();
            _byName = null;
        }
    }
}
