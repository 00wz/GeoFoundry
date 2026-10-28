using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>landuse</c> layer: human land use (residential, industrial, education,
    /// cemetery, military, sports…) as a draped fill mesh colored by <c>class</c>, plus outlines. <c>subclass</c>
    /// (the parking type) can be matched by the rules; <c>access=no</c> areas are optionally faded.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Landuse", fileName = "OmtLanduse")]
    public sealed class OmtLanduseHandler : OmtLayerHandler
    {
        [Tooltip("Fill material (GeoFoundry/Surface Lit). Recommended Sorting Priority: -48.")]
        [SerializeField] private Material _fillMaterial;

        [Tooltip("Outline material (GeoFoundry/Line Lit); leave empty to skip outlines. Recommended Sorting Priority: -36.")]
        [SerializeField] private Material _outlineMaterial;

        [SerializeField] private List<OmtFillRule> _fills = OmtStyleDefaults.LanduseFills();

        [SerializeField] private List<OmtLineRule> _outlines = OmtStyleDefaults.LanduseOutlines();

        [Tooltip("Fades fills of areas with restricted access (access=no) toward the background by this amount.")]
        [SerializeField, Range(0, 1)] private float _restrictedAccessFade;

        public override string LayerName => "landuse";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_fillMaterial != null)
            {
                var mods = new List<OmtModifier>();
                if (_restrictedAccessFade > 0f)
                {
                    var m = OmtModifier.Create("access", "no", OmtModifierAction.Tint);
                    m.Color = OmtColor.ToVertex(OmtStyleDefaults.Background);
                    m.Scale = _restrictedAccessFade;
                    mods.Add(m);
                }
                output.Add(await OmtBuild.FillsAsync(ctx, _fills, "class", "subclass", mods, "Landuse"), _fillMaterial);
            }

            if (_outlineMaterial != null && !ctx.Ct.IsCancellationRequested)
            {
                var pass = new OmtLinePass { Rules = _outlines, Lines = false, Outlines = true };
                var (mesh, _) = await OmtBuild.LinesAsync(ctx, new[] { pass }, "LanduseOutline");
                output.Add(mesh, _outlineMaterial);
            }
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials()
        {
            CheckPriority(_fillMaterial, OmtDrawOrder.Landuse, "Fill Material");
            CheckPriority(_outlineMaterial, OmtDrawOrder.LanduseOutline, "Outline Material");
        }
#endif
    }
}
