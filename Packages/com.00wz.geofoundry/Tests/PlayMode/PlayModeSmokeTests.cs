using System.Collections;
using NUnit.Framework;
using GeoFoundry.Bootstrap;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode
{
    /// <summary>
    /// PlayMode smoke tests: check that GeoFoundryBootstrapper can be created in a scene without errors.
    /// </summary>
    public sealed class PlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator Bootstrapper_CanBeAddedToScene_WithoutErrors()
        {
            // The GameObject is inactive, so OnEnable is not called and initialization does not start.
            var go = new GameObject("BootstrapperTest");
            go.SetActive(false);
            try
            {
                var bootstrapper = go.AddComponent<GeoFoundryBootstrapper>();
                yield return null;

                Assert.That(bootstrapper, Is.Not.Null);
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        [UnityTest]
        public IEnumerator Bootstrapper_WithConfig_CanBeEnabled_WithoutCrash()
        {
            // The GameObject is inactive; otherwise OnEnable would start loading tiles in the background.
            // Only checks that adding the component with a config does not crash.
            var go = new GameObject("BootstrapperTest");
            go.SetActive(false);
            var cfg = ScriptableObject.CreateInstance<GeoFoundryConfig>();
            try
            {
                var bootstrapper = go.AddComponent<GeoFoundryBootstrapper>();

                var configField = typeof(GeoFoundryBootstrapper).GetField("_config",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                configField?.SetValue(bootstrapper, cfg);

                yield return null;

                Assert.That(bootstrapper, Is.Not.Null);
            }
            finally
            {
                Object.Destroy(go);
                Object.DestroyImmediate(cfg);
            }
        }
    }
}
