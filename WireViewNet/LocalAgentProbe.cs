using System.Net.Http;
using System.Text.Json;
using WireView2.Device;
using WireView2.Services;

namespace WireView2.Net
{
    /// <summary>
    /// The GUI's probe for the headless agent (wireview-agent) on this machine:
    /// asks <c>http://127.0.0.1:&lt;PublishPort&gt;/sensors</c> and, when the answer
    /// comes from the agent (its snapshot carries an <c>agent</c> field), yields
    /// each of its devices as a <see cref="NetworkDevice"/> with
    /// <see cref="NetworkDevice.IsLocalAgent"/> set and source <c>agent:&lt;id&gt;</c>.
    /// Registered with <see cref="DeviceManager.SetLocalOwnerProbe"/>, so while the
    /// agent answers the GUI does not open the serial port the agent owns.
    /// Commands are signed with the agent's local key (<see cref="AgentKey"/>),
    /// falling back to the network secret.
    /// </summary>
    public sealed class LocalAgentProbe
    {
        /// <summary>Source prefix of the agent's devices in <see cref="DeviceManager"/>.</summary>
        public const string SourcePrefix = "agent:";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };
        private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(5);

        private readonly Func<int> _port;
        private readonly Func<int>? _pollIntervalMs;
        private readonly object _gate = new();
        private DateTime _lastFetch = DateTime.MinValue;
        private WireViewHostSnapshot? _last;
        private volatile bool _present;

        /// <param name="port">The port to ask (AppSettings.PublishPort), read every probe.</param>
        /// <param name="pollIntervalMs">Poll rate for the agent's devices (the GUI's local rate).</param>
        public LocalAgentProbe(Func<int> port, Func<int>? pollIntervalMs = null)
        {
            _port = port;
            _pollIntervalMs = pollIntervalMs;
        }

        /// <summary>True while the agent answered the last probe.</summary>
        public bool AgentPresent => _present;

        /// <summary>The probe delegate for <see cref="DeviceManager.SetLocalOwnerProbe"/>.</summary>
        public IEnumerable<(IWireViewDevice device, string source)> Probe(ISet<string> held)
        {
            bool holding = held.Any(s => s.StartsWith(SourcePrefix, StringComparison.Ordinal));
            // While no agent device is held, ask every discovery tick (a refused
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
                    IsLocalAgent = true,
                    PollIntervalProvider = _pollIntervalMs,
                }, source);
            }
        }

        private void RefreshNow()
        {
            var snap = TryFetch(_port(), TimeSpan.FromSeconds(1));
            bool present = snap?.Agent != null;
            bool was = _present;
            lock (_gate)
            {
                _lastFetch = DateTime.UtcNow;
                _last = present ? snap : null;
            }
            _present = present;
            if (present != was)
                FileLog.Info(present
                    ? $"wireview-agent {snap!.Agent} found on port {_port()}: using it instead of the serial port"
                    : "wireview-agent no longer answers: back to direct device access");
        }

        /// <summary>Signs commands to the agent: its local key, else the network secret.</summary>
        public static string? Secret() => AgentKey.Read() ?? AppSettings.Current.NetworkSecret;

        /// <summary>"http://127.0.0.1:&lt;port&gt;".</summary>
        public static string BaseUrl(int port) => $"http://127.0.0.1:{port}";

        /// <summary>The <c>/sensors</c> snapshot on localhost, or null when nothing
        /// answers in time or the answer is not a WireView snapshot. Any publisher
        /// may answer (wireviewd, the GUI); check <see cref="WireViewHostSnapshot.Agent"/>.</summary>
        public static WireViewHostSnapshot? TryFetch(int port, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                string json = Http.GetStringAsync($"{BaseUrl(port)}/sensors", cts.Token).GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<WireViewHostSnapshot>(json, WireViewJson.Options);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when the agent answers on <paramref name="port"/>.</summary>
        public static bool IsAgentRunning(int port) => TryFetch(port, TimeSpan.FromSeconds(1))?.Agent != null;
    }
}
