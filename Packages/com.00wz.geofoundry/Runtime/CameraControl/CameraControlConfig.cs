using UnityEngine;

namespace GeoFoundry.CameraControl
{
    /// <summary>
    /// Tuning constants for the camera controller. Optional: <see cref="MapCameraRig"/> works without an
    /// assigned asset and then uses the field defaults.
    /// </summary>
    [CreateAssetMenu(menuName = "GeoFoundry/Camera Control Config", fileName = "CameraControlConfig", order = 1)]
    public sealed class CameraControlConfig : ScriptableObject
    {
        [Header("Ground — Distance (Zoom)")]
        [Tooltip("Minimum distance from the orbit camera to the ground target, meters.")]
        [Min(0.1f)] public float MinCameraDistance = 10f;

        [Tooltip("Maximum distance from the orbit camera to the ground target, meters (clamped).")]
        [Min(1f)] public float MaxCameraDistance = 4000f;

        [Tooltip("Mouse wheel zoom speed: change of log2(distance) per unit of scroll delta.")]
        public float ZoomSpeed = 0.1f;

        [Tooltip("Exponential smoothing factor for the distance per 1/60 second (0..1).")]
        [Range(0.01f, 0.99f)] public float ZoomDampingFactor = 0.3f;

        [Header("Ground — pitch/yaw")]
        [Tooltip("Orbit drag (right mouse button) sensitivity, degrees per pixel.")]
        public float OrbitSensitivityDegPerPixel = 0.25f;

        [Tooltip("Minimum pitch, degrees.")]
        [Range(0f, 89f)] public float MinPitchDeg = 5f;

        [Tooltip("Maximum pitch, degrees.")]
        [Range(1f, 89.99f)] public float MaxPitchDeg = 89.99f;

        [Header("Ground — Keyboard Pan")]
        [Tooltip("WASD pan speed along the ground plane, meters per second.")]
        public float GroundKeyboardSpeed = 400f;

        [Tooltip("WASD pan speed with Shift held, meters per second.")]
        public float GroundKeyboardSpeedFast = 1200f;

        [Header("Free-flight")]
        [Tooltip("6-DoF flight speed, meters per second.")]
        public float FreeFlightSpeed = 400f;

        [Tooltip("6-DoF flight speed with Shift held, meters per second.")]
        public float FreeFlightSpeedFast = 1200f;

        [Tooltip("Mouse-look sensitivity in Free mode (pointer lock), radians per pixel.")]
        public float FreeFlightRotationSensitivity = 0.0025f;

        [Tooltip("Minimum height above the terrain in Free mode, meters (not clamped without an IGroundHeightProvider).")]
        public float MinFreeCameraHeight = 10f;

        [Header("Touch (Google Maps Style)")]
        [Tooltip("Two-finger rotation yaw sensitivity, degrees per degree of gesture rotation.")]
        public float TouchOrbitYawSensitivity = 1f;

        [Tooltip("Two-finger vertical drag pitch sensitivity, degrees per pixel.")]
        public float TouchOrbitPitchSensitivity = 0.15f;

        [Tooltip("Pinch zoom sensitivity: change of log2(distance) per pixel of change in finger distance.")]
        public float TouchZoomSensitivity = 0.003f;
    }
}
