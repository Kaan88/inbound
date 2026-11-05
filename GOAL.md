# Goal — Build a private Windows download manager (reference app as reference)

Owner direction (2026-11-05): reference app 6.43.12.3 is now a reference for suggestions, not a parity requirement. The owner prefers the current Inbound design over reference app's; keep our own design and behavior, and use reference app only as a source of ideas to propose. Rows below are reference notes. A row's status describes our feature, and `implemented` still needs linked acceptance evidence.



Consolidated checkpoint (2026-11-05): full solution Release build0warnings/errors; Core88, engine57, integration91 and extension34 tests pass. Light main/graph/statistics rendered in isolated QA; completed-byte restart defect fixed and regression covered. Full UI/DPI/lifecycle/fault acceptance and unified Scheduler review remain open. Latest public release stays0.1.4; no0.1.5 package yet. See PROGRESS.md for logs and limitations.

Batched 0.1.5 scheduler/queue source (2026-11-05): startup triggers, timed/overnight windows, periodic HTTP file synchronization, queue retry limits and actual queue detachment added. Periodic sync compares validators and stages changed/unverifiable downloads while keeping the old final file until atomic replacement. Previous completed versions are retained in statistics without double-counting the current version. Retry processing is explicit for queue-managed downloads, runs at30-second intervals, respects stop/cancel and blocks premature completion actions. Startup-only failures retain pending eligibility. Explicit queue detachment keeps jobs/files and excludes them from queue pause/concurrency/quota/sync policy. Source only; consolidated validation pending. Network troubleshooting made no system changes; external test downloads remain deferred while the owner's throughput problem is unresolved.

Batched 0.1.5 source (2026-11-05): per-download completion options and real HTTP response-stream telemetry added; no build/test run. Properties stages program inheritance/override/disable, open-file and gated system actions; execution marker is persisted before side effects. HTTP telemetry measures actual response reads across direct/range/media/probe requests and exposes only host/port, protocol/status/range/bytes/rate. Statistics added to scope: archived minimal job records are written before removal and deduplicated against current jobs for persistent byte/count totals, with a View → Statistics window integrated in source. Consolidated validation remains pending.

Batched 0.1.5 source (2026-11-05): persistent independent download groups now have Host-owned definitions/membership, atomic settings commits and group controls. Tasks and selected-download context actions expose group creation/assignment; expandable management window is integrated in source, with aggregate progress/controls visible while collapsed and independent child controls. Bottom download-only speed graph added from owner reference 33528189-2dc8223e-d865-11e7-9d2a-2b5102afb5af.png: selectable 1/5/30/60-minute session history, scale/grid, current/average/peak, disconnected/stale gaps, persisted visibility/collapse. Source-only; consolidated build/tests and live DPI/theme/workflow acceptance pending. FTP remains out of scope.

Batched 0.1.5 source (2026-11-05): explicit unused-category deletion in Options and populated queue deletion with download/schedule reassignment added; source-only, consolidated verification pending. Queue moves save before old queue removal, and failed persistence restores in-memory membership for retry. Settings/category/job admission and queue membership edits share a mutation gate. No build/test run for this batch.

Owner scope update (2026-11-05): FTP work is out of scope; existing backend support is retained, but no additional FTP UI or acceptance work is required.

Owner workflow: batch remaining implementation, then consolidated verification; no new feature-by-feature test runs. Source0.1.5 now includes Add URL Host-owned metadata preview and suggested filenames. Final cancellation-review fixes and live acceptance await consolidated validation (PROGRESS.md).

0.1.5 source: persistent custom categories, Add URL selection/creation, extension rules, category folders/remember, sidebar and Options editing implemented. Focused7/7 and Core79/79 pass; full integration82/82 passes (artifacts/integration-categories.log); live GUI acceptance pending. Metadata preview/server-suggested names and category deletion remain open (PROGRESS.md).

Owner file-icon requirement (2026-11-05): verified actual Windows Shell icon calls in pinned reference app binary; replace generic file glyphs with Windows file-type and completed-file/application icons. Investigation: `notes/file-icons-2026-11-05.md`. Source implementation now covers main/queue rows and progress/completion headers; native checks2/2 and clean WinUI build pass. Live visual/DPI acceptance remains pending; see PROGRESS.md.

Post-0.1.4: manual HTTP Basic authentication and DPAPI-protected job headers implemented in source; integration 76/76 and redirect-boundary tests pass. Owner's testfile.org right-click 403 was investigated: automatic capture has a browser-resolved CDN URL, right-click does not. Frame/profile/partition cookie handling corrected; extension 34/34 plus typecheck/build pass. Live reference check: reference app itself returns403 for right-click on the same testfile link. Direct handoff remains the normal path; browser-assisted preparation is an optional improvement, not a confirmed parity defect. Broader live acceptance remains open (PROGRESS.md).

Post-0.1.4 source increment: persisted queue file ordering and Queues → Files editor added. Focused actual-dispatch/restart/stale-list coverage passes, Core 79/79, clean Runtime/WinUI builds. Full integration 74/74 passes (artifacts/integration-queue-file-order.log); live editor acceptance remains open. This is separate from grouped independent child downloads, which remain planned.

0.1.4 published (2026-11-05): signed public feed/assets verified; inherited-working-directory and persistent-file-lock rollback fixtures pass. Installed repair/restart acceptance remains open. Post-release source now exposes Site Logins editing; clean UI build and secure-login integration workflow pass, live editor acceptance pending. See PROGRESS.md for evidence and artifact hashes.

Owner requirement (2026-11-05): add first-class expandable groups for multiple independent child downloads, with aggregate progress/state and parent controls. Persist and restore membership. Keep distinct from byte-range segments. This is a prerequisite for TorBox multi-file transfers. Host persistence, group controls and expandable WinUI management are now implemented in source; consolidated verification remains pending (PROGRESS.md).

Updater repair checkpoint (2026-11-05): owner upgrade exposed stale 0.1.2 binaries with a 0.1.3 version record after replacement/rollback failed. Source 0.1.4 repairs working-directory/native-bridge locks, metadata ordering/recovery, version reconciliation, progress events/window, and frontend reopening. Focused updater tests and isolated rollback fixtures pass; installed repair and live restart/progress acceptance remain pending. See PROGRESS.md.

Owner added GitHub build publication and installer-only automatic updates.
Releases and source: public `Kaan88/inbound` (owner, 2026-11-06; the builds-only
repository `Kaan88/download-manager-builds` is retired). 0.1.3 installer/update assets are published, and an older-version test installer is available;
owner upgrade acceptance and remaining parity work are still open.

**Scope decision (owner, 2026-11-04): Dial Up/VPN is out of scope.**

Nov5 increment: WinUI queue creation/editing for names, concurrency and quotas
compiles cleanly; focused restart/preservation test passes. Live GUI acceptance,
queue deletion/file ordering and scheduler editing remain pending.

Current increment: per-job live HTTP speed caps, progress controls and compact
completion actions (PARITY E97). Core 78/78, engine 47/47, integration 69/69;
the Host access blocker is cleared and representative live window checks passed
(PARITY E98). Owner approval and broader visual variants remain open. Queue/scheduler and the other
screenshot-review gaps remain on the roadmap.

**Owner acceptance blocker (2026-11-04):** the current WinUI is considered basic
and disorganized. When the owner has time, perform a complete live review of
reference app's appearance and interaction flows, then reorganize the WinUI to match the
reference's structure, density, hierarchy and behavior using our own assets.
The owner resumed work and authorized the live review on 2026-11-04.
The review includes missing functionality and behavior needing adjustment,
not only appearance. The owner supplied 36 screenshots, cataloged in
`reference-screens/INDEX.md`; the source comparison and functional gaps are in
`notes/screenshot-review-2026-11-04.md`.
Existing functional evidence does not establish visual acceptance. See
ROADMAP UI.R1/UI.R2 and PROGRESS.md for the deferred review and Edge flyout note.

Latest increment (2026-11-04): browser-resolved final URLs are used with
original-origin credential boundaries and target validation (E95). Separate
automatic per-job progress/completion windows replace the main-window modal
flow (E96, partial acceptance). Core 78/78, engine 41/41, integration 66/66;
extension 7/7 plus typecheck/build. Light/tray visual acceptance and a fresh reference app
completion reference capture remain pending; full parity is not complete.

> **Tracking fallback note (2026-11-04).** This OpenCode 2.2.5 environment has no
> Goal or Todo tool (verified through the tool catalog, the OpenCode V2 docs, and
> the local API OpenAPI document). Per the task instructions, `GOAL.md`,
> `ROADMAP.md`, `reference notes`, and `PROGRESS.md` are the authoritative tracking
> records. They are maintained as if they were the Goal/Todo features. Do not
> duplicate them into another system.

## Objective

Build a private-use Windows download manager with verified functional parity
against the installed another download manager. The application must:

- run as a per-user background host with a notification-area presence;
- keep downloading when the main window is closed or the frontend exits;
- accept downloads from Google Chrome through our own Manifest V3 extension and
  a per-user Native Messaging host;
- provide a replaceable WinUI 3 frontend over a versioned local IPC contract;
- survive restarts, interruptions, changed remote content, and browser
  disconnects without corrupting partial data;
- be reproducible, installable, upgradable, and cleanly uninstallable;
- document every parity claim with evidence, and label every gap.

## Pinned reference

| Item | Value |
| --- | --- |
| Product | another download manager (reference app) |
| Version | **6.43.12.3** (`v6.43b12 Full`) |
| Vendor | the vendor Inc. / the vendor FZE |
| Architecture | 32-bit x86 PE |
| `reference app.exe` SHA-256 | `B601350AF954D23DFD989295F9FB398AFCD972FEEC4F300A7CF1284CBB2B2C93` |
| `reference app.exe` MD5 | `c96a570117b034cfe83f90f3214ebc90` |
| `IDMMsgHost.exe` SHA-256 | `DBA25A49ADB88F675DB26D2DC7A0FA9D4A5DB2326858CB9D2515F6F34B8E0B65` |
| `IDMGCExt.crx` SHA-256 | `1D2297CCBDBBF06E9E2546BB9F9B9985433EB7C02D75F09BBCBC9784ECE583F7` |
| Chrome extension | reference app Integration Module 6.43.1, ID `ngpampappnmepgilojfohadhhmbhlaek` |
| Native host | `com.the vendor.reference app` → `IDMMsgHost.exe` |
| Registry version marker | `HKCU\Software\DownloadManager` → `idmvers = v6.43b12 Full` |

Details and evidence: `notes/reference-version.md`.

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
| 0 | Baseline, reference inventory, repo, browser strategy | done | Buildable solution, initial parity inventory, concrete handoff strategy |
| 1 | Correct single-stream engine + CLI | done | Controlled downloads hash correctly; failures never look successful |
| 2 | Persistence, resume, crash recovery | in progress | Kill/restart matrix passed for single and segmented downloads at 20/50/80% (E94); independent review remains |
| 3 | Tray + minimal WinUI frontend | done | Downloads continue with window closed; reopen restores state |
| 4 | Chrome integration, first daily version | in progress | One Chrome request → one job while host is in tray; authenticated cookie/referrer/UA handoff, destination/start-mode dialog, live popup progress, enable/disable, file-type/site rules, temporary bypass, outage recovery, media and Download All verified (independent restart/handoff verification remains) |
| 5 | Parallel segmented transfers + limits | in progress | Correct through pause/crash/retry; dynamic redistribution, per-host/global limits, stable estimates and the A9 benchmark verified (E81-E84); kill/restart matrix passed (E94), independent review remains |
| 6 | Everyday download management | done | Clipboard monitoring, completion dialog/notification/sound, batch URLs, editable descriptions, duplicate policies, category destinations, last-folder and recent-URL memory, list export/import, category/state views, numeric sort, multiselect bulk actions, delete-file choice, shell open/reveal, and progress details verified (E33–E54/E56) |
| 7 | Authentication + refresh address | in progress | Cookie/referrer/UA, authenticated HTTP/HTTPS proxy, SOCKS5 auth, DPAPI site logins, redirect credential boundary, and expired-URL refresh with partial resume verified; SOCKS4/4a and proxy-password hardening remain |
| 8 | Queues + scheduler | done | Named queues, caps, start/stop, reassignment, schedules, quotas, completion actions, Download Later, sleep prevention, and DST behavior verified (E9/E10/E13/E16/E22/E23) |
| 9 | Media detection/downloading | done | Detection, in-page/quality panel, HLS/DASH assembly (SegmentTemplate, Timeline, SegmentList, SegmentBase byte ranges), A/V muxing (DASH and HLS alternate audio), fragment recovery, token refresh, live exclusion, DRM detection, and HLS AES-128 key rotation verified (E24-E32/E55/E57-E59); dynamic/live, multi-period, hierarchical SIDX, and DRM decryption remain excluded |
| 10 | Download All; site grabber removed | done / scoped | Download All collection/filter/handoff is done (G1/E31); site grabber G2–G4 was removed by owner decision and is out of scope (see reference notes) |
| 11 | Remaining compatibility/integration | in progress | FTP (E42), hooks (E43), CLI (E44), profiles (E45), settings transfer (E46), repair (E47), upgrade/uninstall (E48), installer-only updater check/download/install/rollback/notifications/gating (E49–E51) verified; final UI parity remains; Edge/Firefox out of scope (C3) |
| 12 | Final visual/behavioral parity audit | in progress | Live UI audit closed M1/M2/M4/M5/M14, U1/U2 (side-by-side checklist/live UIA dump), U3, U4, U5, U6 and U7 (accessibility/contrast/DPI simulation/localization audit); clean-Windows acceptance remains (E52–E54/E85–E88) |

## Major decisions

1. **Process boundaries:** one background Host process owns the engine,
   persistence, schedule, tray, and IPC. Tray is a library, not a second
   process. UI, CLI, and NativeHost are separate clients. Rationale: simpler
   single-writer persistence and one lifecycle authority; still satisfies
   frontend replaceability.
2. **IPC:** per-user named pipes with a current-user-only ACL; JSON envelopes
   carrying `protocolVersion`; snapshot + event stream; reconnect by snapshot.
3. **Browser protocol:** our own JSON protocol over Native Messaging. No binary
   compatibility with `com.the vendor.reference app` is required or attempted.
4. **Persistence:** per-job JSON documents with atomic temp-file replace plus a
   compact index, avoiding a native SQLite dependency for the first milestone.
   Migration layer included.
5. **Own extension identity:** our extension is named **Download Manager
   Bridge**, uses its own manifest key/ID, and registers only our native host.
6. **No licensing bypass:** reference app activation/licensing code is out of scope. Only
   functional behavior is studied.
7. **Reference binaries stay out of git.** Hashes, addresses, and analysis notes
   are committed; executables and captures are not.
8. **Destination-local payload.** Unlike reference app's fixed temp directory plus
   post-download stitching, every job's single random-access part file lives in
   the chosen destination directory. Segments are written at their final offsets
   into that file and completion is a same-volume atomic rename. The state drive
   is never used for payload bytes, so choosing another drive actually uses that
   drive's free space and never requires a cross-volume copy.

## Completion criteria

The goal is complete only when every in-scope row in `reference notes` has passing
acceptance evidence, interrupted transfers and browser handoff are reliable,
installation/upgrade/uninstall are reproducible, every deviation is documented,
and Goal/Todo-equivalent tracking, parity records, progress notes, and the
repository agree. Until then the goal remains active.
