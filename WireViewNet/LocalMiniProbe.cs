using System.Net.Http;
using System.Text.Json;
using WireView2.Device;
using WireView2.Services;

namespace WireView2.Net
{
    /// <summary>
    /// The GUI's probe for the WireView Mini (wireview-mini) on this machine:
    /// asks <c>http://127.0.0.1:&lt;PublishPort&gt;/sensors</c> and, when the answer
    /// comes from WireView Mini (its snapshot carries an <c>mini</c> field), yields
    /// each of its devices as a <see cref="NetworkDevice"/> with
    /// <see cref="NetworkDevice.IsLocalMini"/> set and source <c>mini:&lt;id&gt;</c>.
    /// Registered with <see cref="DeviceManager.SetLocalOwnerProbe"/>, so while the
    /// mini answers the GUI does not open the serial port WireView Mini owns.
    /// Commands are signed with WireView Mini's local key (<see cref="MiniKey"/>),
    /// falling back to the network secret.
    /// </summary>
    public sealed class LocalMiniProbe
    {
        /// <summary>Source prefix of WireView Mini's devices in <see cref="DeviceManager"/>.</summary>
        public const string SourcePrefix = "mini:";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };
        private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(5);

        private readonly Func<int> _port;
        private readonly Func<int>? _pollIntervalMs;
        private readonly object _gate = new();
        private DateTime _lastFetch = DateTime.MinValue;
        private WireViewHostSnapshot? _last;
        private volatile bool _present;

        /// <param name="port">The port to ask (AppSettings.PublishPort), read every probe.</param>
        /// <param name="pollIntervalMs">Poll rate for WireView Mini's devices (the GUI's local rate).</param>
        public LocalMiniProbe(Func<int> port, Func<int>? pollIntervalMs = null)
        {
            _port = port;
            _pollIntervalMs = pollIntervalMs;
        }

        /// <summary>True while WireView Mini answered the last probe.</summary>
        public bool MiniPresent => _present;

        /// <summary>The probe delegate for <see cref="DeviceManager.SetLocalOwnerProbe"/>.</summary>
        public IEnumerable<(IWireViewDevice device, string source)> Probe(ISet<string> held)
        {
            bool holding = held.Any(s => s.StartsWith(SourcePrefix, StringComparison.Ordinal));
            // While no mini device is held, ask every discovery tick (a refused
            // localhost connection costs nothing); while one is, every few seconds.
            bool stale;
            lock (_gate) stale = DateTime.UtcNow - _lastFetch >= Refresh;
            if (!holding || stale) RefreshNow();

            WireViewHostSnapshot? snap;
            lock (_gate) snap = _last;
            if (snap == null) yield break;

            string baseUrl = BaseUrl(_port());
            foreach (var d in snap.Devices)
            {
                if (string.IsNullOrEmpty(d.Id)) continue;
                string source = SourcePrefix + d.Id;
                if (held.Contains(source)) continue;
                yield return (new NetworkDevice(baseUrl, d.Id, d.Name, Secret)
                {
                    IsLocalMini = true,
                    PollIntervalProvider = _pollIntervalMs,
                }, source);
            }
        }

        private void RefreshNow()
        {
            var snap = TryFetch(_port(), TimeSpan.FromSeconds(1));
            bool present = snap?.Mini != null;
            bool was = _present;
            lock (_gate)
            {
                _lastFetch = DateTime.UtcNow;
                _last = present ? snap : null;
            }
            _present = present;
            if (present != was)
                FileLog.Info(present
                    ? $"wireview-mini {snap!.Mini} found on port {_port()}: using it instead of the serial port"
                    : "wireview-mini no longer answers: back to direct device access");
        }

        /// <summary>The port WireView Mini listens on: the one it recorded next to its
        /// token (the publish port, or its loopback fallback while that port is
        /// taken by another program), else the configured publish port.</summary>
        public static int MiniPort() => MiniPanelToken.ReadPort() ?? AppSettings.Current.PublishPort;

        /// <summary>Signs commands to WireView Mini: its local key, else the network secret.</summary>
        public static string? Secret() => MiniKey.Read() ?? AppSettings.Current.NetworkSecret;

        /// <summary>"http://127.0.0.1:&lt;port&gt;".</summary>
        public static string BaseUrl(int port) => $"http://127.0.0.1:{port}";

        /// <summary>The <c>/sensors</c> snapshot on localhost, or null when nothing
        /// answers in time or the answer is not a WireView snapshot. Any publisher
        /// may answer (wireviewd, the GUI); check <see cref="WireViewHostSnapshot.Mini"/>.</summary>
        public static WireViewHostSnapshot? TryFetch(int port, TimeSpan timeout) => TryFetch(port, timeout, "127.0.0.1");

        /// <summary><see cref="TryFetch(int, TimeSpan)"/> on another address of this
        /// machine (a publisher bound to one address, such as wireviewd with bind=).</summary>
        public static WireViewHostSnapshot? TryFetch(int port, TimeSpan timeout, string host)
        {
            try
            {
                string h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
                using var cts = new CancellationTokenSource(timeout);
                string json = Http.GetStringAsync($"http://{h}:{port}/sensors", cts.Token).GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<WireViewHostSnapshot>(json, WireViewJson.Options);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when WireView Mini answers on <paramref name="port"/>.</summary>
        public static bool IsMiniRunning(int port) => TryFetch(port, TimeSpan.FromSeconds(1))?.Mini != null;
    }
}
