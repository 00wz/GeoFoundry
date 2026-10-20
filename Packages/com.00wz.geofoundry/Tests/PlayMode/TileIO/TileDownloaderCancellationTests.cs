using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using GeoFoundry.TileIO.Net;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GeoFoundry.Tests.PlayMode.TileIO
{
    /// <summary>
    /// Cancellation of <see cref="TileDownloader"/> loads against a local server (<see cref="LoopbackHttpServer"/>),
    /// without external network access: the request wait wakes up inside <c>Cancel()</c>/<c>Dispose()</c>, and a
    /// cancelled request creates no texture even if the response does arrive. Otherwise, on exiting Play Mode, Unity
    /// would complete such a request in the middle of scene teardown, and accessing the texture would crash the Editor.
    /// </summary>
    public sealed class TileDownloaderCancellationTests
    {
        // Unusual size to tell the test textures apart from other Editor textures.
        private const int PngWidth = 7;
        private const int PngHeight = 3;

        private static byte[] MakePng()
        {
            var tex = new Texture2D(PngWidth, PngHeight, TextureFormat.RGBA32, false);
            try { return tex.EncodeToPNG(); }
            finally { Object.DestroyImmediate(tex); }
        }

        /// <summary>Returns the number of textures in memory with the size of the test PNG.</summary>
        private static int TestTextureCount()
        {
            int count = 0;
            foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (tex.width == PngWidth && tex.height == PngHeight) count++;
            return count;
        }

        private static IEnumerator WaitFor(Func<bool> condition, string what, int maxFrames = 300)
        {
            for (int i = 0; i < maxFrames && !condition(); i++) yield return null;
            Assert.That(condition(), Is.True, $"timed out waiting for: {what}");
        }

        [UnityTest]
        public IEnumerator GetTextureAsync_ServerResponds_ReturnsTexture()
        {
            using var server = new LoopbackHttpServer(MakePng());
            using var downloader = new TileDownloader(maxConcurrent: 1, maxRetries: 0);

            var download = downloader.GetTextureAsync(server.Url, nonReadable: false, CancellationToken.None);
            yield return WaitFor(() => download.GetAwaiter().IsCompleted, "texture download");

            var tex = download.GetAwaiter().GetResult();
            try
            {
                Assert.That(tex, Is.Not.Null);
                Assert.That(tex.width, Is.EqualTo(PngWidth));
                Assert.That(tex.height, Is.EqualTo(PngHeight));
            }
            finally
            {
                Object.Destroy(tex);
            }
        }

        [UnityTest]
        public IEnumerator GetTextureAsync_CancelledInFlight_CompletesInsideCancel_NeverCreatesTexture()
        {
            using var server = new LoopbackHttpServer();
            using var downloader = new TileDownloader(maxConcurrent: 1, maxRetries: 0);
            using var cts = new CancellationTokenSource();
            int texturesBefore = TestTextureCount();

            var download = downloader.GetTextureAsync(server.Url, nonReadable: false, cts.Token);
            yield return WaitFor(() => server.RequestCount == 1, "request reaching the server");
            Assert.That(download.GetAwaiter().IsCompleted, Is.False, "the server holds the request");

            cts.Cancel();
            Assert.That(download.GetAwaiter().IsCompleted, Is.True, "the request wait wakes up inside Cancel()");
            Assert.Catch<OperationCanceledException>(() => download.GetAwaiter().GetResult());

            // The server responds anyway; the cancelled request must not create a texture.
            server.Respond(MakePng());
            for (int i = 0; i < 10; i++) yield return null;
            Assert.That(TestTextureCount(), Is.EqualTo(texturesBefore), "no texture is created for the cancelled request");
        }

        [UnityTest]
        public IEnumerator Dispose_AbortsInFlightRequest_InsideDispose()
        {
            using var server = new LoopbackHttpServer();
            var downloader = new TileDownloader(maxConcurrent: 1, maxRetries: 0);

            var download = downloader.GetBytesAsync(server.Url, Allocator.Persistent, CancellationToken.None);
            yield return WaitFor(() => server.RequestCount == 1, "request reaching the server");

            downloader.Dispose();
            Assert.That(download.GetAwaiter().IsCompleted, Is.True, "Dispose aborts the downloader's requests synchronously");
            Assert.Catch<OperationCanceledException>(() => download.GetAwaiter().GetResult());
        }
    }
}
