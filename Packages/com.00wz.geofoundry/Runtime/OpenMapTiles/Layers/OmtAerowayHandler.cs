using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>aeroway</c> layer: airport areas (aerodrome, apron, runway, taxiway, helipad
    /// polygons) as fills, runway/taxiway center lines as ribbons, and <c>ref</c> labels along runways and at gates.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Aeroway", fileName = "OmtAeroway")]
    public sealed class OmtAerowayHandler : OmtLayerHandler
    {
        [Tooltip("Fill material (GeoFoundry/Surface Lit). Recommended Sorting Priority: -47.")]
        [SerializeField] private Material _fillMaterial;

        [Tooltip("Line material (GeoFoundry/Line Lit). Recommended Sorting Priority: -35.")]
        [SerializeField] private Material _lineMaterial;

        [SerializeField] private List<OmtFillRule> _fills = OmtStyleDefaults.AerowayFills();

        [SerializeField] private List<OmtLineRule> _lines = OmtStyleDefaults.AerowayLines();

        [Tooltip("Label settings; leave empty to skip labels.")]
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.Aeroway();

        public override string LayerName => "aeroway";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_fillMaterial != null)
                output.Add(await OmtBuild.FillsAsync(ctx, _fills, "class", "", null, "AerowayFill"), _fillMaterial);

            if (_lineMaterial != null && !ctx.Ct.IsCancellationRequested)
            {
                var (mesh, _) = await OmtBuild.LinesAsync(ctx, new[] { new OmtLinePass { Rules = _lines } }, "AerowayLines");
                output.Add(mesh, _lineMaterial);
            }

            await OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.Lines = true;
                    job.TextKind = OmtLabelText.Field;
                    job.FieldKey = OmtRules.Fixed("ref");
                    job.Spacing = 300f;
                    return job;
                }),
            }, output, "AerowayLabels");
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials()
        {
            CheckPriority(_fillMaterial, OmtDrawOrder.AerowayFill, "Fill Material");
            CheckPriority(_lineMaterial, OmtDrawOrder.AerowayLines, "Line Material");
        }
#endif
    }
}
