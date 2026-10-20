using Unity.Burst;
using Unity.Mathematics;

namespace GeoFoundry.Core.Coordinates
{
    /// <summary>
    /// Web Mercator (EPSG:3857) conversions between longitude/latitude, meters and XYZ slippy-map tiles.
    /// The foundation of the package's coordinate system.
    /// </summary>
    /// <remarks>
    /// All methods are pure, allocation-free and independent of Unity objects, so they are Burst-compatible and
    /// can be called both from the main thread and from <c>[BurstCompile]</c> jobs.
    /// </remarks>
    [BurstCompile]
    public static class MercatorProjection
    {
        /// <summary>Radius of the Web Mercator sphere (WGS84 semi-major axis), in meters.</summary>
        public const double R = 6378137.0;

        /// <summary>Equatorial circumference of the Earth, 2πR ≈ 40075016.685578 meters.</summary>
        public const double EarthCircumference = 2.0 * math.PI_DBL * R;

        /// <summary>Half the circumference (πR): the distance from the Mercator world center to its edge.</summary>
        public const double HalfCircumference = math.PI_DBL * R;

        /// <summary>
        /// Standard Web Mercator latitude limit, in degrees: beyond it Y tends to ±∞ (tan(π/4 + lat/2) → ∞ at the
        /// poles). Equals atan(sinh(π)) converted to degrees.
        /// </summary>
        public const double MaxMercatorLatitude = 85.05112877980659;

        /// <summary>
        /// Converts longitude/latitude (decimal degrees, WGS84) to Mercator meters. Latitude is clamped to
        /// ±<see cref="MaxMercatorLatitude"/>.
        /// </summary>
        public static double2 LonLatToMeters(double lonDeg, double latDeg)
        {
            latDeg = math.clamp(latDeg, -MaxMercatorLatitude, MaxMercatorLatitude);
            double x = R * (lonDeg * math.PI_DBL / 180.0);
            double latRad = latDeg * math.PI_DBL / 180.0;
            double y = R * math.log(math.tan(math.PI_DBL * 0.25 + latRad * 0.5));
            return new double2(x, y);
        }

        /// <summary>Converts longitude/latitude (<c>x</c> = lon, <c>y</c> = lat, degrees) to Mercator meters.</summary>
        public static double2 LonLatToMeters(double2 lonLat) => LonLatToMeters(lonLat.x, lonLat.y);

        /// <summary>Converts Mercator meters to longitude/latitude (decimal degrees, WGS84).</summary>
        public static double2 MetersToLonLat(double x, double y)
        {
            double lon = (x / R) * 180.0 / math.PI_DBL;
            double lat = (2.0 * math.atan(math.exp(y / R)) - math.PI_DBL * 0.5) * 180.0 / math.PI_DBL;
            return new double2(lon, lat);
        }

        /// <summary>Converts Mercator meters to longitude/latitude (<c>x</c> = lon, <c>y</c> = lat, degrees).</summary>
        public static double2 MetersToLonLat(double2 meters) => MetersToLonLat(meters.x, meters.y);

        /// <summary>
        /// Converts longitude/latitude to XYZ tile indices (OSM-compatible slippy-map scheme) of the tile that
        /// contains the point at zoom <paramref name="z"/>.
        /// </summary>
        public static int2 LonLatToTile(double lonDeg, double latDeg, int z)
        {
            double n = 1L << z;
            double xtile = (lonDeg + 180.0) / 360.0 * n;
            double latRad = latDeg * math.PI_DBL / 180.0;
            double ytile = (1.0 - math.log(math.tan(latRad) + 1.0 / math.cos(latRad)) / math.PI_DBL) * 0.5 * n;
            return new int2((int)math.floor(xtile), (int)math.floor(ytile));
        }

        /// <summary>Returns the longitude/latitude of the north-west corner of the given XYZ tile.</summary>
        public static double2 TileToLonLat(int x, int y, int z)
        {
            double n = 1L << z;
            double lon = x / n * 360.0 - 180.0;
            double latRad = math.atan(math.sinh(math.PI_DBL * (1.0 - 2.0 * y / n)));
            return new double2(lon, latRad * 180.0 / math.PI_DBL);
        }

        /// <summary>
        /// Returns the bounds of the given XYZ tile in Mercator meters as <c>(minX, minY, maxX, maxY)</c>.
        /// </summary>
        public static double4 TileBoundsMeters(int x, int y, int z)
        {
            double size = EarthCircumference / (1L << z);
            double minX = -HalfCircumference + x * size;
            double maxY = HalfCircumference - y * size;
            double maxX = minX + size;
            double minY = maxY - size;
            return new double4(minX, minY, maxX, maxY);
        }

        /// <summary>Returns the side of a tile at zoom <paramref name="z"/> in Mercator meters (tiles are square).</summary>
        public static double TileSizeMeters(int z) => EarthCircumference / (1L << z);

        /// <summary>
        /// Returns the XYZ indices of the tile that contains a Mercator point at zoom <paramref name="z"/>.
        /// Inverse of <see cref="TileBoundsMeters"/>.
        /// </summary>
        public static int2 MercatorToTile(double2 mercator, int z)
        {
            double ts = TileSizeMeters(z);
            int x = (int)math.floor((mercator.x + HalfCircumference) / ts);
            int y = (int)math.floor((HalfCircumference - mercator.y) / ts);
            return new int2(x, y);
        }
    }
}
