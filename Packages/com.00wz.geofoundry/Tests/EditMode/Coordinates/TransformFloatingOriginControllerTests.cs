using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode.Coordinates
{
    /// <summary>
    /// Tests rebase logic by calling the internal <see cref="TransformFloatingOriginController.Tick"/> directly
    /// instead of relying on the Player Loop: <c>Start()</c>/<c>RunLoop</c> use
    /// <see cref="Awaitable.NextFrameAsync"/>, which does not advance in Edit Mode.
    /// </summary>
    public sealed class TransformFloatingOriginControllerTests
    {
        private const double MoscowLon = 37.6175;
        private const double MoscowLat = 55.7520;

        private GameObject _targetGo;

        [TearDown]
        public void TearDown()
        {
            if (_targetGo != null) Object.DestroyImmediate(_targetGo);
        }

        private Transform MakeTarget(Vector3 localPosition)
        {
            _targetGo = new GameObject("FloatingOriginTestTarget");
            _targetGo.transform.position = localPosition;
            return _targetGo.transform;
        }

        [Test]
        public void Tick_TargetWithinThreshold_DoesNotRebase()
        {
            var initialPivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var target = MakeTarget(new Vector3(500f, 10f, 500f)); // ~707 m from the pivot
            var controller = new TransformFloatingOriginController(initialPivot, target, thresholdMeters: 1000.0);

            bool fired = false;
            controller.PivotChanged += _ => fired = true;

            controller.Tick();

            Assert.That(fired, Is.False);
            Assert.That(controller.Current.PivotMeters, Is.EqualTo(initialPivot.PivotMeters));
        }

        [Test]
        public void Tick_TargetBeyondThreshold_RebasesAndFiresEvent()
        {
            var initialPivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var target = MakeTarget(new Vector3(2000f, 10f, 0f)); // beyond the 1000 m threshold
            var controller = new TransformFloatingOriginController(initialPivot, target, thresholdMeters: 1000.0);

            PivotChangedEventArgs? received = null;
            controller.PivotChanged += args => received = args;

            controller.Tick();

            Assert.That(received, Is.Not.Null, "PivotChanged fires when the threshold is exceeded");
            Assert.That(received.Value.OldPivot.PivotMeters, Is.EqualTo(initialPivot.PivotMeters));
            Assert.That(controller.Current.PivotMeters, Is.EqualTo(received.Value.NewPivot.PivotMeters));
            Assert.That(controller.Current.PivotMeters, Is.Not.EqualTo(initialPivot.PivotMeters));
        }

        [Test]
        public void Tick_AfterRebase_TargetDoesNotJumpInWorldSpace()
        {
            var initialPivot = MercatorPivot.FromLonLat(MoscowLon, MoscowLat);
            var target = MakeTarget(new Vector3(2000f, 42f, -1500f));
            var controller = new TransformFloatingOriginController(initialPivot, target, thresholdMeters: 1000.0);

            // The physical point (in Mercator meters) the target occupied before the rebase, computed relative to
            // the old pivot; this is the ground truth independent of the currently active pivot.
            var physicalPointMercator = initialPivot.MercatorFromWorld(
                new float3(target.position.x, target.position.y, target.position.z));

            controller.Tick();

            // After the rebase the same physical point, mapped through the new pivot, must match the target's new
            // world position: the target did not move, only the coordinate system it is expressed in changed.
            var expectedWorld = controller.Current.WorldFromMercator(physicalPointMercator, target.position.y);
            Assert.That(target.position.x, Is.EqualTo(expectedWorld.x).Within(1e-3f));
            Assert.That(target.position.y, Is.EqualTo(expectedWorld.y).Within(1e-3f));
            Assert.That(target.position.z, Is.EqualTo(expectedWorld.z).Within(1e-3f));

            // And it is now close to zero, which is the point of rebasing.
            Assert.That(math.length(new float2(target.position.x, target.position.z)), Is.LessThan(1e-2f));
        }
    }
}
