namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Recommended Sorting Priority (<c>_QueueOffset</c>, resulting render queue = 2000 + priority) of the materials
    /// of the OpenMapTiles handlers. Draped meshes use the ZTest Always shaders (<c>GeoFoundry/Surface Lit</c>,
    /// <c>GeoFoundry/Line Lit</c>), so the render queue alone decides which layer covers which; the order follows
    /// the <c>order</c> of the official "OSM OpenMapTiles" style. Priorities are set on the material assets in the
    /// editor (see the presets in <c>Presets/OpenMapTiles/Materials</c>); the handlers only warn in the inspector
    /// when an assigned material deviates from this table.
    /// </summary>
    /// <remarks>
    /// The range is bounded by the terrain (−50, drawn first) and the buildings (0, depth-tested and drawn after all
    /// draped layers). Inside one mesh the order of the draw items decides (casing → fill, tunnels → ground →
    /// bridges), so one slot per mesh is enough. Labels and icons render in the transparent queue and are not part
    /// of this table.
    /// </remarks>
    public static class OmtDrawOrder
    {
        public const int Terrain = -50;
        public const int Landcover = -46;
        public const int LandcoverOutline = -45;
        public const int Landuse = -44;
        public const int LanduseOutline = -43;
        public const int ParkFill = -42;
        public const int Water = -40;
        public const int Waterway = -38;
        public const int AerowayFill = -36;
        public const int AerowayLines = -35;
        public const int TransportationAreas = -32;
        public const int Transportation = -30;
        public const int WaterwayBridges = -28;
        public const int MountainLines = -27;
        public const int Boundary = -26;
        public const int ParkOutline = -24;
        public const int Building = 0;
    }
}
