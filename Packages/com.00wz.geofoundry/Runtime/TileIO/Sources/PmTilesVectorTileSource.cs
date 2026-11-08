using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.TileIO.PmTiles;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.TileIO.Sources
{
    /// <summary>Raw MVT bytes from a local PMTiles v3 archive (or a Range-enabled StreamingAssets URL).</summary>
    public sealed class PmTilesVectorTileSource : IVectorTileSource, IDisposable
    {
        readonly PmTilesReader _reader;
        readonly bool _ownsReader;
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        bool _disposed;
        public PmTilesVectorTileSource(string path) : this(new PmTilesReader(PmTilesByteSources.Open(path), true), true) { }
        public PmTilesVectorTileSource(PmTilesReader reader, bool ownsReader = false)
        { _reader = reader ?? throw new ArgumentNullException(nameof(reader)); _ownsReader = ownsReader; }

        public async Awaitable<NativeArray<byte>> GetTileAsync(TileCoord coord, Allocator allocator, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PmTilesVectorTileSource));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            ct = linked.Token;
            var bytes = await _reader.ReadTileAsync(coord, 1, ct);
            ct.ThrowIfCancellationRequested();
            // Match an empty HTTP MVT response: a created zero-length buffer, not a transport exception.
            var result = new NativeArray<byte>(bytes?.Length ?? 0, allocator, NativeArrayOptions.UninitializedMemory);
            try { if (bytes != null) result.CopyFrom(bytes); ct.ThrowIfCancellationRequested(); return result; }
            catch { result.Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); if (_ownsReader) _reader.Dispose();
        }
    }
}
