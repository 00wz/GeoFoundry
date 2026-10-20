using UnityEngine;

namespace GeoFoundry.CameraControl.Input
{
    /// <summary>
    /// One frame of aggregated camera input: the common format between input readers
    /// (<see cref="DesktopCameraInputReader"/>, <see cref="TouchCameraInputReader"/>) and navigators
    /// (<c>GroundOrbitNavigator</c>, <c>FreeFlightNavigator</c>). Decouples navigators from devices: readers
    /// already convert all values to final units (radians, log distance) using the sensitivities from
    /// <c>CameraControlConfig</c>. Frames from several readers are summed (see <c>MapCameraRig.Update</c>);
    /// in practice usually only one reader is active at a time.
    /// </summary>
    internal struct CameraInputFrame
    {
        /// <summary>"Grab the ground" pan is active (left mouse button / one finger). The navigator computes
        /// the world-space delta itself by raycasting <see cref="PointerScreenPosition"/> to the ground each
        /// frame.</summary>
        public bool PanActive;

        /// <summary>Screen position of the cursor or anchor finger, pixels; used for pan and zoom-to-cursor.</summary>
        public Vector2 PointerScreenPosition;
        public bool PointerValid;

        /// <summary>Yaw delta, radians (right mouse drag on desktop, two-finger rotation on touch).</summary>
        public float YawDeltaRad;

        /// <summary>Pitch delta, radians (right mouse drag on desktop, two-finger vertical drag on touch).</summary>
        public float PitchDeltaRad;

        /// <summary>Delta of log2(distance): mouse wheel on desktop, pinch on touch.</summary>
        public float ZoomDeltaLog;

        /// <summary>Keyboard movement axis in camera space: x = strafe, y = forward.</summary>
        public Vector2 KeyboardMoveAxis;

        /// <summary>Speed-up modifier (Shift).</summary>
        public bool FastModifier;

        /// <summary>Ground/Free mode toggle requested (Tab).</summary>
        public bool ToggleModeRequested;

        public static CameraInputFrame operator +(CameraInputFrame a, CameraInputFrame b)
        {
            return new CameraInputFrame
            {
                PanActive = a.PanActive || b.PanActive,
                PointerScreenPosition = b.PointerValid ? b.PointerScreenPosition : a.PointerScreenPosition,
                PointerValid = a.PointerValid || b.PointerValid,
                YawDeltaRad = a.YawDeltaRad + b.YawDeltaRad,
                PitchDeltaRad = a.PitchDeltaRad + b.PitchDeltaRad,
                ZoomDeltaLog = a.ZoomDeltaLog + b.ZoomDeltaLog,
                KeyboardMoveAxis = a.KeyboardMoveAxis + b.KeyboardMoveAxis,
                FastModifier = a.FastModifier || b.FastModifier,
                ToggleModeRequested = a.ToggleModeRequested || b.ToggleModeRequested,
            };
        }
    }
}
