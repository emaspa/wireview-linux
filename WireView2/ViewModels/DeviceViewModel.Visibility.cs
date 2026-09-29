using System;

namespace WireView2.ViewModels;

// Fan-preview gating (upstream 1.0.8): the 100 ms fan animation of the theme
// preview runs only while the Device page is shown and the main window is not
// hidden in the tray. StartFanPreview/StopFanPreview (DeviceViewModel.Theme.cs)
// record whether the preview wants to animate; this decides whether it may.
public sealed partial class DeviceViewModel : IViewVisibilityAware
{
    private bool _fanPreviewWanted;
    private bool _isViewAttached;
    private bool _isMainWindowVisible = true;
    private bool _visibilityHooked;
    private bool _visibilityDisposed;

    /// <summary>Set by DeviceView when the Device page is attached / detached.</summary>
    public bool IsViewVisible
    {
        get => _isViewAttached;
        set
        {
            if (!_visibilityHooked && !_visibilityDisposed)
            {
                App.MainWindowVisibilityChanged += OnMainWindowVisibilityChangedForPreview;
                _visibilityHooked = true;
            }
            if (Set(ref _isViewAttached, value))
                UpdateFanPreviewTimer();
        }
    }

    private void OnMainWindowVisibilityChangedForPreview(object? sender, bool visible)
    {
        _isMainWindowVisible = visible;
        UpdateFanPreviewTimer();
    }

    /// <summary>Starts the timer when the preview wants to animate and can be seen;
    /// stops it otherwise. Resuming continues from the cached frames.</summary>
    private void UpdateFanPreviewTimer()
    {
        var timer = _fanPreviewTimer;
        if (timer == null) return;
        bool run = _fanPreviewWanted && _isViewAttached && _isMainWindowVisible && !_visibilityDisposed;
        if (run && !timer.IsEnabled) timer.Start();
        else if (!run && timer.IsEnabled) timer.Stop();
    }

    private void DisposeViewVisibility()
    {
        _visibilityDisposed = true;
        if (_visibilityHooked)
            App.MainWindowVisibilityChanged -= OnMainWindowVisibilityChangedForPreview;
        UpdateFanPreviewTimer();
    }
}
