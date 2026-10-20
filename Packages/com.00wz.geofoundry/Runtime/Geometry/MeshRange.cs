namespace GeoFoundry.Geometry
{
    /// <summary>
    /// The range of one output <see cref="UnityEngine.Mesh"/> inside the shared layer buffers (<c>positions</c>,
    /// <c>normals</c>, <c>uvs</c>, <c>triangles</c>) as start + count, the same CSR pattern as
    /// <c>NativeMvtTile.Rings</c>/<c>RingPoints</c>. Indices in the corresponding <c>triangles</c> range are
    /// feature-local (0-based relative to <see cref="VertexStart"/>), because each <see cref="MeshRange"/> becomes a
    /// separate <see cref="UnityEngine.Mesh"/> with its own vertex buffer.
    /// </summary>
    internal readonly struct MeshRange
    {
        public readonly int VertexStart, VertexCount, TriStart, TriCount;

        public MeshRange(int vertexStart, int vertexCount, int triStart, int triCount)
        {
            VertexStart = vertexStart;
            VertexCount = vertexCount;
            TriStart = triStart;
            TriCount = triCount;
        }
    }
}
