using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace GeoFoundry.Tiles.TileSelection
{
    /// <summary>
    /// Settings of the quadtree LOD traversal (<see cref="TileQuadtreeTraversal"/>). Serialized as part of
    /// <c>GeoFoundryConfig</c>; the traversal holds a reference to the instance, so inspector edits apply live.
    /// The defaults make the typical selected zoom at the default camera distance
    /// (<c>GeoFoundryConfig.OriginCameraDistance</c> = 1500 meters) roughly zoom 14.
    /// </summary>
    [Serializable]
    public sealed class TileQuadtreeConfig
    {
        /// <summary>
        /// Threshold of a tile's on-screen size in pixels: while the tile's projection (its
        /// <see cref="GeoFoundry.Core.Coordinates.TileCoord.SizeMeters"/> through the camera, see
        /// <see cref="TileGeometryMath.ComputeScreenSpaceError"/>) is larger, the tile is refined further; once it is
        /// smaller, the tile is detailed enough. This is the main control of LOD density and quality.
        ///
        /// The default comes from the small-angle approximation
        /// (<c>screenSize ≈ tileSizeMeters * K / distance</c>, where <c>K = viewportHeightPx / (2*tan(fov/2))</c>): at
        /// 1500 meters (the default <c>GeoFoundryConfig.OriginCameraDistance</c>), a 1080 px viewport and 60° FOV
        /// (K ≈ 935.5), a Z=14 tile (SizeMeters ≈ 2447 meters) gives screenSize ≈ 1526 px, rounded to 1500.
        /// </summary>
        [Tooltip("Threshold of a tile's on-screen size in pixels: while the tile's projection is larger, it is refined further. The main control of LOD density and quality.")]
        [Min(1f)] public float MaxTileScreenSizePixels = 1500f;

        /// <summary>Maximum zoom of the traversal; it does not refine deeper (set to the tile source's maxzoom).</summary>
        [Tooltip("Maximum zoom of the LOD traversal; it does not refine deeper (= the tile source's maxzoom).")]
        [Range(0, 22)] public int MaxZoom = 14;

        /// <summary>
        /// Level skipping: how many intermediate levels loading the descendants of an insufficiently detailed tile may
        /// skip (see <see cref="TileQuadtreeTraversal"/>). Descendants of the required level (detailed enough or
        /// outside the frustum) are requested directly, but no deeper than <c>MaxSkippedLevels + 1</c> levels below
        /// the tile. 0 requests only direct children, refining one level per pass.
        ///
        /// A tile produces no more requests than there are target tiles under it (the traversal goes deeper only into
        /// tiles that are in view and not detailed enough), so large values do not inflate the per-frame cost. But a
        /// placeholder (the rendered parent) is replaced only when its whole subtree is covered: the larger the value,
        /// the longer the coarse tile stays visible and the more abruptly detail appears (like a large
        /// <c>QuadtreePrimitive.loadingDescendantLimit</c> in Cesium); and as the camera moves, more in-flight loads
        /// become unnecessary and are wasted.
        /// </summary>
        [Tooltip("How many intermediate levels of detail loading descendants may skip: descendants of the required level load directly, but no deeper than MaxSkippedLevels+1 levels. 0 means direct children only. Larger values: fewer loads, but detail appears all at once after a longer wait, and more loads are wasted while the camera moves.")]
        [Range(0, 22)] public int MaxSkippedLevels = 1;

        /// <summary>
        /// Vertical margin, in meters, of the tile AABB for frustum culling: the actual height of a tile's terrain and
        /// buildings is unknown until it loads, so a margin is added above and below.
        /// </summary>
        [Tooltip("Vertical margin (meters) of the tile AABB for frustum culling; the actual terrain height is unknown until the tile loads.")]
        [Min(0f)] public float CullBoundsHeightMargin = 1000f;

        /// <summary>
        /// Tile budget hysteresis (see <see cref="TileQuadtreeTraversal"/>), in (0, 1]: when ranking tiles by
        /// importance (<see cref="ITilePrioritizer.Importance"/>), the rank of a tile skipped in the previous frame is
        /// divided by this value, so to return to the budget it must be noticeably more important than its
        /// competitors. Values close to 1 only prevent flicker from camera smoothing and frame-to-frame jitter.
        /// </summary>
        [Tooltip("Tile budget hysteresis: the importance rank of a tile skipped in the previous frame is divided by this value when ranking. Values close to 1 only prevent flicker.")]
        [FormerlySerializedAs("SkipHysteresis")]
        [Range(0.5f, 1f)] public float BudgetHysteresis = 0.98f;
    }
}
