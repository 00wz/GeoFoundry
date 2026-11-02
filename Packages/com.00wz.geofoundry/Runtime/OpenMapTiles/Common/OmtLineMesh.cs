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
    /// Builds one line mesh of a tile layer from an ordered draw list with <see cref="OmtLineBuildJob"/>, for the
    /// <c>GeoFoundry/OMT Line Lit</c> shader. Layout: Position Float32×3 (stream 0); Normal SNorm8×4 (stream 1, see
    /// <see cref="MeshPacking"/>); <see cref="OmtLineVertex"/> (stream 2: TexCoord0 Float32×2, TexCoord3 and TexCoord4
    /// Float32×4, TexCoord5 Float32×2); Color UNorm8×4 (stream 3), within the 4 vertex streams allowed on every platform.
    /// </summary>
    internal static class OmtLineMesh
    {
        [BurstCompile]
        private struct FillJob : IJob
        {
            [ReadOnly] public NativeArray<float3> SrcPositions;
            [ReadOnly] public NativeArray<float3> SrcNormals;
            [ReadOnly] public NativeArray<OmtLineVertex> SrcVertices;
            [ReadOnly] public NativeArray<Color32> SrcColors;
            [ReadOnly] public NativeArray<int> SrcTriangles;

            // MeshData.GetVertexData<T>(stream) for different streams returns views that the job safety system reports as
            // aliasing, although they are distinct buffers (see MeshAssembler.FillMeshDataJob).
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> DstPositions;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<uint> DstNormals;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<OmtLineVertex> DstVertices;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<Color32> DstColors;
            [NativeDisableContainerSafetyRestriction] public NativeArray<byte> DstIndexBytes;
            public bool Use16BitIndices;

            public void Execute()
            {
                DstPositions.CopyFrom(SrcPositions);
                MeshPacking.PackNormals(SrcNormals, DstNormals);
                DstVertices.CopyFrom(SrcVertices);
                DstColors.CopyFrom(SrcColors);
                MeshPacking.WriteIndices(SrcTriangles, DstIndexBytes, Use16BitIndices);
            }
        }

        /// <summary>
        /// Builds the mesh; returns <c>null</c> if nothing is drawn or <paramref name="ct"/> is cancelled. The ribbons are
        /// built and draped onto the ground at their width at <paramref name="metersPerPixel"/> (normally the screen pixel
        /// size of the tile's zoom); the shader resizes them at the camera's pixel size, so the bounds are padded by a
        /// tenth of the tile. The jobs are awaited to completion even on cancellation, because they read the caller's
        /// borrowed inputs. <paramref name="items"/> is borrowed as well.
        /// </summary>
        public static async Awaitable<Mesh> BuildAsync(
            NativeMvtLayer layer, NativeArray<OmtLineDrawItem> items, TileCoord coord, GroundGrid grid,
            MercatorPivot localFrame, float metersPerPixel, CancellationToken ct)
        {
            if (!grid.IsValid) throw new ArgumentException("The tile grid is not set.", nameof(grid));
            if (items.Length == 0) return null;

            var positions = new NativeList<float3>(256, Allocator.Persistent);
            var normals = new NativeList<float3>(256, Allocator.Persistent);
            var uvs = new NativeList<float2>(256, Allocator.Persistent);
            var colors = new NativeList<Color32>(256, Allocator.Persistent);
            var vertices = new NativeList<OmtLineVertex>(256, Allocator.Persistent);
            var triangles = new NativeList<int>(512, Allocator.Persistent);
            try
            {
                var job = new OmtLineBuildJob
                {
                    FeatureGeomTypes = layer.FeatureGeomTypesSlice,
                    FeatureRingRange = layer.FeatureRingRangeSlice,
                    Rings = layer.Rings,
                    RingPoints = layer.RingPoints,
                    DrawItems = items,
                    BaseMetersPerPixel = metersPerPixel,
                    Coord = coord,
                    Extent = layer.Extent,
                    Grid = grid,
                    LocalFrame = localFrame,
                    Positions = positions,
                    Normals = normals,
                    Uvs = uvs,
                    Colors = colors,
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
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4, 2),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 4, 2),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord5, VertexAttributeFormat.Float32, 2, 2));
                var indexFormat = MeshPacking.ChooseIndexFormat(vertexCount);
                data.SetIndexBufferParams(indexCount, indexFormat);

                var fill = new FillJob
                {
                    SrcPositions = positions.AsArray(),
                    SrcNormals = normals.AsArray(),
                    SrcVertices = vertices.AsArray(),
                    SrcColors = colors.AsArray(),
                    SrcTriangles = triangles.AsArray(),
                    DstPositions = data.GetVertexData<float3>(0),
                    DstNormals = data.GetVertexData<uint>(1),
                    DstVertices = data.GetVertexData<OmtLineVertex>(2),
                    DstColors = data.GetVertexData<Color32>(3),
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

                var mesh = new Mesh { name = $"Lines_{coord}" };
                Mesh.ApplyAndDisposeWritableMeshData(dataArray, new[] { mesh });
                mesh.RecalculateBounds();
                var bounds = mesh.bounds;
                float padding = 0.1f * (float)coord.SizeMeters();
                bounds.Expand(new Vector3(2f * padding, 0f, 2f * padding));
                mesh.bounds = bounds;
                return mesh;
            }
            finally
            {
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                colors.Dispose();
                vertices.Dispose();
                triangles.Dispose();
            }
        }

        [BurstCompile]
        private struct RealWidthItemsJob : IJob
        {
            [ReadOnly] public NativeArray<OmtLineDrawItem> Items;
            [WriteOnly] public NativeArray<LineDrawItem> Output;

            public void Execute()
            {
                for (int i = 0; i < Items.Length; i++) Output[i] = ToRealWidth(Items[i]);
            }
        }

        /// <summary>
        /// The package's line item of <paramref name="item"/> at its real width (<see cref="OmtLineWidthMode.RealWorld"/>):
        /// the core and borders in meters, the dash pattern scaled by the core meters; the minimum zoom is dropped.
        /// </summary>
        public static LineDrawItem ToRealWidth(OmtLineDrawItem item) =>
            new LineDrawItem(item.FeatureIndex, item.Width.RealMeters, item.Color, item.Dash * item.Width.Meters, item.Flags);

        /// <summary>
        /// Builds the items at their real width (<see cref="ToRealWidth"/>) with
        /// <see cref="AsyncGeometryWorker.BuildLineMeshAsync"/>, for the <c>GeoFoundry/Line Lit</c> shader; returns
        /// <c>null</c> if nothing is drawn or <paramref name="ct"/> is cancelled. <paramref name="items"/> is borrowed.
        /// </summary>
        public static async Awaitable<Mesh> BuildRealWidthAsync(
            NativeMvtLayer layer, NativeArray<OmtLineDrawItem> items, TileCoord coord, GroundGrid grid,
            MercatorPivot localFrame, CancellationToken ct)
        {
            if (items.Length == 0) return null;
            var real = new NativeArray<LineDrawItem>(items.Length, Allocator.Persistent);
            try
            {
                var job = new RealWidthItemsJob { Items = items, Output = real };
                if (!await job.Schedule().WaitNonBlockingAsync(ct)) return null;
                return await AsyncGeometryWorker.BuildLineMeshAsync(layer, real, coord, grid, localFrame, ct);
            }
            finally
            {
                real.Dispose();
            }
        }
    }
}
