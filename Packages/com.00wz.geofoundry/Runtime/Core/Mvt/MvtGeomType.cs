namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Geometry type of an MVT feature. Matches <c>enum GeomType</c> in the MVT protobuf schema.
    /// </summary>
    public enum MvtGeomType : byte
    {
        Unknown    = 0,
        Point      = 1,
        LineString = 2,
        Polygon    = 3,
    }
}
