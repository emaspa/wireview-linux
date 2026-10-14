using System.Security.Cryptography;

namespace WireView2.Services;

/// <summary>
/// WireView Mini's local command key: a random secret in <c>mini.key</c>
/// next to settings.json, readable by its owner only. WireView Mini creates it at
/// start and accepts it, for commands from this machine only, in addition to the
/// network secret. The GUI signs its commands to WireView Mini with it, so the serial
/// handover and device writes work through WireView Mini with no network secret set.
/// </summary>
public static class MiniKey
{
    /// <summary>Full path of the key file.</summary>
    public static string KeyPath =>
        Path.Combine(Path.GetDirectoryName(AppSettings.GetSettingsPath())!, "mini.key");

    /// <summary>The key, or null when there is none (no mini ever ran).</summary>
    public static string? Read()
    {
        try
        {
            string path = KeyPath;
            if (!File.Exists(path)) return null;
            string key = File.ReadAllText(path).Trim();
            return key.Length >= 32 ? key : null;
        }
        catch { return null; }
    }

    /// <summary>Returns the existing key, or writes a new one (owner-only file).</summary>
    public static string LoadOrCreate()
    {
        if (Read() is { } existing) return existing;
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        AppSettings.WriteOwnerOnly(KeyPath, key + "\n");
        return key;
    }
}
