# CLAUDE.md — agent onboarding

Read this first, then `GOAL.md`, `ROADMAP.md`, `reference notes`, and `PROGRESS.md`.
`GOAL.md` and `ROADMAP.md` are the fallback for the unavailable Goal/Todo tools;
treat them as authoritative tracking. Update them after every meaningful
increment.

## What this project is

A private-use Windows download manager with verified functional parity against
the installed **another download manager 6.43.12.3** (`v6.43b12 Full`). It is a
new C#/.NET application with a WinUI 3 frontend, a separate background host and
download engine, and our own Manifest V3 Chrome extension + Native Messaging
host. It is **not** a patch, crack, or license bypass of reference app, and it does not
copy reference app assets or code.

Pinned reference details and hashes: `notes/reference-version.md`.
Never commit reference app binaries, cookies, credentials, browser profiles, or captures
containing secrets.

## Current status (2026-11-04)

Phases 0–5 are substantially implemented: single-stream engine, resume/crash
recovery, tray/host/IPC/CLI, reference app-shaped WinUI shell, segmented transfers, and
Chrome automatic + context-menu handoff, including authenticated cookie,
referrer and user-agent transfer. Phase 8 is partly implemented: named
queues, per-queue concurrency, start/stop, queue assignment/reassignment,
persistence, and daily/one-time schedule execution are tested; completion
actions, quotas, sleep prevention, and DST/wall-clock tests remain. Payload bytes are
written directly into a destination-local part file and renamed in place, with
no fixed temp directory or cross-volume stitching. Full 1:1 parity is **not**
complete; `reference notes` and `PROGRESS.md` are authoritative. Remaining large
areas: everyday management gaps, additional DASH/HLS modes and AES-128, the
remaining grabber polish, installer/updater work, and clean-environment
install/upgrade verification. Phase 8 (queues/scheduler) is complete; clipboard
URL monitoring, batch/numbered URLs (CLI + WinUI), editable descriptions,
duplicate Rename/Overwrite/Fail policies, category destination rules,
last-folder memory, JSON list export/import, completion dialog/notification/
sound, Download All link collection/filter/selection, batch handoff, a scoped
site grabber with offline link rewriting, persistent/scheduled grabber projects,
FTP auth/resume, post-download hooks, external CLI control, connection profiles,
settings transfer, installer/native-host repair, upgrade/uninstall, installer-
only updater (check/download/install/rollback/notifications/gating), the WinUI
scheduler, list state views, multiselect bulk actions, delete-file choice,
shell open/reveal, progress details, persisted layout, and recent-URL history are
implemented.
Edge/Firefox integration is explicitly out of scope (C3);
direct media detection, in-page panel with HLS quality variants, popup resources,
HLS master/media assembly including AES-128 (single and rotating keys) and
alternate-audio muxing, DASH SegmentTemplate/Timeline/SegmentList assembly, DASH
A/V muxing via FFmpeg, HLS/DASH fragment checkpoint recovery, expiring segment-token refresh,
explicit live-stream
exclusion, and DRM detection are implemented. Account-provider/TorBox integration, unrar, and Docker/Linux web UI
are queued as a post-parity side quest (ROADMAP A1–A5/D1–D4). Authenticated HTTP/HTTPS proxy, SOCKS5, DPAPI site logins, redirect
credential boundaries, and expired-URL refresh are verified; SOCKS4/4a and
DPAPI protection for proxy passwords remain.

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
protocol-only; log to stderr/file.

Browser strategy: the extension observes eligible Chrome downloads and sends
URL, final URL, cookies, referrer, UA and relevant headers to our native host;
the host durably records and ACKs before Chrome’s download is cancelled. On any
failure Chrome’s download is resumed. POST bodies and unsupported request types
stay in Chrome. See `docs/browser-integration.md`.

## UI structure (rebuilt from the observed reference app window)

Dark WinUI shell with menu bar (`Tasks`, `File`, `Downloads`, `View`, `Help`),
large icon+label toolbar (Add URL, Resume, Stop, Stop All, Delete, Delete
Completed, Options; Scheduler/queues disabled until implemented), category
sidebar (All Downloads, Compressed, Documents, Music, Programs, Video,
Unfinished, Finished), and an eight-column compact list (File Name, Q, Size,
Status, Time left, Transfer rate, Last Try Date, Description). Closing hides the
window; a second launch activates the existing UI via a named event.

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

disassembler MCP is a headless server on `http://127.0.0.1:8081/mcp`, project
`reference_project`. Start it with
`C:\Users\kaank\tools\disassembler-mcp\start-reference app-headless.cmd`; only one session may
hold the project. Call `get_program_info` before trusting addresses. The loaded
program is 32-bit x86, image base `0x00400000`, SHA-256 pinned above.

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
- Never mark a parity row `implemented` without linked acceptance evidence.
  Use `partial`, `blocked`, or `unverified` otherwise.
- Do not add unlabelled stubs or settings that do nothing.
- Own branding/assets only; do not copy reference app icons, strings, or binaries.
- Keep `PROGRESS.md` current at each checkpoint; commit coherent increments.

## Known traps

- The OpenCode environment has **no Goal/Todo tools**; use the markdown files.
- **Chrome stable 156 removed `--load-extension`**. Manual “Load unpacked” works;
  for automation use Chrome for Testing 156+ (verified path:
  `C:\Users\kaank\AppData\Local\Temp\opencode\cft\chrome-win64\chrome.exe`).
  Chrome for Testing loads the extension with `--load-extension` and a fresh
  profile. Do not waste time automating Chrome stable's folder picker.
- Mandatory enterprise policy is not a reliable path here; use CfT/manual.
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
- disassembler project lock: one headless server only. MCP config points at the HTTP
  server, not stdio, to avoid duplicate process locks.
- Native-host registration is per user and must point at an absolute
  `DownloadManager.NativeHost.exe`; `scripts\register-native-host.ps1` does this.
- Never dump registry values containing cookies into committed files; redact.
- TestServer and UI executables lock their output during builds; stop them or use
  `scripts\publish.ps1`, which handles this.
