using System.Collections.Generic;
using System.IO;
using System.Text;
using GeoFoundry.Core.Mvt;
using Unity.Mathematics;

namespace GeoFoundry.Tests.EditMode.Vector
{
    /// <summary>
    /// Minimal MVT encoder for parser tests: one layer with one feature. Test-only; generates test tiles.
    /// </summary>
    internal static class MvtTestEncoder
    {
        public static byte[] EncodeSinglePolygonTile(
            string layerName,
            uint extent,
            ulong featureId,
            int2[] ring,
            (string key, MvtValue value)[] properties = null)
        {
            properties ??= System.Array.Empty<(string, MvtValue)>();

            using var layerStream = new MemoryStream();

            // name (1, string)
            WriteTag(layerStream, MvtSpec.LayerName, MvtSpec.WireLen);
            WriteString(layerStream, layerName);

            // keys + values
            var keys = new List<string>();
            var values = new List<MvtValue>();
            var tags = new List<uint>();
            foreach (var (k, v) in properties)
            {
                int keyIndex = keys.IndexOf(k);
                if (keyIndex < 0) { keyIndex = keys.Count; keys.Add(k); }
                int valIndex = values.Count;
                values.Add(v);
                tags.Add((uint)keyIndex);
                tags.Add((uint)valIndex);
            }

            // features (2, len)
            using (var featureStream = new MemoryStream())
            {
                WriteTag(featureStream, MvtSpec.FeatureId, MvtSpec.WireVarint);
                WriteVarint(featureStream, featureId);

                WriteTag(featureStream, MvtSpec.FeatureType, MvtSpec.WireVarint);
                WriteVarint(featureStream, (ulong)MvtGeomType.Polygon);

                if (tags.Count > 0)
                {
                    WriteTag(featureStream, MvtSpec.FeatureTags, MvtSpec.WireLen);
                    using var packed = new MemoryStream();
                    foreach (var t in tags) WriteVarint(packed, t);
                    WriteVarint(featureStream, (ulong)packed.Length);
                    packed.WriteTo(featureStream);
                }

                // geometry (4, len, packed varints)
                WriteTag(featureStream, MvtSpec.FeatureGeometry, MvtSpec.WireLen);
                using (var geom = new MemoryStream())
                {
                    EncodePolygonRing(geom, ring);
                    WriteVarint(featureStream, (ulong)geom.Length);
                    geom.WriteTo(featureStream);
                }

                WriteTag(layerStream, MvtSpec.LayerFeatures, MvtSpec.WireLen);
                WriteVarint(layerStream, (ulong)featureStream.Length);
                featureStream.WriteTo(layerStream);
            }

            // keys (3, string×N)
            foreach (var k in keys)
            {
                WriteTag(layerStream, MvtSpec.LayerKeys, MvtSpec.WireLen);
                WriteString(layerStream, k);
            }

            // values (4, message×N)
            foreach (var v in values)
            {
                WriteTag(layerStream, MvtSpec.LayerValues, MvtSpec.WireLen);
                using var valStream = new MemoryStream();
                EncodeValue(valStream, v);
                WriteVarint(layerStream, (ulong)valStream.Length);
                valStream.WriteTo(layerStream);
            }

            // extent (5, varint)
            WriteTag(layerStream, MvtSpec.LayerExtent, MvtSpec.WireVarint);
            WriteVarint(layerStream, extent);

            // version (15, varint)
            WriteTag(layerStream, MvtSpec.LayerVersion, MvtSpec.WireVarint);
            WriteVarint(layerStream, 2);

            using var tile = new MemoryStream();
            WriteTag(tile, MvtSpec.TileLayers, MvtSpec.WireLen);
            WriteVarint(tile, (ulong)layerStream.Length);
            layerStream.WriteTo(tile);
            return tile.ToArray();
        }

        /// <summary>
        /// Same as <see cref="EncodeSinglePolygonTile"/>, but the geometry is several consecutive rings
        /// (exterior + holes) in a single continuous command stream of one feature; the x/y cursor is not reset
        /// between rings, as the MVT spec requires for a multi-ring Polygon.
        /// </summary>
        public static byte[] EncodeMultiRingPolygonTile(
            string layerName,
            uint extent,
            ulong featureId,
            int2[][] rings,
            (string key, MvtValue value)[] properties = null)
        {
            properties ??= System.Array.Empty<(string, MvtValue)>();

            using var layerStream = new MemoryStream();

            WriteTag(layerStream, MvtSpec.LayerName, MvtSpec.WireLen);
            WriteString(layerStream, layerName);

            var keys = new List<string>();
            var values = new List<MvtValue>();
            var tags = new List<uint>();
            foreach (var (k, v) in properties)
            {
                int keyIndex = keys.IndexOf(k);
                if (keyIndex < 0) { keyIndex = keys.Count; keys.Add(k); }
                int valIndex = values.Count;
                values.Add(v);
                tags.Add((uint)keyIndex);
                tags.Add((uint)valIndex);
            }

            using (var featureStream = new MemoryStream())
            {
                WriteTag(featureStream, MvtSpec.FeatureId, MvtSpec.WireVarint);
                WriteVarint(featureStream, featureId);

                WriteTag(featureStream, MvtSpec.FeatureType, MvtSpec.WireVarint);
                WriteVarint(featureStream, (ulong)MvtGeomType.Polygon);

                if (tags.Count > 0)
                {
                    WriteTag(featureStream, MvtSpec.FeatureTags, MvtSpec.WireLen);
                    using var packed = new MemoryStream();
                    foreach (var t in tags) WriteVarint(packed, t);
                    WriteVarint(featureStream, (ulong)packed.Length);
                    packed.WriteTo(featureStream);
                }

                WriteTag(featureStream, MvtSpec.FeatureGeometry, MvtSpec.WireLen);
                using (var geom = new MemoryStream())
                {
                    EncodeMultiRingPolygon(geom, rings);
                    WriteVarint(featureStream, (ulong)geom.Length);
                    geom.WriteTo(featureStream);
                }

                WriteTag(layerStream, MvtSpec.LayerFeatures, MvtSpec.WireLen);
                WriteVarint(layerStream, (ulong)featureStream.Length);
                featureStream.WriteTo(layerStream);
            }

            foreach (var k in keys)
            {
                WriteTag(layerStream, MvtSpec.LayerKeys, MvtSpec.WireLen);
                WriteString(layerStream, k);
            }

            foreach (var v in values)
            {
                WriteTag(layerStream, MvtSpec.LayerValues, MvtSpec.WireLen);
                using var valStream = new MemoryStream();
                EncodeValue(valStream, v);
                WriteVarint(layerStream, (ulong)valStream.Length);
                valStream.WriteTo(layerStream);
            }

            WriteTag(layerStream, MvtSpec.LayerExtent, MvtSpec.WireVarint);
            WriteVarint(layerStream, extent);

            WriteTag(layerStream, MvtSpec.LayerVersion, MvtSpec.WireVarint);
            WriteVarint(layerStream, 2);

            using var tile = new MemoryStream();
            WriteTag(tile, MvtSpec.TileLayers, MvtSpec.WireLen);
            WriteVarint(tile, (ulong)layerStream.Length);
            layerStream.WriteTo(tile);
            return tile.ToArray();
        }

        /// <summary>
        /// Same as <see cref="EncodeMultiRingPolygonTile"/>, but the feature is a MultiLineString (geometry type
        /// LineString, no ClosePath, so the lines are open).
        /// </summary>
        public static byte[] EncodeMultiLineStringTile(
            string layerName,
            uint extent,
            ulong featureId,
            int2[][] lines,
            (string key, MvtValue value)[] properties = null)
        {
            properties ??= System.Array.Empty<(string, MvtValue)>();

            using var layerStream = new MemoryStream();

            WriteTag(layerStream, MvtSpec.LayerName, MvtSpec.WireLen);
            WriteString(layerStream, layerName);

            var keys = new List<string>();
            var values = new List<MvtValue>();
            var tags = new List<uint>();
            foreach (var (k, v) in properties)
            {
                int keyIndex = keys.IndexOf(k);
                if (keyIndex < 0) { keyIndex = keys.Count; keys.Add(k); }
                int valIndex = values.Count;
                values.Add(v);
                tags.Add((uint)keyIndex);
                tags.Add((uint)valIndex);
            }

            using (var featureStream = new MemoryStream())
            {
                WriteTag(featureStream, MvtSpec.FeatureId, MvtSpec.WireVarint);
                WriteVarint(featureStream, featureId);

                WriteTag(featureStream, MvtSpec.FeatureType, MvtSpec.WireVarint);
                WriteVarint(featureStream, (ulong)MvtGeomType.LineString);

                if (tags.Count > 0)
                {
                    WriteTag(featureStream, MvtSpec.FeatureTags, MvtSpec.WireLen);
                    using var packed = new MemoryStream();
                    foreach (var t in tags) WriteVarint(packed, t);
                    WriteVarint(featureStream, (ulong)packed.Length);
                    packed.WriteTo(featureStream);
                }

                WriteTag(featureStream, MvtSpec.FeatureGeometry, MvtSpec.WireLen);
                using (var geom = new MemoryStream())
                {
                    EncodeMultiLineString(geom, lines);
                    WriteVarint(featureStream, (ulong)geom.Length);
                    geom.WriteTo(featureStream);
                }

                WriteTag(layerStream, MvtSpec.LayerFeatures, MvtSpec.WireLen);
                WriteVarint(layerStream, (ulong)featureStream.Length);
                featureStream.WriteTo(layerStream);
            }

            foreach (var k in keys)
            {
                WriteTag(layerStream, MvtSpec.LayerKeys, MvtSpec.WireLen);
                WriteString(layerStream, k);
            }

            foreach (var v in values)
            {
                WriteTag(layerStream, MvtSpec.LayerValues, MvtSpec.WireLen);
                using var valStream = new MemoryStream();
                EncodeValue(valStream, v);
                WriteVarint(layerStream, (ulong)valStream.Length);
                valStream.WriteTo(layerStream);
            }

            WriteTag(layerStream, MvtSpec.LayerExtent, MvtSpec.WireVarint);
            WriteVarint(layerStream, extent);

            WriteTag(layerStream, MvtSpec.LayerVersion, MvtSpec.WireVarint);
            WriteVarint(layerStream, 2);

            using var tile = new MemoryStream();
            WriteTag(tile, MvtSpec.TileLayers, MvtSpec.WireLen);
            WriteVarint(tile, (ulong)layerStream.Length);
            layerStream.WriteTo(tile);
            return tile.ToArray();
        }

        private static void EncodeMultiLineString(MemoryStream geom, int2[][] lines)
        {
            int x = 0, y = 0;
            foreach (var line in lines)
            {
                WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdMoveTo, 1));
                WriteVarint(geom, ZigZag(line[0].x - x));
                WriteVarint(geom, ZigZag(line[0].y - y));
                x = line[0].x; y = line[0].y;

                int lineToCount = line.Length - 1;
                if (lineToCount > 0)
                {
                    WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdLineTo, lineToCount));
                    for (int i = 1; i <= lineToCount; i++)
                    {
                        WriteVarint(geom, ZigZag(line[i].x - x));
                        WriteVarint(geom, ZigZag(line[i].y - y));
                        x = line[i].x; y = line[i].y;
                    }
                }
                // No ClosePath: a LineString is open.
            }
        }

        private static void EncodeMultiRingPolygon(MemoryStream geom, int2[][] rings)
        {
            int x = 0, y = 0;
            foreach (var ring in rings)
            {
                WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdMoveTo, 1));
                WriteVarint(geom, ZigZag(ring[0].x - x));
                WriteVarint(geom, ZigZag(ring[0].y - y));
                x = ring[0].x; y = ring[0].y;

                int lineToCount = ring.Length - 1;
                if (ring.Length >= 2 && ring[ring.Length - 1].Equals(ring[0])) lineToCount--;
                if (lineToCount > 0)
                {
                    WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdLineTo, lineToCount));
                    for (int i = 1; i <= lineToCount; i++)
                    {
                        WriteVarint(geom, ZigZag(ring[i].x - x));
                        WriteVarint(geom, ZigZag(ring[i].y - y));
                        x = ring[i].x; y = ring[i].y;
                    }
                }
                WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdClosePath, 1));
            }
        }

        private static void EncodePolygonRing(MemoryStream geom, int2[] ring)
        {
            // MoveTo(1)
            WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdMoveTo, 1));
            int x = 0, y = 0;
            WriteVarint(geom, ZigZag(ring[0].x - x));
            WriteVarint(geom, ZigZag(ring[0].y - y));
            x = ring[0].x; y = ring[0].y;

            int lineToCount = ring.Length - 1;
            // If the last point equals the first, skip it: ClosePath closes the ring.
            if (ring.Length >= 2 && ring[ring.Length - 1].Equals(ring[0])) lineToCount--;
            if (lineToCount > 0)
            {
                WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdLineTo, lineToCount));
                for (int i = 1; i <= lineToCount; i++)
                {
                    WriteVarint(geom, ZigZag(ring[i].x - x));
                    WriteVarint(geom, ZigZag(ring[i].y - y));
                    x = ring[i].x; y = ring[i].y;
                }
            }
            // ClosePath
            WriteVarint(geom, (ulong)MvtSpec.CommandIntId(MvtSpec.CmdClosePath, 1));
        }

        private static void EncodeValue(MemoryStream stream, MvtValue v)
        {
            switch (v.Type)
            {
                case MvtValue.Kind.String:
                    WriteTag(stream, MvtSpec.ValueString, MvtSpec.WireLen);
                    WriteString(stream, v.AsString());
                    break;
                case MvtValue.Kind.Int:
                    WriteTag(stream, MvtSpec.ValueInt, MvtSpec.WireVarint);
                    WriteVarint(stream, (ulong)v.AsLong());
                    break;
                case MvtValue.Kind.Bool:
                    WriteTag(stream, MvtSpec.ValueBool, MvtSpec.WireVarint);
                    WriteVarint(stream, v.AsBool() ? 1ul : 0ul);
                    break;
                case MvtValue.Kind.Double:
                    WriteTag(stream, MvtSpec.ValueDouble, MvtSpec.WireI64);
                    var doubleBytes = System.BitConverter.GetBytes(v.AsDouble());
                    stream.Write(doubleBytes, 0, 8);
                    break;
                default: throw new System.NotImplementedException($"Encoder for {v.Type} is not implemented.");
            }
        }

        private static void WriteTag(MemoryStream s, int field, int wire)
        {
            WriteVarint(s, (ulong)((field << 3) | wire));
        }

        private static void WriteVarint(MemoryStream s, ulong v)
        {
            while (v >= 0x80) { s.WriteByte((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }

        private static void WriteString(MemoryStream s, string str)
        {
            var bytes = Encoding.UTF8.GetBytes(str ?? string.Empty);
            WriteVarint(s, (ulong)bytes.Length);
            s.Write(bytes, 0, bytes.Length);
        }

        private static ulong ZigZag(int n) => (ulong)((n << 1) ^ (n >> 31));
    }
}
