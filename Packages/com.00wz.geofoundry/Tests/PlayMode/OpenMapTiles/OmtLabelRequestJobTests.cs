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
    /// ranks order the labels, and ties are broken by the text, not by the feature order in the tile.
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

        private static Dictionary<string, OmtLabelRequest> Run(GeoFoundry.Core.Mvt.NativeMvtTile tile)
        {
            var rules = OmtLabelRules.ToNative(OmtLabelDefaults.Place(), 1f, Allocator.TempJob);
            var nameKeys = OmtRules.ToNative(new List<string> { "name" }, Allocator.TempJob);
            var icons = new NativeHashMap<FixedString64Bytes, int>(1, Allocator.TempJob);
            var modifiers = new NativeArray<OmtModifier>(0, Allocator.TempJob);
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
                    SubclassKey = OmtRules.Fixed("capital"),
                    Points = true,
                    RankKey = OmtRules.Fixed("rank"),
                    StableRankLimit = 10,
                    Output = output,
                }.Run();

                var result = new Dictionary<string, OmtLabelRequest>();
                foreach (var request in output) result[request.Text.ToString()] = request;
                Assert.That(result.Count, Is.EqualTo(3));
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
