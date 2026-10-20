using System;
using GeoFoundry.CameraControl.Input;
using GeoFoundry.CameraControl.Navigators;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.TileSelection;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.CameraControl
{
    /// <summary>Navigation mode of <see cref="MapCameraRig"/>.</summary>
    public enum CameraMode
    {
        /// <summary>Orbit around a target point on the ground.</summary>
        Ground,
        /// <summary>Free 6-DoF flight with mouse look.</summary>
        Free,
    }

    /// <summary>
    /// Camera controller component: moves a <see cref="UnityEngine.Camera"/> using one of two navigation strategies
    /// (<see cref="GroundOrbitNavigator"/>, <see cref="FreeFlightNavigator"/>), merging desktop and touch input
    /// into a common <see cref="CameraInputFrame"/>.
    ///
    /// This is an optional add-on; the rest of the package does not depend on it. It requires one call to
    /// <see cref="Initialize"/> with the same <see cref="IFloatingOriginController"/> the package uses to position
    /// tiles and terrain. A separate pivot would not work: after the first floating origin rebase the camera
    /// would drift away from the tiles, which are rebased against the real pivot.
    ///
    /// The rig does not care what initializes it or when: <c>GeoFoundryBootstrapper</c> does it automatically,
    /// but a custom bootstrap script or DI container can do the same. Until <see cref="Initialize"/> is called,
    /// the component does not move the camera.
    /// </summary>
    [AddComponentMenu("GeoFoundry/Map Camera Rig")]
    public sealed class MapCameraRig : MonoBehaviour, IMapCameraBinding
    {
        [Tooltip("Camera controlled by the rig. Defaults to Camera.main.")]
        [SerializeField] private UnityEngine.Camera _camera;

        [Tooltip("Tuning constants. If not set, the built-in CameraControlConfig defaults are used.")]
        [SerializeField] private CameraControlConfig _config;

        private readonly GroundOrbitNavigator _ground = new();
        private readonly FreeFlightNavigator _free = new();
        private ICameraNavigator _active;

        private DesktopCameraInputReader _desktopReader;
        private TouchCameraInputReader _touchReader;

        private CameraControlConfig _runtimeConfig;
        private IFloatingOriginController _originController;
        private bool _loggedMissingOriginWarning;

        /// <summary>Gets the current navigation mode.</summary>
        public CameraMode Mode { get; private set; } = CameraMode.Ground;

        /// <summary>
        /// Gets whether the rig is active (like <see cref="Behaviour.isActiveAndEnabled"/>) and initialized:
        /// <see cref="Initialize"/> has been called and a camera was found. Only then does the rig drive the camera;
        /// otherwise <see cref="Mode"/> and <see cref="OrbitTargetMercator"/> say nothing about it.
        /// </summary>
        public bool IsActiveAndInitialized =>
            enabled && gameObject.activeInHierarchy && _camera != null && _originController != null;

        /// <summary>
        /// Gets the <see cref="CameraMode.Ground"/> orbit target in Mercator: the point the camera orbits around
        /// and looks at. Not updated in <see cref="CameraMode.Free"/>.
        /// </summary>
        public double2 OrbitTargetMercator => _ground.TargetMercator;

        /// <summary>Gets the required floating origin dependency, set via <see cref="Initialize"/>.</summary>
        public IFloatingOriginController OriginController => _originController;

        /// <summary>
        /// Gets or sets the optional terrain height provider for the ground target and the Free camera height clamp.
        /// Unlike <see cref="OriginController"/>, it is truly optional (without it the target is simply not sampled
        /// from the relief), so it can be changed at any time, not only via <see cref="Initialize"/>.
        /// </summary>
        public IGroundHeightProvider GroundHeightProvider { get; set; }

        private CameraControlConfig EffectiveConfig => _config != null ? _config : _runtimeConfig;

        /// <summary>
        /// Initializes the rig with the <see cref="IFloatingOriginController"/> to use (required; see the class
        /// summary). Can be called at any point of the lifecycle, before or after this component's OnEnable; the
        /// rig activates once both the camera and the pivot are available.
        /// </summary>
        /// <param name="originController">Floating origin controller shared with the rest of the package.</param>
        /// <param name="groundHeightProvider">
        /// Optional terrain height provider; see <see cref="GroundHeightProvider"/>.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="originController"/> is null.</exception>
        public void Initialize(IFloatingOriginController originController, IGroundHeightProvider groundHeightProvider = null)
        {
            _originController = originController ?? throw new ArgumentNullException(nameof(originController));
            GroundHeightProvider = groundHeightProvider;
            TryActivate();
        }

        private void Awake()
        {
            if (_config == null)
            {
                _runtimeConfig = ScriptableObject.CreateInstance<CameraControlConfig>();
            }
        }

        private void OnEnable()
        {
            if (_camera == null) _camera = UnityEngine.Camera.main;
            if (_camera == null)
            {
                Debug.LogError("[GeoFoundry] MapCameraRig: no camera found (neither the _camera field nor Camera.main).", this);
                enabled = false;
                return;
            }

            _desktopReader = new DesktopCameraInputReader();
            _touchReader = new TouchCameraInputReader();

            TryActivate();
        }

        private void OnDisable()
        {
            _active?.Disable();
            _active = null;

            _touchReader?.Dispose();
            _touchReader = null;
            _desktopReader = null;
        }

        private void OnDestroy()
        {
            if (_runtimeConfig != null)
            {
                Destroy(_runtimeConfig);
                _runtimeConfig = null;
            }
        }

        private void Update()
        {
            if (_camera == null) return;

            if (_originController == null)
            {
                if (!_loggedMissingOriginWarning)
                {
                    Debug.LogWarning("[GeoFoundry] MapCameraRig: Initialize() has not been called yet; the camera will " +
                                      "not move until the rig receives an IFloatingOriginController.", this);
                    _loggedMissingOriginWarning = true;
                }
                return;
            }

            // _active is non-null here: Update() only runs after OnEnable(), and if _originController was not set
            // at that point, Initialize() has since called TryActivate().

            var frame = new CameraInputFrame();
            if (_desktopReader != null) frame += _desktopReader.Read(EffectiveConfig);
            if (_touchReader != null) frame += _touchReader.Read(EffectiveConfig);

            if (frame.ToggleModeRequested)
            {
                SetMode(Mode == CameraMode.Ground ? CameraMode.Free : CameraMode.Ground);
            }

            _active.Tick(Time.deltaTime, in frame, _originController.Current, GroundHeightProvider, EffectiveConfig, _camera);
        }

        /// <summary>
        /// Switches the camera mode, keeping the camera position (see <see cref="ICameraNavigator.SyncFromCamera"/>).
        /// Free mode is unavailable without a mouse and keyboard (touch-only devices). Does nothing before
        /// <see cref="Initialize"/>.
        /// </summary>
        public void SetMode(CameraMode mode)
        {
            if (_originController == null) return;
            if (mode == CameraMode.Free && !DesktopCameraInputReader.IsAvailable) return;
            if (mode == Mode) return;
            ActivateNavigator(mode, syncFromCamera: true);
        }

        /// <summary>
        /// Sets the Ground camera state and switches to <see cref="CameraMode.Ground"/>, e.g. to restore a view
        /// from a URL or PlayerPrefs. Can be called before <see cref="Initialize"/>; the state is applied to the
        /// camera once the rig is initialized.
        /// </summary>
        /// <param name="lonDeg">Target longitude, degrees.</param>
        /// <param name="latDeg">Target latitude, degrees.</param>
        /// <param name="distance">Distance from the target to the camera, meters.</param>
        /// <param name="yawDeg">Yaw, degrees.</param>
        /// <param name="pitchDeg">Pitch, degrees.</param>
        public void SetCameraState(double lonDeg, double latDeg, float distance, float yawDeg, float pitchDeg)
        {
            _ground.SetState(lonDeg, latDeg, distance, yawDeg, pitchDeg, GroundHeightProvider);
            ActivateNavigator(CameraMode.Ground, syncFromCamera: false);
        }

        /// <summary>
        /// Returns the Ground camera state in the units of <see cref="SetCameraState"/> (degrees, meters).
        /// Not updated in <see cref="CameraMode.Free"/>.
        /// </summary>
        public (double lonDeg, double latDeg, float distance, float yawDeg, float pitchDeg) GetCameraState() =>
            _ground.GetState();

        // ---- IMapCameraBinding (binding by the bootstrapper) ------------------

        /// <summary>Initial camera pitch, degrees.</summary>
        private const float StartPitchDeg = 80f;

        void IMapCameraBinding.Bind(IFloatingOriginController origin, IGroundHeightProvider heights) =>
            Initialize(origin, heights);

        void IMapCameraBinding.SetStartPosition(double lonDeg, double latDeg, float distanceMeters) =>
            SetCameraState(lonDeg, latDeg, distanceMeters, 0f, StartPitchDeg);

        ITilePrioritizer IMapCameraBinding.CreatePrioritizer(ITilePrioritizer fallback) =>
            new MapCameraRigPrioritizer(this, fallback);

        private void TryActivate()
        {
            if (_active != null || _camera == null || _originController == null) return;
            ActivateNavigator(CameraMode.Ground, syncFromCamera: true);
        }

        private void ActivateNavigator(CameraMode mode, bool syncFromCamera)
        {
            _active?.Disable();
            Mode = mode;
            _active = mode == CameraMode.Free ? (ICameraNavigator)_free : _ground;
            if (syncFromCamera && _camera != null && _originController != null)
            {
                _active.SyncFromCamera(_camera, _originController.Current, GroundHeightProvider);
            }
        }
    }
}
