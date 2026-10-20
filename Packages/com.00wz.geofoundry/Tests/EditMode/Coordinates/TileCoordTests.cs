using System;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;

namespace GeoFoundry.Tests.EditMode.Coordinates
{
    public sealed class TileCoordTests
    {
        [Test]
        public void Equality_SameValues_AreEqual()
        {
            var a = new TileCoord(16, 39614, 20580);
            var b = new TileCoord(16, 39614, 20580);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Equality_DifferentValues_AreNotEqual()
        {
            var a = new TileCoord(16, 39614, 20580);
            var b = new TileCoord(16, 39615, 20580);
            var c = new TileCoord(15, 39614, 20580);
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(a, Is.Not.EqualTo(c));
            Assert.That(a != b, Is.True);
        }

        [Test]
        public void Constructor_RejectsZoomOutOfRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TileCoord(-1, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TileCoord(23, 0, 0));
        }

        [Test]
        public void Bounds_OnZoom0_CoversWholeMercator()
        {
            var t = new TileCoord(0, 0, 0);
            var b = t.BoundsMeters();
            Assert.That(b.x, Is.EqualTo(-MercatorProjection.HalfCircumference).Within(1e-3));
            Assert.That(b.z, Is.EqualTo(MercatorProjection.HalfCircumference).Within(1e-3));
            Assert.That(b.w - b.y, Is.EqualTo(MercatorProjection.EarthCircumference).Within(1e-3));
        }

        [Test]
        public void ToString_FormatsAsZSlashXSlashY()
        {
            var t = new TileCoord(16, 39614, 20580);
            Assert.That(t.ToString(), Is.EqualTo("16/39614/20580"));
        }

        [Test]
        public void IsRoot_OnlyTrueForZ0()
        {
            Assert.That(new TileCoord(0, 0, 0).IsRoot, Is.True);
            Assert.That(new TileCoord(1, 0, 0).IsRoot, Is.False);
        }

        [Test]
        public void Parent_OfRoot_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => new TileCoord(0, 0, 0).Parent());
        }

        [Test]
        public void Parent_OfChild_MatchesExpectedFloorDivision()
        {
            var parent = new TileCoord(16, 39614, 20581); // odd X/Y: checks floor division, not rounding
            var expected = new TileCoord(15, 19807, 10290);
            Assert.That(parent.Parent(), Is.EqualTo(expected));
        }

        [Test]
        public void GetChild_AllFourAreDistinctAndDoubleCoordinates()
        {
            var t = new TileCoord(5, 10, 20);
            var children = t.Children();
            Assert.That(children, Has.Length.EqualTo(4));
            Assert.That(children, Is.Unique);
            foreach (var c in children)
            {
                Assert.That(c.Z, Is.EqualTo(6));
                Assert.That(c.X, Is.EqualTo(20).Or.EqualTo(21));
                Assert.That(c.Y, Is.EqualTo(40).Or.EqualTo(41));
            }
        }

        [Test]
        public void GetChild_ThenParent_RoundTrips()
        {
            var t = new TileCoord(8, 100, 50);
            for (int q = 0; q < 4; q++)
                Assert.That(t.GetChild(q).Parent(), Is.EqualTo(t));
        }

        [Test]
        public void GetChild_OutOfRangeQuadrant_Throws()
        {
            var t = new TileCoord(5, 10, 20);
            Assert.Throws<ArgumentOutOfRangeException>(() => t.GetChild(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.GetChild(-1));
        }

        [Test]
        public void GetChildren_WrongSpanLength_Throws()
        {
            // Span<T> is a ref struct and cannot be captured in a lambda, so Assert.Throws(() => ...) does not
            // work here; call directly and catch the exception manually.
            var t = new TileCoord(5, 10, 20);
            bool threw = false;
            try
            {
                Span<TileCoord> tooShort = stackalloc TileCoord[3];
                t.GetChildren(tooShort);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Assert.That(threw, Is.True);
        }

        [Test]
        public void IsYInWorld_TrueOnlyInsideZeroToTilesPerSide()
        {
            Assert.That(new TileCoord(0, 0, 0).IsYInWorld, Is.True);
            Assert.That(new TileCoord(0, 0, -1).IsYInWorld, Is.False);
            Assert.That(new TileCoord(0, 0, 1).IsYInWorld, Is.False);
            Assert.That(new TileCoord(3, 0, 7).IsYInWorld, Is.True);
            Assert.That(new TileCoord(3, 0, 8).IsYInWorld, Is.False);
            Assert.That(new TileCoord(3, -5, 2).IsYInWorld, Is.True, "X does not affect Y");
        }

        [Test]
        public void WrapX_MapsXIntoZeroToTilesPerSide_KeepsZAndY()
        {
            Assert.That(new TileCoord(3, -1, 2).WrapX(), Is.EqualTo(new TileCoord(3, 7, 2)));
            Assert.That(new TileCoord(3, 8, 2).WrapX(), Is.EqualTo(new TileCoord(3, 0, 2)));
            Assert.That(new TileCoord(3, -9, 2).WrapX(), Is.EqualTo(new TileCoord(3, 7, 2)));
            Assert.That(new TileCoord(3, 5, 2).WrapX(), Is.EqualTo(new TileCoord(3, 5, 2)));
            Assert.That(new TileCoord(0, -2, 0).WrapX(), Is.EqualTo(new TileCoord(0, 0, 0)));
        }

        [Test]
        public void Children_UnionOfBoundsEqualsParentBounds()
        {
            var t = new TileCoord(5, 10, 20);
            var parentBounds = t.BoundsMeters();
            var children = t.Children();

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var c in children)
            {
                var b = c.BoundsMeters();
                minX = Math.Min(minX, b.x); minY = Math.Min(minY, b.y);
                maxX = Math.Max(maxX, b.z); maxY = Math.Max(maxY, b.w);
            }

            Assert.That(minX, Is.EqualTo(parentBounds.x).Within(1e-6));
            Assert.That(minY, Is.EqualTo(parentBounds.y).Within(1e-6));
            Assert.That(maxX, Is.EqualTo(parentBounds.z).Within(1e-6));
            Assert.That(maxY, Is.EqualTo(parentBounds.w).Within(1e-6));
        }
    }
}
