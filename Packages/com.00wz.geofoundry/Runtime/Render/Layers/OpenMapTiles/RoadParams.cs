namespace GeoFoundry.Render.Layers.OpenMapTiles
{
    /// <summary>
    /// Rendering parameters for OpenMapTiles road classes. Takes the class as a string (the value of
    /// <c>feature.Properties["class"]</c>) and returns the ribbon width in meters.
    ///
    /// For another schema, register your own <see cref="ILayerHandler"/> for the "transportation" layer with its
    /// own mapping.
    /// </summary>
    internal static class RoadParams
    {
        /// <summary>Returns the road ribbon width in meters for an OpenMapTiles class.</summary>
        public static float GetWidthMeters(string omtClass) => omtClass switch
        {
            "motorway"       => 20f,
            "motorway_link"  => 10f,
            "trunk"          => 16f,
            "trunk_link"     => 8f,
            "primary"        => 14f,
            "primary_link"   => 8f,
            "secondary"      => 12f,
            "secondary_link" => 7f,
            "tertiary"       => 10f,
            "tertiary_link"  => 6f,
            "minor"          => 6f,
            "residential"    => 6f,
            "unclassified"   => 6f,
            "service"        => 4f,
            "pedestrian"     => 5f,
            "footway"        => 2f,
            "path"           => 1.5f,
            "cycleway"       => 2f,
            "track"          => 3f,
            "bridleway"      => 2f,
            "steps"          => 2f,
            _                => 4f,
        };

        /// <summary>
        /// Returns whether a road of this class is rendered: every non-empty OpenMapTiles class is.
        /// Returns <c>false</c> for a null or empty string.
        /// </summary>
        public static bool ShouldRender(string omtClass) => !string.IsNullOrEmpty(omtClass);
    }
}
