using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Media;
using WireView2.Device;

namespace WireView2.Services;

/// <summary>Persistence for device display themes (.wv2t): JSON with the four ARGB
/// theme colors, display inversion, bitmap slot ids, and optionally the custom
/// background image as Base64-encoded RGB565. File format is shared with the
/// upstream Windows client (Version 1).</summary>
internal static class ThemeFile
{
    internal sealed class ThemeDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public uint PrimaryColor { get; set; }
        public uint SecondaryColor { get; set; }
        public uint HighlightColor { get; set; }
        public uint BackgroundColor { get; set; }
        public bool DisplayInversion { get; set; }
        public byte BackgroundBitmapId { get; set; }
        public byte FanBitmapId { get; set; }
        public string? BackgroundImageBase64 { get; set; }
        // Linux-port extension, written by the theme asset backup: the two fan
        // frames exactly as read from the device (RGB565, device layout). Readers
        // that do not know them (upstream's client) ignore unknown members.
        public string? FanFrame1Base64 { get; set; }
        public string? FanFrame2Base64 { get; set; }
    }

    private const int CurrentVersion = 1;

    internal static async Task SaveAsync(string filePath, Color primary, Color secondary,
        Color highlight, Color background, bool inversion,
        WireViewPro2Device.THEME_BACKGROUND backgroundBitmap, WireViewPro2Device.THEME_FAN fanBitmap,
        byte[]? backgroundRgb565)
    {
        var doc = new ThemeDocument
        {
            PrimaryColor = ToArgb(primary),
            SecondaryColor = ToArgb(secondary),
            HighlightColor = ToArgb(highlight),
            BackgroundColor = ToArgb(background),
            DisplayInversion = inversion,
            BackgroundBitmapId = (byte)backgroundBitmap,
            FanBitmapId = (byte)fanBitmap,
            BackgroundImageBase64 = backgroundRgb565 == null ? null : Convert.ToBase64String(backgroundRgb565),
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(fs, doc, options).ConfigureAwait(false);
    }

    /// <summary>Saves a backup of theme assets read from the device: the stored
    /// colors, the background slot and both frames of its fan, byte for byte.</summary>
    internal static async Task SaveBackupAsync(string filePath, WireViewPro2Device.UiConfigStructV2 ui,
        WireViewPro2Device.THEME_BACKGROUND backgroundSlot, WireViewPro2Device.THEME_FAN fanSlot,
        byte[] backgroundRgb565, byte[] fanFrame1Rgb565, byte[] fanFrame2Rgb565)
    {
        var doc = new ThemeDocument
        {
            PrimaryColor = ui.PrimaryColor,
            SecondaryColor = ui.SecondaryColor,
            HighlightColor = ui.HighlightColor,
            BackgroundColor = ui.BackgroundColor,
            DisplayInversion = ui.DisplayInversion == WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_ON,
            BackgroundBitmapId = (byte)backgroundSlot,
            FanBitmapId = (byte)fanSlot,
            BackgroundImageBase64 = Convert.ToBase64String(backgroundRgb565),
            FanFrame1Base64 = Convert.ToBase64String(fanFrame1Rgb565),
            FanFrame2Base64 = Convert.ToBase64String(fanFrame2Rgb565),
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(fs, doc, options).ConfigureAwait(false);
    }

    /// <summary>Both fan frames when the file carries them (a backup), else null.</summary>
    internal static (byte[] Frame1, byte[] Frame2)? TryDecodeFanFrames(ThemeDocument doc, int expectedBytes)
    {
        if (string.IsNullOrWhiteSpace(doc.FanFrame1Base64) || string.IsNullOrWhiteSpace(doc.FanFrame2Base64))
            return null;
        try
        {
            var f1 = Convert.FromBase64String(doc.FanFrame1Base64);
            var f2 = Convert.FromBase64String(doc.FanFrame2Base64);
            if (f1.Length != expectedBytes || f2.Length != expectedBytes)
                throw new InvalidDataException("Theme file fan frames have an unexpected size.");
            return (f1, f2);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Theme file contains invalid fan frame data.", ex);
        }
    }

    internal static async Task<ThemeDocument> LoadAsync(string filePath)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var doc = await JsonSerializer.DeserializeAsync<ThemeDocument>(fs).ConfigureAwait(false)
                  ?? throw new InvalidDataException("Invalid theme file.");
        if (doc.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported theme file version: {doc.Version}.");
        return doc;
    }

    internal static byte[]? TryDecodeBackgroundRgb565(ThemeDocument doc, int expectedBytes)
    {
        if (string.IsNullOrWhiteSpace(doc.BackgroundImageBase64))
            return null;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(doc.BackgroundImageBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Theme file contains invalid background image data.", ex);
        }
        if (bytes.Length != expectedBytes)
            throw new InvalidDataException("Theme file background image has unexpected size.");
        return bytes;
    }

    private static uint ToArgb(Color c) =>
        (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
}
