using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Builds the terrain mesh of one tile: an (N+1)² vertex grid written to NativeList outputs, the same pattern
    /// as <see cref="SurfaceBuildJob"/>, <see cref="RoadBuildJob"/> and <see cref="BuildingBuildJob"/>.
    ///
    /// Unlike them, one <see cref="IJob"/> builds EXACTLY one mesh (no per-feature batch), and <see cref="Grid"/>
    /// (heights and normals) is fully computed beforehand by the ground grid provider, outside this job. There are
    /// no schema properties and no multi-tile sampling here; the caller guarantees a valid non-empty grid (see
    /// <see cref="AsyncGeometryWorker.BuildTerrainMeshAsync"/>). Indices in <see cref="Triangles"/> are absolute
    /// (single output mesh).
    ///
    /// <see cref="GroundGrid.Normals"/> is checked by <c>Length</c> rather than <c>IsCreated</c>: the Job System
    /// requires ALL NativeContainer fields (including those nested in <see cref="GroundGrid"/>) to be validly
    /// constructed at <c>Schedule()</c> time, so a scheduled job always sees <c>IsCreated == true</c>. "No normals"
    /// is expressed by a valid array of a different (e.g. zero) length, in which case <c>Vector3.up</c> is used.
    /// </summary>
    [BurstCompile]
    internal struct TerrainBuildJob : IJob
    {
        [ReadOnly] public GroundGrid Grid;
        public MercatorPivot LocalFrame;

        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<float2> Uvs;
        public NativeList<int> Triangles;

        public void Execute()
        {
            int n = Grid.SegmentsPerSide;
            int vps = n + 1;
            bool hasNormals = Grid.Normals.Length == vps * vps;

            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++)
                {
                    Positions.Add((float3)Grid.WorldFromGridCorner(i, j, LocalFrame));
                    Vector3 normal = hasNormals ? Grid.Normals[j * vps + i] : Vector3.up;
                    Normals.Add((float3)normal);
                    Uvs.Add(new float2((float)i / n, (float)j / n));
                }
            }

            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int i00 = j * vps + i;
                    int i10 = j * vps + (i + 1);
                    int i01 = (j + 1) * vps + i;
                    int i11 = (j + 1) * vps + (i + 1);
                    Triangles.Add(i00); Triangles.Add(i01); Triangles.Add(i11);
                    Triangles.Add(i00); Triangles.Add(i11); Triangles.Add(i10);
                }
            }
        }
    }
}
