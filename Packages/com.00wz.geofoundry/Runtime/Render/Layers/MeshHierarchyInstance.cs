using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.Render.Layers
{
    /// <summary>
    /// Ready-made <see cref="ILayerHandlerInstance"/> that wraps a root GameObject. On Dispose it destroys the
    /// <c>sharedMesh</c> of every <see cref="MeshFilter"/> in the hierarchy and then the hierarchy itself.
    /// Suits most handlers whose instance data is just scene nodes with meshes.
    /// </summary>
    public sealed class MeshHierarchyInstance : ILayerHandlerInstance
    {
        private GameObject _root;

        /// <summary>
        /// Gets the memory of all unique <c>sharedMesh</c>es in the hierarchy, measured at construction (see
        /// <see cref="MeshMemory.Measure"/>). Create the instance only after the hierarchy is complete and the
        /// meshes have gone through <c>UploadMeshData</c>, if it is called.
        /// </summary>
        public TileMemoryUsage MemoryUsage { get; }

        /// <summary>Wraps <paramref name="root"/> and measures its mesh memory.</summary>
        /// <param name="root">Root of the instance hierarchy; this instance takes ownership of it.</param>
        public MeshHierarchyInstance(GameObject root)
        {
            _root = root;
            MemoryUsage = MeasureHierarchy(root);
        }

        private static TileMemoryUsage MeasureHierarchy(GameObject root)
        {
            if (root == null) return TileMemoryUsage.Zero;

            var total = TileMemoryUsage.Zero;
            var seen = new HashSet<Mesh>();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(includeInactive: true))
            {
                var mesh = filter.sharedMesh;
                if (mesh != null && seen.Add(mesh)) total += MeshMemory.Measure(mesh);
            }
            return total;
        }

        public void Dispose()
        {
            if (_root == null) return;

            var filters = _root.GetComponentsInChildren<MeshFilter>(includeInactive: true);
            for (int i = 0; i < filters.Length; i++)
            {
                var m = filters[i].sharedMesh;
                if (m == null) continue;
                if (Application.isPlaying) Object.Destroy(m);
                else Object.DestroyImmediate(m);
            }

            if (Application.isPlaying) Object.Destroy(_root);
            else Object.DestroyImmediate(_root);
            _root = null;
        }
    }
}
