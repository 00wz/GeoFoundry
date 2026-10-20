using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using Unity.Mathematics;

namespace GeoFoundry.Tests.EditMode.Coordinates
{
    public sealed class MercatorBoundsTests
    {
        [Test]
        public void Contains_PointInside_True()
        {
            var b = new MercatorBounds(0, 0, 100, 100);
            Assert.That(b.Contains(new double2(50, 50)), Is.True);
            Assert.That(b.Contains(new double2(0, 0)), Is.True, "Border counts as inside");
            Assert.That(b.Contains(new double2(100, 100)), Is.True);
        }

        [Test]
        public void Contains_PointOutside_False()
        {
            var b = new MercatorBounds(0, 0, 100, 100);
            Assert.That(b.Contains(new double2(-1, 50)), Is.False);
            Assert.That(b.Contains(new double2(101, 50)), Is.False);
            Assert.That(b.Contains(new double2(50, 101)), Is.False);
        }

        [Test]
        public void DistanceTo_PointInsideOrOnBorder_Zero()
        {
            var b = new MercatorBounds(0, 0, 100, 100);
            Assert.That(b.DistanceTo(new double2(50, 50)), Is.EqualTo(0.0));
            Assert.That(b.DistanceTo(new double2(100, 0)), Is.EqualTo(0.0));
        }

        [Test]
        public void DistanceTo_PointOutside_DistanceToNearestEdgeOrCorner()
        {
            var b = new MercatorBounds(0, 0, 100, 100);
            Assert.That(b.DistanceTo(new double2(150, 50)), Is.EqualTo(50.0).Within(1e-9), "to an edge");
            Assert.That(b.DistanceTo(new double2(130, -40)), Is.EqualTo(50.0).Within(1e-9), "to corner (100, 0)");
        }

        [Test]
        public void Intersects_OverlappingBounds_True()
        {
            var a = new MercatorBounds(0, 0, 100, 100);
            var b = new MercatorBounds(50, 50, 150, 150);
            Assert.That(a.Intersects(b), Is.True);
            Assert.That(b.Intersects(a), Is.True);
        }

        [Test]
        public void Intersects_DisjointBounds_False()
        {
            var a = new MercatorBounds(0, 0, 100, 100);
            var b = new MercatorBounds(200, 200, 300, 300);
            Assert.That(a.Intersects(b), Is.False);
        }

        [Test]
        public void Intersects_TouchingEdges_True()
        {
            // Adjacent tiles must intersect along their shared edge (no gaps in the grid).
            var a = new MercatorBounds(0, 0, 100, 100);
            var b = new MercatorBounds(100, 0, 200, 100);
            Assert.That(a.Intersects(b), Is.True);
        }

        [Test]
        public void FromTile_MatchesTileBoundsMeters()
        {
            var t = new TileCoord(16, 39614, 20580);
            var fromTile = MercatorBounds.FromTile(t);
            var raw = t.BoundsMeters();

            Assert.That(fromTile.MinX, Is.EqualTo(raw.x));
            Assert.That(fromTile.MinY, Is.EqualTo(raw.y));
            Assert.That(fromTile.MaxX, Is.EqualTo(raw.z));
            Assert.That(fromTile.MaxY, Is.EqualTo(raw.w));
        }

        [Test]
        public void Center_OfUnitSquare_IsZeroFive()
        {
            var b = new MercatorBounds(0, 0, 1, 1);
            Assert.That(b.Center.x, Is.EqualTo(0.5));
            Assert.That(b.Center.y, Is.EqualTo(0.5));
        }

        [Test]
        public void Expand_GrowsByMarginOnAllSides()
        {
            var b = new MercatorBounds(0, 0, 100, 100).Expand(10);
            Assert.That(b.MinX, Is.EqualTo(-10));
            Assert.That(b.MinY, Is.EqualTo(-10));
            Assert.That(b.MaxX, Is.EqualTo(110));
            Assert.That(b.MaxY, Is.EqualTo(110));
        }

        [Test]
        public void Encapsulate_PointOutside_ExpandsBounds()
        {
            var b = new MercatorBounds(0, 0, 100, 100).Encapsulate(new double2(200, -50));
            Assert.That(b.MinX, Is.EqualTo(0));
            Assert.That(b.MinY, Is.EqualTo(-50));
            Assert.That(b.MaxX, Is.EqualTo(200));
            Assert.That(b.MaxY, Is.EqualTo(100));
        }
    }
}
