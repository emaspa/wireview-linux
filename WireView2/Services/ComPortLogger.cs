using System.Collections.Generic;
using WireView2.Device;

namespace WireView2.Services;

public class ComPortLogger
{
    private readonly CsvLogger _csvLogger = new CsvLogger();
    private WireViewPro2Device? _device;

    public void StartLogging(string filePath, IEnumerable<string>? selectedHeaders = null)
    {
        _csvLogger.SetSelectedColumns(selectedHeaders);
        _csvLogger.Start(filePath);

        List<string> ports = Stm32PortFinder.FindMatchingComPorts();
        if (ports.Count > 0)
        {
            _device = new WireViewPro2Device(ports[0]);
            _device.ConnectionChanged += delegate { };
            _device.DataUpdated += (_, data) => _csvLogger.OnData(data);
            _device.Connect();
        }
    }

    public void StopLogging()
    {
        _device?.Disconnect();
        _csvLogger.Stop();
    }
}
