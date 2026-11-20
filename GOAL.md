# Goal — Build a private Windows download manager

## Current Android objective

Continue the Android downloader and paired-Host client from the completed 0.0.9 parity milestone. ANDROID.8 passed the consolidated source, visual and flow audit; 0.0.9 was published and verified. The subsequent 0.0.10 Remote Files browse hotfix was signed, installed and checked on the owner's phone; public publication is not recorded. Next is ANDROID.9 (TorBox, torrents and archives), after inventorying its separate working copy. Preserve the ANDROID.14 acceptance deferrals and separate production-upload scope. See the [current handoff](docs/plans/android-parity-handoff.md) and [roadmap](ROADMAP.md#current-android-roadmap).

The dated checkpoints below are historical; their release versions and open
items reflect what was known at the time.

S24 background/reboot batch complete; owner pause boundary (2026-11-19, Codex). Production signed0.0.6/API37 completed a naturally scheduled ordinary queue4100 transfer over direct LAN while locked/noninteractive AOD (8MiB, SHA-256 `f4337909e2c0473eb563c4a8010233439d96d01b668ab9c209ed4bb19cb8fd20`). Separate owner-approved single reboot of a user-started UIDT job1 recovered after unlock without an automated app launch: boot ID changed, fresh job1 start logged, timestamped postboot HTTP Range resumed at byte1,114,112, and final16MiB hash matched `db5e9447f0246a6102d25fb34b353db82a355866a4890ef77c9262cbff257c13`. Optional request tracing added only to AndroidTrialServer; main reviewed Terra code, built0errors/0warnings and passed live HEAD/GET/Range/correlation checks. Both task jobs/files/parts and task queue/schedule removed; baseline Main/empty downloads and display timeout/adaptive mode restored, owned LAN fixture stopped. App source/APK/feed unchanged. Owner said "pause after the step is done, not in the middle"; this batch is finished and cleaned: stop here before another step until resumed. Full ANDROID.6/.7, remaining phone Doze/network/power, long-duration and complete visual/accessibility/remote acceptance, and later independent review remain OPEN. Raw evidence: `artifacts/android-app/android6-s24-background/`.

Android constraint/theme acceptance (2026-11-19, Codex): signed0.0.6 S24 dark C1/C2/C7 and empty dynamic-colour Downloads were viewed beside approved references; original app appearance, adaptive brightness and timeout restored. API33 NativeAOT emulator verified actual OS Wi-Fi metering and battery-saver holds at zero bytes, then automatic hash-matched completion when each condition cleared. Corrected scheduled Doze test used SIGKILL only on the verified idle app PID: payload stayed queued/0bytes in forced idle through12:36:51 for a12:36 schedule, then completed naturally by12:37:01 after unforce (2,097,153bytes; SHA-256 `a740e0039f45586d220c77e43663196aed08648c2e38effd0fb069d026bc098c`). The first `am kill` attempt was Android reason10/user-stop and correctly stayed user-paused; it is not a background-resume failure. All four task jobs/payloads, queue and schedule removed; original emulator metering/low-power/idle-force state and recorded baseline fields restored, non-root adb; hardware emulator remains running. Main verified Terra's evidence review. No app source changes or new release; ANDROID.6/.7 remain PARTIAL for physical API37 background/network/power/reboot, full visual/accessibility flows, broader remote and later independent review. Evidence: latest ANDROID-CODEX-PROGRESS and `artifacts/android-app/android6-acceptance-0.0.5/`.

Android acceptance checkpoint (2026-11-19, Codex): the owner's S24 FE was rediscovered over Wi-Fi ADB (SM-S721B/API37). Its installed 0.0.6 APK SHA-256 exactly matches the public release. Real-phone UI created two disabled schedules, saved/reopened Mon/Wed/Fri, canceled deletion once, then deleted only the second and verified the first remained. A 1 MiB task download waited in the stopped queue, completed after explicit queue start, and matched fixture SHA-256 `b13261ceffb27a120f88c3466a3917bfb2875493d5f802422b433a3599407be6`; transport was Wi-Fi ADB reverse, not direct LAN. Task queue/schedule/job/payload/reverse removed; baseline Main and empty downloads verified. Main personally viewed C1/C2/C7 phone/mockup pairs; Terra checked evidence and visual scope. Owner manually confirmed TalkBack moves between controls and reads useful labels in Downloads. Original TalkBack/accessibility, brightness and timeout values were restored and verified; this does not establish exhaustive accessibility acceptance. Emulator NativeAOT API33 additionally held a charging-only queue without payload requests, then completed with matching hash after simulated AC; a persisted Android job naturally recreated an absent process and started the queue at11:48:18 for11:47 (78seconds late). Emulator task data removed, recorded baseline queue fields restored, non-root adb; full settings hash differs after create/delete, so no byte-identical restoration claim. ANDROID.6/.7 remain PARTIAL; phone Doze/network/reboot, full visual/accessibility and broader remote gates remain OPEN.

Android 0.0.6 published (2026-11-19, Codex): https://github.com/Kaan88/inbound/releases/tag/android-v0.0.6 from source `edd073b09472bab45c9c18adb4ae4161ad08be35`. Permanent-key NativeAOT arm64 APK:20,664,711 bytes, SHA-256 `30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`; manifest SHA-256 `fdc5c2666cd8037a6255529bb5c7603f16eb8f5ba13eecd619f27d4acc005266`. Downloaded public APK/manifest/checksums match staged bytes; android-latest matches the same versionCode6/versionName0.0.6 signed manifest. Desktop latest remains v0.9.0. Main reviewed worker output and final source; NativeAOT rate checks, Mobile528 and solution build passed as scoped in the candidate checkpoint below. Queue/global KB/s correction is shipped; no new Astra milestone audit claimed. ANDROID.6/.7 phone/TalkBack, broader remote and Doze/network/reboot acceptance and later independent review remain OPEN. Main checkout untouched; no merge to main.

Android 0.0.6 candidate (2026-11-19, Codex): remote QA exposed binary speed inputs versus decimal rate summaries. Queue and overall KB/s inputs now use 1000 B/s; unchanged legacy raw limits are preserved. Main personally reviewed the complete diff; Terra scoped source/evidence PASS. Mobile528, Debug build0errors/36warnings, Release solution0errors/87warnings passed. NativeAOT x64 install verified 32 and256 inputs persist as32000 and256000 B/s and render accordingly. Permanent-key arm64 packaging checks passed (20,664,711 bytes, SHA-256 `30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`); staged locally, not yet published. Isolated remote schedule Mon/Wed23:58–23:59 and restart persistence verified; task queue/schedule removed and global limit reset toNone, local settings hash unchanged. Hardware emulator remains visible as requested. Public feed remains0.0.5; ANDROID.6/.7 phone/TalkBack, broader remote and Doze/network/reboot acceptance remain OPEN. Evidence: `artifacts/android-app/android6-acceptance-0.0.5/`; later independent review remains OPEN.

Android 0.0.5 published (2026-11-19, Codex): the approved update dialog and signed changelog ship from source `87555b993ae8f3de7e61a171c87a932a8d6a4a2b`. Public prerelease https://github.com/Kaan88/inbound/releases/tag/android-v0.0.5 contains the permanent-key NativeAOT arm64 APK (20,664,711 bytes; SHA-256 `1cf087a69c56744e1ee7b0bd5e3c29bce1fe7cad5db371f78ada35fac8805691`), signed manifest and checksums. Downloaded assets matched local staged bytes; android-latest serves the identical 0.0.5 manifest. Package, pinned signer, API31/target35, ABI, provider/TLS and no-test-hook checks passed. Mobile528/release-tool10 and solution0errors/53warnings passed; Astra SOURCE PASS. Final NativeAOT emulator verified job stop, one injected crash with forced restart and matching completed payload hash, keyboard/rotation and dialog previews. ANDROID.6/.7 and later independent review remain OPEN for phone/TalkBack, broader remote and remaining Doze/network/reboot acceptance. Desktop latest remains v0.9.0; no merge to main. See latest PROGRESS and ANDROID-CODEX-FABLE-REVIEW.

Historical checkpoints below predate this publication and retain their original validation scope.

Android 0.0.5 source checkpoint (2026-11-19, Codex): the owner approved the compact update dialog with the same changelog in Settings. Separately signed notes preserve the existing APK signature format; Mobile 528 and release-tool 10 tests passed. The new source passed Astra follow-up after main verified/fixed execution callback ownership, API 30 stop-reason access and keyboard event handling. A NativeAOT emulator test of the preceding job-identity/crash-marker revision observed Android stop detaching the execution, a queued partial checkpoint with queue intent preserved, then explicit restart/completion with matching payload SHA-256. Touch selection in the new picker survived delayed metadata and submitted to the selected queue. The final modal picker passed source review, Debug compilation (0 errors/2 warnings), keyboard selection, delayed metadata, both rotations and queue2 admission on the emulator. The clean NativeAOT/arm64 packaging and physical-device acceptance remain pending. Android 0.0.4 stays the public immutable release; 0.0.5 is unpublished, ANDROID.6 remains partial and the later independent review remains OPEN. Full final Integration passed 2,095 with 32 optional skips; Core 2,373, Engine 214 and Server 208 passed. See ANDROID-CODEX-PROGRESS.md and ANDROID-CODEX-FABLE-REVIEW.md for exact artifact scope.


Android 0.0.4 published (2026-11-19, Codex): owner requested immediate APK publication ("just put the apk already"). Source commit b6c2f6668378c0fc0b16c8be5fa5f92397dd2ba7 is pushed to origin/android-app; no merge to main. Public prerelease https://github.com/Kaan88/inbound/releases/tag/android-v0.0.4 contains the permanent-key NativeAOT arm64 APK, signed manifest and SHA256SUMS. Both releases use latest=false; desktop latest remains v0.9.0. The android-latest feed now serves the identical 0.0.4 manifest. Public APK bytes and both manifest copies were downloaded and matched local checksums: APK cd49d3e2b0b3f729752642934a29f2a5ae4b577e3e50486a7b5107d0fe6bff65 (20,599,175 bytes), manifest 3343eabef3a601c116325203c366c210eb5eea64a104ad184f1bbbd248b5f979. Release/signature/API31/target35/ABI/no-test-hook checks passed. Final solution: 0 errors, 23 warnings; Mobile505, Core2373 and focused final Integration10 passed; remaining broad suites still running at publication. Astra source audit PASS. Final NativeAOT emulator install/launch preserves data; S24 is not attached. ANDROID.6 physical-device/accessibility and final broader acceptance remain OPEN; publication does not complete the milestone or later independent review queue. Evidence: artifacts/android-app/android6/final-production-clean-build.txt, final-release-prepare.txt, final-solution-build.txt, final-regressions.txt, audit-mobile-final.txt and public-verification/.


Historical pre-publication checkpoint (2026-11-19, superseded by the publication above): owner asked "could you make a release to github," authorizing Android 0.0.4 publication after verification. C1/C2/C7 follow the approved 28-flow mockup board. Initial major Astra audit returned CHANGES REQUIRED; eight verified findings have source fixes, with re-audit pending. Main Sol personally inspected delegated source and added schedule-parent identity checks. Mobile 489 and focused identity/scope/golden 8 passed; full Integration rerun and final Android builds/live checks are in progress. ANDROID.6 remains partial and the later independent review queue remains OPEN. No release has been published yet.


Android continuation (2026-11-19, Codex): owner reviewed the 28-flow board and said "looks good," approving it as design direction and resuming implementation of the proposed flows. This is not feature acceptance or milestone completion. Terra owns the C2 editor/dialogs and C7 grouped root/Details flows; main Sol owns queue actions, shared completion behavior and integration, and personally checks worker code. Preserve board-marked future capabilities and Android/selected-Host boundaries as pending. Full Integration passed 2,077, skipped 32, failed 0; the latest Android Debug build had 0 errors and 2 known SDK warnings. NativeAOT, phone, remote-Host, Doze, accessibility, theme and visual acceptance plus later Astra review remain open. Public Android feed remains 0.0.2; local Debug 0.0.4 is not a published release. See PROGRESS "ANDROID.6 board-approved implementation checkpoint".
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
