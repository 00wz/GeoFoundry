using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GeoFoundry.TileIO.Dem
{
    /// <summary>
    /// Decoder of Terrarium-encoded DEM tiles (PNG whose pixel RGB encodes the height in meters).
    /// Pixels with alpha=0 mean "no data" and decode to float.NaN; see <see cref="DecodeTerrariumJob"/>.
    ///
    /// Pipeline:
    /// 1. The Texture2D is downloaded with <see cref="Net.TileDownloader.GetTextureAsync"/>; on the Web this is a
    ///    native browser decode, not a WASM PNG decoder.
    /// 2. Pixels are read with <see cref="Texture2D.GetPixels32"/> and copied into a temporary NativeArray.
    /// 3. <see cref="DecodeTerrariumJob"/> is scheduled over all pixels: Burst-parallel decoding into a
    ///    <see cref="NativeArray{T}"/> of float heights.
    /// 4. The job is completed synchronously, or its JobHandle is awaited in async code.
    ///
    /// The texture and the output array are owned by the caller (allocated with its allocators).
    /// </summary>
    internal static class TerrariumDecoder
    {
        /// <summary>Standard titiler/Terrarium DEM tile size: 256x256 px.</summary>
        public const int StandardTileSize = 256;

        /// <summary>
        /// Decodes a Texture2D into a float height array of the same size, running a Burst job and blocking until it
        /// completes. Use <see cref="ScheduleDecode"/> in async chains.
        ///
        /// Format-agnostic: <see cref="Texture2D.GetPixels32"/> always returns Color32 in RGBA order, so both RGBA32
        /// and ARGB32 work (titiler PNGs usually load as ARGB32 on Windows). Costs one managed allocation of
        /// 4·w·h bytes (256 KB for 256×256).
        /// </summary>
        /// <param name="texture">DEM tile in any format; must not be null.</param>
        /// <param name="allocator">Allocator for the returned height array (Persistent/TempJob).</param>
        public static NativeArray<float> Decode(Texture2D texture, Allocator allocator)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));

            int pixelCount = texture.width * texture.height;
            var heights = new NativeArray<float>(pixelCount, allocator, NativeArrayOptions.UninitializedMemory);

            var managedPixels = texture.GetPixels32();
            var pixels = new NativeArray<Color32>(managedPixels.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                pixels.CopyFrom(managedPixels);

                var job = new DecodeTerrariumJob
                {
                    Pixels = pixels,
                    Heights = heights,
                };
                job.Schedule(pixelCount, innerloopBatchCount: 512).Complete();
            }
            finally
            {
                pixels.Dispose();
            }
            return heights;
        }

        /// <summary>
        /// Schedules decoding for async pipelines and returns the <see cref="JobHandle"/> so it can be chained with
        /// other jobs. The caller must complete the handle and dispose the height array. The internal pixel
        /// NativeArray is freed automatically when the job completes.
        /// </summary>
        public static JobHandle ScheduleDecode(Texture2D texture, Allocator allocator,
                                               out NativeArray<float> heights,
                                               JobHandle dependency = default)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));

            int pixelCount = texture.width * texture.height;
            heights = new NativeArray<float>(pixelCount, allocator, NativeArrayOptions.UninitializedMemory);

            var managedPixels = texture.GetPixels32();
            var pixels = new NativeArray<Color32>(managedPixels.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            pixels.CopyFrom(managedPixels);

            var job = new DecodeTerrariumJob
            {
                Pixels = pixels,
                Heights = heights,
            };
            var handle = job.Schedule(pixelCount, innerloopBatchCount: 512, dependsOn: dependency);
            // Free the temporary pixels once the job completes; Unity adds the cleanup to the job graph.
            pixels.Dispose(handle);
            return handle;
        }

        /// <summary>
        /// Decodes a single pixel with the Terrarium formula, for unit tests and one-off queries without a job.
        /// Pixels with alpha=0 (no data, see <see cref="DecodeTerrariumJob"/>) decode to NaN.
        /// </summary>
        public static float DecodePixel(Color32 pixel) =>
            pixel.a == 0
                ? float.NaN
                : (pixel.r * 256f + pixel.g + pixel.b / 256f) - 32768f;
    }
}
