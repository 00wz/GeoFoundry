using System;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Ground
{
    /// <summary>
    /// <see cref="GroundGridProvider"/> without terrain elevation: the ground is a plane at
    /// <see cref="GroundHeightDefaults.FallbackHeightMeters"/>. Each tile gets a 1×1 grid (one cell, two
    /// triangles): the terrain handler builds a flat terrain base from it, and <see cref="NativeGroundProjector"/>
    /// clips layer geometry to the tile boundary with it just as with an elevation grid, without N×N grid work and
    /// without DEM requests.
    ///
    /// All grids share the same heights and normals (4 vertices at one height, normals +Y), created once in the
    /// constructor; a tile's grid differs only in its bounds. Acquiring a grid is therefore synchronous and
    /// allocation-free.
    ///
    /// Ownership is the same as in <see cref="DemGroundGridProvider"/>: the shared arrays are freed by the last
    /// handle, also after the provider's <see cref="Dispose"/> (a holder releases its handle only after its jobs
    /// complete, and <c>TileLifecycleManager.Dispose</c> does not wait for them).
    /// </summary>
    public sealed class FlatGroundGridProvider : GroundGridProvider, IGroundGridOwner
    {
        private const int SegmentsPerSide = 1;
        private const int VertexCount = (SegmentsPerSide + 1) * (SegmentsPerSide + 1);

        private NativeArray<float> _heights;
        private NativeArray<Vector3> _normals;
        private int _liveHandles;
        private bool _disposed;

        public FlatGroundGridProvider()
        {
            _heights = new NativeArray<float>(VertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _normals = new NativeArray<Vector3>(VertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < VertexCount; i++)
            {
                _heights[i] = GroundHeightDefaults.FallbackHeightMeters;
                _normals[i] = Vector3.up;
            }
        }

        /// <summary>
        /// Acquires the grid of tile <paramref name="coord"/> as an already completed <see cref="Awaitable"/>.
        /// Cancellation of <paramref name="ct"/> and calls after <see cref="Dispose"/> throw when awaited, not
        /// synchronously, matching the async <see cref="DemGroundGridProvider.AcquireAsync"/>: a caller that starts
        /// several acquisitions in a row does not lose handles already issued.
        /// </summary>
        public override Awaitable<GroundGridHandle> AcquireAsync(TileCoord coord, CancellationToken ct)
        {
            var completion = new AwaitableCompletionSource<GroundGridHandle>();
            if (_disposed)
            {
                completion.SetException(new ObjectDisposedException(nameof(FlatGroundGridProvider)));
            }
            else if (ct.IsCancellationRequested)
            {
                completion.SetCanceled();
            }
            else
            {
                _liveHandles++;
                completion.SetResult(new GroundGridHandle(this, coord, GridOf(coord)));
            }
            return completion.Awaitable;
        }

        /// <summary>Flat ground exists everywhere, so this returns <c>false</c> only after <see cref="Dispose"/>.</summary>
        public override bool TryGet(TileCoord coord, out GroundGrid grid)
        {
            if (_disposed)
            {
                grid = default;
                return false;
            }
            grid = GridOf(coord);
            return true;
        }

        public override long MemoryBytes =>
            _heights.IsCreated ? (long)VertexCount * (sizeof(float) + 3 * sizeof(float)) : 0;

        void IGroundGridOwner.Release(TileCoord coord)
        {
            _liveHandles--;
            if (_disposed && _liveHandles <= 0) DisposeArrays();
        }

        /// <summary>
        /// Rejects further <see cref="AcquireAsync"/>/<see cref="TryGet"/> calls. Frees the shared arrays immediately
        /// if there are no live handles; otherwise the last <see cref="GroundGridHandle.Dispose"/> frees them.
        /// </summary>
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_liveHandles <= 0) DisposeArrays();
        }

        private GroundGrid GridOf(TileCoord coord) =>
            new GroundGrid(SegmentsPerSide, _heights, _normals, coord.BoundsMeters());

        private void DisposeArrays()
        {
            if (_heights.IsCreated) _heights.Dispose();
            if (_normals.IsCreated) _normals.Dispose();
        }
    }
}
