using System;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// One entry of an ordered draw list for <see cref="AsyncGeometryWorker.BuildSurfaceMeshAsync(GeoFoundry.Core.Mvt.NativeMvtLayer, Unity.Collections.NativeArray{SurfaceDrawItem}, GeoFoundry.Core.Coordinates.TileCoord, GeoFoundry.Core.Ground.GroundGrid, GeoFoundry.Core.Coordinates.MercatorPivot, float, System.Threading.CancellationToken)"/>:
    /// which Polygon feature to emit and with which vertex color. Items are emitted in array order, so with the
    /// ZTest Always surface shaders the array order is the draw order inside the mesh. Features not listed are
    /// skipped; a feature may be listed more than once.
    /// </summary>
    public struct SurfaceDrawItem
    {
        /// <summary>Index of the feature within the layer.</summary>
        public int FeatureIndex;

        /// <summary>Vertex color of the feature (<c>albedo *= vertexColor</c> in the shader).</summary>
        public Color32 Color;

        public SurfaceDrawItem(int featureIndex, Color32 color)
        {
            FeatureIndex = featureIndex;
            Color = color;
        }
    }

    /// <summary>Options of a <see cref="LineDrawItem"/>.</summary>
    [Flags]
    public enum LineDrawFlags : byte
    {
        None = 0,

        /// <summary>
        /// Draws the rings of a Polygon feature as closed lines (an outline) instead of skipping it. Without the
        /// flag only LineString features are drawn.
        /// </summary>
        PolygonOutline = 1,
    }

    /// <summary>
    /// One entry of an ordered draw list for
    /// <see cref="AsyncGeometryWorker.BuildLineMeshAsync"/>: which feature to emit as a ribbon, its width, vertex
    /// color, dash pattern and minimum zoom. Items are emitted in array order (the draw order inside the mesh), so a
    /// casing pass followed by a fill pass is simply the same features listed twice with different widths and colors.
    /// </summary>
    /// <remarks>
    /// Width, dashes and the minimum zoom are evaluated by the <c>GeoFoundry/Line Lit</c> shader at the camera's pixel
    /// size (see <see cref="LineWidth"/>); the item's values are written to TexCoord0..2 of every vertex.
    /// </remarks>
    public struct LineDrawItem
    {
        /// <summary>Index of the feature within the layer.</summary>
        public int FeatureIndex;

        /// <summary>Ribbon width; an item whose width is ≤ 0 at the build pixel size is skipped.</summary>
        public LineWidth Width;

        /// <summary>Vertex color of the ribbon.</summary>
        public Color32 Color;

        /// <summary>
        /// Dash pattern in multiples of the core width (<see cref="LineWidth.Core"/>): <c>x</c> = dash length,
        /// <c>y</c> = gap length; <c>(0, 0)</c> is a solid line. The shader cuts dashes using the distance along the
        /// line stored in TexCoord0.y.
        /// </summary>
        public float2 Dash;

        /// <summary>Drawing options.</summary>
        public LineDrawFlags Flags;

        /// <summary>
        /// Camera zoom below which the line is hidden; it fades in (grows from zero width) over the half zoom below.
        /// 0 shows it at every zoom.
        /// </summary>
        public float MinZoom;

        public LineDrawItem(int featureIndex, LineWidth width, Color32 color, float2 dash = default,
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
}
