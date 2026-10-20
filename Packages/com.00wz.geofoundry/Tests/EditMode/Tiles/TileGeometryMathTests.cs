using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Tiles.TileSelection;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>
    /// Tests the pure LOD traversal math (<see cref="TileGeometryMath"/>) on primitives, without a live scene or
    /// camera. For a symmetric (unskewed) perspective projection the SSE formula is exactly linear in 1/distance
    /// and in geometricError, so exact ratios can be checked rather than just the sign of the effect.
    /// </summary>
    public sealed class TileGeometryMathTests
    {
        private static Matrix4x4 PerspectiveProjection() =>
            Matrix4x4.Perspective(fov: 60f, aspect: 16f / 9f, zNear: 0.1f, zFar: 10000f);

        [Test]
        public void ComputeScreenSpaceError_ZeroGeometricError_ReturnsZero()
        {
            float sse = TileGeometryMath.ComputeScreenSpaceError(PerspectiveProjection(), 1080, 100f, 0.0);
            Assert.That(sse, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void ComputeScreenSpaceError_DoublingDistance_HalvesSse()
        {
            var proj = PerspectiveProjection();
            float sseNear = TileGeometryMath.ComputeScreenSpaceError(proj, 1080, 100f, 10.0);
            float sseFar = TileGeometryMath.ComputeScreenSpaceError(proj, 1080, 200f, 10.0);
            Assert.That(sseNear / sseFar, Is.EqualTo(2.0f).Within(1e-3f));
        }

        [Test]
        public void ComputeScreenSpaceError_DoublingGeometricError_DoublesSse()
        {
            var proj = PerspectiveProjection();
            float sseSmall = TileGeometryMath.ComputeScreenSpaceError(proj, 1080, 100f, 10.0);
            float sseLarge = TileGeometryMath.ComputeScreenSpaceError(proj, 1080, 100f, 20.0);
            Assert.That(sseLarge / sseSmall, Is.EqualTo(2.0f).Within(1e-3f));
        }

        [Test]
        public void DistanceToTileClosestPoint_CameraInsideTile_ReturnsHeightOnly()
        {
            var pivot = new MercatorPivot(double2.zero);
            var bounds = new double4(0, 0, 100, 100);
            var cameraWorld = new float3(50f, 200f, 50f); // inside [0,100]x[0,100]

            float distance = TileGeometryMath.DistanceToTileClosestPoint(bounds, cameraWorld, pivot);
            Assert.That(distance, Is.EqualTo(200f).Within(1e-3f));
        }

        [Test]
        public void DistanceToTileClosestPoint_CameraOutsideTile_ReturnsClampedPlanarDistancePlusHeight()
        {
            var pivot = new MercatorPivot(double2.zero);
            var bounds = new double4(0, 0, 100, 100);
            var cameraWorld = new float3(150f, 0f, 50f); // outside along X, height 0

            float distance = TileGeometryMath.DistanceToTileClosestPoint(bounds, cameraWorld, pivot);
            Assert.That(distance, Is.EqualTo(50f).Within(1e-3f));
        }

        [Test]
        public void HorizontalDistanceToTileClosestPoint_CameraAboveTile_ReturnsZero()
        {
            var pivot = new MercatorPivot(double2.zero);
            var bounds = new double4(0, 0, 100, 100);
            var cameraWorld = new float3(50f, 200f, 50f);

            float distance = TileGeometryMath.HorizontalDistanceToTileClosestPoint(bounds, cameraWorld, pivot);
            Assert.That(distance, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void HorizontalDistanceToTileClosestPoint_CameraOutsideTile_IgnoresHeight()
        {
            var pivot = new MercatorPivot(double2.zero);
            var bounds = new double4(0, 0, 100, 100);
            var cameraWorld = new float3(150f, 200f, 50f);

            float distance = TileGeometryMath.HorizontalDistanceToTileClosestPoint(bounds, cameraWorld, pivot);
            Assert.That(distance, Is.EqualTo(50f).Within(1e-3f));
        }
    }
}
