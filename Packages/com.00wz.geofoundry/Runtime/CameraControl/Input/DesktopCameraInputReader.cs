using UnityEngine;
using UnityEngine.InputSystem;

namespace GeoFoundry.CameraControl.Input
{
    /// <summary>
    /// Reads mouse and keyboard through the Input System and produces a <see cref="CameraInputFrame"/>.
    /// Bindings: left drag = pan, right drag = orbit, wheel = zoom, WASD = pan, Shift = speed up, Tab = toggle mode.
    /// On devices without a mouse or keyboard (touch only) returns an empty frame, so it does not interfere with
    /// <see cref="TouchCameraInputReader"/>.
    /// </summary>
    internal sealed class DesktopCameraInputReader
    {
        public CameraInputFrame Read(CameraControlConfig config)
        {
            var frame = new CameraInputFrame();

            var mouse = Mouse.current;
            if (mouse != null)
            {
                frame.PointerScreenPosition = mouse.position.ReadValue();
                frame.PointerValid = true;

                if (mouse.leftButton.isPressed)
                {
                    frame.PanActive = true;
                }

                if (mouse.rightButton.isPressed)
                {
                    var delta = mouse.delta.ReadValue();
                    var sensRad = Mathf.Deg2Rad * config.OrbitSensitivityDegPerPixel;
                    frame.YawDeltaRad = delta.x * sensRad;
                    // Mouse up (delta.y > 0) tilts the camera toward the horizon (pitch decreases), the usual
                    // orbit camera convention (Google Earth etc.), hence the minus sign.
                    frame.PitchDeltaRad = -delta.y * sensRad;
                }

                // Scrolling up (scroll.y > 0) moves the camera closer (distance decreases), the usual zoom
                // convention, hence the minus sign.
                frame.ZoomDeltaLog = -mouse.scroll.ReadValue().y * config.ZoomSpeed;
            }

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                var axis = Vector2.zero;
                if (keyboard.wKey.isPressed) axis.y += 1f;
                if (keyboard.sKey.isPressed) axis.y -= 1f;
                if (keyboard.dKey.isPressed) axis.x += 1f;
                if (keyboard.aKey.isPressed) axis.x -= 1f;
                frame.KeyboardMoveAxis = axis;

                frame.FastModifier = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                frame.ToggleModeRequested = keyboard.tabKey.wasPressedThisFrame;
            }

            return frame;
        }

        /// <summary>Whether desktop navigation is available (Free mode needs a mouse for pointer lock).</summary>
        public static bool IsAvailable => Mouse.current != null && Keyboard.current != null;
    }
}
