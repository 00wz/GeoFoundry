using System;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeoFoundry.Render
{
    /// <summary>
    /// Publishes per-camera view parameters to the shaders before each camera renders:
    /// <c>_GeoFoundryMetersPerPixel</c>, the Web Mercator meters per screen pixel at the center of the view, with which
    /// the <c>GeoFoundry/Line Lit</c> shader evaluates line widths (see <c>GeoFoundry.Geometry.LineWidth</c>), and
    /// <c>_GeoFoundryDepthOcclusion</c>, 1 if the camera renders the URP depth texture, with which the
    /// <c>GeoFoundry/Label</c> shader hides labels whose anchor is occluded (without it the shader falls back to a
    /// per-pixel depth test).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the scene-wide zoom of MapLibre and Mapbox (<c>transform.zoom</c>): one pixel size for all tiles, so a
    /// line has the same world width on every tile whatever its zoom, including lower-zoom tiles shown close up while
    /// the detailed ones load, and perspective thins distant lines.
    /// </para>
    /// <para>
    /// <c>GeoFoundryBootstrapper</c> creates one; a custom bootstrap creates it after the ground height provider,
    /// calls <see cref="Enable"/> and disposes it on shutdown. Every camera rendered through the render pipeline gets
    /// its own values (game cameras, the Scene view).
    /// </para>
    /// </remarks>
    public sealed class MapViewShaderGlobals : IDisposable
    {
        /// <summary>Name of the global shader property.</summary>
        public const string MetersPerPixelProperty = "_GeoFoundryMetersPerPixel";

        /// <summary>
        /// The steepest view angle from the vertical used for the distance to the ground, as in MapLibre (maximum
        /// pitch 85°): a camera looking at the horizon gets the pixel size at that angle.
        /// </summary>
        public const float MaxPitchDegrees = 85f;

        /// <summary>Name of the global shader property telling whether the camera depth texture is available.</summary>
        public const string DepthOcclusionProperty = "_GeoFoundryDepthOcclusion";

        private static readonly int MetersPerPixelId = Shader.PropertyToID(MetersPerPixelProperty);
        private static readonly int DepthOcclusionId = Shader.PropertyToID(DepthOcclusionProperty);

        private readonly IFloatingOriginController _origin;
        private readonly IGroundHeightProvider _heights;
        private bool _enabled;

        /// <param name="origin">The floating origin, to find the ground below the camera.</param>
        /// <param name="heights">Ground heights; without them the ground is at height 0.</param>
        public MapViewShaderGlobals(IFloatingOriginController origin, IGroundHeightProvider heights)
        {
            _origin = origin ?? throw new ArgumentNullException(nameof(origin));
            _heights = heights;
        }

        /// <summary>Starts publishing the pixel size before each camera renders.</summary>
        public void Enable()
        {
            if (_enabled) return;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            _enabled = true;
        }

        /// <summary>
        /// Stops publishing and resets the globals to 0 (lines then render at their built width, labels use the
        /// depth-buffer test).
        /// </summary>
        public void Dispose()
        {
            if (!_enabled) return;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Shader.SetGlobalFloat(MetersPerPixelId, 0f);
            Shader.SetGlobalFloat(DepthOcclusionId, 0f);
            _enabled = false;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            Shader.SetGlobalFloat(MetersPerPixelId, ComputeMetersPerPixel(camera));
            Shader.SetGlobalFloat(DepthOcclusionId, HasDepthTexture(camera) ? 1f : 0f);
        }

        /// <summary>
        /// Returns whether URP renders the depth texture for <paramref name="camera"/>: its own setting (which
        /// defaults to the pipeline asset's Depth Texture), the asset's setting for cameras without URP data, and
        /// always for the Scene view.
        /// </summary>
        public static bool HasDepthTexture(Camera camera)
        {
            if (camera.cameraType == CameraType.SceneView) return true;
            if (camera.TryGetComponent<UniversalAdditionalCameraData>(out var data)) return data.requiresDepthTexture;
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset &&
                   asset.supportsCameraDepthTexture;
        }

        /// <summary>Returns the meters per screen pixel at the center of the view of <paramref name="camera"/>.</summary>
        public float ComputeMetersPerPixel(Camera camera)
        {
            var t = camera.transform;
            float3 position = t.position;
            float groundHeight = 0f;
            if (_heights != null && _heights.TryGetHeight(_origin.Current.MercatorFromWorld(position), out float h))
                groundHeight = h;

            return ComputeMetersPerPixel(position.y - groundHeight, t.forward, camera.orthographic,
                camera.fieldOfView, camera.orthographicSize, camera.pixelHeight);
        }

        /// <summary>
        /// Returns the meters per screen pixel at the center of the view. For a perspective camera, the distance to
        /// the center is measured to the horizontal ground plane below the camera along the view direction, with the
        /// view angle limited to <see cref="MaxPitchDegrees"/> from the vertical and the altitude to at least 1 m.
        /// </summary>
        /// <param name="altitude">Height of the camera above the ground below it.</param>
        /// <param name="forward">View direction (unit).</param>
        /// <param name="orthographic">Whether the camera is orthographic.</param>
        /// <param name="fieldOfViewDegrees">Vertical field of view of a perspective camera.</param>
        /// <param name="orthographicSize">Half the view height of an orthographic camera.</param>
        /// <param name="pixelHeight">Height of the view in pixels.</param>
        public static float ComputeMetersPerPixel(float altitude, float3 forward, bool orthographic,
            float fieldOfViewDegrees, float orthographicSize, int pixelHeight)
        {
            float pixels = math.max(1, pixelHeight);
            if (orthographic) return 2f * orthographicSize / pixels;

            float down = math.max(-forward.y, math.cos(math.radians(MaxPitchDegrees)));
            float distance = math.max(altitude, 1f) / down;
            return 2f * distance * math.tan(math.radians(fieldOfViewDegrees) * 0.5f) / pixels;
        }
    }
}
