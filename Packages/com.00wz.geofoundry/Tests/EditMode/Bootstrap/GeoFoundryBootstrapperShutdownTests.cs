using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Bootstrap;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.Ground;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GeoFoundry.Tests.EditMode.Bootstrap
{
    /// <summary>
    /// Checks that <see cref="GeoFoundryBootstrapper"/> releases everything it owns on disable even if disposing
    /// one dependency throws; otherwise the remaining ones stay alive, including HTTP requests in flight that
    /// complete in the middle of scene teardown when leaving Play Mode. Dependencies are injected into fields via
    /// reflection, as in <c>PlayModeSmokeTests</c>.
    /// </summary>
    public sealed class GeoFoundryBootstrapperShutdownTests
    {
        private sealed class ThrowingVectorSource : IVectorTileSource, IDisposable
        {
            public Awaitable<NativeArray<byte>> GetTileAsync(TileCoord coord, Allocator allocator, CancellationToken ct) =>
                throw new NotSupportedException();

            public void Dispose() => throw new InvalidOperationException("vector source is broken");
        }

        private sealed class CountingDemSource : IDemTileSource, IDisposable
        {
            public int DisposeCount;

            public int TileSizePixels => 16;

            public Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct) =>
                throw new NotSupportedException();

            public void Dispose() => DisposeCount++;
        }

        private static FieldInfo Field(string name)
        {
            var field = typeof(GeoFoundryBootstrapper).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Field {name} not found; update the test.");
            return field;
        }

        [Test]
        public void OnDisable_OneDependencyThrowsOnDispose_OthersAreStillReleased()
        {
            var go = new GameObject("BootstrapperShutdownTest");
            go.SetActive(false); // OnEnable is not called; the test injects the dependencies
            try
            {
                var bootstrapper = go.AddComponent<GeoFoundryBootstrapper>();
                var dem = new CountingDemSource();
                var grids = new DemGroundGridProvider(dem, gridResolution: 4);
                Field("_ownedGridProvider").SetValue(bootstrapper, grids);
                Field("_ownedVectorSource").SetValue(bootstrapper, new ThrowingVectorSource());
                Field("_ownedDemSource").SetValue(bootstrapper, dem);

                LogAssert.Expect(LogType.Exception, new Regex("vector source is broken"));
                var onDisable = typeof(GeoFoundryBootstrapper).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(onDisable, Is.Not.Null);
                onDisable.Invoke(bootstrapper, null);

                Assert.That(dem.DisposeCount, Is.EqualTo(1), "source after the broken one is still disposed");
                Assert.Throws<ObjectDisposedException>(
                    () => grids.AcquireAsync(new TileCoord(1, 0, 0), CancellationToken.None).GetAwaiter().GetResult(),
                    "grid provider is disposed");
                foreach (var name in new[] { "_manager", "_originController", "_ownedGridProvider", "_ownedVectorSource", "_ownedDemSource", "_ownedDownloader" })
                    Assert.That(Field(name).GetValue(bootstrapper), Is.Null, $"{name} is cleared");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
