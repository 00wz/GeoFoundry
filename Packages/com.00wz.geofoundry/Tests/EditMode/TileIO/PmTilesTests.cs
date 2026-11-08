using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.TileIO.PmTiles;
using GeoFoundry.TileIO.Sources;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace GeoFoundry.Tests.EditMode.TileIO
{
    // Fixtures are generated here from synthetic data. No third-party map data or fixture licences.
    public sealed class PmTilesTests
    {
        static readonly byte[] Mvt = { 0x1a, 8, 0x0a, 1, 0x78, 0x28, 0x80, 0x20, 0x78, 2 };

        sealed class MemorySource : IByteRangeSource
        {
            public byte[] Bytes;
            public int Reads;
            public bool Disposed;
            public Action<int> OnRead;
            public long Length => Bytes.Length;
            public MemorySource(byte[] bytes) { Bytes = bytes; }
            public Awaitable<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
            {
                Reads++; OnRead?.Invoke(Reads); ct.ThrowIfCancellationRequested();
                if (offset < 0 || offset + length > Bytes.Length) throw new PmTilesException("Short fixture");
                var bytes = new byte[length]; Buffer.BlockCopy(Bytes, (int)offset, bytes, 0, length);
                var completion = new AwaitableCompletionSource<byte[]>(); completion.SetResult(bytes);
                return completion.Awaitable;
            }
            public void Dispose() { Disposed = true; }
        }

        static byte[] Gzip(byte[] bytes)
        {
            if (bytes.Length == 0) return new byte[] { 31, 139, 8, 0, 0, 0, 0, 0, 0, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            using var output = new MemoryStream();
            using (var stream = new GZipStream(output, CompressionLevel.Optimal, true)) stream.Write(bytes, 0, bytes.Length);
            return output.ToArray();
        }
        static void Varint(Stream stream, ulong value)
        {
            while (value > 127) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
            stream.WriteByte((byte)value);
        }
        static byte[] Directory(ulong id, ulong run, int length)
        {
            using var bytes = new MemoryStream();
            Varint(bytes, 1); Varint(bytes, id); Varint(bytes, run); Varint(bytes, (ulong)length); Varint(bytes, 1);
            return bytes.ToArray();
        }
        static void U64(byte[] bytes, int index, ulong value)
        { for (int i = 0; i < 8; i++) bytes[index + i] = (byte)(value >> (i * 8)); }

        static byte[] Archive(byte[] tile, byte type = 1, bool leaf = false, bool gzip = true, ulong run = 1)
        {
            var payload = gzip ? Gzip(tile) : tile;
            // Standard v3 exporters use nonempty records; tolerate zero stored bytes for missing DEM too.
            var entries = Directory(1, run, payload.Length);
            var leaves = leaf ? (gzip ? Gzip(entries) : entries) : Array.Empty<byte>();
            var root = leaf ? Directory(1, 0, leaves.Length) : entries;
            if (gzip) root = Gzip(root);
            var bytes = new byte[127 + root.Length + leaves.Length + payload.Length];
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("PMTiles"), 0, bytes, 0, 7);
            bytes[7] = 3;
            U64(bytes, 8, 127); U64(bytes, 16, (ulong)root.Length);
            U64(bytes, 40, (ulong)(127 + root.Length)); U64(bytes, 48, (ulong)leaves.Length);
            U64(bytes, 56, (ulong)(127 + root.Length + leaves.Length)); U64(bytes, 64, (ulong)payload.Length);
            bytes[97] = bytes[98] = (byte)(gzip ? 2 : 1); bytes[99] = type; bytes[100] = bytes[101] = 1;
            Buffer.BlockCopy(root, 0, bytes, 127, root.Length);
            Buffer.BlockCopy(leaves, 0, bytes, 127 + root.Length, leaves.Length);
            Buffer.BlockCopy(payload, 0, bytes, bytes.Length - payload.Length, payload.Length);
            return bytes;
        }

        [TestCase(0, 0, 0, 0UL)]
        [TestCase(1, 0, 0, 1UL)]
        [TestCase(1, 0, 1, 2UL)]
        [TestCase(1, 1, 1, 3UL)]
        [TestCase(1, 1, 0, 4UL)]
        [TestCase(12, 3423, 1763, 19078479UL)]
        public void Hilbert_UsesXyz(int z, int x, int y, ulong expected) =>
            Assert.That(PmTilesReader.TileId(new TileCoord(z, x, y)), Is.EqualTo(expected));

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public async Task Reader_RootAndLeaf_NoneAndGzip(bool leaf, bool gzip)
        {
            using var source = new MemorySource(Archive(Mvt, leaf: leaf, gzip: gzip));
            using var reader = new PmTilesReader(source);
            CollectionAssert.AreEqual(Mvt, await reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, default));
            int firstReads = source.Reads;
            CollectionAssert.AreEqual(Mvt, await reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, default));
            Assert.That(source.Reads, Is.EqualTo(firstReads + 1), "Header/root/leaf should be cached, not tile bytes");
            Assert.That(await reader.ReadTileAsync(new TileCoord(1, 0, 1), 1, default), Is.Null, "XYZ Y is not TMS-flipped");
        }

        [Test]
        public async Task Reader_RunLengthAndMissing()
        {
            using var reader = new PmTilesReader(new MemorySource(Archive(Mvt, run: 3)), true);
            CollectionAssert.AreEqual(Mvt, await reader.ReadTileAsync(new TileCoord(1, 1, 1), 1, default));
            Assert.That(await reader.ReadTileAsync(new TileCoord(1, 1, 0), 1, default), Is.Null);
            Assert.That(await reader.ReadTileAsync(new TileCoord(1, -1, 0), 1, default), Is.Null);
        }

        [Test]
        public async Task Reader_ContiguousOffsetsAndDeduplicatedTiles()
        {
            var other = (byte[])Mvt.Clone(); other[4] = (byte)'y';
            byte[] directory = { 3, 1, 1, 2, 1, 1, 1, 10, 10, 10, 1, 0, 1 };
            var bytes = new byte[127 + directory.Length + Mvt.Length * 2];
            Buffer.BlockCopy(Archive(Mvt, gzip: false), 0, bytes, 0, 127);
            U64(bytes, 16, (ulong)directory.Length); U64(bytes, 40, (ulong)(127 + directory.Length));
            U64(bytes, 56, (ulong)(127 + directory.Length)); U64(bytes, 64, (ulong)(Mvt.Length * 2));
            Buffer.BlockCopy(directory, 0, bytes, 127, directory.Length);
            Buffer.BlockCopy(Mvt, 0, bytes, 127 + directory.Length, Mvt.Length);
            Buffer.BlockCopy(other, 0, bytes, 127 + directory.Length + Mvt.Length, other.Length);
            using var reader = new PmTilesReader(new MemorySource(bytes), true);
            CollectionAssert.AreEqual(other, await reader.ReadTileAsync(new TileCoord(1, 0, 1), 1, default));
            CollectionAssert.AreEqual(Mvt, await reader.ReadTileAsync(new TileCoord(1, 1, 0), 1, default));
        }

        [Test]
        public async Task Sources_CancelBeforeDecodeAndDisposeBorrowedReader()
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            using var source = new MemorySource(Archive(Mvt));
            using var reader = new PmTilesReader(source);
            using var vectors = new PmTilesVectorTileSource(reader);
            using var dem = new PmTilesDemTileSource(reader);
            try { var bytes = await vectors.GetTileAsync(new TileCoord(1, 0, 0), Allocator.Persistent, cts.Token); if (bytes.IsCreated) bytes.Dispose(); Assert.Fail(); }
            catch (OperationCanceledException) { }
            try { var heights = await dem.GetHeightsAsync(new TileCoord(1, 0, 0), Allocator.Persistent, cts.Token); if (heights.IsCreated) heights.Dispose(); Assert.Fail(); }
            catch (OperationCanceledException) { }
            Assert.That(source.Reads, Is.Zero);
            vectors.Dispose();
            try { var bytes = await vectors.GetTileAsync(new TileCoord(1, 0, 0), Allocator.Persistent, default); if (bytes.IsCreated) bytes.Dispose(); Assert.Fail(); }
            catch (ObjectDisposedException) { }
        }

        static async Task Fails(byte[] bytes)
        {
            using var reader = new PmTilesReader(new MemorySource(bytes), true);
            try { await reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, default); Assert.Fail("Expected PmTilesException"); }
            catch (PmTilesException) { }
        }
        [TestCase(97, 0)] [TestCase(97, 3)] [TestCase(98, 4)] [TestCase(7, 2)] [TestCase(0, 0)]
        public async Task Reader_RejectsUnsupportedOrCorruptHeader(int offset, int value)
        { var bytes = Archive(Mvt); bytes[offset] = (byte)value; await Fails(bytes); }

        [Test]
        public async Task Reader_CancelDuringInflate()
        {
            using var cts = new CancellationTokenSource();
            using var reader = new PmTilesReader(new MemorySource(Archive(new byte[512 * 1024])), true);
            var pending = reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, cts.Token);
            cts.Cancel();
            try { await pending; Assert.Fail("Inflate must observe cancellation after yielding"); }
            catch (OperationCanceledException) { }
        }

        [Test]
        public async Task Reader_RejectsTruncationBadVarintAndGzipCrc()
        {
            await Fails(new byte[60]);
            var bytes = Archive(Mvt, gzip: false); bytes[127] = 255; await Fails(bytes);
            bytes = Archive(Mvt); bytes[bytes.Length - 8] ^= 255; await Fails(bytes);
            bytes = Archive(Mvt); U64(bytes, 56, ulong.MaxValue); await Fails(bytes);
        }

        [Test]
        public async Task Reader_CancelDuringReadThenRetry()
        {
            using var cts = new CancellationTokenSource();
            using var source = new MemorySource(Archive(Mvt, leaf: true));
            source.OnRead = count => { if (count == 3) cts.Cancel(); };
            using var reader = new PmTilesReader(source);
            try { await reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, cts.Token); Assert.Fail(); }
            catch (OperationCanceledException) { }
            source.OnRead = null;
            CollectionAssert.AreEqual(Mvt, await reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, default));
        }

        [Test]
        public async Task Reader_CancelBeforeReadAndInflateSizeLimit()
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            using var source = new MemorySource(Archive(new byte[32768]));
            using var reader = new PmTilesReader(source, maxTileBytes: 1024);
            try { await reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, cts.Token); Assert.Fail(); }
            catch (OperationCanceledException) { }
            Assert.That(source.Reads, Is.Zero);
            try { await reader.ReadTileAsync(new TileCoord(1, 0, 0), 1, default); Assert.Fail(); }
            catch (PmTilesException) { }
        }

        [Test]
        public async Task VectorSource_AllocatorOwnershipMissingAndDispose()
        {
            var source = new MemorySource(Archive(Mvt));
            using var reader = new PmTilesReader(source, true);
            using (var tiles = new PmTilesVectorTileSource(reader))
            {
                using var bytes = await tiles.GetTileAsync(new TileCoord(1, 0, 0), Allocator.Persistent, default);
                CollectionAssert.AreEqual(Mvt, bytes.ToArray());
                using var missing = await tiles.GetTileAsync(new TileCoord(1, 0, 1), Allocator.Persistent, default);
                Assert.That(missing.Length, Is.Zero);
            }
            Assert.That(source.Disposed, Is.False, "An injected reader is borrowed by default");
            reader.Dispose(); Assert.That(source.Disposed, Is.True);
        }

        [Test]
        public async Task DemSource_PngHeightsMissingEmptyAndCorrupt()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(new[] { new Color32(128, 1, 0, 255), new Color32(128, 2, 0, 255),
                new Color32(128, 3, 128, 255), new Color32(0, 0, 0, 0) });
            texture.Apply(); var png = texture.EncodeToPNG(); UnityEngine.Object.DestroyImmediate(texture);
            int textureCount = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
            using (var reader = new PmTilesReader(new MemorySource(Archive(png, 2)), true))
            using (var tiles = new PmTilesDemTileSource(reader, 2))
            {
                using var heights = await tiles.GetHeightsAsync(new TileCoord(1, 0, 0), Allocator.Persistent, default);
                Assert.That(heights.Length, Is.EqualTo(4)); Assert.That(heights[0], Is.EqualTo(1));
                Assert.That(heights[2], Is.EqualTo(3.5f)); Assert.That(float.IsNaN(heights[3]), Is.True);
                Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Length, Is.EqualTo(textureCount), "DEM source must release its temporary texture");
                var missing = await tiles.GetHeightsAsync(new TileCoord(1, 0, 1), Allocator.Persistent, default);
                Assert.That(missing.IsCreated, Is.False);
            }
            foreach (bool gzip in new[] { false, true })
                using (var reader = new PmTilesReader(new MemorySource(Archive(Array.Empty<byte>(), 2, gzip: gzip)), true))
                using (var tiles = new PmTilesDemTileSource(reader, 2))
                    Assert.That((await tiles.GetHeightsAsync(new TileCoord(1, 0, 0), Allocator.Persistent, default)).IsCreated, Is.False);
            using (var reader = new PmTilesReader(new MemorySource(Archive(Mvt, 2)), true))
            using (var tiles = new PmTilesDemTileSource(reader, 2))
            {
                try { var bad = await tiles.GetHeightsAsync(new TileCoord(1, 0, 0), Allocator.Persistent, default); if (bad.IsCreated) bad.Dispose(); Assert.Fail(); }
                catch (PmTilesException) { }
            }
        }

        [Test]
        public async Task FileSource_ExactRangesAndReader()
        {
            string path = Path.Combine(Application.temporaryCachePath, Guid.NewGuid() + ".pmtiles");
            try
            {
                File.WriteAllBytes(path, Archive(Mvt));
                using var tiles = new PmTilesVectorTileSource(path);
                using var bytes = await tiles.GetTileAsync(new TileCoord(1, 0, 0), Allocator.Persistent, default);
                CollectionAssert.AreEqual(Mvt, bytes.ToArray());
            }
            finally { File.Delete(path); }
        }
    }
}
