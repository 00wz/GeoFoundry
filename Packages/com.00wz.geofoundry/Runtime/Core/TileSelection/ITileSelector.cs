using System;
using System.Collections.Generic;
using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Core.TileSelection
{
    /// <summary>
    /// Tile selection contract: once per frame, based on the current load state, decides what to show, what to
    /// load and what to keep in memory, and how important each of those tiles is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Runtime.TileLifecycleManager"/> holds only this interface and does not depend on a specific
    /// algorithm (see <see cref="TileQuadtreeTraversal"/>, an LOD traversal of the quadtree). Custom scenarios
    /// (a fixed set of tiles, showing tiles while they are being generated and so on) implement the interface
    /// directly.
    /// </para>
    /// <para>
    /// From the manager the selector receives only the tile state (<see cref="TileFrameInput"/>). Everything else a
    /// specific algorithm needs (the camera, the floating origin, the priority strategy) the implementation
    /// receives at construction (see <c>GeoFoundryBootstrapper</c>); the manager does not need to know about the
    /// camera.
    /// </para>
    /// <para>
    /// The budget is not the selector's concern: it returns the desired set without limits, and the manager
    /// admits the prefix of it by importance (<see cref="TileSelectionResult.ByImportance"/>) that fits the budget.
    /// Denied tiles are not shown, protected or loaded; the manager reports them to the selector in the next frame
    /// (<see cref="TileFrameInput.DeniedLastFrame"/>).
    /// </para>
    /// </remarks>
    public interface ITileSelector
    {
        /// <summary>
        /// Selects tiles for the current frame. Called once per frame on the main thread.
        /// </summary>
        /// <param name="input">The tile state; its collections must not be modified.</param>
        /// <param name="result">
        /// A buffer owned by the caller; the implementation must <see cref="TileSelectionResult.Clear"/> it and
        /// fill it again.
        /// </param>
        /// <returns>
        /// False if there is nothing to select this frame (for example, the camera is destroyed):
        /// <paramref name="result"/> is left untouched and the manager skips the frame, so the previous selection
        /// stays in effect and <see cref="TileFrameInput.FailedSinceLastFrame"/> is delivered in the next call.
        /// </returns>
        bool SelectTiles(in TileFrameInput input, TileSelectionResult result);
    }

    /// <summary>
    /// Input of <see cref="ITileSelector.SelectTiles"/> for one frame: everything the manager knows about tiles.
    /// The collections are owned by the manager and valid only for the duration of the call.
    /// </summary>
    public readonly struct TileFrameInput
    {
        private static readonly TileCoord[] Empty = Array.Empty<TileCoord>();

        /// <summary>
        /// Snapshot of successfully loaded tiles (with content, both active and in the warm cache). Tiles still
        /// loading are not included.
        /// </summary>
        public readonly IReadOnlyCollection<TileCoord> LoadedTiles;

        /// <summary>
        /// Snapshot, not events: all tiles the budget denied in the previous frame, not only evicted ones.
        /// </summary>
        /// <remarks>
        /// The budget re-decides over the whole <see cref="TileSelectionResult.ByImportance"/> every frame, so while
        /// the desired set does not fit, tiles beyond the boundary are denied every frame, including tiles that were
        /// never loaded. A denied tile was not rendered, protected or loaded in the previous frame (and may already
        /// be evicted).
        /// </remarks>
        public readonly IReadOnlyCollection<TileCoord> DeniedLastFrame;

        /// <summary>
        /// Events: tiles whose load failed (not cancelled) since the previous call.
        /// </summary>
        /// <remarks>
        /// The manager accumulates them only until the next call and does not store, retry or ban failed tiles
        /// itself: if the selector includes such a tile in <see cref="TileSelectionResult.TilesToLoad"/> again, the
        /// manager tries to load it again. How to handle the failure (whether and when to retry, whether to treat
        /// the tile as empty for coverage) is entirely up to the implementation.
        /// </remarks>
        public readonly IReadOnlyCollection<TileCoord> FailedSinceLastFrame;

        /// <summary>Initializes the input; null collections are replaced with empty ones.</summary>
        public TileFrameInput(
            IReadOnlyCollection<TileCoord> loadedTiles,
            IReadOnlyCollection<TileCoord> deniedLastFrame = null,
            IReadOnlyCollection<TileCoord> failedSinceLastFrame = null)
        {
            LoadedTiles = loadedTiles ?? Empty;
            DeniedLastFrame = deniedLastFrame ?? Empty;
            FailedSinceLastFrame = failedSinceLastFrame ?? Empty;
        }
    }

    /// <summary>
    /// Result of <see cref="ITileSelector.SelectTiles"/>. Everything in <see cref="TilesToRender"/>,
    /// <see cref="TilesToLoad"/> or <see cref="TilesToKeep"/> is the desired set; tiles need not be duplicated
    /// across collections. The manager protects from eviction only those tiles of the desired set that the budget
    /// admitted (see <see cref="ByImportance"/>).
    /// </summary>
    public sealed class TileSelectionResult
    {
        /// <summary>
        /// Tiles that must be active (rendered) right now. The manager syncs the activity of both loaded tiles and
        /// tiles still being generated with this set, except for tiles the budget denied.
        /// </summary>
        public readonly HashSet<TileCoord> TilesToRender = new();

        /// <summary>
        /// Tiles the selector wants in memory, in descending load priority. Means "make sure it is loaded": the
        /// manager does not reload tiles that are already loaded or loading. It starts loads of admitted tiles in
        /// this order until it reaches the concurrent load limit.
        /// </summary>
        public readonly List<TileCoord> TilesToLoad = new(64);

        /// <summary>
        /// Optional: additional tiles to protect from eviction although they are neither rendered nor loaded (for
        /// example, loaded tiles currently outside the frustum, to show them instantly). Implementations that do
        /// not need this may leave it empty.
        /// </summary>
        public readonly HashSet<TileCoord> TilesToKeep = new();

        /// <summary>
        /// Every tile of <c>TilesToRender ∪ TilesToLoad ∪ TilesToKeep</c> exactly once, from most to least
        /// important.
        /// </summary>
        /// <remarks>
        /// Only the order matters, not numeric values: the manager admits the prefix of this list that fits the
        /// budget and denies the rest. The implementation must keep the order deterministic (tiebreaks); otherwise
        /// the budget boundary flickers.
        /// </remarks>
        public readonly List<TileCoord> ByImportance = new(64);

        /// <summary>Clears all collections.</summary>
        public void Clear()
        {
            TilesToRender.Clear();
            TilesToLoad.Clear();
            TilesToKeep.Clear();
            ByImportance.Clear();
        }
    }
}
