using System.Threading;
using GeoFoundry.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Assembles a <see cref="Mesh"/> from geometry buffers through the Advanced Mesh API: defines the
    /// vertex/index buffer format and the Burst conversion into it.
    ///
    /// Layout: Position Float32×3 (stream 0); Normal SNorm8×4 (stream 1; 4 bytes instead of 12 for Float32×3, w is
    /// unused and only pads the attribute to 4 bytes; angular error ≤ ~0.5°; shaders read NORMAL as float3 and the
    /// GPU unpacks SNorm on vertex fetch); TexCoord0 Float32×2 (stream 2); optional Color UNorm8×4 (stream 3).
    /// Indices are UInt16 if the mesh fits in <see cref="MaxUInt16Vertices"/>, otherwise UInt32.
    ///
    /// Conversion (normal packing, int → ushort) runs ONLY in Burst jobs: an element-wise loop over a NativeArray
    /// on the main thread is very slow (an atomic safety handle check on every access; profiled at up to
    /// ~390 ms per frame).
    /// </summary>
    internal static class MeshAssembler
    {
        /// <summary>
        /// 65535 rather than 65536: WebGL2 always enables fixed-index primitive restart, so index <c>0xFFFF</c> in a
        /// UInt16 buffer breaks the primitive; the maximum usable index is 65534.
        /// </summary>
        private const int MaxUInt16Vertices = 65535;

        private static readonly VertexAttributeDescriptor NormalDescriptor =
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4, 1);

        private static IndexFormat ChooseIndexFormat(int vertexCount) =>
            vertexCount <= MaxUInt16Vertices ? IndexFormat.UInt16 : IndexFormat.UInt32;

        private static void PackNormals(NativeArray<float3> src, NativeArray<uint> dst)
        {
            for (int i = 0; i < src.Length; i++)
            {
                int3 q = (int3)math.round(math.clamp(src[i], -1f, 1f) * 127f);
                dst[i] = (uint)(q.x & 0xFF) | ((uint)(q.y & 0xFF) << 8) | ((uint)(q.z & 0xFF) << 16);
            }
        }

        /// <summary>
        /// Writes indices into a raw (byte) index buffer: one job field serves both formats, with no dummy array for
        /// the unused one (the Job System requires all container fields to be constructed).
        /// </summary>
        private static void WriteIndices(NativeArray<int> src, NativeArray<byte> dstBytes, bool use16)
        {
            if (use16)
            {
                var dst = dstBytes.Reinterpret<ushort>(1);
                for (int i = 0; i < src.Length; i++) dst[i] = (ushort)src[i];
            }
            else
            {
                dstBytes.Reinterpret<int>(1).CopyFrom(src);
            }
        }

        /// <summary>Normals and indices of one range for <see cref="BuildMeshes"/> (invoked via <c>Run()</c>).</summary>
        [BurstCompile]
        private struct PackNormalsAndIndicesJob : IJob
        {
            [ReadOnly] public NativeArray<float3> SrcNormals;
            [ReadOnly] public NativeArray<int> SrcTriangles;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<uint> DstNormals;
            [NativeDisableContainerSafetyRestriction] public NativeArray<byte> DstIndexBytes;
            public bool Use16BitIndices;

            public void Execute()
            {
                PackNormals(SrcNormals, DstNormals);
                WriteIndices(SrcTriangles, DstIndexBytes, Use16BitIndices);
            }
        }

        /// <summary>
        /// Builds one independent <see cref="Mesh"/> per <paramref name="ranges"/>[i] from the shared layer buffers
        /// through the Advanced Mesh API. Positions, UVs and colors are bulk-copied with <c>CopyFrom</c>; normals
        /// (SNorm8×4) and indices (UInt16 or UInt32, see <see cref="ChooseIndexFormat"/>) go through the Burst
        /// <see cref="PackNormalsAndIndicesJob"/>.<c>Run()</c>, with no element-wise loops on the main thread.
        /// Colors are written only if <paramref name="colors"/> has one entry per position. Must be called on the
        /// main thread.
        /// </summary>
        public static Mesh[] BuildMeshes(
            NativeArray<float3> positions, NativeArray<float3> normals, NativeArray<float2> uvs,
            NativeArray<int> triangles, NativeArray<MeshRange> ranges, NativeArray<Color32> colors = default)
        {
            int count = ranges.Length;
            if (count == 0) return System.Array.Empty<Mesh>();

            bool hasColors = colors.Length == positions.Length;

            var dataArray = Mesh.AllocateWritableMeshData(count);
            var meshes = new Mesh[count];

            for (int i = 0; i < count; i++)
            {
                var range = ranges[i];
                var data = dataArray[i];

                if (hasColors)
                {
                    data.SetVertexBufferParams(range.VertexCount,
                        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                        NormalDescriptor,
                        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 2),
                        new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 3));
                }
                else
                {
                    data.SetVertexBufferParams(range.VertexCount,
                        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                        NormalDescriptor,
                        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 2));
                }

                var indexFormat = ChooseIndexFormat(range.VertexCount);
                data.SetIndexBufferParams(range.TriCount, indexFormat);

                data.GetVertexData<float3>(0).CopyFrom(positions.GetSubArray(range.VertexStart, range.VertexCount));
                data.GetVertexData<float2>(2).CopyFrom(uvs.GetSubArray(range.VertexStart, range.VertexCount));
                if (hasColors)
                    data.GetVertexData<Color32>(3).CopyFrom(colors.GetSubArray(range.VertexStart, range.VertexCount));

                new PackNormalsAndIndicesJob
                {
                    SrcNormals = normals.GetSubArray(range.VertexStart, range.VertexCount),
                    SrcTriangles = triangles.GetSubArray(range.TriStart, range.TriCount),
                    DstNormals = data.GetVertexData<uint>(1),
                    DstIndexBytes = data.GetIndexData<byte>(),
                    Use16BitIndices = indexFormat == IndexFormat.UInt16,
                }.Run();

                data.subMeshCount = 1;
                data.SetSubMesh(0, new SubMeshDescriptor(0, range.TriCount));

                meshes[i] = new Mesh();
            }

            Mesh.ApplyAndDisposeWritableMeshData(dataArray, meshes);
            for (int i = 0; i < count; i++) meshes[i].RecalculateBounds();
            return meshes;
        }

        /// <summary>
        /// Bulk-copies geometry buffers into the writable buffers of a <see cref="Mesh.MeshData"/> FROM a job (not on
        /// the main thread); the Advanced Mesh API is designed so that buffers obtained via
        /// <c>GetVertexData</c>/<c>GetIndexData</c> before <c>Apply</c> are writable by jobs. Same compact layout as
        /// <see cref="BuildMeshes"/>: normals packed into SNorm8×4, indices UInt16 or UInt32 per
        /// <c>Use16BitIndices</c>.
        /// </summary>
        [BurstCompile]
        private struct FillMeshDataJob : IJob
        {
            [ReadOnly] public NativeArray<float3> SrcPositions;
            [ReadOnly] public NativeArray<float3> SrcNormals;
            [ReadOnly] public NativeArray<float2> SrcUvs;
            [ReadOnly] public NativeArray<Color32> SrcColors;
            [ReadOnly] public NativeArray<int> SrcTriangles;

            // MeshData.GetVertexData<T>(stream) for DIFFERENT streams of one MeshData returns NativeArray views that
            // the job safety system cannot tell apart at Schedule() time and reports as aliasing, although they are
            // distinct buffers. NativeDisableContainerSafetyRestriction disables that check, uniformly on all Dst*.
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> DstPositions;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<uint> DstNormals;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float2> DstUvs;
            [WriteOnly, NativeDisableContainerSafetyRestriction] public NativeArray<Color32> DstColors;
            [NativeDisableContainerSafetyRestriction] public NativeArray<byte> DstIndexBytes;
            public bool Use16BitIndices;

            public void Execute()
            {
                DstPositions.CopyFrom(SrcPositions);
                PackNormals(SrcNormals, DstNormals);
                DstUvs.CopyFrom(SrcUvs);
                DstColors.CopyFrom(SrcColors);
                WriteIndices(SrcTriangles, DstIndexBytes, Use16BitIndices);
            }
        }

        /// <summary>
        /// Assembles ONE <see cref="Mesh"/> from buffers accumulated for the whole layer (not per feature). Called
        /// AFTER the geometry job has completed (see <see cref="AsyncGeometryWorker.BuildSurfaceMeshAsync"/> and
        /// similar), with <c>Triangles</c> already written as GLOBAL indices (<c>EmitGlobalIndices = true</c>), so the
        /// whole buffer becomes the mesh's single vertex/index buffer without splitting into ranges.
        ///
        /// Runs in phases so that even the bulk copy into the mesh buffers happens on a worker thread rather than
        /// synchronously on the main thread (only <see cref="Mesh"/> allocation and Apply must run on the main thread,
        /// as Unity API): (1) main thread: cheap <c>AllocateWritableMeshData</c>/<c>SetVertexBufferParams</c>/
        /// <c>SetIndexBufferParams</c> without copying data; (2) <see cref="FillMeshDataJob"/> on a worker thread
        /// copies the geometry into those buffers; (3) main thread: <c>Apply</c>/<c>RecalculateBounds</c>.
        ///
        /// Like every other job, the fill job is awaited to completion even on cancellation (it reads the caller's
        /// buffers, see <see cref="JobHandleAwaitExtensions.WaitNonBlockingAsync"/>). If <paramref name="ct"/> is
        /// cancelled by the time it completes, no <see cref="Mesh"/> is created and <c>null</c> is returned: after
        /// exiting Play Mode a build finishes in Edit Mode, and the created mesh (and the handler's GameObject with
        /// it) would otherwise remain in the editor scene. Also returns <c>null</c> if the buffers are empty.
        /// </summary>
        internal static async Awaitable<Mesh> BuildSingleMeshAsync(
            NativeList<float3> positions, NativeList<float3> normals, NativeList<float2> uvs,
            NativeList<Color32> colors, NativeList<int> triangles, string name, CancellationToken ct)
        {
            int vertCount = positions.Length;
            int triCount = triangles.Length;
            if (vertCount == 0 || triCount == 0) return null;

            var dataArray = Mesh.AllocateWritableMeshData(1);
            var data = dataArray[0];

            data.SetVertexBufferParams(vertCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                NormalDescriptor,
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 2),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 3));
            var indexFormat = ChooseIndexFormat(vertCount);
            data.SetIndexBufferParams(triCount, indexFormat);

            var fillJob = new FillMeshDataJob
            {
                SrcPositions = positions.AsArray(),
                SrcNormals = normals.AsArray(),
                SrcUvs = uvs.AsArray(),
                SrcColors = colors.AsArray(),
                SrcTriangles = triangles.AsArray(),
                DstPositions = data.GetVertexData<float3>(0),
                DstNormals = data.GetVertexData<uint>(1),
                DstUvs = data.GetVertexData<float2>(2),
                DstColors = data.GetVertexData<Color32>(3),
                DstIndexBytes = data.GetIndexData<byte>(),
                Use16BitIndices = indexFormat == IndexFormat.UInt16,
            };

            JobHandle handle = fillJob.Schedule();
            if (!await handle.WaitNonBlockingAsync(ct))
            {
                dataArray.Dispose();
                return null;
            }

            data.subMeshCount = 1;
            data.SetSubMesh(0, new SubMeshDescriptor(0, triCount));

            var mesh = new Mesh { name = name };
            Mesh.ApplyAndDisposeWritableMeshData(dataArray, new[] { mesh });
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
