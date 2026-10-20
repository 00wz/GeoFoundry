using System;
using Unity.Mathematics;

namespace GeoFoundry.Core.Coordinates
{
    /// <summary>
    /// Slippy-map XYZ tile coordinate: Z is the zoom level, X and Y are indices in the 2^Z × 2^Z tile grid.
    /// Immutable and blittable, so it can be stored in native containers and used in Burst jobs.
    /// </summary>
    [Serializable]
    public readonly struct TileCoord : IEquatable<TileCoord>
    {
        /// <summary>The maximum supported zoom level.</summary>
        public const int MaxZoom = 22;

        /// <summary>Zoom level, 0..<see cref="MaxZoom"/>.</summary>
        public readonly byte Z;

        /// <summary>Column index; grows eastward.</summary>
        public readonly int X;

        /// <summary>Row index; grows southward.</summary>
        public readonly int Y;

        /// <summary>Initializes a tile coordinate. X and Y are not range-checked.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="z"/> is outside [0, <see cref="MaxZoom"/>].</exception>
        public TileCoord(int z, int x, int y)
        {
            if ((uint)z > MaxZoom)
                throw new ArgumentOutOfRangeException(nameof(z), z, "Zoom level must be in the range [0, 22].");
            Z = (byte)z;
            X = x;
            Y = y;
        }

        /// <summary>Returns the tile that contains the given longitude/latitude point at zoom <paramref name="z"/>.</summary>
        public static TileCoord FromLonLat(double lonDeg, double latDeg, int z)
        {
            var xy = MercatorProjection.LonLatToTile(lonDeg, latDeg, z);
            return new TileCoord(z, xy.x, xy.y);
        }

        /// <summary>Returns the tile bounds in Mercator meters as (minX, minY, maxX, maxY).</summary>
        public double4 BoundsMeters() => MercatorProjection.TileBoundsMeters(X, Y, Z);

        /// <summary>Returns the tile center in Mercator meters.</summary>
        public double2 CenterMeters()
        {
            var b = BoundsMeters();
            return new double2((b.x + b.z) * 0.5, (b.y + b.w) * 0.5);
        }

        /// <summary>Returns the tile side in Mercator meters.</summary>
        public double SizeMeters() => MercatorProjection.TileSizeMeters(Z);

        /// <summary>
        /// Returns the tile offset by (<paramref name="dx"/>, <paramref name="dy"/>) on the same zoom level. In
        /// slippy XYZ, Y grows southward: dy = -1 is the northern neighbor, dy = +1 the southern one; dx = -1 is
        /// western, dx = +1 eastern. The result is not wrapped or clamped (see <see cref="WrapX"/>,
        /// <see cref="IsYInWorld"/>).
        /// </summary>
        public TileCoord Offset(int dx, int dy) => new TileCoord(Z, X + dx, Y + dy);

        /// <summary>Gets the number of tiles along one side of the grid at this zoom level: 2^Z.</summary>
        public int TilesPerSide => 1 << Z;

        /// <summary>
        /// Gets whether Y lies within the world, [0, 2^Z). There are no tiles beyond the Mercator poles, but
        /// <see cref="Offset"/> can produce such coordinates near the world edge.
        /// </summary>
        public bool IsYInWorld => (uint)Y < (uint)TilesPerSide;

        /// <summary>
        /// Returns the same tile with X wrapped into [0, 2^Z). The world is closed in longitude, so the neighbor
        /// across the antimeridian is a tile on the opposite edge of the grid. Wrapping changes
        /// <see cref="BoundsMeters"/>, so use the result to address tile data, not to position the tile.
        /// </summary>
        public TileCoord WrapX()
        {
            int n = TilesPerSide;
            int x = X % n;
            return new TileCoord(Z, x < 0 ? x + n : x, Y);
        }

        /// <summary>Gets whether this is the quadtree root: the single Z = 0 tile covering the whole Mercator extent.</summary>
        public bool IsRoot => Z == 0;

        /// <summary>Returns the parent tile at Z - 1.</summary>
        /// <exception cref="InvalidOperationException">The tile is the root (Z = 0).</exception>
        public TileCoord Parent()
        {
            if (Z == 0)
                throw new InvalidOperationException("Root tile (Z=0) has no parent.");
            return new TileCoord(Z - 1, X >> 1, Y >> 1);
        }

        /// <summary>
        /// Returns one of the four children at Z + 1: 0 = north-west, 1 = north-east, 2 = south-west,
        /// 3 = south-east (Y grows southward, see <see cref="Offset"/>).
        /// </summary>
        public TileCoord GetChild(int quadrant)
        {
            int cx = X << 1, cy = Y << 1;
            return quadrant switch
            {
                0 => new TileCoord(Z + 1, cx,     cy),
                1 => new TileCoord(Z + 1, cx + 1, cy),
                2 => new TileCoord(Z + 1, cx,     cy + 1),
                3 => new TileCoord(Z + 1, cx + 1, cy + 1),
                _ => throw new ArgumentOutOfRangeException(nameof(quadrant), quadrant, "Quadrant must be 0..3.")
            };
        }

        /// <summary>Writes all four children into <paramref name="dest"/> without allocating; for hot-path traversal.</summary>
        /// <exception cref="ArgumentException"><paramref name="dest"/> length is not 4.</exception>
        public void GetChildren(Span<TileCoord> dest)
        {
            if (dest.Length != 4)
                throw new ArgumentException("dest must have length 4.", nameof(dest));
            for (int q = 0; q < 4; q++) dest[q] = GetChild(q);
        }

        /// <summary>Allocating variant of <see cref="GetChildren"/> for tests and gizmos; avoid on hot paths.</summary>
        public TileCoord[] Children()
        {
            var result = new TileCoord[4];
            GetChildren(result);
            return result;
        }

        public bool Equals(TileCoord other) => Z == other.Z && X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is TileCoord tc && Equals(tc);
        public override int GetHashCode() => unchecked((Z << 28) ^ (X * 73856093) ^ (Y * 19349663));
        /// <summary>Returns the coordinate in <c>z/x/y</c> form.</summary>
        public override string ToString() => $"{Z}/{X}/{Y}";

        public static bool operator ==(TileCoord a, TileCoord b) => a.Equals(b);
        public static bool operator !=(TileCoord a, TileCoord b) => !a.Equals(b);
    }
}
