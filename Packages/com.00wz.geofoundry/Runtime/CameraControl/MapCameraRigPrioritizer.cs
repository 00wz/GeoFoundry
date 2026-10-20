using System;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.TileSelection;
using Unity.Mathematics;

namespace GeoFoundry.CameraControl
{
    /// <summary>
    /// Tile priorities (<see cref="ITilePrioritizer"/>) for a camera driven by <see cref="MapCameraRig"/>,
    /// depending on the camera mode:
    /// <list type="bullet">
    /// <item><see cref="CameraMode.Ground"/>: the camera orbits the target and looks straight at it, so importance
    ///   and load priority are the ground distance from the target to the nearest point of the tile. The area
    ///   kept by the budget is a circle around what the camera looks at, and camera rotation does not change it.
    ///   The rig already knows the target, so nothing has to be computed.</item>
    /// <item><see cref="CameraMode.Free"/>, and while the rig does not drive the camera: the strategy passed to
    ///   the constructor (usually <c>CameraDistancePrioritizer</c> from <c>GeoFoundry.Tiles</c>). There is no
    ///   orbit point, and priorities from the look-at point would shift the budget boundary on every turn.</item>
    /// </list>
    /// A custom camera implementation should provide its own <see cref="ITilePrioritizer"/>.
    /// </summary>
    internal sealed class MapCameraRigPrioritizer : ITilePrioritizer
    {
        private readonly MapCameraRig _rig;
        private readonly ITilePrioritizer _fallback;

        /// <summary>The current frame is in Ground mode: priorities come from <see cref="_orbitTarget"/>.</summary>
        private bool _orbit;
        private double2 _orbitTarget;

        /// <param name="fallback">
        /// Strategy used while there is no orbit target (Free mode, or the rig does not drive the camera); usually
        /// <c>CameraDistancePrioritizer</c> for the same camera.
        /// </param>
        public MapCameraRigPrioritizer(MapCameraRig rig, ITilePrioritizer fallback)
        {
            _rig = rig != null ? rig : throw new ArgumentNullException(nameof(rig));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        public void BeginFrame()
        {
            _orbit = _rig != null && _rig.IsActiveAndInitialized && _rig.Mode == CameraMode.Ground;
            if (_orbit) _orbitTarget = _rig.OrbitTargetMercator;
            else _fallback.BeginFrame();
        }

        public float Importance(TileCoord coord) => _orbit
            ? (float)MercatorBounds.FromTile(coord).DistanceTo(_orbitTarget)
            : _fallback.Importance(coord);

        public float LoadPriority(TileCoord coord) => _orbit ? Importance(coord) : _fallback.LoadPriority(coord);
    }
}
