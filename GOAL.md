# Goal — Build a private Windows download manager

Owner direction (2026-11-05): the owner prefers the current Inbound design; keep our own design and behavior. `implemented` still needs linked acceptance evidence.

Release status (2026-11-08): the last release is v0.6.0 (extension 0.5.0), published 2026-11-08. It adds the headless server, web UI and Docker image for linux/amd64 (ROADMAP D1–D4; D1 is still in progress). in Edge the owner checked automatic catching; Firefox is not checked with 0.6.0 (EXT.B3). Clean install, upgrade and uninstall passed on a fresh Windows VM (TEST.2). The older dated entries below are history. ROADMAP and PROGRESS hold the current status of each item.



Consolidated checkpoint (2026-11-05): full solution Release build0warnings/errors; Core88, engine57, integration91 and extension34 tests pass. Light main/graph/statistics rendered in isolated QA; completed-byte restart defect fixed and regression covered. Full UI/DPI/lifecycle/fault acceptance and unified Scheduler review remain open. Latest public release stays0.1.4; no0.1.5 package yet. See PROGRESS.md for logs and limitations.

Batched 0.1.5 scheduler/queue source (2026-11-05): startup triggers, timed/overnight windows, periodic HTTP file synchronization, queue retry limits and actual queue detachment added. Periodic sync compares validators and stages changed/unverifiable downloads while keeping the old final file until atomic replacement. Previous completed versions are retained in statistics without double-counting the current version. Retry processing is explicit for queue-managed downloads, runs at30-second intervals, respects stop/cancel and blocks premature completion actions. Startup-only failures retain pending eligibility. Explicit queue detachment keeps jobs/files and excludes them from queue pause/concurrency/quota/sync policy. Source only; consolidated validation pending. Network troubleshooting made no system changes; external test downloads remain deferred while the owner's throughput problem is unresolved.

Batched 0.1.5 source (2026-11-05): per-download completion options and real HTTP response-stream telemetry added; no build/test run. Properties stages program inheritance/override/disable, open-file and gated system actions; execution marker is persisted before side effects. HTTP telemetry measures actual response reads across direct/range/media/probe requests and exposes only host/port, protocol/status/range/bytes/rate. Statistics added to scope: archived minimal job records are written before removal and deduplicated against current jobs for persistent byte/count totals, with a View → Statistics window integrated in source. Consolidated validation remains pending.

Batched 0.1.5 source (2026-11-05): persistent independent download groups now have Host-owned definitions/membership, atomic settings commits and group controls. Tasks and selected-download context actions expose group creation/assignment; expandable management window is integrated in source, with aggregate progress/controls visible while collapsed and independent child controls. Bottom download-only speed graph added from owner reference 33528189-2dc8223e-d865-11e7-9d2a-2b5102afb5af.png: selectable 1/5/30/60-minute session history, scale/grid, current/average/peak, disconnected/stale gaps, persisted visibility/collapse. Source-only; consolidated build/tests and live DPI/theme/workflow acceptance pending. FTP remains out of scope.

Batched 0.1.5 source (2026-11-05): explicit unused-category deletion in Options and populated queue deletion with download/schedule reassignment added; source-only, consolidated verification pending. Queue moves save before old queue removal, and failed persistence restores in-memory membership for retry. Settings/category/job admission and queue membership edits share a mutation gate. No build/test run for this batch.

Owner scope update (2026-11-05): FTP work is out of scope; existing backend support is retained, but no additional FTP UI or acceptance work is required. Superseded: FTP was cut in 0.7.2 (CUT.1, `docs/plans/feature-cuts.md`); an FTP address now fails with "FTP addresses aren't supported." Owner, 2026-11-12/13: FTP returns as a kind of remote site, with SFTP, WebDAV and cloud storage through a bundled rclone (planned, not started; `docs/plans/remote-sites.md`, ROADMAP SITES.0 to SITES.9); FTP download addresses stay refused.

Owner workflow: batch remaining implementation, then consolidated verification; no new feature-by-feature test runs. Source0.1.5 now includes Add URL Host-owned metadata preview and suggested filenames. Final cancellation-review fixes and live acceptance await consolidated validation (PROGRESS.md).

0.1.5 source: persistent custom categories, Add URL selection/creation, extension rules, category folders/remember, sidebar and Options editing implemented. Focused7/7 and Core79/79 pass; full integration82/82 passes (artifacts/integration-categories.log); live GUI acceptance pending. Metadata preview/server-suggested names and category deletion remain open (PROGRESS.md).

Owner file-icon requirement (2026-11-05): replace generic file glyphs with Windows file-type and completed-file/application icons. Source implementation now covers main/queue rows and progress/completion headers; native checks2/2 and clean WinUI build pass. Live visual/DPI acceptance remains pending; see PROGRESS.md.

Post-0.1.4: manual HTTP Basic authentication and DPAPI-protected job headers implemented in source; integration 76/76 and redirect-boundary tests pass. Owner's testfile.org right-click 403 was investigated: automatic capture has a browser-resolved CDN URL, right-click does not. Frame/profile/partition cookie handling corrected; extension 34/34 plus typecheck/build pass. Direct handoff remains the normal path; browser-assisted preparation is an optional improvement, not a confirmed defect. Broader live acceptance remains open (PROGRESS.md).

Post-0.1.4 source increment: persisted queue file ordering and Queues → Files editor added. Focused actual-dispatch/restart/stale-list coverage passes, Core 79/79, clean Runtime/WinUI builds. Full integration 74/74 passes (artifacts/integration-queue-file-order.log); live editor acceptance remains open. This is separate from grouped independent child downloads, which remain planned.

0.1.4 published (2026-11-05): signed public feed/assets verified; inherited-working-directory and persistent-file-lock rollback fixtures pass. Installed repair/restart acceptance remains open. Post-release source now exposes Site Logins editing; clean UI build and secure-login integration workflow pass, live editor acceptance pending. See PROGRESS.md for evidence and artifact hashes.

Owner requirement (2026-11-05): add first-class expandable groups for multiple independent child downloads, with aggregate progress/state and parent controls. Persist and restore membership. Keep distinct from byte-range segments. This is a prerequisite for TorBox multi-file transfers. Host persistence, group controls and expandable WinUI management are now implemented in source; consolidated verification remains pending (PROGRESS.md).

Updater repair checkpoint (2026-11-05): owner upgrade exposed stale 0.1.2 binaries with a 0.1.3 version record after replacement/rollback failed. Source 0.1.4 repairs working-directory/native-bridge locks, metadata ordering/recovery, version reconciliation, progress events/window, and frontend reopening. Focused updater tests and isolated rollback fixtures pass; installed repair and live restart/progress acceptance remain pending. See PROGRESS.md.

Owner added GitHub build publication and installer-only automatic updates.
Releases and source: public `Kaan88/inbound` (owner, 2026-11-06; the builds-only
repository `Kaan88/download-manager-builds` is retired). 0.1.3 installer/update assets are published, and an older-version test installer is available;
owner upgrade acceptance and remaining work are still open.

**Scope decision (owner, 2026-11-04): Dial Up/VPN is out of scope.**

Nov5 increment: WinUI queue creation/editing for names, concurrency and quotas
compiles cleanly; focused restart/preservation test passes. Live GUI acceptance,
queue deletion/file ordering and scheduler editing remain pending.

Current increment: per-job live HTTP speed caps, progress controls and compact
completion actions. Core 78/78, engine 47/47, integration 69/69;
the Host access blocker is cleared and representative live window checks passed.
Owner approval and broader visual variants remain open. Queue/scheduler and the other
review gaps remain on the roadmap.

**Owner acceptance blocker (2026-11-04):** the current WinUI is considered basic
and disorganized. When the owner has time, perform a complete live review of
its appearance and interaction flows, then reorganize its structure, density,
hierarchy and behavior using our own assets.
The owner resumed work and authorized the live review on 2026-11-04.
The review includes missing functionality and behavior needing adjustment,
not only appearance.
Existing functional evidence does not establish visual acceptance. See
ROADMAP UI.R1/UI.R2 and PROGRESS.md for the deferred review and Edge flyout note.

Latest increment (2026-11-04): browser-resolved final URLs are used with
original-origin credential boundaries and target validation (E95). Separate
automatic per-job progress/completion windows replace the main-window modal
flow (E96, partial acceptance). Core 78/78, engine 41/41, integration 66/66;
extension 7/7 plus typecheck/build. Light/tray visual acceptance remains pending.

> **Tracking fallback note (2026-11-04).** This OpenCode 2.2.5 environment has no
> Goal or Todo tool (verified through the tool catalog, the OpenCode V2 docs, and
> the local API OpenAPI document). Per the task instructions, `GOAL.md`,
> `ROADMAP.md`, and `PROGRESS.md` are the authoritative tracking
> records. They are maintained as if they were the Goal/Todo features. Do not
> duplicate them into another system.

## Objective

Build a private-use Windows download manager with verified functionality. The application must:

- run as a per-user background host with a notification-area presence;
- keep downloading when the main window is closed or the frontend exits;
- accept downloads from Google Chrome through our own Manifest V3 extension and
  a per-user Native Messaging host;
- provide a replaceable WinUI 3 frontend over a versioned local IPC contract;
- survive restarts, interruptions, changed remote content, and browser
  disconnects without corrupting partial data;
- be reproducible, installable, upgradable, and cleanly uninstallable;
- document every feature claim with evidence, and label every gap.

## Architecture constraints

- **DownloadManager.Core** — domain models, state machine, policies, interfaces.
  No WinUI/Windows App SDK references.
- **DownloadManager.Engine** — HTTP transfers, segmentation, resume, retries,
  throttling, integrity. No presentation references.
- **DownloadManager.Persistence** — durable jobs, checkpoints, settings, history,
  migrations. Atomic file replacement; no WinUI references.
- **DownloadManager.Contracts** — versioned IPC commands/events/DTOs.
- **DownloadManager.Host** — single per-user background process; authoritative
  state; scheduler; named-pipe server; owns the tray. One process for engine +
  tray + IPC. Documented in `docs/architecture.md`.
- **DownloadManager.Tray** — tray lifecycle/menu library used by Host.
- **DownloadManager.Desktop.WinUI** — replaceable frontend; talks only over IPC.
- **DownloadManager.NativeHost** — Chrome Native Messaging bridge; stdout is
  reserved for length-prefixed JSON.
- **DownloadManager.Cli** — headless control/diagnostics over IPC.
- **extensions/chrome** — our Manifest V3 TypeScript extension, own ID/name.
- Tests and a controlled local HTTP server.

Core, Engine, Persistence, and Contracts must not reference WinUI, Windows App
SDK, or presentation types.

## Milestones

| # | Milestone | Status | Exit gate |
| --- | --- | --- | --- |
| 0 | Baseline, feature inventory, repo, browser strategy | done | Buildable solution, initial feature inventory, concrete handoff strategy |
| 1 | Correct single-stream engine + CLI | done | Controlled downloads hash correctly; failures never look successful |
| 2 | Persistence, resume, crash recovery | in progress | Kill/restart matrix passed for single and segmented downloads at 20/50/80% (E94); independent review remains |
| 3 | Tray + minimal WinUI frontend | done | Downloads continue with window closed; reopen restores state |
| 4 | Chrome integration, first daily version | in progress | One Chrome request → one job while host is in tray; authenticated cookie/referrer/UA handoff, destination/start-mode dialog, live popup progress, enable/disable, file-type/site rules, temporary bypass, outage recovery, media and Download All verified (independent restart/handoff verification remains) |
| 5 | Parallel segmented transfers + limits | in progress | Correct through pause/crash/retry; dynamic redistribution, per-host/global limits, stable estimates and the A9 benchmark verified (E81-E84); kill/restart matrix passed (E94), independent review remains |
| 6 | Everyday download management | done | Clipboard monitoring, completion dialog/notification/sound, batch URLs, editable descriptions, duplicate policies, category destinations, last-folder and recent-URL memory, list export/import, category/state views, numeric sort, multiselect bulk actions, delete-file choice, shell open/reveal, and progress details verified (E33–E54/E56) |
| 7 | Authentication + refresh address | in progress | Cookie/referrer/UA, authenticated HTTP/HTTPS proxy, SOCKS5 auth, DPAPI site logins, redirect credential boundary, and expired-URL refresh with partial resume verified; SOCKS4/4a and proxy-password hardening remain |
| 8 | Queues + scheduler | done | Named queues, caps, start/stop, reassignment, schedules, quotas, completion actions, Download Later, sleep prevention, and DST behavior verified (E9/E10/E13/E16/E22/E23) |
| 9 | Media downloading | cut in 0.7.2 (CUT.1) | Stream downloading was removed in 0.7.2 (`docs/plans/feature-cuts.md`): a streaming playlist now fails with "Streaming playlists aren't downloaded". What follows was verified before the cut and no longer ships: HLS/DASH assembly (SegmentTemplate, Timeline, SegmentList, SegmentBase byte ranges), A/V muxing (DASH and HLS alternate audio), fragment recovery, token refresh, live exclusion, DRM detection, and HLS AES-128 key rotation verified (E24-E32/E55/E57-E59); dynamic/live, multi-period, hierarchical SIDX, and DRM decryption remain excluded |
| 10 | Download All; site grabber removed | done / scoped | Download All collection/filter/handoff is done (G1/E31); site grabber G2–G4 was removed by owner decision and is out of scope |
| 11 | Remaining compatibility/integration | in progress | FTP (E42, cut in 0.7.2 by CUT.1, `docs/plans/feature-cuts.md`), hooks (E43), CLI (E44), profiles (E45), settings transfer (E46), repair (E47), upgrade/uninstall (E48), installer-only updater check/download/install/rollback/notifications/gating (E49–E51) verified; final UI audit remains; Edge automatic catching checked by the owner (ROADMAP EXT.E1); Firefox built, registered and accepted by the app from 0.6.0, not yet checked in Firefox (EXT.B3) |
| 12 | Final visual/behavioral audit | in progress | Live UI audit closed M1/M2/M4/M5/M14, U1/U2 (checklist/live UIA dump), U3, U4, U5, U6 and U7 (accessibility/contrast/DPI simulation/localization audit); clean install, upgrade and uninstall passed on a fresh Windows VM (TEST.2); the final audit remains (E52–E54/E85–E88) |

## Major decisions

1. **Process boundaries:** one background Host process owns the engine,
   persistence, schedule, tray, and IPC. Tray is a library, not a second
   process. UI, CLI, and NativeHost are separate clients. Rationale: simpler
   single-writer persistence and one lifecycle authority; still satisfies
   frontend replaceability.
2. **IPC:** per-user named pipes with a current-user-only ACL; JSON envelopes
   carrying `protocolVersion`; snapshot + event stream; reconnect by snapshot.
3. **Browser protocol:** our own JSON protocol over Native Messaging.
4. **Persistence:** per-job JSON documents with atomic temp-file replace plus a
   compact index, avoiding a native SQLite dependency for the first milestone.
   Migration layer included.
5. **Own extension identity:** our extension is named **Download Manager
   Bridge**, uses its own manifest key/ID, and registers only our native host.
6. **Destination-local payload.** There is no fixed temp directory or
   post-download stitching: every job's single random-access part file lives in
   the chosen destination directory. Segments are written at their final offsets
   into that file and completion is a same-volume atomic rename. The state drive
   is never used for payload bytes, so choosing another drive actually uses that
   drive's free space and never requires a cross-volume copy.

## Completion criteria

The goal is complete only when every in-scope feature has passing
acceptance evidence, interrupted transfers and browser handoff are reliable,
installation/upgrade/uninstall are reproducible, every deviation is documented,
and Goal/Todo-equivalent tracking, progress notes, and the
repository agree. Until then the goal remains active.
