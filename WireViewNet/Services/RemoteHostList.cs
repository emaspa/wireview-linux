using System.Net;
using System.Net.NetworkInformation;
using WireView2.Net;

namespace WireView2.Services;

/// <summary>
/// The remote hosts to read over the LAN, from <see cref="AppSettings.RemoteHosts"/>,
/// minus blanks and any endpoint that points back at this machine (its devices
/// are already local). Shared by the GUI's discovery and the headless agent.
/// </summary>
public static class RemoteHostList
{
    /// <summary>The configured endpoints, re-read from the current settings on
    /// every call so edits take effect without a restart.</summary>
    public static IEnumerable<string> Endpoints()
    {
        var manual = AppSettings.Current.RemoteHosts;
        if (manual == null) return Array.Empty<string>();
        return manual.Where(e => !string.IsNullOrWhiteSpace(e) && !IsLocalEndpoint(e));
    }

    private static volatile HashSet<string>? _localHosts;
    private static DateTime _localHostsAt;

    /// <summary>True if the endpoint points back at this machine (loopback,
    /// one of its NIC addresses, or its own hostname). Best-effort.</summary>
    public static bool IsLocalEndpoint(string endpoint)
    {
        try
        {
            var host = new Uri(RemoteDeviceProbe.Normalize(endpoint)).Host;
            if (IPAddress.TryParse(host, out var ip))
                return IPAddress.IsLoopback(ip) || LocalHosts().Contains(ip.ToString());

            return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static HashSet<string> LocalHosts()
    {
        var cached = _localHosts;
        if (cached != null && DateTime.UtcNow - _localHostsAt < TimeSpan.FromSeconds(60))
            return cached;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    set.Add(ua.Address.ToString());
            }
        }
        catch { /* ignore: best-effort self detection */ }
        set.Add(IPAddress.Loopback.ToString());
        set.Add(IPAddress.IPv6Loopback.ToString());

        _localHosts = set;
        _localHostsAt = DateTime.UtcNow;
        return set;
    }
}
