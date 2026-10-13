using System.Globalization;
using WireView2.Device;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Agent;

/// <summary><c>--status</c>: asks the running agent on localhost and prints its
/// version, each device and its fault state. The exit code sums it up.</summary>
internal static class StatusCommand
{
    public static int Run()
    {
        AppSettings.Reload();
        int port = AppSettings.Current.PublishPort;
        var snap = LocalAgentProbe.TryFetch(port, TimeSpan.FromSeconds(3));

        if (snap?.Agent == null)
        {
            bool running = AgentInstance.IsRunning();
            string who = snap == null
                ? "nothing answers"
                : $"it is served by {(string.IsNullOrEmpty(snap.AppVersion) ? "another publisher" : snap.AppVersion)}";
            if (running)
            {
                Console.WriteLine($"wireview-agent is running but not publishing on 127.0.0.1:{port} ({who}).");
                Console.WriteLine("Alerts and shutdown on fault work; see its log for details.");
                return Program.ExitNoDevice;
            }
            Console.WriteLine($"wireview-agent is not running (127.0.0.1:{port}: {who}).");
            return Program.ExitNotRunning;
        }

        Console.WriteLine($"wireview-agent {snap.Agent} on {snap.Host}, port {port}");
        if (snap.SerialSuspended)
            Console.WriteLine("serial port: handed over to a local client (last readings shown)");

        if (snap.Devices.Count == 0)
        {
            Console.WriteLine("device: none connected");
            return Program.ExitNoDevice;
        }

        bool anyConnected = false, anyFault = false;
        foreach (var d in snap.Devices)
        {
            anyConnected |= d.Connected;
            anyFault |= d.FaultStatus != 0;
            string fw = string.IsNullOrEmpty(d.FwVer) ? "?" : d.FwVer;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"device: {d.Name} ({d.Id}), firmware {fw}, {(d.Connected ? "connected" : "not connected")}, {d.SumPowerW:0.0} W"));
            Console.WriteLine(d.FaultStatus != 0
                ? $"  fault: {FaultMonitor.FormatFaults((ushort)d.FaultStatus)}"
                : "  fault: none active");
            if (d.FaultLog != 0)
                Console.WriteLine($"  fault log: {LogText((ushort)d.FaultLog)}");
        }

        if (anyFault) return Program.ExitFault;
        return anyConnected ? Program.ExitOk : Program.ExitNoDevice;
    }

    /// <summary>Latched fault-log bits as names, e.g. "Over-Current, Over-Power".</summary>
    private static string LogText(ushort log)
    {
        var names = Enum.GetValues<WireViewPro2Device.FAULT>()
            .Where(f => (log & (1 << (int)f)) != 0)
            .Select(FaultMonitor.FriendlyFaultName)
            .ToList();
        return names.Count > 0 ? string.Join(", ", names) : $"0x{log:X4}";
    }
}
