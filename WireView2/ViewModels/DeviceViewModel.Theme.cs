using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MsgBox;
using WireView2.Device;
using WireView2.Services;

namespace WireView2.ViewModels;

public enum BackgroundImportFitMode
{
    Stretch,
    Uniform,
    UniformToFill,
}

/// <summary>
/// Theme editor (ported from the upstream 1.0.7 Windows client): live preview of
/// the device's main screen with animated fan, custom background import,
/// .wv2t theme files, and factory asset restore from the bundled ext_flash.bin.
/// The upstream image pipeline used GDI+, which does not exist on Linux — all
/// raster work here is pure Avalonia (WriteableBitmap / RenderTargetBitmap).
/// Bulk SPI transfers route through <see cref="DirectSerialSession"/> when the
/// device is connected via the hwmon daemon.
/// </summary>
public sealed partial class DeviceViewModel
{
    private const int ThemeBgWidth = WireViewPro2Device.ThemeBackgroundWidth;    // 320
    private const int ThemeBgHeight = WireViewPro2Device.ThemeBackgroundHeight;  // 170
    private const int ThemeBgBytes = WireViewPro2Device.ThemeBackgroundSizeBytes; // 108800
    private const int ThemeFanSize = WireViewPro2Device.ThemeFanWidth;           // 73
    private const int ThemeFanBytes = WireViewPro2Device.ThemeFanFrameSizeBytes; // 10658
    private const string FactoryExtFlashFileName = "ext_flash.bin";

    // ---- Preview state ----
    private IImage? _themeBackgroundPreview;
    private bool _isThemePreviewBusy;
    private IImage? _cachedThemeBackgroundImage;
    private IImage? _cachedThemeBackgroundImageInverted;
    private IImage? _cachedThemeFanFrame1;
    private IImage? _cachedThemeFanFrame2;
    private IImage? _cachedThemeFanFrame1Inverted;
    private IImage? _cachedThemeFanFrame2Inverted;
    private bool _fanPreviewUseFrame1 = true;
    private DispatcherTimer? _fanPreviewTimer;
    private CancellationTokenSource? _themePreviewCts;

    // ---- Pending background import state ----
    private string? _pendingBackgroundFilePath;
    private byte[]? _pendingBackgroundRgb565;
    private IImage? _pendingBackgroundPreviewImage;
    private IImage? _pendingBackgroundPreviewImageInverted;
    private IImage? _pendingFanPreviewFrame1;
    private IImage? _pendingFanPreviewFrame2;
    private IImage? _pendingFanPreviewFrame1Inverted;
    private IImage? _pendingFanPreviewFrame2Inverted;
    private CancellationTokenSource? _pendingBackgroundCts;
    private long _pendingBackgroundGeneration;

    private BackgroundImportFitMode _backgroundImportFit = BackgroundImportFitMode.UniformToFill;
    private double _backgroundImportScale = 1.0;
    private int _backgroundImportOffsetX;
    private int _backgroundImportOffsetY;

    private bool _isThemeUploadBusy;
    private double _themeUploadProgress;

    /// <summary>Best-known current device screen: the last one this app commanded
    /// (screen dropdown or after-connect setting). The protocol has no way to query
    /// the actual screen, so a change made with the device's physical button is
    /// invisible to us.</summary>
    private WireViewPro2Device.SCREEN_CMD _lastCommandedScreen =
        WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN;

    // ======================== Bound properties ========================

    public IImage? ThemeBackgroundPreview
    {
        get => _themeBackgroundPreview;
        private set => Set(ref _themeBackgroundPreview, value);
    }

    public bool IsThemePreviewBusy
    {
        get => _isThemePreviewBusy;
        private set => Set(ref _isThemePreviewBusy, value);
    }

    public bool IsThemeUploadBusy
    {
        get => _isThemeUploadBusy;
        private set => Set(ref _isThemeUploadBusy, value);
    }

    public double ThemeUploadProgress
    {
        get => _themeUploadProgress;
        private set => Set(ref _themeUploadProgress, value);
    }

    public BackgroundImportFitMode[] BackgroundImportFitModes { get; } =
        Enum.GetValues<BackgroundImportFitMode>();

    public BackgroundImportFitMode BackgroundImportFit
    {
        get => _backgroundImportFit;
        set { if (Set(ref _backgroundImportFit, value)) _ = RegeneratePendingBackgroundAsync(); }
    }

    public double BackgroundImportScale
    {
        get => _backgroundImportScale;
        set { if (Set(ref _backgroundImportScale, Math.Clamp(value, 0.1, 5.0))) _ = RegeneratePendingBackgroundAsync(); }
    }

    public int BackgroundImportOffsetX
    {
        get => _backgroundImportOffsetX;
        set { if (Set(ref _backgroundImportOffsetX, value)) _ = RegeneratePendingBackgroundAsync(); }
    }

    public int BackgroundImportOffsetY
    {
        get => _backgroundImportOffsetY;
        set { if (Set(ref _backgroundImportOffsetY, value)) _ = RegeneratePendingBackgroundAsync(); }
    }

    public bool HasPendingBackgroundUpload => _pendingBackgroundFilePath != null;

    public bool CanLoadSaveThemeFile => IsUiV2Supported;

    // ======================== Wiring (called from the constructor) ========================

    private void InitializeThemeEditor()
    {
        _fanPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100.0) };
        _fanPreviewTimer.Tick += delegate
        {
            _fanPreviewUseFrame1 = !_fanPreviewUseFrame1;
            RecomposeThemePreviewFromCache();
        };

        PropertyChanged += OnThemeRelatedPropertyChanged;
    }

    private void OnThemeRelatedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsUiV2Supported):
                if (IsUiV2Supported) RequestThemePreviewRefresh(force: true);
                else ClearThemePreview();
                break;

            // Raised on every (re)connect announce — including the hwmon entry
            // upgrading to daemon-backed a few seconds after startup, which is
            // when the first automatic preview read becomes possible.
            case nameof(IsConnected):
                if (!IsConnected) ClearThemePreview();
                else if (IsUiV2Supported && ThemeBackgroundPreview == null)
                    RequestThemePreviewRefresh(force: true);
                break;

            // Colors/inversion only change the composited overlay — recompose from cache.
            case nameof(UiPrimaryColor):
            case nameof(UiSecondaryColor):
            case nameof(UiBackgroundColor):
            case nameof(UiDisplayInversionEnabled):
                Dispatcher.UIThread.Post(RecomposeThemePreviewFromCache);
                break;

            // Highlight tints the fan template — regenerate the preview fan frames.
            case nameof(UiHighlightColor):
                Dispatcher.UIThread.Post(UpdateFanPreviewFramesForCurrentState);
                break;

            // Different slot selected — the on-device asset must be re-read.
            case nameof(UiBackgroundBitmap):
            case nameof(UiFanBitmap):
                RequestThemePreviewRefresh(force: true);
                break;
        }
    }

    private void ClearThemePreview()
    {
        void Clear()
        {
            StopFanPreview();
            ThemeBackgroundPreview = null;
            _cachedThemeBackgroundImage = null;
            _cachedThemeBackgroundImageInverted = null;
            _cachedThemeFanFrame1 = null;
            _cachedThemeFanFrame2 = null;
            _cachedThemeFanFrame1Inverted = null;
            _cachedThemeFanFrame2Inverted = null;
        }
        if (Dispatcher.UIThread.CheckAccess()) Clear();
        else Dispatcher.UIThread.Post(Clear);
    }

    // ======================== Device access routing ========================

    /// <summary>Theme assets only exist on the direct serial protocol. Over the
    /// hwmon daemon, borrow the port for the duration of the operation.</summary>
    private async Task<T> RunOnSerialDeviceAsync<T>(Func<WireViewPro2Device, Task<T>> op)
    {
        if (_device is WireViewPro2Device { Connected: true } pro2)
            return await op(pro2).ConfigureAwait(false);
        if (_device is HwmonDevice { Connected: true, DaemonAvailable: true } hwmon)
            return await DirectSerialSession.RunAsync(hwmon, op).ConfigureAwait(false);
        throw new InvalidOperationException("Theme assets require a locally connected device.");
    }

    // ======================== Preview ========================

    [RelayCommand]
    private Task RefreshThemePreview()
    {
        RequestThemePreviewRefresh(force: true);
        return Task.CompletedTask;
    }

    private void RequestThemePreviewRefresh(bool force = false)
    {
        if (!force && _cachedThemeFanFrame1 != null && _cachedThemeFanFrame2 != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                RecomposeThemePreviewFromCache();
                StartFanPreview();
            });
        }
        else if (force || (IsConnected && IsUiV2Supported))
        {
            _themePreviewCts?.Cancel();
            _themePreviewCts = new CancellationTokenSource();
            CancellationToken ct = _themePreviewCts.Token;
            _ = Task.Run(() => LoadThemePreviewAsync(ct), ct);
        }
    }

    private void StartFanPreview()
    {
        if (_fanPreviewTimer is { IsEnabled: false })
            _fanPreviewTimer.Start();
    }

    private void StopFanPreview()
    {
        if (_fanPreviewTimer is { IsEnabled: true })
            _fanPreviewTimer.Stop();
    }

    private async Task LoadThemePreviewAsync(CancellationToken ct)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsThemePreviewBusy = true);
            if (!IsConnected || !IsUiV2Supported)
            {
                ClearThemePreview();
                return;
            }

            var backgroundSlot = UiBackgroundBitmap;
            var fanSlot = UiFanBitmap;

            var (bgBytes, fan1Bytes, fan2Bytes) = await RunOnSerialDeviceAsync(async dev =>
            {
                byte[]? bg = backgroundSlot != WireViewPro2Device.THEME_BACKGROUND.Disabled
                    ? await dev.ReadThemeBackgroundRgb565Async(backgroundSlot, ct).ConfigureAwait(false)
                    : null;
                var (f1, f2) = await dev.ReadThemeFanRgb565Async(fanSlot, ct).ConfigureAwait(false);
                return (bg, f1, f2);
            }).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IImage? bgImage = bgBytes != null ? Rgb565ToImage(bgBytes, ThemeBgWidth, ThemeBgHeight, columnMajor: true) : null;
                IImage fan1Image = Rgb565ToImage(fan1Bytes, ThemeFanSize, ThemeFanSize, columnMajor: true);
                IImage fan2Image = Rgb565ToImage(fan2Bytes, ThemeFanSize, ThemeFanSize, columnMajor: true);

                _cachedThemeBackgroundImage = bgImage;
                _cachedThemeFanFrame1 = fan1Image;
                _cachedThemeFanFrame2 = fan2Image;
                _cachedThemeBackgroundImageInverted = CreateInvertedImage(bgImage);
                _cachedThemeFanFrame1Inverted = CreateInvertedImage(fan1Image);
                _cachedThemeFanFrame2Inverted = CreateInvertedImage(fan2Image);
                _fanPreviewUseFrame1 = true;
                UpdateFanPreviewFramesForCurrentState();
                RecomposeThemePreviewFromCache();
                StartFanPreview();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ThemeBackgroundPreview = null;
                ConfigStatus = "Theme preview failed: " + ex.Message;
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsThemePreviewBusy = false);
        }
    }

    private void RecomposeThemePreviewFromCache()
    {
        if (_cachedThemeFanFrame1 == null || _cachedThemeFanFrame2 == null)
            return;

        IImage? backgroundImage;
        if (_pendingBackgroundPreviewImage != null)
        {
            backgroundImage = UiDisplayInversionEnabled
                ? _pendingBackgroundPreviewImageInverted
                : _pendingBackgroundPreviewImage;
        }
        else if (UiBackgroundBitmap == WireViewPro2Device.THEME_BACKGROUND.Disabled)
        {
            backgroundImage = CreateSolidBitmap(ThemeBgWidth, ThemeBgHeight, UiBackgroundColorDisplay);
        }
        else
        {
            backgroundImage = UiDisplayInversionEnabled
                ? _cachedThemeBackgroundImageInverted
                : _cachedThemeBackgroundImage;
        }

        IImage? normal = _fanPreviewUseFrame1
            ? _pendingFanPreviewFrame1 ?? _cachedThemeFanFrame1
            : _pendingFanPreviewFrame2 ?? _cachedThemeFanFrame2;
        IImage? inverted = _fanPreviewUseFrame1
            ? _pendingFanPreviewFrame1Inverted ?? _cachedThemeFanFrame1Inverted
            : _pendingFanPreviewFrame2Inverted ?? _cachedThemeFanFrame2Inverted;
        IImage? fan = UiDisplayInversionEnabled ? inverted : normal;
        if (fan != null)
            ThemeBackgroundPreview = ComposeMainScreenPreview(backgroundImage, fan);
    }

    private void UpdateFanPreviewFramesForCurrentState()
    {
        var highlight = UiHighlightColor;
        IImage sourceBackground;
        if (_pendingBackgroundPreviewImage != null)
        {
            sourceBackground = _pendingBackgroundPreviewImage;
        }
        else if (UiBackgroundBitmap == WireViewPro2Device.THEME_BACKGROUND.Disabled || _cachedThemeBackgroundImage == null)
        {
            sourceBackground = CreateSolidBitmap(ThemeBgWidth, ThemeBgHeight, UiBackgroundColor);
        }
        else
        {
            sourceBackground = _cachedThemeBackgroundImage;
        }

        var (fan1, fan2) = CreateFanPreviewFramesFromBackground(sourceBackground, highlight);
        _pendingFanPreviewFrame1 = fan1;
        _pendingFanPreviewFrame2 = fan2;
        _pendingFanPreviewFrame1Inverted = CreateInvertedImage(fan1);
        _pendingFanPreviewFrame2Inverted = CreateInvertedImage(fan2);
        RecomposeThemePreviewFromCache();
        StartFanPreview();
    }

    private IImage ComposeMainScreenPreview(IImage? backgroundImage, IImage fanFrame)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(ThemeBgWidth, ThemeBgHeight), new Vector(96.0, 96.0));
        using var ctx = rtb.CreateDrawingContext();

        var bgBrush = new SolidColorBrush(UiBackgroundColorDisplay);
        var primaryBrush = new SolidColorBrush(UiPrimaryColorDisplay);
        var secondaryBrush = new SolidColorBrush(UiSecondaryColorDisplay);
        var highlightBrush = new SolidColorBrush(UiHighlightColorDisplay);

        ctx.FillRectangle(bgBrush, new Rect(0, 0, ThemeBgWidth, ThemeBgHeight));
        if (backgroundImage != null)
            ctx.DrawImage(backgroundImage, new Rect(0, 0, ThemeBgWidth, ThemeBgHeight));
        ctx.DrawImage(fanFrame, new Rect(239, 47, ThemeFanSize, ThemeFanSize));

        // Mock of the firmware's main screen chrome, so color edits are judged in context.
        ctx.DrawRectangle(bgBrush, new Pen(highlightBrush, 2.0), new Rect(182, 17, 195, 26));
        var linePen = new Pen(new SolidColorBrush(UiHighlightColorDisplay, 46.0 / 51.0), 2.0);
        ctx.DrawLine(linePen, new Point(15, 16), new Point(134, 16));
        ctx.DrawLine(linePen, new Point(15, 104), new Point(134, 104));
        DrawUiText(ctx, "TEMP", 186, 20, primaryBrush, 13);
        DrawUiText(ctx, "SENSE", 250, 20, primaryBrush, 13);
        DrawUiText(ctx, "POWER", 126, 115, primaryBrush, 26);
        DrawUiText(ctx, "FAN", 243, 126, primaryBrush, 13);
        for (int i = 0; i < 6; i++)
        {
            double x = 15 + i * 20;
            ctx.FillRectangle(secondaryBrush, new Rect(x, 20, 10, 80));
            ctx.FillRectangle(highlightBrush, new Rect(x, 70, 10, 30));
        }
        ctx.FillRectangle(secondaryBrush, new Rect(15, 120, 110, 30));
        ctx.FillRectangle(highlightBrush, new Rect(15, 120, 20, 30));
        return rtb;

        static void DrawUiText(DrawingContext ctx, string text, double x, double y, IBrush brush, double size)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, size, brush);
            ctx.DrawText(ft, new Point(x, y));
        }
    }

    // ======================== Pixel helpers (pure Avalonia) ========================

    private static WriteableBitmap CreateSolidBitmap(int width, int height, Color color)
    {
        var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96.0, 96.0),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = wb.Lock();
        var row = new byte[width * 4];
        for (int x = 0; x < width; x++)
        {
            row[x * 4] = color.B;
            row[x * 4 + 1] = color.G;
            row[x * 4 + 2] = color.R;
            row[x * 4 + 3] = 255;
        }
        for (int y = 0; y < height; y++)
            Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
        return wb;
    }

    /// <summary>Decodes device RGB565 (column-major, horizontally mirrored) into a
    /// Bgra8888 bitmap.</summary>
    private static WriteableBitmap Rgb565ToImage(byte[] rgb565, int width, int height, bool columnMajor)
    {
        if (rgb565.Length != width * height * 2)
            throw new ArgumentException("RGB565 buffer size does not match expected dimensions.", nameof(rgb565));

        var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96.0, 96.0),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var pixels = new byte[width * height * 4];
        var src = rgb565.AsSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcIndex = (columnMajor ? x * height + y : y * width + x) * 2;
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(srcIndex, 2));
                int r5 = (v >> 11) & 0x1F;
                int g6 = (v >> 5) & 0x3F;
                int b5 = v & 0x1F;
                byte r = (byte)((r5 << 3) | (r5 >> 2));
                byte g = (byte)((g6 << 2) | (g6 >> 4));
                byte b = (byte)((b5 << 3) | (b5 >> 2));
                int dst = (y * width + (width - 1 - x)) * 4;
                pixels[dst] = b;
                pixels[dst + 1] = g;
                pixels[dst + 2] = r;
                pixels[dst + 3] = 255;
            }
        }
        CopyIntoBitmap(wb, pixels, width, height);
        return wb;
    }

    /// <summary>Encodes a Bgra8888 73x73 fan frame back to the device layout
    /// (RGB565, column-major, horizontally mirrored) — the inverse of Rgb565ToImage.</summary>
    private static byte[] RenderFanPreviewToRgb565(IImage image)
    {
        if (image is not WriteableBitmap wb)
            throw new InvalidOperationException("Fan preview frame is not a writable bitmap.");

        int width = wb.PixelSize.Width, height = wb.PixelSize.Height;
        var pixels = ReadBitmapPixels(wb);
        var result = new byte[width * height * 2];
        var span = result.AsSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int src = (y * width + x) * 4;
                byte b = pixels[src], g = pixels[src + 1], r = pixels[src + 2];
                ushort v = (ushort)((r >> 3 << 11) | (g >> 2 << 5) | (b >> 3));
                int dstIndex = ((width - 1 - x) * height + y) * 2;
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(dstIndex, 2), v);
            }
        }
        return result;
    }

    private static IImage? CreateInvertedImage(IImage? image)
    {
        if (image is not WriteableBitmap wb)
            return image;

        int width = wb.PixelSize.Width, height = wb.PixelSize.Height;
        var pixels = ReadBitmapPixels(wb);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(255 - pixels[i]);
            pixels[i + 1] = (byte)(255 - pixels[i + 1]);
            pixels[i + 2] = (byte)(255 - pixels[i + 2]);
        }
        var inverted = new WriteableBitmap(wb.PixelSize, wb.Dpi, PixelFormat.Bgra8888, AlphaFormat.Opaque);
        CopyIntoBitmap(inverted, pixels, width, height);
        return inverted;
    }

    private static byte[] ReadBitmapPixels(WriteableBitmap wb)
    {
        int width = wb.PixelSize.Width, height = wb.PixelSize.Height;
        var pixels = new byte[width * height * 4];
        using var fb = wb.Lock();
        for (int y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(fb.Address, y * fb.RowBytes), pixels, y * width * 4, width * 4);
        return pixels;
    }

    private static void CopyIntoBitmap(WriteableBitmap wb, byte[] pixels, int width, int height)
    {
        using var fb = wb.Lock();
        int rowBytes = width * 4;
        if (fb.RowBytes == rowBytes)
        {
            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        else
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(fb.Address, y * fb.RowBytes), rowBytes);
        }
    }

    /// <summary>Builds the two preview fan frames: the 73x73 crop of the background
    /// under the fan window with the (highlight-tinted) fan template alpha-blended
    /// on top. Replaces the upstream GDI+ implementation.</summary>
    private static (IImage Fan1, IImage Fan2) CreateFanPreviewFramesFromBackground(
        IImage backgroundPreviewImage, Color highlightColor)
    {
        if (backgroundPreviewImage is not WriteableBitmap bg)
        {
            var empty = new WriteableBitmap(new PixelSize(ThemeFanSize, ThemeFanSize),
                new Vector(96.0, 96.0), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            return (empty, empty);
        }

        return (MakeFrame("avares://WireView2/Assets/DeviceAssets/fan1.png"),
                MakeFrame("avares://WireView2/Assets/DeviceAssets/fan2.png"));

        IImage MakeFrame(string templateUri)
        {
            const int size = ThemeFanSize;
            // 1. Crop the fan window (239,47 .. 312,120) out of the background.
            var basePixels = new byte[size * size * 4];
            var bgPixels = ReadBitmapPixels(bg);
            int bgWidth = bg.PixelSize.Width, bgHeight = bg.PixelSize.Height;
            for (int y = 0; y < size; y++)
            {
                int srcY = 47 + y;
                if ((uint)srcY >= (uint)bgHeight) continue;
                for (int x = 0; x < size; x++)
                {
                    int srcX = 239 + x;
                    if ((uint)srcX >= (uint)bgWidth) continue;
                    Array.Copy(bgPixels, (srcY * bgWidth + srcX) * 4, basePixels, (y * size + x) * 4, 4);
                }
            }

            // 2. Load the fan template (white-on-transparent PNG) and read its pixels.
            using var stream = AssetLoader.Open(new Uri(templateUri, UriKind.Absolute));
            var template = WriteableBitmap.Decode(stream);
            var templatePixels = ReadBitmapPixels(template);
            int tw = template.PixelSize.Width, th = template.PixelSize.Height;

            // 3. Tint by highlight and alpha-blend over the crop.
            byte hr = highlightColor.R, hg = highlightColor.G, hb = highlightColor.B;
            for (int y = 0; y < size && y < th; y++)
            {
                for (int x = 0; x < size && x < tw; x++)
                {
                    int t = (y * tw + x) * 4;
                    byte a = templatePixels[t + 3];
                    if (a == 0) continue;
                    int d = (y * size + x) * 4;
                    byte tintB = (byte)(hb * templatePixels[t] / 255);
                    byte tintG = (byte)(hg * templatePixels[t + 1] / 255);
                    byte tintR = (byte)(hr * templatePixels[t + 2] / 255);
                    basePixels[d] = (byte)((tintB * a + basePixels[d] * (255 - a)) / 255);
                    basePixels[d + 1] = (byte)((tintG * a + basePixels[d + 1] * (255 - a)) / 255);
                    basePixels[d + 2] = (byte)((tintR * a + basePixels[d + 2] * (255 - a)) / 255);
                    basePixels[d + 3] = 255;
                }
            }

            var frame = new WriteableBitmap(new PixelSize(size, size), new Vector(96.0, 96.0),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            CopyIntoBitmap(frame, basePixels, size, size);
            return frame;
        }
    }

    // ======================== Background import ========================

    public async Task UploadBackgroundFromFileAsync(string filePath)
    {
        try
        {
            _pendingBackgroundFilePath = filePath;
            await RegeneratePendingBackgroundAsync();
        }
        catch (Exception ex)
        {
            ConfigStatus = "Background import failed: " + ex.Message;
        }
    }

    public void ClearPendingBackground()
    {
        _pendingBackgroundFilePath = null;
        _pendingBackgroundRgb565 = null;
        _pendingBackgroundPreviewImage = null;
        _pendingBackgroundPreviewImageInverted = null;
        _pendingFanPreviewFrame1 = null;
        _pendingFanPreviewFrame2 = null;
        _pendingFanPreviewFrame1Inverted = null;
        _pendingFanPreviewFrame2Inverted = null;
        OnPropertyChanged(nameof(HasPendingBackgroundUpload));
        Dispatcher.UIThread.Post(RecomposeThemePreviewFromCache);
    }

    private async Task RegeneratePendingBackgroundAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingBackgroundFilePath)
            || _pendingBackgroundFilePath == "(theme file)")
            return;

        _pendingBackgroundCts?.Cancel();
        _pendingBackgroundCts = new CancellationTokenSource();
        CancellationToken ct = _pendingBackgroundCts.Token;
        long gen = Interlocked.Increment(ref _pendingBackgroundGeneration);
        try
        {
            string filePath = _pendingBackgroundFilePath;
            var fit = BackgroundImportFit;
            double scale = BackgroundImportScale;
            int offsetX = BackgroundImportOffsetX, offsetY = BackgroundImportOffsetY;

            var (rgb565, previewImage) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                byte[] bytes = LoadImageFileAsRgb565(filePath, ThemeBgWidth, ThemeBgHeight, fit, scale, offsetX, offsetY);
                var img = Rgb565ToImage(bytes, ThemeBgWidth, ThemeBgHeight, columnMajor: true);
                return (bytes, img);
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (gen != Volatile.Read(ref _pendingBackgroundGeneration))
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (gen != Volatile.Read(ref _pendingBackgroundGeneration))
                    return;
                _pendingBackgroundRgb565 = rgb565;
                _pendingBackgroundPreviewImage = previewImage;
                _pendingBackgroundPreviewImageInverted = CreateInvertedImage(previewImage);
                UpdateFanPreviewFramesForCurrentState();
                OnPropertyChanged(nameof(HasPendingBackgroundUpload));
                ConfigStatus = "Background staged (press Apply theme to upload).";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ConfigStatus = "Background import failed: " + ex.Message);
        }
    }

    /// <summary>Rasterizes an image file to the device's 320x170 RGB565 layout with
    /// the requested fit/scale/offset over a black canvas. Pure Avalonia — the
    /// upstream GDI+ implementation does not exist on Linux.</summary>
    private static byte[] LoadImageFileAsRgb565(string filePath, int targetWidth, int targetHeight,
        BackgroundImportFitMode fitMode, double scale, int offsetX, int offsetY)
    {
        using var src = new Bitmap(filePath);
        int srcW = src.PixelSize.Width, srcH = src.PixelSize.Height;

        var canvas = new byte[targetWidth * targetHeight * 4]; // black, opaque
        for (int i = 3; i < canvas.Length; i += 4) canvas[i] = 255;

        if (srcW > 0 && srcH > 0)
        {
            double sx = (double)targetWidth / srcW;
            double sy = (double)targetHeight / srcH;
            double fx, fy;
            switch (fitMode)
            {
                case BackgroundImportFitMode.Uniform:
                    fx = fy = Math.Min(sx, sy) * scale;
                    break;
                case BackgroundImportFitMode.UniformToFill:
                    fx = fy = Math.Max(sx, sy) * scale;
                    break;
                default:
                    fx = sx * scale;
                    fy = sy * scale;
                    break;
            }

            int drawW = Math.Max(1, (int)Math.Round(srcW * fx));
            int drawH = Math.Max(1, (int)Math.Round(srcH * fy));
            int originX = (int)Math.Round((targetWidth - (double)drawW) / 2.0) + offsetX;
            int originY = (int)Math.Round((targetHeight - (double)drawH) / 2.0) + offsetY;

            using var scaled = src.CreateScaledBitmap(new PixelSize(drawW, drawH), BitmapInterpolationMode.HighQuality);
            var scaledPixels = new byte[drawW * drawH * 4];
            var handle = GCHandle.Alloc(scaledPixels, GCHandleType.Pinned);
            try
            {
                scaled.CopyPixels(new PixelRect(0, 0, drawW, drawH),
                    handle.AddrOfPinnedObject(), scaledPixels.Length, drawW * 4);
            }
            finally
            {
                handle.Free();
            }

            for (int y = 0; y < drawH; y++)
            {
                int ty = originY + y;
                if ((uint)ty >= (uint)targetHeight) continue;
                for (int x = 0; x < drawW; x++)
                {
                    int tx = originX + x;
                    if ((uint)tx >= (uint)targetWidth) continue;
                    int s = (y * drawW + x) * 4;
                    int d = (ty * targetWidth + tx) * 4;
                    byte a = scaledPixels[s + 3];
                    canvas[d] = (byte)(scaledPixels[s] * a / 255);
                    canvas[d + 1] = (byte)(scaledPixels[s + 1] * a / 255);
                    canvas[d + 2] = (byte)(scaledPixels[s + 2] * a / 255);
                    canvas[d + 3] = 255;
                }
            }
        }

        var result = new byte[targetWidth * targetHeight * 2];
        var span = result.AsSpan();
        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                int s = (y * targetWidth + x) * 4;
                byte b = canvas[s], g = canvas[s + 1], r = canvas[s + 2];
                ushort v = (ushort)((r >> 3 << 11) | (g >> 2 << 5) | (b >> 3));
                int dstIndex = ((targetWidth - 1 - x) * targetHeight + y) * 2;
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(dstIndex, 2), v);
            }
        }
        return result;
    }

    // ======================== Apply integration ========================

    /// <summary>Uploads a staged background (and matching tinted fan frames) to the
    /// device. Called from ApplyConfig; no-op when nothing is staged.</summary>
    private async Task UploadPendingThemeAssetsAsync()
    {
        if (_pendingBackgroundFilePath == null)
            return;
        if (!IsUiV2Supported)
            throw new InvalidOperationException("Theme assets are only supported on config v2+ devices.");
        if (UiBackgroundBitmap == WireViewPro2Device.THEME_BACKGROUND.Disabled)
            throw new InvalidOperationException("Select a background slot (not Disabled) before applying a staged background.");
        if (_pendingBackgroundRgb565 == null || _pendingBackgroundPreviewImage == null)
            throw new InvalidOperationException("Staged background is not ready yet.");

        ConfigStatus = "Uploading background to device…";
        IsThemeUploadBusy = true;
        ThemeUploadProgress = 0;
        try
        {
            var highlight = UiHighlightColor;
            byte[] bgBytes = _pendingBackgroundRgb565;
            var backgroundSlot = UiBackgroundBitmap;
            var fanSlot = UiFanBitmap;

            var (fan1Bytes, fan2Bytes) = await Task.Run(() =>
            {
                var (f1, f2) = CreateFanPreviewFramesFromBackground(_pendingBackgroundPreviewImage!, highlight);
                return (RenderFanPreviewToRgb565(f1), RenderFanPreviewToRgb565(f2));
            }).ConfigureAwait(false);

            var bgProgress = new Progress<double>(p => ThemeUploadProgress = p * 0.75);
            var fanProgress = new Progress<double>(p => ThemeUploadProgress = 0.75 + p * 0.25);
            await RunOnSerialDeviceAsync<object?>(async dev =>
            {
                await dev.WriteThemeBackgroundRgb565Async(backgroundSlot, bgBytes, bgProgress).ConfigureAwait(false);
                await dev.WriteThemeFanRgb565Async(fanSlot, fan1Bytes, fan2Bytes, fanProgress).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cachedThemeFanFrame1 = Rgb565ToImage(fan1Bytes, ThemeFanSize, ThemeFanSize, columnMajor: true);
                _cachedThemeFanFrame2 = Rgb565ToImage(fan2Bytes, ThemeFanSize, ThemeFanSize, columnMajor: true);
                _cachedThemeFanFrame1Inverted = CreateInvertedImage(_cachedThemeFanFrame1);
                _cachedThemeFanFrame2Inverted = CreateInvertedImage(_cachedThemeFanFrame2);
                _cachedThemeBackgroundImage = _pendingBackgroundPreviewImage;
                _cachedThemeBackgroundImageInverted = _pendingBackgroundPreviewImageInverted;
                ClearPendingBackground();
                ConfigStatus = "Background uploaded.";
            });
        }
        finally
        {
            IsThemeUploadBusy = false;
            ThemeUploadProgress = 1.0;
        }
    }

    // ======================== Theme files (.wv2t) ========================

    public async Task SaveThemeToFileAsync(string filePath)
    {
        try
        {
            if (!IsUiV2Supported)
            {
                ConfigStatus = "Theme files are only supported on config v2+ devices.";
                return;
            }

            byte[]? bgBytes = _pendingBackgroundRgb565;
            if (bgBytes == null && _cachedThemeBackgroundImage != null
                && UiBackgroundBitmap != WireViewPro2Device.THEME_BACKGROUND.Disabled)
            {
                try
                {
                    var slot = UiBackgroundBitmap;
                    bgBytes = await RunOnSerialDeviceAsync(dev =>
                        dev.ReadThemeBackgroundRgb565Async(slot)).ConfigureAwait(false);
                }
                catch
                {
                    // Colors-only theme file is still useful.
                }
            }

            await ThemeFile.SaveAsync(filePath, UiPrimaryColor, UiSecondaryColor, UiHighlightColor,
                UiBackgroundColor, UiDisplayInversionEnabled, UiBackgroundBitmap, UiFanBitmap, bgBytes)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
                ConfigStatus = "Theme saved: " + Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ConfigStatus = "Theme save failed: " + ex.Message);
        }
    }

    public async Task LoadThemeFromFileAsync(string filePath)
    {
        try
        {
            if (!IsUiV2Supported)
            {
                ConfigStatus = "Theme files are only supported on config v2+ devices.";
                return;
            }

            var doc = await ThemeFile.LoadAsync(filePath).ConfigureAwait(false);
            byte[]? bgBytes = ThemeFile.TryDecodeBackgroundRgb565(doc, ThemeBgBytes);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UiPrimaryColor = ArgbToColor(doc.PrimaryColor);
                UiSecondaryColor = ArgbToColor(doc.SecondaryColor);
                UiHighlightColor = ArgbToColor(doc.HighlightColor);
                UiBackgroundColor = ArgbToColor(doc.BackgroundColor);
                UiDisplayInversionEnabled = doc.DisplayInversion;
                var background = (WireViewPro2Device.THEME_BACKGROUND)doc.BackgroundBitmapId;
                UiBackgroundBitmap = Enum.IsDefined(background) ? background : WireViewPro2Device.THEME_BACKGROUND.Disabled;
                var fan = (WireViewPro2Device.THEME_FAN)doc.FanBitmapId;
                UiFanBitmap = Enum.IsDefined(fan) ? fan : MapFanBitmapFromBackground(UiBackgroundBitmap);

                if (bgBytes != null)
                {
                    _pendingBackgroundFilePath = "(theme file)";
                    _pendingBackgroundRgb565 = bgBytes;
                    _pendingBackgroundPreviewImage = Rgb565ToImage(bgBytes, ThemeBgWidth, ThemeBgHeight, columnMajor: true);
                    _pendingBackgroundPreviewImageInverted = CreateInvertedImage(_pendingBackgroundPreviewImage);
                    UpdateFanPreviewFramesForCurrentState();
                    OnPropertyChanged(nameof(HasPendingBackgroundUpload));
                    ConfigStatus = "Theme loaded and background staged (press Apply theme to upload).";
                }
                else
                {
                    ConfigStatus = "Theme loaded (press Apply theme to upload colors).";
                }
                SetThemePresetToCustomIfEdited();
                RequestThemePreviewRefresh(force: true);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ConfigStatus = "Theme load failed: " + ex.Message);
        }
    }

    // ======================== Factory restore ========================

    private static ReadOnlyMemory<byte> SliceFactoryAsset(ReadOnlyMemory<byte> factoryImage, uint addr, uint len)
    {
        checked
        {
            int start = (int)addr;
            int length = (int)len;
            if (start < 0 || length < 0 || start + length > factoryImage.Length)
                throw new ArgumentOutOfRangeException(nameof(factoryImage), "Factory image slice out of range.");
            return factoryImage.Slice(start, length);
        }
    }

    private static WireViewPro2Device.THEME_FAN DefaultFanForBackground(WireViewPro2Device.THEME_BACKGROUND bg) => bg switch
    {
        WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange => WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange,
        WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark => WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark,
        _ => WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange,
    };

    // Offsets within the bundled factory image — note these differ from the
    // on-device SPI flash addresses.
    private static uint GetFactoryBackgroundOffset(WireViewPro2Device.THEME_BACKGROUND background) => background switch
    {
        WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange => 0u,
        WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark => 108800u,
        _ => throw new InvalidOperationException("Unsupported background slot."),
    };

    private static (uint Fan1Offset, uint Fan2Offset) GetFactoryFanOffsets(WireViewPro2Device.THEME_FAN fan) => fan switch
    {
        WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange => (340852u, 362172u),
        WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark => (351512u, 372832u),
        WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite => (383492u, 394152u),
        _ => throw new InvalidOperationException("Unsupported fan theme."),
    };

    [RelayCommand]
    private async Task RestoreThemeAssetsToDefault()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsUiV2Supported)
            {
                ConfigStatus = "Theme assets are only supported on config v2+ devices.";
                return;
            }
            if (UiBackgroundBitmap == WireViewPro2Device.THEME_BACKGROUND.Disabled)
            {
                ConfigStatus = "Select a background slot (not Disabled) before restoring.";
                return;
            }

            string factoryPath = Path.Combine(AppContext.BaseDirectory, FactoryExtFlashFileName);
            if (!File.Exists(factoryPath))
            {
                ConfigStatus = "Factory asset image (ext_flash.bin) not found next to the application.";
                return;
            }

            var confirm = await MessageBox.Show(null,
                "This will overwrite the selected background slot and its matching fan frames with factory defaults. Continue?",
                "Restore theme assets", MessageBox.MessageBoxButtons.YesNo);
            if (confirm != MessageBox.MessageBoxResult.Yes)
                return;

            IsThemeUploadBusy = true;
            ThemeUploadProgress = 0;
            ConfigStatus = "Restoring factory theme assets…";

            // A running preview read would hold the daemon suspended and starve
            // the post-restore relay commands below.
            _themePreviewCts?.Cancel();

            var background = UiBackgroundBitmap;
            ReadOnlyMemory<byte> factoryImage = await File.ReadAllBytesAsync(factoryPath).ConfigureAwait(false);
            var bgBytes = SliceFactoryAsset(factoryImage, GetFactoryBackgroundOffset(background), (uint)ThemeBgBytes);
            var fan = DefaultFanForBackground(background);
            var (fan1Offset, fan2Offset) = GetFactoryFanOffsets(fan);
            var fan1Bytes = SliceFactoryAsset(factoryImage, fan1Offset, (uint)ThemeFanBytes);
            var fan2Bytes = SliceFactoryAsset(factoryImage, fan2Offset, (uint)ThemeFanBytes);

            var bgProgress = new Progress<double>(p => ThemeUploadProgress = 0.1 + p * 0.7);
            var fanProgress = new Progress<double>(p => ThemeUploadProgress = 0.8 + p * 0.2);
            await RunOnSerialDeviceAsync<object?>(async dev =>
            {
                await dev.WriteThemeBackgroundRgb565Async(background, bgBytes, bgProgress).ConfigureAwait(false);
                await dev.WriteThemeFanRgb565Async(fan, fan1Bytes, fan2Bytes, fanProgress).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);

            // The background is blitted from external flash when a screen is
            // ENTERED — config rewrites and SCREEN_GOTO_SAME repaint from the
            // cached framebuffer only. Detour to a different screen and return
            // to the last screen this app commanded (the protocol cannot query
            // the actual current screen).
            //
            // ORDER MATTERS: this must run BEFORE any property update that
            // triggers a theme preview refresh — the preview read suspends the
            // daemon, which would reject these relayed commands (that race made
            // earlier restores look like the redraw never happened).
            string? redrawError = null;
            try
            {
                // Let the device settle after the long SPI session before talking
                // to it again, and give the detour screen time to fully paint —
                // a hasty return command gets dropped while the device is busy.
                await Task.Delay(1000);
                await DeviceWriteConfigAsync(BuildConfigFromEditor());
                var detour = _lastCommandedScreen == WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS
                    ? WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP
                    : WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS;
                await DeviceScreenCmdAsync(detour);
                await Task.Delay(3000);
                await DeviceScreenCmdAsync(_lastCommandedScreen);
            }
            catch (Exception redrawEx)
            {
                redrawError = redrawEx.Message;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UiFanBitmap = fan;
                ClearPendingBackground();
                ConfigStatus = redrawError == null
                    ? "Factory theme assets restored."
                    : "Factory assets restored; screen refresh failed (" + redrawError +
                      "). Switch screens once to see them.";
                RequestThemePreviewRefresh(force: true);
            });
        }
        catch (Exception ex)
        {
            ConfigStatus = "Factory restore failed: " + ex.Message;
        }
        finally
        {
            IsThemeUploadBusy = false;
            ThemeUploadProgress = 1.0;
        }
    }
}
