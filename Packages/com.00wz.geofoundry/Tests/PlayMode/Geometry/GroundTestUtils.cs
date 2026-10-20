using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Ground;
using GeoFoundry.Ground;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GeoFoundry.Tests.PlayMode.Geometry
{
    /// <summary>Shared tile-ground helpers for geometry tests.</summary>
    internal static class GroundTestUtils
    {
        /// <summary>
        /// Returns the flat 1×1 grid of tile <paramref name="coord"/>: ground without terrain elevation, as in
        /// production with elevation disabled. The arrays belong to <paramref name="ground"/>, which must stay alive
        /// until the job completes.
        /// </summary>
        public static GroundGrid FlatGrid(FlatGroundGridProvider ground, TileCoord coord)
        {
            Assert.That(ground.TryGet(coord, out var grid), Is.True);
            return grid;
        }

        /// <summary>Asserts that all vertices lie within the tile bounds (on XZ, in the local frame <paramref name="frame"/>).</summary>
        public static void AssertInsideTile(NativeList<float3> positions, TileCoord coord, MercatorPivot frame)
        {
            for (int i = 0; i < positions.Length; i++) AssertInsideTile(positions[i], i, coord, frame);
        }

        /// <inheritdoc cref="AssertInsideTile(NativeList{float3}, TileCoord, MercatorPivot)"/>
        public static void AssertInsideTile(Vector3[] positions, TileCoord coord, MercatorPivot frame)
        {
            for (int i = 0; i < positions.Length; i++) AssertInsideTile(positions[i], i, coord, frame);
        }

        private static void AssertInsideTile(float3 p, int index, TileCoord coord, MercatorPivot frame)
        {
            const float eps = 1e-2f;
            var b = coord.BoundsMeters();
            float3 min = frame.WorldFromMercator(new double2(b.x, b.y));
            float3 max = frame.WorldFromMercator(new double2(b.z, b.w));

            Assert.That(p.x, Is.InRange(min.x - eps, max.x + eps), $"vertex {index}: X outside the tile bounds");
            Assert.That(p.z, Is.InRange(min.z - eps, max.z + eps), $"vertex {index}: Z outside the tile bounds");
        }
    }
}
