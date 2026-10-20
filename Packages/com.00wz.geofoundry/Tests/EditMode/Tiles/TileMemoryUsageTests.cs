using System;
using System.Collections.Generic;
using NUnit.Framework;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Handlers;
using GeoFoundry.Render.Layers;
using GeoFoundry.Tiles;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoFoundry.Tests.EditMode.Tiles
{
    /// <summary>
    /// Tests tile memory accounting: <see cref="MeshMemory.Measure"/>,
    /// <see cref="MeshHierarchyInstance.MemoryUsage"/> and accumulation in
    /// <see cref="GeoFoundry.Tiles.TileData.MemoryUsage"/>.
    /// </summary>
    public sealed class TileMemoryUsageTests
    {
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);

        /// <summary>4 vertices × (12 B position in stream 0 + 12 B normal in stream 1) + 6 UInt32 indices.</summary>
        private const long TestMeshBytes = 4 * 12 + 4 * 12 + 6 * 4;

        private readonly List<UnityEngine.Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _created)
                if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
            _created.Clear();
        }

        private T Track<T>(T obj) where T : UnityEngine.Object
        {
            _created.Add(obj);
            return obj;
        }

        private Mesh MakeTestMesh()
        {
            var mesh = Track(new Mesh());
            mesh.SetVertexBufferParams(4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 1));
            mesh.SetIndexBufferParams(6, IndexFormat.UInt32);
            mesh.SetIndexBufferData(new[] { 0, 1, 2, 0, 2, 3 }, 0, 0, 6);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, 6));
            return mesh;
        }

        private GameObject MakeMeshObject(Mesh mesh, Transform parent = null)
        {
            var go = Track(new GameObject("Mesh"));
            if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go;
        }

        // ---- MeshMemory --------------------------------------------------------

        [Test]
        public void Measure_ReadableMesh_CountsVertexAndIndexBuffers_AndCpuCopy()
        {
            var usage = MeshMemory.Measure(MakeTestMesh());

            Assert.That(usage.GpuBytes, Is.EqualTo(TestMeshBytes));
            Assert.That(usage.HeapBytes, Is.EqualTo(TestMeshBytes), "readable: Unity keeps a CPU copy");
        }

        [Test]
        public void Measure_NonReadableMesh_SameGpuBytes_NoHeap()
        {
            var mesh = MakeTestMesh();
            mesh.UploadMeshData(markNoLongerReadable: true);

            var usage = MeshMemory.Measure(mesh);

            Assert.That(mesh.isReadable, Is.False);
            Assert.That(usage.GpuBytes, Is.EqualTo(TestMeshBytes), "buffer metadata is available without a CPU copy");
            Assert.That(usage.HeapBytes, Is.EqualTo(0));
        }

        [Test]
        public void Measure_UInt16Indices_TwoBytesPerIndex()
        {
            var mesh = Track(new Mesh());
            mesh.SetVertexBufferParams(4, new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0));
            mesh.SetIndexBufferParams(6, IndexFormat.UInt16);
            mesh.SetIndexBufferData(new ushort[] { 0, 1, 2, 0, 2, 3 }, 0, 0, 6);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, 6));

            Assert.That(MeshMemory.Measure(mesh).GpuBytes, Is.EqualTo(4 * 12 + 6 * 2));
        }

        [Test]
        public void Measure_Null_IsZero()
        {
            Assert.That(MeshMemory.Measure(null), Is.EqualTo(TileMemoryUsage.Zero));
        }

        // ---- MeshHierarchyInstance --------------------------------------------

        [Test]
        public void MeshHierarchyInstance_SumsUniqueMeshesOfWholeHierarchy()
        {
            var shared = MakeTestMesh();
            var other = MakeTestMesh();
            var root = MakeMeshObject(shared);
            MakeMeshObject(shared, root.transform); // same mesh is not counted twice
            var inactive = MakeMeshObject(other, root.transform);
            inactive.SetActive(false); // inactive objects also hold memory

            var instance = new MeshHierarchyInstance(root);

            Assert.That(instance.MemoryUsage.GpuBytes, Is.EqualTo(2 * TestMeshBytes));
        }

        // ---- TileData ----------------------------------------------------------

        private sealed class FakeFloatingOriginController : IFloatingOriginController
        {
            public MercatorPivot Current { get; }
            public event Action<PivotChangedEventArgs> PivotChanged { add { } remove { } }

            public FakeFloatingOriginController(MercatorPivot initial) => Current = initial;
        }

        private sealed class FixedLayerInstance : ILayerHandlerInstance
        {
            public TileMemoryUsage MemoryUsage { get; }
            public FixedLayerInstance(long gpu, long heap) => MemoryUsage = new TileMemoryUsage(gpu, heap);
            public void Dispose() { }
        }

        private sealed class FixedTerrainInstance : ITerrainHandlerInstance
        {
            public TileMemoryUsage MemoryUsage { get; }
            public FixedTerrainInstance(long gpu, long heap) => MemoryUsage = new TileMemoryUsage(gpu, heap);
            public void Dispose() { }
        }

        private GeoFoundry.Tiles.TileData MakeTileData()
        {
            var root = Track(new GameObject($"Tile_{Coord}"));
            var controller = new FakeFloatingOriginController(MercatorPivot.FromLonLat(37.6175, 55.7520));
            return new GeoFoundry.Tiles.TileData(Coord, root, controller);
        }

        [Test]
        public void TileData_AccumulatesMemoryAsInstancesAttach()
        {
            var tileData = MakeTileData();
            Assert.That(tileData.MemoryUsage, Is.EqualTo(TileMemoryUsage.Zero));

            tileData.AttachTerrain(new FixedTerrainInstance(100, 1));
            Assert.That(tileData.MemoryUsage, Is.EqualTo(new TileMemoryUsage(100, 1)), "an in-flight tile counts what is already attached");

            tileData.AttachLayerInstance(new FixedLayerInstance(20, 2));
            tileData.AttachLayerInstance(new FixedLayerInstance(3, 0));
            Assert.That(tileData.MemoryUsage, Is.EqualTo(new TileMemoryUsage(123, 3)));
        }

        [Test]
        public void TileData_ReattachedTerrain_ReplacesItsMemory()
        {
            var tileData = MakeTileData();
            tileData.AttachTerrain(new FixedTerrainInstance(100, 0));
            tileData.AttachTerrain(new FixedTerrainInstance(40, 0));

            Assert.That(tileData.MemoryUsage, Is.EqualTo(new TileMemoryUsage(40, 0)));
        }
    }
}
