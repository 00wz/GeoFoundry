using System;
using System.Collections.Generic;
using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Tiles.TileSelection
{
    /// <summary>
    /// Set of covered tiles. A tile is covered if it is resolved, skipped, or all 4 of its direct children are
    /// covered (the conditions may hold at once). A naive "is the root covered" check would walk all leaf
    /// descendants (up to 4^MaxZoom), so the covered set is built in advance, bottom up, in one pass over the base
    /// tiles (<see cref="Rebuild"/>); after that <see cref="IsCovered"/> is <c>O(1)</c>.
    ///
    /// There are two base cases of coverage:
    /// <list type="bullet">
    /// <item>Resolved: this class does not care why a tile counts as resolved (actually loaded, a failed load the
    ///   caller chose not to retry, or anything else); that is entirely the caller's decision (see
    ///   <see cref="TileQuadtreeTraversal"/>, which passes the union of loaded and failed tiles).</item>
    /// <item>Skipped: the tile is deliberately not needed (e.g. denied by the budget); it has no content but still
    ///   covers its area for coverage purposes (rendered as empty space).</item>
    /// </list>
    /// If a tile is both resolved and skipped, resolved wins: it has content and is not a hole
    /// (see <see cref="CollectCovering"/>).
    ///
    /// The tiles covering a covered tile (<see cref="CollectCovering"/>) are the tile itself if it is resolved or
    /// skipped, otherwise all tiles covering its 4 children. Rendered together (skipped ones as empty space), they
    /// fill the tile's whole area without gaps.
    ///
    /// A separate set holds tiles covered only by resolved tiles (<see cref="IsCoveredByResolved"/>): the tile is
    /// resolved or all 4 children are covered only by resolved tiles, i.e. its whole area is filled with content,
    /// without holes.
    /// </summary>
    internal sealed class TileCoverage
    {
        private static readonly HashSet<TileCoord> EmptySet = new();

        private readonly HashSet<TileCoord> _covered = new();
        private readonly HashSet<TileCoord> _coveredByResolved = new();
        private HashSet<TileCoord> _resolved = new();
        private HashSet<TileCoord> _skipped = EmptySet;

        /// <summary>Rebuilds the covered set from resolved tiles only, with no skipped tiles.</summary>
        public void Rebuild(HashSet<TileCoord> resolvedTiles) => Rebuild(resolvedTiles, EmptySet);

        /// <summary>
        /// Rebuilds the covered set from resolved and skipped tiles. Keeps references to both sets without copying,
        /// so they must not be modified while this object is in use.
        /// </summary>
        public void Rebuild(HashSet<TileCoord> resolvedTiles, HashSet<TileCoord> skippedTiles)
        {
            _resolved = resolvedTiles ?? throw new ArgumentNullException(nameof(resolvedTiles));
            _skipped = skippedTiles ?? throw new ArgumentNullException(nameof(skippedTiles));
            _covered.Clear();
            _coveredByResolved.Clear();

            foreach (var tile in _resolved)
            {
                AddBaseTile(_covered, tile);
                AddBaseTile(_coveredByResolved, tile);
            }
            foreach (var tile in _skipped) AddBaseTile(_covered, tile);
        }

        /// <summary>
        /// Adds a base tile to <paramref name="covered"/> and, cascading upward, every ancestor that becomes covered
        /// thanks to it.
        /// </summary>
        private static void AddBaseTile(HashSet<TileCoord> covered, TileCoord tile)
        {
            // Already added by the cascade (covered by its children); its ancestors were handled at that point.
            if (!covered.Add(tile)) return;

            Span<TileCoord> siblings = stackalloc TileCoord[4];
            var current = tile;
            while (!current.IsRoot)
            {
                var parent = current.Parent();
                if (covered.Contains(parent)) break;

                parent.GetChildren(siblings);
                if (!AllIn(covered, siblings)) break;

                covered.Add(parent);
                current = parent;
            }
        }

        private static bool AllIn(HashSet<TileCoord> set, ReadOnlySpan<TileCoord> tiles)
        {
            for (int i = 0; i < tiles.Length; i++)
                if (!set.Contains(tiles[i])) return false;
            return true;
        }

        public bool IsCovered(TileCoord coord) => _covered.Contains(coord);

        /// <summary>Returns whether the tile is covered only by resolved tiles, without holes (see the class docs).</summary>
        public bool IsCoveredByResolved(TileCoord coord) => _coveredByResolved.Contains(coord);

        public bool IsResolved(TileCoord coord) => _resolved.Contains(coord);

        /// <summary>Returns whether all tiles in <paramref name="tiles"/> are covered (usually the 4 children of one parent).</summary>
        public bool AllCovered(ReadOnlySpan<TileCoord> tiles) => AllIn(_covered, tiles);

        /// <summary>
        /// Adds the tiles covering <paramref name="coord"/> (the minimal set of base tiles filling its area without
        /// gaps), split into disjoint sets: resolved ones into <paramref name="resolvedDest"/>, skipped and
        /// unresolved ones into <paramref name="skippedDest"/>. <paramref name="coord"/> must be covered.
        /// </summary>
        public void CollectCovering(TileCoord coord, HashSet<TileCoord> resolvedDest, HashSet<TileCoord> skippedDest)
        {
            if (_resolved.Contains(coord))
            {
                resolvedDest.Add(coord);
                return;
            }

            if (_skipped.Contains(coord))
            {
                skippedDest.Add(coord);
                return;
            }

            if (!_covered.Contains(coord))
                throw new InvalidOperationException($"Tile {coord} is not covered.");

            Span<TileCoord> children = stackalloc TileCoord[4];
            coord.GetChildren(children);
            for (int i = 0; i < 4; i++)
                CollectCovering(children[i], resolvedDest, skippedDest);
        }

        /// <summary>
        /// Adds to <paramref name="dest"/> the resolved tiles covering <paramref name="coord"/> without gaps: the tile
        /// itself if resolved, otherwise, recursively, those covering its 4 children. <paramref name="coord"/> must be
        /// covered only by resolved tiles (<see cref="IsCoveredByResolved"/>). Skipped tiles are ignored: unlike
        /// <see cref="CollectCovering"/>, a skipped tile whose children are all resolved yields its children, not a
        /// hole.
        /// </summary>
        public void CollectResolvedCovering(TileCoord coord, HashSet<TileCoord> dest)
        {
            if (_resolved.Contains(coord))
            {
                dest.Add(coord);
                return;
            }

            if (!_coveredByResolved.Contains(coord))
                throw new InvalidOperationException($"Tile {coord} is not covered by resolved tiles.");

            Span<TileCoord> children = stackalloc TileCoord[4];
            coord.GetChildren(children);
            for (int i = 0; i < 4; i++)
                CollectResolvedCovering(children[i], dest);
        }
    }
}
