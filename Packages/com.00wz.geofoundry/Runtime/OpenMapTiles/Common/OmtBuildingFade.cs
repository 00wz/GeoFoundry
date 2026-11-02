using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// How a building mesh looked on the tiles of lower zoom, from which the <c>GeoFoundry/OMT Building Lit</c> shader
    /// fades its heights and colors in as the camera approaches. The schema sends merged blocks without heights or colors
    /// below zoom 14 and individual buildings with them from zoom 14: without the fade, tiles change their look at once
    /// when they are replaced. With it, a building starts at the look of the blocks when its tile appears (the roof at
    /// <see cref="TopMeters"/>, the base at <see cref="BottomMeters"/>, the color <see cref="Color"/>) and reaches its own
    /// heights and color <see cref="Zooms"/> zooms later.
    /// </summary>
    /// <remarks>
    /// The shader evaluates the zoom per vertex from its distance to the camera, with the pixel size the tile selection
    /// uses for a tile's screen size: a tile appears when its closest point reaches <see cref="StartZoom"/>, so every
    /// vertex of it is still at the start of the fade.
    /// </remarks>
    public struct OmtBuildingFade
    {
        /// <summary>
        /// Display zoom (the world is <c>512 · 2^zoom</c> pixels wide) at which the fade starts: the zoom from which the
        /// tiles carrying the detailed data are shown (see <see cref="OmtZoom.TileShownFromZoom"/>).
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
        /// of the fade, 1 at its end and when the fade is disabled. Mirrors <c>OmtBuildingFadeFactor</c>
        /// (GeoFoundryOmtBuildingFade.hlsl) for tests: keep both in sync.
        /// </summary>
        public float FactorAt(float metersPerPixel)
        {
            if (!Enabled || metersPerPixel <= 0f) return 1f;
            float zoom = math.log2((float)(OmtZoom.WorldSizeMeters / (OmtZoom.TilePixels * math.max(metersPerPixel, 1e-6f))));
            return math.saturate((zoom - StartZoom) / Zooms);
        }
    }

    /// <summary>
    /// Vertex stream 3 of OpenMapTiles building meshes (see <see cref="OmtBuildingMesh"/>): <c>COLOR</c> = the feature
    /// color, <c>TEXCOORD3</c> = the color at the start of the fade, <c>TEXCOORD4</c> = (height of the vertex above its
    /// height at the start of the fade in meters, <see cref="OmtBuildingFade.StartZoom"/>,
    /// <see cref="OmtBuildingFade.Zooms"/>, 0). Read by GeoFoundryOmtBuildingFade.hlsl.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OmtBuildingVertex
    {
        public Color32 Color;
        public Color32 FadeColor;
        public half4 Fade;
    }
}
