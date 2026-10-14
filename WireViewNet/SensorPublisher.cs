using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WireView2.Device;

namespace WireView2.Net
{
    /// <summary>
    /// HTTP endpoint over a raw <see cref="TcpListener"/> (no http.sys URL ACL on
    /// Windows). Serves read-only <c>GET /sensors</c> (open) and, when a secret +
    /// command sink are supplied, authenticated <c>POST /command</c> (HMAC-signed,
    /// see <see cref="HmacAuth"/>). Flood-protected: a concurrency cap, a request
    /// size cap (413), and a per-IP rate limit (429) for hosts other than this PC.
    /// Binds all interfaces unless
    /// a bind address is given (the WireView Mini binds loopback only while LAN
    /// publishing is off). Commands from this machine may also be signed with a
    /// local-only secret (WireView Mini's key file), and <see cref="WireViewCommand.LocalOnly"/>
    /// ops are refused from any other address. An optional extra-routes hook
    /// (WireView Mini's control panel) answers any other method and path on the same
    /// port, under the same connection, size and rate limits.
    /// </summary>
    public sealed class SensorPublisher : IDisposable
    {
        private readonly int _port;
        private readonly IPAddress _bindAddress;
        private readonly Func<string?>? _localSecretProvider;
        private readonly Func<WireViewHostSnapshot> _snapshotProvider;
        private readonly Func<string?>? _secretProvider;
        private readonly Func<WireViewCommand, DaemonResult>? _commandSink;
        private readonly Func<string?, ConfigSnapshot?>? _configReader;
        private readonly Func<HttpRequestInfo, HttpResponseInfo?>? _extraRoutes;
        private readonly Func<HttpRequestInfo, int>? _extraRouteBodyLimit;
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
            Func<WireViewCommand, DaemonResult>? commandSink = null,
            Func<string?, ConfigSnapshot?>? configReader = null,
            int maxConnections = 8,
            int maxRequestBytes = 8192,
            int rateLimitPerMinute = 120,
            IPAddress? bindAddress = null,
            Func<string?>? localSecretProvider = null,
            Func<HttpRequestInfo, HttpResponseInfo?>? extraRoutes = null,
            Func<HttpRequestInfo, int>? extraRouteBodyLimit = null)
        {
            _extraRoutes = extraRoutes;
            _extraRouteBodyLimit = extraRouteBodyLimit;
            _port = port;
            _bindAddress = bindAddress ?? IPAddress.Any;
            _localSecretProvider = localSecretProvider;
            _snapshotProvider = snapshotProvider;
            _secretProvider = secretProvider;
            _commandSink = commandSink;
            _configReader = configReader;
            _maxRequestBytes = Math.Max(1024, maxRequestBytes);
            _rateLimitPerMinute = rateLimitPerMinute;
            _slots = new SemaphoreSlim(Math.Max(1, maxConnections));
        }

        public bool IsRunning { get; private set; }

        /// <summary>The port this publisher listens on: the one bound, once started
        /// (port 0 binds an ephemeral port), else the one asked for.</summary>
        public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port is int bound && bound != 0 ? bound : _port;

        /// <summary>The address it binds (<see cref="IPAddress.Any"/> unless set).</summary>
        public IPAddress BindAddress => _bindAddress;

        public void Start()
        {
            if (IsRunning) return;
            _listener = new TcpListener(_bindAddress, _port); // no ACL needed
            // Windows lets a wildcard bind share a port with a specific-address
            // bind (and vice versa) unless one side asks for exclusive use, which
            // would let two publishers answer on one port at the same time.
            if (OperatingSystem.IsWindows())
                _listener.ExclusiveAddressUse = true;
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

                    var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    string ip = peer?.ToString() ?? "?";
                    bool loopback = peer != null && IPAddress.IsLoopback(peer);
                    // The rate limit is for the LAN. This PC's own clients (the control
                    // panel page at one request a second, --status, the app's probe) all
                    // share 127.0.0.1 and would starve each other at the limit.
                    if (!loopback && RateLimited(ip)) { await WriteJson(stream, 429, "Too Many Requests", "").ConfigureAwait(false); return; }

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
                        string json;
                        try
                        {
                            json = JsonSerializer.Serialize(_snapshotProvider(), WireViewJsonContext.Default.WireViewHostSnapshot);
                        }
                        catch (Exception ex)
                        {
                            FileLog.Warn($"/sensors snapshot failed: {ex.Message}");
                            await WriteJson(stream, 500, "Internal Server Error", "{\"error\":\"snapshot\"}").ConfigureAwait(false);
                            return;
                        }
                        await WriteJson(stream, 200, "OK", json, cors: true).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && (path == "/config" || path.StartsWith("/config?", StringComparison.Ordinal)))
                    {
                        string? id = null;
                        int q = path.IndexOf("id=", StringComparison.Ordinal);
                        if (q >= 0) id = path[(q + 3)..];
                        var snap = _configReader?.Invoke(id);
                        if (snap == null)
                        {
                            await WriteJson(stream, 503, "Service Unavailable", "{\"error\":\"no config\"}").ConfigureAwait(false);
                            return;
                        }
                        string cfgJson = $"{{\"deviceId\":\"{snap.DeviceId}\",\"version\":{snap.Version}," +
                                         $"\"data\":\"{Convert.ToBase64String(snap.Data)}\"}}";
                        await WriteJson(stream, 200, "OK", cfgJson, cors: true).ConfigureAwait(false);
                        return;
                    }

                    if (method == "POST" && path == "/command")
                    {
                        string? body = await ReadBody(stream, buf, headers, headEnd, total, ct).ConfigureAwait(false);
                        if (body == null)
                        {
                            await WriteJson(stream, 413, "Payload Too Large", "{\"error\":\"too large\"}").ConfigureAwait(false);
                            return;
                        }
                        await HandleCommand(stream, headers, body, ip, loopback).ConfigureAwait(false);
                        return;
                    }

                    if (_extraRoutes != null)
                    {
                        // A route may take a body over the size cap (WireView Mini's
                        // theme images); it is asked with the request line and headers only.
                        int allowed = _extraRouteBodyLimit?.Invoke(new HttpRequestInfo(method, path, headers, "", loopback, ip)) ?? 0;
                        string? body = await ReadBody(stream, buf, headers, headEnd, total, ct, allowed).ConfigureAwait(false);
                        if (body == null)
                        {
                            await WriteJson(stream, 413, "Payload Too Large", "{\"error\":\"too large\"}").ConfigureAwait(false);
                            return;
                        }
                        HttpResponseInfo? answer;
                        try { answer = _extraRoutes(new HttpRequestInfo(method, path, headers, body, loopback, ip)); }
                        catch (Exception ex)
                        {
                            FileLog.Warn($"{method} {path} from {ip} failed: {ex.Message}");
                            answer = HttpResponseInfo.Json(500, "{\"error\":" + JsonSerializer.Serialize(ex.Message, WireViewJsonContext.Default.String) + "}");
                        }
                        if (answer != null)
                        {
                            await WriteResponse(stream, answer).ConfigureAwait(false);
                            return;
                        }
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
                // A rebind disposes the old publisher while a slow request (a
                // control-panel settings change that moves the port) still runs.
                if (gotSlot) try { _slots.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private async Task HandleCommand(NetworkStream stream, string headers, string body, string ip, bool loopback)
        {
            string? secret = _secretProvider?.Invoke();
            // The local secret is only honoured for connections from this machine.
            string? localSecret = loopback ? _localSecretProvider?.Invoke() : null;
            if ((string.IsNullOrEmpty(secret) && string.IsNullOrEmpty(localSecret)) || _commandSink == null)
            {
                FileLog.Warn($"command from {ip} rejected: writes disabled");
                await WriteJson(stream, 403, "Forbidden", "{\"error\":\"writes disabled\"}").ConfigureAwait(false);
                return;
            }

            string? ts = Header(headers, HmacAuth.TsHeader);
            string? nonce = Header(headers, HmacAuth.NonceHeader);
            string? sig = Header(headers, HmacAuth.SigHeader);
            // A failed check records nothing, so trying the second secret after
            // the first is safe for replay protection.
            bool authentic =
                (!string.IsNullOrEmpty(localSecret) && HmacAuth.Verify(localSecret, ts, nonce, sig, body)) ||
                (!string.IsNullOrEmpty(secret) && HmacAuth.Verify(secret, ts, nonce, sig, body));
            if (!authentic)
            {
                FileLog.Warn($"command from {ip} rejected: bad/expired signature");
                await WriteJson(stream, 401, "Unauthorized", "{\"error\":\"auth\"}").ConfigureAwait(false);
                return;
            }

            var cmd = WireViewCommand.Parse(body);
            if (cmd == null)
            {
                FileLog.Warn($"command from {ip} rejected: bad command body");
                await WriteJson(stream, 400, "Bad Request", "{\"error\":\"bad command\"}").ConfigureAwait(false);
                return;
            }

            if (cmd.LocalOnly && !loopback)
            {
                FileLog.Warn($"command from {ip} rejected: op={cmd.Op} is local only");
                await WriteJson(stream, 403, "Forbidden", "{\"error\":\"local only\"}").ConfigureAwait(false);
                return;
            }

            DaemonResult result;
            try { result = _commandSink(cmd); } catch { result = DaemonResult.Error; }
            var (status, text, json, outcome) = CommandResponse(result);
            FileLog.Write(result == DaemonResult.Ok ? "INFO" : "WARN", $"command from {ip}: op={cmd.Op} -> {outcome}");
            await WriteJson(stream, status, text, json).ConfigureAwait(false);
        }

        /// <summary>HTTP answer for a relayed command, in wireviewd's HTTP API shapes
        /// ({"ok":true} / {"error":"reason"}). A wireviewd permission denial on this
        /// host (socket status 3) is 403 with the machine-readable reason "denied" and
        /// this host's name, so the remote client can say which host needs its user in
        /// the 'wireview' group. Clients tell it apart from the other 403 ("writes
        /// disabled") by the reason.</summary>
        internal static (int status, string text, string json, string outcome) CommandResponse(DaemonResult result) => result switch
        {
            DaemonResult.Ok => (200, "OK", "{\"ok\":true}", "executed"),
            DaemonResult.Denied => (403, "Forbidden",
                "{\"error\":\"denied\",\"host\":" + JsonSerializer.Serialize(Environment.MachineName, WireViewJsonContext.Default.String) + "}", "denied by wireviewd"),
            DaemonResult.NotConnected => (503, "Service Unavailable", "{\"error\":\"no device\"}", "no device"),
            DaemonResult.Busy => (409, "Conflict", "{\"error\":\"busy\"}", "busy: firmware update running"),
            _ => (500, "Internal Server Error", "{\"error\":\"relay failed\"}", "relay failed"),
        };

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

        /// <summary>The request body (up to Content-Length, at most the size cap, or
        /// <paramref name="allowed"/> when that is larger), or null when the declared
        /// length is over both.</summary>
        private async Task<string?> ReadBody(NetworkStream stream, byte[] buf, string headers, int headEnd, int total,
            CancellationToken ct, int allowed = 0)
        {
            int want = ContentLength(headers);
            int bodyStart = headEnd + 4, have = total - bodyStart;
            if (want > _maxRequestBytes)
            {
                if (want > allowed) return null;
                // Its own buffer, sized to the declared length: the per-connection
                // one stays at the cap, so other requests cost what they did.
                var big = new byte[want];
                int got = Math.Clamp(have, 0, want);
                Buffer.BlockCopy(buf, bodyStart, big, 0, got);
                while (got < want)
                {
                    int n = await stream.ReadAsync(big.AsMemory(got, want - got), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    got += n;
                }
                return Encoding.UTF8.GetString(big, 0, got);
            }
            while (have < want && total < buf.Length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n; have += n;
            }
            int bodyLen = Math.Max(0, Math.Min(want, total - bodyStart));
            return bodyLen > 0 ? Encoding.UTF8.GetString(buf, bodyStart, bodyLen) : "";
        }

        private static async Task WriteResponse(NetworkStream s, HttpResponseInfo r)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(ReasonPhrase(r.Status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(r.ContentType).Append("\r\n");
            foreach (var h in r.Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            if (r.WriteBody is { } write)
            {
                // Streamed: the end of the body is the end of the connection.
                sb.Append("Connection: close\r\n\r\n");
                await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
                using var buffered = new BufferedStream(s, 64 * 1024);
                write(buffered);
                buffered.Flush();
                return;
            }
            sb.Append("Connection: close\r\nContent-Length: ").Append(r.Body.Length).Append("\r\n\r\n");
            await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
            if (r.Body.Length > 0) await s.WriteAsync(r.Body).ConfigureAwait(false);
        }

        private static string ReasonPhrase(int status) => status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            413 => "Payload Too Large",
            503 => "Service Unavailable",
            _ => status >= 500 ? "Internal Server Error" : "Error",
        };

        internal static string? Header(string headers, string name)
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
