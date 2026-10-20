using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Geometry;
using UnityEngine;

namespace GeoFoundry.Render.Terrain
{
    /// <summary>
    /// Default <see cref="TerrainHandlerSO"/>. Builds the terrain mesh in jobs with <see cref="TerrainBuildJob"/>
    /// from the precomputed heights and normals of a <see cref="GeoFoundry.Core.Ground.GroundGrid"/>, creates a
    /// GameObject with MeshFilter and MeshRenderer and returns the instance. Create the asset via
    /// <c>GeoFoundry / Terrain / Default Terrain Handler</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Terrain/Default Terrain Handler", fileName = "DefaultTerrainHandler")]
    public sealed class DefaultTerrainHandler : TerrainHandlerSO
    {
        [SerializeField] private Material _material;

        public override async Awaitable<ITerrainHandlerInstance> OnTerrainLoadedAsync(TerrainLoadContext ctx)
        {
            var mesh = await AsyncGeometryWorker.BuildTerrainMeshAsync(ctx.Grid, ctx.TileCoord, ctx.LocalFrame, ctx.Ct);
            if (mesh == null) return null;

            // Vector layers take heights from the GroundGrid, not from the mesh, so the CPU copy is not needed
            // (on WebGL it lives in the WASM heap, unlike GPU buffers). Bounds are already computed by the worker.
            mesh.UploadMeshData(markNoLongerReadable: true);

            var go = new GameObject($"Terrain_{ctx.TileCoord}");
            if (ctx.Parent != null)
                go.transform.SetParent(ctx.Parent, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;
            return new Instance(go, mesh);
        }

        // ---- Instance --------------------------------------------------------

        private sealed class Instance : ITerrainHandlerInstance
        {
            private GameObject _go;
            private Mesh _mesh;

            public TileMemoryUsage MemoryUsage { get; }

            public Instance(GameObject go, Mesh mesh)
            {
                _go = go;
                _mesh = mesh;
                MemoryUsage = MeshMemory.Measure(mesh);
            }

            public void Dispose()
            {
                if (_mesh != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(_mesh);
                    else UnityEngine.Object.DestroyImmediate(_mesh);
                    _mesh = null;
                }
                if (_go != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(_go);
                    else UnityEngine.Object.DestroyImmediate(_go);
                    _go = null;
                }
            }
        }
    }
}
