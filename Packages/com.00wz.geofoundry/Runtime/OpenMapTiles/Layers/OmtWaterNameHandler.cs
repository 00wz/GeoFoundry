using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>water_name</c> layer: names of oceans, seas, bays, straits and lakes at their
    /// label points, and of lakes along their center lines, styled by <c>class</c>; <c>intermittent</c> lakes can be
    /// hidden.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Water Name", fileName = "OmtWaterName")]
    public sealed class OmtWaterNameHandler : OmtLayerHandler
    {
        [SerializeField] private OmtTextSettings _text;

        [Tooltip("Styles of point labels by class.")]
        [SerializeField] private List<OmtLabelRule> _points = OmtLabelDefaults.WaterName();

        [Tooltip("Styles of labels along lake center lines.")]
        [SerializeField] private List<OmtLabelRule> _lines = OmtLabelDefaults.WaterNameLines();

        [SerializeField] private bool _hideIntermittent;

        public override string LayerName => "water_name";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            var mods = new List<OmtModifier>();
            if (_hideIntermittent) mods.Add(OmtModifier.Create("intermittent", "", OmtModifierAction.Skip));
            return OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_points, null, mods),
                new OmtLabelPass(_lines, job =>
                {
                    job.Points = false;
                    job.Lines = true;
                    job.Spacing = 500f;
                    return job;
                }, mods),
            }, output, "WaterNames");
        }
    }
}
