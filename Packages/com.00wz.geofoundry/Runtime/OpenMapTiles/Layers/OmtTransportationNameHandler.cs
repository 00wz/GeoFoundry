using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Handler of the OpenMapTiles <c>transportation_name</c> layer: street names along the lines (by <c>class</c>,
    /// <c>name</c> in the preferred language), road shields with <c>ref</c> (falling back to <c>route_1_ref</c>) on the
    /// shield icon of the class, and motorway junction labels at <c>subclass=junction</c> points.
    /// </summary>
    /// <remarks>
    /// <c>network</c>, <c>ref_length</c> and the other <c>route_n_*</c> concurrencies are not drawn separately: the
    /// shield background is chosen by class and stretched to the text. <c>brunnel</c>, <c>level</c>, <c>layer</c> and
    /// <c>indoor</c> can be used to hide labels through <see cref="_hideIndoor"/>.
    /// </remarks>
    [CreateAssetMenu(menuName = "GeoFoundry/Layers/OpenMapTiles/Transportation Name", fileName = "OmtTransportationName")]
    public sealed class OmtTransportationNameHandler : OmtLayerHandler
    {
        [SerializeField] private OmtTextSettings _text;

        [SerializeField] private List<OmtLabelRule> _names = OmtLabelDefaults.TransportationNames();

        [SerializeField] private List<OmtLabelRule> _shields = OmtLabelDefaults.TransportationShields();

        [SerializeField] private List<OmtLabelRule> _junctions = OmtLabelDefaults.TransportationJunctions();

        [Tooltip("Hides names of indoor ways (indoor=1).")]
        [SerializeField] private bool _hideIndoor = true;

        public override string LayerName => "transportation_name";

        protected override Awaitable BuildAsync(LayerLoadContext ctx, OmtTileOutput output)
        {
            var mods = new List<OmtModifier>();
            if (_hideIndoor) mods.Add(OmtModifier.Create("indoor", "", OmtModifierAction.Skip));
            return OmtLabels.BuildAsync(ctx, _text, new[]
            {
                new OmtLabelPass(_names, job =>
                {
                    job.Points = false;
                    job.Lines = true;
                    job.Spacing = 300f;
                    return job;
                }, mods),
                new OmtLabelPass(_shields, job =>
                {
                    job.Points = false;
                    job.Lines = true;
                    job.TextKind = OmtLabelText.Field;
                    job.FieldKey = OmtRules.Fixed("ref");
                    job.FallbackFieldKey = OmtRules.Fixed("route_1_ref");
                    job.IconMode = OmtIconMode.Shield;
                    job.Spacing = 500f;
                    return job;
                }, mods),
                new OmtLabelPass(_junctions, job =>
                {
                    job.SubclassKey = OmtRules.Fixed("subclass");
                    job.TextKind = OmtLabelText.Field;
                    job.FieldKey = OmtRules.Fixed("ref");
                    job.FallbackFieldKey = OmtRules.Fixed("name");
                    return job;
                }, mods),
            }, output, "TransportationNames");
        }
    }
}
