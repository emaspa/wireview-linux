using Makaretu.Dns;

namespace WireView2.Net
{
    /// <summary>
    /// Advertises this host's WireView publisher on the LAN via mDNS/DNS-SD as
    /// <c>_wireview._tcp</c>, so peer apps (and a future CLI) auto-discover it.
    /// Best-effort: failures (no multicast, restricted network) are swallowed so
    /// the HTTP publisher still works with manually-configured hosts.
    /// </summary>
    public sealed class MdnsAdvertiser : IDisposable
    {
        public const string ServiceName = "_wireview._tcp";

        private ServiceDiscovery? _sd;
        private ServiceProfile? _profile;

        public bool IsAdvertising { get; private set; }

        public void Start(string instanceName, int port, string hostName, int deviceCount, string appVersion)
        {
            if (IsAdvertising) return;
            try
            {
                _profile = new ServiceProfile(instanceName, ServiceName, (ushort)port);
                _profile.AddProperty("host", hostName);
                _profile.AddProperty("count", deviceCount.ToString());
                _profile.AddProperty("version", appVersion);

                _sd = new ServiceDiscovery();
                _sd.Advertise(_profile);
                _sd.Announce(_profile);
                IsAdvertising = true;
            }
            catch
            {
                // Multicast may be unavailable; publisher still serves over HTTP.
                Dispose();
            }
        }

        public void Dispose()
        {
            IsAdvertising = false;
            try { if (_profile != null) _sd?.Unadvertise(_profile); } catch { /* ignore */ }
            try { _sd?.Dispose(); } catch { /* ignore */ }
            _sd = null;
            _profile = null;
        }
    }
}
