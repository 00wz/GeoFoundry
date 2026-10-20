using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tiles.TileSelection
{
    /// <summary>
    /// Pure math of the LOD traversal (screen-space error, distance) used by <see cref="TileQuadtreeTraversal"/>, as
    /// static functions over primitives (<see cref="Matrix4x4"/>, <see cref="float3"/>, <see cref="double4"/>). They
    /// do not depend on a live scene or <see cref="Camera"/>, so they are unit-tested directly.
    /// </summary>
    internal static class TileGeometryMath
    {
        /// <summary>
        /// Returns the tile's screen-space error in pixels: the geometric error projected through the camera
        /// projection matrix at <paramref name="distance"/>. A direct port of Cesium's
        /// <c>ViewState::computeScreenSpaceError</c>; Unity's <c>Camera.projectionMatrix</c> uses the same convention
        /// of a camera looking along -Z. Do not use <c>GL.GetGPUProjectionMatrix</c>: that matrix is tied to a specific
        /// graphics API and render target and is unsuitable for this CPU-side computation.
        /// </summary>
        public static float ComputeScreenSpaceError(
            Matrix4x4 projectionMatrix, int viewportHeightPixels, float distance, double geometricError)
        {
            distance = math.max(distance, 1e-7f);

            var centerView = new Vector4(0f, 0f, -distance, 1f);
            var centerClip = projectionMatrix * centerView;
            float centerNdcY = centerClip.y / centerClip.w;

            var errorView = new Vector4(0f, (float)geometricError, -distance, 1f);
            var errorClip = projectionMatrix * errorView;
            float errorNdcY = errorClip.y / errorClip.w;

            return math.abs(errorNdcY - centerNdcY) * viewportHeightPixels * 0.5f;
        }

        /// <summary>
        /// Returns the distance from the camera to the closest point of the tile AABB (not its center, which matters
        /// for large low-zoom tiles whose center can be much farther than the nearest edge): the Mercator-plane
        /// distance combined with the height difference. Tiles lie in the Y=0 plane of the pivot coordinate system, so
        /// the camera height above a tile is simply <c>cameraWorld.y</c>.
        /// </summary>
        public static float DistanceToTileClosestPoint(double4 boundsMeters, float3 cameraWorld, MercatorPivot pivot)
        {
            double groundDist = HorizontalDistanceToTileClosestPointD(boundsMeters, cameraWorld, pivot);
            double height = cameraWorld.y;
            return (float)math.sqrt(groundDist * groundDist + height * height);
        }

        /// <summary>
        /// Returns the horizontal distance (in the Mercator plane, ignoring camera height) from the camera's projection
        /// onto the tile plane to the closest point of the tile AABB. Zero if the camera is above the tile.
        /// </summary>
        public static float HorizontalDistanceToTileClosestPoint(double4 boundsMeters, float3 cameraWorld, MercatorPivot pivot) =>
            (float)HorizontalDistanceToTileClosestPointD(boundsMeters, cameraWorld, pivot);

        private static double HorizontalDistanceToTileClosestPointD(double4 boundsMeters, float3 cameraWorld, MercatorPivot pivot) =>
            new MercatorBounds(boundsMeters.x, boundsMeters.y, boundsMeters.z, boundsMeters.w)
                .DistanceTo(pivot.MercatorFromWorld(cameraWorld));
    }
}
