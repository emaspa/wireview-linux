using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using WireView2.Device;

namespace WireView2.Services;

/// <summary>Persistence for device display themes (.wv2t): JSON with the four ARGB
/// theme colors, display inversion, bitmap slot ids, and optionally the custom
/// background image as Base64-encoded RGB565. File format is shared with the
/// upstream Windows client (Version 1). The document and its reflection-free
/// reader and writer are <see cref="ThemeFileFormat"/> in WireViewDeviceLib,
/// shared with WireView Mini; this adds the Avalonia colors and the file paths.</summary>
internal static class ThemeFile
{
    internal static async Task SaveAsync(string filePath, Color primary, Color secondary,
        Color highlight, Color background, bool inversion,
        WireViewPro2Device.THEME_BACKGROUND backgroundBitmap, WireViewPro2Device.THEME_FAN fanBitmap,
        byte[]? backgroundRgb565)
    {
        var doc = ThemeFileFormat.Create(new DeviceThemeValues(
            ToArgb(primary), ToArgb(secondary), ToArgb(highlight), ToArgb(background),
            inversion, (byte)backgroundBitmap, (byte)fanBitmap), backgroundRgb565);
        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await ThemeFileFormat.SerializeAsync(fs, doc).ConfigureAwait(false);
    }

    /// <summary>Both fan frames when the file carries them (a backup), else null.</summary>
    internal static (byte[] Frame1, byte[] Frame2)? TryDecodeFanFrames(ThemeDocument doc, int expectedBytes) =>
        ThemeFileFormat.TryDecodeFanFrames(doc, expectedBytes);

    internal static async Task<ThemeDocument> LoadAsync(string filePath)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ThemeFileFormat.LoadAsync(fs).ConfigureAwait(false);
    }

    internal static byte[]? TryDecodeBackgroundRgb565(ThemeDocument doc, int expectedBytes) =>
        ThemeFileFormat.TryDecodeBackgroundRgb565(doc, expectedBytes);

    private static uint ToArgb(Color c) =>
        (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
}
