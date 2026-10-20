namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Constants of the Mapbox Vector Tile specification v2.1
    /// (https://github.com/mapbox/vector-tile-spec/tree/master/2.1), used by the parser instead of magic numbers.
    /// </summary>
    internal static class MvtSpec
    {
        // ---- Protocol Buffers wire types --------------------------------------
        public const int WireVarint = 0;
        public const int WireI64    = 1;
        public const int WireLen    = 2;
        public const int WireI32    = 5;

        // ---- Tile message field numbers ---------------------------------------
        public const int TileLayers = 3;

        // ---- Layer message field numbers --------------------------------------
        public const int LayerName     = 1;
        public const int LayerFeatures = 2;
        public const int LayerKeys     = 3;
        public const int LayerValues   = 4;
        public const int LayerExtent   = 5;
        public const int LayerVersion  = 15;

        // ---- Value message field numbers --------------------------------------
        public const int ValueString = 1;
        public const int ValueFloat  = 2;
        public const int ValueDouble = 3;
        public const int ValueInt    = 4;
        public const int ValueUInt   = 5;
        public const int ValueSInt   = 6;
        public const int ValueBool   = 7;

        // ---- Feature message field numbers ------------------------------------
        public const int FeatureId       = 1;
        public const int FeatureTags     = 2;
        public const int FeatureType     = 3;
        public const int FeatureGeometry = 4;

        // ---- Geometry command IDs ---------------------------------------------
        public const int CmdMoveTo    = 1;
        public const int CmdLineTo    = 2;
        public const int CmdClosePath = 7;

        // ---- Defaults ----------------------------------------------------------
        public const uint DefaultExtent  = 4096;
        public const uint DefaultVersion = 1;

        // ---- Helpers -----------------------------------------------------------
        public static int Tag(int fieldNumber, int wireType) => (fieldNumber << 3) | wireType;
        public static int CommandIntId(int commandId, int count) => (commandId & 0x7) | (count << 3);
    }
}
