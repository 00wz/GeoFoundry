using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>How a handler renders features with <c>brunnel=tunnel</c>.</summary>
    public enum OmtTunnelMode
    {
        /// <summary>Not drawn (the style's default for water).</summary>
        Hide,
        /// <summary>Drawn with a color faded toward white.</summary>
        Dim,
        /// <summary>Drawn like regular features.</summary>
        Show,
    }

    /// <summary>
    /// Handler of the OpenMapTiles <c>water</c> layer: water polygons (ocean, lake, river, pond, dock, swimming
    /// pool) as one draped mesh. Reads <c>class</c> (fill rules), <c>intermittent</c> (separate color) and
    /// <c>brunnel</c> (tunnel handling). <c>id</c> carries no rendering information.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Water", fileName = "OmtWater")]
    public sealed class OmtWaterHandler : OmtLayerHandler
    {
        [Tooltip("Surface material (GeoFoundry/Surface Lit, white base color: colors come from the rules). " +
                 "Recommended Sorting Priority: -42.")]
        [SerializeField] private Material _material;

        [Tooltip("Fill colors by class.")]
        [SerializeField] private List<OmtFillRule> _fills = OmtStyleDefaults.WaterFills();

        [Tooltip("Color of intermittent water bodies (intermittent=1).")]
        [SerializeField] private Color _intermittentColor = OmtStyleDefaults.WaterIntermittent;

        [Tooltip("Rendering of covered water (brunnel=tunnel).")]
        [SerializeField] private OmtTunnelMode _tunnels = OmtTunnelMode.Hide;

        public override string LayerName => "water";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_material == null) return;

            var mods = new List<OmtModifier>();
            var intermittent = OmtModifier.Create("intermittent", "", OmtModifierAction.Recolor);
            intermittent.Color = OmtColor.ToVertex(_intermittentColor);
            mods.Add(intermittent);
            OmtLayerHelpers.AddTunnelModifier(mods, _tunnels);

            var mesh = await OmtBuild.FillsAsync(ctx, _fills, "class", "", mods, "Water");
            output.Add(mesh, _material);
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials() => CheckPriority(_material, OmtDrawOrder.Water, "Material");
#endif
    }

    internal static class OmtLayerHelpers
    {
        /// <summary>Adds the modifier implementing <paramref name="mode"/> for <c>brunnel=tunnel</c>.</summary>
        public static void AddTunnelModifier(List<OmtModifier> mods, OmtTunnelMode mode)
        {
            switch (mode)
            {
                case OmtTunnelMode.Hide:
                    mods.Add(OmtModifier.Create("brunnel", "tunnel", OmtModifierAction.Skip));
                    break;
                case OmtTunnelMode.Dim:
                {
                    var m = OmtModifier.Create("brunnel", "tunnel", OmtModifierAction.Tint);
                    m.Color = OmtColor.ToVertex(Color.white);
                    m.Scale = 0.5f;
                    mods.Add(m);
                    break;
                }
            }
        }
    }
}
