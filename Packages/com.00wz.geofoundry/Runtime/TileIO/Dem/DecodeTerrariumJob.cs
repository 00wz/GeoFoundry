using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GeoFoundry.TileIO.Dem
{
    /// <summary>
    /// Burst job that decodes Terrarium-encoded RGB pixels into float heights.
    ///
    /// Terrarium formula (Mapzen / AWS Terrain Tiles):
    /// <code>height = (R*256 + G + B/256) - 32768</code>
    /// Range: [-32768.0, +32767.99609375] meters, step ≈ 0.00390625 meters.
    ///
    /// Nodata convention: pixels with alpha=0 mean "no data" and decode to <see cref="float.NaN"/> instead of the
    /// formula above. Servers that deliver Terrarium PNGs with an alpha mask at the edge of data coverage use this
    /// convention; e.g. titiler, on a boundless read of a tile partly outside the raster bounds, gives pixels
    /// outside the data RGBA=(0,0,0,0), which would otherwise decode to a bogus -32768 meters.
    ///
    /// Sources serving fully opaque Terrarium tiles (alpha always 255, e.g. the classic AWS/Mapzen
    /// elevation-tiles-prod) never hit this check.
    ///
    /// A source with a different nodata convention (e.g. a sentinel RGB value without an alpha mask) needs a
    /// separate decode strategy; do not add source-specific conditions to this job, to keep the data format
    /// separate from the transport.
    /// </summary>
    [BurstCompile]
    internal struct DecodeTerrariumJob : IJobParallelFor
    {
        [ReadOnly]  public NativeArray<Color32> Pixels;
        [WriteOnly] public NativeArray<float>  Heights;

        public void Execute(int i)
        {
            var p = Pixels[i];
            Heights[i] = p.a == 0
                ? float.NaN
                : (p.r * 256f + p.g + p.b / 256f) - 32768f;
        }
    }
}
