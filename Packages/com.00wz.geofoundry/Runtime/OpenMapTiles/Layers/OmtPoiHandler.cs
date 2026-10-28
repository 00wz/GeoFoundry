using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>poi</c> layer: points of interest as icons named after <c>subclass</c> (falling
    /// back to <c>class</c>) with their <c>name</c>, colored, zoom-gated and placed by class (<c>rank</c>, local to each zoom's label grid in the schema, only
    /// filters). Indoor POIs
    /// (<c>indoor=1</c>) and POIs on other <c>level</c>s can be hidden. The schema sets <c>agg_stop=1</c> on every
    /// public transport stop (bus_stop, bus_station, tram_stop, subway) except the secondary stops of a group sharing
    /// a <c>uic_ref</c>, which have no <c>agg_stop</c>; with <see cref="_aggregateStops"/> those are not labeled.
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

        [Tooltip("Shows only the main stop of grouped public transport stops (stops without agg_stop are hidden).")]
        [SerializeField] private bool _aggregateStops = true;

        /// <summary>Subclasses of public transport stops, which carry <c>agg_stop</c> in the schema.</summary>
        private static readonly string[] StopSubclasses = { "bus_stop", "bus_station", "tram_stop", "subway" };

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
            if (_aggregateStops) AddAggregateStopModifiers(mods);

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

        /// <summary>Adds the modifiers skipping public transport stops without <c>agg_stop</c>.</summary>
        internal static void AddAggregateStopModifiers(List<OmtModifier> mods)
        {
            foreach (var stop in StopSubclasses)
                mods.Add(OmtModifier.Create("subclass", stop, OmtModifierAction.Skip).And("agg_stop", "", negate: true));
        }
    }
}
