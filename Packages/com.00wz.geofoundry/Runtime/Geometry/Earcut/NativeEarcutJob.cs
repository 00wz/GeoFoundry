using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GeoFoundry.Geometry.Earcut
{
    /// <summary>
    /// A job over <see cref="NativePolygonTriangulator"/>, ready for <c>Schedule()</c>/<c>Run()</c>: runs the
    /// triangulation on a worker thread with Burst without a hand-written wrapper. The caller owns
    /// <see cref="Vertices"/> and <see cref="Triangles"/> (allocates and disposes them).
    /// </summary>
    [BurstCompile]
    internal struct NativeEarcutJob : IJob
    {
        [ReadOnly] public NativeArray<double2> RingPoints;
        [ReadOnly] public NativeArray<int2> RingRanges;

        public NativeList<double2> Vertices;
        public NativeList<int> Triangles;

        public void Execute()
        {
            NativePolygonTriangulator.Triangulate(RingPoints, RingRanges, Vertices, Triangles);
        }
    }
}
