using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Render.Layers.OpenMapTiles
{
    /// <summary>
    /// Built-in handler for the <c>transportation</c> layer. Builds all geometry of the tile's layer as one
    /// <see cref="Mesh"/> in jobs with <see cref="RoadBuildJob"/> (see
    /// <see cref="AsyncGeometryWorker.BuildRoadMeshAsync"/>) without blocking the main thread, following the same
    /// pattern as <see cref="SurfacesLayerHandler"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Roads", fileName = "RoadsLayerHandler")]
    public sealed class RoadsLayerHandler : LayerHandlerSO
    {
        [SerializeField] private Material _material;

        public override string LayerName => "transportation";

        public override async Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext ctx)
        {
            // The OpenMapTiles schema has no per-feature road colors; see SurfacesLayerHandler.
            var mesh = await AsyncGeometryWorker.BuildRoadMeshAsync(
                ctx.Layer, SelectWidthMeters, ctx.TileCoord, ctx.Grids.Center, ctx.LocalFrame, default(NativeArray<Color32>), ctx.Ct);
            if (mesh == null) return null;

            mesh.name = $"Roads_{ctx.TileCoord}";
            // See SurfacesLayerHandler: frees the CPU copy of the geometry.
            mesh.UploadMeshData(markNoLongerReadable: true);
            var go = new GameObject(mesh.name);
            go.transform.SetParent(ctx.TileRoot, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;

            return new MeshHierarchyInstance(go);
        }

        /// <summary>Maps the OpenMapTiles <c>class</c> to a ribbon width; schema interpretation belongs to the handler, not the job.</summary>
        private static float SelectWidthMeters(NativeMvtFeature feature)
        {
            string cls = feature.Properties.TryGetValue("class", out var cv) ? cv.AsString() : null;
            return RoadParams.ShouldRender(cls) ? RoadParams.GetWidthMeters(cls) : -1f;
        }
    }
}
