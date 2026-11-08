using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.TileIO.Dem;
using GeoFoundry.TileIO.PmTiles;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.TileIO.Sources
{
    /// <summary>Terrarium PNG heights from PMTiles v3. Missing/empty tiles return default without logging.</summary>
    public sealed class PmTilesDemTileSource : IDemTileSource, IDisposable
    {
        readonly PmTilesReader _reader;
        readonly bool _ownsReader;
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        bool _disposed;
        public int TileSizePixels { get; }
        public PmTilesDemTileSource(string path, int tileSizePixels = 256)
            : this(new PmTilesReader(PmTilesByteSources.Open(path), true), tileSizePixels, true) { }
        public PmTilesDemTileSource(PmTilesReader reader, int tileSizePixels = 256, bool ownsReader = false)
        {
            if (tileSizePixels < 2 || tileSizePixels > 4096) throw new ArgumentOutOfRangeException(nameof(tileSizePixels));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _ownsReader = ownsReader; TileSizePixels = tileSizePixels;
        }
        public async Awaitable<NativeArray<float>> GetHeightsAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PmTilesDemTileSource));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            ct = linked.Token;
            var bytes = await _reader.ReadTileAsync(coord, 2, ct);
            ct.ThrowIfCancellationRequested();
            if (bytes == null || bytes.Length == 0) return default;
            // Reject incorrect dimensions before PNG decoding can allocate an oversized texture or log errors.
            ValidatePng(bytes);
            Texture2D texture = null;
            NativeArray<float> heights = default;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                ct.ThrowIfCancellationRequested();
                if (!ImageConversion.LoadImage(texture, bytes, false)) throw new PmTilesException("Invalid PMTiles DEM PNG.");
                ct.ThrowIfCancellationRequested();
                if (texture.width != TileSizePixels || texture.height != TileSizePixels)
                    throw new PmTilesException("PMTiles DEM dimensions do not match TileSizePixels.");
                var pixels = texture.GetPixels32();
                heights = new NativeArray<float>(pixels.Length, allocator, NativeArrayOptions.UninitializedMemory);
                // Same Terrarium/alpha semantics as the HTTP decoder, without temporary NativeArray/job lifetime.
                for (int i = 0; i < pixels.Length; i++)
                {
                    if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                    heights[i] = TerrariumDecoder.DecodePixel(pixels[i]);
                }
                ct.ThrowIfCancellationRequested();
                return heights;
            }
            catch (Exception e) when (!(e is OperationCanceledException) && !(e is PmTilesException))
            {
                if (heights.IsCreated) heights.Dispose();
                throw new PmTilesException("Cannot decode PMTiles DEM PNG.", e);
            }
            catch { if (heights.IsCreated) heights.Dispose(); throw; }
            finally
            {
                if (texture != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                    else UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }
        void ValidatePng(byte[] bytes)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 33) throw new PmTilesException("Truncated PMTiles DEM PNG.");
            for (int i = 0; i < signature.Length; i++)
                if (bytes[i] != signature[i]) throw new PmTilesException("PMTiles DEM payload is not PNG.");
            int width = BigEndian(bytes, 16), height = BigEndian(bytes, 20);
            if (width != TileSizePixels || height != TileSizePixels)
                throw new PmTilesException($"PMTiles DEM must be {TileSizePixels}x{TileSizePixels} Terrarium PNG; got {width}x{height}.");
        }
        static int BigEndian(byte[] bytes, int p) => bytes[p] << 24 | bytes[p + 1] << 16 | bytes[p + 2] << 8 | bytes[p + 3];
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); if (_ownsReader) _reader.Dispose();
        }
    }
}
