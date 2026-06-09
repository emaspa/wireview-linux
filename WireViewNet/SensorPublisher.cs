using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WireView2.Net
{
    /// <summary>
    /// HTTP endpoint over a raw <see cref="TcpListener"/> (no http.sys URL ACL on
    /// Windows). Serves read-only <c>GET /sensors</c> (open) and, when a secret +
    /// command sink are supplied, authenticated <c>POST /command</c> (HMAC-signed,
    /// see <see cref="HmacAuth"/>). Flood-protected: a concurrency cap, a request
    /// size cap (413), and a per-IP rate limit (429).
    /// </summary>
    public sealed class SensorPublisher : IDisposable
    {
        private readonly int _port;
        private readonly Func<WireViewHostSnapshot> _snapshotProvider;
        private readonly Func<string?>? _secretProvider;
        private readonly Func<WireViewCommand, bool>? _commandSink;
        private readonly int _maxRequestBytes;
        private readonly int _rateLimitPerMinute;
        private readonly SemaphoreSlim _slots;
        private readonly ConcurrentDictionary<string, (long windowStart, int count)> _buckets = new();

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public SensorPublisher(
            int port,
            Func<WireViewHostSnapshot> snapshotProvider,
            Func<string?>? secretProvider = null,
            Func<WireViewCommand, bool>? commandSink = null,
            int maxConnections = 8,
            int maxRequestBytes = 8192,
            int rateLimitPerMinute = 120)
        {
            _port = port;
            _snapshotProvider = snapshotProvider;
            _secretProvider = secretProvider;
            _commandSink = commandSink;
            _maxRequestBytes = Math.Max(1024, maxRequestBytes);
            _rateLimitPerMinute = rateLimitPerMinute;
            _slots = new SemaphoreSlim(Math.Max(1, maxConnections));
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
            bool gotSlot = false;
            try
            {
                using (client)
                {
                    using var stream = client.GetStream();
                    gotSlot = _slots.Wait(0);
                    if (!gotSlot) { await WriteJson(stream, 503, "Service Unavailable", "").ConfigureAwait(false); return; }

                    string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                    if (RateLimited(ip)) { await WriteJson(stream, 429, "Too Many Requests", "").ConfigureAwait(false); return; }

                    using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var ct = readCts.Token;
                    var buf = new byte[_maxRequestBytes + 2048];
                    int total = 0, headEnd = -1;
                    while (total < buf.Length)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        total += n;
                        headEnd = FindHeaderEnd(buf, total);
                        if (headEnd >= 0) break;
                    }
                    if (headEnd < 0) return;

                    string headers = Encoding.ASCII.GetString(buf, 0, headEnd);
                    var (method, path) = RequestLine(headers);

                    if (method == "GET" && path == "/sensors")
                    {
                        var json = JsonSerializer.Serialize(_snapshotProvider(), WireViewJson.Options);
                        await WriteJson(stream, 200, "OK", json, cors: true).ConfigureAwait(false);
                        return;
                    }

                    if (method == "POST" && path == "/command")
                    {
                        int want = ContentLength(headers);
                        if (want > _maxRequestBytes)
                        {
                            await WriteJson(stream, 413, "Payload Too Large", "{\"error\":\"too large\"}").ConfigureAwait(false);
                            return;
                        }
                        int bodyStart = headEnd + 4, have = total - bodyStart;
                        while (have < want && total < buf.Length)
                        {
                            int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
                            if (n <= 0) break;
                            total += n; have += n;
                        }
                        int bodyLen = Math.Min(want, total - bodyStart);
                        string body = bodyLen > 0 ? Encoding.UTF8.GetString(buf, bodyStart, bodyLen) : "";
                        await HandleCommand(stream, headers, body).ConfigureAwait(false);
                        return;
                    }

                    await WriteJson(stream, 404, "Not Found", "").ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore per-connection errors (timeout, reset, etc.)
            }
            finally
            {
                if (gotSlot) _slots.Release();
            }
        }

        private async Task HandleCommand(NetworkStream stream, string headers, string body)
        {
            string? secret = _secretProvider?.Invoke();
            if (string.IsNullOrEmpty(secret) || _commandSink == null)
            {
                await WriteJson(stream, 403, "Forbidden", "{\"error\":\"writes disabled\"}").ConfigureAwait(false);
                return;
            }

            if (!HmacAuth.Verify(secret, Header(headers, HmacAuth.TsHeader),
                                 Header(headers, HmacAuth.NonceHeader),
                                 Header(headers, HmacAuth.SigHeader), body))
            {
                await WriteJson(stream, 401, "Unauthorized", "{\"error\":\"auth\"}").ConfigureAwait(false);
                return;
            }

            var cmd = WireViewCommand.Parse(body);
            if (cmd == null)
            {
                await WriteJson(stream, 400, "Bad Request", "{\"error\":\"bad command\"}").ConfigureAwait(false);
                return;
            }

            bool ok;
            try { ok = _commandSink(cmd); } catch { ok = false; }
            await WriteJson(stream, ok ? 200 : 500, ok ? "OK" : "Internal Server Error",
                            ok ? "{\"ok\":true}" : "{\"error\":\"relay failed\"}").ConfigureAwait(false);
        }

        private bool RateLimited(string ip)
        {
            if (_rateLimitPerMinute <= 0) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var e = _buckets.AddOrUpdate(ip,
                _ => (now, 1),
                (_, cur) => now - cur.windowStart >= 60 ? (now, 1) : (cur.windowStart, cur.count + 1));
            return e.count > _rateLimitPerMinute;
        }

        private static int FindHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n')
                    return i;
            return -1;
        }

        private static (string method, string path) RequestLine(string headers)
        {
            int eol = headers.IndexOf("\r\n", StringComparison.Ordinal);
            string line = eol >= 0 ? headers[..eol] : headers;
            var p = line.Split(' ');
            return (p.Length > 0 ? p[0] : "", p.Length > 1 ? p[1] : "");
        }

        private static string? Header(string headers, string name)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line.AsSpan(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line[(c + 1)..].Trim();
            }
            return null;
        }

        private static int ContentLength(string headers)
        {
            var v = Header(headers, "Content-Length");
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        }

        private static async Task WriteJson(NetworkStream s, int status, string text, string json, bool cors = false)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(text).Append("\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            if (cors) sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\nContent-Length: ").Append(body.Length).Append("\r\n\r\n");
            await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
            if (body.Length > 0) await s.WriteAsync(body).ConfigureAwait(false);
        }

        public void Dispose()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts?.Dispose();
            _slots.Dispose();
        }
    }
}
