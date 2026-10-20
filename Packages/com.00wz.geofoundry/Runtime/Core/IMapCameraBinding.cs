using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.TileSelection;

namespace GeoFoundry.Core
{
    /// <summary>
    /// A camera that the bootstrapper connects to the map. The bootstrapper finds a component implementing this
    /// interface on the camera, passes it the floating origin and terrain heights, sets the initial position and
    /// obtains the tile load prioritizer from it.
    /// </summary>
    /// <remarks>
    /// This keeps the bootstrapper independent of a specific camera controller (the package ships
    /// <c>MapCameraRig</c> in the optional <c>GeoFoundry.CameraControl</c> assembly). Call order:
    /// <see cref="CreatePrioritizer"/>, then <see cref="Bind"/>, then <see cref="SetStartPosition"/>; all on the
    /// main thread.
    /// </remarks>
    public interface IMapCameraBinding
    {
        /// <summary>
        /// Connects the camera to the map. Called after the tile manager starts.
        /// </summary>
        /// <param name="origin">The floating origin shared with the rest of the package.</param>
        /// <param name="heights">Ground heights; may be null if none are available.</param>
        void Bind(IFloatingOriginController origin, IGroundHeightProvider heights);

        /// <summary>
        /// Sets the initial camera position from the configuration. Called right after <see cref="Bind"/>.
        /// </summary>
        /// <param name="lonDeg">Longitude of the look-at point, in degrees.</param>
        /// <param name="latDeg">Latitude of the look-at point, in degrees.</param>
        /// <param name="distanceMeters">Distance from the camera to the look-at point, in meters.</param>
        void SetStartPosition(double lonDeg, double latDeg, float distanceMeters);

        /// <summary>
        /// Creates the tile load prioritizer for this camera. Called before <see cref="Bind"/>, while the tile
        /// manager is being assembled.
        /// </summary>
        /// <param name="fallback">
        /// Priorities by distance from the camera, for a camera that has no prioritization of its own.
        /// </param>
        ITilePrioritizer CreatePrioritizer(ITilePrioritizer fallback);
    }
}
