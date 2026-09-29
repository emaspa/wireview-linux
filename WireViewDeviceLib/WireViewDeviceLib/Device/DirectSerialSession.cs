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
    /// daemon for at most <see cref="SuspendWindowSeconds"/>. If a re-arm fails
    /// (denied, daemon unreachable, daemon error), the daemon will resume polling
    /// when the current window runs out, so the session is aborted instead: the
    /// operation's token is cancelled, the temporary device is disconnected so no
    /// further transfer starts, and <see cref="SerialHandoverLostException"/> is
    /// thrown with the reason. The window still left at that point (at least
    /// SuspendWindowSeconds - HeartbeatIntervalMs) covers winding down.
    /// </summary>
    public static class DirectSerialSession
    {
        private const int SuspendWindowSeconds = 120;
        private const int HeartbeatIntervalMs = 60_000;

        // The heartbeat period actually used; HeartbeatIntervalMs unless a test
        // harness shortens it.
        private static int s_heartbeatIntervalMs = HeartbeatIntervalMs;

        public static Task<T> RunAsync<T>(HwmonDevice daemonDevice,
            Func<WireViewPro2Device, Task<T>> operation)
            => RunAsync(daemonDevice, (device, _) => operation(device), CancellationToken.None);

        /// <summary>Runs <paramref name="operation"/> with the port borrowed from
        /// wireviewd. Its token is cancelled when <paramref name="cancellationToken"/>
        /// is, or when the daemon's suspension cannot be re-armed; in the latter case
        /// the call throws <see cref="SerialHandoverLostException"/>.</summary>
        public static Task<T> RunAsync<T>(HwmonDevice daemonDevice,
            Func<WireViewPro2Device, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            return RunWithHandoverAsync(daemonDevice, async token =>
            {
                string? port = Stm32PortFinder.FindMatchingComPorts().FirstOrDefault();
                if (port == null)
                    throw new InvalidOperationException("No WireView serial port found.");

                var device = new WireViewPro2Device(port);
                // Stops an operation that does not observe the token at its next
                // transfer ("Device not connected"), so nothing new starts on the
                // port once the handover is lost.
                using var abort = token.Register(() => { try { device.Disconnect(); } catch { } });
                try
                {
                    device.Connect();
                    if (!device.Connected)
                        throw new InvalidOperationException($"Could not open {port}.");
                    return await operation(device, token).ConfigureAwait(false);
                }
                finally
                {
                    try { device.Disconnect(); } catch { }
                }
            }, cancellationToken);
        }

        /// <summary>The handover protocol around <paramref name="body"/>: suspend,
        /// heartbeat, abort on a failed re-arm, resume.</summary>
        internal static async Task<T> RunWithHandoverAsync<T>(HwmonDevice daemonDevice,
            Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        {
            var suspend = daemonDevice.RequestSuspendSerial(SuspendWindowSeconds);
            if (suspend == DaemonResult.Denied)
                throw new DaemonDeniedException();
            if (suspend != DaemonResult.Ok)
                throw new InvalidOperationException(
                    "The hwmon daemon did not hand over the serial port (wireviewd too old? " +
                    "Suspend needs wireviewd with WCMD_SUSPEND_SERIAL support).");

            daemonDevice.BeginSerialSession();
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var heartbeatCts = new CancellationTokenSource();
            int lost = -1; // DaemonResult of the failed re-arm, -1 while the handover holds
            int intervalMs = s_heartbeatIntervalMs;
            var heartbeat = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(intervalMs, heartbeatCts.Token).ConfigureAwait(false);
                    var rearm = daemonDevice.RequestSuspendSerial(SuspendWindowSeconds);
                    if (rearm != DaemonResult.Ok)
                    {
                        Volatile.Write(ref lost, (int)rearm);
                        session.Cancel();
                        return;
                    }
                }
            });

            try
            {
                T result;
                try
                {
                    result = await body(session.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (Volatile.Read(ref lost) >= 0)
                {
                    throw new SerialHandoverLostException((DaemonResult)Volatile.Read(ref lost), ex);
                }
                // The operation may have ignored the token and returned after the
                // device was disconnected under it; its result is not trustworthy.
                if (Volatile.Read(ref lost) >= 0)
                    throw new SerialHandoverLostException((DaemonResult)Volatile.Read(ref lost), null);
                return result;
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

    /// <summary>Thrown when a <see cref="DirectSerialSession"/> is aborted because
    /// wireviewd's serial suspension could not be re-armed mid-transfer.
    /// <see cref="Reason"/> tells a denial from an unreachable daemon or an error.</summary>
    public sealed class SerialHandoverLostException : InvalidOperationException
    {
        public DaemonResult Reason { get; }

        public SerialHandoverLostException(DaemonResult reason, Exception? inner)
            : base(MessageFor(reason), inner)
        {
            Reason = reason;
        }

        private static string MessageFor(DaemonResult reason) => reason switch
        {
            DaemonResult.Denied =>
                "The transfer was stopped because wireviewd refused to keep the serial port " +
                "handed over. " + DaemonResults.DeniedMessage,
            DaemonResult.Unavailable =>
                "The transfer was stopped because wireviewd could not be reached to keep the " +
                "serial port handed over. Check that wireviewd is running, then try again.",
            _ =>
                $"The transfer was stopped because wireviewd could not keep the serial port " +
                $"handed over ({reason.Describe()}). Try again.",
        };
    }
}
