using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// The compact vertex and index formats of the package's meshes, for handlers that fill a <c>Mesh.MeshData</c> from
    /// their own geometry jobs: normals in SNorm8×4 (4 bytes instead of 12 for Float32×3, w is unused and only pads the
    /// attribute; angular error ≤ ~0.5°) and indices in UInt16 whenever the mesh fits. Call the conversions from Burst
    /// jobs: an element-wise loop over a NativeArray on the main thread is very slow.
    /// </summary>
    public static class MeshPacking
    {
        /// <summary>
        /// 65535 rather than 65536: WebGL2 always enables fixed-index primitive restart, so index <c>0xFFFF</c> in a
        /// UInt16 buffer breaks the primitive; the maximum usable index is 65534.
        /// </summary>
        public const int MaxUInt16Vertices = 65535;

        /// <summary>Returns the descriptor of the packed normal in vertex stream <paramref name="stream"/>.</summary>
        public static VertexAttributeDescriptor NormalDescriptor(int stream = 1) =>
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4, stream);

        /// <summary>Returns the smallest index format for a mesh of <paramref name="vertexCount"/> vertices.</summary>
        public static IndexFormat ChooseIndexFormat(int vertexCount) =>
            vertexCount <= MaxUInt16Vertices ? IndexFormat.UInt16 : IndexFormat.UInt32;

        /// <summary>Packs unit normals into SNorm8×4.</summary>
        public static void PackNormals(NativeArray<float3> src, NativeArray<uint> dst)
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
        public static void WriteIndices(NativeArray<int> src, NativeArray<byte> dstBytes, bool use16)
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
    }
}
