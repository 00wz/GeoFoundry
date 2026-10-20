using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>aerodrome_label</c> layer: airport markers with the <c>name</c> and the IATA
    /// code (<c>iata</c>, falling back to <c>icao</c>), prioritized by <c>class</c> (international first). The
    /// elevation (<c>ele</c>/<c>ele_ft</c>) can be shown instead of the code.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Aerodrome Label", fileName = "OmtAerodromeLabel")]
    public sealed class OmtAerodromeLabelHandler : OmtLayerHandler
    {
        public enum SecondLine
        {
            None,
            Code,
            Elevation,
        }

        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _labels = OmtLabelDefaults.AerodromeLabel();

        [Tooltip("What is shown below the name.")]
        [SerializeField] private SecondLine _secondLine = SecondLine.Code;

        public override string LayerName => "aerodrome_label";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            var second = _secondLine;
            return OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.IconMode = OmtIconMode.Marker;
                    switch (second)
                    {
                        case SecondLine.Code:
                            job.TextKind = OmtLabelText.NameAndField;
                            job.FieldKey = OmtRules.Fixed("iata");
                            job.FallbackFieldKey = OmtRules.Fixed("icao");
                            break;
                        case SecondLine.Elevation:
                            job.TextKind = OmtLabelText.NameAndField;
                            job.FieldKey = OmtRules.Fixed("ele");
                            job.FieldSuffix = " m";
                            break;
                    }
                    return job;
                }),
            }, output, "AerodromeLabels");
        }
    }
}
