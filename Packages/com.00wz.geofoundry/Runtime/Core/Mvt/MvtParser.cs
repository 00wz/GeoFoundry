using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace GeoFoundry.Core.Mvt
{
    /// <summary>
    /// Parses Mapbox Vector Tile bytes into a <see cref="NativeMvtTile"/>: a fully blittable representation of
    /// layers, features, properties and geometry backed by native arrays, ready to be read from Burst jobs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parsing is two-pass within each layer (keys and values first, then features), because the protobuf wire
    /// format does not guarantee field order. The parser is plain managed code (not Burst): it is branch-heavy and
    /// works with strings; a 100 KB tile takes about 1–3 ms on the main thread. Managed scratch lists live only
    /// for one call and are baked into flat native arrays at the end.
    /// </para>
    /// <para>
    /// Stateless and thread-safe. The caller owns the returned tile (allocated with
    /// <see cref="Allocator.Persistent"/>) and must call <see cref="NativeMvtTile.Dispose"/>.
    /// </para>
    /// </remarks>
    public static class MvtParser
    {
        /// <summary>
        /// Parses a tile from a native byte array; see <see cref="Parse(ReadOnlySpan{byte})"/>. The input is not
        /// retained.
        /// </summary>
        public static NativeMvtTile Parse(NativeArray<byte> mvtBytes) =>
            Parse(mvtBytes.AsReadOnlySpan());

        /// <summary>
        /// Parses a tile from a managed byte array; see <see cref="Parse(ReadOnlySpan{byte})"/>. The input is not
        /// retained.
        /// </summary>
        public static NativeMvtTile Parse(byte[] mvtBytes) =>
            Parse(new ReadOnlySpan<byte>(mvtBytes));

        /// <summary>Parses a tile from raw (uncompressed) MVT bytes.</summary>
        /// <param name="mvtBytes">Uncompressed protobuf-encoded tile.</param>
        /// <returns>A new tile owned by the caller; dispose it with <see cref="NativeMvtTile.Dispose"/>.</returns>
        /// <exception cref="InvalidOperationException">The data is truncated or malformed.</exception>
        public static NativeMvtTile Parse(ReadOnlySpan<byte> mvtBytes)
        {
            var acc = new TileAccumulator();
            var reader = new MvtReader(mvtBytes);

            while (!reader.EndReached)
            {
                uint tag = reader.ReadTag();
                int fieldNumber = MvtReader.TagFieldNumber(tag);
                int wireType = MvtReader.TagWireType(tag);

                if (fieldNumber == MvtSpec.TileLayers && wireType == MvtSpec.WireLen)
                {
                    var layerBytes = reader.ReadLengthDelimited();
                    ParseLayer(layerBytes, acc);
                }
                else
                {
                    reader.SkipField(wireType);
                }
            }
            return Bake(acc);
        }

        // ---- Tile-level accumulator ---------------------------------------------
        // Flat managed lists mirroring the NativeMvtTile layout. Tag indices are already global (converted from
        // layer-local ones in ParseLayer), so Bake() just copies everything into native arrays.
        private sealed class TileAccumulator
        {
            public readonly List<FixedString64Bytes> LayerNames = new List<FixedString64Bytes>(8);
            public readonly List<uint> LayerVersions = new List<uint>(8);
            public readonly List<uint> LayerExtents = new List<uint>(8);
            public readonly List<int2> LayerFeatureRange = new List<int2>(8);

            public readonly List<ulong> FeatureIds = new List<ulong>(64);
            public readonly List<MvtGeomType> FeatureGeomTypes = new List<MvtGeomType>(64);
            public readonly List<int2> FeatureTagRange = new List<int2>(64);
            public readonly List<int2> FeatureRingRange = new List<int2>(64);

            public readonly List<int2> Tags = new List<int2>(128);
            public readonly List<FixedString64Bytes> Keys = new List<FixedString64Bytes>(32);
            public readonly List<MvtValue> Values = new List<MvtValue>(32);

            public readonly List<int2> Rings = new List<int2>(128);
            public readonly List<int2> RingPoints = new List<int2>(512);
        }

        // ---- Layer -------------------------------------------------------------

        private static void ParseLayer(ReadOnlySpan<byte> layerBytes, TileAccumulator acc)
        {
            string name = string.Empty;
            uint version = MvtSpec.DefaultVersion;
            uint extent = MvtSpec.DefaultExtent;
            var localKeys = new List<string>();
            var localValues = new List<MvtValue>();

            // Protobuf does not guarantee field order: remember feature offset and length, and parse features at the
            // end, once all keys and values are collected.
            var featureSpans = new List<(int offset, int length)>();
            var reader = new MvtReader(layerBytes);

            while (!reader.EndReached)
            {
                uint tag = reader.ReadTag();
                int fieldNumber = MvtReader.TagFieldNumber(tag);
                int wireType = MvtReader.TagWireType(tag);

                switch (fieldNumber)
                {
                    case MvtSpec.LayerName when wireType == MvtSpec.WireLen:
                        name = reader.ReadString(); break;

                    case MvtSpec.LayerVersion when wireType == MvtSpec.WireVarint:
                        version = reader.ReadVarint32(); break;

                    case MvtSpec.LayerExtent when wireType == MvtSpec.WireVarint:
                        extent = reader.ReadVarint32(); break;

                    case MvtSpec.LayerKeys when wireType == MvtSpec.WireLen:
                        localKeys.Add(reader.ReadString()); break;

                    case MvtSpec.LayerValues when wireType == MvtSpec.WireLen:
                    {
                        var valBytes = reader.ReadLengthDelimited();
                        localValues.Add(ParseValue(valBytes));
                        break;
                    }

                    case MvtSpec.LayerFeatures when wireType == MvtSpec.WireLen:
                    {
                        int len = (int)reader.ReadVarint32();
                        featureSpans.Add((reader.Position, len));
                        reader.Advance(len);
                        break;
                    }

                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }

            // Layer-local key/value indices become global indices into acc.Keys/acc.Values by adding an offset.
            int keysBase = acc.Keys.Count;
            int valuesBase = acc.Values.Count;
            for (int i = 0; i < localKeys.Count; i++)
            {
                FixedString64Bytes fs = default;
                fs.CopyFromTruncated(localKeys[i]);
                acc.Keys.Add(fs);
            }
            acc.Values.AddRange(localValues);

            int featureStart = acc.FeatureIds.Count;
            for (int i = 0; i < featureSpans.Count; i++)
            {
                var (off, len) = featureSpans[i];
                ParseFeature(layerBytes.Slice(off, len), keysBase, localKeys.Count, valuesBase, localValues.Count, acc);
            }
            int featureCount = acc.FeatureIds.Count - featureStart;

            FixedString64Bytes layerNameFs = default;
            layerNameFs.CopyFromTruncated(name);
            acc.LayerNames.Add(layerNameFs);
            acc.LayerVersions.Add(version);
            acc.LayerExtents.Add(extent);
            acc.LayerFeatureRange.Add(new int2(featureStart, featureCount));
        }

        // ---- Value -------------------------------------------------------------

        private static MvtValue ParseValue(ReadOnlySpan<byte> valBytes)
        {
            var reader = new MvtReader(valBytes);
            while (!reader.EndReached)
            {
                uint tag = reader.ReadTag();
                int fieldNumber = MvtReader.TagFieldNumber(tag);
                int wireType = MvtReader.TagWireType(tag);

                switch (fieldNumber)
                {
                    case MvtSpec.ValueString when wireType == MvtSpec.WireLen:
                        return MvtValue.OfString(reader.ReadString());
                    case MvtSpec.ValueFloat when wireType == MvtSpec.WireI32:
                        return MvtValue.OfFloat(reader.ReadFloat());
                    case MvtSpec.ValueDouble when wireType == MvtSpec.WireI64:
                        return MvtValue.OfDouble(reader.ReadDouble());
                    case MvtSpec.ValueInt when wireType == MvtSpec.WireVarint:
                        return MvtValue.OfInt((long)reader.ReadVarint64());
                    case MvtSpec.ValueUInt when wireType == MvtSpec.WireVarint:
                        return MvtValue.OfUInt(reader.ReadVarint64());
                    case MvtSpec.ValueSInt when wireType == MvtSpec.WireVarint:
                        return MvtValue.OfSInt(reader.ReadZigZag64());
                    case MvtSpec.ValueBool when wireType == MvtSpec.WireVarint:
                        return MvtValue.OfBool(reader.ReadBool());
                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }
            return MvtValue.OfString(string.Empty);
        }

        // ---- Feature -----------------------------------------------------------

        private static void ParseFeature(
            ReadOnlySpan<byte> featureBytes,
            int keysBase, int localKeysCount, int valuesBase, int localValuesCount,
            TileAccumulator acc)
        {
            ulong id = 0;
            MvtGeomType geomType = MvtGeomType.Unknown;
            var localTags = new List<uint>(8);
            var geometry = new List<uint>(32);

            var reader = new MvtReader(featureBytes);
            while (!reader.EndReached)
            {
                uint tag = reader.ReadTag();
                int fieldNumber = MvtReader.TagFieldNumber(tag);
                int wireType = MvtReader.TagWireType(tag);

                switch (fieldNumber)
                {
                    case MvtSpec.FeatureId when wireType == MvtSpec.WireVarint:
                        id = reader.ReadVarint64(); break;

                    case MvtSpec.FeatureType when wireType == MvtSpec.WireVarint:
                        geomType = (MvtGeomType)reader.ReadVarint32(); break;

                    case MvtSpec.FeatureTags when wireType == MvtSpec.WireLen:
                        ReadPackedVarintsInto(reader.ReadLengthDelimited(), localTags); break;

                    case MvtSpec.FeatureGeometry when wireType == MvtSpec.WireLen:
                        ReadPackedVarintsInto(reader.ReadLengthDelimited(), geometry); break;

                    default:
                        reader.SkipField(wireType);
                        break;
                }
            }

            int tagStart = acc.Tags.Count;
            for (int i = 0; i + 1 < localTags.Count; i += 2)
            {
                int localKeyIdx = (int)localTags[i];
                int localValIdx = (int)localTags[i + 1];
                if (localKeyIdx >= localKeysCount || localValIdx >= localValuesCount) continue;
                acc.Tags.Add(new int2(keysBase + localKeyIdx, valuesBase + localValIdx));
            }
            int tagCount = acc.Tags.Count - tagStart;

            int ringStart = acc.Rings.Count;
            DecodeGeometryInto(geometry, geomType, acc);
            int ringCount = acc.Rings.Count - ringStart;

            acc.FeatureIds.Add(id);
            acc.FeatureGeomTypes.Add(geomType);
            acc.FeatureTagRange.Add(new int2(tagStart, tagCount));
            acc.FeatureRingRange.Add(new int2(ringStart, ringCount));
        }

        private static void ReadPackedVarintsInto(ReadOnlySpan<byte> packed, List<uint> output)
        {
            var inner = new MvtReader(packed);
            while (!inner.EndReached) output.Add(inner.ReadVarint32());
        }

        // ---- Geometry decoding ------------------------------------------------

        private static void DecodeGeometryInto(List<uint> raw, MvtGeomType type, TileAccumulator acc)
        {
            if (raw.Count == 0) return;

            var current = new List<int2>(32);
            int x = 0, y = 0;
            int i = 0;

            while (i < raw.Count)
            {
                uint cmdInt = raw[i++];
                int cmd = (int)(cmdInt & 0x7);
                int count = (int)(cmdInt >> 3);

                if (cmd == MvtSpec.CmdMoveTo)
                {
                    // A new sub-geometry starts; flush the previous one, if any.
                    FlushRing(current, acc);
                    for (int k = 0; k < count && i + 1 < raw.Count; k++)
                    {
                        x += DecodeZigZag(raw[i++]);
                        y += DecodeZigZag(raw[i++]);
                        current.Add(new int2(x, y));
                    }
                    // For MultiPoint, each MoveTo point becomes a separate single-point ring.
                    if (type == MvtGeomType.Point && count > 1)
                    {
                        for (int k = 0; k < current.Count; k++)
                        {
                            int start = acc.RingPoints.Count;
                            acc.RingPoints.Add(current[k]);
                            acc.Rings.Add(new int2(start, 1));
                        }
                        current.Clear();
                    }
                }
                else if (cmd == MvtSpec.CmdLineTo)
                {
                    for (int k = 0; k < count && i + 1 < raw.Count; k++)
                    {
                        x += DecodeZigZag(raw[i++]);
                        y += DecodeZigZag(raw[i++]);
                        current.Add(new int2(x, y));
                    }
                }
                else if (cmd == MvtSpec.CmdClosePath)
                {
                    // Close the ring: append the first point unless it already equals the last one.
                    if (current.Count > 0)
                    {
                        var first = current[0];
                        if (!current[current.Count - 1].Equals(first))
                            current.Add(first);
                        FlushRing(current, acc);
                    }
                }
                else
                {
                    // Unknown command: stop decoding.
                    break;
                }
            }

            FlushRing(current, acc);
        }

        private static void FlushRing(List<int2> current, TileAccumulator acc)
        {
            if (current.Count == 0) return;
            int start = acc.RingPoints.Count;
            acc.RingPoints.AddRange(current);
            acc.Rings.Add(new int2(start, current.Count));
            current.Clear();
        }

        private static int DecodeZigZag(uint n) => unchecked((int)((n >> 1) ^ (uint)-(int)(n & 1)));

        // ---- Bake: managed scratch → NativeMvtTile -----------------------------

        private static NativeMvtTile Bake(TileAccumulator acc)
        {
            return new NativeMvtTile
            {
                LayerNames = ToNativeArray(acc.LayerNames),
                LayerVersions = ToNativeArray(acc.LayerVersions),
                LayerExtents = ToNativeArray(acc.LayerExtents),
                LayerFeatureRange = ToNativeArray(acc.LayerFeatureRange),

                FeatureIds = ToNativeArray(acc.FeatureIds),
                FeatureGeomTypes = ToNativeArray(acc.FeatureGeomTypes),
                FeatureTagRange = ToNativeArray(acc.FeatureTagRange),
                FeatureRingRange = ToNativeArray(acc.FeatureRingRange),

                Tags = ToNativeArray(acc.Tags),
                Keys = ToNativeArray(acc.Keys),
                Values = ToNativeArray(acc.Values),

                Rings = ToNativeArray(acc.Rings),
                RingPoints = ToNativeArray(acc.RingPoints),
            };
        }

        private static NativeArray<T> ToNativeArray<T>(List<T> source) where T : unmanaged =>
            new NativeArray<T>(source.ToArray(), Allocator.Persistent);
    }

    /// <summary>
    /// Extension for <see cref="NativeArray{T}"/>: a read-only span over its memory without allocating.
    /// </summary>
    internal static class MvtParserExtensions
    {
        public static ReadOnlySpan<byte> AsReadOnlySpan(this NativeArray<byte> array)
        {
            unsafe
            {
                return new ReadOnlySpan<byte>(
                    Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(array),
                    array.Length);
            }
        }
    }
}
