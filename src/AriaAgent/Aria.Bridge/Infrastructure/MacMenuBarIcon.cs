using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aria.Bridge.Services.Logging;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// macOS menu-bar (<c>NSStatusItem</c>) icon for the bridge daemon — the twin of
/// <see cref="WindowsTrayIcon"/>. Raw AppKit via <c>objc_msgSend</c>; no <c>net-macos</c> TFM.
/// <para>
/// AppKit requires status-item creation on the main thread (modern macOS throws
/// <c>NSWindow should only be instantiated on the main thread</c> otherwise). So this type owns
/// the process entry shape on macOS when tray is enabled: web host on a background task, AppKit
/// <c>-[NSApplication run]</c> on main. Menu: Open status page / Quit. Activation policy Accessory
/// keeps it out of the Dock.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacMenuBarIcon
{
    private const string StatusUrl = "http://localhost:5741/";
    private const string IconResource = "Aria.Bridge.Assets.aria-bridge-menubar.png";
    private const string TargetClassName = "AriaBridgeMenuTarget";

    private static Action? _onQuit;

    // GC anchors for native callbacks + retained AppKit objects owned by the main thread.
    private static MenuAction? _openKeepAlive;
    private static MenuAction? _quitKeepAlive;
    private static IntPtr _statusItem;
    private static IntPtr _target;
    private static IntPtr _app;
    private static bool _attached;

    /// <summary>
    /// Runs <paramref name="app"/> with a menu-bar icon. Must be called on the process main
    /// thread (i.e. instead of <c>app.Run()</c>). No-ops the icon (and just runs the host) when
    /// AppKit / WindowServer is unavailable.
    /// </summary>
    public static void RunWebHostWithMenuBar(WebApplication app)
    {
        _onQuit = app.Lifetime.StopApplication;

        // WebApplicationFactory / test hosts often enter Program on a worker thread. Creating an
        // NSStatusItem there aborts the process with NSInternalInconsistencyException (not a
        // managed exception), so fall back to a plain Run() when we are not on AppKit's main.
        if (!IsNsMainThread())
        {
            BridgeLogger.Log("WARN", "[Tray] Not on AppKit main thread — running without menu-bar icon.");
            app.Run();
            return;
        }

        if (!TryPrepareAppKit())
        {
            BridgeLogger.Log("WARN", "[Tray] AppKit unavailable — running without menu-bar icon.");
            app.Run();
            return;
        }

        using var started = new ManualResetEventSlim(false);
        app.Lifetime.ApplicationStarted.Register(() => started.Set());
        app.Lifetime.ApplicationStopping.Register(StopAppKitRunLoop);

        // Host (Kestrel + hosted services) on a worker; main thread stays free for AppKit.
        var hostTask = Task.Run(() => app.RunAsync());

        if (!started.Wait(TimeSpan.FromSeconds(30)))
            BridgeLogger.Log("WARN", "[Tray] Host start timed out — attempting menu-bar attach anyway.");

        if (!TryAttachStatusItem())
        {
            // No icon, no need for NSApp.run — just wait for the host like a normal console app.
            hostTask.GetAwaiter().GetResult();
            return;
        }

        BridgeLogger.Log("INFO", "[Tray] Menu-bar icon active.");
        Msg(_app, "run"); // blocks until Quit / ApplicationStopping posts stop
        hostTask.GetAwaiter().GetResult();
    }

    /// <summary>Safe to call from any thread; wakes <c>-[NSApplication run]</c> if it is pumping.</summary>
    public static void Stop() => StopAppKitRunLoop();

    private static bool IsNsMainThread()
    {
        try
        {
            dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", RTLD_LAZY);
            return objc_msgSend_bool_ret(objc_getClass("NSThread"), Sel("isMainThread"));
        }
        catch { return false; }
    }

    private static bool TryPrepareAppKit()
    {
        try
        {
            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_LAZY);
            _app = Msg(objc_getClass("NSApplication"), "sharedApplication");
            if (_app == IntPtr.Zero) return false;

            // 1 = NSApplicationActivationPolicyAccessory (no Dock icon / activation bounce).
            MsgBoolInt(_app, "setActivationPolicy:", 1);
            return true;
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("WARN", $"[Tray] AppKit prepare failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryAttachStatusItem()
    {
        if (_attached) return true;
        var pool = Msg(Msg(objc_getClass("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            _target = CreateMenuTarget();
            if (_target == IntPtr.Zero)
            {
                BridgeLogger.Log("WARN", "[Tray] Menu target registration failed — no menu-bar icon.");
                return false;
            }

            var bar = Msg(objc_getClass("NSStatusBar"), "systemStatusBar");
            // NSVariableStatusItemLength == -1
            _statusItem = MsgNFloat(bar, "statusItemWithLength:", -1.0);
            if (_statusItem == IntPtr.Zero)
            {
                BridgeLogger.Log("WARN", "[Tray] statusItemWithLength failed — no menu-bar icon.");
                return false;
            }
            Msg(_statusItem, "retain");

            var button = Msg(_statusItem, "button");
            var image = LoadTemplateImage();
            if (button != IntPtr.Zero && image != IntPtr.Zero)
            {
                MsgIntPtr(button, "setImage:", image);
                MsgIntPtr(button, "setToolTip:", NSString($"Aria Bridge v{BridgeLogger.Version} — {StatusUrl}"));
            }
            else if (button != IntPtr.Zero)
            {
                MsgIntPtr(button, "setTitle:", NSString("Aria"));
            }

            MsgIntPtr(_statusItem, "setMenu:", BuildMenu());
            _attached = true;
            return true;
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("WARN", $"[Tray] Menu-bar attach failed: {ex.Message}");
            TearDownStatusItem();
            return false;
        }
        finally
        {
            Msg(pool, "drain");
        }
    }

    private static void StopAppKitRunLoop()
    {
        try
        {
            TearDownStatusItem();
            if (_app == IntPtr.Zero) return;

            // -[NSApplication stop:] only takes effect after the current event completes; post a
            // wake-up event so -run returns promptly when Quit / host shutdown races the menu.
            MsgIntPtr(_app, "stop:", IntPtr.Zero);
            var nsEvent = MsgOtherEvent(
                objc_getClass("NSEvent"),
                "otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:",
                13,                         // NSEventTypeApplicationDefined
                new CGPoint(0, 0),
                (UIntPtr)0,
                0.0,
                0,
                IntPtr.Zero,
                (short)0,
                0,
                0);
            if (nsEvent != IntPtr.Zero)
                MsgIntPtrBool(_app, "postEvent:atStart:", nsEvent, true);
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("WARN", $"[Tray] Menu-bar stop failed: {ex.Message}");
        }
    }

    private static void TearDownStatusItem()
    {
        if (_statusItem == IntPtr.Zero) return;
        try
        {
            var bar = Msg(_statusItem, "statusBar");
            if (bar != IntPtr.Zero)
                MsgIntPtr(bar, "removeStatusItem:", _statusItem);
            Msg(_statusItem, "release");
        }
        catch { /* best-effort */ }
        _statusItem = IntPtr.Zero;
        _attached = false;
    }

    private static IntPtr BuildMenu()
    {
        var menu = Msg(Msg(objc_getClass("NSMenu"), "alloc"), "init");

        var open = MsgTitleActionKey(
            Msg(objc_getClass("NSMenuItem"), "alloc"),
            "initWithTitle:action:keyEquivalent:",
            NSString("Open Aria Bridge status page"),
            sel_registerName("openStatus:"),
            NSString(""));
        MsgIntPtr(open, "setTarget:", _target);
        MsgIntPtr(menu, "addItem:", open);

        MsgIntPtr(menu, "addItem:", Msg(objc_getClass("NSMenuItem"), "separatorItem"));

        var quit = MsgTitleActionKey(
            Msg(objc_getClass("NSMenuItem"), "alloc"),
            "initWithTitle:action:keyEquivalent:",
            NSString("Quit Aria Bridge"),
            sel_registerName("quitBridge:"),
            NSString(""));
        MsgIntPtr(quit, "setTarget:", _target);
        MsgIntPtr(menu, "addItem:", quit);

        return menu;
    }

    private static IntPtr CreateMenuTarget()
    {
        var existing = objc_getClass(TargetClassName);
        if (existing != IntPtr.Zero)
            return Msg(Msg(existing, "alloc"), "init");

        var cls = objc_allocateClassPair(objc_getClass("NSObject"), TargetClassName, IntPtr.Zero);
        if (cls == IntPtr.Zero) return IntPtr.Zero;

        _openKeepAlive = OnOpen;
        _quitKeepAlive = OnQuit;
        // "v@:@" — void return, self, _cmd, sender
        class_addMethod(cls, sel_registerName("openStatus:"),
            Marshal.GetFunctionPointerForDelegate(_openKeepAlive), "v@:@");
        class_addMethod(cls, sel_registerName("quitBridge:"),
            Marshal.GetFunctionPointerForDelegate(_quitKeepAlive), "v@:@");
        objc_registerClassPair(cls);

        return Msg(Msg(cls, "alloc"), "init");
    }

    private static void OnOpen(IntPtr self, IntPtr cmd, IntPtr sender) => OpenStatusPage();

    private static void OnQuit(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        TearDownStatusItem();
        _onQuit?.Invoke();
        StopAppKitRunLoop();
    }

    private static void OpenStatusPage()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(StatusUrl) { UseShellExecute = true });
        }
        catch { /* no browser — nothing sensible to do from the menu bar */ }
    }

    private static IntPtr LoadTemplateImage()
    {
        try
        {
            using var stream = typeof(MacMenuBarIcon).Assembly.GetManifestResourceStream(IconResource);
            if (stream == null)
            {
                BridgeLogger.Log("WARN", $"[Tray] Embedded icon '{IconResource}' missing.");
                return IntPtr.Zero;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var data = MsgBytesLength(
                    objc_getClass("NSData"),
                    "dataWithBytes:length:",
                    handle.AddrOfPinnedObject(),
                    (UIntPtr)(nuint)bytes.Length);
                if (data == IntPtr.Zero) return IntPtr.Zero;

                var image = MsgIntPtr(
                    Msg(objc_getClass("NSImage"), "alloc"),
                    "initWithData:",
                    data);
                if (image == IntPtr.Zero) return IntPtr.Zero;

                // 36×36 PNG → 18 pt template so Retina keeps a 2× backing store.
                MsgSize(image, "setSize:", new CGSize(18, 18));
                MsgBool(image, "setTemplate:", true);
                return image;
            }
            finally { handle.Free(); }
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("WARN", $"[Tray] Icon load failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    // ── objc helpers ──────────────────────────────────────────────────────────

    private static IntPtr Sel(string name) => sel_registerName(name);
    private static IntPtr Msg(IntPtr receiver, string sel) =>
        objc_msgSend(receiver, Sel(sel));
    private static IntPtr MsgIntPtr(IntPtr receiver, string sel, IntPtr arg) =>
        objc_msgSend_IntPtr(receiver, Sel(sel), arg);
    private static void MsgIntPtrBool(IntPtr receiver, string sel, IntPtr arg, bool b) =>
        objc_msgSend_IntPtr_bool(receiver, Sel(sel), arg, b);
    private static IntPtr MsgNFloat(IntPtr receiver, string sel, double arg) =>
        objc_msgSend_nfloat(receiver, Sel(sel), arg);
    private static void MsgBool(IntPtr receiver, string sel, bool arg) =>
        objc_msgSend_bool(receiver, Sel(sel), arg);
    private static void MsgBoolInt(IntPtr receiver, string sel, int arg) =>
        objc_msgSend_int(receiver, Sel(sel), arg);
    private static void MsgSize(IntPtr receiver, string sel, CGSize size) =>
        objc_msgSend_CGSize(receiver, Sel(sel), size);
    private static IntPtr MsgBytesLength(IntPtr receiver, string sel, IntPtr bytes, UIntPtr length) =>
        objc_msgSend_bytes_len(receiver, Sel(sel), bytes, length);
    private static IntPtr MsgTitleActionKey(IntPtr receiver, string sel, IntPtr title, IntPtr action, IntPtr key) =>
        objc_msgSend_title_action_key(receiver, Sel(sel), title, action, key);

    private static IntPtr MsgOtherEvent(
        IntPtr receiver, string sel,
        nint type, CGPoint location, UIntPtr flags, double timestamp,
        nint windowNumber, IntPtr context, short subtype, nint data1, nint data2) =>
        objc_msgSend_otherEvent(receiver, Sel(sel), type, location, flags, timestamp,
            windowNumber, context, subtype, data1, data2);

    private static IntPtr NSString(string s) =>
        objc_msgSend_utf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MenuAction(IntPtr self, IntPtr cmd, IntPtr sender);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
        public CGSize(double w, double h) { Width = w; Height = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
        public CGPoint(double x, double y) { X = x; Y = y; }
    }

    private const int RTLD_LAZY = 1;
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport("libdl.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, IntPtr extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_IntPtr_bool(
        IntPtr receiver, IntPtr selector, IntPtr arg, [MarshalAs(UnmanagedType.I1)] bool b);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_nfloat(IntPtr receiver, IntPtr selector, double arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_bool_ret(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_int(IntPtr receiver, IntPtr selector, int arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_CGSize(IntPtr receiver, IntPtr selector, CGSize size);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_bytes_len(IntPtr receiver, IntPtr selector, IntPtr bytes, UIntPtr length);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_title_action_key(
        IntPtr receiver, IntPtr selector, IntPtr title, IntPtr action, IntPtr keyEquivalent);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_utf8(
        IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8);

    // NSEvent otherEventWithType:… — CGPoint is two doubles; keep the arity matched to the selector.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_otherEvent(
        IntPtr receiver, IntPtr selector,
        nint type, CGPoint location, UIntPtr modifierFlags, double timestamp,
        nint windowNumber, IntPtr context, short subtype, nint data1, nint data2);
}
