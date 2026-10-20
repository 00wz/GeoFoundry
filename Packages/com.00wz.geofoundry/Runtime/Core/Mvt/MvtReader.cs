using System;
using System.Text;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Low-level Protocol Buffers wire-format reader over a <see cref="ReadOnlySpan{T}"/>. A ref struct: it does
    /// not allocate, cannot be stored in fields and cannot be used across awaits. Supports only what MVT needs:
    /// varint, zigzag, length-delimited and fixed32/64 fields.
    /// </summary>
    internal ref struct MvtReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public MvtReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public int Position => _pos;
        public int Length => _data.Length;
        public bool EndReached => _pos >= _data.Length;

        // ---- Wire-tag readers --------------------------------------------------

        /// <summary>Reads next field tag. Returns 0 if end-of-stream.</summary>
        public uint ReadTag()
        {
            if (_pos >= _data.Length) return 0;
            return ReadVarint32();
        }

        public static int TagFieldNumber(uint tag) => (int)(tag >> 3);
        public static int TagWireType(uint tag) => (int)(tag & 0x7);

        // ---- Varint / zigzag ---------------------------------------------------

        public uint ReadVarint32() => unchecked((uint)ReadVarint64());

        public ulong ReadVarint64()
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                if (_pos >= _data.Length)
                    throw new InvalidOperationException("MVT: unexpected end-of-stream while reading varint");
                byte b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 63) throw new InvalidOperationException("MVT: varint too long");
            }
        }

        public int ReadZigZag32()
        {
            uint n = ReadVarint32();
            return unchecked((int)((n >> 1) ^ (uint)-(int)(n & 1)));
        }

        public long ReadZigZag64()
        {
            ulong n = ReadVarint64();
            return unchecked((long)((n >> 1) ^ (ulong)-(long)(n & 1)));
        }

        public bool ReadBool() => ReadVarint64() != 0;

        // ---- Fixed-width readers ----------------------------------------------

        public float ReadFloat()
        {
            EnsureCanRead(4);
            // .NET Standard 2.1 (Unity 6) lacks ReadSingleLittleEndian; use ReadUInt32LittleEndian + Int32BitsToSingle.
            uint bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        public double ReadDouble()
        {
            EnsureCanRead(8);
            ulong bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
            _pos += 8;
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        // ---- Length-delimited --------------------------------------------------

        public string ReadString()
        {
            int len = (int)ReadVarint32();
            EnsureCanRead(len);
            string s = Encoding.UTF8.GetString(_data.Slice(_pos, len));
            _pos += len;
            return s;
        }

        /// <summary>Returns a span over the same buffer without copying; use it right away.</summary>
        public ReadOnlySpan<byte> ReadLengthDelimited()
        {
            int len = (int)ReadVarint32();
            EnsureCanRead(len);
            var slice = _data.Slice(_pos, len);
            _pos += len;
            return slice;
        }

        // ---- Skip --------------------------------------------------------------

        /// <summary>Advances the position by <paramref name="count"/> bytes, skipping a length-delimited field
        /// whose offset and length were saved for deferred parsing.</summary>
        public void Advance(int count)
        {
            EnsureCanRead(count);
            _pos += count;
        }

        /// <summary>Skips the value of a field with the given wire type. Call after <see cref="ReadTag"/>.</summary>
        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case MvtSpec.WireVarint: ReadVarint64(); break;
                case MvtSpec.WireI64:    EnsureCanRead(8); _pos += 8; break;
                case MvtSpec.WireLen:    { int len = (int)ReadVarint32(); EnsureCanRead(len); _pos += len; break; }
                case MvtSpec.WireI32:    EnsureCanRead(4); _pos += 4; break;
                default: throw new InvalidOperationException($"MVT: unknown wire type {wireType}");
            }
        }

        private void EnsureCanRead(int count)
        {
            if (_pos + count > _data.Length)
                throw new InvalidOperationException($"MVT: out of bounds read (need {count} bytes at pos {_pos}, length {_data.Length})");
        }
    }
}
