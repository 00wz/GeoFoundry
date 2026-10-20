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
    /// Builds building geometry that follows the terrain (terrain-conforming extrusion). The building stays a full
    /// 3D volume (roof, walls, optional floor), but each point's height is measured not from a single per-feature
    /// constant (as in <see cref="BuildingBuildJob.SampleTerrainBase"/>) but from the terrain directly beneath that
    /// point: <c>roof = terrain(x,z) + top</c>, <c>floor = terrain(x,z) + bottom</c>. Roof and floor are therefore
    /// not perfectly flat but follow the terrain shape with an offset. This deliberate trade-off keeps
    /// multi-feature buildings from falling apart on slopes: adjacent features sharing a boundary sample the same
    /// deterministic <see cref="GroundGrid"/> and get identical heights at the seam.
    ///
    /// Uses the same techniques as <see cref="SurfaceBuildJob"/> (single-tile <see cref="Grid"/>,
    /// <see cref="NativeGroundProjector.ProjectTriangle"/> to split roof/floor triangles by grid cells) plus
    /// <see cref="NativeGroundProjector.ProjectLineSegment"/> to upsample wall edges along cell boundaries.
    ///
    /// Not to be confused with <see cref="BuildingBuildJob"/> (flat-base extrusion, kept as a starting point for
    /// schemas that group features and not used by the default <c>BuildingsLayerHandler</c>; see its doc).
    /// </summary>
    [BurstCompile]
    internal struct BuildingTerrainBuildJob : IJob
    {
        [ReadOnly] public NativeArray<MvtGeomType> FeatureGeomTypes;
        [ReadOnly] public NativeArray<int2> FeatureRingRange;

        /// <summary>Per-feature "do not build" flag; a hidden feature is skipped entirely.</summary>
        [ReadOnly] public NativeArray<bool> Hidden;
        /// <summary>Per-feature roof height above the terrain, in meters.</summary>
        [ReadOnly] public NativeArray<float> TopMeters;
        /// <summary>Per-feature height of the building bottom above the terrain, in meters (0 means no floor).</summary>
        [ReadOnly] public NativeArray<float> BottomMeters;
        /// <summary>
        /// Per-feature vertex color. A length that
        /// differs from <see cref="FeatureGeomTypes"/> (including an empty array) is the "no per-feature color"
        /// sentinel: opaque white is used.
        /// </summary>
        [ReadOnly] public NativeArray<Color32> FeatureColors;

        [ReadOnly] public NativeArray<int2> Rings;
        [ReadOnly] public NativeArray<int2> RingPoints;

        public TileCoord Coord;
        public uint Extent;
        public float UvScalePerMeter;

        /// <summary>
        /// The tile's ground grid, with the same meaning and the same [ReadOnly] discipline as
        /// <see cref="SurfaceBuildJob.Grid"/> (see its doc). A single grid (not the 9-grid multi-tile input of
        /// <see cref="BuildingBuildJob"/>): buildings at the tile edge behave like the other terrain-conforming layers
        /// (clamped at the boundary) and do not read neighboring tiles' DEM directly.
        /// </summary>
        [ReadOnly] public GroundGrid Grid;

        /// <summary>See <see cref="SurfaceBuildJob.EmitGlobalIndices"/>.</summary>
        public bool EmitGlobalIndices;

        public MercatorPivot LocalFrame;

        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<float2> Uvs;
        /// <summary>Per-vertex output parallel to <see cref="Positions"/>; see <see cref="FeatureColors"/>.</summary>
        public NativeList<Color32> Colors;
        public NativeList<int> Triangles;
        public NativeList<MeshRange> Ranges;
        /// <summary>Source feature index (into the Feature* arrays) of each output MeshRange.</summary>
        public NativeList<int> RangeFeatureIndices;

        public void Execute()
        {
            double4 bounds = Coord.BoundsMeters();
            double tileW = bounds.z - bounds.x;
            double tileH = bounds.w - bounds.y;
            if (tileW <= 0 || tileH <= 0) return;

            float uvFactor = (float)(tileW * UvScalePerMeter);
            bool hasColors = FeatureColors.Length == FeatureGeomTypes.Length;

            var polyPoints = new NativeList<double2>(64, Allocator.Temp);
            var polyRingRanges = new NativeList<int2>(4, Allocator.Temp);
            var polyPolygonRanges = new NativeList<int2>(2, Allocator.Temp);
            var triVerts = new NativeList<double2>(64, Allocator.Temp);
            var triIndices = new NativeList<int>(128, Allocator.Temp);
            var projPos = new NativeList<float3>(16, Allocator.Temp);
            var projNorm = new NativeList<float3>(16, Allocator.Temp);
            var projUv = new NativeList<float2>(16, Allocator.Temp);
            var edgePos = new NativeList<float3>(8, Allocator.Temp);
            var edgeNorm = new NativeList<float3>(8, Allocator.Temp);

            try
            {
                for (int fi = 0; fi < FeatureGeomTypes.Length; fi++)
                {
                    if (FeatureGeomTypes[fi] != MvtGeomType.Polygon) continue;
                    if (Hidden[fi]) continue;

                    NativeMvtPolygonHelpers.SplitMultiPolygon(
                        FeatureRingRange[fi], Rings, RingPoints, Coord, Extent,
                        ref polyPoints, ref polyRingRanges, ref polyPolygonRanges);

                    if (polyPolygonRanges.Length == 0) continue;

                    float top = TopMeters[fi];
                    float bottom = BottomMeters[fi];
                    bool hasFloor = bottom > 0.0001f;
                    float wallH = top - bottom;
                    Color32 color = hasColors ? FeatureColors[fi] : new Color32(255, 255, 255, 255);

                    int featureVertStart = Positions.Length;
                    int featureTriStart = Triangles.Length;
                    int idxBase = EmitGlobalIndices ? 0 : featureVertStart;

                    for (int p = 0; p < polyPolygonRanges.Length; p++)
                    {
                        int2 polyRange = polyPolygonRanges[p];
                        var ringRangesSlice = polyRingRanges.AsArray().GetSubArray(polyRange.x, polyRange.y);

                        bool ok = NativePolygonTriangulator.Triangulate(
                            polyPoints.AsArray(), ringRangesSlice, triVerts, triIndices);
                        if (!ok) continue;

                        for (int t = 0; t < triIndices.Length; t += 3)
                        {
                            double2 a = triVerts[triIndices[t]];
                            double2 b = triVerts[triIndices[t + 1]];
                            double2 c = triVerts[triIndices[t + 2]];
                            AppendRoofAndFloor(
                                a, b, c, bounds, tileW, tileH, uvFactor, top, bottom, hasFloor,
                                idxBase, color, ref projPos, ref projNorm, ref projUv);
                        }

                        for (int r = 0; r < ringRangesSlice.Length; r++)
                        {
                            AppendWalls(
                                polyPoints, ringRangesSlice[r], bounds, tileW, tileH, top, bottom, wallH,
                                idxBase, color, ref edgePos, ref edgeNorm);
                        }
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
                polyPoints.Dispose();
                polyRingRanges.Dispose();
                polyPolygonRanges.Dispose();
                triVerts.Dispose();
                triIndices.Dispose();
                projPos.Dispose();
                projNorm.Dispose();
                projUv.Dispose();
                edgePos.Dispose();
                edgeNorm.Dispose();
            }
        }

        private void AppendRoofAndFloor(
            double2 a, double2 b, double2 c, double4 bounds, double tileW, double tileH, float uvFactor,
            float top, float bottom, bool hasFloor, int idxBase, Color32 color,
            ref NativeList<float3> projPos, ref NativeList<float3> projNorm, ref NativeList<float2> projUv)
        {
            projPos.Clear();
            projNorm.Clear();
            projUv.Clear();

            float2 ua = ToUv(a, bounds, tileW, tileH);
            float2 ub = ToUv(b, bounds, tileW, tileH);
            float2 uc = ToUv(c, bounds, tileW, tileH);
            NativeGroundProjector.ProjectTriangle(ua, ub, uc, Grid, LocalFrame, ref projPos, ref projNorm, ref projUv);

            if (projPos.Length == 0) return;

            // ---- Roof: offset by +top, normal unchanged (the roof slope follows the terrain slope).
            int roofLocalBase = Positions.Length - idxBase;
            for (int k = 0; k < projPos.Length; k++)
            {
                Positions.Add(projPos[k] + new float3(0f, top, 0f));
                Normals.Add(projNorm[k]);
                Uvs.Add(projUv[k] * uvFactor);
                Colors.Add(color);
            }
            for (int k = 0; k < projPos.Length; k++) Triangles.Add(roofLocalBase + k);

            // ---- Floor (if bottom > 0): offset by +bottom, normal inverted, winding reversed.
            if (hasFloor)
            {
                int floorLocalBase = Positions.Length - idxBase;
                for (int k = 0; k < projPos.Length; k++)
                {
                    Positions.Add(projPos[k] + new float3(0f, bottom, 0f));
                    Normals.Add(-projNorm[k]);
                    Uvs.Add(projUv[k] * uvFactor);
                    Colors.Add(color);
                }
                for (int k = 0; k < projPos.Length; k += 3)
                {
                    Triangles.Add(floorLocalBase + k + 0);
                    Triangles.Add(floorLocalBase + k + 2);
                    Triangles.Add(floorLocalBase + k + 1);
                }
            }
        }

        private void AppendWalls(
            NativeList<double2> polyPoints, int2 ringRange, double4 bounds, double tileW, double tileH,
            float top, float bottom, float wallH, int idxBase, Color32 color,
            ref NativeList<float3> edgePos, ref NativeList<float3> edgeNorm)
        {
            int rlen = ringRange.y;

            for (int i = 0; i < rlen; i++)
            {
                double2 a = polyPoints[ringRange.x + i];
                double2 b = polyPoints[ringRange.x + (i + 1) % rlen];
                if (a.Equals(b)) continue; // skip degenerate

                edgePos.Clear();
                edgeNorm.Clear();

                float2 ua = ToUv(a, bounds, tileW, tileH);
                float2 ub = ToUv(b, bounds, tileW, tileH);
                NativeGroundProjector.ProjectLineSegment(ua, ub, Grid, LocalFrame, ref edgePos, ref edgeNorm);

                if (edgePos.Length < 2) continue;

                float uCursor = 0f;
                for (int s = 0; s + 1 < edgePos.Length; s++)
                {
                    float3 p0 = edgePos[s];
                    float3 p1 = edgePos[s + 1];

                    float3 pA_bot = new float3(p0.x, p0.y + bottom, p0.z);
                    float3 pA_top = new float3(p0.x, p0.y + top, p0.z);
                    float3 pB_bot = new float3(p1.x, p1.y + bottom, p1.z);
                    float3 pB_top = new float3(p1.x, p1.y + top, p1.z);

                    int baseV = Positions.Length - idxBase;
                    Positions.Add(pA_bot); Positions.Add(pB_bot); Positions.Add(pB_top); Positions.Add(pA_top);

                    float3 edge = new float3(pB_bot.x - pA_bot.x, 0f, pB_bot.z - pA_bot.z);
                    float3 n = math.normalize(new float3(-edge.z, 0f, edge.x));
                    Normals.Add(n); Normals.Add(n); Normals.Add(n); Normals.Add(n);

                    float segLen = math.length(edge);
                    Uvs.Add(new float2(uCursor, 0f));
                    Uvs.Add(new float2(uCursor + segLen, 0f));
                    Uvs.Add(new float2(uCursor + segLen, wallH));
                    Uvs.Add(new float2(uCursor, wallH));
                    uCursor += segLen;

                    Colors.Add(color); Colors.Add(color); Colors.Add(color); Colors.Add(color);

                    // CW viewed from outside.
                    Triangles.Add(baseV + 0); Triangles.Add(baseV + 1); Triangles.Add(baseV + 2);
                    Triangles.Add(baseV + 0); Triangles.Add(baseV + 2); Triangles.Add(baseV + 3);
                }
            }
        }

        private static float2 ToUv(double2 m, double4 bounds, double tileW, double tileH) =>
            new float2((float)((m.x - bounds.x) / tileW), (float)((m.y - bounds.y) / tileH));
    }
}
