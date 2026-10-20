using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>mountain_peak</c> layer: peaks, volcanoes and saddles as icons with their
    /// <c>name</c> and elevation (<c>ele</c> in meters, or <c>ele_ft</c> in feet where <c>customary_ft=1</c>), ordered
    /// by <c>rank</c>; ridges, aretes and cliffs (LineString features) as lines with names along them.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Mountain Peak", fileName = "OmtMountainPeak")]
    public sealed class OmtMountainPeakHandler : OmtLayerHandler
    {
        [Tooltip("Line material for ridges and cliffs (GeoFoundry/Line Lit); leave empty to skip. Recommended Sorting Priority: -27.")]
        [SerializeField] private Material _lineMaterial;

        [SerializeField] private List<OmtLineRule> _lines = OmtStyleDefaults.MountainLines();

        [Tooltip("Label settings; leave empty to skip labels.")]
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.MountainPeak();

        [Tooltip("Shows the elevation below the name.")]
        [SerializeField] private bool _showElevation = true;

        [Tooltip("Uses feet where they are customary (customary_ft=1); otherwise always meters.")]
        [SerializeField] private bool _customaryFeet = true;

        public override string LayerName => "mountain_peak";

        protected override async Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            if (_lineMaterial != null)
            {
                var (mesh, _) = await OmtBuild.LinesAsync(ctx, new[] { new OmtLinePass { Rules = _lines } }, "MountainLines");
                output.Add(mesh, _lineMaterial);
            }

            bool elevation = _showElevation, feet = _customaryFeet;
            await OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.Lines = true;
                    job.RankKey = OmtRules.Fixed("rank");
                    job.TextKind = elevation ? OmtLabelText.NameAndField : OmtLabelText.Name;
                    job.FieldKey = OmtRules.Fixed("ele");
                    job.FieldSuffix = " m";
                    if (feet)
                    {
                        job.AltConditionKey = OmtRules.Fixed("customary_ft");
                        job.AltFieldKey = OmtRules.Fixed("ele_ft");
                        job.AltSuffix = " ft";
                    }
                    job.IconMode = OmtIconMode.Marker;
                    job.Spacing = 400f;
                    return job;
                }),
            }, output, "PeakLabels");
        }

#if UNITY_EDITOR
        protected override void ValidateMaterials() => CheckPriority(_lineMaterial, OmtDrawOrder.MountainLines, "Line Material");
#endif
    }
}
