using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WireView2.Net
{
    /// <summary>
    /// Shared-secret HMAC authentication for the write endpoint (POST /command).
    /// The secret is never sent on the wire: each request carries a timestamp, a
    /// random nonce, and a signature = HMAC-SHA256(secret, "&lt;ts&gt;\n&lt;nonce&gt;\n&lt;body&gt;").
    /// The server recomputes it, checks the timestamp is fresh (±<see cref="WindowSeconds"/> s),
    /// compares in constant time, and rejects replays (by nonce). The nonce makes
    /// even identical commands in the same second unique. Reads (GET /sensors) are
    /// not signed. An empty secret means writes are disabled.
    /// </summary>
    public static class HmacAuth
    {
        public const string TsHeader = "X-Auth-Ts";
        public const string NonceHeader = "X-Auth-Nonce";
        public const string SigHeader = "X-Auth-Sig";
        public const long WindowSeconds = 30;

        /// <summary>Lowercase-hex HMAC-SHA256(secret, ts + "\n" + nonce + "\n" + body).</summary>
        public static string Sign(string secret, long unixSeconds, string nonce, string body)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var data = Encoding.UTF8.GetBytes(
                unixSeconds.ToString(CultureInfo.InvariantCulture) + "\n" + nonce + "\n" + body);
            return Convert.ToHexString(h.ComputeHash(data)).ToLowerInvariant();
        }

        /// <summary>Sign with the current time and a fresh nonce; returns the headers to attach.</summary>
        public static (long ts, string nonce, string sig) Sign(string secret, string body)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            return (ts, nonce, Sign(secret, ts, nonce, body));
        }

        private static readonly object _gate = new();
        private static readonly Dictionary<string, long> _seen = new(); // nonce -> ts

        /// <summary>True only for a fresh, correctly-signed, non-replayed request.</summary>
        public static bool Verify(string? secret, string? tsHeader, string? nonce, string? sigHeader, string body)
        {
            if (string.IsNullOrEmpty(secret)) return false;                 // writes disabled
            if (string.IsNullOrEmpty(tsHeader) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(sigHeader))
                return false;
            if (!long.TryParse(tsHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts))
                return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - ts) > WindowSeconds) return false;           // stale / clock-skewed

            string expected = Sign(secret, ts, nonce, body);
            var a = Encoding.ASCII.GetBytes(expected);
            var b = Encoding.ASCII.GetBytes(sigHeader);
            if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
                return false;

            lock (_gate)
            {
                long cutoff = now - 2 * WindowSeconds;
                foreach (var dead in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _seen.Remove(dead);
                if (_seen.ContainsKey(nonce)) return false;                 // replay
                _seen[nonce] = ts;
            }
            return true;
        }
    }
}
