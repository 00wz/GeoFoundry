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
    /// Builds geometry for a flat-surface layer (park, water, landuse, landcover): one <see cref="IJob"/> per tile
    /// layer (not per feature, to avoid the overhead of many <c>Schedule()</c> calls). For each Polygon feature:
    /// <see cref="NativeMvtPolygonHelpers.SplitMultiPolygon"/> → <see cref="NativePolygonTriangulator.Triangulate"/>
    /// → <see cref="NativeGroundProjector.ProjectTriangle"/> for every triangle (draped onto <see cref="Grid"/> and
    /// clipped to the tile boundary). Output: shared layer buffers plus one <see cref="MeshRange"/> per feature
    /// (see <see cref="AsyncGeometryWorker"/>).
    ///
    /// Indices in <see cref="Triangles"/> are feature-local (0-based relative to the range's
    /// <see cref="MeshRange.VertexStart"/>), because each feature becomes a separate <see cref="UnityEngine.Mesh"/>;
    /// see <see cref="EmitGlobalIndices"/> for the single-mesh mode.
    /// </summary>
    [BurstCompile]
    internal struct SurfaceBuildJob : IJob
    {
        // Slices of this layer (see NativeMvtLayer.FeatureGeomTypesSlice/FeatureRingRangeSlice) plus the tile-wide
        // Rings/RingPoints (absolute indices, so no per-layer slice is needed).
        [ReadOnly] public NativeArray<MvtGeomType> FeatureGeomTypes;
        [ReadOnly] public NativeArray<int2> FeatureRingRange;
        [ReadOnly] public NativeArray<int2> Rings;
        [ReadOnly] public NativeArray<int2> RingPoints;

        /// <summary>
        /// Per-feature vertex color (index = <c>fi</c>). A length that differs from <see cref="FeatureGeomTypes"/>
        /// (including an empty array) is the "no per-feature color" sentinel: every feature gets opaque white (the
        /// identity for <c>albedo *= vertexColor</c> in the shader). Same pattern as
        /// <see cref="BuildingBuildJob.FeatureColors"/>.
        /// </summary>
        [ReadOnly] public NativeArray<Color32> FeatureColors;

        /// <summary>
        /// Ordered draw list, used instead of iterating all features when <see cref="UseDrawItems"/> is set: only the
        /// listed features are emitted, in list order, with the item's color (<see cref="FeatureColors"/> is then
        /// ignored). Must always be a valid (possibly empty) array, as required by the Job System.
        /// </summary>
        [ReadOnly] public NativeArray<SurfaceDrawItem> DrawItems;

        /// <summary>Whether <see cref="DrawItems"/> drives the output instead of the feature arrays.</summary>
        public bool UseDrawItems;

        public TileCoord Coord;
        public uint Extent;
        public float UvScalePerMeter;

        /// <summary>
        /// The tile's ground grid. [ReadOnly] is mandatory: <see cref="GroundGrid.Heights"/>/
        /// <see cref="GroundGrid.Normals"/> are ref-counted and shared between NEIGHBORING tiles through
        /// <see cref="GeoFoundry.Ground.DemGroundGridProvider"/> (used as sampler grids by other tiles). Without
        /// [ReadOnly] the safety system treats the job as a writer to these NativeArrays and blocks ANY concurrent
        /// read, including synchronous reads by other handlers and tiles of the same shared grid, until
        /// <see cref="JobHandle.Complete"/>. [ReadOnly] lets many readers run concurrently.
        ///
        /// Always required and valid (without terrain elevation, a flat 1×1 grid from <c>FlatGroundGridProvider</c>);
        /// geometry is also clipped to the tile boundary by it.
        /// </summary>
        [ReadOnly] public GroundGrid Grid;

        /// <summary>
        /// <c>false</c> (default): indices in <see cref="Triangles"/> are feature-local (0-based from the start of the
        /// feature's vertex range), and each <see cref="MeshRange"/> becomes a separate <see cref="Mesh"/>.
        /// <c>true</c>: indices are global (0-based from the start of the WHOLE buffer), and the whole buffer becomes
        /// ONE <see cref="Mesh"/> (see <see cref="AsyncGeometryWorker.BuildSurfaceMeshAsync"/>).
        /// </summary>
        public bool EmitGlobalIndices;

        public MercatorPivot LocalFrame;

        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<float2> Uvs;
        /// <summary>Per-vertex output parallel to <see cref="Positions"/>; see <see cref="FeatureColors"/>.</summary>
        public NativeList<Color32> Colors;
        public NativeList<int> Triangles;
        public NativeList<MeshRange> Ranges;
        /// <summary>Source feature index (into the Feature* arrays) of each output MeshRange. The handler uses it
        /// for the feature id (GameObject name) and any rendering properties.</summary>
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

            try
            {
                int itemCount = UseDrawItems ? DrawItems.Length : FeatureGeomTypes.Length;
                for (int item = 0; item < itemCount; item++)
                {
                    int fi = UseDrawItems ? DrawItems[item].FeatureIndex : item;
                    if ((uint)fi >= (uint)FeatureGeomTypes.Length || FeatureGeomTypes[fi] != MvtGeomType.Polygon) continue;

                    NativeMvtPolygonHelpers.SplitMultiPolygon(
                        FeatureRingRange[fi], Rings, RingPoints, Coord, Extent,
                        ref polyPoints, ref polyRingRanges, ref polyPolygonRanges);

                    if (polyPolygonRanges.Length == 0) continue;

                    Color32 color = UseDrawItems ? DrawItems[item].Color
                        : hasColors ? FeatureColors[fi] : new Color32(255, 255, 255, 255);

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
                            AppendTriangle(a, b, c, bounds, tileW, tileH, uvFactor, idxBase, color);
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
            }
        }

        private void AppendTriangle(
            double2 a, double2 b, double2 c, double4 bounds, double tileW, double tileH,
            float uvFactor, int idxBase, Color32 color)
        {
            float2 ua = ToUv(a, bounds, tileW, tileH);
            float2 ub = ToUv(b, bounds, tileW, tileH);
            float2 uc = ToUv(c, bounds, tileW, tileH);

            int before = Positions.Length;
            NativeGroundProjector.ProjectTriangle(ua, ub, uc, Grid, LocalFrame, ref Positions, ref Normals, ref Uvs);
            int after = Positions.Length;

            for (int vi = before; vi < after; vi++)
            {
                Uvs[vi] = Uvs[vi] * uvFactor;
                Colors.Add(color);
                Triangles.Add(vi - idxBase);
            }
        }

        private static float2 ToUv(double2 m, double4 bounds, double tileW, double tileH) =>
            new float2((float)((m.x - bounds.x) / tileW), (float)((m.y - bounds.y) / tileH));
    }
}
