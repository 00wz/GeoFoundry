using System;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// A parsed MVT tile: a fully blittable structure of native arrays, suitable as input for Burst geometry jobs.
    /// Produced by <see cref="MvtParser.Parse(System.ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layout follows the MVT wire model (layers → features → tags as key/value index pairs, geometry as rings
    /// of points) in columnar (CSR) form: tags hold global indices into the tile-wide keys and values (converted
    /// from layer-local indices during parsing), so resolving a property does not require knowing the layer;
    /// rings and their points are the rings of all features of the tile, concatenated.
    /// </para>
    /// <para>
    /// The arrays are allocated with <see cref="Allocator.Persistent"/> and owned by the caller of the parser,
    /// who must call <see cref="Dispose"/>. Layer, feature and property views (<see cref="NativeMvtLayer"/>,
    /// <see cref="NativeMvtFeature"/>, <see cref="NativeMvtPropertyBag"/>) become invalid after that.
    /// </para>
    /// </remarks>
    public struct NativeMvtTile : IDisposable
    {
        internal NativeArray<FixedString64Bytes> LayerNames;
        internal NativeArray<uint> LayerVersions;
        internal NativeArray<uint> LayerExtents;
        internal NativeArray<int2> LayerFeatureRange; // (start, count) in the Feature* arrays

        internal NativeArray<ulong> FeatureIds;
        internal NativeArray<MvtGeomType> FeatureGeomTypes;
        internal NativeArray<int2> FeatureTagRange;   // (start, count) in Tags
        internal NativeArray<int2> FeatureRingRange;  // (start, count) in Rings

        internal NativeArray<int2> Tags;              // (keyIndex, valueIndex): global indices into Keys/Values
        internal NativeArray<FixedString64Bytes> Keys;
        internal NativeArray<MvtValue> Values;

        internal NativeArray<int2> Rings;              // (pointStart, pointCount) in RingPoints
        internal NativeArray<int2> RingPoints;

        /// <summary>Gets whether the tile holds allocated data (false for <c>default</c> and after disposal).</summary>
        public bool IsCreated => LayerNames.IsCreated;

        /// <summary>Gets the number of layers; 0 if the tile is not created.</summary>
        public int LayerCount => IsCreated ? LayerNames.Length : 0;

        /// <summary>Gets all layers of the tile; supports <c>Count</c>, indexing and <c>foreach</c>.</summary>
        public LayerCollection Layers => new LayerCollection(this);

        /// <summary>Returns a view of the layer at <paramref name="index"/>.</summary>
        public NativeMvtLayer GetLayer(int index) => new NativeMvtLayer(this, index);

        /// <summary>
        /// Finds a layer by name. Returns a view whose <see cref="NativeMvtLayer.IsValid"/> is false if not found.
        /// </summary>
        public NativeMvtLayer FindLayer(FixedString64Bytes name)
        {
            for (int i = 0; i < LayerCount; i++)
                if (LayerNames[i].Equals(name)) return GetLayer(i);
            return default;
        }

        /// <summary>Releases all native arrays of the tile. Safe to call on a default or partially created tile.</summary>
        public void Dispose()
        {
            if (LayerNames.IsCreated) LayerNames.Dispose();
            if (LayerVersions.IsCreated) LayerVersions.Dispose();
            if (LayerExtents.IsCreated) LayerExtents.Dispose();
            if (LayerFeatureRange.IsCreated) LayerFeatureRange.Dispose();
            if (FeatureIds.IsCreated) FeatureIds.Dispose();
            if (FeatureGeomTypes.IsCreated) FeatureGeomTypes.Dispose();
            if (FeatureTagRange.IsCreated) FeatureTagRange.Dispose();
            if (FeatureRingRange.IsCreated) FeatureRingRange.Dispose();
            if (Tags.IsCreated) Tags.Dispose();
            if (Keys.IsCreated) Keys.Dispose();
            if (Values.IsCreated) Values.Dispose();
            if (Rings.IsCreated) Rings.Dispose();
            if (RingPoints.IsCreated) RingPoints.Dispose();
        }

        /// <summary>The layers of a tile.</summary>
        public readonly struct LayerCollection
        {
            private readonly NativeMvtTile _tile;
            internal LayerCollection(NativeMvtTile tile) { _tile = tile; }

            /// <summary>Gets the number of layers.</summary>
            public int Count => _tile.LayerCount;

            /// <summary>Gets the layer at <paramref name="index"/>.</summary>
            public NativeMvtLayer this[int index] => new NativeMvtLayer(_tile, index);

            /// <summary>Returns an allocation-free enumerator over the layers.</summary>
            public Enumerator GetEnumerator() => new Enumerator(_tile);

            /// <summary>Allocation-free enumerator over the layers of a tile.</summary>
            public struct Enumerator
            {
                private readonly NativeMvtTile _tile;
                private int _i;
                internal Enumerator(NativeMvtTile tile) { _tile = tile; _i = -1; }
                public NativeMvtLayer Current => new NativeMvtLayer(_tile, _i);
                public bool MoveNext() => ++_i < _tile.LayerCount;
            }
        }
    }
}
