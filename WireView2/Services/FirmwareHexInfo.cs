using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WireView2.Services;

/// <summary>Reads the firmware version and build string out of a bundled Intel-HEX
/// firmware image (TG-WV-PRO2-FW.hex). The firmware's BuildStruct sits at a fixed
/// offset from the image's lowest address: version byte at +194 (192+2), a 32-byte
/// NUL-terminated ASCII build string at +227 (192+35). Matches the upstream 1.0.7
/// Windows client so version comparisons agree across ports.</summary>
public static class FirmwareHexInfo
{
    private const uint BuildStructOffset = 192;
    private const uint VersionOffsetInBuildStruct = 2;
    private const uint BuildInfoOffsetInBuildStruct = 35;
    private const int BuildInfoLength = 32;

    public static bool TryRead(string hexPath, out int firmwareVersion, out string? buildString,
        out string? error)
    {
        firmwareVersion = 0;
        buildString = null;
        try
        {
            var mem = ParseToMemoryMap(hexPath);
            if (mem.Count == 0)
            {
                error = "HEX file does not contain data records.";
                return false;
            }
            error = null;

            uint imageBase = mem.Keys.Min();
            if (!mem.TryGetValue(imageBase + BuildStructOffset + VersionOffsetInBuildStruct, out byte version))
            {
                error = "BuildStruct firmware version byte not found.";
                return false;
            }

            firmwareVersion = version;

            uint buildInfoBase = imageBase + BuildStructOffset + BuildInfoOffsetInBuildStruct;
            byte[] info = new byte[BuildInfoLength];
            bool complete = true;
            for (int i = 0; i < BuildInfoLength; i++)
            {
                if (!mem.TryGetValue(buildInfoBase + (uint)i, out info[i])) { complete = false; break; }
            }
            if (complete)
            {
                int nul = Array.IndexOf(info, (byte)0);
                buildString = Encoding.ASCII.GetString(info, 0, nul >= 0 ? nul : BuildInfoLength).Trim();
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Converts the Intel-HEX image to a flat binary suitable for dfu-util,
    /// padding any gaps with 0xFF (erased-flash value).</summary>
    public static bool TryReadImage(string hexPath, out uint baseAddress, out byte[] image,
        out string? error)
    {
        baseAddress = 0;
        image = Array.Empty<byte>();
        try
        {
            var mem = ParseToMemoryMap(hexPath);
            if (mem.Count == 0)
            {
                error = "HEX file does not contain data records.";
                return false;
            }

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
