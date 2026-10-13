using System.Globalization;
using System.Text.Json.Serialization;

namespace WireView2.Device
{
    /// <summary>A modelled field is out of range or unknown; the message names it.</summary>
    public sealed class DeviceConfigException(string message) : Exception(message);

    /// <summary>One fault type's alarm switches (a bit in each of the four masks).</summary>
    public sealed class FaultAlarmModel
    {
        /// <summary>Short key: otp, ocp, wireOcp, opp, imbalance.</summary>
        public string Fault { get; set; } = "";
        public bool Display { get; set; }
        public bool Buzzer { get; set; }
        public bool SoftOff { get; set; }
        public bool HardOff { get; set; }
    }

    /// <summary>
    /// A WireView Pro II device config decoded into plain properties, in the units
    /// the app's Device page shows (°C, A, W, %, seconds; enums as their numeric
    /// values, see <see cref="DeviceConfigOptions"/> for names and labels).
    ///
    /// <see cref="Version"/> is the device's config layout: 0 = DeviceConfigStructV1,
    /// 1 = V2 (adds averaging), 2 = V3 (UiConfigStructV2: default screen, colours and
    /// bitmaps instead of the legacy theme). Fields a layout does not have are null.
    ///
    /// The model keeps the bytes it was decoded from (<see cref="Raw"/>). Encoding
    /// works like the app's Apply: only the fields that differ from those bytes are
    /// changed (<see cref="DeviceConfigEditor.ApplyChanges"/>), patched onto the
    /// device's current bytes with <see cref="WireViewPro2Device.MergeConfigChanges"/>,
    /// so every byte the model does not cover (colours, bitmaps, unknown enum values,
    /// padding, the CRC field) stays as the device has it, and
    /// <c>Decode(v, raw).Encode()</c> returns <c>raw</c> unchanged. The app writes no
    /// CRC of its own (the CRC field keeps the device's bytes), and neither does this.
    /// </summary>
    public sealed class DeviceConfigModel
    {
        /// <summary>Screens the cycle bitmask and default screen use, bit = value.</summary>
        public static readonly IReadOnlyList<(WireViewPro2Device.Screen Value, string Name)> Screens =
        [
            (WireViewPro2Device.Screen.ScreenMain, "Main"),
            (WireViewPro2Device.Screen.ScreenSimple, "Simple"),
            (WireViewPro2Device.Screen.ScreenCurrent, "Current"),
            (WireViewPro2Device.Screen.ScreenTemp, "Temperature"),
            (WireViewPro2Device.Screen.ScreenStatus, "Status"),
        ];

        /// <summary>The fault types the app's Device page offers alarms for (the chip
        /// temperature bit is not offered there, and keeps its device value).</summary>
        public static readonly IReadOnlyList<(WireViewPro2Device.FAULT Bit, string Key, string Label)> AlarmFaults =
        [
            (WireViewPro2Device.FAULT.FAULT_OTP_TS, "otp", "Over-Temp (OTP)"),
            (WireViewPro2Device.FAULT.FAULT_OCP, "ocp", "Over-Current (OCP)"),
            (WireViewPro2Device.FAULT.FAULT_WIRE_OCP, "wireOcp", "Wire Over-Current"),
            (WireViewPro2Device.FAULT.FAULT_OPP, "opp", "Over-Power (OPP)"),
            (WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE, "imbalance", "Current Imbalance"),
        ];

        public int Version { get; set; }
        /// <summary>The decoded bytes, base64. Sent back unchanged with an edit: the
        /// edit is the difference between these and the other fields.</summary>
        public string Raw { get; set; } = "";

        // General
        public string FriendlyName { get; set; } = "";
        public int BacklightDuty { get; set; }

        // Fan
        public int FanMode { get; set; }
        public int FanTempSource { get; set; }
        public int FanDutyMin { get; set; }
        public int FanDutyMax { get; set; }
        public double FanTempMinC { get; set; }
        public double FanTempMaxC { get; set; }

        // Display
        public int CurrentScale { get; set; }
        public int PowerScale { get; set; }
        /// <summary>Legacy theme, layouts 0 and 1 only (layout 2 has colours and
        /// bitmaps, edited in the app).</summary>
        public int? Theme { get; set; }
        public int DisplayRotation { get; set; }
        public int TimeoutMode { get; set; }
        public List<string> CycleScreens { get; set; } = new();
        public int CycleTimeSeconds { get; set; }
        public int TimeoutSeconds { get; set; }
        /// <summary>Layout 1 and newer.</summary>
        public int? Averaging { get; set; }
        /// <summary>Layout 2 only.</summary>
        public int? DefaultScreen { get; set; }

        // Fault alarms and thresholds
        public List<FaultAlarmModel> FaultAlarms { get; set; } = new();
        public double TempThresholdC { get; set; }
        public int OcpThresholdA { get; set; }
        public double WireOcpThresholdA { get; set; }
        public int OppThresholdW { get; set; }
        public int ImbalanceThresholdPercent { get; set; }
        public int ImbalanceMinLoadA { get; set; }
        public int ShutdownWaitSeconds { get; set; }
        public int LoggingIntervalSeconds { get; set; }

        [JsonIgnore]
        public byte[] RawBytes => string.IsNullOrEmpty(Raw) ? Array.Empty<byte>() : Convert.FromBase64String(Raw);

        // ---- Decode ----

        /// <summary>Decodes raw config bytes in layout <paramref name="version"/>
        /// (0, 1 or 2), the same mapping as the app's Device page.</summary>
        public static DeviceConfigModel Decode(int version, byte[] raw)
        {
            if (version is < 0 or > 2) throw new DeviceConfigException($"unknown config layout {version}");
            int size = WireViewPro2Device.ConfigSizeForVersion(version);
            if (raw.Length < size)
                throw new DeviceConfigException($"config is {raw.Length} bytes, layout {version} needs {size}");
            var cfg = WireViewPro2Device.DeserializeConfig(version, raw);
            var m = new DeviceConfigModel
            {
                Version = version,
                Raw = Convert.ToBase64String(raw),
                FriendlyName = DeviceConfigEditor.DecodeDeviceString(cfg.FriendlyName),
                BacklightDuty = cfg.BacklightDuty,
                FanMode = (int)cfg.FanConfig.Mode,
                FanTempSource = (int)cfg.FanConfig.TempSource,
                FanDutyMin = cfg.FanConfig.DutyMin,
                FanDutyMax = cfg.FanConfig.DutyMax,
                FanTempMinC = cfg.FanConfig.TempMin / 10.0,
                FanTempMaxC = cfg.FanConfig.TempMax / 10.0,
                CurrentScale = (int)cfg.Ui.CurrentScale,
                PowerScale = (int)cfg.Ui.PowerScale,
                Theme = version < 2 ? (int)LegacyTheme(cfg) : null,
                DisplayRotation = (int)cfg.Ui.DisplayRotation,
                TimeoutMode = (int)cfg.Ui.TimeoutMode,
                CycleScreens = Screens.Where(s => (cfg.Ui.CycleScreens & (1 << (int)s.Value)) != 0).Select(s => s.Name).ToList(),
                CycleTimeSeconds = cfg.Ui.CycleTime,
                TimeoutSeconds = cfg.Ui.Timeout,
                Averaging = version >= 1 ? (int)cfg.Average : null,
                DefaultScreen = version >= 2 ? (int)cfg.Ui.DefaultScreen : null,
                TempThresholdC = cfg.TsFaultThreshold / 10.0,
                OcpThresholdA = cfg.OcpFaultThreshold,
                WireOcpThresholdA = cfg.WireOcpFaultThreshold / 10.0,
                OppThresholdW = cfg.OppFaultThreshold,
                ImbalanceThresholdPercent = cfg.CurrentImbalanceFaultThreshold,
                ImbalanceMinLoadA = cfg.CurrentImbalanceFaultMinLoad,
                ShutdownWaitSeconds = cfg.ShutdownWaitTime,
                LoggingIntervalSeconds = cfg.LoggingInterval,
            };
            foreach (var (bit, key, _) in AlarmFaults)
            {
                int b = 1 << (int)bit;
                m.FaultAlarms.Add(new FaultAlarmModel
                {
                    Fault = key,
                    Display = (cfg.FaultDisplayEnable & b) != 0,
                    Buzzer = (cfg.FaultBuzzerEnable & b) != 0,
                    SoftOff = (cfg.FaultSoftPowerEnable & b) != 0,
                    HardOff = (cfg.FaultHardPowerEnable & b) != 0,
                });
            }
            return m;
        }

        // The app derives the legacy theme from the background bitmap id.
        private static WireViewPro2Device.Theme LegacyTheme(WireViewPro2Device.DeviceConfigStructV3 cfg) =>
            cfg.Ui.BackgroundBitmapId == 1 ? WireViewPro2Device.Theme.ThemeTg1
            : cfg.Ui.BackgroundBitmapId == 2 ? WireViewPro2Device.Theme.ThemeTg2
            : WireViewPro2Device.Theme.ThemeTg3;

        // ---- Encode ----

        /// <summary>The bytes to write: <see cref="Raw"/> with the edited fields
        /// patched in. Throws <see cref="DeviceConfigException"/> for an edited field
        /// out of range.</summary>
        public byte[] Encode() => EncodeOnto(RawBytes);

        /// <summary>Like the app's Apply: the fields edited relative to <see cref="Raw"/>
        /// are patched onto <paramref name="current"/> (the device's bytes now, same
        /// layout), every other byte of it is kept.</summary>
        public byte[] EncodeOnto(byte[] current)
        {
            int size = WireViewPro2Device.ConfigSizeForVersion(Version);
            if (current.Length < size)
                throw new DeviceConfigException($"device config is {current.Length} bytes, layout {Version} needs {size}");
            var raw = RawBytes;
            if (raw.Length < size)
                throw new DeviceConfigException("the edit does not carry the config it was made from (raw); reload it");
            var loaded = Decode(Version, raw);
            Validate(loaded);

            var basis = WireViewPro2Device.DeserializeConfig(Version, raw);
            var was = loaded.ToEditorValues(basis);
            var now = ToEditorValues(basis, loaded);
            var edited = DeviceConfigEditor.ApplyChanges(
                WireViewPro2Device.DeserializeConfig(Version, current), was, now,
                averagingSupported: Version >= 1, uiV2Supported: Version >= 2);

            // Cycle screens are not on the app's page; only the five known bits change.
            byte wasCycle = CycleMask(loaded.CycleScreens), nowCycle = CycleMask(CycleScreens);
            if (wasCycle != nowCycle)
            {
                byte changed = (byte)(wasCycle ^ nowCycle);
                edited.Ui.CycleScreens = (byte)((edited.Ui.CycleScreens & ~changed) | (nowCycle & changed));
            }
            return WireViewPro2Device.MergeConfigChanges(Version, current, edited);
        }

        private static byte CycleMask(IEnumerable<string> names)
        {
            int mask = 0;
            foreach (var n in names)
            {
                var s = Screens.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                if (s.Name == null) throw new DeviceConfigException($"cycle screens: unknown screen \"{n}\"");
                mask |= 1 << (int)s.Value;
            }
            return (byte)mask;
        }

        /// <summary>Editor values: the modelled fields from this model, the rest (colours,
        /// bitmaps, inversion, and fields the layout lacks) from <paramref name="basis"/>
        /// so they never count as edited. Masks keep the basis bits the model does not show.</summary>
        private ConfigEditorValues ToEditorValues(WireViewPro2Device.DeviceConfigStructV3 basis, DeviceConfigModel? loaded = null)
        {
            loaded ??= this;
            ushort Mask(ushort basisMask, Func<FaultAlarmModel, bool> pick)
            {
                int mask = basisMask;
                foreach (var (bit, key, _) in AlarmFaults)
                {
                    var a = FaultAlarms.FirstOrDefault(f => f.Fault == key)
                            ?? loaded.FaultAlarms.First(f => f.Fault == key);
                    int b = 1 << (int)bit;
                    mask = pick(a) ? mask | b : mask & ~b;
                }
                return (ushort)mask;
            }
            var bg = (WireViewPro2Device.THEME_BACKGROUND)basis.Ui.BackgroundBitmapId;
            return new ConfigEditorValues(
                FriendlyName, BacklightDuty,
                (WireViewPro2Device.FanMode)FanMode, (WireViewPro2Device.TempSource)FanTempSource,
                FanDutyMin, FanDutyMax, FanTempMinC, FanTempMaxC,
                Version >= 2 && DefaultScreen is { } ds ? (WireViewPro2Device.Screen)ds : basis.Ui.DefaultScreen,
                (WireViewPro2Device.CurrentScale)CurrentScale, (WireViewPro2Device.PowerScale)PowerScale,
                Version < 2 && Theme is { } t ? (WireViewPro2Device.Theme)t : LegacyTheme(basis),
                (WireViewPro2Device.DisplayRotation)DisplayRotation, (WireViewPro2Device.TimeoutMode)TimeoutMode,
                CycleTimeSeconds, TimeoutSeconds,
                basis.Ui.PrimaryColor, basis.Ui.SecondaryColor, basis.Ui.HighlightColor, basis.Ui.BackgroundColor,
                bg, basis.Ui.DisplayInversion == WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_ON,
                Mask(basis.FaultDisplayEnable, a => a.Display), Mask(basis.FaultBuzzerEnable, a => a.Buzzer),
                Mask(basis.FaultSoftPowerEnable, a => a.SoftOff), Mask(basis.FaultHardPowerEnable, a => a.HardOff),
                TempThresholdC, OcpThresholdA, WireOcpThresholdA, OppThresholdW,
                ImbalanceThresholdPercent, ImbalanceMinLoadA, ShutdownWaitSeconds, LoggingIntervalSeconds,
                Version >= 1 && Averaging is { } avg ? (WireViewPro2Device.AVG)avg : basis.Average);
        }

        // ---- Validation ----

        /// <summary>Checks every field edited relative to <paramref name="loaded"/>.
        /// Unedited fields pass as they are, so a value the device holds that this
        /// version does not know is kept, not refused.</summary>
        private void Validate(DeviceConfigModel loaded)
        {
            FriendlyName ??= "";
            CycleScreens ??= new();
            FaultAlarms ??= new();
            if (FriendlyName != loaded.FriendlyName)
            {
                string n = FriendlyName.Trim();
                if (n.Length > WireViewPro2Device.DEVICE_STR_LEN - 1)
                    throw new DeviceConfigException($"friendly name: at most {WireViewPro2Device.DEVICE_STR_LEN - 1} characters");
                if (n.Any(c => c < 0x20 || c > 0x7E))
                    throw new DeviceConfigException("friendly name: plain ASCII letters, digits and punctuation only");
            }
            void Int(string name, int now, int was, string limitKey)
            {
                if (now == was) return;
                var l = DeviceConfigOptions.Limits[limitKey];
                if (now < l.Min || now > l.Max)
                    throw new DeviceConfigException(string.Create(CultureInfo.InvariantCulture, $"{name}: {now} is outside {l.Min} to {l.Max}"));
            }
            void Dbl(string name, double now, double was, string limitKey)
            {
                if (now == was) return;
                var l = DeviceConfigOptions.Limits[limitKey];
                if (!double.IsFinite(now) || now < l.Min || now > l.Max)
                    throw new DeviceConfigException(string.Create(CultureInfo.InvariantCulture, $"{name}: {now} is outside {l.Min} to {l.Max}"));
            }
            void Enum<T>(string name, int? now, int? was) where T : struct, System.Enum
            {
                if (now == was || now is not { } v) return;
                if (!System.Enum.IsDefined(typeof(T), (byte)Math.Clamp(v, 0, 255)) || v is < 0 or > 255)
                    throw new DeviceConfigException($"{name}: unknown value {v}");
            }
            Int("backlight", BacklightDuty, loaded.BacklightDuty, "backlightDuty");
            Enum<WireViewPro2Device.FanMode>("fan mode", FanMode, loaded.FanMode);
            Enum<WireViewPro2Device.TempSource>("fan temperature source", FanTempSource, loaded.FanTempSource);
            Int("fan duty min", FanDutyMin, loaded.FanDutyMin, "fanDuty");
            Int("fan duty max", FanDutyMax, loaded.FanDutyMax, "fanDuty");
            Dbl("fan temp min", FanTempMinC, loaded.FanTempMinC, "fanTemp");
            Dbl("fan temp max", FanTempMaxC, loaded.FanTempMaxC, "fanTemp");
            Enum<WireViewPro2Device.CurrentScale>("current scale", CurrentScale, loaded.CurrentScale);
            Enum<WireViewPro2Device.PowerScale>("power scale", PowerScale, loaded.PowerScale);
            if (Version < 2) Enum<WireViewPro2Device.Theme>("theme", Theme, loaded.Theme);
            Enum<WireViewPro2Device.DisplayRotation>("display rotation", DisplayRotation, loaded.DisplayRotation);
            Enum<WireViewPro2Device.TimeoutMode>("timeout mode", TimeoutMode, loaded.TimeoutMode);
            Int("cycle time", CycleTimeSeconds, loaded.CycleTimeSeconds, "cycleTime");
            Int("timeout", TimeoutSeconds, loaded.TimeoutSeconds, "byteSeconds");
            if (Version >= 1) Enum<WireViewPro2Device.AVG>("averaging", Averaging, loaded.Averaging);
            if (Version >= 2) Enum<WireViewPro2Device.Screen>("default screen", DefaultScreen, loaded.DefaultScreen);
            Dbl("temperature threshold", TempThresholdC, loaded.TempThresholdC, "tempThreshold");
            Int("OCP threshold", OcpThresholdA, loaded.OcpThresholdA, "ocp");
            Dbl("wire OCP threshold", WireOcpThresholdA, loaded.WireOcpThresholdA, "wireOcp");
            Int("OPP threshold", OppThresholdW, loaded.OppThresholdW, "opp");
            Int("current imbalance", ImbalanceThresholdPercent, loaded.ImbalanceThresholdPercent, "percent");
            Int("imbalance min load", ImbalanceMinLoadA, loaded.ImbalanceMinLoadA, "byteAmps");
            Int("shutdown wait time", ShutdownWaitSeconds, loaded.ShutdownWaitSeconds, "byteSeconds");
            Int("logging interval", LoggingIntervalSeconds, loaded.LoggingIntervalSeconds, "byteSeconds");
            foreach (var a in FaultAlarms)
                if (AlarmFaults.All(f => f.Key != a.Fault))
                    throw new DeviceConfigException($"fault alarms: unknown fault \"{a.Fault}\"");
            CycleMask(CycleScreens); // throws for an unknown name
        }
    }

    /// <summary>A select's choice: the numeric value, the enum name and a label.</summary>
    public sealed record ConfigOption(int Value, string Name, string Label);

    /// <summary>An input's range and step, in the model's units.</summary>
    public sealed record ConfigLimit(double Min, double Max, double Step);

    /// <summary>The choices for each enum field of <see cref="DeviceConfigModel"/>
    /// and the numeric ranges, so a page can render its form from them.</summary>
    public sealed class DeviceConfigOptions
    {
        /// <summary>Firmware 05 and newer offer the two long averaging windows
        /// (the app's ExtendedAveragingMinProFirmwareVersion).</summary>
        public const int ExtendedAveragingMinFirmware = 5;

        /// <summary>Ranges: the app's Device page limits, else the struct field's width.</summary>
        public static readonly IReadOnlyDictionary<string, ConfigLimit> Limits = new Dictionary<string, ConfigLimit>
        {
            ["backlightDuty"] = new(0, 100, 1),
            ["fanDuty"] = new(0, 100, 1),
            ["fanTemp"] = new(-50, 150, 0.5),
            ["cycleTime"] = new(1, 60, 1),
            ["byteSeconds"] = new(0, 255, 1),
            ["tempThreshold"] = new(0, 200, 0.5),
            ["ocp"] = new(0, 255, 1),
            ["wireOcp"] = new(0, 25.5, 0.1),
            ["opp"] = new(0, 65535, 1),
            ["percent"] = new(0, 100, 1),
            ["byteAmps"] = new(0, 255, 1),
        };

        public List<ConfigOption> FanModes { get; init; } = new();
        public List<ConfigOption> TempSources { get; init; } = new();
        public List<ConfigOption> CurrentScales { get; init; } = new();
        public List<ConfigOption> PowerScales { get; init; } = new();
        public List<ConfigOption> Themes { get; init; } = new();
        public List<ConfigOption> DisplayRotations { get; init; } = new();
        public List<ConfigOption> TimeoutModes { get; init; } = new();
        public List<ConfigOption> Screens { get; init; } = new();
        public List<ConfigOption> Averaging { get; init; } = new();
        /// <summary>Fault types for the alarm table: value = bit number, name = key.</summary>
        public List<ConfigOption> Faults { get; init; } = new();
        public IReadOnlyDictionary<string, ConfigLimit> Ranges => Limits;

        /// <summary>The options for a device. <paramref name="firmwareVersion"/>
        /// selects the averaging windows like the app does; a value the device holds
        /// that is not in a list is added as "Unknown (n)".</summary>
        public static DeviceConfigOptions For(int? firmwareVersion, DeviceConfigModel? current = null)
        {
            static List<ConfigOption> Of<T>(Func<T, string> label, int? keep = null) where T : struct, Enum
            {
                var list = Enum.GetValues<T>().Select(v => new ConfigOption(Convert.ToInt32(v, CultureInfo.InvariantCulture), v.ToString(), label(v))).ToList();
                if (keep is { } k && list.All(o => o.Value != k))
                    list.Add(new ConfigOption(k, k.ToString(CultureInfo.InvariantCulture), $"Unknown ({k})"));
                return list;
            }
            var avg = Of<WireViewPro2Device.AVG>(AveragingLabel);
            if (!(firmwareVersion >= ExtendedAveragingMinFirmware))
                avg = avg.Where(o => o.Value <= (int)WireViewPro2Device.AVG.AVG_1417MS).ToList();
            if (current?.Averaging is { } a && avg.All(o => o.Value != a))
                avg.Add(new ConfigOption(a, a.ToString(CultureInfo.InvariantCulture),
                    Enum.IsDefined((WireViewPro2Device.AVG)a) ? AveragingLabel((WireViewPro2Device.AVG)a) : $"Unknown ({a})"));

            return new DeviceConfigOptions
            {
                FanModes = Of<WireViewPro2Device.FanMode>(v => v == WireViewPro2Device.FanMode.FanModeCurve ? "Curve" : "Fixed", current?.FanMode),
                TempSources = Of<WireViewPro2Device.TempSource>(v => v switch
                {
                    WireViewPro2Device.TempSource.TempSourceTsIn => "Temp in",
                    WireViewPro2Device.TempSource.TempSourceTsOut => "Temp out",
                    WireViewPro2Device.TempSource.TempSourceTs1 => "External 1",
                    WireViewPro2Device.TempSource.TempSourceTs2 => "External 2",
                    _ => "Highest",
                }, current?.FanTempSource),
                CurrentScales = Of<WireViewPro2Device.CurrentScale>(v => $"{((int)v + 1) * 5} A", current?.CurrentScale),
                PowerScales = Of<WireViewPro2Device.PowerScale>(v => v switch
                {
                    WireViewPro2Device.PowerScale.PowerScale300W => "300 W",
                    WireViewPro2Device.PowerScale.PowerScale600W => "600 W",
                    _ => "Auto",
                }, current?.PowerScale),
                Themes = Of<WireViewPro2Device.Theme>(v => v switch
                {
                    WireViewPro2Device.Theme.ThemeTg1 => "TG 1 (orange)",
                    WireViewPro2Device.Theme.ThemeTg2 => "TG 2 (dark)",
                    _ => "TG 3 (plain)",
                }, current?.Theme),
                DisplayRotations = Of<WireViewPro2Device.DisplayRotation>(v => v == WireViewPro2Device.DisplayRotation.DisplayRotation0 ? "0°" : "180°", current?.DisplayRotation),
                TimeoutModes = Of<WireViewPro2Device.TimeoutMode>(v => v switch
                {
                    WireViewPro2Device.TimeoutMode.TimeoutModeCycle => "Cycle screens",
                    WireViewPro2Device.TimeoutMode.TimeoutModeSleep => "Sleep",
                    _ => "Static",
                }, current?.TimeoutMode),
                Screens = DeviceConfigModel.Screens.Select(s => new ConfigOption((int)s.Value, s.Name, s.Name)).ToList()
                    .Concat(current?.DefaultScreen is { } d && DeviceConfigModel.Screens.All(s => (int)s.Value != d)
                        ? [new ConfigOption(d, d.ToString(CultureInfo.InvariantCulture), $"Unknown ({d})")] : Array.Empty<ConfigOption>())
                    .ToList(),
                Averaging = avg,
                Faults = DeviceConfigModel.AlarmFaults.Select(f => new ConfigOption((int)f.Bit, f.Key, f.Label)).ToList(),
            };
        }

        /// <summary>"22 ms", "1.4 s", as the app shows averaging windows.</summary>
        public static string AveragingLabel(WireViewPro2Device.AVG avg)
        {
            string name = avg.ToString();
            if (name.StartsWith("AVG_", StringComparison.Ordinal) && name.EndsWith("MS", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(4, name.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out int ms))
                return ms < 1000
                    ? ms.ToString(CultureInfo.InvariantCulture) + " ms"
                    : (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            return "Unknown (" + ((int)avg).ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
