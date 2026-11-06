# Progress — session handoff


### Open for the owner (recorded 2026-11-06, for later)

- **Chrome Web Store:** upload `artifacts\chrome-store\inbound-bridge-0.4.0-chrome.zip` as a draft and send the Item ID. The app then accepts that ID, 0.3.3 is built and published on request, and the listing is submitted only after 0.3.3 is out. See ROADMAP EXT.S2.
- **0.3.3 is not released.** The auto-update fix, the slim speed bar and the clean-up are on `main` but in no release yet.
- **The owner's installed app is still broken:** `app\` is empty. Run Setup 0.3.2 or wait for 0.3.3, with RustDesk and WinRAR closed.
- **Old commits on GitHub:** history is rewritten, but GitHub can keep serving old commits by direct link until its support purges them. A purge request is the owner's to send.
- **Settings has two similar checkboxes** now the media option is gone: "Allow automatic sending" and "Automatically send downloads to the desktop app". Consider merging them.
- **Store review fallback:** if review stalls over all-site access, removing the all-pages content script is an option. That script holds the Delete-key gesture and Download All's link collection, so both would need another route.
- **Cookies:** clear them when a download finishes, rather than when it is removed.
- **Small known issues:** a failed retry can delete a kept rollback copy; a postponed tray retry is dropped; graph labels clip at 200% text size; a package row's Size shows received bytes until all sizes are known.
- **Check whether these are stale:** ROADMAP UI.R3 and UI.R4, clean-machine install testing, independent recovery review.
- **TorBox:** work is on the `feature/torbox-integration` branch, not merged. Its plan is `docs/plans/torbox-integration.md`.

### TorBox: review fixes (2026-11-06, branch `feature/torbox-integration`, not committed)

- Durability: a finished file survives a failed rename, a stop that lands just before it, and a kill just after it (`DownloadCheckpoint.FinalizingTo`, cleared once the job is saved). Part files are flushed before each checkpoint, which now comes every 5 s rather than every 1 s. Removal is durable (`DownloadJob.RemovalPending`) and commits package and statistics in one save.
- Dead links: a 200 that can't be the promised file keeps saved progress and counts as `ExpiredUrl`, so a provider job gets a fresh link. A 206 with another total still discards.
- Verification: new `FailureKind.VerificationFailed`, never retried by a queue. A hash the user typed keeps the bytes; provider md5, size or hash deletes them.
- Secrets: API `requestdl` links count as provider links, old jobs and `RecentUrls` are migrated at load, scheduler sync no longer stores a redirected link, `UrlRedactor` handles nested addresses, more names, `;`, bare pairs and magnets, provider text loses addresses. The extension no longer receives settings or `groups.changed`.
- Packages: `remote:` keys are refused from clients. A package built by single adds waits 10 s of quiet before completing (`PackageSettleDelay`); `AddRemoteJobsAsync` is the once-per-batch path.
- Evidence: `RuntimeReviewRegressionTests`, `EngineRecoveryTests`, `ProviderLinkTests`.
- Not verified: the part-file flush has no power-loss test and its cost on a slow disk was not measured. The WinUI duplicate check and Redownload messages were built, not run. Matching a URL against the stored account key waits for key storage (phase 2). A pasted provider link has no promised size, so a dead link's 200 can still reset it.

### TorBox phase 1: package gap check (2026-11-06, branch `feature/torbox-integration`, not committed)

- Package row now has Resume, Stop, Cancel Package (with confirmation), Add to Queue, Remove from Queue and Priority, in the context menu; Resume, Stop and Cancel on the toolbar; Pause, Resume and Cancel in the package window; `dm package ...` and `dm priority` in the CLI. All go through `group.action`. The Q column shows the shared queue or `*`.
- Rule (`PackageRules.ResumeTargets`): parent Resume leaves cancelled files alone, except it undoes a parent Cancel that stopped every file. Parent Cancel keeps partial data.
- Membership: re-adds repair a package lost between job save and package save; a failed package save is logged and the next add saves it. `RemoveAsync` only dissolves the removed job's own package.
- `AddJobRequest` and `JobUpdateRequest` carry `Priority` (High, Normal, Low).
- Member limit: an add that would pass 1000 fails before any job is created, rather than starting a second package, so one provider item never has two sets of controls or two completions.
- `PackageCompleted` is raised once per completion, saved first so a restart does not repeat it. `GetCompletedPackages` is the startup sweep. Nothing in the Host subscribes yet.
- `groups.changed` is coalesced over 100 ms and no longer builds a full snapshot per change.
- Evidence: `RuntimePackageTests`, `PackageRulesTests`. Undoing 14 of these changes one at a time made their tests fail (queue actions and sizes were not checked this way).
- Not verified: the WinUI changes were built but not looked at on screen, in either theme. CLI commands and the pipe coalescing have no automated test. `AddJobAsync` is still one job and up to two settings writes per link for Add Links; only the provider batch path has one commit.

### Repository clean-up and media panel removal (2026-11-06)

- Removed from the extension at the owner's decision: media detection, in-page panel, popup card, Settings option, playlist fetching and badge. The extension now makes no network requests of its own. Right-click on links, images, video and audio, Download All, automatic sending and the Delete gesture remain. The app still downloads stream addresses it is given.
- Also removed: a Settings box that did nothing, and timer re-arming on unrelated settings changes. Removing a download now also removes its stream side files. The delete dialog no longer claims partial data is removed when completed files are involved.
- Reference material moved out of the repository and kept on the owner's PC. History rewritten to match.
- Evidence: Release build, Core, Engine, Integration and extension suites; store images re-rendered from the current build; the zip's contents loaded in real Edge.
- Not verified on screen: the About text, Options page and delete dialog changes were built and tested, but not looked at in the running app.

### Chrome Web Store package and listing (2026-11-06)

- Ready for the owner's upload; not uploaded. Zip: `scripts\package-extension-store.ps1`. Dashboard text, images and the order of steps: `docs/chrome-store/`. Privacy policy: `docs/chrome-extension-privacy.md`, which must be pushed before submitting. See ROADMAP EXT.S2.
- The store assigns a new ID, because the private key for the current one is not available. The app must accept that ID before the listing can work. That is not done yet: it needs the Item ID from the draft upload.
- Extension 0.4.0: automatic sending is off until turned on in Settings, which opens on first install and explains what it shares. Host permissions narrowed to http and https. Incognito not allowed. YouTube refused on every path. Content scripts limited to three messages, failing closed. Delete-key listeners attach only while automatic sending is on. Stream playlists get cookies only from the page's own site, and not through a redirect. Excluded sites also cover the original address and referrer. A missing app is explained in plain words with a link.
- Evidence: extension typecheck and tests; the zip's contents loaded in real Edge (worker runs, Settings opens on install, offline popup and Settings captured). Policy and listing claims were checked sentence by sentence against the code, and that check caught two false statements, both fixed in code.
- Not verified: a real store upload; the extension talking to the app under a store-assigned ID. Accepted limits: a form POST cannot be detected, so it can fail in the app once accepted, and this is disclosed. Cookies stay with a finished download until it is removed, also disclosed. Clearing them on completion is an open owner decision.
- The icon is the owner's own AI-generated artwork. The media panel was removed from the extension at the owner's decision.
- Known, not changed: a package row's Size shows bytes received until every part's size is known.

### Speed graph bar (2026-11-06)

- The stock Expander (about 210 px, with an animation that reserved the space first and left a blank band) is replaced by a 23 px bar styled like the status bar, with an 85 px plot. The panel's real height animates, so the list moves with it.
- Evidence: frame captures before and after show the blank band gone; a click during the animation turns round smoothly, with no step over 11 px, and ten of ten interrupted runs ended exactly closed; period menu; theme switched while running, open and closed; a killed window remembers the collapsed state.
- SaveUiState now updates the one state object, so it no longer wipes expanded packages.

### Auto-update: second round (2026-11-06)

- Two independent reviews found more, all fixed: an in-place rollback could stop on a log file created at runtime and leave a mixed install; a stale success was announced; about 390 MB per update was never deleted; the first hop from a 0.3.2 Host looped silently on failure, because that Host cannot read the result (the script now skips the version and shows a dialog); a Host started during the helper's copy could empty the stage folder; the clean step missed the self-contained build. Refresh Download Address sent a site's saved login to whatever site the job was pointed at; it now drops them when the site changes.
- Evidence: real 0.3.2 Host to the new script, both success and crash (one attempt, version skipped, dialog, no loop); scripted case for the runtime log collision.
- Known narrow, not changed: a failed retry can later delete a kept rollback copy; a tray retry postponed by active downloads is dropped; plot labels clip at 200% text size.
- At the time of this commit, the final full run and a last review of the second round were still in progress.

### Auto-update: no more broken installs or silent failures (2026-11-06)

- Owner report: auto-update sometimes fails silently and leaves a broken install. Reproduced 2/2 on
  this PC's exact state. Root cause: a program opened from the app (RustDesk, WinRAR here) inherits
  current directory = `app\`, and Windows then refuses to rename or remove that folder, though the
  files inside can still be moved. The old script deleted the contents, failed on the empty folder,
  then failed its rollback the same way. Nothing read `install-result.json`, so it was silent, and
  every Host start retried. A DLL-release race was also tested and disproved.
- `apply-update.ps1` rewritten: new files are staged beside the old ones and swapped by rename, with
  an item-by-item fallback (undone exactly) when the folder is held. Each rollback step runs whatever
  became of the one before. "Succeeded" is written last, after the new version has survived starting;
  a crash on start rolls back and restarts the old version. A damaged install gets a dialog naming
  where the old version is.
- Host, windows and shortcuts no longer use `app\` as current directory, so nothing new can hold it.
- `UpdateInstallJournal` (new): the next Host reports the outcome once, in the log, tray and update
  window, including a helper that died without a result. A version that failed is not retried
  automatically for 24 h (the tray still offers it). A helper that exits at once now keeps the Host up
  instead of closing the app with nothing to bring it back.
- Update zips were framework-dependent (91 files, against Setup's 626) and only worked because this PC
  has the runtimes. `package.ps1` now publishes self-contained, and `prepare-release.ps1` requires the
  runtime files in the zip. This affects every release so far.
- Only a window that was really visible comes back after an update: not the update's own progress
  window, and not a main window closed to the tray. The released script reopened the main window for
  either, so background updates popped it up.
- Evidence (sandbox install and data, real Host and WinUI, signed local feed, a program holding
  `app\`): update with window open succeeds, window returns, "Updated to 0.3.2." logged; new version
  crashing on start rolls back to a running old version, logs the failure and "Not retrying", no loop;
  dead helper leaves the Host running with the install untouched; window closed to the tray stays
  closed. Nine scripted cases plus a blocked rollback (`app\` stays new, extension goes back, error
  says so). Release build 0 warnings; Core 107, Engine 57, Integration 114 passed.
- Review input: a subagent read-through found the rollback-failure wording, success announced before
  the start check, and the Host-still-exiting restart, all fixed here.
- Not verified: the tray notification and failure dialog on screen (the log and result file are);
  the path where the old Host takes over 120 s to exit (read, not run); Setup repairing an install
  whose `app\` is held. Not handled: logoff or a kill in the instant between the two renames leaves
  `app\` missing, and Setup is the repair.

### Package names, package delete and sparse part files (2026-11-06)

- Name probe on add no longer waits for a per-host connection slot, so a running segmented download
  to the same CDN no longer times out the names of the rest of a batch. Live: three TorBox CDN links
  resolved to data1.doi, data2.doi and Setup.exe in about 1 s each.
- Unnamed batch packages are named by the Host from the resolved file names (archive name, else the
  largest shared beginning, else the first file) and follow later members until the user renames
  them (`DownloadGroup.AutoNamed`). Live: "data". Test `UnnamedBatchPackageFollowsMemberFileNames`.
- Deleting a package (or several downloads) is one Host operation, `RemoveManyAsync` (`JobIds` on
  `JobRemove`): all members stop together and the scheduler cannot start a member being removed.
  CLI `remove` accepts several ids; `add --package KEY [--package-name NAME]`.
- Root cause of the slow stop: segmented part files were pre-sized non-sparse, so NTFS zero-filled
  everything before a far segment's first write (measured 28.5 s for a write at 18 GB; a 58.7 GB
  download took about 105 s to stop). Part files are now marked sparse before sizing. Live: package
  delete while downloading at 110 MB/s took 0.2 s. Test
  `RemovingSeveralRunningDownloadsStopsThemTogetherAndStartsNoneOfThem`.
- Validation: Release build 0 warnings; Core 107, Engine 57, Integration 98 passed.

### Main window, extension review and Edge flyout (2026-11-05)

- 2026-11-06: repository consolidated to public `Kaan88/inbound` (source + releases). `credentials.txt` purged from all history before publishing; the old private `Kaan88/download-manager` and builds-only `Kaan88/download-manager-builds` were deleted at owner request. 0.2.0 rebuilt with the new feed URL and released at https://github.com/Kaan88/inbound/releases/tag/v0.2.0; anonymous `releases/latest/download/manifest.json` returns 0.2.0. Saved settings on the retired feed migrate to the new one (test `SavedRetiredUpdateFeedMovesToCurrentFeed`). Installs of 0.1.5 or older still poll the deleted feed and need the 0.2.0 installer once.
- Rename to Inbound Download Manager across installer, assembly product, dialogs, CLI, tray and scripts; old "Download Manager" shortcuts removed on upgrade/repair/uninstall (a81c699, 6d5a128).
- Main window: menus regrouped and normalized, context menu grouped, duplicate Stop All / identical Pause All removed, light-theme icon tokens, status bar split (57d6228). Speed-graph expander direction restored to Up per design guidelines after an accidental flip (83e0b93).
- Extension: popup host probe, readable states, pause-catching toggle, handoff retry after rejection, media-scan starvation fix (57d6228).
- Edge flyout flash: interception moved to `downloads.onDeterminingFilename`, replacing `setUiOptions` (b56ae50). Extension tests 35/35. Owner Edge check pending; not accepted.
- Progress window: segment rows updated in place (fixes jumping bars seen in the owner's recording), window grows to fit all segments and restores on collapse (b56ae50). Live check pending.

### Consolidated validation started (2026-11-05)

- Full Release solution build succeeds with zero warnings/errors after correcting accumulated Host/UI compile errors. Separate validation output: artifacts/validation-bin; build log: artifacts/consolidated-build.log. This permits the earlier QA app to remain open without file-lock rebuild failures.
- Core88/88 passes (artifacts/consolidated-core-pass2.log), including new group aggregate semantic cases. Engine57/57 passes (artifacts/consolidated-engine-pass2.log), including gated live telemetry, request-context/payload hash, cancellation and response/handler cleanup cases. Integration91/91 passes (artifacts/consolidated-integration-pass2.log), including group/statistics restart, startup/overnight/periodic-unchanged/retry and queue detach cases. Extension34/34 plus typecheck/build passes (artifacts/consolidated-extension.log).
- First integration pass was85/86: an old test expected a Later job to ignore an explicit queue start. Updated the test to the implemented explicit-queue-start behavior, retaining pre-start non-admission and payload hash checks. The official scheduler workflow supports this change; this was not a skipped assertion or an unexplained flaky rerun.
- Isolated QA profile artifacts/qa-015/state, instance qa-015, starts via direct process creation without shell unsigned-app prompts. Current open QA processes were started as Host2268 and WinUI7616; refresh actual process/window state before using those IDs. The app is displaying local fixtures only; updates/autoresume are disabled. Installed app state was not changed. Main light view and bottom graph rendered; Windows file-type icons visible. Statistics rendered, and live inspection exposed a pre-existing reconciliation defect: missing part files erased received bytes even for completed files. Completed reconciliation now preserves/recovers final-file bytes; a retained-job restart assertion passes in the updated integration suite.
- The open QA UI is the earlier build (before that reconciliation fix); corrected validation binaries are separate. User activity was detected in the QA window, so further UI input was stopped while regression work continued. Full dark/light/DPI and window/workflow acceptance is still incomplete. A unified Scheduler window and additional per-download completion/fault-path regressions are being prepared; do not package or claim it complete yet.

Batched 0.1.5 scheduler/queue source (2026-11-05): startup triggers, timed/overnight windows, periodic HTTP file synchronization, queue retry limits and actual queue detachment added. Periodic sync compares validators and stages changed/unverifiable downloads while keeping the old final file until atomic replacement. Previous completed versions are retained in statistics without double-counting the current version. Retry processing is explicit for queue-managed downloads, runs at30-second intervals, respects stop/cancel and blocks premature completion actions. Startup-only failures retain pending eligibility. Explicit queue detachment keeps jobs/files and excludes them from queue pause/concurrency/quota/sync policy. Source only; consolidated validation pending. Network troubleshooting made no system changes; external test downloads remain deferred while the owner's throughput problem is unresolved.

### Completion actions, HTTP telemetry and statistics — batched source (2026-11-05)

- Per-download completion options are editable in Properties before completion. Program defaults may be inherited, disabled or replaced with an executable; the saved file is passed as one argument without shell parsing. Open-file and explicit exit/shutdown/restart/logoff choices are persisted. System actions require global opt-in and no other unfinished downloads. The execution marker is saved before side effects; restart does not replay them. Per-download system choices override queue system actions while retaining independent queue open-file behavior and avoiding duplicate opens. Luna reviewed source; main corrected that precedence.
- Actual HTTP response streams are instrumented at all five engine send sites (direct/range redirects and media/key/manifest requests). The wrapper preserves response headers and streaming/cancellation, measures returned read bytes and a short rolling rate, and removes entries on failure/disposal. Progress details distinguish active HTTP streams from checkpointed byte ranges and explicitly distinguish HTTP/2 streams from physical sockets. Only host/port/protocol/status/ranges/counters are exposed; no URL paths, query tokens or credentials. Luna source review found no concrete wrapping/disposal defect. Consolidated engine and live checks remain required.
- Statistics now combine current jobs with minimal archived records (ID/state/bytes/dates only). Removal saves its archive before deleting the job; archive records whose IDs are still live are excluded, preventing double counts across interruption. Host-owned archive state survives stale Options saves. View → Statistics shows recorded/completed/today bytes, completion/failure/cancellation counts, current activity and speed. UI states that these are saved-progress records, not network-interface usage; previously deleted history cannot be reconstructed. The archive intentionally retains historical records rather than relying on the visible list.
- Source inspection found that background PersistAsync logs and swallows write failures. Added an explicit required-save path for queue membership, category/completion edits and completion execution markers; failed edits restore in-memory choices and actions do not execute without a saved marker. Job deletion now waits for the runner to settle, is ordered against queued persistence, and retains the in-memory job on durable-delete failure. Scheduler/quota/update-defer settings writes now also coordinate with the shared mutation gate to avoid overwriting Host-owned groups/history.
- Follow-up source fixes: property and queue membership edits hold persistence admission through mutation/save/rollback, and queued background saves take a fresh snapshot after admission, preventing obsolete writes from undoing committed edits/markers. Resume admission coordinates with removal, and completion side effects skip jobs cancelled or being removed. Statistics archive and current-activity semantics were independently reviewed by Luna; no concrete calculation/persistence defect was found. These changes still require the consolidated fault/concurrency tests.
- No builds/tests were run for these source increments, following the owner's batched implementation instruction. Consolidated validation must cover body streaming/resume/hash/cancellation, marker write failures and one-time side effects via fake executors, queue/category write failures, stats removal/restart/interruption/stale settings and all new UI layouts/theme/DPI. Scheduler timing/retries and full live WinUI review remain open;0.1.5 is not packaged or published.

Batched 0.1.5 source (2026-11-05): per-download completion options and real HTTP response-stream telemetry added; no build/test run. Properties stages program inheritance/override/disable, open-file and gated system actions; execution marker is persisted before side effects. HTTP telemetry measures actual response reads across direct/range/media/probe requests and exposes only host/port, protocol/status/range/bytes/rate. Statistics added to scope: archived minimal job records are written before removal and deduplicated against current jobs for persistent byte/count totals, with a View → Statistics window integrated in source. Consolidated validation remains pending.

### Groups and bottom speed graph — batched source, not yet validated (2026-11-05)

- Group membership and definitions share one atomic settings write. General Options preserves Host-owned groups; create/add/detach/rename/ungroup are explicit IPC actions. Each download belongs to at most one group; moving membership does not change its queue or file location. Parent pause/resume/cancel acts on a membership snapshot and reports per-child failures; cancel retains files and skips terminal children. Group controls release the settings mutation gate before waiting for transfers to settle.
- Removing a download first saves its detached group membership under the same mutation gate, then removes the independent job. Failed group persistence retains the job. An interruption between these operations leaves an ordinary ungrouped download. Luna reviewed persistence; main fixed removal coordination and terminal-cancel handling from that review.
- Download-only speed graph follows the supplied qBittorrent screenshot's history/grid/period structure using original WinUI drawing code. Keeps at most one hour of session samples; unknown/stale/offline data creates gaps. Downsampling retains peaks/troughs; visibility and expansion are saved. Luna source review found a reconnect/snapshot freshness edge, fixed by withholding samples until a fresh snapshot. No qBittorrent code/assets copied.
- Main reviewed Luna's groups window, fixed stale child-action capture, polling error lifetime, responsive parent/child rows, collapsed creation editor and DPI-aware initial sizing bounded to the work area. All are source changes awaiting the single consolidated validation pass.
- Consolidated acceptance must cover restart and stale Options saves, moving/detaching children, partial child-control failure, atomic-write failure and interruption, unknown-total aggregation, independent child details, graph gaps/periods/theme/DPI and responsive window layout. No builds or tests were run for this batch. Published release remains0.1.4.

Batched 0.1.5 source (2026-11-05): persistent independent download groups now have Host-owned definitions/membership, atomic settings commits and group controls. Tasks and selected-download context actions expose group creation/assignment; expandable management window is integrated in source, with aggregate progress/controls visible while collapsed and independent child controls. Bottom download-only speed graph added from owner reference 33528189-2dc8223e-d865-11e7-9d2a-2b5102afb5af.png: selectable 1/5/30/60-minute session history, scale/grid, current/average/peak, disconnected/stale gaps, persisted visibility/collapse. Source-only; consolidated build/tests and live DPI/theme/workflow acceptance pending. FTP remains out of scope.

Batched 0.1.5 source (2026-11-05): explicit unused-category deletion in Options and populated queue deletion with download/schedule reassignment added; source-only, consolidated verification pending. Queue moves save before old queue removal, and failed persistence restores in-memory membership for retry. Settings/category/job admission and queue membership edits share a mutation gate. No build/test run for this batch.

Owner scope update (2026-11-05): FTP work is out of scope; existing backend support is retained, but no additional FTP UI or acceptance work is required.

Batched 0.1.5 implementation: Download Properties now supports assigning existing downloads to built-in/custom categories or automatic extension classification. Host validates category identity, persists the choice, and does not relocate payloads. DTO/view model preserve automatic versus explicit classification. Added multi-selection Change category context action with per-file failure reporting. Luna read-only review found no issue in the Properties/Host reassignment path; main inspected the changes. Consolidated verification pending; no build/test run for this increment.

### Event sounds added to implementation batch (verification deferred)

- Added persisted EventSoundFiles settings and mappings, Options Sounds editor with local WAV picker and disabled/system/file modes, plus Host event playback for completed/failed/queue-finished. Legacy completion toggle remains authoritative; previously silent failure/queue events remain silent until explicitly configured. Inline validation keeps Options open on invalid WAV selections. Queue-finished playback is deduplicated per queue cycle.
- Luna implemented isolated SoundOptionsEditor and CompletionSoundPlayer helpers; parent integrated model/DTO/persistence/runtime settings/WinUI/Host event plumbing. Sound playback is asynchronous with file size/playback bounds, no process execution, and owned SoundPlayer disposal. Reviewed helper ownership: disposal cancels playback and defers CTS/semaphore disposal until active calls exit. Parent added inline picker-failure handling and a narrow-window layout.
- No build or test was started for this increment, following the owner's request. Consolidated verification must include settings roundtrip, legacy defaults, WAV picker cancellation/errors, event selection, simultaneous events, shutdown disposal and live sound playback. This is source work, not accepted behavior.

### Batched implementation workflow and metadata preview (0.1.5 source)

- Owner requested implementing the remaining features as a batch, then consolidated verification, to conserve tokens. Do not start new feature-by-feature test/build cycles. Tests already running before this instruction may finish; new final-review fixes are pending consolidated verification.
- Added Host-owned HTTP metadata preview and Add URL filename/size/type display, asynchronous automatic check and refresh button. HEAD falls back to a one-byte Range GET only for405/501, reads headers without consuming response bodies, and creates no jobs/payload/history. Uses the existing engine proxy, saved login, redirect credential boundaries, target validation and host limiter. Runtime allows two previews with12s deadlines; UI uses a separate IPC connection, so slow preview does not hold the normal command stream. It preserves user filename edits, recalculates automatic category folders from suggested names, invalidates responses after credential changes/closure, and allows Start/Later without waiting.
- Closing the dedicated preview pipe now cancels Host work through a one-message lookahead. Pipelined messages are retained for next dispatch. Review found exceptional lookahead faults and operation-first faults needing observation; source corrected to cancel/observe both paths. These last corrections are not yet revalidated per the new batching instruction.
- Before the workflow change: new metadata engine cases6/6 and real-HTTP metadata integration2/2 passed; Core83/83 and Host/WinUI builds passed. Already-started full engine suite53/53 passed. The already-started full integration run also completed84/84 (artifacts/integration-metadata.log,1m46s), before final cancellation-review and sound changes. Do not extrapolate these results to final untested fixes or live GUI acceptance.
- Protocol.HostVersion now derives from Contracts assembly version (0.1.5) instead of stale0.1.3; protocol stays1. Luna compiled/reflected that narrow change before the new testing instruction. Public installer remains0.1.4; no new release published.

### Persistent categories and Add URL selection (0.1.5 source, 2026-11-05)

- Added stable custom category definitions (ID/name/extensions/optional destination), persisted job membership and explicit-versus-automatic classification. Legacy jobs without membership keep built-in filename classification. New automatic jobs use custom rules before built-ins; explicit choices survive filename changes. Automatic membership updates after Move/Rename. Renaming definitions preserves IDs and files.
- Add URL selects Automatic/built-in/custom categories, initializes category folders, supports one-off path override, and creates saved categories using the chosen destination and extension rules. Creation is explicitly immediate even if the download is cancelled. Start/Later wait for an in-flight create. Remember-folder applies to the explicitly selected category; browser captures still use the configured default.
- Sidebar shows custom categories and Other. Options Save to edits names/extensions/folders. CLI --category, export/import and redownload preserve membership. General settings saves cannot remove existing custom definitions; deletion/reassignment UI remains open. Settings updates are serialized so concurrent category creation rejects stale settings instead of losing definitions.
- Focused category/browser checks7/7 and Core79/79 pass: real payload hash, classification, explicit override, remember, rename, restart, invalid/ambiguous rules and stale/concurrent saves. Runtime/CLI/WinUI builds passed. Luna review found GUID casing and missing Other navigation; corrected both. Removal-race concern was checked against current source: all existing definitions are retained, and serialized settings saves reject stale lists. Custom sidebar selection restores after definitions arrive. Full integration82/82 passes in1m46s (artifacts/integration-categories.log). Live GUI/layout acceptance remains open; not published.
- Test exploration confirmed manual downloads retain their URL filename rather than adopting Content-Disposition at start. Metadata preview/server-name handling remains open; category coverage does not verify it.

### Windows Shell file icons (0.1.5 source, 2026-11-05)

- Implemented original Windows Shell icon provider for main download rows, queue file rows and individual progress/completion headers. Pending files use registered file-type icons without opening a partial payload; completed files request the actual local file icon, including embedded application icons. Missing files fall back to their registered type; existing font glyph remains on errors/loading.
- Native calls run off the UI thread under a two-worker gate with COM initialized. Owned HICON/DC/DIB handles are released in finally blocks; dual-background rendering reconstructs premultiplied BGRA for alpha and legacy mask icons. No file is executed to inspect its icon. Cache holds at most256 tasks, expires completed entries after one minute, and distinguishes completed-job revisions. Overflow requests still load under the worker gate. Job view models reject stale async completions after path/state/name changes.
- Native integration checks pass2/2: associated/unknown/extensionless types; actual cmd.exe differs from generic executable type; missing actual path falls back; output transparency/premultiplied bounds;100 repeated lookups do not grow GDI/USER handle counts beyond tolerance. WinUI Release x64 builds with zero warnings/errors; scoped whitespace check passes. These checks do not establish visual/DPI acceptance, cache-burst runtime acceptance or every third-party shell handler's behavior. Not yet packaged/published. Published latest remains0.1.4. Current app/installer/install/package/extension version fields advanced consistently to0.1.5; extension rebuilt at0.1.5 with34/34 tests and typecheck passing.
- Luna reviewed ownership/concurrency/UI key guards. Fixed the identified full-in-flight-cache permanent fallback and clarified the COM apartment behavior. Broader WinUI and lifecycle acceptance remains open.

### Queue file details and priority-aware ordering (2026-11-05)

- Post-0.1.4 source: Queues → Files now shows file name, size, status and time remaining using existing job display formatting, with filename tooltip and inherited application theme. The dialog explicitly labels the values as an opening-time snapshot. Move controls cannot cross priority boundaries, matching the scheduler rather than offering a reorder it would ignore.
- WinUI Release x64 build passes with zero warnings/errors; scoped diff whitespace check passes. Live layout/DPI acceptance remains open. Luna read-only review confirmed populated queue deletion still needs coordinated Host reassignment; composing individual moves in the UI is not sufficient. No deletion behavior was changed.
- Owner requested Windows file/application icons. Main-list executable-specific icon behavior still requires targeted live checking; no icon implementation claimed.

### Right-click handoff on a protected link (2026-11-05)

- Keep direct right-click handoff as normal behavior. Browser-assisted preparation may be an optional improvement for protected/intermediate links. Earlier automatic-job evidence already established the resolved CDN URL differs.
- Source cookie store/frame/partition corrections remain valid independently and tested; do not describe them as a proven testfile.org fix. Broader project work and outstanding live acceptance remain open.

### Manual authentication and protected-site right-click investigation (2026-11-05)

- Post-0.1.4 source: Add URL now exposes optional per-download HTTP Basic user/password controls with masked input and inline validation on Start and Later. It does not add a saved site login. JsonJobStore protects Authorization, Proxy-Authorization and Cookie values in detached snapshots with a separate per-user DPAPI entropy; live engine headers remain unchanged. Both load paths restore usable values and omit undecryptable protected values. Legacy plaintext headers still load and become protected on next save. Read DTOs/export snapshots contain neither plaintext nor protected headers.
- New integration coverage proves a per-download authenticated transfer survives restart with matching SHA256, no saved SiteLogins entry, no plaintext headers in job JSON/snapshot, legacy rewrite and damaged-ciphertext handling. Integration suite 76/76 passes (artifacts/integration-manual-auth.log, 1m46s); redirect credential-boundary engine checks 2/2 pass. Runtime/WinUI builds cleanly. Live Add URL acceptance remains open; not in public 0.1.4.
- Owner reported right-click 403 at public https://testfile.org/files-5GB while automatic capture works. Read-only redacted installed job evidence establishes automatic jobs already carried final CDN URL https://cdn.hotelnearmedanta.com/testfile.org/testfile.org-5GB.dat; right-click jobs had no resolved final URL. Both sources carried cookies and an Edge user agent. This supports browser redirect/preparation as the key difference; do not claim exact Cloudflare cause or full fix yet. Direct web fetch of the protected URL returns403; existing browser renders the public home page and exposes that download link. No payload was started and no real cookie values were printed/copied.
- Additional concrete right-click defects fixed in source: use clicked frame URL as referrer; retain source tab's cookie store and exact frame partition metadata; read fresh unpartitioned plus matching partition cookies at confirmation. Never fall back to a different profile when a known tab's store is unavailable. Preserve same-name cookies at distinct paths in browser path order. No cross-site partition enumeration. Official Chrome cookies API (getAllCookieStores/getPartitionKey/partitionKey) and Cloudflare cookie docs consulted. These corrections do not establish that the protected redirect is now resolved.
- Extension tests 34/34 plus typecheck/build pass, including complete right-click-to-native source-frame/partition flow, partition exclusion, private-store isolation, fresh-at-confirmation cookies and older-browser behavior. Do not make browser-initiated resolution the unconditional right-click path. Keep direct handoff as the normal path; investigate optional browser-assisted preparation for protected/intermediate links, with explicit correlation and duplicate prevention if needed.

### Queue file-order implementation (post-0.1.4 source, 2026-11-05)

- Added Host-owned JobOrder to persisted queues and queue snapshots. Queue action reorderfiles accepts an exact, duplicate-free list of the current queue's job IDs; stale/missing/foreign IDs are rejected without changing order. General Options updates preserve the existing order instead of trusting a stale client snapshot.
- Scheduler uses the saved order among equal-priority waiting jobs, with creation time as fallback for new/unordered jobs; it does not cancel or restart active transfers. Order lookup is precomputed for dispatch instead of repeatedly scanning file lists.
- Queues → Files exposes a selectable filename list, Move up/Move down, Save order and Cancel. Source and WinUI Release x64 compile cleanly. Published 0.1.4 does not contain this source increment. Live UI acceptance remains open.
- QueueFileOrderControlsSchedulingSurvivesRestartAndRejectsStaleLists passes: reverse order persists across service restart, real serial transfers start in that order, malformed/stale/foreign lists are rejected, and a stale general settings save does not reset the order. Core suite passes 79/79. Full integration suite passes 74/74 in 1m45s (artifacts/integration-queue-file-order.log); Host also builds cleanly. The combined scheduler layout and remove-from-queue affordance still require live UI review, so this does not establish full scheduler acceptance.

### 0.1.4 published; Site Logins editor source increment (2026-11-05)

- Public release: https://github.com/Kaan88/download-manager-builds/releases/tag/v0.1.4. Five uploaded assets verified via GitHub; anonymous versioned and cache-busted latest manifests both report 0.1.4. Signature verifies against the unchanged client trust anchor. Installer 75,511,846 bytes SHA256 a4b60cbf9bc8cbdffb82ef27c259b9ef54a194ebe439e815f9df6b88220da4ea; ZIP 112,277,537 bytes SHA256 e2208a6901ed18e38bbbc73a056f8acf66ed6b641980d2d11c3a5d8d6994fd0a. Host and UI assemblies in ZIP both 0.1.4.0. Source repository and private signing key were not published.
- Strengthened helper fixture starts the child with native working directory inside app, and success actually removes/recreates app. Helper explicitly changes both PowerShell location and native Environment.CurrentDirectory. Additional persistent file-lock fixture verifies old metadata is restored even when file rollback fails. All four helper cases pass: artifacts/update-helper-tests-793c27f3ed88464c8aafe127fbf9e602. Partial restart cleanup stops only processes created by that helper before rollback. Installed upgrade/restart/progress acceptance remains open; no user installation changed.
- Post-release source work (NOT included in published 0.1.4): Options now has Site logins tab with add/edit/remove rows, masked password input, saved-password preservation, and inline validation retaining the dialog on incomplete rows. Changing saved identity requires password re-entry to avoid silently losing the credential. Changes remain local until Save; Cancel does not persist them. Host remains DPAPI owner and read DTOs never return passwords. WinUI Release x64 builds cleanly; existing authenticated-transfer/DPAPI/restart integration workflow passes. Live editor acceptance remains pending.

### Updater failure repair in progress (2026-11-05)

- Owner's reported About mismatch confirmed as a failed replacement, not just a label bug: installed Host/UI assemblies remained 0.1.2 while install.json said 0.1.3. Result recorded installer exit 1 and rollback failure because app directory was in use. Installed files/processes were inspected read-only; no user installation changed.
- Helper now leaves the application working directory (runtime also starts it in staging), releases only this installation's native bridges after Host exit, and restores metadata independently before file rollback. Installer writes version metadata after file replacement/native registration. Reopening uses direct process creation and restores the desktop UI when a frontend was present.
- InstalledBuild clamps an ahead-of-binary version record to the actual Host assembly version; focused regression passes. About now derives its version from its running assembly. Source versions advanced consistently to 0.1.4; this version is not yet packaged/published.
- Update installation downloads the same verified manifest selected by its check. Throttled byte progress is available in snapshot/events and a separate compact WinUI progress window; failed/busy results stop its progress state. Host and WinUI compile with zero warnings/errors. Live progress/restart and installed repair acceptance remain unverified.
- Signed updater/concurrency integration tests 6/6 pass. Isolated helper success/missing-installer/partial-failure rollback fixtures pass; fixture artifacts: artifacts/update-helper-tests-48c02caa9f544da698daa31c8d03b967.
- Luna agents hit usage limits. Main verified their folder-picker work, fixed the test mock to dispatch the dedicated native-port reply, and ran extension tests 29/29 plus typecheck/build successfully. No live browser acceptance claimed.
## Current state (2026-11-04)

### Queue editor increment (2026-11-05)

- Fresh latest-download redirect now resolves to0.1.3 (cache-busted anonymous fetch); initial stale0.1.2 response above was propagation/cache lag. Direct0.1.3 release assets verified.

-0.1.3 published: https://github.com/Kaan88/download-manager-builds/releases/tag/v0.1.3. Five asset sizes verified; versioned anonymous manifest signature verifies with unchanged release trust anchor. Setup75,489,064bytes; signedZIP112,268,895bytes SHA256`eb6dfae7b3b4eba3fb98abf26ecbe9e9a8e2ad5218715dc1670c303a39c2ebd3`. Initial unqualified latest redirect still returned cached0.1.2 immediately after upload; versioned0.1.3 is verified, latest propagation being checked. Reload unpacked extension after installation to replace running background worker. Browser live acceptance and installed automatic update remain unproven.
- Main reviewed extension Browse helper and both handoff/batch bindings: cancellation unchanged, only confirmed path applied, errors inline, Browse disabled while awaiting. Main ran25extension tests/typecheck/build successfully (including picker sender rejection/nativepathforwarding; furtherUIhelpercancel tests still being added). Host/WinUI/self-contained installer builds pass. Helper closes only target-install resident frontends before waiting for Host, preventing older reconnect loops from racing upgrade; strengthened isolated harness passes outsideCIM-restricted sandbox, with explicit error-stage assertions. No installed app was changed by agent tests.

- Preparing0.1.3 for owner tray-exit/popup/destination-picker feedback; version fields bumped consistently by Luna, historical releases unchanged. Folderpicker frontend task re-dispatched with followup_task after discovering queued send_message to completed agent had not started a new turn; actual native/background implementation was already landed. Do not claim extension Browse controls complete until source/tests are inspected.
- Legacy folder migration hardened on dm.saveSettings as well as load: a stale Options tab cannot reintroduce defaultDestination after extension reload. Chrome typecheck passes; regression assertion queued with folderpicker tests.

- Full integration now73/73 passes (1m43s; `artifacts/integration-update-admission-fixed.log`). Deterministic timeout was introduced by the cancellation regression fixture: /slow writes its first64KiB before throttling, so an8KiB response could complete client-side while its server handler still delayed, poisoning reuse of that HTTP/1 connection. Enlarging to128KiB ensures an incomplete response on cancellation; focused/new checks and fullsuite now pass. No production timeout increase or skipped test.
- Tray-exit fix refined: HostConnection never launches the Host; only explicit App.OnLaunched does, including secondary activation. This covers exit before first handshake as well as resident connected UIs. UI reconnects to a Host started by user/native handoff/updatehelper. WinUI compile will be rerun before packaging.
- Native extension folder picker uses a dedicated native messaging port, so its long modal interaction cannot block unrelated handoffs on the normal sequential IPC connection. Only extension handoff/batch pages may request it. UI wiring/tests in progress; Host compile and Chrome typecheck pass.

- Owner reported tray Exit relaunching Host: confirmed resident WinUI reconnect loop called EnsureStarted every iteration. Unreleased fix bootstraps until first valid connection, then reconnects without spawning Host; explicit application launch still calls EnsureStarted even for an already resident frontend. EOF disconnect now updates offline status. Host/WinUI Release builds pass; live exit/reopen acceptance pending. Updater IPC success now requests shutdown in finally even if the reply fails, preventing staged/quiesced Host deadlock.
- Owner reported toolbar extension popup sizing and missing destination picker. Luna changed popup intrinsic width to390px with bounded height/scroll; 24 extension tests/typecheck/build passed, visual acceptance pending. Main added native host.pickFolder/host.folderPicked contracts, serialized Windows STA FolderBrowserDialog, default app folder starting location, and extension sender validation permitting only handoff/batch pages. Host builds; Luna implementing both destination Browse controls/tests. These changes are unreleased.
- Diagnostic full integration rerun also failed the same update fixture manifest GET timeout:72/73 (log artifacts/integration-update-admission.log). Paired tests pass; Luna investigating order/shared HTTP-client state. Treat full suite as failing, not transient/accepted.

- Unreleased manual Add URL Browse control implemented by Luna, reviewed by main and Release x64 built with 0 warnings/errors; cancellation preserves editable path, errors inline, field has explicit accessible name. Live picker acceptance remains open; screenshot gap matrix updated to distinguish source/build evidence from remaining gaps.
- Runtime update staging rechecks active transfers after package fetch and atomically pauses new runner admission through staging/restart; failure releases admission and reschedules. Expanded signed-update regression verifies malformed signed ZIP fails safely and following download completes. Six focused update tests pass. Full integration run:72/73, one local fixture manifest request timed out after30s. Paired concurrent-cancellation/signed-update rerun passes2/2; full diagnostic rerun running session12371, log `artifacts/integration-update-admission.log`. Do not claim full suite green yet.
- Update periodic/retry timers now use System.Threading.Timer because settings callbacks arrive on IPC worker threads; disposal and retry changes synchronized, disabled preference rechecked after fetch. Host Release build passes. Runtime periodic/retry acceptance still pending; no new release published during owner's0.1.2 test.

- Browser cleanup verification complete at unit/mock level: 24/24 extension tests (six new cancel/erase/search cases), typecheck and build pass; main reviewed tests and reran tests/typecheck. Live Edge flyout reproduction still outstanding. This work is unreleased.
- Busy installed automatic updates now schedule a one-minute retry rather than waiting the normal daily interval; disabling automatic updates cancels pending retry and automatic checks revalidate the preference after manifest fetch. Host Release build passed before the final preference guard; runtime retry/disable acceptance remains open. Naming/original-icon follow-up recorded in ROADMAP per owner request. Luna now implementing the missing manual destination Browse control; no installed UI actions authorized by that subtask.

- Unreleased post-0.1.2 fixes: Runtime serializes all update installations (automatic/tray/IPC) with a shared nonwaiting gate and refuses another helper after successful staging. Six update integration tests pass, including a slow-manifest concurrent request rejected as busy and cancellation releasing the gate. Runtime Release build clean. Further active-download admission races and busy-update retry cadence remain open.
- Update helper now backs up/restores the browser extension alongside app and metadata, with normalized scoped target checks and rejection of linked target directories. `tools/test-update-helper.ps1` invokes the real helper with isolated fake installers and NoStart: missing installer preserves baseline, partial app/extension/metadata replacement failure rolls back all three, success writes expected metadata without executing fake Host. Main reran all three successfully; fixtures at `artifacts/update-helper-tests-493d52796f414063bd13341b46136b30`. These fixes are not in published0.1.2.
- Browser cleanup investigation found cancel/erase failures were ignored after native ACK. New helper retries boundedly, checks terminal state before erase and verifies absence through downloads.search. Background records durable acceptance before cleanup; post-ACK failures cannot resume a duplicate browser transfer. Typecheck passes; additional regression tests in progress. No live Edge flyout fix/acceptance claimed. Only listed Edge windows; no input or installed-app changes during owner update test.

- 0.1.2 publication completed: https://github.com/Kaan88/download-manager-builds/releases/tag/v0.1.2. Anonymous HTTP fetched `releases/latest/download/manifest.json`; RSA signature verified and version is0.1.2. Owner can now install the local older-version test installer and enable Options→Updates automatic checks if previously disabled. No actual user installation/upgrade result has been observed yet.

- Owner approved public builds-only hosting. Created `Kaan88/download-manager-builds`, initialized generic README; verified it public and source repo private. 0.1.1 preview uploaded with four explicit assets. New 0.1.2 sources configure signed feed/public trust anchor, default checks on for new settings, preserve existing false preference, gate periodic monitor on installed metadata, invoke idle-gated installation on automatic check, and request Host shutdown after tray/automatic staging. Five update integration tests pass. Automatic live acceptance/concurrent install races remain open.
- Built self-contained 0.1.2 installer and ZIP; prepare-release script validated real ZIP and emitted five explicit public assets with signed manifest/checksums. 0.1.2 upload started (exec session27893; verify terminal result). Test installer `artifacts/update-test/DownloadManagerUpdateTest-0.1.1.exe` deliberately records installed0.1.1 using current0.1.2 binaries to exercise upgrade to0.1.2; SHA256 `993595A0D67E28D90726FFD6E3D5AD36A31A3D266A47022EB96865194F9816CD`.
- Update helper no longer removes app on preflight failure before replacement; regression with missing install.ps1 preserves original marker and reports failure. It refuses proceeding after Host exit timeout; install.ps1 scopes process checks to target app path, refuses active Host rather than killing all Hosts by name. Backup retained through restart; success recorded after launch. Final helper revision is in signed update ZIP; setup EXE embeds the preceding helper revision (same successful path, earlier success backup cleanup), so future update execution uses the signed ZIP helper. Further lifecycle acceptance remains required.

- Extension media opt-out and legacy-folder cleanup implemented by Luna and verified by main: 18/18 extension tests, typecheck and 0.1.1 build pass. Media scanning defaults off for missing settings; Options toggle stops observers/listeners and clears panel, resources and badges on existing pages. Serialized broadcasts and content generation guard prevent stale enable after disable. Download All link collection remains independent. Extension Options no longer offers a persistent destination override; explicit one-off handoff destination remains covered. Live browser/UI acceptance still open.
- Release signer now requires an existing private key; explicit `--generate-key` uses exclusive creation, and signing output never contains private key material. Release build passes; two fixture manifests verify against one stable public key. Hosting decision pending: origin repository is private, so unauthenticated default update URLs cannot fetch its release assets. Asked owner whether to use a public builds-only repository or authenticated private updates; no repository visibility changes or release publication performed.

- Full integration suite after browser destination routing and queue-name persistence: 72/72 passed (1m43s). GitHub release/update work explicitly added by owner: remote `Kaan88/download-manager`; `gh auth status` succeeds outside restricted network context. Existing installed-build metadata gating and signed-manifest updater need release wiring/acceptance; no new release uploaded yet. Existing UpdateSigner creates a new key per invocation, so release signing must support a stable private key before publishing an update feed.

- Fixed queue rename propagation: changed job names are persisted and emit `updated`; initialization reconciles denormalized names from authoritative queue settings, including a crash between settings/job writes. Runtime Release build has 0 warnings/errors and the expanded rename/order/delete/restart test passes. Luna read-only review found no regression; shared mutable settings saves and failure rollback remain a broader concurrency/durability gap, not closed by this fix. Scheduler run dates intentionally remain preserved by ID, retaining once-per-day suppression after edits.

- Resumed verification: Release Runtime build passes with 0 warnings/errors. Filtered browser-directory/queue integration run passes 11/11, including both browser sources across restart with a conflicting music category destination and a one-off folder choice. Browser requests use the application default; desktop category routing remains separate. Existing xUnit2031 warning remains. Media opt-out/Options migration is still being implemented by Luna; no live Edge ghost-entry fix is claimed. Chrome API documents cancel completion before the returned promise resolves, so a terminal-state race is not established by source inspection alone.

- Compaction handoff: active goal remains finishing all remaining in-scope functionality/acceptance before TorBox/Docker/WebUI, without dropping quality/features. Version 0.1.1 bumped consistently. Queue create/policy/delete/move actions and scheduler edit UI are in the worktree; live acceptance pending. Latest queued regressions passed 8/8; ongoing agents own further tests and UI/media work.
- New browser requirements: persistent old extension destination must migrate back to application default; disable page media scanning/bubble via explicit setting; investigate Edge ghost download/flyout after native handoff. Main added clearing legacy `settings.defaultDestination` in extension loadSettings and browser Runtime destination routing now skips both last-folder and category-folder fallbacks (explicit one-off destination still wins). These latest changes are not yet tested. `/root/review_filename` owns media opt-out + extension Options cleanup/tests; `/root/extension_review` owns queue/scheduler WinUI work (latest build passed). Main must verify their results, run directory regression with a conflicting category folder, and investigate Edge ghost entry without weakening acknowledged-before-cancel handoff safety. No new 0.1.1 package installed.

- Queue editor now calls Host-owned create/policy actions rather than replacing all settings. Scheduler editing and existing completion-action controls compile cleanly; no live GUI acceptance yet. Settings replacement now rejects queue sets that orphan existing downloads or schedule queue references. Main is verifying queue regressions and scheduler persistence; move/delete UI wiring continues.

- Host queue actions now include `moveup`/`movedown` (persistent queue order) and safe empty-queue deletion. Default queues and queues referenced by jobs or schedules are rejected without deleting downloads; populated-queue reassignment/deletion workflow remains unfinished. Runtime builds cleanly; focused tests and UI wiring are in progress.

- Current product/extension/build metadata consistently bumped to 0.1.1; CLI build and extension typecheck passed. Historical artifact filenames/hashes and test fixture versions are unchanged.
- Host-owned queue creation now allocates identifiers under the runtime lock and creates stopped queues without replacing full settings; shares policy validation with queue edits. Runtime compiled; editor migration, focused creation tests and scheduler editing continue. No 0.1.1 installer or live GUI acceptance is claimed yet.

- Added Host-owned `queue.action` policy updates for name, concurrency and quota limits, preserving live usage/window timestamps instead of accepting those counters from the editor. Backend compilation passed; integration coverage and editor migration are in progress. Owner requested incremented versioning; the next review build is being bumped from 0.1.0 to 0.1.1 consistently, without rewriting historical evidence.

- Added New queue and per-queue Edit controls in WinUI. Editor supports name, simultaneous downloads (1–16), optional quota (up to 10 TiB) and quota window (1–8760 hours), with validation that retains input. New queues start stopped. Existing IDs, order, running state, quota consumption and window start are preserved. Settings are refreshed again after closing the editor before applying edits.
- Release WinUI build passed with zero warnings/errors. `QueueSettingsCreateEditAndPersistWithoutChangingOtherQueues` passed after restart, including unchanged second queue and quota usage. The initial test used a Nov4 quota window that legitimately expired on Nov5; changed the fixture to a current window and reran successfully. No live GUI acceptance or new installer is claimed for this increment.
- Queue deletion/file ordering and scheduler editing remain pending. Full-settings saves still have a narrow concurrent-update race after refresh; atomic queue-specific policy updates are a follow-up before broad concurrent acceptance.

### Host access investigation and live visual check (2026-11-04, blocker cleared)

- Owner exited the elevated installed app. Directly launched normal-privilege workspace Host with `DOWNLOADMANAGER_DATA=artifacts/progress-smoke/data`; IPC immediately connected and confirmed an empty isolated list. The earlier Host-access blocker is cleared. Installed jobs were not changed.
- Live fixture `14e70bde-6a42-4489-94d1-83ce5e861db9`: automatically opened the per-job UI, paused at 6,422,528 bytes, changed its cap from 128 to 64 KB/s via the UI, resumed, and completed. A 4.108 s interval transferred 262,144 bytes (~63,802 B/s); reported rate ~65,536 B/s. Global Don't show again saved false for ShowCompletionDialog while leaving the completion window open; restored true afterward.
- Visual checking found the status expander header and completion saved-path textbox clipped at the former 500/400 heights. Defaults are now 640×560 for progress and 640×450 for completion. Rebuilt and checked fixture `1850c7d3-c868-4da1-9726-2903933e97ac`: status controls and completion path fit, all four checkpoint rows are accessible by scrolling with footer actions still visible. Per-job completion toggled false/true without changing global true. Cancel confirmation and its Keep downloading branch were observed; actual destructive Cancel is covered by integration tests, not a GUI deletion claim.
- Removed the cap using the UI, resumed, and observed ~980,164 B/s over 2.607 s before completion. Both 8,388,608-byte files matched TestServer's `/large/expected` SHA-256 `ECC3DEC859B2355CBD2C9DA1E6513A6AAB7ADFBAAA7FF31FF3F0DA0F2898B59F`. Evidence: `artifacts/ui-smoke/progress-verified.png`, `segments-verified.png`, `completion-verified.png` (isolated fixture content only).
- Completion Open folder/Open with were invoked, but a corresponding external Explorer/chooser window was not observed; do not mark those shell handoffs newly accepted from this smoke. Cancellation dialog now explicitly inherits the window theme; that final theme assignment is compile-verified, not separately recaptured. CLI now distinguishes access denied from Host absence and preserves protocol mismatch diagnostics; Release CLI build passed with no warnings/errors.
- Closed the fixture window and cleanly shut down only the verified isolated Host after both jobs completed. Owner-wide visual approval, dark/DPI variants, shell handoff acceptance, and the other functional gaps remain open.
- Restored the installed WinUI afterward. It now exposes its full accessibility tree to the helper (no higher-integrity warning), shows Host connected and the original eight failed / zero active entries. The restricted execution CLI still receives access denied from that installed Host's pipe; do not confuse that context limitation with the successful isolated IPC/UI checks or claim every caller privilege combination was verified.
- Final updated installer (built, not installed): `artifacts/visual-final/installer/DownloadManagerSetup-0.1.0.exe`, 75,482,690 bytes, SHA-256 `9F19C315C5B10151262F88992F5108E9794750F42353DBEFCA745434535CDE9B`. Staging is `artifacts/visual-final/installer-app`; logs are `artifacts/ui-smoke/visual-final-publish.log` and `visual-final-installer.log`. A prior staging-root removal hit a directory handle left by a shell-action check; publishing to this fresh root succeeded without killing unrelated shell processes.

- Queried the instance mutex owner directly: current owner is PID 63996 at `%LOCALAPPDATA%/DownloadManager/app/DownloadManager.Host.exe`, not the previous workspace smoke executable. Installed Host, Runtime and WinUI DLL hashes match the review package. Installed state has eight failed jobs and no active jobs; existing jobs were not modified.
- The computer-use helper initially reported higher Windows integrity for the installed WinUI window, explaining its restricted control access. The installed Host's pipe is separately denied to the restricted execution CLI even after the accessible UI restart; elevation versus restricted-token ACL behavior was not independently distinguished for that pipe. The CLI's generic “Host not running” diagnostic concealed access denied. The executable manifest uses `asInvoker`, and no RUNASADMIN compatibility flag or `__COMPAT_LAYER` override was found for Host/UI. No Windows security settings or pipe ACLs were changed.
- Opened the installed main window and requested the owner use Tasks → Exit, since Windows prevents this lower-privilege session from controlling that elevated instance. The owner completed that handoff; the successful isolated smoke above supersedes the blocker. Do not treat the earlier PID 139600 diagnosis as the current owner identity.

### Progress/completion control increment (2026-11-04)

- Added an aggregate per-transfer HTTP rate budget shared by single/range workers. Caps now apply to segmented downloads and can be enabled/changed/removed while running. Waits poll live settings at most every 100 ms and honor cancellation; changing a cap resets accumulated credit. Resume no longer charges the existing partial file against a fresh transfer timer. Limit state uses atomic reads/writes and persists in job metadata.
- Optional `job.update` fields set the per-job cap (0 disables, null leaves unchanged) and completion-window preference. Invalid negative caps, terminal/FTP/HLS/DASH live caps, and combined Move/Rename + transfer-options requests are rejected before mutation. Other jobs and the global completion preference remain unchanged. Host and CLI-completion UI launch honor the per-job preference over the global default.
- DownloadWindow now has Download status/Speed limiter/Options on completion tabs, URL/size/rate/ETA, an honest Yes/No/Unknown HTTP resume capability, Cancel with confirmation, saved-segment counters, and per-job completion selection. Completion has a separate compact summary with address, saved path, exact bytes, Open/Open with/Open folder/Close and global Don't show again. Completion resolves the latest global setting; saving Don't show again keeps the current completion window open. Segment data is explicitly checkpointed, not claimed to be instantaneous connection telemetry. Per-job shutdown/program actions remain outside this increment.
- Owner requested avoiding Windows unsigned-app Run/Cancel prompts. Internal Host launches in frontend/CLI/native bridge now use `UseShellExecute=false` and `CreateNoWindow=true`; test launches use direct process execution. No Windows security setting was changed. Opening a downloaded file still uses its normal shell action.
- Validation: Core 78/78, engine 47/47, integration 69/69. Six new engine cases cover single/segmented initial caps, enabling/removing a cap live with final hashes, and prompt cancellation while waiting on a 1-byte/s cap. Integration covers isolated per-job preferences, invalid requests and restart persistence. Existing tests initially hit the running TestServer.exe apphost lock; reran test builds with already-built dependencies. Pre-existing xUnit analyzer warnings remain; WinUI builds have a NU1900 vulnerability-feed availability warning.
- Live UI transfer acceptance remains open: old smoke Host PID 139600 still owns the instance mutex, its pipe is unreachable, and this session cannot stop it (Access denied). New direct test Host launches exit against that existing mutex; the fixture add did not succeed. Do not claim a live new-window transfer/screenshot from this attempt. The new build is staged separately from the older running artifacts.
- Final cancellation review added an opt-in unfinished-only guard to the cancel request, advertised by `JobDto.CanCancel`. Progress-window cancellation cannot delete a file that completes while its confirmation dialog is open; older Hosts do not advertise this control. New `ProgressCancelAfterCompletionKeepsSavedFile` verifies state and file hash preservation. Focused cancellation/pause/transfer-option checks: 4/4 after this guard; the earlier full integration run was 69/69. Removed-job windows now hide stale completion data and transfer actions. Rebuilt the isolated review package after these fixes.
- Review installer (built, not installed): `artifacts/progress-release/installer/DownloadManagerSetup-0.1.0.exe`, 75,479,855 bytes, SHA-256 `0D5E73865D20FFB922AEB664F54BB6328FF5A64A05C51BABFCA79607276ED620`. Exact self-contained app staging is `artifacts/progress-release/installer-app`; publish/build logs are `artifacts/ui-smoke/progress-release-publish.log` and `progress-release-installer.log`. This is a progress/completion increment, not closure of all review gaps or installer lifecycle acceptance.

- Owner confirmed Dial Up/VPN is out of scope (2026-11-04); it is not an open functional gap.

### Resumed owner review and browser fixes (2026-11-04, in progress)

- Owner requested a review for **functionality as well as UI**. Key gaps: manual auth/category/browse/metadata; progress Cancel/resume capability/live per-job limiter/completion settings; completion Open with/hide-again; site-login and sound editing; queue administration; scheduler editing/retries/startup/completion UI. Distinguish backend functionality missing a frontend from missing backend capability.

- Owner explicitly resumed implementation. This supersedes the deferral below.
- Holding Del to prevent capture while clicking links: actual bypass transfer acceptance remains pending.
- Chrome handoff confirmation no longer overwrites the configured default folder. Runtime browser sources (`chromeAuto`, `chromeContextMenu`) ignore the desktop last-folder fallback and do not overwrite that memory; explicit one-download and category destinations still apply. Integration suite: 68/68, including both browser sources with consecutive requests and service restart. One initial focused run had a transient AccessDenied on atomic settings replacement; focused rerun and full suite passed.
- Extension windows fit content within the current display's available bounds, scroll on short screens, and keep handoff/batch actions sticky. Delete+link capture uses page-side one-shot original-URL gestures, queried before pausing Chrome; this replaced a fire-and-forget registration race found by Luna's mocked review. Browser-chrome pages, unloaded source documents, and iframe initiators are not established shortcut coverage. Extension mock/unit suite 16/16 plus typecheck/build; live Chrome acceptance remains pending.
- WinUI Options reorganized into General/Save to/Downloads/Connection/Proxy/Updates tabs with bounded scroll areas and a working completion-window setting. Add URL uses Address → Download file info with Start download/Download later and advanced queue/conflict choices; list header/body share horizontal scrolling to preserve alignment on narrow windows. Build and live visual review in progress; none of this establishes owner visual approval.
- The supplied ThinkBroadband page explicitly disallows scripted/automated file downloads; inspected the page but repeatable transfer checks use the controlled local TestServer instead. No automated ThinkBroadband payload transfer claimed.

### Owner feedback and deferred review (2026-11-04)

- Additional owner requirements (recorded for follow-up, not implemented or
  verified): **UI.R3**, every new Chrome-extension download should start in the
  configured default folder, not remember the previous download's chosen
  folder; **UI.R4**, resize/reflow extension windows so all items and actions fit
  the available screen without clipping (scroll when necessary); **UI.R5**,
  holding Delete while initiating a download should bypass capture and leave
  the transfer in the browser.
  Interpret "pressing down delete" as holding the Delete key, not deleting a
  download/list item. Do not claim existing temporary bypass already meets this gesture.
- **Visual acceptance blocker:** owner describes the current WinUI as basic
  and disorganized. When the owner has time, carry out a complete live review
  and use it to reorganize the WinUI. Cover
  all main screens/dialogs, control grouping, hierarchy, spacing/density,
  theme consistency and window behavior. Existing inventory and functional checks do
  not satisfy this acceptance gate. Tracked as ROADMAP UI.R1; wait to coordinate
  the review with the owner instead of starting desktop work now.
- **Minor Edge issue, take note:** starting a download from Edge tends to open
  its Downloads flyout. Owner's screenshot shows that flyout and a canceled
  testfile.org download. Cause is unverified; investigate browser behavior and
  handoff before proposing a remedy (UI.R2). Do not change browser settings or
  infer a new Edge-integration commitment from this report. Do not copy the
  screenshot's unrelated download history into project records.

### Latest user-test follow-up (supersedes the older fixes-in-progress handoff)

- Engine now starts direct HTTP requests at the browser-supplied `FinalUrl`
  when present, while binding cookies/Authorization/referrer to the original
  URL origin. Invalid targets fail closed; the existing local-network policy
  is also checked at the final URL and each redirect. Eight new cases cover
  single/8-connection output hashes, same/cross-origin credentials, malformed
  and unsupported targets, and disabled local destinations. Engine **41/41**.
  This does not bypass Cloudflare or establish that testfile.org works.
- Reviewed Luna's filename helper; added encoded-separator sanitization and a
  regression. Extension **7/7**, typecheck and build pass. Real GitHub browser
  acceptance against the new installer remains pending.
- Added `DownloadWindow`: a separate per-job frontend (`--job <guid>`), with
  live stats/overall progress, checkpoint connection details, pause/resume,
  hide/close, completion Open file/Open folder actions and reconnect handling.
  The Host launches it for interactive jobs; CLI stays headless unless the
  main UI is already open and completion dialogs are enabled. A per-job named
  event prevents duplicate windows. Queue members open when transferring,
  rather than opening a burst of windows for every queued event. Main-window
  Progress details launches this frontend; the old modal dialogs are removed.
- Smoke test used only workspace fixture state under `artifacts/ui-smoke`.
  The first shell-based launch waited and reported cancellation; internal
  frontend launch now uses direct executable startup. Observed automatic live
  progress, a pause at 73.4% with Resume enabled, checkpoint range progress,
  and a completed 4.00 MB window with Open file/Open folder/Close. Saved paused
  evidence: `artifacts/ui-smoke/progress-paused.jpg`. Final packaged compact completion layout was then visually checked and saved
  as `artifacts/ui-smoke/completion-final.jpg` (connection details hidden).
  Final isolated job `03a6c321-95f2-4c31-9d86-570646b5cedc` completed
  4,194,304 bytes; SHA-256
  `7a48f6942cc7d8917782ab387b6cd6574b117d5890c4f119f30deb85c72e6aa3`. Several fixture jobs were used; one was left
  paused in the earlier smoke data and the test Host was shut down cleanly.
- Earlier tray left-click/balloon click, dedicated settings activation and
  light-theme resource changes are built. **Live tray/settings/light-mode
  acceptance remains pending.** Corrected the pre-existing extra U+00C2 before middle-dot status-bar
  separators. Luna reviewed the routine diff/encoding changes; review is not
  acceptance evidence.
- Computer-use `@oai/sky` works for observation and the new app controls.
  Do not rerun the old PowerShell UI helper in the
  same turn as the computer-use skill.
- Validation: Release solution build succeeds; core **78/78**, engine **41/41**,
  integration **66/66** (185 .NET tests). The restricted test/solution commands
  exited without useful diagnostics; normal approved execution passed. NuGet
  vulnerability lookup was unavailable during one build; no compiler errors.
- Final self-contained installer build log:
  `artifacts/ui-smoke/final-installer-build.log`. Package:
  `artifacts/installer/DownloadManagerSetup-0.1.0.exe` (72.0 MB), SHA-256
  `4AF50CB652B8B09C124FF4B4DA3295A8CF40B9F1E9C878CD601B3D86458176FA`.
  This package has not been installed/upgraded as acceptance evidence.
  A final smoke Host/UI is running from `artifacts/installer-app`, with
  `DOWNLOADMANAGER_DATA=artifacts/ui-smoke/final-data` and one completed fixture
  job. This is test state, not the installed user job list.


**Goal:** Build a private download manager. The
goal is **not complete**; Phase 0–5 are substantially implemented, later phases
remain open.

- Remote: `https://github.com/Kaan88/download-manager` (verified **private**).
- Commits: `0f3a294` baseline engine/host/IPC/CLI/native bridge,
  `a957f70` Chrome extension, `2143b6e` WinUI frontend,
  `eb03ef4` segmented transfers, packaging scripts, browser-handoff evidence,
  and updated onboarding.
- Tracking fallback: Goal/Todo tools are unavailable in this OpenCode 2.2.5
  environment. `GOAL.md`, `ROADMAP.md`, and this file are the
  authoritative records. Do not claim a Goal/Todo update.

## What works now

| Area | State |
| --- | --- |
| Single-stream HTTP/HTTPS | Working; hashes verified; redirects, unknown length, safe names, truncation, cancellation |
| Durable jobs/settings | Atomic JSON stores; restart reconciliation; request-claim dedupe |
| Resume | Byte checkpoints, Range/Content-Range validation, changed-content restart, ignored-range restart |
| Parallel transfers | Fresh downloads split into 2–N ranges (≥1 MiB), RandomAccess assembly, per-segment checkpoints/retries, no-range fallback, segmented resume verified |
| Host lifecycle | One per-user Host, tray, hide-on-close, named-pipe IPC, snapshot/events, CLI control |
| WinUI frontend | Dark shell: Tasks/File/Downloads/View/Help menu, large toolbar, category sidebar, eight-column list; Add URL and Options dialogs; filtering/sorting; single-instance activation |
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
  `artifacts\app\DownloadManager.NativeHost.exe`
  (development publish), allowed origin only the pinned extension.

## Queue evidence (2026-11-04)

- `RuntimeServiceTests` now has three queue acceptance tests
  (E9). The suite is **9/9 passed**.
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

Our engine writes all ranges at their final offsets into a single
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

## Authenticated browser handoff evidence (2026-11-04)

- TestServer `/auth/login` sets an HttpOnly `dmsession` cookie and links to
  `/auth/file/secure.bin?size=2097152&seed=123`; that endpoint requires the
  cookie, a Referer starting with `/auth/login`, and a `Chrome/` user-agent.
- Engine test `SendsCookieRefererAndUserAgentToAuthenticatedEndpoint` downloads
  the protected file, asserts the expected hash, checks auth stats, and checks
  that the stats response never exposes the cookie value. Engine suite:
  **15/15 passed**.
- Live Chrome for Testing 156 (extension
  `pknddmneejadnnngffojnoecojhdfalc`) clicked the login page link. Chrome's
  `downloads.search({})` was empty after takeover; host job `b0dc0ea4...` was
  `chromeAuto`, `completed`, 2,097,152 bytes; SHA-256 matched seed 123:
  `e0816c5b74d3960a47028f600af178dda4fe932f80a02417d8ced54f6101d52b`.
- `/auth/stats` before the negative probe: `authorized=10, denied=0,
  lastCookiePresent=true`, last Referer
  `http://127.0.0.1:6411/auth/login?size=2097152&seed=123`, last UA Chrome/156,
  last Range `bytes=1048576-1310719` (segmented requests all authenticated).
- A request without the cookie returned **403** and `denied=1` with
  `missing or wrong dmsession cookie`.
- Job JSON contained header key `Cookie` and the expected test value; host logs
  contained neither `dmsession` nor the cookie value. The value is redacted from
  all committed records.

## Proxy and credential-boundary evidence (2026-11-04)

- Added dynamic manual/system proxy settings (`ProxySettings`) to the engine and
  runtime settings, exposed via CLI (`proxymode`, `proxyaddress`, `proxyuser`,
  `proxypass`) and IPC.
- TestServer now includes `TestProxyServer`, a local Basic-auth proxy supporting
  absolute-form HTTP forwarding and HTTPS CONNECT. It tracks challenge, denied,
  authorized, and tunnel counts.
- Engine tests (**19/19**):
  - `DownloadsThroughAuthenticatedHttpProxy` — 524,288 bytes, expected hash,
    zero wrong-credential denials.
  - `ProxyRejectsWrongCredentials` — job fails with a proxy denial.
  - `CrossOriginRedirectDropsSensitiveHeaders` — target sees
    `cookie=false, authorization=false, referer=false`.
  - `SameOriginRedirectKeepsSensitiveHeaders` — all three remain true.
- Runtime test (**15/15**): `ProxySettingsApplyToRuntimeDownloads` updates the
  service settings, downloads through the proxy, and verifies the hash.
- Real HTTPS/CONNECT test: published host downloaded the Alpine 3.24.3 virt ISO
  (`70,254,592` bytes) through `127.0.0.1:5965` using 5 authenticated CONNECT
  tunnels (`authorized=5, denied=0, tunnels=5`, last target
  `dl-cdn.alpinelinux.org:443`). SHA-256 matched the official checksum
  `cdc1bc0efc64d8d611bf3733f54868172a545888dfd726f518bf279bcb0ff1af`.
- SOCKS5 (`socks5://host:port` + username/password): added `TestSocks5Server`
  with RFC 1929 auth. Engine tests **21/21**: local HTTP via SOCKS5 with 4
  connections and expected hash; wrong credentials recorded and rejected.
  Runtime **16/16** includes `Socks5SettingsApplyToRuntimeDownloads`. Published
  host downloaded the same Alpine ISO through 4 authenticated SOCKS5
  connections (`authorized=4, denied=0, connects=4`, target
  `dl-cdn.alpinelinux.org:443`), matching the official SHA-256.
- Switching proxy schemes at runtime exposed a stale-handler bug: the client is
  now rebuilt when proxy settings change, so HTTP→SOCKS5 changes apply.
- Proxy password is currently stored in settings JSON; DPAPI protection and a
  proxy settings UI remain open. SOCKS4/4a and UDP ASSOCIATE are untested.

## Expired URL and refresh evidence (2026-11-04)

- TestServer `/expiring/{name}` accepts only the current token; stale tokens
  return **403** with `X-Url-Expired: true`. `/expiring/stats` reports expired,
  served, and Range requests.
- The engine now classifies 403 + `X-Url-Expired` and 410 as
  `expiredUrl`, marks it resumable, and preserves the part file/checkpoint.
- `RefreshUrlAsync` validates the new URL, keeps the existing part file,
  filename, and validators, clears the failure, requeues, and resumes. Invalid
  URLs are rejected without touching the job.
- Integration test **17/17** (`RefreshDownloadAddressResumesAfterExpiredToken`):
  paused after >65,536 bytes, rotated token 1→2, resume failed with
  `expiredUrl`/`IsResumable=true` while partial bytes stayed, rejected
  `not-a-url`, then refreshed to token 2, completed 1,048,576 bytes with the
  expected seed-96 SHA-256, and observed `ExpiringExpired>0` and
  `ExpiringRangeRequests>0` (Range resume, not restart).
- Published CLI/IPC check: paused at 589,824 of 4,194,304 bytes, rotated the
  token, resume failed with `expiredUrl` while keeping 589,824 bytes, then
  `dm refresh <id> <token-2-url>` completed 4,194,304 bytes with SHA-256
  `26f44b54b4514a10a47b40a30a6b383985fbb7a2723882a97f2482ded313c24f`;
  `/expiring/stats` showed `expired=1, served=7, rangeRequests=6`.
- CLI: `dm refresh <job-id> <new-url>`. WinUI row menu:
  **Refresh download address...** with an editable URL dialog. Both use the
  `job.refreshUrl` IPC method.

## Site-login evidence (2026-11-04)

- Site passwords are stored only as per-user Windows DPAPI blobs prefixed
  `dpapi:v1:`; settings readback and `site list` never return the plaintext.
- Engine-side Basic auth is applied for the most specific matching host/port/path;
  it does not persist an `Authorization` header in job JSON.
- TestServer `/site/secure/{name}` requires `siteuser`/test password and reports
  authorized/denied counts.
- Integration suite **18/18**: no-login job failed `unauthorized`; adding a login
  produced settings JSON without plaintext and with a DPAPI blob; a 2 MiB
  two-connection download matched seed-98 SHA-256 with no extra denials; a fresh
  service instance decrypted the login and completed a 256 KiB download matching
  seed-99.
- Published CLI: `site add` → `site list` showed `stored`; settings JSON
  `contains plaintext sitepass: False`, `contains dpapi marker: True`; 2 MiB
  download SHA-256 `81953ebf30a903db33b8538341ff9caf964fe635ae7405eb137fd21c4a469b05`;
  after a host restart a 256 KiB download matched
  `eed4f2827224985e51fedd9ece2ca1716093ac418ad4f7db5d22e0f1e570605f`;
  `/site/stats` ended `authorized=5, denied=0`; `site remove` left an empty list.

## Quotas and completion-action evidence (2026-11-04)

- Queue quotas: `QuotaEnabled/QuotaBytes/QuotaWindowHours/QuotaWindowStart/
  QuotaBytesUsed`. Progress deltas accumulate per queue; reaching the limit
  pauses active jobs, stops the queue, and blocks new starts. A new window
  resets usage. CLI: `dm queue quota <id> <MB> <hours>|off`.
- Completion actions on schedules: open file, exit host, or shutdown
  (shutdown/restart/logoff, optional force). Exit/shutdown require
  `AllowSystemCompletionActions`; they are suppressed while any other download
  is unfinished; exceptions are logged and never fail the job.
- Integration suite **22/22**:
  - `QueueQuotaPausesDownloadsAndBlocksNewStarts` — 512 KiB quota paused a 1 MiB
    job, stopped the queue, left the next job queued; a 25-hour-old window and a
    larger limit allowed both jobs to complete with the expected hash.
  - `CompletionActionsFireAfterQueueFinishes` — exactly one open and one exit
    after the last of two queued jobs completed.
  - `SystemCompletionActionsRequireOptInAndNoActiveDownloads` — exit suppressed
    with opt-in off, suppressed while another queue was active, fired once after
    cancellation.
  - `CompletionActionFailureDoesNotFailTheDownload` — throwing open action left
    the job completed; shutdown dispatched `restart` + force.
- Core `ShutdownCommandTests` verify `/s`, `/r`, `/l`, `/t 30`, and `/f`
  argument construction. Tests use a recording executor; no real shutdown was
  performed.

## Phase 8 closure evidence (2026-11-04)

- `DownloadLaterWaitsForExplicitStart`: a Later job stayed queued and absent
  from disk across queue stop/start until `resume`, then completed with the
  expected hash.
- `ScheduleHandlesRepeatedDstHour`: first 01:30 (−04:00) started the queue; the
  repeated 01:30 (−05:00) did not restart it; the next Sunday did.
- `ScheduleHandlesOvernightStopAcrossDst`: 23:00 start before the spring-forward
  shift stopped at 07:00 after it.
- Sleep prevention: `SleepPreventionFollowsActiveDownloads` and
  `SleepPreventionSettingCanDisableAndReenable` verify acquire → pause release →
  resume acquire → complete release, plus live disable/re-enable. Published host
  logs for an 8 MiB transfer:
  `Sleep prevention active.` then `Sleep prevention released.` after pause.
  `powercfg /requests` requires elevation in this shell, so the OS request was
  confirmed through successful API calls/host logs rather than powercfg output.
- Windows implementation uses a process-wide `PowerCreateRequest` system-required
  request (not thread-affine `SetThreadExecutionState`).

## Media detection evidence (2026-11-04)

- Added a content script that scans `video`, `audio`, `source`, and HLS/DASH
  links, deduplicates absolute HTTP(S) URLs, and reports them to the service
  worker. The worker keeps a per-tab list and sets an action badge; the popup
  lists distinct resources and sends a selection through the existing handoff
  dialog.
- Chrome for Testing 156, `/media-page` with video/audio/HLS link:
  - badge count `3`;
  - media list: `media-video.mp4` (video), `media-audio.mp3` (audio),
    `demo.m3u8` (hls);
  - selecting the video produced a 524,288-byte host download with SHA-256
    `d49a100a8afd43dc7f02b4c2d34b8362b0f00d9250dd75d3ddee7afaeb05b1ce`,
    matching seed 201.
- V1 implemented, V2 partial (popup list; no in-page overlay/quality variants).
  HLS detection exists, but HLS/DASH segment downloading is still pending.

## HLS evidence (2026-11-04)

- Engine detects `.m3u8` targets and downloads HLS playlists:
  - master playlist selects the highest-bandwidth variant;
  - media playlist downloads `#EXTINF` segments sequentially into the
    destination-local part file;
  - concatenated TS output is renamed to `.ts`; fMP4 (`#EXT-X-MAP`) output uses
    `.mp4`.
- Rejections are explicit: encrypted HLS (`#EXT-X-KEY` other than NONE),
  byte-range segments, live playlists without `#EXT-X-ENDLIST`.
- Test assets were generated with ffmpeg 9.0.2: H.264 320×180 + AAC, three 2 s TS
  segments, served by TestServer with master/media playlists and
  `/hls/expected`.
- Integration `HlsMasterAndMediaPlaylistsAssembleSegments` (**28/28 suite**):
  both playlists produced 580,732-byte `.ts` files with SHA-256
  `6a1384036fceb6355c2aae29504a8fa5fd899c642fd47d309685607c0031b946`.
- Published CLI download produced the same hash. ffprobe:
  H.264 320×180, AAC, duration `6.023222` s.
- Limitations: no AES-128, live, byte-range, or cross-restart HLS checkpoint;
  no audio/video muxing.

## DASH evidence (2026-11-04)

- Static DASH MPD support for `SegmentTemplate` with `initialization`, `media`,
  `$RepresentationID$`, `$Bandwidth$`, `$Number%05d$`, and `$Time$`; highest
  bandwidth representation selected.
- Rejects dynamic/live manifests, ContentProtection/DRM, and
  SegmentBase/SegmentList with clear failures.
- Test assets: ffmpeg 9.0.2 video-only MPD, init + three fMP4 chunks, served by
  TestServer with `/dash/expected`.
- Integration `DashManifestDownloadsInitAndMediaSegments` (**29/29 suite**):
  produced 498,366 bytes and matched SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`.
- Published CLI produced the same size/hash; ffprobe reported H.264 320×180,
  duration `6.000000` s. Output is video-only when the MPD has no audio.

## Audio/video muxing evidence (2026-11-04)

- Added `IMediaMuxer` and `FfmpegMediaMuxer` behind an explicit FFmpeg path
  setting (`ffmpegpath` or PATH; optional `DOWNLOADMANAGER_FFMPEG` test hook).
  FFmpeg is invoked with `ArgumentList`, `-c copy`, deterministic `-bitexact`
  flags, a 5-minute timeout, and output validation.
- DASH now selects separate video and audio AdaptationSets, downloads each track
  to a destination-local temp file, muxes to `.mp4`, validates the optional hash,
  and cleans up temp tracks.
- `/dash-av` test assets and a reference `muxed.mp4` (364,519 bytes, SHA-256
  `6074bcc38ce18a4507e84d27173d7d77d9f6931801c7173b437c649b1e5088ab`).
- Integration `DashSeparateAudioVideoMuxesWhenFfmpegAvailable` (**30/30 suite**
  with `DOWNLOADMANAGER_FFMPEG`): output matched the reference size/hash.
- Published CLI with `ffmpegpath` configured produced the same 364,519-byte
  output/hash; ffprobe reported H.264 320×180 + AAC, duration `4.000000` s.
- The test returns early when FFmpeg is absent; the recorded run had FFmpeg
  9.0.2 configured. FFmpeg is not bundled.

## Fragment recovery evidence (2026-11-04)

- Added `MediaCheckpoint`/`IMediaCheckpointStore` with per-job/per-track JSON
  under `media-checkpoints/`. Each checkpoint records the track, manifest
  signature, completed segment count, and completed byte count.
- HLS/DASH assembly loads the checkpoint, verifies the signature and part
  length, truncates any partial fragment, resumes at the next completed
  boundary, and saves after every full segment. Manifest changes invalidate the
  checkpoint and restart safely. DASH A/V tracks checkpoint independently.
- `HlsResumesAtCompletedSegmentAfterInterruption`: `/hls/recovery.m3u8` at
  64 KiB/s paused after >210,000 bytes; after resume `seg0.ts` was requested
  exactly once, a later segment was repeated for its partial bytes, and the
  final file matched the expected SHA-256.
- `DashResumesAtCompletedSegmentAfterInterruption`: `/dash/recovery.mpd` paused
  after >184,000 bytes; `chunk-0-00001.m4s` was requested exactly once, a later
  chunk repeated, and the final file matched the expected SHA-256.
- Integration suite **32/32** with FFmpeg configured. Existing ffprobe checks
  confirm playable HLS/DASH output.

## Expiring media-token evidence (2026-11-04)

- Media checkpoint signatures now use scheme/host/path and intentionally ignore
  query strings, so rotating tokens do not invalidate completed fragments.
- A segment 403 carrying `X-Url-Expired` (or 410) makes the engine refetch the
  playlist/manifest once and resume from the media checkpoint.
- `HlsRefreshesExpiredSegmentTokens`: `/hls-rotating/playlist.m3u8` rotated its
  token after `seg0.ts`; the next segment was rejected with 403 +
  `X-Url-Expired`, the playlist was refetched, and the job completed with the
  expected 580,732-byte SHA-256
  `6a1384036fceb6355c2aae29504a8fa5fd899c642fd47d309685607c0031b946`.
  `rotating-seg0.ts` was requested exactly once.
- `DashRefreshesExpiredSegmentTokens`: `/dash-rotating/manifest.mpd` did the
  same after `chunk-0-00001.m4s` and completed with SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`;
  the first chunk was requested exactly once.
- Integration suite **34/34** with FFmpeg configured.
- Limitation: one automatic refresh per job. A playlist URL that itself expires,
  AES key rotation, and DRM license refresh are not handled.

## Live and DRM evidence (2026-11-04)

- Live behavior is an explicit exclusion: HLS without `#EXT-X-ENDLIST` and DASH
  with `type="dynamic"` fail before any segment/payload work with failure kind
  `liveStream`, non-retryable and non-resumable. The message states that a VOD
  playlist or static MPD is required.
- DRM detection:
  - HLS `#EXT-X-KEY`/`#EXT-X-SESSION-KEY` with `METHOD=SAMPLE-AES`, `skd://`,
    or Apple/Widevine/PlayReady key formats → `drmProtected`.
  - DASH any `ContentProtection` element → `drmProtected`, with scheme IDs in
    the message.
  - AES-128 HLS remains separately unsupported (`InvalidContent`), not labelled
    DRM.
- TestServer endpoints: `/live/playlist.m3u8`, `/live/manifest.mpd`,
  `/drm/hls.m3u8`, `/drm/manifest.mpd`.
- `LiveStreamsAreRejectedExplicitly` and
  `DrmProtectedMediaIsIdentifiedAndNotBypassed`: both cases failed with the
  expected kind/message, were not retryable/resumable, and left no final or part
  file.
- Permanent content failures (`liveStream`, `drmProtected`) now report
  `canResume: false` and `isTerminal: true`; other failed jobs remain retryable
  (for example, a 401 after adding a site login).
- Published CLI reproduced all four failures with 0 bytes and no files:
  - `liveStream` for `/live/playlist.m3u8` and `/live/manifest.mpd`;
  - `drmProtected` for `/drm/hls.m3u8` and `/drm/manifest.mpd`.
- No key request, license exchange, decryption, or bypass is implemented.
- Integration suite **36/36** with FFmpeg configured.

## Download All evidence (G1, 2026-11-04)

- The content script collects unique HTTP(S) URLs from anchors, images, and
  media sources; it resolves relative URLs, strips fragments, excludes
  mailto/javascript/hash links, and reports name, kind, extension, and host.
- The service worker exposes `dm.pageLinks` / `dm.confirmBatch` and opens a
  Download All window. The window lists links with type/host, filters by name,
  type, host, or URL, supports per-link and select-all-visible selection, and
  sends the selected URLs as authenticated batch handoffs.
- TestServer `/download-all-page` contained: A, B, duplicate A, `mailto:`,
  `javascript:`, `#hash`, an image, and an HLS link.
- Chrome for Testing 156 with the extension collected exactly 4 unique links
  (A, B, HLS, image); the duplicate and non-downloadable schemes were excluded.
- UI: 4 rows with `BIN`/`M3U8`/`PNG` labels; filtering `all-a` showed 1 row;
  selecting A and B displayed `2 selected`, enabled Download, and reported
  `Added 2 downloads.`
- Host: `all-a.bin` 262,144 bytes SHA-256
  `af96c694df7c1c1d8ec660ce45083576aea5c4a619ae6336a02124f428526d48`
  (seed 210) and `all-b.bin` 131,072 bytes SHA-256
  `96e8343a81a63c25ee1871936870371b385f8ac045d17937a09d0b1a65ec5e39`
  (seed 211), both matching.
- G2–G4 remain: crawl scope/depth, relative path/link rewriting, preview,
  persistent/scheduled projects.

## In-page panel and quality evidence (V2, 2026-11-04)

- The content script injects a shadow-DOM panel (`#dm-media-panel-host`) with a
  `Media (N)` badge and a list of detected resources, each with a Download
  action. The popup additionally lists distinct resources and HLS master
  variants with quality labels.
- Background HLS enrichment fetches master playlists (4 s timeout, 5 min cache)
  and parses `BANDWIDTH`, `RESOLUTION`, and `NAME`; variants are sorted by
  bandwidth.
- TestServer `/hls/multi.m3u8` advertises:
  - `90p`: 160×90, 250 kbps, `media-low.m3u8`;
  - `180p`: 320×180, 800 kbps, `media.m3u8`.
- Chrome for Testing 156, `/media-page`:
  - panel existed with badge `Media (3)` and rows video/audio/HLS (panel was
    visibly open in the first run);
  - popup rows: video, audio, HLS, `180p`, `90p`.
- Selecting `90p` queued `media-low.m3u8`; host completed `v2-low.ts`:
  - 279,368 bytes, equal to the concatenated low segments;
  - SHA-256 `e72c04e62c10eb2e926dda45f1af03b3fcc7693c34e6a7c2cda9641b9dea079e`
    matched;
  - ffprobe: H.264 160×90 + AAC.
- Remaining media work: additional DASH modes, HLS alternate audio, AES-128.

## Clipboard monitoring evidence (M9, 2026-11-04)

- `ClipboardUrlExtractor` finds the first absolute HTTP(S) URL in arbitrary
  clipboard text and trims trailing punctuation; it rejects non-URL text,
  `ftp://`, and `javascript:`.
- The Host polls the clipboard on its STA UI thread every 600 ms only when
  `ClipboardMonitoring` is enabled, suppresses the same URL for one minute, and
  adds a `clipboardMonitor` job through the normal durable pipeline.
- Core `ClipboardUrlExtractorTests` (8 cases) pass; core suite **25/25**.
- Published Host live check:
  - enabled monitoring, copied
    `http://127.0.0.1:6411/file/clip.bin?size=131072&seed=220`;
  - Host log: `Clipboard URL detected`;
  - job completed 131,072 bytes with SHA-256
    `7fd83e6ce6d28fc1854455a80d41dff387c913cf661602d14dc24064fd9868dc`;
  - disabled monitoring, copied `clip2.bin`; no job and no file appeared.
- Completion notifications and sound now honor `NotifyOnCompletion` and
  `PlayCompletionSound`; failures still notify. `ShowCompletionDialog` remains
  unimplemented.

## Batch URL evidence (M12, 2026-11-04)

- `BatchUrlExpander` lives in Contracts so CLI and WinUI share it. It expands:
  - numeric ranges, preserving zero padding (`[001-003]`);
  - ascending/descending ranges (`[1-10]`, `[c-a]`);
  - comma lists (`[1,3,5]`);
  - multiple groups as a cartesian product;
  - and rejects malformed/mixed ranges or more than 1000 URLs.
- Core `BatchUrlExpanderTests` (9 cases) pass; core suite **34/34**.
- CLI `add-batch`:
  - `batch-[1-3].bin?...seed=230` added 3 jobs, all completed at 65,536 bytes
    with SHA-256
    `2377642935741989d3a43dfefd0e3f027a48603012f3ab22b5ef92b14275dc89`;
  - a `[1-30]` pattern without `--yes` exited 2 and added nothing.
- WinUI: Tasks → Add batch download was opened through UI Automation; the
  dialog showed URL pattern, range hint, destination, start, and queue fields.
- Remaining: wildcard `*` substitution and batch import files.

## Editable properties evidence (M3, 2026-11-04)

- Added `job.update` / `JobUpdateRequest`; the runtime trims and persists the
  description, raises an update event, and rejects unknown job ids.
- CLI `dm describe <id> <text>` and the WinUI Properties dialog (editable
  Description + Save/Cancel) use the same IPC method.
- Integration `DescriptionEditPersistsAcrossRestart`: set/reset description
  trimming, unknown-id rejection, and reload from a fresh service instance.
- Integration suite **37/37**.
- Published CLI: `describe` returned `ok`; `status --json` showed
  `"description": "Quarterly report from UI"` before and after a Host restart.
- Remaining: editing URL/file name/destination, categories, recent folders,
  import/export, and the completion dialog.

## Duplicate/filename policy evidence (M6, 2026-11-04)

- Fixed the overwrite path: regular, HLS, and DASH-mux completion now pass
  `overwrite: ConflictPolicy.Overwrite`; previously Overwrite resolved to the
  existing path but the move refused to replace it.
- Integration `ConflictPoliciesHandleExistingFile`:
  - pre-created `conflict.bin` with `old content`;
  - `rename` produced `conflict (1).bin` and preserved the original;
  - `fail` was rejected with `already exists`;
  - `overwrite` replaced `conflict.bin` with the downloaded bytes.
  - downloaded content was 65,536 bytes with SHA-256
    `aca6f316de23308f512c95311ebd78be8efbe0e387748272f33f5c73bba28da0`.
- Integration suite **38/38**.
- Published CLI reproduced rename/fail/overwrite; rename preserved the original
  11-byte file, overwrite replaced it, and fail exited 1 with the destination
  error.
- WinUI Add/Batch dialogs expose `If the file exists` with Rename/Overwrite/Fail;
  the single Add dialog prompts Download again/Select existing on duplicate URLs,
  and batch can skip URLs already in the list.
- Remaining: duplicate-URL prompt automation and recent-URL/import work.

## Category and remembered-folder evidence (M7/M8, 2026-11-04)

- Added shared `FileCategories` (compressed/documents/music/programs/video/other)
  used by the runtime, CLI, and WinUI sidebar.
- Settings gained `CategoryFolders`; an add without an explicit destination uses
  the matching category folder, otherwise the last folder/default. Explicit
  destinations override category rules and update `LastDestinationDirectory`.
- Core `FileCategoriesTests` (7 cases) pass; core suite **41/41**.
- Integration `CategoryDestinationRulesAndLastFolderPersist`:
  - music category routed a destination-less `song.mp3` to `<dest>\music`;
  - explicit destination wrote at the root;
  - `LastDestinationDirectory` and the category folder survived a fresh service.
  - integration suite **39/39**.
- Published CLI: `musicdir` routed `song-cli.mp3` to `test-downloads\music`
  (SHA-256 `0c82b8f07b9f9b406dbfaaa3a7d6dc3887692152afd7571d786c16127d6539a2`);
  explicit destination wrote `song-explicit.mp3` at the root (SHA-256
  `02345a83f430ffe736fb5b1cb8325ee088f0762fddc87f64ba389860d87bb772`);
  `settings get` showed `last folder` and the music folder after a Host restart.
- WinUI Options exposes the five category folders; the sidebar now uses the
  shared category mapping.
- Remaining: recent-URL history, category UI polish.

## Export/import evidence (M13, 2026-11-04)

- Added `JobListFile`/`JobListEntry` in Contracts; `JobDto` now exposes its
  conflict policy so exports preserve it.
- Core `JobListFileTests` (3 cases) cover full-field round-trip, malformed JSON,
  and URL-less entries; core suite **44/44**.
- Published CLI with a fresh state:
  - added two Later jobs in `export-dest` with descriptions `export alpha` and
    `export beta`;
  - `export` wrote 2 JSON entries with URL/name/destination/queue/connections/
    start mode/conflict policy;
  - removed both jobs, then `import` added 2/2;
  - reloaded jobs retained destination, description, queue, `startMode: later`,
    and conflict policy.
  - resuming both imported jobs produced 65,536-byte files with matching
    SHA-256 `6eddea6c3f39796000cea6740cf4490cd497b19e066c2336c72a6a14d1cca863`
    and `d39a1f0c44e90216185bb2d02e21457f77f143ad5317fa37eb562af69c7bc01b`.
- Integration suite **39/39**.
- WinUI File menu exposes Export/Import with a path dialog.
- Remaining: recent-URL history; own JSON format.

## Completion dialog evidence (M11, 2026-11-04)

- WinUI now shows a `Download complete` dialog when `ShowCompletionDialog` is
  enabled and the window is open: finished file name/path plus Open file, Open
  folder, and Close.
- Completed jobs are queued so only one dialog is shown at a time.
- Published build: a 65,536-byte `complete-dialog.bin` job completed while the
  WinUI window was open; UI Automation read the title/path and all three
  buttons, and Close dismissed it.
- Tray notifications and sound honor `NotifyOnCompletion`/`PlayCompletionSound`
  (E33); completion actions fire once (E22). Sound was not acoustically verified.
- Remaining: recent-URL history.

## Site grabber evidence (G2/G3, 2026-11-04)

- Added a runtime `SiteGrabber` service behind `grabber.run` IPC:
  - BFS page crawl with depth 0–5 and page cap;
  - same-host scope, extension include-list, include/exclude URL globs, and
    existing-file skip;
  - URL-path preservation under the mirror folder;
  - HTML `href`/`src` rewriting to relative local paths when the target was
    mirrored; external links stay unchanged;
  - a separate HTTP client with redirects/decompression and a 5 MB page cap.
- TestServer `/grab/` graph: index → page1 → page2 → page3, with `a.bin`,
  `b.bin`, `c.bin`, `d.bin`, and `pic.png`.
- Integration `SiteGrabberCrawlsScopeAndRewritesLinks` (suite **40/40**):
  - depth 2 crawled exactly index/page1/page2, not page3;
  - extension filter `bin` skipped the PNG;
  - exclude glob `*/files/b.bin*` skipped b.bin;
  - queued a.bin/c.bin completed with seed 290/293 hashes;
  - saved `grab/index.html` had `href="page1.html"` and `files/a.bin`,
    preserved the external link, and page3/b.bin were absent.
- Published CLI reproduced 3 pages/2 queued/1 skipped:
  - `a.bin` SHA-256 `ac4cc81ea530f1ddca9f335980323eee724a351bd5b8d21d325ffd3b7108663b`;
  - `c.bin` SHA-256 `abef0ea89918113d007c57b8d2260ea10d78d31703ad9313b0eed9859f7ee09b`.
- WinUI Tasks → Run site grabber exposes all scope/filter/mirror controls.
- Remaining: CSS url(); verify sandboxed JS rendering from a normal shell (`scripts\test.ps1 -IncludeBrowser`) (robots E91, proxy/auth E92, JS rendering E93)
  rewriting, preview.

## Grabber project evidence (G4, 2026-11-04)

- Added durable `grabber-projects.json` with project config, status, page/file
  counts, last error, created/updated/last-run times; `running` projects become
  `interrupted` after a restart.
- Runtime project API: create/list/run/remove, one run per project, progress
  saves throttled to 2 s; the same project can be resumed after restart.
- Schedules gained `GrabberProjectId`; a due start time launches the project once
  per day on the Host timer and records `LastStartDate`; stop times are ignored
  for grabber schedules.
- Integration (suite **42/42**):
  - `GrabberProjectPersistsAndResumesWithoutRedownload`: first run 3 pages,
    2 queued, 1 skipped; fresh service loaded the completed project; resume
    reported 0 queued/3 skipped, the job count stayed unchanged, and a.bin
    hash/mtime stayed unchanged; removal left an empty list; a bad start URL
    produced status `failed` with LastError (fixes the previous false
    "completed 0 pages").
  - `ScheduledGrabberProjectRunsOncePerDay`: schedule action `grabber` ran the
    project with files queued; the second same-day evaluation produced no action.
- Published CLI:
  - `grabber add/list/run/remove`; resume printed `0 queued, 3 skipped` and both
    job JSON and a.bin mtime were unchanged;
  - `schedule add 0 00:00 --grabber <id>` displayed `grabber=<id>`;
  - the live Host timer ran a scheduled project (2 pages, 2 files, 1 skipped)
    and recorded `last-start=2026-11-04`.
- WinUI: Tasks → Grabber projects lists name/status/URL/counts with Run, Remove,
  and New project; the grabber dialog can save a reusable project.

## FTP evidence (C1, 2026-11-04)

- Added `FtpDownloadEngine` behind the engine interface:
  - passive binary FTP; URL userinfo or matching DPAPI site login;
  - SIZE for total bytes, REST/ContentOffset for resume from the
    destination-local part file;
  - cancellation, progress, expected-hash verification, overwrite policy;
  - falls back to a zero restart once if a server rejects REST.
- Added a local `FtpServer` to TestServer (USER/PASS, SIZE, PASV/EPSV, REST, RETR)
  with `plain.bin` (65,536), `auth.bin` (131,072), and throttled `slow.bin`
  (524,288 at 128 KiB/s), plus byte/REST counters.
- Integration `FtpRequiresLoginAndResumesFromPartialBytes` (suite **43/43**):
  - anonymous request failed `unauthorized`;
  - after adding the FTP site login, `plain.bin` completed with source hash match;
  - `slow.bin` paused at 229,376/524,288, resumed with REST, completed 524,288
    bytes with SHA-256
    `ba2e57b4683ec0881913a6a0637e7c78e6215e263102a45e23bb345a2cb7a681`,
    and the server served less than 1.5× the file.
- Published CLI:
  - `plain.bin` SHA-256
    `d565bfa5c214b4851b4622b9cfa6bd5d1c2eed814d88cf8d9140ae4689002c0b`;
  - `slow.bin` paused at 229,376, resumed to 524,288 with matching hash;
  - `/ftp/stats`: `slowRetr=2, rest=1, slowBytesServed=294912`.
- Limitations: no FTPS, single-stream FTP, no proxy integration; FTP credentials
  come from site logins or URL userinfo.

## Post-download hook evidence (C4, 2026-11-04)

- Settings gained `PostDownloadCommand`. After a successful download, the Host
  runs the command with the final path as one `ProcessStartInfo.ArgumentList`
  entry and `UseShellExecute=false`; a 5-minute timeout kills the process and
  failures are logged without changing the completed job.
- Core `PostDownloadCommandLineTests` verify the command name, no shell, and the
  exact single argument.
- Integration suite **45/45**:
  - `PostDownloadHookRunsOnceWithExactFilePath` delivered
    `hook file (1).bin` unchanged;
  - `PostDownloadHookFailureDoesNotFailDownload` left the job completed after a
    throwing hook.
- Added `tools/HookSpy` (writes its received arguments). Published Host with a
  65,536-byte download named `hook live (2).bin` produced the exact path in the
  hook log and `Post-download hook ran` in the Host log; the CLI showed the
  configured command.
- Remaining: fixed file-path argument only; no custom argument template or
  scanner verdict handling.

## CLI integration evidence (C2, 2026-11-04)

- The published CLI drove the background host externally:
  - `dm add ... --wait` created/completed `cli-c2.bin` (65,536 bytes,
    SHA-256 `abca4b11e6670203f950742f7a62c0702583539630781014785ad32057243292`);
  - `dm list --json` showed `state: completed`, source `cli`, queue/conflict
    policy, ETag, and Last-Modified;
  - `dm host status` returned host/protocol/state/jobs plus tray and native-host
    health;
  - `dm queue list`, `dm schedule list`, and `dm site list` responded via IPC.
- The same CLI surface covers pause/resume/retry/cancel/remove, settings,
  grabber projects, FTP, and export/import (E9/E16/E31/E38/E41/E42).
- Limitation: our own command surface; no
  registered `dm:` URL protocol.

## Connection profile evidence (C5, 2026-11-04)

- Settings gained `ConnectionProfiles` (host pattern + max connections), with
  normalization (trim, 1–32, max 50). `AddJobAsync` takes the minimum of the
  requested connections, the global `MaxConnectionsPerHost`, and the
  most-specific matching profile (`*`/`?` wildcards).
- Integration `ConnectionProfilesCapRequestedConnections` (suite **46/46**):
  - `127.0.0.1` profile 2 reduced a requested 8 to 2;
  - `localhost` did not match and used the global 4;
  - `*.example.com` reduced to 1.
- Published CLI: `profile add 127.0.0.1 2`; `profile list` showed `2 127.0.0.1`;
  a Later job requested with `--connections 8` reported
  `requestedConnections: 2`; `profile remove 127.0.0.1` left an empty list.
- WinUI Options includes a `pattern=max` multi-line profile editor.
- Limitation: caps per job, not aggregate connections across simultaneous jobs.

## Settings transfer evidence (C6, 2026-11-04)

- CLI `settings export <file>` writes the full settings snapshot with proxy and
  site-login passwords cleared; `settings import <file>` applies it via
  SettingsUpdate.
- Proxy DTOs now expose `HasPassword` instead of returning the password over IPC;
  UpdateSettings preserves the existing password when address/username match.
  Site logins already preserve DPAPI passwords by id/site/user.
- Fixed a UI bug where the Options clone omitted proxy, site logins, queues,
  schedules, profiles, sleep/system flags, and ffmpeg path; saving Options could
  wipe them.
- Integration `SettingsUpdatePreservesUnreturnedSecrets` (suite **47/47**):
  a snapshot with no passwords kept both proxy and site-login `HasPassword`
  flags after an update.
- Published CLI:
  - export contained neither `site-secret-123` nor `proxy-secret-456`, but did
    contain `hasPassword`, the music folder, and `127.0.0.1`;
  - after mutating max-concurrent/profiles/music, import restored max 4,
    `127.0.0.1 = 3`, the music folder, site login `PASSWORD stored`, and proxy
    `password=***`.
- Limitations: secrets are not portable to another install; WinUI has no
  export/import buttons yet.

## Installer repair evidence (C7, 2026-11-04)

- Added `scripts/repair.ps1` (`-InstallRoot`, `-AppDir`, `-Source`,
  `-NoShortcut`, `-NoStart`):
  - restores app files from a source if any required executable is missing;
  - re-registers the Chrome native host with `-Force`;
  - restores extension files and the Start Menu shortcut;
  - writes/updates `INSTALL.txt` and optionally starts the Host.
- Test: deleted the HKCU native-host key and manifest, then ran repair against
  `artifacts\app` with `-NoShortcut -NoStart`.
  - key recreated and pointed at the manifest;
  - manifest `path` targeted `DownloadManager.NativeHost.exe` and included the
    extension origin;
  - `extension\manifest.json` and `INSTALL.txt` restored under the test install
    root;
  - starting the Host reported `tray: True native host: registered`.
- Limitation: verified against published artifacts, not a clean Windows install.

## Upgrade/uninstall evidence (C8, 2026-11-04)

- `install.ps1` and `uninstall.ps1` now accept `-InstallRoot` for isolated tests.
- Workspace test in `test-upgrade`:
  - install A created app, extension, `INSTALL.txt`, and native-host
    registration;
  - created `data\marker.txt` to represent durable state;
  - re-running install refreshed the app and preserved the data marker;
  - uninstall without flags removed app files, extension files, and the
    native-host key while keeping `data\marker.txt`;
  - `uninstall -RemoveData` removed the entire install root.
- Dev native-host registration was restored after the test.
- Limitation: verified with a workspace install root, not a clean Windows
  machine. Job-level preservation follows from the untouched data subtree.

## Update-check evidence (C9 foundation, 2026-11-04)

- `install.ps1` now writes `install.json` (version/channel/installRoot).
  `InstalledBuild` walks up to find it or uses
  `DOWNLOADMANAGER_INSTALL_ROOT`; no marker means portable/dev.
- Added a signed manifest format: version, channel, installer URL, size,
  SHA-256, RSA PKCS#1/SHA-256 signature; `UpdateManifestVerifier` checks the
  canonical fields.
- Runtime `update.check`/`update.download`:
  - refuses portable builds before any network call;
  - HTTPS only (loopback allowed for tests), channel filter, newer-version
    check;
  - downloads to `data\updates`, enforces signed size and SHA-256, deletes on
    mismatch.
- Core verifier test passes (**46/46**).
- Integration `UpdateCheckExcludesPortableAndVerifiesSignedDownload` (**48/48**):
  portable `notInstalled`; a signed 1.0.1 manifest against a 1.0.0 install
  returned `updateAvailable`; an 8,192-byte package downloaded with the expected
  SHA-256.
- Published CLI portable check printed `notInstalled`; `update download` exited
  1 with the portable-exclusion message. WinUI Help exposes check/download.
- Remaining: periodic/auto checks, tray notification, critical-download gating.
  C9 remains **partial** (P11.3/P11.7).

## Silent install/rollback evidence (C9 P11.5/P11.6, 2026-11-04)

- `scripts/apply-update.ps1`:
  - waits for the requested Host PID;
  - backs up `app` and `install.json`;
  - runs the package `install.ps1` with `-Source/-InstallRoot/-Version/-NoLaunch`;
  - verifies the installed version and Host executable, removes the backup, writes
    a JSON result, and starts the new Host;
  - on any failure restores both backups, writes `failed`, and restarts the old
    Host.
- `update.install` extracts the verified zip, launches the helper with the Host
  PID, returns `scheduled`, and raises a clean Host shutdown request. CLI
  `update install` and WinUI Install now/Later use it.
- Fixture test:
  - success: 1.0.0→2.0.0, `status=succeeded`, version 2.0.0, `VersionB` created;
  - failure: package wrote `Corrupt.txt` then exited 1; version stayed 1.0.0,
    marker A restored, `Corrupt.txt` absent, `status=failed`.
- Real Host test: installed 1.0.0 in a workspace root, started the Host, ran a
  2.0.0 package with `-HostPid`, shut the Host down; helper returned `succeeded`,
  `install.json` became 2.0.0, `VersionB` existed, and a started Host served
  status. The helper's Host-launch was changed to hidden/explicit exit after a
  lingering-process issue was observed.
- Remaining: clean-Windows end-to-end acceptance.

## Update notification/gating evidence (C9 P11.3/P11.7, 2026-11-04)

- Settings gained `AutoCheckUpdates`, `UpdateCheckIntervalHours` (1–168),
  `SkippedUpdateVersion`, and a Host update monitor timer.
- Host monitor:
  - checks on startup when auto-check is enabled and every configured interval;
  - notifies through the tray once per version unless that version was deferred;
  - exposes tray “Install update …” and “Check for updates…” actions;
  - blocks automatic install with active downloads and reports “Update postponed”.
- UI Options exposes manifest URL, PEM public key, channel, auto-check, and
  interval; Help → Check for updates shows installed/available; Later stores
  `skippedUpdateVersion`; Install now offers “Install anyway” when downloads are
  active.
- Live run against an installed 1.0.0 Host and signed local 1.0.1 manifest:
  - Host log: `Update 1.0.1 available.`;
  - UI: `Installed: 1.0.0 / Available: 1.0.1 / Size: 8192 bytes`;
  - clicking Later set `"skippedUpdateVersion": "1.0.1"`;
  - with a throttled 1-connection job at 327,680/1,048,576 bytes downloading,
    `dm update install` failed with `Downloads are active…`; the Host stayed up.
- Integration suite **48/48** includes busy/defer assertions. Manifest and
  installer redirects to non-HTTPS (non-loopback) are rejected.
- Remaining: clean-Windows end-to-end acceptance.

## Browser scope decision (C3, 2026-11-04)

- Chrome is the only supported browser. Edge (Chromium), Firefox, and others are
  explicit non-goals: no Edge native-messaging registration/package, and no
  Firefox WebExtension/native-messaging implementation was built or verified.
- The decision and reuse path are documented in
  `docs/browser-integration-scope.md`. `C3` is marked **blocked** (explicit
  scope), not implemented.

## WinUI scheduler (2026-11-04)

- Enabled the toolbar Scheduler button and Downloads → Scheduler.
- The dialog lists schedules with target, kind, start/stop, days and status;
  Enable/Disable and Remove act immediately; Add schedule supports a queue or
  grabber-project target, daily/one-time dates, start/stop, and days.
- Verified with UI Automation: added `UI Scheduler` daily 10:30 on the Main
  queue, the reopened dialog showed `UI Scheduler - enabled` / `daily 10:30
  [...] Main`, and `dm schedule list` showed the same schedule.
- Settings clone deep-copy (fixed in C6) keeps queues/schedules when saving
  Options.

## Recent-URL history evidence (M8, 2026-11-04)

- Settings gained `RecentUrls` (normalized to valid HTTP/HTTPS/FTP URLs, deduped,
  newest first, max 20). Every accepted add except grabber/import sources moves
  its URL to the front and persists settings.
- WinUI's Add URL field is an editable ComboBox populated from the history;
  CLI `settings get` prints the three newest URLs.
- Integration `RecentUrlsAreRecordedDeduplicatedAndPersisted`:
  - added history-a then history-b, re-added history-a;
  - observed `[history-a, history-b]`;
  - a fresh service loaded the same order. Integration suite **51/51**.
- Published CLI showed both URLs newest-first; expanding the Add URL combo listed
  `hist-two.bin` and `hist-one.bin`.
- Phase 6 is complete; M8's previous "no recent history" limitation is closed.

## T4/T3/T7/T8/T10 evidence (2026-11-04)

- T4: the published Host downloaded a 200 MB (209,715,200-byte) file with one
  connection at 16 MB/s. Across 117 samples at 100 ms:
  - working set 66,809,856 -> peak 80,457,728 bytes (delta 13.0 MiB);
  - private memory 19,447,808 -> 29,179,904 bytes (delta 9.3 MiB);
  - elapsed 12.92 s; SHA-256
    `99f8eb83d20ba51b3c79aeba226031f1bf79bdc9d281d4e494735b79f0a0bedc`
    matched `/large/expected`.
- T3: engine `ReportsHttpDnsTlsAndTimeoutFailureClasses` maps 404 -> `NotFound`
  (non-retryable), 500 -> `Http` (retryable), DNS -> `Dns`, TLS -> `Tls`, and a
  250 ms read timeout -> `Timeout`; every case fails without a final file.
- T7: engine `ProgressReportsAreMonotonicAndComplete` saw at least three
  reports, non-decreasing bytes, final 1,048,576 bytes, positive speed, and ETA
  values within 0-5 minutes.
- T8: integration `PausePreservesPartialAndCancelDeletesIt` paused at 65,536
  bytes with the part present/final absent, resumed past another 32,768 bytes,
  then deleted both on cancel.
- T10: engine `HtmlPageIsSavedOnlyForHtmlLikeTargets` saved `/html` as
  `legit.html`, while `error-page.bin` received the same `text/html` body and
  failed `invalidContent` with an actionable message and no final file.
- TestServer now has `/large/{name}` (streams 200 MB without allocating it) and
  `/large/expected` for the streaming hash.

## Host-instance and speed-limit evidence (L1/A7, 2026-11-04)

- L1: one published Host was running (PID 45304); a second `--background`
  launch exited while the count stayed at 1 and the original PID remained.
  `dm host status` still answered over IPC.
- A7:
  - engine `PerDownloadSpeedLimitIsApplied`: 512 KiB with a 128 KiB/s
    per-download limit completed in 4.0 s (bound 3.2-8.0 s) with the expected
    hash;
  - integration `GlobalSpeedLimitAppliesToNewJobs`: a 128 KiB/s global setting
    was inherited by a new job, which completed 512 KiB in 4.3 s with the
    expected hash.
- Limitations: limits are per job; aggregate fairness across simultaneous
  limited jobs is not enforced.
- Engine suite **26/26**, integration **57/57**.

## Accessibility, DPI and localization evidence (U7, 2026-11-04)

- Added `scripts/ui-accessibility-check.ps1`.
- Accessibility: no unnamed visible interactive controls; `JobsList`,
  `CategoryList`, status and host-connection text have names; Add dialog
  fields are all named.
- Focus order through the WinUI input site: Options -> Scheduler -> Start Queue
  -> Stop Queue -> Tasks -> Add URL (disabled actions are skipped).
- Contrast: 4.55:1 to 12.59:1 across the explicit dark palette; all normal-text
  pairs pass 4.5:1.
- DPI: system/window 96 (100%). Simulated effective layouts:
  - 100%: 3440x1392
  - 125%: 2752x1114
  - 150%: 2293x928
  - 200%: 1720x696
  All keep the toolbar and Description column inside the window. Actual
  125/150/200 scaling requires sign-out and was not exercised.
- Localization: no `.resw`/`.resx` resources; all UI strings are hardcoded
  English.
- U7 checks passed; build 0. Existing suites engine 30/30, core 51/51,
  integration 61/61.

## Tray interaction evidence (U5, 2026-11-04)

- Extended `tools/HostChecks` with a hidden WinForms message loop so the
  NotifyIcon is registered with the shell (`_added=True`).
- Verified:
  - tray menu callbacks Open, Settings, Pause All, Resume All, Exit;
  - active/integration text and icon tooltip;
  - notification queue `0 -> 1 -> 0` after `RefreshState`;
  - direct `ShowBalloonTip` handed without exception.
- Click-through constraint: `Shell_NotifyIconGetRect` returned `0x80004005`
  and `scripts/open-tray-overflow.ps1` reported `NO OVERFLOW CHEVRON`, so a
  real mouse click on the hidden shell icon could not be automated. The menu
  callbacks are invoked in-process.
- `tools/HostChecks` reports `ALL CHECKS PASSED`; build 0 errors. Existing
  suites remain engine 30/30, core 51/51, integration 61/61.

## Keyboard/focus/modality evidence (U4, 2026-11-04)

- Added `scripts/ui-keyboard-check.ps1`, which uses UI Automation metadata,
  invocation and direct `WM_KEYDOWN` to the WinUI `InputSiteWindowClass`.
- Live results:
  - accelerators: Ctrl+N (Add), Ctrl+F (Find), F3 (Find next),
    Ctrl+Shift+S (By Size), Ctrl+M (Move/Rename);
  - `AddButton` UIA focus: `hasFocus=True focusable=True`;
  - `Delete download?`: `modal=True`, Enter closed it, `rowsAfter=2`;
  - `Move/Rename`: `modal=True`, Enter closed it with the same path;
  - `Add download`: `modal=True`.
- Limits: global `SendInput`/`SendKeys` is still blocked by the desktop
  foreground lock; key activation was verified through the WinUI input-site
  window instead.
- UI-only change; build 0 errors and the existing suites remain engine 30/30,
  core 51/51, integration 61/61.

## UI inventory verification (U1/U2, 2026-11-04)

- Added `scripts/ui-inventory.ps1` (live UI Automation menu/toolbar/category/
  column dump).
- Live dump confirmed:
  - five menus Tasks/File/Downloads/View/Help;
  - toolbar Add URL, Resume, Stop, Stop All, Delete, Delete Completed, Options,
    Scheduler, Start Queue, Stop Queue;
  - category tree with all categories plus Active/Queued/Failed;
  - all eight list columns.
- Added File Stop All/Redownload, Downloads Find next plus Start/Stop queue
  submenus, and context Open with/Move-Rename/Redownload/Resume/Stop.
- Move/Rename is backed by `MoveCompletedDownloadRenamesAndUpdatesPaths`
  (integration); Open with by `OpenWithUsesOpenAsRunDll` (core).
- Core **51/51**, integration **61/61**, engine **30/30**, build 0 errors.
- A later window capture rendered black in this harness session while UI
  Automation still exposed the full tree; the earlier `ui-audit.png` shows the
  rendered layout.

## Segmented benchmark evidence (A9, 2026-11-04)

- Benchmark run: started TestServer and Host, downloaded a fixed URL with 1/4/8
  connections, sampled Host CPU/working set and write bytes.
- 64 MiB at a 4 MiB/s per-connection throttle:
  - 1 connection: 16.24 s, 3.94 MiB/s, 1.44 s CPU, 89.2 MiB peak, 65.2 MiB
    written;
  - 4 connections: 4.06 s, 15.78 MiB/s, 0.52 s CPU, 94.5 MiB peak, 64.4 MiB
    written;
  - 8 connections: 2.06 s, 31.12 MiB/s, 0.11 s CPU, 95.0 MiB peak, 64.2 MiB
    written;
- All three payloads matched SHA-256
  `0abe39d939a3e805da82061b52bf07d6c12d8de3ea7e2ab3614df0aca26ec3dc`.
- Caveats: localhost with a synthetic per-connection throttle.
- Existing suites remain engine **30/30**, core **50/50**, integration
  **60/60**; build 0 errors.

## Speed/ETA stability evidence (A8, 2026-11-04)

- Engine `SpeedAndEtaEstimatesAreStable` (suite **30/30**): 4 MiB at a
  1 MiB/s throttle produced 17 samples; 8 warmed samples stayed at
  1,036,704-1,048,791 B/s (max/min 1.012). ETA at 2 s was 1.77 s against
  1.75 s actual remaining; warmed ETA never rose by more than 0.75 s between
  samples; the seed-33 hash matched.
- Published CLI: 16 MiB at 2 MiB/s produced:
  - `t=0.8s 1.68 MB/s ETA 9.42s`, `1.7s 1.93/7.29`, `2.5s 1.99/6.25`,
    `3.3s 2.02/5.28`, `4.1s 2.10/4.06`, `4.9s 2.10/3.50`,
    `5.7s 2.10/2.69`, `6.5s 2.10/1.66`, `7.3s 2.10/0.91`,
    `8.1s 2.10/0.09`, then completed;
  - speed stayed in 1.99-2.10 MB/s after the ramp and ETA decreased
    monotonically;
  - 16,777,216 bytes with SHA-256
    `63b709bf47103077ee5271b76d97fb9b2d2b671df26e6459f8f2d333f85ac9b2`
    matching seed 901.
- Core **50/50**, integration **60/60**; build 0 errors.
- Limitation: startup ramp is reported as measured.

## Cross-job per-host connection evidence (A5, 2026-11-04)

- Added a shared `HostConnectionLimiter` acquired around single-stream
  requests, range probes and every segmented request. The runtime updates the
  limit from `MaxConnectionsPerHost` on startup and settings changes; each job's
  `RequestedConnections` is also capped by that value.
- TestServer `/file` now exposes `PeakConcurrentFileRequests` for all requests,
  not only range requests.
- Integration `PerHostAndGlobalConnectionLimitsApplyAcrossParallelJobs`
  (suite **60/60**):
  - phase 1: `MaxConcurrentDownloads=2`, host limit 8, three 2 MiB
    single-connection jobs -> peak running 2, peak file requests 2;
  - phase 2: `MaxConcurrentDownloads=3`, host limit 2, three 2 MiB
    single-connection jobs -> peak running 3, peak file requests 2;
  - a job requesting 4 connections was capped to `RequestedConnections=2`;
  - `hostLimiterPeak=2`; all six seed hashes matched.
- Engine suite **29/29**, core **50/50**, build 0 errors.
- Limits: the limiter is per process and keys redirects under the original
  host; HLS/DASH segment requests are not yet gated.

## Dynamic work-redistribution evidence (A4, 2026-11-04)

- Segmented transfers now run a worker pool capped at the requested connection
  count. Idle workers split the in-flight segment with the largest remaining
  range at its midpoint and download the tail; the original worker stops at the
  reduced boundary. Split segments join the checkpoint list under the same lock
  as snapshots, so resume state stays contiguous.
- Engine `DynamicWorkRedistributionStealsSlowSegment` (suite **29/29**):
  - 4 MiB, 2 connections, 256 KiB/s throttle for requests starting at byte 0;
  - completed in about 1 s (the 2 MiB slow range alone needs about 8 s);
  - matched the seed-31 hash and recorded a stolen mid-range request.
- Published CLI: `steal-live.bin` 4 MiB / 2 connections completed in **1.66 s**.
  Range requests: `0-0`, `0-2097151`, `2097152-4194303`, then stolen
  `1050472-2097151`, `527132-1050471`, `296334-527131`.
- SHA-256 `db7013ecafc99eb74cdbfdc3ce75f096a8e1098692ea132a70902d715cd21b00`
  matched seed 777; file size 4,194,304 bytes.
- Stealing requires at least 256 KiB remaining; no cross-job stealing.

## Chrome host-outage recovery evidence (B10, 2026-11-04)

- Outage: Host and NativeHost stopped and the native-messaging registry key
  removed, with a handoff dialog already open.
- Manual failure: `Could not reach the Download Manager host: Specified native
  messaging host not found.` The URL stayed in the dialog, Download was
  re-enabled, and the pending `handoff.b10-manual-test` request remained in
  session storage.
- Popup failure: `Disconnected` / `Native bridge unavailable` / the same native
  messaging error.
- Automatic failure: `b10-auto-down.bin` completed in Chrome at 262,144 bytes,
  not paused, with no Host job; one notification was created.
- Restore: native host re-registered, Host started, `dm.status` connected=true,
  lastError empty.
- Retry in the same dialog: `Added: b10-manual.bin`; pending request cleared;
  job completed 262,144 bytes, SHA-256
  `57af328cfbf9256ddb56e84ba9b73ce4af2e1f6f8e015dcdae560fd38bfbcc42`
  (seed 1001).
- Automatic recovery: `b10-auto-up.bin` was taken over (no Chrome item) and
  completed 262,144 bytes, SHA-256
  `06abf2b4c3c411861eedc3f34e43e841cfadbc17e70e86d833deb61bc393c003`
  (seed 1003).
- Popup after restore: `Connected` / `Native bridge connected` /
  `Automatic handoff on`.
- Limitation: manual handoff has no automatic retry loop; the user clicks
  Download again after the service returns.

## Chrome bridge enable/disable evidence (B9, 2026-11-04)

- The options page now marks `body[data-settings-loaded]` after filling the form,
  giving automation a deterministic ready signal (also used by the manual check).
- Disabled state saved through `options.html` (`enabled=false`,
  autoIntercept=true, 47 intercept extensions, destination
  `test-downloads\b9`):
  - popup: `Disabled` / `Bridge disabled` / `Enable it in Settings.`;
  - `b9-disabled.bin` completed in Chrome at 262,144 bytes;
  - no Host job.
- Re-enabled (`enabled=true`):
  - popup settled to `Connected` / `Native bridge connected` /
    `Automatic handoff on`;
  - `b9-enabled.bin` was erased from Chrome after takeover and completed as a
    `chromeAuto` Host job in `test-downloads\b9`, 262,144 bytes, SHA-256
    `517f9b65d77670d86dba9976567b202b47eacf10be47945ee95fea60db8b8334`
    (seed 902).
- `autoIntercept=false` with `enabled=true`: `b9-auto-off.bin` completed in
  Chrome and created no Host job.
- Both flags were restored to true. `npx tsc --noEmit` exited 0 and
  `npm run build` succeeded.
- Limits: manual context-menu/media handoffs are not gated by `enabled`; the
  popup can briefly report Disconnected while the MV3 worker reconnects.

## Chrome interception-rule evidence (B8, 2026-11-04)

- Rules were saved through `options.html` (not injected): intercept allow-list,
  excluded extensions and excluded hosts.
- Automatic-interception matrix (`downloads.download` uses the same
  `downloads.onCreated` path as page downloads):
  - allow-all: `b8-baseline.bin` produced no Chrome item and one `chromeAuto`
    Host job in `test-downloads\b8`, 262,144 bytes, SHA-256
    `af45672194af38d11fd2ef7a2b65bf7da7a8f7274528b9c3900ef5f8128338bd`.
  - `interceptedExtensions=['zip']`: `b8-type.dat` completed in Chrome, no Host
    job.
  - `excludedExtensions=['bin']`: `b8-excluded-ext.bin` completed in Chrome, no
    Host job.
  - `excludedHosts=['127.0.0.1']`: `b8-excluded-host.bin` completed in Chrome,
    no Host job.
- Temporary bypass: the popup button changed the persisted `bypassUntil` from 0
  to `1793767884661` and displayed `Bypassed (5 min)`; `b8-bypass.bin` completed
  in Chrome with no Host job.
- Limits: host patterns are exact/subdomain strings (no ports or URL globs);
  rules apply only to automatic interception, not manual handoff.

## Chrome destination/start-mode handoff evidence (B6, 2026-11-04)

- The handoff window now prefills the last confirmed destination and start mode
  from extension settings and saves both after a successful confirm, so the same
  choices apply to later handoffs and automatic interception.
- Live Chrome for Testing run:
  - `b6-now` + Start now -> `Added: b6-now (1).bin`; Host job completed
    1,048,576 bytes in `test-downloads\b6-now`, SHA-256
    `f1bdae78d2a683df4a995f04984a5dde44abf31aafa4c9d664ae6ca2fb5ee458`.
  - `b6-later` + Download later -> job `queued`, 0 bytes, no file in
    `test-downloads\b6-later`; after an explicit resume it completed
    1,048,576 bytes there, SHA-256
    `b3a9ed136f1834ecd5f6a922f91f4d0dac92a3c511db60d9083d00d0ed831cc2`.
  - A third dialog prefilled `b6-later`/later, proving persistence.
- `npx tsc --noEmit` exited 0; `npm run build` succeeded.
- Remaining gaps: own dialog layout, no queue picker/scheduler time, no
  per-dialog credentials.

## Chrome popup live-progress evidence (B11, 2026-11-04)

- `popup.ts` now renders live state, received/total bytes, progress, speed and
  ETA for active jobs. The popup polls `jobs.query` every second while open and
  still listens for `job.event` pushes.
- `estimatedRemaining` (Host TimeSpan) is parsed from clock, ISO-8601 or tick
  forms; if absent, ETA is derived from remaining bytes and bytes/sec.
  Terminal jobs show bytes only, so stale speed/ETA never linger.
- Chrome for Testing 156.0.8078.80 loaded `extensions\chrome\dist` as
  `pknddmneejadnnngffojnoecojhdfalc` and opened `popup.html`.
- A 32 MB throttled job produced four DOM samples 7 s apart:
  - `downloading 5.3 MB / 32.0 MB; 512.1 KB/s; ETA 53s` (16.4%)
  - `8.7 MB / 32.0 MB; 512.1 KB/s; ETA 47s` (27.1%)
  - `12.2 MB / 32.0 MB; 511.8 KB/s; ETA 40s` (38.1%)
  - `15.6 MB / 32.0 MB; 512.0 KB/s; ETA 33s` (48.8%)
- A 16 MB job showed `13.4 MB / 16.0 MB; 512.1 KB/s; ETA 5s`, then
  `Completed; 16.0 MB / 16.0 MB`.
- The completed 32 MB file matched seed 612 SHA-256
  `13e0dd0871c4264d9bb52006c51b1700c22b04680c4935ae97e96c836cbb51f4`.
- `npx tsc --noEmit` exited 0; `npm run build` produced `dist\popup.js`.

## Host lifecycle/tray evidence (L2/L5/L7/L8, 2026-11-04)

- L2: new `tools/HostChecks` constructs a TrayIconController with recording
  callbacks. It invoked Open, Settings, Pause All, Resume All and Exit and
  observed `4 active downloads`, `Browser integration: connected`, and
  `Download Manager - 4 active`; all checks passed.
- L5: `PipeSecurityFactory.CreateForCurrentUser` grants FullControl only to the
  current user and LocalSystem. HostChecks created a real pipe with it and
  observed `currentUserFullControl=True`, `localSystemFullControl=True`,
  `broadAllowAce=False`. `icacls` cannot open a busy live pipe.
- L7: tray text, published `dm host status`, and WinUI status all report
  `browser connected`; disabled/missing-registration states are exposed by the
  same helper.
- L8: live 4 MiB transfer logged `Sleep prevention active`; clean Host shutdown
  logged `Sleep prevention released` + `Host stopped` and exited. Restart
  restored a paused 32.8% job and a queued Later job; resuming the Later job
  completed 1,048,576 bytes with SHA-256
  `9c34d73a6d2499da0ca37e07de10c1b55c690deeb673972042bb9d3af7cdd2e4`
  and sleep prevention cycled again.
- Limitations: tray shell click-through not automated; no second Windows
  account; OS shutdown/restart actions and `powercfg /requests` not executed.

## Disk-space, permission, and part-reconciliation evidence (R6/R7, 2026-11-04)

- R6:
  - `DiskFullPreflightFailsBeforeWriting` injected a 1,024-byte free-space
    provider with a 1 MiB reserve; a 65,536-byte job failed `DiskFull` with the
    destination in the message and no part/final file.
  - `PermissionFailureIsReportedWithoutSuccess` placed a directory at the part
    path; the engine failed `Permission` and wrote no final file.
- R7:
  - `CrashReconciliationTruncatesLongPartFile`: a 5,000-byte part with a
    3,000-byte checkpoint was truncated to 3,000 and reloaded as
    `ReceivedBytes=3000`.
  - `OrphanPartFilesAreReported`: an unattached `.part` file appeared in
    `HostStatusDto.OrphanPartFiles`; `dm host status` prints orphan paths.
  - Short and missing part cases from E66 still pass.
- Limitations: disk-full is provider-injected, not a physically full volume;
  HLS/DASH total size is unknown before download; orphans are reported, not
  deleted or quarantined.
- Engine suite **28/28**, integration **59/59**.

## Retry/reconciliation evidence (R5/R7/R8, 2026-11-04)

- R5: engine `RetriesTransientServerErrors` hit `/flaky/retry.bin` with two
  injected 500 responses; the third request completed 65,536 bytes with the
  expected SHA-256, and the server saw exactly 3 requests.
- R7: integration `CrashReconciliationClampsMetadataToPartFile` shortened a
  1,024-byte part while its checkpoint claimed 3,000 bytes; a fresh service
  reported `ReceivedBytes=1024`. Removing the part made a fresh service report
  `ReceivedBytes=0`.
- R8: existing `ServerIgnoringRangeRestartsFromZero`,
  `ChangedRemoteContentRestartsSafely`, and
  `AlwaysBadRangeFailsWithoutCorruption` cover safe zero restarts and the
  terminal `RangeUnsupported` case.
- Engine suite **25/25**, integration **56/56**.

## DASH SegmentBase/SegmentList range evidence (V4, 2026-11-04)

- Media segments now carry inclusive byte ranges. Range requests are issued with
  `Range`, don't send `If-Range` for sub-resources, accept `206`, and handle a
  server that ignores `Range` with a `200` skip/limit fallback.
- `SegmentBase` requires a `BaseURL`; it reads `Initialization@range` and parses
  the SIDX in `indexRange` (versions 0/1, `first_offset`, 31-bit media
  references). Hierarchical SIDX returns a clear error.
- `SegmentList` now accepts `Initialization@range` and
  `SegmentURL@mediaRange`; the previous `mediaRange` rejection is removed.
- TestServer `/dash-range/single.mp4` is 498,434 bytes: an 806-byte init,
  68-byte SIDX, and three media chunks. `/dash-range/segmentbase.mpd` points at
  the SIDX and `/dash-range/segmentlist-range.mpd` lists the media ranges.
- Integration `DashSegmentBaseAndSegmentListRangesMatchExpectedOutput`:
  - both completed at 498,366 bytes with the reference SHA-256;
  - at least 4 ranged requests were observed per manifest;
  - integration suite **54/54**.
- Published CLI reproduced both at 498,366 bytes with SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`.
- Phase 9 is complete; dynamic/live, multi-period, hierarchical SIDX, and DRM
  decryption remain excluded.

## DASH SegmentTimeline/SegmentList evidence (V4, 2026-11-04)

- `SegmentTimeline` now advances `startNumber` for each expanded segment, so
  `$Number$` templates work alongside `$Time$`; `S@t/@d/@r` repeats expand
  correctly.
- `SegmentList` support reads `Initialization@sourceURL` and full-URL
  `SegmentURL@media` entries; `mediaRange` is rejected explicitly for now.
- TestServer:
  - `/dash/timeline.mpd` uses one `S` with `r=2`;
  - `/dash/segmentlist.mpd` lists all three media URLs.
- Integration `DashTimelineAndSegmentListMatchExpectedOutput`:
  both completed at 498,366 bytes with the reference SHA-256;
  integration suite **53/53**.
- Published CLI reproduced both at 498,366 bytes with SHA-256
  `3a17840b06a53432f9fa9a09188b7d68e3de720201bb88b3648277c2227cfba1`.
- Remaining: SegmentBase and SegmentList mediaRange/byte ranges; dynamic/live and
  multi-period remain unsupported.

## HLS alternate-audio evidence (V11, 2026-11-04)

- HLS master parsing now understands `#EXT-X-MEDIA:TYPE=AUDIO` entries
  (GROUP-ID, NAME, DEFAULT, AUTOSELECT, URI) and the variant `AUDIO` attribute.
  Selection prefers `DEFAULT=YES`, then `AUTOSELECT=YES`, then the first
  rendition with a URI; in-band-only audio leaves the single-variant path.
- HLS media parsing was extracted into `PrepareHlsMediaAsync`; a reusable
  `DownloadMediaTrackToFileAsync` now feeds both DASH and HLS muxing with
  per-track `hls-video`/`hls-audio` checkpoints and signatures.
- TestServer `/hls/av-master.m3u8` lists `Commentary` first and `English`
  `DEFAULT=YES` second, pointing at different audio playlists. The video/audio
  tracks are fMP4 init+segments derived from the DASH A/V fixture.
- Integration `HlsAlternateAudioMuxesWhenFfmpegAvailable`:
  - downloaded the default English audio, not the first-listed commentary;
  - muxed to an MP4 matching `/hls/av-expected`;
  - integration suite **52/52**.
- Published CLI:
  - `hls-av.mp4` completed at 364,524 bytes;
  - SHA-256
    `03cfeddb0e614f8189f7a7502ed08731f66cdf204ab2ad1cf81cda07f7fc1ce5`
    matched the reference;
  - ffprobe reported H.264 320x180 + AAC, 4.0 s.
- Remaining media work: additional DASH modes. Alternate-language selection UI
  is not exposed.

## HLS AES-128 evidence (V10, 2026-11-04)

- HLS parsing now supports `#EXT-X-KEY:METHOD=AES-128` with an identity key
  format, relative/absolute key URIs, explicit `IV` attributes, sequence-derived
  IVs, `METHOD=NONE`, and per-segment key rotation. SAMPLE-AES and session keys
  remain `drmProtected`.
- Keys are fetched with the job's headers/auth and cached per key path; any key
  that is not 16 bytes is rejected. Segments are CBC-decrypted with PKCS7
  padding before being appended to the output. Checkpoint signatures include the
  key bytes and IVs.
- TestServer fixtures:
  - `/hls/aes.m3u8` — one key, sequence-derived IVs;
  - `/hls/aes-rotate.m3u8` — key1 for seg0, key2 for seg1/seg2.
- Integration `HlsAes128AndRotatingKeysDecryptToExpectedOutput`:
  both playlists completed at 580,732 bytes with the clear seg0-2 SHA-256;
  integration suite **50/50**.
- Published CLI: `aes-live.ts` and `aes-rotate-live.ts` both completed at
  580,732 bytes with SHA-256
  `6a1384036fceb6355c2aae29504a8fa5fd899c642fd47d309685607c0031b946`,
  matching the concatenated clear segments.
- Remaining media work: additional DASH modes and HLS alternate audio; live
  recording stays out of scope and SAMPLE-AES stays DRM-detected.

## UI audit (M1/M2/M4/M5/M14, U3/U6, 2026-11-04)

- M14: added `job.segments` IPC and a Progress details dialog (state, bytes,
  percent, connections, speed, ETA, part path, overall + per-segment bars).
  Integration `SegmentDetailsMatchTheEngineCheckpoint` validated 2+ segments
  against an active throttled transfer. Live WinUI showed 3,145,728/33,554,432
  bytes (9.4%), 1/1 connections, 65,525 B/s, and `1 segment from the engine
  checkpoint` with 3 progress bars.
- M1: live counts for seven jobs — All 7, Active 2, Queued 0, Failed 1,
  Finished 4, Unfinished 3; the Queues manager opened. The sidebar now exposes
  Active/Queued/Failed and a working Queues entry.
- M2: Extended multiselect showed `(2 selected)`; bulk Stop paused both
  bulk1.bin/bulk2.bin. Sort now uses numeric size, bytes/sec and updated time
  instead of formatted strings.
- M5: delete dialog has `Also delete the downloaded file from disk` checked by
  default. Unchecking removed keep.txt from the list while keeping the file;
  checking removed remove.txt from both the list and disk.
- M4: shared `ShellActions` (unit-tested) and `dm open`/`dm reveal`; live
  `reveal` opened `test-downloads - File Explorer`, and `open` opened
  open2.txt in Notepad.
- U3: `ui-state.json` persists window size, category, sort key/direction,
  toolbar scale and hidden categories. A seeded 1024x640/`sortKey=size`/
  `toolbarLarge=false` state restored a 1024x640 window, 34x32 Add URL button,
  and size-a (1 MB) before size-b (64 KB).
- U6: `AppSettingsDefaultsTests` pin the observed baseline (8 connections,
  startup/clipboard off, notify/sound/dialog on, auto-resume, stable/24h).
- U7: toolbar buttons now expose UIA names; the dark-theme layout was captured.
- Audit limits: synthetic keyboard injection was blocked by Windows keeping
  another process foreground, so U4 stays partial; the tray icon was hidden
  from the overflow UIA tree, so U5 stays partial; column resize/reorder and
  localization remain out of scope.
- Core suite is now **50/50** (added ShellActions, defaults, and manifest tests).

## SOCKS4/4a, proxy UI and DPAPI proxy password (E89, 2026-11-04)

- `ProxySettings.Password` is in-memory only (`[JsonIgnore]`); the settings
  store writes `proxy.passwordProtected` (DPAPI, entropy distinct from site
  logins). A legacy plaintext `password` is adopted on load and the file is
  rewritten without it; an undecryptable blob yields "no password" instead of
  a crash.
- SOCKS4 and SOCKS4a work through .NET's native `socks4://` / `socks4a://`
  proxy URIs; verified with the new `TestSocks4Server` (user id sent, hostname
  resolved at the proxy for 4a, wrong user id refused).
- Options -> Proxy: mode (none/system/manual), type (HTTP/SOCKS4/SOCKS4a/
  SOCKS5), host:port, user, masked password. Address handling is the
  unit-tested `ProxyAddress` helper in Contracts (the WinUI project must not
  reference Core).
- Results: Engine 33/33, Core 78/78, Integration 65/65, solution build 0 errors.
- **Not verified live:** the Options dialog itself.
  `scripts/ui-proxy-options-check.ps1` was blocked by Windows' unverified-
  publisher prompt and cancelled; rerun it after allowing the build once.

## Tests

- Engine: **33/33 passed** (`tests\DownloadManager.Engine.Tests`), including
  segmented transfer/resume, authentication, HTTP/SOCKS5 proxy auth, wrong
  credentials, redirect credential boundaries, 404/500/DNS/TLS/timeout classes,
  HTML-page policy, monotonic progress/ETA, transient-500 retry recovery,
  per-download speed limiting, disk-full preflight, permission failure, and
  dynamic work redistribution, speed/ETA stability, and SOCKS4/SOCKS4a (user ID,
  hostname resolution, rejection).
- Core: **78/78 passed**, including shutdown-command construction, clipboard URL
  extraction, batch URL expansion, file categories, download-list round-trip,
  safe post-download argument construction, update-manifest signatures,
  shell-open/reveal command construction, and settings defaults.
- Runtime/persistence: **65/65 passed** (`tests\DownloadManager.Integration.Tests`),
  including queues, scheduler, quotas, completion actions, Download Later, DST,
  sleep prevention, destination-local payload, authenticated handoff, runtime
  HTTP/SOCKS5 proxy settings, expired-URL refresh/resume, DPAPI site logins,
  HLS master/media, HLS AES-128 key rotation, HLS alternate-audio muxing, DASH
  SegmentTemplate/Timeline/SegmentList/SegmentBase byte ranges, DASH A/V muxing,
  HLS/DASH fragment recovery, rotating-token refresh, live-stream exclusion, DRM
  detection, editable properties, duplicate policies, category destinations,
  recent-URL history, pause-vs-cancel partial files, short/long/missing part
  reconciliation, orphan-part reporting, completed-file move/rename, per-host connection limits, global speed
  limiting, the scoped site
  grabber/offline mirror, persistent/scheduled grabber projects, FTP auth/resume,
  post-download hook isolation, connection profiles, secret-preserving settings
  updates, installer-only update check/download, segment/progress details, and the
  DPAPI-protected proxy password (on disk, restart, legacy migration, bad blob)
  plus SOCKS4 runtime settings.
- Extension: `npx tsc --noEmit` passes; `npm run build` succeeds (including the
  `batch.js` Download All window and the media panel/quality UI).
- Solution: `dotnet build DownloadManager.slnx -c Release` succeeds, including
  the WinUI x64 build.

## Environment limitations observed

- This harness denies child-process writes to `C:\Users\kaank\Downloads` and
  default `%LOCALAPPDATA%\DownloadManager`, so `StoragePaths.CreateDefault()`
  falls back to `artifacts\app\data`. Browser tests used the workspace
  `test-downloads` folder. On a normal user account, LocalAppData is the intended
  data root.
- The same restriction denied the Host's HKCU Run-key update; `L4` is marked
  **blocked** until tested from a normal shell/installed build.
- Native host registration works from PowerShell; the development registration
  currently points at `artifacts\app`, not an installed copy.
- Chrome stable requires manual **Load unpacked**. The extension is at
  `extensions\chrome\dist`.

## Next actions (dependency order)

### User test fixes in progress (handoff to next chat, 2026-11-04)

- User reports GitHub release filenames becoming UUIDs, testfile.org failures, tray balloon clicks and tray Settings doing nothing, broken light mode, and requests separate automatic download progress/completion windows. UI priority is high.
- Installed logs are `%LOCALAPPDATA%\DownloadManager\logs\host-20261104.log`; inspect job records selectively without exposing cookies/headers. testfile.org jobs failed HTTP 403 at zero bytes. Curl confirmed `Cf-Mitigated: challenge` for site links; in-app browser also shows Cloudflare verification (`https://testfile.org/files-5GB`). Do not claim this is fixed or try to bypass challenges.
- Important engine finding still UNFIXED: `HttpDownloadEngine.SendWithRedirectsAsync` always starts at `job.Url` and ignores browser-supplied `FinalUrl`, potentially replaying the challenged landing URL instead of the already-resolved CDN URL. Any fix must preserve original-origin credential stripping, validate target scheme/local-network policy, and add regression tests.
- Luna implemented extension filename helper/tests: prefer informative Chrome filename; when empty/UUID, prefer decoded original URL before opaque final URL. Six Node tests and typecheck passed. Review changes before packaging.
- Luna changed `App.xaml` and `MainWindow.xaml` to theme resources rather than hardcoded dark surfaces. WinUI build passed before subsequent main-session edits.
- Main-session edits NOT YET BUILT/TESTED: tray left click and BalloonTipClicked open UI; App uses separate named settings activation event so secondary-instance `--page settings` is not discarded; MainWindow restores minimized windows, persists nullable DarkMode in UiState, follows system theme when unset, and clears pending settings request correctly. No new installer yet.
- Separate automatic progress/completion windows are NOT implemented yet. Current progress is a manual ContentDialog and completion is a modal dialog tied to the main window. Need standalone per-job windows with live progress and completion actions, including when main UI is hidden/not initially running.
- Computer-use skill was loaded. Tool discovery returned no dedicated computer namespace, but user says it worked with DeepSeek and is starting a new chat to recheck. Do not repeat a blanket claim that desktop control is unavailable; inspect skill/tool availability freshly. Browser tab for testfile.org exists; no verification challenge was interacted with.


Current checkpoint (2026-11-04): the real Host process-kill matrix rerun passed
all six cases (1 and 8 connections, kills at 20/50/80%) at
`artifacts/kill-restart/20261104-152636/results.json` (E94). Single-stream
retained 4,980,736 / 12,386,304 / 20,054,016 bytes; 8-connection retained
4,479,153 / 11,429,751 / 20,658,980 bytes; the segmented sparse part file
remained 25,165,824 bytes. Both modes resumed to the reference hashes: single
`6d905264d6d4fc0445a3e694b6a1ad9bc16632be3310df3809d3b69e75476423`, segmented
`a240b74fda649eab4f67152507e49e9aaa6eb4925636b60f3d48f1269f6410e6`. This was
run in the main session with the Host process-kill script; it is not independent
subagent verification. The first matrix at
`artifacts/kill-restart/20261104-152127` exposed segmented progress loss at 20%
while all single-stream cases and both final hashes passed. `ReconcileAsync` was
truncating a segmented preallocated part file to aggregate `CompletedBytes`; the
fix restricts truncation to `Segments.Count <= 1`. Regression test
`CrashReconciliationPreservesSegmentedPartFileAndResumes` covers noncontiguous
two-range checkpoint bytes and resumed hash; all three CrashReconciliation tests
passed. Release solution build succeeded with 0 errors and one pre-existing
xUnit2031 warning at line 73. The unchanged core/engine suites passed 78/78 and
33/33; full integration passed **66/66** (177 total tests across the three
suites).

Final installer rebuild is successful: `artifacts\installer\DownloadManagerSetup-0.1.0.exe`,
71.9 MB, SHA-256
`A3D5ABCADE836177371CA8D3F5FD1981DE83CA47C8956D138C1C8920260D8363`. Publish
checks passed for every extension relative file and hash, including icons, with
no nested `dist`; this fresh package includes the recovery fix. It is ready for
user installation testing, but actual install/upgrade/uninstall lifecycle and
sign-in behavior remain unverified. Startup/install safety changes are under
validation, not verified:
Setup now uses Windows Restart Manager rather than force-killing by process
name; sign-in configures through synchronous config-only Host mode independently
of optional launch; uninstall retains state. Do not treat the build or these
changes as install acceptance evidence.

1. Done: Options -> Proxy controls, the Forget-password checkbox and the orphan cleanup dialog were live-verified (E93).
2. Site grabber was removed on 2026-11-04 by owner decision; no grabber work remains. Download All is retained.
3. Phase 11/12: finish validating startup/install safety, then perform clean-Windows
   installer smoke and install/upgrade/uninstall acceptance (still
    unverified outside this harness), L4 launch-at-sign-in from a normal shell,
    independent visual review and independent verification/review of the
    main-session kill/restart evidence (P2.7, P5.6, X.5), and final
    evidence.
4. System-proxy mode passthrough remains untested.
5. SOCKS5 UDP ASSOCIATE is not supported (not needed for HTTP downloads).
6. Deferred side quest: TorBox/account-provider integration, unrar, and a
   Linux/Docker headless host and web UI (ROADMAP.md A1–A5/D1–D4). These are
   deferred, not completed. Provider tokens must be supplied via environment
   variables and never committed.

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

Baseline: verified .NET 10.0.403, Node 26.11.1, Chrome
156, Windows App Runtime 1.8/2.x, and GitHub authentication.
Created the private repo, initial feature inventory, initial architecture, and
the first engine/host/IPC implementation.

## Site grabber removed (2026-11-04)

- Owner decision: the site grabber is out of scope. Deleted `SiteGrabber`, `HeadlessRenderer`, `RobotsRules`, grabber projects (model, `JsonGrabberProjectStore`, `grabber-projects.json` is no longer read), `grabber.*` IPC, CLI `grab`/`grabber`/`schedule add --grabber`, WinUI Tasks menu items, Grabber Projects category and scheduler targets, engine robots.txt redirect checks and the auxiliary crawler handler, TestServer `/grab`, `/robo`, `/robots.txt`, `/js` fixtures, and `scripts\test.ps1 -IncludeBrowser`.
- Kept: G1 Download All (page link collection in the extension). `DownloadSource.Grabber` and `ScheduleRule.GrabberProjectId` stay only so old state files load; legacy grabber schedules are removed and persisted on startup.
- Tests: core 78/78 (26 robots cases removed), engine 33/33, integration 65/65 (11 grabber tests removed, `LegacyGrabberSchedulesAreDroppedOnLoad` added).

Luna source review identified startup-only failure consumption, fixed with pending startup IDs; periodic metadata failures intentionally wait for the next configured period and are exposed via LastRunError. Full scheduler, sync hash/atomic replacement, retry counts, stale configuration and queue-detach failure/restart tests are required in the consolidated pass.

## 0.1.5 final launch/packaging checkpoint (2026-11-05)
Scoped Host launch now checks the same named mutex as IPC rather than global process names. WinUI Release build passed with zero warnings/errors (artifacts/validation-launch.log). Fresh isolated QA launched outside the sandbox and rendered the dark main window and unified Scheduler; source fix clears a stale initial connection-error status after successful refresh while retaining editor errors. This last status fix awaits the final rebuild. Packaging review identified output deletion containment and version override guards; fixes in progress. The existing local installer predates these final changes and must be rebuilt before release. No public 0.1.5 publication or install acceptance claimed.

## 2026-11-05 — 0.1.5 release candidate

- Release build clean (0 warnings). Tests: Core 88, Engine 57, Integration 95, extension all passing.
- Rebuilt `DownloadManagerSetup-0.1.5.exe` (72.4 MB) and signed update ZIP (SHA-256 b19cba5a...1ee3); assets in `artifacts\release\v0.1.5`.
- Not yet published. Live acceptance (tray Exit, installer upgrade, updater restart) still unverified.


## 2026-11-05 — UI consistency pass (post-0.1.5 source)

- Single theme palette in App.xaml (dark/light ThemeDictionaries) + `RillTheme` helper; hardcoded window colors removed from main, progress, groups, scheduler, statistics and speed-graph code. Guidelines: `docs/design-guidelines.md`.
- List: colored status text, 6px rounded progress bar in a 140px Status column, blank unknown sizes, flat toolbar without disabled boxes, unified KB/MB units, smaller graph that expands upward.
- Progress window: segmented map replaces the overall bar; segment table (N./progress/downloaded/speed/info) uses live stream positions with a monotonic high-water mark (verified live: values only increase); DPI-scaled default size; fixed invisible text cells.
- Evidence: isolated `uiqa` profile with throttled TestServer downloads, dark and light captures. Core 88/88, clean Release build. Not yet packaged; scheduler/options/groups windows not re-reviewed visually.


## 2026-11-05 — Rebrand to Inbound

- Display name changed from Rill to Inbound Download Manager (UI, tray, installer AppVerName, Chrome extension, README). Install/data paths, native-host name, extension key and update identity unchanged for upgrade compatibility.
- Icon: owner-supplied artwork (plane descending into a tray), `assets/inbound-icon-source.png`, made transparent/cropped by `scripts/prepare-inbound-icon.ps1`; ICO/PNG/extension icons rebuilt by `scripts/build-icons.ps1`. Internal theme tokens renamed to `Inbound*`.
- Verified: clean Release build, extension 34/34, renamed window captured in isolated profile. Not yet packaged as a release.


## 2026-11-05 — Secondary-window review and redesign

Reviewed every window and dialog in both themes (isolated `uiqa` profile, captures from the published build).

- Accent: `SystemAccentColor*` overridden with Inbound blue; Windows' own accent (teal here) no longer leaks into checkboxes, primary buttons and selections.
- Title bars: shared `WindowLayout.ApplyChrome`; Scheduler, Groups, Statistics, progress and update windows previously had a white caption over dark content.
- Options: was a modal ContentDialog with browser-style tabs, equal-width Save/Cancel, typed numbers and folders, and validation that closed the dialog (losing edits). Now `SettingsWindow`: page list, titled sections, NumberBox, Browse… for every folder, proxy RadioButtons that enable the manual fields, side-by-side login credentials, OK/Cancel/Apply, inline errors that jump to the page. Verified live: toggle + OK persisted to settings.json and closed the window.
- Scheduler: SelectorBar instead of stretched tabs; TimePicker (24h) start/stop with clear, weekday toggles + "Every day", CalendarDatePicker one-time date; compact queue buttons. Verified live: New schedule with Sat/Sun off saved `days: Mon,Tue,Wed,Thu,Fri`, `startTime: 09:00`.
- Groups: empty-state text, inline create/add rows, Close button. Statistics: KB/MB units (was MiB). Add URL: full-width field, prefilled from a clipboard link.
- Toolbar: grouped with separators, theme icon tokens (light mode had pale pastel icons), Stop = pause glyph, Delete Completed = clear glyph (was a checkmark), single-line auto-width labels. Help menu says "About Inbound Download Manager".
- Exit: Host now sends `host.exiting` on a user exit (tray Exit, Tasks > Exit, `dm host shutdown`, queue "exit when done"); every frontend process closes. Update restarts don't send it. Live: UI+Host both gone within 3 s after `host shutdown`, nothing respawned after 18 s; relaunch starts both again. The tray menu click itself could not be driven (Windows shell overflow is not automatable here); it calls the same exit path.
- Updater restart (owner-approved, real install): version bumped to 0.1.6; package and installer built from ba2742a; manifest signed with a loopback URL into `artifacts/updtest` (not the release folder, nothing published) and served on 127.0.0.1. Installed 0.1.5: `update check` saw 0.1.6, `download` verified SHA-256, `install` succeeded in ~2 s; Host and WinUI restarted with new PIDs from the install dir, both report 0.1.6+ba2742a, 17 jobs intact. Manifest URL restored to GitHub afterwards.
- Installer: owner ran the relabelled 0.1.6 setup without admin on a clean per-user install (after uninstall) - succeeded. Upgrade-over-existing still to be seen on the next release.
- Installer "Access denied unless admin" root cause (owner-reported since the start): the repo root carries an explicit Low mandatory label, inherited by built setups, so Windows starts them at Low integrity (verified via token: setup 0x1000 vs Explorer 0x2000) and Inno cannot create `%TEMP%\is-*.tmp`. `build-installer.ps1` now sets an explicit Medium label; relabelled setup starts at 0x2000. Release downloads from GitHub never had the label.

## 2026-11-05 — Packages in the main list, Add Links dialog

- Replaced the Download Groups window with JDownloader-style **packages** in the download list: split
  archives (`.partN.rar`, `.rNN`, `.7z.NNN`, `.zip.NNN`/`.zNN`, `.NNN`) group automatically per folder;
  each Add Links / Download All batch becomes one package; manual Group into / Remove from / Rename Package.
  Packages under two files dissolve. Host pushes `groups.changed`. Evidence: `MultipartArchiveNameTests`,
  `SplitArchivePartsAndBatchesBecomePackagesAndSurviveRestart` (Core 107, Engine 57, Integration 96 pass).
- "Add Batch Download" pattern dialog replaced by **Add Links…** (paste any text → review checklist →
  optional "Keep as one package"). CLI `batch` keeps the pattern expander.
- Status: **unverified in the live UI** (row tree, expand/collapse, both themes) — needs a QA pass.

## 2026-11-05 — Dialog pass and live Edge check

- Walked Add Links, Move/Rename, Properties, Delete and Change category in the QA profile (light theme).
  Fixed: list selection fell back to row 0 after a rebuild (toolbar acted on the wrong file); Ctrl+M on
  the context-flyout item crashed WinUI natively (moved to the list); dialogs ignored the light theme;
  Move/Rename now has name + folder + Browse and is enabled only for completed files; Properties is a
  selectable grid; Delete hides the file checkbox for partial downloads.
- Edge 156 (separate profile, `--load-extension`): popup (light/dark), options, Download All link
  scan, and the media panel (light/dark) render and work. Edge falls back to Chrome's native-host key.
  Not exercised: an actual Edge handoff into the QA host (it would reach the installed app).
