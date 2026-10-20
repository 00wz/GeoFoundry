using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Mvt;
using GeoFoundry.OpenMapTiles;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Geometry
{
    /// <summary>
    /// End-to-end tests of <see cref="AsyncBuildingPropertyReader"/>/<see cref="BuildingPropertiesJob"/> on a
    /// hand-built <see cref="NativeMvtTile"/> (its internal fields are visible to this assembly through
    /// <c>InternalsVisibleTo</c>). Cover both the job itself and the wiring of
    /// <c>NativeMvtLayer.FeatureTagRangeSlice</c>/<c>Tags</c>/<c>Keys</c>/<c>Values</c>. Independent of
    /// <see cref="BuildingBuildJob"/>/<see cref="AsyncGeometryWorker"/>; the geometry job is covered by
    /// <see cref="BuildingBuildJobTests"/>.
    /// </summary>
    public sealed class BuildingPropertiesJobTests
    {
        [UnityTest]
        public IEnumerator NoProperties_UsesDefaults() => Run(async ct =>
        {
            using var tile = MakeTile();
            var layer = tile.GetLayer(0);

            var props = await AsyncBuildingPropertyReader.ReadAsync(layer, ct);
            Assert.That(props, Is.Not.Null);
            try
            {
                Assert.That(props.Value.Hidden[0], Is.False);
                Assert.That(props.Value.TopMeters[0], Is.EqualTo(6f), "Default render_height");
                Assert.That(props.Value.BottomMeters[0], Is.EqualTo(0f), "Default render_min_height");
                Assert.That(props.Value.Colors[0], Is.EqualTo(new Color32(255, 255, 255, 255)),
                    "no colour gives opaque white (identity for albedo *= vertexColor)");
            }
            finally
            {
                props.Value.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator Hide3d_True_SetsHidden() => Run(async ct =>
        {
            using var tile = MakeTile(("hide_3d", MvtValue.OfBool(true)));
            var layer = tile.GetLayer(0);

            var props = await AsyncBuildingPropertyReader.ReadAsync(layer, ct);
            try
            {
                Assert.That(props.Value.Hidden[0], Is.True);
            }
            finally
            {
                props.Value.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator RenderHeightAndMinHeight_AreRead() => Run(async ct =>
        {
            using var tile = MakeTile(
                ("render_height", MvtValue.OfDouble(12.5)),
                ("render_min_height", MvtValue.OfDouble(2.0)));
            var layer = tile.GetLayer(0);

            var props = await AsyncBuildingPropertyReader.ReadAsync(layer, ct);
            try
            {
                Assert.That(props.Value.TopMeters[0], Is.EqualTo(12.5f).Within(1e-4f));
                Assert.That(props.Value.BottomMeters[0], Is.EqualTo(2.0f).Within(1e-4f));
            }
            finally
            {
                props.Value.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ColourHex_SixDigit_ParsesToColor32() =>
            AssertColour("#FF0000", new Color32(255, 0, 0, 255));

        [UnityTest]
        public IEnumerator ColourHex_ThreeDigit_NoHash_ParsesToColor32() =>
            AssertColour("F00", new Color32(255, 0, 0, 255));

        [UnityTest]
        public IEnumerator ColourHex_SixDigit_Mixed_ParsesToColor32() =>
            AssertColour("#112233", new Color32(0x11, 0x22, 0x33, 255));

        [UnityTest]
        public IEnumerator ColourHex_EightDigit_WithAlpha_ParsesToColor32() =>
            AssertColour("#FF000080", new Color32(255, 0, 0, 128));

        private static IEnumerator AssertColour(string hex, Color32 expected) => Run(async ct =>
        {
            using var tile = MakeTile(("colour", MvtValue.OfString(hex)));
            var layer = tile.GetLayer(0);

            var props = await AsyncBuildingPropertyReader.ReadAsync(layer, ct);
            try
            {
                Assert.That(props.Value.Colors[0], Is.EqualTo(expected));
            }
            finally
            {
                props.Value.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator ColourInvalid_LeavesOpaqueWhite() => Run(async ct =>
        {
            using var tile = MakeTile(("colour", MvtValue.OfString("not-a-color")));
            var layer = tile.GetLayer(0);

            var props = await AsyncBuildingPropertyReader.ReadAsync(layer, ct);
            try
            {
                Assert.That(props.Value.Colors[0], Is.EqualTo(new Color32(255, 255, 255, 255)));
            }
            finally
            {
                props.Value.Dispose();
            }
        });

        // ---- helpers --------------------------------------------------------

        /// <summary>
        /// Builds one layer "building" with one feature carrying the given properties and no geometry (the
        /// properties job does not need it). NativeMvtTile fields are internal and visible to this assembly through
        /// InternalsVisibleTo (GeoFoundry.Core/AssemblyInfo.cs).
        /// </summary>
        private static NativeMvtTile MakeTile(params (string key, MvtValue value)[] properties)
        {
            var keys = new List<FixedString64Bytes>(properties.Length);
            var values = new List<MvtValue>(properties.Length);
            var tags = new List<int2>(properties.Length);
            foreach (var (key, value) in properties)
            {
                FixedString64Bytes fs = default;
                fs.CopyFromTruncated(key);
                tags.Add(new int2(keys.Count, values.Count));
                keys.Add(fs);
                values.Add(value);
            }

            return new NativeMvtTile
            {
                LayerNames = new NativeArray<FixedString64Bytes>(new[] { (FixedString64Bytes)"building" }, Allocator.Persistent),
                LayerVersions = new NativeArray<uint>(new uint[] { 2 }, Allocator.Persistent),
                LayerExtents = new NativeArray<uint>(new uint[] { 4096 }, Allocator.Persistent),
                LayerFeatureRange = new NativeArray<int2>(new[] { new int2(0, 1) }, Allocator.Persistent),

                FeatureIds = new NativeArray<ulong>(new ulong[] { 1 }, Allocator.Persistent),
                FeatureGeomTypes = new NativeArray<MvtGeomType>(new[] { MvtGeomType.Polygon }, Allocator.Persistent),
                FeatureTagRange = new NativeArray<int2>(new[] { new int2(0, tags.Count) }, Allocator.Persistent),
                FeatureRingRange = new NativeArray<int2>(new[] { new int2(0, 0) }, Allocator.Persistent),

                Tags = new NativeArray<int2>(tags.ToArray(), Allocator.Persistent),
                Keys = new NativeArray<FixedString64Bytes>(keys.ToArray(), Allocator.Persistent),
                Values = new NativeArray<MvtValue>(values.ToArray(), Allocator.Persistent),

                Rings = new NativeArray<int2>(0, Allocator.Persistent),
                RingPoints = new NativeArray<int2>(0, Allocator.Persistent),
            };
        }

        private static IEnumerator Run(System.Func<CancellationToken, Awaitable> body, int timeoutSeconds = 30)
        {
            using var cts = new CancellationTokenSource(timeoutSeconds * 1000);
            var task = body(cts.Token);
            while (!task.GetAwaiter().IsCompleted)
            {
                yield return null;
                if (cts.Token.IsCancellationRequested) Assert.Fail($"Test timed out after {timeoutSeconds}s");
            }
            task.GetAwaiter().GetResult();
        }
    }
}
