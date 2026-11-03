# Roadmap and executable work

> Fallback for the unavailable Todo feature. Status values: `pending`,
> `in_progress`, `done`, `blocked`. A task is `done` only when its acceptance
> condition is met and evidence is recorded (test output, commit, or document).
> The current phase is broken into concrete tasks; future phases stay coarse
> until their dependencies are known.

## Current status (2026-11-04)

- Phase 0: complete (build, test server, pinned reference, parity inventory,
  browser strategy). UI inventory is useful but not exhaustive.
- Phase 1: complete (engine tests + CLI hash evidence).
- Phase 2: in progress (resume, validators, changed content, service restart
  verified; OS process-kill matrix still pending).
- Phase 3: complete (host/tray/IPC, reference app-shaped UI, hide-on-close, single-instance
  activation).
- Phase 4: in progress (automatic and context-menu handoff verified with hashes;
  failure path, disabled integration, authenticated requests, and independent
  verification remain).
- Phase 5: in progress (segmented transfer/resume/fallback verified; dynamic
  redistribution, global/per-host limits, throughput benchmark, and independent
  verification remain).
- Phase 8: in progress (named queues, per-queue caps, start/stop, persistence,
  and CLI/UI assignment verified; schedules/quotas/completion actions remain).
- Phases 6, 7, 9–12: pending. See `reference notes` E1–E10 and `PROGRESS.md`.

## Phase 0 — Baseline and setup (done)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P0.1 | Pin reference app version, hashes, architecture, native-host registration | done | — | `notes/reference-version.md` has hashes and version evidence |
| P0.2 | Verify dev environment: .NET, WinUI runtime, Node, GitHub, disassembler | done | — | Tool versions/status recorded in `PROGRESS.md`; disassembler returns program info |
| P0.3 | Create private GitHub repo `download-manager` and push baseline | in_progress | — | Repo visibility `PRIVATE`, baseline commit pushed |
| P0.4 | Inventory reference app windows, menus, options, tray, extension behavior | pending | P0.1 | `notes/inventory.md` with observed evidence |
| P0.5 | Analyze reference extension + native host protocol | pending | P0.1 | `notes/browser-integration.md` documents observed frames, limits, and handoff strategy |
| P0.6 | Establish solution structure, build scripts, diagnostics | pending | P0.3 | `dotnet build` succeeds; `scripts/build.ps1` documented |
| P0.7 | Implement controlled local HTTP test server | pending | P0.6 | Fault-injection endpoints + health check tested |
| P0.8 | Seed reference notes and tracking files | in_progress | P0.1 | Every known feature has a row or explicit deferral |
| P0.9 | Decide browser interception/replay strategy | pending | P0.5 | `docs/browser-integration.md` states supported/rejected request cases |

**Exit gate:** buildable solution, initial parity inventory, concrete browser
handoff strategy.

## Phase 1 — Correct single-stream downloading (done)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P1.1 | Core job model and explicit terminal states | pending | P0.6 | State-machine unit tests distinguish pause/cancel/recoverable/permanent/success |
| P1.2 | HTTP GET streaming engine, redirects, errors, timeouts | pending | P1.1 | Local server downloads produce expected SHA-256 |
| P1.3 | Filename resolution, safe names, conflict policy | pending | P1.2 | Header/URL filename tests pass; no traversal |
| P1.4 | Unknown length, empty file, unexpected HTML handling | pending | P1.2 | Truncated/error bodies fail; legitimate HTML succeeds |
| P1.5 | Cancellation and temp-file cleanup | pending | P1.2 | Cancel removes/retains temp per policy; no success state |
| P1.6 | CLI add/list/status/cancel | pending | P1.2 | CLI end-to-end download with JSON output |
| P1.7 | Phase 1 verification report | pending | P1.6 | Hashes and failure cases recorded in PARITY/PROGRESS |

**Exit gate:** controlled downloads hash correctly; partial/truncated transfers
never appear successful.

## Phase 2 — Persistence, resume, recovery (in progress)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P2.1 | Durable job store with atomic writes and migration | pending | P1.1 | Kill between writes leaves loadable state |
| P2.2 | Checkpoint + `.part` file protocol | pending | P2.1 | Restart resumes at exact byte |
| P2.3 | Range validation (206, Content-Range, validators) | pending | P2.2 | Ignored/invalid ranges restart safely |
| P2.4 | Changed remote content detection | pending | P2.3 | ETag/Last-Modified/length mismatch restarts, never combines |
| P2.5 | Retry/backoff/offline/disk-full/permission recovery | pending | P2.2 | Fault tests produce recoverable vs permanent states |
| P2.6 | Crash reconciliation | pending | P2.1 | Orphan/missing part files are classified and reported |
| P2.7 | Independent verification of interruption/recovery | pending | P2.6 | Subagent runs kill/restart matrix; hashes match |

**Exit gate:** forced termination/restart preserves safe recovery; incompatible
content is never silently combined.

## Phase 3 — Tray and minimal frontend (done)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P3.1 | Single-instance host + lifecycle | pending | P2.1 | Second launch focuses/notifies, never starts duplicate engine |
| P3.2 | Tray icon/menu/actions | pending | P3.1 | Open/Pause All/Resume All/Status/Settings/Exit work |
| P3.3 | Named-pipe IPC with current-user ACL | pending | P3.1 | Other-user access denied; version mismatch rejected |
| P3.4 | Snapshot/event reconnect protocol | pending | P3.3 | UI reconnect restores state; no lost terminal events |
| P3.5 | WinUI 3 download list + actions | pending | P3.3 | Add/pause/resume/cancel/progress work |
| P3.6 | Hide-on-close, explicit Exit, sign-in startup | pending | P3.1 | Engine survives window close; Exit stops it |
| P3.7 | Frontend indepedence verification | pending | P3.5 | Killing UI does not kill downloads; reopen consistent |

**Exit gate:** downloads manageable with main window closed; UI death does not
kill engine.

## Phase 4 — Chrome integration (in progress)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P4.1 | Extension scaffold with own manifest key/ID | pending | P0.6 | Loads unpacked; ID stable; no reference app assets |
| P4.2 | Native host + registration scripts | pending | P3.1 | Chrome launches bridge; bridge starts host |
| P4.3 | Handoff contract + durable dedupe | pending | P3.3 | Same request ID twice → one job |
| P4.4 | Context-menu “Download with Download Manager” | pending | P4.1/P4.2 | Selected link creates job while UI closed |
| P4.5 | Destination + Start/Download Later dialog | pending | P4.4 | Choices persist and affect job |
| P4.6 | Automatic interception + filters/bypass | pending | P4.4 | Allowed types intercepted; exclusions pass through |
| P4.7 | Progress back to extension + failure recovery | pending | P4.4 | Extension shows state; unavailable host gives actionable error |
| P4.8 | Independent browser handoff verification | pending | P4.7 | Restart Chrome/host; exactly-once handoff |

**Exit gate:** one supported Chrome request reliably creates one job while host
is in tray; failed handoffs never silently lose downloads.

## Phase 5 — Parallel transfers and acceleration (in progress)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P5.1 | Range probing and safe partitioning | done | P2.3 | Servers with/without ranges handled |
| P5.2 | Concurrent segment writer + checkpoints | done | P5.1 | Assembly hash matches; crash-safe |
| P5.3 | Per-host/global limits, fallback | partial | P5.2 | Limits observed; single-stream fallback works |
| P5.4 | Speed limits + estimator | partial | P5.2 | Throttle accuracy within tolerance |
| P5.5 | Benchmarks vs single stream/reference app | pending | P5.4 | Recorded throughput/CPU/memory/disk |
| P5.6 | Segmented interruption verification | pending | P5.2 | Subagent kill/restart matrix passes |
| P5.7 | **Destination-local payload (no fixed temp)** | done | P5.2 | The part file is created next to the final file on the chosen destination volume; segments are written at final offsets into that one file; completion is a same-volume atomic rename with no cross-volume copy. Verified by `PartialPayloadLivesOnDestinationVolume`. |
| P5.8 | Legacy temp-part migration | in progress | P5.7 | On startup, a job whose part file still lives in `%LOCALAPPDATA%\DownloadManager\data\partial` is moved to its destination when possible; resume bytes are preserved. |
| P5.9 | Destination free-space preflight | pending | P5.7 | Before starting, check free space on the destination volume; fail early with the destination path, not the state drive. |
| P5.10 | Stale destination part cleanup | pending | P5.7 | Orphaned `<final>.<jobId>.part` files are identified safely and removed or surfaced without touching active jobs. |

**Payload placement decision (2026-11-04):** unlike reference app's fixed temporary
directory plus post-download stitching, our engine writes every range directly
into a single random-access part file in the destination directory. The part
file consumes space on the drive the user chose; when all ranges complete the
file is renamed in place. This removes the cross-volume copy that makes large
downloads appear to "stitch and move" on C: even when D: was selected. Small
metadata and checkpoints remain in the app data directory.

**Exit gate:** accelerated transfers correct through pause/crash/retry with
measurements and destination-local payload behavior.

## Phase 6 — Everyday download management (pending)

Coarse until Phase 3/4 contracts settle: properties/descriptions, views and
filters, multiselect/bulk actions, open file/folder, history vs file deletion,
duplicates, categories/destination rules, clipboard monitoring, Download Later,
notifications, batch URLs/numbered patterns, import/export, per-download
progress/segment view.

## Phase 7 — Authentication and refreshed addresses (pending)

Coarse: cookies/referrer/UA/custom headers, site logins, HTTP/SOCKS proxies,
redirect credential rules, expired URL errors, Refresh Download Address with
partial-data preservation and content validation.

## Phase 8 - Queues and scheduling (in progress)

Named queues, per-queue concurrency, start/stop, queue persistence, and CLI/UI
assignment are implemented and tested (E9/E10). Remaining: moving jobs between
queues, schedule execution, quotas, completion actions, sleep prevention,
DST/restart behavior.

## Phase 9 — Media detection and downloading (pending)

Coarse: direct media detection, in-page panel, multiple qualities, HLS, DASH,
audio/video muxing, fragment recovery, expiring URLs, live behavior, DRM
detection. FFmpeg behind an interface if adopted.

## Phase 10 — Download All and site grabber (pending)

Coarse: page link collection/filtering/selection, crawl scope/depth, filters,
asset discovery, relative paths, link rewriting, preview, persistent projects,
scheduled runs.

## Phase 11 — Remaining compatibility and integration (pending)

Coarse: FTP/resume, command-line integration, other browsers, AV/post hooks,
connection profiles, accessibility/DPI/theming/localization, settings
import/export, installer repair/native-host repair, upgrade/uninstall, legacy
behavior. Anything out of scope is explicitly deferred in reference notes.

## Phase 12 — Final parity audit (pending)

Coarse: window/dialog layout, menus/context menus, columns/layout persistence,
keyboard/focus/modality, tray/notifications, settings defaults, progress/error
presentation. Side-by-side scenario matrix and independent review.

## Cross-cutting tasks

| ID | Task | Status | Acceptance |
| --- | --- | --- | --- |
| X.1 | Keep `CLAUDE.md` current for agents | in_progress | Reflects structure, commands, status, traps |
| X.2 | Keep reference notes evidence current | in_progress | No status without evidence |
| X.3 | Keep PROGRESS.md handoff current | in_progress | Next action + commit + commands |
| X.4 | Commit coherent increments | in_progress | Each milestone increment pushed |
| X.5 | Independent verification of critical recovery/handoff | pending | Subagent report reviewed and reproduced |
