using GeoFoundry.Geometry;
using GeoFoundry.OpenMapTiles;
using NUnit.Framework;
using Unity.Mathematics;

namespace GeoFoundry.Tests.EditMode.OpenMapTiles
{
    /// <summary>
    /// Line widths evaluated at the camera's pixel size: <see cref="OmtLineWidth"/>, the shader math mirrored by
    /// <see cref="OmtLineShaderMath"/> (GeoFoundryOmtLineWidth.hlsl), including the pixel size the shader evaluates at a
    /// vertex.
    /// </summary>
    public sealed class OmtLineWidthTests
    {
        [Test]
        public void LineWidth_IsTheRealWidthUpClose_AndAtLeastThePixelsFromAfar()
        {
            var width = new OmtLineWidth(10f, 2f, 1f, 0.5f);
            Assert.That(width.Evaluate(1f), Is.EqualTo(10f + 2f * 1f), "meters dominate");
            Assert.That(width.Evaluate(10f), Is.EqualTo(20f + 2f * 5f), "pixels dominate");
            Assert.That(width.Core(10f), Is.EqualTo(20f));
            Assert.That(width.ScaleCore(0.5f), Is.EqualTo(new OmtLineWidth(5f, 1f, 1f, 0.5f)), "the border is kept");
            Assert.That(new OmtLineWidth(4f).WithBorder(1f, 2f), Is.EqualTo(new OmtLineWidth(4f, 0f, 1f, 2f)));
        }

        [Test]
        public void RealWorld_UsesTheGroundMetersOfCoreAndBorders_AndScalesTheDashByTheCore()
        {
            var item = new OmtLineDrawItem(7, new OmtLineWidth(6f, 4f, 0.5f, 1f), new UnityEngine.Color32(1, 2, 3, 4),
                new float2(2f, 1f), LineDrawFlags.None, 12f);
            var real = OmtLineMesh.ToRealWidth(item);
            Assert.That(real.FeatureIndex, Is.EqualTo(7));
            Assert.That(real.WidthMeters, Is.EqualTo(7f), "6 m core + 2 · 0.5 m border, without the pixel minimums");
            Assert.That(real.Dash, Is.EqualTo(new float2(12f, 6f)));
            Assert.That(real.Color, Is.EqualTo(item.Color));
        }

        [Test]
        public void ShaderMath_Zoom_FollowsTheMapLibreConvention()
        {
            Assert.That(OmtLineShaderMath.Zoom(78271.517f), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(OmtLineShaderMath.Zoom(4.7773f), Is.EqualTo(14f).Within(1e-3f));
        }

        [Test]
        public void ShaderMath_HalfWidth_FadesInBelowTheMinZoom()
        {
            var width = new OmtLineWidth(8f);
            float mpp14 = 4.7773f; // zoom 14
            Assert.That(OmtLineShaderMath.HalfWidth(width, 14f, mpp14), Is.EqualTo(4f).Within(1e-3f), "visible from the min zoom");
            Assert.That(OmtLineShaderMath.HalfWidth(width, 14.25f, mpp14), Is.EqualTo(2f).Within(1e-2f), "half faded");
            Assert.That(OmtLineShaderMath.HalfWidth(width, 14.5f, mpp14), Is.EqualTo(0f).Within(1e-3f), "hidden");
            Assert.That(OmtLineShaderMath.HalfWidth(width, 0f, mpp14), Is.EqualTo(4f).Within(1e-3f), "no min zoom");
        }

        [Test]
        public void ShaderMath_Offset_MovesAlongTheExtrusion_OnTheGroundTangentPlane()
        {
            // Built at half width 4, shown at half width 10: an edge vertex (extrusion 1 across +Z) moves by 6.
            var flat = OmtLineShaderMath.Offset(new float2(0f, 1f), 4f, 10f, new float3(0f, 1f, 0f));
            Assert.That(flat.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(flat.y, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(flat.z, Is.EqualTo(6f).Within(1e-5f));

            // The axis does not move; narrower than built moves inward.
            Assert.That(math.length(OmtLineShaderMath.Offset(float2.zero, 4f, 10f, new float3(0f, 1f, 0f))), Is.EqualTo(0f));
            Assert.That(OmtLineShaderMath.Offset(new float2(0f, 1f), 4f, 1f, new float3(0f, 1f, 0f)).z, Is.EqualTo(-3f).Within(1e-5f));

            // Ground rising toward +Z with slope 0.5: the moved vertex rises 3 m over 6 m.
            var n = math.normalize(new float3(0f, 1f, -0.5f));
            var sloped = OmtLineShaderMath.Offset(new float2(0f, 1f), 4f, 10f, n);
            Assert.That(sloped.y, Is.EqualTo(3f).Within(1e-4f));
        }

        [Test]
        public void MetersPerPixel_TopDown_IsTheViewHeightOverThePixels()
        {
            float mpp = OmtLineShaderMath.MetersPerPixel(1000f, new float3(0f, -1f, 0f), false, math.tan(math.radians(30f)), 0f, 1080);
            Assert.That(mpp, Is.EqualTo(2f * 1000f * math.tan(math.radians(30f)) / 1080f).Within(1e-5f));
        }

        [Test]
        public void MetersPerPixel_Pitched_UsesTheDistanceAlongTheViewToTheVertexPlane()
        {
            var forward = math.normalize(new float3(0f, -1f, 1f)); // 45°
            float mpp = OmtLineShaderMath.MetersPerPixel(1000f, forward, false, math.tan(math.radians(30f)), 0f, 1080);
            float distance = 1000f * math.SQRT2;
            Assert.That(mpp, Is.EqualTo(2f * distance * math.tan(math.radians(30f)) / 1080f).Within(1e-4f));
        }

        [Test]
        public void MetersPerPixel_TowardTheHorizon_IsLimitedToTheMaxPitch()
        {
            float level = OmtLineShaderMath.MetersPerPixel(100f, new float3(0f, 0f, 1f), false, math.tan(math.radians(30f)), 0f, 1080);
            float up = OmtLineShaderMath.MetersPerPixel(100f, new float3(0f, 0.5f, 0.866f), false, math.tan(math.radians(30f)), 0f, 1080);
            float distance = 100f / math.cos(math.radians(OmtLineShaderMath.MaxPitchDegrees));
            Assert.That(level, Is.EqualTo(2f * distance * math.tan(math.radians(30f)) / 1080f).Within(1e-4f));
            Assert.That(up, Is.EqualTo(level), "looking up is treated like the horizon");
        }

        [Test]
        public void MetersPerPixel_BelowTheGround_UsesOneMeter_AndOrthographicUsesItsSize()
        {
            float below = OmtLineShaderMath.MetersPerPixel(-5f, new float3(0f, -1f, 0f), false, math.tan(math.radians(30f)), 0f, 1080);
            Assert.That(below, Is.EqualTo(2f * math.tan(math.radians(30f)) / 1080f).Within(1e-6f));
            Assert.That(OmtLineShaderMath.MetersPerPixel(0f, default, true, 0f, 540f, 1080), Is.EqualTo(1f));
        }
    }
}
