using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Scale conversions shared by the OpenMapTiles handlers. Scene units are Web Mercator meters (see
    /// <see cref="MercatorPivot"/>), and style sizes are pixels of the MapLibre convention, where the world at zoom
    /// <c>z</c> is <c>512 · 2^z</c> pixels wide.
    /// </summary>
    public static class OmtZoom
    {
        /// <summary>Length of the equator in Web Mercator meters: the width of the world in scene units.</summary>
        public const double WorldSizeMeters = 40075016.685578488;

        /// <summary>Pixel size of a tile in the MapLibre convention used by the OpenMapTiles style.</summary>
        public const double TilePixels = 512.0;

        /// <summary>Returns the size of one style pixel in Mercator meters at zoom <paramref name="zoom"/>.</summary>
        public static float MetersPerPixel(double zoom) => (float)(WorldSizeMeters / (TilePixels * math.pow(2.0, zoom)));

        /// <summary>
        /// Returns the factor converting real (ground) meters to Mercator meters at the tile's latitude:
        /// <c>1 / cos(latitude)</c>. Real-world sizes (road widths) are multiplied by it so that they match the
        /// Mercator-scaled footprints around them.
        /// </summary>
        public static float MercatorScale(TileCoord coord)
        {
            double2 center = coord.CenterMeters();
            double lat = 2.0 * math.atan(math.exp(center.y / 6378137.0)) - math.PI_DBL * 0.5;
            return (float)(1.0 / math.max(0.01, math.cos(lat)));
        }

        /// <summary>
        /// Returns a line width in Mercator meters: the real width (converted with <paramref name="mercatorScale"/>),
        /// but at least <paramref name="minPixels"/> style pixels at the tile's zoom, so that lines stay visible on
        /// low-zoom tiles and keep their physical size up close.
        /// </summary>
        public static float LineWidth(float realMeters, float minPixels, float mercatorScale, float metersPerPixel) =>
            math.max(realMeters * mercatorScale, minPixels * metersPerPixel);
    }
}
