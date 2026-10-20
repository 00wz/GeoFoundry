using System;
using Unity.Mathematics;

namespace GeoFoundry.Core.Coordinates
{
    /// <summary>
    /// Immutable, blittable 2D axis-aligned bounding box in Web Mercator meters (XY plane). Used by tile
    /// selection to intersect tile bounds with the visible area (the convex hull of the camera frustum on the
    /// ground plane). Pure math with no dependency on the Unity camera.
    /// </summary>
    [Serializable]
    public readonly struct MercatorBounds : IEquatable<MercatorBounds>
    {
        public readonly double MinX;
        public readonly double MinY;
        public readonly double MaxX;
        public readonly double MaxY;

        public MercatorBounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        /// <summary>Creates a box from its minimum and maximum corners.</summary>
        public static MercatorBounds FromMinMax(double2 min, double2 max) =>
            new MercatorBounds(min.x, min.y, max.x, max.y);

        /// <summary>Creates a square box of side <paramref name="size"/> (meters) around <paramref name="center"/>.</summary>
        public static MercatorBounds FromCenterSize(double2 center, double size)
        {
            double h = size * 0.5;
            return new MercatorBounds(center.x - h, center.y - h, center.x + h, center.y + h);
        }

        /// <summary>Creates a box equal to the bounds of <paramref name="tile"/>.</summary>
        public static MercatorBounds FromTile(TileCoord tile)
        {
            var b = tile.BoundsMeters();
            return new MercatorBounds(b.x, b.y, b.z, b.w);
        }

        public double2 Min => new double2(MinX, MinY);
        public double2 Max => new double2(MaxX, MaxY);
        public double2 Center => new double2((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);
        public double2 Size => new double2(MaxX - MinX, MaxY - MinY);
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public double Area => Width * Height;

        /// <summary>Returns whether <paramref name="p"/> lies inside the box or on its edge.</summary>
        public bool Contains(double2 p) =>
            p.x >= MinX && p.x <= MaxX && p.y >= MinY && p.y <= MaxY;

        /// <summary>Returns the distance from <paramref name="p"/> to the nearest point of the box; zero if inside.</summary>
        public double DistanceTo(double2 p)
        {
            double dx = p.x - math.clamp(p.x, MinX, MaxX);
            double dy = p.y - math.clamp(p.y, MinY, MaxY);
            return math.sqrt(dx * dx + dy * dy);
        }

        /// <summary>Returns whether the boxes overlap; touching edges count as overlap.</summary>
        public bool Intersects(MercatorBounds other) =>
            MaxX >= other.MinX && MinX <= other.MaxX &&
            MaxY >= other.MinY && MinY <= other.MaxY;

        /// <summary>Returns a copy grown by <paramref name="margin"/> meters on every side.</summary>
        public MercatorBounds Expand(double margin) =>
            new MercatorBounds(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);

        /// <summary>Returns the smallest box that contains this box and <paramref name="p"/>.</summary>
        public MercatorBounds Encapsulate(double2 p) =>
            new MercatorBounds(
                math.min(MinX, p.x), math.min(MinY, p.y),
                math.max(MaxX, p.x), math.max(MaxY, p.y));

        public bool Equals(MercatorBounds o) =>
            MinX == o.MinX && MinY == o.MinY && MaxX == o.MaxX && MaxY == o.MaxY;

        public override bool Equals(object obj) => obj is MercatorBounds o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(MinX, MinY, MaxX, MaxY);

        public override string ToString() =>
            $"MercatorBounds[{MinX:F1},{MinY:F1} → {MaxX:F1},{MaxY:F1}]";

        public static bool operator ==(MercatorBounds a, MercatorBounds b) => a.Equals(b);
        public static bool operator !=(MercatorBounds a, MercatorBounds b) => !a.Equals(b);
    }
}
