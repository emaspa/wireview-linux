using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WireView2.Device;

namespace WireView2.Services;

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

public static class DeviceProfileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static string GetProfilesDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PowerMonitor", "profiles");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<string> ListProfiles()
    {
        string dir = GetProfilesDir();
        return Directory.GetFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void SaveProfile(DeviceProfile profile)
    {
        string path = Path.Combine(GetProfilesDir(), SanitizeName(profile.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOpts));
    }

    public static DeviceProfile? LoadProfile(string name)
    {
        string path = Path.Combine(GetProfilesDir(), SanitizeName(name) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void DeleteProfile(string name)
    {
        string path = Path.Combine(GetProfilesDir(), SanitizeName(name) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
