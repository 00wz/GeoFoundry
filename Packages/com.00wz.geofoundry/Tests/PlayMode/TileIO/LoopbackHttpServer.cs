using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GeoFoundry.Tests.PlayMode.TileIO
{
    /// <summary>
    /// Minimal loopback HTTP server for downloader tests without external network access. It answers any request with
    /// <c>statusCode</c> (<c>200</c> by default) and the given body, but only after <see cref="Respond"/>; until then the
    /// request stays in flight for as long as the test needs. A server created with a body responds immediately.
    /// </summary>
    internal sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly ManualResetEventSlim _respond = new(false);
        private readonly List<TcpClient> _clients = new();
        private readonly int _statusCode;
        private volatile byte[] _body;
        private volatile bool _disposed;
        private int _requestCount;

        public LoopbackHttpServer(byte[] body = null, int statusCode = 200)
        {
            _statusCode = statusCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            if (body != null) Respond(body);
            new Thread(AcceptLoop) { IsBackground = true, Name = "LoopbackHttpServer" }.Start();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/tile.png";

        /// <summary>Gets the number of requests the server has accepted and read.</summary>
        public int RequestCount => Volatile.Read(ref _requestCount);

        /// <summary>Answers all pending and future requests with <paramref name="body"/>.</summary>
        public void Respond(byte[] body)
        {
            _body = body;
            _respond.Set();
        }

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { return; } // listener stopped

                lock (_clients) _clients.Add(client);
                new Thread(() => Serve(client)) { IsBackground = true, Name = "LoopbackHttpServer.Client" }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    ReadRequestHead(stream);
                    Interlocked.Increment(ref _requestCount);

                    _respond.Wait();
                    var body = _body;
                    if (_disposed || body == null) return;

                    var head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {_statusCode} {(_statusCode == 200 ? "OK" : "Error")}\r\nContent-Type: image/png\r\n" +
                        $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Write(body, 0, body.Length);
                }
            }
            catch (Exception)
            {
                // The client dropped the connection (aborted request) or the server stopped; both are expected.
            }
        }

        /// <summary>Reads the request headers up to the empty line (a GET has no body).</summary>
        private static void ReadRequestHead(NetworkStream stream)
        {
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0) return;
                matched = b == "\r\n\r\n"[matched] ? matched + 1 : (b == '\r' ? 1 : 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _respond.Set();
            _listener.Stop();
            lock (_clients)
            {
                foreach (var client in _clients) client.Close();
                _clients.Clear();
            }
        }
    }
}
