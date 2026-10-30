// GeoFoundry — building zoom fade (GeoFoundry/Building Lit)
// ============================================================================
// Building meshes built with a GeoFoundry.Geometry.BuildingFade carry the look of the lower-zoom tiles they replace
// (AsyncGeometryWorker.BuildBuildingTerrainMeshAsync): this fades their heights and colors in from that look as the
// camera approaches, so a tile does not change its look at once when it is replaced (OpenMapTiles sends merged blocks
// without heights or colors at zoom 13 and individual buildings with them from zoom 14).
//
// The zoom is evaluated per vertex from its distance to the camera, with the pixel size the tile selection uses for a
// tile's screen size: a tile appears when its closest point reaches the start zoom of the fade, so every vertex of it
// is still at the start. The globals come from GeoFoundry.Render.MapViewShaderGlobals; without them (0) buildings
// render at their own heights and colors.
//
// Vertex attributes (GeoFoundry.Geometry.BuildingFadeVertex):
//   COLOR     = the feature color
//   TEXCOORD3 = the color at the start of the fade
//   TEXCOORD4 = (height above the height at the start of the fade in meters, start zoom, length in zooms, 0)
// Meshes without TEXCOORD4 (length 0) render unchanged.
// ============================================================================
#ifndef GEOFOUNDRY_BUILDING_FADE_INCLUDED
#define GEOFOUNDRY_BUILDING_FADE_INCLUDED

// xyz: camera position (world space); w: meters per pixel per meter of distance (perspective), or minus the constant
// meters per pixel (orthographic); 0 when not set. Set per camera by GeoFoundry.Render.MapViewShaderGlobals.
float4 _GeoFoundryCameraPixelSize;

#ifndef GEOFOUNDRY_WORLD_SIZE_METERS
#define GEOFOUNDRY_WORLD_SIZE_METERS 40075016.686
#endif

// Returns the fade factor of a vertex at positionWS: 0 at the start of the fade, 1 at its end (and without fade data).
// Mirrored in GeoFoundry.Geometry.BuildingFade.FactorAt for tests: keep both in sync.
float GeoFoundryBuildingFadeFactor(float3 positionWS, float4 fade)
{
    float k = _GeoFoundryCameraPixelSize.w;
    if (fade.z <= 0.0 || k == 0.0)
        return 1.0;
    float mpp = k > 0.0 ? k * distance(positionWS, _GeoFoundryCameraPixelSize.xyz) : -k;
    float zoom = log2(GEOFOUNDRY_WORLD_SIZE_METERS / (512.0 * max(mpp, 1e-6)));
    return saturate((zoom - fade.y) / fade.z);
}

// Moves positionOS to its height at the camera's zoom and returns the fade factor. The zoom is taken at the vertex's
// position at the start of the fade (near the ground, where the tile selection measures the distance), so a tall
// roof closer to the camera does not start ahead of its tile, and all vertices of a building fade alike.
float GeoFoundryApplyBuildingFade(inout float3 positionOS, float4 fade)
{
    float3 startOS = float3(positionOS.x, positionOS.y - fade.x, positionOS.z);
    float t = GeoFoundryBuildingFadeFactor(TransformObjectToWorld(startOS), fade);
    positionOS.y -= (1.0 - t) * fade.x;
    return t;
}

#endif
