using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Tiles.TileSelection
{
    /// <summary>
    /// Priority group of a load request: the group determines the order as a whole, the tiebreak applies only within
    /// a group (see <see cref="LoadRequest.Tiebreak"/>). Groups where loading only coarsens coverage
    /// (<see cref="Offscreen"/>, <see cref="VisibleCoarsening"/>) come after those where it fills a hole or refines
    /// the picture in its visibility area.
    /// </summary>
    internal enum LoadPriorityGroup : byte
    {
        /// <summary>
        /// A tile outside the camera frustum whose area is already covered by loaded descendants, so loading does
        /// not change the picture; it only coarsens coverage to save memory (the descendants stop being protected
        /// from eviction).
        /// </summary>
        Offscreen = 0,

        /// <summary>
        /// A tile outside the camera frustum whose coverage contains a hole — a tile skipped in the previous frame
        /// (did not fit the budget, see <see cref="TileQuadtreeTraversal"/>). Loading fills the hole before it comes
        /// into view, so it outranks plain coarsening. Within the group, most important first: they return to view
        /// sooner.
        /// </summary>
        OffscreenReentry = 1,

        /// <summary>
        /// A tile in the camera frustum, detailed enough but not loaded yet, whose area is already fully covered by
        /// finer descendants (usually after the camera zooms out). Loading only coarsens coverage to save memory, so
        /// it comes after all refinements; within the group, least important first, since their descendants will not
        /// be needed again soon.
        /// </summary>
        VisibleCoarsening = 2,

        /// <summary>
        /// The request refines the visible picture: a tile in the camera frustum, or a descendant of a visible tile
        /// (not necessarily direct, see <see cref="TileQuadtreeConfig.MaxSkippedLevels"/>) without which that tile
        /// cannot refine.
        /// </summary>
        Visible = 3,

        /// <summary>
        /// A tile in the camera frustum in whose place (fully or partially) a hole is visible now — a tile skipped in
        /// the previous frame. A hole on screen matters more than refining detail.
        /// </summary>
        VisibleReentry = 4
    }

    /// <summary>
    /// A tile load request produced by <see cref="TileQuadtreeTraversal"/> in one frame. Requests are sorted by
    /// <see cref="Group"/> descending, then by <see cref="Tiebreak"/> ascending, and projected into
    /// <see cref="TileSelectionResult.TilesToLoad"/>.
    /// </summary>
    internal readonly struct LoadRequest
    {
        public readonly TileCoord Coord;
        public readonly LoadPriorityGroup Group;

        /// <summary>
        /// Tiebreak within a group: lower loads first. In <see cref="TileQuadtreeTraversal"/> it is a metric of the
        /// priority strategy (<see cref="ITilePrioritizer"/>): load priority in view, importance rank out of view —
        /// positive in groups where the most important loads first, negative in coarsening groups
        /// (<see cref="LoadPriorityGroup.Offscreen"/>, <see cref="LoadPriorityGroup.VisibleCoarsening"/>) where the
        /// least important loads first.
        /// </summary>
        public readonly float Tiebreak;

        public LoadRequest(TileCoord coord, LoadPriorityGroup group, float tiebreak)
        {
            Coord = coord;
            Group = group;
            Tiebreak = tiebreak;
        }
    }
}
