# Progress — session handoff

## Current state (2026-11-04)

**Goal:** Build a private reference app-compatible download manager. The full 1:1 parity
goal is **not complete**; Phase 0–5 are substantially implemented, later phases
remain open. Authoritative parity evidence is in `reference notes`.

- Repository: `C:\Users\kaank\Downloads\reference app`; remote
  `https://github.com/Kaan88/download-manager` (verified **private**).
- Commits: `0f3a294` baseline engine/host/IPC/CLI/native bridge,
  `a957f70` Chrome extension, `2143b6e` reference app-shaped WinUI frontend.
  The segmented-transfer, packaging, and evidence increment is pending at the
  time of this note.
- reference app reference pinned to **6.43.12.3** (`v6.43b12 Full`); see
  `notes/reference-version.md`.
- Tracking fallback: Goal/Todo tools are unavailable in this OpenCode 2.2.5
  environment. `GOAL.md`, `ROADMAP.md`, `reference notes`, and this file are the
  authoritative records. Do not claim a Goal/Todo update.

## What works now

| Area | State |
| --- | --- |
| Single-stream HTTP/HTTPS | Working; hashes verified; redirects, unknown length, safe names, truncation, cancellation |
| Durable jobs/settings | Atomic JSON stores; restart reconciliation; request-claim dedupe |
| Resume | Byte checkpoints, Range/Content-Range validation, changed-content restart, ignored-range restart |
| Parallel transfers | Fresh downloads split into 2–N ranges (≥1 MiB), RandomAccess assembly, per-segment checkpoints/retries, no-range fallback, segmented resume verified |
| Host lifecycle | One per-user Host, tray, hide-on-close, named-pipe IPC, snapshot/events, CLI control |
| WinUI frontend | reference app-shaped dark shell: Tasks/File/Downloads/View/Help menu, large toolbar, category sidebar, eight-column list; Add URL and Options dialogs; filtering/sorting; single-instance activation |
| Chrome extension | Own MV3 identity `pknddmneejadnnngffojnoecojhdfalc`; automatic interception and context-menu handoff; destination/start dialog; popup/options |
| Native bridge | Registered per user as `com.kaank.downloadmanager`; durable ACK and dedupe; Chrome for Testing handoff verified |
| Packaging | `scripts/build.ps1`, `publish.ps1`, `register-native-host.ps1`, `install.ps1`, `uninstall.ps1`, `package.ps1`; clean-environment install still unverified |

## Browser evidence (2026-11-04)

Chrome stable 156 removed `--load-extension`; automated verification used
**Chrome for Testing 156.0.8078.80** with a fresh profile and the unpacked
extension from `extensions\chrome\dist`. The extension ID and service-worker
target were:

```
chrome-extension://pknddmneejadnnngffojnoecojhdfalc/background.js
```

Automatic interception:

- Triggered `http://127.0.0.1:6411/file/browser-auto2.bin?...size=2097152&seed=202`.
- Host job `430cdc3a...` source `chromeAuto`, extension ID
  `pknddmneejadnnngffojnoecojhdfalc`, state `completed`, 2,097,152 bytes.
- Chrome `downloads.search({})` returned an empty list after takeover.
- File SHA-256 matched seed 202:
  `b960b0a9b5085893db0d4a0b8935b48107c90299700d96ff5cafd86d8b4bd8f6`.

Context-menu handoff:

- Context menu contained **Download with Download Manager**; invoking it opened
  the extension handoff dialog.
- Confirming it produced host job `8becefbb...`, source `chromeContextMenu`,
  state `completed`, 1,048,576 bytes, file `browser-context.bin`.
- File SHA-256 matched seed 303:
  `58ad8b2859b3f4b4eb686d0df4c5ac164daf3c36bff7c12f5a653eac8231d53c`.

Native-host registration evidence:

- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager`
  points at `%LOCALAPPDATA%\DownloadManager\native-host\com.kaank.downloadmanager.json`.
- Manifest path:
  `C:\Users\kaank\Downloads\reference app\artifacts\app\DownloadManager.NativeHost.exe`
  (development publish), allowed origin only the pinned extension.

## Tests

- Engine: **14/14 passed** (`tests\DownloadManager.Engine.Tests`), including
  segmented transfer, segmented resume, no-range fallback, changed content,
  truncation, and single-stream resume.
- Runtime/persistence: **6/6 passed** (`tests\DownloadManager.Integration.Tests`).
- Extension: `npx tsc --noEmit` passes; `npm run build` succeeds.
- Solution: `dotnet build DownloadManager.slnx -c Release` succeeds, including
  the WinUI x64 build.

## Environment limitations observed

- This harness denies child-process writes to `C:\Users\kaank\Downloads` and
  default `%LOCALAPPDATA%\DownloadManager`, so `StoragePaths.CreateDefault()`
  falls back to `artifacts\app\data`. Browser tests used the workspace
  `test-downloads` folder. On a normal user account, LocalAppData is the intended
  data root.
- The same restriction denied the Host's HKCU Run-key update; `L4` is marked
  **blocked** in `reference notes` until tested from a normal shell/installed build.
- Native host registration works from PowerShell; the development registration
  currently points at `artifacts\app`, not an installed copy.
- Chrome stable requires manual **Load unpacked**. The extension is at
  `C:\Users\kaank\Downloads\reference app\extensions\chrome\dist`.

## Next actions (dependency order)

1. Commit the segmented-transfer, packaging-script, and evidence increment; push.
2. Run browser handoff once more from a normal Chrome installation if available
   (extension already loadable manually; use the dist directory).
3. Implement Phase 6 management gaps exercised by the UI (open-folder action,
   duplicate decisions, categories/destination rules, batch URLs).
4. Phase 7: authenticated browser request transfer, proxy/site login, Refresh
   Download Address preserving partial data.
5. Phase 8: queues and scheduler; complete completion actions/sleep prevention.
6. Phase 9: media detection/HLS/DASH with FFmpeg behind an interface; label DRM
   unsupported.
7. Phase 10: Download All/site grabber.
8. Phase 11/12: FTP, clean-environment install/upgrade/uninstall verification,
   visual/behavioral audit, final parity evidence.

## Commands

```powershell
# Build everything (solution includes WinUI x64)
dotnet build DownloadManager.slnx -c Release

# Tests
powershell -ExecutionPolicy Bypass -File scripts\test.ps1

# Extension
cd extensions\chrome
npm ci
npm run build
# chrome://extensions -> Developer mode -> Load unpacked -> dist

# Publish/package/install (development)
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
powershell -ExecutionPolicy Bypass -File scripts\package.ps1

# Local fault-injection server
tools\TestServer\bin\Debug\net10.0\TestServer.exe --port 6411
```

## Session 1 archive (2026-11-04)

Baseline: pinned reference app 6.43.12.3; verified .NET 10.0.403, Node 26.11.1, Chrome
156, Windows App Runtime 1.8/2.x, GitHub authentication, and disassembler 12.1.4
headless MCP (`http://127.0.0.1:8081/mcp`, project `reference_project`; loaded
`/reference app.exe`, SHA-256 matches the pinned executable, 18,988 functions).
Created the private repo, initial parity inventory, initial architecture, and
the first engine/host/IPC implementation.
