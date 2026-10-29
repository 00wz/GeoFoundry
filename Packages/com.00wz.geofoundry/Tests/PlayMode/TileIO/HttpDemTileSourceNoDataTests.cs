using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.Core.Coordinates;
using GeoFoundry.Core.Sources;
using GeoFoundry.TileIO.Net;
using GeoFoundry.TileIO.Sources;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace GeoFoundry.Tests.PlayMode.TileIO
{
    /// <summary>
    /// Missing data of <see cref="HttpDemTileSource"/> against a local server (<see cref="LoopbackHttpServer"/>): a
    /// missing-data status is a tile without data, as <see cref="IDemTileSource"/> defines it, rather than an error.
    /// </summary>
    public sealed class HttpDemTileSourceNoDataTests
    {
        private static readonly TileCoord Coord = new TileCoord(12, 2477, 1280);

        private static IEnumerator WaitFor(Func<bool> condition, string what, float timeoutSeconds = 10f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(condition(), Is.True, $"timed out waiting for: {what}");
        }

        [UnityTest]
        public IEnumerator DefaultNoDataStatus_ReturnsHeightsNotCreated()
        {
            foreach (long status in HttpDemTileSource.DefaultNoDataStatuses)
            {
                // The URL has no placeholders, so every tile resolves to the server.
                using var empty = new LoopbackHttpServer(Array.Empty<byte>(), statusCode: (int)status);
                using var source = new HttpDemTileSource(empty.Url, maxConcurrent: 1);

                var load = source.GetHeightsAsync(Coord, Allocator.Persistent, CancellationToken.None);
                yield return WaitFor(() => load.GetAwaiter().IsCompleted, $"HTTP {status} response");

                Assert.That(load.GetAwaiter().GetResult().IsCreated, Is.False, $"HTTP {status} is a tile without data");
            }
        }

        [UnityTest]
        public IEnumerator EmptyNoDataStatuses_404_ThrowsTileDownloadException()
        {
            using var missing = new LoopbackHttpServer(Array.Empty<byte>(), statusCode: 404);
            using var source = new HttpDemTileSource(missing.Url, maxConcurrent: 1, noDataStatuses: Array.Empty<long>());

            var load = source.GetHeightsAsync(Coord, Allocator.Persistent, CancellationToken.None);
            yield return WaitFor(() => load.GetAwaiter().IsCompleted, "404 response");

            var ex = Assert.Catch<TileDownloadException>(() => load.GetAwaiter().GetResult());
            Assert.That(ex.HttpStatus, Is.EqualTo(404));
        }
    }
}
