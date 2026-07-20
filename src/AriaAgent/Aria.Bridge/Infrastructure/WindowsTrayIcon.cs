using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aria.Bridge.Services.Logging;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Windows notification-area ("system tray") icon for the bridge daemon. The installer starts the
/// process with -WindowStyle Hidden, so without this the only signs of life are the port and the
/// task manager. Raw Shell_NotifyIcon P/Invoke on a dedicated message-loop thread — no WinForms,
/// no net-windows TFM, so the single cross-platform csproj is untouched (dormant off-Windows).
/// Left-click opens the status page; right-click offers Open / Quit.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTrayIcon
{
    private const string StatusUrl = "http://localhost:5741/";
    private const string WndClass  = "AriaBridgeTrayWnd";

    private const uint WM_DESTROY    = 0x0002;
    private const uint WM_CLOSE      = 0x0010;
    private const uint WM_COMMAND    = 0x0111;
    private const uint WM_LBUTTONUP  = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP  = 0x0205;
    private const uint WM_APP_TRAY   = 0x8001;          // WM_APP + 1: tray callback

    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const uint NIM_ADD = 0x0, NIM_DELETE = 0x2;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    private const int  IDM_OPEN = 1001, IDM_QUIT = 1002;

    private static Thread?  _thread;
    private static IntPtr   _hwnd;
    private static IntPtr   _hicon;
    private static uint     _taskbarCreatedMsg;
    private static Action?  _onQuit;
    private static WndProc? _wndProcKeepAlive;          // GC anchor for the native callback

    /// <summary>Creates the tray icon on its own message-loop thread. Idempotent.</summary>
    public static void Start(Action onQuit)
    {
        if (_thread != null) return;
        _onQuit = onQuit;
        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "aria-tray" };
        _thread.Start();
    }

    /// <summary>Removes the icon and ends the message loop (safe to call from any thread).</summary>
    public static void Stop()
    {
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static void RunMessageLoop()
    {
        try
        {
            _wndProcKeepAlive = WndProcImpl;
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");   // explorer restart → re-add icon

            var wc = new WNDCLASS
            {
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                hInstance     = GetModuleHandle(null),
                lpszClassName = WndClass,
            };
            if (RegisterClass(ref wc) == 0) { BridgeLogger.Log("WARN", "[Tray] RegisterClass failed — no tray icon."); return; }

            // Ordinary (never-shown) top-level window: message-only windows don't reliably get
            // TaskbarCreated broadcasts, and TrackPopupMenu wants a real foreground-able HWND.
            _hwnd = CreateWindowEx(0, WndClass, "Aria Bridge", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { BridgeLogger.Log("WARN", "[Tray] CreateWindowEx failed — no tray icon."); return; }

            _hicon = LoadOwnIcon();
            AddIcon();
            BridgeLogger.Log("INFO", "[Tray] Tray icon active.");

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("WARN", $"[Tray] Tray icon failed: {ex.Message}");
        }
        finally
        {
            _hwnd = IntPtr.Zero;
        }
    }

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK) OpenStatusPage();
            else if (evt == WM_RBUTTONUP) ShowMenu(hwnd);
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AddIcon();                                   // explorer.exe restarted — re-register
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_CLOSE:
                RemoveIcon();
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, IDM_OPEN, "Open Aria Bridge status page");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IDM_QUIT, "Quit Aria Bridge");
            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd);                   // required, or the menu won't dismiss on outside click
            var cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            switch (cmd)
            {
                case IDM_OPEN: OpenStatusPage(); break;
                case IDM_QUIT:
                    RemoveIcon();
                    _onQuit?.Invoke();                   // graceful host shutdown
                    DestroyWindow(hwnd);
                    break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    private static void OpenStatusPage()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(StatusUrl) { UseShellExecute = true });
        }
        catch { /* no browser — nothing sensible to do from the tray */ }
    }

    private static void AddIcon()
    {
        var data = NewIconData();
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private static void RemoveIcon()
    {
        var data = NewIconData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private static NOTIFYICONDATA NewIconData() => new()
    {
        cbSize           = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd             = _hwnd,
        uID              = 1,
        uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon            = _hicon,
        szTip            = $"Aria Bridge v{BridgeLogger.Version} — {StatusUrl}",
    };

    // The exe carries its own icon (<ApplicationIcon> in the csproj); fall back to the stock
    // application icon so the tray still works if extraction fails.
    private static IntPtr LoadOwnIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var h = ExtractIcon(GetModuleHandle(null), exe, 0);
                if (h.ToInt64() > 1) return h;           // NULL / 1 = no icon in file
            }
        }
        catch { }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle,
        string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? item);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
