// GeoFoundry — OpenMapTiles line width at the camera's pixel size (GeoFoundry/OMT Line Lit)
// ============================================================================
// Line meshes of the OpenMapTiles handlers (OmtLineMesh) are built and draped onto the ground at their width at the
// tile's pixel size. This vertex function resizes each ribbon at the camera's pixel size, as in MapLibre/Mapbox: the
// world width of a line is the same on every tile, whatever its zoom, and perspective thins distant lines. The vertex
// moves along its extrusion (its offset from the line axis, interpolated with the grid-clipped sub-vertices, so the
// axis is preserved) and its height follows the ground's tangent plane (the vertex normal is the ground normal).
//
// The pixel size is that of the center of the view, measured to the horizontal plane through the vertex: on flat
// ground every vertex gets the same value; on terrain it follows the height of the vertex. It is computed from the
// camera matrices, so no script publishes it.
//
// Vertex attributes (GeoFoundry.OpenMapTiles.OmtLineVertex):
//   TEXCOORD0 = (across [0, 1], distance along the line in meters)
//   TEXCOORD3 = (extrusion.xy, dash, gap in core widths)
//   TEXCOORD4 = OmtLineWidth (meters, pixels, extra meters, extra pixels)
//   TEXCOORD5 = (built half width, min zoom)
//
// Mirrored line by line in GeoFoundry.OpenMapTiles.OmtLineShaderMath (OmtLineWidth.cs) for tests: keep both in sync.
// ============================================================================
#ifndef GEOFOUNDRY_OMT_LINE_WIDTH_INCLUDED
#define GEOFOUNDRY_OMT_LINE_WIDTH_INCLUDED

#define OMT_WORLD_SIZE_METERS 40075016.686
#define OMT_LINE_FADE_ZOOMS 0.5
#define OMT_COS_MAX_PITCH 0.0871557 // cos(85°), the steepest view angle from the vertical, as in MapLibre

// Mercator meters per screen pixel at the center of the view, measured to the horizontal plane through positionWS.
float OmtMetersPerPixel(float3 positionWS)
{
    float pixels = max(_ScreenParams.y, 1.0);
    if (unity_OrthoParams.w > 0.5)
        return 2.0 * unity_OrthoParams.y / pixels;

    float3 forward = -UNITY_MATRIX_V[2].xyz;
    float down = max(-forward.y, OMT_COS_MAX_PITCH);
    float distanceToCenter = max(_WorldSpaceCameraPos.y - positionWS.y, 1.0) / down;
    // abs: the projection is flipped when rendering into a texture on some platforms.
    float tanHalfFov = 1.0 / abs(UNITY_MATRIX_P._m11);
    return 2.0 * distanceToCenter * tanHalfFov / pixels;
}

// Moves positionOS to the line width at the camera's pixel size and returns the dash pattern (dash, gap) in meters.
float2 OmtWidenLine(inout float3 positionOS, float3 normalOS, float4 uv3, float4 width, float2 uv5)
{
    float mpp = OmtMetersPerPixel(TransformObjectToWorld(positionOS));
    float core = max(width.x, width.y * mpp);
    float halfWidth = 0.5 * core + max(width.z, width.w * mpp);
    float zoom = log2(OMT_WORLD_SIZE_METERS / (512.0 * mpp));
    halfWidth *= saturate((zoom - uv5.y) / OMT_LINE_FADE_ZOOMS + 1.0);

    float2 d = uv3.xy * (halfWidth - uv5.x);
    positionOS += float3(d.x, -(normalOS.x * d.x + normalOS.z * d.y) / max(normalOS.y, 0.2), d.y);
    return uv3.zw * core;
}

// Discards the fragments in the gaps: dash = (distance along the line, dash, gap) in meters; (0, 0) is solid.
void OmtClipDash(float3 dash)
{
    float period = dash.y + dash.z;
    if (dash.y > 0.0 && dash.z > 0.0 && fmod(max(dash.x, 0.0), period) > dash.y)
        discard;
}

#endif
