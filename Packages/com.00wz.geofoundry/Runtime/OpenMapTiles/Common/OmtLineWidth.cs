using System;
using GeoFoundry.Geometry;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Width of a line ribbon as a function of the screen pixel size: a core of <see cref="Meters"/> ground meters
    /// but at least <see cref="Pixels"/> screen pixels, plus an extra border on each side (casings) of
    /// <see cref="ExtraMeters"/> but at least <see cref="ExtraPixels"/>:
    /// <c>W(mpp) = max(Meters, Pixels · mpp) + 2 · max(ExtraMeters, ExtraPixels · mpp)</c>.
    /// </summary>
    /// <remarks>
    /// The <c>GeoFoundry/OMT Line Lit</c> shader evaluates it per frame at the camera's pixel size (see
    /// <see cref="OmtLineShaderMath"/>), as in MapLibre: the world width of a line is the same on every tile, and
    /// perspective thins distant lines. The geometry is built (and draped onto the ground) at the tile's pixel size and
    /// only resized by the shader. All lengths are Web Mercator meters (scene units); real ground widths must already be
    /// scaled by <c>1 / cos(latitude)</c>.
    /// </remarks>
    [Serializable]
    public struct OmtLineWidth : IEquatable<OmtLineWidth>
    {
        /// <summary>Core width in meters.</summary>
        public float Meters;

        /// <summary>Minimum core width in screen pixels.</summary>
        public float Pixels;

        /// <summary>Border added on each side, in meters.</summary>
        public float ExtraMeters;

        /// <summary>Minimum border added on each side, in screen pixels.</summary>
        public float ExtraPixels;

        public OmtLineWidth(float meters, float pixels = 0f, float extraMeters = 0f, float extraPixels = 0f)
        {
            Meters = meters;
            Pixels = pixels;
            ExtraMeters = extraMeters;
            ExtraPixels = extraPixels;
        }

        /// <summary>Returns the core width at <paramref name="metersPerPixel"/> meters per screen pixel.</summary>
        public float Core(float metersPerPixel) => math.max(Meters, Pixels * metersPerPixel);

        /// <summary>Returns the full width at <paramref name="metersPerPixel"/> meters per screen pixel.</summary>
        public float Evaluate(float metersPerPixel) =>
            Core(metersPerPixel) + 2f * math.max(ExtraMeters, ExtraPixels * metersPerPixel);

        /// <summary>Returns the real (ground) width: the core and the borders in meters, without the pixel minimums.</summary>
        public float RealMeters => Meters + 2f * ExtraMeters;

        /// <summary>Returns this width with the core scaled by <paramref name="factor"/> (the border is kept).</summary>
        public OmtLineWidth ScaleCore(float factor) =>
            new OmtLineWidth(Meters * factor, Pixels * factor, ExtraMeters, ExtraPixels);

        /// <summary>Returns the core of this width with a border added on each side.</summary>
        public OmtLineWidth WithBorder(float extraMeters, float extraPixels) =>
            new OmtLineWidth(Meters, Pixels, extraMeters, extraPixels);

        public bool Equals(OmtLineWidth other) =>
            Meters == other.Meters && Pixels == other.Pixels && ExtraMeters == other.ExtraMeters &&
            ExtraPixels == other.ExtraPixels;

        public override bool Equals(object obj) => obj is OmtLineWidth other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Meters, Pixels, ExtraMeters, ExtraPixels);

        public override string ToString() => $"max({Meters} m, {Pixels} px) + 2·max({ExtraMeters} m, {ExtraPixels} px)";
    }

    /// <summary>How a handler sizes its lines.</summary>
    public enum OmtLineWidthMode
    {
        /// <summary>
        /// As in the style: the real width, but at least a minimum in screen pixels, evaluated per frame by the
        /// <c>GeoFoundry/OMT Line Lit</c> shader; lines fade in at their minimum zoom.
        /// </summary>
        Style,

        /// <summary>
        /// The real ground width only (<see cref="OmtLineWidth.RealMeters"/>), built by the package
        /// (<c>AsyncGeometryWorker.BuildLineMeshAsync</c>, <c>GeoFoundry/Line Lit</c>): no pixel minimums and no zoom
        /// fade, so thin lines become sub-pixel from afar. Lines the style sizes in pixels only use
        /// <see cref="OmtLineRule.RealWidthMeters"/>; rules without any width in meters are not drawn.
        /// </summary>
        RealWorld,
    }

    /// <summary>
    /// One entry of an ordered draw list for <see cref="OmtLineMesh.BuildAsync"/>: which feature to emit as a ribbon,
    /// with which width, vertex color, dash pattern and minimum zoom. Items are emitted in array order; the ribbons
    /// are coplanar with the ground and with each other, so the array order is the draw order inside the mesh.
    /// </summary>
    public struct OmtLineDrawItem
    {
        /// <summary>Index of the feature within the layer.</summary>
        public int FeatureIndex;

        /// <summary>Ribbon width; an item whose width is ≤ 0 at the build pixel size is skipped.</summary>
        public OmtLineWidth Width;

        /// <summary>Vertex color of the ribbon.</summary>
        public Color32 Color;

        /// <summary>
        /// Dash pattern in multiples of the core width (<see cref="OmtLineWidth.Core"/>): <c>x</c> = dash length,
        /// <c>y</c> = gap length; <c>(0, 0)</c> is a solid line.
        /// </summary>
        public float2 Dash;

        /// <summary>Drawing options.</summary>
        public LineDrawFlags Flags;

        /// <summary>
        /// Camera zoom below which the line is hidden; it fades in (grows from zero width) over the half zoom below.
        /// 0 shows it at every zoom.
        /// </summary>
        public float MinZoom;

        public OmtLineDrawItem(int featureIndex, OmtLineWidth width, Color32 color, float2 dash = default,
            LineDrawFlags flags = LineDrawFlags.None, float minZoom = 0f)
        {
            FeatureIndex = featureIndex;
            Width = width;
            Color = color;
            Dash = dash;
            Flags = flags;
            MinZoom = minZoom;
        }
    }

    /// <summary>
    /// The per-vertex attributes of an OpenMapTiles line mesh (vertex stream 2, interleaved Float32, see
    /// <see cref="OmtLineMesh"/>): TexCoord0 = (across [0, 1], distance along the line in meters), TexCoord3 =
    /// (extrusion.xy, dash.xy in core widths), TexCoord4 = the item's <see cref="OmtLineWidth"/> (Meters, Pixels,
    /// ExtraMeters, ExtraPixels), TexCoord5 = (built half width, <see cref="OmtLineDrawItem.MinZoom"/>). The extrusion is
    /// the vertex's offset from the line axis (Mercator x/y = scene X/Z) divided by the built half width.
    /// </summary>
    internal struct OmtLineVertex
    {
        public float2 Uv0;
        public float4 Uv3;
        public float4 Uv4;
        public float2 Uv5;
    }

    /// <summary>
    /// The per-vertex math of the <c>GeoFoundry/OMT Line Lit</c> shader (<c>GeoFoundryOmtLineWidth.hlsl</c>),
    /// mirrored line by line for tests: keep both in sync.
    /// </summary>
    internal static class OmtLineShaderMath
    {
        /// <summary>Width of the world in Web Mercator meters.</summary>
        public const float WorldSizeMeters = 40075016.686f;

        /// <summary>Zoom range below <see cref="OmtLineDrawItem.MinZoom"/> over which a line fades in (shrinks to 0).</summary>
        public const float FadeZooms = 0.5f;

        /// <summary>
        /// The steepest view angle from the vertical used for the distance to the ground, as in MapLibre (maximum
        /// pitch 85°): a camera looking at the horizon gets the pixel size at that angle.
        /// </summary>
        public const float MaxPitchDegrees = 85f;

        /// <summary>
        /// Returns the meters per screen pixel at the center of the view, as the shader evaluates it at a vertex. For a
        /// perspective camera, the distance to the center is measured to the horizontal plane through the vertex along
        /// the view direction, with the view angle limited to <see cref="MaxPitchDegrees"/> from the vertical and the
        /// altitude to at least 1 m: on flat ground every vertex gets the pixel size of the center of the view.
        /// </summary>
        /// <param name="altitude">Height of the camera above the vertex.</param>
        /// <param name="forward">View direction (unit).</param>
        /// <param name="orthographic">Whether the camera is orthographic.</param>
        /// <param name="tanHalfFov">Tangent of half the vertical field of view of a perspective camera.</param>
        /// <param name="orthographicSize">Half the view height of an orthographic camera.</param>
        /// <param name="pixelHeight">Height of the view in pixels.</param>
        public static float MetersPerPixel(float altitude, float3 forward, bool orthographic, float tanHalfFov,
            float orthographicSize, float pixelHeight)
        {
            float pixels = math.max(1f, pixelHeight);
            if (orthographic) return 2f * orthographicSize / pixels;

            float down = math.max(-forward.y, math.cos(math.radians(MaxPitchDegrees)));
            float distance = math.max(altitude, 1f) / down;
            return 2f * distance * tanHalfFov / pixels;
        }

        /// <summary>Display zoom of a pixel size (MapLibre convention: the world is <c>512 · 2^zoom</c> pixels wide).</summary>
        public static float Zoom(float metersPerPixel) => math.log2(WorldSizeMeters / (512f * metersPerPixel));

        /// <summary>Half width of a line at the camera's pixel size, faded in below <paramref name="minZoom"/>.</summary>
        public static float HalfWidth(OmtLineWidth width, float minZoom, float metersPerPixel)
        {
            float half = 0.5f * width.Evaluate(metersPerPixel);
            return half * math.saturate((Zoom(metersPerPixel) - minZoom) / FadeZooms + 1f);
        }

        /// <summary>
        /// Object-space displacement of a vertex: along its extrusion (<paramref name="extrude"/>, the offset from the
        /// line axis divided by <paramref name="baseHalf"/>) from the built half width to <paramref name="half"/>,
        /// with the height following the ground's tangent plane (<paramref name="normal"/>).
        /// </summary>
        public static float3 Offset(float2 extrude, float baseHalf, float half, float3 normal)
        {
            float2 d = extrude * (half - baseHalf);
            return new float3(d.x, -(normal.x * d.x + normal.z * d.y) / math.max(normal.y, 0.2f), d.y);
        }
    }
}
