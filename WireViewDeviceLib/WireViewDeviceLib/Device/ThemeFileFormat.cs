using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireView2.Device
{
    /// <summary>A device display theme file (.wv2t), format shared with the
    /// upstream Windows client (Version 1): the four ARGB theme colours, display
    /// inversion, the bitmap slot ids and optionally the custom background image as
    /// base64 RGB565 (device layout). Property names and order are the file format.</summary>
    public sealed class ThemeDocument
    {
        public int Version { get; set; } = ThemeFileFormat.CurrentVersion;
        public uint PrimaryColor { get; set; }
        public uint SecondaryColor { get; set; }
        public uint HighlightColor { get; set; }
        public uint BackgroundColor { get; set; }
        public bool DisplayInversion { get; set; }
        public byte BackgroundBitmapId { get; set; }
        public byte FanBitmapId { get; set; }
        public string? BackgroundImageBase64 { get; set; }
        // Linux-port extension, written by 1.3.0.0's theme asset backup: the two
        // fan frames exactly as read from the device (RGB565, device layout). Readers
        // that do not know them (upstream's client) ignore unknown members.
        public string? FanFrame1Base64 { get; set; }
        public string? FanFrame2Base64 { get; set; }
    }

    /// <summary>
    /// Reads and writes .wv2t theme files without reflection, so WireView Mini's
    /// NativeAOT build can use it; the app's ThemeFile is built on it. Writing gives
    /// the bytes the app always wrote (indented, nulls left out); reading is
    /// System.Text.Json's defaults, as before (case-sensitive names, unknown
    /// members ignored).
    /// </summary>
    public static class ThemeFileFormat
    {
        public const int CurrentVersion = 1;

        /// <summary>A file's size limit for readers that take one from a network
        /// peer: the background image is 145 KB of base64, the fan frames 14 KB each.</summary>
        public const int MaxFileBytes = 256 * 1024;

        public static ThemeDocument Create(DeviceThemeValues theme, byte[]? backgroundRgb565) => new()
        {
            PrimaryColor = theme.PrimaryColor,
            SecondaryColor = theme.SecondaryColor,
            HighlightColor = theme.HighlightColor,
            BackgroundColor = theme.BackgroundColor,
            DisplayInversion = theme.DisplayInversion,
            BackgroundBitmapId = theme.BackgroundBitmapId,
            FanBitmapId = theme.FanBitmapId,
            BackgroundImageBase64 = backgroundRgb565 == null ? null : Convert.ToBase64String(backgroundRgb565),
        };

        public static DeviceThemeValues Values(ThemeDocument doc) => new(
            doc.PrimaryColor, doc.SecondaryColor, doc.HighlightColor, doc.BackgroundColor,
            doc.DisplayInversion, doc.BackgroundBitmapId, doc.FanBitmapId);

        public static byte[] Serialize(ThemeDocument doc) =>
            JsonSerializer.SerializeToUtf8Bytes(doc, ThemeFileJsonContext.Default.ThemeDocument);

        public static Task SerializeAsync(Stream stream, ThemeDocument doc) =>
            JsonSerializer.SerializeAsync(stream, doc, ThemeFileJsonContext.Default.ThemeDocument);

        /// <summary>Parses a theme file. Throws <see cref="JsonException"/> for text
        /// that is not one and <see cref="InvalidDataException"/> for another version.</summary>
        public static ThemeDocument Parse(ReadOnlySpan<byte> utf8Json) =>
            Checked(JsonSerializer.Deserialize(utf8Json, ThemeFileJsonContext.Default.ThemeDocument));

        public static ThemeDocument Parse(string json) =>
            Checked(JsonSerializer.Deserialize(json, ThemeFileJsonContext.Default.ThemeDocument));

        public static async Task<ThemeDocument> LoadAsync(Stream stream) =>
            Checked(await JsonSerializer.DeserializeAsync(stream, ThemeFileJsonContext.Default.ThemeDocument).ConfigureAwait(false));

        private static ThemeDocument Checked(ThemeDocument? doc)
        {
            if (doc == null) throw new InvalidDataException("Invalid theme file.");
            if (doc.Version != CurrentVersion)
                throw new InvalidDataException($"Unsupported theme file version: {doc.Version}.");
            return doc;
        }

        /// <summary>The background image when the file has one, else null.</summary>
        public static byte[]? TryDecodeBackgroundRgb565(ThemeDocument doc, int expectedBytes)
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

        /// <summary>Both fan frames when the file carries them (a backup), else null.</summary>
        public static (byte[] Frame1, byte[] Frame2)? TryDecodeFanFrames(ThemeDocument doc, int expectedBytes)
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
    }

    /// <summary>The .wv2t serializer: the options the app always wrote with
    /// (indented, nulls left out), PascalCase names as declared.</summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ThemeDocument))]
    internal sealed partial class ThemeFileJsonContext : JsonSerializerContext
    {
    }
}
