using System;
using System.Collections.Generic;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Ground
{
    /// <summary>
    /// Ref-counted DEM tile cache that deduplicates concurrent loads.
    ///
    /// The same DEM tile is often needed by several terrain tiles at once: for seamless terrain each
    /// terrain tile A loads A + S + E + SE, and S, E, SE usually coincide with DEM tiles already loaded
    /// for neighboring terrain tiles.
    ///
    /// Contract:
    /// - <see cref="AcquireAsync"/> returns a <see cref="DemTileHandle"/> with an incremented ref count.
    /// - The caller must call <see cref="DemTileHandle.Dispose"/>; the NativeArray is freed when the count
    ///   reaches 0.
    /// - Concurrent acquires of the same tile share one pending load (no duplicate HTTP requests).
    /// - A load nobody waits for anymore (all acquires cancelled) is aborted, down to the HTTP request, so
    ///   abandoned loads do not occupy downloader slots. An acquire that arrives at an aborted but not yet
    ///   finished load starts a new one.
    /// - <see cref="Dispose"/> aborts loads and frees unreferenced tiles; a tile that still has a handle is
    ///   freed by its last <see cref="DemTileHandle.Dispose"/>, so memory is never freed under a live handle.
    ///
    /// Not thread-safe: all operations are expected on the main thread (Unity player loop).
    /// </summary>
    internal sealed class DemTileCache : IDisposable
    {
        private sealed class Entry
        {
            public NativeArray<float> Heights;
            public int RefCount;
            public bool LoadCompleted; // false while the initial load is running
            public Exception LoadError;

            /// <summary>
            /// Cancels the load when nobody waits for it anymore (see <see cref="DecrementAndMaybeEvict"/>) or on
            /// cache <see cref="Dispose"/>. Disposed when the load finishes.
            /// </summary>
            public CancellationTokenSource LoadCts;
        }

        private readonly IDemTileSource _source;
        private readonly Dictionary<TileCoord, Entry> _entries = new Dictionary<TileCoord, Entry>(16);

        /// <summary>
        /// Cancelled in <see cref="Dispose"/>. Never disposed itself: loads and waits that wake up inside
        /// <c>Cancel()</c> still access its token.
        /// </summary>
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private bool _disposed;

        public int TileSizePixels => _source.TileSizePixels;

        /// <summary>
        /// Gets the memory held by the heights of all loaded DEM tiles, for statistics. A DEM tile is usually
        /// held only while a ground grid is being built, so the value stays small.
        /// </summary>
        public long MemoryBytes
        {
            get
            {
                long bytes = 0;
                foreach (var entry in _entries.Values)
                    if (entry.Heights.IsCreated) bytes += (long)entry.Heights.Length * sizeof(float);
                return bytes;
            }
        }

        public DemTileCache(IDemTileSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>
        /// Acquires a handle to a DEM tile and increments its ref count. Starts the load if the tile is not loaded
        /// yet, or waits (by polling) for a load already in flight. On cancellation of <paramref name="ct"/> or
        /// <see cref="Dispose"/>, the ref count increment is rolled back and
        /// <see cref="OperationCanceledException"/> is thrown.
        /// </summary>
        /// <exception cref="InvalidOperationException">The load failed; the source error is the inner exception.</exception>
        public async Awaitable<DemTileHandle> AcquireAsync(TileCoord coord, CancellationToken ct)
        {
            ThrowIfDisposed();

            // An aborted load that has not finished yet (the source did not react to cancellation synchronously)
            // is unusable for a new caller, who would otherwise get someone else's cancellation as an error. Its
            // entry frees itself: Evict checks that the dictionary still holds that entry.
            if (!_entries.TryGetValue(coord, out var entry) ||
                (!entry.LoadCompleted && entry.LoadCts.IsCancellationRequested))
            {
                // First caller: create the entry and start the load.
                entry = new Entry
                {
                    RefCount = 1,
                    LoadCompleted = false,
                    LoadCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token),
                };
                _entries[coord] = entry;
                _ = LoadIntoEntryAsync(coord, entry);
            }
            else
            {
                entry.RefCount++;
            }

            // Wait for the initial load, if still running. Cancelling ct wakes the wait immediately, inside
            // Cancel(). If the ref count drops to 0 while the load is running, LoadIntoEntryAsync frees the entry.
            try
            {
                while (!entry.LoadCompleted)
                {
                    _shutdownCts.Token.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync(ct);
                }
                _shutdownCts.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                DecrementAndMaybeEvict(coord, entry);
                throw;
            }

            if (entry.LoadError != null)
            {
                DecrementAndMaybeEvict(coord, entry);
                throw new InvalidOperationException($"DEM tile {coord} load failed", entry.LoadError);
            }

            return new DemTileHandle(this, coord, entry.Heights);
        }

        internal void Release(TileCoord coord)
        {
            if (!_entries.TryGetValue(coord, out var entry)) return;
            DecrementAndMaybeEvict(coord, entry);
        }

        private void DecrementAndMaybeEvict(TileCoord coord, Entry entry)
        {
            entry.RefCount--;
            if (entry.RefCount > 0) return;
            if (entry.LoadCompleted) Evict(coord, entry);
            // Nobody waits for the load anymore: abort it. It usually finishes right inside Cancel(), and
            // LoadIntoEntryAsync frees the entry.
            else entry.LoadCts.Cancel();
        }

        private void Evict(TileCoord coord, Entry entry)
        {
            if (entry.Heights.IsCreated) entry.Heights.Dispose();
            if (_entries.TryGetValue(coord, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(coord);
        }

        private async Awaitable LoadIntoEntryAsync(TileCoord coord, Entry entry)
        {
            try
            {
                var heights = await _source.GetHeightsAsync(coord, Allocator.Persistent, entry.LoadCts.Token);
                entry.Heights = heights;
            }
            catch (Exception ex)
            {
                entry.LoadError = ex;
            }
            finally
            {
                entry.LoadCompleted = true;
                // Free the entry if all referrers cancelled during the load. Heights that arrive after Dispose
                // for a caller still waiting are freed when that caller rolls back its ref count.
                if (entry.RefCount <= 0) Evict(coord, entry);
                entry.LoadCts.Dispose();
            }
        }

        /// <summary>
        /// Aborts loads (synchronously: waits wake up inside <c>Cancel()</c>) and frees tiles without handles.
        /// Tiles with live handles are freed by their last <see cref="DemTileHandle.Dispose"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdownCts.Cancel();

            var unreferenced = new List<TileCoord>();
            foreach (var kv in _entries)
                if (kv.Value.RefCount <= 0 && kv.Value.LoadCompleted) unreferenced.Add(kv.Key);
            foreach (var coord in unreferenced) Evict(coord, _entries[coord]);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DemTileCache));
        }
    }

    /// <summary>
    /// Handle for one acquisition of a DEM tile. Dispose decrements the ref count in the cache.
    /// </summary>
    internal readonly struct DemTileHandle : IDisposable
    {
        private readonly DemTileCache _owner;
        public TileCoord Coord { get; }
        public NativeArray<float> Heights { get; }

        internal DemTileHandle(DemTileCache owner, TileCoord coord, NativeArray<float> heights)
        {
            _owner = owner;
            Coord = coord;
            Heights = heights;
        }

        public void Dispose() => _owner?.Release(Coord);
    }
}
