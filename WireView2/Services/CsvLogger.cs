using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WireView2.Device;

namespace WireView2.Services;

public class CsvLogger : IDisposable
{
    private StreamWriter? _writer;
    private bool _headerWritten;
    private HashSet<string>? _selectedHeaders;

    public bool IsLogging => _writer != null;

    public void SetSelectedColumns(IEnumerable<string>? headers)
    {
        if (headers == null)
        {
            _selectedHeaders = null;
            return;
        }

        var collection = headers
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim());
        _selectedHeaders = new HashSet<string>(collection, StringComparer.OrdinalIgnoreCase);
    }

    public void Start(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: true, Encoding.UTF8)
        {
            AutoFlush = true
        };
        _headerWritten = false;
    }

    public void Stop()
    {
        _writer?.Dispose();
        _writer = null;
        _headerWritten = false;
    }

    public void OnData(DeviceData data)
    {
        if (_writer == null)
            return;

        List<string> allHeaders = GetAllHeaders();
        List<string> activeHeaders = (_selectedHeaders == null || _selectedHeaders.Count == 0)
            ? allHeaders
            : allHeaders.Where(h => _selectedHeaders.Contains(h)).ToList();

        if (!_headerWritten)
        {
            _writer.WriteLine(string.Join(",", activeHeaders));
            _headerWritten = true;
        }

        var values = new List<string>(activeHeaders.Count);
        var valueMap = GetValueMap(data);
        foreach (string header in activeHeaders)
        {
            values.Add(valueMap.TryGetValue(header, out var val) ? val : "");
        }
        _writer.WriteLine(string.Join(",", values));
    }

    private static List<string> GetAllHeaders()
    {
        var headers = new List<string>
        {
            "Timestamp", "Connected", "HW", "FW",
            "SumPowerW", "SumCurrentA",
            "OnboardInC", "OnboardOutC", "Ext1C", "Ext2C"
        };
        for (int i = 1; i <= 6; i++)
        {
            headers.Add($"V{i}");
            headers.Add($"I{i}");
        }
        return headers;
    }

    private static Dictionary<string, string> GetValueMap(DeviceData data)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Timestamp"] = data.Timestamp.ToString("o", CultureInfo.InvariantCulture),
            ["Connected"] = data.Connected ? "True" : "False",
            ["HW"] = data.HardwareRevision ?? "",
            ["FW"] = data.FirmwareVersion ?? "",
            ["SumPowerW"] = data.SumPowerW.ToString("F3", CultureInfo.InvariantCulture),
            ["SumCurrentA"] = data.SumCurrentA.ToString("F3", CultureInfo.InvariantCulture),
            ["OnboardInC"] = data.OnboardTempInC.ToString("F2", CultureInfo.InvariantCulture),
            ["OnboardOutC"] = data.OnboardTempOutC.ToString("F2", CultureInfo.InvariantCulture),
            ["Ext1C"] = data.ExternalTemp1C.ToString("F2", CultureInfo.InvariantCulture),
            ["Ext2C"] = data.ExternalTemp2C.ToString("F2", CultureInfo.InvariantCulture),
        };
        for (int i = 1; i <= 6; i++)
        {
            map[$"V{i}"] = data.PinVoltage[i - 1].ToString("F3", CultureInfo.InvariantCulture);
            map[$"I{i}"] = data.PinCurrent[i - 1].ToString("F3", CultureInfo.InvariantCulture);
        }
        return map;
    }

    public void Dispose()
    {
        Stop();
    }
}
