using System.Collections.Generic;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Tiles.TileSelection;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace GeoFoundry.Bootstrap
{
    /// <summary>
    /// Map description: tile sources, layer and terrain handlers, LOD, loading and memory limits, initial position.
    /// Scene-independent: one config can be reused across scenes, and different platforms can use different configs
    /// (for example, smaller budgets for WebGL). Created via <c>Create → GeoFoundry → Config</c> and assigned to
    /// <c>GeoFoundryBootstrapper</c>; scene objects (the camera) are set on the bootstrapper. Materials are set in
    /// the handler ScriptableObjects.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Config", fileName = "GeoFoundryConfig", order = 0)]
    public sealed class GeoFoundryConfig : ScriptableObject
    {
        [Header("Tile Sources")]
        [Tooltip("Required. URL template for vector MVT tiles with {z} {x} {y} placeholders, " +
                 "e.g. http://host:3000/moscow/{z}/{x}/{y} (martin).")]
        public string VectorTilesEndpointTemplate = "";

        [Tooltip("URL template for Terrarium PNG DEM tiles with {z} {x} {y} placeholders, e.g. " +
                 "http://host:8000/cog/tiles/WebMercatorQuad/{z}/{x}/{y}.png?url=/dem/dem.tif&algorithm=terrarium " +
                 "(titiler). Leave empty for flat terrain without relief.")]
        public string DemTilesEndpointTemplate = "";

        [Header("LOD (adaptive quadtree)")]
        [Tooltip("Quadtree LOD traversal settings. The traversal references this instance, so changes apply live.")]
        public TileQuadtreeConfig Lod = new();

        [Header("Handlers")]
        [Tooltip("Terrain handler that builds the tile terrain (with relief or flat). If not set, no terrain is built.")]
        public TerrainHandlerSO TerrainHandler;

        [Tooltip("Layer handlers, registered in list order.")]
        public List<LayerHandlerSO> LayerHandlers = new();

        [Tooltip("Also provide the grids of the 8 neighbors for each tile (LayerLoadContext.Grids). Built-in handlers " +
                 "do not need them; they cost 8 extra grids and a 5×5 DEM neighborhood instead of 3×3 per tile. " +
                 "Enable only if a custom handler samples neighbor heights. When off, neighbor slots are empty.")]
        public bool LoadNeighborGrids = false;

        [Header("Tile Loading and Budget")]
        [Tooltip("Shared limit of concurrent HTTP requests across all sources (vector and DEM tiles).")]
        [Min(1)] public int MaxConcurrentHttpRequests = 6;

        [Tooltip("Maximum number of tiles built at the same time, including cancelling builds.")]
        [Range(1, 16)] public int MaxConcurrentLoads = 4;

        [Tooltip("GPU memory budget for resident tiles (loaded and loading), MB: mesh vertex and index buffers; " +
                 "on WebGL this lives in the browser's WebGL context, outside the WASM heap. The most important " +
                 "selected tiles are admitted while they fit both budgets (the rest are denied and left empty); " +
                 "excess tiles are evicted. The budget can be exceeded by one boundary tile and by tiles in flight, " +
                 "so leave headroom.")]
        [Range(64, 4096)] public int GpuBudgetMB = 512;

        [Tooltip("Application heap budget (WASM heap on WebGL) held by resident tiles, MB: CPU copies of readable " +
                 "meshes, etc. Shared ground grids and temporary build buffers are not included; leave headroom " +
                 "for them in the overall heap limit. Can be exceeded the same way as the GPU budget.")]
        [Range(16, 2048)] public int HeapBudgetMB = 256;

        [Header("Initial Camera Position (Web Mercator origin)")]
        [Tooltip("Longitude, decimal degrees. Defaults to central Moscow.")]
        public double OriginLongitude = 37.6175;

        [Tooltip("Latitude, decimal degrees.")]
        public double OriginLatitude = 55.7520;

        [Tooltip("Initial distance from the ground target to the camera, meters.")]
        public float OriginCameraDistance = 1500f;

        [Header("Terrain")]
        [Tooltip("Relief grid resolution per tile (N×N cells). 32 gives about 76 meters per cell at zoom 14. " +
                 "Not used without relief.")]
        [Range(4, 128)] public int TerrainGridResolution = 32;

        [Tooltip("Load DEM tiles and build relief. When off, the terrain is flat and no DEM requests are made; " +
                 "layer geometry is clipped to tile bounds the same way as with relief.")]
        [FormerlySerializedAs("EnableTerrain")]
        public bool EnableRelief = true;

        [Header("Floating origin")]
        [Tooltip("When the camera moves horizontally farther than this distance (meters) from the current pivot, " +
                 "the pivot is moved under the camera.")]
        [Min(1f)] public float FloatingOriginThresholdMeters = 1000f;

        /// <summary>Returns the initial position as (longitude, latitude) in decimal degrees.</summary>
        public double2 GetOriginLonLat() => new double2(OriginLongitude, OriginLatitude);
    }
}
