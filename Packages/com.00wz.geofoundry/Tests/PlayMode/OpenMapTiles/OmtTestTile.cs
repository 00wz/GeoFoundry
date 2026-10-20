using System.Collections;
using System.Collections.Generic;
using System.Threading;
using GeoFoundry.Core.Mvt;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tests.PlayMode.OpenMapTiles
{
    /// <summary>
    /// Builds a single-layer <see cref="NativeMvtTile"/> with arbitrary features for the OpenMapTiles tests. Uses the
    /// internal fields of the tile (InternalsVisibleTo from GeoFoundry.Core).
    /// </summary>
    internal sealed class OmtTestTile
    {
        private readonly string _layer;
        private readonly List<MvtGeomType> _types = new List<MvtGeomType>();
        private readonly List<int2> _ringRanges = new List<int2>();
        private readonly List<int2> _tagRanges = new List<int2>();
        private readonly List<int2> _rings = new List<int2>();
        private readonly List<int2> _points = new List<int2>();
        private readonly List<int2> _tags = new List<int2>();
        private readonly List<FixedString64Bytes> _keys = new List<FixedString64Bytes>();
        private readonly List<MvtValue> _values = new List<MvtValue>();

        public OmtTestTile(string layer) => _layer = layer;

        public OmtTestTile Add(MvtGeomType type, int2[][] rings, params (string key, object value)[] props)
        {
            _types.Add(type);
            _ringRanges.Add(new int2(_rings.Count, rings?.Length ?? 0));
            if (rings != null)
            {
                foreach (var ring in rings)
                {
                    _rings.Add(new int2(_points.Count, ring.Length));
                    _points.AddRange(ring);
                }
            }
            _tagRanges.Add(new int2(_tags.Count, props.Length));
            foreach (var (key, value) in props)
            {
                FixedString64Bytes k = default;
                k.CopyFromTruncated(key);
                int ki = _keys.IndexOf(k);
                if (ki < 0) { ki = _keys.Count; _keys.Add(k); }
                _tags.Add(new int2(ki, _values.Count));
                _values.Add(value switch
                {
                    string s => MvtValue.OfString(s),
                    int i => MvtValue.OfInt(i),
                    long l => MvtValue.OfInt(l),
                    float f => MvtValue.OfFloat(f),
                    double d => MvtValue.OfDouble(d),
                    bool b => MvtValue.OfBool(b),
                    _ => MvtValue.OfString(value?.ToString()),
                });
            }
            return this;
        }

        public OmtTestTile Point(int x, int y, params (string key, object value)[] props) =>
            Add(MvtGeomType.Point, new[] { new[] { new int2(x, y) } }, props);

        public OmtTestTile Line(int2[] points, params (string key, object value)[] props) =>
            Add(MvtGeomType.LineString, new[] { points }, props);

        public OmtTestTile Polygon(int2[] ring, params (string key, object value)[] props) =>
            Add(MvtGeomType.Polygon, new[] { ring }, props);

        public static int2[] Square(int x0, int y0, int x1, int y1) =>
            new[] { new int2(x0, y0), new int2(x1, y0), new int2(x1, y1), new int2(x0, y1), new int2(x0, y0) };

        public NativeMvtTile Build()
        {
            FixedString64Bytes name = default;
            name.CopyFromTruncated(_layer);
            int n = _types.Count;
            var ids = new ulong[n];
            for (int i = 0; i < n; i++) ids[i] = (ulong)(i + 1);
            return new NativeMvtTile
            {
                LayerNames = new NativeArray<FixedString64Bytes>(new[] { name }, Allocator.Persistent),
                LayerVersions = new NativeArray<uint>(new uint[] { 2 }, Allocator.Persistent),
                LayerExtents = new NativeArray<uint>(new uint[] { 4096 }, Allocator.Persistent),
                LayerFeatureRange = new NativeArray<int2>(new[] { new int2(0, n) }, Allocator.Persistent),
                FeatureIds = new NativeArray<ulong>(ids, Allocator.Persistent),
                FeatureGeomTypes = new NativeArray<MvtGeomType>(_types.ToArray(), Allocator.Persistent),
                FeatureTagRange = new NativeArray<int2>(_tagRanges.ToArray(), Allocator.Persistent),
                FeatureRingRange = new NativeArray<int2>(_ringRanges.ToArray(), Allocator.Persistent),
                Tags = new NativeArray<int2>(_tags.ToArray(), Allocator.Persistent),
                Keys = new NativeArray<FixedString64Bytes>(_keys.ToArray(), Allocator.Persistent),
                Values = new NativeArray<MvtValue>(_values.ToArray(), Allocator.Persistent),
                Rings = new NativeArray<int2>(_rings.ToArray(), Allocator.Persistent),
                RingPoints = new NativeArray<int2>(_points.ToArray(), Allocator.Persistent),
            };
        }

        /// <summary>Runs an async test body with a timeout, as the other PlayMode tests do.</summary>
        public static IEnumerator Run(System.Func<CancellationToken, Awaitable> body, int timeoutSeconds = 30)
        {
            using var cts = new CancellationTokenSource(timeoutSeconds * 1000);
            var task = body(cts.Token);
            while (!task.GetAwaiter().IsCompleted)
            {
                yield return null;
                if (cts.Token.IsCancellationRequested) Assert.Fail($"Test timed out after {timeoutSeconds}s");
            }
            task.GetAwaiter().GetResult();
        }
    }
}
