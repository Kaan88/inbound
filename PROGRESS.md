# Progress — session handoff

## Current state (2026-11-04)

**Goal:** Build a private reference app-compatible download manager. The full 1:1 parity
goal is **not complete**; Phase 0–5 are substantially implemented, later phases
remain open. Authoritative parity evidence is in `reference notes`.

- Repository: `C:\Users\kaank\Downloads\reference app`; remote
  `https://github.com/Kaan88/download-manager` (verified **private**).
- Commits: `0f3a294` baseline engine/host/IPC/CLI/native bridge,
  `a957f70` Chrome extension, `2143b6e` reference app-shaped WinUI frontend,
  `eb03ef4` segmented transfers, packaging scripts, browser-handoff evidence,
  and updated parity/onboarding.
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
| Queues | Named queues (`Main`, added `Night`), per-queue max concurrency, start/stop, queue assignment at add time, persistence across restart; CLI `dm queue` and WinUI Add URL/Start Queue/Stop Queue |
| Packaging | `scripts/build.ps1`, `publish.ps1`, `register-native-host.ps1`, `install.ps1`, `uninstall.ps1`, `package.ps1`; installed-build download and uninstall verified in this harness; clean external environment still unverified |

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

## Queue evidence (2026-11-04)

- `RuntimeServiceTests` now has three queue acceptance tests (see `reference notes`
  E9). The suite is **9/9 passed**.
  - `StoppingQueuePausesOnlyThatQueue`: stopping queue `Night` paused only its
    job; `Main` completed; starting `Night` resumed and the SHA-256 matched
    seed 42.
  - `PerQueueConcurrencyIsRespected`: two queues capped at one each ran exactly
    one job each while the global cap was three.
  - `QueueStateAndMembershipSurviveRestart`: queue name, max, stopped state, and
    the job's queue membership survived service recreation.
- CLI end-to-end against the published host (E10): `queue add Night 2`,
  `queue stop 1`, add with `--queue 1` stayed `queued` with no file,
  `queue start 1` completed 524,288 bytes; SHA-256
  `cd77d60c1cd3feb361d2013317b43a5e40cdac91bcf973cc9421d5a281dd121f`
  matched test seed 707.
- Reassignment (E13): `job.moveToQueue` moves a job between queues, persists the
  new membership, and pauses an active job when the target queue is stopped.
  It is exposed through CLI `queue move`, the WinUI row context menu, and the
  integration test `MovingJobToStoppedQueueUpdatesMembershipAndPauses`.
- WinUI controls (E11): the real window showed the `Queue` column with `Night`.
  UI Automation selected an active row and invoked **Stop Queue**; the host
  reported queue 1 stopped and the job paused at 983,040/8,388,608 bytes.
  Invoking **Start Queue** made queue 1 running and the job resumed at
  1,441,792 bytes. The job was then cancelled and its partial file removed.
- WinUI row menu (E14): the compact `Q` column shows the queue number. Selecting
  a completed row and pressing Shift+F10 opens the row menu with `Open`,
  `Open folder`, `Remove`, `Add to queue >`, `Delete from queue`, and
  `Properties`. The menu is constructed in code and was verified in the running
  window.
- Packaging fix (E15): the published WinUI app was missing
  `DownloadManager.Desktop.WinUI.pri`, causing `XamlParseException` at startup
  while the bin build worked. `publish.ps1` now copies the generated `.pri` and
  fails if it is absent; the republished app started and reached
  `MainWindow activated` with no crash log.

## Payload placement (2026-11-04)

reference app downloads to a fixed temporary directory and then stitches/moves the result,
so choosing another drive does not avoid C: space or a cross-volume copy. Our
engine instead writes all ranges at their final offsets into a single
random-access part file **in the destination directory**:

- `DownloadManager.Runtime` now sets `PartFilePath` to
  `<final path>.<job id>.part` in the chosen destination.
- The engine already used `RandomAccess.WriteAsync` at range offsets, so there
  is no separate segment stitching step; completion is `File.Move` within the
  same directory/volume, which is atomic and needs no extra free space.
- Metadata and checkpoints remain small and stay in the data directory.
- `PartialPayloadLivesOnDestinationVolume` verifies the live part location, the
  empty state partial directory, part cleanup, and the final hash.
- Published CLI check: the live `payload-loc.bin` part file was
  `test-downloads\payload-loc.bin.51948fdc....part` (1,310,720 bytes while
  downloading), `artifacts\app\data\partial` was empty, and after completion the
  part was gone, the 4 MiB file remained, and its SHA-256 matched seed 99
  (`4f1f120d5224489310bb0c39f4ff32ea3798e8ee1a2d99fe0b647696a20afec0`).

## Scheduler evidence (2026-11-04)

- Daily and one-time schedules target one queue, honor weekday selection, and
  start/stop that queue only. Same-day duplicate starts are suppressed; a
  same-day window that already closed is not started late; a scheduled stop
  pauses the queue's active jobs.
- Last start/stop dates persist in settings across service recreation, so a
  restart does not repeat the same day's start.
- The host runs an evaluation immediately at startup and every 30 seconds.
- Integration tests (E16): `DailyScheduleStartsAndStopsOnlyItsQueue`,
  `OneTimeScheduleOnlyRunsOnItsDate`, `ScheduleRunStateSurvivesRestart`.
  Clearing all schedules is also covered (an empty list is not treated as
  "keep existing").
- Published host: added a Wednesday 02:42–04:42 schedule for stopped queue
  `Night` at 02:42:20. At 02:42:42 the host logged
  `Scheduler start queue 1 for 2026-11-04`; `queue list` showed queue 1
  `running` and `schedule list` showed `last-start=2026-11-04`.
- CLI: `dm schedule list|add|remove|enable|disable`; removal accepts a
  hyphenated or compact GUID prefix.

## Tests

- Engine: **14/14 passed** (`tests\DownloadManager.Engine.Tests`), including
  segmented transfer, segmented resume, no-range fallback, changed content,
  truncation, and single-stream resume.
- Core: **11/11 passed**.
- Runtime/persistence: **14/14 passed** (`tests\DownloadManager.Integration.Tests`),
  including queues, reassignment, destination-local payload, and three scheduler
  tests.
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

1. Commit the scheduler increment (host timer, models, CLI, tests, evidence).
2. Complete Phase 8: completion actions (open/exit/shutdown), quotas, sleep
   prevention, and DST/wall-clock tests.
3. Implement Phase 6 management gaps exercised by the UI (duplicate decisions,
   categories/destination rules, batch URLs, batch download from clipboard).
4. Phase 7: authenticated browser request transfer, proxy/site login, Refresh
   Download Address preserving partial data.
5. Phase 9: media detection/HLS/DASH with FFmpeg behind an interface; label DRM
   unsupported.
6. Phase 10: Download All/site grabber.
7. Phase 11/12: FTP, clean-environment install/upgrade/uninstall verification,
   visual/behavioral audit, final parity evidence. Continue the reference app GUI
   inventory for U1/U2/U4 (see `notes/ui-inventory.md`).

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
