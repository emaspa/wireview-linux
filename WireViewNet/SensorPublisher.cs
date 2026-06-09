using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WireView2.Net
{
    /// <summary>
    /// Read-only HTTP endpoint that serves the host's current WireView readings
    /// at <c>GET /sensors</c> as JSON. No command surface is exposed.
    ///
    /// Implemented over a raw <see cref="TcpListener"/> (not HttpListener) so it
    /// binds all interfaces without a Windows http.sys URL ACL / admin rights —
    /// the same approach as the wireviewd daemon. The app supplies a snapshot
    /// provider so this stays decoupled from the device layer.
    /// </summary>
    public sealed class SensorPublisher : IDisposable
    {
        private readonly int _port;
        private readonly Func<WireViewHostSnapshot> _snapshotProvider;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public SensorPublisher(int port, Func<WireViewHostSnapshot> snapshotProvider)
        {
            _port = port;
            _snapshotProvider = snapshotProvider;
        }

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            _listener = new TcpListener(IPAddress.Any, _port); // all interfaces; no ACL needed
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            IsRunning = true;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }

                _ = Task.Run(() => HandleAsync(client), ct);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    // HTTP clients send the request immediately; bound the read so a
                    // silent connection can't pin the handler.
                    using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var buf = new byte[1024];
                    int n = await stream.ReadAsync(buf, readCts.Token).ConfigureAwait(false);
                    if (n <= 0) return;

                    string req = Encoding.ASCII.GetString(buf, 0, n);
                    if (req.StartsWith("GET /sensors", StringComparison.Ordinal))
                    {
                        var json = JsonSerializer.Serialize(_snapshotProvider(), WireViewJson.Options);
                        var body = Encoding.UTF8.GetBytes(json);
                        var head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: application/json; charset=utf-8\r\n" +
                            "Access-Control-Allow-Origin: *\r\n" +
                            "Connection: close\r\n" +
                            $"Content-Length: {body.Length}\r\n\r\n");
                        await stream.WriteAsync(head).ConfigureAwait(false);
                        await stream.WriteAsync(body).ConfigureAwait(false);
                    }
                    else
                    {
                        var resp = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                        await stream.WriteAsync(resp).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // ignore per-connection errors (timeout, reset, etc.)
            }
        }

        public void Dispose()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts?.Dispose();
        }
    }
}
