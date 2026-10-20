using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Lightweight view of one layer inside a <see cref="NativeMvtTile"/> (parent tile plus index, no copying).
    /// Layer names depend on the tile schema; in OpenMapTiles they are building, transportation, water, landuse,
    /// landcover, park, poi, place, boundary and so on. The view is valid only while the parent tile is not
    /// disposed.
    /// </summary>
    public readonly struct NativeMvtLayer
    {
        private readonly NativeMvtTile _tile;
        private readonly int _index;

        internal NativeMvtLayer(NativeMvtTile tile, int index)
        {
            _tile = tile;
            _index = index;
        }

        /// <summary>
        /// Gets whether the view refers to an existing layer; false for <c>default</c> and for the result of
        /// <see cref="NativeMvtTile.FindLayer"/> when the layer is not found.
        /// </summary>
        public bool IsValid => _tile.IsCreated && _index >= 0 && _index < _tile.LayerCount;

        /// <summary>Gets the layer name.</summary>
        public FixedString64Bytes Name => _tile.LayerNames[_index];

        /// <summary>Gets the MVT layer version.</summary>
        public uint Version => _tile.LayerVersions[_index];

        /// <summary>Gets the extent: the size of the tile-local coordinate range, usually 4096.</summary>
        public uint Extent => _tile.LayerExtents[_index];

        private int FeatureStart => _tile.LayerFeatureRange[_index].x;

        /// <summary>Gets the number of features in the layer.</summary>
        public int FeatureCount => _tile.LayerFeatureRange[_index].y;

        /// <summary>Gets the features of the layer; supports <c>Count</c>, indexing and <c>foreach</c>.</summary>
        public FeatureCollection Features => new FeatureCollection(_tile, FeatureStart, FeatureCount);

        // ---- Job-facing raw slices ---------------------------------------------
        // NativeMvtTile storage is internal to GeoFoundry.Core, so jobs in other assemblies cannot hold a whole
        // NativeMvtLayer/NativeMvtFeature in a job field; they need concrete native arrays. These accessors
        // expose exactly that.

        /// <summary>
        /// Gets the geometry types of this layer's features, as a zero-copy view
        /// (<see cref="NativeArray{T}.GetSubArray"/>) suitable for a <c>[ReadOnly]</c> job field. Indexed by
        /// feature index within the layer.
        /// </summary>
        public NativeArray<MvtGeomType> FeatureGeomTypesSlice => _tile.FeatureGeomTypes.GetSubArray(FeatureStart, FeatureCount);

        /// <summary>
        /// Gets the (start, count) ring ranges of this layer's features, as indices into <see cref="Rings"/>.
        /// Zero-copy view, like <see cref="FeatureGeomTypesSlice"/>.
        /// </summary>
        public NativeArray<int2> FeatureRingRangeSlice => _tile.FeatureRingRange.GetSubArray(FeatureStart, FeatureCount);

        /// <summary>Gets the ids of this layer's features; a zero-copy view like <see cref="FeatureGeomTypesSlice"/>.</summary>
        public NativeArray<ulong> FeatureIdsSlice => _tile.FeatureIds.GetSubArray(FeatureStart, FeatureCount);

        /// <summary>
        /// Gets the tile-wide array of (pointStart, pointCount) ring ranges into <see cref="RingPoints"/>. Not sliced
        /// per layer: <see cref="FeatureRingRangeSlice"/> holds absolute indices into it.
        /// </summary>
        public NativeArray<int2> Rings => _tile.Rings;

        /// <summary>Gets the tile-wide array of the points of all rings (see <see cref="Rings"/>).</summary>
        public NativeArray<int2> RingPoints => _tile.RingPoints;

        /// <summary>
        /// Gets the (start, count) tag ranges of this layer's features, as indices into <see cref="Tags"/>.
        /// Zero-copy view, like <see cref="FeatureGeomTypesSlice"/>.
        /// </summary>
        public NativeArray<int2> FeatureTagRangeSlice => _tile.FeatureTagRange.GetSubArray(FeatureStart, FeatureCount);

        /// <summary>
        /// Gets the tile-wide array of (keyIndex, valueIndex) pairs: absolute indices into <see cref="Keys"/> and
        /// <see cref="Values"/> (see <see cref="FeatureTagRangeSlice"/>).
        /// </summary>
        public NativeArray<int2> Tags => _tile.Tags;

        /// <summary>Gets the tile-wide array of property key names (see <see cref="Tags"/>).</summary>
        public NativeArray<FixedString64Bytes> Keys => _tile.Keys;

        /// <summary>Gets the tile-wide array of property values (see <see cref="Tags"/>).</summary>
        public NativeArray<MvtValue> Values => _tile.Values;

        /// <summary>The features of a layer.</summary>
        public readonly struct FeatureCollection
        {
            private readonly NativeMvtTile _tile;
            private readonly int _start;
            private readonly int _count;

            internal FeatureCollection(NativeMvtTile tile, int start, int count)
            {
                _tile = tile;
                _start = start;
                _count = count;
            }

            /// <summary>Gets the number of features.</summary>
            public int Count => _count;

            /// <summary>Gets the feature at <paramref name="index"/> within the layer.</summary>
            public NativeMvtFeature this[int index] => new NativeMvtFeature(_tile, _start + index);

            /// <summary>Returns an allocation-free enumerator over the features.</summary>
            public Enumerator GetEnumerator() => new Enumerator(_tile, _start, _count);

            /// <summary>Allocation-free enumerator over the features of a layer.</summary>
            public struct Enumerator
            {
                private readonly NativeMvtTile _tile;
                private readonly int _start;
                private readonly int _count;
                private int _i;

                internal Enumerator(NativeMvtTile tile, int start, int count)
                {
                    _tile = tile; _start = start; _count = count; _i = -1;
                }

                public NativeMvtFeature Current => new NativeMvtFeature(_tile, _start + _i);
                public bool MoveNext() => ++_i < _count;
            }
        }
    }
}
