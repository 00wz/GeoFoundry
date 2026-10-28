using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>aerodrome_label</c> layer: airport markers with the <c>name</c>, prioritized by
    /// <c>class</c> (international first). Like the style, only airports with an IATA code (<c>iata</c>) are labeled
    /// unless <see cref="_requireIata"/> is off. The IATA code (falling back to <c>icao</c>) or the elevation
    /// (<c>ele</c>) can be shown below the name.
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
        [SerializeField] private SecondLine _secondLine = SecondLine.None;

        [Tooltip("Labels only airports with an IATA code, as the style does.")]
        [SerializeField] private bool _requireIata = true;

        public override string LayerName => "aerodrome_label";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            var second = _secondLine;
            bool requireIata = _requireIata;
            return OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_labels, job =>
                {
                    job.IconMode = OmtIconMode.Marker;
                    if (requireIata) job.RequireKey = OmtRules.Fixed("iata");
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
