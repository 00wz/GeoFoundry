using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Geometry
{
    /// <summary>
    /// Output buffers of one geometry job (<see cref="SurfaceBuildJob"/>, <see cref="RoadBuildJob"/>,
    /// <see cref="BuildingBuildJob"/>, <see cref="BuildingTerrainBuildJob"/>, <see cref="TerrainBuildJob"/>) plus
    /// auxiliary inputs owned by the call (empty colors, widths etc., see <see cref="Own{T}"/>). Everything is
    /// released by a single <see cref="Dispose"/>, which must be called only after the job has completed, including
    /// on cancellation (see <see cref="GeoFoundry.Core.JobHandleAwaitExtensions.WaitNonBlockingAsync"/>).
    /// </summary>
    internal sealed class GeometryBuffers
    {
        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<float2> Uvs;
        public NativeList<Color32> Colors;
        public NativeList<int> Triangles;
        public NativeList<MeshRange> Ranges;
        public NativeList<int> RangeFeatureIndices;

        private readonly List<IDisposable> _owned = new();

        private GeometryBuffers() { }

        public static GeometryBuffers Allocate(int featureCount, int vertexCapacity = 256, int indexCapacity = 512)
        {
            int rangeCapacity = math.max(1, featureCount);
            return new GeometryBuffers
            {
                Positions = new NativeList<float3>(vertexCapacity, Allocator.Persistent),
                Normals = new NativeList<float3>(vertexCapacity, Allocator.Persistent),
                Uvs = new NativeList<float2>(vertexCapacity, Allocator.Persistent),
                Colors = new NativeList<Color32>(vertexCapacity, Allocator.Persistent),
                Triangles = new NativeList<int>(indexCapacity, Allocator.Persistent),
                Ranges = new NativeList<MeshRange>(rangeCapacity, Allocator.Persistent),
                RangeFeatureIndices = new NativeList<int>(rangeCapacity, Allocator.Persistent),
            };
        }

        /// <summary>Transfers ownership of <paramref name="array"/> to the buffers (it is disposed with them).</summary>
        public NativeArray<T> Own<T>(NativeArray<T> array) where T : struct
        {
            if (array.IsCreated) _owned.Add(array);
            return array;
        }

        public void Dispose()
        {
            Positions.Dispose();
            Normals.Dispose();
            Uvs.Dispose();
            Colors.Dispose();
            Triangles.Dispose();
            Ranges.Dispose();
            RangeFeatureIndices.Dispose();
            foreach (var owned in _owned) owned.Dispose();
            _owned.Clear();
        }
    }
}
