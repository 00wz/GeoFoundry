using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry.Projection;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Builds road layer geometry (<c>transportation</c>): one <see cref="IJob"/> per tile layer, the same pattern
    /// as <see cref="SurfaceBuildJob"/>. For each LineString feature: <see cref="NativeMvtLineHelpers.ProjectLines"/>
    /// → a flat 2D ribbon with miter joins at bends (each join has two halves, one for the incoming and one for the
    /// outgoing segment; miter length is capped at <c>5 × width</c>) → each ribbon triangle goes through
    /// <see cref="NativeGroundProjector.ProjectTriangle"/> (draped onto <see cref="Grid"/> and clipped to the tile
    /// boundary). Indices in <see cref="Triangles"/> are feature-local unless <see cref="EmitGlobalIndices"/> is set,
    /// as in <see cref="SurfaceBuildJob"/>.
    ///
    /// End stitching: a road is often split in the data into several lines (different features or parts of a
    /// MultiLineString) sharing an endpoint. If exactly two line ends of the layer meet at a point, each line is
    /// given the neighboring vertex of the other, and the join is built as at an interior vertex (each line builds
    /// its own half), without a wedge-shaped gap.
    ///
    /// Ribbon UVs: <c>x</c> ∈ [0,1] across the road, <c>y</c> is the distance traveled along the road in meters;
    /// values are interpolated barycentrically to the sub-vertices produced by grid clipping.
    ///
    /// Draw-list mode (<see cref="UseDrawItems"/>): instead of all LineString features with <see cref="Widths"/>, the
    /// job emits the items of <see cref="DrawItems"/> in order (a feature may appear several times, e.g. casing then
    /// fill), optionally outlining Polygon features, and writes each item's dash pattern to <see cref="Uv1"/>. End
    /// stitching then only joins line ends of items with the same width, so a casing never stitches to a fill.
    /// </summary>
    [BurstCompile]
    internal struct RoadBuildJob : IJob
    {
        [ReadOnly] public NativeArray<MvtGeomType> FeatureGeomTypes;
        [ReadOnly] public NativeArray<int2> FeatureRingRange;

        /// <summary>Ribbon width in meters per feature (indexed like the other Feature* arrays); ≤ 0 skips the
        /// feature.</summary>
        [ReadOnly] public NativeArray<float> Widths;

        [ReadOnly] public NativeArray<int2> Rings;
        [ReadOnly] public NativeArray<int2> RingPoints;

        /// <summary>Same pattern as <see cref="SurfaceBuildJob.FeatureColors"/>.</summary>
        [ReadOnly] public NativeArray<Color32> FeatureColors;

        /// <summary>
        /// Ordered draw list used when <see cref="UseDrawItems"/> is set (<see cref="Widths"/> and
        /// <see cref="FeatureColors"/> are then ignored). Must always be a valid (possibly empty) array.
        /// </summary>
        [ReadOnly] public NativeArray<LineDrawItem> DrawItems;

        /// <summary>Whether <see cref="DrawItems"/> drives the output instead of the feature arrays.</summary>
        public bool UseDrawItems;

        public TileCoord Coord;
        public uint Extent;

        /// <summary>Same rules as <see cref="SurfaceBuildJob.Grid"/>.</summary>
        [ReadOnly] public GroundGrid Grid;

        /// <summary>See <see cref="SurfaceBuildJob.EmitGlobalIndices"/>.</summary>
        public bool EmitGlobalIndices;

        public MercatorPivot LocalFrame;

        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<float2> Uvs;
        /// <summary>Per-vertex output parallel to <see cref="Positions"/>; see <see cref="FeatureColors"/>.</summary>
        public NativeList<Color32> Colors;
        /// <summary>Per-vertex dash pattern (<see cref="LineDrawItem.Dash"/>), written only in draw-list mode.</summary>
        public NativeList<float2> Uv1;
        public NativeList<int> Triangles;
        public NativeList<MeshRange> Ranges;
        /// <summary>Source feature index (into the Feature* arrays) of each output MeshRange. The handler uses it
        /// for the feature id (GameObject name) and any rendering properties.</summary>
        public NativeList<int> RangeFeatureIndices;

        /// <summary>
        /// Control points of a ribbon vertex (see <see cref="BuildControlPoints"/>):
        /// <see cref="P0"/>/<see cref="P1"/> are the left/right edges at the start of the outgoing segment,
        /// <see cref="P2"/>/<see cref="P3"/> the left/right edges at the end of the incoming segment, and
        /// <see cref="P4"/> the outer (miter) point of the join (only when <see cref="HasJoin"/>).
        /// </summary>
        private struct ControlPoint
        {
            public float2 P0, P1, P2, P3, P4;
            public bool HasJoin;
            /// <summary><c>P0 == P2</c>: the shared (inner) side of the bend is the left one, the join is on the
            /// right.</summary>
            public bool SharedLeft;
        }

        /// <summary>A line end of the layer: the ring it belongs to and the nearest vertex distinct from it.</summary>
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

            bool hasColors = FeatureColors.Length == FeatureGeomTypes.Length;

            double4 bb = Grid.BoundsMeters;
            float2 tileSize = new float2((float)(bb.z - bb.x), (float)(bb.w - bb.y));
            double4 tileBounds = Coord.BoundsMeters();

            try
            {
                CollectLineEnds(ref lineEnds);

                int itemCount = UseDrawItems ? DrawItems.Length : FeatureGeomTypes.Length;
                for (int item = 0; item < itemCount; item++)
                {
                    if (!TryGetItem(item, hasColors, out int fi, out float width, out Color32 color, out float2 dash, out bool outline))
                        continue;
                    int stitchKey = StitchKey(width);

                    NativeMvtLineHelpers.ProjectLines(
                        FeatureRingRange[fi], Rings, RingPoints, Coord, Extent,
                        ref linePoints, ref lineRanges, ref lineRings);

                    if (lineRanges.Length == 0) continue;

                    int featureVertStart = Positions.Length;
                    int featureTriStart = Triangles.Length;
                    int idxBase = EmitGlobalIndices ? 0 : featureVertStart;

                    for (int li = 0; li < lineRanges.Length; li++)
                    {
                        // Tile-local meters (relative to the tile's min corner): float precision is sufficient here,
                        // unlike for absolute Mercator coordinates.
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
                        BuildRibbon(controlPoints, isClosed, tileSize, idxBase, color, dash);
                    }

                    int featureVertCount = Positions.Length - featureVertStart;
                    int featureTriCount = Triangles.Length - featureTriStart;
                    if (featureVertCount > 0 && featureTriCount > 0)
                    {
                        Ranges.Add(new MeshRange(featureVertStart, featureVertCount, featureTriStart, featureTriCount));
                        RangeFeatureIndices.Add(fi);
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

        /// <summary>
        /// Resolves one entry of the iteration (a feature in feature mode, a <see cref="LineDrawItem"/> in draw-list
        /// mode). Returns <c>false</c> if it emits nothing: out-of-range index, unsupported geometry or width ≤ 0.
        /// </summary>
        private bool TryGetItem(int item, bool hasColors, out int fi, out float width, out Color32 color, out float2 dash, out bool outline)
        {
            if (UseDrawItems)
            {
                LineDrawItem di = DrawItems[item];
                fi = di.FeatureIndex;
                width = di.WidthMeters;
                color = di.Color;
                dash = di.Dash;
                outline = (di.Flags & LineDrawFlags.PolygonOutline) != 0;
            }
            else
            {
                fi = item;
                width = Widths[fi];
                color = hasColors ? FeatureColors[fi] : new Color32(255, 255, 255, 255);
                dash = default;
                outline = false;
            }

            if ((uint)fi >= (uint)FeatureGeomTypes.Length || width <= 0f) return false;
            MvtGeomType geom = FeatureGeomTypes[fi];
            if (outline) return geom == MvtGeomType.Polygon;
            return geom == MvtGeomType.LineString;
        }

        /// <summary>
        /// Third component of the line-end key: in draw-list mode lines are stitched only to lines of the same width
        /// (the same pass, e.g. fills to fills), in feature mode to any rendered line.
        /// </summary>
        private int StitchKey(float width) => UseDrawItems ? math.asint(width) : 0;

        /// <summary>Collects the ends of all open, rendered lines, keyed by their MVT coordinates (exact match) and
        /// <see cref="StitchKey"/>.</summary>
        private void CollectLineEnds(ref NativeParallelMultiHashMap<int3, LineEnd> lineEnds)
        {
            bool hasColors = FeatureColors.Length == FeatureGeomTypes.Length;
            int itemCount = UseDrawItems ? DrawItems.Length : FeatureGeomTypes.Length;
            for (int item = 0; item < itemCount; item++)
            {
                if (!TryGetItem(item, hasColors, out int fi, out float width, out _, out _, out bool outline) || outline) continue;
                int key = StitchKey(width);

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
        /// ends meet there (three or more ends form an intersection, not a road continuation) and the other line does
        /// not double back along our own segment.
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
        /// across the incoming and outgoing segments (the ribbon edges at segment ends) and the outer miter point.
        /// <paramref name="adjStart"/>/<paramref name="adjEnd"/> are the vertices of the adjacent line beyond each end
        /// (see end stitching), so a join is built there as well.
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
                    HasJoin = hasPrev && hasNext,
                    SharedLeft = !inverse,
                });
            }
        }

        /// <summary>
        /// Emits ribbon triangles from the control points: for each segment, the join half at its start, the segment
        /// quad and the join half at its end.
        /// </summary>
        private void BuildRibbon(NativeList<ControlPoint> cps, bool isClosed, float2 tileSize, int idxBase, Color32 color, float2 dash)
        {
            int segmentCount = cps.Length - (isClosed ? 0 : 1);
            float uvProgress = 0f;

            for (int i = 0; i < segmentCount; i++)
            {
                ControlPoint current = cps[i];
                ControlPoint next = cps[(i + 1) % cps.Length];

                // Join at the segment start: (P0, P1, P4) of the current vertex.
                if (current.HasJoin)
                {
                    float start = uvProgress;
                    float end = start + math.distance(current.P4, current.SharedLeft ? current.P1 : current.P0);
                    float c4x = current.SharedLeft ? 1f : 0f;
                    EmitTriangle(current.P0, current.P1, current.P4,
                        new float2(0f, end), new float2(1f, end), new float2(c4x, start),
                        tileSize, idxBase, color, dash);
                    uvProgress = end;
                }

                // The segment itself: quad (current.P0, current.P1) → (next.P2, next.P3).
                {
                    float2 a = current.P0, b = current.P1, c = next.P2, d = next.P3;
                    float start = uvProgress;
                    float end = start + math.distance(a, c);
                    EmitTriangle(a, b, c, new float2(0f, start), new float2(1f, start), new float2(0f, end), tileSize, idxBase, color, dash);
                    EmitTriangle(b, d, c, new float2(1f, start), new float2(1f, end), new float2(0f, end), tileSize, idxBase, color, dash);
                    uvProgress = end;
                }

                // Join at the segment end: (P2, P3, P4) of the next vertex.
                if (next.HasJoin)
                {
                    float start = uvProgress;
                    float end = start + math.distance(next.P4, next.SharedLeft ? next.P1 : next.P0);
                    float c4x = next.SharedLeft ? 1f : 0f;
                    EmitTriangle(next.P2, next.P3, next.P4,
                        new float2(0f, start), new float2(1f, start), new float2(c4x, end),
                        tileSize, idxBase, color, dash);
                    uvProgress = end;
                }
            }
        }

        /// <summary>
        /// Projects a ribbon triangle (tile-local meters) onto the ground and writes its sub-triangles to the output
        /// buffers, interpolating the ribbon UV to each sub-vertex.
        /// </summary>
        private void EmitTriangle(
            float2 a, float2 b, float2 c, float2 uvA, float2 uvB, float2 uvC,
            float2 tileSize, int idxBase, Color32 color, float2 dash)
        {
            int before = Positions.Length;
            NativeGroundProjector.ProjectTriangle(a / tileSize, b / tileSize, c / tileSize, Grid, LocalFrame, ref Positions, ref Normals, ref Uvs);
            int after = Positions.Length;

            for (int vi = before; vi < after; vi++)
            {
                // Barycentrics are computed in meters, not tile UV: in [0,1] space the denominator for meter-sized
                // triangles falls below the float precision threshold.
                float3 w = NativeTriangleMath.Barycentric(Uvs[vi] * tileSize, a, b, c);
                Uvs[vi] = uvA * w.x + uvB * w.y + uvC * w.z;
                Colors.Add(color);
                if (UseDrawItems) Uv1.Add(dash);
                Triangles.Add(vi - idxBase);
            }
        }

        private static float2 RotateLeft(float2 v) => new float2(-v.y, v.x);

        /// <summary>Reflects a point across the line through <paramref name="origin"/> with unit direction
        /// <paramref name="dir"/>.</summary>
        private static float2 Reflect(float2 point, float2 origin, float2 dir)
        {
            float2 rel = point - origin;
            return origin + 2f * math.dot(rel, dir) * dir - rel;
        }
    }
}
