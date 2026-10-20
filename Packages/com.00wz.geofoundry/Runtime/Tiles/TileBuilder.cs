using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Core.Sources;
using GeoFoundry.Ground;
using Unity.Collections;
using UnityEngine;

namespace GeoFoundry.Tiles
{
    /// <summary>
    /// Fills an already created <see cref="TileData"/>, already registered with the floating-origin system
    /// (the GameObject and rebase subscription are created before this class is called; see
    /// <see cref="TileLifecycleManager.LoadTileAsync"/>), with content: concurrently acquires the ground grid handle
    /// of the center (and of 8 neighbors, if enabled) and the MVT bytes, then builds terrain and vector geometry.
    /// The center grid always exists (flat without relief, see <see cref="FlatGroundGridProvider"/>); neighbor grids
    /// are either all present or all absent (see <see cref="TileGroundGrids"/>).
    ///
    /// Creates no GameObjects and knows nothing about the floating-origin pivot; that is the concern of
    /// <see cref="TileData"/> and <see cref="TileLifecycleManager"/>. Geometry is baked relative to the tile's local
    /// frame (see <c>frame</c> in <see cref="PopulateAsync"/>), independent of the pivot — the RTC pattern, see
    /// <see cref="MercatorPivot"/>.
    /// </summary>
    public sealed class TileBuilder
    {
        // Tile grid slots: center and 8 neighbors. Offsets are in GridOffsets, in the same order.
        private const int Center = 0, North = 1, South = 2, East = 3, West = 4;
        private const int NorthEast = 5, NorthWest = 6, SouthEast = 7, SouthWest = 8;
        private const int GridSlots = 9;

        private static readonly (int dx, int dy)[] GridOffsets =
        {
            (0, 0), (0, -1), (0, +1), (+1, 0), (-1, 0), (+1, -1), (-1, -1), (+1, +1), (-1, +1),
        };

        private readonly GroundGridProvider _gridProvider;
        private readonly IVectorTileSource _vectorSource;
        private readonly ITerrainHandler _terrainHandler;
        private readonly LayerHandlerRegistry _handlerRegistry;
        private readonly bool _loadNeighborGrids;

        /// <summary>Creates a builder.</summary>
        /// <param name="gridProvider">
        /// Source of the ground under tiles: relief (<see cref="DemGroundGridProvider"/>) or a plane
        /// (<see cref="FlatGroundGridProvider"/>). Required: the grid is used to build the terrain and to clip layer
        /// geometry to the tile bounds.
        /// </param>
        /// <param name="vectorSource">Source of MVT tile bytes.</param>
        /// <param name="terrainHandler">Builds the tile terrain; <c>null</c> for tiles without terrain.</param>
        /// <param name="handlerRegistry">Layer handlers that build vector geometry.</param>
        /// <param name="loadNeighborGrids">
        /// Whether to also acquire the 8 neighbor grids for each tile (<see cref="TileGroundGrids"/>). Built-in
        /// handlers do not need them, and they cost 8 extra grids and a 5×5 DEM neighborhood instead of 3×3; enable
        /// only if a custom handler samples neighbor elevations.
        /// </param>
        public TileBuilder(
            GroundGridProvider gridProvider,
            IVectorTileSource vectorSource,
            ITerrainHandler terrainHandler,
            LayerHandlerRegistry handlerRegistry,
            bool loadNeighborGrids)
        {
            _gridProvider      = gridProvider ?? throw new ArgumentNullException(nameof(gridProvider));
            _vectorSource      = vectorSource ?? throw new ArgumentNullException(nameof(vectorSource));
            _terrainHandler    = terrainHandler;
            _handlerRegistry   = handlerRegistry ?? throw new ArgumentNullException(nameof(handlerRegistry));
            _loadNeighborGrids = loadNeighborGrids;
        }

        /// <summary>
        /// Fills <paramref name="tileData"/> with terrain and vector content of tile <paramref name="coord"/>.
        /// On any error or cancellation, whatever has already been attached to <paramref name="tileData"/> (terrain,
        /// some layer instances) stays attached; the caller that owns <paramref name="tileData"/> cleans up the
        /// partially built tile (see <see cref="TileLifecycleManager.LoadTileAsync"/>, which disposes it on failure).
        /// Cancellation of <paramref name="ct"/> always ends with <see cref="OperationCanceledException"/>, even if
        /// layer handlers return <c>null</c> on cancellation: normal completion means the tile is complete. Failure to
        /// acquire any grid fails the tile build.
        /// </summary>
        internal async Awaitable PopulateAsync(TileData tileData, TileCoord coord, CancellationToken ct)
        {
            // The tile's local frame (RTC pattern, see MercatorPivot) is centered on the tile itself, independent of
            // the global floating-origin pivot, and does not change on rebase.
            var frame = new MercatorPivot(coord.CenterMeters());

            GroundGridHandle?[] gridHandles = null;

            // MVT load whose result has not been taken yet: if the build stops earlier (cancellation, terrain
            // error), the bytes are released in finally; see ReleaseWhenCompletedAsync.
            Awaitable<NativeArray<byte>> pendingMvt = null;

            try
            {
                var gridTasks = new Awaitable<GroundGridHandle>[GridSlots];
                gridTasks[Center] = _gridProvider.AcquireAsync(coord, ct);
                if (_loadNeighborGrids)
                {
                    // All 8 neighbors, including those beyond the pole: there is no tile there, but there is a grid
                    // (the provider does not request DEM beyond the pole). Neighbors are not wrapped in longitude, so
                    // the grid lies next to the tile (DemGroundGridProvider wraps the DEM request itself).
                    for (int slot = Center + 1; slot < GridSlots; slot++)
                    {
                        var (dx, dy) = GridOffsets[slot];
                        gridTasks[slot] = _gridProvider.AcquireAsync(coord.Offset(dx, dy), ct);
                    }
                }

                pendingMvt = _vectorSource.GetTileAsync(coord, Allocator.Persistent, ct);

                gridHandles = await AcquireGridsAsync(gridTasks);

                ct.ThrowIfCancellationRequested();

                var centerGrid = gridHandles[Center].Value.Grid;

                if (_terrainHandler != null)
                {
                    var tCtx = new TerrainLoadContext(centerGrid, coord, frame, tileData.Root, ct);
                    var terrainInst = await _terrainHandler.OnTerrainLoadedAsync(tCtx);
                    if (terrainInst != null) tileData.AttachTerrain(terrainInst);
                }

                ct.ThrowIfCancellationRequested();

                // From here on the load result is ours to release (an Awaitable cannot be awaited twice).
                var mvtTask = pendingMvt;
                pendingMvt = null;
                NativeArray<byte> mvtBytes = await mvtTask;
                NativeMvtTile mvtTile;
                try   { mvtTile = MvtParser.Parse(mvtBytes); }
                finally { if (mvtBytes.IsCreated) mvtBytes.Dispose(); }

                try
                {
                    ct.ThrowIfCancellationRequested();

                    var grids = new TileGroundGrids(
                        centerGrid,
                        GridOrDefault(gridHandles[North]), GridOrDefault(gridHandles[South]),
                        GridOrDefault(gridHandles[East]), GridOrDefault(gridHandles[West]),
                        GridOrDefault(gridHandles[NorthEast]), GridOrDefault(gridHandles[NorthWest]),
                        GridOrDefault(gridHandles[SouthEast]), GridOrDefault(gridHandles[SouthWest]));

                    await VectorTileBuilder.BuildAsync(
                        mvtTile, coord, tileData, frame,
                        grids, _handlerRegistry, ct);

                    // Layer handlers return null on cancellation and BuildAsync completes normally; without this
                    // check a cancelled tile would be returned as complete.
                    ct.ThrowIfCancellationRequested();

                    tileData.AttachHandles(CollectHandles(gridHandles));
                    gridHandles = null;
                }
                finally
                {
                    mvtTile.Dispose();
                }
            }
            finally
            {
                DisposeHandles(gridHandles);

                if (pendingMvt != null) _ = ReleaseWhenCompletedAsync(pendingMvt);
            }
        }

        /// <summary>
        /// Releases the bytes of an MVT load whose result <see cref="PopulateAsync"/> did not take because it stopped
        /// earlier. Not awaited by the caller: on cancellation the load is aborted by the same token, and on a build
        /// error there is no need to wait for it before reporting the error.
        /// </summary>
        private static async Awaitable ReleaseWhenCompletedAsync(Awaitable<NativeArray<byte>> mvtTask)
        {
            try
            {
                var bytes = await mvtTask;
                if (bytes.IsCreated) bytes.Dispose();
            }
            catch (Exception)
            {
                // Cancellation or load error: nothing to release.
            }
        }

        /// <summary>
        /// Awaits all started grid acquisitions (a <c>null</c> task means neighbors are not requested and the slot
        /// stays empty). Does not stop on the first error: the other acquisitions still complete and return held
        /// handles, which must be awaited and released, or the grid ref counts leak. Then rethrows the first error.
        /// </summary>
        private static async Awaitable<GroundGridHandle?[]> AcquireGridsAsync(Awaitable<GroundGridHandle>[] tasks)
        {
            var handles = new GroundGridHandle?[tasks.Length];
            ExceptionDispatchInfo firstError = null;

            for (int i = 0; i < tasks.Length; i++)
            {
                if (tasks[i] == null) continue;
                try { handles[i] = await tasks[i]; }
                catch (Exception ex) { firstError ??= ExceptionDispatchInfo.Capture(ex); }
            }

            if (firstError != null)
            {
                DisposeHandles(handles);
                firstError.Throw();
            }
            return handles;
        }

        private static GroundGrid GridOrDefault(GroundGridHandle? handle) => handle?.Grid ?? default;

        private static GroundGridHandle[] CollectHandles(GroundGridHandle?[] handles)
        {
            var list = new List<GroundGridHandle>(handles.Length);
            foreach (var h in handles)
                if (h.HasValue) list.Add(h.Value);
            return list.ToArray();
        }

        private static void DisposeHandles(GroundGridHandle?[] handles)
        {
            if (handles == null) return;
            foreach (var h in handles) h?.Dispose();
        }
    }
}
