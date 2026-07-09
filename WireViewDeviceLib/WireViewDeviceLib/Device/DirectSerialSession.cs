using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Device
{
    /// <summary>
    /// Borrows the serial port from wireviewd for one bulk operation. The daemon
    /// owns the port while it is attached (concurrent I/O corrupts both sides), but
    /// SPI-flash transfers — log readback, theme assets — only exist on the direct
    /// serial protocol. This asks the daemon to go quiet, runs the operation on a
    /// temporary <see cref="WireViewPro2Device"/>, and hands the port back.
    ///
    /// The suspension is re-armed every <see cref="HeartbeatIntervalMs"/> so
    /// arbitrarily long transfers stay clean, while a crashed caller stalls the
    /// daemon for at most <see cref="SuspendWindowSeconds"/>.
    /// </summary>
    public static class DirectSerialSession
    {
        private const int SuspendWindowSeconds = 120;
        private const int HeartbeatIntervalMs = 60_000;

        public static async Task<T> RunAsync<T>(HwmonDevice daemonDevice,
            Func<WireViewPro2Device, Task<T>> operation)
        {
            if (!daemonDevice.SuspendSerial(SuspendWindowSeconds))
                throw new InvalidOperationException(
                    "The hwmon daemon did not hand over the serial port (wireviewd too old? " +
                    "Suspend needs wireviewd with WCMD_SUSPEND_SERIAL support).");

            daemonDevice.BeginSerialSession();
            using var heartbeatCts = new CancellationTokenSource();
            var heartbeat = Task.Run(async () =>
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatIntervalMs, heartbeatCts.Token).ConfigureAwait(false);
                    daemonDevice.SuspendSerial(SuspendWindowSeconds);
                }
            }, heartbeatCts.Token);

            try
            {
                string? port = Stm32PortFinder.FindMatchingComPorts().FirstOrDefault();
                if (port == null)
                    throw new InvalidOperationException("No WireView serial port found.");

                var device = new WireViewPro2Device(port);
                try
                {
                    device.Connect();
                    if (!device.Connected)
                        throw new InvalidOperationException($"Could not open {port}.");
                    return await operation(device).ConfigureAwait(false);
                }
                finally
                {
                    try { device.Disconnect(); } catch { }
                }
            }
            finally
            {
                heartbeatCts.Cancel();
                try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
                daemonDevice.EndSerialSession();
                try { daemonDevice.ResumeSerial(); } catch { }
            }
        }
    }
}
