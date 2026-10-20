using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.Ground;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Ground
{
    /// <summary>
    /// <see cref="GroundGridHeightProvider"/> takes the height from the most detailed loaded grid that contains the
    /// point. The fake DEM is flat at height <see cref="HeightOf"/> of the tile zoom, so the height shows which grid
    /// it came from.
    /// </summary>
    public sealed class GroundGridHeightProviderTests
    {
        private static readonly double2 Point = MercatorProjection.LonLatToMeters(37.6175, 55.7520);

        private static float HeightOf(int zoom) => zoom * 100f;

        private sealed class ZoomHeightDemSource : IDemTileSource
        {
            public int TileSizePixels => 16;

            public Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
            {
                var heights = new NativeArray<float>(TileSizePixels * TileSizePixels, allocator);
                for (int i = 0; i < heights.Length; i++) heights[i] = HeightOf(coord.Z);
                var completion = new AwaitableCompletionSource<NativeArray<float>>();
                completion.SetResult(heights);
                return completion.Awaitable;
            }
        }

        private DemGroundGridProvider _grids;

        [TearDown]
        public void TearDown()
        {
            _grids?.Dispose();
            _grids = null;
        }

        private static TileCoord TileAt(int zoom)
        {
            var xy = MercatorProjection.MercatorToTile(Point, zoom);
            return new TileCoord(zoom, xy.x, xy.y);
        }

        [UnityTest]
        public IEnumerator TryGetHeight_TakesFinestLoadedGrid()
        {
            _grids = new DemGroundGridProvider(new ZoomHeightDemSource(), gridResolution: 4);
            var heights = new GroundGridHeightProvider(_grids);
            Assert.That(heights.TryGetHeight(Point, out _), Is.False, "no grids");

            var coarseLoad = _grids.AcquireAsync(TileAt(5), CancellationToken.None);
            var fineLoad = _grids.AcquireAsync(TileAt(8), CancellationToken.None);
            for (int i = 0; i < 100 && !(coarseLoad.GetAwaiter().IsCompleted && fineLoad.GetAwaiter().IsCompleted); i++)
                yield return null;
            Assert.That(coarseLoad.GetAwaiter().IsCompleted && fineLoad.GetAwaiter().IsCompleted, Is.True, "grids did not load in time");
            var coarse = coarseLoad.GetAwaiter().GetResult();
            var fine = fineLoad.GetAwaiter().GetResult();

            Assert.That(heights.TryGetHeight(Point, out float height), Is.True);
            Assert.That(height, Is.EqualTo(HeightOf(8)).Within(1e-3f), "the most detailed loaded grid");

            fine.Dispose();
            Assert.That(heights.TryGetHeight(Point, out height), Is.True);
            Assert.That(height, Is.EqualTo(HeightOf(5)).Within(1e-3f), "fine grid released, the next most detailed one is used");

            coarse.Dispose();
            Assert.That(heights.TryGetHeight(Point, out _), Is.False, "all grids released");
        }
    }
}
