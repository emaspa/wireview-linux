using System.Runtime.InteropServices;

namespace WireView2.Device;

public static class DeviceLogParser
{
    // Firmware constants / layout assumptions
    private const int SENSOR_POWER_NUM = 6;
    private const int SENSOR_TS_NUM = 4;

    // Flash layout assumptions (matches firmware)
    private const int SpiFlashSectorSizeBytes = 4096; // 4KB
    private const int SpiFlashPageSizeBytes = 256;    // page read size / alignment

    public enum ENTRY_TYPE : byte
    {
        ENTRY_TYPE_MCU_TICK = 0x00,
        ENTRY_TYPE_SYSTEM_TIME = 0x01,
        ENTRY_TYPE_POWER_ON = 0x02,
        ENTRY_TYPE_EMPTY = 0x03
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DATALOGGER_Entry
    {
        public uint Data; // Type:2 + Timestamp:30
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SENSOR_TS_NUM)]
        public byte[] Ts; // Temperatures
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SENSOR_POWER_NUM)]
        public byte[] Voltage; // Voltage in 100 mV
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SENSOR_POWER_NUM)]
        public byte[] Current; // Current in 100 mA
        public byte HpwrSense;
    }

    // Decode helpers for the union bitfields
    public static ENTRY_TYPE DecodeType(uint data) => (ENTRY_TYPE)(data & 0b11u);
    public static uint DecodeTimestamp30(uint data) => (data >> 2) & 0x3FFF_FFFFu;

    /// <summary>Size of one <see cref="DATALOGGER_Entry"/> in the flash (packed):
    /// Data (4), Ts (4), Voltage (6), Current (6), HpwrSense (1).</summary>
    public const int EntrySize = 4 + SENSOR_TS_NUM + SENSOR_POWER_NUM + SENSOR_POWER_NUM + 1;

    /// <summary>Byte offsets of the fields in an entry's <see cref="EntrySize"/> bytes.</summary>
    public const int TsOffset = 4, VoltageOffset = 8, CurrentOffset = 14, HpwrSenseOffset = 20;

    /// <summary>Called by <see cref="Scan"/> with the <see cref="EntrySize"/> bytes of each entry it keeps.</summary>
    public delegate void EntryVisitor(ReadOnlySpan<byte> entry);

    public static IReadOnlyList<DATALOGGER_Entry> Parse(ReadOnlySpan<byte> data)
    {
        int entrySizeBytes = Marshal.SizeOf<DATALOGGER_Entry>();
        if (entrySizeBytes <= 0)
            return Array.Empty<DATALOGGER_Entry>();

        var results = new List<DATALOGGER_Entry>(capacity: Math.Max(1024, data.Length / entrySizeBytes));
        Scan(data, entry => results.Add(WireViewPro2Device.BytesToStruct<DATALOGGER_Entry>(entry.ToArray())));
        return results;
    }

    /// <summary>The walk <see cref="Parse"/> makes over the log, without decoding:
    /// hands every entry it keeps (power-on markers and plausible samples, in flash
    /// order) to <paramref name="visit"/> as raw bytes. WireView Mini reads the log
    /// through this, so a full log costs no object per entry.</summary>
    public static void Scan(ReadOnlySpan<byte> data, EntryVisitor visit)
    {
        const int entrySizeBytes = EntrySize;
        if (data.Length < entrySizeBytes)
            return;

        int entriesPerSector = SpiFlashSectorSizeBytes / entrySizeBytes;
        if (entriesPerSector <= 0)
            return;

        int totalSectors = data.Length / SpiFlashSectorSizeBytes;
        int parseBytes = totalSectors * SpiFlashSectorSizeBytes;

        bool firstEntryFound = false;
        int emptyCount = 0;

        for (int sector = 0; sector < totalSectors; sector++)
        {
            int sectorBase = sector * SpiFlashSectorSizeBytes;

            for (int index = 0; index < entriesPerSector;)
            {
                int offset = sectorBase + (index * entrySizeBytes);
                if (offset + entrySizeBytes > parseBytes)
                    break;

                // Re-add: if near page end, jump to next page and align so we start at the next full entry.
                // This mirrors the old heuristic used to avoid decoding entries spanning 256B boundaries
                // (common when reads are performed in page-sized chunks).
                if (firstEntryFound && (offset & (SpiFlashPageSizeBytes - 1)) > (SpiFlashPageSizeBytes - entrySizeBytes))
                {
                    int remainingInPage = SpiFlashPageSizeBytes - (offset & (SpiFlashPageSizeBytes - 1));
                    offset += remainingInPage; // go to next page boundary

                    int entryMisalign = offset % entrySizeBytes;
                    if (entryMisalign != 0)
                        offset += entrySizeBytes - entryMisalign;

                    // Convert the new offset back into a sector-local index (keep firmware-style Sector/Index iteration)
                    int newIndex = (offset - sectorBase) / entrySizeBytes;
                    if (newIndex <= index)
                        newIndex = index + 1; // safety: always make progress

                    index = newIndex;
                    continue;
                }

                ReadOnlySpan<byte> entryBytes = data.Slice(offset, entrySizeBytes);

                uint rawData = ReadU32LE(entryBytes, 0);

                // Firmware uses erased flash == 0xFFFFFFFF as "empty"
                if (rawData == 0xFFFF_FFFFu)
                {
                    if (firstEntryFound)
                    {
                        emptyCount++;
                        if (emptyCount >= 32)
                            return;
                    }

                    index++;
                    continue;
                }

                var type = DecodeType(rawData);

                emptyCount = 0;

                switch (type)
                {
                    // POWER_ON entries are power-cycle boundary markers: their sensor
                    // payload is not a valid sample (so no sanity checks apply), but
                    // the consumer needs them to split the log into cycles.
                    case ENTRY_TYPE.ENTRY_TYPE_POWER_ON:
                    {
                        visit(entryBytes);
                        firstEntryFound = true;
                        index++;
                        break;
                    }
                    case ENTRY_TYPE.ENTRY_TYPE_MCU_TICK:
                    {
                        if (entryBytes[HpwrSenseOffset] > 3)
                        {
                            index++;
                            break;
                        }
                        // Upstream 1.0.7 plausibility gate: the six voltage channels
                        // (100 mV units) must sum to 6..90 V, else the row is noise
                        // from a partially written / corrupted flash page.
                        int voltageSum = 0;
                        for (int v = 0; v < SENSOR_POWER_NUM; v++)
                            voltageSum += entryBytes[VoltageOffset + v];
                        if (voltageSum <= 60 || voltageSum >= 900)
                        {
                            index++;
                            break;
                        }
                        visit(entryBytes);
                        firstEntryFound = true;
                        index++;
                        break;
                    }
                    default:
                        index++;
                        break;
                }
            }
        }
    }

    /// <summary>The Data word (type and timestamp) of an entry's raw bytes.</summary>
    public static uint ReadData(ReadOnlySpan<byte> entry) => ReadU32LE(entry, 0);

    private static uint ReadU32LE(ReadOnlySpan<byte> s, int o)
        => (uint)(s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24));
}