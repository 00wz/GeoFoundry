using GeoFoundry.CameraControl.Input;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;

namespace GeoFoundry.CameraControl.Navigators
{
    /// <summary>
    /// Camera navigation strategy. <see cref="MapCameraRig"/> owns the active navigator and switches between them
    /// on Tab or programmatically.
    /// </summary>
    internal interface ICameraNavigator
    {
        /// <summary>
        /// Called when the navigator is activated: derives its internal state from the current camera pose so that
        /// switching modes does not teleport the camera. <paramref name="heights"/> is the terrain, if any (may be
        /// null).
        /// </summary>
        void SyncFromCamera(UnityEngine.Camera camera, MercatorPivot pivot, IGroundHeightProvider heights);

        /// <summary>Called when the navigator is deactivated; releases resources such as pointer lock.</summary>
        void Disable();

        /// <summary>Performs one update step: reads the aggregated input frame and moves camera.transform.</summary>
        void Tick(
            float deltaTime,
            in CameraInputFrame input,
            MercatorPivot pivot,
            IGroundHeightProvider heights,
            CameraControlConfig config,
            UnityEngine.Camera camera);
    }
}
