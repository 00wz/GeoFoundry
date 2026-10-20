using UnityEngine;
using UnityEngine.Rendering;

namespace GeoFoundry.Core
{
    /// <summary>
    /// Estimates the memory used by a <see cref="Mesh"/> from its buffer metadata, without reading the data itself,
    /// so it also works for non-readable meshes (after <c>Mesh.UploadMeshData(true)</c>).
    /// </summary>
    public static class MeshMemory
    {
        /// <summary>
        /// Returns the estimated memory of <paramref name="mesh"/>; <see cref="TileMemoryUsage.Zero"/> for null.
        /// </summary>
        /// <remarks>
        /// <see cref="TileMemoryUsage.GpuBytes"/> is the sum of the vertex buffers (<c>vertexCount ×</c> the stride
        /// of each stream) and the index buffer (index count of all submeshes × 2 or 4 bytes depending on
        /// <see cref="Mesh.indexFormat"/>). <see cref="TileMemoryUsage.HeapBytes"/> is the same amount if the mesh
        /// is readable (Unity keeps a CPU copy), otherwise 0. Alignment and driver overhead are not included, so
        /// this is a lower bound. Must be called on the main thread.
        /// </remarks>
        public static TileMemoryUsage Measure(Mesh mesh)
        {
            if (mesh == null) return TileMemoryUsage.Zero;

            long bytes = 0;
            long vertexCount = mesh.vertexCount;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
                bytes += vertexCount * mesh.GetVertexBufferStride(stream);

            long indexSize = mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                bytes += (long)mesh.GetIndexCount(submesh) * indexSize;

            return new TileMemoryUsage(bytes, mesh.isReadable ? bytes : 0);
        }
    }
}
