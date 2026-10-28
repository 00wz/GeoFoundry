using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>waterway</c> layer: rivers, canals, streams, drains and ditches as ribbons sized
    /// by <c>class</c>; <c>intermittent</c> waterways dashed (rivers and other classes with the style's two
    /// patterns); tunnels (<c>brunnel=tunnel</c>) dashed from <see cref="OmtStyleDefaults.WaterwayTunnelMinZoom"/>;
    /// bridges (aqueducts) in a separate mesh with a casing drawn above the roads, one width for all classes as in the
    /// style; names along the lines.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Waterway", fileName = "OmtWaterway")]
    public sealed class OmtWaterwayHandler : OmtLayerHandler
    {
        [Tooltip("Line material (GeoFoundry/Line Lit). Recommended Sorting Priority: -44.")]
        [SerializeField] private Material _material;

        [Tooltip("Material of waterway bridges (GeoFoundry/Line Lit); empty draws them with the regular lines. " +
                 "Recommended Sorting Priority: -28.")]
        [SerializeField] private Material _bridgeMaterial;

        [SerializeField] private List<OmtLineRule> _lines = OmtStyleDefaults.WaterwayLines();

        [Tooltip("Dash pattern of intermittent rivers in multiples of the width.")]
        [SerializeField] private Vector2 _intermittentDash = new Vector2(3, 2);

        [Tooltip("Dash pattern of intermittent waterways of other classes in multiples of the width.")]
        [SerializeField] private Vector2 _intermittentOtherDash = new Vector2(4, 3);

        [SerializeField] private OmtTunnelMode _tunnels = OmtTunnelMode.Show;

        [SerializeField] private Color _bridgeCasing = OmtStyleDefaults.WaterwayBridgeCasing;

        [Tooltip("Label settings; leave empty to skip labels.")]
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.Waterway();

        public override string LayerName => "waterway";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_material != null)
            {
                var mods = new List<OmtModifier>();
                var intermittent = OmtModifier.Create("intermittent", "", OmtModifierAction.Dash);
                intermittent.Dash = new float2(_intermittentOtherDash.x, _intermittentOtherDash.y);
                mods.Add(intermittent);
                var intermittentRiver = OmtModifier.Create("intermittent", "", OmtModifierAction.Dash).And("class", "river");
                intermittentRiver.Dash = new float2(_intermittentDash.x, _intermittentDash.y);
                mods.Add(intermittentRiver);
                OmtLayerHelpers.AddTunnelModifier(mods, _tunnels);
                if (_tunnels != OmtTunnelMode.Hide)
                {
                    var tunnelDash = OmtModifier.Create("brunnel", "tunnel", OmtModifierAction.Dash);
                    tunnelDash.Dash = new float2(2, 4);
                    mods.Add(tunnelDash);
                    var tunnelZoom = OmtModifier.Create("brunnel", "tunnel", OmtModifierAction.MinZoom);
                    tunnelZoom.MinZoom = OmtStyleDefaults.WaterwayTunnelMinZoom;
                    mods.Add(tunnelZoom);
                }

                // With a bridge material, bridges are drawn by a second mesh above the roads instead.
                if (_bridgeMaterial != null) mods.Add(OmtModifier.Create("brunnel", "bridge", OmtModifierAction.Skip));

                var (lines, _) = await OmtBuild.LinesAsync(ctx, new[] { new OmtLinePass { Rules = _lines, Modifiers = mods } }, "Waterway");
                output.Add(lines, _material);

                if (_bridgeMaterial != null && !ctx.Ct.IsCancellationRequested)
                {
                    var bridgeRules = new List<OmtLineRule>(_lines.Count);
                    foreach (var r in _lines)
                    {
                        bridgeRules.Add(new OmtLineRule(r.Class, r.Subclass, OmtStyleDefaults.WaterwayBridgeFill,
                                OmtStyleDefaults.WaterwayBridgeMeters, OmtStyleDefaults.WaterwayBridgePixels, r.Order,
                                r.MinZoom) { Visible = r.Visible }
                            .WithCasing(_bridgeCasing, OmtStyleDefaults.WaterwayBridgeCasingPixels,
                                OmtStyleDefaults.WaterwayBridgeCasingMeters));
                    }
                    var (bridges, _) = await OmtBuild.LinesAsync(ctx, new[]
                    {
                        new OmtLinePass { Rules = bridgeRules, Modifiers = BridgesOnly() },
                    }, "WaterwayBridges");
                    output.Add(bridges, _bridgeMaterial);
                }
            }

            await OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.Points = false;
                    job.Lines = true;
                    job.Spacing = 400f;
                    return job;
                }),
            }, output, "WaterwayLabels");
        }

        /// <summary>Modifiers keeping only <c>brunnel=bridge</c> features.</summary>
        private static List<OmtModifier> BridgesOnly()
        {
            // "Skip unless brunnel=bridge": skip features whose brunnel differs from bridge, and those without brunnel.
            var differs = OmtModifier.Create("brunnel", "bridge", OmtModifierAction.Skip);
            differs.Negate = true;
            var missing = OmtModifier.Create("brunnel", "", OmtModifierAction.Skip);
            missing.Negate = true;
            return new List<OmtModifier> { differs, missing };
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials()
        {
            CheckPriority(_material, OmtDrawOrder.Waterway, "Material");
            CheckPriority(_bridgeMaterial, OmtDrawOrder.WaterwayBridges, "Bridge Material");
        }
#endif
    }
}
