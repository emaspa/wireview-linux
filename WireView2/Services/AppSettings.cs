using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WireView2.Services;

public class AppSettings
{
    public enum BackgroundColorMode
    {
        Auto,
        Black,
        White
    }

    public enum ThemeMode
    {
        Auto,
        Light,
        Dark
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
    public int MonitoringXWindowSeconds { get; set; } = 30;

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

    public static void Reload()
    {
        lock (Sync)
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
            {
                Current = new AppSettings();
                return;
            }
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                          ?? new AppSettings();
            }
            catch
            {
                Current = new AppSettings();
            }
        }
    }

    public static void SaveCurrent()
    {
        lock (Sync)
        {
            string path = GetSettingsPath();
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
        }
        Saved?.Invoke(Current, EventArgs.Empty);
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
