using System.Net;
using System.Text;
using System.Text.Json;

namespace WireView2.Net
{
    /// <summary>
    /// Read-only HTTP endpoint that serves the host's current WireView readings
    /// at <c>GET /sensors</c> as JSON. No command surface is exposed.
    /// The app supplies a snapshot provider so this stays decoupled from the
    /// device layer.
    /// </summary>
    public sealed class SensorPublisher : IDisposable
    {
        private readonly int _port;
        private readonly Func<WireViewHostSnapshot> _snapshotProvider;
        private readonly HttpListener _listener = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public SensorPublisher(int port, Func<WireViewHostSnapshot> snapshotProvider)
        {
            _port = port;
            _snapshotProvider = snapshotProvider;
            // "+" binds all interfaces so the LAN can reach it (Linux: no extra ACL).
            _listener.Prefixes.Add($"http://+:{_port}/");
        }

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            IsRunning = true;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch when (ct.IsCancellationRequested) { break; }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = Task.Run(() => HandleAsync(ctx), ct);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                res.Headers["Access-Control-Allow-Origin"] = "*"; // allow a future web UI

                if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    res.StatusCode = 405;
                    res.Close();
                    return;
                }

                var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
                if (path != "/sensors")
                {
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                var snapshot = _snapshotProvider();
                var json = JsonSerializer.Serialize(snapshot, WireViewJson.Options);
                var bytes = Encoding.UTF8.GetBytes(json);

                res.StatusCode = 200;
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                res.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts?.Dispose();
        }
    }
}
