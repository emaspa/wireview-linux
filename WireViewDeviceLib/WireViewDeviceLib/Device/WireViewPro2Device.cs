using System.IO.Ports;
using System.Runtime.InteropServices;

namespace WireView2.Device
{
    // Use SharedSerialPort instead of SerialPort
    using SerialPort = SharedSerialPort;

    public partial class WireViewPro2Device : IWireViewDevice, IDisposable
    {
        /// <summary>Welcome string of the Pro II and its Noctua Edition (identical).</summary>
        public const string WelcomeMessage = "Thermal Grizzly WireView Pro II";
        /// <summary>Common prefix of every WireView welcome string (upstream's
        /// WireViewBasicDevice probe). WireView II sends "Thermal Grizzly WireView II".</summary>
        private const string WelcomePrefix = "Thermal Grizzly WireView";
        private const int MaxWelcomeLength = 64;
        private readonly string _portName;
        private readonly int _baud;
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private int _disconnecting; // 1 once a Disconnect has begun; reset on connect

        /// <summary>Consecutive failed sensor reads after which a device whose port
        /// node still exists is given up. Same rule as HwmonDevice ("more than 5"),
        /// so both transports drop a silent device after the same number of missed
        /// samples; one corrupt frame or a 2 s bus-mutex wait never trips it, and an
        /// unplug is caught at once by the port-node check.</summary>
        public const int MaxConsecutiveFailedReads = 6;

        public event EventHandler<DeviceData>? DataUpdated;
        public event EventHandler<bool>? ConnectionChanged;

        public bool Connected { get; private set; }
        public string DeviceName => WireViewEditions.DisplayName(Edition);
        public string HardwareRevision { get; private set; } = string.Empty;
        public byte VendorId { get; private set; }
        public byte ProductId { get; private set; }
        public WireViewEdition Edition => WireViewEditions.FromIds(VendorId, ProductId);

        /// <summary>Set when the last <see cref="Connect"/> found a WireView whose
        /// product this class does not speak (WireView II / Phanteks Edition, products
        /// 7 and 8, or anything newer). The device manager stops re-probing that port
        /// until it disappears.</summary>
        public (byte VendorId, byte ProductId)? RejectedProduct { get; private set; }
        public string FirmwareVersion { get; private set; } = string.Empty;
        public string UniqueId { get; private set; } = string.Empty;
        public string BuildString { get; private set; } = string.Empty;

        public int ConfigVersion { get; private set; }

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

            RejectedProduct = null;

            // Probe like upstream's DeviceAutoConnector: any WireView answers RTS
            // with a welcome string starting "Thermal Grizzly WireView", and its
            // vendor data names the product.
            string? welcome = ReadWelcomeString();
            if (welcome == null || !welcome.StartsWith(WelcomePrefix, StringComparison.Ordinal))
            {
                Connected = false;
                return;
            }

            var vd = ReadVendorData();
            if (vd != null && !WireViewEditions.IsSupported(vd.Value.VendorId, vd.Value.ProductId))
            {
                RejectedProduct = (vd.Value.VendorId, vd.Value.ProductId);
                System.Diagnostics.Debug.WriteLine(
                    $"[WireViewPro2Device] {_portName}: unsupported WireView product " +
                    $"{vd.Value.VendorId:X2}{vd.Value.ProductId:X2} (\"{welcome}\"), skipped");
                Connected = false;
                return;
            }

            // Products 5 and 6 share the Pro II welcome string, protocol and config.
            if (vd != null && welcome == WelcomeMessage)
            {
                VendorId = vd.Value.VendorId;
                ProductId = vd.Value.ProductId;
                HardwareRevision = WireViewEditions.FormatHardwareRevision(VendorId, ProductId);
                FirmwareVersion = vd.Value.FwVersion.ToString();

                int? cfgVer = ReadConfigVersion();
                if (!cfgVer.HasValue)
                {
                    Connected = false;
                    return;
                }
                ConfigVersion = cfgVer.Value;

                UniqueId = ReadUid() ?? string.Empty;

                // Enable display updates just in case
                ScreenCmd(SCREEN_CMD.SCREEN_RESUME_UPDATES);

                Connected = true;
                // Cache the firmware build string once (static for the session) so it
                // can be shown locally and published to remote viewers over the LAN.
                BuildString = ReadBuildString() ?? string.Empty;
                ConnectionChanged?.Invoke(this, true);
            }
            else
            {
                Connected = false;
            }

            if (Connected)
            {
                Interlocked.Exchange(ref _disconnecting, 0);
                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => PollLoop(_cts.Token));
            }
        }

        public void Disconnect() => DisconnectCore(fromWorker: false);

        /// <param name="fromWorker">True when the poll loop gives up on the device:
        /// it must not wait for its own task (that only stalled for the 1 s timeout).</param>
        private void DisconnectCore(bool fromWorker)
        {
            if (!Connected) return;
            if (Interlocked.CompareExchange(ref _disconnecting, 1, 0) != 0) return;

            try
            {
                _cts?.Cancel();
                if (!fromWorker) _worker?.Wait(1000);
            }
            catch { }

            Connected = false;

            HardwareRevision = string.Empty;
            VendorId = 0;
            ProductId = 0;
            FirmwareVersion = string.Empty;
            UniqueId = string.Empty;
            BuildString = string.Empty;

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

        public DeviceConfigStructV3? ReadConfig()
        {
            if (!Connected || _port == null) return null;

            int size;
            if (ConfigVersion == 0)
                size = Marshal.SizeOf<DeviceConfigStructV1>();
            else if (ConfigVersion == 1)
                size = Marshal.SizeOf<DeviceConfigStructV2>();
            else if (ConfigVersion == 2)
                size = Marshal.SizeOf<DeviceConfigStructV3>();
            else
                return null;

            byte[]? buf = SendCmd(UsbCmd.CMD_READ_CONFIG, size);
            if (buf == null) return null;

            if (ConfigVersion == 0)
                return ConvertConfigV1ToV3(BytesToStruct<DeviceConfigStructV1>(buf));
            else if (ConfigVersion == 1)
                return ConvertConfigV2ToV3(BytesToStruct<DeviceConfigStructV2>(buf));
            else if (ConfigVersion == 2)
                return BytesToStruct<DeviceConfigStructV3>(buf);
            else
                return null;
        }

        /// <summary>Raw config byte length for a device config version (0=V1, 1=V2, 2=V3).</summary>
        public static int ConfigSizeForVersion(int configVersion) => configVersion switch
        {
            0 => Marshal.SizeOf<DeviceConfigStructV1>(),
            1 => Marshal.SizeOf<DeviceConfigStructV2>(),
            _ => Marshal.SizeOf<DeviceConfigStructV3>(),
        };

        /// <summary>Decode raw config bytes (in the device's version layout) to a V3 struct.</summary>
        public static DeviceConfigStructV3 DeserializeConfig(int configVersion, byte[] bytes) => configVersion switch
        {
            0 => ConvertConfigV1ToV3(BytesToStruct<DeviceConfigStructV1>(bytes)),
            1 => ConvertConfigV2ToV3(BytesToStruct<DeviceConfigStructV2>(bytes)),
            _ => BytesToStruct<DeviceConfigStructV3>(bytes),
        };

        /// <summary>Encode a V3 struct to raw config bytes in the device's version layout.</summary>
        public static byte[] SerializeConfig(DeviceConfigStructV3 config, int configVersion) => configVersion switch
        {
            0 => StructToBytes(ConvertConfigV3ToV1(config)),
            1 => StructToBytes(ConvertConfigV3ToV2(config)),
            _ => StructToBytes(config),
        };

        public void WriteConfig(DeviceConfigStructV3 config)
        {
            if (!Connected || _port == null) return;

            byte[] payload;
            if (ConfigVersion == 0)
                payload = StructToBytes(ConvertConfigV3ToV1(config));
            else if (ConfigVersion == 1)
                payload = StructToBytes(ConvertConfigV3ToV2(config));
            else if (ConfigVersion == 2)
                payload = StructToBytes(config);
            else
                return;

            var frame = new byte[64];
            frame[0] = (byte)UsbCmd.CMD_WRITE_CONFIG;

            lock (_port)
            {
                if (!_port!.Open()) return; // bus busy / port unavailable
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

        /// <summary>Write already-serialized config bytes (relayed from the network).</summary>
        public void WriteConfigRaw(byte[] payload)
        {
            if (!Connected || _port == null || payload == null || payload.Length == 0) return;

            var frame = new byte[64];
            frame[0] = (byte)UsbCmd.CMD_WRITE_CONFIG;

            lock (_port)
            {
                if (!_port!.Open()) return;
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

        /// <summary>
        /// Clears latched faults. Both masks are KEEP-masks: the firmware does
        /// <c>fault &amp;= mask</c>, so a set bit keeps that fault and a clear bit
        /// clears it. <c>0</c> clears everything, <c>0xFFFF</c> clears nothing and
        /// <c>~(1 &lt;&lt; (int)fault)</c> clears one fault. A parameterless call
        /// clears both the active faults and the fault log.
        /// </summary>
        /// <param name="keepStatusMask">Active (status) faults to keep.</param>
        /// <param name="keepLogMask">Fault-log (history) bits to keep.</param>
        public void ClearFaults(int keepStatusMask = 0, int keepLogMask = 0)
        {
            if (!Connected || _port == null) return;
            SendData(new[] { (byte)UsbCmd.CMD_CLEAR_FAULTS, (byte)(keepStatusMask & 0xFF), (byte)((keepStatusMask >> 8) & 0xFF), (byte)(keepLogMask & 0xFF), (byte)((keepLogMask >> 8) & 0xFF) }, 0);
        }

        /// <summary>Upstream 1.0.8 disconnects on the first failed read. Here a read
        /// also fails for a corrupt frame (discarded, see ReadSensorValues) and when
        /// the bus mutex stays busy for 2 s, so: disconnect at once when the port node
        /// is gone (unplug), otherwise after <see cref="MaxConsecutiveFailedReads"/>
        /// failed reads in a row. Long SPI transfers (log read, theme upload) hold the
        /// port lock, so the poll waits for them instead of failing.</summary>
        private void PollLoop(CancellationToken ct)
        {
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    SensorStruct? sensors;
                    bool corrupt;
                    try
                    {
                        sensors = ReadSensorValues(out corrupt);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        sensors = null; // e.g. IOException writing to a vanished port
                        corrupt = false;
                    }
                    if (ct.IsCancellationRequested) break;

                    if (sensors != null)
                    {
                        failures = 0;
                        var d = MapSensorStruct(sensors.Value);
                        DataUpdated?.Invoke(this, d);
                    }
                    else
                    {
                        failures++;
                        // A corrupt frame proves the device answered; it only counts.
                        if ((!corrupt && !PortNodeExists()) || failures >= MaxConsecutiveFailedReads)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[WireViewPro2Device] {_portName}: lost after {failures} failed read(s)");
                            DisconnectCore(fromWorker: true);
                            return;
                        }
                    }
                    Thread.Sleep(_pollIntervalMs);
                }
            }
            catch (Exception)
            {
                DisconnectCore(fromWorker: true);
            }
        }

        /// <summary>Whether the serial device node still exists: /dev/ttyACM* (or the
        /// path given) on Linux, a registered COM port on Windows.</summary>
        private bool PortNodeExists()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return System.IO.Ports.SerialPort.GetPortNames()
                        .Any(p => string.Equals(p, _portName, StringComparison.OrdinalIgnoreCase));
                return File.Exists(_portName);
            }
            catch
            {
                return true; // cannot tell: leave it to the failure count
            }
        }

        private int? ReadConfigVersion()
        {
            if (_port == null) return null;
            byte[]? buf = SendCmd(UsbCmd.CMD_READ_CONFIG, 4);
            if (buf == null) return null;
            return buf[2];
        }

        /// <summary>Asserts RTS and reads the NUL-terminated welcome string the
        /// device answers with. Its length depends on the product ("...Pro II" is 31
        /// characters, "...WireView II" 27), so read up to the terminator instead of
        /// a fixed size. Null when nothing (or no terminator) arrives in time.</summary>
        private string? ReadWelcomeString()
        {
            if (_port == null) return null;
            lock (_port)
            {
                if (!_port.Open()) return null;
                try
                {
                    _port.DiscardInBuffer();
                    _port.RtsEnable = true;
                    Thread.Sleep(10);
                    byte[]? buf = ReadUntilNul(MaxWelcomeLength);
                    Thread.Sleep(10);
                    _port.RtsEnable = false;
                    return buf == null ? null : System.Text.Encoding.ASCII.GetString(buf);
                }
                finally
                {
                    _port.Close();
                }
            }
        }

        /// <summary>Blocking reads (ReadTimeout paced) until a NUL byte, at most
        /// <paramref name="maxLength"/> bytes or one second. Returns the bytes before
        /// the NUL, or null without one.</summary>
        private byte[]? ReadUntilNul(int maxLength)
        {
            var buf = new byte[maxLength];
            int offset = 0;
            long deadline = Environment.TickCount64 + 1000;
            while (offset < maxLength && Environment.TickCount64 < deadline)
            {
                int n;
                try { n = _port!.Read(buf, offset, maxLength - offset); }
                catch (TimeoutException) { break; }
                if (n <= 0) break;
                int nul = Array.IndexOf(buf, (byte)0, offset, n);
                if (nul >= 0) return buf[..nul];
                offset += n;
            }
            return null;
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
            // NB: called from Connect() before Connected is set, so guard only on
            // the port (matching ReadVendorData/ReadConfigVersion). A stale
            // "!Connected" check here left UniqueId empty for serial devices.
            if (_port == null) return null;

            const int uidBytes = 12;
            byte[]? buf = SendCmd(UsbCmd.CMD_READ_UID, uidBytes);

            if (buf == null) return null;
            return BitConverter.ToString(buf).Replace("-", string.Empty);
        }

        private SensorStruct? ReadSensorValues(out bool corrupt)
        {
            corrupt = false;
            if (_port == null) return null;

            var size = Marshal.SizeOf<SensorStruct>();

            byte[]? buf = SendCmd(UsbCmd.CMD_READ_SENSOR_VALUES, size);

            if (buf == null) return null;

            // The serial protocol has no framing/CRC, so a desynced read
            // corrupts arbitrary fields for one poll (including the trailing
            // fault masks). Real frames always carry zero padding bytes and a
            // fan duty <= 100 - discard anything else; the next poll's input
            // flush realigns the stream. Wire layout: fan duty at offset 10,
            // pad1 at 11, pad2 five bytes from the end (before the two
            // 16-bit fault masks).
            if (buf.Length > 11 &&
                (buf[10] > 100 || buf[11] != 0 || buf[buf.Length - 5] != 0))
            {
                corrupt = true;
                return null;
            }

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
                FaultLog = ss.FaultLog,
                FanDuty = ss.FanDuty
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
                if (!_port!.Open()) return null; // bus busy / port unavailable -> failed read
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