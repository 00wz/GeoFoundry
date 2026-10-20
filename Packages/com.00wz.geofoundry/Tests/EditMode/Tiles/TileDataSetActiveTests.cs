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
    /// Tests <see cref="GeoFoundry.Tiles.TileData.SetActive"/>: toggling the root GameObject and forwarding
    /// <c>OnActiveChanged</c> to attached instances, including syncing a new instance with the current state on
    /// Attach (the same pattern <see cref="TileDataApplyPivotTests"/> checks for the pivot).
    /// </summary>
    public sealed class TileDataSetActiveTests
    {
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);

        private GameObject _tileRootGo;

        [TearDown]
        public void TearDown()
        {
            if (_tileRootGo != null) UnityEngine.Object.DestroyImmediate(_tileRootGo);
        }

        private sealed class FakeFloatingOriginController : IFloatingOriginController
        {
            public MercatorPivot Current { get; }
            public event Action<PivotChangedEventArgs> PivotChanged { add { } remove { } }

            public FakeFloatingOriginController(MercatorPivot initial) => Current = initial;
        }

        private sealed class RecordingLayerInstance : ILayerHandlerInstance
        {
            public int ActiveChangedCallCount { get; private set; }
            public bool LastActive { get; private set; }
            public TileMemoryUsage MemoryUsage => TileMemoryUsage.Zero;

            public void OnActiveChanged(bool active)
            {
                ActiveChangedCallCount++;
                LastActive = active;
            }

            public void Dispose() { }
        }

        private sealed class RecordingTerrainInstance : ITerrainHandlerInstance
        {
            public int ActiveChangedCallCount { get; private set; }
            public bool LastActive { get; private set; }
            public TileMemoryUsage MemoryUsage => TileMemoryUsage.Zero;

            public void OnActiveChanged(bool active)
            {
                ActiveChangedCallCount++;
                LastActive = active;
            }

            public void Dispose() { }
        }

        private GeoFoundry.Tiles.TileData MakeTileData()
        {
            _tileRootGo = new GameObject($"Tile_{Coord}");
            var controller = new FakeFloatingOriginController(MercatorPivot.FromLonLat(37.6175, 55.7520));
            return new GeoFoundry.Tiles.TileData(Coord, _tileRootGo, controller);
        }

        [Test]
        public void IsActive_DefaultsToTrue()
        {
            var tileData = MakeTileData();
            Assert.That(tileData.IsActive, Is.True);
        }

        [Test]
        public void SetActive_False_TogglesRootAndForwardsToAttachedInstances()
        {
            var tileData = MakeTileData();
            var layer = new RecordingLayerInstance();
            var terrain = new RecordingTerrainInstance();
            tileData.AttachLayerInstance(layer);
            tileData.AttachTerrain(terrain);

            // Attach on an active tile has already synced the instance with IsActive == true.
            Assert.That(layer.ActiveChangedCallCount, Is.EqualTo(1));
            Assert.That(layer.LastActive, Is.True);
            Assert.That(terrain.ActiveChangedCallCount, Is.EqualTo(1));
            Assert.That(terrain.LastActive, Is.True);

            tileData.SetActive(false);

            Assert.That(tileData.IsActive, Is.False);
            Assert.That(_tileRootGo.activeSelf, Is.False);
            Assert.That(layer.ActiveChangedCallCount, Is.EqualTo(2));
            Assert.That(layer.LastActive, Is.False);
            Assert.That(terrain.ActiveChangedCallCount, Is.EqualTo(2));
            Assert.That(terrain.LastActive, Is.False);
        }

        [Test]
        public void SetActive_True_AfterFalse_RestoresRootAndForwards()
        {
            var tileData = MakeTileData();
            var layer = new RecordingLayerInstance();
            tileData.AttachLayerInstance(layer);

            tileData.SetActive(false);
            tileData.SetActive(true);

            Assert.That(tileData.IsActive, Is.True);
            Assert.That(_tileRootGo.activeSelf, Is.True);
            Assert.That(layer.ActiveChangedCallCount, Is.EqualTo(3), "attach(1) + false(1) + true(1)");
            Assert.That(layer.LastActive, Is.True);
        }

        [Test]
        public void SetActive_SameValueAsCurrent_IsNoOp()
        {
            var tileData = MakeTileData();
            var layer = new RecordingLayerInstance();
            tileData.AttachLayerInstance(layer);
            int callsAfterAttach = layer.ActiveChangedCallCount;

            tileData.SetActive(true); // already true

            Assert.That(layer.ActiveChangedCallCount, Is.EqualTo(callsAfterAttach), "SetActive(true) on an already active tile forwards nothing");
        }

        [Test]
        public void AttachLayerInstance_WhileTileInactive_ImmediatelyDeliversInactiveState()
        {
            var tileData = MakeTileData();
            tileData.SetActive(false); // simulates a prefetch load: the tile is deactivated before Attach

            var lateLayer = new RecordingLayerInstance();
            tileData.AttachLayerInstance(lateLayer);

            Assert.That(lateLayer.ActiveChangedCallCount, Is.EqualTo(1));
            Assert.That(lateLayer.LastActive, Is.False);
        }
    }
}
