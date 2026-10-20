using System;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Tiles;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>
    /// Tests <see cref="GeoFoundry.Tiles.TileData"/>, including that it subscribes to
    /// <see cref="IFloatingOriginController.PivotChanged"/> in its constructor (before any content exists) and
    /// syncs each attached instance with the current pivot immediately rather than in a batch at the end. This
    /// prevents a race between a rebase and an asynchronous tile build.
    /// </summary>
    public sealed class TileDataApplyPivotTests
    {
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);

        private GameObject _tileRootGo;

        [TearDown]
        public void TearDown()
        {
            if (_tileRootGo != null) UnityEngine.Object.DestroyImmediate(_tileRootGo);
        }

        /// <summary>Minimal test double: mutable Current plus a manual Raise(...) to simulate a rebase.</summary>
        private sealed class FakeFloatingOriginController : IFloatingOriginController
        {
            public MercatorPivot Current { get; private set; }
            public event Action<PivotChangedEventArgs> PivotChanged;

            public FakeFloatingOriginController(MercatorPivot initial) => Current = initial;

            public void Raise(MercatorPivot newPivot)
            {
                var old = Current;
                Current = newPivot;
                PivotChanged?.Invoke(new PivotChangedEventArgs(old, Current));
            }
        }

        private sealed class RecordingLayerInstance : ILayerHandlerInstance
        {
            public int CallCount { get; private set; }
            public MercatorPivot? LastPivot { get; private set; }
            public TileMemoryUsage MemoryUsage => TileMemoryUsage.Zero;

            public void OnOriginShifted(MercatorPivot newPivot)
            {
                CallCount++;
                LastPivot = newPivot;
            }

            public void Dispose() { }
        }

        private sealed class RecordingTerrainInstance : ITerrainHandlerInstance
        {
            public int CallCount { get; private set; }
            public TileMemoryUsage MemoryUsage => TileMemoryUsage.Zero;
            public void OnOriginShifted(MercatorPivot newPivot) => CallCount++;
            public void Dispose() { }
        }

        private GeoFoundry.Tiles.TileData MakeTileData(FakeFloatingOriginController controller)
        {
            _tileRootGo = new GameObject($"Tile_{Coord}");
            return new GeoFoundry.Tiles.TileData(Coord, _tileRootGo, controller);
        }

        [Test]
        public void Constructor_PositionsRootFromControllerCurrent_Immediately()
        {
            var pivot = MercatorPivot.FromLonLat(37.6175, 55.7520);
            var controller = new FakeFloatingOriginController(pivot);

            MakeTileData(controller); // no external ApplyPivot call

            var expected = pivot.WorldFromMercator(Coord.CenterMeters());
            var actual = _tileRootGo.transform.localPosition;
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-3f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-3f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(1e-3f));
        }

        [Test]
        public void PivotChanged_RepositionsRoot_AndNotifiesAlreadyAttachedInstances()
        {
            var pivotA = MercatorPivot.FromLonLat(37.6175, 55.7520);
            var controller = new FakeFloatingOriginController(pivotA);
            var tileData = MakeTileData(controller);

            var layerRecorder = new RecordingLayerInstance();
            var terrainRecorder = new RecordingTerrainInstance();
            tileData.AttachLayerInstance(layerRecorder);
            tileData.AttachTerrain(terrainRecorder);
            Assert.That(layerRecorder.CallCount, Is.EqualTo(1), "Attach syncs with the current pivot by itself");
            Assert.That(terrainRecorder.CallCount, Is.EqualTo(1));

            var pivotB = MercatorPivot.FromLonLat(37.62, 55.75);
            controller.Raise(pivotB); // simulated rebase; TileData is subscribed itself, nothing needs to be called externally

            var expected = pivotB.WorldFromMercator(Coord.CenterMeters());
            var actual = _tileRootGo.transform.localPosition;
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-3f));
            Assert.That(layerRecorder.CallCount, Is.EqualTo(2));
            Assert.That(layerRecorder.LastPivot.Value.PivotMeters, Is.EqualTo(pivotB.PivotMeters));
            Assert.That(terrainRecorder.CallCount, Is.EqualTo(2));
        }

        [Test]
        public void AttachLayerInstance_AfterRebase_SyncsToNewPivot_NotStaleOne()
        {
            // The rebase happens before the instance exists (while the tile is still building); Attach must pick up
            // the current (new) pivot immediately, not the one from when TileData was created.
            var pivotA = MercatorPivot.FromLonLat(37.6175, 55.7520);
            var controller = new FakeFloatingOriginController(pivotA);
            var tileData = MakeTileData(controller);

            var pivotB = MercatorPivot.FromLonLat(37.62, 55.75);
            controller.Raise(pivotB); // rebase "during the build", before Attach

            var lateInstance = new RecordingLayerInstance();
            tileData.AttachLayerInstance(lateInstance);

            Assert.That(lateInstance.CallCount, Is.EqualTo(1));
            Assert.That(lateInstance.LastPivot.Value.PivotMeters, Is.EqualTo(pivotB.PivotMeters),
                "instance attached after a rebase gets the new pivot, not a stale one");
        }

        [Test]
        public void ApplyPivot_RecomputesFromScratch_NotAccumulated()
        {
            var pivotA = MercatorPivot.FromLonLat(37.6175, 55.7520);
            var controller = new FakeFloatingOriginController(pivotA);
            var tileData = MakeTileData(controller);
            var afterA = _tileRootGo.transform.localPosition;

            var pivotB = MercatorPivot.FromLonLat(37.62, 55.75);
            controller.Raise(pivotB);
            var afterB = _tileRootGo.transform.localPosition;
            Assert.That(afterB, Is.Not.EqualTo(afterA), "different pivots give different positions");

            // Returning to pivotA must give exactly the same position as the first time; accumulating deltas instead
            // of recomputing from Coord would drift.
            controller.Raise(pivotA);
            var afterA2 = _tileRootGo.transform.localPosition;
            Assert.That(afterA2.x, Is.EqualTo(afterA.x).Within(1e-4f));
            Assert.That(afterA2.y, Is.EqualTo(afterA.y).Within(1e-4f));
            Assert.That(afterA2.z, Is.EqualTo(afterA.z).Within(1e-4f));
        }

        [Test]
        public void Dispose_UnsubscribesFromPivotChanged()
        {
            var pivotA = MercatorPivot.FromLonLat(37.6175, 55.7520);
            var controller = new FakeFloatingOriginController(pivotA);
            var tileData = MakeTileData(controller);

            tileData.Dispose();
            _tileRootGo = null; // already destroyed inside Dispose()

            // Had TileData not unsubscribed, this Raise would access the destroyed GameObject from ApplyPivot and throw
            // MissingReferenceException.
            var pivotB = MercatorPivot.FromLonLat(37.62, 55.75);
            Assert.DoesNotThrow(() => controller.Raise(pivotB));
        }
    }
}
