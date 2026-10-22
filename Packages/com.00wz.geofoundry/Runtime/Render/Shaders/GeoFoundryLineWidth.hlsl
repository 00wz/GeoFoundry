// GeoFoundry — line width at the camera's pixel size (GeoFoundry/Line Lit)
// ============================================================================
// Line meshes (AsyncGeometryWorker.BuildLineMeshAsync) are built and draped onto the ground at their width at the
// tile's pixel size. This vertex function resizes each ribbon at the camera's pixel size, one value for the whole
// scene as in MapLibre/Mapbox (u_ratio from transform.zoom): the world width of a line is the same on every tile,
// whatever its zoom, and perspective thins distant lines. The vertex moves along its extrusion (its offset from the
// line axis, interpolated with the grid-clipped sub-vertices, so the axis is preserved) and its height follows the
// ground's tangent plane (the vertex normal is the ground normal).
//
// Vertex attributes (GeoFoundry.Geometry.LineVertex):
//   TEXCOORD0 = (across [0, 1], distance along the line in meters, extrusion.xy)
//   TEXCOORD1 = LineWidth (meters, pixels, extra meters, extra pixels)
//   TEXCOORD2 = (dash, gap in core widths, built half width, min zoom)
//
// Mirrored line by line in GeoFoundry.Geometry.LineShaderMath (LineWidth.cs) for tests: keep both in sync.
// ============================================================================
#ifndef GEOFOUNDRY_LINE_WIDTH_INCLUDED
#define GEOFOUNDRY_LINE_WIDTH_INCLUDED

// Mercator meters per screen pixel at the center of the view of the rendering camera, set per camera by
// GeoFoundry.Render.MapViewShaderGlobals; 0 when not set (the built width is rendered).
float _GeoFoundryMetersPerPixel;

#define GEOFOUNDRY_WORLD_SIZE_METERS 40075016.686
#define GEOFOUNDRY_LINE_FADE_ZOOMS 0.5

// Moves positionOS to the line width at the camera's pixel size and returns the dash pattern (dash, gap) in meters.
float2 GeoFoundryWidenLine(inout float3 positionOS, float3 normalOS, float4 uv0, float4 uv1, float4 uv2)
{
    float mpp = _GeoFoundryMetersPerPixel;
    if (mpp <= 0.0)
        return uv2.xy * max(2.0 * (uv2.z - uv1.z), 0.0);  // built width; the border is taken at its meter value

    float core = max(uv1.x, uv1.y * mpp);
    float halfWidth = 0.5 * core + max(uv1.z, uv1.w * mpp);
    float zoom = log2(GEOFOUNDRY_WORLD_SIZE_METERS / (512.0 * mpp));
    halfWidth *= saturate((zoom - uv2.w) / GEOFOUNDRY_LINE_FADE_ZOOMS + 1.0);

    float2 d = uv0.zw * (halfWidth - uv2.z);
    positionOS += float3(d.x, -(normalOS.x * d.x + normalOS.z * d.y) / max(normalOS.y, 0.2), d.y);
    return uv2.xy * core;
}

// Discards the fragments in the gaps: dash = (distance along the line, dash, gap) in meters; (0, 0) is solid.
void GeoFoundryClipDash(float3 dash)
{
    float period = dash.y + dash.z;
    if (dash.y > 0.0 && dash.z > 0.0 && fmod(max(dash.x, 0.0), period) > dash.y)
        discard;
}

#endif
