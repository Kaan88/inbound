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
Update 2026-11-06 (TorBox phase 1): parent controls, queue, priority, membership repair,
member limit and a once-per-completion event are in with tests of restart, partial
failure and mixed states. UI expansion and the new package controls still need a look
on screen before this is complete. See `PROGRESS.md`.

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
| UI.R2 | Investigate Edge downloads flyout opening when a download starts | partial; fix implemented, owner Edge check pending | Cause: interception ran in `downloads.onCreated`, after Chromium had already added the download to its UI. The extension now holds `downloads.onDeterminingFilename` instead (b56ae50, docs/browser-integration.md). Accept only after the owner confirms no flyout flash in Edge for accepted downloads and a normal flyout for downloads left to the browser. |
| EXT.S1 | Publish the extension as a hidden Edge Add-ons listing | parked (owner, 2026-11-06) | Store zip built at `artifacts\edge-store\inbound-bridge-0.2.0-edge.zip` (0.2.0, `key` removed). Partner Center sign-up stalled on the generic Home page with no Edge program. On resume: finish enrollment, submit hidden listing, then add the store ID to native-host `allowed_origins` and register the host for Edge in the installer. |
| EXT.S2 | Publish the extension on the Chrome Web Store | submitted by the owner (2026-11-06); not yet public when last checked | Item ID `olhbcdbjdnfoiinllnlpandggadfabfm`, accepted by the app from 0.4.0. Zip: `scripts\package-extension-store.ps1`. All dashboard text and images: `docs/chrome-store/`. Privacy policy: `docs/chrome-extension-privacy.md`, online. The store CRX's code under the store ID connected to installed Setup 0.4.0, and a real download completed against a separate instance of it (2026-11-07, see PROGRESS). A store package unpacked by hand gets a folder ID and is refused; use `scripts\make-store-id-test.ps1`. Left: once approved, install from the store and check it the same way. The extension has no media panel. |
| UI.R3 | Use the default folder for each new Chrome-extension download | pending; owner requirement | Each new extension download starts with the configured default download folder, rather than the destination selected for the previous download. A one-download folder choice must not change the next download's initial destination. Verify consecutive downloads with different destinations and after extension/browser restart. |
| UI.R4 | Fit Chrome-extension windows and their controls to the available screen | pending; owner requirement | Review the handoff and other extension windows at supported screen sizes/scaling. Size and lay out windows so all items and primary actions are accessible without clipping; use scrolling where available space cannot accommodate the full content. Include this in the owner visual review. |
| UI.R5 | Hold Delete to leave a download in the browser | pending; owner requirement | Support holding Delete while initiating a Chrome download to bypass manager capture for that download. Chrome must retain/download the file normally, with no manager job or duplicate transfer. Verify capture resumes for subsequent downloads after key release. Reuse the existing bypass flow where appropriate; exact key timing and browser limitations remain unverified. |
| UI.R6 | Keep moving numbers from shifting, everywhere counters appear | pending; owner request, 2026-11-07; approach undecided | Counters such as download size jump sideways as their digits change, because the font's digits differ in width. This applies to live counters in general, not only the segment details: the main list, progress windows, status bar and graph readouts too. The owner suggested monospace and does not know how other apps handle it, so compare monospace with same-width digits (tabular figures) in the current font first. Accept when live counters hold still in both themes at normal and high DPI. |
| UI.R7 | Hover info on the speed graph | building; owner request, 2026-11-07, wanted before 0.5.0 | Hovering over the graph shows the details of the point under the pointer. At least the speed and time; the rest to be settled when built. Must work in both themes and at every graph period, and stay readable near the graph's edges. |
| UI.R8 | Smooth the speed graph, maybe scrolling too | maybe; owner, 2026-11-07, not committed | Optional. If taken up: smoother drawing of the line, and possibly a graph that scrolls continuously. Must not hide real drops or spikes, or add noticeable CPU use while the window is idle. |
| UI.R9 | Favorite download locations | built, not yet seen on screen; owner request, 2026-11-07, from a prospective Docker user; the owner tests it | A saved list of named folders, managed in Options. Every destination box (Add URL, Add Links, My TorBox, container import, the default folder) offers them first, then recent folders and the category folder. A star beside the box saves the typed folder. A category can point at a favorite, for example Video to "Movies". The Host keeps the list in settings, so the CLI (`--to <name>`) and the future web UI use the same one. |
| UI.R10 | Free space for each download location | built, not yet seen on screen; owner request, 2026-11-07, from a prospective Docker user; the owner tests it. The container case is unit-tested only: no Linux Host exists yet | The Host reports free and total space for each favorite, recent and default folder. It's measured on the Host's side so it stays right in Docker, where the UI runs on another machine. A drive that doesn't answer shows "unavailable" after a short timeout instead of hanging. In a container the space must be measured for the folder itself, not its drive root, which on Linux is the container's own `/`. A folder that is not on a mounted volume is flagged, since files there are lost when the container is rebuilt. Space that can't be measured honestly shows as unknown. The destination box shows the space of whatever folder is typed, and warns before adding when a known size won't fit. |
| UI.R11 | Scrollbars cover the right edge of dialogs | fixed, not yet seen on screen; owner screenshot, 2026-11-07 | In Add URL the scrollbar covered the star button. WinUI draws a scrollbar over what it scrolls, so every dialog scroller now has 16 px on the right (`InboundTheme.DialogScrollPadding`). |
| UI.R12 | Add URL shows an empty strip under the URL box | fixed, not yet seen on screen; owner screenshot, 2026-11-07 | With no recent addresses the editable box opened an empty list over the dialog. It now stays closed when there is nothing in it. |
| UI.R13 | Hoster status window | built, not seen on screen; owner request, 2026-11-07 | TorBox's hosters with their icon, up or down, today's use against the daily limits, the per-link limit and notes. The Host already fetches the list (`webdl/hosters`) and caches it for about an hour, so the window reads the cache and adds no API calls. It opens from a Hosters button in the My TorBox toolbar and from Tasks > TorBox Hosters (owner's choice). Evidence: `HostPipeRemoteTests.TheHosterListComesFromTheCacheWithIconsAndTodaysUse`. |
| UP.B1 | Saving Options cuts the update key to its first line | open, owner report 2026-11-07 | - | Owner report: 0.4.0 says "The update manifest signature is invalid." when checking for 0.5.0. The release verifies against the built-in key (checked); the installed settings hold only `-----BEGIN PUBLIC KEY-----`. Likely cause: the Options "Update public key (PEM)" box sets `Text` before `AcceptsReturn = true`, so it is single-line when the key goes in and keeps its first line, and any Options save writes that back. Present in 0.4.0 and 0.5.0. A copy that has saved Options cannot update itself; it needs Setup run by hand, and a fixed version must also repair the stored key. |
| UI.R14 | Update progress window placement and size | open, owner report 2026-11-07, low priority ("can be fixed whenever") | - | Owner report from the 0.4.0 to 0.5.0 update: the "Inbound Download Manager update" window (Software update, Version, "Verifying downloaded files...", progress bar, Close) opens in the top-left corner of the screen instead of centred on the main window. It is too small for its content: the progress bar runs into the right edge and the Close button is cut off at the bottom right. Seen in 0.4.0's window; the code that opens it has to be checked in 0.5.0. Otherwise the update worked. |
| TB.F1 | Progress details look completely different for provider downloads | done, tests and seen on screen against the mock; owner to judge | **Cause:** two windows written apart. File rows opened `DownloadWindow`; package rows and My provider opened `PackageWindow`. Both now share `JobDetailsPanel`, and a package of one opens the file's own window. Kept apart on purpose: no address on provider files; overall bar and file list in a package; limiter and completion options on the file's own window. |
| TB.F2 | Remove the quota warnings | wording done with a test; the approve step itself goes with TB.F10 | "Uses your account's quota" and "may count against your limits" had no source. Remove them from the second step and the approve dialog. Per-hoster limits TorBox itself reports may stay. **Owner decision, 2026-11-07:** no approval for uncached links the user adds. Choosing files after adding is sound and stays. Copied links still wait, because copying isn't a request to download. |
| TB.F10 | Rethink the add flow for usability | building; owner approved the proposal with changes, 2026-11-07 (see TB.F9) | The owner asked for a rethink of the approach, not just the wording. The dialog that says "Close this and choose the files from the download list" is the example. Design first, then build, then walk it on real screens. |
| TB.F3 | Columns can't be resized or sorted | done, tests and seen on screen against the mock | Main list and My provider: header sort with a direction chevron, drag and keyboard resize, double-click reset, saved in `ui-state.json`. File trees stay without headers, because folder-then-natural order shows the structure. |
| TB.F4 | Mouse region selection of files | done, tests and seen on screen against the mock; one owner choice open | Box selects rows on the main list and ticks rows in file trees. Ctrl adds. My provider's item list stays single-select because it drives one detail pane. **Open:** a plain drag in a tree replaces existing ticks; see `PROGRESS.md`. |
| TB.F5 | Right-click menu on an extracted item is sparse and looks like a bug | done, tests and seen on screen against the mock; owner to judge | A record whose download is on the list now offers that download's commands; a split archive or several records offer Show Downloads. A record with no download keeps its extraction commands. Which of the two paths gave the owner's screenshot isn't known, because the sandbox's jobs are gone. |
| TB.F6 | Don't add the file extension to the extracted folder | done, tests, seen on screen against the mock, and seen on real provider data by CLI | Cause was the provider path `x.rar/x.rar`, kept as given. It now becomes `x\x.rar`, and `name.bin/name.bin` becomes `name.bin`. Files already on disk are not moved. |
| TB.F7 | No notification per item of a grouped download | done, tests; Host decision seen in the log on real provider data, real balloons and sounds not seen | One notice per package, decided by the Host in `DownloadNotices`. A failing package gives at most two: the first failure and the closing count. |
| TB.F8 | Deleting a group after a missing volume leaves a broken item | done, tests and seen on screen against the mock | When every download behind a set is gone the Host deletes the set and no files are touched. A set picked from disk stays. Upgrading also clears finished records whose downloads are gone. |
| TB.F9 | A cached torrent file doesn't list its files by itself | pending; confirmed bug, owner's second try, 2026-11-07 | A cached torrent added from a `.torrent` file needs the right-click menu to choose files. An uncached magnet can't list files yet, which is normal, but the owner isn't sure about the approach for magnets. Review both flows. **Second try (sandbox `0870d34`):** one torrent file couldn't be added at all, and the "Added for TorBox" box stayed stuck. Another was added, but Approve never became clickable. For a cached magnet the box said "Close this and choose the files from the download list". The owner says choosing files is broken. **Owner requirement:** a torrent file shows the file choice first, when it is added, whether it is cached or not. File choice comes before downloading, cached or uncached. This changes the proposal's "not listing files from the `.torrent` bytes". **Owner decision, 2026-11-07:** "yes, build it", with one change: an uncached magnet doesn't wait for a file choice. TorBox likely gives its file list only once it has finished, which can take too long, so the magnet downloads all files when ready. No BitTorrent code just to fetch a file list. **Refinement, same day:** a checkbox when adding, off by default, lets an uncached magnet wait for a file choice when TorBox is done instead of downloading in full. |
| TB.F11 | Direct TorBox links through Add Links: three windows, a name left unresolved, a failed empty file | done, tests against TestServer; not seen live or on screen | Owner report, 2026-11-07. Three direct links pasted into Add Links opened three separate windows. One file kept the link's id as its name, and an empty text file failed. Its package then read Failed 1/3. A Download complete window opened for one file of a grouped download, which TB.F7 should prevent. **Fixed:** jobs join their package before they are announced. The window rule is one tested rule in the Runtime, `DownloadWindows`. An empty file answered with "nothing in that range" completes. The name in the transfer's own answer replaces one taken from the address. |
| TB.F12 | The Download complete window shows a direct link's token in full | done, tests; not seen on screen; security | Owner screenshot, 2026-11-07. **Fixed:** one rule, `ShownAddress`. A direct provider link shows its site alone in every window, the list, Properties, the CLI and Add Links, and the JSON export leaves it out. The app has no copy command of its own, so a box copies only what it shows. Logs and recent addresses were already safe, and a test checks them. Reported: the Address box in the completion window shows the whole direct link, including its `token`, which is the account key. The progress window masks it as `token=***`. Mask it everywhere a link is shown or copied, and search every other surface: logs, Properties, the clipboard, exports, the address history. |
| TB.F13 | Download in My TorBox should close the window | done, tests for the rule; not seen on screen | Owner request, 2026-11-07. Once a download starts from My TorBox, close the window. Keep it open on a refusal or error. Opening the downloaded location and showing progress move elsewhere, for example Progress Details. **Done:** it closes once the Host has the download on its way, and otherwise stays with the reason. Show progress and Open downloaded location are removed. The package row's Progress details has both. |
| TB.F14 | "Local downloads created" but nothing on the list | pending; owner screenshot, 2026-11-07 | After a cached multi-file magnet the box said "Local downloads created", but All Downloads was empty and the status bar stayed at "Adding to TorBox...". Check after TB.F10, which removes the box, that the rows really appear. |
| TB.F15 | Import Torrent File said "The file picker couldn't open" | fixed, not yet seen on screen; owner report, 2026-11-07 | The owner's sandbox ran as administrator, and the older `Windows.Storage.Pickers` pickers don't open in an elevated app. Every file and folder picker now goes through `Pickers.cs`, which uses the Windows App SDK pickers. The owner's check in an elevated window is the acceptance evidence. |
| TB.S1 | Start or remove an item waiting in TorBox's queue | suggestion; reference for suggestions, not a parity target; owner to judge | Seen live: TorBox did not start queued items by itself in 14 to 16 minutes with slots free, and `queued/controlqueued` with `start` or `delete` worked. The app has no command for either, removing such a row from the provider is refused, and the row reads "Waiting for TorBox", the same as waiting for metadata. JDownloader only waits too (read from its code). Proposal: give a queued row its own words and offer Start now and Remove from TorBox's queue. |
| TB.S2 | Offer a link's hoster password to its own archive set | suggestion; reference for suggestions, not a parity target; owner decision, as the plan keeps the two apart | JDownloader sends a hoster link password on create and also tries it on the archives from that link (read from its code). This app sends it on create only. Proposal, if wanted: offer it for that link's own set only, once, with the user's consent, never as a global list or a guess. Not tried live: no password-protected hoster link was available. |
| TB.S3 | Remove `.rev` recovery volumes with their archive set | suggestion; reference for suggestions, not a parity target | JDownloader deletes a split archive's `.rev` volumes with the set when archive deletion is on (read from its code). This app's archive code has no `.rev` handling, so they would stay behind. Not tried. Accept when a set with `.rev` files and deletion on leaves none, and one with deletion off keeps them. |

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
  notifications/gating verified; final UI audit remains. Other browsers are
  explicitly out of scope (C3).
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
| A1 | Provider abstraction + account manager | partial, mock only (branch `feature/torbox-integration`, not merged) | Phase 7 | `IFileStorageProvider` was replaced by small capability interfaces in `Core/Providers/RemoteProviders.cs`, looked up through `RemoteProviderRegistry`. Key per provider through `RemoteAccountStore` (DPAPI, own entropy, `remote/account-<provider>.json`, never in settings, an unreadable key is its own state), write-only over `remote.account.*`. **Evidence:** `HostPipeRemoteTests` (real Host process over its pipe), `RemoteAccountStoreTests`, `RemoteProviderNeutralityTests` (scenarios A and C through a fake second provider with no Runtime change). **Not verified:** nothing against the real provider in this entry; no Options UI yet (Phase 8) |
| A2 | TorBox client | partial, mock only (branch `feature/torbox-integration`, not merged) | A1 | `Providers.TorBox/TorBoxClient`: key validation, plan and expiry, listing by kind with paging, direct-link resolution, cache check, submit, queue, hosters, remove, and error mapping. The API proxy follows the download proxy. Composed by `Host/RemoteProviderSetup`; `DOWNLOADMANAGER_TORBOX_API` points a sandbox profile at a mock, and `TorBoxApiAddress` allows only this computer or the real host. CLI `dm remote ...`. Messages in `docs/ipc-protocol.md`. **Evidence:** `tests/DownloadManager.TorBox.Tests` (stub handler, mock over HTTP, `TorBoxApiAddressTests`), `HostPipeRemoteTests`. Read-only live calls shaped the mapping (`notes-local`), but **no acceptance scenario has been verified live**: the mock is still inferred for pending states and `queued/getqueued`. Search is not implemented |
| A1.C | JD container import (Host side) | partial, mock only (branch `feature/torbox-integration`, not merged) | A1, A2 | `Runtime/Remote/ContainerImportService` reads `.rsdf` and `.ccf` through `DownloadManager.Containers`, classifies each link, holds links in memory against a preview id, and adds chosen entries through `RemoteJobCoordinator.AddAsync`. `remote.container.preview`, `remote.container.add`, `remote.container.sendToJd`, `remote.archivePassword`, `dm remote container`, `dm remote container-send-jd`, `dm remote archive-password`. `.dlc` gets a labelled "not supported" and an explicit hand-off to a running JDownloader. **Evidence:** `HostPipeContainerTests` (real Host, real HTTP to the mock, the bundled 7-Zip on Rar.exe-built archives), `RuntimeRemoteContainerTests`. **Not verified:** no real `.rsdf` or `.ccf` sample exists, so the readers are only proven against fixtures made from the same format notes; the JDownloader route has never run against a real JDownloader; nothing live; no WinUI entry yet (Phase 8) |
| A3 | Provider browse/add UI + CLI | deferred | A2 | Browse/search account files and queue selected items; expired direct links refresh once; CLI equivalent of the UI flow |
| A4 | Archive extraction | partial, not merged (branch `feature/torbox-integration`) | A2 | `Archives/SevenZipExtractor` behind `IArchiveExtractor` (UnRAR until A4.B), driven by `Runtime/Archives/ArchiveCoordinator`; traversal-safe paths, progress, cleanup policy. Provider downloads reach it the same way ordinary ones do, and a job's archive password reaches it through `IPackagePasswordKeeper`. **Evidence:** `RuntimeArchiveTests`, `RuntimeArchiveHostTests`, and scenarios G and H through the provider in `HostPipeContainerTests` (real Host, real engine against the mock CDN, the bundled 7-Zip, archives built by Rar.exe), `ReviewRegressionTests` and `RuntimeArchiveReviewTests` for the review's findings: only what the tool read can be deleted, a volume flag beats a numbered name, nothing in a staging folder is trusted, output is flushed before archives go, and a download whose archive went stays completed across a restart. **Not verified:** nothing against the real provider; compressed RAR4; a real power cut; no WinUI entry yet (Phase 8), which also has to show `statusNote` |
| A4.B | Bundle 7-Zip and drop UnRAR | implemented on the branch, not seen on screen; owner decision, 2026-11-07 | A4 | The owner first chose UnRAR, then on learning it opens only RAR decided: bundle 7-Zip, remove UnRAR and the program-path setting completely, and use "the best possible extractor for most popular files". 7-Zip 26.04 x64 (`third_party/7-Zip`, unmodified, hashes in `SOURCE.md`) is copied to `tools\7-Zip` next to the Host, CLI and tests, and `scripts\publish.ps1` refuses an output without it. `SevenZipExtractor` replaces `UnrarExtractor`; the UnRAR path setting, `--unrar-path` and the Options picker are gone. Passwords go on stdin as UTF-8 (`-sccUTF-8`), never on the command line. NTFS streams in an archive are no longer written (`-sns-`), and the download's own zone mark is copied to the files. LGPL notice in `docs/extraction.md` and the Options page. **Evidence:** Archives tests 291 (RAR4/RAR5, encrypted names, wrong and damaged, volumes, links, crash and cancel, plus 7z, zip, tar, split 7z and quoted passwords in `OtherFormatTests`), 149 archive integration tests through a real Host. Detection of other formats: A4.C. |
| A4.C | Recognise 7z, zip, tar and split .001 sets | implemented on the branch, 2026-11-07 | A4.B | Automatic, manual and package extraction now take single 7z, zip, tar and compressed tars (.tar.gz/.tgz, .tar.bz2, .tar.xz, .tar.zst), and split `name.{7z,zip,rar,tar}.001` sets (`ArchiveVolumes` scheme `Split`, `ArchiveNames`). A compressed tar is unpacked in two layers, and the inner tar is listed and checked like any archive before its files are written. A split set that 7-Zip finds cut short waits for the next piece. The Extract menu rule and the Extract archive picker take the same names. **Evidence:** Archives tests 309 (grouping, tar.gz/tgz/tar.bz2/tar.xz, a plain .gz, a split set missing its last piece), Core 818, `RuntimeArchiveTests.ADownloadedZipExtractsByItselfAndOtherFilesAreLeftAlone` through a real Host. **Not done:** spanned zip (`name.zip` + `name.z01`), no tool here writes one to test with; .mkv.001-style split files are not joined. |
| A4.D | Spanned zip sets (`name.zip` with `name.z01`, `name.z02` ...) | open, owner asked to note it, 2026-11-07 | A4.C | Not recognised yet: only `name.zip.001`-style splits are. Neither 7-Zip nor anything else on this PC writes a spanned zip, so there is nothing to test with. Needs a test set made by a tool that writes them (for example Info-ZIP `zip -s` or WinZip), then the volume grouping (`.z01`... first, `.zip` last, the `.zip` opened) and a check that 7-Zip 26.04 extracts it and reports a missing piece. |
| A5 | Provider expansion | deferred | A1 | Adding a provider requires only a new client + capability declaration; no host/runtime changes |
| D1 | Cross-platform headless host | deferred | A2 | Host runs on Linux without WinForms/WinUI; named-pipe/Unix-socket IPC; XDG state paths; systemd unit; existing runtime tests pass on Linux. A user-provided LAN VM is available for verification; credentials are held outside the repository |
| D2 | Web UI | deferred | D1 | Folder choosing must not rely on a native picker: a Host-side folder browser limited to the mounted volumes and favorites (UI.R9, UI.R10), because another download manager's Docker browse was the user's main complaint. Local web app covering downloads, queues, schedules, settings, and provider accounts; authenticated remote access; no WinUI dependency |
| D3 | Docker packaging | deferred | D1/D2 | Dockerfile + compose with state/download volumes, non-root user, healthcheck, host and web UI deployed together |
| D4 | End-to-end provider/Docker test | deferred | A4/D3 | TorBox link/list → queue → download → unrar through the Linux container and web UI, including expiring-link refresh; image/logs contain no token |
| D5 | Linux port | far goal; owner, 2026-11-07 | D1 | Run the app on Linux. D1 to D4 cover a headless host, web UI and Docker. WinUI 3 and the tray are Windows-only, so whether a port also needs a native Linux desktop frontend is undecided |

## Cross-cutting tasks

| ID | Task | Status | Acceptance |
| --- | --- | --- | --- |
| X.1 | Keep `CLAUDE.md` current for agents | in_progress | Reflects structure, commands, status, traps |
| X.2 | Keep feature evidence current | in_progress | No status without evidence |
| X.3 | Keep PROGRESS.md handoff current | in_progress | Next action + commit + commands |
| X.4 | Commit coherent increments | in_progress | Each milestone increment pushed |
| X.5 | Independent verification of critical recovery/handoff | pending | E94 records main-session recovery results; independent review/reproduction of recovery and handoff remains outstanding |
