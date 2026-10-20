using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>poi</c> layer: points of interest as icons named after <c>subclass</c> (falling
    /// back to <c>class</c>) with their <c>name</c>, colored and zoom-gated by class, placed by <c>rank</c>. Indoor POIs
    /// (<c>indoor=1</c>) and POIs on other <c>level</c>s can be hidden; with <c>agg_stop</c> only one stop of a group of
    /// equally named public transport stops is labeled.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/POI", fileName = "OmtPoi")]
    public sealed class OmtPoiHandler : OmtLayerHandler
    {
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.Poi();

        [Tooltip("POIs with a higher rank are not shown; 0 shows all.")]
        [SerializeField, Min(0)] private int _maxRank;

        [Tooltip("Hides indoor POIs (indoor=1).")]
        [SerializeField] private bool _hideIndoor;

        [Tooltip("Hides POIs whose level is not 0.")]
        [SerializeField] private bool _hideOtherLevels;

        [Tooltip("Shows only the aggregated stop (agg_stop=1) of grouped public transport stops.")]
        [SerializeField] private bool _aggregateStops = true;

        public override string LayerName => "poi";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            var mods = new List<OmtModifier>();
            if (_hideIndoor) mods.Add(OmtModifier.Create("indoor", "", OmtModifierAction.Skip));
            if (_hideOtherLevels)
            {
                var level = OmtModifier.Create("level", "0", OmtModifierAction.Skip);
                level.Negate = true;
                mods.Add(level);
            }
            if (_aggregateStops)
            {
                // Stops of an aggregated group carry agg_stop; only the one marked 1 is kept.
                var agg = OmtModifier.Create("agg_stop", "1", OmtModifierAction.Skip);
                agg.Negate = true;
                mods.Add(agg);
            }

            int maxRank = _maxRank;
            return OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.SubclassKey = OmtRules.Fixed("subclass");
                    job.RankKey = OmtRules.Fixed("rank");
                    job.MaxRank = maxRank;
                    job.IconMode = OmtIconMode.Marker;
                    job.FallbackIcon = OmtRules.Fixed("{class}");
                    return job;
                }, mods),
            }, output, "PoiLabels");
        }
    }
}
