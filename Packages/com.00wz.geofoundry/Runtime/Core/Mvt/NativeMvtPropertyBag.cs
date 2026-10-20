using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// View of the properties of one feature, with a dictionary-like API (<see cref="TryGetValue"/>,
    /// <see cref="ContainsKey"/>, indexer). Lookups scan the feature's tags linearly: features usually have fewer
    /// than 20 tags, so this is cheaper than a managed dictionary and works in jobs. Valid only while the parent
    /// tile is not disposed.
    /// </summary>
    public readonly struct NativeMvtPropertyBag
    {
        private readonly NativeMvtTile _tile;
        private readonly int2 _tagRange; // (start, count) in Tags

        internal NativeMvtPropertyBag(NativeMvtTile tile, int2 tagRange)
        {
            _tile = tile;
            _tagRange = tagRange;
        }

        /// <summary>Gets the number of properties.</summary>
        public int Count => _tagRange.y;

        /// <summary>Gets the value of the property <paramref name="key"/>. Returns false if there is none.</summary>
        public bool TryGetValue(FixedString64Bytes key, out MvtValue value)
        {
            for (int i = 0; i < _tagRange.y; i++)
            {
                var tag = _tile.Tags[_tagRange.x + i];
                if (_tile.Keys[tag.x].Equals(key))
                {
                    value = _tile.Values[tag.y];
                    return true;
                }
            }
            value = default;
            return false;
        }

        /// <summary>Returns whether the feature has the property <paramref name="key"/>.</summary>
        public bool ContainsKey(FixedString64Bytes key) => TryGetValue(key, out _);

        /// <summary>Gets the value of the property <paramref name="key"/>.</summary>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">The property does not exist.</exception>
        public MvtValue this[FixedString64Bytes key]
        {
            get
            {
                if (TryGetValue(key, out var v)) return v;
                throw new System.Collections.Generic.KeyNotFoundException(key.ToString());
            }
        }
    }
}
