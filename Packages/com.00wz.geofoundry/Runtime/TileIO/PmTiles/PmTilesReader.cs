using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using UnityEngine;

namespace GeoFoundry.TileIO.PmTiles
{
    /// <summary>PMTiles v3 reader. XYZ/Hilbert addressing, None/Gzip, bounded directories and tile payloads.</summary>
    /// <remarks>Calls originate on the main thread. Reads are serialized; root and a bounded LRU of leaves are cached.
    /// No tile payload cache. Missing tiles return null, present zero-byte decoded tiles return an empty array.</remarks>
    public sealed class PmTilesReader : IDisposable
    {
        struct Entry { public ulong Id, Offset, Run; public int Length; }
        sealed class Leaf { public ulong Offset; public int Length; public Entry[] Entries; }
        readonly IByteRangeSource _source;
        readonly bool _ownsSource;
        readonly int _maxTileBytes, _maxDirectoryBytes, _leafCacheCount;
        readonly LinkedList<Leaf> _leaves = new LinkedList<Leaf>();
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        static readonly uint[] CrcTable = MakeCrcTable();
        bool _busy, _disposed;
        Entry[] _root;
        ulong _leafOffset, _leafLength, _tileOffset, _tileLength;
        byte _internalCompression, _tileCompression, _tileType, _minZoom, _maxZoom;

        public PmTilesReader(IByteRangeSource source, bool ownsSource = false,
            int maxTileBytes = 16 * 1024 * 1024, int maxDirectoryBytes = 1024 * 1024, int leafCacheCount = 4)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (maxTileBytes < 1 || maxDirectoryBytes < 1 || leafCacheCount < 0)
                throw new ArgumentOutOfRangeException(nameof(maxTileBytes), "Positive size limits and nonnegative cache size required.");
            _ownsSource = ownsSource; _maxTileBytes = maxTileBytes;
            _maxDirectoryBytes = maxDirectoryBytes; _leafCacheCount = leafCacheCount;
        }

        /// <summary>Reads one decoded tile; expectedTileType is 1 for MVT or 2 for PNG.</summary>
        public async Awaitable<byte[]> ReadTileAsync(TileCoord coord, byte expectedTileType, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PmTilesReader));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            ct = linked.Token;
            ct.ThrowIfCancellationRequested();
            while (_busy) await YieldAsync(ct);
            ct.ThrowIfCancellationRequested();
            _busy = true;
            try
            {
                if (_root == null) await InitializeAsync(ct);
                if (_tileType != expectedTileType)
                    throw new PmTilesException($"PMTiles tile type {_tileType} does not match required type {expectedTileType} (1=MVT, 2=PNG).");
                if ((uint)coord.X >= (uint)coord.TilesPerSide || !coord.IsYInWorld || coord.Z < _minZoom || coord.Z > _maxZoom)
                    return null;
                ulong id = TileId(coord);
                var entries = _root;
                for (int depth = 0; depth < 4; depth++)
                {
                    ct.ThrowIfCancellationRequested();
                    int index = Find(entries, id);
                    if (index < 0) return null;
                    var entry = entries[index];
                    if (entry.Run != 0)
                    {
                        if (id - entry.Id >= entry.Run) return null;
                        CheckRange(entry.Offset, (ulong)entry.Length, _tileLength, "tile data");
                        // Tolerate empty tile records as missing payloads; empty leaf pointers remain corrupt.
                        if (entry.Length == 0) return Array.Empty<byte>();
                        if (entry.Length > _maxTileBytes) throw new PmTilesException("Compressed PMTiles tile exceeds the configured size limit.");
                        var bytes = await ReadExactAsync(_tileOffset + entry.Offset, entry.Length, ct);
                        return await InflateAsync(bytes, _tileCompression, _maxTileBytes, ct);
                    }
                    CheckRange(entry.Offset, (ulong)entry.Length, _leafLength, "leaf directories");
                    entries = await LeafAsync(entry, ct);
                }
                throw new PmTilesException("PMTiles directory depth exceeds 4 (cyclic or corrupt archive).");
            }
            catch (Exception e) when (e is IOException || e is OverflowException || e is ArgumentException)
            { throw new PmTilesException("Invalid or unreadable PMTiles archive.", e); }
            finally { _busy = false; }
        }

        async Awaitable InitializeAsync(CancellationToken ct)
        {
            var header = await ReadExactAsync(0, 127, ct);
            var magic = new byte[] { 80, 77, 84, 105, 108, 101, 115 };
            for (int i = 0; i < magic.Length; i++)
                if (header[i] != magic[i]) throw new PmTilesException("Not a PMTiles archive (invalid magic).");
            if (header[7] != 3) throw new PmTilesException($"PMTiles version {header[7]} is not supported; version 3 is required.");
            _internalCompression = header[97]; _tileCompression = header[98]; _tileType = header[99];
            Compression(_internalCompression); Compression(_tileCompression);
            _minZoom = header[100]; _maxZoom = header[101];
            if (_minZoom > _maxZoom || _maxZoom > 31) throw new PmTilesException("Invalid PMTiles zoom range.");
            ulong rootOffset = U64(header, 8), rootLength = U64(header, 16);
            _leafOffset = U64(header, 40); _leafLength = U64(header, 48);
            _tileOffset = U64(header, 56); _tileLength = U64(header, 64);
            if (rootOffset < 127 || rootLength == 0) throw new PmTilesException("Invalid PMTiles root directory.");
            CheckRange(rootOffset, rootLength, 16384, "first 16 KiB");
            Section(rootOffset, rootLength); Section(U64(header, 24), U64(header, 32));
            Section(_leafOffset, _leafLength); Section(_tileOffset, _tileLength);
            var root = await DirectoryAsync(rootOffset, checked((int)rootLength), ct);
            ct.ThrowIfCancellationRequested();
            _root = root; // Failed/cancelled initialization is retried; partial state is never published.
        }

        async Awaitable<Entry[]> LeafAsync(Entry entry, CancellationToken ct)
        {
            for (var node = _leaves.First; node != null; node = node.Next)
            {
                if (node.Value.Offset != entry.Offset || node.Value.Length != entry.Length) continue;
                var cached = node.Value.Entries;
                _leaves.Remove(node); _leaves.AddFirst(node);
                return cached;
            }
            var entries = await DirectoryAsync(_leafOffset + entry.Offset, entry.Length, ct);
            if (_leafCacheCount != 0)
            {
                _leaves.AddFirst(new Leaf { Offset = entry.Offset, Length = entry.Length, Entries = entries });
                if (_leaves.Count > _leafCacheCount) _leaves.RemoveLast();
            }
            return entries;
        }

        async Awaitable<Entry[]> DirectoryAsync(ulong offset, int length, CancellationToken ct)
        {
            if (length > _maxDirectoryBytes) throw new PmTilesException("PMTiles directory exceeds the configured size limit.");
            var raw = await ReadExactAsync(offset, length, ct);
            raw = await InflateAsync(raw, _internalCompression, _maxDirectoryBytes, ct);
            int cursor = 0;
            ulong count = Varint(raw, ref cursor);
            if (count > 65536 || count > (ulong)(raw.Length / 4)) throw new PmTilesException("Invalid PMTiles directory entry count (limit 65536).");
            var entries = new Entry[(int)count];
            ulong id = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                ulong delta = Varint(raw, ref cursor);
                if (i > 0 && delta == 0) throw new PmTilesException("PMTiles tile IDs must increase.");
                entries[i].Id = id = checked(id + delta);
            }
            for (int i = 0; i < entries.Length; i++) entries[i].Run = Varint(raw, ref cursor);
            for (int i = 0; i < entries.Length; i++)
            {
                ulong n = Varint(raw, ref cursor);
                if (n > int.MaxValue || (n == 0 && entries[i].Run == 0)) throw new PmTilesException("Invalid PMTiles entry length.");
                entries[i].Length = (int)n;
            }
            for (int i = 0; i < entries.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                ulong encoded = Varint(raw, ref cursor);
                if (encoded == 0 && i == 0) throw new PmTilesException("Invalid first PMTiles entry offset.");
                entries[i].Offset = encoded == 0 ? checked(entries[i - 1].Offset + (ulong)entries[i - 1].Length) : encoded - 1;
                if (entries[i].Run != 0 && (entries[i].Id > ulong.MaxValue - entries[i].Run ||
                    (i + 1 < entries.Length && entries[i + 1].Id < entries[i].Id + entries[i].Run)))
                    throw new PmTilesException("Overlapping PMTiles tile runs.");
            }
            if (cursor != raw.Length) throw new PmTilesException("Trailing bytes in PMTiles directory.");
            return entries;
        }

        async Awaitable<byte[]> ReadExactAsync(ulong offset, int length, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (offset > long.MaxValue) throw new PmTilesException("PMTiles offset exceeds supported file size.");
            var data = await _source.ReadAsync((long)offset, length, ct);
            ct.ThrowIfCancellationRequested();
            if (data == null || data.Length != length) throw new PmTilesException("Truncated PMTiles range.");
            return data;
        }

        static async Awaitable<byte[]> InflateAsync(byte[] bytes, byte compression, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (compression == 1) return bytes;
            if (bytes.Length < 18) throw new PmTilesException("Truncated PMTiles gzip payload.");
            using var input = new MemoryStream(bytes, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            uint crc = uint.MaxValue;
            int sinceYield = 0, n;
            while ((n = gzip.Read(buffer, 0, buffer.Length)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                if (n > limit - output.Length) throw new PmTilesException("Inflated PMTiles payload exceeds the configured size limit.");
                output.Write(buffer, 0, n);
                for (int i = 0; i < n; i++)
                    crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[i]) & 255];
                sinceYield += n;
                if (sinceYield >= 262144) { sinceYield = 0; await YieldAsync(ct); }
            }
            ct.ThrowIfCancellationRequested();
            if (~crc != U32(bytes, bytes.Length - 8) || (uint)output.Length != U32(bytes, bytes.Length - 4))
                throw new PmTilesException("PMTiles gzip checksum/size mismatch (truncated or corrupt payload).");
            return output.ToArray();
        }

        void Section(ulong offset, ulong length)
        {
            if (length == 0) return;
            if (offset < 127 || offset > long.MaxValue || length > long.MaxValue - offset)
                throw new PmTilesException("Invalid PMTiles section offsets.");
            if (_source.Length >= 0) CheckRange(offset, length, (ulong)_source.Length, "archive");
        }
        static async Awaitable YieldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
#if UNITY_EDITOR
            // EditMode tools/tests do not have a normal PlayerLoop frame schedule.
            if (!Application.isPlaying)
            {
                await System.Threading.Tasks.Task.Yield();
                ct.ThrowIfCancellationRequested();
                return;
            }
#endif
            await Awaitable.NextFrameAsync(ct);
        }
        static void CheckRange(ulong offset, ulong length, ulong total, string name)
        {
            if (offset > total || length > total - offset) throw new PmTilesException($"PMTiles range outside {name}.");
        }
        static void Compression(byte value)
        {
            if (value != 1 && value != 2)
                throw new PmTilesException($"PMTiles compression {value} is unsupported (Brotli/Zstd/unknown); use None or Gzip.");
        }
        static uint U32(byte[] b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);
        static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (int bit = 0; bit < 8; bit++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xedb88320u : 0);
                table[i] = c;
            }
            return table;
        }
        static ulong U64(byte[] b, int p) => U32(b, p) | (ulong)U32(b, p + 4) << 32;
        static ulong Varint(byte[] bytes, ref int p)
        {
            ulong result = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (p >= bytes.Length) throw new PmTilesException("Truncated PMTiles varint.");
                byte value = bytes[p++];
                if (shift == 63 && value > 1) throw new PmTilesException("Overflowing PMTiles varint.");
                result |= (ulong)(value & 127) << shift;
                if ((value & 128) == 0) return result;
            }
            throw new PmTilesException("Invalid PMTiles varint.");
        }
        static int Find(Entry[] entries, ulong id)
        {
            int low = 0, high = entries.Length - 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (entries[mid].Id <= id) low = mid + 1; else high = mid - 1;
            }
            return high;
        }
        /// <summary>XYZ to cumulative Hilbert tile ID; Y is never flipped to TMS.</summary>
        public static ulong TileId(TileCoord coord)
        {
            if ((uint)coord.X >= (uint)coord.TilesPerSide || !coord.IsYInWorld)
                throw new ArgumentOutOfRangeException(nameof(coord));
            long x = coord.X, y = coord.Y;
            ulong id = ((1UL << (2 * coord.Z)) - 1) / 3;
            if (coord.Z == 0) return id;
            for (long s = 1L << (coord.Z - 1); s > 0; s >>= 1)
            {
                long rx = (x & s) != 0 ? 1 : 0, ry = (y & s) != 0 ? 1 : 0;
                id += (ulong)(s * s * ((3 * rx) ^ ry));
                if (ry == 0)
                {
                    if (rx == 1) { x = s - 1 - x; y = s - 1 - y; }
                    (x, y) = (y, x);
                }
            }
            return id;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); _root = null; _leaves.Clear();
            if (_ownsSource) _source.Dispose();
        }
    }
}
