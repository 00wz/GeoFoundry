using NUnit.Framework;
using GeoFoundry.CameraControl;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.TileSelection;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GeoFoundry.Tests.CameraControl
{
    /// <summary>
    /// <see cref="MapCameraRigPrioritizer"/>: while the rig drives the camera in Ground mode, priorities are the
    /// distance from the orbit target and the fallback strategy is not used at all; otherwise the fallback's
    /// priorities apply. The camera looks down above <see cref="CameraTile"/>, and the orbit target lies in
    /// <see cref="TargetTile"/>. In EditMode the rig gets no OnEnable, so the camera is assigned through the
    /// serialized field before the rig is initialized.
    /// </summary>
    public sealed class MapCameraRigPrioritizerTests
    {
        private static readonly MercatorPivot Pivot = new MercatorPivot(double2.zero);
        private static readonly double2 Target = new double2(5e6, 5e6);
        private static readonly TileCoord TargetTile = new TileCoord(2, 2, 1); // x, y ∈ [0, C/4]
        private static readonly TileCoord CameraTile = new TileCoord(2, 1, 2); // x, y ∈ [−C/4, 0]

        private sealed class FixedOrigin : IFloatingOriginController
        {
            public MercatorPivot Current { get; }
            public event System.Action<PivotChangedEventArgs> PivotChanged { add { } remove { } }

            public FixedOrigin(MercatorPivot pivot) => Current = pivot;
        }

        /// <summary>Fallback strategy with recognizable priorities; counts frames.</summary>
        private sealed class FakeFallback : ITilePrioritizer
        {
            public const float ImportanceValue = 123f;
            public const float LoadPriorityValue = -7f;
            public int FramesBegun;

            public void BeginFrame() => FramesBegun++;
            public float Importance(TileCoord coord) => ImportanceValue;
            public float LoadPriority(TileCoord coord) => LoadPriorityValue;
        }

        private GameObject _go;
        private UnityEngine.Camera _camera;
        private MapCameraRig _rig;
        private FakeFallback _fallback;
        private MapCameraRigPrioritizer _prioritizer;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("MapCameraRigPrioritizerTests");
            _go.transform.SetPositionAndRotation(new Vector3(-1e6f, 1000f, -1e6f), Quaternion.Euler(90f, 0f, 0f));
            _camera = _go.AddComponent<UnityEngine.Camera>();
            _rig = _go.AddComponent<MapCameraRig>();
            _fallback = new FakeFallback();
            _prioritizer = new MapCameraRigPrioritizer(_rig, _fallback);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        /// <summary>Makes the rig drive the camera in Ground mode with the orbit target at <see cref="Target"/>.</summary>
        private void InitializeRig()
        {
            var serialized = new SerializedObject(_rig);
            serialized.FindProperty("_camera").objectReferenceValue = _camera;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _rig.Initialize(new FixedOrigin(Pivot));

            var lonLat = MercatorProjection.MetersToLonLat(Target);
            _rig.SetCameraState(lonLat.x, lonLat.y, 1500f, 0f, 45f);
            Assume.That(_rig.IsActiveAndInitialized, Is.True);
            Assume.That(_rig.Mode, Is.EqualTo(CameraMode.Ground));
        }

        private void AssertFallbackPriorities(string context)
        {
            foreach (var tile in new[] { TargetTile, CameraTile })
            {
                Assert.That(_prioritizer.Importance(tile), Is.EqualTo(FakeFallback.ImportanceValue), $"{context}: importance {tile}");
                Assert.That(_prioritizer.LoadPriority(tile), Is.EqualTo(FakeFallback.LoadPriorityValue), $"{context}: load priority {tile}");
            }
        }

        [Test]
        public void Ground_PrioritiesAreGroundDistanceFromOrbitTarget()
        {
            InitializeRig();

            _prioritizer.BeginFrame();

            Assert.That(_prioritizer.Importance(TargetTile), Is.EqualTo(0f), "the orbit target tile is the most important");
            Assert.That(_prioritizer.Importance(CameraTile), Is.EqualTo((float)math.length(Target)).Within(1f),
                "the tile under the camera is ranked by distance from the target to its corner (0, 0), not from the camera");
            Assert.That(_prioritizer.LoadPriority(CameraTile), Is.EqualTo(_prioritizer.Importance(CameraTile)));
            Assert.That(_fallback.FramesBegun, Is.Zero, "the fallback strategy is not used in Ground mode");
        }

        [Test]
        public void RigNotControllingCamera_PrioritiesAreFallbacks()
        {
            _prioritizer.BeginFrame();
            AssertFallbackPriorities("rig not initialized");

            InitializeRig();
            _rig.enabled = false;
            _prioritizer.BeginFrame();
            AssertFallbackPriorities("rig disabled");

            Assert.That(_fallback.FramesBegun, Is.EqualTo(2));
        }

        [Test]
        public void FreeMode_PrioritiesAreFallbacks()
        {
            InitializeRig();
            _rig.SetMode(CameraMode.Free);
            Assume.That(_rig.Mode, Is.EqualTo(CameraMode.Free), "Free mode is unavailable without a mouse and keyboard");

            _prioritizer.BeginFrame();

            AssertFallbackPriorities("Free");
            Assert.That(_fallback.FramesBegun, Is.EqualTo(1));
        }
    }
}
