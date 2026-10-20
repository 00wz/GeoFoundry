using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Flat-base extrusion of buildings (<c>building</c>): <see cref="SampleTerrainBase"/> computes a single
    /// per-feature terrain height (the minimum over the footprint) inside this job, and the contract offers no
    /// way to pass in a height computed outside (e.g. shared by a group of features). As a result, multi-feature
    /// buildings (<c>building</c> + <c>building:part</c>) can "fall apart" on slopes, because neighboring
    /// features get independent bases. Not used by the default <c>OmtBuildingHandler</c> (OMT); see
    /// <see cref="BuildingTerrainBuildJob"/>, which is terrain-conforming and avoids this by construction. Kept as
    /// a starting point for schemas that group the features of one building (e.g. by <c>group_id</c>); to be
    /// usable, this job and <see cref="AsyncGeometryWorker.BuildBuildingMeshesAsync"/> would need to accept a
    /// precomputed base height (per feature or per group) instead of the internal
    /// <see cref="SampleTerrainBase"/>. This is not a ready-to-use alternative.
    ///
    /// One <see cref="IJob"/> per tile layer, the same pattern as <see cref="SurfaceBuildJob"/> and
    /// <see cref="RoadBuildJob"/>. For each Polygon feature:
    /// <see cref="NativeMvtPolygonHelpers.SplitMultiPolygon"/> → <see cref="NativePolygonTriangulator.Triangulate"/>
    /// of the footprint → flat roof (plus a floor if <c>bottom&gt;0</c>) plus walls for every edge of every ring.
    ///
    /// Geometry only, independent of the tile schema: <see cref="Hidden"/>, <see cref="TopMeters"/> and
    /// <see cref="BottomMeters"/> come precomputed by the calling handler, which interprets its schema's tags;
    /// this job only indexes them by feature. Rendering
    /// properties that do not affect geometry never reach this job: <see cref="RangeFeatureIndices"/> gives the
    /// handler the source feature index of each emitted <see cref="MeshRange"/> so it can resolve them after the
    /// job. Extending the schema with new rendering properties therefore requires no changes here or in
    /// <see cref="AsyncGeometryWorker.BuildBuildingMeshesAsync"/>.
    ///
    /// The terrain base is the exception: it is not read from tags but depends on the geometry, so it is
    /// computed here on the already assembled <c>polyPoints</c>, per feature, through 9 named
    /// <c>[ReadOnly] SamplerGridView</c> fields (up to 9 neighboring tiles). This repeats the single
    /// <c>[ReadOnly] GroundGrid</c> pattern of the surface and road jobs 9 times, because it is the only
    /// multi-grid layout the job safety system accepts: a <c>NativeArray&lt;GroundGrid&gt;</c> (an array of
    /// structs with nested NativeArrays) gives no such guarantee.
    ///
    /// Indices in <see cref="Triangles"/> are feature-local, as in the other build jobs.
    /// </summary>
    [BurstCompile]
    internal struct BuildingBuildJob : IJob
    {
        /// <summary>
        /// One slot of the 9-grid multi-tile height sampler. An empty slot (<c>Heights.Length == 0</c>) is skipped;
        /// it is used when the neighboring tile is missing or terrain is not configured.
        /// </summary>
        public struct SamplerGridView
        {
            public NativeArray<float> Heights;
            /// <summary>minX, minY, maxX, maxY in Mercator meters.</summary>
            public double4 Bounds;
            public int VertsPerSide;
        }

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
        /// sentinel: opaque white is used (the identity for <c>albedo *= vertexColor</c> in the shader).
        /// </summary>
        [ReadOnly] public NativeArray<Color32> FeatureColors;

        [ReadOnly] public NativeArray<int2> Rings;
        [ReadOnly] public NativeArray<int2> RingPoints;

        [ReadOnly] public SamplerGridView GridCenter;
        [ReadOnly] public SamplerGridView GridNorth;
        [ReadOnly] public SamplerGridView GridSouth;
        [ReadOnly] public SamplerGridView GridEast;
        [ReadOnly] public SamplerGridView GridWest;
        [ReadOnly] public SamplerGridView GridNorthEast;
        [ReadOnly] public SamplerGridView GridNorthWest;
        [ReadOnly] public SamplerGridView GridSouthEast;
        [ReadOnly] public SamplerGridView GridSouthWest;

        public TileCoord Coord;
        public uint Extent;
        public MercatorPivot LocalFrame;

        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<float2> Uvs;
        /// <summary>Per-vertex output parallel to <see cref="Positions"/>; see <see cref="FeatureColors"/>.</summary>
        public NativeList<Color32> Colors;
        public NativeList<int> Triangles;
        public NativeList<MeshRange> Ranges;
        /// <summary>Source feature index (into the Feature* arrays) of each output MeshRange. The handler uses it
        /// for the feature id (GameObject name) and to resolve rendering properties (colour etc.) after the job.</summary>
        public NativeList<int> RangeFeatureIndices;

        public void Execute()
        {
            var polyPoints = new NativeList<double2>(64, Allocator.Temp);
            var polyRingRanges = new NativeList<int2>(4, Allocator.Temp);
            var polyPolygonRanges = new NativeList<int2>(2, Allocator.Temp);
            var triVerts = new NativeList<double2>(64, Allocator.Temp);
            var triIndices = new NativeList<int>(128, Allocator.Temp);

            bool hasColors = FeatureColors.Length == FeatureGeomTypes.Length;

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
                    float terrainBase = SampleTerrainBase(polyPoints, polyRingRanges);
                    Color32 color = hasColors ? FeatureColors[fi] : new Color32(255, 255, 255, 255);

                    int featureVertStart = Positions.Length;
                    int featureTriStart = Triangles.Length;

                    for (int p = 0; p < polyPolygonRanges.Length; p++)
                    {
                        int2 polyRange = polyPolygonRanges[p];
                        var ringRangesSlice = polyRingRanges.AsArray().GetSubArray(polyRange.x, polyRange.y);

                        bool ok = NativePolygonTriangulator.Triangulate(
                            polyPoints.AsArray(), ringRangesSlice, triVerts, triIndices);
                        if (!ok) continue;

                        AppendPolygon(
                            polyPoints, ringRangesSlice, triVerts, triIndices,
                            top, bottom, hasFloor, terrainBase, featureVertStart, color);
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

        /// <summary>
        /// Per-feature terrain offset: the minimum height over all points of all rings of the feature's valid
        /// polygons, computed on the already assembled native points.
        /// </summary>
        private float SampleTerrainBase(NativeList<double2> polyPoints, NativeList<int2> polyRingRanges)
        {
            float minH = float.PositiveInfinity;
            bool any = false;
            for (int r = 0; r < polyRingRanges.Length; r++)
            {
                int2 ringRange = polyRingRanges[r];
                for (int i = 0; i < ringRange.y; i++)
                {
                    if (TrySample(polyPoints[ringRange.x + i], out float h))
                    {
                        if (h < minH) minH = h;
                        any = true;
                    }
                }
            }
            return any ? minH : GroundHeightDefaults.FallbackHeightMeters;
        }

        private bool TrySample(double2 mercator, out float height)
        {
            if (TrySampleGrid(GridCenter, mercator, out height)) return true;
            if (TrySampleGrid(GridNorth, mercator, out height)) return true;
            if (TrySampleGrid(GridSouth, mercator, out height)) return true;
            if (TrySampleGrid(GridEast, mercator, out height)) return true;
            if (TrySampleGrid(GridWest, mercator, out height)) return true;
            if (TrySampleGrid(GridNorthEast, mercator, out height)) return true;
            if (TrySampleGrid(GridNorthWest, mercator, out height)) return true;
            if (TrySampleGrid(GridSouthEast, mercator, out height)) return true;
            if (TrySampleGrid(GridSouthWest, mercator, out height)) return true;
            height = 0f;
            return false;
        }

        private static bool TrySampleGrid(in SamplerGridView grid, double2 mercator, out float height)
        {
            if (grid.Heights.Length == 0)
            {
                height = 0f;
                return false;
            }
            double4 b = grid.Bounds;
            if (mercator.x < b.x || mercator.x > b.z || mercator.y < b.y || mercator.y > b.w)
            {
                height = 0f;
                return false;
            }
            float u = (float)((mercator.x - b.x) / (b.z - b.x));
            float v = (float)((mercator.y - b.y) / (b.w - b.y));
            height = GroundGrid.SampleHeightAtUv(grid.Heights, grid.VertsPerSide, u, v);
            return true;
        }

        private void AppendPolygon(
            NativeList<double2> polyPoints, NativeArray<int2> ringRangesSlice,
            NativeList<double2> triVerts, NativeList<int> triIndices,
            float top, float bottom, bool hasFloor, float terrainBase, int featureVertStart, Color32 color)
        {
            float roofY = terrainBase + top;
            float floorY = terrainBase + bottom;

            int footprintVertCount = triVerts.Length;
            int footprintTriCount = triIndices.Length;

            // ---- Roof (top face), flat.
            int roofLocalBase = Positions.Length - featureVertStart;
            for (int i = 0; i < footprintVertCount; i++)
            {
                double2 v = triVerts[i];
                Positions.Add(LocalFrame.WorldFromMercator(v, roofY));
                Normals.Add(new float3(0f, 1f, 0f));
                Uvs.Add(new float2((float)(v.x * 0.05), (float)(v.y * 0.05)));
                Colors.Add(color);
            }
            for (int i = 0; i < footprintTriCount; i++) Triangles.Add(roofLocalBase + triIndices[i]);

            // ---- Floor (if bottom > 0): reversed winding → normal -Y, visible from below.
            if (hasFloor)
            {
                int floorLocalBase = Positions.Length - featureVertStart;
                for (int i = 0; i < footprintVertCount; i++)
                {
                    double2 v = triVerts[i];
                    Positions.Add(LocalFrame.WorldFromMercator(v, floorY));
                    Normals.Add(new float3(0f, -1f, 0f));
                    Uvs.Add(new float2((float)(v.x * 0.05), (float)(v.y * 0.05)));
                    Colors.Add(color);
                }
                for (int i = 0; i < footprintTriCount; i += 3)
                {
                    Triangles.Add(floorLocalBase + triIndices[i + 0]);
                    Triangles.Add(floorLocalBase + triIndices[i + 2]);
                    Triangles.Add(floorLocalBase + triIndices[i + 1]);
                }
            }

            // ---- Walls for every ring (exterior + holes) with the same winding/normal formula: the normal points
            // LEFT of the walking direction, which is outward for a CW exterior and into the courtyard for a CCW hole.
            for (int r = 0; r < ringRangesSlice.Length; r++)
            {
                AppendWalls(polyPoints, ringRangesSlice[r], roofY, floorY, featureVertStart, color);
            }
        }

        private void AppendWalls(
            NativeList<double2> polyPoints, int2 ringRange, float wallTop, float wallBottom, int featureVertStart, Color32 color)
        {
            int rlen = ringRange.y;
            float wallH = wallTop - wallBottom;

            for (int i = 0; i < rlen; i++)
            {
                double2 a = polyPoints[ringRange.x + i];
                double2 b = polyPoints[ringRange.x + (i + 1) % rlen];
                if (a.Equals(b)) continue; // skip degenerate

                float3 pA_bot = LocalFrame.WorldFromMercator(a, wallBottom);
                float3 pA_top = LocalFrame.WorldFromMercator(a, wallTop);
                float3 pB_bot = LocalFrame.WorldFromMercator(b, wallBottom);
                float3 pB_top = LocalFrame.WorldFromMercator(b, wallTop);

                int baseV = Positions.Length - featureVertStart;
                Positions.Add(pA_bot); Positions.Add(pB_bot); Positions.Add(pB_top); Positions.Add(pA_top);

                float3 edge = new float3(pB_bot.x - pA_bot.x, 0f, pB_bot.z - pA_bot.z);
                float3 n = math.normalize(new float3(-edge.z, 0f, edge.x));
                Normals.Add(n); Normals.Add(n); Normals.Add(n); Normals.Add(n);

                float edgeLen = math.length(edge);
                Uvs.Add(new float2(0f, 0f));
                Uvs.Add(new float2(edgeLen, 0f));
                Uvs.Add(new float2(edgeLen, wallH));
                Uvs.Add(new float2(0f, wallH));

                Colors.Add(color); Colors.Add(color); Colors.Add(color); Colors.Add(color);

                // CW viewed from outside.
                Triangles.Add(baseV + 0); Triangles.Add(baseV + 1); Triangles.Add(baseV + 2);
                Triangles.Add(baseV + 0); Triangles.Add(baseV + 2); Triangles.Add(baseV + 3);
            }
        }
    }
}
