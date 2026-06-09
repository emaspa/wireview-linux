using System;

namespace WireView2.Device
{
    /// <summary>
    /// Facade over <see cref="DeviceManager"/> that presents the single
    /// <b>selected</b> device, preserving the original single-device API the
    /// ViewModels were built against (<c>Shared</c>, <c>Device</c>,
    /// <c>DataUpdated</c>, <c>ConnectionChanged</c>, <c>Start</c>/<c>Stop</c>,
    /// <c>SetPollInterval</c>). All hardware is owned by <see cref="DeviceManager"/>;
    /// this just forwards the selected device's events so existing UI keeps working
    /// while the app gains multi-device support.
    /// </summary>
    public sealed class DeviceAutoConnector : IDisposable
    {
        public static DeviceAutoConnector Shared { get; } = new DeviceAutoConnector();

        private readonly DeviceManager _mgr = DeviceManager.Shared;

        public event EventHandler<bool>? ConnectionChanged; // true=connected
        public event EventHandler<DeviceData>? DataUpdated;

        private DeviceAutoConnector()
        {
            _mgr.DataUpdated += OnManagerData;
            _mgr.SelectedChanged += OnSelectedChanged;
        }

        /// <summary>The currently selected device (or first available), or null.</summary>
        public IWireViewDevice? Device => _mgr.Selected?.Device;

        public void Start() => _mgr.Start();

        public void Stop() => _mgr.Stop();

        public void SetPollInterval(int ms) => _mgr.SetPollInterval(ms);

        private void OnManagerData(object? sender, (string Id, DeviceData Data) e)
        {
            // Forward only the selected device's stream to the single-device UI.
            if (e.Id == _mgr.SelectedId)
                DataUpdated?.Invoke(this, e.Data);
        }

        private void OnSelectedChanged(object? sender, EventArgs e)
        {
            var selected = _mgr.Selected;
            ConnectionChanged?.Invoke(this, selected?.Device.Connected ?? false);
            // Push the latest frame so the UI updates immediately on selection change.
            if (selected?.Latest != null)
                DataUpdated?.Invoke(this, selected.Latest);
        }

        public void Dispose()
        {
            _mgr.DataUpdated -= OnManagerData;
            _mgr.SelectedChanged -= OnSelectedChanged;
        }
    }
}
