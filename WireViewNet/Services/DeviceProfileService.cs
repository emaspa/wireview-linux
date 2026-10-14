using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WireView2.Device;

namespace WireView2.Services;

/// <summary>
/// A saved device configuration: the editable fields of the app's Device page,
/// in its units. Stored as <c>&lt;name&gt;.json</c> in <see cref="DeviceProfileService.GetProfilesDir"/>,
/// shared by the app and WireView Mini's control panel. The property names and
/// their order are the file format: renaming or reordering one changes it.
/// </summary>
public class DeviceProfile
{
    public string Name { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public int BacklightDuty { get; set; } = 50;

    // Fan
    public int FanMode { get; set; }
    public int FanTempSource { get; set; }
    public int FanDutyMin { get; set; } = 20;
    public int FanDutyMax { get; set; } = 80;
    public double FanTempMinC { get; set; } = 30.0;
    public double FanTempMaxC { get; set; } = 60.0;

    // Display
    public int UiCurrentScale { get; set; }
    public int UiPowerScale { get; set; }
    public int UiTheme { get; set; }
    public int UiRotation { get; set; }
    public int UiTimeoutMode { get; set; }
    public int UiCycleTimeSeconds { get; set; } = 10;
    public int UiTimeoutSeconds { get; set; } = 60;
    public int Averaging { get; set; }

    // Fault masks
    public ushort FaultDisplayEnableMask { get; set; }
    public ushort FaultBuzzerEnableMask { get; set; }
    public ushort FaultSoftPowerEnableMask { get; set; }
    public ushort FaultHardPowerEnableMask { get; set; }

    // Fault thresholds
    public double TsFaultThresholdC { get; set; }
    public int OcpFaultThresholdA { get; set; }
    public double WireOcpFaultThresholdA { get; set; }
    public int OppFaultThresholdW { get; set; }
    public int CurrentImbalanceFaultThresholdPercent { get; set; }
    public int CurrentImbalanceFaultMinLoadA { get; set; }
    public int ShutdownWaitTimeSeconds { get; set; }
    public int LoggingIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// The profile files: the <c>profiles</c> folder in the <c>PowerMonitor</c>
/// folder (<c>~/.config/PowerMonitor/profiles</c> on Linux,
/// <c>%APPDATA%\PowerMonitor\profiles</c> on Windows). Used by the app's Device
/// page and by WireView Mini's control panel, so a profile saved in one shows in
/// the other. JSON goes through <see cref="DeviceProfileJsonContext"/> (no
/// reflection, so it works in Mini's NativeAOT build).
/// </summary>
public static class DeviceProfileService
{
    public static string GetProfilesDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PowerMonitor", "profiles");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Profile names (file names without .json), sorted ignoring case.</summary>
    public static List<string> ListProfiles()
    {
        string dir = GetProfilesDir();
        return Directory.GetFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Writes the profile as <c>&lt;Name&gt;.json</c>, replacing a profile
    /// of that name. Characters a file name cannot hold become '_'.</summary>
    public static void SaveProfile(DeviceProfile profile)
    {
        File.WriteAllText(PathFor(profile.Name), JsonSerializer.Serialize(profile, DeviceProfileJsonContext.Default.DeviceProfile));
    }

    /// <summary>The profile, or null when there is none of that name or the file
    /// cannot be read.</summary>
    public static DeviceProfile? LoadProfile(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), DeviceProfileJsonContext.Default.DeviceProfile);
        }
        catch
        {
            return null;
        }
    }

    public static bool ProfileExists(string name) => File.Exists(PathFor(name));

    /// <summary>Deletes the profile; false when there was none of that name.</summary>
    public static bool DeleteProfile(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static string PathFor(string name) => Path.Combine(GetProfilesDir(), FileName(name) + ".json");

    /// <summary>The name a profile is listed under: <paramref name="name"/> with
    /// every character a file name cannot hold replaced by '_'.</summary>
    public static string FileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}

/// <summary>
/// A profile in <see cref="DeviceConfigModel"/> terms, for WireView Mini's control
/// panel. The mapping is the app's (DeviceViewModel.BuildProfileFromEditor and
/// ApplyProfileToEditor): a profile holds the Device page's editor fields, the
/// fault masks whole, the legacy theme and averaging whatever the layout.
/// </summary>
public static class DeviceProfileMapping
{
    /// <summary>The model fields (camelCase, as the panel names them) a profile
    /// sets on a layout that has every one of them.</summary>
    private static readonly string[] AllFields =
    [
        "friendlyName", "backlightDuty", "fanMode", "fanTempSource", "fanDutyMin", "fanDutyMax",
        "fanTempMinC", "fanTempMaxC", "currentScale", "powerScale", "theme", "displayRotation",
        "timeoutMode", "cycleTimeSeconds", "timeoutSeconds", "averaging", "faultAlarms",
        "tempThresholdC", "ocpThresholdA", "wireOcpThresholdA", "oppThresholdW",
        "imbalanceThresholdPercent", "imbalanceMinLoadA", "shutdownWaitSeconds", "loggingIntervalSeconds",
    ];

    /// <summary>The profile of the editor state <paramref name="m"/>, like the app's
    /// "Save Current as Profile". What the model does not carry comes from the
    /// config it was loaded from (<see cref="DeviceConfigModel.Raw"/>), as the app's
    /// editor holds it: the fault mask bits the panel does not show, the legacy
    /// theme on layout 2 (derived from the background bitmap). Throws <see cref="DeviceConfigException"/> when Raw is missing.</summary>
    public static DeviceProfile FromModel(string name, DeviceConfigModel m)
    {
        if (m.Version is < 0 or > 2) throw new DeviceConfigException($"unknown config layout {m.Version}");
        byte[] raw;
        try { raw = m.RawBytes; }
        catch (FormatException) { throw new DeviceConfigException("raw is not valid base64"); }
        if (raw.Length < WireViewPro2Device.ConfigSizeForVersion(m.Version))
            throw new DeviceConfigException("the edit does not carry the config it was made from (raw); reload it");
        var basis = WireViewPro2Device.DeserializeConfig(m.Version, raw);
        var alarms = m.FaultAlarms ?? new();

        ushort Mask(ushort basisMask, Func<FaultAlarmModel, bool> pick)
        {
            int mask = basisMask;
            foreach (var (bit, key, _) in DeviceConfigModel.AlarmFaults)
            {
                if (alarms.FirstOrDefault(f => f.Fault == key) is not { } a) continue;
                int b = 1 << (int)bit;
                mask = pick(a) ? mask | b : mask & ~b;
            }
            return (ushort)mask;
        }

        return new DeviceProfile
        {
            Name = name,
            FriendlyName = m.FriendlyName ?? string.Empty,
            BacklightDuty = m.BacklightDuty,
            FanMode = m.FanMode,
            FanTempSource = m.FanTempSource,
            FanDutyMin = m.FanDutyMin,
            FanDutyMax = m.FanDutyMax,
            FanTempMinC = m.FanTempMinC,
            FanTempMaxC = m.FanTempMaxC,
            UiCurrentScale = m.CurrentScale,
            UiPowerScale = m.PowerScale,
            UiTheme = m.Theme ?? (int)LegacyTheme(basis.Ui.BackgroundBitmapId),
            UiRotation = m.DisplayRotation,
            UiTimeoutMode = m.TimeoutMode,
            UiCycleTimeSeconds = m.CycleTimeSeconds,
            UiTimeoutSeconds = m.TimeoutSeconds,
            // Layout 0 has no averaging: the app's editor then keeps its initial value.
            Averaging = m.Averaging ?? (int)default(WireViewPro2Device.AVG),
            FaultDisplayEnableMask = Mask(basis.FaultDisplayEnable, a => a.Display),
            FaultBuzzerEnableMask = Mask(basis.FaultBuzzerEnable, a => a.Buzzer),
            FaultSoftPowerEnableMask = Mask(basis.FaultSoftPowerEnable, a => a.SoftOff),
            FaultHardPowerEnableMask = Mask(basis.FaultHardPowerEnable, a => a.HardOff),
            TsFaultThresholdC = m.TempThresholdC,
            OcpFaultThresholdA = m.OcpThresholdA,
            WireOcpFaultThresholdA = m.WireOcpThresholdA,
            OppFaultThresholdW = m.OppThresholdW,
            CurrentImbalanceFaultThresholdPercent = m.ImbalanceThresholdPercent,
            CurrentImbalanceFaultMinLoadA = m.ImbalanceMinLoadA,
            ShutdownWaitTimeSeconds = m.ShutdownWaitSeconds,
            LoggingIntervalSeconds = m.LoggingIntervalSeconds,
        };
    }

    /// <summary>The profile as model values for a device with config layout
    /// <paramref name="version"/> and firmware <paramref name="firmware"/> (null:
    /// unknown, every field is kept). <c>Fields</c> names the model fields the
    /// profile sets; the others (cycle screens, default screen, Raw) are left at
    /// their defaults and are not the profile's. The legacy theme on layout 2 and
    /// averaging on layout 0 are left out (the app writes neither to such a
    /// device). <c>Notes</c> says what was left out or changed: averaging on layout
    /// 0, and an averaging window the firmware does not offer, clamped to the
    /// longest it has as the app does.</summary>
    public static (DeviceConfigModel Values, List<string> Fields, List<string> Notes) ToModel(
        DeviceProfile p, int? version, int? firmware)
    {
        var fields = AllFields.ToList();
        var notes = new List<string>();
        var m = new DeviceConfigModel
        {
            Version = version ?? 2,
            FriendlyName = p.FriendlyName ?? string.Empty,
            BacklightDuty = p.BacklightDuty,
            FanMode = p.FanMode,
            FanTempSource = p.FanTempSource,
            FanDutyMin = p.FanDutyMin,
            FanDutyMax = p.FanDutyMax,
            FanTempMinC = p.FanTempMinC,
            FanTempMaxC = p.FanTempMaxC,
            CurrentScale = p.UiCurrentScale,
            PowerScale = p.UiPowerScale,
            Theme = p.UiTheme,
            DisplayRotation = p.UiRotation,
            TimeoutMode = p.UiTimeoutMode,
            CycleTimeSeconds = p.UiCycleTimeSeconds,
            TimeoutSeconds = p.UiTimeoutSeconds,
            Averaging = version is 0 ? null : ClampAveraging(p.Averaging, firmware, notes),
            TempThresholdC = p.TsFaultThresholdC,
            OcpThresholdA = p.OcpFaultThresholdA,
            WireOcpThresholdA = p.WireOcpFaultThresholdA,
            OppThresholdW = p.OppFaultThresholdW,
            ImbalanceThresholdPercent = p.CurrentImbalanceFaultThresholdPercent,
            ImbalanceMinLoadA = p.CurrentImbalanceFaultMinLoadA,
            ShutdownWaitSeconds = p.ShutdownWaitTimeSeconds,
            LoggingIntervalSeconds = p.LoggingIntervalSeconds,
        };
        foreach (var (bit, key, _) in DeviceConfigModel.AlarmFaults)
        {
            int b = 1 << (int)bit;
            m.FaultAlarms.Add(new FaultAlarmModel
            {
                Fault = key,
                Display = (p.FaultDisplayEnableMask & b) != 0,
                Buzzer = (p.FaultBuzzerEnableMask & b) != 0,
                SoftOff = (p.FaultSoftPowerEnableMask & b) != 0,
                HardOff = (p.FaultHardPowerEnableMask & b) != 0,
            });
        }
        // No note for the theme: every profile carries one (the app derives it from
        // the background on any layout), and on layout 2 the app ignores it silently.
        if (version >= 2)
        {
            m.Theme = null;
            fields.Remove("theme");
        }
        if (version is 0)
        {
            fields.Remove("averaging");
            notes.Add("its averaging is not used on this device (the firmware has no averaging setting)");
        }
        return (m, fields, notes);
    }

    // DeviceViewModel.ClampAveragingForDevice: firmware before 05 offers windows up
    // to 1417 ms, a longer one from a newer device's profile becomes the longest.
    private static int ClampAveraging(int value, int? firmware, List<string> notes)
    {
        if (firmware is not { } fw || fw >= DeviceConfigOptions.ExtendedAveragingMinFirmware) return value;
        int longest = (int)WireViewPro2Device.AVG.AVG_1417MS;
        if (value <= longest) return value;
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"its averaging of {DeviceConfigOptions.AveragingLabel((WireViewPro2Device.AVG)value)} is not on firmware v{fw:00}, set to {DeviceConfigOptions.AveragingLabel(WireViewPro2Device.AVG.AVG_1417MS)}"));
        return longest;
    }

    // The app's editor derives the legacy theme from the background bitmap id
    // (DeviceViewModel.ApplyToEditor), on every layout.
    private static WireViewPro2Device.Theme LegacyTheme(byte backgroundBitmapId) =>
        backgroundBitmapId == 1 ? WireViewPro2Device.Theme.ThemeTg1
        : backgroundBitmapId == 2 ? WireViewPro2Device.Theme.ThemeTg2
        : WireViewPro2Device.Theme.ThemeTg3;
}

/// <summary>Profile files through a source-generated serializer, with the
/// System.Text.Json defaults the app has always written them with (property
/// names as declared, case-sensitive reading), indented.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DeviceProfile))]
internal sealed partial class DeviceProfileJsonContext : JsonSerializerContext
{
}
