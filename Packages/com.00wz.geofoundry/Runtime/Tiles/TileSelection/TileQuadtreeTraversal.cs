using System;
using System.Collections.Generic;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.TileSelection;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tiles.TileSelection
{
    /// <summary>
    /// LOD tile selection by traversing the slippy-map quadtree (a single global root at Z=0, pure replace
    /// refinement, one camera), built on the notion of a covered tile (see <see cref="TileCoverage"/>): a tile is
    /// covered if it is resolved, skipped, or all 4 of its children are covered.
    ///
    /// Resolved here means <c>loadedTiles ∪ </c><see cref="_failedTiles"/> (see <see cref="SelectTiles"/> and
    /// <see cref="_resolvedScratch"/>). The manager reports failed loads in the frame input
    /// (<see cref="TileFrameInput.FailedSinceLastFrame"/>); a failed tile is kept while it is in
    /// <c>TilesToRender</c> or <c>TilesToKeep</c> and forgotten as soon as it is no longer needed (it is requested
    /// again the next time it is). For coverage, a failed tile counts the same as a loaded one; otherwise a single
    /// load error would block the parent's coverage forever. Having no content, it renders as empty space in
    /// <see cref="TileSelectionResult.TilesToRender"/> or <c>TilesToKeep</c>, which is the correct result for a
    /// tile known to have no data. While a failed tile is kept, this implementation deliberately does not request
    /// it again (load requests are made only for unresolved tiles); active retrying is left to more specialized
    /// <see cref="ITileSelector"/> implementations.
    ///
    /// Skipped means denied by the manager's budget (see <see cref="TileFrameInput.DeniedLastFrame"/>): the tile is
    /// not loaded or kept in memory and leaves a hole, but still covers its area for coverage purposes. The state
    /// lives across frames in <see cref="_lastFrameSkippedTiles"/> and <see cref="_currentFrameSkippedTiles"/>: a
    /// frame's base skipped set is the tiles denied in the previous frame plus carried-over tiles that coverage
    /// still depends on (e.g. a tile readmitted by the budget but not loaded yet; without carrying it over, its
    /// parent would lose coverage and the picture would fall back to coarse tiles up to the root). Anything the
    /// traversal does not reach is forgotten automatically, with no separate cleanup.
    ///
    /// Key property: if the root is covered, only resolved tiles are rendered, and together with skipped ones they
    /// fill the whole map without gaps. Holes appear only where tiles did not fit the budget, regardless of the
    /// order and speed of loading. While the root is not covered (at startup), there is nothing to render; the root
    /// and its descendants of the required level are loaded (see <see cref="RequestUncoveredRoot"/>).
    ///
    /// Each frame (<see cref="SelectTiles"/>) rebuilds the covered set and then traverses recursively from the
    /// root; only covered tiles enter <see cref="VisitTile"/>:
    /// <list type="bullet">
    /// <item>outside the frustum — kept in cache (<see cref="TileSelectionResult.TilesToKeep"/>) to appear
    ///   instantly when it comes into view: the tile itself if resolved, otherwise all tiles covering it (otherwise
    ///   evicting any of them could uncover everything up to the root); the tile is also requested for loading;</item>
    /// <item>detailed enough (<see cref="TileQuadtreeConfig.MaxTileScreenSizePixels"/>, or a leaf at
    ///   <see cref="TileQuadtreeConfig.MaxZoom"/>) — rendered itself if resolved, otherwise its covering tiles are
    ///   rendered while it loads;</item>
    /// <item>too coarse — if all 4 children are covered, the traversal recurses into them; otherwise the tile is
    ///   either resolved and rendered, or was skipped and stays skipped (a hole) and is requested for loading. In both
    ///   cases the descendants needed to replace it are requested by the load traversal
    ///   (<see cref="RequestSubtree"/>).</item>
    /// </list>
    ///
    /// The load traversal skips levels of detail: it requests descendants of the required level directly (detailed
    /// enough or outside the frustum), bypassing intermediate levels, but no deeper than
    /// <see cref="TileQuadtreeConfig.MaxSkippedLevels"/> + 1 levels. Intermediate levels are then never loaded:
    /// <see cref="TileCoverage"/> propagates coverage through them, and the traversal descends through them to the
    /// loaded descendants. A part of the subtree already covered only by resolved tiles is ready to replace the
    /// parent: its covering tiles are protected in <c>TilesToKeep</c>, and the traversal goes no deeper.
    ///
    /// A skipped tile requests both itself and its descendants, and the budget decides which level to load. This
    /// way the traversal does not assume that a parent is always more important than its descendants: with a metric
    /// where it is not, important descendants are still offered for loading, and the budget edge produces neither a
    /// dead end (a hole into which no tile is offered) nor a load cycle. Only unresolved tiles go into
    /// <see cref="TileSelectionResult.TilesToLoad"/>, including those already loading: the selector does not know
    /// about in-flight loads, and the manager does not start them twice. Resolved tiles are kept via
    /// <c>TilesToRender</c> and <c>TilesToKeep</c>.
    ///
    /// The budget belongs to the manager. The selector only orders the union
    /// <c>TilesToRender ∪ TilesToLoad ∪ TilesToKeep</c> by importance (<see cref="BuildImportanceOrder"/>); the
    /// manager admits the prefix that fits the budget, and denied tiles become skipped in the next frame. Detail is
    /// not coarsened; the least important tiles are sacrificed instead.
    ///
    /// The priority strategy (<see cref="ITilePrioritizer"/>, <see cref="CameraDistancePrioritizer"/> by default)
    /// decides how tiles compare: the importance rank orders tiles for the budget
    /// (<see cref="TileSelectionResult.ByImportance"/>) and for offscreen loads; the load priority orders visible
    /// requests within a priority group (<see cref="LoadPriorityGroup"/>, see <see cref="AddLoadRequest"/>). The LOD
    /// criterion (whether a tile is detailed enough) does not depend on the strategy; it is always the screen-space
    /// size. The traversal gets the camera and floating origin at construction, not from the manager (see
    /// <see cref="ITileSelector"/>).
    /// </summary>
    public sealed class TileQuadtreeTraversal : ITileSelector
    {
        /// <summary>A tile with its importance rank (see <see cref="BuildImportanceOrder"/>).</summary>
        private readonly struct RankedTile
        {
            public readonly TileCoord Coord;

            /// <summary>Importance rank (<see cref="ITilePrioritizer.Importance"/>) adjusted for hysteresis; lower is more important.</summary>
            public readonly float Rank;

            public RankedTile(TileCoord coord, float rank)
            {
                Coord = coord;
                Rank = rank;
            }
        }

        private readonly TileQuadtreeConfig _config;
        private readonly TileCoverage _coverage = new();
        private readonly List<LoadRequest> _loadRequests = new(64);
        private readonly Plane[] _frustumPlanes = new Plane[6];

        /// <summary>
        /// Tiles whose load failed (see <see cref="TileFrameInput.FailedSinceLastFrame"/>). Kept while they are in
        /// <c>TilesToRender</c> or <c>TilesToKeep</c>; the rest are removed at the end of <see cref="SelectTiles"/>.
        /// </summary>
        private readonly HashSet<TileCoord> _failedTiles = new();

        /// <summary>
        /// <c>loadedTiles ∪ </c><see cref="_failedTiles"/> of the current frame: what counts as resolved for coverage
        /// and reloading. Rebuilt once per frame in <see cref="SelectTiles"/>.
        /// </summary>
        private readonly HashSet<TileCoord> _resolvedScratch = new();

        /// <summary>
        /// Tiles skipped in the previous frame: carried over and denied by the budget (see the class docs). The second
        /// base coverage set of the current frame and the state for budget hysteresis (see
        /// <see cref="BuildImportanceOrder"/>). May overlap <see cref="_resolvedScratch"/>; resolved wins (see
        /// <see cref="TileCoverage.CollectCovering"/>).
        /// </summary>
        private HashSet<TileCoord> _lastFrameSkippedTiles = new();

        /// <summary>Tiles skipped in the current frame. Becomes <see cref="_lastFrameSkippedTiles"/> at the start of the next frame.</summary>
        private HashSet<TileCoord> _currentFrameSkippedTiles = new();

        /// <summary>Scratch: resolved part of the covering tiles (see <see cref="TileCoverage.CollectCovering"/>).</summary>
        private readonly HashSet<TileCoord> _coveringResolvedScratch = new();

        /// <summary>Scratch: skipped (contentless) part of the covering tiles (see <see cref="TileCoverage.CollectCovering"/>).</summary>
        private readonly HashSet<TileCoord> _coveringSkippedScratch = new();

        /// <summary><see cref="BuildImportanceOrder"/> scratch: the union Render ∪ Load ∪ Keep without duplicates.</summary>
        private readonly HashSet<TileCoord> _unionScratch = new();

        /// <summary><see cref="BuildImportanceOrder"/> scratch: union tiles with their ranks.</summary>
        private readonly List<RankedTile> _rankedScratch = new(64);

        /// <summary>Cached predicate for pruning <see cref="_failedTiles"/>, avoiding a closure allocation every frame.</summary>
        private readonly Predicate<TileCoord> _isNoLongerProtected;

        private static readonly Comparison<RankedTile> CompareByImportance = (a, b) =>
        {
            int byRank = a.Rank.CompareTo(b.Rank);
            if (byRank != 0) return byRank;
            // Deterministic tie-break: with equal ranks (e.g. a parent and child sharing the closest point) the order
            // must not depend on hash set enumeration order, or the budget edge would flicker between frames.
            int byZ = a.Coord.Z.CompareTo(b.Coord.Z);
            if (byZ != 0) return byZ;
            int byX = a.Coord.X.CompareTo(b.Coord.X);
            return byX != 0 ? byX : a.Coord.Y.CompareTo(b.Coord.Y);
        };

        private readonly Camera _camera;
        private readonly IFloatingOriginController _originController;
        private readonly ITilePrioritizer _prioritizer;

        private TileSelectionResult _result;
        private MercatorPivot _pivot;
        private float3 _cameraWorld;
        private Matrix4x4 _projectionMatrix;
        private int _viewportHeightPixels;

        /// <summary>
        /// Prioritized requests of the current frame (sorted the same way as
        /// <see cref="TileSelectionResult.TilesToLoad"/>), for debugging and tests: checking a tile's priority group.
        /// </summary>
        internal IReadOnlyList<LoadRequest> CurrentLoadRequests => _loadRequests;

        /// <summary>Gets the failed tiles the selector currently keeps, for debugging and tests.</summary>
        public IReadOnlyCollection<TileCoord> FailedTiles => _failedTiles;

        /// <summary>Gets the tiles skipped in the last frame, for debugging and tests.</summary>
        public IReadOnlyCollection<TileCoord> SkippedTiles => _currentFrameSkippedTiles;

        /// <summary>Creates a selector for the given camera.</summary>
        /// <param name="config">Traversal settings.</param>
        /// <param name="camera">The camera tiles are selected for.</param>
        /// <param name="originController">The floating origin in whose coordinates the camera is placed.</param>
        /// <param name="prioritizer">Priority strategy; <c>null</c> for a <see cref="CameraDistancePrioritizer"/> for this camera.</param>
        public TileQuadtreeTraversal(
            TileQuadtreeConfig config, Camera camera, IFloatingOriginController originController,
            ITilePrioritizer prioritizer = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _camera = camera != null ? camera : throw new ArgumentNullException(nameof(camera));
            _originController = originController ?? throw new ArgumentNullException(nameof(originController));
            _prioritizer = prioritizer ?? new CameraDistancePrioritizer(camera, originController);
            _isNoLongerProtected = c => !_result.TilesToRender.Contains(c) && !_result.TilesToKeep.Contains(c);
        }

        /// <inheritdoc/>
        /// <remarks>Returns <c>false</c> only if the camera has been destroyed.</remarks>
        public bool SelectTiles(in TileFrameInput input, TileSelectionResult result)
        {
            if (_camera == null) return false;

            result.Clear();
            _loadRequests.Clear();
            _result = result;

            Vector3 pos = _camera.transform.position;
            _cameraWorld = new float3(pos.x, pos.y, pos.z);
            _projectionMatrix = _camera.projectionMatrix;
            _viewportHeightPixels = math.max(1, _camera.pixelHeight);
            _pivot = _originController.Current;
            GeometryUtility.CalculateFrustumPlanes(_camera, _frustumPlanes);
            _prioritizer.BeginFrame();

            // Skipped tiles of the previous frame: carried over by the traversal and denied by the manager's budget.
            (_lastFrameSkippedTiles, _currentFrameSkippedTiles) = (_currentFrameSkippedTiles, _lastFrameSkippedTiles);
            _currentFrameSkippedTiles.Clear();
            _lastFrameSkippedTiles.UnionWith(input.DeniedLastFrame);

            _failedTiles.UnionWith(input.FailedSinceLastFrame);

            // Failed tiles count as resolved just like loaded ones (see the class docs); for TileCoverage this is one
            // combined set.
            _resolvedScratch.Clear();
            _resolvedScratch.UnionWith(input.LoadedTiles);
            _resolvedScratch.UnionWith(_failedTiles);
            _coverage.Rebuild(_resolvedScratch, _lastFrameSkippedTiles);

            // The single global root TileCoord(0,0,0) covers the whole Mercator extent.
            var root = new TileCoord(0, 0, 0);
            if (_coverage.IsCovered(root)) VisitTile(root);
            else RequestUncoveredRoot(root);

            _loadRequests.Sort(CompareLoadRequests);
            foreach (var req in _loadRequests) result.TilesToLoad.Add(req.Coord);
            BuildImportanceOrder();

            // Resolved tiles go only into Render/Keep (never TilesToLoad), so a failed tile outside them is no longer
            // needed. If it is needed again, it will be requested again.
            _failedTiles.RemoveWhere(_isNoLongerProtected);

            _result = null;
            return true;
        }

        private static int CompareLoadRequests(LoadRequest a, LoadRequest b)
        {
            int byGroup = ((int)b.Group).CompareTo((int)a.Group); // higher group first
            return byGroup != 0 ? byGroup : a.Tiebreak.CompareTo(b.Tiebreak);
        }

        /// <summary>
        /// Fills <see cref="TileSelectionResult.ByImportance"/> with the union
        /// <c>TilesToRender ∪ TilesToLoad ∪ TilesToKeep</c> ordered by importance rank
        /// (<see cref="ITilePrioritizer.Importance"/>, lower is more important). The manager admits the prefix that
        /// fits the budget; denied tiles become skipped in the next frame, so their area stays covered and the
        /// ancestors' coverage does not collapse after they are evicted.
        ///
        /// Hysteresis: the rank of a tile skipped in the previous frame (including one denied by the budget) is
        /// divided by <see cref="TileQuadtreeConfig.BudgetHysteresis"/>; otherwise tiles at the budget edge would swap
        /// places on the slightest camera movement, and a denied tile would be evicted and immediately reloaded.
        /// </summary>
        private void BuildImportanceOrder()
        {
            _unionScratch.Clear();
            _unionScratch.UnionWith(_result.TilesToRender);
            _unionScratch.UnionWith(_result.TilesToKeep);
            _unionScratch.UnionWith(_result.TilesToLoad);

            float hysteresis = math.clamp(_config.BudgetHysteresis, 1e-3f, 1f);
            _rankedScratch.Clear();
            foreach (var coord in _unionScratch)
            {
                float rank = _prioritizer.Importance(coord);
                if (_lastFrameSkippedTiles.Contains(coord)) rank /= hysteresis;
                _rankedScratch.Add(new RankedTile(coord, rank));
            }
            _rankedScratch.Sort(CompareByImportance);

            foreach (var ranked in _rankedScratch) _result.ByImportance.Add(ranked.Coord);
        }

        /// <summary>Traverses a subtree. <paramref name="coord"/> must be covered.</summary>
        private void VisitTile(TileCoord coord)
        {
            bool resolved = _coverage.IsResolved(coord);

            if (!IsInFrustum(coord))
            {
                // Not rendered, but kept in cache to show instantly when it comes into view.
                if (resolved)
                {
                    _result.TilesToKeep.Add(coord);
                }
                else
                {
                    // Until resolved itself, the tile is covered only by its covering tiles, which must not be
                    // lost: resolved ones stay loaded, skipped ones stay skipped.
                    _coveringResolvedScratch.Clear();
                    _coveringSkippedScratch.Clear();
                    _coverage.CollectCovering(coord, _coveringResolvedScratch, _coveringSkippedScratch);
                    _result.TilesToKeep.UnionWith(_coveringResolvedScratch);
                    _currentFrameSkippedTiles.UnionWith(_coveringSkippedScratch);
                    // Usually loading only coarsens coverage to save memory. Least important first: the important
                    // ones return to view first and will likely need their descendants' detail again. But if the
                    // covering tiles include a hole (a tile skipped in the previous frame), loading fills it before
                    // it comes into view, which matters more; then most important first, since they return to view
                    // sooner and the budget, ranking by the same importance, admits them first. If the budget
                    // denies the request, the manager does not start it and its group is irrelevant.
                    float importance = _prioritizer.Importance(coord);
                    if (_coveringSkippedScratch.Count > 0) AddLoadRequest(coord, LoadPriorityGroup.OffscreenReentry, importance);
                    else AddLoadRequest(coord, LoadPriorityGroup.Offscreen, -importance);
                }
                return;
            }

            if (IsDetailedEnough(coord))
            {
                if (resolved)
                {
                    _result.TilesToRender.Add(coord);
                }
                else
                {
                    // The covering tiles render while the tile loads (skipped ones as empty space).
                    _coveringResolvedScratch.Clear();
                    _coveringSkippedScratch.Clear();
                    _coverage.CollectCovering(coord, _coveringResolvedScratch, _coveringSkippedScratch);
                    _result.TilesToRender.UnionWith(_coveringResolvedScratch);
                    _currentFrameSkippedTiles.UnionWith(_coveringSkippedScratch);
                    if (_coveringSkippedScratch.Count > 0)
                        // A hole on screen: if the budget admits the request, filling it outranks refinements.
                        AddLoadRequest(coord, LoadPriorityGroup.VisibleReentry, _prioritizer.LoadPriority(coord));
                    else
                        // The area is already covered by finer descendants, so loading only coarsens coverage:
                        // after all refinement requests, least important first, since their descendants will not
                        // be needed again soon.
                        AddLoadRequest(coord, LoadPriorityGroup.VisibleCoarsening, -_prioritizer.LoadPriority(coord));
                }
                return;
            }

            Span<TileCoord> children = stackalloc TileCoord[4];
            coord.GetChildren(children);

            if (_coverage.AllCovered(children))
            {
                for (int i = 0; i < 4; i++) VisitTile(children[i]);
                return;
            }

            if (!resolved)
            {
                // Not all children are covered, yet the tile is covered and unresolved, so it was skipped in the
                // previous frame. It stays skipped (a hole) until covered otherwise; otherwise its parent would
                // lose coverage.
                Debug.Assert(_lastFrameSkippedTiles.Contains(coord), $"Tile {coord} is covered but neither resolved nor skipped.");
                _currentFrameSkippedTiles.Add(coord);
                float priority = _prioritizer.LoadPriority(coord);
                AddLoadRequest(coord, LoadPriorityGroup.VisibleReentry, priority);
                // Both the tile and its descendants: the budget decides which level to load (see the class docs).
                // Descendants go into Visible: the group order ensures all holes start loading before refinements;
                // with distance metrics the tile passes the budget before its descendants anyway, so they are a
                // refinement.
                RequestChildren(children, priority);
                return;
            }

            // Resolved: render it while the children load (all 4 must be covered to replace it without gaps).
            _result.TilesToRender.Add(coord);
            RequestChildren(children, _prioritizer.LoadPriority(coord));
        }

        /// <summary>
        /// Handles an uncovered root (at startup, or while the root and part of the subtree are not loaded yet):
        /// there is nothing to render and the whole screen is a hole. As for a skipped tile (see
        /// <see cref="VisitTile"/>), both the root and, if it is in view and lacks detail, its descendants of the
        /// required level are requested; the budget decides which level to load (the root contains every point and has
        /// the lowest Z, so with distance metrics and little room it passes first). Already loaded descendants are
        /// protected (see <see cref="RequestSubtree"/>), and requests denied by the budget become skipped and cover
        /// their area, so the root becomes covered even with a small budget.
        /// </summary>
        private void RequestUncoveredRoot(TileCoord root)
        {
            if (!IsInFrustum(root))
            {
                AddLoadRequest(root, LoadPriorityGroup.OffscreenReentry, 0f); // the only request, so the tiebreak is irrelevant
                return;
            }

            float priority = _prioritizer.LoadPriority(root);
            AddLoadRequest(root, LoadPriorityGroup.VisibleReentry, priority);
            if (IsDetailedEnough(root)) return;

            Span<TileCoord> children = stackalloc TileCoord[4];
            root.GetChildren(children);
            RequestChildren(children, priority);
        }

        /// <summary>
        /// Requests the descendants needed to replace a tile (see <see cref="RequestSubtree"/>). The whole subtree
        /// shares the parent's load priority (<paramref name="priority"/>): the parent (or a hole in its place) is what
        /// is on screen now, and it is what refines once all 4 children are covered. A shared priority prevents the
        /// lowest-priority descendant from delaying the replacement of the whole tile.
        ///
        /// The group is Visible regardless of each descendant's own frustum test: the parent is in the frustum and
        /// renders in place of all 4 children until they are all covered, so an "invisible" descendant blocks the
        /// transition as much as the others and affects the visible picture just as much. Demoting it to Offscreen
        /// would risk it getting stuck behind less important visible content elsewhere in the scene.
        /// </summary>
        private void RequestChildren(ReadOnlySpan<TileCoord> children, float priority)
        {
            int depth = math.max(0, _config.MaxSkippedLevels);
            for (int i = 0; i < children.Length; i++)
                RequestSubtree(children[i], depth, LoadPriorityGroup.Visible, priority);
        }

        /// <summary>
        /// Load traversal: requests the tiles of the <paramref name="coord"/> subtree needed for it to become covered,
        /// directly at the required level of detail, bypassing intermediate levels, but no deeper than
        /// <paramref name="depth"/> levels below <paramref name="coord"/>. <paramref name="coord"/> itself need not be
        /// covered.
        ///
        /// If the subtree is already covered only by resolved tiles, it is ready to replace the parent: its covering
        /// tiles are protected in <c>TilesToKeep</c> (otherwise they would be evicted just before they become useful,
        /// while the parent still renders in their place), and the traversal goes no deeper. Coverage involving
        /// skipped tiles counts as uncovered: skipped tiles are not carried over here (otherwise descendants denied by
        /// the budget would replace a rendered parent with holes), and resolved parts are protected by the traversal at
        /// their own level.
        /// </summary>
        private void RequestSubtree(TileCoord coord, int depth, LoadPriorityGroup group, float tiebreak)
        {
            if (_coverage.IsCoveredByResolved(coord))
            {
                _coverage.CollectResolvedCovering(coord, _result.TilesToKeep);
                return;
            }

            // Outside the frustum detail does not matter, so load the tile itself; the same if it is detailed
            // enough or at the depth limit.
            if (depth == 0 || !IsInFrustum(coord) || IsDetailedEnough(coord))
            {
                AddLoadRequest(coord, group, tiebreak);
                return;
            }

            Span<TileCoord> children = stackalloc TileCoord[4];
            coord.GetChildren(children);
            for (int i = 0; i < 4; i++)
                RequestSubtree(children[i], depth - 1, group, tiebreak);
        }

        /// <summary>
        /// Returns the tile's screen-space size in pixels: its physical size (<see cref="TileCoord.SizeMeters"/>)
        /// projected from the distance to the tile's closest point via
        /// <see cref="TileGeometryMath.ComputeScreenSpaceError"/>.
        /// </summary>
        private float ComputeScreenSpaceError(TileCoord coord)
        {
            float distance = TileGeometryMath.DistanceToTileClosestPoint(coord.BoundsMeters(), _cameraWorld, _pivot);
            return TileGeometryMath.ComputeScreenSpaceError(_projectionMatrix, _viewportHeightPixels, distance, coord.SizeMeters());
        }

        /// <summary>
        /// Whether the tile is detailed enough: a leaf at <see cref="TileQuadtreeConfig.MaxZoom"/>, or its screen-space
        /// size (<see cref="ComputeScreenSpaceError"/>) is below <see cref="TileQuadtreeConfig.MaxTileScreenSizePixels"/>.
        /// The screen-space size is not computed for a leaf.
        /// </summary>
        private bool IsDetailedEnough(TileCoord coord) =>
            coord.Z >= _config.MaxZoom || ComputeScreenSpaceError(coord) < _config.MaxTileScreenSizePixels;

        private bool IsInFrustum(TileCoord coord) => GeometryUtility.TestPlanesAABB(_frustumPlanes, WorldBounds(coord));

        /// <summary>
        /// Adds a load request. Within a group, a lower <paramref name="tiebreak"/> loads first: in view it is the load
        /// priority (<see cref="ITilePrioritizer.LoadPriority"/>), out of view the importance rank
        /// (<see cref="ITilePrioritizer.Importance"/>), signed. <c>+</c> (most important first) in groups where loading
        /// fills a hole or refines the picture: <see cref="LoadPriorityGroup.VisibleReentry"/>,
        /// <see cref="LoadPriorityGroup.Visible"/> (descendants use the parent's priority, see
        /// <see cref="RequestChildren"/>), <see cref="LoadPriorityGroup.OffscreenReentry"/>. <c>−</c> (least important
        /// first, since their descendants will be needed again last) in coarsening groups:
        /// <see cref="LoadPriorityGroup.VisibleCoarsening"/>, <see cref="LoadPriorityGroup.Offscreen"/>.
        /// </summary>
        private void AddLoadRequest(TileCoord coord, LoadPriorityGroup group, float tiebreak) =>
            _loadRequests.Add(new LoadRequest(coord, group, tiebreak));

        private Bounds WorldBounds(TileCoord coord)
        {
            var b = coord.BoundsMeters();
            float3 minWorld = _pivot.WorldFromMercator(new double2(b.x, b.y));
            float3 maxWorld = _pivot.WorldFromMercator(new double2(b.z, b.w));
            var center = new Vector3((minWorld.x + maxWorld.x) * 0.5f, 0f, (minWorld.z + maxWorld.z) * 0.5f);
            var size = new Vector3(
                math.abs(maxWorld.x - minWorld.x),
                _config.CullBoundsHeightMargin * 2f,
                math.abs(maxWorld.z - minWorld.z));
            return new Bounds(center, size);
        }
    }
}
