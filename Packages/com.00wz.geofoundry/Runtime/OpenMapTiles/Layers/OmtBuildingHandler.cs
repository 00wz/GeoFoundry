using GeoFoundry.Core.Handlers;
using GeoFoundry.Geometry;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>building</c> layer: extruded buildings and building parts. Reads
    /// <c>render_height</c>/<c>render_min_height</c> (heights above the terrain), <c>colour</c> (vertex tint, hex) and
    /// <c>hide_3d</c> (building outlines whose parts are mapped separately are not extruded), in jobs through
    /// <see cref="BuildingPropertiesJob"/>, and builds one terrain-conforming mesh with
    /// <see cref="AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/>.
    /// </summary>
    /// <remarks>
    /// Roofs, walls and floors follow the terrain: every point samples the terrain height under itself instead of
    /// a per-feature constant. This keeps buildings made of several features (<c>building</c> + <c>building:part</c>)
    /// together on slopes: OpenMapTiles offers no way to group the features of one building, so instead of guessing a
    /// shared height all features follow the same deterministic terrain grid and meet at shared boundaries.
    /// </remarks>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Building", fileName = "OmtBuilding")]
    public sealed class OmtBuildingHandler : OmtLayerHandler
    {
        [Tooltip("Building material (GeoFoundry/Building Lit, multiplies albedo by the vertex color). " +
                 "Recommended Sorting Priority: 0.")]
        [SerializeField] private Material _material;

        [Tooltip("Color of buildings without a colour attribute (multiplied with the material's base color).")]
        [SerializeField] private Color _defaultColor = OmtStyleDefaults.Building;

        [Tooltip("Height used when render_height is missing (the merged blocks of zoom 13), in meters; the schema uses 5 m.")]
        [SerializeField, Min(0)] private float _defaultHeight = 5f;

        [Tooltip("Multiplier of all heights.")]
        [SerializeField, Range(0.1f, 5f)] private float _heightScale = 1f;

        [Tooltip("Minimum height above the base, in meters, so that zero-height parts remain visible.")]
        [SerializeField, Min(0)] private float _minHeight = 1f;

        [Tooltip("Uses the colour attribute of the data; otherwise all buildings get the default color.")]
        [SerializeField] private bool _useColourAttribute = true;

        [Tooltip("Shadow casting of the building meshes.")]
        [SerializeField] private bool _castShadows = true;

        public override string LayerName => "building";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_material == null) return;

            var options = new BuildingPropertyOptions
            {
                DefaultHeight = _defaultHeight,
                HeightScale = _heightScale,
                MinHeight = _minHeight,
                DefaultColor = OmtColor.ToVertex(_defaultColor),
                LinearizeColours = QualitySettings.activeColorSpace == ColorSpace.Linear,
                UseColour = _useColourAttribute,
            };
            var props = await AsyncBuildingPropertyReader.ReadAsync(ctx.Layer, options, ctx.Ct);
            if (props == null) return;

            try
            {
                var mesh = await AsyncGeometryWorker.BuildBuildingTerrainMeshAsync(
                    ctx.Layer, props.Value.Hidden, props.Value.TopMeters, props.Value.BottomMeters, props.Value.Colors,
                    ctx.Grids.Center, ctx.TileCoord, ctx.LocalFrame, ctx.Ct);
                if (mesh == null) return;
                mesh.name = $"Buildings_{ctx.TileCoord}";
                output.Add(mesh, _material, _castShadows);
            }
            finally
            {
                props.Value.Dispose();
            }
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials() => CheckPriority(_material, OmtDrawOrder.Building, "Material");
#endif
    }
}
