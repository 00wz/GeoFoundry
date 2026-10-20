using System;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace GeoFoundry.CameraControl.Input
{
    /// <summary>
    /// Reads multi-touch through EnhancedTouch and produces a Google Maps style <see cref="CameraInputFrame"/>:
    /// one-finger drag = pan (the same "grab the ground" channel as the left mouse button on desktop),
    /// pinch = zoom, two-finger rotation = yaw orbit, average two-finger vertical drag = pitch orbit.
    /// The three two-finger channels are computed simultaneously and independently each frame, with no gesture
    /// recognition state machine (as in Mapbox GL and the Google Maps SDK), so pinch, rotate and tilt can be
    /// combined in one continuous gesture.
    /// </summary>
    internal sealed class TouchCameraInputReader : IDisposable
    {
        private bool _hasPrevTwoTouch;
        private Vector2 _prevA;
        private Vector2 _prevB;

        public TouchCameraInputReader()
        {
            EnhancedTouchSupport.Enable();
        }

        public CameraInputFrame Read(CameraControlConfig config)
        {
            var frame = new CameraInputFrame();
            var touches = Touch.activeTouches;

            if (touches.Count == 1)
            {
                _hasPrevTwoTouch = false;

                frame.PanActive = true;
                frame.PointerScreenPosition = touches[0].screenPosition;
                frame.PointerValid = true;
            }
            else if (touches.Count == 2)
            {
                var a = touches[0].screenPosition;
                var b = touches[1].screenPosition;

                frame.PointerScreenPosition = (a + b) * 0.5f;
                frame.PointerValid = true;

                if (_hasPrevTwoTouch)
                {
                    var currDist = Vector2.Distance(a, b);
                    var prevDist = Vector2.Distance(_prevA, _prevB);
                    frame.ZoomDeltaLog = -(currDist - prevDist) * config.TouchZoomSensitivity;

                    var currAngle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
                    var prevAngle = Mathf.Atan2(_prevB.y - _prevA.y, _prevB.x - _prevA.x) * Mathf.Rad2Deg;
                    var angleDeltaDeg = Mathf.DeltaAngle(prevAngle, currAngle);
                    frame.YawDeltaRad = -angleDeltaDeg * Mathf.Deg2Rad * config.TouchOrbitYawSensitivity;

                    // Fingers moving up the screen tilt the camera toward the horizon (pitch decreases),
                    // the same convention as right-drag orbit on desktop.
                    var currAvgY = (a.y + b.y) * 0.5f;
                    var prevAvgY = (_prevA.y + _prevB.y) * 0.5f;
                    frame.PitchDeltaRad = -(currAvgY - prevAvgY) * Mathf.Deg2Rad * config.TouchOrbitPitchSensitivity;
                }

                _prevA = a;
                _prevB = b;
                _hasPrevTwoTouch = true;
            }
            else
            {
                _hasPrevTwoTouch = false;
            }

            return frame;
        }

        public void Dispose()
        {
            EnhancedTouchSupport.Disable();
        }
    }
}
