// GeoFoundry — OpenMapTiles building zoom fade (GeoFoundry/OMT Building Lit)
// ============================================================================
// OpenMapTiles building meshes carry the look of the lower-zoom tiles they replace (OmtBuildingVertex): this fades
// their heights and colors in from that look as the camera approaches, so a tile does not change its look at once when
// it is replaced (the schema sends merged blocks without heights or colors at zoom 13 and individual buildings with them
// from zoom 14).
//
// The zoom is evaluated per vertex from its distance to the camera, with the pixel size the tile selection uses for a
// tile's screen size: a tile appears when its closest point reaches the start zoom of the fade, so every vertex of it
// is still at the start. The camera values are read from URP's per-camera constants, which stay those of the rendering
// camera in the shadow passes too (unity_CameraProjection, unlike UNITY_MATRIX_P), so no script has to publish them.
//
// Vertex attributes (GeoFoundry.OpenMapTiles.OmtBuildingVertex):
//   COLOR     = the feature color
//   TEXCOORD3 = the color at the start of the fade
//   TEXCOORD4 = (height above the height at the start of the fade in meters, start zoom, length in zooms, 0)
// ============================================================================
#ifndef GEOFOUNDRY_OMT_BUILDING_FADE_INCLUDED
#define GEOFOUNDRY_OMT_BUILDING_FADE_INCLUDED

#define GEOFOUNDRY_OMT_WORLD_SIZE_METERS 40075016.686

// Returns the fade factor of a vertex at positionWS: 0 at the start of the fade, 1 at its end (and without fade data).
// Mirrored in GeoFoundry.OpenMapTiles.OmtBuildingFade.FactorAt for tests: keep both in sync.
float OmtBuildingFadeFactor(float3 positionWS, float4 fade)
{
    if (fade.z <= 0.0)
        return 1.0;
    float pixels = max(_ScaledScreenParams.y, 1.0);
    float mpp = unity_OrthoParams.w > 0.5
        ? 2.0 * unity_OrthoParams.y / pixels
        // abs: the projection is flipped when rendering into a texture on some platforms.
        : 2.0 * distance(positionWS, _WorldSpaceCameraPos) / (max(abs(unity_CameraProjection._m11), 1e-6) * pixels);
    float zoom = log2(GEOFOUNDRY_OMT_WORLD_SIZE_METERS / (512.0 * max(mpp, 1e-6)));
    return saturate((zoom - fade.y) / fade.z);
}

// Moves positionOS to its height at the camera's zoom and returns the fade factor. The zoom is taken at the vertex's
// position at the start of the fade (near the ground, where the tile selection measures the distance), so a tall
// roof closer to the camera does not start ahead of its tile, and all vertices of a building fade alike.
float OmtApplyBuildingFade(inout float3 positionOS, float4 fade)
{
    float3 startOS = float3(positionOS.x, positionOS.y - fade.x, positionOS.z);
    float t = OmtBuildingFadeFactor(TransformObjectToWorld(startOS), fade);
    positionOS.y -= (1.0 - t) * fade.x;
    return t;
}

#endif
