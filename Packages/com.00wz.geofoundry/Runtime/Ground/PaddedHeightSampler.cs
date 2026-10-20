using GeoFoundry.Core.Ground;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Ground
{
    /// <summary>
    /// Height sampler over an extended pixel grid of the 3×3 DEM tiles around the central tile A.
    /// A single bilinear formula over the continuous pixel grid gives exact values at any point, including tile
    /// boundaries, and makes neighboring terrain tiles consistent by construction.
    ///
    /// Pixel index convention:
    /// - Pixel[k] is centered at the normalized coordinate <c>u = (k + 0.5) / demWidth</c>.
    /// - Continuous pixel coordinate: <c>fpx = u * demWidth - 0.5</c>.
    /// - At the edge of A (u=0), fpx=-0.5: bilinear between W's pixel[demWidth-1] and A's pixel[0] with equal
    ///   weights 0.5, i.e. exactly the midpoint, which is the exact value at the edge.
    ///
    /// Any of the 9 DEM arrays (the central A or any of the 8 neighbors, symmetrically) may be default (not loaded),
    /// at map edges or after an HTTP failure; its pixels then read as <see cref="GroundHeightDefaults.FallbackHeightMeters"/>,
    /// not as the height of A. The missing neighbor is itself built with the same fallback when it becomes a
    /// central tile, so copying A's heights would make boundary normals disagree and leave a visible seam. A tile
    /// without DEM data of its own gets the fallback height for its vertices, while normals on its boundary still
    /// use real neighbor data where available (see <see cref="DemGroundGridProvider"/>).
    ///
    /// The same fallback applies per pixel: a single pixel of an otherwise valid DEM array may be nodata
    /// (<see cref="float.NaN"/>, see <see cref="GeoFoundry.TileIO.Dem.DecodeTerrariumJob"/>) and is treated like a
    /// missing tile at the scale of one pixel (see <see cref="ReadPixel"/>).
    /// </summary>
    internal readonly struct PaddedHeightSampler
    {
        // 9 DEM arrays named by compass direction; c is the central tile A.
        private readonly NativeArray<float> _nw, _n, _ne;
        private readonly NativeArray<float> _w,  _c, _e;
        private readonly NativeArray<float> _sw, _s, _se;
        private readonly int _w_; // demWidth (256)

        public int DemWidth => _w_;

        /// <param name="central">Tile A, or default if its DEM data is unavailable too.</param>
        /// <param name="north">North neighbor (slippy y-1), or default if unavailable.</param>
        /// <param name="south">South neighbor (slippy y+1), or default.</param>
        /// <param name="east">East neighbor (slippy x+1), or default.</param>
        /// <param name="west">West neighbor (slippy x-1), or default.</param>
        public PaddedHeightSampler(
            NativeArray<float> central, int demWidth,
            NativeArray<float> north, NativeArray<float> south, NativeArray<float> east, NativeArray<float> west,
            NativeArray<float> northEast, NativeArray<float> northWest,
            NativeArray<float> southEast, NativeArray<float> southWest)
        {
            _c = central;
            _w_ = demWidth;
            _n = north; _s = south; _e = east; _w = west;
            _ne = northEast; _nw = northWest; _se = southEast; _sw = southWest;
        }

        /// <summary>
        /// Samples the height bilinearly at the normalized point (u, v) over the central tile A.
        /// (u, v) may be slightly outside [0, 1] (by one step, for padded grid normals).
        /// </summary>
        public float SampleBilinear(double u, double v)
        {
            double fpx = u * _w_ - 0.5;
            double fpy = v * _w_ - 0.5;
            int px0 = (int)math.floor(fpx);
            int py0 = (int)math.floor(fpy);
            int px1 = px0 + 1;
            int py1 = py0 + 1;
            float tx = (float)(fpx - px0);
            float ty = (float)(fpy - py0);

            float h00 = ReadPixel(px0, py0);
            float h10 = ReadPixel(px1, py0);
            float h01 = ReadPixel(px0, py1);
            float h11 = ReadPixel(px1, py1);
            float h0 = math.lerp(h00, h10, tx);
            float h1 = math.lerp(h01, h11, tx);
            return math.lerp(h0, h1, ty);
        }

        /// <summary>
        /// Reads one pixel by extended index; <paramref name="px"/>/<paramref name="py"/> may be in
        /// [-demWidth, 2*demWidth-1]. Picks the DEM tile out of 9 by range; if that tile has no data
        /// (NativeArray.IsCreated=false), central or neighbor alike, returns the fallback height. If the tile exists
        /// but the pixel is nodata (NaN, see <see cref="GeoFoundry.TileIO.Dem.DecodeTerrariumJob"/>), the same
        /// fallback applies to that pixel.
        /// </summary>
        private float ReadPixel(int px, int py)
        {
            // demX/demY ∈ {-1, 0, +1}. Pixel Y convention: row 0 = south (Unity texture layout),
            // row demWidth-1 = north. So py < 0 is the south neighbor and py >= demWidth is the north one.
            int demX = px < 0 ? -1 : (px < _w_ ? 0 : +1);
            int demY = py < 0 ? -1 : (py < _w_ ? 0 : +1);
            int localX = px - demX * _w_;
            int localY = py - demY * _w_;
            // Clamp out-of-range requests (should not happen with N+3 padding).
            localX = math.clamp(localX, 0, _w_ - 1);
            localY = math.clamp(localY, 0, _w_ - 1);

            var dem = SelectDem(demX, demY);
            if (!dem.IsCreated)
            {
                // No data (central tile or neighbor; map edge or HTTP failure): use the same fallback height
                // as GroundGridHeightProvider and the *BuildJob jobs, since the neighbor itself renders at it.
                return GroundHeightDefaults.FallbackHeightMeters;
            }

            float h = dem[localY * _w_ + localX];
            // NaN is a nodata pixel inside an otherwise valid DEM tile: TerrariumDecoder/DecodeTerrariumJob decode
            // alpha=0 this way (e.g. titiler boundless reads at the edge of raster coverage). Treat it like a missing
            // tile so NaN does not spread through bilinear interpolation and normal gradients.
            return float.IsNaN(h) ? GroundHeightDefaults.FallbackHeightMeters : h;
        }

        private NativeArray<float> SelectDem(int dx, int dy)
        {
            // (dx, dy) = (0, 0) → central; dy=+1 = north, dy=-1 = south, etc.
            if (dx == 0 && dy == 0) return _c;
            if (dx == 0 && dy == +1) return _n;
            if (dx == 0 && dy == -1) return _s;
            if (dx == +1 && dy == 0) return _e;
            if (dx == -1 && dy == 0) return _w;
            if (dx == +1 && dy == +1) return _ne;
            if (dx == -1 && dy == +1) return _nw;
            if (dx == +1 && dy == -1) return _se;
            if (dx == -1 && dy == -1) return _sw;
            return _c; // unreachable
        }
    }
}
