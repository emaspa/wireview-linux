#if WINDOWS
using System.Runtime.InteropServices;
using WireView2.Net;

namespace WireView2.Mini;

/// <summary>
/// Windows tray icon through raw Win32: Shell_NotifyIcon on a hidden window
/// that runs its own message loop on a dedicated thread.
///
/// The window is a hidden top-level window (WS_POPUP, WS_EX_TOOLWINDOW, never
/// shown), not a message-only one: Explorer announces a restart by broadcasting
/// "TaskbarCreated", and message-only windows (HWND_MESSAGE) receive no
/// broadcasts, so the icon would be lost after an Explorer restart.
///
/// The menu (CreatePopupMenu, TrackPopupMenuEx with TPM_RETURNCMD) is built from
/// the latest state on every open, after SetForegroundWindow and followed by a
/// WM_NULL post, so it closes when the user clicks elsewhere (KB135788).
/// Commands run on the tray thread through the callback.
/// </summary>
internal sealed class Win32TrayIcon : ITrayIcon
{
    private const string ClassName = "WireViewMiniTrayWindow";
    private const int IconId = 1;

    private const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_USER = 0x0400, WM_APP = 0x8000;
    private const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    private const uint WM_TRAY = WM_APP + 1;      // the icon's callback message
    private const uint WM_TRAYSTATE = WM_APP + 2; // SetState: refresh the tooltip

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_USER = 0x04, NIIF_LARGE_ICON = 0x20;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_POPUP = 0x0010, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080;

    private const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const int SM_CXSMICON = 49, SM_CXICON = 11;
    private const uint NIIF_INFO = 0x01;

    // The window class keeps a raw pointer to the window procedure: the delegate
    // must stay alive as long as the class is registered, so it lives in a static.
    private static readonly WndProcDelegate s_wndProc = StaticWndProc;
    private static Win32TrayIcon? s_instance;   // one tray per process

    private readonly Action<TrayCommand> _onCommand;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private TrayState _state;
    private string _shownTip = "";
    private IntPtr _hwnd;
    private IntPtr _icon;        // the small icon in the notification area
    private IntPtr _balloonIcon; // the large one a notification shows
    private uint _taskbarCreated;
    private bool _version4;
    private bool _added;
    private bool _loggedAddFailure;

    private Win32TrayIcon(TrayState initial, Action<TrayCommand> onCommand)
    {
        _state = initial;
        _onCommand = onCommand;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "wireview-mini tray" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>The process's tray icon, for <see cref="WindowsToastNotifier"/>.</summary>
    public static Win32TrayIcon? Current => s_instance;

    /// <summary>Starts the tray thread and waits for the window; null when the
    /// window cannot be created (logged).</summary>
    public static Win32TrayIcon? TryCreate(TrayState initial, Action<TrayCommand> onCommand)
    {
        var tray = new Win32TrayIcon(initial, onCommand);
        s_instance = tray;
        tray._thread.Start();
        tray._ready.Wait(TimeSpan.FromSeconds(5));
        if (tray._hwnd != IntPtr.Zero) return tray;
        s_instance = null;
        return null;
    }

    public void SetState(TrayState state)
    {
        IntPtr hwnd;
        lock (_lock)
        {
            if (_state == state) return;
            _state = state;
            hwnd = _hwnd;
        }
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WM_TRAYSTATE, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- Tray thread ----

    private void MessageLoop()
    {
        try
        {
            IntPtr hInstance = GetModuleHandleW(null);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = hInstance,
                lpszClassName = ClassName,
            };
            RegisterClassExW(ref wc); // 0 when already registered: CreateWindowExW tells
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

            IntPtr hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, TrayState.AppTitle, WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                FileLog.Warn($"tray: cannot create the tray window (error {Marshal.GetLastWin32Error()}). Running without a tray icon.");
                return;
            }
            lock (_lock) _hwnd = hwnd;
            _icon = LoadIcon(SM_CXSMICON, 16);
            _balloonIcon = LoadIcon(SM_CXICON, 32);
            AddIcon();
            _ready.Set();

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn($"tray: the tray thread failed: {ex.Message}");
        }
        finally
        {
            _ready.Set();
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            if (_balloonIcon != IntPtr.Zero) DestroyIcon(_balloonIcon);
            _icon = _balloonIcon = IntPtr.Zero;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (s_instance is { } tray && tray.WndProc(hwnd, msg, wParam, lParam) is { } handled)
                return handled;
        }
        catch (Exception ex)
        {
            FileLog.Warn($"tray: {ex.Message}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr? WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer (re)started: its notification area knows no icons.
            _added = false;
            AddIcon();
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_TRAY:
            {
                // Version 4: the event in LOWORD(lParam), the anchor point in
                // wParam. Older shells: the mouse message is lParam itself.
                uint evt = _version4 ? (uint)(lParam.ToInt64() & 0xFFFF) : (uint)lParam.ToInt64();
                // Left click (or Enter on the icon): the control panel. Right click
                // (or Shift+F10): the menu. A double click's second press is ignored.
                bool select = _version4 ? evt is NIN_SELECT or NIN_KEYSELECT : evt == WM_LBUTTONUP;
                bool menu = _version4 ? evt == WM_CONTEXTMENU : evt == WM_RBUTTONUP;
                if (select)
                {
                    try { _onCommand(TrayCommand.OpenPanel); }
                    catch (Exception ex) { FileLog.Warn($"tray: {TrayCommand.OpenPanel} failed: {ex.Message}"); }
                }
                else if (menu)
                {
                    int x, y;
                    if (_version4)
                    {
                        long wp = wParam.ToInt64();
                        x = (short)(wp & 0xFFFF);
                        y = (short)((wp >> 16) & 0xFFFF);
                    }
                    else
                    {
                        GetCursorPos(out var pt);
                        x = pt.X;
                        y = pt.Y;
                    }
                    ShowMenu(hwnd, x, y);
                }
                return IntPtr.Zero;
            }
            case WM_TRAYSTATE:
                UpdateTip();
                return IntPtr.Zero;
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                RemoveIcon();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return null;
    }

    private void ShowMenu(IntPtr hwnd, int x, int y)
    {
        TrayState state;
        lock (_lock) state = _state;
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        int command;
        try
        {
            AppendItems(menu, state.Items);
            // Without the foreground the menu does not close on an outside click.
            SetForegroundWindow(hwnd);
            command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }

        if (command != 0 && Enum.IsDefined((TrayCommand)command) &&
            TrayState.Flatten(state.Items).Any(i => i.Id == command && i.IsEnabled))
        {
            try { _onCommand((TrayCommand)command); }
            catch (Exception ex) { FileLog.Warn($"tray: {(TrayCommand)command} failed: {ex.Message}"); }
        }
    }

    /// <summary>Appends the lines to a menu; a submenu becomes a child popup menu,
    /// which DestroyMenu on the parent destroys with it.</summary>
    private static void AppendItems(IntPtr menu, IReadOnlyList<TrayItem> items)
    {
        foreach (var item in items)
        {
            uint grayed = item.IsEnabled ? 0 : MF_GRAYED;
            switch (item.Kind)
            {
                case TrayItemKind.Separator:
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    break;
                case TrayItemKind.Header:
                case TrayItemKind.Action:
                    AppendMenuW(menu, MF_STRING | grayed, (UIntPtr)item.Id, MenuText(item.Label));
                    break;
                case TrayItemKind.Check:
                    AppendMenuW(menu, MF_STRING | grayed | (item.Checked ? MF_CHECKED : 0), (UIntPtr)item.Id, MenuText(item.Label));
                    break;
                case TrayItemKind.Submenu:
                {
                    IntPtr sub = CreatePopupMenu();
                    if (sub == IntPtr.Zero) break;
                    AppendItems(sub, item.Children ?? Array.Empty<TrayItem>());
                    // MF_POPUP: the id argument is the submenu's handle.
                    if (!AppendMenuW(menu, MF_STRING | MF_POPUP | grayed, (UIntPtr)(nuint)sub, MenuText(item.Label)))
                        DestroyMenu(sub);
                    break;
                }
            }
        }
    }

    /// <summary>'&amp;' marks a mnemonic in menu text; a literal one is doubled.</summary>
    private static string MenuText(string label) => label.Replace("&", "&&");

    // ---- Shell_NotifyIcon ----

    private NOTIFYICONDATAW NewData(uint flags)
    {
        IntPtr hwnd;
        lock (_lock) hwnd = _hwnd;
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = IconId,
            uFlags = flags,
            szTip = "",
            szInfo = "",
            szInfoTitle = "",
        };
    }

    private string CurrentTip()
    {
        TrayState state;
        lock (_lock) state = _state;
        string tip = state.Tooltip;
        return tip.Length > 127 ? tip[..127] : tip; // szTip holds 128 chars with the terminator
    }

    private void AddIcon()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = _icon;
        data.szTip = _shownTip = CurrentTip();
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            // No taskbar yet (early logon) or Explorer down: TaskbarCreated adds it.
            if (!_loggedAddFailure)
                FileLog.Info("tray: the notification area is not available yet; the icon appears when Explorer starts");
            _loggedAddFailure = true;
            return;
        }
        _added = true;
        _loggedAddFailure = false;
        data.uVersion = NOTIFYICON_VERSION_4;
        _version4 = Shell_NotifyIconW(NIM_SETVERSION, ref data);
        FileLog.Info("tray: icon added to the notification area");
    }

    /// <summary>A notification from the icon, with the app's icon: Windows 10 and
    /// 11 show it as a toast and keep it in the notification center. Any thread.
    /// False when the icon is not in the notification area (no Explorer yet) or
    /// the shell refused it. The custom icon must be the large size when
    /// NIIF_LARGE_ICON is set (the shell answers "incorrect size" otherwise), so
    /// the balloon gets its own icon; refused anyway, the notification is sent
    /// again with the stock information icon.</summary>
    public bool ShowNotification(string title, string message)
    {
        if (!_added) return false;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;      // 64 chars with the terminator
        data.szInfo = message.Length > 255 ? message[..255] : message;   // 256
        if (_balloonIcon != IntPtr.Zero)
        {
            data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
            data.hBalloonIcon = _balloonIcon;
            if (Shell_NotifyIconW(NIM_MODIFY, ref data)) return true;
            FileLog.Warn($"tray: notification with the app's icon refused (error {Marshal.GetLastWin32Error()}), retrying with the stock icon");
        }
        data.dwInfoFlags = NIIF_INFO;
        data.hBalloonIcon = IntPtr.Zero;
        if (Shell_NotifyIconW(NIM_MODIFY, ref data)) return true;
        FileLog.Warn($"tray: notification refused (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    private void UpdateTip()
    {
        if (!_added) return;
        string tip = CurrentTip();
        if (tip == _shownTip) return;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        data.szTip = tip;
        if (Shell_NotifyIconW(NIM_MODIFY, ref data)) _shownTip = tip;
    }

    private void RemoveIcon()
    {
        if (!_added) return;
        var data = NewData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>The embedded .ico's entry for a system icon size (SM_CXSMICON, 16
    /// px at 100 % scaling, for the notification area; SM_CXICON, 32 px, for a
    /// notification), as an HICON.</summary>
    private static IntPtr LoadIcon(int metric, int fallback)
    {
        if (TrayIconImage.Ico is not { } ico) return IntPtr.Zero;
        int size = GetSystemMetrics(metric);
        if (size <= 0) size = fallback;
        if (TrayIconImage.Pick(ico, size) is not { } entry) return IntPtr.Zero;
        byte[] bits = ico.AsSpan(entry.Offset, entry.Size).ToArray();
        return CreateIconFromResourceEx(bits, (uint)bits.Length, true, 0x00030000, size, size, 0);
    }

    public void Dispose()
    {
        IntPtr hwnd;
        lock (_lock) hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        lock (_lock) _hwnd = IntPtr.Zero;
        s_instance = null;
        _ready.Dispose();
    }

    // ---- Interop ----

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion; // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize, bool fIcon, uint dwVer,
        int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
#endif
