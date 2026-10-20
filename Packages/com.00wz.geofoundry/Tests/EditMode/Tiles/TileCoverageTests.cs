using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Tiles.TileSelection;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>Tests <see cref="TileCoverage"/>: the covered-tile map and lookup of covering tiles.</summary>
    public sealed class TileCoverageTests
    {
        private static readonly TileCoord Root = new TileCoord(0, 0, 0);

        private static TileCoverage Build(params TileCoord[] loaded)
        {
            var coverage = new TileCoverage();
            coverage.Rebuild(new HashSet<TileCoord>(loaded));
            return coverage;
        }

        [Test]
        public void Rebuild_EmptySet_NothingCovered()
        {
            var coverage = Build();

            Assert.That(coverage.IsCovered(Root), Is.False);
        }

        [Test]
        public void Rebuild_SingleLoadedTile_IsCoveredAndLoaded_ParentIsNot()
        {
            var tile = Root.GetChild(0);
            var coverage = Build(tile);

            Assert.That(coverage.IsCovered(tile), Is.True);
            Assert.That(coverage.IsResolved(tile), Is.True);
            Assert.That(coverage.IsCovered(Root), Is.False);
        }

        [Test]
        public void Rebuild_AllFourChildrenLoaded_ParentCoveredButNotLoaded()
        {
            var coverage = Build(Root.Children());

            Assert.That(coverage.IsCovered(Root), Is.True);
            Assert.That(coverage.IsResolved(Root), Is.False);
        }

        [Test]
        public void Rebuild_ThreeOfFourChildren_ParentNotCovered()
        {
            var coverage = Build(Root.Children().Take(3).ToArray());

            Assert.That(coverage.IsCovered(Root), Is.False);
        }

        [Test]
        public void Rebuild_CascadesThroughSeveralLevels()
        {
            var grandChildren = Root.Children().SelectMany(c => c.Children()).ToArray();
            var coverage = Build(grandChildren);

            foreach (var c in Root.Children())
            {
                Assert.That(coverage.IsCovered(c), Is.True);
                Assert.That(coverage.IsResolved(c), Is.False);
            }
            Assert.That(coverage.IsCovered(Root), Is.True);
        }

        [Test]
        public void Rebuild_ParentAndChildrenLoaded_ParentStaysLoaded_InAnyOrder()
        {
            var children = Root.Children();
            var parentFirst = new List<TileCoord> { Root };
            parentFirst.AddRange(children);
            var parentLast = new List<TileCoord>(children) { Root };

            foreach (var order in new[] { parentFirst, parentLast })
            {
                var coverage = new TileCoverage();
                coverage.Rebuild(new HashSet<TileCoord>(order));

                Assert.That(coverage.IsCovered(Root), Is.True);
                Assert.That(coverage.IsResolved(Root), Is.True, "the resolved flag comes from resolvedTiles, not from insertion order");
            }
        }

        [Test]
        public void CollectCovering_LoadedTile_ReturnsItself()
        {
            var coverage = Build(Root, Root.GetChild(0));
            var result = new HashSet<TileCoord>();
            var skipped = new HashSet<TileCoord>();

            coverage.CollectCovering(Root, result, skipped);

            Assert.That(result, Is.EquivalentTo(new[] { Root }));
            Assert.That(skipped, Is.Empty);
        }

        [Test]
        public void CollectCovering_NotLoadedTile_ReturnsMinimalCoveringSet()
        {
            // child0 is not loaded but is covered by its 4 children; the other 3 children of the root are loaded.
            var children = Root.Children();
            var loaded = new List<TileCoord>(children.Skip(1)); // child1..3
            loaded.AddRange(children[0].Children());
            var coverage = Build(loaded.ToArray());
            var result = new HashSet<TileCoord>();
            var skipped = new HashSet<TileCoord>();

            coverage.CollectCovering(Root, result, skipped);

            Assert.That(result, Is.EquivalentTo(loaded));
            Assert.That(result, Has.Count.EqualTo(7));
            Assert.That(skipped, Is.Empty);
        }

        [Test]
        public void CollectCovering_NotCoveredTile_Throws()
        {
            var coverage = Build(Root.GetChild(0));

            Assert.Throws<InvalidOperationException>(() =>
                coverage.CollectCovering(Root, new HashSet<TileCoord>(), new HashSet<TileCoord>()));
        }

        // ---- Skipped tiles -----------------------------------------------------

        private static TileCoverage Build(TileCoord[] resolved, TileCoord[] skipped)
        {
            var coverage = new TileCoverage();
            coverage.Rebuild(new HashSet<TileCoord>(resolved), new HashSet<TileCoord>(skipped));
            return coverage;
        }

        [Test]
        public void Rebuild_SkippedTile_IsCoveredButNotResolved()
        {
            var tile = Root.GetChild(0);
            var coverage = Build(new TileCoord[0], new[] { tile });

            Assert.That(coverage.IsCovered(tile), Is.True);
            Assert.That(coverage.IsResolved(tile), Is.False);
        }

        [Test]
        public void Rebuild_ResolvedAndSkippedChildren_CascadeToParent()
        {
            var children = Root.Children();
            // child0 is covered by grandchildren (2 resolved, 2 skipped); child1 is resolved; child2..3 are skipped.
            var grandChildren = children[0].Children();
            var resolved = new[] { grandChildren[0], grandChildren[1], children[1] };
            var skipped = new[] { grandChildren[2], grandChildren[3], children[2], children[3] };
            var coverage = Build(resolved, skipped);

            Assert.That(coverage.IsCovered(children[0]), Is.True);
            Assert.That(coverage.IsCovered(Root), Is.True);
            Assert.That(coverage.IsResolved(Root), Is.False);
        }

        [Test]
        public void CollectCovering_SplitsResolvedAndSkipped()
        {
            var children = Root.Children();
            var coverage = Build(new[] { children[0], children[1] }, new[] { children[2], children[3] });
            var resolved = new HashSet<TileCoord>();
            var skipped = new HashSet<TileCoord>();

            coverage.CollectCovering(Root, resolved, skipped);

            Assert.That(resolved, Is.EquivalentTo(new[] { children[0], children[1] }));
            Assert.That(skipped, Is.EquivalentTo(new[] { children[2], children[3] }));
        }

        [Test]
        public void CollectCovering_TileBothResolvedAndSkipped_GoesToResolvedOnly()
        {
            var tile = Root.GetChild(0);
            var coverage = Build(new[] { tile }, new[] { tile });
            var resolved = new HashSet<TileCoord>();
            var skipped = new HashSet<TileCoord>();

            coverage.CollectCovering(tile, resolved, skipped);

            Assert.That(resolved, Is.EquivalentTo(new[] { tile }), "resolved wins over skipped: the tile has content");
            Assert.That(skipped, Is.Empty);
        }

        // ---- Coverage by resolved tiles only ---------------------------------

        [Test]
        public void IsCoveredByResolved_ResolvedDescendantsOnly_CascadesThroughSeveralLevels()
        {
            var children = Root.Children();
            var resolved = children[0].Children().Concat(children.Skip(1)).ToArray();
            var coverage = Build(resolved, new TileCoord[0]);

            Assert.That(coverage.IsCoveredByResolved(children[0]), Is.True);
            Assert.That(coverage.IsCoveredByResolved(Root), Is.True);
        }

        [Test]
        public void IsCoveredByResolved_SkippedTileInCoverage_CoveredButNotByResolvedOnly()
        {
            var children = Root.Children();
            var coverage = Build(children.Take(3).ToArray(), new[] { children[3] });

            Assert.That(coverage.IsCovered(Root), Is.True);
            Assert.That(coverage.IsCoveredByResolved(Root), Is.False, "a skipped tile leaves a hole");
            Assert.That(coverage.IsCoveredByResolved(children[3]), Is.False);
            Assert.That(coverage.IsCoveredByResolved(children[0]), Is.True);
        }

        [Test]
        public void CollectResolvedCovering_SkippedTileWithResolvedChildren_ReturnsChildren()
        {
            var tile = Root.GetChild(0);
            var coverage = Build(tile.Children(), new[] { tile });
            var dest = new HashSet<TileCoord>();

            coverage.CollectResolvedCovering(tile, dest);

            Assert.That(dest, Is.EquivalentTo(tile.Children()), "a skipped tile with resolved children is not a hole");
        }

        [Test]
        public void CollectResolvedCovering_NotCoveredByResolved_Throws()
        {
            var children = Root.Children();
            var coverage = Build(children.Take(3).ToArray(), new[] { children[3] });

            Assert.Throws<InvalidOperationException>(() => coverage.CollectResolvedCovering(Root, new HashSet<TileCoord>()));
        }
    }
}
