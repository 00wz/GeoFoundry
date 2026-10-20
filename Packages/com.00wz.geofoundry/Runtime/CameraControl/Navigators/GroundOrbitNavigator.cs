using GeoFoundry.CameraControl.Input;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.CameraControl.Navigators
{
    /// <summary>
    /// Orbit camera around a ground target.
    /// The state (target, pitch, yaw, distance) is stored in double-precision Mercator meters rather than local
    /// Unity coordinates: the local target position is recomputed from <see cref="MercatorPivot"/> every Tick, so a
    /// floating origin rebase needs no special synchronization.
    ///
    /// Left drag = pan ("grab the ground under the cursor"), right drag = orbit (yaw/pitch), wheel/pinch = zoom
    /// with exponential damping and reprojection under the cursor. Operation order inside <see cref="Tick"/>:
    /// keyboard pan → zoom (a self-contained predictive compensation that does not depend on the current camera
    /// pose, see <see cref="ProjectPointerOnGroundPredict"/>) → height → commit pose → pointer pan (using the fresh
    /// pose) → commit again. Panning before zooming, on the previous frame's pose, makes the camera jitter during
    /// zoom and combined pan and zoom.
    ///
    /// Cursor rays are intersected with the ground in double (<see cref="TryIntersectGroundPlaneDouble"/>), and the
    /// ray itself comes from <see cref="ScreenPointToRayPrecise"/>: Unity's
    /// <see cref="UnityEngine.Camera.ScreenPointToRay(Vector3)"/> computes the direction in float as a world point
    /// minus the camera position, giving a direction error of about |position| × 2⁻²³. Multiplied by the distance
    /// to the hit point, this error shows up as camera jitter that grows with the distance from the floating
    /// origin.
    /// </summary>
    internal sealed class GroundOrbitNavigator : ICameraNavigator
    {
        /// <summary>
        /// Number of times <see cref="SyncFromCamera"/> refines the height of the plane intersected with the camera
        /// axis: intersect → sample relief height there → intersect again.
        /// </summary>
        private const int SyncHeightIterations = 2;

        private double2 _targetMercator;
        private float _targetHeight;
        private float _pitch = 45f * Mathf.Deg2Rad;
        private float _yaw;
        private float _distance = 1500f;
        private float _distanceTarget = 1500f;

        private bool _hasPanAnchor;
        private double2 _panAnchorMercator;

        /// <summary>
        /// Orbit target in Mercator: the point the camera orbits around and looks at (see
        /// <see cref="ApplyTransform"/>).
        /// </summary>
        public double2 TargetMercator => _targetMercator;

        public void SyncFromCamera(UnityEngine.Camera camera, MercatorPivot pivot, IGroundHeightProvider heights)
        {
            var t = camera.transform;
            var pos = t.position;
            var fwd = t.forward;

            // The new target is the relief point the camera looks at, not a point on the zero-height plane: the
            // next Tick resamples the target height, and over high relief the camera would jump up by that height.
            // The axis is intersected with a horizontal plane at the relief height, refined at the hit point; if
            // the axis does not point down, the target is under the camera.
            var targetMercator = pivot.MercatorFromWorld(new float3(pos.x, pos.y, pos.z));
            var targetHeight = SampleHeight(heights, targetMercator);
            if (fwd.y < -0.01f)
            {
                var ray = new Ray(pos, fwd);
                for (int i = 0; i < SyncHeightIterations; i++)
                {
                    if (!TryIntersectGroundPlaneDouble(ray, targetHeight, out var hitLocal)) break;
                    targetMercator = pivot.PivotMeters + new double2(hitLocal.x, hitLocal.z);
                    targetHeight = SampleHeight(heights, targetMercator);
                }
            }

            var groundPoint = ToVector3(pivot.WorldFromMercator(targetMercator, targetHeight));

            var offset = pos - groundPoint;
            var distance = Mathf.Max(offset.magnitude, 0.01f);
            var dir = offset / distance;

            _distance = distance;
            _distanceTarget = distance;
            _pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f));
            _yaw = NormalizeAngle(Mathf.Atan2(dir.x, dir.z));

            _targetMercator = targetMercator;
            _targetHeight = targetHeight;
            _hasPanAnchor = false;
        }

        public void Disable() { }

        /// <summary>Sets the state programmatically; used by <c>MapCameraRig.SetCameraState</c>.</summary>
        public void SetState(double lonDeg, double latDeg, float distance, float yawDeg, float pitchDeg, IGroundHeightProvider heights)
        {
            _targetMercator = MercatorProjection.LonLatToMeters(lonDeg, latDeg);
            _distance = distance;
            _distanceTarget = distance;
            _yaw = NormalizeAngle(yawDeg * Mathf.Deg2Rad);
            _pitch = pitchDeg * Mathf.Deg2Rad;
            _targetHeight = heights != null && heights.TryGetHeight(_targetMercator, out var h) ? h : GroundHeightDefaults.FallbackHeightMeters;
            _hasPanAnchor = false;
        }

        public (double lonDeg, double latDeg, float distance, float yawDeg, float pitchDeg) GetState()
        {
            var lonLat = MercatorProjection.MetersToLonLat(_targetMercator);
            return (lonLat.x, lonLat.y, _distance, _yaw * Mathf.Rad2Deg, _pitch * Mathf.Rad2Deg);
        }

        public void Tick(
            float deltaTime,
            in CameraInputFrame input,
            MercatorPivot pivot,
            IGroundHeightProvider heights,
            CameraControlConfig config,
            UnityEngine.Camera camera)
        {
            // 1. Orbit (yaw/pitch).
            _yaw = NormalizeAngle(_yaw + input.YawDeltaRad);
            _pitch = Mathf.Clamp(_pitch + input.PitchDeltaRad,
                config.MinPitchDeg * Mathf.Deg2Rad, config.MaxPitchDeg * Mathf.Deg2Rad);

            // 2. Keyboard pan along the ground plane, relative to the current yaw. "Forward" is the horizontal part
            // of the view direction (-dir), not dir: the camera sits at target + dir * distance and looks AT the
            // target (see ApplyTransform/PitchYawToDirection).
            if (input.KeyboardMoveAxis.sqrMagnitude > 0.0001f)
            {
                var speed = input.FastModifier ? config.GroundKeyboardSpeedFast : config.GroundKeyboardSpeed;
                var forward = new Vector2(-Mathf.Sin(_yaw), -Mathf.Cos(_yaw));
                var right = new Vector2(forward.y, -forward.x);
                var move = (right * input.KeyboardMoveAxis.x + forward * input.KeyboardMoveAxis.y) * (speed * deltaTime);
                _targetMercator += new double2(move.x, move.y);
            }

            // 3. Zoom: log-space target with exponential damping and reprojection under the cursor. The compensation
            // is self-contained (see ProjectPointerOnGroundPredict): it does not depend on camera.transform but
            // temporarily places the camera at the old and new distances and compares the projections. That is
            // why it can, and must, run BEFORE the camera pose is committed.
            if (Mathf.Abs(input.ZoomDeltaLog) > 0f)
            {
                var logDist = Mathf.Log(_distanceTarget, 2f) + input.ZoomDeltaLog;
                _distanceTarget = Mathf.Clamp(Mathf.Pow(2f, logDist), config.MinCameraDistance, config.MaxCameraDistance);
            }

            var alpha = 1f - Mathf.Pow(1f - config.ZoomDampingFactor, deltaTime * 60f);
            var newDistance = Mathf.Lerp(_distance, _distanceTarget, alpha);
            if (Mathf.Abs(newDistance - _distanceTarget) < 0.001f) newDistance = _distanceTarget;

            if (newDistance != _distance && input.PointerValid)
            {
                var hasBefore = ProjectPointerOnGroundPredict(camera, pivot, input.PointerScreenPosition, _distance, out var beforeMercator);
                var hasAfter = ProjectPointerOnGroundPredict(camera, pivot, input.PointerScreenPosition, newDistance, out var afterMercator);

                if (hasBefore && hasAfter)
                {
                    _targetMercator += beforeMercator - afterMercator;
                }
            }

            _distance = newDistance;

            // 4. Target height: a fresh relief sample under the point already moved by keyboard pan and zoom.
            if (heights != null && heights.TryGetHeight(_targetMercator, out var sampledHeight))
            {
                _targetHeight = sampledHeight;
            }

            // 5. Commit the camera pose; the pointer pan below relies on it.
            ApplyTransform(camera, pivot);

            // 6. Pointer grab pan (left mouse button / one finger): the Mercator anchor point is fixed when the drag
            // starts, and each frame the target moves so that the anchor is under the cursor again. Uses the real
            // camera pose committed in step 5, without predictive probing (unlike zoom).
            if (input.PanActive && input.PointerValid)
            {
                var ray = ScreenPointToRayPrecise(camera, input.PointerScreenPosition);

                if (TryIntersectGroundPlaneDouble(ray, _targetHeight, out var hitLocal))
                {
                    var groundMercatorNow = pivot.PivotMeters + new double2(hitLocal.x, hitLocal.z);
                    if (!_hasPanAnchor)
                    {
                        _panAnchorMercator = groundMercatorNow;
                        _hasPanAnchor = true;
                    }
                    else
                    {
                        _targetMercator += _panAnchorMercator - groundMercatorNow;

                        // 7. Final commit, only when the pan actually moved the target.
                        ApplyTransform(camera, pivot);
                    }
                }
            }
            else
            {
                _hasPanAnchor = false;
            }
        }

        /// <summary>
        /// Predictive projection of the cursor onto the ground: temporarily places the camera in a hypothetical pose
        /// at <paramref name="distance"/> (same target, pitch and yaw), projects the screen point onto the horizontal
        /// plane at the target height and returns the Mercator coordinates of the hit. Independent of the current
        /// camera.transform; called twice per Tick (old and new distance), and ApplyTransform overwrites the final
        /// pose afterwards.
        /// </summary>
        private bool ProjectPointerOnGroundPredict(
            UnityEngine.Camera camera, MercatorPivot pivot, Vector2 pointerScreenPosition, float distance, out double2 hitMercator)
        {
            var localTarget = ToVector3(pivot.WorldFromMercator(_targetMercator, _targetHeight));
            var dir = PitchYawToDirection(_pitch, _yaw);
            var probePos = localTarget + dir * distance;

            var t = camera.transform;
            t.position = probePos;
            var lookDir = localTarget - probePos;
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                t.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
            }

            var ray = ScreenPointToRayPrecise(camera, pointerScreenPosition);
            if (TryIntersectGroundPlaneDouble(ray, _targetHeight, out var hitLocal))
            {
                hitMercator = pivot.PivotMeters + new double2(hitLocal.x, hitLocal.z);
                return true;
            }

            hitMercator = default;
            return false;
        }

        /// <summary>
        /// Wrapper over <see cref="UnityEngine.Camera.ScreenPointToRay(Vector3)"/> that removes its float direction
        /// error at large camera positions (see the class summary): computes the direction with the camera
        /// temporarily moved to the local origin, where ScreenPointToRay is precise, and returns the real origin.
        /// For a fixed rotation and projection, the ray direction does not depend on the camera position.
        /// </summary>
        private static Ray ScreenPointToRayPrecise(UnityEngine.Camera camera, Vector2 screenPosition)
        {
            var t = camera.transform;
            var realPosition = t.position;
            t.position = Vector3.zero;
            var rayAtOrigin = camera.ScreenPointToRay(screenPosition);
            t.position = realPosition;
            return new Ray(realPosition, rayAtOrigin.direction);
        }

        /// <summary>
        /// Intersects a ray with the horizontal plane at <paramref name="planeHeight"/>, computed in double
        /// (<see cref="UnityEngine.Plane.Raycast"/> works in float). For rays nearly parallel to the ground (low
        /// pitch, or a high camera at a large <c>_distance</c>) the denominator is close to zero and <c>t</c> can be
        /// very large; adding <c>t * direction</c> to a much smaller origin in float would lose precision. Callers
        /// convert the result straight to absolute Mercator double2 without materializing the potentially distant
        /// hit point as a float Vector3.
        /// </summary>
        private static bool TryIntersectGroundPlaneDouble(Ray ray, float planeHeight, out double3 hitLocal)
        {
            double dirY = ray.direction.y;
            if (math.abs(dirY) < 1e-12)
            {
                hitLocal = default;
                return false;
            }

            double t = ((double)planeHeight - (double)ray.origin.y) / dirY;
            if (t < 0.0)
            {
                hitLocal = default;
                return false;
            }

            hitLocal = new double3(
                ray.origin.x + t * ray.direction.x,
                planeHeight,
                ray.origin.z + t * ray.direction.z);
            return true;
        }

        private void ApplyTransform(UnityEngine.Camera camera, MercatorPivot pivot)
        {
            var localTarget = ToVector3(pivot.WorldFromMercator(_targetMercator, _targetHeight));
            var dir = PitchYawToDirection(_pitch, _yaw);
            var camPos = localTarget + dir * _distance;

            var t = camera.transform;
            t.position = camPos;

            var lookDir = localTarget - camPos;
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                t.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
            }
        }

        private static Vector3 PitchYawToDirection(float pitch, float yaw)
        {
            var cosPitch = Mathf.Cos(pitch);
            return new Vector3(cosPitch * Mathf.Sin(yaw), Mathf.Sin(pitch), cosPitch * Mathf.Cos(yaw));
        }

        private static Vector3 ToVector3(float3 v) => new(v.x, v.y, v.z);

        private static float SampleHeight(IGroundHeightProvider heights, double2 mercator) =>
            heights != null && heights.TryGetHeight(mercator, out var h) ? h : GroundHeightDefaults.FallbackHeightMeters;

        private static float NormalizeAngle(float rad)
        {
            const float twoPi = 2f * Mathf.PI;
            rad %= twoPi;
            if (rad < 0f) rad += twoPi;
            return rad;
        }
    }
}
