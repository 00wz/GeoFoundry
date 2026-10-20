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
    /// color and dash pattern. Items are emitted in array order (the draw order inside the mesh), so a casing pass
    /// followed by a fill pass is simply the same features listed twice with different widths and colors.
    /// </summary>
    public struct LineDrawItem
    {
        /// <summary>Index of the feature within the layer.</summary>
        public int FeatureIndex;

        /// <summary>Ribbon width in meters; ≤ 0 skips the item.</summary>
        public float WidthMeters;

        /// <summary>Vertex color of the ribbon.</summary>
        public Color32 Color;

        /// <summary>
        /// Dash pattern in meters along the line: <c>x</c> = dash length, <c>y</c> = gap length; <c>(0, 0)</c> is a
        /// solid line. Written to TexCoord1 of every vertex; the line shader cuts dashes using the distance along the
        /// line stored in TexCoord0.y.
        /// </summary>
        public float2 Dash;

        /// <summary>Drawing options.</summary>
        public LineDrawFlags Flags;

        public LineDrawItem(int featureIndex, float widthMeters, Color32 color, float2 dash = default, LineDrawFlags flags = LineDrawFlags.None)
        {
            FeatureIndex = featureIndex;
            WidthMeters = widthMeters;
            Color = color;
            Dash = dash;
            Flags = flags;
        }
    }
}
