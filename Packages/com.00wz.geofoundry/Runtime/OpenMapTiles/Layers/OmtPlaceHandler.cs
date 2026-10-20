using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>place</c> layer: names of continents, countries, states, cities, towns, villages,
    /// suburbs, neighbourhoods, islands and aboriginal lands, styled by <c>class</c> and <c>capital</c> (capitals get a
    /// star marker) and placed by <c>rank</c>. <c>iso_a2</c> is available to the rules through the class key only.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Place", fileName = "OmtPlace")]
    public sealed class OmtPlaceHandler : OmtLayerHandler
    {
        [Tooltip("Label settings (fonts, materials, icon atlas).")]
        [SerializeField] private OmtTextSettings _text;

        [Tooltip("Label styles matched by class and capital (the admin level a city is the capital of, e.g. \"2\").")]
        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.Place();

        public override string LayerName => "place";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output) =>
            OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.SubclassKey = OmtRules.Fixed("capital");
                    job.RankKey = OmtRules.Fixed("rank");
                    job.IconMode = OmtIconMode.Marker;
                    return job;
                }),
            }, output, "PlaceLabels");
    }
}
