using System.Globalization;

namespace WireView2.Device;

/// <summary>
/// The app's CSV columns and value formats for <see cref="DeviceData"/>, shared
/// by the app (the live CSV logger and the Logging page's export) and WireView
/// Mini's control panel, so a file from either reads the same.
/// </summary>
public static class DeviceDataCsv
{
    private static readonly string[] Leading =
    [
        "Timestamp", "Connected", "HW", "FW",
        "SumPowerW", "SumCurrentA",
        "OnboardInC", "OnboardOutC", "Ext1C", "Ext2C",
    ];

    /// <summary>The live logger's columns (CsvLogger): ..., V1, I1, V2, I2, ... V6, I6.</summary>
    public static IReadOnlyList<string> LiveHeaders { get; } =
        [.. Leading, .. Enumerable.Range(1, 6).SelectMany(i => new[] { $"V{i}", $"I{i}" })];

    /// <summary>The Logging page's export columns: ..., V1 ... V6, I1 ... I6.</summary>
    public static IReadOnlyList<string> LogHeaders { get; } =
        [.. Leading, .. Enumerable.Range(1, 6).Select(i => $"V{i}"), .. Enumerable.Range(1, 6).Select(i => $"I{i}")];

    /// <summary>One column's value (exact column name), null for a name that is not a column. Text
    /// columns (HW, FW) are quoted when <paramref name="escape"/> is set and they
    /// need it (the Logging export does, the live logger does not).</summary>
    public static string? Value(DeviceData d, string header, bool escape = false)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (header)
        {
            case "Timestamp": return d.Timestamp.ToString("o", inv);
            case "Connected": return d.Connected ? "True" : "False";
            case "HW": return escape ? Escape(d.HardwareRevision ?? "") : d.HardwareRevision ?? "";
            case "FW": return escape ? Escape(d.FirmwareVersion ?? "") : d.FirmwareVersion ?? "";
            case "SumPowerW": return d.SumPowerW.ToString("F3", inv);
            case "SumCurrentA": return d.SumCurrentA.ToString("F3", inv);
            case "OnboardInC": return d.OnboardTempInC.ToString("F2", inv);
            case "OnboardOutC": return d.OnboardTempOutC.ToString("F2", inv);
            case "Ext1C": return d.ExternalTemp1C.ToString("F2", inv);
            case "Ext2C": return d.ExternalTemp2C.ToString("F2", inv);
        }
        if (header.Length == 2 && header[1] is >= '1' and <= '6')
        {
            int pin = header[1] - '1';
            if (header[0] == 'V') return d.PinVoltage[pin].ToString("F3", inv);
            if (header[0] == 'I') return d.PinCurrent[pin].ToString("F3", inv);
        }
        return null;
    }

    /// <summary>The header line for <paramref name="headers"/>.</summary>
    public static void WriteHeader(TextWriter w, IReadOnlyList<string> headers) => w.WriteLine(string.Join(",", headers));

    /// <summary>One row of <see cref="LiveHeaders"/>, as the live logger writes it.</summary>
    public static void WriteLiveRow(TextWriter w, DeviceData d) => WriteRow(w, d, LiveHeaders, escape: false);

    /// <summary>One row of the Logging page's export.</summary>
    public static void WriteLogRow(TextWriter w, DeviceData d) => WriteRow(w, d, LogHeaders, escape: true);

    private static void WriteRow(TextWriter w, DeviceData d, IReadOnlyList<string> headers, bool escape)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            if (i > 0) w.Write(',');
            w.Write(Value(d, headers[i], escape));
        }
        w.WriteLine();
    }

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? s
            : "\"" + s.Replace("\"", "\"\"") + "\"";
}
