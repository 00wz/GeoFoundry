using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Core.TileSelection
{
    /// <summary>
    /// Tile priority strategy used by tile selection (<c>TileQuadtreeTraversal</c> in <c>GeoFoundry.Tiles</c>):
    /// the importance of a tile (the budget uses it to decide which tiles to keep) and its load priority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both metrics live in one object, so that keeping them consistent is the job of a single class and the frame
    /// state is captured once. The strategy holds everything it needs itself (the camera, the camera controller).
    /// </para>
    /// <para>
    /// The interface lives in <c>GeoFoundry.Core</c> so that a module unaware of tile selection, such as a camera
    /// controller (<c>MapCameraRigPrioritizer</c> in <c>GeoFoundry.CameraControl</c>), can implement it, while the
    /// composition root (<c>GeoFoundryBootstrapper</c>) wires them together. All members are called on the main
    /// thread.
    /// </para>
    /// </remarks>
    public interface ITilePrioritizer
    {
        /// <summary>
        /// Captures the frame state (camera pose, camera controller mode and so on). Called once per frame, before
        /// <see cref="Importance"/> and <see cref="LoadPriority"/>.
        /// </summary>
        void BeginFrame();

        /// <summary>
        /// Returns the importance rank of a tile: a finite non-negative number, lower is more important.
        /// </summary>
        /// <remarks>
        /// It orders <c>TileSelectionResult.ByImportance</c>. It must be non-negative for budget hysteresis, which
        /// divides the rank by <c>TileQuadtreeConfig.BudgetHysteresis</c> &lt; 1 to push a tile back. It also orders
        /// loads of tiles outside the view: they do not affect the image, and what matters is which of them returns
        /// to the view, and to the budget, sooner. An ancestor should be at least as important as its descendants
        /// (distance metrics satisfy this); otherwise the budget may admit descendants while denying the ancestor.
        /// </remarks>
        float Importance(TileCoord coord);

        /// <summary>
        /// Returns the load priority of a tile that affects the image in the current view: lower loads sooner. Any
        /// sign is allowed (see <c>LoadRequest.Tiebreak</c>).
        /// </summary>
        float LoadPriority(TileCoord coord);
    }
}
