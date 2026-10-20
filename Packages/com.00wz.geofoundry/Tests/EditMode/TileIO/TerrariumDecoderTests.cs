using NUnit.Framework;
using GeoFoundry.TileIO.Dem;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;

namespace GeoFoundry.Tests.EditMode.TileIO
{
    /// <summary>
    /// Tests the Burst Terrarium formula on synthetic Color32 values. The network path is covered by a Play Mode test.
    /// </summary>
    public sealed class TerrariumDecoderTests
    {
        [Test]
        public void DecodePixel_SeaLevel_Returns128R_0G_0B()
        {
            // 0 m height: R*256 + G + B/256 = 32768 → R=128, G=0, B=0
            var pixel = new Color32(128, 0, 0, 255);
            Assert.That(TerrariumDecoder.DecodePixel(pixel), Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void DecodePixel_Negative_Below128R()
        {
            // -100 m → R*256 + G + B/256 = 32668 → R=127, G=156 (32668-127*256=156), B=0
            var pixel = new Color32(127, 156, 0, 255);
            Assert.That(TerrariumDecoder.DecodePixel(pixel), Is.EqualTo(-100f).Within(0.01f));
        }

        [Test]
        public void DecodePixel_Mountain_Above128R()
        {
            // +5000 m (Elbrus) → 37768 → R=147, G=136, B=0
            var pixel = new Color32(147, 136, 0, 255);
            Assert.That(TerrariumDecoder.DecodePixel(pixel), Is.EqualTo(5000f).Within(0.01f));
        }

        [Test]
        public void DecodePixel_FractionalMeters_PreservedInBlue()
        {
            // 0.5 m → R=128, G=0, B=128 (since 128/256 = 0.5)
            var pixel = new Color32(128, 0, 128, 255);
            Assert.That(TerrariumDecoder.DecodePixel(pixel), Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void DecodePixel_Alpha0_ReturnsNaN()
        {
            // Nodata convention: alpha=0 → NaN regardless of RGB (here RGB encodes 0 m, but alpha is checked first).
            var pixel = new Color32(128, 0, 0, 0);
            Assert.That(float.IsNaN(TerrariumDecoder.DecodePixel(pixel)), Is.True);
        }

        [Test]
        public void DecodePixel_NonZeroAlpha_IgnoresAlphaValue()
        {
            // A semi-transparent (non-zero) alpha is not nodata; only exactly a==0 is.
            var pixel = new Color32(128, 0, 0, 128);
            Assert.That(TerrariumDecoder.DecodePixel(pixel), Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void DecodeJob_AppliesFormulaPerPixel()
        {
            const int n = 16;
            var pixels = new NativeArray<Color32>(n, Allocator.TempJob);
            var heights = new NativeArray<float>(n, Allocator.TempJob);
            try
            {
                // Fill pixels with values encoding [0, 100, 200, ..., 1500] m.
                for (int i = 0; i < n; i++)
                {
                    int meters = i * 100;
                    int encoded = meters + 32768;
                    byte r = (byte)(encoded / 256);
                    byte g = (byte)(encoded % 256);
                    pixels[i] = new Color32(r, g, 0, 255);
                }

                var job = new global::GeoFoundry.TileIO.Dem.DecodeTerrariumJob
                {
                    Pixels = pixels,
                    Heights = heights,
                };
                job.Schedule(n, 4).Complete();

                for (int i = 0; i < n; i++)
                {
                    Assert.That(heights[i], Is.EqualTo(i * 100f).Within(0.01f), $"pixel {i}");
                }
            }
            finally
            {
                pixels.Dispose();
                heights.Dispose();
            }
        }

        [Test]
        public void DecodeJob_Alpha0Pixel_ProducesNaN_OthersUnaffected()
        {
            var pixels = new NativeArray<Color32>(2, Allocator.TempJob);
            var heights = new NativeArray<float>(2, Allocator.TempJob);
            try
            {
                pixels[0] = new Color32(128, 0, 0, 255); // 0 m, valid
                pixels[1] = new Color32(128, 0, 0, 0);   // nodata

                var job = new global::GeoFoundry.TileIO.Dem.DecodeTerrariumJob
                {
                    Pixels = pixels,
                    Heights = heights,
                };
                job.Schedule(2, 2).Complete();

                Assert.That(heights[0], Is.EqualTo(0f).Within(0.01f), "valid pixel is unaffected by the neighboring nodata pixel");
                Assert.That(float.IsNaN(heights[1]), Is.True, "alpha=0 → NaN");
            }
            finally
            {
                pixels.Dispose();
                heights.Dispose();
            }
        }

        [Test]
        public void Decode_OnRGBA32Texture_ReturnsHeightsArray()
        {
            // A synthetic 4x4 texture of a single color, so all heights are equal.
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false, linear: false);
            try
            {
                var px = new Color32[16];
                for (int i = 0; i < 16; i++) px[i] = new Color32(128, 200, 0, 255); // height = 200 m
                tex.SetPixels32(px);
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                var heights = TerrariumDecoder.Decode(tex, Allocator.TempJob);
                try
                {
                    Assert.That(heights.Length, Is.EqualTo(16));
                    for (int i = 0; i < 16; i++)
                        Assert.That(heights[i], Is.EqualTo(200f).Within(0.01f));
                }
                finally
                {
                    heights.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void Decode_TextureWithAlpha0Pixel_ProducesNaN()
        {
            // End-to-end path Texture2D → GetPixels32 → job: alpha reaches the decode formula undistorted (e.g. by an
            // sRGB conversion, which on some platforms/formats also affects alpha).
            var tex = new Texture2D(2, 1, TextureFormat.RGBA32, mipChain: false, linear: false);
            try
            {
                var px = new[]
                {
                    new Color32(128, 0, 0, 255), // 0 m, valid
                    new Color32(128, 0, 0, 0),   // nodata
                };
                tex.SetPixels32(px);
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                var heights = TerrariumDecoder.Decode(tex, Allocator.TempJob);
                try
                {
                    Assert.That(heights[0], Is.EqualTo(0f).Within(0.01f));
                    Assert.That(float.IsNaN(heights[1]), Is.True);
                }
                finally
                {
                    heights.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void Decode_AnyFormat_WorksViaGetPixels32()
        {
            // Format-agnostic: GetPixels32 normalizes the layout, whether RGBA32, ARGB32 or RGB24.
            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, mipChain: false, linear: false);
            try
            {
                var px = new Color32[] {
                    new Color32(128, 0, 0, 255),
                    new Color32(128, 0, 0, 255),
                    new Color32(128, 0, 0, 255),
                    new Color32(128, 0, 0, 255),
                };
                tex.SetPixels32(px);
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                var heights = TerrariumDecoder.Decode(tex, Allocator.TempJob);
                try
                {
                    for (int i = 0; i < 4; i++)
                        Assert.That(heights[i], Is.EqualTo(0f).Within(0.01f),
                            "sea-level pixel decodes to 0 m regardless of texture format");
                }
                finally
                {
                    heights.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }
    }
}
