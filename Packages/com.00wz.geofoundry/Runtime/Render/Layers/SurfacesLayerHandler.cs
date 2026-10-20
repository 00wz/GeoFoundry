using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Geometry;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Render.Layers
{
    /// <summary>
    /// Built-in handler for flat surface layers of any schema (in OpenMapTiles: <c>water</c>, <c>park</c>,
    /// <c>landuse</c>, <c>landcover</c>).
    /// Builds all geometry of the tile's layer as one <see cref="Mesh"/> in jobs with <see cref="SurfaceBuildJob"/>
    /// (see <see cref="AsyncGeometryWorker.BuildSurfaceMeshAsync"/>) without blocking the main thread
    /// (see <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>).
    /// Create a separate asset for each layer via <c>GeoFoundry / Layers / Surfaces</c>.
    ///
    /// UVs are in meters (one UV unit per meter); set the texture density with the material's Tiling, e.g.
    /// 0.05 repeats the texture every 20 m.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/Surfaces", fileName = "SurfacesLayerHandler")]
    public sealed class SurfacesLayerHandler : LayerHandlerSO
    {
        [Tooltip("MVT layer name, e.g. water, park, landuse, landcover.")]
        [SerializeField] private string _layerName;

        [SerializeField] private Material _material;

        public override string LayerName => _layerName;

        /// <summary>UVs in meters: texture density is set by the material's Tiling.</summary>
        private const float UvScalePerMeter = 1f;

        public override async Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext ctx)
        {
            // The OpenMapTiles schema has no per-feature surface colors. An empty array tells the job so
            // (see SurfaceBuildJob.FeatureColors): all vertices get opaque white and the material's uniform
            // _BaseColor applies unchanged.
            var mesh = await AsyncGeometryWorker.BuildSurfaceMeshAsync(
                ctx.Layer, ctx.TileCoord, ctx.Grids.Center, ctx.LocalFrame, UvScalePerMeter, default(NativeArray<Color32>), ctx.Ct);
            if (mesh == null) return null;

            mesh.name = $"{_layerName}_{ctx.TileCoord}";
            // The geometry is no longer read on the CPU, so free the CPU copy (on WebGL it lives in the WASM heap,
            // unlike GPU buffers). Bounds are already computed by the worker.
            mesh.UploadMeshData(markNoLongerReadable: true);
            var go = new GameObject(mesh.name);
            go.transform.SetParent(ctx.TileRoot, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;

            return new MeshHierarchyInstance(go);
        }
    }
}
