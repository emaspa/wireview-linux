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

    public static IReadOnlyList<DATALOGGER_Entry> Parse(ReadOnlySpan<byte> data)
    {
        int entrySizeBytes = Marshal.SizeOf<DATALOGGER_Entry>();
        if (entrySizeBytes <= 0)
            return Array.Empty<DATALOGGER_Entry>();

        var results = new List<DATALOGGER_Entry>(capacity: Math.Max(1024, data.Length / entrySizeBytes));
        if (data.Length < entrySizeBytes)
            return results;

        int entriesPerSector = SpiFlashSectorSizeBytes / entrySizeBytes;
        if (entriesPerSector <= 0)
            return results;

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
                            return results;
                    }

                    index++;
                    continue;
                }

                var type = DecodeType(rawData);

                emptyCount = 0;

                switch (type)
                {
                    case ENTRY_TYPE.ENTRY_TYPE_MCU_TICK:
                    case ENTRY_TYPE.ENTRY_TYPE_POWER_ON:
                    {
                        var entry = WireViewPro2Device.BytesToStruct<DATALOGGER_Entry>(entryBytes.ToArray());
                        if (entry.HpwrSense > 3)
                        {
                            index++;
                            break;
                        }
                        results.Add(entry);
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

        return results;
    }

    private static uint ReadU32LE(ReadOnlySpan<byte> s, int o)
        => (uint)(s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24));
}