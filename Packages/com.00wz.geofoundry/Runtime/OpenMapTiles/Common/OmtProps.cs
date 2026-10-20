using GeoFoundry.Core.Mvt;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Burst-compatible reader of feature properties of one MVT layer, working on the raw slices
    /// (<see cref="NativeMvtLayer.FeatureTagRangeSlice"/>, <see cref="NativeMvtLayer.Tags"/>,
    /// <see cref="NativeMvtLayer.Keys"/>, <see cref="NativeMvtLayer.Values"/>), so it can be a field of a job in this
    /// assembly. Lookups scan the feature's tags linearly, like <see cref="NativeMvtPropertyBag"/>.
    /// </summary>
    internal struct OmtProps
    {
        [ReadOnly] public NativeArray<int2> FeatureTagRange;
        [ReadOnly] public NativeArray<int2> Tags;
        [ReadOnly] public NativeArray<FixedString64Bytes> Keys;
        [ReadOnly] public NativeArray<MvtValue> Values;

        public static OmtProps From(NativeMvtLayer layer) => new OmtProps
        {
            FeatureTagRange = layer.FeatureTagRangeSlice,
            Tags = layer.Tags,
            Keys = layer.Keys,
            Values = layer.Values,
        };

        public int FeatureCount => FeatureTagRange.Length;

        public bool TryGet(int fi, in FixedString64Bytes key, out MvtValue value)
        {
            int2 range = FeatureTagRange[fi];
            for (int i = 0; i < range.y; i++)
            {
                int2 tag = Tags[range.x + i];
                if (Keys[tag.x].Equals(key))
                {
                    value = Values[tag.y];
                    return true;
                }
            }
            value = default;
            return false;
        }

        public bool Has(int fi, in FixedString64Bytes key) => TryGet(fi, key, out _);

        /// <summary>
        /// Gets a property as text: strings as is, numbers formatted (integers without a fractional part), so that
        /// numeric attributes such as <c>admin_level</c> can be matched against string rules.
        /// </summary>
        public bool TryGetText(int fi, in FixedString64Bytes key, out FixedString128Bytes text)
        {
            text = default;
            if (!TryGet(fi, key, out var v)) return false;
            if (v.TryGetRawString(out text)) return true;
            AppendNumber(ref text, v);
            return true;
        }

        /// <summary>Gets a non-empty string property; numbers are formatted as in <see cref="TryGetText"/>.</summary>
        public bool TryGetNonEmpty(int fi, in FixedString64Bytes key, out FixedString128Bytes text) =>
            TryGetText(fi, key, out text) && text.Length > 0;

        /// <summary>
        /// Gets a property as a number. String values holding a number (OpenMapTiles occasionally writes
        /// <c>"1"</c>) are parsed; other strings give <paramref name="fallback"/>.
        /// </summary>
        public double GetNumber(int fi, in FixedString64Bytes key, double fallback)
        {
            if (!TryGet(fi, key, out var v)) return fallback;
            if (v.TryGetRawString(out var s)) return TryParseNumber(s, out double d) ? d : fallback;
            return v.AsDouble();
        }

        public long GetLong(int fi, in FixedString64Bytes key, long fallback) =>
            (long)math.round(GetNumber(fi, key, fallback));

        /// <summary>Whether the property exists and equals <paramref name="value"/> (string or number text).</summary>
        public bool Is(int fi, in FixedString64Bytes key, in FixedString64Bytes value) =>
            TryGetText(fi, key, out var text) && text.Equals(value);

        /// <summary>Whether the property is "truthy": a non-zero number, <c>true</c>, or a non-empty string other
        /// than <c>"0"</c>/<c>"no"</c>/<c>"false"</c>.</summary>
        public bool IsTrue(int fi, in FixedString64Bytes key)
        {
            if (!TryGet(fi, key, out var v)) return false;
            if (v.TryGetRawString(out var s))
            {
                if (s.Length == 0) return false;
                FixedString32Bytes zero = "0", no = "no", f = "false";
                return !(s.Equals(zero) || s.Equals(no) || s.Equals(f));
            }
            return v.AsDouble() != 0;
        }

        /// <summary>
        /// Selects the first non-empty string among <paramref name="keys"/> (a language preference list such as
        /// <c>name:ru</c>, <c>name:en</c>, <c>name</c>).
        /// </summary>
        public bool SelectText(int fi, NativeArray<FixedString64Bytes> keys, out FixedString128Bytes text)
        {
            for (int i = 0; i < keys.Length; i++)
                if (TryGetNonEmpty(fi, keys[i], out text)) return true;
            text = default;
            return false;
        }

        private static void AppendNumber(ref FixedString128Bytes text, MvtValue v)
        {
            switch (v.Type)
            {
                case MvtValue.Kind.Bool:
                    text.Append(v.AsLong() != 0 ? 1 : 0);
                    break;
                case MvtValue.Kind.Float:
                case MvtValue.Kind.Double:
                {
                    double d = v.AsDouble();
                    if (math.abs(d - math.round(d)) < 1e-9 && math.abs(d) < 1e15) text.Append((long)math.round(d));
                    else text.Append((float)d);
                    break;
                }
                default:
                    text.Append(v.AsLong());
                    break;
            }
        }

        /// <summary>Parses an optionally signed decimal number (<c>-12.5</c>) without allocations.</summary>
        public static bool TryParseNumber(in FixedString128Bytes s, out double value)
        {
            value = 0;
            int i = 0, n = s.Length;
            while (i < n && s[i] == ' ') i++;
            bool neg = false;
            if (i < n && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
            bool any = false;
            double result = 0;
            while (i < n && s[i] >= '0' && s[i] <= '9') { result = result * 10 + (s[i] - '0'); i++; any = true; }
            if (i < n && (s[i] == '.' || s[i] == ','))
            {
                i++;
                double scale = 0.1;
                while (i < n && s[i] >= '0' && s[i] <= '9') { result += (s[i] - '0') * scale; scale *= 0.1; i++; any = true; }
            }
            if (!any) return false;
            value = neg ? -result : result;
            return true;
        }
    }
}
