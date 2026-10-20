using Unity.Mathematics;

namespace GeoFoundry.Core.Ground
{
    /// <summary>
    /// Queries the terrain height at an arbitrary Mercator point, independently of how terrain is stored or loaded.
    /// </summary>
    /// <remarks>
    /// Implemented in <c>GeoFoundry.Ground</c> (see <c>GroundGridHeightProvider</c>). The interface lives in
    /// <c>GeoFoundry.Core</c> so that leaf assemblies such as <c>GeoFoundry.CameraControl</c> can receive it
    /// through dependency injection without referencing <c>GeoFoundry.Ground</c>.
    /// </remarks>
    public interface IGroundHeightProvider
    {
        /// <summary>
        /// Tries to get the terrain height at <paramref name="mercatorMeters"/>.
        /// </summary>
        /// <param name="mercatorMeters">The query point in Web Mercator meters.</param>
        /// <param name="heightMeters">The terrain height in meters when the method returns true.</param>
        /// <returns>
        /// False if the terrain tile at that point is not loaded yet. The caller chooses the fallback, usually the
        /// last known height or <see cref="GroundHeightDefaults.FallbackHeightMeters"/>.
        /// </returns>
        bool TryGetHeight(double2 mercatorMeters, out float heightMeters);
    }
}
