using System;
using System.Threading;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>
    /// Builds one mesh of a tile's building layer with <see cref="OmtBuildingBuildJob"/>. Layout: Position Float32×3
    /// (stream 0); Normal SNorm8×4 (stream 1, see <see cref="MeshPacking"/>); TexCoord0 Float32×2 (stream 2);
    /// <see cref="OmtBuildingVertex"/> (stream 3: Color and TexCoord3 UNorm8×4, TexCoord4 Float16×4), within the 4 vertex
    /// streams allowed on every platform.
    /// </summary>
    internal static class OmtBuildingMesh
    {
        [BurstCompile]
        private struct FillJob : IJob
        {
            [ReadOnly] public NativeArray<float3> SrcPositions;
            [ReadOnly] public NativeArray<float3> SrcNormals;
            [ReadOnly] public NativeArray<float2> SrcUvs;
            [ReadOnly] public NativeArray<OmtBuildingVertex> SrcVertices;
            [ReadOnly] public NativeArray<int> SrcTriangles;

            // MeshData.GetVertexData<T>(stream) for different streams returns views that the job safety system reports as
            // aliasing, although they are distinct buffers (see MeshAssembler.FillMeshDataJob).
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> DstPositions;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<uint> DstNormals;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float2> DstUvs;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<OmtBuildingVertex> DstVertices;
            [NativeDisableContainerSafetyRestriction] public NativeArray<byte> DstIndexBytes;
            public bool Use16BitIndices;

            public void Execute()
            {
                DstPositions.CopyFrom(SrcPositions);
                MeshPacking.PackNormals(SrcNormals, DstNormals);
                DstUvs.CopyFrom(SrcUvs);
                DstVertices.CopyFrom(SrcVertices);
                MeshPacking.WriteIndices(SrcTriangles, DstIndexBytes, Use16BitIndices);
            }
        }

        /// <summary>
        /// Builds the mesh; returns <c>null</c> if the layer has no geometry or <paramref name="ct"/> is cancelled. The jobs
        /// are awaited to completion even on cancellation, because they read the caller's borrowed inputs. With an enabled
        /// <paramref name="fade"/> the bounds are raised to its roof height, which may be above a feature's own top.
        /// </summary>
        public static async Awaitable<Mesh> BuildAsync(
            NativeMvtLayer layer,
            NativeArray<bool> hidden, NativeArray<float> topMeters, NativeArray<float> bottomMeters,
            NativeArray<Color32> featureColors,
            GroundGrid grid, TileCoord coord, MercatorPivot localFrame, OmtBuildingFade fade, CancellationToken ct)
        {
            if (!grid.IsValid) throw new ArgumentException("The tile grid is not set.", nameof(grid));

            var positions = new NativeList<float3>(256, Allocator.Persistent);
            var normals = new NativeList<float3>(256, Allocator.Persistent);
            var uvs = new NativeList<float2>(256, Allocator.Persistent);
            var vertices = new NativeList<OmtBuildingVertex>(256, Allocator.Persistent);
            var triangles = new NativeList<int>(512, Allocator.Persistent);
            try
            {
                var job = new OmtBuildingBuildJob
                {
                    FeatureGeomTypes = layer.FeatureGeomTypesSlice,
                    FeatureRingRange = layer.FeatureRingRangeSlice,
                    Hidden = hidden,
                    TopMeters = topMeters,
                    BottomMeters = bottomMeters,
                    FeatureColors = featureColors,
                    Rings = layer.Rings,
                    RingPoints = layer.RingPoints,
                    Coord = coord,
                    Extent = layer.Extent,
                    Grid = grid,
                    LocalFrame = localFrame,
                    Fade = fade,
                    Positions = positions,
                    Normals = normals,
                    Uvs = uvs,
                    Vertices = vertices,
                    Triangles = triangles,
                };
                if (!await job.Schedule().WaitNonBlockingAsync(ct)) return null;

                int vertexCount = positions.Length;
                int indexCount = triangles.Length;
                if (vertexCount == 0 || indexCount == 0) return null;

                var dataArray = Mesh.AllocateWritableMeshData(1);
                var data = dataArray[0];
                data.SetVertexBufferParams(vertexCount,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                    MeshPacking.NormalDescriptor(),
                    new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 3),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 2),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.UNorm8, 4, 3),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float16, 4, 3));
                var indexFormat = MeshPacking.ChooseIndexFormat(vertexCount);
                data.SetIndexBufferParams(indexCount, indexFormat);

                var fill = new FillJob
                {
                    SrcPositions = positions.AsArray(),
                    SrcNormals = normals.AsArray(),
                    SrcUvs = uvs.AsArray(),
                    SrcVertices = vertices.AsArray(),
                    SrcTriangles = triangles.AsArray(),
                    DstPositions = data.GetVertexData<float3>(0),
                    DstNormals = data.GetVertexData<uint>(1),
                    DstUvs = data.GetVertexData<float2>(2),
                    DstVertices = data.GetVertexData<OmtBuildingVertex>(3),
                    DstIndexBytes = data.GetIndexData<byte>(),
                    Use16BitIndices = indexFormat == IndexFormat.UInt16,
                };
                if (!await fill.Schedule().WaitNonBlockingAsync(ct))
                {
                    dataArray.Dispose();
                    return null;
                }

                data.subMeshCount = 1;
                data.SetSubMesh(0, new SubMeshDescriptor(0, indexCount));

                var mesh = new Mesh { name = $"Buildings_{coord}" };
                Mesh.ApplyAndDisposeWritableMeshData(dataArray, new[] { mesh });
                mesh.RecalculateBounds();
                if (fade.Enabled && fade.TopMeters > 0f)
                {
                    var bounds = mesh.bounds;
                    bounds.Encapsulate(bounds.max + new Vector3(0f, fade.TopMeters, 0f));
                    mesh.bounds = bounds;
                }
                return mesh;
            }
            finally
            {
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                vertices.Dispose();
                triangles.Dispose();
            }
        }
    }
}
