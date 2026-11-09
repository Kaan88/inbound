# D7: GUI toolkits for Inbound, Windows and Linux (2026-11-09)

This draws on four sourced briefs (.NET cross-platform, web shells, native non-.NET, Windows side) and a read of the codebase. Claims carry a source URL; "[unverified]" means the brief had no source. Effort is given in person-weeks (pw) and is an estimate from the codebase study, not a measurement. Where two briefs disagree, both values are given.

## 1. The answer in five lines

1. **Best secondary GUI for Linux: Avalonia 12.x.** It has mature X11 support, AT-SPI2 (Linux screen readers), a built-in tray icon, a portal file picker and an MIT licence. It also reuses our C# client code.
2. **Best single GUI for both systems:**
   - **Avalonia**, if one native frontend should eventually replace WinUI.
   - **The existing Svelte web UI in a thin shell** (Tauri 2, or a small C# WebView2/WebKitGTK host), if the goal is the cheapest Linux window soon.
3. **On Windows, nothing clearly beats WinUI 3 for this app.** It has the most native Fluent look and mature UIA, it is now developed on GitHub, and Microsoft is working on its memory use. The reasons to switch are Linux and one codebase, not a better Windows app.
4. **Uno Platform reuses the most WinUI code**, but its Linux build has no native Wayland, no Linux screen reader yet, and no automatic DPI detection under XWayland.
5. **Recommended path:**
   1. Build a Linux desktop Host role first. Every option needs it.
   2. Prototype Avalonia and the Svelte shell side by side and measure idle wake-ups.
   3. Keep WinUI on Windows. Try Native AOT on it in a branch.

## 2. Comparison table

Ratings are a judgement based on the briefs. "Reuse" means reuse of our code: WinUI XAML/C#, the 26 UI-free files (6.7k lines), Contracts, and the Svelte UI (about 12.5k lines).

| Toolkit | Linux quality | Windows quality | Reuse of our code | Idle cost | Size | Accessibility | Tray / notifications | Licence | Maturity | Effort |
|---|---|---|---|---|---|---|---|---|---|---|
| **WinUI 3 (today)** | None | Best (native Fluent, Mica) | 100% | Not measured; Microsoft says its memory use is too high and promises a fix | ~200 MB install (Windows App SDK) | UIA, native | No built-in tray (the Host owns it) | MIT [unverified] | Windows App SDK 2.6.5 stable, 2026-11-02 | 0 |
| **Avalonia 12.x** | Good on X11; Wayland experimental and opt-in | Good (Win32 + Skia), not WinUI-native | Contracts + 26 files as they are; views rewritten (fairly mechanical for code-built UI) | Vendor: <0.01% idle CPU, measured on Android only | AOT supported; community claim ~18 MB [unverified] | UIA (listed as full) + AT-SPI2 (first in .NET) | Built-in TrayIcon (SNI on Linux, GNOME needs extension); notifications via how-to or third-party D-Bus library | MIT core; paid extras (TreeDataGrid, Charts) | 12.0 April 2026, 12.1 July 2026 | Linux-only 14–22 pw; replace WinUI on both systems +~4 pw |
| **Uno Platform 6.6/6.7** | Weak: X11/framebuffer only, manual DPI under XWayland | Can keep the native WinAppSDK head (identical to today) | Highest of the .NET options: WinUI XAML/C# largely compiles | No data | Skia desktop ~52 MB | Windows yes; Linux "planned" | No built-in tray; Linux notifications undocumented | Apache-2.0 | 6.x, active | 8–14 pw + Windows-only gaps |
| **Tauri 2 + Svelte** | Medium: WebKitGTK 4.1 with known NVIDIA/Wayland problems | Good (WebView2) | Svelte UI fully; no C#; Contracts mirrored in TS/Rust | Risky: WebKitGTK hidden-window and wake-up bugs | ~8.6 MiB bundle (system webview not counted) | As good as the webview; WebKitGTK AT-SPI has open bugs | Tray via plugin (no click events on Linux); notification plugin | MIT/Apache [unverified] | 2.12.x, active | 3–6 pw + Host work; adds Rust |
| **C# thin web host** (WebView2Aot on Windows, GirCore WebKit-6.0 on Linux) | Medium (WebKitGTK 6.0/GTK4) | Good (WebView2) | Svelte UI + Contracts + C# client | Best control of the webview (IsVisible, TrySuspendAsync) | Small shell; WebView2 Evergreen already installed | As good as the webview | Build our own; tray stays in the Host | MIT/Apache parts | Bindings pre-1.0 | Not estimated; more than Tauri [estimate] |
| **PhotinoX / Photino.NET** | Medium (WebKitGTK 4.1) | Good (WebView2) | Svelte + C# | Same as other webviews | Small | As good as the webview | Notifications yes; no tray | Apache-2.0 | Upstream stale since 2025-01; fork thinly maintained | Small, but a maintenance risk |
| **Electron** | Good, consistent (bundled Chromium, Wayland by default) | Good | Svelte fully | Worst: a whole Chromium, several processes | ~244 MiB | Chromium (good) [unverified] | Tray: SNI with fallback; click ignored with appindicator | MIT [unverified] | Very mature | 3–6 pw [estimate] |
| **GTK4 + libadwaita via Gir.Core (C#)** | Best on GNOME (Wayland, portals, AT-SPI, GApplication) | Poor | Contracts + 26 files; views rewritten | Event-driven, near zero [unverified] | Small; Flatpak GNOME runtime | AT-SPI (GTK) | No tray (SNI by hand); GNotification | MIT bindings, LGPL GTK | Bindings 0.8.1, pre-1.0 | ~14–20 pw, Linux-only |
| **GTK4 + libadwaita (Rust/Python)** | Best on GNOME | Poor (MSYS2/gvsbuild, looks foreign) | Protocol only | Low [unverified] | Small | AT-SPI direct | ksni for tray; GNOME needs extension | LGPL [unverified] | GTK 4.24, mature | 20–35 pw |
| **Qt 6 (C++/QML)** | Very good, KDE-native | Good-ish: FluentWinUI3 style has gaps in the controls a list needs | Protocol only (full rewrite) | Retained-mode, near zero [unverified] | Tens of MB [unverified] | Most mature UIA + AT-SPI in its group | QSystemTrayIcon with SNI; D-Bus notifications | LGPLv3/commercial; some modules GPL-only | Very mature | 20–35 pw |
| **Qt Bridge for C#** | Qt quality | Qt quality | C# backend, QML UI rewritten | As Qt | Qt runtime | As Qt | As Qt | LGPL/commercial [unverified] | Technology Preview, no 1.0 | Too early |
| **Slint** | Fair; own look | Fair; Fluent style | Protocol only | Likely low [unverified] | Small [unverified] | AccessKit; Linux Orca issues | SystemTrayIcon (1.17, SNI only) | GPL / royalty-free with visible credit / commercial | Desktop features young | 20–35 pw |
| **Flutter** | Fair: GTK3 embedder | Good; UIA works | Protocol only | Not measured | Not measured | Windows good; Linux weak (ATK bridge) | Plugins only | BSD-3 [unverified] | Canonical maintains desktop | 20–35 pw |
| **MAUI (Linux)** | Preview on two competing tracks | WinUI underneath | MAUI XAML differs: rewrite | — | — | — | — | MIT | Experimental | Not now |
| **Compose Multiplatform** | Poor: X11 only, no a11y | Fair (Java Access Bridge) | Protocol only | JVM overhead | ~92–171 MB | No Linux a11y | — | Apache [unverified] | 1.10 | Not recommended |
| **Terminal.Gui (TUI)** | Good, as a terminal | Good | Contracts directly | Minimal | Small | None (GUI sense) | None | MIT | 2.5.0 stable | An add-on, not a replacement |

## 3. Per-family discussion

### 3.1 Applies to every option

- **The Host is the real Linux work.** `Host` is `net10.0-windows`. Its Windows-only parts are:
  - the pipe server and ACL
  - startup registration
  - sleep prevention
  - firewall rules
  - native-host registration
  - clipboard monitor
  - completion sound
  - updater
  - window activation
  - the WinForms tray

  `Server` already runs on Linux, but `ServerPlatform` switches off the open-UI, folder-picker, tray, shutdown, updater and completion-action features. That makes it a network server, not a desktop Host. The codebase study estimates **3–5 pw** for a Linux desktop Host role, whichever GUI is picked.
- **Local IPC on Linux.**
  - .NET's named-pipe classes on Linux sit on Unix domain sockets. .NET 11, due 2026-11-10, sets the socket file to mode 0600 (https://learn.microsoft.com/en-ie/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions, https://visualstudiomagazine.com/articles/2026/09/14/net-11-rc1-gets-go-live-support-ahead-of-november-launch.aspx).
  - `IpcClient`, the framing (4-byte length plus JSON) and the hello pipe can therefore be reused almost unchanged.
  - `PipeOwner.VerifyServer` needs a Unix version that checks the peer's user ID [unverified: the exact .NET API].
  - Put the socket under `$XDG_RUNTIME_DIR`, not the default `/tmp` location [unverified].
- **The tray should stay in the Host.** On Linux that means StatusNotifierItem over D-Bus.
  - Stock GNOME has no tray and needs the AppIndicator extension (https://docs.avaloniaui.net/controls/navigation/trayicon, https://linuxiac.com/how-to-enable-system-tray-icons-in-gnome/).
  - GNOME recommends the Background portal plus notifications instead (https://discourse.gnome.org/t/what-to-use-instead-of-statusicon-in-gtk4-to-display-the-icon-in-the-system-tray/7175).
  - Keeping the tray in the Host makes the toolkit's tray support irrelevant. The window can also stay closed when idle, as on Windows today (PERF.5).
  - The Host must work fully with no tray.
- **How often progress repaints matters more than which toolkit draws them.** Throttling progress redraws to 1–2 Hz and stopping animations when hidden will matter more for idle cost than the toolkit, for every retained-mode toolkit. This is a design judgement from the native-other brief, not a measurement.
- **No published desktop idle CPU or wake-up figures exist for any candidate.** We have to measure them ourselves (section 5).

### 3.2 .NET native: Avalonia, Uno, Gir.Core

**Avalonia 12.x**

- **Releases:** 12.0 GA on 2026-04-07, on .NET 10 with compiled bindings by default; 12.1 on 2026-07-08 (https://avaloniaui.net/blog/avalonia-12, https://avaloniaui.net/blog/release-12-1). The latest patch is 12.1.5 (2026-11-02) per NuGet (https://www.nuget.org/packages/Avalonia); one brief cites 12.1.4 via Wikipedia.
- **Linux:**
  - X11 is mature.
  - Native Wayland graduated to experimental in 12.1. It needs `Avalonia.Wayland` plus `UseWayland()`; otherwise apps run through XWayland (https://avaloniaui.net/blog/release-12-1). A `UseWaylandWithFallback` API exists (https://docs.avaloniaui.net/api/avalonia/avaloniawaylandplatformextensions).
  - Tier-1 distributions are Ubuntu 25.x, Fedora 43 and Debian 13 (https://docs.avaloniaui.net/docs/supported-platforms).
- **Accessibility:** an AT-SPI2 backend on Linux, which Avalonia calls a first for a .NET framework, and UIA listed as full support on Windows (https://docs.avaloniaui.net/docs/app-development/accessibility).
- **Desktop integration:**
  - Built-in TrayIcon. It is confirmed on Ubuntu, and click events work only on some desktops (trayicon docs).
  - D-Bus portal file picker with a GTK fallback (https://docs.avaloniaui.net/docs/services/file-dialogs). There is a known hang with some filters (https://lightrun.com/answers/avaloniaui-avalonia-istorageprovideropenfilepickerasync-does-not-return).
  - Notifications: a docs how-to exists (https://docs.avaloniaui.net/docs/how-to/notifications-how-to), or the third-party D-Bus library (https://github.com/pr8x/DesktopNotifications).
- **Native AOT:** supported, with compiled bindings required (https://docs.avaloniaui.net/docs/deployment/native-aot).
- **Packaging:** the docs cover a self-contained .deb and Parcel for .rpm (https://docs.avaloniaui.net/docs/deployment/linux). Flatpak and AppImage are not documented by Avalonia.
- **Migration aids in 12.1:**
  - `Avalonia.WinUI` embeds Avalonia controls inside a WinUI 3 app, so shared custom controls can be built during a migration.
  - A new `TableView` control (https://avaloniaui.net/blog/release-12-1).
- **Fit with our code:**
  - Contracts and the 26 UI-free files (6.7k lines) carry over as they are.
  - `HostConnection` needs only the transport change.
  - The code-built UI (82 TextBlock, 77 StackPanel, 38 Button and so on) ports fairly mechanically.
  - No direct equivalents: `ContentDialog` (45 uses, needs a helper), `MenuFlyout`, the AppWindow title-bar work and `ShellFileIconReader`.
- **Licence:**
  - The core is MIT.
  - The commercial tooling changed this year. One brief says the Accelerate terms changed on 2026-02-05 (https://avaloniaui.net/blog/building-a-sustainable-future-for-avalonia). Another says Accelerate was retired in April 2026 and replaced by tiers, with TreeDataGrid and Charts paid (https://avaloniaui.net/blog/retiring-accelerate). Both agree the framework stays MIT.
  - We would avoid paid controls. A ListBox or the new TableView is enough for the list.
  - The tie between TreeDataGrid and the Pro tier is [unverified].
- **Risks:**
  - Wayland is experimental.
  - The vendor's commercial model has shifted twice.
  - The idle figure (0.20% to <0.01%) was measured on Android only (https://avaloniaui.net/blog/avalonia-12). Before 12, LightBulb found the render loop dominated its background CPU (https://github.com/Tyrrrz/LightBulb/issues/273).

**Uno Platform 6.6/6.7**

- **Licence and releases:** Apache-2.0; the latest is Uno.WinUI 6.7.359, 2026-11-05 (https://www.nuget.org/packages/Uno.WinUI).
- **Main strength:** it implements the WinUI API. One solution can keep a native WinAppSDK head on Windows, identical to today's app, plus a Skia `net10.0-desktop` head for Linux (https://platform.uno/docs/articles/getting-started/requirements.html).
- **Linux weaknesses:**
  - X11 and framebuffer only.
  - Under XWayland, "DPI scaling can't be determined reliably", so the scale is set by hand with `UNO_DISPLAY_SCALE_OVERRIDE` (https://platform.uno/docs/articles/features/using-skia-desktop.html).
  - Linux screen-reader support is "planned" (https://infoq.com/news/2026/08/uno-platform-6-6).
- **Performance:**
  - AOT startup on Linux 870 ms → 350 ms; Windows memory 282 → 212 MB (https://platform.uno/blog/native-aot-in-uno-platform-faster-startup-five-platforms/).
  - The Skia desktop build shrank from 200 MB to 52 MB in 6.0 (https://www.infoq.com/news/2025/05/uno-platform-6-release).
- **Tray:** no built-in tray. H.NotifyIcon.Uno is unconfirmed on Linux (https://www.nuget.org/packages/H.NotifyIcon.Uno).
- **Verdict:** the cheapest code path, but Linux would be a second-class build. Re-rank it if Uno ships Linux accessibility and Wayland.

**Gir.Core (GTK4 + libadwaita in C#)**

- **Version:** 0.8.1 stable (2026-07-12), 0.9.0-preview.1, MIT, pre-1.0 API (https://www.nuget.org/packages/GirCore.Gtk-4.0).
- **Coverage:** binds GTK4, libadwaita, Gio, WebKit-6.0 and libsecret. Known gaps include virtual-method overrides (https://github.com/gircore/gir.core).
- **Momentum:** Microsoft's experimental MAUI GTK4 backend is built on it (https://www.nuget.org/packages/Microsoft.Maui.Platforms.Linux.Gtk4/).
- **Linux quality:** the most native GNOME result while staying in C#. GApplication single-instance support maps onto today's "second launch activates the window" behaviour (https://api.pygobject.gnome.org/Gio-2.0/class-Application.html).
- **Downsides:** Linux-only in practice, and the API may churn until 1.0.

### 3.3 Web shells around the Svelte UI

The Svelte UI already follows the desktop flows (D2c). It has 41 components, including SpeedGraph and SegmentMap, and `app.css` already mirrors the `App.xaml` tokens. A shell reuses all of it.

**Two code changes every shell needs:**
- The client connects only to `${location.host}/api/ws` and redirects to `/signin/` on a 401 (`webui/lib/store.svelte.ts:61-68`). It needs a pluggable transport: a WebSocket, or a relay through the shell's native side.
- The Host needs a desktop endpoint. Today the bridge runs only while Options > Web UI is on. Two routes:
  - **A: the shell's native side opens the pipe or Unix socket and relays.** No token is needed; this is the most secure.
  - **B: a local loopback WebSocket with a per-launch token.** Pass the token in the URL fragment or a header, never a query string, and apply the Origin/Host guards from `docs/ipc-protocol.md`. Any local user can reach a loopback port.

**Linux rendering risk (WebKitGTK).**
- Tauri documents blank windows, flicker and the Wayland "Error 71" crash, mostly on NVIDIA (https://v2.tauri.app/develop/debug/linux-graphics/).
- Workarounds: `WEBKIT_DISABLE_DMABUF_RENDERER=1` (do not ship it unconditionally), `__NV_DISABLE_EXPLICIT_SYNC=1` and `WEBKIT_DISABLE_COMPOSITING_MODE=1`.
- Scrolling bugs are open in 2026 (https://bugs.webkit.org/show_bug.cgi?id=305290).

**Idle risk, which matters most to you.**
- WebKitGTK ignores GTK's "suspended" state, so a hidden window can keep using CPU (https://bugs.webkit.org/show_bug.cgi?id=285167).
- `ThreadedCompositor` wake-ups were reported in 2.42 (https://bugs.webkit.org/show_bug.cgi?id=263695).
- Throttling of DOM timers on hidden pages was still NEW in the snapshot found (https://bugs.webkit.org/show_bug.cgi?id=99059).
- On Windows, `IsVisible=false` lets Chromium throttle the page, and `TrySuspendAsync` pauses scripts (https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.trysuspendasync). Pushing events to a hidden page undoes the suspension.
- **Mitigation (design):**
  - The Svelte UI stops its timers, `requestAnimationFrame` loops and speed-graph animation when hidden.
  - The shell destroys the webview when the window closes instead of hiding it. PERF.5 already does this for WinUI.

**Accessibility on Linux is the weak side.**
- WebKitGTK reworked AT-SPI in 2.44 (https://webkitgtk.org/2024/03/27/webkigit-2.44.html), but 2.44.1 emitted no accessibility events (https://auto-bugs.webkit.org/show_bug.cgi?id=273944).
- Tauri's own issue tracker says Tauri is "basically as accessible as the underlying web view" (https://github.com/tauri-apps/tauri/issues/207).

**The shells:**

- **Tauri 2.**
  - Most mature WebKitGTK integration.
  - Packaging: deb, rpm, AppImage, Flatpak, Snap, AUR and a Windows installer (https://v2.tauri.app/distribute/).
  - Plugins for notifications, updater, single-instance, autostart and portal dialogs (https://v2.tauri.app/release/).
  - Versions differ between briefs: 2.12.3 per the release page, 2.12.5 per GitHub releases, 2.12.0 dated 2026-09-26. v3 is in alpha.
  - Linux tray: no click events, menu only (https://v2.tauri.app/learn/system-tray/). This doesn't matter if the Host keeps the tray.
  - Idle memory was close to Electron in one controlled test, 109 MB vs 128 MB (https://betterstack.com/community/guides/scaling-nodejs/tauri-vs-electron-vs-deno-vs-electrobun/).
  - Rust becomes a second language in the repo.
- **Thin C# host.**
  - Windows: `WebView2Aot`, which targets .NET 10 and is AOT-compatible (https://packages.nuget.org/packages/WebView2Aot).
  - Linux: `GirCore.WebKit-6.0` on the API-stable GTK4 line (https://nuget.org/packages/GirCore.WebKit-6.0/0.6.3, https://discourse.gnome.org/t/webkitgtk-for-gtk-4-is-now-api-stable/14378).
  - Gains: all C#, Contracts shared, direct pipe or socket, and exact control over suspension. On Windows it drops the ~200 MB Windows App SDK in favour of the Evergreen runtime that is already installed (https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).
  - Cost: we build our own dialogs, single instance and packaging.
- **Photino / PhotinoX.**
  - Upstream Photino.NET's last release was 2025-01, and maintenance moved to "AI-agent-assisted" in March 2026 (https://github.com/tryphotino/photino.NET).
  - Fork versions differ between briefs: PhotinoX 5.4.1 (2026-11-03) vs 4.2.0 (2026-04-01), both from https://packages.nuget.org/packages/PhotinoX.
  - The idea is right, but the maintenance base is thin.
- **Electron.**
  - Consistent rendering and Wayland by default (https://electronjs.org/blog/electron-38-0).
  - ~244 MiB, a full Chromium, and it duplicates WebView2.
  - Keep it only as a fallback if WebKitGTK fails the spike.
- **Wails v3.** Brand new (3.0.0 on 2026-10-26 per GitHub; another brief still calls it beta), and adds Go (https://github.com/wailsapp/wails/releases).
- **Neutralino.** Too thin a fit for a pipe client.
- **PWA via Chrome or Edge.** Costs nothing today, but has no tray or pipe and needs the bridge or Server running (https://web.dev/learn/pwa/installation).

### 3.4 Non-.NET native toolkits

All of these reuse only the protocol. The 26 UI-free files (about 6.7k lines of rules) would be duplicated in another language, and the UI rewritten: **20–35 pw** each.

- **Qt 6.**
  - The most mature on both systems at once: UIA and AT-SPI (https://doc.qt.io/qt-6/qaccessible.html), an SNI tray (https://doc.qt.io/qt-6/qsystemtrayicon.html), and native on KDE.
  - Its FluentWinUI3 style is "under development", and SplitView, HorizontalHeaderView and TreeViewDelegate fall back to Fusion. Those are the controls a download list uses (https://doc.qt.io/qt-6/qtquickcontrols-fluentwinui3.html).
  - Qt Charts is GPL-only (https://doc.qt.io/qt-6/licensing.html).
  - The C# bridge is a Technology Preview (https://www.qt.io/blog/qt-bridges-technology-preview-rust-csharp).
  - It is the strongest non-.NET option, but it means leaving C#.
- **GTK4 + libadwaita (Rust or Python).** Best on GNOME. GTK 4.24 improves fractional scaling (https://www.phoronix.com/news/GTK-4.24-Released). Poor on Windows. Gir.Core gets the same result in C#.
- **Slint.**
  - Tray since 1.17 (https://slint.dev/blog/slint-1.17-released), and markup close to XAML.
  - The royalty-free licence requires visible Slint credit, which conflicts with "own branding" (https://slint.dev/terms-and-conditions).
  - Accessibility on Linux is unproven.
- **Flutter.** Canonical now leads Flutter desktop (https://www.omgubuntu.co.uk/2026/05/flutter-desktop-canonical-maintained). But it uses a GTK3 embedder, Linux accessibility is weak (https://documentation.ubuntu.com/desktop/en/latest/explanation/accessibility-stack), and the tray needs plugins.

### 3.5 Windows-only alternatives

- **WinUI 3 / Windows App SDK.**
  - 2.x since 2026-04-29; 2.6.5 is current (https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0).
  - Development has been on GitHub since August 2026 (https://www.thurrott.com/dev/340841/microsoft-moves-winui-wasdk-development-to-github).
  - Microsoft has promised memory work, with no date (https://windowslatest.com/2026/07/27/microsoft-admits-windows-11-native-apps-hog-ram-promises-a-winui-performance-boost-before-start-menu-rewrite).
  - Native AOT has been supported since 1.6, with about 50% faster start and a package about 2x smaller when self-contained (https://blogs.windows.com/windowsdeveloper/2024/09/04/whats-new-in-windows-app-sdk-1-6/). We don't set `PublishAot` today, and reflection-based `{Binding}` needs work (https://zenn.dev/shinta0806/articles/native-aot-tips). Our XAML has only 58 bindings, so that work is small.
- **WPF (.NET 10).**
  - Mature, with strong UIA. Fluent theming is "still in progress" (https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100).
  - No AOT [unverified], and no Linux.
  - It would be a rewrite with no gain.
- **WinForms.** Keep it for the Host tray only.

### 3.6 Extra: a TUI

Terminal.Gui 2.5.0 (MIT, net10.0; https://www.nuget.org/packages/Terminal.Gui) could become a `tui` client over the WebSocket for the Docker server and SSH. It reuses Contracts directly. It is a cheap add-on, not a GUI.

## 4. Options ruled out, and why

| Option | Why not |
|---|---|
| MAUI on Linux | Two competing preview tracks: Avalonia's backend is tied to .NET 11 (https://theregister.com/2026/03/23/maui_linux_avalonia) and maui-labs GTK4 is "early / experimental" (https://www.nuget.org/packages/Microsoft.Maui.Platforms.Linux.Gtk4/). MAUI XAML is a rewrite anyway. Watch the GTK4 work. |
| Eto.Forms | Depends on GtkSharp, whose last stable release was 2023 (https://www.nuget.org/packages/GtkSharp): GTK3 only, no libadwaita. A step down in design. |
| Qt via C# (Qt Bridge, Qml.Net, QtSharp) | The bridge is a Technology Preview. Qml.Net has been inactive since 2022 (https://github.com/qmlnet/qmlnet); QtSharp is obsolete. Check again in 2027. |
| WPF, WinForms (as the main window) | Windows-only, a rewrite, no gain over WinUI. |
| Electron (as first choice) | Size (~244 MiB), a bundled Chromium, idle cost, duplicates WebView2. Kept only as a fallback if WebKitGTK fails. |
| Compose Multiplatform | No Linux accessibility, X11 only, bundled JRE (https://kotlinlang.org/docs/multiplatform/compose-desktop-accessibility.html). |
| egui, Dear ImGui | Immediate mode; looks like a tool; ImGui has no accessibility. |
| iced | No upstream accessibility (issue #552 open since 2020). Relevant only for COSMIC. |
| wxWidgets | Wayland tray support is only in PRs; its own widgets have no AT-SPI; hard to match our design. |
| Wails v3, Neutralino | Too new or too thin; adds Go or C++ for no gain over Tauri. |
| Photino upstream | About 21 months without a release; thin maintenance. |
| Uno as the Linux-first frontend | No Wayland, no Linux screen reader, manual DPI. Not ruled out for later. |

## 5. Staged plan

**Stage 0: Windows baseline (about 1 pw, no rewrite)**
- Measure today's WinUI window (method in Stage 3).
- In a branch, set `PublishAot` plus trimming on `DownloadManager.Desktop.WinUI` and fix the `{Binding}`/reflection uses. Measure again.
- This tells us how much of the "~200 MB / slow start" problem can be fixed without leaving WinUI.

**Stage 1: Linux desktop Host role (3–5 pw, needed by every option)**
- Add a desktop mode to `DownloadManager.Server` (new `hostKind`, a Linux `IHostPlatform`), or retarget `Host` to `net10.0`.
- Local IPC over a Unix socket in `$XDG_RUNTIME_DIR` with a peer-user-ID check, on .NET 11.
- An SNI tray in the Host, via Tmds.DBus [unverified fit], optional and with the Host working fully without it.
- Linux equivalents of the Windows parts:

  | Function | Linux route |
  |---|---|
  | Notifications | `org.freedesktop.Notifications` |
  | Sign-in start | XDG autostart or a systemd user unit |
  | Sleep prevention | logind Inhibit |
  | Open / reveal | `xdg-open` and `FileManager1.ShowItems` |
  | Secrets (DPAPI today) | libsecret |
  | Browser native-messaging | Linux manifest paths |

  These routes are all [unverified] in the briefs. Drop the firewall step; updates come from distribution packages or Flatpak.

**Stage 2: two prototypes against the same scenario (about 2–3 pw)**
- **A: Avalonia 12.1 + Native AOT.** Main window with the download list, sidebar and status bar, live progress from the real Host. Run once on X11/XWayland and once with `UseWayland()`.
- **B: the Svelte UI in a shell.** Tauri 2, or the C# WebView2Aot/GirCore WebKit-6.0 host, with the Svelte UI pausing timers and graph animation when hidden and the webview destroyed on close. Also add the pluggable transport and token handoff.
- **Optional C: Uno Skia desktop head** compiled from today's code. Mainly to size the `#if` work; it doubles as a Windows comparison.
- **Scenario:** 500 rows, one updating per second, plus 5 active downloads at full rate. Then the window hidden, then closed with only the Host running.

**Stage 3: measure against today's WinUI window**

| Metric | Windows | Linux |
|---|---|---|
| Idle CPU and wake-ups (visible, idle) | ETW / WPA context switches per second per process; `typeperf "\Process(*)\% Processor Time"` | `perf stat -e context-switches,cpu-clock`, `powertop` wake-ups per second, `pidstat -w` |
| Same, window hidden / closed to tray | Same, counting every child process (WebView2 browser, renderer, GPU) | Same, including WebKit processes |
| Memory | Private working set summed over all processes | PSS via `smem` summed over all processes |
| Cold and warm start | Launch to first list paint (an ETW mark, or a log timestamp when the first snapshot renders) | Same |
| Install and disk size | Install folder size; do not count shared WebView2 Evergreen, but note it | Package size (deb/Flatpak), plus runtime if not shared |
| Under load | CPU while 5 downloads report progress | Same |
| Accessibility | Narrator + Accessibility Insights walk of the main flows | Orca + Accerciser walk |
| Visual/HiDPI | 100/150/200% and per-monitor | GNOME Wayland at 125/150% fractional, KDE, NVIDIA machine for webviews |

Keep the results in the local `benchmarks\` folder, run each three times, and record the median.

**Decision criteria, in your priority order:**
1. Idle wake-ups and CPU with the window hidden or closed must be no worse than today's WinUI, and close to zero for the GUI process itself.
2. Idle CPU with the window visible and no transfers running should be comparable to WinUI.
3. Orca can read and operate the list, Add URL, Options and the context menu. Narrator parity on Windows if the option is meant to replace WinUI.
4. It renders correctly on GNOME Wayland (with fractional scaling) and on NVIDIA.
5. It keeps the current design within reason.
6. Effort and maintenance: language count, vendor risk.

**How to read the results:**
- If B passes criteria 1, 3 and 4 on Linux, ship B as the Linux window soon (3–6 pw). It also covers D6.
- If B fails on WebKitGTK idle, rendering or accessibility, go with A for Linux (14–22 pw).
- Replace WinUI on Windows only if A matches WinUI on Windows idle cost and UIA, and you want one codebase (about +4 pw).
- Otherwise keep WinUI plus AOT on Windows.

## 6. Risks and open questions

**Risks**
- WebKitGTK idle wake-ups and NVIDIA/Wayland rendering could disqualify every webview shell on Linux. Only measurement settles this.
- Avalonia's native Wayland is experimental. XWayland works, but fractional scaling is better with native Wayland.
- Avalonia's vendor model has changed twice. The MIT core is the hedge; avoid paid controls.
- Clipboard monitoring is restricted under Wayland [unverified]. One of today's features may not carry over to Linux.
- The GNOME tray needs an extension. Anything that depends on the tray needs a no-tray path.
- Gir.Core and the WebKit-6.0 bindings are pre-1.0 and their APIs may change.
- A second UI codebase, whether Avalonia next to WinUI or a shell next to WinUI, doubles UI maintenance until one wins.
- Windows App SDK memory work is promised but not shipped. WinUI's idle and memory profile may improve on its own.

**Questions for the owner**
1. Is the Linux window for you only, or should it be publishable (Flathub, distributions)? That decides how much accessibility and packaging matter now.
2. Which Linux desktop and GPU will you use (GNOME or KDE, Wayland, NVIDIA)? It is the first test target and the main WebKitGTK risk.
3. Is a webview-based Linux window acceptable if it looks like the web UI rather than the desktop window?
4. Is one codebase for both systems a goal, or is WinUI on Windows plus a separate Linux window fine for the long term?
5. Is Rust (for Tauri) acceptable in the repo, or should everything stay C#/TypeScript?
6. Does the Linux Host need the tray at all, or would background running plus notifications (GNOME's model) do?
7. Shall Stage 0 (WinUI AOT and the baseline measurement) go ahead on its own, since it is useful whatever is chosen?

## 7. Sources

**WinUI / Windows**
- https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0
- https://www.thurrott.com/dev/340841/microsoft-moves-winui-wasdk-development-to-github
- https://www.windowslatest.com/2026/08/29/windows-11s-native-app-era-is-coming-back-after-years-of-web-app-slop-and-microsoft-is-finally-building-it-in-public/
- https://windowslatest.com/2026/07/27/microsoft-admits-windows-11-native-apps-hog-ram-promises-a-winui-performance-boost-before-start-menu-rewrite
- https://www.pcworld.com/article/3199779/microsoft-admits-its-windows-11-apps-hog-ram-a-fix-is-coming.html
- https://www.windowslatest.com/2026/09/16/windows-11-got-more-web-apps-because-winui-couldnt-get-the-basics-right-now-microsoft-is-fixing-the-mess/
- https://www.windowslatest.com/2026/10/31/one-reason-windows-11-has-so-many-web-apps-winui-still-lacked-basic-features-and-thats-finally-changing/
- https://blogs.windows.com/windowsdeveloper/2024/09/04/whats-new-in-windows-app-sdk-1-6/
- https://windowsreport.com/microsoft-unveils-windows-app-sdk-1-6-with-native-aot-support-improved-controls-and-more/
- https://microsoft.github.io/microsoft-ui-reactor/latest/packaging/
- https://zenn.dev/shinta0806/articles/native-aot-tips
- https://www.nuget.org/packages/H.NotifyIcon.WinUI/2.5.0-dev.2
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://platform.uno/articles/wpf-modernization-in-2026-a-source-backed-decision-guide/ (vendor)
- https://platform.uno/articles/winui-alignment-is-a-feature-not-a-dependency/ (vendor)

**Avalonia**
- https://avaloniaui.net/blog/avalonia-12
- https://avaloniaui.net/blog/release-12-1
- https://www.nuget.org/packages/Avalonia
- https://www.nuget.org/packages/Avalonia.Wayland
- https://avaloniaui.net/blog/bringing-wayland-support-to-avalonia
- https://avaloniaui.net/blog/building-a-sustainable-future-for-avalonia
- https://avaloniaui.net/blog/retiring-accelerate
- https://avaloniaui.net/legal/1A
- https://avaloniaui.net/xpf/pricing
- https://docs.avaloniaui.net/docs/supported-platforms
- https://docs.avaloniaui.net/docs/app-development/accessibility
- https://docs.avaloniaui.net/controls/navigation/trayicon
- https://docs.avaloniaui.net/docs/services/file-dialogs
- https://docs.avaloniaui.net/docs/how-to/notifications-how-to
- https://docs.avaloniaui.net/docs/deployment/native-aot
- https://docs.avaloniaui.net/docs/deployment/linux
- https://docs.avaloniaui.net/docs/platform-specific-guides/linux
- https://docs.avaloniaui.net/api/avalonia/avaloniawaylandplatformextensions
- https://docs.avaloniaui.net/controls/web/nativewebview
- https://github.com/AvaloniaUI/Avalonia
- https://en.wikipedia.org/wiki/Avalonia_(software_framework)
- https://github.com/Tyrrrz/LightBulb/issues/273
- https://github.com/pr8x/DesktopNotifications
- https://github.com/lixinyang123/AvaloniaAOT
- https://lightrun.com/answers/avaloniaui-avalonia-istorageprovideropenfilepickerasync-does-not-return

**Uno**
- https://www.nuget.org/packages/Uno.WinUI
- https://platform.uno/docs/articles/getting-started/requirements.html
- https://platform.uno/docs/articles/features/using-skia-desktop.html
- https://platform.uno/docs/articles/features/using-skia-gtk.html
- https://platform.uno/docs/articles/migrating-apps.html
- https://learn.microsoft.com/en-us/windows/apps/how-tos/uno-multiplatform
- https://www.infoq.com/news/2025/05/uno-platform-6-release
- https://infoq.com/news/2026/08/uno-platform-6-6
- https://platform.uno/blog/uno-platform-6-6/
- https://platform.uno/blog/uno-platform-6-4/
- https://platform.uno/blog/native-aot-in-uno-platform-faster-startup-five-platforms/
- https://platform.uno/pricing
- https://github.com/unoplatform/uno
- https://www.nuget.org/packages/H.NotifyIcon.Uno

**MAUI, Eto, Gir.Core, Qt (C#)**
- https://avaloniaui.net/blog/maui-avalonia-preview-1
- https://theregister.com/2026/03/23/maui_linux_avalonia
- https://www.nuget.org/packages/Microsoft.Maui.Platforms.Linux.Gtk4/
- https://nubrowse.com/packages/Eto.Platform.Gtk
- https://www.nuget.org/packages/GtkSharp
- https://www.nuget.org/packages/GirCore.Gtk-4.0
- https://github.com/gircore/gir.core
- https://nuget.org/packages/GirCore.WebKit-6.0/0.6.3
- https://www.nuget.org/packages/WebKit.BlazorWebView.GirCore
- https://doc.qt.io/qtbridge-csharp/introduction.html
- https://www.nuget.org/packages/QtGroup.Qt.Bridge.CSharp.linux-x64
- https://www.qt.io/blog/qt-bridges-technology-preview-rust-csharp
- https://doc-snapshots.qt.io/qtbridge-csharp/release-status.html
- https://github.com/qmlnet/qmlnet
- https://github.com/ddobrev/qtsharp
- https://www.nuget.org/packages/Terminal.Gui
- https://blog.kindel.com/2026/03/05/terminal-gui-still-absurd-now-beta/

**Web shells**
- https://v2.tauri.app/release/
- https://v2.tauri.app/release/tauri/v2.12.0/
- https://github.com/tauri-apps/tauri/releases
- https://v2.tauri.app/develop/debug/linux-graphics/
- https://v2.tauri.app/learn/system-tray/
- https://v2.tauri.app/distribute/
- https://v2.tauri.app/distribute/flatpak/
- https://v2.tauri.app/blog/tauri-2-0-0-alpha-3/
- https://v2.tauri.app/release/@tauri-apps/api/v2.3.0
- https://v1.tauri.app/v1/guides/features/system-tray/
- https://github.com/tauri-apps/tauri/issues/12562
- https://github.com/tauri-apps/tauri/issues/11210
- https://github.com/tauri-apps/tauri/issues/5889
- https://github.com/tauri-apps/tauri/issues/207
- https://docs.rs/crate/tauri-plugin-dialog/2.5.0/source/README.md
- https://docs.rs/tray-icon/0.23.0/src/tray_icon/lib.rs.html
- https://docs.rs/webkit2gtk-nvidia-quirk
- https://github-redirect.dependabot.com/tauri-apps/wry/discussions/1014
- https://dev.to/gethopp/tauri-vs-electron-performance-bundle-size-and-the-real-trade-offs-1el4
- https://betterstack.com/community/guides/scaling-nodejs/tauri-vs-electron-vs-deno-vs-electrobun/
- https://tech-insider.org/tauri-vs-electron-2026/ (non-authoritative)
- https://bugs.webkit.org/show_bug.cgi?id=285167
- https://bugs.webkit.org/show_bug.cgi?id=263695
- https://bugs.webkit.org/show_bug.cgi?id=99059
- https://bugs.webkit.org/show_bug.cgi?id=305290
- https://bugs.webkit.org/show_bug.cgi?id=221738
- https://webkitgtk.org/2024/03/27/webkigit-2.44.html
- https://auto-bugs.webkit.org/show_bug.cgi?id=273944
- https://discourse.gnome.org/t/webkitgtk-for-gtk-4-is-now-api-stable/14378
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.trysuspendasync
- https://learn.microsoft.com/microsoft-edge/webview2/concepts/performance
- https://packages.nuget.org/packages/WebView2Aot
- https://releases.electronjs.org/
- https://electronjs.org/docs/latest/tutorial/electron-timelines
- https://electronjs.org/blog/electron-38-0
- https://electronjs.org/docs/latest/api/tray
- https://www.electronjs.org/docs/latest/api/browser-window
- https://endoflife.date/electron
- https://github.com/wailsapp/wails/releases
- https://docs-preview.wails.io/changelog/
- https://www.nuget.org/packages/Photino.NET
- https://github.com/tryphotino/photino.NET
- https://github.com/tryphotino/photino.Native
- https://packages.nuget.org/packages/PhotinoX
- https://neutralino.js.org/docs/release-notes/framework/
- https://web.dev/learn/pwa/installation
- https://piunikaweb.com/2026/06/22/firefox-web-app-pwa-support-linux/

**Native non-.NET**
- https://wiki.qt.io/Qt_6.11_Release
- https://doc.qt.io/qt-6/qt-releases.html
- https://doc.qt.io/qt-6/licensing.html
- https://doc.qt.io/qt-6/qtquickcontrols-fluentwinui3.html
- https://doc.qt.io/qt-6/qsystemtrayicon.html
- https://doc.qt.io/qt-6/qaccessible.html
- https://bugs.kde.org/show_bug.cgi?id=353062
- https://discuss.kde.org/t/flatpak-org-kde-sdk-6-10-runtime-is-now-available/41158
- https://www.phoronix.com/news/GTK-4.24-Released
- https://release.gnome.org/50/developers/
- https://release.gnome.org/49/developers/
- https://api.pygobject.gnome.org/Gio-2.0/class-Application.html
- https://docs.rs/ksni
- https://gtk.org/docs/installations/windows/
- https://discourse.gnome.org/t/system-tray-icons-in-gtk4/22615
- https://discourse.gnome.org/t/what-to-use-instead-of-statusicon-in-gtk4-to-display-the-icon-in-the-system-tray/7175
- https://slint.dev/blog/slint-1.17-released
- https://slint.dev/terms-and-conditions
- https://github.com/AccessKit/accesskit
- https://docs.flutter.dev/platform-integration/linux/setup
- https://www.omgubuntu.co.uk/2026/05/flutter-desktop-canonical-maintained
- https://documentation.ubuntu.com/desktop/en/latest/explanation/accessibility-stack
- https://github.com/iced-rs/iced/releases/tag/0.14.0
- https://kotlinlang.org/docs/multiplatform/compose-desktop-accessibility.html
- https://www.wxwidgets.org/news/2026/07/wxwidgets-3.2.11-and-3.3.3-released/

**Linux platform**
- https://learn.microsoft.com/en-ie/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions
- https://visualstudiomagazine.com/articles/2026/09/14/net-11-rc1-gets-go-live-support-ahead-of-november-launch.aspx
- https://linuxiac.com/how-to-enable-system-tray-icons-in-gnome/
- https://tracker.debian.org/pkg/gnome-shell-extension-appindicator

**Codebase facts**
These come from a read-only pass over `C:\Users\kaank\Downloads\idm\.claude\worktrees\dev`, covering `src\DownloadManager.Desktop.WinUI`, `Contracts`, `Host`, `Server`, `webui\` and `docs\ipc-protocol.md`. The codebase brief was cut off partway through its Qt section, so its effort figure for Qt is the group figure of 20–35 pw.

## Appendix: Fact-check of D7 (2026-11-09)

I checked 14 claims against current web sources. None of the main version or status claims is wrong. Six of them need a correction or a caveat, and the recommendation still holds.

### Claims

1. **Avalonia 12.0 GA on 2026-04-07 on .NET 10, compiled bindings on by default; 12.1 on 2026-07-08; latest patch 12.1.5 on 2026-11-02; MIT.** Confirmed.
   - Sources: https://avaloniaui.net/blog/avalonia-12, https://avaloniaui.net/blog/release-12-1, https://www.nuget.org/packages/Avalonia
   - NuGet dates 12.1.0 to 2026-07-09, one day after the blog post. 12.1.4 (2026-10-09) is one patch behind, so the one brief that cites 12.1.4 via Wikipedia is out of date.

2. **Avalonia native Wayland is experimental and opt-in in 12.1 (`Avalonia.Wayland` plus `UseWayland()`), otherwise XWayland.** Confirmed.
   - Source: https://avaloniaui.net/blog/release-12-1 ("graduated from private preview"; `UsePlatformDetect()` does not select it).
   - `Avalonia.WinUI` (embedding in WinUI 3) and `TableView` are also confirmed there.

3. **Avalonia AT-SPI2 on Linux, "a first for a .NET framework"; UIA listed as full.** Confirmed, with a caveat.
   - The "first .NET UI framework to ship a native Linux accessibility backend" wording is the vendor's own claim in the 12.0 post, not independently verified (https://avaloniaui.net/blog/avalonia-12).
   - The docs table lists UIA, NSAccessibility and AT-SPI2 all as "Full support" (https://docs.avaloniaui.net/docs/app-development/accessibility).
   - **Corrected sentence:** "Accessibility: an AT-SPI2 backend on Linux, which Avalonia's own announcement calls a first for a .NET UI framework (a vendor claim), and both UIA and AT-SPI2 listed as full support in the docs."

4. **Avalonia idle CPU went from 0.20% to <0.01%, measured on Android only.** Confirmed.
   - Source: https://avaloniaui.net/blog/avalonia-12 (no desktop figure given).

5. **Windows App SDK 2.0 stable on 2026-04-29; 2.6.5 stable on 2026-11-02.** Confirmed.
   - Source: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0
   - There is also a 2.6.6-Experimental from 2026-11-06.

6. **WinUI / Windows App SDK development moved to GitHub in August 2026; table says "MIT [unverified]".** Partly wrong: the wording overstates how open WinUI is.
   - Thurrott (2026-08-29) says engineers now work in the open on GitHub, but only Microsoft staff can change the code, and WinUI/WASDK is not yet fully open source (https://www.thurrott.com/dev/340841/microsoft-moves-winui-wasdk-development-to-github).
   - The repo itself is MIT, publishes source and says code contributions are not yet accepted (https://github.com/microsoft/microsoft-ui-xaml).
   - **Corrected sentence:** "Development has been in the open on GitHub since August 2026 (MIT-licensed repo), but outside contributions are not yet accepted and Microsoft calls full open-sourcing a later goal."
   - **Corrected table cell:** Licence "MIT (microsoft-ui-xaml repo)".

7. **Uno.WinUI 6.7.359 on 2026-11-05, Apache-2.0.** Confirmed.
   - Source: https://www.nuget.org/packages/Uno.WinUI

8. **Uno Linux: X11 and framebuffer only, manual DPI under XWayland (`UNO_DISPLAY_SCALE_OVERRIDE`), Linux screen reader "planned".** Confirmed.
   - Sources: https://platform.uno/docs/articles/features/using-skia-desktop.html (the docs say "DPI scaling cannot be determined in a reliable way"; the report's "can't" wording is a paraphrase), https://infoq.com/news/2026/08/uno-platform-6-6 (dated 2026-08-07).

9. **Uno performance: AOT start on Linux 870 to 350 ms; Windows memory 282 to 212 MB; Skia desktop size 200 to 52 MB in 6.0.** Confirmed, but the size figure is wrongly sourced and needs a caveat.
   - The 870/350 ms figure is for the Uno.Chefs app and the 282/212 MB figure for the Build Pulse app, both on .NET 10 (https://platform.uno/blog/native-aot-in-uno-platform-faster-startup-five-platforms/, 2026-08-06).
   - The cited InfoQ article does not give 200/52 MB; it only says "-74% on Windows" and "100 MB" less (https://www.infoq.com/news/2025/05/uno-platform-6-release).
   - The 200 to 52 MB figure is in Uno's own 6.0 page (https://platform.uno/6-0), and it is for a blank `dotnet new unoapp` Win32 app.
   - **Corrected sentence:** "The blank-template Skia desktop app shrank from 200 MB to 52 MB in 6.0, per Uno's own figures (https://platform.uno/6-0)."
   - **Corrected table cell:** "Skia desktop ~52 MB (blank template, vendor figure)".

10. **Tauri 2.12.x; v3 in alpha; no tray click events on Linux.** Confirmed, and the version conflict is resolved.
    - The latest core release is v2.12.5, dated 21 Oct (the page shows no year), and v3.0.0-alpha.6 is from 06 Nov (https://github.com/tauri-apps/tauri/releases).
    - On Linux the tray click, double-click, enter, move and leave events are not emitted; a right click still shows the menu (https://v2.tauri.app/learn/system-tray/).
    - **Corrected sentence:** "Latest is 2.12.5 (GitHub releases; the release page lags at 2.12.3); v3 is in alpha (3.0.0-alpha.6)."

11. **.NET 11 due 2026-11-10 sets the socket file to mode 0600.** Wrong in scope: 0600 applies only with `PipeOptions.CurrentUserOnly`.
    - Microsoft's breaking-change note: introduced in .NET 11 Preview 4; without `CurrentUserOnly` the socket still inherits the umask. `CurrentUserOnly` already rejected cross-user connections at connect time by checking the peer's credentials (https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions).
    - The GA date of 2026-11-10 is confirmed, as a Standard Term Support release (https://visualstudiomagazine.com/articles/2026/09/14/net-11-rc1-gets-go-live-support-ahead-of-november-launch.aspx).
    - **Corrected sentence:** "With `PipeOptions.CurrentUserOnly`, .NET 11 (GA 2026-11-10) chmods the socket file to 0600 after bind; `CurrentUserOnly` already rejects cross-user peers at connect time, so a separate Unix `PipeOwner.VerifyServer` check may be largely redundant, though the client side still needs its own server-identity check [unverified]."
    - The `$XDG_RUNTIME_DIR` point stands, since the default path is under `/tmp` (`/tmp/CoreFxPipe_<name>`, per the same doc).

12. **Slint's royalty-free licence requires visible Slint credit, which conflicts with "own branding".** Overstated.
    - Either the `AboutSlint` widget in an About dialog reachable from the top-level menu (or the splash screen if there is none), or an attribution badge on a public web page, preferably the download page. Desktop is permitted; embedded is excluded (https://slint.dev/terms-and-conditions).
    - **Corrected sentence:** "The royalty-free licence requires an AboutSlint widget in the About dialog or a Slint badge on a public download page. That is a small branding cost, not a blocker."

13. **Qt FluentWinUI3 is "under development"; SplitView, HorizontalHeaderView and TreeViewDelegate fall back to Fusion.** Confirmed.
    - Source: https://doc.qt.io/qt-6/qtquickcontrols-fluentwinui3.html (VerticalHeaderView, StackView and others also fall back).

14. **Photino.NET upstream stale since 2025-01, "AI-agent-assisted" since March 2026; PhotinoX 5.4.1 vs 4.2.0; fork "thinly maintained".** Upstream is confirmed. The PhotinoX conflict is resolved in favour of 5.4.1, and "thinly maintained" could not be verified.
    - Photino.NET 4.0.16 is from 2025-01-23 (https://www.nuget.org/packages/Photino.NET), and the 2026-03-26 banner announces the AI-assisted workflow (https://github.com/tryphotino/photino.NET).
    - PhotinoX 5.4.1 is from 2026-11-03, with five releases between 2026-10-01 and 2026-11-03, Apache-2.0 (https://www.nuget.org/packages/PhotinoX).
    - **Corrected sentence:** "Fork: PhotinoX 5.4.1 (2026-11-03), released often, but with an unknown number of maintainers."

### Does the recommendation follow from the evidence and the owner's priorities?

Mostly yes.

- **Idle cost.** The report says no desktop idle figures exist, makes measuring idle wake-ups the first decision criterion, and puts "Host keeps the tray, window closes" (PERF.5) in front. That fits "idle over memory".
- **WebKitGTK risk.** It is backed by the cited bugs and correctly left to measurement.

Four gaps:

1. **The design criterion is dropped from the rule.** "Ship B if it passes criteria 1, 3 and 4" leaves out criterion 5 (keeps the current design). The owner likes the current desktop design, so B should also need a yes to owner question 3, or pass criterion 5.
2. **The Avalonia accessibility advantage is vendor-claimed.** It should be weighted as unproven until Orca is tested (Stage 3 already covers this).
3. **"Uno … no Linux screen reader yet" is accurate.** The verdict to re-rank Uno later holds.
4. **The IPC section overstates the new security work.** Because of claim 11, the Linux Host IPC effort may be slightly lower than stated. That does not change the ranking.

The overall answer stands: Avalonia for one native frontend, the Svelte UI in a shell as the cheapest Linux window, and WinUI kept on Windows with an AOT trial.
