using GeoFoundry.CameraControl.Input;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GeoFoundry.CameraControl.Navigators
{
    /// <summary>
    /// 6-DoF flight camera: pointer lock mouse look plus WASD.
    /// Desktop only (pointer lock is unavailable in touch browsers); <see cref="MapCameraRig"/> does not switch to
    /// this navigator without a mouse and keyboard.
    ///
    /// The position is not cached: it is read from and written to <c>camera.transform.position</c> directly, which
    /// <see cref="TransformFloatingOriginController"/> already shifts on rebase (it follows the camera).
    /// </summary>
    internal sealed class FreeFlightNavigator : ICameraNavigator
    {
        private float _pitch;
        private float _yaw;
        private bool _pointerLocked;

        public void SyncFromCamera(UnityEngine.Camera camera, MercatorPivot pivot, IGroundHeightProvider heights)
        {
            var fwd = camera.transform.forward;
            _yaw = Mathf.Atan2(fwd.x, fwd.z);
            _pitch = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f));
        }

        public void Disable()
        {
            ReleasePointerLock();
        }

        public void Tick(
            float deltaTime,
            in CameraInputFrame input,
            MercatorPivot pivot,
            IGroundHeightProvider heights,
            CameraControlConfig config,
            UnityEngine.Camera camera)
        {
            UpdatePointerLock();

            var mouse = Mouse.current;
            if (_pointerLocked && mouse != null)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * config.FreeFlightRotationSensitivity;
                // Mouse up (delta.y > 0) raises the view (pitch increases), the usual FPS mouse-look convention.
                _pitch = Mathf.Clamp(
                    _pitch + delta.y * config.FreeFlightRotationSensitivity,
                    -89.99f * Mathf.Deg2Rad, 89.99f * Mathf.Deg2Rad);
            }

            var t = camera.transform;
            t.rotation = Quaternion.Euler(-_pitch * Mathf.Rad2Deg, _yaw * Mathf.Rad2Deg, 0f);

            if (input.KeyboardMoveAxis.sqrMagnitude > 0.0001f)
            {
                var speed = input.FastModifier ? config.FreeFlightSpeedFast : config.FreeFlightSpeed;
                var move = (t.right * input.KeyboardMoveAxis.x + t.forward * input.KeyboardMoveAxis.y) * (speed * deltaTime);
                t.position += move;
            }

            if (heights != null)
            {
                var pos = t.position;
                var mercator = pivot.MercatorFromWorld(new Unity.Mathematics.float3(pos.x, pos.y, pos.z));
                if (heights.TryGetHeight(mercator, out var groundHeight))
                {
                    var minY = groundHeight + config.MinFreeCameraHeight;
                    if (pos.y < minY)
                    {
                        pos.y = minY;
                        t.position = pos;
                    }
                }
            }
        }

        private void UpdatePointerLock()
        {
            var mouse = Mouse.current;
            if (mouse == null)
            {
                _pointerLocked = false;
                return;
            }

            if (!_pointerLocked && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                _pointerLocked = true;
            }
            else if (_pointerLocked && Cursor.lockState != CursorLockMode.Locked)
            {
                // The user left the lock (Esc, or the window or tab lost focus).
                _pointerLocked = false;
            }
        }

        private void ReleasePointerLock()
        {
            if (!_pointerLocked) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _pointerLocked = false;
        }
    }
}
