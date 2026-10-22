using System.Collections;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Geometry;
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
    /// <summary>
    /// Draw-list geometry (<see cref="AsyncGeometryWorker.BuildLineMeshAsync"/>, the draw-item overload of
    /// <see cref="AsyncGeometryWorker.BuildSurfaceMeshAsync(GeoFoundry.Core.Mvt.NativeMvtLayer, NativeArray{SurfaceDrawItem}, TileCoord, GeoFoundry.Core.Ground.GroundGrid, MercatorPivot, float, System.Threading.CancellationToken)"/>),
    /// <see cref="GroundAnchors"/> and the label layout (<see cref="OmtLabelLayoutJob"/>) on synthetic data.
    /// </summary>
    public sealed class OmtGeometryAndLabelTests
    {
        private static readonly TileCoord Coord = new TileCoord(14, 9903, 5145);
        private static readonly MercatorPivot Frame = MercatorPivot.FromLonLat(37.6, 55.75);

        [UnityTest]
        public IEnumerator LineMesh_DrawItems_WriteLineAttributes_AndOutlinePolygons() => OmtTestTile.Run(async ct =>
        {
            using var tile = new OmtTestTile("t")
                .Line(new[] { new int2(500, 500), new int2(3500, 500) })
                .Polygon(OmtTestTile.Square(1000, 1000, 3000, 3000))
                .Build();
            using var ground = new FlatGroundGridProvider();
            var items = new NativeArray<LineDrawItem>(new[]
            {
                new LineDrawItem(0, new LineWidth(8f, 2f), new Color32(255, 0, 0, 255), new float2(0.5f, 0.25f),
                    LineDrawFlags.None, 13f),
                new LineDrawItem(1, new LineWidth(4f), new Color32(0, 255, 0, 255), default, LineDrawFlags.PolygonOutline),
            }, Allocator.Persistent);
            try
            {
                var mesh = await AsyncGeometryWorker.BuildLineMeshAsync(
                    tile.GetLayer(0), items, Coord, GroundTestUtils.FlatGrid(ground, Coord), Frame, 1f, ct);
                Assert.That(mesh, Is.Not.Null);
                Assert.That(mesh.vertexBufferCount, Is.EqualTo(4), "within the WebGL limit of 4 vertex streams");
                for (int c = 0; c < 3; c++)
                {
                    var attribute = VertexAttribute.TexCoord0 + c;
                    Assert.That(mesh.GetVertexAttributeDimension(attribute), Is.EqualTo(4), attribute.ToString());
                    Assert.That(mesh.GetVertexAttributeStream(attribute), Is.EqualTo(2), attribute.ToString());
                }

                var uv0 = new System.Collections.Generic.List<Vector4>();
                var uv1 = new System.Collections.Generic.List<Vector4>();
                var uv2 = new System.Collections.Generic.List<Vector4>();
                mesh.GetUVs(0, uv0);
                mesh.GetUVs(1, uv1);
                mesh.GetUVs(2, uv2);
                var positions = mesh.vertices;
                var colors = mesh.colors32;
                bool line = false, outline = false;
                float axisZ = float.NaN;
                for (int i = 0; i < positions.Length; i++)
                {
                    if (colors[i].r == 255)
                    {
                        line = true;
                        Assert.That(uv1[i], Is.EqualTo(new Vector4(8f, 2f, 0f, 0f)), "the item's LineWidth");
                        Assert.That(uv2[i], Is.EqualTo(new Vector4(0.5f, 0.25f, 4f, 13f)), "dash, built half width, min zoom");
                        // The line runs along X: the extrusion is across it (Z) and leads back to the axis.
                        Assert.That(uv0[i].z, Is.EqualTo(0f).Within(1e-4f));
                        Assert.That(Mathf.Abs(uv0[i].w), Is.LessThanOrEqualTo(1f + 1e-4f));
                        float z = positions[i].z - uv0[i].w * 4f;
                        if (float.IsNaN(axisZ)) axisZ = z;
                        Assert.That(z, Is.EqualTo(axisZ).Within(1e-2f), "every vertex resolves to the line axis");
                    }
                    if (colors[i].g == 255 && uv2[i].x == 0f && uv2[i].z == 2f) outline = true;
                }
                Assert.That(line, Is.True);
                Assert.That(outline, Is.True, "the polygon outline is emitted (solid, half width 2 m)");
                Object.Destroy(mesh);
            }
            finally
            {
                items.Dispose();
            }
        });

        [UnityTest]
        public IEnumerator SurfaceMesh_DrawItems_EmitOnlyListedFeaturesWithTheirColors() => OmtTestTile.Run(async ct =>
        {
            using var tile = new OmtTestTile("t")
                .Polygon(OmtTestTile.Square(100, 100, 1000, 1000))
                .Polygon(OmtTestTile.Square(2000, 2000, 3000, 3000))
                .Build();
            using var ground = new FlatGroundGridProvider();
            var color = new Color32(10, 20, 30, 255);
            var items = new NativeArray<SurfaceDrawItem>(new[] { new SurfaceDrawItem(1, color) }, Allocator.Persistent);
            try
            {
                var mesh = await AsyncGeometryWorker.BuildSurfaceMeshAsync(
                    tile.GetLayer(0), items, Coord, GroundTestUtils.FlatGrid(ground, Coord), Frame, 1f, ct);
                Assert.That(mesh, Is.Not.Null);
                foreach (var c in mesh.colors32) Assert.That(c, Is.EqualTo(color));
                Assert.That(mesh.HasVertexAttribute(VertexAttribute.TexCoord1), Is.False);
                Object.Destroy(mesh);
            }
            finally
            {
                items.Dispose();
            }
        });

        [Test]
        public void GroundAnchors_RejectBufferPoints()
        {
            using var ground = new FlatGroundGridProvider();
            var grid = GroundTestUtils.FlatGrid(ground, Coord);
            Assert.That(GroundAnchors.TryProjectPoint(new int2(2048, 2048), Coord, 4096, grid, Frame, out _, out var uv), Is.True);
            Assert.That(uv.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(uv.y, Is.EqualTo(0.5f).Within(1e-4f), "tile-local Y down maps to UV V up (center stays center)");
            Assert.That(GroundAnchors.TryProjectPoint(new int2(-10, 100), Coord, 4096, grid, Frame, out _, out _), Is.False);
            Assert.That(GroundAnchors.TryProjectPoint(new int2(100, 4096), Coord, 4096, grid, Frame, out _, out _), Is.False);
        }

        [Test]
        public void LabelLayout_DeclutersByTier_RejectsBufferPoints_AndRotatesLineLabels()
        {
            using var tile = new OmtTestTile("place")
                .Point(2000, 2000)        // 0: placed at the tile zoom
                .Point(2040, 2000)        // 1: overlaps 0 at the tile zoom, fits deeper (~5 px apart at tier 0)
                .Point(2000, 2000)        // 2: same position as 0: never fits
                .Point(-20, 1000)         // 3: in the buffer: rejected
                .Line(new[] { new int2(200, 3500), new int2(3800, 3500) }) // 4: line label
                .Build();

            var requests = new NativeArray<OmtLabelRequest>(5, Allocator.TempJob);
            for (int i = 0; i < 4; i++) requests[i] = OmtLabelRequest.Create(i, OmtLabelKind.Point, 0, Text, i);
            var lineRequest = OmtLabelRequest.Create(4, OmtLabelKind.Line, 0, Text, 4);
            lineRequest.Spacing = 1000f; // longer than the line: one label in the middle
            requests[4] = lineRequest;

            RunLayout(tile, requests, firstTier: 0, lastTier: 5, (positions, vertices, indices, subMeshes) =>
            {
                Assert.That(positions.Length, Is.EqualTo(3 * QuadsPerLabel * 4), "labels 0, 1 and the line label");
                Assert.That(subMeshes.Length, Is.EqualTo(1));
                Assert.That(subMeshes[0].x, Is.EqualTo(1), "glyph texture slot");
                Assert.That(indices.Length, Is.EqualTo(3 * QuadsPerLabel * 6));

                const int vPerLabel = QuadsPerLabel * 4;
                Assert.That(vertices[0].Uv1.z, Is.EqualTo(14f), "the first label shows from the tile zoom");
                Assert.That(vertices[vPerLabel].Uv1.z, Is.GreaterThan(14f), "the overlapping label appears at a deeper zoom");
                Assert.That(vertices[2 * vPerLabel].Uv2.w, Is.EqualTo(1f), "line labels rotate along the line (kept upright)");
                Assert.That(math.length(vertices[2 * vPerLabel].Uv2.xyz), Is.EqualTo(1f).Within(1e-3f));
                Assert.That(positions[0].y, Is.EqualTo(2f).Within(1e-4f), "anchors are lifted above flat ground");
            });
        }

        [Test]
        public void LabelLayout_NegativeTiers_ShowLabelsBelowTheTileZoom()
        {
            using var tile = new OmtTestTile("place")
                .Point(2000, 2000)        // 0: placed from the first tier
                .Point(2040, 2000)        // 1: overlaps 0 down to tier 2
                .Point(1000, 1000)        // 2: alone, but its style shows it from zoom 15
                .Build();

            var requests = new NativeArray<OmtLabelRequest>(3, Allocator.TempJob);
            for (int i = 0; i < 3; i++) requests[i] = OmtLabelRequest.Create(i, OmtLabelKind.Point, 0, Text, i);
            var late = requests[2];
            late.MinZoom = 15f;
            requests[2] = late;

            RunLayout(tile, requests, firstTier: -3, lastTier: 4, (positions, vertices, indices, subMeshes) =>
            {
                const int vPerLabel = QuadsPerLabel * 4;
                Assert.That(positions.Length, Is.EqualTo(3 * vPerLabel));
                Assert.That(vertices[0].Uv1.z, Is.EqualTo(11f), "tier -3: shown from three zooms below the tile zoom");
                Assert.That(vertices[vPerLabel].Uv1.z, Is.GreaterThan(11f).And.LessThanOrEqualTo(18f),
                    "the overlapping label appears deeper, within the tiers");
                Assert.That(vertices[2 * vPerLabel].Uv1.z, Is.EqualTo(15f), "the style min zoom still applies");
            });
        }

        // ---- label layout helpers -------------------------------------------

        private static readonly FixedString128Bytes Text = "AB AB";
        private const int QuadsPerLabel = 4; // "AB AB": four glyph quads, the space has none

        /// <summary>
        /// Runs <see cref="OmtLabelLayoutJob"/> over layer 0 of <paramref name="tile"/> with one 12 px text rule and
        /// fake glyphs for "A", "B" and space, then calls <paramref name="check"/> with the outputs. Disposes
        /// <paramref name="requests"/>.
        /// </summary>
        private static void RunLayout(GeoFoundry.Core.Mvt.NativeMvtTile tile, NativeArray<OmtLabelRequest> requests,
            int firstTier, int lastTier,
            System.Action<NativeList<float3>, NativeList<OmtLabelVertex>, NativeList<int>, NativeList<int3>> check)
        {
            using var ground = new FlatGroundGridProvider();
            var rules = new NativeArray<OmtLabelRuleData>(1, Allocator.TempJob);
            rules[0] = new OmtLabelRuleData
            {
                Size = 12f, Color = new Color32(0, 0, 0, 255), HaloWidth = 1f, MaxWidth = 8f, MinZoom = 0f, MaxZoom = 24f,
                Visible = true,
            };

            var glyphs = new NativeHashMap<int, OmtGlyphInfo>(4, Allocator.TempJob);
            var g = new OmtGlyphInfo { Uv = new float4(0, 0, 0.1f, 0.1f), Quad = new float4(0, 0, 0.5f, 0.7f), Advance = 0.6f, Slot = 1 };
            glyphs.TryAdd(OmtText.GlyphKey(OmtFontStyle.Regular, 'A'), g);
            glyphs.TryAdd(OmtText.GlyphKey(OmtFontStyle.Regular, 'B'), g);
            glyphs.TryAdd(OmtText.GlyphKey(OmtFontStyle.Regular, ' '), new OmtGlyphInfo { Advance = 0.25f, Slot = 0 });
            var metrics = new NativeArray<OmtFontMetrics>(3, Allocator.TempJob);
            metrics[0] = new OmtFontMetrics { LineHeight = 1.2f, Ascent = 0.9f, Descent = -0.3f, SpaceAdvance = 0.25f };
            var icons = new NativeArray<OmtIconInfo>(0, Allocator.TempJob);

            var positions = new NativeList<float3>(Allocator.TempJob);
            var vertices = new NativeList<OmtLabelVertex>(Allocator.TempJob);
            var colors = new NativeList<OmtLabelColors>(Allocator.TempJob);
            var indices = new NativeList<int>(Allocator.TempJob);
            var subMeshes = new NativeList<int3>(Allocator.TempJob);
            var bounds = new NativeArray<float3>(2, Allocator.TempJob);
            var occupancy = new NativeParallelMultiHashMap<int, float4>(64, Allocator.TempJob);
            try
            {
                var layer = tile.GetLayer(0);
                new OmtLabelLayoutJob
                {
                    Requests = requests, Rules = rules, Glyphs = glyphs, Metrics = metrics, Icons = icons,
                    GeomTypes = layer.FeatureGeomTypesSlice, FeatureRingRange = layer.FeatureRingRangeSlice,
                    Rings = layer.Rings, RingPoints = layer.RingPoints, Coord = Coord, Extent = layer.Extent,
                    Grid = GroundTestUtils.FlatGrid(ground, Coord), LocalFrame = Frame, Lift = 2f, TileZoom = 14f,
                    FirstTier = firstTier, LastTier = lastTier, Padding = 2f,
                    Positions = positions, Vertices = vertices, Colors = colors,
                    Indices = indices, SubMeshes = subMeshes, Bounds = bounds, Occupancy = occupancy, Occupy = true,
                }.Run();

                check(positions, vertices, indices, subMeshes);
            }
            finally
            {
                rules.Dispose(); requests.Dispose(); glyphs.Dispose(); metrics.Dispose(); icons.Dispose();
                positions.Dispose(); vertices.Dispose(); colors.Dispose();
                indices.Dispose(); subMeshes.Dispose(); bounds.Dispose(); occupancy.Dispose();
            }
        }
    }
}
