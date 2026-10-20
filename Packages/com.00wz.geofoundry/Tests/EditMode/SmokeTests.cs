using NUnit.Framework;
using GeoFoundry.Bootstrap;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode
{
    /// <summary>
    /// Smoke tests: the asmdef graph compiles, public types are accessible and config defaults are sensible.
    /// </summary>
    public sealed class SmokeTests
    {
        [Test]
        public void Config_DefaultEndpoints_AreEmpty()
        {
            var cfg = ScriptableObject.CreateInstance<GeoFoundryConfig>();
            try
            {
                // Server addresses are up to the consuming project; the package ships with none by default.
                Assert.That(cfg.VectorTilesEndpointTemplate, Is.Empty);
                Assert.That(cfg.DemTilesEndpointTemplate, Is.Empty);
                // Lod.MaxZoom = 14 matches the OpenMapTiles schema, whose maxzoom is 14.
                Assert.That(cfg.Lod.MaxZoom, Is.EqualTo(14));
            }
            finally
            {
                ScriptableObject.DestroyImmediate(cfg);
            }
        }

        [Test]
        public void Config_OriginLonLat_IsMoscowCenter()
        {
            var cfg = ScriptableObject.CreateInstance<GeoFoundryConfig>();
            try
            {
                var origin = cfg.GetOriginLonLat();
                Assert.That(origin.x, Is.InRange(37.5, 37.7), "longitude is near central Moscow");
                Assert.That(origin.y, Is.InRange(55.7, 55.8), "latitude is near central Moscow");
            }
            finally
            {
                ScriptableObject.DestroyImmediate(cfg);
            }
        }
    }
}
