using System.Collections;
using NUnit.Framework;
using GeoFoundry.Geometry.Earcut;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.Geometry
{
    /// <summary>
    /// Schedules <see cref="NativeEarcutJob"/> via Schedule()+Complete() instead of calling it on the main thread:
    /// only a scheduled job catches Burst-incompatible constructs, which EditMode tests do not check.
    /// </summary>
    public sealed class NativeEarcutJobTests
    {
        [UnityTest]
        public IEnumerator ScheduledJob_SquareWithHole_ProducesExpectedTriangleCount()
        {
            var ringPointsArr = new[]
            {
                new double2(0, 0), new double2(20, 0), new double2(20, 20), new double2(0, 20),
                new double2(8, 8), new double2(8, 12), new double2(12, 12), new double2(12, 8),
            };
            var ringRangesArr = new[] { new int2(0, 4), new int2(4, 4) };

            var ringPoints = new NativeArray<double2>(ringPointsArr, Allocator.TempJob);
            var ringRanges = new NativeArray<int2>(ringRangesArr, Allocator.TempJob);
            var vertices = new NativeList<double2>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);

            try
            {
                var job = new NativeEarcutJob
                {
                    RingPoints = ringPoints,
                    RingRanges = ringRanges,
                    Vertices = vertices,
                    Triangles = triangles,
                };

                var handle = job.Schedule();
                yield return null;
                handle.Complete();

                Assert.That(vertices.Length, Is.EqualTo(ringPointsArr.Length));
                Assert.That(triangles.Length, Is.GreaterThan(0));
                Assert.That(triangles.Length % 3, Is.EqualTo(0));
            }
            finally
            {
                ringPoints.Dispose();
                ringRanges.Dispose();
                vertices.Dispose();
                triangles.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ScheduledJob_LargePolygon_MatchesEditModeTriangleCount()
        {
            const int n = 100;
            var ring = new double2[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * math.PI_DBL * i / n;
                ring[i] = new double2(50 * math.cos(a), 50 * math.sin(a));
            }

            var ringPoints = new NativeArray<double2>(ring, Allocator.TempJob);
            var ringRanges = new NativeArray<int2>(new[] { new int2(0, n) }, Allocator.TempJob);
            var vertices = new NativeList<double2>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);

            try
            {
                var job = new NativeEarcutJob
                {
                    RingPoints = ringPoints,
                    RingRanges = ringRanges,
                    Vertices = vertices,
                    Triangles = triangles,
                };

                var handle = job.Schedule();
                yield return null;
                handle.Complete();

                // Simple polygon (no holes) triangulated by ear-clipping: (n-2) triangles.
                Assert.That(triangles.Length, Is.EqualTo((n - 2) * 3));
            }
            finally
            {
                ringPoints.Dispose();
                ringRanges.Dispose();
                vertices.Dispose();
                triangles.Dispose();
            }
        }
    }
}
