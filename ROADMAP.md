# Roadmap and executable work


Consolidated checkpoint (2026-11-05): full solution Release build0warnings/errors; Core88, engine57, integration91 and extension34 tests pass. Light main/graph/statistics rendered in isolated QA; completed-byte restart defect fixed and regression covered. Full UI/DPI/lifecycle/fault acceptance and unified Scheduler review remain open. Latest public release stays0.1.4; no0.1.5 package yet. See PROGRESS.md for logs and limitations.

Batched 0.1.5 scheduler/queue source (2026-11-05): startup triggers, timed/overnight windows, periodic HTTP file synchronization, queue retry limits and actual queue detachment added. Periodic sync compares validators and stages changed/unverifiable downloads while keeping the old final file until atomic replacement. Previous completed versions are retained in statistics without double-counting the current version. Retry processing is explicit for queue-managed downloads, runs at30-second intervals, respects stop/cancel and blocks premature completion actions. Startup-only failures retain pending eligibility. Explicit queue detachment keeps jobs/files and excludes them from queue pause/concurrency/quota/sync policy. Source only; consolidated validation pending. Network troubleshooting made no system changes; external test downloads remain deferred while the owner's throughput problem is unresolved.

Batched 0.1.5 source (2026-11-05): per-download completion options and real HTTP response-stream telemetry added; no build/test run. Properties stages program inheritance/override/disable, open-file and gated system actions; execution marker is persisted before side effects. HTTP telemetry measures actual response reads across direct/range/media/probe requests and exposes only host/port, protocol/status/range/bytes/rate. Statistics added to scope: archived minimal job records are written before removal and deduplicated against current jobs for persistent byte/count totals, with a View → Statistics window integrated in source. Consolidated validation remains pending.

Batched 0.1.5 source (2026-11-05): persistent independent download groups now have Host-owned definitions/membership, atomic settings commits and group controls. Tasks and selected-download context actions expose group creation/assignment; expandable management window is integrated in source, with aggregate progress/controls visible while collapsed and independent child controls. Bottom download-only speed graph added from owner reference 33528189-2dc8223e-d865-11e7-9d2a-2b5102afb5af.png: selectable 1/5/30/60-minute session history, scale/grid, current/average/peak, disconnected/stale gaps, persisted visibility/collapse. Source-only; consolidated build/tests and live DPI/theme/workflow acceptance pending. FTP remains out of scope.

Batched 0.1.5 source (2026-11-05): explicit unused-category deletion in Options and populated queue deletion with download/schedule reassignment added; source-only, consolidated verification pending. Queue moves save before old queue removal, and failed persistence restores in-memory membership for retry. Settings/category/job admission and queue membership edits share a mutation gate. No build/test run for this batch.

Owner scope update (2026-11-05): FTP work is out of scope; existing backend support is retained, but no additional FTP UI or acceptance work is required.

Owner workflow: batch remaining implementation, then consolidated verification; no new feature-by-feature test runs. Source0.1.5 now includes Add URL Host-owned metadata preview and suggested filenames. Final cancellation-review fixes and live acceptance await consolidated validation (PROGRESS.md).

0.1.5 source: persistent custom categories, Add URL selection/creation, extension rules, category folders/remember, sidebar and Options editing implemented. Focused7/7 and Core79/79 pass; full integration82/82 passes (artifacts/integration-categories.log); live GUI acceptance pending. Metadata preview/server-suggested names and category deletion remain open (PROGRESS.md).

Owner file-icon requirement (2026-11-05): replace generic file glyphs with Windows file-type and completed-file/application icons. Source implementation now covers main/queue rows and progress/completion headers; native checks2/2 and clean WinUI build pass. Live visual/DPI acceptance remains pending; see PROGRESS.md.

Post-0.1.4: manual HTTP Basic authentication and DPAPI-protected job headers implemented in source; integration 76/76 and redirect-boundary tests pass. Owner's testfile.org right-click 403 was investigated: automatic capture has a browser-resolved CDN URL, right-click does not. Frame/profile/partition cookie handling corrected; extension 34/34 plus typecheck/build pass. Direct handoff remains the normal path; browser-assisted preparation is an optional improvement, not a confirmed defect. Broader live acceptance remains open (PROGRESS.md).

Post-0.1.4 source increment: persisted queue file ordering and Queues → Files editor added. Focused actual-dispatch/restart/stale-list coverage passes, Core 79/79, clean Runtime/WinUI builds. Full integration 74/74 passes (artifacts/integration-queue-file-order.log); live editor acceptance remains open. This is separate from grouped independent child downloads, which remain planned.

0.1.4 published (2026-11-05): signed public feed/assets verified; inherited-working-directory and persistent-file-lock rollback fixtures pass. Installed repair/restart acceptance remains open. Post-release source now exposes Site Logins editing; clean UI build and secure-login integration workflow pass, live editor acceptance pending. See PROGRESS.md for evidence and artifact hashes.

Updater repair checkpoint (2026-11-05): owner upgrade exposed stale 0.1.2 binaries with a 0.1.3 version record after replacement/rollback failed. Source 0.1.4 repairs working-directory/native-bridge locks, metadata ordering/recovery, version reconciliation, progress events/window, and frontend reopening. Focused updater tests and isolated rollback fixtures pass; installed repair and live restart/progress acceptance remain pending. See PROGRESS.md.

> Fallback for the unavailable Todo feature. Status values: `pending`,
> `in_progress`, `done`, `blocked`. A task is `done` only when its acceptance
> condition is met and evidence is recorded (test output, commit, or document).
> The current phase is broken into concrete tasks; future phases stay coarse
> until their dependencies are known.

## Current status (2026-11-04)

### Planned: grouped multi-part downloads (owner, 2026-11-05)

Add first-class download groups that present multiple independent child files as
one expandable parent with aggregate progress/state and parent-level controls.
Keep these groups distinct from byte-range segments, which remain per-file
transport details. The model and IPC must support creating a group with child
jobs, persisting and restoring membership, and safely aggregating mixed child
states. This is a prerequisite for future TorBox integration, where one
transfer can contain multiple files. Validate restart recovery, partial child
failure, pause/resume/cancel behavior and UI expansion before marking complete.

2026-11-05 release increment: public builds-only repository created with source
privacy verified unchanged. Signed 0.1.2 release assets prepared, default trust
anchor/feed configured, installed-only automatic checker/installer wired.
Owner test installer records 0.1.1 to exercise upgrade to 0.1.2. Publication
verification, actual upgrade/restart/rollback acceptance remain in progress.

2026-11-05: Queue New/Edit UI is implemented for names, concurrency and quota
policy; clean WinUI build and focused restart/preservation test pass. GUI
acceptance and atomic concurrent policy updates remain open; next work is
scheduler editing and remaining queue administration.

Dial Up/VPN is explicitly out of scope by owner decision (2026-11-04).

### Owner review priorities (resumed by owner, 2026-11-04)

Branding follow-up (owner, 2026-11-05): develop a creative product name and a
better original icon. Present cohesive options after the current updater/browser
fixes; keep this separate from functional acceptance.

Current implementation increment: progress/completion controls and live HTTP
speed limits. Backend tests and representative live controls /
hash checks pass. The elevated installed-Host access blocker is cleared;
progress/completion height clipping is fixed. Owner approval, dark/DPI variants
and external shell handoff checks remain open.

| ID | Task | Status | Acceptance |
| --- | --- | --- | --- |
| UI.R1 | Complete functional/interaction/visual review and WinUI reorganization | in_progress; owner acceptance blocker | Cover functionality, workflow, settings/defaults, hierarchy, density and appearance. Implement and verify the recorded gaps with original assets, then obtain owner acceptance. Current UI is not accepted. |
| UI.R2 | Investigate Edge downloads flyout opening when a download starts | partial; owner confirmed the flyout stays closed for an accepted download (2026-11-06), the left-in-browser half is pending | Cause: interception ran in `downloads.onCreated`, after Chromium had already added the download to its UI. The extension now holds `downloads.onDeterminingFilename` instead (b56ae50, docs/browser-integration.md). Accept only after the owner confirms no flyout flash in Edge for accepted downloads and a normal flyout for downloads left to the browser. |
| EXT.S1 | Publish the extension as a hidden Edge Add-ons listing | parked (owner, 2026-11-06) | The owner cannot get into the Edge program (Partner Center sign-up stalled on the generic Home page), so Edge users install the Chrome Web Store listing instead (EDGE.1). A store zip was built earlier at `artifacts\edge-store\inbound-bridge-0.2.0-edge.zip` (0.2.0, `key` removed); it is stale. On resume: finish enrollment and submit a hidden listing. Only if Edge assigns a different ID does `BrowserBridge` and the native-host `allowed_origins` need that ID added; the Edge registry key is already written (EDGE.1). |
| EDGE.1 | Support Edge as a browser | implemented in source; not verified in a real install | Owner decision 2026-11-06, reversing C3. Edge installs the Chrome Web Store listing, so the same ID `olhbcdbjdnfoiinllnlpandggadfabfm` applies. Setup, `register-native-host.ps1` (so install, repair and the updater) write the native-host manifest path under `HKCU\Software\Microsoft\Edge\NativeMessagingHosts` as well as Chrome's; a denied Edge key does not fail them. Uninstall removes a key only when it points at this install or at a file that is gone, except that an install from 0.4.0 Setup updated through the app keeps the 0.4.0 uninstaller and leaves Edge's key dangling (first Edge version through Setup avoids this; see `PROGRESS.md`). The Host reports registered when either key works, names a browser that cannot reach the host, and at start repairs a missing or dangling key from the working one. Evidence: tests of the registration logic over a fake store, of the installer text, and of `register-native-host.ps1` and `apply-update.ps1` against a fake registry; the owner reports a real Edge handoff on 0.4.0 under the store ID (owner-reported); `host.ping` from Edge under the store ID with no Edge key present, which measures the Chrome key fallback. Not verified: Edge reading its own key when both exist, Setup writing the Edge key, Setup's uninstaller, upgrade from 0.4.0 by Setup and by the updater followed by uninstall, a real store install and its auto-update in Edge. Accept after the owner's real Edge check. |
| FF.1 | Support Firefox as a browser | pending; required, comes last after everything else | Owner decision 2026-11-06. No work started. |
| EXT.S2 | Publish the extension on the Chrome Web Store | submitted by the owner (2026-11-06); not yet public when last checked | Item ID `olhbcdbjdnfoiinllnlpandggadfabfm`, accepted by the app from 0.4.0. Zip: `scripts\package-extension-store.ps1`. All dashboard text and images: `docs/chrome-store/`. Privacy policy: `docs/chrome-extension-privacy.md`, online. Left: once approved, install from the store and check it connects to the app, which has not been tested under this ID. The extension has no media panel. |
| UI.R3 | Use the default folder for each new Chrome-extension download | pending; owner requirement | Each new extension download starts with the configured default download folder, rather than the destination selected for the previous download. A one-download folder choice must not change the next download's initial destination. Verify consecutive downloads with different destinations and after extension/browser restart. |
| UI.R4 | Fit Chrome-extension windows and their controls to the available screen | pending; owner requirement | Review the handoff and other extension windows at supported screen sizes/scaling. Size and lay out windows so all items and primary actions are accessible without clipping; use scrolling where available space cannot accommodate the full content. Include this in the owner visual review. |
| UI.R5 | Hold Delete to leave a download in the browser | pending; owner requirement | Support holding Delete while initiating a Chrome download to bypass manager capture for that download. Chrome must retain/download the file normally, with no manager job or duplicate transfer. Verify capture resumes for subsequent downloads after key release. Reuse the existing bypass flow where appropriate; exact key timing and browser limitations remain unverified. |

- User-test follow-up: filename fallback and resolved-URL replay fixed (E95);
  independent per-job progress/completion frontend added (E96, partial).
  Tray/settings activation and theme changes compile but still need live
  acceptance. Do not infer
  installer lifecycle acceptance from the rebuilt package.

- Phase 0: complete (build, test server, feature inventory, browser strategy).
- Phase 1: complete (engine tests + CLI hash evidence).
- Phase 2: in progress (resume, validators, changed content, and Host
  process-kill matrix verified in E94; independent review remains pending).
- Phase 3: complete (host/tray/IPC, the shell, hide-on-close, single-instance
  activation).
- Phase 4: in progress (automatic, context-menu, and authenticated handoff
  verified with hashes; failure-path recovery, disabled integration, and
  independent verification remain).
- Phase 5: in progress (segmented transfer/resume/fallback, dynamic work
  redistribution, per-host/global limits, stable estimates and the A9 benchmark
  verified; independent interruption verification remains).
- Phase 7: complete (SOCKS4/4a, proxy UI and DPAPI proxy password added; cookie/referrer/UA/custom headers, authenticated HTTP/HTTPS
  proxy, SOCKS5 auth, DPAPI site logins, redirect credential boundaries,
  expired-URL classification, and Refresh Download Address with partial resume
  verified; SOCKS4/4a and proxy credential hardening remain).
- Phase 8: complete (named queues, caps, start/stop, reassignment, schedules,
  quotas, completion actions, Download Later, sleep prevention, and DST tests
  verified).
- Phase 9: complete (HLS
  and DASH assembly including SegmentTemplate/Timeline/SegmentList/SegmentBase
  byte ranges, DASH and HLS alternate-audio muxing via FFmpeg, fragment
  recovery, expiring-token refresh, explicit live exclusion, DRM detection, HLS
  AES-128 key rotation, and quality selection verified).
- Phase 6: complete (clipboard URL monitoring, completion notification/sound,
  batch URLs, search/sort/context actions, editable descriptions, duplicate
  policies, category destinations, remembered last folder and recent URLs, JSON
  list export/import, completion dialog, state views, multiselect bulk actions,
  delete-file choice, shell open/reveal, and progress details verified).
- Phase 10: Download All link collection/filtering/handoff complete; site
  grabber G2–G4 removed by owner decision and out of scope.
- Phase 11: in progress (FTP auth/resume, hooks, CLI, profiles, settings
  transfer, repair, upgrade/uninstall, and the installer-only updater including
  notifications/gating verified; final UI audit remains. Edge is a required,
  supported browser (EDGE.1); Firefox is required and comes last (FF.1).
- Phases 6, 12: in progress/pending; see `PROGRESS.md` for the handoff.

## Phase 0 — Baseline and setup (done)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P0.2 | Verify dev environment: .NET, WinUI runtime, Node, GitHub | done | — | Tool versions/status recorded in `PROGRESS.md` |
| P0.3 | Create private GitHub repo `download-manager` and push baseline | in_progress | — | Repo visibility `PRIVATE`, baseline commit pushed |
| P0.6 | Establish solution structure, build scripts, diagnostics | done (`DownloadManager.slnx`, `scripts/build.ps1`, file logger) | P0.3 | `dotnet build` succeeds; `scripts/build.ps1` documented |
| P0.7 | Implement controlled local HTTP test server | done (`tools/TestServer` fault-injection endpoints; used by every suite) | P0.6 | Fault-injection endpoints + health check tested |
| P0.8 | Seed tracking files | in_progress | — | Every known feature has a row or explicit deferral |
| P0.9 | Decide browser interception/replay strategy | done (`docs/browser-integration-scope.md`) | — | `docs/browser-integration.md` states supported/rejected request cases |

**Exit gate:** buildable solution, initial feature inventory, concrete browser
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
| P1.7 | Phase 1 verification report | done | P1.6 | Hashes and failure cases recorded in PROGRESS (E1/E3/E60-E64) |

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
| P2.7 | Independent verification of interruption/recovery | partial (E94 main-session matrix; independent review pending) | P2.6 | Main-session Host process-kill matrix passes; independent review/reproduction still required |

**Exit gate:** forced termination/restart preserves safe recovery; incompatible
content is never silently combined.

## Phase 3 — Tray and minimal frontend (done)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P3.1 | Single-instance host + lifecycle | done (L1, E68) | P2.1 | Second launch focuses/notifies, never starts duplicate engine |
| P3.2 | Tray icon/menu/actions | done (L2, E72/E87) | P3.1 | Open/Pause All/Resume All/Status/Settings/Exit work |
| P3.3 | Named-pipe IPC with current-user ACL | done (L5, E73) | P3.1 | Other-user access denied; version mismatch rejected |
| P3.4 | Snapshot/event reconnect protocol | done (L6) | P3.3 | UI reconnect restores state; no lost terminal events |
| P3.5 | WinUI 3 download list + actions | done (E11/E14/E53) | P3.3 | Add/pause/resume/cancel/progress work |
| P3.6 | Hide-on-close, explicit Exit, sign-in startup | partial; sign-in launch remains unverified (L3, E75) | P3.1 | Engine survives window close; Exit stops it |
| P3.7 | Frontend indepedence verification | done (L6: UI restart restores state and live progress) | P3.5 | Killing UI does not kill downloads; reopen consistent |

**Exit gate:** downloads manageable with main window closed; UI death does not
kill engine.

## Phase 4 — Chrome integration (in progress)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P4.1 | Extension scaffold with own manifest key/ID | done | P0.6 | Loads unpacked; ID stable |
| P4.2 | Native host + registration scripts | done | P3.1 | Chrome launches bridge; bridge starts host |
| P4.3 | Handoff contract + durable dedupe | done | P3.3 | Same request ID twice → one job |
| P4.4 | Context-menu “Download with Download Manager” | done | P4.1/P4.2 | Selected link creates job while UI closed |
| P4.5 | Destination + Start/Download Later dialog | done | P4.4 | Choices persist and affect job state/destination (E77) |
| P4.6 | Authenticated handoff (cookies/referrer/UA) | done | P4.3 | Auth endpoint accepts all segment requests; cookie absent from logs |
| P4.7 | Failure-path recovery (host/native down) | done | P4.3 | Manual retry succeeds after restore; automatic downloads stay in Chrome with actionable errors and no silent loss (E80) |
| P4.6b | Automatic interception + filters/bypass | done | P4.4 | Allow-list intercepts; excluded extensions/hosts and popup bypass leave downloads in Chrome (E78) |
| P4.8 | Progress back to extension | done | P4.4 | Popup shows live state, bytes, speed, ETA and progress; Host ETA parsed/derived (E76) |
| P4.9 | Independent browser handoff verification | done (E80 host/native-host outage and recovery) | P4.7 | Restart Chrome/host; exactly-once handoff |

**Exit gate:** one supported Chrome request reliably creates one job while host
is in tray; failed handoffs never silently lose downloads.

## Phase 5 — Parallel transfers and acceleration (in progress)

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| P5.1 | Range probing and safe partitioning | done | P2.3 | Servers with/without ranges handled |
| P5.2 | Concurrent segment writer + checkpoints | done | P5.1 | Assembly hash matches; crash-safe |
| P5.3 | Per-host/global limits, fallback | done | P5.2 | Cross-job host cap and global job cap measured; single-stream fallback works (E82) |
| P5.4 | Speed limits + estimator | done | P5.2 | Per-download/global 128 KiB/s limits measured (E69); stable speed/ETA samples verified (A8/E83) |
| P5.5 | Benchmarks vs single stream | done | P5.4 | 64 MiB 1/4/8-connection run recorded with throughput/CPU/memory/write bytes (E84) |
| P5.6 | Segmented interruption verification | partial (E94 main-session matrix; independent review pending) | P5.2 | Main-session Host process-kill matrix passes at 20/50/80%; independent review/reproduction still required |
| P5.7 | **Destination-local payload (no fixed temp)** | done | P5.2 | The part file is created next to the final file on the chosen destination volume; segments are written at final offsets into that one file; completion is a same-volume atomic rename with no cross-volume copy. Verified by `PartialPayloadLivesOnDestinationVolume`. |
| P5.8 | Destination free-space preflight | done | P5.7 | Known-length jobs check destination free space with a 1 MiB reserve and fail `diskFull` before writing (E70). |
| P5.9 | Stale destination part cleanup | done | P5.7 | Unattached `.part` files are reported (E71) and can be quarantined to `.dm-orphans` or deleted via `dm host clean-orphans [--delete]` (E90). No WinUI button yet. |

**Payload placement decision (2026-11-04):** our engine writes every range directly
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

## Phase 7 - Authentication and refreshed addresses (done)

Cookies, referrer, user-agent, and custom headers are verified end to end for
CLI/engine and Chrome handoff (`S1`, `E17`). Authenticated manual HTTP proxies
and HTTPS CONNECT are verified, including wrong-credential failure, runtime
settings propagation, and same/cross-origin redirect credential boundaries
(`S3`, `S5`, `E18`). SOCKS5 username/password is verified for HTTP and HTTPS
(`S4`, `E19`). Site logins use a DPAPI-protected password store and matching
host/path Basic auth (`S2`, `E21`). Expired URLs are classified and Refresh
Download Address preserves partial data through a Range resume (`S6`, `S7`,
`E20`). SOCKS4/4a, a proxy settings UI in Options, and DPAPI protection for proxy
passwords are verified (`S3`, `S4`, `E89`). Phase 7 is complete.

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

## Phase 9 - Media downloading (done)

The extension's media panel was removed in 0.4.0 at the owner's decision. The app
still downloads stream addresses it is given. Unencrypted HLS
master/media playlists download segments into a playable TS output (V3 done;
E25). Static DASH `SegmentTemplate` manifests assemble init/media segments into
a playable fMP4 (V4 done; E26). Separate DASH video/audio tracks are muxed via an
FFmpeg interface configured by PATH/`ffmpegpath` (V5 done; E27). Per-track
media checkpoints resume HLS/DASH at completed fragment boundaries
(V6 done; E28). Expiring HLS/DASH segment tokens trigger a safe manifest refresh
and checkpoint resume (V7 done; E29). Live HLS/DASH is explicitly excluded with
a `liveStream` failure, and DRM-protected HLS/DASH is identified with a
`drmProtected` failure without any bypass (V8/V9 done; E30). HLS AES-128 with single and rotating keys is verified (V10 done; E55).
HLS alternate audio groups are selected and muxed with video (V11 done; E57).
DASH SegmentTimeline and full-URL SegmentList are verified (E58), and
SegmentBase/SIDX plus SegmentList `mediaRange` byte ranges are verified (E59).
Dynamic/live, multi-period, hierarchical SIDX, and DRM decryption remain
excluded; all advertised media rows are implemented.

## Phase 10 - Download All and site grabber (Download All done; site grabber removed 2026-11-04 by owner decision)

Download All link collection is implemented: the extension scans anchors,
images, and media sources, deduplicates and filters them, supports selection and
select-all-visible, and batch-adds selected URLs through authenticated handoffs
(G1 done; E31). The site grabber (G2–G4) was removed by owner decision and is
out of scope; its earlier evidence is historical, not current functionality.

## Phase 11 — Remaining compatibility and integration (pending)

Coarse: FTP/resume, command-line integration, other browsers, AV/post hooks,
connection profiles, accessibility/DPI/theming/localization, settings
import/export, installer repair/native-host repair, upgrade/uninstall, legacy
behavior. Anything out of scope is explicitly deferred.

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

## Phase 12 - Final audit (in progress)

U1/U2 are closed by the live UI Automation dump (E85); U4 is closed by the
keyboard/focus/modality check (E86), U5 by the tray check (E87), and U7 by the
accessibility/DPI/localization audit (E88). Clean-Windows acceptance remains.

Coarse: window/dialog layout, menus/context menus, columns/layout persistence,
keyboard/focus/modality, tray/notifications, settings defaults, progress/error
presentation. Scenario matrix and independent review.

Done in the live audit: list views (including Active/Queued/Failed and the queue
manager), numeric sort and multiselect bulk actions, delete-list-vs-file choice,
shell open/reveal, progress details with segment bars, persisted window/sort/
toolbar/category layout, and settings defaults. Remaining: exact menu ordering,
keyboard injection validation under the desktop foreground lock, tray click-
through (icon hidden from the overflow UIA tree), DPI/localization audit, and
the independent visual review.

## Side quest — Account-based file storage providers, unrar, Docker/Linux web UI (queued)

Starts with TorBox
(https://api-docs.torbox.app/) behind a provider abstraction so other
account-based storage services can be added without host changes. Test
credentials are supplied by the user and must never be committed, logged, or
placed in settings JSON; tests read them from `TORBOX_API_KEY` at run time. The
provided direct link may expire, so verification may fall back to listing or
searching the account.

| ID | Task | Status | Depends on | Acceptance |
| --- | --- | --- | --- | --- |
| A1 | Provider abstraction + account manager | deferred | Phase 7 | `IFileStorageProvider` (validate account, list/search files, resolve expiring link, refresh, quota) registered per account; tokens protected by `SecretProtector` (DPAPI on Windows, restricted file/OS keyring on Linux) and absent from logs/JSON |
| A2 | TorBox client | deferred | A1 | Token validation, account file/download listing, search, pagination, expiring direct-link resolution and API error mapping verified against the documented API with a supplied test account |
| A3 | Provider browse/add UI + CLI | deferred | A2 | Browse/search account files and queue selected items; expired direct links refresh once; CLI equivalent of the UI flow |
| A4 | Archive extraction (unrar) | deferred | A2 | Detect RAR/multi-volume archives; extract through an `IArchiveExtractor` using an external unrar/7z path (never bundled); traversal-safe paths, progress, cleanup policy, and tests with a real archive |
| A5 | Provider expansion | deferred | A1 | Adding a provider requires only a new client + capability declaration; no host/runtime changes |
| D1 | Cross-platform headless host | deferred | A2 | Host runs on Linux without WinForms/WinUI; named-pipe/Unix-socket IPC; XDG state paths; systemd unit; existing runtime tests pass on Linux. A user-provided LAN VM is available for verification; credentials are held outside the repository |
| D2 | Web UI | deferred | D1 | Local web app covering downloads, queues, schedules, settings, and provider accounts; authenticated remote access; no WinUI dependency |
| D3 | Docker packaging | deferred | D1/D2 | Dockerfile + compose with state/download volumes, non-root user, healthcheck, host and web UI deployed together |
| D4 | End-to-end provider/Docker test | deferred | A4/D3 | TorBox link/list → queue → download → unrar through the Linux container and web UI, including expiring-link refresh; image/logs contain no token |

## Cross-cutting tasks

| ID | Task | Status | Acceptance |
| --- | --- | --- | --- |
| X.1 | Keep `CLAUDE.md` current for agents | in_progress | Reflects structure, commands, status, traps |
| X.2 | Keep feature evidence current | in_progress | No status without evidence |
| X.3 | Keep PROGRESS.md handoff current | in_progress | Next action + commit + commands |
| X.4 | Commit coherent increments | in_progress | Each milestone increment pushed |
| X.5 | Independent verification of critical recovery/handoff | pending | E94 records main-session recovery results; independent review/reproduction of recovery and handoff remains outstanding |
