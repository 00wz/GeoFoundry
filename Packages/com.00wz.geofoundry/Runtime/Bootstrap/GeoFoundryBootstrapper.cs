using System.Collections.Generic;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Core.Sources;
using GeoFoundry.Core.TileSelection;
using GeoFoundry.Ground;
using GeoFoundry.Render;
using GeoFoundry.TileIO.Net;
using GeoFoundry.TileIO.Sources;
using GeoFoundry.Tiles;
using GeoFoundry.Tiles.Diagnostics;
using GeoFoundry.Tiles.TileSelection;
using UnityEngine;
using UnityEngine.Serialization;

namespace GeoFoundry.Bootstrap
{
    /// <summary>
    /// Entry point of the package in a Unity scene. Creates all dependencies and starts the
    /// <see cref="TileLifecycleManager"/>, which ticks itself via Awaitable.
    /// </summary>
    [AddComponentMenu("GeoFoundry/Map Bootstrapper")]
    [DisallowMultipleComponent]
    public sealed class GeoFoundryBootstrapper : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private GeoFoundryConfig _config;

        [Header("Scene")]
        [Tooltip("Camera used for tile selection and followed by the floating origin. Defaults to Camera.main. " +
                 "Tiles are created as children of this object, so keep it at the origin with no rotation or scale.")]
        [SerializeField] private UnityEngine.Camera _viewCamera;

        [Header("Debug")]
        [Tooltip("Draws tile frames and a statistics panel (budget, loads, cache) in the Scene view. Editor only; " +
                 "see the Diagnostics section of the package documentation for colors and panel rows.")]
        [FormerlySerializedAs("_drawDebugGrid")]
        [SerializeField] private bool _drawDebugOverlay;

        // ---- Runtime state ---------------------------------------------------

        private TileLifecycleManager _manager;
        private TransformFloatingOriginController _originController;
        private GroundGridProvider _ownedGridProvider;
        private GroundGridHeightProvider _groundHeightProvider;
        private MapViewShaderGlobals _shaderGlobals;
        private TileDownloader _ownedDownloader;
        private IVectorTileSource _ownedVectorSource;
        private IDemTileSource _ownedDemSource;
        private TileDebugOverlay _debugOverlay;

        private UnityEngine.Camera ViewCamera => _viewCamera != null ? _viewCamera : UnityEngine.Camera.main;

        /// <summary>
        /// Gets the floating origin controller. Subscribe to <see cref="IFloatingOriginController.PivotChanged"/>
        /// to keep your own objects and systems in sync with rebases (see the package documentation).
        /// </summary>
        public IFloatingOriginController OriginController => _originController;

        /// <summary>
        /// Gets the ground height at an arbitrary Mercator point, for client code such as the camera (ground-target
        /// orbit height). Without relief, returns the flat ground height. Null while the component is disabled.
        /// </summary>
        public IGroundHeightProvider GroundHeightProvider => _groundHeightProvider;

        /// <summary>
        /// Gets the tile manager, for diagnostics (<see cref="TileLifecycleManager.GetDebugSnapshot"/>, a custom
        /// HUD). Null while the component is disabled. The bootstrapper owns its lifecycle: do not call
        /// <c>Start</c>/<c>Dispose</c>.
        /// </summary>
        public TileLifecycleManager Manager => _manager;

        /// <summary>
        /// Gets the memory used by shared ground grids (see <see cref="GroundGridProvider.MemoryBytes"/>). It is not
        /// part of the tile budget but lives in the same heap on WebGL. 0 while the component is disabled.
        /// </summary>
        public long GroundGridMemoryBytes => _ownedGridProvider?.MemoryBytes ?? 0;

        /// <summary>
        /// Raised when a tile load fails; see <see cref="TileLifecycleManager.TileLoadFailed"/>.
        /// Subscriptions survive disabling and re-enabling the component (the manager is recreated).
        /// </summary>
        public event System.Action<TileCoord, System.Exception> TileLoadFailed;

        // ---- Unity lifecycle -------------------------------------------------

        private void OnEnable()
        {
            _manager = TryBuildManager();
            if (_manager == null) { enabled = false; return; }
            _manager.TileLoadFailed += OnTileLoadFailed;
            _originController.Start();
            _manager.Start();

            BindCamera(ViewCamera);
        }

        /// <summary>
        /// Shuts the subsystem down, consumers before what they use (manager → shader globals → terrain grids →
        /// sources).
        /// Dispose cancels operations synchronously: when this method returns, no GeoFoundry HTTP requests are in
        /// flight; builds waiting on a job finish releasing when the job completes. OnDisable runs before scene
        /// teardown on exiting Play Mode, before a domain reload and when the editor closes, so no editor event
        /// subscriptions are needed. Each step is isolated so that an exception in one still releases the rest;
        /// an uncancelled request would otherwise complete in the middle of scene teardown.
        /// </summary>
        private void OnDisable()
        {
            if (_manager != null) _manager.TileLoadFailed -= OnTileLoadFailed;
            DisposeSafely(_manager);
            DisposeSafely(_shaderGlobals);
            DisposeSafely(_originController);
            DisposeSafely(_ownedGridProvider);
            DisposeSafely(_ownedVectorSource as System.IDisposable);
            DisposeSafely(_ownedDemSource as System.IDisposable);
            DisposeSafely(_ownedDownloader);

            _manager              = null;
            _shaderGlobals        = null;
            _originController     = null;
            _ownedGridProvider    = null;
            _groundHeightProvider = null;
            _ownedVectorSource    = null;
            _ownedDemSource       = null;
            _ownedDownloader      = null;
        }

        private void DisposeSafely(System.IDisposable disposable)
        {
            if (disposable == null) return;
            try { disposable.Dispose(); }
            catch (System.Exception ex) { Debug.LogException(ex, this); }
        }

        private void OnTileLoadFailed(TileCoord coord, System.Exception error) => TileLoadFailed?.Invoke(coord, error);

        private void OnDrawGizmos()
        {
            if (!_drawDebugOverlay || _manager == null) return;
            _debugOverlay ??= new TileDebugOverlay();
            _debugOverlay.Draw(_manager, _originController.Current, GroundGridMemoryBytes);
        }

        // ---- Wiring ----------------------------------------------------------

        /// <summary>
        /// If the camera has an <see cref="IMapCameraBinding"/> component (in this package, <c>MapCameraRig</c> from
        /// the optional <c>GeoFoundry.CameraControl</c> assembly), binds it to the map: passes the same
        /// <see cref="OriginController"/>/<see cref="GroundHeightProvider"/> the rest of the package uses and sets
        /// the initial position from <see cref="_config"/>. A custom bootstrap or DI container can do the same.
        /// </summary>
        private void BindCamera(UnityEngine.Camera viewCamera)
        {
            if (viewCamera == null) return;
            if (!viewCamera.TryGetComponent<IMapCameraBinding>(out var binding)) return;

            binding.Bind(_originController, _groundHeightProvider);
            binding.SetStartPosition(_config.OriginLongitude, _config.OriginLatitude, _config.OriginCameraDistance);
        }

        /// <summary>
        /// Tiles are positioned locally relative to this object, while tile selection, culling and the floating
        /// origin work in world space. A moved, rotated or scaled root silently separates the two: tiles end up
        /// away from where LOD culls and ranks them.
        /// </summary>
        private void WarnIfTransformNotIdentity()
        {
            var t = transform;
            if (t.position.sqrMagnitude < 1e-6f && Quaternion.Angle(t.rotation, Quaternion.identity) < 0.01f &&
                (t.lossyScale - Vector3.one).sqrMagnitude < 1e-6f) return;

            Debug.LogWarning("[GeoFoundry] GeoFoundryBootstrapper: the object must be at the origin with no rotation " +
                             "or scale: tiles are created as its children, while LOD and culling work in world " +
                             "space.", this);
        }

        private TileLifecycleManager TryBuildManager()
        {
            if (_config == null)
            {
                Debug.LogError("[GeoFoundry] GeoFoundryBootstrapper: Config is not assigned.", this);
                return null;
            }

            if (string.IsNullOrWhiteSpace(_config.VectorTilesEndpointTemplate))
            {
                Debug.LogError("[GeoFoundry] GeoFoundryBootstrapper: the vector tile URL is not set in Config " +
                               "(VectorTilesEndpointTemplate).", this);
                return null;
            }

            WarnIfTransformNotIdentity();

            var viewCamera = ViewCamera;
            if (viewCamera == null)
            {
                Debug.LogError("[GeoFoundry] GeoFoundryBootstrapper: no camera found (neither _viewCamera nor Camera.main); the floating origin follows the camera and cannot be created without it.", this);
                return null;
            }

            var initialPivot = MercatorPivot.FromLonLat(_config.OriginLongitude, _config.OriginLatitude);
            _originController = new TransformFloatingOriginController(
                initialPivot, viewCamera.transform, _config.FloatingOriginThresholdMeters);

            // One downloader for all sources, so MaxConcurrentHttpRequests is a shared request limit.
            // Sources do not own the downloader; OnDisable releases it after them.
            _ownedDownloader = new TileDownloader(_config.MaxConcurrentHttpRequests);

            // Vector source
            var vectorSource = new HttpVectorTileSource(_config.VectorTilesEndpointTemplate, _ownedDownloader);
            _ownedVectorSource = vectorSource;

            // Ground under the tiles: DEM relief or a flat plane.
            bool relief = _config.EnableRelief && !string.IsNullOrEmpty(_config.DemTilesEndpointTemplate);
            if (relief)
            {
                var demSource      = new HttpDemTileSource(_config.DemTilesEndpointTemplate, _ownedDownloader);
                _ownedDemSource    = demSource;
                _ownedGridProvider = new DemGroundGridProvider(demSource, _config.TerrainGridResolution);
            }
            else
            {
                _ownedGridProvider = new FlatGroundGridProvider();
            }
            _groundHeightProvider = new GroundGridHeightProvider(_ownedGridProvider);

            // Camera pixel size for the line shader (widths evaluated per frame, as in MapLibre).
            _shaderGlobals = new MapViewShaderGlobals(_originController, _groundHeightProvider);
            _shaderGlobals.Enable();

            // LOD traversal. Tile priorities come from the camera binding if there is one (IMapCameraBinding;
            // MapCameraRig ranks by camera mode), otherwise from the camera position.
            ITilePrioritizer cameraDistance = new CameraDistancePrioritizer(viewCamera, _originController);
            ITilePrioritizer prioritizer = viewCamera.TryGetComponent<IMapCameraBinding>(out var binding)
                ? binding.CreatePrioritizer(cameraDistance) ?? cameraDistance
                : cameraDistance;
            var traversal = new TileQuadtreeTraversal(_config.Lod, viewCamera, _originController, prioritizer);

            // Handler registry
            var registry = new LayerHandlerRegistry();
            foreach (var handler in _config.LayerHandlers)
            {
                if (handler != null) registry.Register(handler);
            }

            // TileBuilder. An unassigned SO is compared with null via UnityEngine.Object so that a true null
            // is passed to the interface.
            var tileBuilder = new TileBuilder(
                _ownedGridProvider, vectorSource,
                _config.TerrainHandler != null ? _config.TerrainHandler : null,
                registry, _config.LoadNeighborGrids);

            const long bytesPerMB = 1024L * 1024L;
            var budget = new TileMemoryUsage(_config.GpuBudgetMB * bytesPerMB, _config.HeapBudgetMB * bytesPerMB);

            return new TileLifecycleManager(
                tileBuilder, traversal, _originController, transform, budget, _config.MaxConcurrentLoads);
        }
    }
}
