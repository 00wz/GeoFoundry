using System.Collections.Generic;
using GeoFoundry.OpenMapTiles;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GeoFoundry.Tests.PlayMode.OpenMapTiles
{
    /// <summary>
    /// Label priorities of <see cref="OmtLabelRequestJob"/> stay the same between tiles of different zooms: only stable
    /// ranks order the labels, and ties are broken by the text, not by the feature order in the tile. Also the
    /// attribute filters of the handlers: <c>agg_stop</c>, rule rank limits, <c>iso_a2</c> and required attributes.
    /// </summary>
    public sealed class OmtLabelRequestJobTests
    {
        [Test]
        public void Priorities_UseOnlyStableRanks_AndTheKeyFollowsTheText()
        {
            // The same features as two zooms of the OpenMapTiles place layer would send them: grid ranks above 10
            // differ (Zamoskvorechye is rank 22 at z13, 13 at z14), the Natural Earth rank of the city does not.
            using var z13 = new OmtTestTile("place")
                .Point(100, 100, ("class", "city"), ("name", "Moscow"), ("rank", 1))
                .Point(200, 200, ("class", "suburb"), ("name", "Zamoskvorechye"), ("rank", 22))
                .Point(300, 300, ("class", "suburb"), ("name", "Basmanny"), ("rank", 13))
                .Build();
            using var z14 = new OmtTestTile("place")
                .Point(300, 300, ("class", "suburb"), ("name", "Basmanny"), ("rank", 11))
                .Point(200, 200, ("class", "suburb"), ("name", "Zamoskvorechye"), ("rank", 13))
                .Point(100, 100, ("class", "city"), ("name", "Moscow"), ("rank", 1))
                .Build();

            var a = Run(z13);
            var b = Run(z14);
            Assert.That(a["Moscow"].Priority, Is.EqualTo(1f), "a stable rank orders the labels");
            Assert.That(a["Zamoskvorechye"].Priority, Is.EqualTo(11f), "grid ranks above the limit do not");
            foreach (var name in new[] { "Moscow", "Zamoskvorechye", "Basmanny" })
            {
                Assert.That(b[name].Priority, Is.EqualTo(a[name].Priority), name);
                Assert.That(b[name].Key, Is.EqualTo(a[name].Key), name + ": the tie-break follows the text");
            }
            Assert.That(a["Zamoskvorechye"].Key, Is.Not.EqualTo(a["Basmanny"].Key));
        }

        [Test]
        public void Poi_AggregatedStops_KeepOnlyTheMainStopOfAGroup()
        {
            // The schema sets agg_stop=1 on every stop, except the secondary stops of a uic_ref group (no agg_stop).
            using var tile = new OmtTestTile("poi")
                .Point(100, 100, ("class", "bus"), ("subclass", "bus_stop"), ("name", "Main"), ("agg_stop", 1))
                .Point(110, 100, ("class", "bus"), ("subclass", "bus_stop"), ("name", "Secondary"))
                .Point(200, 200, ("class", "railway"), ("subclass", "tram_stop"), ("name", "Tram"), ("agg_stop", 1))
                .Point(300, 300, ("class", "cafe"), ("subclass", "cafe"), ("name", "Cafe"))
                .Build();
            var mods = new List<OmtModifier>();
            OmtPoiHandler.AddAggregateStopModifiers(mods);

            var result = Run(tile, OmtLabelDefaults.Poi(), "subclass", mods);
            Assert.That(result.Keys, Is.EquivalentTo(new[] { "Main", "Tram", "Cafe" }), "other POIs have no agg_stop");
        }

        [Test]
        public void Place_RuleRankLimit_AndHiddenCountries()
        {
            using var tile = new OmtTestTile("place")
                .Point(100, 100, ("class", "state"), ("name", "Big state"), ("rank", 2))
                .Point(200, 200, ("class", "state"), ("name", "Small state"), ("rank", 4))
                .Point(300, 300, ("class", "country"), ("name", "Italy"), ("iso_a2", "IT"), ("rank", 1))
                .Point(310, 300, ("class", "country"), ("name", "Vatican"), ("iso_a2", "VA"), ("rank", 6))
                .Build();
            var mods = new List<OmtModifier> { OmtModifier.Create("iso_a2", "VA", OmtModifierAction.Skip) };

            var result = Run(tile, OmtLabelDefaults.Place(), "capital", mods);
            Assert.That(result.Keys, Is.EquivalentTo(new[] { "Big state", "Italy" }), "states of rank 1–2 only; VA hidden");
        }

        [Test]
        public void RequiredAttribute_SkipsFeaturesWithoutIt()
        {
            using var tile = new OmtTestTile("aerodrome_label")
                .Point(100, 100, ("class", "international"), ("name", "Sheremetyevo"), ("iata", "SVO"))
                .Point(200, 200, ("class", "other"), ("name", "Airfield"), ("icao", "UUMB"))
                .Build();

            var result = Run(tile, OmtLabelDefaults.AerodromeLabel(), "", new List<OmtModifier>(), "iata");
            Assert.That(result.Keys, Is.EquivalentTo(new[] { "Sheremetyevo" }));
        }

        private static Dictionary<string, OmtLabelRequest> Run(GeoFoundry.Core.Mvt.NativeMvtTile tile)
        {
            var result = Run(tile, OmtLabelDefaults.Place(), "capital", new List<OmtModifier>());
            Assert.That(result.Count, Is.EqualTo(3));
            return result;
        }

        private static Dictionary<string, OmtLabelRequest> Run(GeoFoundry.Core.Mvt.NativeMvtTile tile,
            List<OmtLabelRule> ruleList, string subclassKey, List<OmtModifier> mods, string requireKey = "")
        {
            var rules = OmtLabelRules.ToNative(ruleList, 1f, Allocator.TempJob);
            var nameKeys = OmtRules.ToNative(new List<string> { "name" }, Allocator.TempJob);
            var icons = new NativeHashMap<FixedString64Bytes, int>(1, Allocator.TempJob);
            var modifiers = OmtModifiers.ToNative(mods, Allocator.TempJob);
            var output = new NativeList<OmtLabelRequest>(8, Allocator.TempJob);
            try
            {
                var layer = tile.GetLayer(0);
                new OmtLabelRequestJob
                {
                    GeomTypes = layer.FeatureGeomTypesSlice,
                    Props = OmtProps.From(layer),
                    Rules = rules,
                    NameKeys = nameKeys,
                    IconIds = icons,
                    Modifiers = modifiers,
                    ClassKey = OmtRules.Fixed("class"),
                    SubclassKey = OmtRules.Fixed(subclassKey),
                    Points = true,
                    RequireKey = OmtRules.Fixed(requireKey),
                    RankKey = OmtRules.Fixed("rank"),
                    StableRankLimit = 10,
                    Output = output,
                }.Run();

                var result = new Dictionary<string, OmtLabelRequest>();
                foreach (var request in output) result[request.Text.ToString()] = request;
                return result;
            }
            finally
            {
                rules.Dispose();
                nameKeys.Dispose();
                icons.Dispose();
                modifiers.Dispose();
                output.Dispose();
            }
        }
    }
}
