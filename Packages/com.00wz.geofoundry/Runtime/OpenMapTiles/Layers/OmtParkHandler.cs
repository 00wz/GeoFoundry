using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>park</c> layer: protected areas (national parks, nature reserves, protected
    /// areas by <c>class</c>) as a faint fill plus an outline band, and park names at the label points (<c>name</c>;
    /// <c>rank</c>, local to each zoom's label grid in the schema, only filters).
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Park", fileName = "OmtPark")]
    public sealed class OmtParkHandler : OmtLayerHandler
    {
        [Tooltip("Fill material (GeoFoundry/Surface Lit); leave empty to skip fills. Recommended Sorting Priority: -43.")]
        [SerializeField] private Material _fillMaterial;

        [Tooltip("Outline material (GeoFoundry/Line Lit); leave empty to skip outlines. Recommended Sorting Priority: -40.")]
        [SerializeField] private Material _outlineMaterial;

        [SerializeField] private List<OmtFillRule> _fills = OmtStyleDefaults.ParkFills();

        [SerializeField] private List<OmtLineRule> _outlines = OmtStyleDefaults.ParkOutlines();

        [Tooltip("Label settings; leave empty to skip labels.")]
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.Park();

        [Tooltip("Parks with a higher rank (less important) are not labeled; 0 labels all.")]
        [SerializeField, Min(0)] private int _maxLabelRank;

        public override string LayerName => "park";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_fillMaterial != null)
                output.Add(await OmtBuild.FillsAsync(ctx, _fills, "class", "", null, "ParkFill"), _fillMaterial);

            if (_outlineMaterial != null && !ctx.Ct.IsCancellationRequested)
            {
                var pass = new OmtLinePass { Rules = _outlines, Lines = false, Outlines = true };
                var (mesh, _) = await OmtBuild.LinesAsync(ctx, new[] { pass }, "ParkOutline");
                output.Add(mesh, _outlineMaterial);
            }

            int maxRank = _maxLabelRank;
            await OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.RankKey = OmtRules.Fixed("rank");
                    job.MaxRank = maxRank;
                    return job;
                }),
            }, output, "ParkLabels");
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials()
        {
            CheckPriority(_fillMaterial, OmtDrawOrder.ParkFill, "Fill Material");
            CheckPriority(_outlineMaterial, OmtDrawOrder.ParkOutline, "Outline Material");
        }
#endif
    }
}
