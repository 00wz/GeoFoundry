namespace GeoFoundry.Core.Ground
{
    /// <summary>
    /// The single source of truth for the fallback terrain height used whenever real height data is unavailable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is used when there is no DEM data for a tile, whether the central one or any of its 8 neighbors,
    /// symmetrically (<c>PaddedHeightSampler</c>, used by <c>DemGroundGridProvider</c>); when terrain elevation
    /// is disabled (the flat 1×1 grid of <c>FlatGroundGridProvider</c>); when the tile is not loaded yet
    /// (<c>GroundGridHeightProvider</c>); and when there is no height provider at all
    /// (<c>GroundOrbitNavigator</c>). A single value guarantees that there is no false seam where a tile with real
    /// data meets a fallback tile: every part of the system agrees that "no data" means exactly this number.
    /// </para>
    /// <para>
    /// It lives in <c>GeoFoundry.Core</c>, together with <see cref="IGroundHeightProvider"/>, so that leaf
    /// assemblies such as <c>GeoFoundry.CameraControl</c> can use it without referencing
    /// <c>GeoFoundry.Geometry</c> or <c>GeoFoundry.Ground</c>.
    /// </para>
    /// </remarks>
    public static class GroundHeightDefaults
    {
        /// <summary>Fallback terrain height, in meters.</summary>
        public const float FallbackHeightMeters = 0f;
    }
}
