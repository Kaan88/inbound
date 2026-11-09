# Usability test, 2026-11-09 (0.6.3)

Owner, 2026-11-09: "do a comprehensive ui usability test." Four testers walked the 0.6.3 build from `dev` (6b8c980) on test instances, in dark mode only (light mode is deferred), and changed nothing. Severity is by user impact. IDs are `U-<area>-<n>`; the ROADMAP row TEST.5 tracks them.

## Desktop window, this PC

I walked the WinUI window in local mode on test instance utwl-7731, built from a detached worktree at C:\Users\kaank\Downloads\idm\.claude\worktrees\ut-window-local (dev 6b8c980, 0.6.3). Data was under artifacts\utwl-data, downloads under test-downloads\utwl, and TestServer with its mock TorBox ran on port 47315. I seeded 14 jobs (completed, active, paused, failed, retrying, queued for later, a long name, a 3-file TorBox package, extraction records) and drove the window through UI Automation, screenshots and a few real key and mouse inputs. Screenshots and the accessibility-check log are in C:\Users\kaank\Downloads\idm\.claude\worktrees\ut-window-local\artifacts\usability\window-local\. Nothing was fixed or committed.

The most serious problems are queue behaviour and failure feedback. A job saved with "Download later" stays Queued forever while the Main queue already reports running: Scheduler's Start Now is greyed out, and both toolbar Start Queue and Stop Queue are enabled. Start Queue also restarts downloads the user paused and failed downloads. A failed download shows no reason in the list or in Properties; only the Progress Details window, at its bottom, gives the operating-system message.

Other notable problems:
- Each finished download opens its own completion window in its own process, and they all open stacked at the same spot.
- One list row drew its columns in the wrong places after the Extraction column appeared.
- The toolbar buttons move sideways when a package is selected.
- The main window has no minimum size, so the toolbar clips at small sizes.
- Find filters the list with no sign that a filter is on.
- The status bar says "browser connected" when no browser is connected, and reports a broken registration with no repair action anywhere in the window.
- My TorBox in the window also has no context menu, and closes itself after Download all.
- The Web UI page puts the on/off box above the password, which must be set first, and shows no addresses for other devices before the firewall step.
- Numbered URL patterns are not expanded in the window.
- Several controls have no accessibility names.

More of the same as the owner's earlier reports: UI.R27 (Web UI is the last Options page, and its on/off box is unusable until a password is set lower down) and D2d (no context menu in the desktop My TorBox window either).

Cleanup: I stopped only the processes I started (UI 60492 and 12968, Host 55732 and 35364, web UI server 55244, TestServer 63508); none remain. I added no native-host, Run-key or firewall registrations and never clicked the firewall step. "Open in browser" opened a localhost tab in the owner's default browser (Edge, Personal profile), which is still open. I generated the web UI test password and did not print it; the mock TorBox key came from TestServer's fixture and was not echoed.

Covered: Main window: empty state, seeded list (completed, active, paused, failed, retrying, queued for later, long name, TorBox package), row selection, context menus for a single job, a package and an extraction record, toolbar enabled states; Menu bar: Tasks (before and after a TorBox account exists), File, Downloads, View, Help; Add URL: empty-URL validation, file-info step, Download later, numbered pattern, layout in a 640x420 window; Add Links (provider variant shown once a TorBox account exists): mixed text, a numbered pattern, a duplicate, a malformed ftp link; Add Magnet dialog (opened, not submitted); Download properties dialog; Progress Details window: failed and active jobs, all three tabs, segment details, Pause, Cancel confirmation; Package progress window (completed TorBox package); Completion dialog: 5 at once, controls and accessibility names; Delete dialog (single job) and Delete Completed confirmation; Options, every page: General, Save to, Downloads, Connection, Proxy, Site logins, Updates, Sounds, TorBox account (save and validate against the mock), Extraction, Web UI (password set, turned on, port changed, Open in browser used; firewall step never clicked); Scheduler: queue list, Start Now and Stop, schedule editor in its empty state; Statistics window; My TorBox window against the mock: item list, file tree, right-click, Download all, Escape; Extractions view: completed, corrupted and 'Downloaded' records, completion banner; Find dialog and its filtered result; About dialog; Keyboard tab order of the main window (toolbar, categories, header, list, graph, server switcher, menu); Accessibility check script (scripts\ui-accessibility-check.ps1): names, tab order, contrast tokens, simulated DPI; log saved as accessibility-check.txt; Small window sizes: 800x500 and 640x420 main window, Add dialog at 640x420

Not covered: Download All link-collection window and batch handoff: both start from the browser extension, and this run was window-only (no browser handoff); Import JD Container and Import Torrent File: they open the native Windows file picker, which UI Automation could not drive reliably with other agents' windows taking the foreground; Extract Archive... from the window: also opens a file picker. Extraction was started from the CLI and the results were checked in the window; Archive password prompt: no password-protected archive could be made without starting an exe from the shell; Remote server pairing and switching (File > Servers): the owner already reported this (UI.R26, UI.B16, UI.B17), and it is out of scope for local mode; Web UI pages in the browser: only Open in browser was exercised, which opened the owner's default Edge. The DOM was not inspected (that is the web UI track); Windows Firewall step: deliberately never clicked; Check for Updates: the test instance might contact the real update server; Light theme: deferred by the owner; Tray menu and closing or relaunching the window: not exercised; Full keyboard walk of every dialog (only the main window's tab order was traced): real key input kept losing the foreground to other agents' windows on this shared desktop; Real DPI scaling other than 100% (only the script's simulated matrix ran)

### U-WL-1 (high, bug): 'Download later' jobs never start while the Main queue reports running, and Scheduler's Start Now is greyed out

- Area: Queues / Scheduler
- Steps: 1. Add a URL in the Add dialog and choose Download later (or CLI add --start later). 2. Open Scheduler with Main selected. 3. Wait several minutes.
- Expected: Either the job starts because the queue is running, or Start Now is available to start it. The queue state shown should be consistent everywhere.
- Actual: The job stays Queued with 0 active and 3 allowed. The CLI shows Main as 'running'. Scheduler shows Start Now disabled and Stop enabled. The Downloads menu shows 'Start Main download queue' disabled, yet the toolbar shows Start Queue and Stop Queue both enabled. Only toolbar Start Queue, or Scheduler Stop then Start Now, gets the job going. later-archive.zip and the long .tar.gz sat Queued for about 10 minutes until Stop and Start Now.
- Evidence: 52-scheduler.png (Start Now disabled while 2 items are queued); 53-download-later-stuck.png; UIA: 'later-two.bin, Queued' persisted 20 s after adding; CLI queue list: '0 running 3 Main'.

### U-WL-2 (high, bug): Starting the queue resumes downloads the user paused, and retries failed ones

- Area: Queues / toolbar
- Steps: 1. Pause big-dataset.iso from its Progress Details window (it shows Paused). 2. Click Scheduler > Start Now, or toolbar Start Queue.
- Expected: A download the user paused stays paused. Starting the queue starts queued items only.
- Actual: big-dataset.iso went back to Downloading. unreachable-file.bin went from Failed back to Retrying/Connecting, and the status bar showed '1 retrying'.
- Evidence: CLI list after Start Now: 'downloading 381.5 MB 31.4% big-dataset.iso' (it had been Paused at 23.2%); status text '14 downloads · 1 active · 0 queued · 1 retrying'.

### U-WL-3 (high, usability): A failed download's reason appears only at the bottom of Progress Details

- Area: Main list / Properties
- Steps: 1. Add http://127.0.0.1:1/unreachable-file.bin (or let any download fail). 2. Look at the row. 3. Open Properties. 4. Open Progress Details.
- Expected: The list (tooltip, Description or a status detail) and Properties say why it failed, in plain words, with what to do next.
- Actual: The row shows only 'Failed'. Properties has File/Status/Queue/Source/Address/Saved to/Last try and no error. Only Progress Details shows the raw operating-system text 'No connection could be made because the target machine actively refused it. (127.0.0.1:1)', as a small notice above the buttons, far from Status. report.pdf (failed writing to a folder it couldn't write) never shows any reason in the list.
- Evidence: 06-after-start.png, 32-properties.png, 33-progress-failed-1.png

### U-WL-4 (medium, usability): Each finished download opens its own completion window, all stacked in one spot far from the main window

- Area: Completion dialog
- Steps: 1. Let 5 downloads finish close together (CLI add ... --start now, or any batch). 2. Look at the screen.
- Expected: One completion summary (or a notification) for a burst, opened near the main window. Or windows offset so the user sees there are several.
- Actual: Five 'Download complete' windows, each its own process (PIDs 71380, 57632, 72424, 70952, 70932), opened exactly on top of each other at 78,78 while the main window was at 1960,80. The user sees one and has to close five. Progress and package windows likewise open at fixed spots (78,78 and 312,312), not near the main window.
- Evidence: 30-main-seeded-1.png to 30-main-seeded-5.png; UIA window list shows the five titles with rect=78,78,600,480.

### U-WL-5 (medium, visual): An existing row keeps the old column positions after the Extraction column appears

- Area: Main list layout
- Steps: 1. Have a failed job in the list (report.pdf). 2. Add downloads so the Extraction column appears (a .zip is added).
- Expected: Every row lines up with the new headers.
- Actual: report.pdf drew its Q value '1' under Size and 'Failed' under Extraction, while new rows lined up correctly. Later redraws fixed it.
- Evidence: 31-complete-delete.png (last row)

### U-WL-6 (medium, usability): Toolbar buttons shift sideways when a package is selected

- Area: Toolbar
- Steps: 1. Select a normal job and note where Delete is. 2. Select the 'Sample Torrent' package row.
- Expected: Buttons stay put. A package-only command appears in a fixed slot or is just disabled.
- Actual: A 'Cancel Package' button is inserted after Stop All, pushing Delete, Delete Completed, Options, Scheduler, Start Queue and Stop Queue about 97 px to the right (Delete from x≈375 to 472). That breaks muscle memory and risks clicking the wrong button.
- Evidence: 50-package-context.png compared with 49-mytorbox-downloadall.png

### U-WL-7 (medium, visual): The main window has no minimum size; the toolbar and columns clip with no overflow

- Area: Main window size
- Steps: Resize the main window to 800x500 and then 640x420.
- Expected: A sensible minimum (startup already clamps a remembered size to 900x560), or a toolbar overflow (…) menu and a horizontal scroll for columns.
- Actual: At 800x500, Scheduler/Start Queue/Stop Queue are cut off with no overflow menu, and Status/Extraction columns are clipped. At 640x420 only 3 rows show under the fixed-height speed graph, and Status is cut off.
- Evidence: 60-main-800x500.png, 61-main-640x420.png

### U-WL-8 (medium, usability): Find filters the list with no sign that a filter is on

- Area: Find
- Steps: 1. Downloads > Find… 2. Search 'video'. 3. Close the dialog.
- Expected: A visible 'Showing results for video — Clear' bar or search box, and an updated count.
- Actual: The list silently shrinks to 2 rows. The status bar still says '14 downloads', the sidebar still highlights All Downloads, and the only way back is to reopen Find and press Clear. A user can think downloads vanished.
- Evidence: 66-find-result.png

### U-WL-9 (medium, wording): Status bar says 'browser connected' when no browser is connected, then reports a broken registration with no fix in the window

- Area: Status bar / browser integration
- Steps: Start the window (no browser running) and read the right side of the status bar.
- Expected: Text that means what it says (for example 'browser integration on'), and a clickable repair or explanation when registration is broken.
- Actual: 'Host connected · 0.6.3 · browser connected; Chrome registration is broken'. 'connected' just means the native host is registered (NativeHostRegistrationState.IntegrationText). The text is not clickable, and Options has no browser page or Repair button. The broken flag itself may come from the test instance and the agent shell's registry view (CLAUDE.md known trap); the wording and missing action stand regardless.
- Evidence: 01-main-empty.png; src/DownloadManager.Contracts/NativeHostRegistrationState.cs IntegrationText; grep finds no Repair action in DownloadManager.Desktop.WinUI

### U-WL-10 (medium, usability): Web UI setup order is backwards, and no address is shown before the firewall step (more of UI.R27)

- Area: Options > Web UI
- Steps: 1. Options > Web UI (last page). 2. The 'Let other devices…' box is greyed out. 3. Type the password twice and click Set password. 4. Look for the box and the address to use.
- Expected: Password first, then the on switch, both visible together. After turning it on, the address to type on another device (for example http://<PC>:port) is shown.
- Actual: The on box sits above the password and is disabled until the password lower down is set. After setting the password the page scrolls so the box is off screen. The new-password boxes are tiny (91 px and 79 px, sized to their labels). After Apply, the Addresses section shows only an empty 'Also reachable as' box and 'Other devices can't reach the web UI until Windows Firewall allows it', so the user can't learn the URL before the firewall step. Default port 8080 is often in use.
- Evidence: 22-opt-Web-UI.png, 24-webui-pw-set.png, 27-webui-running.png; UIA: 'Edit name=New web UI password rect=…,91,59' and '…again rect=…,79,59'

### U-WL-11 (medium, usability): Desktop My TorBox has no context menu, right-click doesn't select, and Download all closes the window (more of D2d)

- Area: My TorBox window
- Steps: 1. Tasks > Browse My TorBox… 2. Right-click 'Sample Torrent'. 3. Select it and click Download all.
- Expected: A context menu with quick actions (Download, Download to…, Copy link, Delete from TorBox, Refresh). The window stays open so more items can be fetched.
- Actual: Right-click shows nothing and doesn't even select the row (detail pane says 'Select an item to see its files'). Download all queues the files and closes My TorBox. The file tree area is about 120 px tall while the item list above is mostly empty, so a 3-file item already needs scrolling. Hosts are named 'Hosters' (button), 'TorBox Hosters…' (menu) and 'TorBox file hosts' (accessibility name).
- Evidence: 47-mytorbox-selected.png, 48-mytorbox-right.png, 49-mytorbox-downloadall.png

### U-WL-12 (medium, parity): Numbered URL patterns are not expanded in the window

- Area: Add Links / Add URL
- Steps: 1. Tasks > Add Links…, paste http://127.0.0.1:47315/file/part[1-3].bin?size=100000. 2. Or put the same in Add URL.
- Expected: part1.bin to part3.bin are offered, as the CLI add-batch pattern does (CLAUDE.md lists batch/numbered URLs for CLI and WinUI).
- Actual: One item 'part[1-3].bin' as an 'Ordinary link'. Add URL treats it as a literal file name 'part[1-3].bin'. With a TorBox account, Add Links is replaced by the provider dialog, so the classic batch dialog is gone.
- Evidence: 56-addlinks-filled.png, 57-addurl-pattern.png; MainWindow.xaml.cs MenuAddBatch_Click routes to ShowProviderAddAsync when ProviderAddAppliesAsync()

### U-WL-13 (medium, usability): Open and Open With stay enabled for a failed download that has no file, and do nothing

- Area: Context menus
- Steps: Right-click a Failed row (report.pdf, never written) and choose Open.
- Expected: Open/Open With disabled for jobs without a finished file, or a message saying the file doesn't exist yet.
- Actual: Both are enabled. Open does nothing visible. Open Folder and Extract Archive… are enabled too.
- Evidence: 08-context-menu.png, 10-open-failed.png

### U-WL-14 (low, usability): Package context menu disables useful actions and enables odd ones

- Area: Context menus / package
- Steps: Right-click the 'Sample Torrent' package row.
- Expected: Open Folder opens the package folder. Remove from Package is not offered for the package itself. Archive Password only appears when it holds archives.
- Actual: Open, Open With, Open Folder, Move/Rename and Properties are disabled with '(select a file, not a package)'. 'Remove from Package' and 'Archive Password…' are enabled for a package of .bin/.txt files. 'Extract (no archive selected)' (disabled) sits next to an enabled 'Extract Archive…'.
- Evidence: 50-package-context.png

### U-WL-15 (low, wording): Stop vs Pause, Cancel vs Delete, and the time-left format differ between windows

- Area: Terminology
- Steps: Compare the main toolbar and menus with the Progress Details window and the list.
- Expected: One verb for pausing and one time format throughout.
- Actual: Toolbar and menu: 'Stop' and 'Stop All'. Progress window: 'Pause' and 'Resume'; the paused job's cancel dialog offers 'Keep downloading'. Time left is '12m 4s' in the list but '00:08:52' in Progress Details. Size rounds to '2.9 MB' in the list but '2.86 MB' in the completion dialog. Category 'Automatic (from file type)' in Add vs 'Automatic (file extension)' in Properties.
- Evidence: 31-complete-delete.png, 36-progress-active-1.png, 38-progress-cancel-confirm-1.png, 04-addurl-step2.png, 32-properties.png

### U-WL-16 (low, visual): Failed job's window title says '0.0%', and value columns are aligned differently

- Area: Progress Details window
- Steps: Open Progress Details for unreachable-file.bin.
- Expected: Title reflects state (for example 'Failed — file'). Values line up in one column.
- Actual: Title '0.0% — unreachable-file.bin'. Status and Resume capability are left-aligned at x≈172 while File size, Downloaded, rate and time are right-aligned at x≈382. A large empty area sits between the details and the error notice. 'Options on completion' says the global preference 'can be changed from the completion window', though it is also in Options > Downloads.
- Evidence: 33-progress-failed-1.png, 35-progress-oncomplete-2.png

### U-WL-17 (low, usability): Completion dialog puts Delete first and has no default action

- Area: Completion dialog
- Steps: Let a download finish and look at the dialog buttons.
- Expected: Open or Open folder as the highlighted default, with Delete away from it.
- Actual: Buttons read Delete | Open | Open with… | Open folder | Close, none highlighted, Delete leftmost next to Open. The Address and saved-path edit boxes have no accessibility names. A full progress bar and a large gap fill the middle.
- Evidence: 30-main-seeded-5.png; UIA 'Edit id=CompletedUrl name=' and 'Edit id=CompletedPath name='

### U-WL-18 (low, usability): Several controls have missing or garbled accessible names

- Area: Accessibility
- Steps: Inspect with UI Automation (Options > Sounds, Scheduler, server switcher, Connection, Web UI).
- Expected: Every input has a meaningful name, and the switcher announces the current choice.
- Actual: Sounds: the three sound ComboBoxes have empty names. Scheduler: the queue list and the schedule picker ComboBox have empty names. Server switcher is named 'Show downloads of' without 'This PC'. Number boxes read 'Total speed limit, all downloads (KB/s) Minimum1 Maximum1e+07'. Web UI reads 'Network type: Network type: Ethernet is Private…'. Ellipsis characters come through as '.' in names such as 'Browse.' and 'Open with.'.
- Evidence: UIA dumps: Sounds 'ComboBox id= name= ' ×3; Scheduler 'List id= name=' and 'ComboBox id= name='; 'Button id=ServerSwitcher name=Show downloads of'

### U-WL-19 (low, usability): Category folder Browse and favourite buttons stay enabled when category folders are turned off

- Area: Options > Save to
- Steps: Options > Save to, clear 'Save downloads to a folder for their category'.
- Expected: Browse and the star buttons disable with their fields.
- Actual: The fields grey out but 'Browse for Compressed' and the others stay enabled.
- Evidence: 29-saveto-edited.png; UIA 'Edit name=Compressed en=False' next to 'Button name=Browse for Compressed en=True'

### U-WL-20 (low, usability): Extraction records reuse download columns and also appear in All Downloads

- Area: Extractions view
- Steps: Extract a local archive (CLI archive extract), then open Extractions and All Downloads.
- Expected: Columns that fit extractions, and extraction records kept out of the download list (or clearly marked).
- Actual: Q, Size, Status, Time left and Transfer rate are empty for every record, and Description repeats the Extraction text. An auto-registered .tar.gz that was never extracted shows the ambiguous state 'Downloaded'. 'real-archive' (an extraction of a local file, not a download) appears as a row in All Downloads.
- Evidence: 58-extractions-view.png, 60-main-800x500.png (real-archive row at top)

### U-WL-21 (low, visual): Placeholder URL looks like a pre-filled value

- Area: Add URL
- Steps: Click Add URL.
- Expected: Placeholder in a clearly dimmer colour.
- Actual: 'https://example.com/file.zip' is drawn near-white in the URL box, though the value is empty (UIA value='').
- Evidence: 02-addurl.png; UIA 'ComboBox name=URL value= help=https://example.com/file.zip'

### U-WL-22 (low, wording): Container import is hidden until a TorBox account exists and uses jargon

- Area: Tasks menu
- Steps: Open Tasks before and after saving a TorBox key.
- Expected: A clear 'Import link container (.dlc)…' item, or a note on why it needs an account.
- Actual: Without an account the Tasks menu has no import item. With one, 'Import JD Container…' appears with no explanation of 'JD'.
- Evidence: 42-tasks-menu (Tasks menu UIA list without the account) compared with 45-tasks-menu-torbox; MainWindow.Remote.cs adds it only when usable.Count > 0

### U-WL-23 (low, usability): A malformed link is selected by default and items show no host or URL

- Area: Add Links
- Steps: Paste text containing 'ftp://bad host' with other links.
- Expected: Invalid links unchecked or flagged. Each item shows its host or URL so same-named files can be told apart.
- Actual: 'bad' appears as a checked 'Ordinary link'. Rows show only a file name and 'Ordinary link'. At a 760 px window the options below the Start/Queue row are clipped and need scrolling.
- Evidence: 56-addlinks-filled.png

### U-WL-24 (low, usability): Same-named files in different folders can't be told apart, and the window is 820 px tall

- Area: Package window
- Steps: Open Progress Details for the 'Sample Torrent' package.
- Expected: The relative path (other/video.bin, sub/video.bin), and a height that fits 768-px screens.
- Actual: Two rows read 'video.bin, Complete'. The window opens 720x820 at 312,312, away from the main window.
- Evidence: 51-package-window-1.png

### U-WL-25 (low, usability): Saving a TorBox key doesn't validate it, and the page mixes immediate and OK/Apply actions

- Area: TorBox account / Options
- Steps: Options > TorBox account, enter a key, Save key.
- Expected: Save validates at once, or one commit model for the whole Options window.
- Actual: After Save: 'Key saved. Select Validate to check it with TorBox.' A second click is needed. The key buttons act at once while the rest of Options waits for OK/Apply (the page explains this in a note).
- Evidence: 43-torbox-saved.png, 44-torbox-validated.png

### U-WL-26 (low, wording): About text is developer jargon

- Area: About
- Steps: Help > About Inbound Download Manager.
- Expected: A user-facing description, version, and links (licences, diagnostics).
- Actual: 'Private download manager built around a durable background host, a replaceable WinUI 3 frontend, and a Manifest V3 browser bridge.'
- Evidence: UIA dump of the About dialog (63-about capture)

### U-WL-27 (low, wording): 'Closing the window ends it' is ambiguous

- Area: Options > General
- Steps: Options > General, read the Window section.
- Expected: Says that downloads keep running in the background (tray) and only the window closes.
- Actual: 'Closing the window ends it, to use less memory and power while closed' — it is unclear whether downloads stop.
- Evidence: 12-options-general.png

## Desktop window managing a server (and the bridge)

I walked the WinUI desktop window while it showed another server. The server was a local DownloadManager.Server (hostKind server, roots under artifacts) and a second test Host with its web UI bridge on (hostKind windows). I also drove the web UI with Playwright Chromium. Pairing, adding URLs into server folders, Move/Rename, progress and package windows, My TorBox (mock), Add Links (batch, torrent, magnet), reconnection, the wrong-password check and the revoked-token message mostly work.

The biggest problems are about state:
- A server that can't be reached looks like an empty server: "No downloads in this view. Use Add URL..." with "0 downloads", and only a small "Couldn't reach the server." in the corner. This happens at startup too.
- A lost connection does not dim the list or offer an action.
- The window seldom says which machine it shows.

Other problems:
- More of UI.B17: the extraction folder and the archive-password target use this PC's path rules.
- More of UI.B16: the folder chooser is not owned by the main window and its New folder dialog has no theme.
- More of UI.R26: switching servers is hard.
- More of D2d: there is no context menu in My TorBox on the desktop.
- Container import dead-ends.
- The bridge's Add URL warns and then fails.
- Several wording and accessible-name problems.

Every process I started was stopped by PID. Bridge 66848 ended with Host B, and nothing under ut-window-remote is still running. No registry key or native-host registration was added. Artifacts are in C:\Users\kaank\Downloads\idm\.claude\worktrees\ut-window-remote\artifacts\usability\window-remote\ (shots\, web\, logs).

Covered: Servers dialog: Add with a wrong password, then the right one; Edit to re-pair; Show; Remove; the status-bar switcher; Remote list, row context menu, Move/Rename inside and outside the roots; Add URL to the server with the server's folder chooser (browse, New folder, roots) and free space; Add Links: numbered batch, torrent file path (uploaded), magnet; the package window; completion windows; Container (.rsdf) import dialog; Options for a server (General, Connection, TorBox key, Extraction view) and for a Windows bridge host; My TorBox window against the mock API: list, download, right-click; Progress window for a remote job (--server); Server stopped while shown, then restarted (reconnects in about 5 s); revoked token; Options while the server is down; Startup with the last-shown server down; switching to a server that is down; Host B Options > Web UI: password, turn on, state text, firewall state (firewall button never clicked); Web UI on Host B through Playwright: wrong and right sign-in, General settings, Apply of a host-only setting, folder browser, removing a desktop pairing, host switcher; Pairing desktop window A to Host B's bridge and adding a URL there; Linux path rules checked by code reading (ServerPaths.IsFull, AddLinksDialog.IsFullPath, ArchiveForm, ArchiveRows, MyProviderWindow)

Not covered: A real Linux/Docker server (only code reading and a local server with Windows paths); Real LAN access, the firewall rule, HTTPS; Browser extension and Firefox native host (EXT.B6); Scheduler and Statistics windows in remote mode; Extract from disk on the server, and the archive password typed in advance, at runtime; A progress window open during a disconnect; Light theme (dark only, by rule); Keyboard-only walk of every dialog (only spot checks)

### U-WR-1 (high, usability): A server that can't be reached looks like an empty server, at startup and after switching

- Area: Desktop: remote server unreachable
- Steps: 1. Pair a server and show it. 2. Stop the server. 3. Restart the window, or switch from This PC to that server.
- Expected: A clear state across the list ('Can't reach Test server', the reason, Retry, Switch to This PC). The list is not presented as empty.
- Actual: The list says 'No downloads in this view. Use Add URL or the browser extension to start one.' and the status bar says '0 downloads · 0 active · 0 queued'. Only a small 'Couldn't reach the server.' appears at the bottom right. Add URL looks usable. The Extractions item is gone from the sidebar.
- Evidence: shots\70-switch-to-down-server-1.png, shots\71-startup-last-server-down-1.png

### U-WR-2 (medium, bug): Add URL warns that the address may not work, offers Start anyway, then fails and loses the input

- Area: Desktop: adding to a Windows bridge host
- Steps: 1. In window A, pair Host B's web bridge (hostKind windows). 2. Add URL with http://127.0.0.1:<port>/file. 3. Read the file info, then click Start.
- Expected: Either the add works, or the dialog blocks Start with a clear reason and keeps what was typed.
- Actual: The file info says 'Downloads added from another device can't use addresses on this PC or its local network. You can still start the download.' ('this PC' is ambiguous: the server, or the PC the user sits at?). Start then shows 'Could not add download', and the Add URL dialog has already closed, so the URL, folder and description are lost.
- Evidence: shots\67-A-add-to-B.png; UIA showed the 'Could not add download' dialog

### U-WR-3 (medium, usability): A lost connection or a revoked token shows only in the status-bar corner; the stale list stays live

- Area: Desktop: connection lost / token revoked
- Steps: 1. Show a paired server with some downloads. 2. Stop the server, or revoke the pairing in its web UI.
- Expected: A banner or overlay that says the list is out of date, with Reconnect / Pair again / Switch to This PC. Actions are disabled until it is back.
- Actual: The list stays as it was and looks live. Only the status-bar text changes ('Couldn't reach the server.'; the revoked-token text itself is good). There is no action except going through File > Servers. Reconnecting after the server came back took about 5 s.
- Evidence: shots\46-*.png, shots\47-server-gone*.png, shots\49-revoked*.png

### U-WR-4 (medium, wording): Options says '<name> isn't connected yet.' and This PC's options can't be reached

- Area: Desktop: Options while the server is down
- Steps: 1. Show a paired server. 2. Stop the server. 3. Click Options.
- Expected: 'Can't reach 127.0.0.1 — its options can't be shown right now', and a way to open This PC's options.
- Actual: The message is '127.0.0.1 isn't connected yet.' ('yet' implies it is connecting). There is no route to this PC's own Options without switching first.
- Evidence: shots\48-options-while-gone.png; MainWindow.Servers.cs ShowServerOptionsAsync

### U-WR-5 (medium, usability): (More of UI.R26) The window, Add URL, progress and completion windows don't name the server; switching takes too many steps

- Area: Desktop: knowing which machine is shown
- Steps: 1. Pair a server via File > Servers… (the last item of File). 2. After a successful pair, look at the dialog. 3. Show the server, open Add URL and a progress window.
- Expected: After pairing, the server is shown, or a 'Show now' is offered. The title bar and the secondary windows name the server ('Inbound Download Manager — Home server'). Servers is easy to find, for example from the switcher.
- Actual: After a successful pair the dialog returns to its list; the user must select the server and click Show. The title stays 'Inbound Download Manager'. Only the small switcher text and the status text change. Add URL, progress, completion and My TorBox ('My TorBox — Inbound Download Manager') don't say which server they act on.
- Evidence: shots\02*.png, shots\08*.png, shots\09*.png, shots\11*.png, shots\20-progress-remote-1.png, shots\33-mytorbox.png

### U-WR-6 (medium, wording): The TorBox page for a server doesn't say whose account it is and wrongly says the key is kept for your Windows account

- Area: Desktop: TorBox account for a server
- Steps: 1. Show a paired server. 2. Options > TorBox.
- Expected: 'This is the TorBox account of <server>. The key is stored on that server.'
- Actual: No mention of the server. The text 'The key is kept encrypted for your Windows account' is wrong for a Docker/Linux server.
- Evidence: shots\29-options-torbox.png

### U-WR-7 (medium, usability): (More of D2d) The desktop My TorBox has no context menu either

- Area: Desktop: My TorBox
- Steps: 1. Open My TorBox (local or remote). 2. Right-click an item.
- Expected: Context actions: Download, Download to…, Copy link, Delete.
- Actual: Nothing happens. MyProviderWindow sets no ContextFlyout.
- Evidence: MyProviderWindow.cs (no ContextFlyout); right-click tested in the session

### U-WR-8 (medium, bug): Container import says links 'can be added as a normal download' but offers no way to do it

- Area: Desktop: container import
- Steps: 1. Add Links / import an .rsdf container whose links are plain HTTP. 2. Look at the import dialog.
- Expected: A button or checkbox to add those links as normal downloads.
- Actual: The rows are marked Unsupported with 'It can be added as a normal download.' The checkboxes are disabled and only Cancel is offered: a dead end. This happens locally too.
- Evidence: shots\45-container-import.png; ContainerImportService.cs:287

### U-WR-9 (medium, bug): (More of UI.B17) The extraction folder and the archive-password target check server paths with this PC's rules

- Area: Desktop: Linux paths on a server
- Steps: 1. Show a Linux/Docker server. 2. Options > Extraction: enter /downloads/extracted. 3. Or set a password in advance for an archive row on the server.
- Expected: The server's rules decide (ServerPaths.IsFull already does this correctly).
- Actual: ArchiveForm.cs:66 uses Path.IsPathFullyQualified(folder), so a Linux path is refused as not full. ArchiveRows.cs:394 uses the same check, so the password-in-advance target is not offered for Linux paths. Found by code reading; not run against a real Linux server.
- Evidence: Remote/ArchiveForm.cs:66, Remote/ArchiveRows.cs:394, compared with RemoteRules.cs ServerPaths.IsFull

### U-WR-10 (medium, visual): (More of UI.B16) The folder chooser cuts off root paths, opens away from its owner, isn't owned or modal, and the New folder dialog has no theme

- Area: Desktop: server folder chooser
- Steps: 1. Show a server with two roots ...\srv-dl and ...\srv-dl2. 2. Add URL > Browse (server folder chooser). 3. Click New folder.
- Expected: Full paths or a tooltip. The chooser is centred on and owned by its dialog. The New folder dialog is dark. 'Choose this folder' is disabled when nothing is chosen. An empty folder says so.
- Actual: Long roots are cut off at the right, so srv-dl and srv-dl2 look the same, with no tooltip. The window opened at 1440,436, partly under other windows. It is not owned or modal, so it can fall behind. The New folder ContentDialog sets no RequestedTheme. 'Choose this folder' stays enabled with nothing chosen. An empty folder shows a blank list.
- Evidence: shots\12-folderchooser.png; Servers/ServerFolderWindow.cs

### U-WR-11 (medium, usability): Removing a desktop pairing has no confirmation and the toast talks about a browser

- Area: Web UI: devices
- Steps: 1. Web UI > devices/pairings. 2. Remove the desktop window's pairing.
- Expected: A confirmation ('Window A will lose access'), then a toast about that device.
- Actual: It is removed at once. The toast says 'That browser can no longer reach the server.' even though it was a desktop app.
- Evidence: web\w07-remove-confirm.png

### U-WR-12 (low, usability): A Move/Rename error closes the dialog and doesn't say which folders are allowed

- Area: Desktop: remote Move/Rename
- Steps: 1. On a server row, Move/Rename to a folder outside the roots. 2. Apply.
- Expected: The error appears inside the dialog with the allowed folders listed; the input is kept.
- Actual: 'That folder is outside the folders this server may use.' appears after the dialog has closed, so everything must be re-entered. The folder box has no accessible name.
- Evidence: shots\18*.png

### U-WR-13 (low, wording): 'Anything not shown here is changed in its web UI' is not true for folders on a Windows host; no link to the web UI

- Area: Desktop: Options for a Windows PC host
- Steps: 1. Pair Host B (windows). 2. Options.
- Expected: Accurate text and a button to open that host's web UI.
- Actual: Folders can't be changed in that web UI either. Desktop Options shows far fewer pages than the web UI, and there is no link.
- Evidence: Session observation of Options for Host B

### U-WR-14 (low, bug): The bridge offers settings it can't save and shows a raw field name

- Area: Web UI on a Windows host (bridge)
- Steps: 1. Turn on the web UI on Host B. 2. Sign in. 3. General > tick 'Capture downloads from your browser' > Apply.
- Expected: Settings only this PC can change are hidden or shown read-only with a note.
- Actual: 'These settings can only be changed in the app on this PC: browserIntegrationEnabled. Nothing was saved.' 'Resume unfinished downloads when the server starts' and the footer 'Server 0.6.3' aren't adjusted for hostKind windows. The folder browser's Allowed folders lists Downloads twice.
- Evidence: web\b08-apply-capture.png

### U-WR-15 (low, visual): Web UI password boxes and 'Also reachable as' are narrow; no address shown while the firewall rule is missing

- Area: Desktop: Web UI options page
- Steps: 1. Host B Options > Web UI.
- Expected: Full-width inputs. The address to use from this PC is shown, even before the firewall step.
- Actual: The two password boxes are very narrow and not the same width. 'Also reachable as' is narrow. The state is 'On, but only this PC can reach it until Windows Firewall allows it' without the loopback address and port (it was listening on 127.0.0.1 and ::1, port 18743).
- Evidence: shots\6*-hostB-webui*.png

### U-WR-16 (low, usability): Several 'Download complete' windows stack at the same spot for one package or torrent; the text says 'Choose an action below' with only Delete offered

- Area: Desktop: completion windows
- Steps: 1. Add a numbered batch or a multi-file torrent to a server. 2. Let it finish.
- Expected: One summary per package, or windows offset from each other. Text that matches the actions.
- Actual: Several windows open on top of each other at the same position. For remote items only Delete is offered, yet the text says 'Choose an action below'.
- Evidence: shots\38-*.png

### U-WR-17 (low, wording): Generic package and torrent names

- Area: Desktop: names
- Steps: 1. Add a numbered batch. 2. Add a magnet with dn=.
- Expected: Names from the pattern and the dn=/torrent name.
- Actual: The package is named 'batch'. Torrent rows show 'torrent-01234567' until metadata arrives, and the dn= name is not used.
- Evidence: Session list screenshots shots\3*.png

### U-WR-18 (low, wording): Accessible names that are cut off, repeated or raw

- Area: Desktop: accessibility
- Steps: Inspect with UI Automation: the status-bar switcher, number boxes in Options, Network type.
- Expected: Clear names.
- Actual: The switcher's name is 'Show downloads of' (cut off). Number boxes read 'Minimum1 Maximum1e+07'. 'Network type: Network type:' is said twice.
- Evidence: UIA dumps from ui-drive-pid.ps1

### U-WR-19 (low, wording): Status-bar plural, error colour, placeholder, window position, 'Remember this folder' default

- Area: Desktop: small wording and defaults
- Steps: Use the window with one download; pair with a wrong password; open Add Server; open Options; Add URL to a server.
- Expected: '1 download'. The pairing error in an error colour. A placeholder that is clearly a hint. Options opened near the main window. 'Remember this folder' off by default, or saying that it changes the server's default.
- Actual: '1 downloads'. The pairing error is plain text. The 'Home server' placeholder looks typed. Options opens at a fixed 338,338. 'Remember this folder' is ticked by default and silently changes the server's settings.
- Evidence: shots\0*.png, shots\1*.png

## Web UI

I tested the web UI by hand against the 0.6.3 build from dev (commit 6b8c980), in a detached worktree at C:\Users\kaank\Downloads\idm\.claude\worktrees\ut-webui. Everything was dark mode, at 1360x820 and 390x844 in Playwright Chromium. Two test DownloadManager.Server instances ran on loopback, ut-webui-a on port 18851 and ut-webui-b on 18852 (second host). Each had its own data folder under artifacts\usability\webui\run. TestServer provided the downloads and the mock TorBox, and a small local file server provided the test archives and the .torrent file.

I first ran a dark-only copy of webui's screens walk (webui\scripts\screens-dark.mjs, untracked): 120 of 120 steps passed. I then wrote my own scripts for each flow (webui\ut\t1-t21.mjs, untracked). Screenshots are in artifacts\usability\webui\screens (the full walk) and artifacts\usability\webui\shots (my flows).

Overall the web UI works and follows the desktop window closely: OK/Cancel/Apply drafts, row menu, package details, keyboard grid navigation, Alt+N, Shift+F10 and Delete all behave. The biggest problems are:
- **Getting back after switching hosts.** The saved host list belongs to each web UI address, so the second host's page doesn't list the first one. This is the root of the already-reported UI.R26 and makes it worse.
- **Things that drop out of reach or out of sight.** The toolbar's More button is pushed off-screen in the Waiting for TorBox view; long folder paths show only their start; a folder outside the allowed roots is silently replaced by the download folder.
- **Phone use.** Touch targets are small, the toolbar is icons only, and Back leaves the open dialog on top.
- **Dialog focus.** After a step changes, focus falls to the page and Esc stops working.
- **Desktop parity.** The row menu lacks Move/Rename, Redownload, Change Category, Refresh download address and Start Now. Numbered URLs like img[1-3] are not expanded.

On the already-reported items: I could only check UI.B17 for the web folder picker, where a Linux path gets "Give the folder's full path." on this Windows server. The other five were not in my scope.

I stopped every process I started, by PID only: TestServer 41956, server A 9256 (killed on purpose for the server-stop test) and its restart 70796, server B 58372, and the Python file server 49712. All four ports are free again. The chrome-headless-shell processes still running belong to other agents' Playwright runs and were left alone. I added no registry, native-host or firewall entries, made no commits and fixed nothing. The ut-webui worktree is kept for the evidence; I deleted the saved test sign-in state (webui\ut\state.json).

Covered: Build: dotnet build of DownloadManager.slnx (Release), webui npm ci and build, then a server rebuild so wwwroot has the built UI (index.html checked identical to webui\dist); Scripted walk of every screen at 1360 and 390 wide, dark mode (120/120 steps passed; artifacts\usability\webui\screens); Sign-in: first password setup on the second host, wrong password, many wrong tries and lockout messages, desktop and phone; List: views, search plus select all plus clearing the search, bulk Delete dialog, selection when the view changes, row menu by mouse, Shift+F10 and the ContextMenu key, package rows, Progress Details, the speed graph compared with the status bar; Add: Alt+N, Add URL with a bad address, a 404 address, the two-step Add URL dialog and Esc, Add links with a numbered pattern, Import container (bad .dlc), torrent upload to the mock provider (scripted walk), magnet, import list; Details and packages: job details tabs, package status dialog on the phone, Properties, Redownload and Move/Rename found only inside details; Extractions view, archive password notice and dialog, extraction completing after the password (scripted walk); Queues and scheduler: queue list, new schedule form (phone), new queue and Files dialogs (scripted walk); Options: every page; OK/Cancel/Apply; invalid values with Apply and with OK from another page; Back with unsaved changes; site login and proxy passwords (scripted walk); change password with a wrong current password; pairing code countdown; Folder browser: Up at the top folder, a path outside the allowed folders, a Linux-style path, a missing folder inside the allowed folder, recent and allowed lists; Paired apps: pair, limited token, revoke (scripted walk); pairing code counts down; Host switcher: add a host (bad and good address), switch to the second host, first password setup there, that host's own switcher, the phone header; Keyboard only: Tab order across the header, toolbar, column headers, grid, speed graph and sidebar; grid arrows, Shift+Arrow, Space, Home/End, Ctrl+A, Enter, Delete, Ctrl+F; focus inside dialogs; Esc; Touch: phone target sizes measured on the list, Options, Queues and My TorBox; phone drawer, toolbar and More menu; Notices and errors: server killed mid-session (header, list, actions after 3 s and 15 s), restart and reconnect (about 1 s, still signed in), wrong password, lockout, invalid Options values, folder picker errors, container error, 404 file info; My TorBox: tabs, sort, Check now disabled state, item expand, no right-click menu (as D2d says); Compared with the desktop window from its source (MainWindow.xaml, SelectionCommands, SettingsWindow pages, row menu items)

Not covered: Linux folder paths against a Linux server (UI.B17 as the owner saw it): the test server ran on Windows; the owner's Docker test VM was not used; Desktop window in remote mode (UI.B16 black folder chooser, UI.R26 in the desktop, UI.R27 Web UI setting): out of scope for this web UI walk, already reported; Firefox and Edge extension pairing (EXT.B6): already reported; I loaded no extension and wrote no native-host registration; Real TorBox account: only the mock TorBox was used; Light mode: deferred by the owner; Screen reader output (NVDA/Narrator): only DOM roles, names and focus were checked; Real touch gestures (long press, swipe, pinch) and real mobile browsers: phone size was emulated in desktop Chromium; Large lists (hundreds of rows), scrolling performance, many hosts in the switcher; A schedule actually starting or stopping at its time; queue actions over time; Session expiry, the same session in several tabs, a server on another machine over plain http (setup code and plain-http warning paths); Pairing a desktop app to the server; container formats other than a broken .dlc (no valid .rsdf/.ccf sample)

### U-WU-1 (high, usability): After switching to another host there is no way back: each host's web UI keeps its own list, so host B doesn't know host A (more of UI.R26)

- Area: Host switcher (header)
- Steps: 1. On http://127.0.0.1:18851, open the host button in the header and choose 'Add a host…'. 2. Name it 'Second box' with address 127.0.0.1:18852, then Add. 3. Choose 'Second box' in the switcher. 4. Set the password or sign in on B. 5. Open B's host switcher.
- Expected: As in the desktop's Servers menu: one list of my servers that is the same wherever I am, so I can go back to A with one click. The sign-in page of the other host should also say which server it is and offer a way back.
- Actual: B's switcher shows only 'This host 127.0.0.1:18852' and 'Add a host…'. A is missing, and the popup itself explains 'This list is kept in this browser for this web UI address. Another host's web UI keeps its own list.' B's sign-in and setup pages have no switcher and no link back. The only way back is the browser's Back button or typing A's address again. The list is also lost when the same server is opened under another name (localhost versus 127.0.0.1). The popup has no menu roles.
- Evidence: shots\host-switcher-two.desktop-dark.png (A lists B); shots\host-b-landing.desktop-dark.png (B's setup page, no way back); shots\host-b-switcher.desktop-dark.png (B lists only itself); webui\src\lib\hosts.ts header comment ('The list lives in this browser, for this web UI's address only')

### U-WU-2 (medium, bug): In the 'Waiting for TorBox' view at 1360 px the toolbar overflows and the More button is off-screen

- Area: List toolbar
- Steps: 1. Desktop window 1360x820. 2. Sidebar, choose 'Waiting for TorBox'. 3. Look for More (Sort by, Simultaneous Downloads, Connections, Speed Limit, Clean Up) at the right end of the toolbar.
- Expected: More stays visible, or buttons fold into it, as in every other view.
- Actual: Adding 'Check now' makes the toolbar 1226 px wide in a 1150 px area. More moves to x 1365-1429, past the 1360 px window, and can only be reached by scrolling the toolbar sideways, with no sign that it scrolls. The screens check doesn't catch this because it only looks for overlapping buttons.
- Evidence: DOM: view all tbW 1150 / scrollWidth 1150, More at 1281-1345; view remote tbW 1150 / scrollWidth 1226, More at 1365-1429 (vw 1360). screens\25-waiting-at-provider.desktop-dark.png; shots\toolbar-more-offscreen.desktop-dark.png

### U-WU-3 (medium, usability): When the server stops, the list keeps showing old values as if live, and the only sign on the desktop layout is small 'Reconnecting…' text

- Area: Connection loss
- Steps: 1. Open the list while a download is running. 2. Stop the server process. 3. Watch for 15 s and try Add URL. 4. Start the server again.
- Expected: A clear banner above the list (as the phone layout has): 'Can't reach the server since 05:27, retrying'. Rates and time left blanked or greyed so they are not read as live, and a reason on disabled buttons.
- Actual: At 3 s and 15 s the Holiday row still says 'Downloading · 2/3, 15h 33m, 18.4 KB/s'. The status bar still says '1 active · 18.4 KB/s'. The only change is the 12 px header text 'Reconnecting…' and a greyed toolbar. The toolbar's Add URL silently does nothing (the click times out), while the sidebar's Add URL still looks enabled. The speed graph label changes to 'no data'. Reconnect after restart was quick (about 1 s, still signed in).
- Evidence: shots\server-stopped-3s.desktop-dark.png, shots\server-stopped-15s.desktop-dark.png, shots\server-restarted.desktop-dark.png; t9 log: 'add click failed locator.click: Timeout'; App.svelte:136 (status text hidden on phones; the phone gets its own banner at App.svelte:147)

### U-WU-4 (medium, bug): A path outside the allowed folders silently opens the download folder, with no message

- Area: Folder browser
- Steps: 1. Options, Save to, Browse… 2. Type C:\Windows in the path box and press Check (or Enter).
- Expected: A message such as 'C:\Windows is outside the folders this server may use', with the typed path kept.
- Actual: The picker jumps back to the download folder and replaces the typed text. No error is shown, so it looks as if Check ignored the path. A Linux-style path gets 'Give the folder's full path.' (on this Windows server, which relates to UI.B17). A missing folder inside the root correctly says 'That folder doesn't exist.'
- Evidence: shots\folder-picker-outside-root.desktop-dark.png (box shows the download folder after typing C:\Windows); shots\folder-picker-linux-path.desktop-dark.png; shots\folder-picker-missing-inside-root.desktop-dark.png

### U-WU-5 (medium, usability): Long server paths are cut off at the end, hiding the part that matters; Recent entries look identical

- Area: Folder fields (Add URL, Options, folder browser)
- Steps: 1. Add URL, Next: look at Destination folder. 2. Options, Save to, Browse…: look at Recent and Allowed folders.
- Expected: Show the end of the path (the folder name), or shorten the middle; a tooltip with the full path.
- Actual: The Destination folder box shows 'C:\Users\kaank\Downloads\idm\.claude\worktre…' (on the phone 'C:\Users\kaank\Downloads\idm\.claude\'). The two Recent entries both read 'C:\Users\kaank\Downloads\id…' and can't be told apart; only a hover title reveals ...\Compressed and ...\Programs, and phones have no hover. The current-folder line in the picker is cut the same way.
- Evidence: screens\18-add-url-info.desktop-dark.png and .phone-dark.png; screens\42-options-folder-picker.desktop-dark.png; DOM titles of the Recent buttons end in \Compressed and \Programs while the visible text is identical

### U-WU-6 (medium, bug): When the Add URL dialog moves to its second step, focus falls to the page; Esc stops working and Tab starts over

- Area: Dialogs (keyboard)
- Steps: 1. Alt+N (or Add URL). 2. Type an address and press Enter or Next. 3. Press Esc.
- Expected: Focus moves into the 'Download file info' step (for example the File name box), and Esc closes the dialog, as on the first step.
- Actual: The focused element is BODY after Next. Esc does nothing until the user Tabs into the dialog. The same happens after clicking the dark backdrop of Add links. The Esc handler is on the dialog's wrapper (Dialog.svelte:32, onkeydown) and focus is set only when the dialog first opens (Dialog.svelte:19-20), so a step change or a click outside leaves Esc without effect.
- Evidence: t11/t12 output: 'focus after Next {tag:BODY}', 'Esc #1 -> dialogs 1', then Esc with focus in a field -> dialogs 0; shots\add-url-esc.desktop-dark.png

### U-WU-7 (medium, bug): On a phone, Back with a dialog open changes the view behind it and leaves the dialog open

- Area: Phone navigation
- Steps: 390x844: 1. Menu, Finished. 2. Tap a row (its details open full screen). 3. Press the browser or Android Back.
- Expected: Back closes the dialog and stays on Finished, as phone users expect.
- Actual: The address goes from #/finished to the previous view ('') and the details dialog stays on top. The Menu button can't be pressed because the dialog still covers the page.
- Evidence: t20 output: 'after Back: hash  dialogs 1'; shots\phone-back-with-dialog.phone-dark.png

### U-WU-8 (medium, usability): Phone targets are well under touch size: row checkboxes are 15x15, Expand 18x18, row Actions 28x28, header buttons 28x28

- Area: Touch targets (phone)
- Steps: Measure the visible buttons, links and inputs at 390x844 on the list, Options, Queues and My TorBox.
- Expected: About 44x44 px (at least 24 px with spacing, WCAG 2.5.8), especially for row selection, which is the only way to multi-select on a touch screen.
- Actual: Every target found is under 32 px in at least one direction. Select all and every row checkbox are 15x15; the package Expand is 18x18; 'Actions for …' is 28x28 and drawn as a faint '···'; Menu, Theme and Sign out are 28x28; the host switcher is 48x28; toolbar buttons are 44x30; sidebar links and Options OK/Cancel/Apply are 30 tall; queue Move up/down are 31x30. A tap on the row opens details, so selecting several rows means hitting the 15 px boxes.
- Evidence: t6 output (list: 56 targets, all under 32 px; 'Select documents.zip 15x15', 'Expand 18x18', 'Actions for … 28x28'); screens\03-list-all.phone-dark.png

### U-WU-9 (medium, usability): The phone toolbar is a sideways-scrolling strip of unlabelled icons

- Area: List toolbar (phone)
- Steps: 390x844, list view: look at the row under the header.
- Expected: Labels, or the few main actions with the rest under More; a visible sign that the strip scrolls.
- Actual: 14 icon-only buttons (44 px each, about 616 px in total) in a 390 px strip, with no labels or tooltips on touch. Icons such as the key (Archive password), the box (Extract / Extract archive…) and the calendar are hard to guess, and Add URL appears both here and in the drawer. More is reached only by scrolling the strip.
- Evidence: shots\phone-header.phone-dark.png; screens\09-toolbar-more-menu.phone-dark.png

### U-WU-10 (medium, parity): The row menu lacks the desktop's Move/Rename…, Redownload, Change Category…, Refresh Download Address… and Start Now; Change category can't be done for several rows

- Area: Desktop parity: row menu
- Steps: Right-click a finished download (report.pdf) or an unfinished one in the list.
- Expected: The same menu as the desktop window, where SelectionCommands and the row menu offer Open Folder, Move/Rename…, Redownload, Change Category…, Refresh Download Address… and Start Now.
- Actual: The web menu has Copy path, Copy folder path, Resume, Stop, Add to/Remove from Queue, Priority, Delete…, Progress Details…, package items and Properties. Redownload, Move/Rename… and Refresh download address… are only in the details dialog's buttons; category is only in single-row Properties. webui\src\screens\ChangeCategory.svelte exists for several downloads, but nothing imports it, so it can't be reached.
- Evidence: screens\06-row-menu.desktop-dark.png; ListScreen.svelte:605-640 (menu items); JobDetails.svelte:401-409; desktop Item("Move/Rename…"), Item("Redownload"), Item("Change Category…"), Item("Refresh Download Address…"), Item("Start Now") in src\DownloadManager.Desktop.WinUI; grep shows no import of ChangeCategory

### U-WU-11 (medium, parity): Numbered URLs (img[1-3].jpg) are not expanded, unlike the desktop and CLI batch/numbered URLs

- Area: Add links
- Steps: Add menu, Add links: paste http://127.0.0.1:18850/file/img[1-3].jpg?size=1000.
- Expected: Three links (img1, img2, img3), as the desktop batch dialog makes, or at least a note that patterns aren't supported here.
- Actual: '1 of 1 selected. img[1-3].jpg Ordinary link': the pattern would be requested as one literal address.
- Evidence: t13 output; shots\add-links-pattern.desktop-dark.png; CLAUDE.md lists 'batch/numbered URLs (CLI + WinUI)'

### U-WU-12 (medium, usability): Options shows only the first invalid value, doesn't mark the field, and doesn't say which page it is on

- Area: Options validation
- Steps: 1. Options, Connection: Maximum simultaneous downloads = 0, Connections per download = 999. 2. Apply. 3. Go to the General page and press OK.
- Expected: Every problem listed, each field marked (aria-invalid, red border), and the page with the problem marked in the Options list; ideally min/max on the number boxes.
- Actual: Only 'Maximum simultaneous downloads must be between 1 and 16.' is shown, at the bottom beside the buttons. 999 connections isn't mentioned. Zero fields have aria-invalid. On General the same message is shown with no hint that it belongs to Connection. On the phone the message squeezes into a narrow column beside OK/Cancel/Apply. OK correctly refuses to close.
- Evidence: t15 output ('invalid fields 0'); shots\options-invalid-apply.desktop-dark.png; shots\options-invalid-ok-other-page.phone-dark.png

### U-WU-13 (medium, bug): After repeated wrong passwords, two countdowns disagree, and the red one doesn't count

- Area: Sign-in
- Steps: Sign-in page at 390x844 (after a few wrong tries): type a wrong password and press Enter.
- Expected: One message with one live countdown, and Sign in disabled until it ends.
- Actual: 'Too many wrong tries. Wait 4 s and try again.' (red, static) above 'Wait 3 s before trying again.' (grey). After 7 tries: 'That password is wrong. Wait 8 s before trying again.' The password box is also emptied on every try.
- Evidence: shots\signin-wrong-password.phone-dark.png; shots\signin-many-wrong.desktop-dark.png; t2 output

### U-WU-14 (low, bug): The speed graph's label and the status bar show different speeds at the same moment

- Area: Speed graph and status bar
- Steps: Keep a throttled download running (TestServer /slow/…?bps=4096) and compare 'Speed graph X' with the status bar.
- Expected: The same 'now' figure in both places.
- Actual: For example the graph label says 0 B/s while the status bar says 1.49 MB/s, or 6.82 KB/s against 1.30 MB/s, or 1.26 MB/s against 10.2 KB/s. The graph samples once a second and the status bar shows the newest job rate. The rate itself jumps between about 2 MB/s spikes and about 18-35 KB/s for a download throttled to about 32 KB/s, which may be partly the test server's bursts.
- Evidence: screens\06-row-menu.desktop-dark.png (0 B/s vs 1.49 MB/s); screens\35-options-connection.phone-dark.png (6.82 KB/s vs 1.30 MB/s); screens\44-my-torbox-tab-1.phone-dark.png (1.26 MB/s vs 10.2 KB/s)

### U-WU-15 (low, wording): The container dialog offers .dlc, then refuses it with a long policy paragraph and advice that makes no sense on a server

- Area: Import container
- Steps: Add menu, Import container file: choose a file named bad.dlc.
- Expected: Either don't list .dlc as accepted, or one short line ('DLC files can't be opened; use the program that made it'). Keep the chosen file name, and use a styled file button like the rest of the UI.
- Actual: The label says '(.rsdf, .ccf or .dlc)'. After choosing the file, a five-line red paragraph explains the online unlocking service and 'Impersonating … isn't acceptable', and suggests sending the file to another download manager 'if it is running', which is meaningless when the server is a remote box. The native 'Choose File / No file chosen' control is reset, so the user loses which file was rejected.
- Evidence: shots\container-bad.desktop-dark.png

### U-WU-16 (low, wording): A 404 shows the raw .NET exception text

- Area: Add URL file info
- Steps: Add URL: http://127.0.0.1:18850/gone/x.iso, Next.
- Expected: 'The server says this file doesn't exist (404).'
- Actual: 'File information unavailable: Response status code does not indicate success: 404 (Not Found). You can still start the download.'
- Evidence: shots\add-url-404.desktop-dark.png; t10 output

### U-WU-17 (low, usability): Change password has no 'repeat new password' box, and it applies at once inside a page that has OK/Cancel/Apply

- Area: Options, Sign-in and browsers
- Steps: Options, Sign-in and browsers: fill in Current password and New password.
- Expected: A repeat box like the setup page ('The same password again'), so a typo can't lock the owner out; and clarity that Change password doesn't wait for OK.
- Actual: Only Current and New. 'Change password' acts immediately and signs out other browsers, while the footer's OK/Cancel/Apply suggest a draft. A wrong current password gives a clear message ('The current password is wrong.').
- Evidence: screens\41-options-sign-in-and-browsers.desktop-dark.png; t18 output: pw inputs ['pw-current','pw-new']

### U-WU-18 (low, usability): Tabbing into the download list shows no focus mark until an arrow key is pressed

- Area: Keyboard focus
- Steps: Tab from the column headers into the list (role=grid) with nothing selected.
- Expected: A visible focus ring on the grid or on its first row.
- Actual: The grid has focus (outline none) and nothing on screen changes; pressing ArrowDown then selects and marks the first row. Menu items have only a faint hover-style highlight. Other controls show a clear 2 px ring.
- Evidence: shots\kbd-grid-focus.desktop-dark.png; t4/t5 focus dumps (DIV[grid] outline none)

### U-WU-19 (low, visual): The host switcher and the theme button use the same monitor icon; on the phone the switcher is only an icon

- Area: Header
- Steps: Look at the right end of the header at 1360 and at 390 px.
- Expected: Different icons (for example a server icon for hosts), and the host name or address shown on phones too.
- Actual: Both buttons show a monitor icon side by side. At 390 px the switcher is a monitor plus an arrow with no text, so nothing says which server you are on, which adds to UI.R26.
- Evidence: shots\phone-header.phone-dark.png; App.svelte themes[0].icon 'monitor'; HostSwitcher.svelte Icon name='monitor'

### U-WU-20 (low, parity): Columns can't be resized or hidden, so Description is cut, and the Extractions view shows empty download columns

- Area: List columns
- Steps: Look at the Description column at 1360 px; open Extractions.
- Expected: Resizable or hideable columns with the layout remembered, as the desktop window does.
- Actual: Description is cut ('TorBox reports 50%,…', 'Extraction complete…'). In Extractions the Q, Size, Status, Time left and Transfer rate columns are always empty. No column resize code exists in the web list.
- Evidence: screens\03-list-all.desktop-dark.png; screens\26-extractions.desktop-dark.png

### U-WU-21 (low, wording): Three different date formats, and inconsistent ellipses in the Add menu

- Area: Wording consistency
- Steps: Compare the list's Last Try Date, the details 'Added' line and Statistics; open the Add menu.
- Expected: One date format; '…' on items that open a dialog, as in the desktop ('Add Links…').
- Actual: List: 'Nov 09 05:19:47 2026'. Details: 'Nov 9, 2026, 05:19 AM'. Statistics: '11/9/2026, 5:22:02 AM'. The Add menu has 'Add links', 'Import torrent file' and so on without '…'; the desktop uses 'Add Links…'.
- Evidence: screens\14-job-details.desktop-dark.png; screens\48-statistics.desktop-dark.png; screens\16-add-menu.desktop-dark.png

### U-WU-22 (low, visual): On the phone the speed graph takes about 130 px by default, the drawer has no backdrop or close button, and rows don't show time left

- Area: Phone layout
- Steps: 390x844: open the list; open the Menu drawer.
- Expected: Speed graph collapsed by default on small screens; a dimmed backdrop behind the drawer; time left visible on active rows.
- Actual: The open speed graph takes about 15% of the height. The drawer slides over the list with no dimming and closes only by choosing an item or tapping elsewhere. Active rows show status, size and rate but no time left.
- Evidence: screens\03-list-all.phone-dark.png; shots\touch-drawer.phone-dark.png

## Browser extension

I tested the browser extension (0.5.3) from a detached worktree at C:\Users\kaank\Downloads\idm\.claude\worktrees\ut-extension, built from dev 6b8c980. The setup was a test Host (instance ut-ext-host), a local test DownloadManager.Server on 127.0.0.1:19480 with the web UI built, and TestServer on :19481. Chrome was the headless Playwright build of Chrome for Testing 158, because the cft2 copy named in CLAUDE.md was empty and the old cft copy was being changed while I looked. I registered the test native host for Chrome and removed it at the end. Firefox 159 ran headless with BiDi and a fresh profile, using the installed app's Firefox key read-only, as CLAUDE.md describes.

The most serious finding is in "Ask each time". While the save window for one download is open, starting a second caught download makes the first one finish in the browser anyway. If the user then clicks Download, the file is saved twice. The browser's copy is also removed from the browser's download list, so the user never sees it. This happened in about 8 of my downloads and I reproduced it on purpose twice. When only one window was open, the download stayed held correctly.

Other notable problems:
- Deleting an unfinished download from the popup leaves a full-size .part file behind.
- With a server paired, the popup's buttons are pushed below its 600 px limit.
- When the paired server is down, the popup still says "Connected" and "Automatic sending: on".
- There are two settings checkboxes that look like they do the same thing.
- Changes in Settings are lost without warning if the tab is closed or reloaded, and an older Settings tab can overwrite newer saves.
- The Start choice made for one download quietly becomes the default for all later ones.
- A "Download later" item in the popup has no Start button.

Pairing, the This PC / Server switch, the server folder browser, pause/resume/delete on both places, pause catching, the badge, unpair and the pairing error messages all work. Firefox (EXT.B6): it connected to the test Host, caught a download from its response and paired with the loopback test server using the same steps as Chrome, so I could not reproduce "no answer from host" with a 127.0.0.1 server.

All processes I started are stopped by PID: TestServer 34596, Server 21296 and 51320, Host 50032 and 40012 (the one the native host restarted), Chrome 38864 and its restart (closed through DevTools), and Firefox 51900/43936 and 36892/36904 (closed through BiDi). The test Chromium registration is removed and the key no longer exists. A few test files the browsers saved into C:\Users\kaank\Downloads were moved to artifacts\usability\extension\stray-from-browser. Nothing was committed.

Covered: Builds: dotnet build DownloadManager.slnx -c Release; webui npm ci + build; extension dist and dist-firefox (0.5.3); Settings page: first open, dark screenshots, the two automatic toggles and their banners, start mode, folder mode (default / ask), file and site rules shown, Save, unsaved changes lost on reload, several open Settings tabs overwriting each other; Pairing in Settings: cleartext note, wrong code, server not answering, bad address, ftp:// address, empty address, successful pairing, Check connection, unpair with the server up and with it down, the automatic target select; Popup: connected state, empty list, live progress/speed/ETA, Pause/Resume/Delete with the 'Delete the file too' confirmation for This PC and Server jobs, server card, This PC/Server switch (it also updates Settings), server-down state, Left in your browser card, Pause catching and Resume, badge text and title (OFF / paused), automatic sending off via either toggle, overflow at the 600 px height limit; Automatic catching to the test Host (default folder) and to the server; jobs created and files on disk checked; Ask-each-time window: one place and two places, the local folder dialog (opened in the test Host; checked with UIA, closed with WM_CLOSE), the inline server folder browser (Up at the roots, choosing a subfolder, Use this folder, Cancel), a Linux path and a path outside the roots on the server, renamed file, Start choice carrying over, success message and auto-close, the server-down message in the window; Context-menu path: the same window opened through contextMenus.onClicked dispatch (shows Cancel instead of Keep in browser), sent with Download later; Download all links: link list, filter and select-all present, This PC/Server switch, server folder browse, sending 2 of 4 links to the server; the error when started from a non-web tab; The test Host killed: the native host restarted it and the popup reconnected without the user noticing; Firefox 159 headless via BiDi: Settings and popup in dark, Check connection to the test Host through the installed native host, automatic catching from the response (slow file, shown downloading in the popup), pairing with the server through a real pointer click, ask window with Server preselected, Keep in browser; Wording and consistency checked across settings, popup, ask window and Download all

Not covered: A real right-click menu: headless Chrome cannot show context menus, so I opened the context-menu window by dispatching contextMenus.onClicked in the service worker.; The real popup window and its size, and the window sizing done by fitWindowToContent: headless has no toolbar or popup windows. I opened the popup and ask windows as tabs with emulated sizes (390x600, 560x640-900). The duplicate-file finding should be confirmed once in headed Chrome.; Desktop notifications: headless Chrome and Firefox do not show them. I only read the code paths in background.ts.; The 'desktop app not installed' state: removing the Chromium test key would have made Chrome for Testing fall back to Chrome's key, which belongs to the installed app, so I put the key back at once without testing it. That message was only reviewed in connection-problem.ts.; 'Open app' / 'Open Inbound Download Manager': the bin build has no WinUI next to the Host. The test Host showed 4 'desktop frontend was not found' message boxes (closed with WM_CLOSE); that comes from the dev build layout, not the product.; Edge, Chrome stable and installed-store builds; the Chrome Web Store ID path.; Pairing with a server on a LAN address or a Linux/Docker server, so UI.B17 (Linux folders) could not be tested here: this server runs on Windows with a Windows root. A '/downloads/movies' path was correctly refused as outside the roots.; EXT.B6 in depth (another agent is on it). Firefox only paired with http://127.0.0.1 here.; Holding Delete while clicking a link (needs real key input on the page); clipboard and media detection.; Light mode (deferred by the owner).

### U-EX-1 (high, bug): A second caught download makes the first held download finish in the browser; Download then saves the file twice, and the browser's copy is hidden

- Area: Ask-each-time window (Chrome)
- Steps: 1. Settings: 'When sending a download' = Ask each time, Save. 2. On a page, click a small download link (http://127.0.0.1:19481/file/hold3.bin?size=100000). The ask window opens, and chrome.downloads shows the download in_progress, paused, with no file name (held). 3. Leave the window open and click another download link (multi1.bin). 4. Look at chrome.downloads, then click Download in the first window (This PC).
- Expected: The first download stays held until the user picks Download or Keep in browser in its own window. Only one copy of the file is made.
- Actual: Right after the second download starts, the first one becomes state 'complete' (paused:true) in the browser's folder, while its window still says it is waiting. Clicking Download sends it to the app as well, and the app saves 'hold3 (1).bin' next to the browser's 'hold3.bin'. removeAcceptedBrowserDownload then erases the browser's entry, so the extra copy is invisible in Chrome's download list. Earlier in the session the same thing silently left ask1.zip, srv1.iso, rep0-2.bin, page, page (1) and whendown.bin in C:\Users\kaank\Downloads, the browser's folder at the time. The browser's copies of ask1.zip and srv1.iso duplicated files already sent to the app and the server (ask1.zip was renamed to renamed-ask1.zip in the window); the others stayed in the browser while their windows were still open. All were moved to artifacts\usability\extension\stray-from-browser. Seen in headless Chrome for Testing 158; one check in headed Chrome is advised.
- Evidence: s/32.mjs then s/33.mjs output: before = [15,'',in_progress,paused,100000]; after the second download = [15,'hold3.bin',complete,paused]. test-downloads then held 'hold3.bin' and 'hold3 (1).bin', both 100000 bytes. Screenshot shots/34-held-window-after-browser-finished.png. A slow file held alone stayed at 0 bytes for 10 s (s/31.mjs), so the hold works when only one download is waiting.

### U-EX-2 (medium, bug): Deleting an unfinished download leaves a full-size hidden .part file in the download folder

- Area: Popup / delete
- Steps: 1. Catch a slow download (/slow/catch2.bin?size=8000000&bps=15000) to This PC. 2. In the popup, click Delete on it, leave 'Delete the file too' unticked, and confirm Delete.
- Expected: Removing an unfinished download also removes its partial data, or the confirmation says the partial file will be kept.
- Actual: The job leaves the list, but test-downloads still holds 'catch2.bin.57404b8d4b4c4324816b41f1ad5b1b7e.part' (8,000,000 bytes, allocated at full size) with no job referring to it. It was still there at the end of the session. With the box ticked, a server job's part file was removed correctly.
- Evidence: ls of artifacts\usability\extension\test-downloads before and after s/05.mjs; shots/03-popup-delete-confirm.png

### U-EX-3 (medium, usability): With a server paired, the popup's buttons are pushed out of view

- Area: Popup
- Steps: Pair a server, have three or more downloads, and open the popup (Chrome limits it to 600 px; body max-height is 600px).
- Expected: Download all links, Open app, Settings and Pause catching stay visible. Only the download list scrolls.
- Actual: The page is 737 px tall inside a 600 px popup. The button row starts below the bottom (#pauseCatching bottom = 737). There are two nested scrollbars (the 210 px job list and the body). The newest server download (srv1.iso) is listed last, under all This PC jobs, so it is hidden even after scrolling the list.
- Evidence: s/15.mjs: {bodyH:600, scrollH:737, overflow:auto, pauseBottom:737}; shots/15-popup-600-viewport.png, shots/20-popup-server-job.png

### U-EX-4 (medium, usability): When downloads go to a server that is down, the popup still says 'Connected' and 'Automatic sending: on'

- Area: Popup / server unavailable
- Steps: 1. Pair the server and switch the popup to 'Server'. 2. Stop the server. 3. Open the popup.
- Expected: The header and the automatic-sending line say that caught downloads cannot be sent right now, because the place they go to is not answering.
- Actual: The header says 'Connected' and the desktop card shows a green 'Connected to the desktop app' with 'Downloads that match your rules go to the server'. 'Automatic sending: on' is green. Only the separate server card says 'Server not answering'. The badge and title stay 'catching downloads'. The server's downloads drop out of the list without explanation.
- Evidence: s/25.mjs output; shots/25-popup-server-down.png; s/26.mjs: badge '' and title 'Inbound Download Manager: catching downloads'

### U-EX-5 (medium, usability): Two checkboxes look like one setting: 'Allow automatic sending' and 'Automatically send downloads to the desktop app'

- Area: Settings page
- Steps: Open Settings. Untick either checkbox and Save, then open the popup.
- Expected: One clear on/off switch for automatic sending, or clearly different labels.
- Actual: Either one gives the same banner ('Automatic sending is off: …') and the same OFF badge. The popup differs: with the first one off it hides the connection state ('Automatic off' header, amber dot). With the second one off it shows 'Connected'. The second label also says 'to the desktop app' even when downloads go to the server. 'Turn on in Settings' in the popup does not say which box to tick.
- Evidence: s/37.mjs output; shots/37-popup-enabled-off.png, shots/37-popup-auto-off.png, shots/01-options-first-open.png

### U-EX-6 (medium, usability): Unsaved changes are lost silently, and an older Settings tab overwrites newer settings

- Area: Settings page
- Steps: A) Change 'When sending a download' and reload or close the tab without clicking Save. B) Keep an older Settings tab open, change the Start mode in an ask window or switch the target in the popup, then click Save in the older tab.
- Expected: A warning about unsaved changes, or saving each change at once. An open Settings page shows changes made elsewhere.
- Actual: A) After reload the old value is back with no warning (s/40.mjs: 'default -> default'). B) Saving from a stale tab put back destinationMode 'ask' and startMode 'later', undoing a later save. This is how I ended up in ask mode by accident during the walk.
- Evidence: s/12.mjs: two Settings tabs showed 'now ask' and 'later ask' at the same time; s/28.mjs: stored settings went back to destinationMode 'ask' after the stale-tab save in s/22.mjs

### U-EX-7 (medium, usability): The Start choice for one download quietly becomes the default for every later download

- Area: Ask-each-time / context menu window
- Steps: In an ask or right-click window, set Start = 'Download later' and click Download. Then open Settings, or catch the next download.
- Expected: A choice in a one-download window applies to that download only, like the folder does.
- Actual: Settings now shows 'Download later' as the default start mode, and the next ask windows open with 'Download later'. Only a sentence in the long hint paragraph mentions it.
- Evidence: s/11.mjs: 'options startMode now: later'; shots/16-handoff-two-targets.png shows Download later preselected

### U-EX-8 (medium, usability): A 'Download later' item in the popup can be paused or deleted but not started

- Area: Popup
- Steps: Send a download with Start = Download later, then open the popup.
- Expected: A Start (or Resume) button for a queued item.
- Actual: all-b.bin shows 'Queued · 0 B / unknown size' with only Pause and Delete. The user has to open the app to start it.
- Evidence: s/11.mjs and s/14.mjs output; shots/11-popup-queued-later.png

### U-EX-9 (low, usability): Finished downloads cannot be opened or shown in their folder from the popup

- Area: Popup
- Steps: Open the popup after a download completes.
- Expected: Open and Show in folder for completed items, as the app's own list has.
- Actual: The only action on a completed item is Delete.
- Evidence: shots/04-popup-downloading.png (catch1.bin Completed - Delete only)

### U-EX-10 (low, wording): The desktop-app card describes the server target, and 'automatic sending is off' is said twice

- Area: Popup wording
- Steps: Switch the popup to Server, or turn automatic sending off.
- Expected: Each card describes its own place, and each state is said once.
- Actual: The 'Connected to the desktop app' card says 'Downloads that match your rules go to the server'. With automatic sending off, the status card says 'Automatic sending is off' and the card below repeats 'Automatic sending: off'. 'Open the web UI' wraps onto two lines in the server card.
- Evidence: shots/14-popup-paired-server.png, shots/37-popup-enabled-off.png

### U-EX-11 (low, usability): With the server down, the ask window still preselects the server

- Area: Ask-each-time window (server)
- Steps: Default target = server, stop the server, and catch a download with Ask each time.
- Expected: Server is shown as unavailable (or not preselected), so Download is not one click away from failing.
- Actual: Server is selected, Download is enabled, and the red 'not answering' text is under the buttons. The user has to notice it and switch to This PC by hand. The folder field shows the placeholder 'The server's default folder'.
- Evidence: shots/29-handoff-server-down.png

### U-EX-12 (low, usability): The server folder browser has no new-folder button, and a stale error stays on screen

- Area: Server folder browser (ask window)
- Steps: Choose… with Server selected. Type an outside path, click Download (error shown), then pick a valid folder with the browser.
- Expected: A way to make a subfolder, and the old error cleared once a valid folder is picked.
- Actual: No New folder. The 'That folder is outside the folders this server may use.' error stays visible until the next Download click. The error does not name the allowed root.
- Evidence: s/19.mjs and s/20.mjs output; shots/17-server-folder-browser.png, shots/19-server-path-linux.png

### U-EX-13 (low, usability): Unpair acts at once with no confirmation

- Area: Settings page / unpair
- Steps: Settings > Inbound server > Unpair.
- Expected: A confirmation, because getting back needs a new code from the server's web UI.
- Actual: One click unpairs, and the target falls back to This PC.
- Evidence: s/35.mjs and s/40.mjs output; shots/35-unpaired-server-down.png

### U-EX-14 (low, wording): Download all from a non-web tab says 'Reload it and try again' and keeps 'Scanning...'

- Area: Download all links
- Steps: Use Download all links… while the active tab is an extension or browser page.
- Expected: A message that this kind of page has no links to collect, and no 'Scanning...' left showing.
- Actual: The window opens, the count still says 'Scanning...', and the bottom says 'This page cannot be scanned. Reload it and try again.' Reloading cannot help.
- Evidence: shots/42-batch-non-web-tab.png

### U-EX-15 (low, visual): Folder fields are too narrow to read the folder, and labels differ between windows

- Area: Download all links / ask window
- Steps: Open Download all links… or the ask window.
- Expected: The end of the path (the folder name) is visible, and the same words are used in both windows.
- Actual: In Download all, 'Destination folder (optional)' is about 140 px wide and shows only 'C:\Users\kaank\Down'. The ask window shows the start of the path and cuts off the folder name. The labels differ: 'Browse…' vs 'Choose…', and 'Destination folder (optional)' vs 'Save in'. 'Scanning...' and 'Unpairing...' use three dots where every other text uses '…'.
- Evidence: shots/38-batch.png, shots/10-context-handoff.png; batch.html, options.ts

### U-EX-16 (low, visual): Server buttons are spread across the card, and the floating Save button covers fields

- Area: Settings page
- Steps: Pair a server and scroll the Settings page.
- Expected: Check connection / Open the web UI / Unpair grouped together. Save does not cover form fields.
- Actual: The three buttons are pushed to the left edge, the middle and the right edge. The sticky 'Save settings' button floats over the textarea on a transparent background.
- Evidence: shots/13-paired.png, shots/02-options-viewport.png

### U-EX-17 (low, bug): Save once stayed on 'Saving…' with no result (not reproduced)

- Area: Settings page / save
- Steps: In a fresh Settings tab, Save, then straight away catch a download in another tab (s/21.mjs).
- Expected: 'Saved.' or an error.
- Actual: That tab still showed 'Saving…' minutes later. Eight later tries each finished in about 110 ms. Whether that save was stored could not be told, because a stale-tab save overwrote the settings afterwards.
- Evidence: s/22.mjs output: tab 627AB7 saveResult 'Saving…'

### U-EX-18 (low, parity): Firefox paired with the loopback test server and reached the test Host; EXT.B6 not reproduced here

- Area: Firefox (EXT.B6 observation)
- Steps: Headless Firefox 159 with dist-firefox installed through BiDi webExtension.install. Check connection; pair with http://127.0.0.1:19480 using a real pointer click; catch a download; Ask each time.
- Expected: Same as Chrome.
- Actual: Check connection said 'Connected to the desktop app.' Pairing gave 'Paired with the server at 127.0.0.1:19480 as "Firefox on Windows". It is answering.' A slow download was caught from its response and shown downloading in the popup. The ask window showed This PC/Server. With a script click (no user gesture), the raw browser error 'permissions.request may only be called from a user input handler' appears in the pairing result; a real click works. Keep in browser restarted the file into C:\Users\kaank\Downloads, not the profile's browser.download.dir.
- Evidence: shots/f01-ff-options.png, shots/f01-ff-popup.png, shots/f04-ff-pair-result.png, shots/f05-ff-handoff.png; s/f03.mjs and s/f04.mjs output
