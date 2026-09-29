using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WireView2.Device;

namespace WireView2.Services;

/// <summary>Identity of a firmware image: the BuildStruct's vendor/product id,
/// version and build string ("TG-WV-PRO2-FW_20260902_0741").</summary>
public sealed record FirmwareImageInfo(byte VendorId, byte ProductId, int Version, string? BuildString)
{
    /// <summary>The build string's yyyyMMdd_HHmm timestamp, null when absent.</summary>
    public DateTime? BuildTime => FirmwareHexInfo.TryParseBuildTimestamp(BuildString);
}

/// <summary>Reads the identity out of a bundled Intel-HEX firmware image
/// (TG-WV-PRO2-FW.hex). The firmware's BuildStruct sits at a fixed offset from the
/// image's lowest address: vendor id at +192, product id at +193, version byte at
/// +194, a 32-byte NUL-terminated ASCII build string at +227 (192+35). Matches the
/// upstream 1.0.8 Windows client so comparisons agree across ports.</summary>
public static class FirmwareHexInfo
{
    private const uint BuildStructOffset = 192;
    private const uint VendorIdOffsetInBuildStruct = 0;
    private const uint ProductIdOffsetInBuildStruct = 1;
    private const uint VersionOffsetInBuildStruct = 2;
    private const uint BuildInfoOffsetInBuildStruct = 35;
    private const int BuildInfoLength = 32;

    // Upstream's BuildTimestampRegex: "_yyyyMMdd_HHmm" not followed by a digit.
    private static readonly Regex BuildTimestampRegex =
        new(@"_(\d{8})_(\d{4})(?!\d)", RegexOptions.CultureInvariant);

    /// <summary>Parses the build timestamp out of a firmware build string such as
    /// "TG-WV-PRO2-FW_20260902_0741" (upstream's TryParseBuildTimestamp).</summary>
    public static DateTime? TryParseBuildTimestamp(string? buildString)
    {
        if (string.IsNullOrWhiteSpace(buildString)) return null;
        var m = BuildTimestampRegex.Match(buildString);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    public static bool TryRead(string hexPath, out int firmwareVersion, out string? buildString,
        out string? error)
    {
        bool ok = TryReadInfo(hexPath, out var info, out error);
        firmwareVersion = info?.Version ?? 0;
        buildString = info?.BuildString;
        return ok;
    }

    public static bool TryReadInfo(string hexPath, out FirmwareImageInfo? info, out string? error)
    {
        info = null;
        try
        {
            var mem = ParseToMemoryMap(hexPath);
            if (mem.Count == 0)
            {
                error = "HEX file does not contain data records.";
                return false;
            }
            return TryGetInfo(mem, out info, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryGetInfo(Dictionary<uint, byte> mem, out FirmwareImageInfo? info, out string? error)
    {
        info = null;
        uint buildStruct = mem.Keys.Min() + BuildStructOffset;
        if (!mem.TryGetValue(buildStruct + VersionOffsetInBuildStruct, out byte version))
        {
            error = "BuildStruct firmware version byte not found.";
            return false;
        }
        if (!mem.TryGetValue(buildStruct + VendorIdOffsetInBuildStruct, out byte vendorId)
            || !mem.TryGetValue(buildStruct + ProductIdOffsetInBuildStruct, out byte productId))
        {
            error = "BuildStruct vendor/product id not found.";
            return false;
        }

        string? buildString = null;
        uint buildInfoBase = buildStruct + BuildInfoOffsetInBuildStruct;
        byte[] text = new byte[BuildInfoLength];
        bool complete = true;
        for (int i = 0; i < BuildInfoLength; i++)
        {
            if (!mem.TryGetValue(buildInfoBase + (uint)i, out text[i])) { complete = false; break; }
        }
        if (complete)
        {
            int nul = Array.IndexOf(text, (byte)0);
            buildString = Encoding.ASCII.GetString(text, 0, nul >= 0 ? nul : BuildInfoLength).Trim();
        }
        info = new FirmwareImageInfo(vendorId, productId, version, buildString);
        error = null;
        return true;
    }

    /// <summary>Converts the Intel-HEX image to a flat binary suitable for dfu-util,
    /// padding any gaps with 0xFF (erased-flash value).</summary>
    public static bool TryReadImage(string hexPath, out uint baseAddress, out byte[] image,
        out string? error) => TryReadImage(hexPath, out baseAddress, out image, out _, out error);

    /// <summary><see cref="TryReadImage(string, out uint, out byte[], out string?)"/>
    /// plus the identity parsed from the same bytes, so a flash is gated on exactly
    /// the image it writes.</summary>
    public static bool TryReadImage(string hexPath, out uint baseAddress, out byte[] image,
        out FirmwareImageInfo? info, out string? error)
    {
        baseAddress = 0;
        image = Array.Empty<byte>();
        info = null;
        try
        {
            var mem = ParseToMemoryMap(hexPath);
            if (mem.Count == 0)
            {
                error = "HEX file does not contain data records.";
                return false;
            }
            if (!TryGetInfo(mem, out info, out error))
                return false;

            uint min = mem.Keys.Min(), max = mem.Keys.Max();
            long size = (long)max - min + 1;
            if (size > 4 * 1024 * 1024)
            {
                error = $"Firmware image is implausibly large ({size} bytes).";
                return false;
            }

            var buffer = new byte[size];
            Array.Fill(buffer, (byte)0xFF);
            foreach (var kv in mem)
                buffer[kv.Key - min] = kv.Value;

            baseAddress = min;
            image = buffer;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<uint, byte> ParseToMemoryMap(string hexPath)
    {
        var mem = new Dictionary<uint, byte>();
        uint upperLinear = 0, upperSegment = 0;
        bool haveLinear = false;

        foreach (string raw in File.ReadLines(hexPath))
        {
            string line = raw.Trim();
            if (line.Length < 11 || !line.StartsWith(':'))
                continue;

            byte count = ParseHexByte(line.AsSpan(1, 2));
            ushort addr = ParseHexUInt16(line.AsSpan(3, 4));
            byte recordType = ParseHexByte(line.AsSpan(7, 2));

            if (recordType == 0) // data
            {
                uint baseAddr = haveLinear ? upperLinear << 16 : upperSegment;
                for (int i = 0; i < count; i++)
                    mem[baseAddr + addr + (uint)i] = ParseHexByte(line.AsSpan(9 + i * 2, 2));
            }
            else if (recordType == 1) // EOF
            {
                break;
            }
            else if (recordType == 2) // extended segment address
            {
                upperSegment = (uint)(ParseHexUInt16(line.AsSpan(9, 4)) << 4);
                haveLinear = false;
            }
            else if (recordType == 4) // extended linear address
            {
                upperLinear = ParseHexUInt16(line.AsSpan(9, 4));
                haveLinear = true;
            }
        }
        return mem;
    }

    private static byte ParseHexByte(ReadOnlySpan<char> hex) =>
        byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static ushort ParseHexUInt16(ReadOnlySpan<char> hex) =>
        ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

/// <summary>Whether a firmware image may go onto a device (upstream 1.0.8's
/// FirmwareProductIdAliases / CompareBundledFirmwareToDevice, plus the port's
/// Noctua Edition floor).</summary>
public static class FirmwareCompatibility
{
    /// <summary>Device (vendor, product) served by another product's image: the
    /// Noctua Edition runs the Pro II firmware.</summary>
    private static readonly Dictionary<(byte VendorId, byte ProductId), byte> ProductAliases = new()
    {
        [(WireViewEditions.ThermalGrizzlyVendorId, WireViewEditions.Pro2NoctuaProductId)] = WireViewEditions.Pro2ProductId,
    };

    /// <summary>First firmware build with Noctua Edition support
    /// (TG-WV-PRO2-FW_20260902_0741). An older image never goes onto product 6.</summary>
    public static readonly DateTime NoctuaMinimumBuildDate = new(2026, 9, 2);

    public static byte FirmwareProductId(byte vendorId, byte productId) =>
        ProductAliases.TryGetValue((vendorId, productId), out byte p) ? p : productId;

    /// <summary>Same vendor, and the image's product is the device's (aliased) one.</summary>
    public static bool ProductMatches(FirmwareImageInfo image, byte deviceVendorId, byte deviceProductId) =>
        deviceVendorId != 0
        && image.VendorId == deviceVendorId
        && image.ProductId == FirmwareProductId(deviceVendorId, deviceProductId);

    /// <summary>Version first, then build timestamp. Positive when the image is
    /// newer, negative when older, null when either side is unknown.</summary>
    public static int? Compare(FirmwareImageInfo image, int? deviceVersion, DateTime? deviceBuildTime)
    {
        if (!deviceVersion.HasValue) return null;
        int byVersion = image.Version.CompareTo(deviceVersion.Value);
        if (byVersion != 0) return byVersion;
        var imageTime = image.BuildTime;
        if (!imageTime.HasValue || !deviceBuildTime.HasValue) return null;
        return imageTime.Value.CompareTo(deviceBuildTime.Value);
    }

    /// <summary>True when the image predates Noctua Edition support and the device
    /// is one. An image without a parsable build date counts as too old.</summary>
    public static bool IsBelowNoctuaFloor(FirmwareImageInfo image, byte deviceVendorId, byte deviceProductId) =>
        WireViewEditions.FromIds(deviceVendorId, deviceProductId) == WireViewEdition.Pro2Noctua
        && (image.BuildTime is not { } t || t.Date < NoctuaMinimumBuildDate);

    public static FlashVerdict Evaluate(FirmwareImageInfo image, byte deviceVendorId, byte deviceProductId,
        int? deviceVersion, DateTime? deviceBuildTime)
    {
        if (!ProductMatches(image, deviceVendorId, deviceProductId)) return FlashVerdict.ProductMismatch;
        if (IsBelowNoctuaFloor(image, deviceVendorId, deviceProductId)) return FlashVerdict.TooOldForNoctua;
        int? cmp = Compare(image, deviceVersion, deviceBuildTime);
        if (cmp < 0) return FlashVerdict.Downgrade;
        if (cmp > 0) return FlashVerdict.Newer;
        return cmp == 0 ? FlashVerdict.Same : FlashVerdict.Unknown;
    }
}

/// <summary>Outcome of <see cref="FirmwareCompatibility.Evaluate"/>, in the gate
/// order `wireviewctl flash` uses: product (with the alias), then the Noctua date
/// floor, then version and build date. ProductMismatch and TooOldForNoctua refuse
/// the flash; Downgrade and Same need an explicit override; Unknown (equal version,
/// a build date missing) goes ahead like upstream.</summary>
public enum FlashVerdict
{
    Newer,
    Same,
    Unknown,
    Downgrade,
    ProductMismatch,
    TooOldForNoctua,
}
