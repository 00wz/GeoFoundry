using System;

namespace GeoFoundry.Core
{
    /// <summary>
    /// Memory held by tile content (or a part of it, such as one layer or terrain instance), in two independent
    /// pools.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="GpuBytes"/>: graphics buffers (mesh vertices and indices). On WebGL they live in the
    ///   browser's WebGL context, outside the WASM heap; overflowing it leads to context loss.</item>
    /// <item><see cref="HeapBytes"/>: memory in the application heap (on WebGL, the WASM heap with a hard
    ///   limit): CPU copies of readable meshes, native arrays and the like held by the tile.</item>
    /// </list>
    /// </remarks>
    public readonly struct TileMemoryUsage : IEquatable<TileMemoryUsage>
    {
        /// <summary>No memory in either pool.</summary>
        public static readonly TileMemoryUsage Zero = default;

        /// <summary>Bytes of graphics buffers.</summary>
        public readonly long GpuBytes;

        /// <summary>Bytes of application heap memory.</summary>
        public readonly long HeapBytes;

        /// <summary>Initializes a value with the given byte counts for each pool.</summary>
        public TileMemoryUsage(long gpuBytes, long heapBytes)
        {
            GpuBytes = gpuBytes;
            HeapBytes = heapBytes;
        }

        public static TileMemoryUsage operator +(TileMemoryUsage a, TileMemoryUsage b) =>
            new TileMemoryUsage(a.GpuBytes + b.GpuBytes, a.HeapBytes + b.HeapBytes);

        public static TileMemoryUsage operator -(TileMemoryUsage a, TileMemoryUsage b) =>
            new TileMemoryUsage(a.GpuBytes - b.GpuBytes, a.HeapBytes - b.HeapBytes);

        /// <summary>Returns the component-wise maximum: the larger of the two values in each pool.</summary>
        public static TileMemoryUsage Max(TileMemoryUsage a, TileMemoryUsage b) =>
            new TileMemoryUsage(Math.Max(a.GpuBytes, b.GpuBytes), Math.Max(a.HeapBytes, b.HeapBytes));

        public static bool operator ==(TileMemoryUsage a, TileMemoryUsage b) => a.Equals(b);
        public static bool operator !=(TileMemoryUsage a, TileMemoryUsage b) => !a.Equals(b);

        public bool Equals(TileMemoryUsage other) => GpuBytes == other.GpuBytes && HeapBytes == other.HeapBytes;
        public override bool Equals(object obj) => obj is TileMemoryUsage other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(GpuBytes, HeapBytes);

        public override string ToString() => $"GPU {GpuBytes / (1024.0 * 1024.0):0.0} MB, heap {HeapBytes / (1024.0 * 1024.0):0.0} MB";
    }
}
