using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Device
{
    public class HwmonDevice : IWireViewDevice, IDisposable
    {
        private const string DaemonSocketPath = "/run/wireviewd.sock";

        // Socket protocol command types (must match wireviewd)
        private const byte WCMD_GET_DEVICE_INFO  = 0x01;
        private const byte WCMD_CLEAR_FAULTS     = 0x02;
        private const byte WCMD_READ_CONFIG      = 0x03;
        private const byte WCMD_WRITE_CONFIG     = 0x04;
        private const byte WCMD_SCREEN_CMD       = 0x05;
        private const byte WCMD_NVM_CMD          = 0x06;
        private const byte WCMD_READ_BUILD       = 0x07;
        private const byte WCMD_ENTER_BOOTLOADER = 0x08;
        private const byte WCMD_SUSPEND_SERIAL   = 0x09;
        private const byte WCMD_RESUME_SERIAL    = 0x0A;

        // Socket protocol response status (must match wireviewd)
        private const byte RESP_OK            = 0;
        private const byte RESP_ERROR         = 1;
        private const byte RESP_NOT_CONNECTED = 2;
        private const byte RESP_DENIED        = 3;
        // Local marker: no socket, or the request failed in transport.
        private const byte RESP_TRANSPORT     = 0xFF;

        private readonly string _hwmonPath;
        // A field rather than the const so a test harness can point it at a fake daemon.
        private readonly string _socketPath = DaemonSocketPath;
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private Socket? _daemonSocket;
        private readonly object _socketLock = new();

        private string _firmwareVersion = string.Empty;
        private string _uniqueId = string.Empty;
        private string _buildString = string.Empty;
        private int _configVersion = -1;
        // wireviewd 1.6.0 and older only attach product 5 and do not report the ids;
        // until a newer daemon says otherwise the device is a Pro II (EF05).
        private byte _vendorId = WireViewEditions.ThermalGrizzlyVendorId;
        private byte _productId = WireViewEditions.Pro2ProductId;

        public event EventHandler<DeviceData>? DataUpdated;
        public event EventHandler<bool>? ConnectionChanged;

        public bool Connected { get; private set; }
        public string DeviceName => WireViewEditions.DisplayName(Edition)
            + (DaemonAvailable ? " (hwmon + daemon)" : " (hwmon)");
        public string HardwareRevision => WireViewEditions.FormatHardwareRevision(_vendorId, _productId);
        public byte VendorId => _vendorId;
        public byte ProductId => _productId;
        public WireViewEdition Edition => WireViewEditions.FromIds(_vendorId, _productId);
        public string FirmwareVersion => _firmwareVersion;
        public string UniqueId => _uniqueId;
        public string BuildString => _buildString;
        public bool DaemonAvailable { get; private set; }
        public int ConfigVersion => _configVersion;

        private int _pollIntervalMs = 1000;
        public int PollIntervalMs
        {
            get => _pollIntervalMs;
            set => _pollIntervalMs = Math.Clamp(value, 100, 5000);
        }

        public HwmonDevice(string hwmonPath)
        {
            _hwmonPath = hwmonPath;
        }

        public void Connect()
        {
            if (Connected) return;

            string namePath = Path.Combine(_hwmonPath, "name");
            if (!File.Exists(namePath)) return;
            string name = File.ReadAllText(namePath).Trim();
            if (!name.Equals("wireview", StringComparison.OrdinalIgnoreCase)) return;

            string testPath = Path.Combine(_hwmonPath, "in0_input");
            if (!File.Exists(testPath)) return;
            try { File.ReadAllText(testPath).Trim(); }
            catch { return; }

            Connected = true;
            ConnectionChanged?.Invoke(this, true);

            TryConnectDaemon();

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => PollLoop(_cts.Token));
        }

        public void Disconnect()
        {
            if (!Connected) return;
            _cts?.Cancel();
            try { _worker?.Wait(1000); } catch { }
            DisconnectDaemon();
            Connected = false;
            ConnectionChanged?.Invoke(this, false);
        }

        // ---- Daemon socket connection ----

        private void TryConnectDaemon()
        {
            try
            {
                if (!File.Exists(_socketPath)) return;

                var socket = OpenDaemonSocket();

                lock (_socketLock)
                    _daemonSocket = socket;

                var (status, data) = SendDaemonRequest(WCMD_GET_DEVICE_INFO, Array.Empty<byte>());
                if (status == RESP_OK && data != null && data.Length >= 14)
                {
                    byte fwVersion = data[0];
                    _configVersion = data[1];

                    var uid = new byte[12];
                    Buffer.BlockCopy(data, 2, uid, 0, 12);
                    _uniqueId = BitConverter.ToString(uid).Replace("-", "");

                    byte vendorId = WireViewEditions.ThermalGrizzlyVendorId;
                    byte productId = WireViewEditions.Pro2ProductId;
                    if (data.Length > 14)
                    {
                        int end = Array.IndexOf(data, (byte)0, 14);
                        if (end < 0) end = data.Length;
                        _buildString = System.Text.Encoding.ASCII.GetString(data, 14, end - 14).Trim();
                        // Newer daemons append vendor_id, product_id after the build
                        // string's NUL; older ones stop at the NUL (assume EF05).
                        if (end + 2 < data.Length)
                        {
                            vendorId = data[end + 1];
                            productId = data[end + 2];
                        }
                    }
                    _vendorId = vendorId;
                    _productId = productId;

                    _firmwareVersion = fwVersion.ToString();
                    DaemonAvailable = true;
                }
                else
                {
                    DisconnectDaemon();
                }
            }
            catch
            {
                DisconnectDaemon();
            }
        }

        private Socket OpenDaemonSocket()
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.ReceiveTimeout = 3000;
                socket.SendTimeout = 3000;
                socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>wireviewd decides a client's privilege once, when it accepts the
        /// connection. After a denial, swap in a fresh connection so the next
        /// attempt is judged against the user's current groups (e.g. after
        /// <c>usermod -aG wireview</c>). The denied request itself is not retried.
        /// Caller holds <see cref="_socketLock"/>.</summary>
        private void RenewDaemonSocketAfterDenial()
        {
            try
            {
                var fresh = OpenDaemonSocket();
                try { _daemonSocket?.Dispose(); } catch { }
                _daemonSocket = fresh;
            }
            catch
            {
                // Keep the old connection: it still serves unprivileged commands.
            }
        }

        private static DaemonResult ToResult(byte status) => status switch
        {
            RESP_OK            => DaemonResult.Ok,
            RESP_NOT_CONNECTED => DaemonResult.NotConnected,
            RESP_DENIED        => DaemonResult.Denied,
            RESP_TRANSPORT     => DaemonResult.Unavailable,
            _                  => DaemonResult.Error,
        };

        private DaemonResult SendDaemonCommand(byte cmdType, byte[] payload)
        {
            var (status, _) = SendDaemonRequest(cmdType, payload);
            return ToResult(status);
        }

        private void DisconnectDaemon()
        {
            lock (_socketLock)
            {
                try { _daemonSocket?.Shutdown(SocketShutdown.Both); } catch { }
                try { _daemonSocket?.Dispose(); } catch { }
                _daemonSocket = null;
                DaemonAvailable = false;
            }
        }

        /// <summary>Sends one request over the long-lived daemon socket. If the
        /// connection turns out to be dead before any reply byte arrives (the daemon
        /// closed an idle client, or restarted), reconnects and sends the request
        /// once more. Replies are never retried: a denial or error is final, and a
        /// timeout or a truncated reply may mean the daemon already acted.</summary>
        private (byte status, byte[]? data) SendDaemonRequest(byte cmdType, byte[] payload)
        {
            lock (_socketLock)
            {
                if (_daemonSocket == null)
                    return (RESP_TRANSPORT, null);

                try
                {
                    (byte status, byte[]? data) reply;
                    try
                    {
                        reply = ExchangeDaemonRequest(_daemonSocket, cmdType, payload);
                    }
                    catch (DaemonConnectionLostException)
                    {
                        var fresh = OpenDaemonSocket();
                        try { _daemonSocket.Dispose(); } catch { }
                        _daemonSocket = fresh;
                        reply = ExchangeDaemonRequest(fresh, cmdType, payload);
                    }

                    if (reply.status == RESP_DENIED)
                        RenewDaemonSocketAfterDenial();

                    return reply;
                }
                catch
                {
                    try { _daemonSocket?.Dispose(); } catch { }
                    _daemonSocket = null;
                    DaemonAvailable = false;
                    return (RESP_TRANSPORT, null);
                }
            }
        }

        /// <summary>One request/reply round trip. Throws
        /// <see cref="DaemonConnectionLostException"/> only when the peer is gone and
        /// no reply byte was read, i.e. when the daemon cannot have handled the
        /// request (it dispatches a request only once it is complete and answers
        /// before doing anything else with that client).</summary>
        private static (byte status, byte[]? data) ExchangeDaemonRequest(Socket socket, byte cmdType, byte[] payload)
        {
            // Send: [type:u8][len:u16 LE][payload]
            var request = new byte[3 + payload.Length];
            request[0] = cmdType;
            request[1] = (byte)(payload.Length & 0xFF);
            request[2] = (byte)((payload.Length >> 8) & 0xFF);
            Buffer.BlockCopy(payload, 0, request, 3, payload.Length);
            try
            {
                socket.Send(request);
            }
            catch (SocketException ex) when (IsConnectionLost(ex.SocketErrorCode))
            {
                throw new DaemonConnectionLostException(ex);
            }

            // Receive: [status:u8][len:u16 LE][payload]
            var respHdr = new byte[3];
            int got;
            try
            {
                got = socket.Receive(respHdr, 0, 3, SocketFlags.None);
            }
            catch (SocketException ex) when (IsConnectionLost(ex.SocketErrorCode))
            {
                throw new DaemonConnectionLostException(ex);
            }
            if (got == 0)
                throw new DaemonConnectionLostException(null);
            SocketReadExact(socket, respHdr, got, 3);

            byte status = respHdr[0];
            int respLen = respHdr[1] | (respHdr[2] << 8);
            if (respLen > 1024)
                throw new IOException($"Oversized daemon reply ({respLen} bytes)");

            byte[]? respData = null;
            if (respLen > 0)
            {
                respData = new byte[respLen];
                SocketReadExact(socket, respData, 0, respLen);
            }
            return (status, respData);
        }

        // EPIPE surfaces as Shutdown; ECONNRESET when the daemon closed the socket
        // with our request still unread.
        private static bool IsConnectionLost(SocketError error) =>
            error is SocketError.Shutdown or SocketError.ConnectionReset
                  or SocketError.ConnectionAborted or SocketError.NotConnected;

        private sealed class DaemonConnectionLostException : IOException
        {
            public DaemonConnectionLostException(Exception? inner)
                : base("wireviewd closed the connection", inner) { }
        }

        /// <summary>Fills buffer[offset..end).</summary>
        private static void SocketReadExact(Socket socket, byte[] buffer, int offset, int end)
        {
            while (offset < end)
            {
                int n = socket.Receive(buffer, offset, end - offset, SocketFlags.None);
                if (n == 0) throw new IOException("Socket closed");
                offset += n;
            }
        }

        // ---- Command methods ----

        /// <summary>
        /// Clears latched faults through wireviewd. Both masks are KEEP-masks,
        /// passed through to the firmware, which does <c>fault &amp;= mask</c>: a set
        /// bit keeps that fault, a clear bit clears it. <c>0</c> clears everything,
        /// <c>0xFFFF</c> clears nothing and <c>~(1 &lt;&lt; (int)fault)</c> clears one
        /// fault. A parameterless call clears both the active faults and the log.
        /// </summary>
        /// <param name="keepStatusMask">Active (status) faults to keep.</param>
        /// <param name="keepLogMask">Fault-log (history) bits to keep.</param>
        public DaemonResult ClearFaults(int keepStatusMask = 0, int keepLogMask = 0)
        {
            if (!DaemonAvailable) return DaemonResult.Unavailable;
            var payload = new byte[4];
            payload[0] = (byte)(keepStatusMask & 0xFF);
            payload[1] = (byte)((keepStatusMask >> 8) & 0xFF);
            payload[2] = (byte)(keepLogMask & 0xFF);
            payload[3] = (byte)((keepLogMask >> 8) & 0xFF);
            return SendDaemonCommand(WCMD_CLEAR_FAULTS, payload);
        }

        public WireViewPro2Device.DeviceConfigStructV3? ReadConfig()
        {
            if (!DaemonAvailable || _configVersion < 0) return null;

            int size;
            if (_configVersion == 0)
                size = Marshal.SizeOf<WireViewPro2Device.DeviceConfigStructV1>();
            else if (_configVersion == 1)
                size = Marshal.SizeOf<WireViewPro2Device.DeviceConfigStructV2>();
            else if (_configVersion == 2)
                size = Marshal.SizeOf<WireViewPro2Device.DeviceConfigStructV3>();
            else
                return null;

            var payload = new byte[2];
            payload[0] = (byte)(size & 0xFF);
            payload[1] = (byte)((size >> 8) & 0xFF);

            var (status, data) = SendDaemonRequest(WCMD_READ_CONFIG, payload);
            if (status != RESP_OK || data == null || data.Length < 2) return null;

            byte configVer = data[0];
            byte[] configBytes = new byte[data.Length - 1];
            Buffer.BlockCopy(data, 1, configBytes, 0, configBytes.Length);

            if (configVer == 0)
            {
                var v1 = BytesToStruct<WireViewPro2Device.DeviceConfigStructV1>(configBytes);
                return WireViewPro2Device.ConvertConfigV1ToV3(v1);
            }
            else if (configVer == 1)
            {
                var v2 = BytesToStruct<WireViewPro2Device.DeviceConfigStructV2>(configBytes);
                return WireViewPro2Device.ConvertConfigV2ToV3(v2);
            }
            else if (configVer == 2)
            {
                return BytesToStruct<WireViewPro2Device.DeviceConfigStructV3>(configBytes);
            }
            else
            {
                return null;
            }
        }

        public DaemonResult WriteConfig(WireViewPro2Device.DeviceConfigStructV3 config)
        {
            if (!DaemonAvailable || _configVersion < 0) return DaemonResult.Unavailable;

            byte[] configBytes;
            if (_configVersion == 0)
            {
                var v1 = WireViewPro2Device.ConvertConfigV3ToV1(config);
                configBytes = StructToBytes(v1);
            }
            else if (_configVersion == 1)
            {
                var v2 = WireViewPro2Device.ConvertConfigV3ToV2(config);
                configBytes = StructToBytes(v2);
            }
            else if (_configVersion == 2)
            {
                configBytes = StructToBytes(config);
            }
            else
            {
                return DaemonResult.Error;
            }

            var payload = new byte[1 + configBytes.Length];
            payload[0] = (byte)_configVersion;
            Buffer.BlockCopy(configBytes, 0, payload, 1, configBytes.Length);

            return SendDaemonCommand(WCMD_WRITE_CONFIG, payload);
        }

        /// <summary>Write already-serialized config bytes (relayed from the network).</summary>
        public DaemonResult WriteConfigRaw(int version, byte[] configBytes)
        {
            if (!DaemonAvailable) return DaemonResult.Unavailable;
            if (configBytes == null || configBytes.Length == 0) return DaemonResult.Error;
            var payload = new byte[1 + configBytes.Length];
            payload[0] = (byte)version;
            Buffer.BlockCopy(configBytes, 0, payload, 1, configBytes.Length);
            return SendDaemonCommand(WCMD_WRITE_CONFIG, payload);
        }

        public DaemonResult ScreenCmd(WireViewPro2Device.SCREEN_CMD cmd)
        {
            if (!DaemonAvailable) return DaemonResult.Unavailable;
            return SendDaemonCommand(WCMD_SCREEN_CMD, new[] { (byte)cmd });
        }

        public DaemonResult NvmCmd(WireViewPro2Device.NVM_CMD cmd)
        {
            if (!DaemonAvailable) return DaemonResult.Unavailable;
            return SendDaemonCommand(WCMD_NVM_CMD, new[] { (byte)cmd });
        }

        public string? ReadBuildString()
        {
            if (!DaemonAvailable) return null;
            var (status, data) = SendDaemonRequest(WCMD_READ_BUILD, Array.Empty<byte>());
            if (status != RESP_OK || data == null || data.Length == 0) return null;
            int end = Array.IndexOf(data, (byte)0);
            if (end < 0) end = data.Length;
            return Encoding.ASCII.GetString(data, 0, end);
        }

        /// <summary>Asks wireviewd to reboot the device into its DFU bootloader. Only
        /// disconnects when the daemon accepted the command; on a denial or error
        /// the device is still running normally and stays connected.</summary>
        public DaemonResult EnterBootloader()
        {
            if (!DaemonAvailable) return DaemonResult.Unavailable;
            var result = SendDaemonCommand(WCMD_ENTER_BOOTLOADER, Array.Empty<byte>());
            if (result == DaemonResult.Ok)
                try { Disconnect(); } catch { }
            return result;
        }

        /// <summary>Set while a <see cref="DirectSerialSession"/> owns the port. The
        /// daemon stops feeding the kernel module during the handover, so sysfs goes
        /// stale (ENODATA) — that must not be treated as a lost device.</summary>
        private volatile bool _serialSessionActive;

        public void BeginSerialSession() => _serialSessionActive = true;
        public void EndSerialSession() => _serialSessionActive = false;

        /// <summary>Asks wireviewd to stop all serial I/O for <paramref name="seconds"/>
        /// (clamped to 1..300 by the daemon) so the caller can drive the port directly
        /// — SPI-flash log reads and theme asset transfers need exclusive access.
        /// Re-arming before the deadline extends it (heartbeat pattern). The daemon
        /// resumes on its own at the deadline even if the caller dies; call
        /// <see cref="ResumeSerial"/> when done to hand the port back early.</summary>
        public bool SuspendSerial(int seconds) => RequestSuspendSerial(seconds) == DaemonResult.Ok;

        /// <summary><see cref="SuspendSerial"/> with the daemon's answer, so a caller
        /// can tell a permission denial from an old daemon or a dead socket.</summary>
        public DaemonResult RequestSuspendSerial(int seconds)
        {
            if (!DaemonAvailable) return DaemonResult.Unavailable;
            var payload = new byte[] { (byte)(seconds & 0xFF), (byte)((seconds >> 8) & 0xFF) };
            return SendDaemonCommand(WCMD_SUSPEND_SERIAL, payload);
        }

        public DaemonResult ResumeSerial()
        {
            // The socket may have died during the handover (daemon restart, entry
            // churn) — reconnect rather than leave the daemon waiting out its
            // suspension deadline.
            if (!DaemonAvailable) TryConnectDaemon();
            if (!DaemonAvailable) return DaemonResult.Unavailable;
            return SendDaemonCommand(WCMD_RESUME_SERIAL, Array.Empty<byte>());
        }

        // ---- Sensor reading ----

        private void PollLoop(CancellationToken ct)
        {
            int consecutiveFailures = 0;
            var lastDaemonRetry = Environment.TickCount64;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!Directory.Exists(_hwmonPath))
                    {
                        Disconnect();
                        return;
                    }

                    // The daemon may come up (or come back) after we connected — e.g.
                    // wireviewd is still in its device-lost retry loop right after a
                    // firmware flash. Keep retrying so the entry regains commands,
                    // config, and the firmware version without an app restart.
                    if (!DaemonAvailable && Environment.TickCount64 - lastDaemonRetry >= 5000)
                    {
                        lastDaemonRetry = Environment.TickCount64;
                        TryConnectDaemon();
                        if (DaemonAvailable)
                            // Re-announce so listeners refresh name/firmware/config.
                            ConnectionChanged?.Invoke(this, true);
                    }

                    var data = ReadSensors();
                    if (data != null)
                    {
                        consecutiveFailures = 0;
                        DataUpdated?.Invoke(this, data);
                    }
                    else if (_serialSessionActive)
                    {
                        // A direct-serial session owns the port; the daemon isn't
                        // feeding the module, so stale/ENODATA reads are expected.
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures > 5)
                        {
                            Disconnect();
                            return;
                        }
                    }

                    Thread.Sleep(_pollIntervalMs);
                }
            }
            catch (Exception)
            {
                Disconnect();
            }
        }

        private DeviceData? ReadSensors()
        {
            try
            {
                var dd = new DeviceData
                {
                    Connected = true,
                    HardwareRevision = HardwareRevision,
                    FirmwareVersion = _firmwareVersion,
                };

                for (int i = 0; i < 6; i++)
                    dd.PinVoltage[i] = ReadIntFile($"in{i}_input") / 1000.0;

                for (int i = 0; i < 6; i++)
                    dd.PinCurrent[i] = ReadIntFile($"curr{i + 1}_input") / 1000.0;

                dd.OnboardTempInC  = ReadTempFile("temp1_input");
                dd.OnboardTempOutC = ReadTempFile("temp2_input");
                dd.ExternalTemp1C  = ReadTempFile("temp3_input");
                dd.ExternalTemp2C  = ReadTempFile("temp4_input");

                // Raw fault bitmasks from extended sysfs attrs, fallback to boolean intrusion alarms
                if (TryReadIntFile("fault_status_raw", out int rawFaultStatus))
                    dd.FaultStatus = (ushort)rawFaultStatus;
                else
                    dd.FaultStatus = ReadIntFile("intrusion0_alarm") != 0 ? (ushort)0xFFFF : (ushort)0;

                if (TryReadIntFile("fault_log_raw", out int rawFaultLog))
                    dd.FaultLog = (ushort)rawFaultLog;
                else
                    dd.FaultLog = ReadIntFile("intrusion1_alarm") != 0 ? (ushort)0xFFFF : (ushort)0;

                // PSU capability: power1_cap (microwatts) on current modules, else
                // the deprecated psu_cap enum.
                if (TryReadLongFile("power1_cap", out long psuCapUw))
                {
                    dd.PsuCapabilityW = (int)Math.Round(psuCapUw / 1_000_000.0);
                }
                else if (TryReadIntFile("psu_cap", out int psuCap))
                {
                    dd.PsuCapabilityW = psuCap switch
                    {
                        0 => 600,
                        1 => 450,
                        2 => 300,
                        3 => 150,
                        _ => 0
                    };
                }

                // Fan duty: pwm1 (0-255) on current modules, else the deprecated
                // fan1_input (percent).
                if (TryReadIntFile("pwm1", out int pwm))
                    dd.FanDuty = (int)Math.Round(Math.Clamp(pwm, 0, 255) * 100 / 255.0, MidpointRounding.AwayFromZero);
                else if (TryReadIntFile("fan1_input", out int fanPercent))
                    dd.FanDuty = fanPercent;

                // Energy in microjoules since the daemon started; absent on older
                // modules and ENODATA until the device sends v3 frames.
                if (TryReadLongFile("energy1_input", out long energyUj))
                    dd.EnergyJ = energyUj / 1_000_000.0;

                return dd;
            }
            catch
            {
                return null;
            }
        }

        // ---- Sysfs file helpers ----

        private double ReadTempFile(string fileName)
        {
            string path = Path.Combine(_hwmonPath, fileName);
            if (!File.Exists(path)) return double.NaN;
            try
            {
                string text = File.ReadAllText(path).Trim();
                if (int.TryParse(text, out int val))
                    return val / 1000.0;
            }
            catch { }
            return double.NaN;
        }

        private int ReadIntFile(string fileName)
        {
            string path = Path.Combine(_hwmonPath, fileName);
            if (!File.Exists(path)) return 0;
            string text = File.ReadAllText(path).Trim();
            return int.TryParse(text, out int val) ? val : 0;
        }

        private bool TryReadIntFile(string fileName, out int value)
        {
            value = 0;
            string path = Path.Combine(_hwmonPath, fileName);
            if (!File.Exists(path)) return false;
            try
            {
                string text = File.ReadAllText(path).Trim();
                return int.TryParse(text, out value);
            }
            catch { return false; }
        }

        private bool TryReadLongFile(string fileName, out long value)
        {
            value = 0;
            string path = Path.Combine(_hwmonPath, fileName);
            if (!File.Exists(path)) return false;
            try
            {
                string text = File.ReadAllText(path).Trim();
                return long.TryParse(text, out value);
            }
            catch { return false; }
        }

        // ---- Marshal helpers ----

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, 0, ptr, Math.Min(bytes.Length, size));
                return Marshal.PtrToStructure<T>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static byte[] StructToBytes<T>(T s) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(s, ptr, false);
                Marshal.Copy(ptr, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return bytes;
        }

        // ---- Static discovery ----

        public static string? FindHwmonPath()
        {
            const string hwmonBase = "/sys/class/hwmon";
            if (!Directory.Exists(hwmonBase)) return null;

            foreach (var dir in Directory.GetDirectories(hwmonBase))
            {
                string namePath = Path.Combine(dir, "name");
                if (!File.Exists(namePath)) continue;

                try
                {
                    string name = File.ReadAllText(namePath).Trim();
                    if (name.Equals("wireview", StringComparison.OrdinalIgnoreCase))
                        return dir;
                }
                catch { }
            }
            return null;
        }

        public void Dispose() => Disconnect();
    }
}
