using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>place</c> layer: names of continents, countries, states, cities, towns, villages,
    /// suburbs, neighbourhoods, islands and aboriginal lands, styled by <c>class</c> and <c>capital</c> (capitals get a
    /// star marker) and placed by class, then by <c>rank</c> up to 10 (the global Natural Earth ranks; higher ranks are
    /// local to each zoom's label grid in the schema). Rules can limit the rank (<see cref="OmtLabelRule.MaxRank"/>:
    /// states of rank 1–2, as in the style). Countries whose <c>iso_a2</c> is listed in <see cref="_hiddenCountries"/>
    /// are not labeled (the style hides Vatican City, <c>VA</c>, next to Rome).
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Place", fileName = "OmtPlace")]
    public sealed class OmtPlaceHandler : OmtLayerHandler
    {
        [Tooltip("Label settings (fonts, materials, icon atlas).")]
        [SerializeField] private OmtTextSettings _text;

        [Tooltip("Label styles matched by class and capital (the admin level a city is the capital of, e.g. \"2\").")]
        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.Place();

        [Tooltip("ISO 3166-1 alpha-2 codes (iso_a2) of countries that are not labeled.")]
        [SerializeField] private List<string> _hiddenCountries = new List<string> { "VA" };

        public override string LayerName => "place";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            var mods = new List<OmtModifier>();
            if (_hiddenCountries != null)
                foreach (var code in _hiddenCountries)
                    if (!string.IsNullOrEmpty(code)) mods.Add(OmtModifier.Create("iso_a2", code, OmtModifierAction.Skip));
            return OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.SubclassKey = OmtRules.Fixed("capital");
                    job.RankKey = OmtRules.Fixed("rank");
                    job.StableRankLimit = 10; // Natural Earth ranks; higher city ranks are grid ranks
                    job.IconMode = OmtIconMode.Marker;
                    return job;
                }, mods),
            }, output, "PlaceLabels");
        }
    }
}
