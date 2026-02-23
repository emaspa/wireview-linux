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

        private const byte RESP_OK = 0;

        private readonly string _hwmonPath;
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private Socket? _daemonSocket;
        private readonly object _socketLock = new();

        private string _firmwareVersion = string.Empty;
        private string _uniqueId = string.Empty;
        private int _configVersion = -1;

        public event EventHandler<DeviceData>? DataUpdated;
        public event EventHandler<bool>? ConnectionChanged;

        public bool Connected { get; private set; }
        public string DeviceName => DaemonAvailable
            ? "WireView Pro II (hwmon + daemon)"
            : "WireView Pro II (hwmon)";
        public string HardwareRevision => string.Empty;
        public string FirmwareVersion => _firmwareVersion;
        public string UniqueId => _uniqueId;
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
                if (!File.Exists(DaemonSocketPath)) return;

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.ReceiveTimeout = 3000;
                socket.SendTimeout = 3000;
                socket.Connect(new UnixDomainSocketEndPoint(DaemonSocketPath));

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

                    if (data.Length > 14)
                    {
                        int end = Array.IndexOf(data, (byte)0, 14);
                        if (end < 0) end = data.Length;
                        // store build string if needed later
                    }

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

        private (byte status, byte[]? data) SendDaemonRequest(byte cmdType, byte[] payload)
        {
            lock (_socketLock)
            {
                try
                {
                    if (_daemonSocket == null)
                        return (0xFF, null);

                    // Send: [type:u8][len:u16 LE][payload]
                    var hdr = new byte[3];
                    hdr[0] = cmdType;
                    hdr[1] = (byte)(payload.Length & 0xFF);
                    hdr[2] = (byte)((payload.Length >> 8) & 0xFF);
                    _daemonSocket.Send(hdr);
                    if (payload.Length > 0)
                        _daemonSocket.Send(payload);

                    // Receive: [status:u8][len:u16 LE][payload]
                    var respHdr = new byte[3];
                    SocketReadExact(_daemonSocket, respHdr, 3);
                    byte status = respHdr[0];
                    int respLen = respHdr[1] | (respHdr[2] << 8);

                    if (respLen > 1024)
                        return (0xFF, null);

                    byte[]? respData = null;
                    if (respLen > 0)
                    {
                        respData = new byte[respLen];
                        SocketReadExact(_daemonSocket, respData, respLen);
                    }

                    return (status, respData);
                }
                catch
                {
                    try { _daemonSocket?.Dispose(); } catch { }
                    _daemonSocket = null;
                    DaemonAvailable = false;
                    return (0xFF, null);
                }
            }
        }

        private static void SocketReadExact(Socket socket, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = socket.Receive(buffer, offset, count - offset, SocketFlags.None);
                if (n == 0) throw new IOException("Socket closed");
                offset += n;
            }
        }

        // ---- Command methods ----

        public void ClearFaults(int faultStatusMask = 0xFFFF, int faultLogMask = 0xFFFF)
        {
            if (!DaemonAvailable) return;
            var payload = new byte[4];
            payload[0] = (byte)(faultStatusMask & 0xFF);
            payload[1] = (byte)((faultStatusMask >> 8) & 0xFF);
            payload[2] = (byte)(faultLogMask & 0xFF);
            payload[3] = (byte)((faultLogMask >> 8) & 0xFF);
            SendDaemonRequest(WCMD_CLEAR_FAULTS, payload);
        }

        public WireViewPro2Device.DeviceConfigStructV2? ReadConfig()
        {
            if (!DaemonAvailable || _configVersion < 0) return null;

            int size;
            if (_configVersion == 0)
                size = Marshal.SizeOf<WireViewPro2Device.DeviceConfigStructV1>();
            else if (_configVersion == 1)
                size = Marshal.SizeOf<WireViewPro2Device.DeviceConfigStructV2>();
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
                return WireViewPro2Device.ConvertConfigV1ToV2(v1);
            }
            else if (configVer == 1)
            {
                return BytesToStruct<WireViewPro2Device.DeviceConfigStructV2>(configBytes);
            }
            else
            {
                return null;
            }
        }

        public void WriteConfig(WireViewPro2Device.DeviceConfigStructV2 config)
        {
            if (!DaemonAvailable || _configVersion < 0) return;

            byte[] configBytes;
            if (_configVersion == 0)
            {
                var v1 = WireViewPro2Device.ConvertConfigV2ToV1(config);
                configBytes = StructToBytes(v1);
            }
            else
            {
                configBytes = StructToBytes(config);
            }

            var payload = new byte[1 + configBytes.Length];
            payload[0] = (byte)_configVersion;
            Buffer.BlockCopy(configBytes, 0, payload, 1, configBytes.Length);

            SendDaemonRequest(WCMD_WRITE_CONFIG, payload);
        }

        public void ScreenCmd(WireViewPro2Device.SCREEN_CMD cmd)
        {
            if (!DaemonAvailable) return;
            SendDaemonRequest(WCMD_SCREEN_CMD, new[] { (byte)cmd });
        }

        public void NvmCmd(WireViewPro2Device.NVM_CMD cmd)
        {
            if (!DaemonAvailable) return;
            SendDaemonRequest(WCMD_NVM_CMD, new[] { (byte)cmd });
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

        public void EnterBootloader()
        {
            if (!DaemonAvailable) return;
            SendDaemonRequest(WCMD_ENTER_BOOTLOADER, Array.Empty<byte>());
            try { Disconnect(); } catch { }
        }

        // ---- Sensor reading ----

        private void PollLoop(CancellationToken ct)
        {
            int consecutiveFailures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!Directory.Exists(_hwmonPath))
                    {
                        Disconnect();
                        return;
                    }

                    var data = ReadSensors();
                    if (data != null)
                    {
                        consecutiveFailures = 0;
                        DataUpdated?.Invoke(this, data);
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
                var dd = new DeviceData { Connected = true };

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

                if (TryReadIntFile("psu_cap", out int psuCap))
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
