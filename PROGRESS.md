# Progress — session handoff

### Web UI in the Windows app, and remote management (2026-11-08, requested)

- Owner, 2026-11-08: "put webui to installer too. have it be possible to enable webui from desktop app. like how qbittorrent does." and "since host is separate, like transmission, have desktop or webui be able to connect to each other easily. maybe I want to manage my homeserver from my desktop ui or vice versa." Recorded as ROADMAP D8 and D9; the design goes to the owner before it is built. The 0.6.3 release stays paused.
- 2026-11-09: mapped the Host, server, window, web UI and packaging, and how qBittorrent and Transmission do it; wrote `docs/plans/remote-management.md`; three reviewers found that the first draft would have let a browser run programs on the PC (open-when-done, schedules), act on files outside its folders, and leak the Windows sign-in hash through UNC paths. The revised design uses a bridge process started by the Host, web-role rights with an allow-list, text checks on paths, and a firewall step that clears block rules. Two questions wait for the owner. REV.4 (no internet mark on downloads) recorded.
- Owner, 2026-11-09: review the web UI against the desktop window ("work flow and features differ in an awkward way. some missing, some work differently. overall look is awesome though. I prefer desktop version."). Recorded as D2c. Reviewed: 174 differences kept after a second check against the code (`docs/plans/webui-desktop-review.md`), with five batches of work.
- Owner, 2026-11-09: "start batch 1 and all others at once if possible. use cheaper models for simple tasks." Built as seven branches from 0a9092c, one per screen (list, add, details and packages, provider and extractions, Options, queues, plus the three desktop bugs), Sonnet for the screens and Haiku for the desktop bugs and the diff reviews; each branch had a diff review and a parity review against the desktop code, then a fix round. Merged on `d2c-merge` (duplicate implementations of the sidebar gating, the provider and archive row actions and the completion notices folded into one each) and fast-forwarded into `dev` (ec7544d). svelte-check 0 errors, vitest 274, build ok, WinUI build ok, Core AddLines 104. Host changes the web side still needs are listed in the branch reports: speed history for the graph, a container upload endpoint, an archive capability in the hello, a web add source that opens no windows, refresh-address on completed jobs, file listing in folders.browse for archives, a file download for "Save to this device". A browser walk of every screen is running.
- Owner, 2026-11-09: native desktop window for other servers ("Native desktop window"), and "The app's folders" for browsers. Owner restated the goal (Setup installs the web UI, off by default and turned on from the window, runs like on Docker, both the web UI and the window can switch hosts) and accepted the recommendation that the web UI switches with a dropdown opening each host's own web UI rather than one page for all hosts. Building in stages from `dev`: web-role rights and server token roles first, then the bridge, Options page and Setup, then the native remote window.

### Paused: 0.6.3 release half done (2026-11-08)

- Owner paused the session ("pause here, I have more ideas suddenly. save work"). Nothing of 0.6.3 is pushed or released.
- Done: addons.mozilla.org signed extension 0.5.3 as unlisted (version 0.5.3 is now used up there). The signed XPI (SHA-256 17dfe6e0...b5a1d) is in `artifacts\firefox-addon\` of the main checkout and of `dev`. Firefox 159.0.1 in a fresh test profile installed it permanently (signature accepted); the unsigned zip was refused with ERROR_SIGNEDSTATE_REQUIRED. `extensions/firefox-updates.json` points at `releases/download/v0.6.3/inbound-bridge-0.5.3-firefox.xpi` with that hash.
- First real signing found that web-ext rewrites `manifest.json` (drops the final newline); the check now compares that one file by its JSON content. The signed file was kept and reused, not uploaded again.
- The release squash for 0.6.3 is local only: commit 2aee7e5 on top of `main` 0c99668 (local branch `release-0.6.3-wip`, worktree `.claude\worktrees\rel063`), without the local-only docs. Setup, the package and the release files were not built yet (the build was stopped for EXT.S5).
- To resume: add EXT.S5 if wanted (needs a new squash), then in the release tree build the package and Setup, run prepare-release, push the tag, create the release with six files, then push `main`. Do not run the sign script for 0.5.3 again without `-SignedXpi`.

### Firefox add-on self-distributed (2026-11-08, `dev`)

- Owner, 2026-11-08: keep the Firefox add-on unlisted, publish the XPI on GitHub, and have Setup install it when a box is ticked; asked whether they must hand over each XPI. No: with an AMO API key pair (Developer Hub, Manage API Keys) saved outside the repository, `scripts\sign-extension-firefox.ps1` signs each version. ROADMAP EXT.S4 has the design and what was checked. Extension 0.5.3 carries the `update_url`; the owner's current copy (0.5.1/0.5.2 from AMO) has none, so it has to be added again once from the 0.6.3 Setup or the release XPI.

### 0.6.2: the last open items (2026-11-08, `dev`)

- Extension 0.5.2 (owner, 2026-11-08: the 0.5.1 upload to Firefox Add-ons went in unlisted, and a listed one needs a new version). It adds the pairing-path wording on top of 0.5.1; 161 extension tests pass. Zips for both stores are in the main checkout under artifacts, chrome-store and firefox-addon. The Firefox privacy-policy field took the policy text, not a link; addons.mozilla.org answered a first, longer paste with 406 Not Acceptable (cause not known).
- Owner, 2026-11-08: 0.5.1 is waiting for Chrome Web Store review; the Chrome listing moves to 0.5.2 later. The bump is on `main` (0c99668).
- **Released 2026-11-08** (tag `v0.6.2`, squashed to `main` as 541056b without the Komodo guide and the mini PC record). The assets were built from that commit (binaries `0.6.2+541056b`), not from `dev`: a first build from `dev` carried `docs/komodo.md` in the package and was deleted before upload. Checks: checksums and signature verified, the latest feed serves 0.6.2, Setup's hash on GitHub matches, the token scan found nothing, the Docker image workflow for `v0.6.2` passed. Assets: `artifacts\release\v0.6.2\` in the main checkout. The extension is unchanged (0.5.1).
- Owner, 2026-11-08: "Just finish what's left and make a final release." Done for 0.6.2: UI.B12 (a Host started beside an exiting one takes over instead of quitting), UI.R23 (1), (2), (4), (6), PERF.7 (short-lived clients skip the snapshot), TEST.4 (cause found: the first `WM_CLOSE` went to Options; scripts fixed), UI.B9 (not reproduced). UI.R23 (5) is not built, because the row says only if wanted.
- Built in four worktrees, each reviewed by two adversarial reviewers and fixed. Merged onto `dev` by cherry-pick (the worktrees had been cut from `main`). A review of the merge found that the PERF.7 no-events probe missed the exiting broadcast; an exiting Host now answers `host.openUi` with `host.exiting`, with a test that fails without the fix.
- Every suite passed on the merged tree (`artifacts\campaign\20261108-062\`): Core 912, Engine 162, Integration 1,294, TorBox 323, Archives 351, Containers 187, Server 86, extension 161, web UI 47, svelte-check 0 errors. After the merge fix, the dispatcher, restart and hello-pipe tests passed 41 of 41, before the full Integration rerun.
- Still for the owner: the UI.R4 on-screen review, the light-mode walk, AMO listing, the extension store uploads, and the server's sign-in password. PERF.4's last item (the NVIDIA driver's two threads in the window process) can't be changed by Inbound; PERF.5 ends the window process on close by default.

### 0.6.1 released; server running on the owner's mini PC (2026-11-08)

- 0.6.1 is on GitHub (tag `v0.6.1`, squashed to `main`). The assets are in `artifacts\release\v0.6.1\`. Checks: signature and checksums verified, the latest feed serves 0.6.1, Setup's hash on GitHub matches, binaries are `0.6.1+f460462`, and the token scan found nothing. The Docker image workflow for `v0.6.1` passed. Extension 0.5.1 zips for the owner: `artifacts\chrome-store\` and `artifacts\firefox-addon\`.
- Mini PC (Ubuntu 24.04, amd64, Komodo 2): the stack `inbound` runs `ghcr.io/kaan88/inbound:0.6.1` as a Komodo "Files On Server" stack. Its download roots are the same two disks the other download manager used; the first one is the default. It runs as `1000:1000` on host port 8095. After start it was healthy, at 39 MiB, with no warnings in its log. The owner set up the Komodo stack. When checked, the sign-in password was not set yet.

### Komodo guide (2026-11-08, `dev`, after 0.6.1)

- Owner asked for user instructions for Komodo: `docs/komodo.md` (ROADMAP D3c). Komodo's labels come from its UI source, because its docs site answered 404. Reviewed by three agents; the confirmed findings are fixed.
- Found on the way: the pairing path "Settings, Paired browsers" named a heading, not a tab. The tab is "Sign-in and browsers". Fixed in the extension's options page and pairing error, and in the server's wrong-code message; these ship with the next release. Extension 161 and Server 86 tests pass.

### 0.6.1 release preparation (2026-11-08, `dev`)

- Owner, 2026-11-08: "put a link to my firefox addon page to setup" (ROADMAP EXT.S3). `INSTALL.txt` names the Chrome Web Store page and the Firefox Add-ons page, and Setup's last page has two unticked boxes that open them. The README and `docs/browser-integration.md` link them too.
- The Firefox page is not public: AMO's API answers "Authentication credentials were not provided" for the slug `inbound-download-manager` (a slug that doesn't exist gives "Not found"), so the add-on exists but is unlisted or awaiting review. The owner was told how to submit a listed version.
- Versions: app 0.6.1, extension 0.5.1.
- `scripts/install.ps1` writes the same two links into its `INSTALL.txt` (found by the Haiku review of the release commit, which also found a garbled evidence path here).
- `scripts/publish.ps1` without `-SelfContained` failed since REV.3: such a publish carries no WinUI language files, so the check for the English ones always threw. The check now runs only for a self-contained publish, which Setup and the update package use.
- Every Windows suite passed (`artifacts\campaign\20261108-061\`): Core 895, Engine 162, Integration 1,269, TorBox 323, Archives 351, Containers 187, Server 86, extension 161, web UI 44, svelte-check 0 errors.

### After 0.6.0: password boxes, Firefox popup size, target switch (2026-11-08, `dev`)

- TB.S4 (owner): in Add Links a hoster link's password section has an archive password box first, a tick to use it as the site password too, and a separate site password box. The two never stand in for each other unless ticked. The retry dialog and the web UI's Add Links say the same. Core and Integration tests pass; not seen on screen.
- EXT.B4 (owner's screenshot): Firefox opened the popup small with a scroll bar. The popup's size caps in `vw`/`vh` clamped it to Firefox's first, small layout viewport. They are fixed sizes now; measured in a stand-in viewport, not yet in a real Firefox popup.
- EXT.B5 (owner): Firefox showed its own Save dialog before the extension could catch the download, and the app then ignored the folder chosen there. The owner chose to catch it before the dialog. The Firefox build now looks at each page or frame load's response with blocking `webRequest`, and hands over one Firefox would save; the request is cancelled only once the app accepted it. In Firefox 159 with "Always ask" on, a clicked link and a typed address reached a test Host with no download begun in Firefox and the tab left on its page (`artifacts\evidence\dev-final\firefox-ext-b5\`). Not yet in the owner's Firefox.
- EXT.R6 (owner): the popup has a This PC / Server switch once a server is paired. It sets the same default target as Settings.
- The owner asked whether the extension got more controls: EXT.R2 (the newest 20 downloads with pause, resume, retry and delete) shipped in extension 0.5.0. In the owner's Firefox the small popup hid that list below the scroll.
- `npm test` 151 pass, Chrome and Firefox builds made; Core 895 pass; the TB.S2 and new archive-password Integration tests pass.

### 0.6.0 released (2026-11-08, squashed to `main`, tag `v0.6.0`)

- All work branches are merged into `dev`: streams A to C, the campaign, perf, webui, Edge, and the six owner-answer branches. Their worktrees and branches are removed. The evidence they held is in `artifacts\evidence\<branch>\` in the main checkout: logs, trx files and summaries, with no payloads or browser profiles. It was scanned for credentials and none were found.
- Fixes found while merging:
  - The register-native-host path in ROADMAP and PROGRESS was split by a stray CR.
  - The golden frames now normalise this PC's browser registration (`nativeHostRegistered`, `browserIntegration`).
  - PERF.5 is turned on once for upgraded installs (a settings marker).
  - The FTP resume test was counting bytes that another test had fetched.
- A four-agent semantic-merge review found nothing. It covered the files that two branches both changed: `DownloadManagerService.cs`, `HttpDownloadEngine.cs`, `MainWindow.xaml.cs` and `Dtos.cs`.
- **Windows** (Release, `artifacts\campaign\20261108-final\`): every suite passed.

  | Suite | Passed |
  | --- | --- |
  | Core | 888 |
  | Engine | 161 |
  | TorBox | 323 |
  | Archives | 336 |
  | Containers | 187 |
  | Server | 84 |
  | Integration | 1,263 (a rerun after the FTP test fix; the first run's only failure was that test) |
  | Extension | typecheck and 143 tests |
  | Web UI | 37 |

- **Linux** (the VM, SDK 10 container, `Platform!=Windows`, in `...\linux\`): no failures.

  | Suite | Passed |
  | --- | --- |
  | Core | 881 |
  | Engine | 161 |
  | Archives | 274 (52 not run on Linux) |
  | Containers | 183 |
  | TorBox | 323 |
  | Server | 84 |
  | Integration | 1,118 (7 not run) |

  The VM's test image and folders are removed again. The owner's containers were not touched.
- **Release assets**, rebuilt from `056b424` after every fix below and uploaded to the GitHub release. They are in `artifacts\release\v0.6.0\`:
  - `DownloadManagerSetup-0.6.0.exe`: 55,954,754 bytes, SHA-256 `20df0c69daaf2a3ad971b1d54d61fd6da303e8a4382bb053bbe1d5b8179b1adb`.
  - `download-manager-0.6.0.zip` (self-contained): 79,240,453 bytes, SHA-256 `889c45a2b1ae8b71c0e562ced5fb19daee39b8f0614aaee505503d2b50a41e60`. Its binaries carry `0.6.0+056b424`.
  - `manifest.json`, signed with `secrets\update-signing.pem`. openssl verifies it with `public.pem`, which matches the key in `ReleaseUpdates.cs`.
  - `SHA256SUMS`: all four hashes match (`sha256sum -c`; the file now has LF line ends).
  - A token scan of the tree and the packages found none of the credentials file's values.
- **Extension 0.5.0:**
  - `artifacts\chrome-store\inbound-bridge-0.5.0-chrome.zip`.
  - `artifacts\firefox-addon\inbound-bridge-0.5.0-firefox.zip` (unsigned, for AMO).
- None of these packages contains a credential from the credentials file.
- Draft notes are in `artifacts\release\v0.6.0-notes.md`.
- **Reviews (2026-11-08), all fixed on `dev` (ROADMAP REV.1, REV.2):**
  - My release review: 17 confirmed findings, each fixed in its own branch (`fix/engine`, `fix/runtime`, `fix/persist`, `fix/server`, `fix/extension`, `fix/winui`, `fix/webui`) by an agent working from `bfee07b`.
    - I reviewed the engine, runtime, secrets and server diffs myself (concurrency, durability, security).
    - Haiku reviewed the extension, WinUI and web UI diffs. It found one more gap, fixed on `dev`: Download All read the default cookie store on Firefox.
  - The owner's ultrareview: 10 findings. Three were the same as mine: (1) is my 3, (3) is my 12, and (8) is next to my 4.
    - (2) The roots escape through provider adds, the one security finding, is fixed with two tests that fail without the fix.
    - (4) was real. My first check was wrong: its test zips held no backslash. Zips made on Windows now extract into their folders on Linux (ROADMAP REV.2): Archives 351/351 on Windows, and on the Linux VM 289 passed, 52 skipped, none failed, with every new test passing.
    - (5)–(10) are fixed; ROADMAP REV.2 has each one.
  - Install folder trimmed (ROADMAP REV.3, owner's question): 281 MB to 201 MB, Setup 77.5 MB to 56.0 MB, by taking only the Windows App SDK parts the app uses and leaving out other languages and .NET's debugger files. Checked on a test instance and on the clean VM.
  - Not checked in a real browser: Firefox's `cookieStoreId`, `storage.local.setAccessLevel` and `permissions.contains`, which the extension tests stand in for.
  - Not seen on screen: the Options Theme box and the refused install dialog.
  - EXT.R5 (Chrome and Edge with site access withdrawn by the user): fixed before release; such a download now stays in the browser with a note.
- **After the review fixes** (`2adb35b`; later commits touch only the installer, docs and the window's trace file), every Windows suite passed: Core 895, Engine 162, TorBox 323, Archives 351, Containers 187, Server 86, Integration 1,268, and the extension's 151.
- **Clean VM with the final build** (ROADMAP TEST.2, evidence in `artifacts\evidence\dev-final\vm-0.6.0-056b424\`):
  - Setup took 0.5.3 to 0.6.0 and kept both jobs. Uninstall left no program folder and kept the state; a fresh install started with the kept jobs.
  - The update helper took 0.5.3 to 0.6.0 (exit 0, result `succeeded`), and Settings > Apps showed 0.6.0 (UP.B2).
  - The window started each time from a 199.3 MB folder (REV.3).
  - Found and fixed on the way: the window's start-up trace was left in the install folder after uninstall. It now goes to the logs folder.
- Owner, 2026-11-08: two ideas for later, recorded as ROADMAP D6 and D7.
- **Left for the owner, and what was settled after the release:**
  - The owner's parts: the Chrome Web Store and AMO uploads of extension 0.5.0.
  - PERF.2, now done: no sign of a leak. With a fixed list, the lowest heap samples kept coming back down (43 MiB in hour 9 of the 8-hour run) and private memory came back to 140 MiB; no heap dumps were compared (ROADMAP PERF.2, `artifacts\evidence\perf\heap-floors.txt`). The rerun on `dev` (`soak-fixed`) was stopped after an hour (10:50 to 11:53 UTC) by the owner's choice. Both processes ran throughout; the Host rose 147.5 to 190.1 MB (+24 MB/h, like the 8-hour run), the window 302.5 to 331.9 MB. Summary in `artifacts\soak\soak-fixed\summary.txt`; payload deleted.
  - EXT.B3, checked after the release: Firefox 159 with a test profile caught a download, and the installed native host handed it to a 0.6.0 test Host, which finished it. In the owner's first attempt the 0.5.3 Host was shut down 7.5 s after the request, which closed the bridge, and a closed bridge is reported as "unavailable". The second attempt's answer is not in the logs. Still to see: the owner's own Firefox once 0.6.0 is installed.
  - TEST.3: `scripts\register-test-native-host.ps1` registers a test build for Chrome for Testing only (the Chromium key) and refuses when another test holds the key (`artifacts\evidence\dev-final\test3-register\run.txt`).
  - D1 marked implemented, as planned for 0.6.0.
  - Run on test builds (evidence in `artifacts\evidence\dev-final\`):
    - UI.B7, the owner's scenario: six downloads on one host. Raising the limit from 3 to 6 got all six transferring. Lowering it to 2 sent the four newest back to Queued with their data kept.
    - UI.R20: real Shift+Delete ticks "Delete from disk"; Delete and the toolbar don't.
    - UI.R6: the Size, Time left and Transfer rate values share a right edge with their headers.
    - TEST.3: a Chrome for Testing handoff went through the test registration, and Chrome's key was unchanged.
  - TorBox live, read-only: 45 of 45 pass after updating three tests that expected the old cap of 4, and one that expected the old key form in links. TorBox now gives a torrent link the key without dashes when asked that way (TB.L3), which doesn't affect the app.
  - TB.S1's queue read checked live; Start now and Remove couldn't be, with nothing queued. TB.S2 is blocked: no password-protected hoster link (later verified by the owner, below).
- Owner answers and checks (2026-11-08):
  - Done by the owner's word: EXT.S2 and EXT.E1 (the owner does the store and AMO side), D.PLAN. UI.B3 confirmed. TB.F4's open choice settled: "fine", as built.
  - TB.S2 verified by the owner: a gofile link worked, but only with "Also try it on this link's own archives" ticked. The site password itself is the hoster link's password, not an archive's, so its archive most likely had the password.
  - TB.L2 removed from the roadmap (owner: "TB.L2, remove", read as the row). Nothing was deleted from the TorBox account, and `KeptArchiveTests` still use the archive.
  - D3a and D2a run with the published 0.6.0 image on the owner's Linux VM, D3b read from GitHub's run list. Default user 1000:1000, PUID 1234 honoured, PUID 0 refused with exit 2. A sign-in survives `docker restart`, and after signing out the cookie stays signed out across one. Evidence in `artifacts\evidence\dev-final\vm-d2a-d3a\`. The test containers, folders and image were removed afterwards (`cleanup-check.txt`).

### Owner answers built in the WinUI app (2026-11-08, branch `owner-winui`)

- **PERF.5:** "Closing the window ends it" is on by default for new installs. A stored value is kept, and the settings file stores every value, so an upgraded install whose settings were saved since 0.5.3 (when the option arrived) keeps it off. Turning it on there needs a one-time settings migration: asked below.
- **UI.R25:** the Remove items in the right-click and File menus are now "Delete…", the same dialog as the Delete key.
- **UI.B8:** every `ContentDialog` goes through `DialogGuard`; a second one is refused and counts as cancel. Grep finds no direct `ShowAsync` left; a test-instance repro (Delete dialog open, Delete Completed and Add URL invoked) kept the window alive.
- **UI.R23:** Statistics has a Close button; the settings' Theme (System, Light, Dark) is the theme source for every window, with a Theme box in Options and View > Dark Mode as a toggle; the old `darkMode` moves into it once. A Windows theme change while running was not checked.
- Tests: Core 881 passed (12 new); Integration 1,236 of 1,237 passed, the one failure the golden frames expecting the old default, now updated; after it the golden test still differs only in `nativeHostRegistered` (true on this PC since the owner registered the browsers today; TEST.3), not from this work.

Review of this branch (2026-11-08), each finding checked against the code:
- Fixed: the right-click menu's Delete… on an archive record removed the record, not the download behind it, which the old Remove deleted (`DeleteRules`; the File menu, toolbar and Delete key never acted on a record's download and still don't).
- Fixed: the Hosters window still read the old `darkMode` and did not follow the Theme setting; it now uses `AppTheme` like every other window (a source test checks that every window does, and that only `AppTheme` reads `darkMode`).
- Fixed: a View > Dark Mode click before the Host had saved the old value could be undone by the next snapshot and the old value sent to the Host. The click now keeps its own choice in ui-state until the Host has saved it, a save in progress picks up a later click, and an Options save drops any kept choice.
- Fixed: a download or package window that opened before the main window moved `darkMode` never followed the setting; it now reads ui-state again on each snapshot until the value has gone.
- Fixed: `DialogGuard` refuses a dialog without a `XamlRoot` (it could not show anyway); a source test checks all 44 dialogs set one.
- Docs: the design guidelines described the old theme source and a capture method that no longer works; `docs/architecture.md` says closing ends the window's process by default.
- Not a code defect: closing ending the process by default is the owner's PERF.5 decision. The tray, a second launch or the Start menu entry brings the window back; no tray check is made.
- Question for the owner (PERF.5): an install whose settings were saved since 0.5.3 has "Closing the window ends it" stored as off and keeps it. Turn it on once for those installs with a settings migration, or keep the stored value? Answered by the owner's "On by default": on dev a settings load with no `settings.json.perf5` marker beside it turns the option on once and writes the marker; every save from this build writes it, so a later choice of off is kept (`WindowSettingsDefaultsTests.AnUpgradedInstallThatStoredOffGetsItOnOnce`).
- Not checked: the theme and Delete changes in the running window; a Windows theme change while the app runs.
- Tests after these fixes: WinUI build clean (one older nullable warning); Core 888 passed (7 new); Integration 1,236 of 1,237 passed, the one failure the golden frames' `nativeHostRegistered` (true on this PC, TEST.3).

### Host speed history and full queues (2026-11-08, branch `owner-host`)

- **UI.R24 (implemented, automated tests; the graph not checked on screen):** owner: "Keep the hour". The Host keeps the last hour of total download speed in a 3,600-slot ring buffer (`SpeedHistory`), written from progress reports with no timer; idle seconds are filled in as zeros from the times alone, so an idle Host does no work for it. A window asks for it (`speed.history`, new and additive, no protocol bump) on each connection and the speed graph merges it in, so the hour survives a closed window.
- **Full queues (implemented, automated tests):** owner: "Yes, same rule". Moving a running download into a full queue now runs the lowered-limit check, so the queue's newest extra download goes back to Queued with its data; downloads that can't resume (and verifying, finishing or muxing ones) keep running.
- **UI.R24 review fixes:** each download's figure now holds only for two seconds after its own report, so one that went quiet or hung (after a pause, say) stops counting while others keep reporting; a paused, cancelled or removal-pending job's late report drops its share at once; the progress callback reads the clock under the lock, so no report is dropped as coming from an earlier second; and an AES-128 HLS segment reports progress while it arrives (only bytes that are surely plaintext are counted, so the count never goes backwards), instead of only after it is decrypted. Tests: two `SpeedHistory` unit tests, an AES-128 test with a slow first segment (`aes-slow.m3u8`), a pause test through the service, and the Host test now runs a download through the pipe and checks that the history shows it. The AES and unit tests fail without the fixes. The pause test passes either way, because the runner's own stop clears the share right after the cancel; the hang case is covered by the unit test.
- Tests (Release), rerun after the review fixes: Core 869/869, Engine 159/159; Integration 1,245 of 1,246 passed (earlier run before the fixes: 1,241 of 1,242). The one failure, `HostGoldenFramesTests.AScriptedSessionSendsTheSameFrames`, differs only in `nativeHostRegistered: true`: this PC now has the `com.kaank.downloadmanager` Chrome registration (the owner ran `register-native-host.ps1` today), which the Host's status reads. Environmental, not from this branch.

### Owner answers built on the server side (`owner-server`, 2026-11-08)

- **D2a, sessions survive a restart:** `<data>/sessions.json` with hashes only (never a cookie's id), atomic and mode 600, restored at start without expired ones; sign-in, sign-out and password change written at once (an expired one leaves the file at the next write or start), last-use times at most every 10 minutes and on a clean stop, and for an open WebSocket on the same beat. A password changed while the server was down drops them. `SessionPersistenceTests` (5), `SessionDurabilityTests` (5).
- **Review fixes (2026-11-08):** a password change and a racing sign-in or save can't leave an old session under the new stamp (one lock; the sign-in is checked against the password it verified); a listening WebSocket refreshes last-seen; a failed write deletes or empties the file and a folder refusing both is logged; directory fsync after a replace off Windows (libc, best effort, **not run on Linux**); sign-in off removes the saved sessions; `AuthStore.FindToken` also swallows `UnauthorizedAccessException`; Node is named once in the Dockerfile (a `node-base` stage) so a Dependabot PR can't split the two stages (**image not rebuilt**, no Docker here); an upgrade note for the old 1654 default in `docs/docker.md`, and the entrypoint's warning names the folder's owner; doc wording on expiry and the token's scope corrected.
- **D2b, `DELETE /api/pair/self`:** the extension's Unpair; the bearer token authenticates as on `/api/ipc` and removes only its own record, 204. CORS for our extension origins. `PairSelfTests` (3). Contract in `docs/ipc-protocol.md`.
- **D3a, PUID/PGID 1000:** Dockerfile, entrypoint and docs; ids used only as numbers, so an existing account with that id changes nothing. **Partial: the image was not rebuilt or run** (no Docker on this PC).
- **D3b, Dependabot:** `.github/dependabot.yml` for `docker` and `github-actions`, weekly, majors of the base images ignored, nothing auto-merges. The base images moved from `ARG`s into the `FROM` lines so Dependabot can read them. Not checked on GitHub.
- Tests (Windows, Release): Server.Tests 84/84 after the review fixes (79 before), Core 869/869. No review by a cheaper model: this agent had no way to start one.

### Extension: Download All default folder, Unpair self-revoke (2026-11-08, branch `owner-ext`)

- **EXT.R3** (owner: "Show the default"): Download All's Save in shows the chosen target's default folder from `dm.targets` (the app's, or the server's), switches with the target, and is sent only when changed, so the app still decides when untouched. The EXT.B2 gate and held target radios are unchanged.
- **EXT.R4** (owner: "Add self-revoke"): Unpair forgets the token, then calls `DELETE /api/pair/self` with the bearer token (4 s); on 204 Settings says the server removed it too, otherwise it keeps the advice to remove it in the web UI. The server endpoint is built on another branch; not yet run against it.
- Review fixes: a folder typed in Save in before the late `dm.targets` answer is kept (a later target switch still shows that target's default); Unpair with nothing paired says "Not paired" rather than advising removal on a server; `revokeSelf` time-out and wrong-token 401 are tested. Open for the owner: Unpair forgets the token before calling `DELETE /api/pair/self` (the stated order was the reverse), so no download can use the token meanwhile; the end state is the same either way.
- Evidence: extension `npx tsc --noEmit` clean, `npm test` 143/143 (new: 3 page tests in `form-gate.test.cjs`, 9 in `server-target.test.cjs`, 1 in `transport.test.cjs`; 13 in all), `npm run build` and `npm run build:firefox` succeed. Docs: `docs/browser-integration.md`, `docs/chrome-extension-privacy.md`. Not checked on screen.

### TorBox kept archive, TB.L2 (2026-11-08, branch `owner-torbox-file`)

- Added web download `inbound-live-check-archive.zip` (id 2240130) to the account and turned Airlock on ("yes add one and airlock it"). Content: a 1.29 MB public domain zip from archive.org (Project Gutenberg eBook 14838), no password. TorBox can't take a local file, GitHub and sqlite.org links were refused, so not our own zip.
- A fresh `mylist` read: `cached: true`, `airlocked: true`, tag `inbound-live-check`, one 1,291,466-byte zip. `expires_at` still said 2026-12-08 after Airlock; whether Airlock lifts it is unverified.
- Live tests now remove items only through `LiveAccount.RemoveUnlessKeptAsync`, which skips `inbound-live-check*` names. `KeptArchiveTests`: 9 pass without a key; with the key all 10 pass, including the live read of the item. LiveTests project without a key: 9 passed, 30 skipped.
- API calls: 7 in all (2 refused submissions, 1 submission, 1 read, 1 edit, 1 read, 1 list from the live test). No key or link was printed, logged or committed.
- Review fixes (2026-11-08): TB.L2 is now **partial**, not done: the keeping past 30 days is unverified until a read after 2026-12-08, and Airlock's meaning is the owner's description (TorBox's schema has only an undescribed `airlocked` field). The removal guard now scans subfolders, catches any `RemoveAsync` mention outside the helper's body (spaced calls, method groups), `RemoteQueueAction.Remove`, `controlqueued` and any "delete" string, with sample-text tests for each. The kept-name rule needs a non-letter, non-digit after the prefix. The live read compares TorBox's md5 when given, and a new live test downloads the file and checks its md5 (not yet run: no key in this session). Docs say the protection covers live test cleanup only. LiveTests project without a key: 24 passed, 31 skipped.

### UI.R21 "Apply to all": connection changes reach running provider, protected and FTP downloads (2026-11-08, branch `owner-engine`)

- Owner decision (2026-11-08): "Apply to all". Before, a changed connections-per-download reached only the user's own running HTTP downloads; provider and protected jobs were left out because their real host and the provider's cap are known only to the run (the job's own address is a reference or masked).
- **Built:** a provider run now keeps its link's host and the provider's per-file cap in memory (`JobRuntime.RunLink`), so a running TorBox job takes the new count at once, clamped by the cap (16) and the host limit of that link. A change before the run has its link is taken when the link comes. A protected (pasted provider) link follows unless its count was chosen by hand, capped at 16. New workers come from the existing steal path and queue on the per-host fair limiter; no link refresh is asked for or counted. FTP is one connection by design (the FTP engine has no segments), so a change while one runs is a no-op; written up in `docs/architecture.md`.
- **Tests (Release):** Core 869/869, Engine 161/161, TorBox 323/323. Integration 1,236/1,239 on the first full run: `RuntimeAndContractsNameNoConcreteProvider` caught a provider name in a new comment (fixed, passes); `FtpRequiresLoginAndResumesFromPartialBytes` failed once under the full run and passed 4 of 4 alone; `HostGoldenFramesTests.AScriptedSessionSendsTheSameFrames` expects `nativeHostRegistered: false` and this PC's user registry has the installed app's Chrome registration (environmental, not from this change).
- **New tests, each run 10 times:** Engine `ARaisedCountOnAProviderLinkStaysWithinTheHostLimit` and `EveryCheckpointDuringAConnectionChangeResumesToTheRightFile` 10/10; Integration `AChangedConnectionCountReachesARunningTorBoxDownloadWithinItsCap`, `ARaisedCountOnARunningTorBoxDownloadStaysWithinThePerHostLimit`, `AChangeBeforeAProviderRunHasItsLinkIsTakenWhenTheLinkComes`, `AChangedConnectionCountReachesARunningProtectedLinkWithinTheProviderCap`, `AChangedConnectionCountLeavesARunningFtpDownloadAlone` and the three `KilledWhileAConnectionChangeReachesAProviderDownload_TheCheckpointHoldsAndTheBytesMatch` cells (real Host, MockTorBox, killed right after the change is saved, while the new workers connect, and after a checkpoint with 12 split ranges) 10/10. Results in `artifacts\owner-engine\`.
- Not checked on screen; no live TorBox run.
- Review fixes (main-model review, commit 6c0d5e1): the segmented run is registered before its worker count is read, so a change landing in between is in the count or wakes the run; a worker that waited for a host connection while the count went down retires before it claims a range; every input of the default count (default, per-host limit, connection profiles) reaches running downloads; a resumed provider job drops the previous run's link. A single-stream run (count 1 at start, one-segment checkpoint, ranges refused, file too small) keeps one connection; its record still takes the new count (`docs/architecture.md`).
- Final run after the fixes (Release, `owner-engine`): Core 869/869, Engine 161/161, TorBox 323/323; Integration 1,238 of 1,241. The three failures: the golden frames (this PC's browser registration, normalised on `dev` since), and `KilledDuringExtractionAndDuringArchiveCleanup_OneCopyComesOutAndTheSiblingStays` and `FtpRequiresLoginAndResumesFromPartialBytes`, which passed 3 of 3 alone. The FTP one was a test defect, not load: the class shares one FTP server, and the new `RuntimeLiveConnectionTests` FTP case fetches `slow.bin` too, so whenever it ran first the "served under 1.5 times the file" check counted its bytes (884,736 served in 12 of 12 runs with both tests). It now measures the bytes served during its own download; 5 of 5 passed with both tests. The UI.R21 set (live connection, kill-during-change, per-host-limit-alone, change-after-resume) passed 10 of 10 repeated runs.

### Owner answers to the open questions (2026-11-08, asked in chat)

To build (each recorded in ROADMAP):
- **PERF.5:** "Closing the window ends it" on by default.
- **Remove menu items:** drop Remove from the right-click and File menus ("just delete? seems remove seems redundant"); Delete and Shift+Delete stay.
- **UI.B8:** guard every dialog with the shared helper, not only delete.
- **UI.R23:** the Statistics window gets a Close button. The settings' Theme value becomes the theme source (System, Light or Dark; System follows Windows); View > Dark Mode sets it.
- **UI.R24:** the Host keeps the speed graph's last hour, so it survives a closed window.
- **Download all links:** Save in shows the app's default folder, like the folder window.
- **UI.R21:** a changed connection count also reaches running TorBox, protected and FTP downloads ("Apply to all").
- **Full queues:** moving a running download into a full queue sends the queue's newest extra download back to Queued, the same rule as settings and queue changes.
- **Web UI sessions** survive a server restart (same 24 h idle / 7 day lengths).
- **Pairing:** an extension's token can revoke itself, so Unpair also removes it on the server.
- **Docker:** default PUID/PGID 1000; Dependabot opens update PRs for the digest-pinned base images.
- **TorBox:** keep one small archive in the account for live extraction checks, kept out of any test cleanup, with TorBox's Airlock turned on for it ("yes add one and airlock it"; owner: Airlock is a TorBox feature that keeps a file from being deleted after 30 days).

Kept as they are: CLI downloads get no completion window while the window is closed; no cap on segment retries while bytes arrive; downloads that can't resume keep running when the simultaneous limit drops; no separate per-host default for TorBox ("don't seperate hosters, options changes for all"); media downloads count under the per-host limit; program-running settings stay refused on a network server; the setup code for a first remote setup; minimum password 8; tokens don't expire; any Firefox extension may try to pair (the code is the credential); the This PC / Server pick is per download; a failed target leaves the download in the browser; root (PUID=0) refused; FFmpeg not in the image; a lost `secrets.key` stops the server; no extraction where the hard-link check can't run. Close button: the owner never needed two clicks (TEST.4 is a script-only effect).
Later: a live expiring-link check with a real TorBox download (only the mock so far); the arm64 image; the second 8-hour soak runs without anything waiting for it.

### `webui` merged; owner answers (2026-11-08)

- `webui` (D2 web UI with sign-in and pairing, D3 Docker image and compose, the extension's server transport, D4 on the VM) merged into `dev`. The extension's folder and batch windows conflicted (the EXT.B2 gate against the This PC / Server choice) and were combined by hand. A review of that merge found real gaps, now fixed: the target radios stayed live during a folder pick or a send (a local folder could go into a server request), the server folder list could stay open during a send, and a request for a server unpaired meanwhile went to this PC with its server folder (now refused).
- Full runs on merged `dev`:
  - Windows, all green: Core 869, Engine 159, Integration 1,231, TorBox 323, Archives 336, Containers 187, Server 71; extension 130; web UI 37, `svelte-check` clean. Results in `artifacts\campaign\20261108-merged-webui\`.
  - Linux (the owner's VM, .NET 10 SDK container, `Platform!=Windows`): Core 862, Engine 159, Archives 274 (52 skipped, need Rar.exe), Containers 183, TorBox 323, Server 71, Integration 1,090 (7 skipped). The first Integration run failed 50 tests that need the Windows Host or registry and had no Windows tag; they are tagged now.
- Firefox: addons.mozilla.org warned that `data_collection_permissions` needs Firefox 140 (Android 142) while the manifest said 128. The build now asks for 140.0 and `gecko_android` 142.0. Its warnings about `cookies.getPartitionKey` and `downloads.onDeterminingFilename` stay: the code checks for both and Firefox takes the other path.
- Owner answers, 2026-11-08:
  - UI.R22 (selected rows hard to see): leave it as is. Closed.
  - TB.L1: keep 16 connections per TorBox file.
  - Automatic sending on a fresh extension install: on. `DEFAULT_SETTINGS.autoIntercept` is now true; Settings still opens on install with the explanation; an update keeps the saved choice. Docs and the privacy page say so (the online copy needs the same change when this ships).
  - Image license label: the owner has no license in mind. The image keeps no `licenses` label until one is chosen.
  - UI.B11: Tasks > Exit and the tray's Exit quit the whole app (the Host), which pauses running downloads; closing the window does not. Owner: "leave as is". Closed.
  - EXT.B1 verified in the owner's Edge: the `dev` build caught a TorBox Direct Download.
  - Firefox (EXT.E1): the owner's add-on shows "Inbound unavailable": the installed 0.5.3 writes no Firefox native-messaging key. The next release's installer writes it; until then `scripts
  - The owner ran the script: the Firefox and Edge keys and both manifests are in place, and Firefox now starts the native host, which forwards `handoff.request` to the Host (native-host log, 12:07). Still "Inbound unavailable": 0.5.3's Host trusts only the Chrome extension IDs, so it refuses Firefox's add-on ID (EXT.B3). Firefox needs the next app release; the wrong notification text is still to explain. `scripts\register-native-host.ps1` from `dev` registers Chrome, Edge and Firefox for the installed app (run by the owner: this harness cannot write LocalAppData).

### Test campaign merged into `dev` (2026-11-08)

- Merged after review fixes: `campaign-recovery` (independent recovery check), `campaign-ext` (extension end to end in Chrome for Testing, EXT.B2), `campaign-ui` (dark and light walkthrough, UI.B8 dialog guard, layout and script fixes) and `perf` (UP.B2, PERF.2/3/5, UI.B13 to UI.B15, PERF.7).
- Row numbers clashed between the tracks and were renumbered before merging: recovery's exit-pause and silent-Host rows are UI.B11 and UI.B12; perf's pipe-client, CLI-source and segment rows are UI.B13 to UI.B15, its WM_CLOSE row TEST.4 and its speed-graph idea UI.R24.
- Review fixes, each checked against the code before changing it:
  - recovery: the uninstall script's own-install check now needs a trailing `\` (a sibling folder such as `...Manager2` matched before); the proxy check restores the caller's instance variables.
  - extension: Send and Browse share one state gate (a failed send during a pick could leave Send off for good, or turn it on while the app's folder dialog was open). Keep in browser and Escape now wait for a send in flight (it could keep the file in the browser and add it to the app too). The batch window redraws its rows after a send. The tests drive the real page code; the old substring test is gone.
  - UI: the dialog check was right (an open dialog's popup child is the `ContentDialog`, shown with a trace build); the shared `ShowDialogAsync` helper now covers delete, messages, questions and the address dialog, and treats any failure to show as "not confirmed". `ui-drive.ps1` no longer presses title-bar buttons, clicks only when the test UI is at the point (checked again right before the click), and `closewin:` needs a title.
  - perf: CLI `add-batch` and `import` send their own sources (`cliBatch`, `cliImport`), so a running main window no longer opens a completion window per job and imports stay out of recent addresses; the UP.B2 rollback test now reaches the rollback; a segment attempt that gets bytes on its last allowed try retries; `perf-window.sh` labels its rows right.
- Full run on the merged branch, all green with no reruns: Core 869, Engine 159, Integration 1,231, TorBox 323, Archives 336, Containers 187, Server 41, extension 103. Results: `artifacts\campaign\20261108-merged-campaign\` (trx).
- Still running: the 8-hour PERF.2 soak (old build, ends about 10:19 UTC) and the `webui` branch (D2 web UI, D3 Docker, the extension's server transport, D4).

### D4: end to end on the owner's VM through the web UI, from this PC (2026-11-08, `webui`)

Image `inbound-test:dev` rebuilt on the VM from `webui` `e8324ae` (`REVISION` label set; only the TestServer changed
since the D2-fix image, so the server layers came from the cache). Run with sign-in on, `-p 18080:8080`, `PUID`/`PGID`
of the VM user, `TZ`, volumes in a test folder, label `inbound-test`. Driven from this PC over the LAN with
Playwright's headless Chromium (a driver script in the session scratchpad, not committed); the VM address is in no
tracked file, screenshot or file name.

- **First open:** setup code field shown (browser not on the server's machine), code from `docker logs`, throwaway
  password set; cookie `HttpOnly`, `SameSite=Strict`. Sign-out, then `/api/ipc` 401; sign-in again worked. From this
  PC without a cookie 401, with a foreign `Origin` 403.
- **TorBox (live, minimal):** key read from the owner's credentials file into the page without printing it, saved in
  My TorBox; the page showed "Saved on the server" and the plan, and the key was nowhere in the page. In my account
  listed the torrents (one call); a small cached item already in the account (3 files, 18.4 MB) went through
  Download, Choose files (all), the default queue: three runs, each "torbox gave a link for this run", about 4 MB/s,
  two valid JPEGs (end marker `FFD9`) and a 20-byte README, owned by the VM user, no `.part` left. About eight
  TorBox calls; nothing added to or deleted from the account.
- **Extraction (stand-in):** the TorBox item has no archive. A 3-volume RAR5 with a password (WinRAR's `Rar.exe`
  here, `-v500k -p`) and a plain zip, 1.2 MB of random data and a text file, were served over HTTP by a labelled Node
  container (`node:24-alpine`, already on the VM) on a labelled test network. Added through Add links (the RAR as
  package `d4-sample`). The zip extracted by itself; the RAR showed "needs a password to extract", the password went
  in through the prompt, and it extracted. Both outputs have the originals' SHA-256 (`87a0c016…`, `d4abd5a9…`).
- **Expiring-link refresh (mock):** a live link can't be made to expire on demand. New TestServer option
  `--first-link-dies-after <bytes>` (commit `e8324ae`): the mock's first CDN link stops after that many bytes, cuts
  its connections and answers 403 after. A second server container pointed at the mock (`DOWNLOADMANAGER_INSTANCE`,
  `DOWNLOADMANAGER_TORBOX_API` on loopback; the TestServer in the same network namespace, built for Linux in an SDK
  container) was set up through its own web UI (port 18081) with the mock key. Download, choose `sub/video.bin`
  (8 MiB): "the link was refused (Forbidden, HTTP 403), so asking for a fresh one, 1 of 2", a new link, Completed;
  SHA-256 `1b30edc3…` equal to the mock's generated content.
- **No token:** the key, the key without dashes and its first 6 characters, held in shell variables and never
  printed: 0 in `docker logs` of both servers and the file server, 0 files in the data folder (logs, settings,
  jobs, remote state), 0 in the downloads, 0 in `docker history --no-trunc` and `docker inspect` of the image and the
  container, 0 of the image's 4,054 files (searched inside a throwaway container with `find -xdev`; a first `grep -r /`
  found only its own `/proc/1/cmdline`). The mock key was in neither mock-server log.
- **Memory (`docker stats`):** idle after sign-in and settings, 29 samples every 10 s: 44.0 MiB average (42.6 to
  45.5), CPU 0.016%. TorBox download and extractions (every 2 to 3 s): up to 87 MiB. A 400 MB download from the file
  server (8 ranges of 50 MiB, about 8 MB/s, SHA-256 checked): 87 to 106 MiB; the cgroup's anonymous memory went
  only from 50 to 56 MiB, the rest is page cache of the written file. Idle after the whole session, with the 400 MB
  file deleted through the web UI with its file: 100.8 MiB (100.6 to 101.8), CPU 0.016%. Of that, 64 MiB anonymous,
  33 MiB shared memory that is .NET's W^X double mapping of compiled code (`/memfd:doublemapper`, 201 mappings in a
  fresh server), 4 MiB kernel. Target under 100 MB idle: met before use, reached after a full session.
- **Cleanup:** the test containers, the test network and `~/inbound-d4` (downloads, data with the sealed key,
  sources, mock build) removed; `inbound-test:dev` kept; class-compass and komodo untouched; 8.8 GB free, as before.

Evidence: `artifacts\d4\` in the worktree (ignored): the screenshots (the account's item list among them, so keep
it local), the four memory CSVs, both servers' logs with addresses replaced. ROADMAP D2 and D4 and
`docs/docker-webui-plan.md` updated. Not run again: the Windows or Linux test suites (only the TestServer's
`Program.cs` changed; it builds on Windows and in the Linux SDK container). Not delegated to a cheaper reviewer (no
agent tool in this session).

Seen, not fixed (the tested image stays as built): the delete toast says "Deleted 1 download and their files" for
one download; "Put each kind of file in its category's folder" is on by default on the server, yet the zip and RAR
went to `/downloads` itself (not checked whether the server has category folders at all).

Questions for the owner (safer choice taken): (1) The refresh was shown with the mock, not a live link; is that
enough, or should a later run leave a live download paused past the link's lifetime? (2) Should a small archive
(a few MB) be kept in the TorBox account for future live extraction checks? None was added. (3) Idle after use is
about 101 MiB by `docker stats`; `DOTNET_EnableWriteXorExecute=0` would drop the 33 MiB double mapping but turns off
a hardening feature, so it was not tried; PERF.3 can measure it if wanted.

### D2 / EXT.R1 review fixes: sign-in waits, trusted proxies, bodies, auth.json, upload references (2026-11-08, `webui`)

Sixteen reviewer findings on `git diff dev...webui`, each checked against the code. Fixed (commit `a7aba23`):

- **Waits a burst can't skip (high):** sign-in, setup code, password change and pairing all checked the wait, then
  awaited the PBKDF2 check, and counted the failure only after it, so parallel guesses all passed the check. Now
  `SignInLimiter.Begin` counts a try as wrong when it starts and `Succeeded` clears it. IPv6 clients count by
  their /64. `AuthHardeningTests.ABurstOfParallelWrongPasswordsGetsOneCheckAndTheRestWait` (8 parallel wrong
  sign-ins: 1 × 401, 7 × 429; 6 pairing codes: 1 × 401, 5 × 429).
- **Proxy trust (two medium, one low):** `DOWNLOADMANAGER_TRUST_PROXY=1` believed `X-Forwarded-For` from anyone, so
  a client reaching the port directly got a fresh address per try and could send `127.0.0.1` to skip the setup
  code. It now takes the proxy's address or network (`172.30.0.10`, `172.30.0.0/24`); `1`, `/0` and nonsense stop
  the start (exit 2). The setup code goes by the real peer, noted before the forwarded-headers middleware, and is
  always asked through a listed proxy. A same-host proxy that adds no header can't be detected; docs/docker.md says
  to list it (`127.0.0.1`) or use `DOWNLOADMANAGER_PASSWORD_FILE`. Compose example: Caddy with a fixed address.
- **Bodies:** setup, sign-in and pairing JSON is read up to 16 KB, chunked or not (was Content-Length only, 8 MB).
- **auth.json:** a password, token add, remove or remove-all that can't be saved is undone in memory; the endpoint
  answers 500 and nothing changes.
- **Password change** unpairs every extension unless `keepPairedBrowsers` (web UI checkbox "Keep the paired
  browsers"); answers `{ok, unpaired}`.
- **Host check without Origin:** a rebinding page's same-origin GET carried no Origin and was let through with
  sign-in off. Every `/api/` request now needs a known host name.
- **Pairing DoS:** 10 wrong codes from anyone ended the owner's code; now 1,000 (under 10^-9 chance of a hit).
- **Upload path:** `/api/upload/torrent` answers `upload:<id>`; `RemoteJobCoordinator.ResolveReference` maps it.
- **Idle wake-ups:** the sign-in and Settings countdowns run only while counting (`webui/src/lib/ticker.ts`).
- **Extension:** `chrome.storage.local.setAccessLevel({accessLevel: "TRUSTED_CONTEXTS"})` at start, awaited before a
  token is written (content scripts use no storage); the pairing test asserts it, and its title says what it checks.

Evidence: Server.Tests 71/71 on Windows and 71/71 on Linux (the owner's VM, SDK container, `Platform!=Windows`);
new `AuthHardeningTests` (11). Integration remote and dispatcher tests 279/279 (Windows). webui vitest 37/37 (new
`ticker.test.ts` 3), `svelte-check` 0 errors, `npm run build`; extension npm 118/118, `tsc --noEmit` clean.
Image `inbound-test:dev` rebuilt on the VM; in it: `TRUST_PROXY=1` exit 2 with the message; a proxy listed
elsewhere: `X-Forwarded-For: 127.0.0.1` still needs the setup code, four tries with new forwarded addresses
`403 429 429 429`; the bridge gateway listed: forwarded addresses believed (`403 429 403 429`, two addresses in
the log), setup code still asked; a chunked 64 KB sign-in body 400. Containers removed; disk unchanged (8.8 GB free).

Not done: the Playwright screens walk was not rerun (the upload and password screens changed only in what they
send; the server tests cover the calls). Not delegated to a cheaper reviewer (no agent tool in this session).

Questions for the owner (safer choice taken): (1) `DOWNLOADMANAGER_TRUST_PROXY=1` is refused instead of meaning
"trust everyone"; the proxy's address is needed. (2) A password change unpairs the extensions unless kept. (3) No
count across all addresses: it would let anyone keep the owner waiting; devices sharing one IPv6 /64 share a wait.
(4) 1,000 wrong pairing codes end a code. (5) A same-host proxy without forwarding headers must be listed; the
server can't tell.

### EXT.R1 server target: the extension's transport, pairing and This PC / Server choice, on `webui` (2026-11-08)

- **Built (extensions/chrome):** `src/transport.ts` with `NativeTransport` (the native-messaging code moved out of
  `background.ts`, same behaviour) and `ServerTransport` (`POST <address>/api/ipc`, `Authorization: Bearer`,
  `credentials: "omit"`, `redirect: "error"`, time limit; 401 means "pair again"); address and code normalising,
  `pairWithServer` (`/api/pair`, then one ping before anything is stored), the target rules. Settings: an
  "Inbound server" card (address, code, Pair, state, Check connection, Open the web UI, Unpair; a note for plain
  http beyond loopback) and "Automatic downloads go to: This PC / The server" (`settings.defaultTarget`, This PC by
  default). Pair asks the browser for that host only (`permissions.request`). The folder window and Download All
  show "Save to" only when both targets can work (the server alone when the desktop app is not installed here),
  with each target's default folder and favorites; Choose… is the app's dialog for This PC and a folder list
  (`folders.browse`, new `src/destination-target.ts`) for the server. The popup lists the newest 20 of each place,
  labelled, with a server card, and sends actions to the right place. Any failure resumes the download in the
  browser; nothing is ever sent to the other place instead. Firefox build too (`dist-firefox`). Test helpers moved
  to `tests/harness.cjs`; the "no network requests" test now allows one `fetch`, the transport's.
- **Evidence:** npm 118/118 (92 before + `transport.test.cjs` 13 + `server-target.test.cjs` 13; a mutation that
  sent automatic catches to this PC failed 4 of them); `tsc --noEmit` clean; both builds. Server.Tests 60/60 (new
  `ExtensionTransportTests` 3). `scripts/server-smoke.mjs` against a test server (`DownloadManager.Server` on
  127.0.0.1:18480, data under `artifacts\ext-smoke`): 12/12 steps (sign in, code, pair with the code typed with a
  dash in lower case, default folder, roots, handoff, duplicate, list without settings, delete, `job.add` refused,
  token removed in the web UI, then 401 "pair again"). Chrome for Testing (fresh profile, `dist` loaded, downloads
  folder in `artifacts`): paired from the real Settings page ("Paired with the server at 127.0.0.1:18480 as "Chrome
  on Windows". It is answering."); automatic sending to the server: `browser-to-server.bin` (3 MiB) completed on
  the server, SHA-256 05445bb3…0704e5 equal, the browser's entry removed, notification "Sent to the Inbound server";
  repeated with the same address: a second job "browser-to-server (1).bin", completed; "ask each time": the window
  showed no choice and the server's default folder (CfT has no native host registered, so only the server was
  offered, as designed), Choose… listed the server's root with free space, Download sent it, `asked.bin` (1 MiB)
  completed with SHA-256 c5c2a841…5d70 equal; the popup showed the server card and hid the desktop app's. CfT
  reported "Specified native messaging host not found", so it never reached the installed app. Screenshots and
  logs: `artifacts\ext-smoke\out` (not committed).
- **Not explained:** one automatic attempt (the second CfT run, the same address as the first run, right after
  the session was restored) left no trace: no request reached the file server, no browser entry, no note in the
  popup. The first run had used CDP `Browser.setDownloadBehavior`, which takes downloads away from the extension,
  and its result is discarded. Two later attempts, one with the same address, worked. Worth one more look in a
  real Chrome.
- **Not done:** no real Firefox or Edge run; no browser against the Docker server over the LAN (only loopback);
  the two-target window was checked by tests, not on screen (CfT had no native host). Not delegated to a cheaper
  reviewer (no agent tool in this session).
- **Questions for the owner (safer choice taken):** (1) no `optional_host_permissions` entry: the required
  `http://*/*`/`https://*/*` already cover any server, so Chrome would only warn about a duplicate; pairing still
  asks for the one host (matters in Firefox). (2) No fallback to the other place when the chosen one fails; the
  download stays in the browser. (3) The token is kept in `chrome.storage.local` as is (not synced, not encrypted
  by the extension). (4) Unpair can't revoke the token on the server (a token can't revoke itself); Settings says
  to remove it in the web UI. (5) The window's This PC / Server pick is for that download only and does not change
  the default. (6) The popup asks the server once a second while open, like the desktop app. (7) The extension
  version stays 0.4.0; the store listing's cookie and host-permission lines were updated, but the store's data
  disclosure should be reviewed before a store upload, since a user's server is a new recipient.

### D3 Docker image, compose and GHCR workflow, on `webui` (2026-11-08)

- **Built:** `Dockerfile` (Node 24 web UI stage from the lockfile; official 7-Zip 26.04 `7zz` for x64/arm64 with a
  checked SHA-256 from 7-zip.org, equal to GitHub's release digests; .NET 10 SDK cross-publish for `TARGETARCH` with
  `-p:BundleWindows7Zip=false`, a new switch in the Archives csproj, so no `7z.exe`/`7z.dll` in the image; runtime
  `aspnet:10.0` on Ubuntu 24.04; base images pinned by digest), `docker/entrypoint.sh` (`PUID`/`PGID`, chown of
  `/data` only when needed, `setpriv` with no new privileges), `docker/healthcheck.sh` (no curl), allowlist
  `.dockerignore`, `.gitattributes` (LF for the files read in Linux), `docker/compose.example.yml`,
  `.github/workflows/docker.yml` (on `v*` tags only; never run), `docs/docker.md`, a README section.
- **Evidence (owner's VM, `inbound-test:dev`, port 18080):** build passed, context 2.09 MB, decoy secrets and local
  folders kept out of the context, nothing of ours named like a key or credential in the image or its history;
  healthy, runs as the VM user; `/health`, setup with the log's code, sign-in, sign-out, wrong password, origin
  refusal, roots limit, WebSocket 101 and a real 1.5 MB download (right SHA-256 and owner) checked with curl; the test
  password in no log; restart, `docker stop` (exit 0), a `PUID` change, `PUID=0` (exit 2) and `--user` checked. Idle
  memory over about 6 minutes: 34.4 MiB fresh, 53.9 MiB after use; workstation GC or conserve-memory save about
  4 MiB, so none is set. Image 350 MB (base 342). FFmpeg would add 425 MB: left out, documented.
- **Not done:** no arm64 image (no QEMU on the VM; not installed). The arm64 7-Zip check and `linux-arm64` publish
  ran as build stages only. The workflow is not linted (no actionlint offline); its inputs were checked against each
  action's `action.yml` at the pinned commits. Not delegated to a cheaper reviewer (no agent tool in this session).
- **On the VM:** kept the `inbound-test:dev` image and the files in `~/inbound-d3` for D4, removed every container made; the
  `node:24-alpine` base image pulled for the build is left there (not labelled, so not removed); build cache left.
- **Questions for the owner (safer choice taken):** no `org.opencontainers.image.licenses` label, since the repository
  names no license; base images pinned by digest (reproducible, but security fixes need a bump: add Dependabot or
  Renovate?); default `PUID`/`PGID` 1654 (the image's `app` user; the compose example uses 1000); a new data folder is
  made 700; `PUID=0` refused; FFmpeg not in the image; `latest` not moved by a pre-release tag; provenance `mode=max`
  and an SBOM in the published image.

### D2 web UI with sign-in and pairing, on `webui` (2026-11-08)

- **Server sign-in** (`src/DownloadManager.Server/Auth`), on by default: one password (setup page, or
  `DOWNLOADMANAGER_PASSWORD` / `_FILE`), PBKDF2-SHA256 600k in `<data>/auth.json`; a setup code from the server log
  for a browser that isn't on the server's machine; `inbound_session` cookie (`HttpOnly`, `SameSite=Strict`,
  `Secure` on HTTPS, `X-Forwarded-Proto` only with `DOWNLOADMANAGER_TRUST_PROXY=1`), 24 h idle and 7 days at most,
  sign-out and password change close the session's WebSockets; growing waits per address; only `/health`, the
  sign-in page, `/favicon.svg` and `/theme.js` public. `DOWNLOADMANAGER_AUTH=off` brings back the D1b loopback rule.
- **Pairing** for the extension: an 8-character code for 10 minutes, `POST /api/pair` gives a token (hash stored),
  listed and revocable, allowed only the extension's commands plus `folders.browse` on `POST /api/ipc`; CORS for our
  extension ids. The extension's own server transport is not built (Stream C).
- **Web UI** in `webui/` (Svelte 5.57.3, Vite 8.3.6, Tailwind 4.3.5, exact versions, `npm ci` works offline from the
  lockfile), built into the server's `wwwroot` by its csproj; `scripts\build.ps1` builds it first. Every planned
  screen: list with live progress, packages, views and bulk actions; add links incl. magnets, provider links and
  torrent upload; extraction and its password; queues and scheduler; settings, favorites, site logins, proxy,
  extraction, read-only program settings; sign-in, password change, pairing; My TorBox and hoster status; the
  server folder browser wherever a folder is chosen. Both themes from the `App.xaml` tokens; phone layout at 390 px
  (tap opens a download there).
- **Evidence:** Server.Tests 57/57 (`AuthTests` 11, `PairingTests` 5). webui: vitest 34/34, `npm run check` 0
  errors and 0 warnings. `npm run screens` (Playwright Chromium 158) against the server with a temp data folder and
  roots under `artifacts\webui-run\`, the TestServer with its mock TorBox, and a local file server with a test 7z:
  26 screens at 1360 and 390 px in dark and light (`artifacts\webui\`, looked at), no console error, page error or
  sideways scroll, and end to end: the archive password extracted the 7z, pause and resume, delete without the
  file left it on disk, a `.torrent` upload reached the mock provider, site login and proxy passwords saved
  write-only, a pairing code from the page gave a token that was refused `settings.get` and answered 401 once
  removed, sign-out. Nothing ran against the real TorBox, and nothing in a container yet.
- **Found and fixed by the walk:** the sign-in page had no styles (Tailwind didn't scan `src/` from the sign-in
  build's root); long paths widened the folder browser and settings on a phone; selecting a package in Finished also
  selected its unfinished files, so Delete would have removed them (now a package row stands for its files in that
  view, vitest); phone toolbar buttons had no accessible name; a phone had no way to open a download (no
  double-click); a past provider wait was shown as "Waiting until".
- **Not delegated:** the review delegation to a cheaper model was not done; this session has no agent tool. The work
  is unreviewed.
- **Questions for the owner (safer choice taken):** the setup code from the log for a browser elsewhere; session
  lengths (24 h idle, 7 days); sign-in off only by `DOWNLOADMANAGER_AUTH=off`, not from the web UI; password at least
  8 characters; at most 50 paired browsers (the oldest goes); `moz-extension://` origins let in on `/api/pair` and on
  `/api/ipc` with a token, since Firefox ids are random; the server version hidden from browsers that aren't signed
  in.
- **Next:** D3 (Dockerfile and compose, the web UI built in a Node stage), the extension's server transport, D4 on the
  test VM.

### D1b merged into `dev` (2026-11-08)

- `stream-b` (the headless server and its Linux replacements, with review fixes) merged. Conflicts: the spanned-zip grouping now uses the OS-aware name comparer from D1b, the service keeps stream-c's `NativeHostState` next to D1b's `Roots`, and the server no longer sets the old `NativeHostRegistered` property (unset reads as not registered).
- Full Windows run after the merge, all green: Core 869, Engine 155, Integration 1,215, TorBox 323, Archives 336, Containers 187, Server 41. Logs: `artifacts\campaign\20261108-merged-d1b\`.
- Next: branch `webui` (D2 web UI with sign-in and pairing, D3 Docker, the extension's server transport, D4 on the test VM).

### Streams A1, A2 and C merged into `dev` (2026-11-08)

- Merged after each stream's review fixes: `stream-a` (A1), `stream-a2` (A2), `stream-c` (extension). Only PROGRESS and ROADMAP conflicted, plus one stream-c command (`settings.downloadFolders`) that was added to the old pipe server; it now lives in `HostCommandDispatcher` with the other commands.
- Full run on the merged branch, all green with no reruns: Core 863, Engine 154, Integration 1,215, TorBox 323, Archives 329, Containers 183, extension 92 (tsc clean). Logs: `artifacts\campaign\20261108-merged\` (trx per suite).
- Running next: the UI walkthrough in dark and light, the independent recovery check, the extension end to end in Chrome for Testing, PERF.5/PERF.2/PERF.3 and the UP.B2 fix; D1b on `stream-b`.

### Independent recovery check (P2.7, P5.6, X.5; branch `campaign-recovery`, 2026-11-08)

- Setup: own worktree and test instances (`DOWNLOADMANAGER_INSTANCE` + `DOWNLOADMANAGER_DATA` under the worktree's `artifacts`), Release builds of this branch, `tools\TestServer` only. Evidence: `artifacts\campaign\20261108-recovery\` (index in `README.txt`). My numbers were taken before reading E94; the comparison is the last bullet. Other tracks were loading the CPU throughout.
- **Kill/restart matrix** (`scripts\kill-restart-matrix.ps1 -Instance ...`, 24 MiB, kills at 20/50/80%), three runs, all passed:

  | run | conn | bytes kept at the 20 / 50 / 80% kills | final SHA-256 |
  | --- | --- | --- | --- |
  | 1 (256 KiB/s per conn) | 1 | 4,128,768 / 12,320,768 / 19,202,048 | `6d905264…476423` match |
  | 1 | 8 | 492,810 / 11,633,927 / 12,034,711 | `a240b74f…6410e6` match |
  | 2 (128 KiB/s per conn) | 1 | 4,915,200 / 11,927,552 / 19,726,336 | match |
  | 2 | 8 | 5,516,375 / 11,148,690 / 16,588,177 | match |
  | 3 (256 KiB/s, checkpoint column) | 1 | checkpoint 4,128,768 / 12,320,768 / 19,202,048 | match |
  | 3 | 8 | checkpoint 200,399 / 11,144,911 / 11,352,301 | match |

  Every final file equals the independent unthrottled copy; no `.part` file and no checkpoint left (checked by hand for runs 1 and 2, by the script for run 3). Runs 1 and 2 report "kept after restart", read once the job is moving again, so it can be above the bytes at the kill (run 2, 8 conn, 20%); the script now also reads the checkpoint before the restart (`checkpointBytes`) and fails if a finished job's checkpoint is left. At 8 connections and 2 MiB/s a kill point is crossed less than 5 s after the restart, so it keeps the previous checkpoint (run 3: 80% kill kept 11.35 MB of 20.5 MB): that is the 5 s `CheckpointInterval`, as designed; at half speed (run 2) it kept 16.6 MB.
- **A1 yield and resplit under a kill:** two 24 MiB 8-connection jobs on one host (per-host cap 8) ran 4 + 4 connections; Host killed mid-transfer once (`fair-kill`) and twice (`fair-kill2`); both files match their references, no checkpoint left.
- **Provider cells:** `HostKillMatrixTests` 6/6 (TEST.1's cell passed), plus `HostPipeRemoteTests.ScenarioJ…` and `RuntimeArchiveHostTests.KillingTheHostMidExtraction…` 2/2. By hand against `TestServer --torbox --cdn-bps 32768`: the seeded 3-file package added by magnet, Host killed with the 8 MiB file at 3.4 MB (`torbox-kill4`): after a restart it resumed by itself, all three files match the mock's bytes, one remote job, one item on the mock account, no part or checkpoint left.
- **Browser handoff, Chrome for Testing 156** (headless, fresh profiles, unpacked `dist` built here, loaded as `pknddmneejadnnngffojnoecojhdfalc`, driven over the DevTools protocol; automatic sending ticked and saved in the options page):
  - Normal: a TestServer file landed in the test instance, SHA-256 match, the browser's entry removed, nothing in the browser's folder.
  - Host frozen, request forwarded to its pipe, Host killed 1.5 s later, Host startable: the native host exited, the extension's retry started a new native host, which started a new Host and was accepted; one job, SHA-256 match, browser entry removed.
  - Same, but no Host can be started (isolated native-host copy, `DOWNLOADMANAGER_HOST` pointing nowhere): Chrome resumed its own download 0.4 s after the retry failed and finished it, SHA-256 match, no job in the app.
  - Registration: the installed app's Chrome key was never written. The test build was registered under `HKCU\Software\Chromium\NativeMessagingHosts` and `HKCU\Software\Google\Chrome for Testing\NativeMessagingHosts` (neither had our name) and both were removed afterwards; before and after are in `browser\registry-*.txt`. At the time the Chrome key held another track's test value, not the installed app's; it was left as found.
- Findings: **TEST.3** (CfT reads the Chromium key first and falls back to the Chrome key; the keys are shared by every track: another track's CfT used this track's native host for about 3 minutes while the Chromium key was set, it connects to whichever Host that browser's environment names, as that track's own native host would, but it was this branch's build). **UI.B11** (an exit pauses running downloads, so the auto-resume option only covers crashes; owner decision). **UI.B12** (a Host started while a killed one is still exiting quits silently; plausible mutex cause). Script fix committed: `kill-restart-matrix.ps1` checkpoint column and leftover-checkpoint check.
- **Comparison with E94** (read after the runs): same final hashes (`6d905264…`, `a240b74f…`) and the same pass criteria; single-stream retention is close (E94 4.98/12.39/20.05 MB, here 4.13–4.92 / 11.93–12.32 / 19.20–19.73 MB). The 8-connection 20% and 80% kills kept less here in runs 1 and 3 (0.2–0.5 MB and 11.4–12.0 MB, against E94's 4.48 and 20.66 MB); run 2 at half speed came close (5.5 / 11.1 / 16.6 MB). The difference is when a kill falls relative to the 5 s checkpoint, not a loss of written data: every kill kept progress and every file was exact. P2.7, P5.6 and X.5 are marked verified.
- Not done: Edge and Firefox handoff; a Windows sign-out during a download (UI.B11); a real-process test for UI.B12.

### Campaign item 5: the extension end to end in Chrome for Testing (2026-11-08, branch `campaign-ext`)

- Setup: Chrome for Testing 156.0.8078.80 with `--load-extension` (`dist`, ID `pkndd…`) and a fresh profile whose download folder is `test-downloads\cft-browser`; a test Host (`DOWNLOADMANAGER_INSTANCE=campaign-ext`, data under the worktree's `artifacts`) with its default folder in `test-downloads\ext`; TestServer on 127.0.0.1. Driven through the DevTools protocol (real mouse events for clicks, so each download has a user gesture), the browser's own right-click menu through UI Automation, and the app's folder dialog through window messages. Evidence, scripts and screenshots: `artifacts\campaign\20261108-ext\`.
- Native-host registration without touching the owner's key: in the agent's shells, HKCU writes go to a private layer that the real hive does not see (a probe key written there was absent when read through WMI `StdRegProv`), and Chrome started from these shells reads that layer. The layer already held a stale value (`%LOCALAPPDATA%\DownloadManager\native-host\…`, a file that no longer exists), which is why agent-launched Chrome reported "native messaging host not found" before. It was saved, pointed at this worktree's manifest for the run and written back exactly. The real key, read through WMI before, during and after, stayed on the installed app's manifest the whole time.
- Results (all passed):
  - Fresh install: automatic sending off; Settings banner, popup "Automatic sending: off" and the OFF badge; "Check connection" reached the test Host. A direct `.mp4` stayed in the browser with its reason.
  - On: `.flac`, `.mp4` and `.pdf` with `Content-Disposition: attachment` were caught, removed from the browser's list and completed (SHA-256 equal to the server's; the three test bodies are identical, so this shows integrity, not which file is which). `.png` (excluded), `blob:` and `data:` stayed in the browser; only the `.png` got a note, as designed.
  - Right-click "Download with Inbound Download Manager" (the browser's own menu): the folder window opened at the default folder; Choose… opened the app's dialog; the chosen folder was used; hash matches.
  - "Ask each time": the caught download was held paused, Download sent it to the default folder, Keep in browser let it finish in the browser.
  - Download all links on `/download-all-page`: 4 unique links (duplicate, `mailto:`, `javascript:` and `#` left out), all 4 completed, the HLS one as `master.ts`.
  - Popup: all downloads of the app (11, including a CLI job and a failed one); Pause, Resume, Retry and Delete with and without the file worked against the Host; Pause catching showed OFF and a note.
  - Firefox: `npm run build:firefox` built. `web-ext lint` not run (no cached copy offline). Edge not run (no test copy; the owner's Edge is off limits).
  - Extension: typecheck clean; `npm test` 94 of 94 after the fix (92 before).
- Found and fixed: EXT.B2, Send could be pressed while the app's folder dialog was open (the download went to the folder shown and the choice was lost). Checked on screen after the first fix. Review fix: that version could leave Send disabled for good after a send that failed while the dialog was open, and let Choose… be pressed during a send; both windows now use one state model (`FormGate`), covered by npm tests that run the page scripts. The review fix is not yet checked on screen.
- Test setup notes, not product bugs: a Host built without the WinUI app shows a "frontend was not found" message box for each completed download while the completion dialog is on (turned off in the test instance's settings); `chrome.runtime.reload()` on an extension loaded with `--load-extension` unloaded it, so the browser was restarted instead; `chrome.action.openPopup()` needs a focused window, so the popup was opened as a tab.
- Not done: Edge; a real Firefox run and AMO signing; the owner's TorBox Direct Download check (EXT.B1). The Haiku diff review named in CLAUDE.md was not available to this subagent (no agent tool).

### UI walkthrough of every window, dark and light (campaign item 2, branch `campaign-ui`, 2026-11-08)

- Setup: Release build published to the worktree's `artifacts\app`, a test instance (`DOWNLOADMANAGER_INSTANCE=campaign-ui`, data in `artifacts\campaign-ui`), TestServer on 127.0.0.1 only, update feed pointed at TestServer and automatic updates off. Seeded with finished, failed, queued, paused and running downloads, a three-file package and a password-protected zip (made with the bundled 7-Zip) so the extraction password prompt appears. The theme comes from `ui-state.json` `darkMode` in the instance's data folder (View > Dark Mode writes the same value). The Host's `Theme` setting ("System") is copied around but nothing reads it (UI.R23).
- Covered, both themes: main window and every sidebar view, row and package context menus, every menu including the three limit submenus, Add URL (both pages), Add Links and its review, every Options page (including the unsigned TorBox account page), Scheduler (Schedule, Files, Limits), progress window (tabs and segment details), package window, Statistics, the update progress window (started with `update-progress`; a portable build's Check for Updates only shows a message), About, Delete and Shift+Delete, the extraction banner and password prompt, Properties, Move/Rename, Change Category, Queues. Not covered: My TorBox itself (only offered with a usable account) and native file pickers. There is no column-header menu.
- Screenshots: `artifacts\campaign\20261108-ui\dark\` and `light\`, made with the new `scripts\ui-capture.ps1` (PrintWindow, starts nothing) and driven with the new `scripts\ui-drive.ps1` (UI Automation steps; refuses the installed app and never presses a title-bar Close). Each was looked at.
- Light mode: no unreadable text, wrong-theme colours or clipped controls found. Contrast: `ui-accessibility-check.ps1` now reads every colour token from `App.xaml` for both themes (62 pairs: text tokens 4.5:1 on surface and chrome, glyph and progress tokens 3:1, primary text and accent-button text); all pass. It used to check a stale dark-only list.
- Fixed: **UI.B8** (the window ended when a second dialog was requested over an open delete dialog; delete path guarded through a shared `ShowDialogAsync` that refuses a second dialog, which messages, `AskAsync` and the address dialog use too; rest recorded), Scheduler Files tab state text indented for downloads without a size, Scheduler Limits tab centred instead of left-aligned, Statistics note cut off at the default size (650 to 720 px tall).
- Scripts: `ui-accessibility-check.ps1` and `ui-inventory.ps1` attached to the first `DownloadManager.Desktop.WinUI` process of any kind, which on this PC can be the installed app or another track's test UI (one was running); both now take `-PathPart`, refuse the installed app and find the main window by its title. `ui-keyboard-check.ps1` picked the package row (its dialog, "Delete 3 downloads?", was not recognised, so it went on pressing keys and the toolbar Delete behind it: that is how UI.B8 showed), used a stale Add dialog title, and could attach to a progress-window process; fixed. `ui-counter-alignment-check.ps1` could attach to a progress-window process; fixed.
- Check results (test instance, dark and light; outputs in the evidence folder): accessibility U7 CHECKS PASSED in both; keyboard: Delete opens with Enter cancelling, Shift+Delete On, Delete key Off, toolbar Off, Add dialog modal, in both; counter alignment: Size, Time left and Transfer rate cells end on their header's right edge in both; inventory lists all menus, categories and the eight columns in both.
- Recorded: UI.B9 (black client area until resized, seen only in this harness so far), UI.R22 (selected rows nearly invisible in both themes; owner decision), UI.R23 (wording and small layout points).
- Harness notes: screenshots of a test build stay black until the window is resized by a pixel (`ui-drive.ps1 nudge`); the Alt tap used to bring a window forward can leave the menu bar's access-key tips showing.
- UI.B8 re-check after review (fresh publish): Shift+Delete on the package row, then the toolbar Delete invoked through UI Automation while the dialog was open, also with the unconverted archive password prompt open: the window process stayed up and nothing was deleted (12 downloads, package files unchanged). A temporary trace build (not committed) showed `Popup.Child` is the `ContentDialog`, so the earlier popup check did match. Evidence: `artifacts\campaign\20261108-ui\b8\` (`notes.txt`).
- Review fixes to `ui-drive.ps1`: `~` prefix matches could press the title-bar Minimize/Maximize/Close; `click:`/`right:` sent real mouse input without checking the test UI was in front and could click an offscreen element; `closewin:` with no title closed every secondary window. All fixed; the offscreen refusal was not exercised (no offscreen match came up).
- Not done: high-DPI runs (only simulated by the accessibility script), the owner's look at the screenshots, the other dialogs' UI.B8 guard (about 40 `ShowAsync` calls; owner question whether to convert all).

### Performance campaign: soak, GC, closing the window, UP.B2 (branch `perf`, 2026-11-08)

Evidence: `artifacts\campaign\20261108-perf\` and `artifacts\soak\` in the `perf` worktree (not tracked). Other
work ran on this PC at the same time and loaded the CPU, so timings are from a busy machine; each timing below
was repeated (three runs, or two GC runs) and the repeats agreed.

- **UP.B2 (implemented, automated tests):** the update helper writes the new version into Setup's uninstall
  entry (`DisplayVersion`, and the major/minor DWORDs when present) under
  `HKCU\...\Uninstall\{6F4B7C2E-3D91-4E8A-9B57-2C1D0A8E5F13}_is1`, only when the key exists and its
  `InstallLocation` is the folder being updated; it never creates the key, and a failed update restores the old
  values. `UpdateUninstallEntryTests` (7) use a fake key through the test-only `-UninstallKey` parameter; 4 fail
  on the old script, and the rollback test fails without the script's restore call. Not yet seen on a real in-app update.
- **PERF.2 soak (partial, running):** `scripts/soak.sh` (Git Bash) runs a test instance with 5,040 jobs and a
  new TestServer download every 20 s in six kinds (segmented, 503 then served, cut short, slow, no ranges, 500
  forever), and writes `samples.csv` (Host, window, TestServer: private bytes, working set, handles, threads,
  CPU, every minute) and `gc.csv` (the Host's GC every minute, `DOWNLOADMANAGER_GC_LOG`).
  - The 8-hour run is `artifacts\soak\soak-8h\` in the worktree. It started 02:17 UTC and stops by itself at
    about 10:19 UTC. To stop it earlier, run `bash scripts/soak.sh --stop soak-8h` from the worktree. That
    creates the `stop` file, and the driver then shuts down its own Host, window and TestServer.
  - It runs the build of `eb4404a`, from before the fixes below, so it measures the old Host.
  - **Full 8 hours (476 one-minute samples, `soak-8h-summary-final.txt`):** Host private MB 178.4 first, 382.0 last, mean 326.8, max 642.8, trend +22.4 MB/h; handles 490 to 498, threads 17 to 18; CPU 0.45 s a minute. Window private MB 305.3 to 317.1 (trend +0.8 MB/h), handles 1,307 to 1,331. Host GC: 410.6 MB allocated a minute, heap mean 173.6 MB, max 573.9, trend +13.2 MB/h, pause 0.03%. Both processes ran to the end. The Host trend is not yet told apart from the list growing to 5,000 jobs; a rerun on `dev` is still to do. Evidence copied to `artifacts\evidence\perf\` in the main checkout.
  - First 142 minutes (`soak-8h-summary-at-*.txt` in the evidence folder):
    - Host: private mean 256 MB, max 552 MB, trend -10 MB/h; handles 490 to 495; threads 17 to 18; 0.43 s CPU
      a minute.
    - Window, hidden after 02:59: 305 to 315 MB (+3 MB/h); 0.53 s CPU a minute with 5,000 jobs while downloads
      stream.
    - No CLI errors.
    - The Host's GC heap swings between about 100 and 450 MB and comes back down. This is snapshot garbage
      (PERF.7), not a leak.
    - The soak's TestServer was replaced twice while running (driver.log). Its content cache was the problem,
      now fixed.
- **Fixed on the way:**
  - The tray's once-a-second count and the settings reads built a full snapshot. In 16-minute runs with 5,000
    jobs this changed the Host from 394 to 199 MB allocated a minute, private mean from 182 to 165 MB, and max
    from 308 to 214 MB.
  - UI.B13: the pipe took only 8 clients; now 64.
  - UI.B14: CLI `add-batch`/`import` opened windows. They now send sources of their own (`cliBatch`,
    `cliImport`): no progress or completion window per job, the same list labels as before.
  - UI.B15: segment retries now start over after progress, and the TestServer resets only responses it cuts.
  - The TestServer's content cache: from 3.2 GB to a 256 MB cap.
- **PERF.3 (implemented):** the Host now runs with `GCConserveMemory=5`. Default GC, two runs:
  - private mean 165 and 167 MB, max 214 and 224 MB, committed 140 and 134 MB;
  - CPU 0.38 to 0.39 s a minute.

  With `GCConserveMemory=5`, two runs:
  - private mean 104 and 98 MB, max 132 and 130 MB, committed 86 and 85 MB;
  - CPU 0.37 to 0.40 s a minute;
  - gen2 collections 2.4 times as often, GC pause 0.03%.

  The built Host with the setting in its runtimeconfig had a private mean of 118 MB, max 148 MB and
  0.37 s a minute. Measured and not kept: `gcConcurrent=0` (150 MB), and server GC (129 MB, 50 threads).
  Test: `HostGcSettingsTests`.
- **PERF.5, "Closing the window ends it":** the option stays off by default for now. The numbers below come from
  `scripts/perf-window.sh`, and ROADMAP PERF.5 has the details.
  - Open time, measured to the first frame that shows the list. Each figure is the range of three runs.

    | Jobs | Option on (a cold start) | Option off (showing the hidden window) |
    | --- | --- | --- |
    | 0 | 656–667 ms | 289–312 ms |
    | 500 | 644–688 ms | 311–329 ms |
    | 5,000 | 1,065–1,129 ms | 390–465 ms |

  - 30 minutes idle with 500 jobs and the window closed:
    - Option on: no window process at all. The Host used 0.01 s CPU, 0.3 context switches a second and 52 MB.
    - Option off: the hidden window used 3.0 s CPU (6.1 s an hour), averaged 120 context switches a second
      (max 205) and held 201 MB. The Host was the same as with the option on.
  - What is lost with the option on:
    - the speed graph's history;
    - completion windows for jobs added from the CLI;
    - selection, scroll position and open secondary windows.

    Everything else lives in the Host: downloads, the tray, sounds, the browser handoff, the clipboard monitor,
    and TorBox and remote state.
  - Recommendation: on by default. It saves about 120 wake-ups a second, 6 s of CPU an hour and 200 MB, at a
    cost of about 0.35 to 0.65 s per open. The default is left for the owner to change.
- **Recorded:**
  - PERF.7: every pipe connection gets the full snapshot, about 8.5 MB with 5,040 jobs.
  - UI.R24: an idea, keeping the speed history in the Host.
  - TEST.4: UI Automation can't close the window, and the first posted WM_CLOSE is ignored.
  - While idle, the Host's handle count rises about 12 a minute and then falls back at once (749 to 443 in the
    first idle run; 782 to 446 in the second). The handles are released, not leaked. Their source is not found.
- **Tests:** on the final code, with no failures and no reruns: Core 863, Engine 157, Integration 1,224,
  Archives 329, Containers 183, TorBox 323 (`tests-*run2*.txt` in the evidence folder). After the review fixes
  (UI.B14 sources, UP.B2 rollback test, UI.B15 reset order, `perf-window.sh` label), 2026-11-08: Core 863,
  Engine 158, Integration 1,231, no failures; Archives, Containers and TorBox not rerun. The window was not
  started to check the labels or completion windows by eye.
- **Not done:**
  - The soak's full 8 hours.
  - A soak on the fixed build.
  - Checking UP.B2 on a real in-app update.
  - The TEST.4 root cause.
  - The review delegation in CLAUDE.md: this workflow step had no way to start a reviewer agent.

### Clean install, upgrade and uninstall in a Windows VM (TEST.2, 2026-11-08)

- VM: VirtualBox on E:, Windows 11 Enterprise LTSC evaluation from Microsoft, a new standard user, snapshot `clean-install`. Commands go in through the desktop session's Run dialog (VirtualBox keyboard input) because starting processes through the guest additions fails in this guest; files go both ways through the guest additions.
- Result: all phases pass. See ROADMAP TEST.2 for each check and `artifacts\campaign\20261108-vm-install\` for the reports.
- Found: UP.B2 (installed-apps list keeps the old version after an in-app update).
- WinZip: removed at the owner's request (2026-11-08, "purge winzip after you're done with it"). Spanned-zip test sets are built in code by the Archives tests instead. A 72-byte ID file in `C:\ProgramData\WinZip` needs admin to delete.

### D1b: the server for Linux and Docker (branch `stream-b`, 2026-11-08)

- What it is: a new program, `src\DownloadManager.Server`, that runs the same download engine, queues, archives, containers and TorBox provider as the Windows Host, without any Windows part. Clients reach it over HTTP instead of the named pipe: a WebSocket at `/api/ws` that speaks exactly the pipe's messages (hello, snapshot, then events), `POST /api/ipc` for one request and one answer, and `/health`. The page at `/` is a labelled placeholder until the web UI (D2).
- Folder browsing for the web UI: two new messages, `folders.browse` and `folders.create`, list and make folders only under `DOWNLOADMANAGER_ROOTS` (default `/downloads`). Every path is resolved through its links before the check, so a link that points out of a root is refused. The desktop Host doesn't offer them (it has a folder dialog).
- Safety until sign-in exists: the server won't listen beyond this machine unless `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1` is set, and then logs a warning. A browser request must come from the server's own page and name it by IP address, `localhost` or a name in `DOWNLOADMANAGER_HOSTS`, so another web page can't drive it, not even one that points its own name at the server. Anyone who can reach the port can still do everything; that is in `docs\docker-webui-plan.md`.
- What replaces the Windows parts on Linux: secrets (site logins, proxy password, TorBox key) are sealed with AES-256-GCM and a key file `secrets.key` in the data folder, made readable by its owner only; a backup of the data folder must keep that file. 7-Zip is the official `7zz`, found on PATH or through `DOWNLOADMANAGER_7ZIP`. The tool and its children are killed on cancel and when the server exits. FFmpeg is found on PATH. Names that differ only in capitals are two files on Linux, so they are compared that way there; Windows is unchanged. Free space is measured on the mount that holds the folder.
- Bugs the Linux run found and fixed: 7-Zip for Linux read a password with a trailing carriage return, so every password was wrong; the extraction check for hard links did nothing on Linux (it now asks the C library); container files and the bridge to another download manager only took drive paths.
- Tests: new `DownloadManager.Server.Tests`, 30 pass on Windows and on Linux (protocol over the WebSocket and POST, folder browsing with links and `..`, the origin and host-name check, the loopback rule, the key file). Tests that need Windows itself (the Windows Host and its pipe, drive letters, NTFS streams, share locks, junctions, Explorer, the updater) are marked `Platform=Windows`; the rest run on both.
- Linux run, on the owner's test VM in the .NET 10 SDK container, `--filter "Platform!=Windows"`: Core 817 of 817, Engine 147 of 147, Archives 252 passed and 52 skipped (those need WinRAR's Rar.exe to build test archives), Containers 183 of 183, TorBox 321 of 321, Server 30 of 30, Integration 1,047 passed and 7 skipped (Rar.exe). No failures.
- Windows run: Core 824, Engine 147, Archives 314, Containers 187, TorBox 321, Server 30, Integration 1,111; all pass (the flaky TEST.1 failed once in an earlier run of this branch and passed in the final one).
- Not done: no web UI, Docker files or sign-in (D2, D3). No manual run of the desktop app (nothing on screen changed). A directory `fsync` after extraction renames is still Windows-only. The review agent named in CLAUDE.md wasn't available to this run.

#### Review fixes (D1b, 2026-11-08)

Eleven findings from the review of `dev...stream-b`; ten were real and are fixed, one (the hidden-folder listing) already behaved as documented and is now explicit and tested.

- The roots now hold everything a client names, not only the folder browser: a download's folder (asked for, or the default, last, category or custom-category folder it falls back on), Move/Rename of a finished download, a changed folder setting (the download folder always, since saving creates it), a container file to preview or send, a `.torrent` file given by its path, and an archive to extract, its password record and the "extract to this folder" setting. Each path is resolved through its links and the resolved one is used. This answers the earlier question about destinations outside the roots (it was "recommended yes"). The desktop Host has no roots and is unchanged.
- `Kestrel__Endpoints__...` variables (a common compose line) could make the server listen on every address while the loopback check saw only `127.0.0.1`. Kestrel's endpoint configuration is now ignored, and the addresses it really listens on are checked again after start. The reviewer's reproduction is a test.
- Two servers on one data folder: the server now holds `server.lock` there with no sharing for its whole life (an exclusive lock on Linux, dropped by the kernel when the process ends), and a second one stops with exit code 2.
- A missing or different `secrets.key` no longer starts quietly: before any store opens, the data folder is searched for sealed secrets, and if they were sealed with another key (or the key file is gone) the server stops with exit code 2, naming the key file, and makes no new key. Before, the secrets read as empty and the next settings save dropped them for good. `DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS=1` is the explicit way to start without them.
- Folder listing: hidden folders are left out explicitly (Hidden or System on Windows, a leading dot on Linux), and a listing cut at 2,000 now holds the first 2,000 by name, as the protocol says, not the first the disk returned.
- Archives on Linux: two files whose names differ only by case are two files there, and the archive code compared some of them ignoring case, so one download could be tied to another set's cleanup. Every file-name comparison in the archive code now uses the machine's rule. Volume grouping still matches base names ignoring case (a set named `Film.PART1.RAR`, `film.part2.rar` stays one set), except in a folder that holds two names equal but for case, where bases are matched exactly. The first Linux run caught that the plain switch split such a set; the second change fixed it.
- The Linux hard-link check after extraction could be skipped silently (no `lstat` on glibc before 2.33, or another architecture). It now also tries `__lxstat`, and when the count still can't be read it refuses the extraction with a message saying why, instead of counting one link. Windows keeps its rule.
- New tests: 11 in Server.Tests (root confinement over the server for adds, settings, moves, containers and archives; the Kestrel variable; the address re-check; the data-folder lock; missing, other and discarded keys; JSON-escaped blobs; hidden folders; truncation), 2 in Archives.Tests (case grouping, link-count layouts and the real count on Linux). One Integration test (`OneSetKeepsOneIdWhateverOrderItsVolumesAreSeenIn`) now expects two sets on Linux for `v.part10.rar` and `V.PART10.RAR`, which are two files there; Windows is unchanged.
- Windows: Server 41 of 41, Archives 316 of 316, Integration 1,111 of 1,111. Linux (owner's VM, .NET 10 SDK container, `--filter "Platform!=Windows"`): Server 41 of 41, Archives 254 passed and 52 skipped (Rar.exe), TorBox 321 of 321, Integration 1,047 passed and 7 skipped (Rar.exe); Core 817, Engine 147 and Containers 183 all passed on the first commit of these fixes. No live TorBox calls; the VM's test containers were removed.
- Not tested: the `.torrent` path check (it needs a configured torrent provider, so it is covered by the code only); a data folder on a network share without working locks.

#### Questions for the owner (D1b)

- A `.torrent` file, container file or archive the server reads must now be inside `DOWNLOADMANAGER_ROOTS`, like downloads. D2's upload of a torrent file would go through the server instead of a path. Fine?
- Losing `secrets.key` now stops the server (exit code 2) until the key is put back or `DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS=1` is set once. Safer than losing the secrets on the next save, but a restore without the key needs that variable. Fine?
- When the hard-link count can't be read on Linux (an architecture other than x64 and arm64, such as 32-bit ARM), extraction is refused rather than done unchecked. A 32-bit Raspberry Pi would then not extract. Acceptable, or should that case only warn?
- A browser may only reach the server by IP address, `localhost` or a name listed in `DOWNLOADMANAGER_HOSTS` (against DNS rebinding). Opening it as `http://nas.local:8080` needs that name listed. Fine, or should D2's sign-in replace this rule?
- The default address is `0.0.0.0:8080` (what Docker needs), and it refuses to start there without `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1` until sign-in exists. Keep that, or default to loopback?
- On Linux, extraction refuses a file name with a backslash (it would become a folder on Windows) and allows names that differ only in capitals. Keep?
- The Linux build output still carries the Windows `7z.exe` next to the server. Drop it from the Linux publish in D3?

### D1a: the Host's protocol without a pipe (branch `stream-b`, 2026-11-08)

- What changed: every command, the hello, the snapshot (including its fallback when it is too large for a frame) and every broadcast (download events, update progress, package changes with their 100 ms debounce, provider changes, archive changes, "Host exiting") moved out of the pipe server into `src\DownloadManager.Runtime\Ipc\HostCommandDispatcher.cs` (and `.Remote.cs`). It has no transport, so the Docker server (D1b) can put a WebSocket under the same code.
- New seams in Runtime: `IClientSession` and `QueuedClientSession` (a queue of 512 frames that drops the oldest when full and keeps order; direct sends refuse a frame over 8 MB), `IHostPlatform` with `PlatformCapabilities` (window, folder dialog, tray, exit, updater, folder listing; replaces the Host's `IHostController`), and `IHostLog` (the Host's `FileLogger` implements it). `HostPipeServer` now only accepts, sets the ACL, reads frames and writes the queue; the "wait for the client to go away" lookahead stays there, asking `HostCommandDispatcher.NeedsLifetime`.
- On a machine without a window, dialog, exit or updater, those commands answer that they are not available instead of acting. The desktop Host has all of them, so nothing changes for the WinUI app, the CLI or the browser bridge. The hello's list is the same, in the same order.
- Evidence: a scripted session against the real Host (`HostGoldenFramesTests`: 26 commands, a wrong-version client and the exit, 38 frames) was recorded on the code before the move, twice (identical), and again after it: the files in `artifacts\d1a\` are identical. New `HostCommandDispatcherTests`: 15 pass (hello with and without the provider, snapshot before any event, oversize snapshot, exit order, one package message per burst, unknown type, unavailable commands, update window opened once, wrong version, which commands get the lifetime).
- Tests: Core 818 passed. Integration 1,106 of 1,107; the one failure was the known flaky TEST.1 (`KilledWithAPackageHalfDone`), which passed 3 of 3 alone. The pipe suites together (HostPipeRemote, HostKillMatrix, BrowserBridge, HostPipeContainer, the dispatcher and golden tests) passed 80 of 80.
- Not done: no manual run of the desktop app (the change has no visible effect); the review agent named in CLAUDE.md wasn't available to this run.
- Review fixes (same day):
  - The golden test used to compare only when an environment variable named a baseline, so a normal run checked nothing. It now always compares with a committed file, `tests\DownloadManager.Integration.Tests\GoldenFrames\host-pipe-session.txt`. To rewrite that file on purpose, set `GOLDEN_FRAMES_UPDATE=1`. The script also sends `update.check`, `update.download`, `update.install` and `update.defer` now, so the updater gates and the `update.progress` broadcast are covered on the real Host. That makes 30 commands and 43 frames. The new baseline was recorded on the pre-move commit, in a temporary worktree, three times, and all three runs were byte-identical. The branch's frames match it in 3 of 3 runs. A baseline with one frame changed makes the test fail.
  - A new test loads the built Host and checks that `HostController` claims exactly `PlatformCapabilities.Desktop`. Another checks that, with the desktop capabilities, `host.openUi`, `host.pickFolder` and the update commands reach the machine and the service.
  - The hello's capability list is now documented as a set: clients look a name up, never a position. The desktop list and its order are unchanged. A Host without a tray leaves `tray` out (code comment, `docs\ipc-protocol.md`).
  - New `PlatformCapabilities.SupportsProgramSettings` (on for the desktop). Without it, `settings.update` refuses changes to the post-download command, FFmpeg path, update manifest URL, update public key or the system completion actions switch. `job.update` likewise refuses to set a completion program. Sending the current values back with other changes still works. This closes the gap before D1b puts a network transport under the dispatcher. Two tests cover it, one with the setting off and one with the desktop.
  - Tests: `HostCommandDispatcherTests` 19 pass. Core 818 of 818. Integration 1,111 of 1,111 (TEST.1 passed this time; it also passed 3 of 3 alone).
  - Left as they are: the flag `SupportsFolderBrowse` (PLAN.md asks for it, it is labelled, and D1b's folder listing will read it). Many older PROGRESS and ROADMAP entries already on `dev` name the other download manager directly; this branch adds none of them. Rewording them here would conflict with every other stream, so it is left for one pass on `dev` after the merge.

### A1: fair host connections, limits that apply live, TEST.1 (2026-11-08, branch `stream-a`)

- **UI.B7 (implemented, automated tests):** the per-host connection cap is now shared fairly. One gate per host with
  a limit that can change while connections are out (the old `host|limit` keys let old plus new connections run
  together). A free connection goes to the waiting job with the fewest; a job below its fair share
  (`max(1, limit / jobs on the host)`) asks the job furthest above it to give one back. Segment workers take a
  connection before a range; when asked, they stop at the last byte written, hand the rest of the range back and
  save the checkpoint at once, then wait for a connection again. Nothing new goes into the checkpoint: a kill between
  the hand-back and the save leaves the previous checkpoint, which only means fetching a few bytes again. Probes,
  single streams and HLS/DASH fragments never give a connection back. Fragments now count under the cap, one at a
  time per job; measured cost is under 1 µs per request, so they stay in.
- A job that waits with no connection shows "Waiting for a connection to <host>" (host name only) in WinUI and the
  CLI, through the runtime-only `WaitingForHost` (not saved) and `JobDto.WaitingForHost` (added field, no protocol bump).
- **UI.R21 (implemented, automated tests; not checked on screen):** a lower simultaneous limit, overall or a
  queue's, sends the newest excess back to Queued with its data (reverse of the start order). Left running, and
  counted in the excess so an older job isn't stopped instead: jobs whose server can't resume or hasn't said, jobs
  verifying or finishing, and jobs muxing. A provider job asks for one fresh link on its next run without using its
  dead-link refresh budget. A changed default connection count reaches running segmented downloads of the user's own:
  more connections add workers through the existing range-splitting path; fewer let the extra workers stop after
  their current range.
- **TEST.1 (fixed):** the cause was UI.B7 itself. The mock CDN now holds the two big files (`MockTorBox.HoldFile`,
  `Release`, `ReleaseAll`) so only the small one can finish. 20 of 20 passes after the change, and another 20 of 20
  after the final runtime fix (log: `artifacts\test1\runs.log`, not tracked).
- Tests: Core 826 passed, Engine 150 passed, Integration 1,096 passed (no failures, no reruns); the six
  `HostKillMatrixTests` passed again run on their own. New tests: `FairHostConnectionLimiterTests` (8),
  Engine `HostFairnessTests` (4, with `ConnectionCountChangesApplyToARunningSegmentedJob`), Integration
  `RuntimeConcurrencyLimitTests` (5).
- Not done: the owner's scenario on a test build (3 TorBox downloads, raise to 6, lower to 2); a running job moved
  into a full queue isn't sent back (only settings saves and queue changes trigger the check).
- **Review fixes (same day):** seven reviewer findings checked against the code; six were real and are fixed, and one is
  only partly fixed:
  - A segment worker waiting for a connection now gives up once nothing is left that it could take (no free range,
    none long enough to split). Before, a job with every byte fetched could wait for another job to free a
    connection. New Engine test `AWorkerWithNothingLeftStopsWaitingForAConnection` fails without the fix.
  - Connection churn on a shared host: a job below its share that gives a connection back keeps it for 250 ms
    while nobody waiting is short of theirs. A media job's next fragment, or a worker's next range, gets it back
    without making the other job yield, abort a response and flush its file. Anyone waiting below their share
    still gets it at once. Three new Core tests; the main one fails with the window off.
  - A late progress report from a requeued transfer still counts its bytes, but no longer sets the job back to
    Downloading or Connecting, or saves that state (checked in the lowering test).
  - While a yield is pending, a failed file write or checkpoint save is no longer taken for a yield. No test:
    the engine has no way to make a write fail on cue.
  - The limiter's cancel registration now lives on the waiting side, so a grant can't race its creation and leave
    it on the job's token.
  - Partly fixed: FTP now records whether the server took REST, so an FTP job that has resumed once can be
    requeued. A first FTP run stays above the limit, because a server that refuses REST makes the engine start
    over from zero. HLS/DASH detected through the engine's phase or the description now counts as resumable.
- Tests after the fixes: Core 830 passed, Engine 151 passed, Integration 1,096 passed, no failures and no reruns.
  TEST.1 then passed 10 of 10 runs in a row (appended to `artifacts\test1\runs.log`).

### Stream A2: Shift+Delete, TorBox cap and queue, FTP speed limit, counters, spanned zips (2026-11-08, branch `stream-a2`)

- UI.R20: Shift+Delete asks with "Delete from disk" ticked; the Delete key and the toolbar Delete ask with it unticked (it used to start ticked). Checked with `scripts\ui-keyboard-check.ps1` on a test instance.
- TB.L1: the live measurement (one cached 300 MB file, 1 to 16 parallel range requests, all HTTP 206, about 6.4 MB/s per connection, 92.4 MB/s at 16) is recorded. The TorBox per-file cap is now 16, and provider jobs take the smallest of the user's connections per download, the host limit and that cap on every run. The per-host limit (default 8) still caps one host overall. The plan doc's older note of "4 per file" from TorBox support and a TorBox staff reply saying there is no explicit limit disagree; both are noted in ROADMAP.
- TB.S1 to TB.S3: My TorBox can start an item waiting in TorBox's queue or remove it from the queue (`queued/controlqueued`, sourced from TorBox's API schema); a link's hoster password can be offered to that link's own archives, opt-in; a set's `.rev` recovery volumes go with it when archive deletion is on. Mock tests only, no live calls this round.
- PERF.6: FTP downloads now follow the per-download speed limit and the shared total cap, which covers HTTP and FTP together.
- UI.R6: live counters (main list Size, Time left, Transfer rate and their headers; details values and segment rows; the progress window; the speed graph) are right-aligned so they no longer move when the digit count changes. Checked by UIA bounds on a test instance; not checked by the owner, at high DPI or in light mode.
- A4.D: spanned zip sets (`name.z01` ... `name.zip`) are one set, opened from `name.zip`; a missing middle or last part is named. WinZip has no command line here, so the tests build spanned sets by hand; the bundled 7-Zip 26.04 lists and extracts them.
- Tests: Core 819, Engine 149, Archives 321, TorBox 323, Integration 1,103, all passed.
- Not done: the owner's on-screen checks; a live TorBox check of TB.S1/TB.S2; a spanned set made by a real zip tool.
- Review fixes: `.rev` files now go only with RAR sets, so a 7z, zip or split set called `name` leaves a `name.rev` beside it alone (new Archives theory and an Integration test with a zip). `scripts\ui-keyboard-check.ps1` now needs `-PathPart` naming the test instance's folder, refuses the installed app, and stops if a delete dialog is still open before sending the next key. `dm remote add` and `dm remote retry` refuse `--link-password-for-archives` without `--link-password-stdin` instead of ignoring it (exit 2; covered in the CLI test), and the main usage text shows they go together. A spanned zip with locked headers and no password now names the gaps it can see from the file names, as a RAR set does. 7-Zip has no zip it calls header-encrypted, so no test reaches that path. After the fixes: Archives 329, Integration 1,104, all passed.

### Stream C: the extension, local parts (2026-11-08)

- Edge: merged `feature/edge-support` (277bf73, 9381038, bc728d0, b996c90, 0f60dfe) into this branch (a143c47); its registration tests use their own fake store and registry, and pass.
- EXT.B1: the extension now catches every file type except an editable exclude list (web pages, images); a copy still on the untouched old allow-list moves over once. A download left in the browser is noted in the popup with the reason (not for `blob:`/`data:`), with a notification for the user's own rules. The badge reads OFF while automatic sending is off or paused, and popup and Settings say so. Cookies of a same-origin final address are merged in without duplicates. Not yet checked live in the owner's Edge with the file host's Direct Download button.
- EXT.R2: the popup lists the newest 20 of all downloads with Pause, Resume/Retry and Delete (with "Delete the file too"). `NativeBridgePolicy` is a role-based allow-list both ways; the new `settings.downloadFolders` returns only the default folder and favorites; snapshots still lose their settings.
- EXT.R1 (this PC): "When sending a download: use the app's default folder / ask each time". Ask opens `handoff.html` with the default folder, favorites and Choose…. The place for the later "this PC or the server" choice is marked in `handoff.html` and Options.
- EXT.E1 Firefox: `npm run build:firefox` writes `dist-firefox` (gecko id `inbound-bridge@kaan88`, event page, no key); a Firefox native manifest and the HKCU Mozilla key in Setup and the scripts; `scripts\package-extension-firefox.ps1` builds the AMO zip (not signed: needs the owner's account).
- Security: the native host skipped its caller check when no `chrome-extension://` argument was given. It now accepts only a trusted Chrome/Edge origin or the trusted gecko id and exits otherwise (tested as a real process).
- UI.R3 confirmed by tests (browser handoffs start at the default folder, also after a Host restart; a one-off folder is not kept). UI.R4 checked in code and by new `fit-window` tests; `handoff.html` refits after the favorites load. On-screen checks are left for the owner.
- Tests: extension `tsc --noEmit` clean, npm 85 of 85, Chrome and Firefox builds succeed; solution build clean; Core 833 of 833; Integration 1,176 of 1,176 (full suite).

#### Stream C review fixes (2026-11-08)

- Rules move: a copy on the old allow-list that also had its own excludes now gets the new default excludes added to them, so pages and images, never caught before, stay in the browser (the move had kept only the user's excludes).
- "Ask each time": the folder window keeps a port open to the extension's worker. Its pings keep the worker, which holds the browser's file-name callback in memory, alive; the port closing is how a closed window is seen, replacing a listener that woke the worker for every browser window that closed. Closing the window while the download is being sent waits for the app's answer. If the worker restarts anyway, Keep in browser cancels the stuck download and starts it again in the browser once. Tested with mocks; that the pings keep a real Chrome or Edge worker awake is not yet checked in a browser.
- Firefox (no file-name hold): the download is paused as soon as the rules say to catch it, before the slower Delete-gesture lookup. One that will not pause is left in the browser with a note instead of being sent while it still downloads; one that finished first is noted; one the browser cannot continue after a refusal is noted.
- Popup actions (pause, resume, retry, delete) use their own native connection, so a slow delete cannot hold up an automatic handoff on the shared one.
- Bridge: job actions from the extension are checked by payload too: exactly one download, no list, file deletion only with Delete (`job.remove`). Tested in Core and through the real native host.
- `TrustedCaller`'s comment and the docs now say what the check is: it stops other browser extensions, not local programs, which can reach the Host's pipe anyway.
- Uninstall: installs upgraded from 0.5.3 or earlier still have `uninsdeletekey` for Chrome's key in their uninstall log. Uninstall now notes another program's working Chrome registration first and writes it back afterwards. Checked by a source test and an Inno Setup 7 compile of Setup; not by a real upgrade and uninstall.
- Not changed, with reasons: cookies of a cross-origin final address are still not added (the engine drops `Cookie` on a cross-origin request, `HttpDownloadEngine` `sameOrigin` check, so they would go nowhere); automatic sending still starts off on a fresh install (the 0.4.0 privacy choice; question below).
- Tidy: CLAUDE.md's Chrome for Testing path uses `%LOCALAPPDATA%` instead of the account name; the BOM added to `ProviderLinkTests.cs` is gone.
- Tests: extension `tsc --noEmit` clean, npm 92 of 92, Chrome and Firefox builds succeed; solution build clean (2 old nullable warnings); Core 850 of 850; Integration bridge/registration/native-host tests 106 of 106; full Integration suite 1,176 of 1,177, the one failure the known flaky TEST.1 (`HostKillMatrixTests.KilledWithAPackageHalfDone…`), which passed when run alone.

#### Questions for the owner (stream C)

- Automatic sending starts off on a fresh install (since 0.4.0, so cookies are sent only after the user agrees). EXT.B1 now makes this visible (OFF badge, popup, Settings, one notification) but does not turn it on. Should a fresh install catch by default?

- AMO signing: upload `artifacts\firefox-addon` (from `scripts\package-extension-firefox.ps1`) as an unlisted version from your addons.mozilla.org account, so release Firefox can run it.
- The privacy policy (`docs/chrome-extension-privacy.md`) changed (final-address cookies, the "left in browser" notes, folder window, popup actions); the online copy needs updating when this ships.
- The EXT.B1 live check in your Edge with the file host's Direct Download button, when you are around.

### Plan for the remaining work; owner hands over (2026-11-08)

- Owner: "plan time." Decisions: UI.R1 closed ("happy with the ui"); P3.6 works; UI.R5 already a thing; TB.S1 to TB.S3 approved; all PERF items approved; testing items approved (TEST.1, independent recovery check, clean install and upgrade, light mode); small fixes UI.R6 and A4.D to do; Edge and Firefox with the extension work, Firefox signed through AMO; the extension popup sees all downloads and, when asking, can send to the local app or the Docker server; lowering the limit requeues the newest first; clean-install testing in a VirtualBox Windows trial VM on E:; WinZip is installed for spanned-zip test sets; TorBox is open in a separate Edge window for the EXT.B1 test.
- New owner and user reports recorded: UI.B7 (limit raise leaves jobs connecting), UI.R20 (Shift+Delete), UI.R21 (limits apply live), TB.L1 (measure TorBox limits).
- The plan is `PLAN.md` (untracked, in the worktree). Owner: "don't ask me for anything, I'll be a sleep. it's completely yours. do whatever is planned." Questions that need the owner go in the "Questions for the owner" list below instead of stopping.
- Stale rows tidied: UP.B1 and UI.B2 released in 0.5.1, PERF.1 superseded by PERF.5, A1/A2/A1.C/A4 on main; CLAUDE.md status updated.

#### Questions for the owner (2026-11-08 run)

Each has a default already in place; nothing waits on the answer unless it says so.

- **Firefox (EXT.E1), needs you:** upload the zip from `scripts\package-extension-firefox.ps1` (`artifacts\firefox-addon`) as an unlisted version from your addons.mozilla.org account, so release Firefox can run it.
- **EXT.B1 live check, needs you:** computer use can only look at Edge, not click in it. The fix is in the extension build on `dev` (catch everything except an exclude list, a note when a download stays in the browser, cookies for the final address). To check the file host's Direct Download button, load `extensions\chrome\dist` in your Edge, or click the button while I watch the extension's log.
- **Privacy policy:** `docs\chrome-extension-privacy.md` changed (final-address cookies, "left in browser" notes, the folder window, popup actions). The online copy needs updating when this ships.
- **Automatic sending on a fresh install:** still off (the 0.4.0 privacy choice), now clearly shown as OFF in the popup. Turn it on by default?
- **TorBox per-file cap (TB.L1):** now 16, from your measurement. Sources disagree: a note in `docs\plans\torbox-integration.md` marked as from TorBox support says at most 4 per file; a TorBox staff reply on its feature board (2024-03-10) says no explicit limit, only fair use. Keep 16 or go back to 4?
- **TorBox host limit:** the per-host limit (8 by default) still caps the TorBox CDN host overall, so one TorBox file gets 16 only if you raise it in Options. Give TorBox CDN hosts a higher default of their own?
- **Delete (UI.R20):** Delete now opens unticked and Shift+Delete ticked. The context menu and File menu "Remove" still just remove the row and keep the files, with no dialog. Is that what you want?
- **Lowering the simultaneous limit (UI.R21):** downloads whose server hasn't said whether it can resume, and FTP downloads that have never resumed, keep running (stopping them could lose their data or start them over). Requeue them anyway?
- **Media downloads:** HLS/DASH fragment requests now count under the per-host cap, one connection per media job (cost under 1 µs per request). Keep it?
- **Connections for running downloads:** a changed count reaches running downloads only for your own HTTP downloads; TorBox, protected and FTP downloads keep the count they started with. Acceptable?
- **Full queues:** should moving a running download into a queue that's already full also send its newest extra download back to Queued? (Now only settings saves and queue changes do.)
- **Server program settings (D1a):** a network server refuses settings that run programs (post-download command, FFmpeg path, update feed and key, system completion actions). Allow them for a signed-in admin later, or keep them refused?
- **Exit with downloads running (UI.B11):** exiting the app (and possibly a Windows sign-out) pauses running downloads, so "Resume unfinished downloads when Inbound starts" only helps after a crash. Resume them at the next start too?
- **Selected rows (UI.R22):** selected rows are almost invisible in both themes (about 1.1:1). May I tint them (for example with the highlight colour) or turn on WinUI's selection bar? It changes the dark look you approved.
- **Second dialog guard (UI.B8):** delete, messages, questions and the address dialog are guarded; about 40 other dialogs are not. Convert them all to the shared helper?
- **Statistics and Theme (UI.R23):** give the Statistics window a Close button like the other windows? The settings' Theme value is never read (the window uses View > Dark Mode): remove it, or make it the theme source?
- **"Closing the window ends it" by default (PERF.5):** with the window closed it saves about 120 wake-ups a second, 6 s of CPU an hour and 200 MB; opening then takes about 0.65 s with 500 downloads and 1.1 s with 5,000 (0.3 to 0.45 s for a hidden window). Recommended on; still off.
- **CLI downloads while the window is closed:** with that option on, a CLI download gets no completion window, because only a running main window opens them. Should the Host open them instead?
- **Speed graph history (UI.R24):** keep the last hour in the Host so it survives a closed window?
- **Close button (TEST.4):** a scripted close is ignored the first time. Does a real click on the close button ever need two tries for you?
- **Segment retries (UI.B15):** a segment that keeps getting bytes now retries without limit, as single-stream downloads already did. Want a cap on total retries per segment instead?
- **Download all links:** should its Save in field show the app's default folder, as the folder window does? Today it is blank, meaning the app decides.

### Owner's remaining checks (2026-11-08)

- Owner: the 0.5.2 list "all work" (UI.R9 to UI.R13, TB.F15, UI.R2 verified). TB.F9 and TB.F14 "fixed already"; the add flow (TB.F10, TB.F2) "also done and fixed"; hover info (UI.R7) done; smoothing (UI.R8) removed; update window placement (UI.R14) fixed. Edge and Firefox go with the extension work (EXT.E1).

### Owner's 0.5.3 check; extension requests (2026-11-08)

- Owner on the 0.5.3 list: "all work as intended." Marked verified: UI.B4, UI.B5, UI.B6, UI.R15 to UI.R19, PERF.5 (option; follow-up still listed). UI.R6: "much better, they only move when digit count changes"; next step recorded. UI.R14 shows only on the next update.
- Owner: make the extension more complete (status, pause, delete) beside the folder question: EXT.R2. Regression: the extension doesn't catch downloads reliably, for example TorBox's Direct Download button: EXT.B1; the test needs the owner's Edge signed in to TorBox. Both to work on during the web UI and Docker work, as agreed.

### Release 0.5.3 (2026-11-08)

- Owner: "publish the build so I can test". Main commit 79b8884, tag v0.5.3, https://github.com/Kaan88/inbound/releases/tag/v0.5.3. Contents: everything since 0.5.2 (UI.B4, UI.B5, UI.B6, UI.B3, UI.R6, UI.R14 to UI.R19, PERF.5 option).
- Checks: manifest signature verified with the release key (same as 0.3.0 onwards), the latest feed serves 0.5.3, the downloaded Setup matches SHA256SUMS, the package has 7-Zip, the credential-token scan found nothing, and the squashed main tree equals `torbox-addflow`.
- Not done: the owner's on-screen checks.

### Extension request for later (2026-11-08)

- Owner: an extension option to ask whether to save to a default folder set in the extension, or a custom location, probably as a popup from the existing menu; to do during the web UI and Docker work. Recorded as EXT.R1.

### Two options: folders by category (UI.R19), closing ends the window (PERF.5) (2026-11-08)

- Owner: "make folders by category an option. make closing the window as part of perf changes an option and implement but make note of more investigation in the future."
- Both are in Options (and the CLI), off/on as before by default. Test instance: closing with the option on ended the window process, the Host kept running, and a new start showed the window. Further investigation for PERF.5 is listed in ROADMAP.
- Tests before these options: all suites passed except the known flaky TEST.1; the new folder and settings tests (24) pass.
- Tests with these options: Core 818 passed; Integration 1,090 of 1,091, the one failure the load-sensitive RAR password test (30 s timeout), which passed alone in under a second.

### UI.B6: folders in Options (2026-11-08)

- Owner report recorded and fixed as UI.B6: category folders follow a new default only when they were still under the old one; Options now shows what was saved, loads fresh settings when it opens, and adds no longer change category folders unless asked. Folder and category tests (61) pass; not checked on screen.

### Limits moved to the Downloads menu; UI.R6 tabular digits (2026-11-08)

- Owner: "I meant a dropdown, like under downloads menu on the toolbar. this is ugly. also, did counter font fix happened?" The limit buttons left the toolbar; they are submenus under Downloads with the value in each name (checked on a test instance with UI Automation). UI.R6 hadn't been done; now all unstyled text uses same-width digits. Neither is checked on screen.

### Web UI and Docker decisions (2026-11-08)

- Owner: "for web ui tech, whatever is fancy. published on ghcr sure. turn it off on home network, these won't be open to internet." Recorded in `docs/docker-webui-plan.md`: Svelte 5 + Vite + Tailwind, GHCR on each tag, sign-in can be turned off.
- Owner provided a LAN-only Linux VM for container tests (details kept out of tracked files).

### User reports built in one go (2026-11-08, branch `torbox-addflow`)

- Owner: "Yes, unstarted ones", then "make ftp speed limit a future maybe goal, also implement everything I said in one go and test it."
- Built: UI.B5 connections for unstarted downloads and the default queue following the overall limit; UI.R15 and UI.R16 toolbar menus; UI.R17 all files chosen in My TorBox; UI.R18 long links cut short in Add URL; UI.R14 update window sized for scaling and centred on the main window; UI.B3 likely cause (stale settings in the window) fixed; PERF.6 recorded. Details in ROADMAP.
- Tests: Core 818, Engine 146, Archives 309, Containers 183, TorBox 321 passed; Integration 1,088 of 1,089, the one failure the known flaky TEST.1. Test instance (UI Automation): the three toolbar buttons show "3 downloads", "8 connections" and "No speed limit"; the menus' items, the update window and the tray focus weren't checked on screen.

### UI.B5: one shared speed cap (2026-11-08, branch `torbox-addflow`)

- Owner: "Yes, shared cap". The Options speed limit is now a total for all HTTP/HTTPS downloads together, applied live to running and resumed downloads, and no longer copied into new downloads. Details in ROADMAP UI.B5.
- Tests: Core 818, Engine 146 (two new) and Integration 1,087 (one replaced) run. One Integration failure, the known flaky kill test (now TEST.1, with its message); it passed 2 of 3 alone and sets no speed limit.
- Not done: FTP has no speed limit at all; the connections and queue-maximum decisions are still open.

### UI.B4 tray focus fix; UI.B5 limits checked (2026-11-08)

- Owner: "clicking tray popup does not bring the app into focus." Recorded and fixed as UI.B4 (foreground permission passed from the Host and the second launch to the running window). Not yet confirmed on screen.
- Owner: "yes, check limits". Found by reading the code (UI.B5): the simultaneous-download limit works, but queue maximums also cap it; connections per download and the "global" speed limit are copied into each job when it is added, so changes don't reach existing or resumed downloads, and the speed limit applies to each download separately, not to the total. Not changed; it needs the owner's decisions.

### User reports and owner findings (2026-11-08)

- Recorded in ROADMAP: UI.B3 (default folder not reaching Add Links; owner couldn't reproduce), UI.R15 (simultaneous downloads and connections as a main-window dropdown; check limits apply to resumed jobs), UI.R16 (speed presets in MB/s as a dropdown), UI.R17 (select all files in the download window), UI.R18 (shorten long links in the Add Links dropdown), and UI.R14 updated (update progress centred on the main window, or inline). Nothing built yet.

### PERF.4: what still wakes the hidden window (2026-11-08)

- Owner: "Yes, trace it". Per-thread counters: two threads in the window process wake about 95 times a second each, whether the window is shown, minimised or closed to the tray, in installed, published and bin builds, with an empty test instance too.
- They aren't .NET threads (dotnet-stack, installed into the scratchpad, run against a test instance only). Their start address is in `nvwgf2umx.dll`, the NVIDIA Direct3D driver, which WinUI loads for drawing. Not caused by Inbound's timers.
- Option for the owner: end the window process when it is closed to the tray and start it again from the tray. That removes these wake-ups and the window's memory while closed, but reopening takes a fresh start. Not built.
- Owner: "whatever debugger you need, install it." Installed WinDbg (winget `Microsoft.WinDbg`). cdb, noninvasive, on a test instance: both threads wait in a loop inside the NVIDIA driver, with nothing else on their stacks.
- Owner: "make closing completely a future goal/idea to try." Recorded as PERF.5. Owner asked whether RivaTuner could be the cause: RivaTuner and Afterburner are running and `RTSSHooks64.dll` is loaded in the window, but it isn't on these threads' stacks. A comparison with RivaTuner excluded for the window would settle it.
- Owner closed RivaTuner ("I just closed it, easier."). A fresh test window then loaded no RivaTuner hook and still had both driver threads at 95/s: RivaTuner is not the cause.

### PERF.4: idle check on installed 0.5.2 (2026-11-08)

- Owner: "do the idle check". 30 minutes, window hidden, read-only sampling, compared with the 0.5.1 run: Host CPU 3.0 s to 0.08 s, Host reads 17,910 to 0, Host other I/O 35,821 to 3, Host context switches 12/s to 0.8/s; window CPU 16.1 s to 3.0 s. No writes from either process.
- Still open: the hidden window averages 202 context switches/s (was 212); the cause isn't traced yet. Details in ROADMAP PERF.4.

### Release 0.5.2 (2026-11-07)

- Owner: "Release 0.5.2". Contents: the PERF.4 idle fixes. Main commit 0ba1f74, tag v0.5.2, https://github.com/Kaan88/inbound/releases/tag/v0.5.2.
- Checks: manifest signature verified with the release public key (same key as 0.3.0 onwards), the latest feed serves 0.5.2, the downloaded Setup hash matches SHA256SUMS, the package contains 7-Zip, and the credential-token scan of the tree found nothing. The squashed main tree is identical to `torbox-addflow`.
- Not yet done: updating the installed app to 0.5.2 and a 30-minute idle measurement on it.

### PERF.4: idle fixes (2026-11-07, branch `torbox-addflow`)

- Tray refreshes on job and setting changes instead of every second, and checks browser integration only when its menu opens. Clipboard polling runs only while monitoring is on. The speed graph draws only while it can be seen.
- Test-instance probe: Host other I/O 1,800/min before, 7/min after; window 110 ms CPU per 30 s visible, 62 ms minimised. Not yet measured on an installed build.
- Build clean. Core 818 passed. Integration: one failure in the first full run, then 1,087 passed on the rerun; the failing test's name wasn't captured, most likely the known load-sensitive RAR test.

### Memory, and the Docker and web UI plan (2026-11-07)

- The owner asked for a Docker and web UI plan and how memory use is, since high memory is what they dislike in another download manager.
- Measured, private bytes, build 0.5.1: the owner's idle copy has Host 31 MB and window 215 MB. In a test instance: Host 19 MB idle, 48 MB with 500 queued jobs, 69 to 72 MB (flat) while four 500 MB files downloaded from the local test server; window 233 to 235 MB with 504 rows, the same as empty. Not measured: long runs, real internet, thousands of jobs.
- Plan in `docs/docker-webui-plan.md`; ROADMAP D.PLAN, PERF.1 to PERF.3. Waiting on four owner decisions listed in the plan.
- Owner: PERF.1 "Not now", and "I care more about idle resource usage than 200 mb." Recorded as PERF.4: measure and cut what Inbound does while idle (CPU, wake-ups, disk writes, network polling).
- PERF.4 measured on the installed app (30 min idle, window hidden): Host 0.17% of a core, no writes, no network, but steady reads (~10/s) and other I/O (~20/s); window 0.9% of a core and 212 context switches/s, likely the speed graph redrawing every second while hidden. Details and fixes in ROADMAP PERF.4. About 35 minutes, mostly waiting.

### Release 0.5.1 (2026-11-07)

- The owner asked for 0.5.1 with the UI.B2 and UP.B1 fixes, after proper tests (results under Release 0.5.0 below).
- Published as v0.5.1 on `Kaan88/inbound`: main `460c11f`, the same tree as branch `ab142d7`, tag `v0.5.1`, five assets. Same signing key; manifest signature verified; the live feed says 0.5.1; the downloaded Setup matches SHA256SUMS; the package holds 7-Zip. No token from the test credentials file in the tree.
- After the version bump: Core 818 and 65 update, release, bridge and settings integration tests.
- Not verified: install of Setup 0.5.1, auto-update from 0.5.0 to 0.5.1, Options on screen by the owner.

### Release 0.5.0 (2026-11-07)

- The owner finished the UI and TorBox tests and asked for quick extraction tests, then push and release.
- Published as v0.5.0 on `Kaan88/inbound`: the branch squashed onto `origin/main` as one commit (`1c2ceb0`, same tree as `bbc0a28`), main pushed, tag `v0.5.0`, five assets.
- Checks: public key equals 0.4.0's, `releases/latest/download/manifest.json` says 0.5.0, the downloaded Setup matches SHA256SUMS. The tree held no token from the test credentials file. The package holds `app\tools\7-Zip`.
- The tests before release: Archives 309, Core 818, 86 runtime archive integration tests. The full Engine and Integration suites were not run again after A4.C, which touched only archive and UI code.
- The store extension stays 0.4.0. Since 0.4.0 its source has changed only in one message, for a refused extension ID; bump the version before the next store upload.
- Not verified: install of Setup 0.5.0, auto-update from 0.4.0 to 0.5.0.
- Owner report: 0.4.0 can't update ("The update manifest signature is invalid."). The live manifest verifies against the built-in key; the installed settings' key is only its first line. Recorded as UP.B1; not fixed yet.
- After the key was restored through the installed CLI (`settings set updatekeyfile`), the owner updated 0.4.0 to 0.5.0 from the app and reports it works. They noted the update progress window opens top-left and clipped: UI.R14, fix whenever.
- Owner report: Options does not open in 0.5.0 (cast of StackPanel to Grid on the Extraction page): UI.B2. Fixed on the branch together with UP.B1 (hidden update boxes removed; a bad stored key is replaced by the built-in one).
- Tests before any 0.5.1: Core 818, Engine 144, Integration 1086 of 1087, TorBox 321, Archives 309, Containers 183, extension 56, typecheck clean. The one failure, `Rar4PasswordsWorkAndAWrongOneIsReportedAsSuch(encryptedHeaders: True)`, timed out at 30 s under the full load and passed three runs alone in about 0.5 s. The suites ran before the hidden-box removal, which is WinUI-only; Core ran again after it (818). UI Automation check of the built app: 9 of 9.
- The owner said not to start .exe files from the PowerShell tool, since a Windows warning pops up and the session hangs. Recorded in CLAUDE.local.md and memory; the check above was started from Bash.
- The owner asked to note that spanned zips (`name.zip` with `name.z01`) are not recognised, since nothing on this PC can make one to test with. Recorded as ROADMAP A4.D.

### Owner's try of favorites and free space (2026-11-07)

The owner said the features work. They reported two layout faults (UI.R11, UI.R12, both fixed and built, not yet seen on screen). They asked for three more things before 0.5.0: a bundled extractor (A4.B), a hoster status window (UI.R13) and hover info on the speed graph (UI.R7). They also asked that TorBox's API limits be respected. The hoster window reads the Host's cached list, and the client already paces on TorBox's rate-limit headers.

UI.R13 is built. `remote.hosters` returns the first account's cached host list with when it was read. The window shows each host's icon (https only), up or down, today's links and bandwidth against the limits, the per-file limit and notes, with search and a "Down only" filter. A pipe test checks that a second open inside the hour makes no provider call. Not seen on screen yet: the owner tests it.

A4.B is in. The owner said to drop UnRAR and its program-path setting, then asked for "the best possible extractor for most popular files". The bundled 7-Zip 26.04 does that: RAR4 and RAR5, 7z, zip, tar, gz, bz2, xz, zstd, iso and cab, with passwords and split sets. libarchive is weaker on RAR5 and can't open encrypted RAR. Checked by hand on 7-Zip 26.04:
- A password goes on stdin, and with `-sccUTF-8` 7-Zip reads it as UTF-8, so no password needs the command line any more.
- A wrong password gives "Wrong password" only for RAR5. 7z, zip and RAR4 say "Data Error in encrypted file. Wrong password?", which can also mean damage.
- `-sns-` keeps an archive's NTFS streams from being written, so archives carrying streams now extract (without them) instead of being refused.
- 7-Zip has no switch for the mark of the web, so the download's zone is written onto the extracted files.

Tests: Archives 291, Core 811 (the UnRAR-path tests are gone), 149 archive integration tests through a real Host. A4.C followed: 7z, zip, tar, compressed tars and split `.001` sets are recognised for automatic and manual extraction. A .tar.gz comes out as its files, through a checked inner tar. A split set 7-Zip finds cut short waits for its next piece. Tests: Archives 309, Core 818, 86 runtime archive integration tests, including a downloaded zip extracting by itself through a real Host. Spanned zip (`.z01`) is not recognised. The owner then reported the UI and TorBox tests done and asked for quick extraction tests, then push and release.

### UI.R9, UI.R10: favorite folders and free space (2026-11-07, branch `torbox-addflow`)

Built; the owner tests it on screen and asked that no live or screen checks be run here.
- **Host:**
  - Settings gain `FavoriteFolders` (name and path, normalized, at most 50) and a Host-owned `RecentDestinationDirectories` (newest first, five), which a client's old copy can't undo.
  - A new `folders.space` request measures each folder on the Host's side, with a 2 s limit per folder.
  - On Linux each folder is measured on its own mount, not `/`, and a container folder that isn't on a mounted volume is flagged.
- **Window:** a star button beside every folder box (Add URL, batch, Add Links, container import, My TorBox, Options) lists favorites and recent folders with free space, and saves or removes the typed folder. A line under the box shows its free space. Add URL warns when the checked size won't fit. Options > Save to has the favorites list, with rename, reorder and remove, and doesn't undo favorites saved from a star while it's open.
- **CLI:** `dm folders [--json]`, and `--to FAVORITE` on `add` and `add-batch`.
- **Tests:** 9 Core and 4 Integration tests are new.
- **Fixed along the way:** `TheClientWaitsOutAnUsedUpBudgetInsteadOfHittingTheLimit` failed about two runs in three. The reset time is rounded down to the millisecond and timers can wake early, so the client could call just before the window ended. It now waits 250 ms past the reset; the TorBox suite then passed five runs in a row.

### Owner request: download locations and free space (2026-11-07)

Passed on from a prospective Docker user: choosing the download location matters most. They want several saved locations with each one's free space, especially in Docker, where another download manager's folder browsing was poor. The default folder, last folder, category folders and Browse already exist; free space and saved favorites don't. Recorded as UI.R9 and UI.R10, with a note on D2 for a Host-side folder browser. A plan went to the owner; nothing is built yet.

Also: the 0.5.0 release was in progress when the session ended. The version bump is uncommitted in the `torbox-addflow` worktree, and the last full test run's result was lost, so it has to run again.

### Owner's third try: add flow works, Import Torrent File's picker didn't open (2026-11-07, branch `torbox-addflow`)

The owner tried the sandbox build of the merge `3519653`. The add flow worked: a cached item showed its file choice at once, and an uncached magnet's choice came later. Import Torrent File showed "The file picker couldn't open." The sandbox's processes ran elevated, and the older WinRT pickers fail there. All eleven pickers now use the Windows App SDK pickers (TB.F15). The UI builds. Not yet seen on screen in an elevated window. The owner asked for a release next.

### TB.F11, TB.F12, TB.F13: direct links in Add Links, hidden addresses, My TorBox closes (2026-11-07, branch `torbox-addflow`)

Traced against `tools/TestServer` and a fake CDN in the tests. No live run: the links the owner used had expired, and nothing was added to the account. Each new test fails with its fix undone.
- **TB.F12, security: a direct link's address is no longer shown, copied or exported anywhere.** One rule, `ShownAddress`, says what of an address may be shown. A link on a TorBox host, or any address the Host says held the key (`JobDto.AddressHidden`), shows its site alone, such as `store-1.weur.tb-cdn.st`, labelled Site. A masked link still gave away the account's file id. Why the completion window showed the token while the progress window masked it was not found for certain: both now show the site, from the same rule.
  - Uses it: the progress and Download complete windows, Properties, the list's second line and tooltip, the Description column, the Refresh address box (it starts empty and an empty box tries the saved link), the Add Links lines and the CLI list and details.
  - The JSON list export leaves hidden addresses out, as it already did for provider files. Logs were already masked, and recent addresses already left out protected links. The test below checks both.
  - No secret is kept anywhere new. The engine still downloads from the protected copy.
  - Tests: `ShownAddressTests` (14), and `APastedDirectLinksTokenReachesNoSurface`, which pastes a link with a known key and checks the snapshot every client gets, the export, the service log, recent addresses and the files on disk.
- **TB.F11: each report traced and fixed.**
  - *Three windows.* The Host put each pasted link in its package only after announcing it, so the first "started" saw a single job with no package and opened its own window. The window rule also wanted more than one member. Jobs now join their package before anyone hears of them, and the window rule moved to `DownloadWindows` in the Runtime, where it is tested: one package window per start, and one window when a package completes.
  - *The Download complete window for one file of a package.* The same rule: a finished file of a package waits for the package's own completion.
  - *The empty file failed.* The engine asks for `bytes=0-` once it knows the server. For an empty file the server can only answer "nothing in that range" (416 or 206 with `bytes */0`), which was taken as a failure. With no bytes, a total of 0 and no other expected size, that answer now completes an empty file. With no size known it still fails as before.
  - *A name left as the link's id.* The add-time name check is a separate request, and a failed one left the id. The name in the transfer's own answer is now used when the name came only from the address, before any byte is written. A name the user typed, a moved or renamed file, and a name that is the token are never replaced.
  - Tests: `DirectLinkBatchTests` (3 more beside the token test), and 3 in `ResolvedLinkEngineTests`.
- **TB.F13: Download in My TorBox closes the window once the Host has the download on its way.** A refusal, a job that failed or was blocked at once, or a note that part of the request wasn't used keeps it open with the reason. `RemoteRules.DownloadStarted` holds the rule, with tests. Show progress and Open downloaded location are gone, since they only served the open window. Progress details on the package row opens the package window, and that window has Open folder once it is done.
- **Not checked:** none of this was walked on real screens. Why the owner's own name check failed is not known, since the links had expired. How the real CDN answers a range request for an empty file is not known: both answers are handled.
- **Suites, one run each at the end:** Archives 293, Containers 183, Core 766, Engine 144, Integration 993, TorBox 314, all passed. In an earlier full Integration run, `KilledWithAPackageHalfDone_TheFinishedFileIsKeptAndTheRestGoOn` failed once and then passed 3 of 3 alone. That test covers provider jobs, which these changes leave alone. The failure message wasn't kept, so the cause is not known.
- Commits: `4549382` (TB.F11, TB.F12), `57015e1` (TB.F13).

### Five review claims checked, against the code, the mock and the real API (2026-11-07)

Independent reviewers made five claims. I checked each against the code and reproduced each with a test before fixing it. Each new test failed with the fixes taken out and passed with them in. Nothing here touches the add flow (TB.F9, TB.F10).

| Claim | Verdict | Cause and fix |
|---|---|---|
| A stale settings save puts back a recent address that holds the stored key | Real | The settings save copied the client's recent addresses as they came. The store's own filter only knows provider hosts. The save now drops any address that holds a stored key, the same rule the scrub uses. Test: `AStaleSettingsSaveCannotPutAKeyBearingAddressBack`. |
| Two jobs can reach one provider item and both download it | Real | Only the add checked for a twin, and it skips failed jobs and compares only fields already set. Adopting an item found by hash, a submission answered with an item id, and a retry that keeps its item id now check, under the gate, for another job that is not failed and holds the same item. Such a job fails with words saying another job brings the files, and a retry is refused with them. Tests: `ACopiedMagnetApprovedLaterDoesntDownloadAnItemAnotherJobBrings`, `RetryingAFailedJobWhoseItemAnotherJobNowBringsIsRefused`. |
| Delete from TorBox fails for an item already gone, and a client leaving can split the delete | Real, and the real API differs from the mock | The filter let a not-found answer escape as an error. It now counts as removed. Once the provider has deleted, the local removal no longer takes the client's token. **Seen live** with ids not on the account: deleting a torrent answers 500 `DATABASE_ERROR`, and deleting a web or usenet download answers 200 "deleted successfully". Never `ITEM_NOT_FOUND`, which the mock taught. The TorBox client now looks the item up after a service error on delete, and reads it as not found only when the look says it is gone. An outage on an item that is there stays an outage. The mock now answers missing deletes as the real service did. Tests: `DeletingAnItemAlreadyGoneFromTheProviderTakesTheJobOff`, `AClientLeavingOnceTheProviderDeletedStillTakesTheJobOff`, `DeletingAMissingItemAnswersAsTheRealServiceDid`, `AnOutageDeletingATorrentThatIsThereStaysAnOutage`. |
| A retry during a running submission step sends the old password or approval | Real | The step sent the link password and the cached-only flag from the record it started with. It now sends them from the record as saved in the move to Submitting. Test: `APasswordGivenWhileALookIsWaitingIsTheOneSent`. |
| An offered package password can replace the user's | Real | The keep-existing check ran outside the gate and only against memory. It now runs under the gate. A password file held open at the start is remembered and counts as a password. An archive side whose saved passwords were never read refuses offers, so the remote job keeps its copy. Tests: `AnOfferNeverReplacesAPasswordWhoseFileWasHeldOpenAtTheStart`, `AnArchiveSideThatCouldNotStartTakesNoPasswordOffers`. |

**Bears on the add flow, for the owner, not built:** adding the same thing again after its job failed makes a new job instead of reviving the failed one. That is still the case.

**Not checked:** the race fixes are proved with the mock only; the delete of an item that exists was seen live in earlier stages, not again here. No sandbox Host ran in this stage.

### Comparison with another download manager's TorBox support, read only (2026-11-07)

Another download manager's TorBox support was read for its API usage; findings are folded into the plan (`docs/plans/torbox-integration.md`). It was not run. No code changed in this stage.

TorBox API facts the plan relies on, from the TorBox schema and this app's own runs (live unless said):

- Ready means `download_present` and either `cached` or `download_finished` (read from code). Status waits use `eta` clamped to 15 to 60 s, else 30 s (read from code).
- Web download creates are spaced 65 s per account, reserved before the POST. Asking again for a link already on the account returns the same id and no second item.
- A dead hoster link answers 500 `DOWNLOAD_SERVER_ERROR` with the reason in words and no item. `LINK_OFFLINE` was never seen from the real service.
- TorBox did not start queued items by itself in 14 to 16 minutes with slots free.
- Direct links are fetched fresh for each attempt and never kept on disk. Real link expiry was not seen live.

**Kept different on purpose:** no guessing of archive passwords, no global password list, no clearing of TorBox notifications, no stored direct links.

**Suggestions added to ROADMAP**, for the owner to judge, nothing built: TB.S1 start or remove a queued TorBox item and give it its own words; TB.S2 offer a link's own hoster password to its own archive set; TB.S3 remove `.rev` recovery volumes with their set.

### Interruption matrix and regressions, against the mock and TestServer (2026-11-07)

Nothing here used the real account. The real Host process was killed hard, by process, against the mock provider and `tools/TestServer`. CLI runs used a sandbox instance of their own, with binaries copied out of the repo.

**Interruption matrix.** After each restart I checked: one create on the mock, one child job per file, every file's size and SHA-256 equal to the mock's bytes, and no `.part` or ` (1)` file left. New cells are in `HostKillMatrixTests`.

| Kill point | Evidence | What I saw |
|---|---|---|
| During a submit the provider never got | `KilledDuringASubmitThatNeverArrived...`, new | The record on disk said `submitting`, and the mock had nothing. After restart the row said "Checking whether TorBox received it", sent nothing for the 60 s grace, then sent once. One item. With the restart rule changed to send again at once, the test failed. |
| After the provider took a submit, before its answer was saved | `KilledAfterTheProviderTookASubmit...`, new; the mock's new `AcceptThenDelay` | The record said `submitting`, and the item was on the mock. After restart the job took that item's id at once. One create. |
| Between status checks | `HostPipeRemoteTests.ScenarioJ_...`, existing | Same item, one submission. |
| During a status check | `KilledDuringAStatusCheck...`, new | Same item id, one create. |
| While links are requested | `KilledWhileLinksAreRequested...`, new | Both link requests were hanging, and no link had been given. After restart: the same two child jobs, both files equal. |
| During a local provider transfer | `ScenarioJ_...`, existing; also seen live earlier | File equal, one submission. |
| A package half done | `KilledWithAPackageHalfDone...`, new | One file of three finished, another past 64 KB. After restart: the same three child jobs. The finished file got no new link and kept its write time. The others were equal. |
| During extraction | `RuntimeArchiveHostTests.KillingTheHostMidExtraction...`, existing, a local archive; `KilledDuringExtractionAndDuringArchiveCleanup...`, new, a provider package of 13 volumes and a text file beside them, with auto-extract and archive deletion on | UnRAR was running and went with the Host. The set's record on disk was not completed. The next Host ran it again (2 attempts) and wrote one copy of all 301 files, with the big file's hash equal and no staging left. |
| During archive cleanup | the same new test | Killed when the first volume deletion was seen: 9 or 10 of 13 volumes were left, over five runs. The next Host finished: no volumes left, the set completed with archives deleted, the text file kept, all 14 downloads still completed, 13 marked as removed after extraction, and no new link asked for. |

No defect showed in the matrix.

**Ordinary HTTP, through the CLI and a sandbox Host against `tools/TestServer`:**
- Segmented, 64 MB: 8 connections on the wire for the whole run. SHA-256 equal.
- Pause and resume: the bytes stood still for 3 s while paused. SHA-256 equal.
- Kill and resume: the Host was killed at 16.2 MB of 48 MB. The new Host went on from 10.1 MB, the last checkpoint. SHA-256 equal. A kill 1.7 s in started again from zero, because checkpoints come every 5 s.
- Three redirects: SHA-256 equal.
- Resume refused, from a server without ranges: after pause and resume it started from zero, as it must. SHA-256 equal.
- Connections, from the server's own count of requests in flight: a provider file had at most 4, also after pause and resume. An ordinary download had 8. The TCP table showed one or two more on that port. Those were idle keep-alive connections of the API client and my script, which share the port with the mock. The provider file's SHA-256 equalled the mock's bytes.

**Queues and scheduler, in a sandbox Host:**
- A stopped queue with a limit of 1 held three downloads as queued. Start ran them one at a time. Stop paused the running one, its bytes stood still, and the next stayed queued.
- A one-time schedule window fired. The queue started about 7 s into the start minute and stopped about 6 s into the stop minute. A download running at the stop was paused. When the queue started again it went on from 60 MB, and its SHA-256 was equal.
- Resuming a download in a stopped queue puts it back as queued, to wait for the queue. That is the existing rule.
- For the owner: `dm schedule list` shows days next to a one-time schedule, which doesn't use them.

**Updater:**
- `dm update check` in the sandbox says `notInstalled`, by design.
- Check, download and signature checks are covered only by tests: 26 integration tests with Update in the name, and 2 manifest tests. All passed.
- `scripts\apply-update.ps1` ran on a pretend install in a scratch folder, with `-NoStart` and no native host script in the package. A failure after the swap put the old app folder and `install.json` back and wrote a failed result. A clean run swapped in the new folder, kept the old one as the rollback copy, and wrote succeeded.
- Not run: the rollback when a new version fails to start. Nothing installed was touched.

**Chrome handoff:**
- The native host finds its pipe from `DOWNLOADMANAGER_INSTANCE` and `DOWNLOADMANAGER_DATA`, the same way the Host does. I ran this build's native host with the sandbox variables and the extension's origin as its argument. I sent it framed messages on stdin, built as `extensions/chrome/src/background.ts` builds them.
- `host.ping` answered.
- A `handoff.request` with the TestServer's test cookie, a referrer and a Chrome user agent was acknowledged. The job file was on disk at the acknowledgement, without the cookie in clear. The same request id again came back as a duplicate of the same job. The server saw the cookie, the referrer and the user agent, and the file's SHA-256 was equal.
- Refused: a `blob:` link (`invalid_url`), an unknown extension id (`untrusted_extension`), and `remote.add` and `job.cancel` sent from the extension side.
- A handoff with no cookie was accepted, then failed on the server's 403, as designed.
- `extensions/chrome` has no change on this branch since it left `main`. `origin/main` has one later commit there, 6a4f942, which this branch doesn't have.
- **Not run:** the real browser path. Chrome would use the installed app's registration and reach the installed Host.

**Changed:** 78b74e2 adds the matrix tests, the mock's `AcceptThenDelay`, and `SandboxHost.ProcessId`. 582b3bc adds `--cdn-bps` and `/admin/concurrency` to `tools/TestServer`. No product code changed. All suites passed on 582b3bc: Archives 293, Containers 183, Core 745, Engine 134, Integration 989, TorBox 314.

**Bearing on the add flow, nothing built:** when a create's answer is lost, the row reads "Checking whether TorBox received it" for the whole 60 s grace before it sends again. Seen with the mock only.

**A slip:** one mutation check used a relative path in .NET file calls. So it wrote to the main checkout's `RemoteJobRules.cs` instead of the worktree's, then put it back. `git status` there shows no change.


### Owner's second try in the sandbox (`0870d34`, 2026-11-07)

Recorded as reported. Nothing is traced or fixed yet. See ROADMAP TB.F9 and TB.F11 to TB.F14.
- **TB.F11:** three direct TorBox links added through Add Links opened three separate windows. One file kept the link's id as its name, an empty text file failed, and a Download complete window opened for a single file of the package.
- **TB.F12, security:** the Download complete window's Address box shows a direct link with its token, which is the account key. The progress window masks it. The owner also pasted three such links into the conversation, so the key has been seen there too. Rotating it after testing is already an open item.
- **TB.F9, now a confirmed bug:** one torrent file could not be added and its box stayed stuck. Another was added, but Approve never became clickable. Choosing files is broken. **The owner wants** a torrent file's file choice shown first, when it is added, cached or not. File choice comes before downloading.
- **TB.F13:** Download in My TorBox should close the window. Answered in the conversation: it stayed open only so several items could be downloaded in a row, and nothing else depends on it.
- **TB.F14:** "Local downloads created" with nothing on the list, and the status bar stuck at "Adding to TorBox...".
- **Owner's go-ahead on the add flow:** "yes, build it. but I believe uncached magnets would only give file list after torbox completely finishes it, so unless we add bitorrent functionality just for file list, waiting for files is not needed as it can take too long." So: a torrent file shows its file choice when added, from its own file list, cached or not. A cached magnet does the same if TorBox's cache check gives its files. An uncached magnet downloads all files when TorBox is done, with no wait for a choice. The notice-line and Chrome hoster-link questions are still open, and neither is built.
- **Owner's refinement, same day:** "have a checkbox for uncached magnet so when torbox finishes, it won't start download and wait for file selection, otherwise it'll download in full." So adding an uncached magnet offers a checkbox, off by default, to choose files when TorBox is done. Off means all files download when ready. On means the row waits as Choose files.

### Live scenario I and the WinUI walk on the real account (2026-11-07)

Run in a sandbox Host on the real API, with the key set through `dm remote key set`. On screen means the real WinUI window, driven by UI Automation and the real mouse, with real pixels read after checking the window was in front. Both themes. I added only freely licensed test files and deleted them all afterwards. The account is back to its earlier counts, with an empty queue.

- **I, RSDF and CCF from real inputs: passed live, through the real Import JD Container dialog.** I built an RSDF and CCF 5.0, 3.0 and 0.7-1.0 files with the app's own format code in a scratch tool outside the repo. So this shows the reader agrees with the writer. No file from other software was tried.
  - The dialog listed each link with its kind and reasons. A direct link or a MEGA link shows its host only.
  - An unticked link was not sent. An uncached link put nothing on the account until I approved it. A cached magnet went on without asking.
  - Every file that arrived had the size and md5 its source publishes, or the size TorBox reports where it gives no md5.
  - Package boxes, a package comment, a stated archive password used with no prompt, and a wrong stated password asked for on the row all worked.
  - A real .dlc is not read locally, by the documented decision. The dialog says so and offers JDownloader. I didn't press that, and JDownloader was not started.
- **My TorBox with the real list: passed live.** 146 items: Load more, scrolling, every column sort both ways, resize by the real mouse kept after reopening, a 292-file tree, box selection by the real mouse with Ctrl, wheel scrolling.
- **Rows while an item processes: passed live.** Two uncached items, one per theme. "Processing on TorBox" with a note that files come later, then Downloading locally, then Completed. The main list matched. Pressing Download all while it processed made no second download.
- **Progress details for a real provider download: passed live.** Three cached items at a speed limit. The package window, the segment table and Pause all and Resume all worked. No address is in the window. The files had 0 bad torrent pieces. The window for one file of a package was not opened during a transfer.
- **Extraction row commands on real data: passed live, except the four that open Explorer or another app.** Those I left alone, since they open windows outside the sandbox.
- **Fixed, all seen only with real data:**
  - The Time left column said "1m 59s" for 59 seconds, and anything under a minute had "1m" in front. `Time_left_says_what_is_left`.
  - A column sort in My TorBox ordered only the 50 items read so far. It now reads the rest first, and shows the first rows of the order. `A_sorted_list_reads_the_rest_of_the_account_and_stops_when_there_is_no_more`.
  - The Files header read "F..." with the sort arrow on show.
  - WinUI floated a shortcut's name, "Esc" or "Ctrl+N", over the windows when the pointer rested. `No_window_lets_WinUI_float_a_shortcuts_name_over_it`.
  - After a start, rows said "From torbox" until redrawn. They now take the account's name when it arrives.
  - Adding again what is on the list said "the destination given here weren't used. Choose its files with remote select" in the window, said "already on the list" twice, and counted it as "Added 2". The words are plain now, said once, and it says they were already on the list.
  - A host name or a package comment ran into the next sentence in the import dialog.
  - Each new test failed with its fix undone. Suites: Archives 293, Containers 183, Core 745, Engine 134, Integration 983, TorBox 314, all passed.
- **For the owner, nothing changed:** 25 of 165 items have an Expires date in the past and are still cached, and a link was still given for one. The column shows the old date with no hint. In the package window, the segment table needs a scroll when the archive password panel is on show.
- **Bearing on the add flow, nothing built:** A container magnet or folder link stops at "Choose the files", and the dialog's "Close this and choose" shows while another row still processes. An approved web link reads "Checking TorBox" for about 65 s while it waits its turn. A folder link to a one-file web item still asks for a file choice. Re-adding with another folder doesn't use the folder.

### Live scenarios F, G and H, with TB.F6 and TB.F7 on real data (2026-11-07)

Run in a sandbox Host on the real API, with the key set through `dm remote key set`. On screen means the real WinUI window read through UI Automation. I added only freely licensed test files and one link of the owner's, and deleted all of it afterwards. The account is back to its earlier counts, with an empty queue.

- **F, real link expiry: not verified live.** It wasn't seen in 4 hours earlier, and I didn't wait for it here.
- **F, stopped for a while, then resumed: passed live.** Real link, real CDN, nothing forced. Stopped for 360 s at about 103 MB of a 571 MB file. It resumed without starting again, and the Host log shows a new link request. Size equal, 1088 torrent pieces checked, 0 bad. I didn't compare whether the new link's text differed.
- **F, refused link and refresh: partly verified live.** The refusal is a real CDN 403, and the refresh a real API call and a real transfer. The refused link was forced, and this ran in the real service and engine in a test process, not in the Host, because a provider job keeps no link on disk to tamper with. New test `LiveLinkRefreshTests`, three cases:
  - Refused at the start: 2 links asked, md5 equal to TorBox's.
  - Refused after a stop at 1 089 536 of 4 116 552 bytes: resumed from there, 3 links, md5 equal, at most 4 connections.
  - Always refused: 3 links, then it fails saying the bytes were kept. A later resume finishes with md5 equal.
- **F, pasted direct link swapped in the job file with the Host down: passed live.** Real CDN 403, job failed as resumable, bytes kept. `dm refresh` with a real link resumed, size equal, 0 bad pieces. A pasted link doesn't refresh itself; that is the user's step. The address was protected again after the run.
- **F, job or checkpoint changed by hand: safe restart seen.** A changed size or Last-Modified restarts from about the start and ends with 0 bad pieces. The changed size says why in the log. The changed Last-Modified doesn't.
- **F, item deleted from the account during its download: the transfer finished.** It was my own test item. The CDN kept delivering for the 64 s it took, including on new connections. Size equal, 0 bad pieces.
- **F, connections: no more than 4 carried data.** Counted per tunnel by a local proxy over a whole file, asked for 4. A 5th tunnel was open at times and carried 219 bytes, so it was idle.
- **G, password RAR: passed live, by CLI and on screen.** Public test archives through TorBox, 3 sets. Each stopped at "Password required". On screen I entered a wrong password, saw "Wrong password" and the dialog saying so, then entered the right one and saw "Extraction completed" with the folder. CRC of the extracted files equals what the tool lists. The password was in clear in 0 of 72 sandbox data files.
- **H, multi-part RAR: passed live on small sets.** All volumes through TorBox, one set, no duplicated output, output md5 equal to an independent UnRAR extraction, no staging left. Two sets in one item stayed two sets.
- **H, a volume left unselected: passed live, after a fix.** A first or middle volume showed "Waiting: part1 missing" or "Waiting: part2 missing" on screen. A last one showed "Missing volume" as a failure, since local names can't show where a set ends until the tool runs. It now reads "Waiting: part3 missing", seen on screen. Selecting the part afterwards extracted it with no other step, with output equal to the all-volumes run.
- **H, 6 GB of real volumes through the owner's mega link: partly verified live.** With one volume held back the set waited for it, and once it came there was one set and all sizes equal TorBox's. That archive needs a password I don't have and didn't guess, so its extraction and content are not verified live.
- **TB.F7, one notice per package: seen in the Host log on real data.** Each package gave one notice when it closed, and each later user action one more. The log holds kinds only, so I didn't read the words, and I didn't see the balloons.
- **TB.F6, folder naming: seen on real data.** A lone web archive `x.rar/x.rar` landed in `x\x.rar`, a lone web non-archive `x/x` straight in the destination, and a single-file torrent `s/s.rar` in `s\s.rar`. Exact matches only, so a torrent of `a.rar/a.rar.exp` keeps the folder `a.rar\`. A lone torrent non-archive keeps its stem folder and a lone web one doesn't; I left that for the owner to judge.
- **Web folder links work:** seen here, which closes a gap left from scenario A.
- **Fixed:**
  - A last volume left unselected now reads as waiting, on the row and on a package row. It is still a stopped set underneath, so Retry stays. `A_last_volume_named_by_the_tool_reads_like_any_other_part_being_waited_for`, `A_package_with_a_last_volume_missing_is_waiting_and_has_no_problem`.
  - The Host said "Missing volume: Missing volume: x". `AMissingVolumeIsSaidOnceInTheSetsWords`.
  - A package row's spoken text had ".." between its sets. `A_package_is_spoken_without_a_doubled_full_stop`.
  - The Host log now says when a provider gave a link for a run and when a refused link led to asking for another, with no address in either line.
  - The new tests above failed with the wording changes undone.
- **Bearing on the add flow, nothing built:** GitHub repository file links are refused by TorBox with its own words, and no item is created. Adding an input whose row is already on the list answers "already on the list" and ignores the new destination and choice. Files can be added to a row after its download started, and the row goes back to waiting for a moment.
- **Not seen:** real link expiry, a refresh inside the real Host process, extraction of the owner's password archive, real balloons and sounds.

### Live scenarios E and J, and killing the Host during a provider file (2026-11-07)

Run in a sandbox Host on the real API, with the key set through `dm remote key set`. On screen means the real WinUI window read through UI Automation. Only freely licensed test files were added, and all of them are deleted again: the account is back at 144 and 13.

- **E, cached magnet and cached .torrent: passed live, by CLI and on screen.** Multi-file items, not on the account before. By CLI the row reached file choice in about 1.7 s, and on screen in 2.9 s. The file count and bytes equal the torrent. Chosen files completed, sizes equal, and torrent pieces or md5 equal where the file is big enough to check.
- **E, uncached magnet and uncached .torrent: passed live, by CLI and on screen.** The row waits for approval and nothing is on the account until then. After approval: about 60 to 120 s to file choice for files of 2.6 to 3.9 GB. Chosen files completed with sizes equal. These small files fit inside one torrent piece, so their content was compared only through the full-file checks below. One item on the account each time.
- **Some uncached items never finish on TorBox's side.** Three public domain items from one site stayed `checking` or stalled at about 84% for over 37 minutes. Their rows stayed waiting, and they could be removed. That is TorBox's or the source's state, not a fault found in the app.
- **J, restart while TorBox works: passed live.** The Host and UI were killed hard, by process, in three places:
  - **During the create request.** The row was `submitting`. TorBox did receive it. After restart the row said it was checking, found the item within seconds, and there was one item and no second create.
  - **While TorBox processed an item.** Same item id after restart, one item on the account, monitoring went on and the local download started.
  - **While the item was in TorBox's queue.** It came back queued, with one queue entry and nothing on the list.
- **Not seen in J:** a kill at the exact moment TorBox reported `downloading` with progress above 0 on an item that then finished. The kill landed at `checkingResumeData`. Earlier items were killed during `metaDL`, `checking` and a stall, and kept their ids.
- **Killing the Host during a local provider file: passed live.** Four files, killed once or twice each:
  - 571 MB at 8 MB/s: killed at 194.8 MB, resumed from 194.1 MB. All 1088 torrent pieces correct.
  - 2.94 GB without a limit: killed at 2.78 GB, resumed from 2.73 GB. All 11217 pieces correct, and SHA-256 equal to the distributor's checksum file.
  - 4.1 MB at 400 KB/s: killed at 2.5 MB, resumed from 2.0 MB. md5 equal to TorBox's.
  - 276 MB, killed twice in the first 5 s: restarted from about zero, as nothing had been checkpointed yet. All pieces correct.
- **TorBox gave no md5 for a large file I could test.** On the account, files with an md5 are either 4 MB or smaller, or 20 GB or larger. So large files were checked against torrent pieces and the distributor's checksum.
- **Five defects fixed, each with a test that failed with the fix undone:**
  - c2a3859: a row read "about 2400h 0m left". TorBox answers `eta` 8640000 when it has no estimate, and `size` 0 or -1. Seen gone on a new build, with the row on a real stalled item.
  - e2b7996: a row read "about 23h 59m left" while TorBox seeded, with about a minute to wait. Seen gone on a new build.
  - aa946fa: a Host started by the CLI kept the CLI's output open, so `dm ... | x` hung while that Host lived. Tested through a real CLI and Host; `dm` in the sandbox scripts now reads files, so not separately seen with the live scripts.
  - c8e05fe: a speed limit gave far less than its figure on small reads. 8 MB/s gave about 2 MB/s on a real provider download, and about 1 MB/s in the test. Seen at 7.77 MB/s on a new build. This holds for any HTTPS download, not only providers.
  - 9b02022: a magnet that was removed from the queue failed with words about folder links. Tested with the mock; the wrong words were seen live, the new words were not.
- **The mock is corrected** to what the real service does: no files until the item finishes, the queued create's answer, "Download already queued", and the exact queue filter word.
- **For the add flow, nothing built (TB.F9, TB.F10):**
  - TB.F9 is not only .torrent. A cached magnet behaves the same. The Host reaches file choice by itself in under 3 s. The "Added for TorBox" box stays open saying "Ready. Close this and choose the files from the download list: select the item and press Enter." The chooser does not open by itself. After Close, Enter on the row opens it, with everything ticked.
  - An uncached item behaves the same once ready. The same box was still open at 98 and 118 s and changed to the same words.
  - `files[]` stayed empty through `metaDL`, `checking`, `checkingResumeData`, `downloading`, `processing`, `uploading (no peers)` and `stalled (no seeds)`. It appears at `completed` or `cached`. So files can't be listed early for an uncached item.
  - A .torrent shows its real size at once. A magnet shows 0, then -1, then the real size at `downloading`.
  - Creating an uncached .torrent: 200, about 4.3 s, "Torrent Added Successfully", `data` holds `hash`, `torrent_id`, `auth_id`. Asking again: 200, about 0.6 s, "Found Cached Torrent. Using Cached Torrent.", same id.
  - With 3 active items on a plan with no extra slots, a 4th create: 200, "Torrent queued successfully.", `data` holds `hash`, `name`, `queued_id`. Asking again: 400 `DIFF_ISSUE`, "Download already queued.", `data` null. The Host handles both.
  - **TorBox did not start a queued item by itself when slots were free.** One entry waited 16 minutes and another 14 minutes with free slots. Longer was not tried. `queued/controlqueued` with `start` started one at once, and the Host picked it up within seconds. With `delete` it left the queue, and the row failed after its grace, with no second create. The app has no command for either.
  - In the UI a queued row reads only "Waiting for TorBox", the same words as waiting for metadata. The CLI status says "Added to TorBox queue". Removing it with delete-from-provider is refused, in plain words that say to do it on TorBox's site.
- **Not changed, for the owner or a later stage:**
  - `DownloadManager.NativeHost` and the WinUI app start the Host the same way the CLI did. The native host may hold its pipe the same way. Not proven.
  - Two tests failed once each, then passed again: `Rar4HeaderPasswordRightWrongAndMissing` (1 in about 14 runs, wrong password read as corrupted) and `TheClientWaitsOutAnUsedUpBudgetInsteadOfHittingTheLimit` (2 in about 8 runs, a timing race with the mock is likely). Neither looks tied to these changes, and neither cause is proven.
- **One slip on the owner's desktop.** My first CLI fix started the Host through the shell. A test then put a real "Open File - Security Warning" box on the desktop for about 7 minutes, because the build carries the repo's Low label. I stopped that CLI by process id and dropped that approach. Nothing was opened.
- All suites on the last commits: Archives 293, Containers 183, Core 722, Engine 134, Integration 982, TorBox 314.

### Live scenarios A to D against the real TorBox account (2026-11-07)

Run in a sandbox Host on the real API, with the key set through `dm remote key set`. All Host traffic went through a local proxy that logged each destination, and a poll of the Host's TCP connections ran beside it. On screen means the real WinUI window read through UI Automation. Screenshots of it came out black, so no pixels were seen.

- **A, folder link: passed live.** A link built from a real id, with a wrong `name`, listed all 241 files of a cached item in 0.3 s under the item's real name. 9 files chosen by id, in 7 folders: all completed, at TorBox's paths, sizes equal, md5 equal on the 8 that have one. Two with the same name in different folders stayed apart. On screen with another item: pasted in Add Links, the chooser showed the tree, 3 of 19 ticked, those 3 on disk at the right depth with the right sizes. Web, usenet and removed items by folder link were not run; only a link to an id not on the account was.
- **B, cached hoster link: passed live.** A cached public domain file not on the account: from add to ready in 1.3 s, md5 equal to the source site's published md5. One of the owner's mega links, read as cached first: one 1 GB file chosen of 6, completed in 19 s, size equal, 16 sampled ranges of 256 KiB equal to a fresh link. TorBox reports no md5 for web items, so the whole mega file wasn't hashed against anything. The proxy log shows no connection to either site.
- **C, uncached hoster link: passed live.** An archive.org file that read uncached, three times, twice by CLI and once on screen: waits for approval, nothing on the account until then, then adding, processing, polled, local start, completed, md5 equal to the source. 37 to 124 s. On-screen words seen: Checking TorBox, Not cached, Adding to TorBox, Processing on TorBox with the next check's time. `Added to queue` was never seen, because the queue stayed empty. Check Now answered, but no change could be told apart from it.
- **D, failures: passed live for what could be provoked, once the fix below was in.** Each stopped with TorBox's words on the row and no local job: a hoster TorBox reports down (no POST), a dead link on three hosters, a Filecrypt link TorBox couldn't read, a folder link not on the account. An ordinary site is refused by `dm remote add` and `dm add` refuses a hoster link. With TorBox unreachable the row says so, retries by itself and went on to finish correctly with one item on the account. A wrong key shows the refusal, and nothing was created. **The network view:** 81 connections through the proxy: 19 to TorBox's API, 61 to its CDN, and one to `example.com`, which a deliberate `dm add` caused and which shows a direct request would have appeared. About 30,000 polls saw the Host connected only to the proxy. Polling could miss a very short connection. **Not verified live:** an exhausted limit, a real TorBox outage answer, a hoster's daily limit; those are proved with the mock only. The turned-off hoster's 503 was captured by a raw POST, and through the Host only with the mock, because the Host's list check catches it first.
- **Defect: a dead link looked like an outage.** The real answer to a dead or malformed web link is 500 `DOWNLOAD_SERVER_ERROR` with the reason in words, and nothing is created. The Host read that as a lost answer, so the job posted the link every minute under "Checking whether TorBox received it", and would have ended on "didn't list this after 4 tries" with the reason gone. Seen before the fix on the real service for one link, through three POSTs. It now fails once with TorBox's words, seen live, and a retry looks at the list first and posts once more. The same code on a torrent, without words, or on any other call stays a service error: it hasn't been seen on a torrent. `TEMPORARILY_DISABLED` was unknown and is now a refusal. A real outage is still retried, and the last message now holds what the provider said.
- **Defect: a site's own address started a stranger's file.** `https://` plus a hoster's domain alone read as cached, because the cache goes by the link's exact text, and it brought a 9 GB file somebody else once got for that text, with nothing asked. It was deleted from the account and the disk. A link with no path, query or fragment on a hoster is now refused with a reason. An old-style link in the fragment still counts.
- **Mock:** it now answers with the captured refusals, by `RefuseLink`. `LINK_OFFLINE` stays in old tests and has never been seen from the real service.
- **For the add flow, nothing built (TB.F9, TB.F10):**
  - A cached web link not on the account was ready in 1.1 to 1.3 s when nothing else had just been created. The 65 s spacing also holds cached ones: a link added soon after another showed "Cached on TorBox" for 40 s. So the proposed 30 s auto-open would miss a cached web link added soon after another.
  - On an uncached web item `files[]` stayed empty through `downloading` and `processing`, and appeared at `completed`. So its file list can't be read early.
  - Creating uncached: 200, about 4.4 s, `data` holds `webdownload_id`, `hash`, `auth_id`, `jdownloader_id`, `link_list`. Asking again answers the same id, "Found cached web download", with no second item.
  - Stage words seen on web items: `downloading`, then `processing` with finished true and present false, then `completed` or `cached`.
  - The "Added for TorBox" box stays open after Approve, and its line changes to "Cached on TorBox, Completed".
  - A torrent's name can hold `&amp;` in TorBox's own data, and the chooser's title shows it as it is. Left as it is.
- Evidence: `ADeadWebLinkIsARefusalWithTheServicesOwnWords`, `ScenarioD_ARealRefusalStopsAtOnce...`, `ScenarioD_ARetryAfterARefusal...`, `ScenarioJ_RepeatedProviderErrorsEnd...`, `ASitesOwnAddressIsNotALinkToAFile`; each of the four fixes undone once and its tests failed. All suites pass: Archives 293, Containers 183, Core 722, Engine 132, Integration 981, TorBox 307.
- What was added to the account is deleted: it is back at 144 and 13 with nothing created today. No owner item was deleted or changed. One slip: the `dm add` of `example.com` used the Host's default folder, the real Downloads folder. It got a 404, wrote nothing there, and the job was removed. The sandbox's default folder now points inside it.

### The real TorBox API read live: live test project, four defects, mock corrected (2026-11-07)

- New opt-in project `tests/DownloadManager.TorBox.LiveTests`. It builds with the solution. `dotnet test DownloadManager.slnx --list-tests` doesn't list it, and `scripts\test.ps1` doesn't name it. With `-p:RunLiveTests=true` and no key, all of it skips and says why. Commands are in `CLAUDE.md`.
- Run against the real account: trait `TorBoxLive`, 40 passed. Trait `TorBoxLiveSubmit` skips without `TORBOX_LIVE_SUBMIT=1`; run once with it, 2 passed. Those two asked only-if-cached for invented content, and nothing was created. The account's item count was the same before and after the sandbox runs.
- **Four real defects, each fix undone once and its test failed:**
  - An HTML file sent whole by TorBox's CDN arrives rewritten: 617 bytes against 256 reported, no Content-Length. The same file as `Range: bytes=0-` is exact. At bc2fcdd the sandbox Host failed that job with a size mismatch. `HttpDownloadEngine` now asks a provider's file for a range from zero. Seen on the fixed build: completed, 256 bytes on disk, md5 equal to TorBox's. One HTML file was available to try. Text, JSON, XML and batch files were not rewritten. A range from zero with the wrong total and nothing saved still asks for a fresh link, as a whole file of the wrong size did; `WrongWholeFileAgainstExpectedAsksForAFreshLink` caught that when the range went in.
  - The state `expired` wasn't in the table, so a job waited forever. Seen at bc2fcdd: waiting, with a next check set. It now counts as failed. Seen on the fixed build: failed in under a second, with TorBox's word and no next check. A remark in brackets, as in `uploading (no peers)`, is now cut before the table.
  - A cache check by an alternative hash answers under the main hash, so the caller read a miss. A one-hash ask now records it under the hash asked.
  - `data:null` read as an empty list. The real service answers `data:[]` for every empty list seen, so null is now a failed look, and can't make a lost submission look absent.
- All suites pass: Archives 293, Containers 183, Core 722, Engine 132, Integration 968, TorBox 304.
- **Finding 39 settled: the dashes stay.** The API takes the key dashed, dashless or in capitals. The link it returns always holds the dashed form, and the CDN takes only that exact form. `RemoteKeyPattern` already masks every form.
- **Answered from real calls:** an empty list and an empty queue are `data:[]`, on a page past the end too. An id not on the account is a 404 with `success:true` and `ITEM_NOT_FOUND`. A Ready item with `error` set: none of 157. Every real hoster pattern compiles under .NET and matches its own domains through the domain gate, and not lookalikes. A web item's hash is the md5 of its link. File ids run from 0 in each item, so they repeat across items.
- **The mock taught wrong shapes, now corrected:** its CDN address and token check, an HTML file sent whole, `AUTH_ERROR` and the real words, per-family not-found bodies, 422 detail lists, plain 404s, 500 `DATABASE_ERROR` for links to missing things, only `torrents/getqueued` answering 410, the real `user/me` and item fields, real hoster patterns, file ids from 0. `TheMockTakesAKeyWithoutDashes_AnAssumptionAboutTheRealOne` is replaced by a test of what the real API does.
- **For the add flow, not built (TB.F9, TB.F10):** items already on the account reached the file list in 0.41 to 1.43 s by magnet, and 0.38 to 0.77 s by folder link. Only 3-file items were timed. Only-if-cached refusals are clean: `DOWNLOAD_NOT_CACHED`, with nothing created.
- **Not seen live, all need a submit:** a cached item not yet on the account, when `files[]` first appears on an uncached item, the answer to creating an uncached item, a queued item's shape, the pending stage words, and what a create answers for an expired item's hash. A hash match on an expired item now adopts it and fails rather than creating again; whether it should create again is untested.
- Eight ranged connections at once on one link all kept delivering, so the CDN wasn't seen enforcing a cap. The cap of 4 stays as our policy, from TorBox's support article.
- **A hash from the credentials file was in two test files.** A scan found the hash of the test magnet in `MockTorBoxApiTests.cs` and `TorBoxClientTests.cs`, in 4819683 and 20da401 of 2026-11-06. How it got there wasn't looked into. Both files now use an invented hash. It stays in the history of `feature/torbox-integration` and `torbox-winui`. Neither commit is on `main` or a remote branch the repo knows of. **Question for the owner:** rewrite those two branches before any push, or leave it? Nothing was rewritten.

### Review of TB.F3, TB.F5, TB.F7: stopped files, list position, a record's menu (2026-11-07)

- A package with a cancelled or stopped file and none failed told nothing at all, because only a package whose files all finished closed. It now closes once nothing is working, in one notice such as "name: 2 of 3 files". Resuming a stopped file and finishing it says "name: 3 files" afterwards. Cancelling the whole package, removing files, and shutting the Host down say nothing. If the last file finishes just before its archive's extraction starts, the extraction's notice comes separately.
- The list swapped its source on any rebuild with over 64 rows out of position, so one row appearing above a long list sent it to the top. Only a sort swaps now, and a sort goes to the selection or the top itself.
- The Extractions view lists records, so a record whose download was still on the list had only extraction commands. With one download behind it, its row now has Open, Open With, Open Folder, Show Download, Remove Download, Progress Details and Properties. A split archive or several records have Show Downloads, which goes to All Downloads, opens the package and selects the files, where every command is. A record with no download keeps its three lines: there is nothing else for it to act on. The view still lists records, because several sets of one package each need their own row.
- Evidence: `DownloadNoticesTests`, `ListSortTests`, `SelectionCommandsTests`, each fix undone once and failed. Seen in a sandbox against the mock: the menus, Properties, Progress Details, Show Download and Show Downloads with the focus on the row, Remove Download keeping the files and taking the record, and the list holding its place at 120 rows while a sort went to the top (menus in both themes, the list in dark). Not checked: balloons and sounds, the Host's shutdown path (no test), Open, Open With and Open Folder pressed from a record (they would open windows outside the sandbox), live TorBox.

### TB.F1 to TB.F8 looked at on real screens (2026-11-07)

- Run in dark and light in a sandbox instance against the mock provider, with invented data only, with real mouse drags and clicks over sandbox windows at 100% scaling. **Seen working:** column resize, header sort and both surviving a UI restart, in the download list and My provider; the selection box with Ctrl, plain click, double-click and autoscroll; a provider file's progress window beside an ordinary one; the package window; deleting a package with a missing-volume set (no rows or records left, also after a restart, volumes stay on disk); a finished extraction row's menu; `Plain Album\` as the extracted folder's name on disk; a removed archive's note and dimmed Open; Import JD Container including the DLC refusal; the Import Download List and Redownload routing prompts; the Refresh Address refusal; a copied link held as waiting; live region status words through UIA; keyboard paths.
- One notice per package, read from the real Host through a new log line that holds the notice's kind and no names: a package of four and a package with an archive set each gave one `Completed`. A failing package gave two `Failed`, the first failure and the closing count, as designed above. The balloons themselves were not seen.
- Fixed from looking: Resume, Stop, Remove from Queue and the File menu's Download Now and Stop Download were on with nothing to act on. On a package row Open, Open With, Open Folder, Redownload, Refresh Address and Properties did nothing, and now dim with the File menu's reason. Group into Package no longer shows for a package row alone. A header sort kept the old top row in place, hiding the new first rows; it now shows the selection, or the top. The package window's two panes were equal, which cut off the file's details; they are now 1:2. The DLC refusal's first focus was on Open in JDownloader, so Enter would have sent the file; it is now on Cancel, and Tab reaches the button.
- Evidence: `SelectionCommandsTests` for the group rule, undone once and failed; everything else is wiring seen on screen. Not checked: real 150% scaling (only the logical sizes 1280x720 and 900x560), balloons and sounds, Open in JDownloader (never pressed), and live TorBox.
- Seen once and not again: the first drag of a run left a column's width unchanged, in one dark run with rows being added. Eight later first drags worked, including with active rows. No cause found.
- A setting changed through the CLI doesn't reach a running UI's copy until it restarts, as My provider's default folder showed. Left as it is.

### TB.F1, TB.F3, TB.F4: progress details, list columns, selection box (2026-11-07)

- TB.F1: the package window and a file's own window were written separately, so a package showed no figures per file and no segments. Both now use one `JobDetailsPanel`, and the package window shows it for the selected file, under the file list. My provider's Show progress opened the package window even for one file; that now opens the file's own window. A provider file still shows no address, on purpose: its address is a reference to the account's item, not something to read or send. Speed limiter and completion options stay in the file's own window, reached by double-click or Enter.
- TB.F3: the download list and My provider's list have a header button per column that sorts, with a chevron, and a grip on each right edge that resizes. Widths and order are saved in `ui-state.json`. All View menu sorts share one path. Remote and archive rows stay on top. A reorder touching more than 64 rows swaps the list's source once instead of moving rows one by one. My provider sorts what is loaded so far, and Load more merges in. File trees keep their path order and have no header: they have only a name and a size, under an indent.
- TB.F4: dragging a box selects rows in the download list and ticks them in file trees, with Ctrl to add. My provider's item list stays single-select, as it drives one detail pane. The arithmetic is in `RegionSelection`, the tree's rule in `RemoteFileTree.ChooseRows`.
- Evidence: `ListColumnsAndRegionTests`; each of eight fixes was undone once and its test failed. Seen on screen in both themes: see the entry above.

### TB.F7, TB.F2 wording: one notice per package, no quota claims (2026-11-07)

- `DownloadNotices` (Runtime) now decides every completion, failure and extraction notice, and the Host only maps them to tray and sound under the existing settings. A package of two or more gets one closing notice once nothing in it is working, covering its files and archive sets. A download on its own, and a package of one file, keep their words.
- A failing package says at most two things: the first failure at once, and the closing count. Retrying starts this again. Extraction failures always show; a package's extraction success follows the notification setting.
- The extraction line above the list holds a package's completions until its last set is done, and a CLI package opens one window when the main window is up.
- Every quota, allowance and "count against" claim about a provider is gone from the add, approve and container screens and from the Host's status text. Limits the provider reports stay and read as its figures. The screens' shape is unchanged, pending TB.F9 and TB.F10.
- Evidence: `DownloadNoticesTests`, `ProviderWordingTests`, `ArchiveRowsTests`, and the real Host's notice log in the entry above. No balloon or sound was seen on screen, and the Host's mapping and the CLI package window have no test.

### TB.F5, TB.F6, TB.F8: extraction records and folders (2026-11-07)

- A set now goes with the last download behind it, finished or not. Its files are never touched. A set picked from disk never had a download and stays, because its row is the only place its outcome shows. A running set finishes first, and a set with archives still to delete finishes that first. Each Host start clears older sets with no downloads.
- A lone provider file in a folder of its own name no longer keeps that folder name. An archive goes to x/x.rar with extraction beside it; any other file goes straight to the destination. Only an exact match counts, so a real folder keeps its whole name, dots included.
- Menu: a failed or finished set shows one line to run it again, and a record's own row gets Open Archive Folder and loses the file picker from its context menu.
- Evidence: `RuntimeArchiveTests`, `RuntimeArchiveReviewTests`, `RemoteJobRulesTests`, `ArchiveRowsTests`. Status: **unverified in the live UI**.


### Owner's first try of TorBox in a sandbox copy (2026-11-07)

Build: snapshot `f6aebae`, from `feature/torbox-integration` plus `torbox-winui` at `e5a4113`, real TorBox, the owner's own account. Tracked as ROADMAP TB.F1 to TB.F9. Causes are not traced yet, so the wording below is what the owner reported.

**Worked, owner-reported, not acceptance evidence:** TorBox's own links; browsing files; a mega link through caching, downloading and extraction, "pretty much perfectly"; extraction from a direct TorBox link. Live scenarios A to J are still not run as a set.

**Quota wording, corrected.** The app says fetching "uses your account's quota" or "may count against your limits". The owner says there isn't really a quota and asked for the source. There is none for that claim: it was an assumption stated as fact. The brief mentions quotas only in general terms (failure causes, testing care) and asks for confirmation before submitting uncached content. The API research shows a 300 request rate limit and per-hoster daily limits, nothing saying a fetch uses up an account quota.

**Live tests (owner, 2026-11-07):** allowed unattended, including adding, caching, magnets, torrents and deleting from the TorBox account: "files there don't matter if deleted". Local files, the installed app, pushing and releasing are not covered.

**Add flow (owner, 2026-11-07):** TB.F10 waits for the owner's answer on the proposal, which is local at `notes-local/add-flow-proposal-2026-11-07.md`. Everything else continues. Questions for the owner: is the proposal right overall; should a line above the list appear when two or more items wait (recommended yes); and should hoster links clicked in Chrome go straight to TorBox (recommended yes) or wait like copied links.
**Owner's answer (2026-11-07, about 12:30):** "I like the proposal but I'd like to test first." Read as: the owner tries a sandbox build with the TB.F1 to TB.F8 fixes before TB.F10 is built. A new sandbox copy is made from `0870d34`. TB.F10 and TB.F9 stay unbuilt until the owner has tested. The two smaller questions, about the notice line and Chrome hoster links, are still open.

### Open for the owner (recorded 2026-11-06, for later)

- **Chrome Web Store:** submitted by the owner on 2026-11-06 (Item ID `olhbcdbjdnfoiinllnlpandggadfabfm`). Once approved, install from the store and check it connects to the app. See ROADMAP EXT.S2.
- **Old commits on GitHub:** history is rewritten, but GitHub can keep serving old commits by direct link until its support purges them. A purge request is the owner's to send.
- **Settings has two similar checkboxes** now the media option is gone: "Allow automatic sending" and "Automatically send downloads to the desktop app". Consider merging them.
- **Store review fallback:** if review stalls over all-site access, removing the all-pages content script is an option. That script holds the Delete-key gesture and Download All's link collection, so both would need another route.
- **Cookies:** clear them when a download finishes, rather than when it is removed.
- **Small known issues:** a failed retry can delete a kept rollback copy; a postponed tray retry is dropped; graph labels clip at 200% text size; a package row's Size shows received bytes until all sizes are known.
- **Check whether these are stale:** ROADMAP UI.R3 and UI.R4, clean-machine install testing, independent recovery review.
- **TorBox:** work is on the `feature/torbox-integration` branch, not merged. Its plan is `docs/plans/torbox-integration.md`.
- **Extraction refusals the owner may want to know about:** an archive whose entries differ only by capital letters, one holding NTFS streams, and one with an entry named like the app's working folder are all refused with `Unsafe archive` and kept. Nothing can be extracted from such an archive through the app. WinRAR still can.
- **TorBox, decisions for the owner:**
  - DLC is not read locally; it answers "not supported" with an explicit hand-over to a running JDownloader. Reading back through JDownloader's port 3128 was not built.
  - The CCF reader's constants come from a public GPLv3 mirror, so it is not clean-room and has never met a real file. Remove it before any merge if that is uneasy.
  - Should this feature ship in a public release at all?
  - The extractor is not bundled: the app uses an UnRAR that is already on the machine.
  - Rotate the TorBox API key after testing.
  - A browser handoff of a hoster link still goes direct while a key exists: `handoff.request` calls `AddJobAsync`, which never classifies. The docs allow this as an explicit direct request, while the brief says not to fall back to direct hoster downloading. Should a handoff with a usable key be refused back to Chrome, or held as the clipboard does?
- **TorBox, behaviour to check you are happy with:**
  - Empty files in a provider item are skipped.
  - An archive set does not follow a download that was moved.
  - After a crashed extraction, rollback deletes placed files by size and time match; a file changed while the Host was down stays.
  - A wrong package password is marked refused per set rather than cleared.
  - Add Links still makes one `job.add` per link.
  - Re-adding a duplicate regroups a job that had been detached from its package.
  - The owner's magnet is already cached, so the uncached torrent case needs another input.

### TorBox: WinUI container import and archive notes (2026-11-07, branch `torbox-winui`)

- **Import JD Container** is on the Tasks menu while a key is usable. It reads the file on a connection of its own, shows packages and entries in words, and adds what is ticked. Approval of anything uncached comes afterwards, unticked, in the same words as Add Links. The path only ever goes to the Host, so a network path gets the Host's refusal and this side never asks about it. The hand-over to JDownloader is a separate button, offered only when nothing in the file can be added here, such as a DLC.
- **A removed archive** (`fileRemovedAfterExtraction`) shows its note in Description, the row's tooltip and accessible name, and Properties. Open, Open With, Open Folder and Move/Rename say why they are off, its progress window has no open buttons, and Delete doesn't ask to delete a file that isn't there.
- **An archive set's note** is in its tooltip, its accessible name and the package window's extraction status.
- Evidence: `ContainerImportTests`, additions to `RemoteClientTests`, `SelectionCommandsTests`, `ArchiveRowsTests`. Not checked on screen, in either theme, and never run against a real Host: unverified.
- Host-side finding, not changed: a mixed delete of removed and other downloads sends one `deleteFile` for all of them, and `job.remove` doesn't look at `FileRemovedAfterExtraction`, so a new file at a removed archive's old path would be deleted with it.

### TorBox: three routing gaps on the Host (2026-11-07, branch `torbox-winui`)

- **Clipboard:** with a usable key a copied link is classified off the tray thread. Ordinary and direct links are added as before. A hoster or folder link becomes a job held for approval (`heldForApproval`), with no call to the provider until the user approves, across Check Now, a key change and a restart. Anything else isn't added, and the tray says to use Add Links. With no key the path is what it was, and the log line about a copied address names only the site when there is a key. The logic is `CopiedLinks` in Runtime, so it can be tested without the Host.
- **Refresh:** `job.refreshUrl` refuses a changed hoster or folder link, and refuses an undetermined or unclassifiable one unless `explicitDirect` is set (`dm refresh --direct`, the WinUI's "Download directly"). The WinUI sends a changed address on a connection of its own.
- **Names:** `ServerNameWithoutSecret` keeps the key out of file metadata names and of names an add takes from the server, also after a redirect or when cleaning has disguised it. The WinUI's `ServerNameToShow` stays: it also hides a meaningless name in a direct link, such as `1`, which isn't a secret and which the Host keeps.
- Evidence: `RuntimeRemoteRoutingTests`; each fix undone once and its tests failed. Not run: the tests that start a real Host (`HostPipe*`, `RuntimeArchiveHostTests`), and so the Host's tray and monitor wiring and `dm refresh --direct` are unverified. Not checked on screen.

### Checkpoint: paused by the owner (2026-11-06, about 15:50)

The owner needed the PC, so work stopped here. **The goal is not finished: no live scenario A to J has been run yet.** Nothing was pushed, merged or released. Nothing from this work is left running.

| Branch | Tip | State |
| --- | --- | --- |
| `feature/torbox-integration` | this commit, on `23c4414` | Host side of everything, containers, archive review fixes. Build 0 warnings; Core 249, Engine 132, TorBox 291, Archives 293, Containers 183, Integration 876, extension 50, all passing on `23c4414`, run by the main session. Does not yet contain the WinUI work or `main`'s 0.4.0 commits. |
| `torbox-winui` (`.claude\worktrees\winui`) | WIP commit on `ba2137f` | Options, My provider window, file selection, add flows, extraction screens. Stopped during on-screen checks. Its last commit is labelled WIP and was not built. |
| `feature/edge-support` (`.claude\worktrees\edge`) | `0f60dfe` | From `origin/main` (0.4.0). Separate from TorBox. Integration failed 1 of 7 runs with the name lost; unexplained. Core 107, Engine 57, extension 50 passing. |

**To resume, in order:**
1. WinUI: build `torbox-winui`, finish the on-screen checks (dark was in progress, then light), then the UI review and fixes, which have not started. The job's script is `workflows\scripts` in the session folder, named for the WinUI build.
2. Merge `torbox-winui` into `feature/torbox-integration`, then merge `origin/main` so the tests cover 0.4.0's two trusted extension IDs. Remove the worktree and branch.
3. WinUI still has to show `statusNote` and `fileRemovedAfterExtraction` and read `jobsLeftOut`, `jobId` and a set's `note`. Add the Import JD Container dialog.
4. Final stage: all suites, interruption matrix, regression of ordinary HTTP, Chrome handoff, queues and updater; opt-in live test project; live scenarios A to J in a sandbox Host; compare with JDownloader; secret search over the whole branch; update ROADMAP, GOAL, PROGRESS and CLAUDE.

**For the final stage:**
- Run browser tests from a normal terminal, not an agent shell, and use `cft2`; see `CLAUDE.md` on `feature/edge-support`. Chrome handoff testing must leave the owner's installed 0.4.0 registration as it was.
- A link was checked for 4 hours and never expired, and `requestdl` returned a different link from about 60 minutes while the old one kept working. So real expiry in scenario F will probably be "not verified live" with that reason.
- `UpdateHelperThatExitsAtOnceLeavesTheHostRunning` failed in 2 runs under heavy load and was left alone, outside this work. `TheJDownloaderRouteOverThePipeSaysWhatReallyHappened` failed once under stress, probably two test processes sharing the fixed port. `BrowsingReadsTheProvidersList` never reproduced.

**Separate, later:** Firefox is required and comes last, after everything else (owner, 2026-11-06). Edge's first release should go through Setup. UI.R2 still needs a check that the flyout appears normally for a download left to the browser.

### TorBox: review of extraction and containers, flaky tests, deleted archives (2026-11-06, branch `feature/torbox-integration`, `23c4414`)

Twenty-two review findings checked, all against the mock and real UnRAR 7.23. No live call. No WinUI.

- **A leftover folder is no longer trusted (12, 13, 14, 5, 7).** `attempt.json` is gone. What a staging folder is for comes from the set saved in app state, and the folder's name, a hash of first volume and destination, has to agree. A `.inbound-extract-*` folder no set accounts for is left alone and logged. A log line removes a file only under the saved destination and only if its size and last-write time still match what was logged before the move. A displaced original whose place is taken goes in beside it under a free name instead of being deleted. A bad log line is skipped and can't throw. The name is reserved in `DescribeUnsafePath` (listing and walk), `RemotePaths.CleanSegment` and the subfolder name. `archives.InitializeAsync` has its own guard, so a failure there disables archive commands and not the Host.
- **A name only suggests a set (1).** New `RarVolumeHeader` reads the volume flag: a numbered name whose header says whole archive is its own set, as UnRAR treats it. RAR5 with encrypted headers has no readable flag, so names decide there. `lt -v`'s `Archive:` lines give `ExtractionResult.VolumesRead`, and only those can be deleted. A named file the tool didn't read is kept and the set's `Note` says so. An extractor that doesn't say what it read deletes nothing.
- **Identity (2).** A set's key is its first volume's real name, so `part1` and `part01` are two sets from the start. Only a first name worked out from a later volume can be wrong (`part10` alone suggests `part1`), and such a record moves to the real first volume when it turns up, unless its own first volume exists or it has run. Old keys are corrected at the next start. `WhatTheRunReallyUsed` no longer looks anything up by key.
- **Case and repeats (3, 15).** Two file entries equal under case folding, a file and a folder that collapse, and the same name twice other than as a continuation into the next volume are refused from the listing. The walk also compares the listed count with what was written.
- **Streams (18).** UnRAR restores streams whatever `-os-` says (tried). `lta -v` shows them, so an archive holding any is refused before `x` runs. The walk also refuses any stream, and accepts a `Zone.Identifier` only when the archive has one for `-om` to have copied.
- **Durable before deleted (8).** With `DeleteArchives` on, staged files are flushed before their rename, destination folders after, and all before `Completed` is saved. A failed flush keeps the archives and says so in `Note`.
- **Smaller.** Subfolder names are unique per set and kept on retry; a set whose destination changed recovers its earlier staging first (4). Rollback clears read-only and keeps staging on any failure (6). Drive roots work, end to end through a `subst` drive (10, 16). A volume that arrives during a failing run puts the set back in line, except one that was already there at the claim, which would loop (11). A held store file isn't quarantined and blocks re-creating its set until a later start; held settings stop archive start-up rather than being overwritten (9). `SecretInput` is the one stdin reader in the CLI, UTF-8 and unechoed (17). Clearing a password clears the package's other jobs (19). `remote.container.add` drops full jobs rather than failing when the answer is too large, with `jobId` and `jobsLeftOut` added (20). `ToFileUrl` builds from escaped segments and checks the result (21). All of a package's stated passwords are masked in its entries (22).
- **Findings rejected:** none. 10 and 16 are the same defect, and so are 3 and 15. On 5, the suggested "keep the folder when the marker is unreadable" was replaced by removing the marker, since nothing needs it now.
- **Deleted archives (job C).** New `DownloadJob.FileRemovedAfterExtraction`, saved before the delete, cleared again if the delete fails and when the job runs again. `ReconcileAsync` leaves such a job completed. `JobDto` gains `fileRemovedAfterExtraction` and `statusNote` ("Archive removed after extraction"), printed by `dm list` and `dm status`.
- **The flaky `ScenarioF_ALinkLostMidTransfer (connections: 4)`.** Reproduced under load (1 filtered run in 60, and once in a full suite running beside them). It never restarted from zero. The ranges recorded on the second link start at the saved offsets, for example `134856-2097151` and `2625218-4194303`, while `BytesServed` read 8,414,141 of 8,388,608. `BytesServed` counts what the server pushed, and when one connection steals half another's range, the server keeps sending until the client closes, so abandoned bytes add up. The test now records the checkpoint the service was handed and asserts the distinct bytes asked of the new link equal what was missing. Stealing's overshoot is real traffic and stays: a range can't be shortened once requested.
- **A real zero-restart found on the way.** `JsonCheckpointStore.LoadAsync` returned null on any `IOException`, and null makes the engine delete the part file. A scanner holding the checkpoint for a moment could cost the whole download. It now retries, then throws, and the run fails as resumable with the bytes kept. `AtomicFile` retries its final replace for the same reason, since a failed save was swallowed and could leave no checkpoint at all.
- **A real start-up race found on the way.** A `host.shutdown` that arrived before the message loop started was lost, and the Host then ran forever. `RequestShutdown` now leaves a flag that `Program` checks before `Application.Run`.
- **A lost reply at exit.** The reply to `host.shutdown` went into the writer queue, and stopping ends the writer, so under load the client saw "The Host closed the connection" (seen once in `ScenarioI`, at `StopAsync`). It is now written directly.
- **"Already running" for a job that had just failed.** A client sees `failed` before the run has finished saving it, so Resume or Refresh Address in that moment was refused (`RefreshDownloadAddressResumesAfterExpiredToken`, `NoKeyAndAnUnreadableKey...`, one loaded run). Both now wait for the run to end, for at most 5 s.
- **A second assertion in the same test was also about the server's view.** With the first fixed, `PeakCdnConnections` read 5 of 4 in 2 of 150 runs: the old link's aborted answers were still winding down on the server after the client had closed them. The mock now counts only answers whose client is still there, and scenario F no longer asserts a peak, since `TheProvidersConnectionLimit...` tests the cap.
- **Test harness races.** A killed Host's restart could exit with 0, because `WaitForExit` returns on the exit code, before the old mutex handle closes (reasoned from .NET's source, seen once before the fix, not reproduced since). A neutrality test read a save's temporary file while it was open, and `JobJsonAsync` read a job file during its replace.
- **`BrowsingReadsTheProvidersList`: not reproduced, cause not proven.** It passed every time here: 150, 200 and 100 filtered runs under load, and every full run. Its original message is lost. The likeliest cause is the lost `host.shutdown` reply below, because every Host test ends in that call and it surfaces as an exception, not an assertion. That is a reasoned match, not evidence. Treat it as open if it comes back.
- **Evidence.** Last build: 0 warnings. Core 249, Engine 132, TorBox 291, Archives 293, Containers 183, Integration 876, all passing, none skipped. Integration ran 3 of 3 clean on the last build. On the build before the last two small fixes, full runs were 4 of 4 clean, with 100 of 100 filtered runs under load (scenario F, Browsing, scenario J, connection cap) with 24 CPU burners and a second full suite beside them; before that, 200 of 200. On the last build the Host-process classes plus scenario F went 24 of 25 under that load. The one failure, `TheJDownloaderRouteOverThePipeSaysWhatReallyHappened`, comes from two test processes sharing the fixed JDownloader port, which only this stress setup does. Undoing three fixes (the identity check, the reconcile mark, the reserved name) failed 10 tests in `StagingAreaTests`, `ReviewRegressionTests` and `RuntimeArchiveReviewTests`, and undoing the Resume wait failed its test; all restored. New tests: `ReviewRegressionTests`, `RuntimeArchiveReviewTests`, additions to `StagingAreaTests`, `RuntimeRemoteContainerTests`, `RuntimeRemoteBrokerTests`, `RuntimeArchiveHostTests`, `HostPipeRemoteTests`, `JDownloaderBridgeTests`.
- **Not verified or not done:**
  - A power cut. The flush order is by reading and a seam that makes a flush fail, not by losing power. Flushing a folder handle has only been exercised on NTFS.
  - The order "mark saved, then delete" is by reading. A kill between the two is simulated by setting the mark and restarting.
  - WinUI doesn't show `statusNote` or treat `fileRemovedAfterExtraction` yet. That project is another team's, so the visible state is DTO and CLI only for now. The row still reads Completed, and opening its file will fail.
  - Jobs whose archives were deleted before this change have no mark, and a restart still reports them missing. Nothing shipped with this, so only test data can be affected.
  - RAR5 with encrypted headers has no readable volume flag. Two separate such archives with numbered names group by name: the first extracts, and the second is kept and named in `Note`, but asking for it by name answers "already extracted". Renaming it works.
  - A set's unread file with a volume header, a volume of some other set under a matching name, has the same limit.
  - `lta` is read by matching one English line of its output. A build of UnRAR with other wording would fall through to the walk, which refuses after writing.
  - `UpdateHelperThatExitsAtOnceLeavesTheHostRunning` failed in 2 side runs with 24 burners and a second suite beside it. A helper that exits at once wasn't seen to exit within the fixed grace, so under that load a failed launch would read as scheduled. Not changed: it is outside this work, and it never failed without that load. For the owner.
  - The side runs also showed `RuntimeAndContractsNameNoConcreteProvider` failing every time, only because the copied build there has no `src` folder to scan.
  - Under the Overwrite policy with destination `nextToArchive`, two sets with a file of the same name still overwrite each other, because that is what the setting says.

### TorBox: scenario I, JD container import on the Host side (2026-11-06, branch `feature/torbox-integration`, not committed)

Everything here is against the mock. No live call was made, and no scenario is verified live. No WinUI.

- **Import.** `ContainerImportService` in `Runtime/Remote` reads a local `.rsdf`, `.ccf` or `.dlc` by full path. It refuses network and device paths before touching them, and limits a file to 16 MB and 5000 links. Malformed input gets a refusal in words.
- **A hole closed on the way.** `\??\UNC\host\share\x` counts as fully qualified and got past the classifier's network check, so a `.torrent` path written that way could reach a share. `IsNetworkOrDevicePath` now refuses it, and so do the CLI's checks.
- **Preview.** Each link is sorted by the existing classifier. An ordinary link is unsupported, with a reason that names the provider and says it can be a normal download. With no key it says so, on the preview and on each entry, with no API call. Links stay in Host memory against a preview id for 15 minutes, at most 8 previews and 8 MB of links, and a timer drops them. What can't be added isn't kept at all. The DTO has entry ids, package index, display host, scrubbed display name, size, kind and reason. A name is taken from the link only if its last segment looks like a file name, because otherwise it is the file's id on the site.
- **Add.** Each chosen entry goes through `RemoteJobCoordinator.AddAsync`, so cache check, approval, 65 s spacing and dedupe apply, and each gets its own outcome. A container can't name a local file, and a direct link holding a key is refused.
- **One folder, one set.** New `RemoteJobPlacement` on the record: a container package's jobs share one local package and one folder, and an item holding one file loses the item's own folder. So parts from separate links meet and are claimed as one set. An item with several files keeps its folders.
- **Archive passwords.** New protected field on the remote job, with its own `SecretProtector` purpose, settable at `remote.add` (`archivePassword`), from a container, and by `remote.archivePassword`. `IPackagePasswordKeeper` in Core is the seam, `ArchiveCoordinator` implements it, and the Host composes it. The password stays on the job until there is a package, then moves, so one copy is kept. Container passwords are offers: they never replace what the package already has, so a later job can't undo what the user typed.
- **Fixes to the archive side these needed.** A set now learns its package from a package of one file too (`FindGroupOf`), otherwise a single RAR never saw its package password. A password that arrives while a run without one is going on is no longer lost: the failure sends the set back to waiting. A volume that finishes between a claim and the tool's start now counts in the result, so "delete archives" removes it; only volumes a download brought count, so an extracted file with a volume's name is never taken for one.
- **DLC.** `remote.container.preview` gives "not supported" and the reason. `remote.container.sendToJd` is separate and explicit, uses `JDownloaderBridge` on 127.0.0.1:9666, and reports whether JDownloader really answered. No read-back on port 3128.
- **CLI.** `dm remote container <file> [--add ID,ID|--all] [--dest] [--approve-uncached] [--queue] [--start]`, `dm remote container-send-jd <file>`, `dm remote archive-password <id>`, `dm remote add --archive-password-stdin`.
- **Bridge.** `NativeBridgePolicy` keeps all of it out: new reflection test over every `remote.*` constant, and the existing native host test now covers them too.
- **Evidence.** `HostPipeContainerTests` runs the real Host over its pipe, the real engine against the mock CDN, the real `ArchiveCoordinator` and real UnRAR on archives built by `Rar.exe`: scenario I with a mixed CCF 5.0 (kinds, reasons, only chosen entries submitted, uncached needs approval, password from the container opens a `-hp` RAR, secret search over every frame, log and data file), parts from separate links in a CCF 3.0 and in an RSDF, scenario G (asks, wrong, right, told at add, told before the download exists, manual extract), scenario H (one item, one set, one output; a gap waits by name; a missing last volume is named by the tool; both finish once chosen). `RuntimeRemoteContainerTests` covers expiry, bounds, malformed files in all three formats, path refusals, spacing across entries, placement, password hand-over and crash points, and the JDownloader outcomes with a stand-in.
- **Not verified or not done:**
  - No real `.rsdf` or `.ccf` sample was available. Fixtures come from the same format notes the readers use, so these prove what happens to a container's contents, not that a file made by other software opens.
  - The JDownloader route has never run against a real JDownloader, only against a stand-in on the real port. The pipe test does nothing if the port is taken, so as not to send anything to a real one. Sending a CCF by file URL is from public source and unproven.
  - A package with several passwords uses the first and says so. The others aren't tried, and a client can't read them. Trying them needs per-set state on the archive side.
  - With auto-extract on, parts that arrive at very different times can make a set stop for a moment with "missing volume", naming the next part, plus a tray notice, because nothing tells the archive side that more are coming. It runs again by itself when the part arrives. The same can happen by a narrow race inside one item, if a first small volume finishes before the next has a job. Output is never duplicated.
  - `status` on the jobs of one container package describes the whole shared local package, not that job's own file.
  - The classification of a link is decided again at add, so a list that changed in between can give a different answer than the preview did. That answer is the outcome.
  - No WinUI for any of this (Phase 8). Nothing live.
  - A 30 second stall, cause not established: `ScenarioI_PartsFromSeparateLinks...` took about 31 s instead of about 2 s, and still passed, in 3 of roughly 260 runs. It did not come back in 160 runs made with logging added to catch it, so the log was never seen. A 30 s wait exists in several places (a refused save pausing the account, the retry after Ready, the first backoff, the scheduler's tick), and which one this is is a guess, so nothing was changed for it.
- **Tests seen:** build 0 warnings. Core 249, Containers 176, Archives 252, TorBox 291, Integration 838, all passing. `HostPipeContainerTests` (10 tests) passed in every repeat run, more than 50 of them. No reviewer was run on this: this session had no way to delegate.

### TorBox: remote core review (2026-11-06, branch `feature/torbox-integration`, not committed)

Reviewers checked the remote core with 43 findings. Each was checked against the code first. Everything here is proven against the mock only, with no live call. Numbers are the review's.

- **Failed looks are not empty lists (1, 27).** Only `ITEM_NOT_FOUND`, a 404 whose JSON says "no ... found", `data:[]` and `data:null` count as nothing there. A non-JSON or empty 404, `ENDPOINT_NOT_FOUND`, a 410 or `DEPRECATED` are service errors, and `success:false`, a word or `{}` for `data`, or no `data` are bad responses, none of which can lead to a submit. A single object with an id is a list of one, as JD reads it. The same holds for `GetAsync`, the queue and the hoster list. **Kept on purpose:** `{success:true,data:null}` still reads as empty on a list, because JD's bytecode accepts null as nothing, and refusing it could stop every add if TorBox really sends it. This is the third variant the review reproduced, so that one duplicate path remains until a live read settles it.
- **Children follow their item (2, 34).** When a resubmit gives a new item id, existing children are repointed durably and keep their bytes. The next run compares size and md5 and restarts from zero only if they differ. A finished file that differs stays and gets a note. A child failed only because its item went away resumes by itself.
- **Timing (3, 7, 12, 18, 35).** The grace and the 65 s slot count from when the POST ended, not from before it was queued. A rate limit holds the account whatever status said it, at most 1 hour, on save and on load: a 429's Retry-After, else the words ("hour" = 1 h, otherwise 1 min). The loop's wait is kept to what a timer accepts, and `StopAsync` survives a dead loop. User Retry and a key change lift the hold; Check Now still doesn't. Budget headers: a reset over 5 minutes ahead or out of range is ignored, budget coming back clears the wait, and no gate is held across it.
- **Stuck records (4).** Ids are canonicalised on add through `IRemoteCatalog.NormalizeItemId`, an answer about another id is a failed look, and `StepAsync` moves on any record a step left unchanged and due.
- **Key changes and approvals (5, 6).** With no key, Intended and Blocked keep their state, as Waiting and SubmissionUnknown already did. `FailAsync` clears `ApprovalNeeded`. A key change clears the creation stop. An approval given while a step runs, or before the first look through a twin, now counts.
- **Durability (8, 9, 10, 11).** A claim that can't be written takes its job file back and returns `persistence_failed`. A file choice made during child creation stays Waiting and is planned again. `JsonRemoteJobStore` quarantines only JSON and format errors, and retries read errors, then leaves the file for the next start. `Raise` sends the record's current state, and order is held by a lock across the listener.
- **Bounds (13, 14, 16, 17).** Failure text is capped at the broker and in job failures, note paths at 300 characters. Over-size pushes are dropped and logged instead of killing the writer, and a too-large snapshot loses its failure text rather than refusing every client. `RemotePaths` refuses a segment over 200 or a path over 512 characters, and a path that can't be created under a good folder is skipped with a note (`unusable_path`). `UrlRedactor` looks at 64 KB at most, and its patterns time out to hidden text. API bodies stop at 64 MiB after decompression, read by `TorBoxClient` itself with the client's timeout.
- **Hoster routing (15, 21, 28, 29, 38, 40, 41).** A hoster with domains is only matched on them, and they decide alone if its pattern won't compile or times out. A null `domains` uses the name, and only an exact `status:false` is down. Patterns get 2 s per paste. No list at all gives the new `Undetermined` kind, and `remote.add` then says TorBox couldn't be reached. With no selection a hoster link now takes all, so scenarios B and C start by themselves. A site's daily limit that is used up blocks without spending a creation, and the classify reply shows the limits. `dm add` refuses a hoster link, and `--direct` only answers `Undetermined`. **Decision:** `job.add` stays an explicit direct path, because browser handoff is made of those.
- **Words and safety (19, 20, 22, 23, 24, 25, 33, 36, 37, 39, 42).** No https to http redirect for a resolved link, unless to loopback. Submitted links and passwords are hidden raw, escaped and without a scheme, before any cut, and the key mask runs first too. `magnet:` is hidden in any form. Bidi marks and C1 controls leave file names. CLI output and `Safe` strip control characters, also from item and file ids. UNC and device paths are refused before anything opens them, in the classifier and the CLI. `remote classify` takes `-`, and a secret on the command line gets a warning. LocalStarted's status now reads from the package (Completed, N failed, Paused locally, Downloading locally, Waiting to download locally). A repeat add says what wasn't used. A failed choice can be chosen again, and a bad choice on a job with children is refused or noted, never a failure. Spacing and poll bounds moved to `RemoteTransferPolicy`. `LinkLifetime`, `IssuedAt` and `DisplayName` are gone. The queued-item delete message is accurate.
- **Provider reading (26, 30, 31, 32).** A past-the-end `206 bytes */N` now counts like a 416, and the mock answers that way as the live notes say. `DOWNLOAD_TOO_LARGE` and `PLAN_RESTRICTED_FEATURE` fail one job only. A cached item ignores the creation stop, which now says it came from an earlier refusal. A hash search prefers Ready, then not failed, the latest of those, and a retry of a failed submitted item looks by hash again. Ready is checked before `error`, and a leftover error becomes a note.
- **Undone to prove the tests:** the 404 classification (8 lost-answer and gateway rows failed), the grace stamp (`TheGraceCountsFromWhen...` failed), and the 206 branch (`AResumeOfAFinishedFile...(PartialContent)` and the end-to-end `ScenarioF_AResumeAfterAFailedRename...` failed). All restored and re-run.
- **Not verified or not done:**
  - No live call. To check live: what an empty `mylist` and an empty `queued/getqueued` answer (`data:null` above); whether TorBox ever sends a Ready item with `error` set; that real hoster patterns match with the domain gate. Answered 2026-11-07, see "The real TorBox API read live".
  - Finding 39: the key is still sent as entered. JD sends it dashless, and `TheMockTakesAKeyWithoutDashes_AnAssumptionAboutTheRealOne` proves nothing about TorBox. Strip dashes once a live read confirms. Settled 2026-11-07: the dashes stay.
  - Finding 43: no opt-in live test project exists yet, and the plan requires one before Phase 9. Done 2026-11-07.
  - No regression test: event order (11, read from code, no way to force the interleaving), the over-size push and snapshot guards (13), a regex timeout itself (16), `MoveOnIfStuckAsync` (4, nothing reaches it now that the id check is there), the skip when a job file can't be taken back (8).
  - Not done: the hoster list isn't saved, so every start until TorBox answers gives `Undetermined` (28); no queue cancel (42); no cap on files per item or items per page, only the 64 MiB byte cap (17); the creation stop has no expiry, so a new billing month still needs a retry (30); `PerLinkSizeLimit` is shown, not enforced, because the size isn't known before submitting (40); a repeat add reports ignored options instead of applying them (36); only `dm add` classifies, not the CLI's batch paths, clipboard monitoring or browser handoff (41); container inputs aren't routed yet, so "never a path" isn't tested for them (24); a file unreadable at start stays off the list for that session, and re-adding it then could give two records for one hash (10).
- **Also fixed while testing:** a client could get an event before its snapshot, because the writer started first (seen once as `TheKeyGoesInAndNothingButHasKeyComesBack` failing). The writer now starts after the snapshot, and what arrives meanwhile waits in the channel. The segmented path knows the `206 bytes */N` answer too, by reading only: no test reaches it.
- **Tests seen:** build 0 warnings. Last run after all changes: Core 247, Engine 132, TorBox 291, Integration 694 (twice), all passing.
- **A flaky test, cause not established:** `ScenarioF_ALinkLostMidTransferIsReplacedAndTheTransferResumes(connections: 4)` failed in 2 of the 9 full Integration runs of this session, and never alone, including 18 runs under load. It asserts that the new link served less than the whole file, and the failing runs served 8.5 MB of 8 MB, so that transfer started over from zero. The file was still correct, since the hash assertion comes first. My guess, not proven: under full-suite load the first link dies with its bytes still in socket buffers, so nothing was saved to continue from. No change in this review obviously touches that path, but I have no run of the old code under the same load to compare. The test is unchanged.
- **One more single failure:** `BrowsingReadsTheProvidersListByKindAndPageWithNoLinkInIt` failed once, in the same run as one of those above, and its message wasn't captured. It hasn't recurred in the 6 full runs since, 2 of them after the connect-order fix. It may have been that same race, but that isn't established.

### TorBox: Host composition, `remote.*` IPC, CLI (2026-11-06, branch `feature/torbox-integration`, not committed)

Everything here is against the mock. No live call was made and no scenario is verified live.

- **Host:** `Program.cs` builds the registry, client, account store, broker, coordinator and `RemoteCommands`. Subscribers attach before `InitializeAsync`, the coordinator reconciles after it, and shutdown runs pipe, coordinator, service. If the coordinator can't start, ordinary downloads carry on and clients don't get `remote.*`.
- **Sandbox:** `DOWNLOADMANAGER_TORBOX_API` counts only with `DOWNLOADMANAGER_INSTANCE` and `DOWNLOADMANAGER_DATA` set, and only for this computer or the real host (`TorBoxApiAddress`). A sandbox profile whose data folder can't be used now exits with code 5, where it used to fall back to the real state folder.
- **IPC:** 14 commands and `remote.changed`, protocol still 1, in `HostPipeServer.Remote.cs` and `Runtime/Remote/RemoteCommands.cs`. New doc: `docs/ipc-protocol.md`. Calls that wait on the network cancel when their client goes away. A reply over the frame limit becomes a plain failure instead of ending the connection.
- **CLI:** `dm remote status|key|classify|add|add-torrent|list|files|select|retry|check|remove|browse`. The key is read from standard input only. A key on the command line is refused and not echoed. `add -` keeps a link off the command line too.
- **Evidence (real `DownloadManager.Host.exe`, own profile, real pipe, mock provider):** `HostPipeRemoteTests`:
  - Scenarios A, C and E in one run, then every file under the data folder, every log line and every frame the Host sent are searched for the key (dashed, dashless, either case, percent-encoded, base64, UTF-16), hoster link, fragment, link password, magnet, tracker passkey and direct links. None found. `TheSearchFindsASecretInEveryFormItLooksFor` shows the search can find them.
  - Scenario J with real process kills, once while the provider works and once mid-download: one submission, no duplicate child, matching hash. This closes the gap the coordinator stage left.
  - The real native host refuses every `remote.*` and `archive.*` from the browser, and a `remote.changed` broadcast doesn't reach the browser's side. Provider text quoting the key and links arrives scrubbed.
  - Temporarily breaking the scrubbing and the native allowlist made those tests fail. Both were restored.
- **Provider neutrality:** `RemoteProviderNeutralityTests` runs scenarios A and C through a fake provider beside the real client, with no Runtime change. A source scan keeps the word out of Runtime and Contracts.
  - Still specific: `Core/Providers/ProviderLinks` knows one provider's CDN and API hosts, so another provider's pasted direct link is recognised only if it holds the account key, and its cap is that constant. With two providers offering torrents, the first registered takes every magnet. Item kinds are a closed enum of three.
- **Tests seen:** build 0 warnings. Core 225, Engine 125, TorBox 244, Archives 245, Containers 176, Integration 592, all passing.
- **Not done or not verified:** live calls; WinUI (Options, My TorBox, Add Links routing), so nothing shows a classification `note` yet; container routing; `remote.deleteRemote` works only for an item with a job here, so a browser can't delete one it merely lists; list search; a power-loss test as opposed to a process kill.

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

### Store package unpacked by hand is refused (2026-11-07)

- Owner report: Setup 0.4.0 "does not work with the store extension", with a "signature error", after unpacking the packaged extension because the listing is not public yet.
- Cause: a store package has no `key`, so a copy loaded unpacked gets an ID from its folder, and the native host's `allowed_origins` refuses it. Setup 0.4.0 is correct and needs no change. The extension then said "Update the desktop app", which was wrong advice for this case.
- Evidence, in Chrome for Testing 156 from a normal terminal, against the owner's installed Setup 0.4.0 (real registry value read through WMI, manifest lists both IDs; local Setup hash equals the published asset):
  - Store CRX's code under the store ID (key added): `connected: true`, `dm.ping` ok, "Connected to the desktop app."
  - The CRX and the zip unpacked unchanged: IDs `eahiead…` and `dpjlmpm…`, `Access to the specified native messaging host is forbidden.`
  - The CRX's three signatures verify, and it differs from the uploaded zip only by `update_url` and `_metadata`.
- Owner's detail: the error appears in the Inbound notification, only when a download is triggered; Setup's own extension works and only Google's package does not. Reproduced with a real download and automatic sending on: the raw CRX shows "Inbound unavailable: Update the Inbound Download Manager desktop app. The installed version does not recognise this extension. This download stays in Chrome."
- Real download under the store ID, against a separate app instance (`DOWNLOADMANAGER_INSTANCE` + `DOWNLOADMANAGER_DATA`): "Sent to Inbound", Chrome's copy removed, job completed with all bytes.
- The owner confirmed the "signature error" was this "does not recognise this extension" notification (2026-11-07). No signature check was involved.
- Fix: the extension now names the refused ID and what to do, and still says to update the app when an accepted ID is refused. `scripts\make-store-id-test.ps1` makes a folder that runs the uploaded package under the store ID. New tests tie the store key, manifest key, `.iss`, registration script and extension to `BrowserBridge`.
- Fix evidence: extension 56/56, typecheck, Integration 139/139. In the same browser, the uploaded zip through the new script runs as `olhbcdb…` and shows "Connected to the desktop app."; a keyless build of this source shows the new message with its ID, in Settings and in the notification on a real download.
- Trap: Chrome for Testing saves downloads the extension leaves in Chrome to the user's real Downloads folder, and a separate instance also opens its own window and tray icon.
- Not verified: a real store install (the listing is not public). The new message is not in the package under review. **Bump the extension version before the next store upload**, since this source differs from the uploaded 0.4.0.

### Edge support (2026-11-06, branch `feature/edge-support`, not merged)

- Owner decision: Edge is a required, supported browser, reversing C3. Firefox is required too and comes last, after everything else (ROADMAP FF.1, no work started). Edge users install the Chrome Web Store listing, so the same ID applies and no new ID is added. The Edge Add-ons listing (EXT.S1) stays parked because the owner cannot get into the Edge program.
- Done in source: the native-host manifest is registered under `HKCU\Software\Microsoft\Edge\NativeMessagingHosts` as well as Chrome's, by Setup and by `register-native-host.ps1` (which install, repair and the updater all call). Edge's key is optional: a denied write gives a warning in the scripts and no error in Setup (`noerror` on that line only), and does not fail an install, repair or update; a denied Chrome key still does. `register-native-host.ps1` checks every key for a conflicting registration before writing anything, so `-Force` is needed for either and nothing is half written; a key whose manifest file is gone is not a conflict. The updater now replaces the `repair.ps1` and `register-native-host.ps1` that Setup put beside `app\`, adds none to a script install, and puts them back on rollback. The key paths are constants in `BrowserBridge`.
- Uninstall: Setup's uninstaller and `uninstall.ps1` remove a key only when it points at this install's manifest or at a file that no longer exists. Both exceptions come from the uninstall log 0.4.0 already wrote. An install from 0.4.0 Setup that moves up through the in-app updater keeps the 0.4.0 uninstaller, which knows only Chrome's key, so uninstalling it leaves Edge's key dangling (the same after an update rolled back to 0.4.0); that key is not a conflict and the Host replaces it. A 0.4.0 install upgraded through a later Setup is expected to keep its old log entry that removes Chrome's key without the check. **Owner decision when releasing:** a first Edge version shipped through Setup avoids the dangling Edge key; one shipped only through the in-app updater leaves it on uninstall.
- Registered state: each key is absent, usable or broken; usable means a manifest that lists the store origin and names a program that exists. Registered when either is usable. The status text (tray, status bar, CLI `status` and `doctor`, whose `integration:` line now shows this text) names a browser that cannot reach the host, such as `connected; Edge registration is broken` or `connected; not registered for Chrome`. Chrome with no Edge key stays `connected`. No protocol change: `HostStatusDto.BrowserIntegration` already carried this text.
- Repair at Host start, both ways: when one key is usable and the other is missing or points at a file that is gone, the Host writes the working value into the other (best effort, log only on failure, skipped when `DOWNLOADMANAGER_DATA` is set, never replacing a registration whose manifest exists).
- Wording: the app now says "browser" where Chrome was wrong for Edge (job source labels "Browser" and "Browser menu", the Settings checkbox, About text, INSTALL text) and so does the extension (notifications, popup, Settings consent text). Stored identifiers `chromeAuto` and `chromeContextMenu` are unchanged. The extension version is not bumped: the new extension wording reaches users only at the next Chrome Web Store upload.
- Evidence run: Release build 0 warnings; Core 107, Engine 57, Integration 190 passed; extension typecheck and 50 of 50 tests. `NativeHostRegistrationTests` cover the key paths, the state logic, the status text, the repair over a fake store (Chrome only, Edge only, dangling key, foreign manifest, program gone, isolated data, write failure), and Setup's text with comments removed. `register-native-host.ps1` and `apply-update.ps1` run for real against a fake in-memory registry in a PowerShell whose `HKCU:` drive has been removed, so a call that escaped the fakes fails instead of reaching the registry: both keys written, Edge denied, Chrome denied, conflict, dangling key, root scripts replaced and restored on rollback. `uninstall.ps1` stops processes by name and removes the real shortcut, so it is never run; only its ownership function is, lifted out with the PowerShell parser. Breaking each of ten fixes in turn made at least one of these tests fail. `installer\DownloadManager.iss` compiles with ISCC 7 against placeholder content; that output was not run.
- Earlier evidence about the store ID, with its source: the owner reports a real download handoff under the store ID worked in Edge on 0.4.0 (owner-reported; not reproduced here). The owner also confirms the Edge flyout stayed closed during it (UI.R2; the other half of that item, a normal flyout for downloads left to the browser, is still open). Measured on the installed 0.4.0 with no Edge key on the machine: Edge in a throwaway headless profile, under the store ID, got `host.ping` with `ok: true, pong`, so Edge does fall back to the Chrome key. Whether Edge reads its own key first when both exist was not measured: the check that writes the real registry was blocked by the session's permission system and needs the owner. The signed store CRX was verified (ID matches, 3 signatures valid, files the same as `dist` apart from Google's verification file and the manifest `key`/`update_url` difference), and a copy under the store ID answered `host.ping` with `ok: true, pong` through the installed 0.4.0 native host in Chrome for Testing 156 with a throwaway profile.
- Not verified: Setup or an update writing the Edge key on a real machine; the Host repair writing a key on a real 0.4.0 install, including the Host's real registry store, which tests replace with a fake; Setup's uninstaller applying the ownership rule, which was compiled but never run; the dangling Edge key after 0.4.0 Setup, an in-app update and an uninstall from Settings > Apps, and after a rollback; a 0.4.0 install upgraded through a later Setup and then uninstalled; a real Chrome Web Store install in Edge, whether Edge shows a prompt about other stores, and its auto-update. No Setup, real registry write or browser run happened for this change. The owner's main session runs the real Edge check.
- Traps: an agent shell can read a stale native-host registry value while a normal terminal reads the real one, and Chrome launched from the agent shell then reports "Specified native messaging host not found", so browser tests must run from a normal terminal; why the views differ is not established. The Chrome for Testing copy at `%TEMP%\opencode\cft\chrome-win64` lost its version manifest and fails to start with a side-by-side error; `%TEMP%\opencode\cft2\chrome-win64` works (CLAUDE.md corrected).

### Repository clean-up and media panel removal (2026-11-06)

- Removed from the extension at the owner's decision: media detection, in-page panel, popup card, Settings option, playlist fetching and badge. The extension now makes no network requests of its own. Right-click on links, images, video and audio, Download All, automatic sending and the Delete gesture remain. The app still downloads stream addresses it is given.
- Also removed: a Settings box that did nothing, and timer re-arming on unrelated settings changes. Removing a download now also removes its stream side files. The delete dialog no longer claims partial data is removed when completed files are involved.
- Reference material moved out of the repository and kept on the owner's PC. History rewritten to match.
- Evidence: Release build, Core, Engine, Integration and extension suites; store images re-rendered from the current build; the zip's contents loaded in real Edge.
- Not verified on screen: the About text, Options page and delete dialog changes were built and tested, but not looked at in the running app.

### Release 0.4.0 and the store ID (2026-11-06)

- App 0.4.0, the version after 0.3.2; there is no 0.3.3. It carries the auto-update fix, the slim speed bar and the clean-up, and trusts two extension IDs: the store's and the unpacked build's. Built from `main` in its own worktree, without TorBox.
- Evidence: extension 50/50, Core 107/107, Engine 57/57, Integration 134/134 (these include the new `BrowserBridgeTests`). The built native host exits 2 for an unknown origin and runs for both trusted ones. The update zip's registration script lists both.
- Published as v0.4.0. The owner ran Setup 0.4.0 on this PC and reports a clean install; its log shows the native-host registry value was set, and the real value points at the new manifest, which lists both IDs.
- The store page answered at `/detail/empty-title/<id>` with a generic title just after the submit, which suggests it is not public yet. The dashboard has the actual status.
- Not verified: a real connection under the store ID, which needs a store install; auto-update from an installed 0.3.2 to 0.4.0.
- Trap: an agent shell here can read a stale value for the native-host registry key (it showed the old `%LOCALAPPDATA%\DownloadManager` path, last written 2026-11-05). The cause is not confirmed. Before calling the registration broken, read the real value through WMI: `StdRegProv` `GetStringValue` under `HKEY_USERS\<SID>`.
- `NativeHostRegistration.IsRegistered` now needs the store origin, so a manifest from before 0.4.0 reads as not registered until Setup or an update rewrites it.
- Trap: running `DownloadManager.NativeHost.exe` directly starts a Host if none is running. Stop it afterwards, and clear `artifacts\app\data` before packaging.

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
