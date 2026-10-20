using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Render.Layers;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Collects the meshes a handler builds for one tile and turns them into a single scene hierarchy wrapped in a
    /// <see cref="MeshHierarchyInstance"/>. Meshes are kept as plain objects until <see cref="CreateInstance"/>, so a
    /// cancelled build only has to destroy them (<see cref="DestroyAll"/>) and leaves no GameObjects behind.
    /// </summary>
    public sealed class OmtTileOutput
    {
        private readonly struct Entry
        {
            public readonly Mesh Mesh;
            public readonly Material[] Materials;
            public readonly bool CastShadows;
            public readonly MaterialPropertyBlock[] Blocks;

            public Entry(Mesh mesh, Material[] materials, bool castShadows, MaterialPropertyBlock[] blocks)
            {
                Mesh = mesh;
                Materials = materials;
                CastShadows = castShadows;
                Blocks = blocks;
            }
        }

        private readonly LayerLoadContext _ctx;
        private readonly string _name;
        private readonly List<Entry> _entries = new List<Entry>(4);

        internal OmtTileOutput(LayerLoadContext ctx, string name)
        {
            _ctx = ctx;
            _name = name;
        }

        /// <summary>Gets the number of meshes collected so far.</summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Adds a mesh rendered with one material. A null mesh is ignored; with a null material the mesh is
        /// destroyed (the slot is disabled). The CPU copy of the mesh is released (<c>UploadMeshData(true)</c>).
        /// </summary>
        public void Add(Mesh mesh, Material material, bool castShadows = false)
        {
            if (mesh == null) return;
            if (material == null)
            {
                Destroy(mesh);
                return;
            }
            Add(mesh, new[] { material }, castShadows);
        }

        /// <summary>Adds a mesh with one material per submesh (and optional per-submesh property blocks).</summary>
        public void Add(Mesh mesh, Material[] materials, bool castShadows = false, MaterialPropertyBlock[] blocks = null)
        {
            if (mesh == null) return;
            mesh.UploadMeshData(markNoLongerReadable: true);
            _entries.Add(new Entry(mesh, materials, castShadows, blocks));
        }

        /// <summary>Destroys every collected mesh; used on cancellation and errors.</summary>
        public void DestroyAll()
        {
            foreach (var e in _entries) Destroy(e.Mesh);
            _entries.Clear();
        }

        /// <summary>
        /// Creates the tile hierarchy under <see cref="LayerLoadContext.TileRoot"/>: one child GameObject per mesh.
        /// Returns <c>null</c> if nothing was collected.
        /// </summary>
        public ILayerHandlerInstance CreateInstance()
        {
            if (_entries.Count == 0) return null;

            var root = new GameObject($"{_name}_{_ctx.TileCoord}");
            root.transform.SetParent(_ctx.TileRoot, worldPositionStays: false);

            foreach (var e in _entries)
            {
                var go = new GameObject(e.Mesh.name);
                go.transform.SetParent(root.transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh = e.Mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = e.Materials;
                renderer.shadowCastingMode = e.CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                if (e.Blocks != null)
                    for (int i = 0; i < e.Blocks.Length; i++)
                        if (e.Blocks[i] != null) renderer.SetPropertyBlock(e.Blocks[i], i);
            }
            _entries.Clear();
            return new MeshHierarchyInstance(root);
        }

        private static void Destroy(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }
    }
}
