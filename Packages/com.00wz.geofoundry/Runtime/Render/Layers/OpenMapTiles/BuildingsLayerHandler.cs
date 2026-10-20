using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Geometry;
using UnityEngine;

namespace GeoFoundry.Render.Layers.OpenMapTiles
{
    /// <summary>
    /// Built-in handler for the <c>building</c> layer of the OpenMapTiles schema (planetiler default). Reads
    /// <c>render_height</c>/<c>render_min_height</c>/<c>hide_3d</c>/<c>colour</c> in jobs through
    /// <see cref="BuildingPropertiesJob"/> and builds all terrain-conforming extruded geometry of the tile's layer
    /// as one <see cref="Mesh"/> with <see cref="BuildingTerrainBuildJob"/>
    /// (see <see cref="AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/>); neither blocks the main thread.
    /// Roofs, walls and floors follow the terrain: every point samples the terrain height under itself instead of
    /// a per-feature constant. This keeps buildings made of several MVT features (<c>building</c> +
    /// <c>building:part</c>) from falling apart on slopes: OpenMapTiles offers no way to group the features of one
    /// building, so instead of guessing a shared height all features follow the same deterministic terrain grid
    /// and meet at shared boundaries.
    ///
    /// For another schema (Shortbread or your own), register your own handler with the same
    /// <see cref="LayerName"/> = "building"; it overrides the default one. If the schema groups the features of a
    /// building (e.g. <c>group_id</c>) and a consistent height can be computed per group, start from
    /// <see cref="AsyncGeometryWorker.BuildBuildingMeshesAsync"/>/<see cref="BuildingBuildJob"/> (flat-base
    /// extrusion) instead. Note that they currently compute the base height inside the job and would need changes
    /// to accept a precomputed per-group height.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Buildings", fileName = "BuildingsLayerHandler")]
    public sealed class BuildingsLayerHandler : LayerHandlerSO
    {
        [SerializeField] private Material _material;

        public override string LayerName => "building";

        public override async Awaitable<ILayerHandlerInstance> OnLayerLoadedAsync(LayerLoadContext ctx)
        {
            var props = await AsyncBuildingPropertyReader.ReadAsync(ctx.Layer, ctx.Ct);
            if (props == null) return null;

            try
            {
                // The job bakes colour into the mesh vertex colors; the material must multiply albedo by them
                // (GeoFoundryBuildingLit).
                var mesh = await AsyncGeometryWorker.BuildBuildingTerrainMeshAsync(
                    ctx.Layer, props.Value.Hidden, props.Value.TopMeters, props.Value.BottomMeters, props.Value.Colors,
                    ctx.Grids.Center, ctx.TileCoord, ctx.LocalFrame, ctx.Ct);
                if (mesh == null) return null;

                mesh.name = $"Buildings_{ctx.TileCoord}";
                // See SurfacesLayerHandler: frees the CPU copy of the geometry.
                mesh.UploadMeshData(markNoLongerReadable: true);
                var go = new GameObject(mesh.name);
                go.transform.SetParent(ctx.TileRoot, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = _material;

                return new MeshHierarchyInstance(go);
            }
            finally
            {
                props.Value.Dispose();
            }
        }
    }
}
