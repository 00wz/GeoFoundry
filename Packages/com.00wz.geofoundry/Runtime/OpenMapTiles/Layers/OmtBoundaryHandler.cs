using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>boundary</c> layer: administrative boundaries styled by <c>admin_level</c>, with
    /// <c>disputed</c> borders dashed and recolored, <c>maritime</c> borders tinted or hidden, a worldview filter on
    /// <c>claimed_by</c>, and the outlines of <c>class=aboriginal_lands</c> polygons. Country names on either side of
    /// a border (<c>adm0_l</c>/<c>adm0_r</c>) are not drawn: the <c>place</c> layer labels countries.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Boundary", fileName = "OmtBoundary")]
    public sealed class OmtBoundaryHandler : OmtLayerHandler
    {
        [Tooltip("Line material (GeoFoundry/Line Lit). Recommended Sorting Priority: -26.")]
        [SerializeField] private Material _material;

        [Tooltip("Line styles by admin_level (compared as text).")]
        [SerializeField] private List<OmtLineRule> _lines = OmtStyleDefaults.BoundaryLines();

        [Tooltip("Outline styles of boundary polygons by class (aboriginal_lands).")]
        [SerializeField] private List<OmtLineRule> _areas = OmtStyleDefaults.BoundaryAreaOutlines();

        [Tooltip("Color of disputed borders (disputed=1); they are also dashed.")]
        [SerializeField] private Color _disputedColor = OmtStyleDefaults.BoundaryDisputed;

        [SerializeField] private Vector2 _disputedDash = new Vector2(4, 3);

        [Tooltip("Hides maritime borders (maritime=1) instead of tinting them.")]
        [SerializeField] private bool _hideMaritime;

        [SerializeField] private Color _maritimeColor = OmtStyleDefaults.BoundaryMaritime;

        [Tooltip("ISO 3166-1 alpha-2 code of the worldview. Lines carrying claimed_by are claims of one country: with an " +
                 "empty worldview they are hidden, otherwise only the claims of that country are shown.")]
        [SerializeField] private string _worldview = "";

        public override string LayerName => "boundary";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_material == null) return;

            var mods = new List<OmtModifier>();
            if (string.IsNullOrEmpty(_worldview))
            {
                mods.Add(OmtModifier.Create("claimed_by", "", OmtModifierAction.Skip));
            }
            else
            {
                var other = OmtModifier.Create("claimed_by", _worldview, OmtModifierAction.Skip);
                other.Negate = true;
                mods.Add(other);
            }
            if (_hideMaritime)
            {
                mods.Add(OmtModifier.Create("maritime", "", OmtModifierAction.Skip));
            }
            else
            {
                var maritime = OmtModifier.Create("maritime", "", OmtModifierAction.Recolor);
                maritime.Color = OmtColor.ToVertex(_maritimeColor);
                mods.Add(maritime);
            }
            var disputedColor = OmtModifier.Create("disputed", "", OmtModifierAction.Recolor);
            disputedColor.Color = OmtColor.ToVertex(_disputedColor);
            mods.Add(disputedColor);
            var disputedDash = OmtModifier.Create("disputed", "", OmtModifierAction.Dash);
            disputedDash.Dash = new Unity.Mathematics.float2(_disputedDash.x, _disputedDash.y);
            mods.Add(disputedDash);

            var passes = new[]
            {
                new OmtLinePass { Rules = _lines, ClassKey = "admin_level", Modifiers = mods },
                new OmtLinePass { Rules = _areas, ClassKey = "class", Lines = false, Outlines = true },
            };
            var (mesh, _) = await OmtBuild.LinesAsync(ctx, passes, "Boundary");
            output.Add(mesh, _material);
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials() => CheckPriority(_material, OmtDrawOrder.Boundary, "Material");
#endif
    }
}
