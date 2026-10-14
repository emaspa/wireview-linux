using WireView2.Device;
using WireView2.Net;

namespace WireView2.Mini;

/// <summary>The theme transfers <see cref="ThemeService"/> makes on the device's
/// SPI flash: <see cref="WireViewPro2Device"/>'s theme asset API
/// (<see cref="SerialThemeDevice"/>). A test harness can fake it.</summary>
internal interface IThemeDevice
{
    Task<byte[]?> ReadBackgroundAsync(WireViewPro2Device.THEME_BACKGROUND slot, CancellationToken ct);
    Task<(byte[] Frame1, byte[] Frame2)> ReadFanAsync(WireViewPro2Device.THEME_FAN slot, CancellationToken ct);
    Task WriteBackgroundAsync(WireViewPro2Device.THEME_BACKGROUND slot, ReadOnlyMemory<byte> rgb565, IProgress<double>? progress, CancellationToken ct);
    Task WriteFanAsync(WireViewPro2Device.THEME_FAN slot, ReadOnlyMemory<byte> frame1, ReadOnlyMemory<byte> frame2, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>The real device: the theme asset API of an open <see cref="WireViewPro2Device"/>.</summary>
internal sealed class SerialThemeDevice(WireViewPro2Device device) : IThemeDevice
{
    public Task<byte[]?> ReadBackgroundAsync(WireViewPro2Device.THEME_BACKGROUND slot, CancellationToken ct) =>
        device.ReadThemeBackgroundRgb565Async(slot, ct);

    public Task<(byte[] Frame1, byte[] Frame2)> ReadFanAsync(WireViewPro2Device.THEME_FAN slot, CancellationToken ct) =>
        device.ReadThemeFanRgb565Async(slot, ct);

    public Task WriteBackgroundAsync(WireViewPro2Device.THEME_BACKGROUND slot, ReadOnlyMemory<byte> rgb565, IProgress<double>? progress, CancellationToken ct) =>
        device.WriteThemeBackgroundRgb565Async(slot, rgb565, progress, ct);

    public Task WriteFanAsync(WireViewPro2Device.THEME_FAN slot, ReadOnlyMemory<byte> frame1, ReadOnlyMemory<byte> frame2, IProgress<double>? progress, CancellationToken ct) =>
        device.WriteThemeFanRgb565Async(slot, frame1, frame2, progress, ct);
}

/// <summary>What <see cref="ThemeService"/> needs from WireView Mini: the local
/// device, its config read and write (the panel's relay), screen commands, and a
/// serial connection for the SPI transfers. <see cref="MiniService"/> implements
/// it; a test harness can fake it.</summary>
internal interface IThemeHost
{
    /// <summary>The serial port is lent to the GUI (suspendSerial).</summary>
    bool HandoverActive { get; }

    /// <summary>A firmware flash is running.</summary>
    bool FlashRunning { get; }

    /// <summary>This PC's device the panel drives (its route tells serial from
    /// wireviewd), or null when there is none or the port is out of use.</summary>
    FlashTarget? ThemeTarget();

    /// <summary>The device's config bytes, null when they cannot be read.</summary>
    ConfigSnapshot? ReadConfig();

    /// <summary>A device command through the panel's relay (config write, screen).</summary>
    DaemonResult RunCommand(WireViewCommand cmd);

    /// <summary>The last screen WireView Mini sent the device to (the protocol cannot
    /// ask the device which one it shows).</summary>
    WireViewPro2Device.SCREEN_CMD LastScreen { get; }

    /// <summary>Runs <paramref name="op"/> on a serial connection to the device:
    /// WireView Mini's own port (direct serial), or the port borrowed from wireviewd
    /// for the duration (<see cref="DirectSerialSession"/>, which throws
    /// <see cref="DaemonDeniedException"/> when the user is not in the group).</summary>
    Task<T> RunOnSerialAsync<T>(Func<IThemeDevice, CancellationToken, Task<T>> op, CancellationToken ct);
}

/// <summary>The steps of a theme upload; <see cref="Done"/> and <see cref="Failed"/>
/// are final until the next one starts.</summary>
internal enum ThemePhase { Idle, Uploading, WritingConfig, Redrawing, Done, Failed }

/// <summary>A theme upload's state, for the panel.</summary>
/// <param name="Progress">0 to 1 while running.</param>
/// <param name="Message">The status line, null while idle.</param>
internal sealed record ThemeUploadState(ThemePhase Phase, double Progress, string? Message)
{
    public static ThemeUploadState Idle { get; } = new(ThemePhase.Idle, 0, null);

    public bool Running => Phase is ThemePhase.Uploading or ThemePhase.WritingConfig or ThemePhase.Redrawing;
}

/// <summary>A theme edit (POST /api/theme): the device theme to set, and the
/// staged artwork. Colours are "#rrggbb" (or "#aarrggbb"); the images are base64
/// RGB565 in the device layout.</summary>
internal sealed class ThemeEdit
{
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public string? HighlightColor { get; set; }
    public string? BackgroundColor { get; set; }
    public bool DisplayInversion { get; set; }
    public int BackgroundBitmapId { get; set; }
    public int FanBitmapId { get; set; }
    /// <summary>A custom background (320x170) for the selected slot, null for none.</summary>
    public string? Background { get; set; }
    /// <summary>The two fan frames (73x73) to go with it.</summary>
    public string? FanFrame1 { get; set; }
    public string? FanFrame2 { get; set; }
}

/// <summary>The device's artwork read for the preview: the background slot's
/// image (null for none) and the fan slot's two frames (null when not read).</summary>
internal sealed record ThemeArtwork(byte BackgroundSlot, byte FanSlot, byte[]? Background, byte[]? FanFrame1, byte[]? FanFrame2);

/// <summary>What a theme read found. <paramref name="Status"/>: 200, or the
/// HTTP status of <paramref name="Problem"/> when the device cannot be used.</summary>
internal sealed record ThemeRead(
    int Status, string? Problem, int? ConfigVersion, DeviceThemeValues? Values,
    ThemeArtwork? Artwork, string? ArtworkError);

/// <summary>An apply or restore answer: 200 done (colours only), 202 started
/// (the progress is in /api/status), else the refusal's status and message.</summary>
internal readonly record struct ThemeStart(int Status, string? Error, string? Message, ThemeUploadState State);

/// <summary>
/// The control panel's theme editor, the app's Device page theme flow
/// (DeviceViewModel.Theme.cs) without the raster work, which the page does in the
/// browser: reads the device's theme (colours, inversion and slots from its config,
/// the slot artwork from its SPI flash, on request only) and applies one (the
/// colours through the config write the panel already uses; a custom background
/// and its fan frames to the SPI flash first). Factory artwork comes back from
/// the bundled ext_flash.bin, as in the app. SPI transfers use WireView Mini's own
/// port in direct serial mode and borrow the port from wireviewd in daemon mode,
/// like the app's RunOnSerialDeviceAsync. One transfer at a time; refused while a
/// firmware flash runs or the app holds the port. Nothing here runs until asked.
/// </summary>
internal sealed class ThemeService
{
    public const string FactoryFileName = "ext_flash.bin";

    private const int BgBytes = WireViewPro2Device.ThemeBackgroundSizeBytes;   // 108800
    private const int FanBytes = WireViewPro2Device.ThemeFanFrameSizeBytes;    // 10658

    /// <summary>Pause after an SPI session before talking to the device again: the
    /// app's restore waits this long, and with wireviewd the daemon is reopening
    /// the port meanwhile.</summary>
    internal static TimeSpan SettleDelay = TimeSpan.FromSeconds(1);
    /// <summary>The app's redraw detour: time on the other screen, then for the
    /// returned screen to paint.</summary>
    internal static TimeSpan DetourDelay = TimeSpan.FromSeconds(3);
    internal static TimeSpan ReturnDelay = TimeSpan.FromMilliseconds(1500);

    private readonly IThemeHost _host;
    private readonly string _factoryPath;
    private readonly object _lock = new();   // _state, _generation, _task
    private ThemeUploadState _state = ThemeUploadState.Idle;
    private int _generation;
    private Task? _task;
    private int _busy;                       // 1 while a read or an upload uses the port

    public ThemeService(IThemeHost host, string? factoryPath = null)
    {
        _host = host;
        _factoryPath = factoryPath ?? Path.Combine(AppContext.BaseDirectory, FactoryFileName);
    }

    public ThemeUploadState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>A read or an upload is using the serial port (the app's handover
    /// and a firmware flash are refused meanwhile).</summary>
    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>An upload is between its start and its end.</summary>
    public bool UploadRunning => State.Running;

    /// <summary>The upload task (for a test harness to wait on), null before the first.</summary>
    internal Task? CurrentTask
    {
        get { lock (_lock) return _task; }
    }

    /// <summary>The factory artwork image is next to WireView Mini.</summary>
    public bool FactoryArtworkAvailable => File.Exists(_factoryPath);

    private bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    private void Exit() => Volatile.Write(ref _busy, 0);

    // ---- Gates ----

    /// <summary>Why the device cannot be used for the theme now (status, message),
    /// or null. Layout 2 is checked separately (it needs the config).</summary>
    private (int Status, string Message)? Problem(out FlashTarget? target)
    {
        target = null;
        if (_host.FlashRunning) return (409, "A firmware update is running. Try again when it has finished.");
        if (_host.HandoverActive) return (409, "The serial port is lent to the WireView app. Try again when it hands the port back.");
        if (UploadRunning) return (409, "A theme upload is running. Try again when it has finished.");
        target = _host.ThemeTarget();
        if (target == null || !target.Connected) return (503, "Not connected.");
        if (target.Route == FlashRoute.ReadOnly)
            return (503, "The device is reached through hwmon without wireviewd, which is read only. Start wireviewd and try again.");
        return null;
    }

    /// <summary>The app's words for a firmware without the colour theme.</summary>
    public const string LayoutMessage = "Theme assets are only supported on config v2+ devices.";

    // ---- Read ----

    /// <summary>The device's theme: the config's theme fields, and with
    /// <paramref name="artwork"/> the artwork of the background and fan slots
    /// (the device's own unless <paramref name="backgroundSlot"/> or
    /// <paramref name="fanSlot"/> asks for another) read over the serial port.
    /// Blocks for the transfer (a few seconds, plus the handover with wireviewd).
    /// A denied or failed artwork read still returns the colours, with the reason.</summary>
    public ThemeRead Read(bool artwork, byte? backgroundSlot = null, byte? fanSlot = null, bool fans = true)
    {
        if (Problem(out _) is { } p) return new ThemeRead(p.Status, p.Message, null, null, null, null);
        var snap = _host.ReadConfig();
        if (snap == null) return new ThemeRead(503, "Could not read the device configuration.", null, null, null, null);
        if (snap.Version < 2) return new ThemeRead(200, null, snap.Version, null, null, null);
        var values = DeviceThemeValues.From(WireViewPro2Device.DeserializeConfig(snap.Version, snap.Data));
        if (!artwork) return new ThemeRead(200, null, snap.Version, values, null, null);

        byte bgId = backgroundSlot ?? values.BackgroundBitmapId;
        byte fanId = fanSlot ?? values.FanBitmapId;
        if (!TryEnter()) return new ThemeRead(409, "A theme transfer is running. Try again when it has finished.", snap.Version, values, null, null);
        try
        {
            // Unknown slot ids have no artwork address: nothing to read for them.
            bool bgKnown = bgId is (byte)WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange or (byte)WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark;
            bool fanKnown = fanId is (byte)WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange or (byte)WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark
                or (byte)WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite;
            var started = DateTime.UtcNow;
            var art = _host.RunOnSerialAsync(async (dev, ct) =>
            {
                byte[]? bg = bgKnown ? await dev.ReadBackgroundAsync((WireViewPro2Device.THEME_BACKGROUND)bgId, ct).ConfigureAwait(false) : null;
                byte[]? f1 = null, f2 = null;
                if (fans && fanKnown) (f1, f2) = await dev.ReadFanAsync((WireViewPro2Device.THEME_FAN)fanId, ct).ConfigureAwait(false);
                return new ThemeArtwork(bgId, fanId, bg, f1, f2);
            }, CancellationToken.None).GetAwaiter().GetResult();
            FileLog.Info($"theme: artwork read (background slot {bgId}" + (fans ? $", fan slot {fanId}" : "") +
                         $") in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
            return new ThemeRead(200, null, snap.Version, values, art, null);
        }
        catch (Exception ex)
        {
            // The app's preview: the editor still works, the preview has no artwork.
            FileLog.Warn($"theme: artwork read failed: {ex.Message}");
            return new ThemeRead(200, null, snap.Version, values, null, "Theme preview failed: " + ex.Message);
        }
        finally
        {
            Exit();
        }
    }

    // ---- Apply ----

    /// <summary>Applies <paramref name="edit"/>. Colours, inversion and slots only:
    /// written now, like the app's Apply (the changed theme fields onto the device's
    /// current config, then a redraw), 200. With a custom background: its upload
    /// and the fan frames start on a background task first, 202. Refusals: 409
    /// busy, 503 no device, 400 invalid.</summary>
    public ThemeStart Apply(ThemeEdit edit)
    {
        if (!TryParse(edit, out var wanted, out byte[]? bg, out byte[]? fan1, out byte[]? fan2, out string? invalid))
            return Refused(400, invalid!);
        if (Problem(out var target) is { } p) return Refused(p.Status, p.Message);
        var snap = _host.ReadConfig();
        if (snap == null) return Refused(503, "Could not read the device configuration.");
        if (snap.Version < 2) return Refused(400, LayoutMessage);
        var current = DeviceThemeValues.From(WireViewPro2Device.DeserializeConfig(snap.Version, snap.Data));
        if (CheckIds(wanted, current) is { } badId) return Refused(400, badId);

        if (bg == null)
        {
            if (!TryEnter()) return Refused(409, "A theme transfer is running. Try again when it has finished.");
            try
            {
                var (status, message) = WriteTheme(wanted);
                if (status != 200) return Refused(status, "Apply: " + message);
                return new ThemeStart(200, null, message, State);
            }
            finally
            {
                Exit();
            }
        }

        if (wanted.BackgroundBitmapId == (byte)WireViewPro2Device.THEME_BACKGROUND.Disabled)
            return Refused(400, "Select a background slot (not Disabled) before applying a staged background.");
        if (!TryEnter()) return Refused(409, "A theme transfer is running. Try again when it has finished.");
        FileLog.Info($"theme: upload starting, background slot {wanted.BackgroundBitmapId}, fan slot {wanted.FanBitmapId} ({target!.Route} route)");
        return StartTask(new ThemeUploadState(ThemePhase.Uploading, 0, "Uploading background to device..."),
            gen => UploadAsync(gen, wanted, bg, fan1!, fan2!));
    }

    /// <summary>The app's Restore default background + fan: the background slot
    /// and its fan frames from the bundled ext_flash.bin, then a redraw. 202 once
    /// started.</summary>
    public ThemeStart RestoreDefaults(int backgroundSlot)
    {
        if (Problem(out var target) is { } p) return Refused(p.Status, p.Message);
        var snap = _host.ReadConfig();
        if (snap == null) return Refused(503, "Could not read the device configuration.");
        if (snap.Version < 2) return Refused(400, LayoutMessage);
        if (backgroundSlot == (int)WireViewPro2Device.THEME_BACKGROUND.Disabled)
            return Refused(400, "Select a background slot (not Disabled) before restoring.");
        if (backgroundSlot is not ((int)WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange or (int)WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark))
            return Refused(400, $"Unknown background slot {backgroundSlot}.");
        if (!File.Exists(_factoryPath))
            return Refused(400, $"Factory asset image ({FactoryFileName}) not found next to WireView Mini.");

        var background = (WireViewPro2Device.THEME_BACKGROUND)backgroundSlot;
        byte[] image;
        try { image = File.ReadAllBytes(_factoryPath); }
        catch (Exception ex) { return Refused(500, $"Could not read {FactoryFileName}: {ex.Message}"); }
        if (!TrySliceFactory(image, background, out var bgBytes, out var fan, out var f1, out var f2, out string? sliceError))
            return Refused(500, sliceError!);
        if (!TryEnter()) return Refused(409, "A theme transfer is running. Try again when it has finished.");
        FileLog.Info($"theme: factory restore starting, background slot {backgroundSlot}, fan slot {(byte)fan} ({target!.Route} route)");
        return StartTask(new ThemeUploadState(ThemePhase.Uploading, 0, "Restoring factory theme assets..."),
            gen => RestoreAsync(gen, background, bgBytes, fan, f1, f2));
    }

    private ThemeStart StartTask(ThemeUploadState first, Func<int, Task> body)
    {
        lock (_lock)
        {
            int generation = ++_generation;
            _state = first;
            _task = Task.Run(async () =>
            {
                try { await body(generation).ConfigureAwait(false); }
                catch (Exception ex) { Fail(generation, ex.Message); }
                finally { Exit(); }
            });
            return new ThemeStart(202, null, first.Message, first);
        }
    }

    private ThemeStart Refused(int status, string message)
    {
        if (status != 409) FileLog.Warn($"theme: refused: {message}");
        return new ThemeStart(status, message, null, State);
    }

    // ---- Upload and restore tasks ----

    private async Task UploadAsync(int generation, DeviceThemeValues wanted, byte[] bg, byte[] fan1, byte[] fan2)
    {
        var bgSlot = (WireViewPro2Device.THEME_BACKGROUND)wanted.BackgroundBitmapId;
        var fanSlot = (WireViewPro2Device.THEME_FAN)wanted.FanBitmapId;
        // The app's split: the background is three quarters of the transfer.
        var bgProgress = new DirectProgress(v => SetProgress(generation, v * 0.7));
        var fanProgress = new DirectProgress(v => SetProgress(generation, 0.7 + v * 0.2));
        try
        {
            await _host.RunOnSerialAsync<object?>(async (dev, ct) =>
            {
                await dev.WriteBackgroundAsync(bgSlot, bg, bgProgress, ct).ConfigureAwait(false);
                Set(generation, ThemePhase.Uploading, 0.7, "Uploading fan frames to device...");
                await dev.WriteFanAsync(fanSlot, fan1, fan2, fanProgress, ct).ConfigureAwait(false);
                return null;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(generation, "Background upload failed: " + ex.Message);
            return;
        }
        FileLog.Info("theme: background and fan frames uploaded");

        await Task.Delay(SettleDelay).ConfigureAwait(false);
        Set(generation, ThemePhase.WritingConfig, 0.9, "Writing the theme to the device...");
        var (status, message) = WriteTheme(wanted, retries: 3);
        if (status != 200) { Fail(generation, "Background uploaded, but the theme could not be written: " + message); return; }

        string? redrawError = await RedrawAsync(generation).ConfigureAwait(false);
        Done(generation, redrawError == null
            ? "Theme applied. Background uploaded."
            : "Theme applied and background uploaded; screen refresh failed (" + redrawError + "). Switch screens once to see it.");
    }

    private async Task RestoreAsync(int generation, WireViewPro2Device.THEME_BACKGROUND background, byte[] bg,
        WireViewPro2Device.THEME_FAN fan, byte[] fan1, byte[] fan2)
    {
        // The app's split (background from 0.1, fan frames the last fifth), up to
        // 0.9: the redraw is the rest.
        var bgProgress = new DirectProgress(v => SetProgress(generation, 0.1 + v * 0.62));
        var fanProgress = new DirectProgress(v => SetProgress(generation, 0.72 + v * 0.18));
        try
        {
            SetProgress(generation, 0.1);
            await _host.RunOnSerialAsync<object?>(async (dev, ct) =>
            {
                await dev.WriteBackgroundAsync(background, bg, bgProgress, ct).ConfigureAwait(false);
                await dev.WriteFanAsync(fan, fan1, fan2, fanProgress, ct).ConfigureAwait(false);
                return null;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(generation, "Factory restore failed: " + ex.Message);
            return;
        }
        FileLog.Info("theme: factory artwork written");

        await Task.Delay(SettleDelay).ConfigureAwait(false);
        string? redrawError = await RedrawAsync(generation).ConfigureAwait(false);
        Done(generation, redrawError == null
            ? "Factory theme assets restored."
            : "Factory assets restored; screen refresh failed (" + redrawError + "). Switch screens once to see them.");
    }

    /// <summary>The app's redraw after new artwork: the background is copied from
    /// the SPI flash when a screen is entered (a config write and SCREEN_GOTO_SAME
    /// repaint from the framebuffer only), so go to another screen, wait for it to
    /// paint, and come back to the last screen WireView Mini commanded. Null when
    /// done, else the reason.</summary>
    private async Task<string?> RedrawAsync(int generation)
    {
        Set(generation, ThemePhase.Redrawing, 0.95, "Refreshing the device screen...");
        var back = _host.LastScreen;
        var detour = back == WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS
            ? WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP
            : WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS;
        var r = _host.RunCommand(WireViewCommand.Screen("", (int)detour));
        if (r != DaemonResult.Ok) return r.Describe();
        await Task.Delay(DetourDelay).ConfigureAwait(false);
        r = _host.RunCommand(WireViewCommand.Screen("", (int)back));
        if (r != DaemonResult.Ok) return r.Describe();
        await Task.Delay(ReturnDelay).ConfigureAwait(false);
        return null;
    }

    /// <summary>Writes the theme fields that differ from the device's config now
    /// onto its current bytes (the panel's config write), redraws with
    /// SCREEN_GOTO_SAME like the app's Apply, and checks the read-back. Status 200
    /// with the result line, else the HTTP status and why (retried up to
    /// <paramref name="retries"/> times while the device does not answer, as after
    /// a handover while wireviewd reopens the port).</summary>
    private (int Status, string Message) WriteTheme(DeviceThemeValues wanted, int retries = 1)
    {
        (int, string) last = (500, "the device did not take the config write");
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            if (attempt > 1) Thread.Sleep(SettleDelay);
            var snap = _host.ReadConfig();
            if (snap == null) { last = (503, "could not read the device configuration"); continue; }
            if (snap.Version < 2) return (400, LayoutMessage);
            var current = DeviceThemeValues.From(WireViewPro2Device.DeserializeConfig(snap.Version, snap.Data));
            byte[] bytes;
            try { bytes = wanted.EncodeOnto(snap.Version, snap.Data, current); }
            catch (DeviceConfigException ex) { return (400, ex.Message); }
            if (bytes.AsSpan().SequenceEqual(snap.Data)) return (200, "Theme applied (nothing changed).");

            var r = _host.RunCommand(WireViewCommand.WriteConfig(snap.DeviceId, snap.Version, bytes));
            if (r == DaemonResult.Denied)
                return (403, "wireviewd refused the config write. Add your user to the 'wireview' group and log in again.");
            if (r == DaemonResult.Busy) return (409, "a firmware update is running, try again when it is done");
            if (r != DaemonResult.Ok)
            {
                last = r == DaemonResult.NotConnected ? (503, "no device connected") : (500, "the device did not take the config write");
                continue;
            }
            _host.RunCommand(WireViewCommand.Screen(snap.DeviceId, (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME));
            FileLog.Info($"theme: config written ({bytes.Length} bytes)");

            Thread.Sleep(150); // let the device take the frames before reading back
            var after = _host.ReadConfig();
            if (after == null) return (200, "Theme applied (it could not be read back).");
            return after.Data.AsSpan().SequenceEqual(bytes)
                ? (200, "Theme applied.")
                : (200, "Theme applied, but the device reads back a different configuration.");
        }
        return last;
    }

    // ---- State ----

    private void Set(int generation, ThemePhase phase, double progress, string? message)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _state = new ThemeUploadState(phase, Math.Clamp(progress, 0, 1), message);
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

    private void Done(int generation, string message)
    {
        FileLog.Info($"theme: {message}");
        Set(generation, ThemePhase.Done, 1, message);
    }

    private void Fail(int generation, string message)
    {
        FileLog.Warn($"theme: {message}");
        Set(generation, ThemePhase.Failed, State.Progress, message);
    }

    // ---- Validation and helpers ----

    /// <summary>The edit's values and images, checked for form and size (not yet
    /// against the device).</summary>
    internal static bool TryParse(ThemeEdit edit, out DeviceThemeValues values, out byte[]? background,
        out byte[]? fan1, out byte[]? fan2, out string? error, bool requireFans = true)
    {
        values = default;
        background = fan1 = fan2 = null;
        error = null;
        uint[] colors = new uint[4];
        string?[] texts = [edit.PrimaryColor, edit.SecondaryColor, edit.HighlightColor, edit.BackgroundColor];
        string[] names = ["primary color", "secondary color", "highlight color", "background color"];
        for (int i = 0; i < 4; i++)
            if (!DeviceThemePresets.TryParseColor(texts[i], out colors[i]))
            {
                error = $"{names[i]}: \"{texts[i]}\" is not a #rrggbb color";
                return false;
            }
        if (edit.BackgroundBitmapId is < 0 or > 255 || edit.FanBitmapId is < 0 or > 255)
        {
            error = "the bitmap ids are 0 to 255";
            return false;
        }
        values = new DeviceThemeValues(colors[0], colors[1], colors[2], colors[3], edit.DisplayInversion,
            (byte)edit.BackgroundBitmapId, (byte)edit.FanBitmapId);

        if (!TryImage(edit.Background, BgBytes, "background image (320x170)", out background, out error)) return false;
        if (!TryImage(edit.FanFrame1, FanBytes, "fan frame 1 (73x73)", out fan1, out error)) return false;
        if (!TryImage(edit.FanFrame2, FanBytes, "fan frame 2 (73x73)", out fan2, out error)) return false;
        if (requireFans && background != null && (fan1 == null || fan2 == null))
        {
            error = "a custom background needs its two fan frames";
            return false;
        }
        if (requireFans && background == null && (fan1 != null || fan2 != null))
        {
            error = "fan frames are uploaded with a custom background only";
            return false;
        }
        return true;
    }

    private static bool TryImage(string? base64, int size, string what, out byte[]? bytes, out string? error)
    {
        bytes = null;
        error = null;
        if (string.IsNullOrEmpty(base64)) return true;
        // Base64 of `size` bytes is about 4/3 of it; anything far longer is not one.
        if (base64.Length > size / 3 * 4 + 8) { error = $"{what}: too large"; return false; }
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { error = $"{what}: not valid base64"; return false; }
        if (bytes.Length != size) { error = $"{what}: {bytes.Length} bytes, expected {size} (RGB565)"; return false; }
        return true;
    }

    /// <summary>Slot ids the device can take: the known ones, or the one it already
    /// has (an id this version does not know is kept, not refused).</summary>
    private static string? CheckIds(DeviceThemeValues wanted, DeviceThemeValues current)
    {
        bool bgOk = wanted.BackgroundBitmapId == current.BackgroundBitmapId
                    || DeviceThemePresets.Backgrounds.Any(b => b.Value == wanted.BackgroundBitmapId);
        bool fanOk = wanted.FanBitmapId == current.FanBitmapId
                     || DeviceThemePresets.Fans.Any(f => f.Value == wanted.FanBitmapId);
        if (!bgOk) return $"unknown background slot {wanted.BackgroundBitmapId}";
        if (!fanOk) return $"unknown fan slot {wanted.FanBitmapId}";
        return null;
    }

    /// <summary>The fan slot the app restores with a background slot.</summary>
    internal static WireViewPro2Device.THEME_FAN DefaultFanForBackground(WireViewPro2Device.THEME_BACKGROUND bg) => bg switch
    {
        WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark => WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark,
        _ => WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange,
    };

    /// <summary>The app's slices of ext_flash.bin (offsets in the file, which differ
    /// from the device's SPI addresses).</summary>
    internal static bool TrySliceFactory(byte[] image, WireViewPro2Device.THEME_BACKGROUND background,
        out byte[] bg, out WireViewPro2Device.THEME_FAN fan, out byte[] fan1, out byte[] fan2, out string? error)
    {
        bg = fan1 = fan2 = Array.Empty<byte>();
        fan = DefaultFanForBackground(background);
        error = null;
        int bgOffset = background == WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark ? 108800 : 0;
        var (o1, o2) = fan switch
        {
            WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark => (351512, 372832),
            WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite => (383492, 394152),
            _ => (340852, 362172),
        };
        foreach (int end in new[] { bgOffset + BgBytes, o1 + FanBytes, o2 + FanBytes })
            if (end > image.Length)
            {
                error = $"{FactoryFileName} is {image.Length} bytes, too short for the factory artwork.";
                return false;
            }
        bg = image.AsSpan(bgOffset, BgBytes).ToArray();
        fan1 = image.AsSpan(o1, FanBytes).ToArray();
        fan2 = image.AsSpan(o2, FanBytes).ToArray();
        return true;
    }

    /// <summary>A .wv2t file for these values (the app's and upstream's format,
    /// written by the shared code).</summary>
    public static byte[] BuildFile(DeviceThemeValues values, byte[]? background) =>
        ThemeFileFormat.Serialize(ThemeFileFormat.Create(values, background));

    /// <summary>Reports on the reporting thread: <see cref="Progress{T}"/> would
    /// post to the thread pool, which can reorder the reports.</summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
