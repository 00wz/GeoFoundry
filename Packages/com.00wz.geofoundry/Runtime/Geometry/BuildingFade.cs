using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// How a building mesh looked on the tiles of lower zoom, from which the <c>GeoFoundry/Building Lit</c> shader fades
    /// its heights and colors in as the camera approaches (see <see cref="AsyncGeometryWorker.BuildBuildingTerrainMeshAsync"/>).
    /// Sources such as OpenMapTiles send merged building blocks without heights or colors below some zoom and
    /// individual buildings with them from that zoom: without the fade, tiles change their look at once when they are
    /// replaced. With it, a building starts at the look of the lower zoom when its tile appears (the roof at
    /// <see cref="TopMeters"/>, the base at <see cref="BottomMeters"/>, the color <see cref="Color"/>) and reaches its
    /// own heights and color <see cref="Zooms"/> zooms later.
    /// </summary>
    /// <remarks>
    /// The shader evaluates the zoom per vertex from its distance to the camera (the pixel size the tile selection
    /// uses for the tile's screen size, published by <c>GeoFoundry.Render.MapViewShaderGlobals</c>), not one value for
    /// the whole scene: a tile appears when its closest point reaches <see cref="StartZoom"/>, so every vertex of it is
    /// still at the start of the fade. Without the globals (no map view) buildings render at their own heights.
    /// </remarks>
    public struct BuildingFade
    {
        /// <summary>
        /// Display zoom (the world is <c>512 · 2^zoom</c> pixels wide) at which the fade starts: the zoom from which
        /// the tiles carrying the detailed data are shown.
        /// </summary>
        public float StartZoom;

        /// <summary>Length of the fade in zooms; 0 disables the fade (the default).</summary>
        public float Zooms;

        /// <summary>Roof height above the terrain at the start of the fade, in meters.</summary>
        public float TopMeters;

        /// <summary>Height of the building bottom above the terrain at the start of the fade, in meters.</summary>
        public float BottomMeters;

        /// <summary>Vertex color at the start of the fade (in the same color space as the feature colors).</summary>
        public Color32 Color;

        /// <summary>Gets whether the fade is enabled.</summary>
        public bool Enabled => Zooms > 0f;

        /// <summary>
        /// Returns the fade factor at a pixel size of <paramref name="metersPerPixel"/> Mercator meters: 0 at the start
        /// of the fade, 1 at its end and when the fade is disabled. Mirrors <c>GeoFoundryBuildingFadeFactor</c>
        /// (GeoFoundryBuildingFade.hlsl) for tests: keep both in sync.
        /// </summary>
        public float FactorAt(float metersPerPixel)
        {
            if (!Enabled || metersPerPixel <= 0f) return 1f;
            float zoom = math.log2((float)(40075016.686 / (512.0 * math.max(metersPerPixel, 1e-6f))));
            return math.saturate((zoom - StartZoom) / Zooms);
        }
    }

    /// <summary>
    /// Vertex stream 3 of building meshes with a <see cref="BuildingFade"/> (see <see cref="MeshAssembler"/>):
    /// <c>COLOR</c> = the feature color, <c>TEXCOORD3</c> = the color at the start of the fade, <c>TEXCOORD4</c> =
    /// (height of the vertex above its height at the start of the fade in meters, <see cref="BuildingFade.StartZoom"/>,
    /// <see cref="BuildingFade.Zooms"/>, 0). Read by <c>GeoFoundryBuildingFade.hlsl</c>; meshes without these channels
    /// render unchanged.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BuildingFadeVertex
    {
        public Color32 Color;
        public Color32 FadeColor;
        public half4 Fade;
    }
}
