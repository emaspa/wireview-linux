using System.IO.Ports;
using System.Runtime.InteropServices;

namespace WireView2.Device
{
    // Use SharedSerialPort instead of SerialPort
    using SerialPort = SharedSerialPort;

    public partial class WireViewPro2Device : IWireViewDevice, IDisposable
    {
        public const string WelcomeMessage = "Thermal Grizzly WireView Pro II";
        private readonly string _portName;
        private readonly int _baud;
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _worker;

        public event EventHandler<DeviceData>? DataUpdated;
        public event EventHandler<bool>? ConnectionChanged;

        public bool Connected { get; private set; }
        public string DeviceName => "WireView Pro II";
        public string HardwareRevision { get; private set; } = string.Empty;
        public string FirmwareVersion { get; private set; } = string.Empty;
        public string UniqueId { get; private set; } = string.Empty;

        private int ConfigVersion = 0;

        private int _pollIntervalMs = 1000;
        public int PollIntervalMs
        {
            get => _pollIntervalMs;
            set => _pollIntervalMs = Math.Max(100, Math.Min(5000, value));
        }

        public WireViewPro2Device(string portName, int baud = 115200)
        {
            _portName = portName;
            _baud = baud;
        }

        public void Connect()
        {
            if (Connected) return;

            _port = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One);
            _port.ReadTimeout = 1000;
            _port.WriteTimeout = 1000;

            // First try to read welcome message without sending command
            if (!ReadWelcomeMessage(false))
            {
                Connected = false;
                return;
            }

            var vd = ReadVendorData();
            if (vd != null && vd.Value.VendorId == 0xEF && vd.Value.ProductId == 0x05)
            {
                HardwareRevision = $"{vd.Value.VendorId:X2}{vd.Value.ProductId:X2}";
                FirmwareVersion = vd.Value.FwVersion.ToString();

                ConfigVersion = vd.Value.FwVersion > 2 ? 1 : 0;

                UniqueId = ReadUid() ?? string.Empty;

                // Enable display updates just in case
                ScreenCmd(SCREEN_CMD.SCREEN_RESUME_UPDATES);

                Connected = true;
                ConnectionChanged?.Invoke(this, true);
            }
            else
            {
                Connected = false;
            }
            _port.RtsEnable = false;
            _port.Close();

            if (Connected)
            {
                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => PollLoop(_cts.Token));
            }
        }

        public void Disconnect()
        {
            if (!Connected) return;

            try
            {
                _cts?.Cancel();
                _worker?.Wait(1000);
            }
            catch { }

            Connected = false;

            HardwareRevision = string.Empty;
            FirmwareVersion = string.Empty;
            UniqueId = string.Empty;

            ConnectionChanged?.Invoke(this, false);
        }

        public string? ReadBuildString()
        {
            if (!Connected || _port == null) return null;

            var size = Marshal.SizeOf<BuildStruct>();
            byte[]? buf = SendCmd(UsbCmd.CMD_READ_BUILD_INFO, size);

            if (buf == null) return null;
            BuildStruct buildStruct = BytesToStruct<BuildStruct>(buf);

            return buildStruct.BuildInfo;
        }

        public void EnterBootloader()
        {
            if (!Connected || _port == null) return;

            SendCmd(UsbCmd.CMD_BOOTLOADER);
            try { Disconnect(); } catch { }
        }

        public DeviceConfigStructV2? ReadConfig()
        {
            if (!Connected || _port == null) return null;

            var size = 0;


            if (ConfigVersion == 0)
            {
                size = Marshal.SizeOf<DeviceConfigStructV1>();
            }
            else if (ConfigVersion == 1)
            {
                size = Marshal.SizeOf<DeviceConfigStructV2>();
            }
            else
            {
                return null;
            }

            byte[]? buf = SendCmd(UsbCmd.CMD_READ_CONFIG, size);

            if (buf == null) return null;

            if (ConfigVersion == 0)
            {
                var _s = BytesToStruct<DeviceConfigStructV1>(buf);
                return ConvertConfigV1ToV2(_s);
            }
            else if (ConfigVersion == 1)
            {
                return BytesToStruct<DeviceConfigStructV2>(buf);
            }
            else
            {
                return null;
            }
        }

        public void WriteConfig(DeviceConfigStructV2 config)
        {
            if (!Connected || _port == null) return;

            var payload = new byte[0];

            if (ConfigVersion == 0)
            {
                DeviceConfigStructV1 _s = ConvertConfigV2ToV1(config);
                payload = StructToBytes(_s);
            }
            else if (ConfigVersion == 1)
            {
                payload = StructToBytes(config);
            }
            else
            {
                return;
            }

            var frame = new byte[64];
            frame[0] = (byte)UsbCmd.CMD_WRITE_CONFIG;

            lock (_port)
            {
                _port!.Open();
                try
                {
                    _port!.DiscardInBuffer();

                    const int maxPayloadPerFrame = 62;

                    for (int offset = 0; offset < payload.Length && offset <= 255; offset += maxPayloadPerFrame)
                    {
                        int bytesToWrite = Math.Min(maxPayloadPerFrame, payload.Length - offset);

                        frame[1] = (byte)offset;
                        Buffer.BlockCopy(payload, offset, frame, 2, bytesToWrite);

                        _port!.Write(frame, 0, bytesToWrite + 2);
                    }
                }
                finally
                {
                    _port!.Close();
                }
            }
        }

        public void NvmCmd(NVM_CMD cmd)
        {
            if (!Connected || _port == null) return;
            SendData(new[] { (byte)UsbCmd.CMD_NVM_CONFIG, (byte)0x55, (byte)0xAA, (byte)0x55, (byte)0xAA, (byte)cmd }, 0);
        }

        public void ScreenCmd(SCREEN_CMD cmd)
        {
            if (!Connected || _port == null) return;
            SendData(new[] { (byte)UsbCmd.CMD_SCREEN_CHANGE, (byte)cmd }, 0);
        }

        public void ClearFaults(int faultStatusMask = 0xFFFF, int faultLogMask = 0xFFFF)
        {
            if (!Connected || _port == null) return;
            SendData(new[] { (byte)UsbCmd.CMD_CLEAR_FAULTS, (byte)(faultStatusMask & 0xFF), (byte)((faultStatusMask >> 8) & 0xFF), (byte)(faultLogMask & 0xFF), (byte)((faultLogMask >> 8) & 0xFF) }, 0);
        }

        private void PollLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var sensors = ReadSensorValues();
                    if (sensors != null)
                    {
                        var d = MapSensorStruct(sensors.Value);
                        DataUpdated?.Invoke(this, d);
                    }
                    Thread.Sleep(_pollIntervalMs);
                }
            }
            catch (Exception)
            {
                Disconnect();
            }
        }

        private bool ReadWelcomeMessage(bool sendCmd = false)
        {
            if (_port == null) return false;

            var size = WelcomeMessage.Length + 1;

            byte[]? buf = SendData(new byte[0], size, rts: true);

            if (buf == null) return false;
            return System.Text.Encoding.ASCII.GetString(buf, 0, size).TrimEnd('\0').CompareTo(WelcomeMessage) == 0;
        }

        private VendorDataStruct? ReadVendorData()
        {
            if (_port == null) return null;

            var size = Marshal.SizeOf<VendorDataStruct>();
            byte[]? buf = SendCmd(UsbCmd.CMD_READ_VENDOR_DATA, size);

            if (buf == null) return null;
            return BytesToStruct<VendorDataStruct>(buf);
        }

        private string? ReadUid()
        {
            if (!Connected || _port == null) return null;

            const int uidBytes = 12;
            byte[]? buf = SendCmd(UsbCmd.CMD_READ_UID, uidBytes);

            if (buf == null) return null;
            return BitConverter.ToString(buf).Replace("-", string.Empty);
        }

        private SensorStruct? ReadSensorValues()
        {
            if (_port == null) return null;

            var size = Marshal.SizeOf<SensorStruct>();

            byte[]? buf = SendCmd(UsbCmd.CMD_READ_SENSOR_VALUES, size);

            if (buf == null) return null;
            return BytesToStruct<SensorStruct>(buf);
        }

        private DeviceData MapSensorStruct(SensorStruct ss)
        {
            var dd = new DeviceData
            {
                Connected = true,
                HardwareRevision = HardwareRevision,
                FirmwareVersion = FirmwareVersion,
                OnboardTempInC = ss.Ts[(int)SensorTs.SENSOR_TS_IN] / 10.0,
                OnboardTempOutC = ss.Ts[(int)SensorTs.SENSOR_TS_OUT] / 10.0,
                ExternalTemp1C = ss.Ts[(int)SensorTs.SENSOR_TS3] / 10.0,
                ExternalTemp2C = ss.Ts[(int)SensorTs.SENSOR_TS4] / 10.0,
                PsuCapabilityW = ss.HpwrCapability == HpwrCapability.PSU_CAP_600W ? 600 :
                                  ss.HpwrCapability == HpwrCapability.PSU_CAP_450W ? 450 :
                                  ss.HpwrCapability == HpwrCapability.PSU_CAP_300W ? 300 :
                                  ss.HpwrCapability == HpwrCapability.PSU_CAP_150W ? 150 : 0,

                FaultStatus = ss.FaultStatus,
                FaultLog = ss.FaultLog
            };

            for (int i = 0; i < 6; i++)
            {
                dd.PinVoltage[i] = ss.PowerReadings[i].Voltage / 1000.0;
                dd.PinCurrent[i] = ss.PowerReadings[i].Current / 1000.0;
            }

            return dd;
        }

        private byte[]? SendCmd(UsbCmd cmd, int responseSize = 0, bool rts = false)
        {
            return SendData(new[] { (byte)cmd }, responseSize, rts);
        }

        private byte[]? SendData(byte[] data, int responseSize = 0, bool rts = false)
        {
            if (_port == null) return null;
            byte[]? buf = null;
            lock (_port)
            {
                _port!.Open();
                try
                {
                    _port!.DiscardInBuffer();
                    if (rts)
                    {
                        _port!.RtsEnable = true;
                        Thread.Sleep(10);
                    }
                    if (data.Length > 0)
                        _port!.Write(data, 0, data.Length);
                    if (responseSize > 0)
                    {
                        buf = ReadExact(responseSize);
                    }
                    if (rts)
                    {
                        Thread.Sleep(10);
                        _port!.RtsEnable = false;
                    }
                }
                finally
                {
                    _port!.Close();
                }
            }
            return buf;
        }

        private byte[]? ReadExact(int size)
        {
            var buf = new byte[size];
            int offset = 0;
            int timeout = 1000;
            var start = Environment.TickCount64;

            while (offset < size && Environment.TickCount64 - start < timeout)
            {
                if (_port!.BytesToRead > 0)
                {
                    offset += _port!.Read(buf, offset, size - offset);
                }
            }
            return offset == size ? buf : null;
        }

        public static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
        }

        public static byte[] StructToBytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[size];

            nint p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                Marshal.Copy(p, bytes, 0, size);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        public void Dispose() => Disconnect();
    }
}