using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using GeoFoundry.Geometry.Projection;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Builds the line ribbons of a draw list (<see cref="OmtLineDrawItem"/>) for the <c>GeoFoundry/OMT Line Lit</c>
    /// shader, which resizes them at the camera's pixel size. A copy of the draw-list mode of the package's
    /// <c>RoadBuildJob</c> that writes <see cref="OmtLineVertex"/> attributes; keep the ribbon construction in sync.
    ///
    /// The items are emitted in order (a feature may appear several times, e.g. casing then fill), optionally outlining
    /// Polygon features. Each LineString goes through <see cref="NativeMvtLineHelpers.ProjectLines"/> → a flat 2D
    /// ribbon with miter joins at bends (each join has two halves, one for the incoming and one for the outgoing
    /// segment; miter length is capped at <c>5 × width</c>) at its width at <see cref="BaseMetersPerPixel"/> → each
    /// ribbon triangle goes through <see cref="NativeGroundProjector.ProjectTriangle"/> (draped onto <see cref="Grid"/>
    /// and clipped to the tile boundary). Every vertex gets its extrusion from the line axis plus the item's width,
    /// dash and minimum zoom. Indices are global (one mesh per layer).
    ///
    /// End stitching: a road is often split in the data into several lines sharing an endpoint. If exactly two line
    /// ends of items with the same width meet at a point, each line is given the neighboring vertex of the other, and
    /// the join is built as at an interior vertex, without a wedge-shaped gap; a casing never stitches to a fill.
    ///
    /// Ribbon UVs: <c>x</c> ∈ [0,1] across the road, <c>y</c> is the distance traveled along the road in meters;
    /// values are interpolated barycentrically to the sub-vertices produced by grid clipping.
    /// </summary>
    [BurstCompile]
    internal struct OmtLineBuildJob : IJob
    {
        [ReadOnly] public NativeArray<MvtGeomType> FeatureGeomTypes;
        [ReadOnly] public NativeArray<int2> FeatureRingRange;
        [ReadOnly] public NativeArray<int2> Rings;
        [ReadOnly] public NativeArray<int2> RingPoints;

        /// <summary>Ordered draw list.</summary>
        [ReadOnly] public NativeArray<OmtLineDrawItem> DrawItems;

        /// <summary>Screen pixel size in meters at which the ribbons are built, normally that of the tile's zoom.</summary>
        public float BaseMetersPerPixel;

        public TileCoord Coord;
        public uint Extent;

        /// <summary>The tile's ground grid (read only).</summary>
        [ReadOnly] public GroundGrid Grid;

        public MercatorPivot LocalFrame;

        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        /// <summary>Scratch output of the ground projection (tile UVs, then ribbon UVs), parallel to <see cref="Positions"/>.</summary>
        public NativeList<float2> Uvs;
        public NativeList<Color32> Colors;
        public NativeList<OmtLineVertex> Vertices;
        public NativeList<int> Triangles;

        private struct ControlPoint
        {
            public float2 P0, P1, P2, P3, P4;
            public float2 Center;
            public bool HasJoin;
            public bool SharedLeft;
        }

        private struct LineEnd
        {
            public int Ring;
            public int2 Neighbor;
        }

        public void Execute()
        {
            var linePoints = new NativeList<double2>(64, Allocator.Temp);
            var lineRanges = new NativeList<int2>(4, Allocator.Temp);
            var lineRings = new NativeList<int>(4, Allocator.Temp);
            var points = new NativeList<float2>(64, Allocator.Temp);
            var controlPoints = new NativeList<ControlPoint>(64, Allocator.Temp);
            var lineEnds = new NativeParallelMultiHashMap<int3, LineEnd>(64, Allocator.Temp);

            double4 bb = Grid.BoundsMeters;
            float2 tileSize = new float2((float)(bb.z - bb.x), (float)(bb.w - bb.y));
            double4 tileBounds = Coord.BoundsMeters();

            try
            {
                CollectLineEnds(ref lineEnds);

                for (int item = 0; item < DrawItems.Length; item++)
                {
                    if (!TryGetItem(item, out int fi, out float width, out bool outline)) continue;
                    OmtLineDrawItem di = DrawItems[item];
                    int stitchKey = math.asint(width);
                    OmtLineWidth w = di.Width;
                    var template = new OmtLineVertex
                    {
                        Uv3 = new float4(0f, 0f, di.Dash),
                        Uv4 = new float4(w.Meters, w.Pixels, w.ExtraMeters, w.ExtraPixels),
                        Uv5 = new float2(0.5f * width, di.MinZoom),
                    };

                    NativeMvtLineHelpers.ProjectLines(
                        FeatureRingRange[fi], Rings, RingPoints, Coord, Extent,
                        ref linePoints, ref lineRanges, ref lineRings);

                    for (int li = 0; li < lineRanges.Length; li++)
                    {
                        // Tile-local meters (relative to the tile's min corner): float precision is sufficient here.
                        points.Clear();
                        int2 lineRange = lineRanges[li];
                        for (int i = 0; i < lineRange.y; i++)
                        {
                            double2 m = linePoints[lineRange.x + i];
                            float2 p = new float2((float)(m.x - bb.x), (float)(m.y - bb.y));
                            if (points.Length > 0 && math.distancesq(points[points.Length - 1], p) < 1e-8f) continue;
                            points.Add(p);
                        }
                        if (points.Length < 2) continue;

                        bool isClosed = points.Length > 2 && math.distancesq(points[0], points[points.Length - 1]) < 1e-8f;
                        if (isClosed) points.RemoveAt(points.Length - 1);
                        if (outline)
                        {
                            if (points.Length < 3) continue;
                            isClosed = true;
                        }

                        bool hasAdjStart = false, hasAdjEnd = false;
                        float2 adjStart = default, adjEnd = default;
                        if (!isClosed && TryGetLineEnds(lineRings[li], out int2 start, out int2 startNeighbor, out int2 end, out int2 endNeighbor))
                        {
                            if (TryGetAdjacent(lineEnds, new int3(start, stitchKey), lineRings[li], startNeighbor, out int2 adj))
                            {
                                hasAdjStart = true;
                                adjStart = ToTileLocal(adj, tileBounds, bb);
                            }
                            if (TryGetAdjacent(lineEnds, new int3(end, stitchKey), lineRings[li], endNeighbor, out adj))
                            {
                                hasAdjEnd = true;
                                adjEnd = ToTileLocal(adj, tileBounds, bb);
                            }
                        }

                        BuildControlPoints(points, isClosed, width, hasAdjStart, adjStart, hasAdjEnd, adjEnd, ref controlPoints);
                        BuildRibbon(controlPoints, isClosed, tileSize, di.Color, template);
                    }
                }
            }
            finally
            {
                linePoints.Dispose();
                lineRanges.Dispose();
                lineRings.Dispose();
                points.Dispose();
                controlPoints.Dispose();
                lineEnds.Dispose();
            }
        }

        /// <summary>Returns <c>false</c> if the item emits nothing: out-of-range index, unsupported geometry or width ≤ 0.</summary>
        private bool TryGetItem(int item, out int fi, out float width, out bool outline)
        {
            OmtLineDrawItem di = DrawItems[item];
            fi = di.FeatureIndex;
            width = di.Width.Evaluate(BaseMetersPerPixel);
            outline = (di.Flags & LineDrawFlags.PolygonOutline) != 0;

            if ((uint)fi >= (uint)FeatureGeomTypes.Length || width <= 0f) return false;
            MvtGeomType geom = FeatureGeomTypes[fi];
            if (outline) return geom == MvtGeomType.Polygon;
            return geom == MvtGeomType.LineString;
        }

        /// <summary>Collects the ends of all open lines, keyed by their MVT coordinates (exact match) and width.</summary>
        private void CollectLineEnds(ref NativeParallelMultiHashMap<int3, LineEnd> lineEnds)
        {
            for (int item = 0; item < DrawItems.Length; item++)
            {
                if (!TryGetItem(item, out int fi, out float width, out bool outline) || outline) continue;
                int key = math.asint(width);

                int2 ringRange = FeatureRingRange[fi];
                for (int r = 0; r < ringRange.y; r++)
                {
                    int ring = ringRange.x + r;
                    if (!TryGetLineEnds(ring, out int2 start, out int2 startNeighbor, out int2 end, out int2 endNeighbor)) continue;
                    lineEnds.Add(new int3(start, key), new LineEnd { Ring = ring, Neighbor = startNeighbor });
                    lineEnds.Add(new int3(end, key), new LineEnd { Ring = ring, Neighbor = endNeighbor });
                }
            }
        }

        /// <summary>
        /// Gets the ring's ends and the nearest vertices distinct from them. Returns <c>false</c> if the ring is
        /// degenerate (all points coincide) or closed (the ends coincide, nothing to stitch).
        /// </summary>
        private bool TryGetLineEnds(int ring, out int2 start, out int2 startNeighbor, out int2 end, out int2 endNeighbor)
        {
            int2 range = Rings[ring];
            start = startNeighbor = end = endNeighbor = default;
            if (range.y < 2) return false;

            int first = range.x, last = range.x + range.y - 1;
            start = RingPoints[first];
            end = RingPoints[last];
            if (start.Equals(end)) return false;

            int s = first + 1;
            while (RingPoints[s].Equals(start)) s++;
            int e = last - 1;
            while (RingPoints[e].Equals(end)) e--;

            startNeighbor = RingPoints[s];
            endNeighbor = RingPoints[e];
            return true;
        }

        /// <summary>
        /// Gets the neighboring vertex of the other line meeting at <paramref name="end"/>, only if exactly two line
        /// ends meet there and the other line does not double back along our own segment.
        /// </summary>
        private static bool TryGetAdjacent(
            NativeParallelMultiHashMap<int3, LineEnd> lineEnds, int3 end, int ring, int2 ownNeighbor, out int2 adjacent)
        {
            adjacent = default;
            int count = 0;
            bool found = false;

            if (lineEnds.TryGetFirstValue(end, out LineEnd item, out var it))
            {
                do
                {
                    count++;
                    if (item.Ring != ring)
                    {
                        adjacent = item.Neighbor;
                        found = true;
                    }
                } while (lineEnds.TryGetNextValue(out item, ref it));
            }

            return count == 2 && found && !adjacent.Equals(ownNeighbor);
        }

        private float2 ToTileLocal(int2 local, double4 tileBounds, double4 bb)
        {
            double2 m = MvtTileCoords.ToMercator(local, tileBounds, Extent);
            return new float2((float)(m.x - bb.x), (float)(m.y - bb.y));
        }

        /// <summary>
        /// Computes the control points of every line vertex: the inner miter point on the bisector, its reflections
        /// across the incoming and outgoing segments and the outer miter point.
        /// </summary>
        private static void BuildControlPoints(
            NativeList<float2> vertices, bool isClosed, float width,
            bool hasAdjStart, float2 adjStart, bool hasAdjEnd, float2 adjEnd,
            ref NativeList<ControlPoint> output)
        {
            output.Clear();
            int n = vertices.Length;

            for (int i = 0; i < n; i++)
            {
                float2 current = vertices[i];
                bool hasPrev = isClosed || i > 0 || hasAdjStart;
                bool hasNext = isClosed || i < n - 1 || hasAdjEnd;
                float2 prev = !hasPrev ? default : (isClosed || i > 0) ? vertices[(i - 1 + n) % n] : adjStart;
                float2 next = !hasNext ? default : (isClosed || i < n - 1) ? vertices[(i + 1) % n] : adjEnd;

                float2 vA = hasPrev ? current - prev : next - current;
                float2 vB = hasNext ? next - current : vA;

                float2 aNorm = math.normalize(vA);
                float2 bNorm = math.normalize(vB);
                float2 leftA = RotateLeft(aNorm);
                float2 leftB = RotateLeft(bNorm);

                float alpha = math.atan2(bNorm.y, bNorm.x) - math.atan2(aNorm.y, aNorm.x);
                float alphaFixed = alpha < 0f ? alpha + math.PI * 2f : alpha;

                // A ~180° turn degenerates the bisector, so the incoming segment's normal is used instead.
                float2 leftSum = leftA + leftB;
                float2 offsetDir = math.lengthsq(leftSum) > 1e-12f ? math.normalize(leftSum) : leftA;
                float offsetLength = width / (2f * math.cos(alpha * 0.5f));
                float offsetLengthAbs = math.min(math.abs(offsetLength), width * 5f);

                bool inverse = alphaFixed >= math.PI;

                float2 pointLeft = current + offsetDir * offsetLengthAbs;
                float2 pointRight = current - offsetDir * offsetLengthAbs;
                float2 inner = inverse ? pointRight : pointLeft;

                float2 mirroredA = Reflect(inner, current, aNorm);
                float2 mirroredB = Reflect(inner, current, bNorm);

                output.Add(new ControlPoint
                {
                    P0 = inverse ? mirroredB : pointLeft,
                    P1 = inverse ? pointRight : mirroredB,
                    P2 = inverse ? mirroredA : pointLeft,
                    P3 = inverse ? pointRight : mirroredA,
                    P4 = inverse ? pointLeft : pointRight,
                    Center = current,
                    HasJoin = hasPrev && hasNext,
                    SharedLeft = !inverse,
                });
            }
        }

        /// <summary>
        /// Emits ribbon triangles from the control points: for each segment, the join half at its start, the segment
        /// quad and the join half at its end.
        /// </summary>
        private void BuildRibbon(NativeList<ControlPoint> cps, bool isClosed, float2 tileSize, Color32 color,
            OmtLineVertex template)
        {
            int segmentCount = cps.Length - (isClosed ? 0 : 1);
            float uvProgress = 0f;

            for (int i = 0; i < segmentCount; i++)
            {
                ControlPoint current = cps[i];
                ControlPoint next = cps[(i + 1) % cps.Length];

                if (current.HasJoin)
                {
                    float start = uvProgress;
                    float end = start + math.distance(current.P4, current.SharedLeft ? current.P1 : current.P0);
                    float c4x = current.SharedLeft ? 1f : 0f;
                    EmitTriangle(current.P0, current.P1, current.P4, current.Center, current.Center, current.Center,
                        new float2(0f, end), new float2(1f, end), new float2(c4x, start), tileSize, color, template);
                    uvProgress = end;
                }

                {
                    float2 a = current.P0, b = current.P1, c = next.P2, d = next.P3;
                    float start = uvProgress;
                    float end = start + math.distance(a, c);
                    float2 ca = current.Center, cc = next.Center;
                    EmitTriangle(a, b, c, ca, ca, cc, new float2(0f, start), new float2(1f, start), new float2(0f, end),
                        tileSize, color, template);
                    EmitTriangle(b, d, c, ca, cc, cc, new float2(1f, start), new float2(1f, end), new float2(0f, end),
                        tileSize, color, template);
                    uvProgress = end;
                }

                if (next.HasJoin)
                {
                    float start = uvProgress;
                    float end = start + math.distance(next.P4, next.SharedLeft ? next.P1 : next.P0);
                    float c4x = next.SharedLeft ? 1f : 0f;
                    EmitTriangle(next.P2, next.P3, next.P4, next.Center, next.Center, next.Center,
                        new float2(0f, start), new float2(1f, start), new float2(c4x, end), tileSize, color, template);
                    uvProgress = end;
                }
            }
        }

        /// <summary>
        /// Projects a ribbon triangle (tile-local meters) onto the ground and writes its sub-triangles, interpolating the
        /// ribbon UV and the extrusions of the corners (offsets from their line vertices <paramref name="ca"/>..
        /// <paramref name="cc"/>) to each sub-vertex: the interpolated center stays on the line axis, so the shader can
        /// resize the ribbon around it.
        /// </summary>
        private void EmitTriangle(
            float2 a, float2 b, float2 c, float2 ca, float2 cb, float2 cc, float2 uvA, float2 uvB, float2 uvC,
            float2 tileSize, Color32 color, OmtLineVertex template)
        {
            float invHalf = template.Uv5.x > 0f ? 1f / template.Uv5.x : 0f;
            float2 eA = (a - ca) * invHalf, eB = (b - cb) * invHalf, eC = (c - cc) * invHalf;

            int before = Positions.Length;
            NativeGroundProjector.ProjectTriangle(a / tileSize, b / tileSize, c / tileSize, Grid, LocalFrame, ref Positions, ref Normals, ref Uvs);
            int after = Positions.Length;

            for (int vi = before; vi < after; vi++)
            {
                // Barycentrics are computed in meters, not tile UV: in [0,1] space the denominator for meter-sized
                // triangles falls below the float precision threshold.
                float3 w = NativeTriangleMath.Barycentric(Uvs[vi] * tileSize, a, b, c);
                float2 uv = uvA * w.x + uvB * w.y + uvC * w.z;
                Uvs[vi] = uv;
                Colors.Add(color);
                OmtLineVertex v = template;
                v.Uv0 = uv;
                v.Uv3.xy = eA * w.x + eB * w.y + eC * w.z;
                Vertices.Add(v);
                Triangles.Add(vi);
            }
        }

        private static float2 RotateLeft(float2 v) => new float2(-v.y, v.x);

        private static float2 Reflect(float2 point, float2 origin, float2 dir)
        {
            float2 rel = point - origin;
            return origin + 2f * math.dot(rel, dir) * dir - rel;
        }
    }
}
