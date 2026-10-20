using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>housenumber</c> layer: <c>housenumber</c> labels at address points. House numbers
    /// avoid the labels of other layers of the tile but do not block them.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Housenumber", fileName = "OmtHousenumber")]
    public sealed class OmtHousenumberHandler : OmtLayerHandler
    {
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.Housenumber();

        public override string LayerName => "housenumber";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output) =>
            OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.ClassKey = default;
                    job.TextKind = OmtLabelText.Field;
                    job.FieldKey = OmtRules.Fixed("housenumber");
                    return job;
                }),
            }, output, "Housenumbers", occupy: false);
    }
}
