# // IDEA — macOS menu-bar icon for Aria.Bridge

**Status: done (landed in tree).** Windows already has a notification-area icon
(`WindowsTrayIcon`); macOS LaunchAgent / background runs have no equivalent affordance.
Add a menu-bar (`NSStatusItem`) twin so users can find, open, and quit the bridge.

## Current state

- `WindowsTrayIcon` (`Aria.Bridge/Infrastructure/WindowsTrayIcon.cs`): raw `Shell_NotifyIcon`
  P/Invoke on a dedicated message-loop thread; left-click opens the status page, right-click
  menu Open / Quit. Gated by `OperatingSystem.IsWindows()` + `Bridge:TrayIcon` (default true)
  in `BridgeLifetimeEvents`.
- Single `net10.0` Web csproj — deliberately no `net-windows` / WinForms TFM; the tray code is
  dormant off-Windows.
- Icon resource: `aria-bridge.ico` via `<ApplicationIcon>` (orange rounded square, gold
  triangle-over-bar “eject/A” mark).
- Open question in `docs/ideas/done/bridge-launch-at-startup.md`: “Should the bridge show a
  tray/menu-bar icon so users know it's running?”

## Design

### Parity with Windows, same constraints

| Concern | Choice |
|---|---|
| API surface | `MacMenuBarIcon` parallel to `WindowsTrayIcon` — same Start/Stop, same Open / Quit UX |
| TFM / packages | Stay on `net10.0`. Raw `objc_msgSend` + AppKit/Foundation — no `net-macos`, no MAUI/Avalonia |
| Config | Reuse `Bridge:TrayIcon` (default true); gate with `OperatingSystem.IsMacOS()` |
| Lifetime | Hooked in `BridgeLifetimeEvents` next to the Windows path |
| Activation | `NSApplicationActivationPolicyAccessory` so there is no Dock bounce / icon (menu-bar only) |
| Threading | **Main thread owns AppKit.** Modern macOS rejects `NSStatusItem` off-main (`NSWindow should only be instantiated on the main thread`). `Program.cs` calls `MacMenuBarIcon.RunWebHostWithMenuBar` instead of `app.Run()`: Kestrel/host on a worker task, `-[NSApplication run]` on main. Quit / `ApplicationStopping` posts `stop:` + a wake-up `NSEvent` |
| Asset | Template PNG (white glyph, transparent background) derived from the Windows mark — macOS tints it for light/dark menu bars. Embedded resource so single-file publish still works |
| Security | Local UI only. No new tunnel allowlist paths, no trust/key changes. Compromised `Aria.Web` gains nothing |

### Menu

- **Open Aria Bridge status page** → `http://localhost:5741/`
- separator
- **Quit Aria Bridge** → remove item, invoke `IHostApplicationLifetime.StopApplication`

Clicking the status item shows the menu (macOS convention); no separate left-click handler.

### Asset

- `Assets/aria-bridge-menubar.png` — 36×36 template (18 pt with `setSize:` 18×18 for Retina).
- Generated from `aria-bridge.ico`: keep the orange border + gold symbol as opaque white; drop the
  dark fill to alpha 0. Pixel-art style preserved via nearest-neighbour scale.

## Implementation steps

1. ✅ Add plan + Assets PNG; register as `EmbeddedResource`.
2. ✅ `MacMenuBarIcon.cs`: load AppKit, shared `NSApplication`, accessory policy, `NSStatusItem` +
   template image, `NSMenu` with an `objc_allocateClassPair` target for Open/Quit selectors.
3. ✅ `Program.cs` calls `RunWebHostWithMenuBar` on macOS when `Bridge:TrayIcon` is true;
   `ApplicationStopping` still calls `MacMenuBarIcon.Stop`.
4. ✅ Brief mention in `docs/readme/bridge-features.md` (tray / menu bar + config key).
5. ✅ `dotnet build` Bridge + smoke (`[Tray] Menu-bar icon active`) + `dotnet test` suite.

## Out of scope

- Linux AppIndicator / StatusNotifierItem
- Custom popover UI (status page in the browser is enough)
- `.app` bundle / `Info.plist` `LSUIElement` (accessory policy covers Dock hiding for the daemon)
- Changing LaunchAgent install scripts (icon works whether started from terminal or LaunchAgent,
  as long as the process is in a user GUI session)

## Open questions

- None. Off-main entry (tests / WAF) is detected via `NSThread.isMainThread` and falls back to
  plain `app.Run()` so NSExceptions cannot abort the process.
