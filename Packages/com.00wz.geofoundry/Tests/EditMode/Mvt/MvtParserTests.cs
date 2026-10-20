using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Mvt;
using Unity.Mathematics;

namespace GeoFoundry.Tests.EditMode.Vector
{
    public sealed class MvtParserTests
    {
        [Test]
        public void Parse_SinglePolygonTile_RecoversLayerAndFeature()
        {
            var ring = new[]
            {
                new int2(1000, 1000),
                new int2(3000, 1000),
                new int2(3000, 3000),
                new int2(1000, 3000),
            };
            var bytes = MvtTestEncoder.EncodeSinglePolygonTile(
                layerName: "building",
                extent: 4096,
                featureId: 42,
                ring: ring,
                properties: new[]
                {
                    ("class", MvtValue.OfString("residential")),
                    ("render_height", MvtValue.OfDouble(25.5)),
                });

            using var tile = MvtParser.Parse(bytes);

            Assert.That(tile.Layers.Count, Is.EqualTo(1));
            var layer = tile.Layers[0];
            Assert.That(layer.Name.ToString(), Is.EqualTo("building"));
            Assert.That(layer.Version, Is.EqualTo(2u));
            Assert.That(layer.Extent, Is.EqualTo(4096u));
            Assert.That(layer.Features.Count, Is.EqualTo(1));

            var feature = layer.Features[0];
            Assert.That(feature.Id, Is.EqualTo(42ul));
            Assert.That(feature.GeometryType, Is.EqualTo(MvtGeomType.Polygon));
            Assert.That(feature.Properties.ContainsKey("class"));
            Assert.That(feature.Properties["class"].AsString(), Is.EqualTo("residential"));
            Assert.That(feature.Properties["render_height"].AsDouble(), Is.EqualTo(25.5));

            Assert.That(feature.Geometry.Count, Is.EqualTo(1), "one ring");
            var decoded = feature.Geometry[0];
            // ClosePath duplicates the first point, so the length is 5.
            Assert.That(decoded.Length, Is.EqualTo(5));
            for (int i = 0; i < 4; i++)
                Assert.That(decoded[i].Equals(ring[i]), $"point {i}: expected {ring[i]} got {decoded[i]}");
            Assert.That(decoded[4].Equals(ring[0]), "closing point equals the first point");
        }

        [Test]
        public void Parse_EmptyBytes_ReturnsEmptyTile()
        {
            using var tile = MvtParser.Parse(System.Array.Empty<byte>());
            Assert.That(tile.LayerCount, Is.EqualTo(0));
        }

        [Test]
        public void FindLayer_ByName_ReturnsCorrectOne()
        {
            var bytes = MvtTestEncoder.EncodeSinglePolygonTile(
                "water", 4096, 1,
                new[] { new int2(0, 0), new int2(100, 0), new int2(100, 100) });

            using var tile = MvtParser.Parse(bytes);
            Assert.That(tile.FindLayer("water").IsValid, Is.True);
            Assert.That(tile.FindLayer("building").IsValid, Is.False);
        }

        [Test]
        public void MvtTileCoords_TileLocalToMercator_OriginIsNW()
        {
            var tile = new GeoFoundry.Core.Coordinates.TileCoord(14, 9903, 5145);
            var b = tile.BoundsMeters();

            // Tile-local (0,0) is the northwest corner.
            var nw = MvtTileCoords.ToMercator(new int2(0, 0), tile, 4096);
            Assert.That(nw.x, Is.EqualTo(b.x).Within(1e-3));
            Assert.That(nw.y, Is.EqualTo(b.w).Within(1e-3));

            // (extent, extent) is the southeast corner.
            var se = MvtTileCoords.ToMercator(new int2(4096, 4096), tile, 4096);
            Assert.That(se.x, Is.EqualTo(b.z).Within(1e-3));
            Assert.That(se.y, Is.EqualTo(b.y).Within(1e-3));
        }

        [Test]
        public void MvtReader_Varint_RoundTrip()
        {
            // Hand-encoded varint of 300 = 0xAC 0x02.
            var bytes = new byte[] { 0xAC, 0x02 };
            var reader = new MvtReader(bytes);
            Assert.That(reader.ReadVarint32(), Is.EqualTo(300u));
            Assert.That(reader.EndReached, Is.True);
        }

        [Test]
        public void MvtReader_ZigZag_RoundTrip()
        {
            // zigzag(-1) = 1, varint(1) = 0x01.
            var bytes = new byte[] { 0x01 };
            var reader = new MvtReader(bytes);
            Assert.That(reader.ReadZigZag32(), Is.EqualTo(-1));
        }
    }
}
