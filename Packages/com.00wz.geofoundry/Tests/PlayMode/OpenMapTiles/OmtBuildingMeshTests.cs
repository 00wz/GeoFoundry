using System.Collections;
using GeoFoundry.Core;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Mvt;
using GeoFoundry.Ground;
using GeoFoundry.OpenMapTiles;
using GeoFoundry.Tests.PlayMode.Geometry;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.OpenMapTiles
{
    /// <summary>The fade attributes of <see cref="OmtBuildingBuildJob"/> and the mesh of <see cref="OmtBuildingMesh"/>.</summary>
    public sealed class OmtBuildingMeshTests
    {
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);
        private static readonly MercatorPivot Frame = MercatorPivot.FromLonLat(37, 55);
        private static readonly int2[] Block = OmtTestTile.Square(500, 500, 1500, 1500);
        private static readonly Color32 FeatureColor = new Color32(90, 120, 160, 255);

        [UnityTest]
        public IEnumerator Fade_GivesEveryVertexItsHeightAboveTheStartLook() => OmtTestTile.Run(async ct =>
        {
            using var tile = new OmtTestTile("building").Polygon(Block).Build();
            using var ground = new FlatGroundGridProvider();
            var layer = tile.GetLayer(0);
            var fade = new OmtBuildingFade
            {
                StartZoom = 13.97f, Zooms = 1f, TopMeters = 5f, BottomMeters = 0f,
                Color = new Color32(200, 190, 180, 255),
            };
            var hidden = new NativeArray<bool>(new[] { false }, Allocator.Persistent);
            var top = new NativeArray<float>(new[] { 20f }, Allocator.Persistent);
            var bottom = new NativeArray<float>(new[] { 3f }, Allocator.Persistent);
            var colors = new NativeArray<Color32>(new[] { FeatureColor }, Allocator.Persistent);
            var positions = new NativeList<float3>(Allocator.Persistent);
            var normals = new NativeList<float3>(Allocator.Persistent);
            var uvs = new NativeList<float2>(Allocator.Persistent);
            var vertices = new NativeList<OmtBuildingVertex>(Allocator.Persistent);
            var triangles = new NativeList<int>(Allocator.Persistent);
            try
            {
                var job = new OmtBuildingBuildJob
                {
                    FeatureGeomTypes = layer.FeatureGeomTypesSlice,
                    FeatureRingRange = layer.FeatureRingRangeSlice,
                    Hidden = hidden,
                    TopMeters = top,
                    BottomMeters = bottom,
                    FeatureColors = colors,
                    Rings = layer.Rings,
                    RingPoints = layer.RingPoints,
                    Coord = Coord,
                    Extent = layer.Extent,
                    Grid = GroundTestUtils.FlatGrid(ground, Coord),
                    LocalFrame = Frame,
                    Fade = fade,
                    Positions = positions,
                    Normals = normals,
                    Uvs = uvs,
                    Vertices = vertices,
                    Triangles = triangles,
                };
                Assert.That(await job.Schedule().WaitNonBlockingAsync(ct), Is.True);

                Assert.That(positions.Length, Is.GreaterThan(0));
                Assert.That(vertices.Length, Is.EqualTo(positions.Length), "one fade entry per vertex");
                for (int i = 0; i < positions.Length; i++)
                {
                    var v = vertices[i];
                    float y = positions[i].y;
                    // Roof and wall tops rise from 5 m to 20 m, floor and wall bottoms from 0 m to 3 m.
                    float expected = y > 10f ? 15f : 3f;
                    Assert.That((float)v.Fade.x, Is.EqualTo(expected).Within(1e-2f), $"height delta of vertex {i} (y={y})");
                    Assert.That((float)v.Fade.y, Is.EqualTo(13.97f).Within(0.01f), "start zoom");
                    Assert.That((float)v.Fade.z, Is.EqualTo(1f), "fade length");
                    Assert.That(v.Color, Is.EqualTo(FeatureColor), "feature color");
                    Assert.That(v.FadeColor, Is.EqualTo(fade.Color), "color at the start of the fade");
                }
            }
            finally
            {
                hidden.Dispose();
                top.Dispose();
                bottom.Dispose();
                colors.Dispose();
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                vertices.Dispose();
                triangles.Dispose();
            }
        });

        /// <summary>
        /// The mesh carries the fade channels in stream 3, and its bounds cover the roof at the start of the fade (a 2 m
        /// building starts at the 5 m of the lower zoom).
        /// </summary>
        [UnityTest]
        public IEnumerator BuildAsync_AddsFadeChannels_AndRaisesBounds() => OmtTestTile.Run(async ct =>
        {
            using var tile = new OmtTestTile("building").Polygon(Block).Build();
            using var ground = new FlatGroundGridProvider();
            var hidden = new NativeArray<bool>(new[] { false }, Allocator.Persistent);
            var top = new NativeArray<float>(new[] { 2f }, Allocator.Persistent);
            var bottom = new NativeArray<float>(new[] { 0f }, Allocator.Persistent);
            var colors = new NativeArray<Color32>(new[] { FeatureColor }, Allocator.Persistent);
            var grid = GroundTestUtils.FlatGrid(ground, Coord);
            Mesh faded = null, plain = null;
            try
            {
                var fade = new OmtBuildingFade { StartZoom = 14f, Zooms = 1f, TopMeters = 5f, Color = new Color32(1, 2, 3, 255) };
                faded = await OmtBuildingMesh.BuildAsync(tile.GetLayer(0), hidden, top, bottom, colors, grid, Coord, Frame, fade, ct);
                plain = await OmtBuildingMesh.BuildAsync(tile.GetLayer(0), hidden, top, bottom, colors, grid, Coord, Frame, default, ct);

                Assert.That(faded.GetVertexAttributeFormat(VertexAttribute.TexCoord3), Is.EqualTo(VertexAttributeFormat.UNorm8));
                Assert.That(faded.GetVertexAttributeFormat(VertexAttribute.TexCoord4), Is.EqualTo(VertexAttributeFormat.Float16));
                Assert.That(faded.GetVertexAttributeStream(VertexAttribute.Color), Is.EqualTo(3));
                Assert.That(faded.GetVertexAttributeStream(VertexAttribute.TexCoord4), Is.EqualTo(3));
                Assert.That(faded.colors32[0], Is.EqualTo(FeatureColor), "feature color kept");

                Assert.That(plain.bounds.max.y, Is.EqualTo(2f).Within(1e-3f), "a disabled fade does not raise the bounds");
                Assert.That(faded.bounds.max.y, Is.GreaterThanOrEqualTo(5f), "bounds cover the roof at the start of the fade");
            }
            finally
            {
                if (faded != null) Object.DestroyImmediate(faded);
                if (plain != null) Object.DestroyImmediate(plain);
                hidden.Dispose();
                top.Dispose();
                bottom.Dispose();
                colors.Dispose();
            }
        });
    }
}
