using System.Globalization;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Mini;

/// <summary>Where a firmware flash runs, seen from <see cref="FirmwareService"/>.</summary>
internal enum FlashRoute
{
    /// <summary>WireView Mini owns the serial port (<see cref="WireViewPro2Device"/>).</summary>
    Serial,
    /// <summary>wireviewd serves the device (Linux, <see cref="HwmonDevice"/> with the daemon).</summary>
    Daemon,
    /// <summary>hwmon without wireviewd: read only, no bootloader command can be sent.</summary>
    ReadOnly,
}

/// <summary>This PC's device as the firmware gates see it, taken at one moment.</summary>
internal sealed record FlashTarget(
    string Id, string Name, byte VendorId, byte ProductId, string FirmwareVersion, string BuildString,
    bool Connected, FlashRoute Route)
{
    /// <summary>The device's firmware version number, null while unknown.</summary>
    public int? Version =>
        int.TryParse(FirmwareVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    public DateTime? BuildTime => FirmwareHexInfo.TryParseBuildTimestamp(BuildString);

    /// <summary>"v05 (TG-WV-PRO2-FW_...)", or the GUI's words when the version is unknown.</summary>
    public string Label => Version.HasValue
        ? BundledFirmware.Label(Version, BuildString)
        : "an unknown firmware version";
}

/// <summary>What <see cref="FirmwareService"/> needs from WireView Mini: the local
/// device, the bootloader commands, and pausing its own use of the port while
/// the device is in its bootloader. <see cref="MiniService"/> implements it; a
/// test harness can fake it.</summary>
internal interface IFlashHost
{
    /// <summary>The serial port is lent to the GUI (suspendSerial).</summary>
    bool HandoverActive { get; }

    /// <summary>This PC's device the panel drives, or null when there is none.</summary>
    FlashTarget? FlashTarget();

    /// <summary>Daemon route: the bootloader command through wireviewd.</summary>
    DaemonResult EnterBootloaderThroughDaemon();

    /// <summary>Closes WireView Mini's serial port and pauses device discovery
    /// for at most <paramref name="seconds"/>. False while the GUI holds the port.</summary>
    bool SuspendForFlash(int seconds);

    /// <summary>Serial route, after <see cref="SuspendForFlash"/>: opens the
    /// port directly and sends the bootloader command. Null on success, else the
    /// status line.</summary>
    string? EnterBootloaderDirect();

    /// <summary>Ends <see cref="SuspendForFlash"/>: device discovery resumes.</summary>
    void ResumeAfterFlash();

    /// <summary>WireView Mini is moving theme artwork over the serial port
    /// (<see cref="ThemeService"/>): no flash may start meanwhile.</summary>
    bool SerialTransferRunning => false;
}

/// <summary>The steps of a flash, in order; <see cref="Done"/> and
/// <see cref="Failed"/> are final until the next one starts.</summary>
internal enum FlashPhase { Idle, EnteringBootloader, WaitingForDfu, Flashing, Done, Failed }

/// <summary>The flash state the panel and the tray show.</summary>
/// <param name="Phase">Where the flash is.</param>
/// <param name="Progress">0 to 1 while flashing (dfu-util's or the native flasher's progress).</param>
/// <param name="Message">The status line, null while idle.</param>
internal sealed record FlashState(FlashPhase Phase, double Progress, string? Message)
{
    public static FlashState Idle { get; } = new(FlashPhase.Idle, 0, null);

    public bool Running => Phase is FlashPhase.EnteringBootloader or FlashPhase.WaitingForDfu or FlashPhase.Flashing;
}

/// <summary>The answer to a flash request: the HTTP status the panel returns
/// (202 started, 409 busy, 400 refused by a gate) and the message.</summary>
internal readonly record struct FlashStart(int Status, string? Error, FlashState State);

/// <summary>The firmware view for the panel (GET /api/firmware).</summary>
internal sealed record FirmwareReport(
    FlashTarget? Device,
    string BundledPath,
    FirmwareImageInfo? Bundled,
    string? BundledError,
    FlashVerdict? Verdict,
    bool CanFlash,
    string? Reason,
    string? OverrideWarning,
    string? OverrideLabel,
    string? ConfirmText,
    bool DfuUtilRequired,
    string? DfuUtilVersion,
    string Hint,
    FlashState State);

/// <summary>
/// Flashes the bundled firmware (<see cref="BundledFirmware"/>) to this PC's
/// WireView, the same flow as the app's Device page (DeviceViewModel.UpdateFirmware)
/// with the dialogs replaced by the request's <c>allowOlder</c>: the image must
/// parse and be for this product, a Noctua Edition takes only images from
/// Noctua support on, dfu-util must be installed (Linux), an older or identical
/// build needs the override. Then the device restarts into its bootloader
/// (serial: WireView Mini closes its own port and sends the command over a
/// direct connection, as the app does through WireView Mini; daemon: through
/// wireviewd), is flashed with dfu-util (Linux, macOS) or the native WinUSB
/// flasher (Windows), and discovery resumes so the device reconnects. One flash
/// at a time, on a background task; nothing here runs until one is asked for.
/// </summary>
internal sealed class FirmwareService
{
    /// <summary>How long WireView Mini keeps its port closed and discovery paused
    /// at most (the flash resumes it as soon as it ends): the GUI's handover window.</summary>
    internal const int FlashSuspendSeconds = 600;

    private static readonly TimeSpan DfuWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectWait = TimeSpan.FromSeconds(30);

    private readonly IFlashHost _host;
    private readonly string _bundledPath;
    private readonly object _lock = new();       // _state, _generation, the caches
    private readonly object _startLock = new();  // one Start at a time, gates included
    private FlashState _state = FlashState.Idle;
    private int _generation;
    private Task? _task;

    // The bundled image's identity, re-read only when the file changes on disk.
    private (DateTime Written, long Length)? _bundledStamp;
    private FirmwareImageInfo? _bundledInfo;
    private string? _bundledError;

    // The last verdict, recomputed only when the device or its firmware changes.
    private (string Id, byte Vendor, byte Product, string Fw, string Build, FirmwareImageInfo? Image)? _verdictKey;
    private FlashVerdict? _verdict;

    public FirmwareService(IFlashHost host, string? bundledPath = null)
    {
        _host = host;
        _bundledPath = bundledPath ?? BundledFirmware.GetPath();
    }

    /// <summary>The current flash state.</summary>
    public FlashState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>A flash is between its start and its end.</summary>
    public bool Running => State.Running;

    /// <summary>The flash task (for a test harness to wait on), null before the first.</summary>
    internal Task? CurrentTask
    {
        get { lock (_lock) return _task; }
    }

    // ---- Bundled image and verdict ----

    /// <summary>The bundled image's identity, read once and again only when the
    /// file's write time or size changes (the parse builds a byte map of the
    /// whole image, so it is not done per request).</summary>
    private FirmwareImageInfo? BundledInfo(out string? error)
    {
        (DateTime, long)? stamp = null;
        try
        {
            var fi = new FileInfo(_bundledPath);
            if (fi.Exists) stamp = (fi.LastWriteTimeUtc, fi.Length);
        }
        catch { }

        lock (_lock)
        {
            if (stamp == null)
            {
                _bundledStamp = null;
                _bundledInfo = null;
                _bundledError = $"No bundled firmware image ({BundledFirmware.FileName}) next to WireView Mini.";
            }
            else if (_bundledStamp != stamp)
            {
                _bundledStamp = stamp;
                _bundledInfo = FirmwareHexInfo.TryReadInfo(_bundledPath, out var info, out var err) ? info : null;
                _bundledError = _bundledInfo == null ? "Could not parse the bundled firmware image: " + err : null;
                FileLog.Info(_bundledInfo != null
                    ? $"firmware: bundled image {BundledFirmware.Label(_bundledInfo.Version, _bundledInfo.BuildString)} ({_bundledPath})"
                    : $"firmware: {_bundledError} ({_bundledPath})");
            }
            error = _bundledError;
            return _bundledInfo;
        }
    }

    /// <summary>The verdict for <paramref name="device"/> against the bundled
    /// image, cached until the device, its firmware or the image changes, so the
    /// tray can ask every second.</summary>
    private FlashVerdict? VerdictFor(FlashTarget? device, FirmwareImageInfo? image)
    {
        if (device == null || !device.Connected || image == null) return null;
        var key = (device.Id, device.VendorId, device.ProductId, device.FirmwareVersion, device.BuildString, image);
        lock (_lock)
        {
            if (_verdictKey == key) return _verdict;
            _verdictKey = key;
            _verdict = FirmwareCompatibility.Evaluate(image, device.VendorId, device.ProductId,
                device.Version, device.BuildTime);
            FileLog.Info($"firmware: {device.Name} runs {device.Label}, bundled " +
                         $"{BundledFirmware.Label(image.Version, image.BuildString)}: {_verdict}");
            return _verdict;
        }
    }

    /// <summary>The bundled image is newer than this PC's device firmware (the
    /// tray's "Firmware update available" line). Cheap: the image is read once
    /// and the verdict is cached per device and firmware.</summary>
    public bool UpdateAvailable()
    {
        if (Running) return false;
        FirmwareImageInfo? image;
        bool neverRead;
        lock (_lock)
        {
            image = _bundledInfo;
            neverRead = _bundledStamp == null && _bundledError == null;
        }
        // First call: read the image once. Later calls use the cache (the panel's
        // GET /api/firmware and a flash notice a changed file).
        if (neverRead) image = BundledInfo(out _);
        return VerdictFor(_host.FlashTarget(), image) == FlashVerdict.Newer;
    }

    // ---- Panel view ----

    /// <summary>Everything GET /api/firmware shows. <paramref name="checkDfuUtil"/>
    /// runs dfu-util --version (Linux), so it is for requests, not the status poll.</summary>
    public FirmwareReport Report(bool checkDfuUtil = true)
    {
        var device = _host.FlashTarget();
        var image = BundledInfo(out string? imageError);
        var verdict = VerdictFor(device, image);
        bool dfuRequired = !OperatingSystem.IsWindows();
        string? dfuVersion = dfuRequired && checkDfuUtil ? DfuUtilVersion() : null;
        string? reason = GateProblem(device, image, imageError, verdict, dfuRequired && checkDfuUtil, dfuVersion);
        var state = State;
        return new FirmwareReport(
            Device: device,
            BundledPath: _bundledPath,
            Bundled: image,
            BundledError: imageError,
            Verdict: verdict,
            CanFlash: reason == null,
            Reason: reason,
            OverrideWarning: image != null && device != null ? OverrideWarning(verdict, image, device) : null,
            OverrideLabel: verdict switch
            {
                FlashVerdict.Downgrade => "Flash the older firmware anyway",
                FlashVerdict.Same => "Flash the same firmware anyway",
                _ => null,
            },
            ConfirmText: image != null && device != null ? ConfirmText(image, device) : null,
            DfuUtilRequired: dfuRequired,
            DfuUtilVersion: dfuVersion,
            Hint: Hint,
            State: state);
    }

    /// <summary>The verdict in words, for the panel.</summary>
    public static string? VerdictText(FlashVerdict? verdict) => verdict switch
    {
        FlashVerdict.Newer => "The bundled firmware is newer than the device firmware: an update is available.",
        FlashVerdict.Same => "The device already runs the bundled firmware build.",
        FlashVerdict.Downgrade => "The bundled firmware is older than the device firmware.",
        FlashVerdict.Unknown => "The device's firmware build date is not known, so it cannot be compared with the bundled firmware.",
        FlashVerdict.ProductMismatch => "The bundled firmware does not match this device.",
        FlashVerdict.TooOldForNoctua => $"The bundled firmware predates {WireViewEditions.Pro2NoctuaName} support.",
        _ => null,
    };

    /// <summary>The app's Device page hint, for WireView Mini.</summary>
    public static string Hint => OperatingSystem.IsWindows()
        ? "Flashes the firmware image shipped with WireView Mini (TG-WV-PRO2-FW.hex) over USB, using " +
          "the same native DFU method as the official client. The device restarts into its " +
          "bootloader, is flashed, and reboots on its own. Do not unplug it during the update. " +
          "Requires a WinUSB driver for the DFU device (0483:df11). " +
          "Unofficial software: flash at your own risk."
        : "Flashes the firmware image shipped with WireView Mini (TG-WV-PRO2-FW.hex) over USB using " +
          "dfu-util. The device restarts into its bootloader, is flashed, and reboots on its own. " +
          "Do not unplug it during the update. Requires the dfu-util package and the bundled udev rules. " +
          "Unofficial software: flash at your own risk.";

    private static string ImageLabel(FirmwareImageInfo image) => BundledFirmware.Label(image.Version, image.BuildString);

    /// <summary>The app's final confirmation dialog text.</summary>
    private static string ConfirmText(FirmwareImageInfo image, FlashTarget device) =>
        $"Flash firmware {ImageLabel(image)} to {device.Name}?\n\n" +
        $"The device runs {device.Label}.\n\n" +
        "The device restarts into its bootloader and is flashed over USB. " +
        "Do not unplug it until the update finishes.\n\n" +
        "This is unofficial software, not affiliated with Thermal Grizzly. " +
        "Flashing is at your own risk.";

    /// <summary>The app's downgrade and same-build dialog text, null when no
    /// override is needed.</summary>
    private static string? OverrideWarning(FlashVerdict? verdict, FirmwareImageInfo image, FlashTarget device) => verdict switch
    {
        FlashVerdict.Downgrade =>
            $"The bundled firmware ({ImageLabel(image)}) is older than the device firmware ({device.Label}).\n\n" +
            "Flashing older firmware can remove features or fixes.",
        FlashVerdict.Same => $"The device already runs this firmware build ({device.Label}).",
        _ => null,
    };

    /// <summary>Why a flash cannot start now (the app's status lines), or null.
    /// The downgrade/same override is not a problem here: it is asked for separately.</summary>
    private string? GateProblem(FlashTarget? device, FirmwareImageInfo? image, string? imageError,
        FlashVerdict? verdict, bool checkDfuUtil, string? dfuVersion)
    {
        if (Running) return "A firmware update is already running.";
        if (_host.SerialTransferRunning) return SerialTransferText;
        if (_host.HandoverActive)
            return "The serial port is lent to the WireView app. Flash from the app, or try again when it hands the port back.";
        if (device == null || !device.Connected) return "Not connected.";
        if (device.Route == FlashRoute.ReadOnly)
            return "The device is reached through hwmon without wireviewd, which is read only. Start wireviewd and try again.";
        if (image == null) return imageError;
        if (verdict == FlashVerdict.ProductMismatch)
            return $"The firmware image is for product {WireViewEditions.FormatHardwareRevision(image.VendorId, image.ProductId)}, " +
                   $"not for this device ({WireViewEditions.FormatHardwareRevision(device.VendorId, device.ProductId)}). Nothing was flashed.";
        if (verdict == FlashVerdict.TooOldForNoctua)
            return $"The firmware image {ImageLabel(image)} predates Noctua Edition support (builds from " +
                   $"{FirmwareCompatibility.NoctuaMinimumBuildDate:yyyy-MM-dd} on) and cannot be flashed to a " +
                   $"{WireViewEditions.Pro2NoctuaName}. Nothing was flashed.";
        if (checkDfuUtil && dfuVersion == null)
            return "dfu-util not found. Install the 'dfu-util' package and try again.";
        return null;
    }

    private static string? DfuUtilVersion()
    {
        try { return DfuUtilFlasher.GetDfuUtilVersionAsync().GetAwaiter().GetResult(); }
        catch { return null; }
    }

    // ---- Flash ----

    /// <summary>Checks every gate and starts the flash on a background task.
    /// Returns 202 when it started, 409 when one runs or the GUI holds the port,
    /// 400 with the gate's message otherwise (nothing flashed).</summary>
    /// <param name="allowOlder">Flash an older or identical build (the app's
    /// override dialog). Without it such a request is refused.</param>
    public FlashStart Start(bool allowOlder)
    {
        lock (_startLock)
        {
            if (Running) return new FlashStart(409, "A firmware update is already running.", State);
            if (_host.SerialTransferRunning) return new FlashStart(409, SerialTransferText, State);
            if (_host.HandoverActive)
                return new FlashStart(409,
                    "The serial port is lent to the WireView app. Flash from the app, or try again when it hands the port back.",
                    State);

            var device = _host.FlashTarget();
            bool dfuRequired = !OperatingSystem.IsWindows();
            if (device == null || !device.Connected) return Refused("Not connected.");
            // Re-read the image: it may have been swapped on disk. The gates judge
            // exactly the bytes that get flashed, as in the app.
            if (!File.Exists(_bundledPath))
                return Refused($"No bundled firmware image ({BundledFirmware.FileName}) next to WireView Mini.");
            if (!FirmwareHexInfo.TryReadImage(_bundledPath, out uint baseAddress, out byte[] image,
                    out FirmwareImageInfo? info, out string? imageError) || info == null)
                return Refused("Could not parse the bundled firmware image: " + imageError);

            var verdict = FirmwareCompatibility.Evaluate(info, device.VendorId, device.ProductId,
                device.Version, device.BuildTime);
            string? dfuVersion = dfuRequired ? DfuUtilVersion() : null;
            if (GateProblem(device, info, null, verdict, dfuRequired, dfuVersion) is { } problem)
                return Refused(problem);

            // Same rules as the app and `wireviewctl flash`: an older or identical
            // build only with the explicit override.
            if (verdict is FlashVerdict.Downgrade or FlashVerdict.Same && !allowOlder)
                return Refused(OverrideWarning(verdict, info, device) +
                               "\n\nNothing was flashed. Confirm the override (allowOlder) to flash it anyway.");

            string imageLabel = ImageLabel(info);
            FileLog.Info($"firmware update: starting, {imageLabel} to {device.Name} ({device.Route} route), " +
                         $"device runs {device.Label}, verdict {verdict}" +
                         (verdict is FlashVerdict.Downgrade or FlashVerdict.Same ? ", override confirmed" : "") +
                         (dfuVersion != null ? $", {dfuVersion}" : ""));
            int generation;
            lock (_lock)
            {
                generation = ++_generation;
                _state = new FlashState(FlashPhase.EnteringBootloader, 0, "Restarting device into DFU bootloader...");
                _task = Task.Run(() => RunAsync(generation, device, imageLabel, baseAddress, image));
                return new FlashStart(202, null, _state);
            }
        }

        FlashStart Refused(string message)
        {
            FileLog.Warn($"firmware update refused: {message.Replace("\n\n", " ")}");
            return new FlashStart(400, message, State);
        }
    }

    private void Set(int generation, FlashPhase phase, double progress, string? message)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _state = new FlashState(phase, Math.Clamp(progress, 0, 1), message);
        }
    }

    private void SetProgress(int generation, double progress)
    {
        lock (_lock)
        {
            if (generation != _generation || !_state.Running) return;
            _state = _state with { Progress = Math.Clamp(progress, 0, 1) };
        }
    }

    private const string FailedPrefix = "Firmware update failed: ";
    private const string SerialTransferText = "A theme transfer is using the serial port. Try again when it has finished.";

    private void Fail(int generation, string message)
    {
        string detail = message.StartsWith(FailedPrefix, StringComparison.Ordinal) ? message[FailedPrefix.Length..] : message;
        FileLog.Warn($"firmware update failed: {detail.Replace("\n", " ")}");
        Set(generation, FlashPhase.Failed, State.Progress, message);
    }

    private async Task RunAsync(int generation, FlashTarget device, string imageLabel, uint baseAddress, byte[] image)
    {
        string binPath = Path.Combine(Path.GetTempPath(), $"wireview-mini-fw-{Guid.NewGuid():N}.bin");
        bool suspended = false, flashed = false;
        try
        {
            if (device.Route == FlashRoute.Daemon)
            {
                // Denied/Error/NotConnected mean wireviewd never sent the command, so
                // the device is still running: stop here. With no answer at all
                // (Unavailable) the command may have gone through; wait for the DFU
                // device as the app does.
                var boot = _host.EnterBootloaderThroughDaemon();
                FileLog.Info($"firmware update: bootloader command through wireviewd -> {boot}");
                if (boot == DaemonResult.Denied) { Fail(generation, DaemonResults.DeniedMessage); return; }
                if (boot is DaemonResult.Error or DaemonResult.NotConnected)
                {
                    Fail(generation, $"Could not restart the device into its bootloader: {boot.Describe()}.");
                    return;
                }
                // Pause discovery while the device is gone, so WireView Mini does
                // not race wireviewd for the serial port when it comes back.
                suspended = _host.SuspendForFlash(FlashSuspendSeconds);
            }
            else
            {
                // WireView Mini's own port: close it (the handover mechanism, so
                // discovery stays off), then the bootloader command over a direct
                // connection, as the app does through WireView Mini.
                if (!_host.SuspendForFlash(FlashSuspendSeconds))
                {
                    Fail(generation, "The serial port is lent to the WireView app. Nothing was flashed.");
                    return;
                }
                suspended = true;
                FileLog.Info("firmware update: serial port closed, sending the bootloader command over a direct connection");
                if (_host.EnterBootloaderDirect() is { } bootError) { Fail(generation, bootError); return; }
            }

            var progress = new DirectProgress(p => SetProgress(generation, p));
            if (OperatingSystem.IsWindows())
            {
                // Native WinUSB DFU, same method as the official Windows client (it
                // waits for the DFU interface itself). The flat image goes to
                // 0x08000000, which is where the bundled hex is based.
                if (baseAddress != 0x08000000u)
                {
                    Fail(generation, $"Unexpected firmware base address 0x{baseAddress:X8}.");
                    return;
                }
                Set(generation, FlashPhase.Flashing, 0, $"Flashing {imageLabel}...");
                FileLog.Info($"firmware update: flashing {image.Length} bytes over WinUSB");
                using var imageStream = new MemoryStream(image);
                await DfuFirmwareUpdater.UpdateAsync(imageStream, progress, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllBytesAsync(binPath, image).ConfigureAwait(false);
                Set(generation, FlashPhase.WaitingForDfu, 0, "Waiting for the DFU bootloader...");
                FileLog.Info($"firmware update: waiting up to {DfuWait.TotalSeconds:0} s for the DFU device {DfuUtilFlasher.DfuVid}:{DfuUtilFlasher.DfuPid}");
                if (!await DfuUtilFlasher.WaitForDfuDeviceAsync(DfuWait, CancellationToken.None).ConfigureAwait(false))
                {
                    Fail(generation,
                        "The DFU bootloader did not appear. Check that the udev rule for 0483:df11 is " +
                        "installed, then power-cycle the device and try again.");
                    return;
                }
                Set(generation, FlashPhase.Flashing, 0, $"Flashing {imageLabel}...");
                FileLog.Info($"firmware update: flashing {image.Length} bytes at 0x{baseAddress:X8} with dfu-util");
                await DfuUtilFlasher.FlashAsync(binPath, baseAddress, progress, CancellationToken.None).ConfigureAwait(false);
            }

            flashed = true;
            Set(generation, FlashPhase.Done, 1, "Firmware update complete. The device is restarting.");
            FileLog.Info("firmware update: complete, the device is restarting");
        }
        catch (Exception ex)
        {
            Fail(generation, FailedPrefix + ex.Message);
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
            if (suspended)
            {
                FileLog.Info("firmware update: device discovery resumed");
                try { _host.ResumeAfterFlash(); }
                catch (Exception ex) { FileLog.Warn($"firmware update: resuming discovery failed: {ex.Message}"); }
            }
        }

        if (flashed) await ReportReconnectAsync(generation).ConfigureAwait(false);
    }

    /// <summary>After a flash, like the app: once the device is back, say so with
    /// its new firmware version (only while no newer flash has started).</summary>
    private async Task ReportReconnectAsync(int generation)
    {
        var until = DateTime.UtcNow + ReconnectWait;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (_host.FlashTarget() is { Connected: true } back)
            {
                string fw = back.Version.HasValue ? BundledFirmware.FormatVersion(back.Version) : "";
                Set(generation, FlashPhase.Done, 1, fw.Length > 0
                    ? $"Firmware update complete. Device reconnected ({fw})."
                    : "Firmware update complete. Device reconnected.");
                FileLog.Info($"firmware update: device reconnected, running {back.Label}");
                return;
            }
        }
        FileLog.Warn($"firmware update: the device did not reconnect within {ReconnectWait.TotalSeconds:0} s");
    }

    /// <summary>Reports on the reporting thread: <see cref="Progress{T}"/> would
    /// post to the thread pool, which can reorder the reports.</summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
