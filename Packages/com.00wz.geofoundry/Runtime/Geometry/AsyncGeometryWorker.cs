using System;
using System.Threading;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Asynchronous geometry building on the Job System: a facade over the geometry jobs. Every method runs the
    /// same cycle: <see cref="GeometryBuffers.Allocate"/> → job factory → schedule → non-blocking wait
    /// (<see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>) → <see cref="Mesh"/> assembly through
    /// <see cref="MeshAssembler"/>. Methods come in pairs: per-feature (<c>*MeshesAsync</c>, one mesh per
    /// feature, <see cref="GeometryResult"/>) and single (<c>*MeshAsync</c>, one mesh for the whole tile layer;
    /// the same job with <c>EmitGlobalIndices = true</c>). All of them return <c>null</c> if <c>ct</c> is
    /// cancelled before the result is ready.
    ///
    /// <c>grid</c> is required and must be valid (<c>Heights.IsCreated</c>, otherwise
    /// <see cref="ArgumentException"/>): geometry is draped onto it and clipped to the tile boundary. There is
    /// no "no ground" fallback: what lies under the tile is decided by a single ground grid source
    /// (<c>GroundGridProvider</c>; without terrain elevation it is a flat 1×1 grid).
    ///
    /// Inputs (<c>layer</c>, <c>grid</c>/<c>grids</c>, <c>featureColors</c>, building properties) are borrowed
    /// from the caller. Control returns only after every scheduled job has completed, including on cancellation
    /// (the wait does not block the thread), so the caller may release the inputs right after the call returns.
    /// Stateless; handlers call it directly.
    /// </summary>
    public static class AsyncGeometryWorker
    {
        // ---- Surfaces ----------------------------------------------------------

        /// <summary>
        /// Builds one mesh per Polygon feature of a flat-surface layer using <see cref="SurfaceBuildJob"/>.
        /// <paramref name="featureColors"/> is a borrowed per-feature vertex color array (not disposed by this
        /// method); an empty/default array means the schema has no per-feature color, and the job writes opaque
        /// white to every vertex.
        /// </summary>
        internal static async Awaitable<GeometryResult?> BuildSurfaceMeshesAsync(
            NativeMvtLayer layer, TileCoord coord, GroundGrid grid, MercatorPivot localFrame,
            float uvScalePerMeter, NativeArray<Color32> featureColors, CancellationToken ct)
        {
            JobInputs.RequireGrid(grid, nameof(grid));
            var buffers = GeometryBuffers.Allocate(layer.FeatureCount);
            var job = CreateSurfaceJob(layer, coord, grid, localFrame, uvScalePerMeter, featureColors, buffers);
            return await RunPerFeatureAsync(job.Schedule(), buffers, ct);
        }

        /// <summary>
        /// Builds all Polygon geometry of a tile's flat-surface layer as a single <see cref="Mesh"/>
        /// (an alternative to <see cref="BuildSurfaceMeshesAsync"/>: the whole layer in one draw call). See
        /// <see cref="MeshAssembler.BuildSingleMeshAsync"/> for assembly details. Also returns <c>null</c> if the
        /// layer has no geometry at all.
        /// </summary>
        public static async Awaitable<Mesh> BuildSurfaceMeshAsync(
            NativeMvtLayer layer, TileCoord coord, GroundGrid grid, MercatorPivot localFrame,
            float uvScalePerMeter, NativeArray<Color32> featureColors, CancellationToken ct)
        {
            JobInputs.RequireGrid(grid, nameof(grid));
            var buffers = GeometryBuffers.Allocate(layer.FeatureCount);
            var job = CreateSurfaceJob(layer, coord, grid, localFrame, uvScalePerMeter, featureColors, buffers);
            job.EmitGlobalIndices = true;
            return await RunSingleMeshAsync(job.Schedule(), buffers, $"Surface_{coord}", ct);
        }

        // ---- Roads -------------------------------------------------------------

        /// <summary>
        /// Builds one mesh per LineString feature of a road layer using <see cref="RoadBuildJob"/>.
        /// <paramref name="widthSelector"/> carries the schema interpretation (e.g. OMT <c>class</c> → width or
        /// "do not render"), which belongs to the calling handler that owns the schema. It is invoked on the main
        /// thread before the job is scheduled, once per feature; a result ≤ 0 means the feature is not rendered.
        /// For <paramref name="featureColors"/> see <see cref="BuildSurfaceMeshesAsync"/>.
        /// </summary>
        internal static async Awaitable<GeometryResult?> BuildRoadMeshesAsync(
            NativeMvtLayer layer, Func<NativeMvtFeature, float> widthSelector,
            TileCoord coord, GroundGrid grid, MercatorPivot localFrame, NativeArray<Color32> featureColors, CancellationToken ct)
        {
            JobInputs.RequireGrid(grid, nameof(grid));
            var buffers = GeometryBuffers.Allocate(layer.FeatureCount);
            var job = CreateRoadJob(layer, widthSelector, coord, grid, localFrame, featureColors, buffers);
            return await RunPerFeatureAsync(job.Schedule(), buffers, ct);
        }

        /// <summary>
        /// Builds all LineString geometry of a tile's road layer as a single <see cref="Mesh"/>; an alternative to
        /// <see cref="BuildRoadMeshesAsync"/>. See <see cref="BuildSurfaceMeshAsync"/>.
        /// </summary>
        public static async Awaitable<Mesh> BuildRoadMeshAsync(
            NativeMvtLayer layer, Func<NativeMvtFeature, float> widthSelector,
            TileCoord coord, GroundGrid grid, MercatorPivot localFrame, NativeArray<Color32> featureColors, CancellationToken ct)
        {
            JobInputs.RequireGrid(grid, nameof(grid));
            var buffers = GeometryBuffers.Allocate(layer.FeatureCount);
            var job = CreateRoadJob(layer, widthSelector, coord, grid, localFrame, featureColors, buffers);
            job.EmitGlobalIndices = true;
            return await RunSingleMeshAsync(job.Schedule(), buffers, $"Road_{coord}", ct);
        }

        // ---- Buildings ---------------------------------------------------------

        /// <summary>
        /// Builds one mesh per Polygon feature of a building layer using <see cref="BuildingBuildJob"/>
        /// (flat-base extrusion: the per-feature terrain base is computed inside the job, and there is no way to
        /// pass in a precomputed height shared by a group of features). Not used by the default
        /// <c>BuildingsLayerHandler</c> (OMT), see <see cref="BuildBuildingTerrainMeshesAsync"/>. Kept as a starting
        /// point for schemas that group features (e.g. by <c>group_id</c>); both the method and the job need
        /// further work before they are usable for that, so this is not a ready-to-use alternative.
        /// The method builds geometry only: <paramref name="hidden"/>, <paramref name="topMeters"/>,
        /// <paramref name="bottomMeters"/> and <paramref name="featureColors"/> are borrowed (neither owned nor
        /// disposed here) results of schema interpretation prepared beforehand by the calling handler, which owns and
        /// disposes them. Rendering
        /// properties that do not affect geometry are not passed in: <see cref="GeometryResult.FeatureIndices"/>
        /// gives the handler the source feature index of each output mesh so it can resolve them itself.
        /// Reads all 9 grids of <paramref name="grids"/> and throws <see cref="ArgumentException"/> if any of
        /// them is not set (<c>TileBuilder</c> provides neighbor grids only when
        /// <c>GeoFoundryConfig.LoadNeighborGrids</c> is enabled).
        /// </summary>
        internal static async Awaitable<GeometryResult?> BuildBuildingMeshesAsync(
            NativeMvtLayer layer,
            NativeArray<bool> hidden, NativeArray<float> topMeters, NativeArray<float> bottomMeters,
            NativeArray<Color32> featureColors,
            TileGroundGrids grids, TileCoord coord, MercatorPivot localFrame, CancellationToken ct)
        {
            JobInputs.RequireAllGrids(grids, nameof(grids));
            var buffers = GeometryBuffers.Allocate(layer.FeatureCount);

            var job = new BuildingBuildJob
            {
                FeatureGeomTypes = layer.FeatureGeomTypesSlice,
                FeatureRingRange = layer.FeatureRingRangeSlice,
                Hidden = hidden,
                TopMeters = topMeters,
                BottomMeters = bottomMeters,
                FeatureColors = featureColors,
                Rings = layer.Rings,
                RingPoints = layer.RingPoints,
                GridCenter = JobInputs.MakeSamplerGridView(grids.Center),
                GridNorth = JobInputs.MakeSamplerGridView(grids.North),
                GridSouth = JobInputs.MakeSamplerGridView(grids.South),
                GridEast = JobInputs.MakeSamplerGridView(grids.East),
                GridWest = JobInputs.MakeSamplerGridView(grids.West),
                GridNorthEast = JobInputs.MakeSamplerGridView(grids.NorthEast),
                GridNorthWest = JobInputs.MakeSamplerGridView(grids.NorthWest),
                GridSouthEast = JobInputs.MakeSamplerGridView(grids.SouthEast),
                GridSouthWest = JobInputs.MakeSamplerGridView(grids.SouthWest),
                Coord = coord,
                Extent = layer.Extent,
                LocalFrame = localFrame,
                Positions = buffers.Positions,
                Normals = buffers.Normals,
                Uvs = buffers.Uvs,
                Colors = buffers.Colors,
                Triangles = buffers.Triangles,
                Ranges = buffers.Ranges,
                RangeFeatureIndices = buffers.RangeFeatureIndices,
            };
            return await RunPerFeatureAsync(job.Schedule(), buffers, ct);
        }

        /// <summary>
        /// Builds one mesh per Polygon feature of a building layer using <see cref="BuildingTerrainBuildJob"/>.
        /// The building stays a 3D volume (roof, walls, optional floor), but every point's height is measured from
        /// the terrain directly beneath it (terrain-conforming) rather than from a single per-feature constant; see
        /// the job's documentation. Uses the same single-tile <paramref name="grid"/> as
        /// <see cref="BuildSurfaceMeshesAsync"/> (not the 9-grid multi-tile input of the legacy
        /// <see cref="BuildBuildingMeshesAsync"/>). Parameters and ownership are as in
        /// <see cref="BuildBuildingMeshesAsync"/>.
        /// </summary>
        internal static async Awaitable<GeometryResult?> BuildBuildingTerrainMeshesAsync(
            NativeMvtLayer layer,
            NativeArray<bool> hidden, NativeArray<float> topMeters, NativeArray<float> bottomMeters,
            NativeArray<Color32> featureColors,
            GroundGrid grid, TileCoord coord, MercatorPivot localFrame, CancellationToken ct)
        {
            JobInputs.RequireGrid(grid, nameof(grid));
            var buffers = GeometryBuffers.Allocate(layer.FeatureCount);
            var job = CreateBuildingTerrainJob(layer, hidden, topMeters, bottomMeters, featureColors, grid, coord, localFrame, buffers);
            return await RunPerFeatureAsync(job.Schedule(), buffers, ct);
        }

        /// <summary>
        /// Builds all Polygon geometry of a tile's building layer as a single <see cref="Mesh"/>; an alternative to
        /// <see cref="BuildBuildingTerrainMeshesAsync"/>. See <see cref="BuildSurfaceMeshAsync"/>.
        /// </summary>
        public static async Awaitable<Mesh> BuildBuildingTerrainMeshAsync(
            NativeMvtLayer layer,
            NativeArray<bool> hidden, NativeArray<float> topMeters, NativeArray<float> bottomMeters,
            NativeArray<Color32> featureColors,
            GroundGrid grid, TileCoord coord, MercatorPivot localFrame, CancellationToken ct)
        {
            JobInputs.RequireGrid(grid, nameof(grid));
            var buffers = GeometryBuffers.Allocate(layer.FeatureCount);
            var job = CreateBuildingTerrainJob(layer, hidden, topMeters, bottomMeters, featureColors, grid, coord, localFrame, buffers);
            job.EmitGlobalIndices = true;
            return await RunSingleMeshAsync(job.Schedule(), buffers, $"Buildings_{coord}", ct);
        }

        // ---- Terrain -----------------------------------------------------------

        /// <summary>
        /// Builds the tile's terrain mesh using <see cref="TerrainBuildJob"/>. Unlike surfaces, roads and buildings,
        /// this produces exactly one <see cref="Mesh"/> per call (no per-feature batch), so a single
        /// <see cref="MeshRange"/> covers the whole buffer. <paramref name="grid"/> must be valid, as for the other
        /// methods (see the class documentation); without terrain elevation it is a flat 1×1 grid and the mesh is
        /// a single quad.
        /// </summary>
        public static async Awaitable<Mesh> BuildTerrainMeshAsync(GroundGrid grid, TileCoord coord, MercatorPivot localFrame, CancellationToken ct)
        {
            JobInputs.RequireGrid(grid, nameof(grid));
            int n = grid.SegmentsPerSide;
            var buffers = GeometryBuffers.Allocate(1, vertexCapacity: (n + 1) * (n + 1), indexCapacity: n * n * 6);

            var job = new TerrainBuildJob
            {
                Grid = grid,
                LocalFrame = localFrame,
                Positions = buffers.Positions,
                Normals = buffers.Normals,
                Uvs = buffers.Uvs,
                Triangles = buffers.Triangles,
            };
            if (!await CompleteOrDisposeAsync(job.Schedule(), buffers, ct)) return null;

            try
            {
                var ranges = new NativeArray<MeshRange>(1, Allocator.Temp)
                {
                    [0] = new MeshRange(0, buffers.Positions.Length, 0, buffers.Triangles.Length),
                };
                var meshes = MeshAssembler.BuildMeshes(
                    buffers.Positions.AsArray(), buffers.Normals.AsArray(), buffers.Uvs.AsArray(),
                    buffers.Triangles.AsArray(), ranges);
                ranges.Dispose();

                var mesh = meshes[0];
                mesh.name = $"Terrain_{coord}";
                return mesh;
            }
            finally
            {
                buffers.Dispose();
            }
        }

        // ---- Job factories (all fields except EmitGlobalIndices) ---------------

        private static SurfaceBuildJob CreateSurfaceJob(
            NativeMvtLayer layer, TileCoord coord, GroundGrid grid, MercatorPivot localFrame,
            float uvScalePerMeter, NativeArray<Color32> featureColors, GeometryBuffers buffers)
        {
            return new SurfaceBuildJob
            {
                FeatureGeomTypes = layer.FeatureGeomTypesSlice,
                FeatureRingRange = layer.FeatureRingRangeSlice,
                Rings = layer.Rings,
                RingPoints = layer.RingPoints,
                FeatureColors = JobInputs.PrepareFeatureColors(featureColors, buffers),
                Coord = coord,
                Extent = layer.Extent,
                UvScalePerMeter = uvScalePerMeter,
                Grid = grid,
                LocalFrame = localFrame,
                Positions = buffers.Positions,
                Normals = buffers.Normals,
                Uvs = buffers.Uvs,
                Colors = buffers.Colors,
                Triangles = buffers.Triangles,
                Ranges = buffers.Ranges,
                RangeFeatureIndices = buffers.RangeFeatureIndices,
            };
        }

        private static RoadBuildJob CreateRoadJob(
            NativeMvtLayer layer, Func<NativeMvtFeature, float> widthSelector,
            TileCoord coord, GroundGrid grid, MercatorPivot localFrame, NativeArray<Color32> featureColors, GeometryBuffers buffers)
        {
            var widths = buffers.Own(new NativeArray<float>(math.max(1, layer.FeatureCount), Allocator.Persistent));
            for (int i = 0; i < layer.FeatureCount; i++) widths[i] = widthSelector(layer.Features[i]);

            return new RoadBuildJob
            {
                FeatureGeomTypes = layer.FeatureGeomTypesSlice,
                FeatureRingRange = layer.FeatureRingRangeSlice,
                Widths = widths,
                Rings = layer.Rings,
                RingPoints = layer.RingPoints,
                FeatureColors = JobInputs.PrepareFeatureColors(featureColors, buffers),
                Coord = coord,
                Extent = layer.Extent,
                Grid = grid,
                LocalFrame = localFrame,
                Positions = buffers.Positions,
                Normals = buffers.Normals,
                Uvs = buffers.Uvs,
                Colors = buffers.Colors,
                Triangles = buffers.Triangles,
                Ranges = buffers.Ranges,
                RangeFeatureIndices = buffers.RangeFeatureIndices,
            };
        }

        private static BuildingTerrainBuildJob CreateBuildingTerrainJob(
            NativeMvtLayer layer,
            NativeArray<bool> hidden, NativeArray<float> topMeters, NativeArray<float> bottomMeters,
            NativeArray<Color32> featureColors,
            GroundGrid grid, TileCoord coord, MercatorPivot localFrame, GeometryBuffers buffers)
        {
            return new BuildingTerrainBuildJob
            {
                FeatureGeomTypes = layer.FeatureGeomTypesSlice,
                FeatureRingRange = layer.FeatureRingRangeSlice,
                Hidden = hidden,
                TopMeters = topMeters,
                BottomMeters = bottomMeters,
                FeatureColors = JobInputs.PrepareFeatureColors(featureColors, buffers),
                Rings = layer.Rings,
                RingPoints = layer.RingPoints,
                Coord = coord,
                Extent = layer.Extent,
                UvScalePerMeter = 1f, // UVs in meters; texture density is set by the material's Tiling
                Grid = grid,
                LocalFrame = localFrame,
                Positions = buffers.Positions,
                Normals = buffers.Normals,
                Uvs = buffers.Uvs,
                Colors = buffers.Colors,
                Triangles = buffers.Triangles,
                Ranges = buffers.Ranges,
                RangeFeatureIndices = buffers.RangeFeatureIndices,
            };
        }

        // ---- Shared runners ----------------------------------------------------

        /// <summary>
        /// Waits for a geometry job without blocking the main thread, until the job has actually completed even if
        /// <paramref name="ct"/> is cancelled: the job reads the caller's borrowed inputs (see the class doc). On
        /// cancellation, disposes <paramref name="buffers"/> and returns <c>false</c>.
        /// </summary>
        private static async Awaitable<bool> CompleteOrDisposeAsync(JobHandle handle, GeometryBuffers buffers, CancellationToken ct)
        {
            if (await handle.WaitNonBlockingAsync(ct)) return true;
            buffers.Dispose();
            return false;
        }

        private static async Awaitable<GeometryResult?> RunPerFeatureAsync(JobHandle handle, GeometryBuffers buffers, CancellationToken ct)
        {
            if (!await CompleteOrDisposeAsync(handle, buffers, ct)) return null;

            try
            {
                var meshes = MeshAssembler.BuildMeshes(
                    buffers.Positions.AsArray(), buffers.Normals.AsArray(), buffers.Uvs.AsArray(),
                    buffers.Triangles.AsArray(), buffers.Ranges.AsArray(), buffers.Colors.AsArray());

                var featureIndices = new int[buffers.RangeFeatureIndices.Length];
                buffers.RangeFeatureIndices.AsArray().CopyTo(featureIndices);

                return new GeometryResult(meshes, featureIndices);
            }
            finally
            {
                buffers.Dispose();
            }
        }

        private static async Awaitable<Mesh> RunSingleMeshAsync(JobHandle handle, GeometryBuffers buffers, string name, CancellationToken ct)
        {
            if (!await CompleteOrDisposeAsync(handle, buffers, ct)) return null;

            try
            {
                return await MeshAssembler.BuildSingleMeshAsync(
                    buffers.Positions, buffers.Normals, buffers.Uvs, buffers.Colors, buffers.Triangles, name, ct);
            }
            finally
            {
                buffers.Dispose();
            }
        }
    }
}
