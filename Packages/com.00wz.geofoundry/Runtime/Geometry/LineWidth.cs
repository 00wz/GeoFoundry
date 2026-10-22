using System;
using Unity.Mathematics;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Width of a line ribbon as a function of the screen pixel size: a core of <see cref="Meters"/> ground meters
    /// but at least <see cref="Pixels"/> screen pixels, plus an extra border on each side (casings) of
    /// <see cref="ExtraMeters"/> but at least <see cref="ExtraPixels"/>:
    /// <c>W(mpp) = max(Meters, Pixels · mpp) + 2 · max(ExtraMeters, ExtraPixels · mpp)</c>.
    /// </summary>
    /// <remarks>
    /// The <c>GeoFoundry/Line Lit</c> shader evaluates it per frame at the camera's pixel size
    /// (<c>_GeoFoundryMetersPerPixel</c>, see <c>MapViewShaderGlobals</c>), one value for the whole scene as in
    /// MapLibre: the world width of a line is the same on every tile, and perspective thins distant lines. The
    /// geometry is built (and draped onto the ground) at the tile's pixel size and only resized by the shader.
    /// All lengths are Web Mercator meters (scene units); real ground widths must already be scaled by
    /// <c>1 / cos(latitude)</c>.
    /// </remarks>
    [Serializable]
    public struct LineWidth : IEquatable<LineWidth>
    {
        /// <summary>Core width in meters.</summary>
        public float Meters;

        /// <summary>Minimum core width in screen pixels.</summary>
        public float Pixels;

        /// <summary>Border added on each side, in meters.</summary>
        public float ExtraMeters;

        /// <summary>Minimum border added on each side, in screen pixels.</summary>
        public float ExtraPixels;

        public LineWidth(float meters, float pixels = 0f, float extraMeters = 0f, float extraPixels = 0f)
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

        /// <summary>Returns this width with the core scaled by <paramref name="factor"/> (the border is kept).</summary>
        public LineWidth ScaleCore(float factor) =>
            new LineWidth(Meters * factor, Pixels * factor, ExtraMeters, ExtraPixels);

        /// <summary>Returns the core of this width with a border added on each side.</summary>
        public LineWidth WithBorder(float extraMeters, float extraPixels) =>
            new LineWidth(Meters, Pixels, extraMeters, extraPixels);

        public bool Equals(LineWidth other) =>
            Meters == other.Meters && Pixels == other.Pixels && ExtraMeters == other.ExtraMeters &&
            ExtraPixels == other.ExtraPixels;

        public override bool Equals(object obj) => obj is LineWidth other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Meters, Pixels, ExtraMeters, ExtraPixels);

        public override string ToString() => $"max({Meters} m, {Pixels} px) + 2·max({ExtraMeters} m, {ExtraPixels} px)";
    }

    /// <summary>
    /// The per-vertex line attributes of a draw-list line mesh (vertex stream 2, interleaved Float32):
    /// TexCoord0 = (across [0, 1], distance along the line in meters, extrusion.xy), TexCoord1 = the item's
    /// <see cref="LineWidth"/> (Meters, Pixels, ExtraMeters, ExtraPixels), TexCoord2 = (dash.xy in core widths,
    /// built half width, <see cref="LineDrawItem.MinZoom"/>). The extrusion is the vertex's offset from the line axis
    /// (Mercator x/y = scene X/Z) divided by the built half width.
    /// </summary>
    internal struct LineVertex
    {
        public float4 Uv0;
        public float4 Uv1;
        public float4 Uv2;
    }

    /// <summary>
    /// The per-vertex math of the <c>GeoFoundry/Line Lit</c> shader (<c>GeoFoundryLineWidth.hlsl</c>), mirrored line
    /// by line for tests: keep both in sync.
    /// </summary>
    internal static class LineShaderMath
    {
        /// <summary>Width of the world in Web Mercator meters.</summary>
        public const float WorldSizeMeters = 40075016.686f;

        /// <summary>Zoom range below <see cref="LineDrawItem.MinZoom"/> over which a line fades in (shrinks to 0).</summary>
        public const float FadeZooms = 0.5f;

        /// <summary>Display zoom of a pixel size (MapLibre convention: the world is <c>512 · 2^zoom</c> pixels wide).</summary>
        public static float Zoom(float metersPerPixel) => math.log2(WorldSizeMeters / (512f * metersPerPixel));

        /// <summary>Half width of a line at the camera's pixel size, faded in below <paramref name="minZoom"/>.</summary>
        public static float HalfWidth(LineWidth width, float minZoom, float metersPerPixel)
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
