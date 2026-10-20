using System;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.TileSelection;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tiles.TileSelection
{
    /// <summary>
    /// Tile priorities based on camera position, suitable for any camera, including free-fly and first-person:
    /// camera rotation does not change them. Importance is the distance from the camera to the closest point of the
    /// tile; load priority is the tile's screen-space size (<see cref="TileGeometryMath.ComputeScreenSpaceError"/>),
    /// so tiles whose lack of detail is most visible load first. With a tilted camera, the area kept by the budget is
    /// a sphere around the camera, a noticeable part of which is out of view.
    /// </summary>
    public sealed class CameraDistancePrioritizer : ITilePrioritizer
    {
        private readonly Camera _camera;
        private readonly IFloatingOriginController _originController;

        private MercatorPivot _pivot;
        private float3 _cameraWorld;
        private Matrix4x4 _projectionMatrix;
        private int _viewportHeightPixels;

        public CameraDistancePrioritizer(Camera camera, IFloatingOriginController originController)
        {
            _camera = camera != null ? camera : throw new ArgumentNullException(nameof(camera));
            _originController = originController ?? throw new ArgumentNullException(nameof(originController));
        }

        public void BeginFrame()
        {
            Vector3 pos = _camera.transform.position;
            _cameraWorld = new float3(pos.x, pos.y, pos.z);
            _projectionMatrix = _camera.projectionMatrix;
            _viewportHeightPixels = math.max(1, _camera.pixelHeight);
            _pivot = _originController.Current;
        }

        public float Importance(TileCoord coord) =>
            TileGeometryMath.DistanceToTileClosestPoint(coord.BoundsMeters(), _cameraWorld, _pivot);

        public float LoadPriority(TileCoord coord) =>
            -TileGeometryMath.ComputeScreenSpaceError(_projectionMatrix, _viewportHeightPixels, Importance(coord), coord.SizeMeters());
    }
}
