using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace GeoFoundry.TileIO.PmTiles
{
    /// <summary>Read-only filesystem archive. Every request has its own stream position.</summary>
    public sealed class FileByteRangeSource : IByteRangeSource
    {
        readonly string _path;
        readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        bool _disposed;
        public long Length { get; }

        public FileByteRangeSource(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A file path is required.", nameof(path));
            _path = path;
            try { Length = new FileInfo(path).Length; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { throw new PmTilesException($"Cannot open PMTiles file '{path}'.", e); }
        }

        public async Awaitable<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileByteRangeSource));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            ct = linked.Token;
            ct.ThrowIfCancellationRequested();
            if (offset < 0 || length < 0 || offset > Length || length > Length - offset)
                throw new PmTilesException("PMTiles range is outside the file.");
            try
            {
                using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16384, FileOptions.Asynchronous | FileOptions.RandomAccess);
                file.Seek(offset, SeekOrigin.Begin);
                var result = new byte[length];
                int read = 0;
                while (read < length)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = await file.ReadAsync(result, read, Math.Min(65536, length - read), ct);
                    if (n == 0) throw new PmTilesException("Truncated PMTiles file.");
                    read += n;
                }
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { throw new PmTilesException($"Cannot read PMTiles file '{_path}'.", e); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
    }
}
