using System;
using System.Collections.Generic;
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
        foreach (string header in activeHeaders)
        {
            values.Add(DeviceDataCsv.Value(data, header) ?? "");
        }
        _writer.WriteLine(string.Join(",", values));
    }

    // The columns and formats are shared with WireView Mini's history export.
    private static List<string> GetAllHeaders() => DeviceDataCsv.LiveHeaders.ToList();

    public void Dispose()
    {
        Stop();
    }
}
