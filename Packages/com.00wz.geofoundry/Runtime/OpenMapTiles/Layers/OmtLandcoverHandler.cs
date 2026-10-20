using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>landcover</c> layer: natural surfaces (wood, grass, wetland, ice, sand, rock,
    /// farmland) as a draped fill mesh colored by <c>subclass</c> with <c>class</c> fallbacks, plus optional outlines
    /// (farmland, allotments, glaciers) in a separate line mesh.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Landcover", fileName = "OmtLandcover")]
    public sealed class OmtLandcoverHandler : OmtLayerHandler
    {
        [Tooltip("Fill material (GeoFoundry/Surface Lit). Recommended Sorting Priority: -46.")]
        [SerializeField] private Material _fillMaterial;

        [Tooltip("Outline material (GeoFoundry/Line Lit); leave empty to skip outlines. Recommended Sorting Priority: -45.")]
        [SerializeField] private Material _outlineMaterial;

        [Tooltip("Fill colors; subclass rules first, class fallbacks last.")]
        [SerializeField] private List<OmtFillRule> _fills = OmtStyleDefaults.LandcoverFills();

        [Tooltip("Outline styles by class/subclass.")]
        [SerializeField] private List<OmtLineRule> _outlines = OmtStyleDefaults.LandcoverOutlines();

        public override string LayerName => "landcover";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_fillMaterial != null)
                output.Add(await OmtBuild.FillsAsync(ctx, _fills, "class", "subclass", null, "Landcover"), _fillMaterial);

            if (_outlineMaterial != null && !ctx.Ct.IsCancellationRequested)
            {
                var pass = new OmtLinePass { Rules = _outlines, SubclassKey = "subclass", Lines = false, Outlines = true };
                var (mesh, _) = await OmtBuild.LinesAsync(ctx, new[] { pass }, "LandcoverOutline");
                output.Add(mesh, _outlineMaterial);
            }
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials()
        {
            CheckPriority(_fillMaterial, OmtDrawOrder.Landcover, "Fill Material");
            CheckPriority(_outlineMaterial, OmtDrawOrder.LandcoverOutline, "Outline Material");
        }
#endif
    }
}
