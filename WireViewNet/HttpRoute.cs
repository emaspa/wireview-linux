using System.Text;

namespace WireView2.Net
{
    /// <summary>A request handed to <see cref="SensorPublisher"/>'s extra routes
    /// (WireView Mini's control panel). <see cref="Path"/> excludes the query string.</summary>
    public sealed class HttpRequestInfo
    {
        private readonly string _headers;

        public HttpRequestInfo(string method, string target, string headers, string body, bool isLoopback, string remoteIp)
        {
            Method = method;
            int q = target.IndexOf('?');
            Path = q >= 0 ? target[..q] : target;
            Query = q >= 0 ? target[(q + 1)..] : "";
            _headers = headers;
            Body = body;
            IsLoopback = isLoopback;
            RemoteIp = remoteIp;
        }

        public string Method { get; }
        public string Path { get; }
        public string Query { get; }
        public string Body { get; }
        /// <summary>The peer is this machine (127.0.0.0/8 or ::1).</summary>
        public bool IsLoopback { get; }
        public string RemoteIp { get; }

        /// <summary>A request header's value (case-insensitive name), or null.</summary>
        public string? Header(string name) => SensorPublisher.Header(_headers, name);
    }

    /// <summary>An extra route's answer.</summary>
    public sealed record HttpResponseInfo(int Status, string ContentType, byte[] Body)
    {
        /// <summary>Extra response headers, e.g. Cache-Control.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();

        /// <summary>Writes the body in place of <see cref="Body"/>, while it is sent
        /// (a large download, such as a CSV export, never held in memory whole). The
        /// answer then has no Content-Length and ends when the connection closes.</summary>
        public Action<Stream>? WriteBody { get; init; }

        public static HttpResponseInfo Json(int status, string json) =>
            new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json))
            {
                Headers = [new("Cache-Control", "no-store")],
            };
    }
}
