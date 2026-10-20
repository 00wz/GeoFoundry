using NUnit.Framework;
using GeoFoundry.Core.Ground;
using GeoFoundry.Ground;
using Unity.Collections;

namespace GeoFoundry.Tests.EditMode.Ground
{
    public sealed class TerrainMeshTests
    {
        // ---- PaddedHeightSampler (bilinear over 9 DEM tiles) ----------------

        [Test]
        public void PaddedSampler_InteriorPoint_MatchesNearestPixel()
        {
            // At an interior point bilinear sampling must stay close to the nearest pixel.
            const int demSize = 8;
            var c = NewDem(demSize, (i, j) => i + j * 10f);
            var s = new PaddedHeightSampler(c, demSize,
                north: default, south: default, east: default, west: default,
                northEast: default, northWest: default, southEast: default, southWest: default);
            try
            {
                // u=0.5, v=0.5 in an 8×8 DEM: fpx = 4 - 0.5 = 3.5, fpy = 3.5.
                // Bilinear between (3,3), (4,3), (3,4), (4,4): values 33, 34, 43, 44 → avg = 38.5.
                Assert.That(s.SampleBilinear(0.5, 0.5), Is.EqualTo(38.5f).Within(0.01f));
            }
            finally { c.Dispose(); }
        }

        [Test]
        public void PaddedSampler_EastEdge_AveragesAandE()
        {
            // u=1.0 (east edge of A) → fpx = demSize - 0.5 = 7.5.
            // Bilinear between A.pixel[7] (value=7) and E.pixel[0] (value=100) = (7+100)/2 = 53.5.
            const int demSize = 8;
            var a = NewDem(demSize, (i, j) => i);
            var e = NewDem(demSize, (i, j) => i + 100);
            var s = new PaddedHeightSampler(a, demSize,
                north: default, south: default, east: e, west: default,
                northEast: default, northWest: default, southEast: default, southWest: default);
            try
            {
                Assert.That(s.SampleBilinear(1.0, 0.5), Is.EqualTo(53.5f).Within(0.01f));
            }
            finally { a.Dispose(); e.Dispose(); }
        }

        [Test]
        public void PaddedSampler_WestEdge_AveragesAandW()
        {
            // u=0.0 (west edge) → fpx = -0.5.
            // Bilinear between W.pixel[demSize-1] (last column, value = (demSize-1) + 1000 = 7 + 1000 = 1007)
            // and A.pixel[0] (value=0) = (1007+0)/2 = 503.5.
            const int demSize = 8;
            var a = NewDem(demSize, (i, j) => i);
            var w = NewDem(demSize, (i, j) => i + 1000);
            var s = new PaddedHeightSampler(a, demSize,
                north: default, south: default, east: default, west: w,
                northEast: default, northWest: default, southEast: default, southWest: default);
            try
            {
                Assert.That(s.SampleBilinear(0.0, 0.5), Is.EqualTo(503.5f).Within(0.01f));
            }
            finally { a.Dispose(); w.Dispose(); }
        }

        [Test]
        public void PaddedSampler_AdjacentTiles_AgreeOnSharedEdge()
        {
            // Core requirement: the A.east_edge sample equals the B.west_edge sample.
            // Model: a shared "world DEM" covers the area around A and B, and tile (gx, gy) gets its piece via
            // GetDem(gx, gy). Then A's SE equals B's S (the same tile), and bilinear sampling at a corner point
            // uses the same pixel values from both sides.
            const int demSize = 16;
            using var dems = new WorldDems(demSize, fn: (gx, gy, i, j) =>
                10f + (gx * demSize + i) * 0.5f + (gy * demSize + j) * 0.3f);

            // A at world (0, 0), B at world (1, 0).
            var sa = dems.MakeSampler(gx: 0, gy: 0);
            var sb = dems.MakeSampler(gx: 1, gy: 0);

            for (double v = 0; v <= 1.0; v += 0.1)
            {
                float a_east = sa.SampleBilinear(1.0, v);
                float b_west = sb.SampleBilinear(0.0, v);
                Assert.That(a_east, Is.EqualTo(b_west).Within(0.001f),
                    $"v={v:F2}: A.east_edge ({a_east}) matches B.west_edge ({b_west})");
            }
        }

        [Test]
        public void PaddedSampler_NoNeighbour_FallsBackToGlobalFallbackHeight()
        {
            // No neighbors: at u=1 → fpx=demSize-0.5 → bilinear between px=demSize-1 (own A, value=7)
            // and px=demSize (no neighbor → GroundHeightDefaults.FallbackHeightMeters=0), weights 0.5/0.5.
            // ReadPixel returns the same fallback for any missing DEM (central or neighbor), so the result is a
            // bilinear blend between the real central DEM and the fallback rather than clamping to the central one.
            const int demSize = 8;
            var a = NewDem(demSize, (i, j) => i);
            var s = new PaddedHeightSampler(a, demSize,
                north: default, south: default, east: default, west: default,
                northEast: default, northWest: default, southEast: default, southWest: default);
            try
            {
                float expected = (7f + GroundHeightDefaults.FallbackHeightMeters) / 2f;
                Assert.That(s.SampleBilinear(1.0, 0.5), Is.EqualTo(expected).Within(0.01f),
                    "without an east neighbor the sample blends central.pixel[last] with the fallback height");
            }
            finally { a.Dispose(); }
        }

        [Test]
        public void PaddedSampler_NaNPixel_TreatedAsFallbackHeight()
        {
            // A NaN inside an otherwise valid DEM array is a nodata pixel, exactly as TerrariumDecoder and
            // DecodeTerrariumJob decode alpha=0. To isolate this from the missing-neighbor case (see the test above),
            // the rest of the central DEM is real (=50) and only the interior pixel (4,4) is NaN. The sampled cell
            // (3,3) has exactly one of its 4 corners at that NaN pixel.
            const int demSize = 8;
            var a = NewDem(demSize, (i, j) => 50f);
            a[4 * demSize + 4] = float.NaN; // pixel (i=4, j=4)
            var s = new PaddedHeightSampler(a, demSize,
                north: default, south: default, east: default, west: default,
                northEast: default, northWest: default, southEast: default, southWest: default);
            try
            {
                // u=v=0.5 → fpx=fpy=3.5 → corners (3,3)=50, (4,3)=50, (3,4)=50, (4,4)=NaN→fallback(0).
                // tx=ty=0.5 → the result is the mean of all 4 corners = (50+50+50+0)/4 = 37.5.
                float sample = s.SampleBilinear(0.5, 0.5);
                Assert.That(float.IsNaN(sample), Is.False, "NaN does not leak through bilinear sampling");
                float expected = (50f + 50f + 50f + GroundHeightDefaults.FallbackHeightMeters) / 4f;
                Assert.That(sample, Is.EqualTo(expected).Within(0.01f));
            }
            finally { a.Dispose(); }
        }

        private static NativeArray<float> NewDem(int size, System.Func<int, int, float> fn)
        {
            var a = new NativeArray<float>(size * size, Allocator.Temp);
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                    a[j * size + i] = fn(i, j);
            return a;
        }

        /// <summary>
        /// Test helper that emulates a global DEM map covering several tiles around the tested one.
        /// <see cref="MakeSampler(int, int)"/> returns a <see cref="PaddedHeightSampler"/> for the tile at world
        /// coordinates <c>(gx, gy)</c> with the 9 neighboring DEMs assembled automatically. gy grows northward
        /// (like Mercator Y). <see cref="Dispose"/> releases all native arrays.
        /// </summary>
        private sealed class WorldDems : System.IDisposable
        {
            private readonly System.Collections.Generic.Dictionary<(int gx, int gy), NativeArray<float>> _dems
                = new System.Collections.Generic.Dictionary<(int gx, int gy), NativeArray<float>>();
            private readonly int _demSize;
            private readonly System.Func<int, int, int, int, float> _fn;

            public WorldDems(int demSize, System.Func<int, int, int, int, float> fn)
            {
                _demSize = demSize;
                _fn = fn;
            }

            public NativeArray<float> GetDem(int gx, int gy)
            {
                if (!_dems.TryGetValue((gx, gy), out var dem))
                {
                    dem = new NativeArray<float>(_demSize * _demSize, Allocator.Temp);
                    for (int j = 0; j < _demSize; j++)
                        for (int i = 0; i < _demSize; i++)
                            dem[j * _demSize + i] = _fn(gx, gy, i, j);
                    _dems[(gx, gy)] = dem;
                }
                return dem;
            }

            public PaddedHeightSampler MakeSampler(int gx, int gy)
            {
                return new PaddedHeightSampler(
                    central:   GetDem(gx,     gy),
                    demWidth:  _demSize,
                    north:     GetDem(gx,     gy + 1),
                    south:     GetDem(gx,     gy - 1),
                    east:      GetDem(gx + 1, gy),
                    west:      GetDem(gx - 1, gy),
                    northEast: GetDem(gx + 1, gy + 1),
                    northWest: GetDem(gx - 1, gy + 1),
                    southEast: GetDem(gx + 1, gy - 1),
                    southWest: GetDem(gx - 1, gy - 1));
            }

            public void Dispose()
            {
                foreach (var kv in _dems) kv.Value.Dispose();
                _dems.Clear();
            }
        }
    }
}
