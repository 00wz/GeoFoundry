using System;
using System.Collections.Generic;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Sources;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Ground
{
    /// <summary>
    /// Ref-counted provider of per-tile <see cref="GroundGrid"/>s (heights and normals in persistent memory) built
    /// from DEM tiles. Acquires 9 DEM tiles through <see cref="DemTileCache"/>, builds (N+1)² heights and normals
    /// with <see cref="TerrainMeshBuilder.FillGridFromPaddedSampler"/> and returns a <see cref="GroundGridHandle"/>
    /// that decrements the ref count on Dispose.
    ///
    /// Data lives while at least one handle is not disposed. This lets tile data hold 9 handles (center plus
    /// 8 neighbors) for cross-tile height sampling of buildings.
    ///
    /// Ownership: grid memory is freed by its last handle, also after the provider's <see cref="Dispose"/>
    /// (which aborts loads, rejects new <see cref="AcquireAsync"/> calls and frees only unreferenced grids).
    /// A handle holder releases it only after the jobs reading the grid complete
    /// (see <see cref="GeoFoundry.Core.JobHandleAwaitExtensions.WaitNonBlockingAsync"/>), so a grid is never freed
    /// under a running job, and borrowing code never keeps a <see cref="GroundGrid"/> copy pointing to freed memory.
    ///
    /// Missing DEM data is not a tile error and is handled the same way for the central tile and each of the
    /// 8 neighbors: a missing DEM tile is an invalid <see cref="Unity.Collections.NativeArray{T}"/> passed to
    /// <see cref="PaddedHeightSampler"/>, which substitutes
    /// <see cref="GeoFoundry.Core.Ground.GroundHeightDefaults.FallbackHeightMeters"/> for every missing pixel.
    /// A DEM tile is missing either because the source has no data for it (a regular result, see
    /// <see cref="IDemTileSource"/>, not logged) or because its load failed (logged once by
    /// <see cref="DemTileCache"/>).
    /// If only the central tile is missing, boundary normals still reflect the real slope towards the fallback
    /// height; if the central tile and all neighbors are missing, the whole grid is flat at the fallback height.
    /// <see cref="AcquireAsync"/> throws only on cancellation, Dispose or an unexpected internal error.
    ///
    /// DEM neighbors at the world edges: beyond the poles (<see cref="TileCoord.IsYInWorld"/>) no DEM is requested,
    /// since none exists; in longitude the neighbor wraps around (<see cref="TileCoord.WrapX"/>) because the world
    /// is closed. The grid's own coordinate and geometry do not wrap: a neighbor grid beyond the antimeridian lies
    /// next to the tile, not at the other edge of the map. A grid beyond a pole can also be acquired (as a neighbor
    /// of an edge-row tile): it has no DEM of its own and uses the fallback height, except at the edge shared with
    /// the boundary row, whose DEM tiles the tile's own grid already needs, so it makes no extra DEM requests.
    ///
    /// A grid load nobody waits for anymore (all <see cref="AcquireAsync"/> calls cancelled) is aborted together
    /// with its DEM loads, as in <see cref="DemTileCache"/>.
    /// </summary>
    public sealed class DemGroundGridProvider : GroundGridProvider, IGroundGridOwner
    {
        private sealed class Entry
        {
            public NativeArray<float> Heights;
            public NativeArray<Vector3> Normals;
            public double4 BoundsMeters;
            public int SegmentsPerSide;
            public int RefCount;
            public bool LoadCompleted;
            public Exception LoadError;

            /// <summary>
            /// Cancels the load when nobody waits for it anymore (see <see cref="DecrementAndMaybeEvict"/>) or on
            /// provider <see cref="Dispose"/>. Disposed when the load finishes.
            /// </summary>
            public CancellationTokenSource LoadCts;
        }

        private readonly IDemTileSource _demSource;
        private readonly DemTileCache _demCache;
        private readonly int _gridResolution;
        private readonly Dictionary<TileCoord, Entry> _entries = new(16);

        /// <summary>
        /// Cancelled in <see cref="Dispose"/>. Never disposed itself: loads and waits that wake up inside
        /// <c>Cancel()</c> still access its token.
        /// </summary>
        private readonly CancellationTokenSource _shutdownCts = new();
        private bool _disposed;

        public DemGroundGridProvider(IDemTileSource demSource, int gridResolution)
        {
            _demSource = demSource ?? throw new ArgumentNullException(nameof(demSource));
            _demCache = new DemTileCache(demSource);
            _gridResolution = Math.Max(1, gridResolution);
        }

        /// <summary>
        /// Acquires the ground grid for <paramref name="coord"/>, loading it or waiting for a load in flight, and
        /// increments its ref count; the caller must dispose the handle. Missing DEM data (including the central
        /// tile) does not throw: the fallback height is used where data is missing (see the class remarks). Throws
        /// only on cancellation of <paramref name="ct"/>, provider Dispose or an unexpected internal error.
        /// </summary>
        public override async Awaitable<GroundGridHandle> AcquireAsync(TileCoord coord, CancellationToken ct)
        {
            ThrowIfDisposed();

            // An aborted but unfinished load is unusable for a new caller (see DemTileCache.AcquireAsync).
            if (!_entries.TryGetValue(coord, out var entry) ||
                (!entry.LoadCompleted && entry.LoadCts.IsCancellationRequested))
            {
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

            // Cancelling ct wakes the wait immediately, inside Cancel(). If the ref count drops to 0 while the
            // load is running, LoadIntoEntryAsync frees the entry.
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
                throw new InvalidOperationException($"GroundGrid tile {coord} load failed", entry.LoadError);
            }

            var grid = new GroundGrid(entry.SegmentsPerSide, entry.Heights, entry.Normals,
                                      entry.BoundsMeters);
            return new GroundGridHandle(this, coord, grid);
        }

        /// <summary>
        /// Gets the memory held by heights and normals of all loaded ground grids plus the DEM tiles currently held
        /// by the internal cache, for statistics. Not counted in the tile budget: grids are shared by neighboring
        /// tiles and freed by ref count when no tile needs them.
        /// </summary>
        public override long MemoryBytes
        {
            get
            {
                long bytes = _demCache.MemoryBytes;
                foreach (var entry in _entries.Values)
                {
                    if (entry.Heights.IsCreated) bytes += (long)entry.Heights.Length * sizeof(float);
                    if (entry.Normals.IsCreated) bytes += (long)entry.Normals.Length * 3 * sizeof(float);
                }
                return bytes;
            }
        }

        /// <summary>
        /// Gets an already loaded grid without changing its ref count, for external queries such as camera height.
        /// Returns <c>false</c> if the grid is not loaded.
        /// </summary>
        public override bool TryGet(TileCoord coord, out GroundGrid grid)
        {
            if (!_disposed && _entries.TryGetValue(coord, out var entry) && entry.LoadCompleted && entry.LoadError == null)
            {
                grid = new GroundGrid(entry.SegmentsPerSide, entry.Heights, entry.Normals,
                                      entry.BoundsMeters);
                return true;
            }
            grid = default;
            return false;
        }

        void IGroundGridOwner.Release(TileCoord coord)
        {
            if (!_entries.TryGetValue(coord, out var entry)) return;
            DecrementAndMaybeEvict(coord, entry);
        }

        private void DecrementAndMaybeEvict(TileCoord coord, Entry entry)
        {
            entry.RefCount--;
            if (entry.RefCount > 0) return;
            if (entry.LoadCompleted) Evict(coord, entry);
            // Nobody waits for the load anymore: abort it together with its DEM loads. It usually finishes right
            // inside Cancel(), and LoadIntoEntryAsync frees the entry.
            else entry.LoadCts.Cancel();
        }

        private void Evict(TileCoord coord, Entry entry)
        {
            if (entry.Heights.IsCreated) entry.Heights.Dispose();
            if (entry.Normals.IsCreated) entry.Normals.Dispose();
            if (_entries.TryGetValue(coord, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(coord);
        }

        private async Awaitable LoadIntoEntryAsync(TileCoord coord, Entry entry)
        {
            DemTileHandle? hA = null, hN = null, hS = null, hE = null, hW = null;
            DemTileHandle? hNE = null, hNW = null, hSE = null, hSW = null;
            NativeArray<float> heights = default;
            NativeArray<Vector3> normals = default;
            var ct = entry.LoadCts.Token;
            try
            {
                var taskA  = AcquireDemOrNull(coord,                ct);
                var taskN  = AcquireDemOrNull(coord.Offset(0, -1),  ct);
                var taskS  = AcquireDemOrNull(coord.Offset(0, +1),  ct);
                var taskE  = AcquireDemOrNull(coord.Offset(+1, 0),  ct);
                var taskW  = AcquireDemOrNull(coord.Offset(-1, 0),  ct);
                var taskNE = AcquireDemOrNull(coord.Offset(+1, -1), ct);
                var taskNW = AcquireDemOrNull(coord.Offset(-1, -1), ct);
                var taskSE = AcquireDemOrNull(coord.Offset(+1, +1), ct);
                var taskSW = AcquireDemOrNull(coord.Offset(-1, +1), ct);

                hA  = await SafeAcquire(taskA);
                hN  = await SafeAcquire(taskN);
                hS  = await SafeAcquire(taskS);
                hE  = await SafeAcquire(taskE);
                hW  = await SafeAcquire(taskW);
                hNE = await SafeAcquire(taskNE);
                hNW = await SafeAcquire(taskNW);
                hSE = await SafeAcquire(taskSE);
                hSW = await SafeAcquire(taskSW);

                // SafeAcquire swallows cancellation: once the load is cancelled (provider Dispose or nobody waits
                // for it), every DEM tile is "missing", and without this check the grid would be allocated for nothing.
                ct.ThrowIfCancellationRequested();

                int vps = _gridResolution + 1;
                heights = new NativeArray<float>(vps * vps, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                normals = new NativeArray<Vector3>(vps * vps, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                // The central tile also goes through OrDefault: a missing central DEM tile is handled the same
                // way as a missing neighbor (see the class remarks), so real neighbor data still shapes the normals.
                var sampler = new PaddedHeightSampler(
                    central:   OrDefault(hA),
                    demWidth:  _demSource.TileSizePixels,
                    north:     OrDefault(hN),
                    south:     OrDefault(hS),
                    east:      OrDefault(hE),
                    west:      OrDefault(hW),
                    northEast: OrDefault(hNE),
                    northWest: OrDefault(hNW),
                    southEast: OrDefault(hSE),
                    southWest: OrDefault(hSW));

                TerrainMeshBuilder.FillGridFromPaddedSampler(
                    sampler, _gridResolution, coord, heights, normals);

                entry.Heights = heights;
                entry.Normals = normals;
                entry.BoundsMeters = coord.BoundsMeters();
                entry.SegmentsPerSide = _gridResolution;
                heights = default;
                normals = default;
            }
            catch (OperationCanceledException ex)
            {
                // Provider shutdown or a load nobody waits for is not a load error.
                entry.LoadError = ex;
            }
            catch (Exception ex)
            {
                entry.LoadError = ex;
                Debug.LogError($"[GeoFoundry] GroundGrid {coord} load failed: {ex}");
            }
            finally
            {
                hA?.Dispose(); hN?.Dispose(); hS?.Dispose(); hE?.Dispose(); hW?.Dispose();
                hNE?.Dispose(); hNW?.Dispose(); hSE?.Dispose(); hSW?.Dispose();
                if (heights.IsCreated) heights.Dispose();
                if (normals.IsCreated) normals.Dispose();

                entry.LoadCompleted = true;

                // All waiters cancelled: free the grid. Otherwise the last handle frees it (or the ref count
                // rollback of a waiter that wakes up after provider Dispose).
                if (entry.RefCount <= 0) Evict(coord, entry);
                entry.LoadCts.Dispose();
            }
        }

        /// <summary>
        /// Acquires the DEM tile of neighbor <paramref name="demCoord"/> (see the class remarks on world edges):
        /// <c>null</c> without a request beyond the poles, a wrapped tile in longitude.
        /// </summary>
        private Awaitable<DemTileHandle> AcquireDemOrNull(TileCoord demCoord, CancellationToken ct) =>
            demCoord.IsYInWorld ? _demCache.AcquireAsync(demCoord.WrapX(), ct) : null;

        /// <summary>
        /// Returns <c>null</c> for a failed or cancelled DEM load: the grid uses the fallback height there. Not logged
        /// here: the cache has already logged the failure once per load, while this runs once per grid.
        /// </summary>
        private static async Awaitable<DemTileHandle?> SafeAcquire(Awaitable<DemTileHandle> task)
        {
            if (task == null) return null;
            try { return await task; }
            catch (Exception) { return null; }
        }

        private static NativeArray<float> OrDefault(DemTileHandle? h) =>
            h.HasValue ? h.Value.Heights : default;

        /// <summary>
        /// Aborts grid loads (synchronously: waits wake up inside <c>Cancel()</c>) and frees unreferenced grids and
        /// the DEM cache. Grids with live handles are left alone: their last <see cref="GroundGridHandle.Dispose"/>
        /// frees them once the holder's jobs reading the grid have completed (see the class remarks).
        /// </summary>
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdownCts.Cancel();

            var unreferenced = new List<TileCoord>();
            foreach (var kv in _entries)
                if (kv.Value.RefCount <= 0 && kv.Value.LoadCompleted) unreferenced.Add(kv.Key);
            foreach (var coord in unreferenced) Evict(coord, _entries[coord]);

            _demCache.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DemGroundGridProvider));
        }
    }
}
