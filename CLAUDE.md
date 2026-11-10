# CLAUDE.md — agent onboarding

Read this first, then `GOAL.md`, `ROADMAP.md`, and `PROGRESS.md`.
`GOAL.md` and `ROADMAP.md` are the fallback for the unavailable Goal/Todo tools;
treat them as authoritative tracking. Update them after every meaningful
increment.

## What this project is

A private-use Windows download manager, **Inbound Download Manager**. It is a
new C#/.NET application with a WinUI 3 frontend, a separate background host and
download engine, and our own Manifest V3 Chrome extension + Native Messaging
host.

Never commit cookies, credentials, browser profiles, or captures containing
secrets.

## Current status (2026-11-10, release 0.6.6, extension 0.6.1)

Phases 0–5 are substantially implemented: single-stream engine, resume/crash
recovery, tray/host/IPC/CLI, the WinUI shell, segmented transfers, and
Chrome automatic + context-menu handoff, including authenticated cookie,
referrer and user-agent transfer. Phase 8 (queues/scheduler) is complete,
including completion actions, quotas, sleep prevention, and DST behavior.
Payload bytes are written directly into a destination-local part file and renamed in place, with
no fixed temp directory or cross-volume stitching. `PROGRESS.md` is authoritative for status. Remaining
verification includes independent interruption/restart checks. Clipboard
URL monitoring, batch/numbered URLs (CLI + WinUI), editable descriptions,
duplicate Rename/Overwrite/Fail policies, category destination rules,
last-folder memory, JSON list export/import, completion dialog/notification/
sound, Download All link collection/filter/selection, batch handoff,
FTP auth/resume, post-download hooks, external CLI control, connection profiles,
settings transfer, installer/native-host repair, upgrade/uninstall, installer-
only updater (check/download/install/rollback/notifications/gating), the WinUI
scheduler, list state views, multiselect bulk actions, delete-file choice,
shell open/reveal, progress details, persisted layout, and recent-URL history are
implemented.
Edge and Firefox integration is in scope again (owner, 2026-11-06; ROADMAP EXT.E1).
Edge users install the Chrome Web Store listing (same ID); Setup, repair and the Host register the native host under
Edge's key too. In Edge the owner checked automatic catching (2026-11-08). Firefox gets its own build and gecko ID, which the app accepts
from 0.6.0; checked in Firefox 159 with a test profile (EXT.B3). In Firefox, downloads are caught from
their response headers with blocking `webRequest`, before Firefox's own Save dialog (EXT.B5).
Extension 0.5.0 sends downloads automatically by default. Its popup lists the newest 20 downloads of the app (and
of a paired server) and can pause, resume, retry and delete them (and the file). It can optionally pair with the user's own Inbound server. A download
from a site the extension has no access to stays in the browser with a note (EXT.R5).
HLS master/media assembly including AES-128 (single and rotating keys) and
alternate-audio muxing, DASH SegmentTemplate/Timeline/SegmentList/SegmentBase
byte ranges, DASH A/V muxing via FFmpeg, HLS/DASH fragment checkpoint recovery,
expiring segment-token refresh,
explicit live-stream exclusion and DRM detection are implemented. HLS AES-128
VOD decryption supports key rotation. Authenticated HTTP/HTTPS proxy,
SOCKS4/4a and SOCKS5, DPAPI site logins and proxy-password protection, redirect
credential boundaries, and expired-URL refresh are verified.
TorBox and archive extraction ship in 0.5.x. 0.6.3 adds the web UI to the Windows app (D8): Setup ships `DownloadManager.Server.exe`, which the Host runs as a bridge (`--bridge`) only while Options > Web UI is on, with browser sessions limited to the app's folders. The desktop window can manage another Inbound server and the web UI can switch hosts (D9); the web UI follows the desktop window's flows (D2c). These were built and tested automatically but not yet tried in the running app. The headless server, web UI and Docker image (ROADMAP D1–D4) ship in
0.6.0 for linux/amd64 only (D1 is still in progress); see `docs\docker.md` and `docs\docker-webui-plan.md`.
Clean install, upgrade (0.5.3 to 0.6.0), sign-in start and uninstall are verified on a fresh Windows VM (TEST.2).
Setup uses Windows Restart Manager rather than force-killing by process name, and uninstall retains state.
0.6.0 trims the install folder from 281 MB to about 200 MB (REV.3, partial: the VM run with the trimmed Setup is
pending). Closing the window now ends the window process by default; the Host keeps
running (PERF.5).

Browser evidence is recorded in `PROGRESS.md`: Chrome for Testing loaded the
extension as `pknddmneejadnnngffojnoecojhdfalc`; automatic and context-menu
handoffs completed with matching SHA-256 hashes. The extension is at
`extensions\chrome\dist` and can be loaded unpacked in normal Chrome.

## Architecture

One per-user **Host** process owns engine, persistence, scheduler, tray, and IPC.
The WinUI app, CLI, and Chrome native host are clients. This is deliberate:
single-writer durability and one lifecycle authority, while keeping the UI
replaceable. Orchestration lives in `DownloadManager.Runtime`, which is
UI-agnostic and directly testable.

| Project | Purpose | TFM / notes |
| --- | --- | --- |
| `DownloadManager.Core` | domain models, states, policies, interfaces | `net10.0`; no WinUI/Windows App SDK |
| `DownloadManager.Contracts` | versioned IPC DTOs/commands/events | `net10.0` |
| `DownloadManager.Engine` | HTTP transfers, segmentation, resume, retries, limits | `net10.0`; no presentation |
| `DownloadManager.Persistence` | atomic job/settings/checkpoint stores + migrations | `net10.0` |
| `DownloadManager.Runtime` | job orchestration, scheduling, dedupe, reconciliation | `net10.0` |
| `DownloadManager.Host` | background host, named-pipe server, tray, lifecycle | `net10.0-windows` |
| `DownloadManager.Tray` | tray icon/menu library | `net10.0-windows`, WinForms |
| `DownloadManager.Desktop.WinUI` | replaceable WinUI 3 frontend | `net10.0-windows10.0.19041.0`, WinUI 3 |
| `DownloadManager.NativeHost` | Chrome Native Messaging bridge | `net10.0` |
| `DownloadManager.Cli` | headless control/diagnostics | `net10.0` |
| `extensions/chrome` | MV3 TypeScript extension, own ID/assets | Node/tsc |
| `tests/*` | unit + integration + fault tests | xUnit |
| `tools/TestServer` | controlled HTTP fault-injection server | `net10.0` |

IPC: per-user named pipe, current-user-only ACL, JSON envelopes with
`protocolVersion`, snapshot on connect then events. Native Messaging stdout is
protocol-only; log to stderr/file. Commands and broadcasts live in the
transport-free `Runtime/Ipc/HostCommandDispatcher`; `HostPipeServer` is only the
pipe transport, and machine actions go through `IHostPlatform`.

Browser strategy: the extension observes eligible Chrome downloads and sends
URL, final URL, cookies, referrer, UA and relevant headers to our native host;
the host durably records and ACKs before Chrome’s download is cancelled. On any
failure Chrome’s download is resumed. `blob:` and `data:` downloads stay in Chrome.
A form POST cannot be told apart from a GET, so it can fail in the app once accepted. See `docs/browser-integration.md`.

## UI structure (current Inbound shell)

Dark/light WinUI shell (theme tokens in `App.xaml`, rules in
`docs/design-guidelines.md`) with menu bar (`Tasks`, `File`, `Downloads`,
`View`, `Help`), grouped icon+label toolbar (Add URL | Resume, Stop, Stop All |
Delete, Delete Completed | Options, Scheduler | Start/Stop Queue), category
sidebar (categories, Unfinished/Active/Queued/Failed, Finished, Queues), the
download list (File Name, Q, Size, Status, Time left, Transfer rate, Last Try
Date, Description) with expandable packages, a collapsible bottom speed graph
(a slim bar that opens upward by design) and a status bar. Secondary windows (Options,
Scheduler, progress, statistics) use `WindowLayout.ApplyChrome`. Closing hides
the window; a second launch activates the existing UI via a named event.

## Commands

```powershell
dotnet build DownloadManager.slnx -c Release
dotnet test tests\DownloadManager.Engine.Tests\DownloadManager.Engine.Tests.csproj -c Release
dotnet test tests\DownloadManager.Integration.Tests\DownloadManager.Integration.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\test.ps1
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
```

Extension:

```powershell
cd extensions\chrome
npm ci
npx tsc --noEmit
npm run build
# chrome://extensions -> Developer mode -> Load unpacked -> dist
```

TorBox live tests call the real API, so they are opt-in. The project builds with the solution, but
`dotnet test` finds nothing in it without `-p:RunLiveTests=true`, `scripts\test.ps1` doesn't list it,
and without the key each test skips and says why. Set the key for this one shell, and never echo it.
Output carries no key, link or name.

```powershell
# Reads only.
dotnet test tests\DownloadManager.TorBox.LiveTests\DownloadManager.TorBox.LiveTests.csproj -c Release -p:RunLiveTests=true --filter Category=TorBoxLive --logger "console;verbosity=detailed"
# Sends only-if-cached requests for invented content, which create nothing. Also needs TORBOX_LIVE_SUBMIT=1.
dotnet test tests\DownloadManager.TorBox.LiveTests\DownloadManager.TorBox.LiveTests.csproj -c Release -p:RunLiveTests=true --filter Category=TorBoxLiveSubmit --logger "console;verbosity=detailed"
```

## Conventions

- Nullable enabled, implicit usings, file-scoped namespaces, async APIs with
  `CancellationToken`, no `.Result`/`.Wait()` on I/O. A few synchronous bridge
  entry points use `GetAwaiter().GetResult()` intentionally.
- Engine must remain UI-agnostic and testable against the local HTTP server.
- All durable writes: temp file in same directory + atomic replace + fsync where
  practical. Checkpoints must be recoverable after a kill at any point.
- Payload placement: never put download bytes in the state directory. The job's
  single random-access part file must live in `DestinationDirectory`
  (`<final>.<jobId>.part`), with ranges written at final offsets, so completion
  is a same-volume rename. Do not reintroduce a fixed temp directory or an
  assembly copy. Only metadata/checkpoints stay in app data.
- Never mark a feature `implemented` without linked acceptance evidence.
  Use `partial`, `blocked`, or `unverified` otherwise.
- Do not add unlabelled stubs or settings that do nothing.
- Own branding/assets only.
- Keep `PROGRESS.md` current at each checkpoint; commit coherent increments.

## Review delegation (user requirement, 2026-11-04)

For Claude-led sessions, delegate simple work and reviews to
`claude-agent/haiku`; for OpenAI/GPT-led sessions, use `openai/gpt-6-luna`.
Generally, every main agent should delegate routine/simple work to a cheaper
capable available model. Simple work includes small changes, diff/commit
reviews, documentation/encoding checks, evidence wording, and coverage
spot checks. Discover exact model IDs; do not guess. Verify delegated outputs.
If the preferred model is unavailable, disclose that and use an available
cheaper capable equivalent, or ask if none is available. Keep complex work
(concurrency, durability, security, IPC protocol changes, architecture) in the
main model. Give reviewers full context and treat findings as input to verify,
not as acceptance evidence.

## Known traps

- The OpenCode environment has **no Goal/Todo tools**; use the markdown files.
- **Chrome stable 156 removed `--load-extension`**. Manual “Load unpacked” works;
  for automation use Chrome for Testing 156+ (verified path:
  `%LOCALAPPDATA%\Temp\opencode\cft2\chrome-win64\chrome.exe`; the older
  `...\opencode\cft\chrome-win64` copy lost its version manifest and fails to start with a side-by-side error).
  Chrome for Testing loads the extension with `--load-extension` and a fresh
  profile. Do not waste time automating Chrome stable's folder picker.
  CfT reads `HKCU\Software\Chromium\NativeMessagingHosts` before Chrome's key, so register a test
  build with `scripts\register-test-native-host.ps1 -NativeExe <test NativeHost.exe>` and run it with
  `-Remove` afterwards. It refuses when the key is taken (another track's test) and never touches the
  Chrome, Edge or Firefox keys (TEST.3).
- Firefox without writing any key (it uses the installed app's Firefox key): start `firefox.exe --headless --no-remote --remote-debugging-port <p>
  --profile <fresh dir>` with `DOWNLOADMANAGER_INSTANCE`/`DOWNLOADMANAGER_DATA` set, and load
  `extensions\chrome\dist-firefox` with WebDriver BiDi `webExtension.install`. The installed native host
  then connects to the test instance's Host (EXT.B3 check, 2026-11-08).
- Mandatory enterprise policy is not a reliable path here; use CfT/manual.
- An agent shell can read a stale native-host registry value while a normal terminal reads the real one,
  and Chrome launched from the agent shell then reports "Specified native messaging host not found".
  Run browser tests from a normal terminal. The reason the views differ is not established; to read the
  real value from an agent shell use WMI `StdRegProv` `GetStringValue` under `HKEY_USERS\<SID>`.
- The harness denies child-process writes to user `Downloads` and default
  LocalAppData, so Host state falls back to `artifacts\app\data` during tests.
  Use `test-downloads` for browser-test destinations.
- WinUI 3 unpackaged apps need the Windows App SDK runtime or self-contained
  deployment. Build the UI with `-p:Platform=x64`; the solution build does this
  automatically now.
- The published WinUI app needs `DownloadManager.Desktop.WinUI.pri` next to the
  exe. `scripts\publish.ps1` copies it and fails loudly if missing. A startup
  `XamlParseException` in `MainWindow.InitializeComponent` while the bin build
  runs is the symptom of a missing/stale `.pri`.
- Native-host registration is per user and must point at an absolute
  `DownloadManager.NativeHost.exe`; `scripts\register-native-host.ps1` does this.
- A store zip or CRX unpacked by hand has no `key`, so its ID comes from its folder and the native
  host is `forbidden`. That is not a release fault. Use `scripts\make-store-id-test.ps1`.
- Never dump registry values containing cookies into committed files; redact.
- The repo root has an explicit **Low mandatory integrity label** (agent sandbox setup); everything
  built here inherits it, and Windows runs Low-labelled exes at Low integrity. That is the real cause
  of "harness can't write LocalAppData/Downloads" and of the local installer failing with
  "unable to create the directory %TEMP%\is-*.tmp, Error 5" unless run as admin.
  `scripts\build-installer.ps1` now relabels the setup Medium; check with `icacls <file>`.
- TestServer and UI executables lock their output during builds; stop them or use
  `scripts\publish.ps1`, which handles this.
