using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.TileSelection;
using GeoFoundry.Tiles.Budget;
using GeoFoundry.Tiles.TileSelection;
using UnityEngine;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>
    /// Tests <see cref="TileQuadtreeTraversal"/>, the coverage-based LOD traversal implementing
    /// <see cref="ITileSelector"/>. The camera is placed directly above the pivot (Mercator (0,0)) looking straight
    /// down, so it sits exactly at the center of the root tile (the shared corner of its 4 Z=1 children), which
    /// gives predictable distances and sizes at a small MaxZoom. FOV=120°, zFar=1e10 and
    /// <c>CullBoundsHeightMargin</c>=5e7 are chosen so that frustum culling does not interfere with coverage logic
    /// (every tile AABB intersects the frustum); tests that need real culling (<c>Culled*</c>) turn the camera
    /// upward so the tile plane is behind it. The selector receives the camera and the floating origin (pivot at
    /// zero) on creation (see <see cref="Traversal"/>); the default prioritizer is
    /// <see cref="CameraDistancePrioritizer"/>.
    /// </summary>
    public sealed class TileSelectionTests
    {
        private static readonly TileCoord Root = new TileCoord(0, 0, 0);

        private GameObject _cameraGo;
        private UnityEngine.Camera _camera;
        private RenderTexture _renderTexture;
        private readonly MercatorPivot _pivot = new MercatorPivot(Unity.Mathematics.double2.zero);
        private IFloatingOriginController _origin;

        private sealed class FixedOrigin : IFloatingOriginController
        {
            public MercatorPivot Current { get; }
            public event System.Action<PivotChangedEventArgs> PivotChanged { add { } remove { } }

            public FixedOrigin(MercatorPivot pivot) => Current = pivot;
        }

        [SetUp]
        public void SetUp()
        {
            _deniedByTraversal.Clear();
            _lastDenied = new List<TileCoord>();

            _origin = new FixedOrigin(_pivot);
            _cameraGo = new GameObject("TileSelectionTests_Camera");
            _camera = _cameraGo.AddComponent<UnityEngine.Camera>();
            _renderTexture = new RenderTexture(100, 100, 16); // fixes pixelHeight=100, aspect=1
            _camera.targetTexture = _renderTexture;
            _camera.fieldOfView = 120f;
            _camera.nearClipPlane = 0.01f;
            _camera.farClipPlane = 1e10f;
        }

        [TearDown]
        public void TearDown()
        {
            if (_cameraGo != null) Object.DestroyImmediate(_cameraGo);
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Object.DestroyImmediate(_renderTexture);
            }
        }

        private void LookDownFrom(float height) => LookDownFrom(new Vector3(0f, height, 0f));

        private void LookDownFrom(Vector3 position)
        {
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // forward = (0,-1,0)
        }

        private void LookUpFrom(float height)
        {
            _camera.transform.position = new Vector3(0f, height, 0f);
            _camera.transform.rotation = Quaternion.Euler(-90f, 0f, 0f); // forward = (0,1,0)
        }

        private TileQuadtreeTraversal Traversal(TileQuadtreeConfig config, ITilePrioritizer prioritizer = null) =>
            new TileQuadtreeTraversal(config, _camera, _origin, prioritizer);

        /// <summary>No level skipping by default: only direct children are requested.</summary>
        private static TileQuadtreeConfig MakeConfig(int maxZoom, float cullMargin = 5e7f, int maxSkippedLevels = 0) =>
            new TileQuadtreeConfig
            {
                MaxTileScreenSizePixels = 1500f,
                MaxZoom = maxZoom,
                MaxSkippedLevels = maxSkippedLevels,
                CullBoundsHeightMargin = cullMargin,
                BudgetHysteresis = 0.98f
            };

        private static HashSet<TileCoord> Loaded(params TileCoord[] tiles) => new HashSet<TileCoord>(tiles);
        private static HashSet<TileCoord> Failed(params TileCoord[] tiles) => new HashSet<TileCoord>(tiles);

        /// <summary>Cost in tile counts: every tile costs one, including tiles not yet loaded.</summary>
        private static readonly System.Func<TileCoord, TileMemoryUsage> UnitCost = _ => new TileMemoryUsage(1, 0);

        /// <summary>Tiles denied by the budget in the last frame, per selector (input for the next frame).</summary>
        private readonly Dictionary<TileQuadtreeTraversal, HashSet<TileCoord>> _deniedByTraversal = new();

        /// <summary>Tiles denied by the budget in the last <see cref="Select"/> call, in importance order.</summary>
        private List<TileCoord> _lastDenied = new();

        /// <summary>
        /// Runs one frame as the manager (<see cref="GeoFoundry.Tiles.TileLifecycleManager"/>) sees it: passes the
        /// selector the failed tiles from <paramref name="failed"/> and the tiles this selector had denied by the
        /// budget last frame, applies a tile-count budget (<see cref="TileBudgetPolicy.Admit"/>) and returns the
        /// result trimmed to admitted tiles; denied tiles go to <see cref="_lastDenied"/>. Also checks the
        /// <see cref="TileSelectionResult.ByImportance"/> contract. No budget by default. There is no eviction: the
        /// test decides what is loaded. Unlike the manager, the cost of unloaded tiles is known here, so denial comes
        /// immediately rather than after loading; this suffices for the traversal's reaction to denials, while the
        /// manager-style budget dynamics are covered by <see cref="TileBudgetPolicyTests"/>.
        /// </summary>
        /// <param name="budget">
        /// Number of most important tiles to admit. <c>Admit</c> allows a one-tile overshoot, so the tile-count limit
        /// is <c>budget − 1</c>.
        /// </param>
        private TileSelectionResult Select(
            TileQuadtreeTraversal traversal, HashSet<TileCoord> loaded, HashSet<TileCoord> failed = null, int budget = int.MaxValue)
        {
            if (!_deniedByTraversal.TryGetValue(traversal, out var deniedLastFrame))
                _deniedByTraversal[traversal] = deniedLastFrame = new HashSet<TileCoord>();

            var selection = new TileSelectionResult();
            Assert.That(traversal.SelectTiles(new TileFrameInput(loaded, deniedLastFrame, failed), selection), Is.True,
                "camera is alive, so there is a selection");

            var union = Protected(selection);
            Assert.That(selection.ByImportance, Is.Unique, "ByImportance: each tile exactly once");
            Assert.That(selection.ByImportance, Is.EquivalentTo(union), "ByImportance: exactly Render ∪ Load ∪ Keep");

            var admitted = new HashSet<TileCoord>();
            _lastDenied = new List<TileCoord>();
            TileBudgetPolicy.Admit(selection.ByImportance, UnitCost, new TileMemoryUsage(budget - 1, long.MaxValue), admitted, _lastDenied);
            deniedLastFrame.Clear();
            deniedLastFrame.UnionWith(_lastDenied);

            var result = new TileSelectionResult();
            result.TilesToRender.UnionWith(selection.TilesToRender.Where(admitted.Contains));
            result.TilesToKeep.UnionWith(selection.TilesToKeep.Where(admitted.Contains));
            result.TilesToLoad.AddRange(selection.TilesToLoad.Where(admitted.Contains));
            result.ByImportance.AddRange(selection.ByImportance.Where(admitted.Contains));
            return result;
        }

        /// <summary><c>TilesToRender ∪ TilesToLoad ∪ TilesToKeep</c>: what is protected from eviction and limited by the budget.</summary>
        private static HashSet<TileCoord> Protected(TileSelectionResult result)
        {
            var union = new HashSet<TileCoord>(result.TilesToRender);
            union.UnionWith(result.TilesToLoad);
            union.UnionWith(result.TilesToKeep);
            return union;
        }

        private static LoadPriorityGroup GroupOf(TileQuadtreeTraversal traversal, TileCoord coord) =>
            traversal.CurrentLoadRequests.First(r => r.Coord.Equals(coord)).Group;

        private static float TiebreakOf(TileQuadtreeTraversal traversal, TileCoord coord) =>
            traversal.CurrentLoadRequests.First(r => r.Coord.Equals(coord)).Tiebreak;

        // ---- Startup / root ---------------------------------------------------

        [Test]
        public void Select_NothingLoaded_RequestsRootFirstAndItsChildren()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 6));

            var result = Select(traversal, Loaded());

            Assert.That(result.TilesToRender, Is.Empty);
            Assert.That(result.TilesToKeep, Is.Empty);
            Assert.That(result.TilesToLoad[0], Is.EqualTo(Root), "the whole screen is a hole: the root itself loads first");
            Assert.That(result.TilesToLoad.Skip(1), Is.EquivalentTo(Z1));
            Assert.That(GroupOf(traversal, Root), Is.EqualTo(LoadPriorityGroup.VisibleReentry));
            foreach (var c in Z1)
                Assert.That(GroupOf(traversal, c), Is.EqualTo(LoadPriorityGroup.Visible));
        }

        [Test]
        public void Select_NothingLoaded_RootDetailedEnough_RequestsOnlyRoot()
        {
            LookDownFrom(1e9f);
            var traversal = Traversal(MakeConfig(maxZoom: 6, maxSkippedLevels: 2));

            var result = Select(traversal, Loaded());

            Assert.That(result.TilesToLoad, Is.EqualTo(new[] { Root }));
        }

        [Test]
        public void Select_NothingLoaded_SkipLevels_RequestsRootAndTargetLevel()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 1));

            var result = Select(traversal, Loaded());

            Assert.That(result.TilesToLoad[0], Is.EqualTo(Root));
            Assert.That(result.TilesToLoad.Skip(1), Is.EquivalentTo(Z2), "without the intermediate Z1");
        }

        [Test]
        public void Select_RootNotCovered_KeepsPartiallyLoadedDescendants()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 1));
            var loadedChildren = Root.Children().Take(3).ToArray();

            var result = Select(traversal, Loaded(loadedChildren));

            Assert.That(result.TilesToRender, Is.Empty, "root is not covered: nothing to render, otherwise there would be holes");
            Assert.That(result.TilesToKeep, Is.EquivalentTo(loadedChildren), "already loaded descendants are not evicted");
            Assert.That(result.TilesToLoad, Is.EqualTo(new[] { Root, Root.GetChild(3) }));
        }

        // ---- Level of detail ---------------------------------------------------

        [Test]
        public void Select_CameraFarAbove_RootLoaded_RendersOnlyRoot()
        {
            LookDownFrom(1e9f); // size(root) at this distance is trivially below the threshold
            var traversal = Traversal(MakeConfig(maxZoom: 6));

            var result = Select(traversal, Loaded(Root));

            Assert.That(result.TilesToRender, Is.EquivalentTo(new[] { Root }));
            Assert.That(result.TilesToLoad, Is.Empty);
        }

        [Test]
        public void Select_DetailedEnoughButNotLoaded_RendersCoveringTilesAndRequestsIt()
        {
            LookDownFrom(1e9f);
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            var result = Select(traversal, Loaded(children));

            Assert.That(result.TilesToRender, Is.EquivalentTo(children), "while the root loads, its covering children are rendered");
            Assert.That(result.TilesToLoad, Is.EqualTo(new[] { Root }));
            Assert.That(GroupOf(traversal, Root), Is.EqualTo(LoadPriorityGroup.VisibleCoarsening),
                "the area is already covered by children without holes; the load only coarsens coverage");
        }

        [Test]
        public void Select_TooCoarseAndChildrenNotCovered_RendersParentAndRequestsChildren()
        {
            LookDownFrom(1000f); // size(root) here clearly exceeds the threshold -> refinement needed
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            var result = Select(traversal, Loaded(Root, children[0]));

            Assert.That(result.TilesToRender, Is.EquivalentTo(new[] { Root }),
                "not all children are covered: the parent renders until all 4 are ready");
            Assert.That(result.TilesToLoad, Is.EquivalentTo(children.Where(c => !c.Equals(children[0]))),
                "only still unknown children are requested");
            Assert.That(result.TilesToKeep, Is.EquivalentTo(new[] { children[0] }),
                "an already loaded child is not reloaded but is protected from eviction while waiting for siblings");
            foreach (var c in children.Where(c => !c.Equals(children[0])))
                Assert.That(GroupOf(traversal, c), Is.EqualTo(LoadPriorityGroup.Visible));
        }

        [Test]
        public void Select_TooCoarseAndOneChildFailed_KeepsFailedChildWithoutReloadRequest()
        {
            LookDownFrom(1000f);
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            var result = Select(traversal, Loaded(Root), Failed(children[0]));

            Assert.That(result.TilesToRender, Is.EquivalentTo(new[] { Root }));
            Assert.That(result.TilesToKeep, Is.EquivalentTo(new[] { children[0] }),
                "a failed child is not retried by this implementation but stays protected from eviction");
            Assert.That(result.TilesToLoad, Is.EquivalentTo(children.Where(c => !c.Equals(children[0]))),
                "the remaining unknown children are requested as usual");
            Assert.That(traversal.FailedTiles, Is.EquivalentTo(new[] { children[0] }),
                "the selector remembers a failed tile while it is protected");
        }

        [Test]
        public void Select_FailedTileNoLongerProtected_IsForgottenAndRequestedAgain()
        {
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            LookDownFrom(1000f);
            Select(traversal, Loaded(Root), Failed(children[0]));
            Assert.That(traversal.FailedTiles, Is.EquivalentTo(new[] { children[0] }));

            LookDownFrom(1e9f); // the root is detailed enough; children are no longer needed
            var far = Select(traversal, Loaded(Root));
            Assert.That(far.TilesToRender, Is.EquivalentTo(new[] { Root }));
            Assert.That(traversal.FailedTiles, Is.Empty, "an unprotected failed tile is forgotten");

            LookDownFrom(1000f);
            var near = Select(traversal, Loaded(Root));
            Assert.That(near.TilesToLoad, Does.Contain(children[0]), "a forgotten failed tile is requested again");
        }

        [Test]
        public void Select_FailedTileNotNeeded_IsDroppedImmediately()
        {
            LookDownFrom(1e9f);
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            var result = Select(traversal, Loaded(Root), Failed(children[0]));

            Assert.That(result.TilesToRender, Is.EquivalentTo(new[] { Root }));
            Assert.That(result.TilesToKeep, Is.Empty);
            Assert.That(traversal.FailedTiles, Is.Empty);
        }

        [Test]
        public void Select_TooCoarseAndAllChildrenLoaded_RendersChildren()
        {
            LookDownFrom(1000f);
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            var result = Select(traversal, Loaded(new[] { Root }.Concat(children).ToArray()));

            Assert.That(result.TilesToRender, Is.EquivalentTo(children));
            Assert.That(result.TilesToLoad, Is.Empty);
        }

        [Test]
        public void Select_RootCoveredOnlyByChildren_DescendsWithoutRequestingRoot()
        {
            LookDownFrom(1000f);
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            var result = Select(traversal, Loaded(children)); // the root is not loaded but is covered by its children

            Assert.That(result.TilesToRender, Is.EquivalentTo(children));
            Assert.That(result.TilesToLoad, Is.Empty, "root is not needed: it is too coarse and its children are already shown");
        }

        [Test]
        public void Select_TileAtMaxZoom_NeverRefinesFurther()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 0)); // the root itself is a leaf

            var result = Select(traversal, Loaded(Root));

            Assert.That(result.TilesToRender, Is.EquivalentTo(new[] { Root }));
            Assert.That(result.TilesToLoad, Is.Empty);
        }

        // ---- Frustum culling ---------------------------------------------------

        [Test]
        public void Select_CulledLoadedTile_IsKeptButNotRendered()
        {
            LookUpFrom(1000f); // tile plane is behind the camera -> Culled
            var traversal = Traversal(MakeConfig(maxZoom: 1, cullMargin: 100f));

            var result = Select(traversal, Loaded(Root));

            Assert.That(result.TilesToRender, Is.Empty);
            Assert.That(result.TilesToKeep, Is.EquivalentTo(new[] { Root }));
            Assert.That(result.TilesToLoad, Is.Empty);
        }

        [Test]
        public void Select_CulledNotLoadedTile_KeepsCoveringTilesAndLoadsItAsOffscreen()
        {
            LookUpFrom(1000f);
            var children = Root.Children();
            var traversal = Traversal(MakeConfig(maxZoom: 1, cullMargin: 100f));

            var result = Select(traversal, Loaded(children)); // the root is covered only by its children

            Assert.That(result.TilesToRender, Is.Empty);
            Assert.That(result.TilesToKeep, Is.EquivalentTo(children),
                "without protecting the covering tiles, their eviction would collapse coverage up to the root");
            Assert.That(result.TilesToLoad, Is.EqualTo(new[] { Root }));
            Assert.That(GroupOf(traversal, Root), Is.EqualTo(LoadPriorityGroup.Offscreen));
            Assert.That(TiebreakOf(traversal, Root), Is.EqualTo(-DistanceFromCamera(Root)),
                "coarsening: farthest first");
        }

        [Test]
        public void Select_CulledNotLoadedTile_CoveredWithHole_LoadsItAsOffscreenReentry_NearestFirst()
        {
            var traversal = Traversal(MakeConfig(maxZoom: 1, cullMargin: 100f));

            // Frame 1: Z1 tiles render; the budget admits two, the other two become holes.
            LookDownFrom(1000f);
            Select(traversal, Loaded(Z1), budget: 2);
            Assume.That(_lastDenied, Has.Count.EqualTo(2));
            var admitted = Z1.Where(c => !_lastDenied.Contains(c)).ToArray();

            // Frame 2: denied tiles are evicted and the camera has turned away; the root is covered by loaded tiles
            // and holes.
            LookUpFrom(1000f);
            var result = Select(traversal, Loaded(admitted));

            Assert.That(result.TilesToLoad, Is.EqualTo(new[] { Root }));
            Assert.That(GroupOf(traversal, Root), Is.EqualTo(LoadPriorityGroup.OffscreenReentry));
            Assert.That(TiebreakOf(traversal, Root), Is.EqualTo(DistanceFromCamera(Root)),
                "holes are filled nearest to farthest, in the order the budget admits them");
        }

        // ---- Algorithm properties ----------------------------------------------

        private static IEnumerable<TileCoord> AllTilesUpTo(int maxZoom)
        {
            var level = new List<TileCoord> { Root };
            while (true)
            {
                foreach (var t in level) yield return t;
                if (level[0].Z >= maxZoom) yield break;
                level = level.SelectMany(t => t.Children()).ToList();
            }
        }

        private static double Area(TileCoord c) => System.Math.Pow(4, -c.Z);

        private static bool IsAncestor(TileCoord ancestor, TileCoord tile)
        {
            while (tile.Z > ancestor.Z) tile = tile.Parent();
            return tile.Equals(ancestor);
        }

        [Test]
        public void Select_RandomLoadedAndFailedSets_NeverLeavesHolesAndProtectionIsSufficient()
        {
            const int maxZoom = 4;
            var allTiles = AllTilesUpTo(maxZoom).ToArray();
            var rng = new System.Random(12345);

            foreach (float height in new[] { 1e9f, 1e6f, 1e5f, 1e3f })
            {
                LookDownFrom(height);
                foreach (int skippedLevels in new[] { 0, 2 })
                foreach (double p in new[] { 0.3, 0.6, 0.9, 0.99 })
                {
                    for (int iter = 0; iter < 25; iter++)
                    {
                        // failed is a subset of the remaining tiles disjoint from loaded (at half the rate p so it
                        // does not consume loaded entirely at large p).
                        var loaded = new HashSet<TileCoord>(allTiles.Where(_ => rng.NextDouble() < p));
                        var failed = new HashSet<TileCoord>(allTiles.Where(t => !loaded.Contains(t) && rng.NextDouble() < p / 2));
                        // A fresh selector per iteration: the failed set lives inside it between frames.
                        var traversal = Traversal(MakeConfig(maxZoom, maxSkippedLevels: skippedLevels));
                        var result = Select(traversal, loaded, failed);
                        string ctx = $"height={height}, skippedLevels={skippedLevels}, p={p}, iter={iter}";

                        var resolved = new HashSet<TileCoord>(loaded);
                        resolved.UnionWith(failed);
                        var coverage = new TileCoverage();
                        coverage.Rebuild(resolved);
                        if (!coverage.IsCovered(Root))
                        {
                            Assert.That(result.TilesToRender, Is.Empty, ctx);
                            Assert.That(result.TilesToLoad[0], Is.EqualTo(Root), ctx + ": root loads first");
                            Assert.That(result.TilesToLoad.Any(resolved.Contains), Is.False, ctx + ": resolved tiles are not requested");
                            Assert.That(result.TilesToKeep.All(resolved.Contains), Is.True, ctx + ": keep contains only resolved tiles");
                            continue;
                        }

                        // Only resolved (loaded or failed) tiles are rendered or kept, without overlaps or gaps; the
                        // coverage geometry does not depend on whether a failed tile has real content.
                        Assert.That(result.TilesToRender.All(resolved.Contains), Is.True, ctx + ": render contains only resolved tiles");
                        Assert.That(result.TilesToKeep.All(resolved.Contains), Is.True, ctx + ": keep contains only resolved tiles");
                        Assert.That(result.TilesToRender.Sum(t => Area(t)), Is.EqualTo(1.0).Within(1e-9), ctx + ": gaps/overlaps");
                        foreach (var a in result.TilesToRender)
                            foreach (var b in result.TilesToRender)
                                if (!a.Equals(b)) Assert.That(IsAncestor(a, b), Is.False, ctx + $": {a} is nested in {b}");

                        // This implementation never re-requests failed tiles.
                        Assert.That(result.TilesToLoad.Any(failed.Contains), Is.False, ctx + ": failed tile is not retried");

                        // The protected set is sufficient: evicting everything else changes nothing.
                        var protectedTiles = new HashSet<TileCoord>(result.TilesToRender);
                        protectedTiles.UnionWith(result.TilesToLoad);
                        protectedTiles.UnionWith(result.TilesToKeep);
                        var survivingLoaded = new HashSet<TileCoord>(loaded.Where(protectedTiles.Contains));
                        var survivingFailed = new HashSet<TileCoord>(failed.Where(protectedTiles.Contains));
                        Assert.That(traversal.FailedTiles, Is.EquivalentTo(survivingFailed),
                            ctx + ": selector remembers exactly the protected failed tiles");

                        // Failed tiles are not reported again; the surviving ones are stored in the selector itself.
                        var afterEviction = Select(traversal, survivingLoaded);
                        Assert.That(afterEviction.TilesToRender, Is.EquivalentTo(result.TilesToRender), ctx + ": after eviction");
                    }
                }
            }
        }

        // ---- Tile budget --------------------------------------------------------
        //
        // Camera above the world center: the 4 "inner" Z2 tiles touch the camera (distance to the closest point
        // equals the camera height), as do all 4 Z1 tiles; the 12 "outer" Z2 tiles are no closer than C/4 ≈ 1e7 m
        // (C is the equator length). At 1000 m neither the root nor Z1 is detailed enough.

        private bool TouchesCamera(TileCoord t) =>
            TileGeometryMath.HorizontalDistanceToTileClosestPoint(t.BoundsMeters(), Unity.Mathematics.float3.zero, _pivot) < 1f;

        private float DistanceFromCamera(TileCoord t)
        {
            var p = _camera.transform.position;
            return TileGeometryMath.DistanceToTileClosestPoint(t.BoundsMeters(), new Unity.Mathematics.float3(p.x, p.y, p.z), _pivot);
        }

        private static TileCoord[] Z1 => Root.Children();
        private static TileCoord[] Z2 => Root.Children().SelectMany(c => c.Children()).ToArray();
        private TileCoord[] InnerZ2 => Z2.Where(TouchesCamera).ToArray();
        private TileCoord[] OuterZ2 => Z2.Where(t => !TouchesCamera(t)).ToArray();

        /// <summary>The root, all Z1 and the inner Z2 tiles.</summary>
        private HashSet<TileCoord> LoadedUpToInnerZ2() =>
            new HashSet<TileCoord>(new[] { Root }.Concat(Z1).Concat(InnerZ2));

        [Test]
        public void Budget_NotExceeded_IsNoOp()
        {
            LookDownFrom(1000f);
            var loaded = new HashSet<TileCoord>(new[] { Root }.Concat(Z1).Concat(Z2));

            var unlimited = Select(Traversal(MakeConfig(maxZoom: 2)), loaded);
            var traversal = Traversal(MakeConfig(maxZoom: 2));
            var exact = Select(traversal, loaded, budget: Z2.Length);

            Assert.That(exact.TilesToRender, Is.EquivalentTo(unlimited.TilesToRender));
            Assert.That(exact.TilesToRender, Is.EquivalentTo(Z2));
            Assert.That(_lastDenied, Is.Empty);
        }

        [Test]
        public void Budget_DeniesFarthestTiles()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2));
            var loaded = new HashSet<TileCoord>(new[] { Root }.Concat(Z1).Concat(Z2));

            var result = Select(traversal, loaded, budget: 4);

            Assert.That(result.TilesToRender, Is.EquivalentTo(InnerZ2), "tiles nearest to the camera remain");
            Assert.That(_lastDenied, Is.EquivalentTo(OuterZ2), "farthest tiles are denied");
            Assert.That(result.TilesToLoad, Is.Empty);
            Assert.That(result.TilesToKeep, Is.Empty, "denied tiles are not protected from eviction");
        }

        [Test]
        public void Budget_ImportanceOrder_NearestFirst_CoarserFirstOnEqualDistance()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2));

            var result = Select(traversal, LoadedUpToInnerZ2());

            // Z1 tiles render, inner Z2 are protected, outer Z2 are requested. Z1 and inner Z2 touch the camera (equal
            // distance); on a tie the lower Z is more important.
            var order = result.ByImportance;
            Assert.That(order.Take(4), Is.EquivalentTo(Z1));
            Assert.That(order.Skip(4).Take(4), Is.EquivalentTo(InnerZ2));
            Assert.That(order.Skip(8), Is.EquivalentTo(OuterZ2));
            var outerDistances = order.Skip(8).Select(DistanceFromCamera).ToArray();
            Assert.That(outerDistances, Is.Ordered, "farther means less important");
        }

        [Test]
        public void Budget_DeniedLoadRequestsAreDropped_AndHolesDoNotBlockRefinement()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2));
            var loaded = LoadedUpToInnerZ2();

            var first = Select(traversal, loaded, budget: 8);
            Assert.That(first.TilesToRender, Is.EquivalentTo(Z1), "parents render while outer children are not covered");
            Assert.That(first.TilesToKeep, Is.EquivalentTo(InnerZ2));
            Assert.That(first.TilesToLoad, Is.Empty, "requests for outer Z2 are the farthest and are denied");
            Assert.That(_lastDenied, Is.EquivalentTo(OuterZ2));

            var second = Select(traversal, loaded, budget: 8);
            Assert.That(second.TilesToRender, Is.EquivalentTo(InnerZ2), "skipped tiles cover their area, so parents give way to children");
            Assert.That(Protected(second).Count, Is.LessThanOrEqualTo(8));
            Assert.That(second.TilesToLoad, Has.Count.EqualTo(4), "the nearest holes fit into the remaining budget");
            Assert.That(OuterZ2, Is.SupersetOf(second.TilesToLoad));
            foreach (var t in second.TilesToLoad)
                Assert.That(GroupOf(traversal, t), Is.EqualTo(LoadPriorityGroup.VisibleReentry));
            Assert.That(traversal.SkippedTiles, Is.EquivalentTo(OuterZ2), "all outer tiles are holes until loaded");
        }

        [Test]
        public void Budget_DeniedTilesReturnWhenBudgetGrows()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2));
            var loaded = LoadedUpToInnerZ2();
            Select(traversal, loaded, budget: 8);
            Select(traversal, loaded, budget: 8);

            var grown = Select(traversal, loaded);
            Assert.That(grown.TilesToRender, Is.EquivalentTo(InnerZ2), "a hole does not revert the view to coarse tiles");
            Assert.That(grown.TilesToLoad, Is.EquivalentTo(OuterZ2));
            foreach (var t in OuterZ2)
                Assert.That(GroupOf(traversal, t), Is.EqualTo(LoadPriorityGroup.VisibleReentry), "filling a visible hole is urgent");

            loaded.UnionWith(OuterZ2);
            var afterLoad = Select(traversal, loaded);
            Assert.That(afterLoad.TilesToRender, Is.EquivalentTo(Z2));
            Assert.That(afterLoad.TilesToLoad, Is.Empty);
            Assert.That(traversal.SkippedTiles, Is.Empty);
        }

        [Test]
        public void Budget_Hysteresis_TileDeniedLastFrameMustBeNoticeablyCloserToReturn()
        {
            var loaded = new HashSet<TileCoord>(new[] { Root }.Concat(Z1));
            var traversal = Traversal(MakeConfig(maxZoom: 1));

            // Camera above one of the Z1 tiles, slightly off the world center: the X neighbor is closer than the
            // Z neighbor, and the diagonal one is the farthest.
            LookDownFrom(new Vector3(100f, 1000f, 200f));
            var byDistance = Z1.OrderBy(DistanceFromCamera).ToArray();
            TileCoord under = byDistance[0], neighborX = byDistance[1], neighborZ = byDistance[2];

            var first = Select(traversal, loaded, budget: 2);
            Assert.That(first.TilesToRender, Is.EquivalentTo(new[] { under, neighborX }));
            Assert.That(_lastDenied, Does.Contain(neighborZ));

            // Now the Z neighbor is slightly closer than the X neighbor, but by less than 2% (hysteresis 0.98).
            LookDownFrom(new Vector3(200f, 1000f, 190f));
            float dx = DistanceFromCamera(neighborX), dz = DistanceFromCamera(neighborZ);
            Assume.That(dz, Is.LessThan(dx), "precondition: without hysteresis the Z neighbor would win");
            Assume.That(dz / 0.98f, Is.GreaterThan(dx), "precondition: with hysteresis it does not");

            var second = Select(traversal, loaded, budget: 2);
            Assert.That(second.TilesToRender, Is.EquivalentTo(new[] { under, neighborX }), "budget boundary does not flicker");

            var fresh = Traversal(MakeConfig(maxZoom: 1));
            var freshResult = Select(fresh, loaded, budget: 2);
            Assert.That(freshResult.TilesToRender, Is.EquivalentTo(new[] { under, neighborZ }), "without history, pure distance decides");
        }

        [Test]
        public void Budget_RandomLoadedSets_StaysWithinBudget_KeepsRootCovered_AndNeverCoarsens()
        {
            const int maxZoom = 4;
            var allTiles = AllTilesUpTo(maxZoom).ToArray();
            var rng = new System.Random(54321);

            foreach (float height in new[] { 1e5f, 1e3f })
            {
                LookDownFrom(height);
                foreach (int skippedLevels in new[] { 0, 2 })
                foreach (int budget in new[] { 1, 3, 10 })
                foreach (double p in new[] { 0.3, 0.6, 0.9 })
                for (int iter = 0; iter < 25; iter++)
                {
                    var loaded = new HashSet<TileCoord>(allTiles.Where(_ => rng.NextDouble() < p));
                    var traversal = Traversal(MakeConfig(maxZoom, maxSkippedLevels: skippedLevels));
                    var initialCoverage = new TileCoverage();
                    initialCoverage.Rebuild(new HashSet<TileCoord>(loaded));
                    bool rootCovered = initialCoverage.IsCovered(Root);
                    HashSet<TileCoord> previousRender = null;

                    // Several frames with a static camera: everything unprotected is evicted after each, and there are
                    // no new loads.
                    for (int frame = 0; frame < 4; frame++)
                    {
                        string ctx = $"height={height}, skippedLevels={skippedLevels}, budget={budget}, p={p}, iter={iter}, frame={frame}";
                        var result = Select(traversal, loaded, budget: budget);
                        var protectedTiles = Protected(result);

                        Assert.That(protectedTiles.Count, Is.LessThanOrEqualTo(budget), ctx + ": budget");
                        Assert.That(result.TilesToRender.All(loaded.Contains), Is.True, ctx + ": render contains only loaded tiles");
                        Assert.That(_lastDenied.Any(protectedTiles.Contains), Is.False, ctx + ": denied tile is protected");
                        Assert.That(traversal.SkippedTiles.Any(result.TilesToRender.Contains), Is.False, ctx + ": skipped tile is rendered");
                        foreach (var a in result.TilesToRender)
                            foreach (var b in result.TilesToRender)
                                if (!a.Equals(b)) Assert.That(IsAncestor(a, b), Is.False, ctx + $": {a} is nested in {b}");

                        if (previousRender != null)
                            foreach (var tile in result.TilesToRender)
                                foreach (var prev in previousRender)
                                    if (!tile.Equals(prev))
                                        Assert.That(IsAncestor(tile, prev), Is.False, ctx + $": {prev} reverted to coarser {tile}");
                        previousRender = new HashSet<TileCoord>(result.TilesToRender);

                        loaded.IntersectWith(protectedTiles);

                        // Root coverage relies on loaded and skipped tiles (carried over by the traversal and denied by
                        // the budget); evicting denied tiles must not break it (otherwise it falls back to loading only
                        // the root).
                        if (rootCovered)
                        {
                            var skipped = new HashSet<TileCoord>(traversal.SkippedTiles);
                            skipped.UnionWith(_lastDenied);
                            var coverage = new TileCoverage();
                            coverage.Rebuild(new HashSet<TileCoord>(loaded), skipped);
                            Assert.That(coverage.IsCovered(Root), Is.True, ctx + ": root coverage lost after eviction");
                        }
                    }
                }
            }
        }

        // ---- Level skipping -----------------------------------------------------
        //
        // Camera at 1000 m above the world center: the root, all Z1 and inner Z2 are not detailed enough (they
        // touch the camera), outer Z2 are (≈29 px at a distance of ≈1e7 m).

        private static TileCoord InnerChild(TileCoord tile) =>
            tile.Children().Single(c => TileGeometryMath.HorizontalDistanceToTileClosestPoint(
                c.BoundsMeters(), Unity.Mathematics.float3.zero, new MercatorPivot(Unity.Mathematics.double2.zero)) < 1f);

        [Test]
        public void SkipLevels_RequestsTargetLevelDirectly_WithSharedPriority()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 1));

            var result = Select(traversal, Loaded(Root));

            Assert.That(result.TilesToRender, Is.EquivalentTo(new[] { Root }), "parent renders while descendants load");
            Assert.That(result.TilesToLoad, Is.EquivalentTo(Z2), "target level directly, without the intermediate Z1");
            Assert.That(traversal.CurrentLoadRequests.All(r => r.Group == LoadPriorityGroup.Visible), Is.True);
            Assert.That(traversal.CurrentLoadRequests.Select(r => r.Tiebreak).Distinct().Count(), Is.EqualTo(1),
                "the whole subtree shares the parent's SSE as priority");

            var noSkip = Select(Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 0)), Loaded(Root));
            Assert.That(noSkip.TilesToLoad, Is.EquivalentTo(Z1), "without skipping, only direct children");
        }

        [Test]
        public void SkipLevels_IntermediateLevelIsNeverLoaded()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 1));

            var result = Select(traversal, new HashSet<TileCoord>(new[] { Root }.Concat(Z2)));

            Assert.That(result.TilesToRender, Is.EquivalentTo(Z2), "traversal descends through never-loaded Z1");
            Assert.That(result.TilesToLoad, Is.Empty);
        }

        [Test]
        public void SkipLevels_StopsAtDepthLimitOrAtDetailedEnoughTile()
        {
            LookDownFrom(1000f);

            var depth1 = Select(Traversal(MakeConfig(maxZoom: 3, maxSkippedLevels: 1)), Loaded(Root));
            Assert.That(depth1.TilesToLoad, Is.EquivalentTo(Z2), "depth is exhausted at Z2 even though inner Z2 lack detail");

            var depth2 = Select(Traversal(MakeConfig(maxZoom: 3, maxSkippedLevels: 2)), Loaded(Root));
            Assert.That(depth2.TilesToLoad, Is.EquivalentTo(OuterZ2.Concat(InnerZ2.SelectMany(t => t.Children()))),
                "detailed-enough outer Z2 by themselves, inner ones by their children");
        }

        [Test]
        public void SkipLevels_ResolvedIntermediateTile_IsKeptAndNotDescended()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 1));

            var result = Select(traversal, Loaded(Root, Z1[0]));

            Assert.That(result.TilesToKeep, Is.EquivalentTo(new[] { Z1[0] }), "an already loaded intermediate tile is not evicted");
            Assert.That(result.TilesToLoad, Is.EquivalentTo(Z1.Skip(1).SelectMany(t => t.Children())),
                "its children are not requested: its quarter is already ready to replace the parent");
        }

        [Test]
        public void SkipLevels_IntermediateCoveredByResolvedDescendants_KeepsThemWithoutRequests()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 1));

            var result = Select(traversal, Loaded(new[] { Root }.Concat(Z1[0].Children()).ToArray()));

            Assert.That(result.TilesToKeep, Is.EquivalentTo(Z1[0].Children()));
            Assert.That(result.TilesToLoad, Has.No.Member(Z1[0]), "a tile covered by loaded descendants is not requested again");
            Assert.That(result.TilesToLoad, Is.EquivalentTo(Z1.Skip(1).SelectMany(t => t.Children())));
        }

        [Test]
        public void SkipLevels_SkippedTile_RequestsItselfFirstAndDescendants()
        {
            var traversal = Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 1));

            // Frame 1: Z1 tiles are detailed enough; the budget cuts off one of them.
            LookDownFrom(5e5f);
            Select(traversal, new HashSet<TileCoord>(new[] { Root }.Concat(Z1)), budget: 3);
            Assume.That(_lastDenied, Has.Count.EqualTo(1));
            var hole = _lastDenied.Single();

            // Frame 2: the camera is closer and the hole lacks detail; it is evicted itself, one of its children is
            // loaded.
            LookDownFrom(1000f);
            var holeChildren = hole.Children();
            var loaded = new HashSet<TileCoord>(new[] { Root }.Concat(Z1).Where(t => !t.Equals(hole))) { holeChildren[0] };
            var result = Select(traversal, loaded);

            Assert.That(traversal.SkippedTiles, Does.Contain(hole), "a hole stays a hole until covered");
            Assert.That(GroupOf(traversal, hole), Is.EqualTo(LoadPriorityGroup.VisibleReentry));
            Assert.That(result.TilesToKeep, Does.Contain(holeChildren[0]), "an already loaded descendant of the hole is protected");
            int holeIndex = result.TilesToLoad.IndexOf(hole);
            foreach (var child in holeChildren.Skip(1))
            {
                Assert.That(GroupOf(traversal, child), Is.EqualTo(LoadPriorityGroup.Visible));
                Assert.That(result.TilesToLoad.IndexOf(child), Is.GreaterThan(holeIndex), "the hole itself loads before its descendants");
            }
        }

        [Test]
        public void SkipLevels_ChildCoveredPartlyBySkipped_IsNotTreatedAsCovered()
        {
            var traversal = Traversal(MakeConfig(maxZoom: 2, maxSkippedLevels: 1));
            LookDownFrom(1000f);

            // Frame 1: the budget keeps only inner Z2; outer ones become skipped.
            Select(traversal, new HashSet<TileCoord>(new[] { Root }.Concat(Z1).Concat(Z2)), budget: 4);
            Assume.That(_lastDenied, Is.EquivalentTo(OuterZ2));

            // Frame 2: denied tiles and Z1 are evicted; one Z1 also lacks its inner child, so the root is not covered
            // by children and renders itself while requesting descendants.
            var partly = Z1[0];
            var uncovered = Z1[1];
            var loaded = new HashSet<TileCoord>(new[] { Root }.Concat(InnerZ2).Where(t => !t.Equals(InnerChild(uncovered))));
            var result = Select(traversal, loaded);

            Assert.That(result.TilesToRender, Is.EquivalentTo(new[] { Root }));
            Assert.That(result.TilesToKeep, Does.Contain(InnerChild(partly)), "loaded sub-part is protected");
            var skippedChildren = partly.Children().Where(c => !c.Equals(InnerChild(partly))).ToArray();
            Assert.That(result.TilesToLoad, Is.SupersetOf(skippedChildren),
                "skipped sub-parts are requested; otherwise the parent would be replaced by holes");
            Assert.That(result.TilesToKeep, Has.No.Member(partly));
            Assert.That(result.TilesToLoad, Has.No.Member(partly));
        }

        // ---- Prioritizer and camera ----------------------------------------------

        /// <summary>Prioritizer with test-defined priorities (zero by default); counts frames.</summary>
        private sealed class FakePrioritizer : ITilePrioritizer
        {
            public readonly Dictionary<TileCoord, float> ImportanceOf = new();
            public readonly Dictionary<TileCoord, float> LoadPriorityOf = new();
            public int FramesBegun;

            public void BeginFrame() => FramesBegun++;
            public float Importance(TileCoord coord) => ImportanceOf.TryGetValue(coord, out float v) ? v : 0f;
            public float LoadPriority(TileCoord coord) => LoadPriorityOf.TryGetValue(coord, out float v) ? v : 0f;
        }

        [Test]
        public void Prioritizer_ImportanceOrdersBudget_LoadPriorityOrdersVisibleLoads()
        {
            LookDownFrom(1000f);
            var prioritizer = new FakePrioritizer();
            var traversal = Traversal(MakeConfig(maxZoom: 2), prioritizer);

            // Z1 tiles render and request their children; importance is a permutation unrelated to geometry, and the
            // load priority is a separate order of parents.
            var union = Z1.Concat(Z2).ToArray();
            for (int i = 0; i < union.Length; i++) prioritizer.ImportanceOf[union[i]] = (i * 7) % union.Length;
            var parentsByLoadPriority = new[] { Z1[2], Z1[0], Z1[3], Z1[1] };
            for (int i = 0; i < parentsByLoadPriority.Length; i++) prioritizer.LoadPriorityOf[parentsByLoadPriority[i]] = i;

            var result = Select(traversal, new HashSet<TileCoord>(new[] { Root }.Concat(Z1)));

            Assert.That(prioritizer.FramesBegun, Is.EqualTo(1), "frame state is captured once");
            Assert.That(result.ByImportance, Is.EqualTo(union.OrderBy(t => prioritizer.ImportanceOf[t])), "budget follows importance");
            Assert.That(result.TilesToLoad.Select(t => t.Parent()),
                Is.EqualTo(parentsByLoadPriority.SelectMany(p => Enumerable.Repeat(p, 4))),
                "visible requests follow the parent's load priority");
        }

        [Test]
        public void SelectTiles_CameraDestroyed_ReturnsFalseAndLeavesResultUntouched()
        {
            LookDownFrom(1000f);
            var traversal = Traversal(MakeConfig(maxZoom: 1));
            var result = new TileSelectionResult();
            Assume.That(traversal.SelectTiles(new TileFrameInput(Loaded(Root)), result), Is.True);
            var rendered = result.TilesToRender.ToArray();

            Object.DestroyImmediate(_cameraGo);

            Assert.That(traversal.SelectTiles(new TileFrameInput(Loaded(Root)), result), Is.False);
            Assert.That(result.TilesToRender, Is.EquivalentTo(rendered), "previous selection stays in effect");
        }
    }
}
