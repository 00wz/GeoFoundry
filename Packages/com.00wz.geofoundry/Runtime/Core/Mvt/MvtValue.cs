using Unity.Collections;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Value of an MVT feature property. The MVT specification defines it as a union of 7 types
    /// (string, float, double, int64, uint64, sint64, bool); here it is a single struct with a discriminator.
    /// Fully blittable (<see cref="FixedString128Bytes"/> instead of a managed string), so it can be stored in
    /// native arrays and job fields.
    /// </summary>
    public readonly struct MvtValue
    {
        /// <summary>The MVT value type.</summary>
        public enum Kind : byte { String, Float, Double, Int, UInt, SInt, Bool }

        /// <summary>The type of the stored value.</summary>
        public readonly Kind Type;

        // Not a real union (that would require an explicit layout). Three fields suffice:
        // FixedString128Bytes, long (int/uint/sint/bool), double (float/double).
        private readonly FixedString128Bytes _str;
        private readonly long _long;
        private readonly double _dbl;

        private MvtValue(Kind type, FixedString128Bytes s = default, long l = 0, double d = 0)
        {
            Type = type; _str = s; _long = l; _dbl = d;
        }

        /// <summary>
        /// Creates a string value. Tag values are short in practice (names, classes, hex colors); strings longer
        /// than the <see cref="FixedString128Bytes"/> capacity (rare, such as long descriptions) are truncated
        /// with <see cref="FixedStringMethods.CopyFromTruncated{T}(ref T, string)"/> without throwing.
        /// </summary>
        public static MvtValue OfString(string s)
        {
            FixedString128Bytes fs = default;
            if (!string.IsNullOrEmpty(s)) fs.CopyFromTruncated(s);
            return new MvtValue(Kind.String, s: fs);
        }
        /// <summary>Creates a float value.</summary>
        public static MvtValue OfFloat(float f) => new MvtValue(Kind.Float, d: f);
        /// <summary>Creates a double value.</summary>
        public static MvtValue OfDouble(double d) => new MvtValue(Kind.Double, d: d);
        /// <summary>Creates an int64 value.</summary>
        public static MvtValue OfInt(long v) => new MvtValue(Kind.Int, l: v);
        /// <summary>Creates a uint64 value.</summary>
        public static MvtValue OfUInt(ulong v) => new MvtValue(Kind.UInt, l: unchecked((long)v));
        /// <summary>Creates a sint64 (zigzag-encoded) value.</summary>
        public static MvtValue OfSInt(long v) => new MvtValue(Kind.SInt, l: v);
        /// <summary>Creates a bool value.</summary>
        public static MvtValue OfBool(bool b) => new MvtValue(Kind.Bool, l: b ? 1 : 0);

        /// <summary>Returns the string value, or the text form of a non-string value. Not Burst-compatible.</summary>
        public string AsString() => Type == Kind.String ? _str.ToString() : ToString();

        /// <summary>
        /// Gets the raw <see cref="FixedString128Bytes"/> without going through a managed <see cref="string"/>;
        /// the only way to read string properties from a <c>[BurstCompile]</c> job (the managed
        /// <see cref="AsString"/> and <see cref="ToString"/> do not compile under Burst). Returns false for
        /// non-string values.
        /// </summary>
        public bool TryGetRawString(out FixedString128Bytes raw)
        {
            if (Type == Kind.String) { raw = _str; return true; }
            raw = default;
            return false;
        }
        /// <summary>Returns the value as a double: numbers as is, bool as 0 or 1, strings as 0.</summary>
        public double AsDouble() => Type switch
        {
            Kind.Float or Kind.Double => _dbl,
            Kind.Int or Kind.SInt => _long,
            Kind.UInt => unchecked((ulong)_long),
            Kind.Bool => _long,
            _ => 0,
        };
        /// <summary>Returns the value as a long: floating-point values truncated, bool as 0 or 1, strings as 0.</summary>
        public long AsLong() => Type switch
        {
            Kind.Float or Kind.Double => (long)_dbl,
            Kind.Int or Kind.SInt or Kind.UInt or Kind.Bool => _long,
            _ => 0,
        };
        /// <summary>
        /// Returns the value as a bool: non-zero numbers are true; strings are parsed with
        /// <see cref="bool.TryParse(string, out bool)"/>.
        /// </summary>
        public bool AsBool() => Type switch
        {
            Kind.Bool => _long != 0,
            Kind.Int or Kind.SInt => _long != 0,
            Kind.UInt => _long != 0,
            Kind.Float or Kind.Double => _dbl != 0,
            Kind.String => bool.TryParse(_str.ToString(), out var b) && b,
            _ => false,
        };

        /// <summary>Returns the value as text, using the invariant culture for numbers.</summary>
        public override string ToString() => Type switch
        {
            Kind.String => _str.ToString(),
            Kind.Float or Kind.Double => _dbl.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind.Int or Kind.SInt => _long.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind.UInt => unchecked((ulong)_long).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind.Bool => _long != 0 ? "true" : "false",
            _ => string.Empty,
        };
    }
}
