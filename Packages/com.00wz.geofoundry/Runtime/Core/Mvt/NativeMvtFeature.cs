using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Lightweight view of one feature inside a <see cref="NativeMvtTile"/> (parent tile plus index, no copying):
    /// id, geometry type, properties and geometry rings in tile-local coordinates.
    /// </summary>
    /// <remarks>
    /// Tile-local coordinates are int2(x, y) with the origin at the top-left corner, Y pointing down, in the range
    /// [0, layer extent]. Convert them to Mercator meters with
    /// <see cref="MvtTileCoords.ToMercator(int2, GeoFoundry.Core.Coordinates.TileCoord, uint)"/>.
    /// The view is valid only while the parent tile is not disposed.
    /// </remarks>
    public readonly struct NativeMvtFeature
    {
        private readonly NativeMvtTile _tile;
        private readonly int _index;

        internal NativeMvtFeature(NativeMvtTile tile, int index)
        {
            _tile = tile;
            _index = index;
        }

        /// <summary>Gets the feature id; 0 if the tile does not specify one.</summary>
        public ulong Id => _tile.FeatureIds[_index];

        /// <summary>Gets the geometry type.</summary>
        public MvtGeomType GeometryType => _tile.FeatureGeomTypes[_index];

        /// <summary>Gets the feature properties, decoded from its tags and the keys and values of its layer.</summary>
        public NativeMvtPropertyBag Properties => new NativeMvtPropertyBag(_tile, _tile.FeatureTagRange[_index]);

        private int RingStart => _tile.FeatureRingRange[_index].x;
        private int RingCount => _tile.FeatureRingRange[_index].y;

        /// <summary>
        /// Gets the geometry rings in tile-local coordinates: for Polygon, exterior rings and holes; for LineString,
        /// separate lines; for Point, one element per point.
        /// </summary>
        public RingCollection Geometry => new RingCollection(_tile, RingStart, RingCount);

        /// <summary>The rings of a feature's geometry.</summary>
        public readonly struct RingCollection
        {
            private readonly NativeMvtTile _tile;
            private readonly int _start;
            private readonly int _count;

            internal RingCollection(NativeMvtTile tile, int start, int count)
            {
                _tile = tile; _start = start; _count = count;
            }

            /// <summary>Gets the number of rings.</summary>
            public int Count => _count;

            /// <summary>Gets the points of a ring as a view (no copy) into the tile's shared point buffer.</summary>
            public NativeArray<int2> this[int index]
            {
                get
                {
                    var r = _tile.Rings[_start + index];
                    return _tile.RingPoints.GetSubArray(r.x, r.y);
                }
            }
        }
    }
}
