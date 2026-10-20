using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireView2.Services;

/// <summary>
/// The user's settings, persisted as <c>settings.json</c> in the
/// <c>PowerMonitor</c> folder under the user's application-data directory
/// (<c>~/.config/PowerMonitor</c> on Linux, <c>%APPDATA%\PowerMonitor</c> on
/// Windows). Shared by the GUI and the WireView Mini: both edit and save it,
/// and WireView Mini follows the GUI's changes with <see cref="ReloadIfChanged"/>.
/// </summary>
public class AppSettings
{
    public enum BackgroundColorMode
    {
        Auto,
        Black,
        White
    }

    // Stored as a number: append new modes, never reorder (upstream 1.0.8 added
    // the two Noctua modes the same way, so both apps read each other's files).
    public enum ThemeMode
    {
        Auto,
        Light,
        Dark,
        NoctuaLight,
        NoctuaDark
    }

    public enum StartupScreen
    {
        Main,
        Simple,
        Current,
        Temperature,
        Status,
        NoChange
    }

    public enum PortSelectionMode
    {
        Auto,
        Manual
    }

    public enum NavPaneMode
    {
        Auto,
        Minimal,
        Expanded
    }

    public sealed class MonitoringAxisSettings
    {
        public bool Auto { get; set; } = true;
        public double Min { get; set; }
        public double Max { get; set; } = 100.0;
    }

    public sealed class MonitoringSeriesSettings
    {
        public string Key { get; set; } = string.Empty;
        public string? Color { get; set; }
    }

    private static readonly object Sync = new object();

    public static AppSettings Current { get; private set; } = new AppSettings();

    public bool AutoStart { get; set; }
    public bool LoggingOnStart { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>Draw with the GPU (Avalonia's default backend) instead of the
    /// software renderer. Off by default on Linux: the software path keeps Mesa
    /// out of the process (about 120 MB less resident, 55 MB less proportional)
    /// and costs CPU only on the Monitoring page at fast polling. Windows keeps
    /// the GPU default. Read by Program before Avalonia starts, so it applies at
    /// the next launch; WIREVIEW_RENDER_MODE overrides it.</summary>
    public bool GpuRendering { get; set; } = OperatingSystem.IsWindows();

    public string? ScreensaverFilePath { get; set; }
    public int ScreensaverTimeoutSeconds { get; set; }
    public int ScreensaverFrameDelayMs { get; set; } = 100;

    public string? CsvFilePath { get; set; }
    public int CsvIntervalSeconds { get; set; } = 1;
    public List<string>? CsvItems { get; set; }

    public int DeviceLoggingIntervalSeconds { get; set; } = 5;

    public double BackgroundOpacity { get; set; } = 0.5;
    public BackgroundColorMode BackgroundColorPreference { get; set; }
    public ThemeMode ThemePreference { get; set; }
    public StartupScreen ScreenAfterConnection { get; set; } = StartupScreen.NoChange;

    public PortSelectionMode PortMode { get; set; }
    public string? ForcedComPort { get; set; }

    public int MonitoringUpdateIntervalMs { get; set; } = 1000;
    public bool SoftwareShutdownOnFault { get; set; }

    public NavPaneMode NavPane { get; set; }

    // LAN monitoring: open the network listener that publishes this host's
    // WireView(s) (GET /sensors) and, with a secret, accepts authenticated writes
    // (POST /command). OFF by default — opening a port is opt-in and independent of
    // reading remote hosts.
    public bool PublishEnabled { get; set; } = false;
    public int PublishPort { get; set; } = 9876;
    // Remote hosts to read over the LAN ("host" or "host:port"; port defaults to
    // 9876). Set in Settings as a comma-separated list. No mDNS auto-discovery.
    public List<string>? RemoteHosts { get; set; }

    // Shared HMAC passphrase for authenticated remote writes (POST /command).
    // Empty disables remote writes both ways. Stored plaintext (both ends need
    // the real value to sign and verify).
    public string? NetworkSecret { get; set; }

    // Publisher flood-protection limits.
    public int MaxHttpConnections { get; set; } = 8;
    public int MaxRequestBytes { get; set; } = 8192;
    public int RateLimitPerMinute { get; set; } = 120;

    // Days of audit logs to keep (daily-rotating wireview2-*.log under the
    // settings folder's logs/ subdir). 0 = keep forever.
    public int LogRetentionDays { get; set; } = 14;

    // Tray icon presentation — show a second tray icon with the live total power
    // (plain text), shown as a red icon when the device is disconnected.
    public bool ShowTrayPower { get; set; } = true;
    // Whether the tray power icon includes the unit word under the number.
    public bool ShowTrayPowerUnit { get; set; } = true;

    public int MonitoringXWindowSeconds { get; set; } = 30;

    /// <summary>Minutes of readings WireView Mini keeps in memory for its control
    /// panel's charts and history CSV (one sample a second), 0 for none. Null
    /// (left out of the file, so files written before it existed stay
    /// byte-identical) means <see cref="DefaultHistoryMinutes"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HistoryMinutes { get; set; }

    public const int DefaultHistoryMinutes = 60;
    /// <summary>A day: 86,400 samples, about 8.5 MB.</summary>
    public const int MaxHistoryMinutes = 1440;

    /// <summary><see cref="HistoryMinutes"/> with the default applied, 0 to
    /// <see cref="MaxHistoryMinutes"/>.</summary>
    [JsonIgnore]
    public int EffectiveHistoryMinutes => Math.Clamp(HistoryMinutes ?? DefaultHistoryMinutes, 0, MaxHistoryMinutes);


    public MonitoringAxisSettings MonitoringYV { get; set; } = new MonitoringAxisSettings();
    public MonitoringAxisSettings MonitoringYA { get; set; } = new MonitoringAxisSettings();
    public MonitoringAxisSettings MonitoringYW { get; set; } = new MonitoringAxisSettings();
    public MonitoringAxisSettings MonitoringYC { get; set; } = new MonitoringAxisSettings();

    public List<string>? MonitoringEnabledSeriesKeys { get; set; }
    public List<MonitoringSeriesSettings>? MonitoringSeries { get; set; }

    public static event EventHandler? Saved;

    public static string GetSettingsPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PowerMonitor");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    // Write time of the file the current values were read from (or last saved
    // to); DateTime.MinValue when there was no file.
    private static DateTime _loadedWriteTimeUtc = DateTime.MinValue;

    public static void Reload()
    {
        lock (Sync)
        {
            string path = GetSettingsPath();
            _loadedWriteTimeUtc = WriteTimeUtc(path);
            if (!File.Exists(path))
            {
                Current = new AppSettings();
                return;
            }
            RestrictToOwner(path);
            try
            {
                Current = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsReadContext.Default.AppSettings)
                          ?? new AppSettings();
            }
            catch
            {
                Current = new AppSettings();
            }
            Current.Normalize();
        }
    }

    /// <summary>Reloads <see cref="Current"/> when settings.json changed on disk
    /// since it was last read or saved by this process (compares the file's last
    /// write time). Cheap enough to call on every poll: one stat when nothing
    /// changed. Returns true when the settings were reloaded.</summary>
    public static bool ReloadIfChanged()
    {
        string path = GetSettingsPath();
        DateTime stamp = WriteTimeUtc(path);
        lock (Sync)
        {
            if (stamp == _loadedWriteTimeUtc) return false;
        }
        Reload();
        return true;
    }

    private static DateTime WriteTimeUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>A value this version does not know (a newer app's theme, a
    /// hand-edited file) falls back to the default instead of reaching the UI.</summary>
    private void Normalize()
    {
        if (!Enum.IsDefined(ThemePreference)) ThemePreference = ThemeMode.Auto;
        if (!Enum.IsDefined(BackgroundColorPreference)) BackgroundColorPreference = BackgroundColorMode.Auto;
    }

    public static void SaveCurrent()
    {
        lock (Sync)
        {
            string path = GetSettingsPath();
            string json = JsonSerializer.Serialize(Current, AppSettingsWriteContext.Default.AppSettings);
            WriteOwnerOnly(path, json);
            _loadedWriteTimeUtc = WriteTimeUtc(path);
        }
        Saved?.Invoke(Current, EventArgs.Empty);
    }

    // The file holds the LAN write secret, so only its owner may read it. It is
    // written to a temporary file created with that mode and then renamed over the
    // old one, which also means a crash mid-write cannot leave a truncated file.
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void WriteOwnerOnly(string path, string text)
    {
        string tmp = path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        try
        {
            File.Delete(tmp);
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(text);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Tightens a file written by an older version, which was created
    /// readable by everyone.</summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if ((File.GetUnixFileMode(path) & ~OwnerOnly) != 0)
                File.SetUnixFileMode(path, OwnerOnly);
        }
        catch
        {
            // Not ours to change (read-only mount, foreign owner): keep going.
        }
    }

    public static AppSettings Load()
    {
        Reload();
        return Current;
    }

    public void Save()
    {
        SaveCurrent();
    }
}

// settings.json through source-generated serializers (no reflection, so WireView
// Mini also reads it in a NativeAOT build). Both use the System.Text.Json
// defaults the file has always been written with: property names as declared,
// case-sensitive, nulls written.

/// <summary>Writing: indented, enums as numbers so older app versions can read
/// the file.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsWriteContext : JsonSerializerContext
{
}

/// <summary>Reading: also accepts enum names ("NoctuaDark") besides numbers, one
/// string converter per enum (the generic ones need no reflection). A new enum
/// setting needs its converter in this list.</summary>
[JsonSourceGenerationOptions(Converters =
[
    typeof(JsonStringEnumConverter<AppSettings.BackgroundColorMode>),
    typeof(JsonStringEnumConverter<AppSettings.ThemeMode>),
    typeof(JsonStringEnumConverter<AppSettings.StartupScreen>),
    typeof(JsonStringEnumConverter<AppSettings.PortSelectionMode>),
    typeof(JsonStringEnumConverter<AppSettings.NavPaneMode>),
])]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsReadContext : JsonSerializerContext
{
}
