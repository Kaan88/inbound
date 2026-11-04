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
- Phase 4: in progress (automatic, context-menu, and authenticated handoff
  verified with hashes; failure-path recovery, disabled integration, and
  independent verification remain).
- Phase 5: in progress (segmented transfer/resume/fallback verified; dynamic
  redistribution, global/per-host limits, throughput benchmark, and independent
  verification remain).
- Phase 7: in progress (cookie/referrer/UA/custom headers, authenticated HTTP/HTTPS
  proxy, SOCKS5 auth, DPAPI site logins, redirect credential boundaries,
  expired-URL classification, and Refresh Download Address with partial resume
  verified; SOCKS4/4a and proxy credential hardening remain).
- Phase 8: complete (named queues, caps, start/stop, reassignment, schedules,
  quotas, completion actions, Download Later, sleep prevention, and DST tests
  verified).
- Phase 9: complete (direct media detection, popup/in-page resource list, HLS
  and DASH assembly including SegmentTemplate/Timeline/SegmentList/SegmentBase
  byte ranges, DASH and HLS alternate-audio muxing via FFmpeg, fragment
  recovery, expiring-token refresh, explicit live exclusion, DRM detection, HLS
  AES-128 key rotation, and quality selection verified).
- Phase 6: complete (clipboard URL monitoring, completion notification/sound,
  batch URLs, search/sort/context actions, editable descriptions, duplicate
  policies, category destinations, remembered last folder and recent URLs, JSON
  list export/import, completion dialog, state views, multiselect bulk actions,
  delete-file choice, shell open/reveal, and progress details verified).
- Phase 10: complete (Download All link collection/filtering/handoff, scoped
  crawl, offline link rewriting, and persistent/scheduled grabber projects
  verified).
- Phase 11: in progress (FTP auth/resume, hooks, CLI, profiles, settings
  transfer, repair, upgrade/uninstall, and the installer-only updater including
  notifications/gating verified; final UI parity remains. Other browsers are
  explicitly out of scope (C3).
- Phases 6, 12: in progress/pending as tracked in `reference notes`; see
  `PROGRESS.md` for the handoff.

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
| P1.1 | Core job model and explicit terminal states | done | P0.6 | Core/job tests and integration pause/cancel/failure tests cover terminal states (E1/E61/E63) |
| P1.2 | HTTP GET streaming engine, redirects, errors, timeouts | done | P1.1 | Local hashes match; 404/500/DNS/TLS/timeout classes verified (E1/E3/E61) |
| P1.3 | Filename resolution, safe names, conflict policy | done | P1.2 | Core filename traversal/reserved-name tests and live conflict policies pass (E36) |
| P1.4 | Unknown length, empty file, unexpected HTML handling | done | P1.2 | Truncated/error bodies fail; legitimate HTML succeeds and error pages are rejected (E64) |
| P1.5 | Cancellation and temp-file cleanup | done | P1.2 | Pause keeps the part, cancel deletes it, no success state (E63) |
| P1.6 | CLI add/list/status/cancel | done | P1.2 | CLI end-to-end and JSON status verified (E6/E44) |
| P1.7 | Phase 1 verification report | done | P1.6 | Hashes and failure cases recorded in PARITY/PROGRESS (E1/E3/E60-E64) |

**Exit gate:** controlled downloads hash correctly; partial/truncated transfers
never appear successful.

## Phase 2 — Persistence, resume, recovery (in progress)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P2.1 | Durable job store with atomic writes and migration | done | P1.1 | Restart tests load jobs, settings, queues and metadata (E12/E23/E35) |
| P2.2 | Checkpoint + `.part` file protocol | done | P2.1 | Destination-local part and byte-exact resume verified (E12/E28) |
| P2.3 | Range validation (206, Content-Range, validators) | done | P2.2 | Ignored/invalid ranges restart safely; bad ranges fail without corruption (E67) |
| P2.4 | Changed remote content detection | done | P2.3 | Length/validator mismatch restarts instead of combining (E28/E67) |
| P2.5 | Retry/backoff/offline/disk-full/permission recovery | done | P2.2 | Transient 500 retry, timeout/DNS/TLS, disk-full preflight and permission failure verified (E61/E65/E70) |
| P2.6 | Crash reconciliation | done | P2.1 | Short/missing part clamping, long-part truncation and orphan reporting verified (E66/E71) |
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
| P4.1 | Extension scaffold with own manifest key/ID | done | P0.6 | Loads unpacked; ID stable; no reference app assets |
| P4.2 | Native host + registration scripts | done | P3.1 | Chrome launches bridge; bridge starts host |
| P4.3 | Handoff contract + durable dedupe | done | P3.3 | Same request ID twice → one job |
| P4.4 | Context-menu “Download with Download Manager” | done | P4.1/P4.2 | Selected link creates job while UI closed |
| P4.5 | Destination + Start/Download Later dialog | done | P4.4 | Choices persist and affect job state/destination (E77) |
| P4.6 | Authenticated handoff (cookies/referrer/UA) | done | P4.3 | Auth endpoint accepts all segment requests; cookie absent from logs |
| P4.7 | Failure-path recovery (host/native down) | pending | P4.3 | Chrome resumes; user sees actionable error; no silent loss |
| P4.6b | Automatic interception + filters/bypass | pending | P4.4 | Allowed types intercepted; exclusions pass through |
| P4.8 | Progress back to extension | done | P4.4 | Popup shows live state, bytes, speed, ETA and progress; Host ETA parsed/derived (E76) |
| P4.9 | Independent browser handoff verification | pending | P4.7 | Restart Chrome/host; exactly-once handoff |

**Exit gate:** one supported Chrome request reliably creates one job while host
is in tray; failed handoffs never silently lose downloads.

## Phase 5 — Parallel transfers and acceleration (in progress)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P5.1 | Range probing and safe partitioning | done | P2.3 | Servers with/without ranges handled |
| P5.2 | Concurrent segment writer + checkpoints | done | P5.1 | Assembly hash matches; crash-safe |
| P5.3 | Per-host/global limits, fallback | partial | P5.2 | Limits observed; single-stream fallback works |
| P5.4 | Speed limits + estimator | done | P5.2 | Per-download and global 128 KiB/s limits measured within tolerance (E69); estimator quality remains under A8 |
| P5.5 | Benchmarks vs single stream/reference app | pending | P5.4 | Recorded throughput/CPU/memory/disk |
| P5.6 | Segmented interruption verification | pending | P5.2 | Subagent kill/restart matrix passes |
| P5.7 | **Destination-local payload (no fixed temp)** | done | P5.2 | The part file is created next to the final file on the chosen destination volume; segments are written at final offsets into that one file; completion is a same-volume atomic rename with no cross-volume copy. Verified by `PartialPayloadLivesOnDestinationVolume`. |
| P5.8 | Destination free-space preflight | done | P5.7 | Known-length jobs check destination free space with a 1 MiB reserve and fail `diskFull` before writing (E70). |
| P5.9 | Stale destination part cleanup | partial | P5.7 | Unattached `.part` files in known dest/category folders are reported in host status/logs; automatic removal/quarantine remains (E71). |

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

## Phase 7 - Authentication and refreshed addresses (in progress)

Cookies, referrer, user-agent, and custom headers are verified end to end for
CLI/engine and Chrome handoff (`S1`, `E17`). Authenticated manual HTTP proxies
and HTTPS CONNECT are verified, including wrong-credential failure, runtime
settings propagation, and same/cross-origin redirect credential boundaries
(`S3`, `S5`, `E18`). SOCKS5 username/password is verified for HTTP and HTTPS
(`S4`, `E19`). Site logins use a DPAPI-protected password store and matching
host/path Basic auth (`S2`, `E21`). Expired URLs are classified and Refresh
Download Address preserves partial data through a Range resume (`S6`, `S7`,
`E20`). Remaining: SOCKS4/4a, proxy settings UI, and SECURITY: DPAPI protection
for proxy passwords.

## Phase 8 - Queues and scheduling (done)

Named queues, per-queue concurrency, start/stop, queue persistence, CLI/UI
assignment/reassignment, and daily/one-time schedule execution are implemented
and tested (E9/E10/E13/E16). The host evaluates schedules every 30 seconds and
on startup; schedules target one queue, honor weekday selection, and persist
last-run dates across restart. Per-queue byte quotas and completion actions
(open/exit/shutdown with opt-in and active-download safeguards) are verified
(E22). Download Later gating, sleep prevention, repeated DST hours, and
overnight DST stops are verified (E23). CLI management exists as
`dm schedule list|add|remove|enable|disable` and
`dm queue quota <id> <MB> <hours>|off`.

## Phase 9 - Media detection and downloading (done)

Direct media detection is implemented: a content script scans video/audio/source
elements and HLS/DASH links, the extension popup lists distinct resources, and
selection uses the handoff flow (V1 done, V2 partial; E24). Unencrypted HLS
master/media playlists download segments into a playable TS output (V3 done;
E25). Static DASH `SegmentTemplate` manifests assemble init/media segments into
a playable fMP4 (V4 done; E26). Separate DASH video/audio tracks are muxed via an
FFmpeg interface configured by PATH/`ffmpegpath` (V5 done; E27). Per-track
media checkpoints resume HLS/DASH at completed fragment boundaries
(V6 done; E28). Expiring HLS/DASH segment tokens trigger a safe manifest refresh
and checkpoint resume (V7 done; E29). Live HLS/DASH is explicitly excluded with
a `liveStream` failure, and DRM-protected HLS/DASH is identified with a
`drmProtected` failure without any bypass (V8/V9 done; E30). An in-page
shadow-DOM media panel and HLS quality-variant selection are verified (V2 done;
E32). HLS AES-128 with single and rotating keys is verified (V10 done; E55).
HLS alternate audio groups are selected and muxed with video (V11 done; E57).
DASH SegmentTimeline and full-URL SegmentList are verified (E58), and
SegmentBase/SIDX plus SegmentList `mediaRange` byte ranges are verified (E59).
Dynamic/live, multi-period, hierarchical SIDX, and DRM decryption remain
excluded; all advertised media rows are implemented.

## Phase 10 - Download All and site grabber (done)

Download All link collection is implemented: the extension scans anchors,
images, and media sources, deduplicates and filters them, supports selection and
select-all-visible, and batch-adds selected URLs through authenticated handoffs
(G1 done; E31). A scoped site grabber crawls by depth/page/host/extension/URL
filters, saves pages and files under their URL paths, and rewrites mirrored links
to relative local paths (G2/G3 done; E40). Projects persist config/status/progress
and can be run, resumed, removed, or scheduled; resume skips existing files
(G4 done; E41). Remaining polish: grabber preview and a WinUI schedule editor.

## Phase 11 — Remaining compatibility and integration (pending)

Coarse: FTP/resume, command-line integration, other browsers, AV/post hooks,
connection profiles, accessibility/DPI/theming/localization, settings
import/export, installer repair/native-host repair, upgrade/uninstall, legacy
behavior. Anything out of scope is explicitly deferred in reference notes.

### Installer-only update check and auto-update (new, pending)

**Scope:** only builds installed by `install.ps1`/the installer. Portable,
development, and unpacked builds must never auto-update. Installed builds are
identified by the install marker (`INSTALL.txt` plus the per-user uninstall
registry entry), not by the executable name.

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P11.1 | Installed-build detection | done | C8 | Portable/dev builds report "not installed" and expose no updater; installed builds read version/channel from install metadata (`install.json`, E49) |
| P11.2 | Signed update manifest | done | P11.1 | HTTPS manifest includes version, channel, installer URL, size, SHA-256 and RSA signature; manifest is verified before use (E49) |
| P11.3 | Update check policy | done | P11.2 | Manual + configurable periodic/automatic check, stable channel default, signature/channel/version filtering, HTTPS/loopback redirect enforcement, start-on-enable check (E51) |
| P11.4 | Download and verify installer | done | P11.2 | Installer downloaded to the state updates folder; signed size and SHA-256 enforced; mismatch deletes the file (E49) |
| P11.5 | Installer-only silent upgrade | done | P11.4/C8 | Detached helper waits for Host exit, backs up app/metadata, installs the verified package, verifies version B, and restarts the Host; data tree untouched (E50) |
| P11.6 | Failure and rollback | done | P11.5 | Failed installer/version check restores app+install.json, writes a failed result, and restarts the old Host (E50 fixture evidence) |
| P11.7 | Update UI/tray flow | done | P11.3/P11.5 | Check shows installed/latest, update-available balloon and tray install item, Install now/Later (Later persists `skippedUpdateVersion`), and install is blocked while downloads are active unless forced (E51) |

Completion requires an end-to-end test on a clean installer build: install
version A, publish version B, check, download, verify, upgrade, and confirm jobs
and settings survive. Portable/dev builds are tested to confirm they are
excluded.

## Phase 12 - Final parity audit (in progress)

Coarse: window/dialog layout, menus/context menus, columns/layout persistence,
keyboard/focus/modality, tray/notifications, settings defaults, progress/error
presentation. Side-by-side scenario matrix and independent review.

Done in the live audit: list views (including Active/Queued/Failed and the queue
manager), numeric sort and multiselect bulk actions, delete-list-vs-file choice,
shell open/reveal, progress details with segment bars, persisted window/sort/
toolbar/category layout, and settings defaults. Remaining: exact menu ordering,
keyboard injection validation under the desktop foreground lock, tray click-
through (icon hidden from the overflow UIA tree), DPI/localization audit, and
the independent visual review.

## Side quest — Account-based file storage providers, unrar, Docker/Linux web UI (queued)

Requested after the reference app parity work. Starts with TorBox
(https://api-docs.torbox.app/) behind a provider abstraction so other
account-based storage services can be added without host changes. Test
credentials are supplied by the user and must never be committed, logged, or
placed in settings JSON; tests read them from `TORBOX_API_KEY` at run time. The
provided direct link may expire, so verification may fall back to listing or
searching the account.

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| A1 | Provider abstraction + account manager | pending | Phase 7 | `IFileStorageProvider` (validate account, list/search files, resolve expiring link, refresh, quota) registered per account; tokens protected by `SecretProtector` (DPAPI on Windows, restricted file/OS keyring on Linux) and absent from logs/JSON |
| A2 | TorBox client | pending | A1 | Token validation, account file/download listing, search, pagination, expiring direct-link resolution and API error mapping verified against the documented API with a supplied test account |
| A3 | Provider browse/add UI + CLI | pending | A2 | Browse/search account files and queue selected items; expired direct links refresh once; CLI equivalent of the UI flow |
| A4 | Archive extraction (unrar) | pending | A2 | Detect RAR/multi-volume archives; extract through an `IArchiveExtractor` using an external unrar/7z path (never bundled); traversal-safe paths, progress, cleanup policy, and tests with a real archive |
| A5 | Provider expansion | pending | A1 | Adding a provider requires only a new client + capability declaration; no host/runtime changes |
| D1 | Cross-platform headless host | pending | A2 | Host runs on Linux without WinForms/WinUI; named-pipe/Unix-socket IPC; XDG state paths; systemd unit; existing runtime tests pass on Linux. A user-provided LAN VM is available for verification; credentials are held outside the repository |
| D2 | Web UI | pending | D1 | Local web app covering downloads, queues, schedules, settings, and provider accounts; authenticated remote access; no WinUI dependency |
| D3 | Docker packaging | pending | D1/D2 | Dockerfile + compose with state/download volumes, non-root user, healthcheck, host and web UI deployed together |
| D4 | End-to-end provider/Docker test | pending | A4/D3 | TorBox link/list → queue → download → unrar through the Linux container and web UI, including expiring-link refresh; image/logs contain no token |

## Cross-cutting tasks

| ID | Task | Status | Acceptance |
| --- | --- | --- | --- |
| X.1 | Keep `CLAUDE.md` current for agents | in_progress | Reflects structure, commands, status, traps |
| X.2 | Keep reference notes evidence current | in_progress | No status without evidence |
| X.3 | Keep PROGRESS.md handoff current | in_progress | Next action + commit + commands |
| X.4 | Commit coherent increments | in_progress | Each milestone increment pushed |
| X.5 | Independent verification of critical recovery/handoff | pending | Subagent report reviewed and reproduced |
