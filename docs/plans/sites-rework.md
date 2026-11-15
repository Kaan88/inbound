# Sites UI rework: plan (ROADMAP SITES.11)

Status: plan accepted, 2026-11-16 (owner: "I accept all picks but don't start yet"). Nothing here is built. The nine
picks under "Decisions needed" are decided as picked. Work starts only when the owner says so.

Owner, 2026-11-16: the Sites UI in 0.8.0 is "a tacked on feature that is borderline unusable"; "backend seems sound
and it seems to work but ui is not finished"; "I agreed on mockups for the design USE THEM".

## What this is, and what it is not

- A rebuild of the Sites screens in the WinUI window and the web UI, against the agreed mockup.
- The Host side stays: `SitesCommands`, `SiteTransfers`, the rclone process, stores, the CLI. It gets only the few
  additions the screens need (listed under R1).
- The spec is `docs/plans/sites-mockups/4-agreed-design.html` (open it in a browser; the list on its left is the
  screen list). Pages 1 to 3 in the same folder are the earlier rounds and are used only where page 4 is silent
  (the file context menu is drawn on pages 1 and 3).

## Why the first build missed

1. The mockup pages were never in the repo. The build worked from the ten-line summary in `remote-sites.md`
   ("The agreed design in short"), so the structure is there and the screens are not.
2. Every "walked" note for SITES.3 to SITES.7 was a function check through UI Automation. Those rows say "Not
   checked: visuals". Nobody put a built screen beside its mockup screen.
3. Things the summary did not spell out were never built: a context menu, double click, what to do with a finished
   file, the look of a transfer row.

## Rules for this rework

These are what make the plan hold. A stage that breaks one is not done.

1. **The mockup is the spec, screen by screen.** Each stage names the mockup screens it builds (ids below, as in the
   page's address after `#`).
2. **Side by side before "done".** For every screen: a screenshot of the mockup and one of the built screen at the
   same window size, saved in `docs/plans/sites-mockups/compare/` as `<screen>-mockup.png` and `<screen>-built.png`.
   The owner sees each stage's pairs before the next stage starts. Dark mode; light mode at the end with the rest.
3. **Looked at, not only automated.** Each stage ends with a walk of the real window by sight (screenshots of the
   running app), on top of the UI Automation and Chromium walks. The stage note says what was looked at.
4. **The app's own parts, not new ones.** Transfer rows use the main list's row template, columns, status colours and
   progress bar. Menus use the main list's context-menu style. Dialogs use the existing dialog helpers. No new look.
5. **Mouse and keyboard both.** Every pointer action has a key: Enter, F2, Delete, F5, Shift+F10 or the menu key,
   Ctrl+A, Backspace for the folder above.
6. **No silent deviation.** Where the mockup cannot be built as drawn, or says nothing, it goes in "Decisions needed"
   and is asked, not guessed.
7. **Record as built, not as hoped.** ROADMAP says `partial` until the pairs exist and the owner has seen them.

## Where the built screens differ from the mockup

### Desktop window

| Mockup (page 4) | Built in 0.8.0 | Gap |
| --- | --- | --- |
| `browse`: pane with a header (icon, site name, kind chip), then a row with Up, breadcrumbs, Star and Filter, then Name / Size / Modified / On this PC, then a footer "1 of 6 selected · 5.8 GB · 2 already on this PC" | One bare list. Name and breadcrumbs share a strip, Up / Refresh / Star are big toolbar buttons, no kind chip (only "Not encrypted"), no filter, footer is a count | Pane chrome, filter, selection summary |
| File context menu (pages 1 and 3): Download, Download to…, Rename, Delete…, New folder, Copy address | None (`SitesPanel.xaml` has no `RightTapped` or flyout) | All of it |
| Double click | Opens a folder; on a file nothing (`ItemsList_DoubleTapped` only calls `OpenSelectedFolder`) | File action |
| Download "sends files to the site's folder or to your category rules, so there is nothing to aim at" | Always opens a dialog asking for folder and conflict rule | A dialog on every download |
| `browse`: view switch "Two panes / Server only" above the panes, with "Linked folders: D:\… follows /media/isos" | A toolbar button "Two panes"; no linked-folders line | Switch and line |
| `browse`: gutter between panes with Upload, Download and Linked buttons | Arrows exist (`MainWindow.TwoPanes.cs`); to be compared by sight | Check, restyle |
| This PC pane: same chrome as the server pane (breadcrumbs, star, footer) | A text box for the path, own layout, no context menu, double click opens folders only | Match the server pane |
| Drag between panes, and from Explorer | None | All of it |
| `transfers`: opened bar shows the main list's columns (File Name with a direction arrow, Size, Status with a progress bar, Time left, Transfer rate, Description); one row per action; a folder row opens to its files | Cards: title, route, progress bar, a sentence, and up to six buttons per row (Pause, Resume, Retry, Files, Cancel, Clear). No columns, no selection, no expand. "Files" opens a read-only dialog of lines | The whole list |
| Finished transfer | Nothing can be done with it except Clear | Open, Open folder, Show on the site |
| `transfers` tabs "Running and waiting (4) / Failed (1) / Done (24)", "Clear done", "Retry all" | Tabs and both buttons exist as toggle buttons; "Clear finished" | Restyle as the mockup's segmented tabs, wording |
| Slim bar: "Transfers ↓ 118.4 MB/s ↑ 38.2 MB/s 2 running · 2 waiting" | "Transfers" and one summary text | Speeds with arrows |
| Site toolbar: Download, Upload, New folder, Rename, Delete on server, Refresh, Pause transfers, Edit site, Options | Download, Upload, New folder, Rename, Delete on server, Pause transfers, Up, Refresh, Star, Two panes | Edit site and Options missing; Up, Star, Two panes belong in the pane |
| Menu bar gains "Site" | No Site menu | All of it |
| Sidebar: "All sites", then each site with a state dot, arrows with counts, a clock for waiting, a thin progress line | Sites with a text mark and a small progress bar; no dot, no "All sites" | Dot, All sites, compare marks by sight |
| `home`: All sites page (site, kind, folder on this PC, last used, activity), "Open an address once", "Add a site" | Does not exist. Sites are added in Options > Sites | All of it |
| `add-tiles`, `add-fields`: eight tiles, then four fields, Advanced closed, Test with a result line | One form with a "Kind" drop-down and every field; Test is a button in Options | All of it |
| `trouble`: in the pane, icon, "Can't reach …", why, Retry now, Edit site; waiting transfers say why | A text with "Try again" and "Open Options" | Restyle, Edit site, retry countdown |
| `delete`, `overwrite` dialogs | Exist; to be compared by sight | Check wording and layout |
| `downloads`: status bar chip "Sites · 2 running · 156 MB/s" | Chip exists | Check by sight |

### Web UI

| Mockup (page 4) | Built in 0.8.0 | Gap |
| --- | --- | --- |
| `web-download`: Download asks "To home-server (keeps going if you close this tab)" first, "To this device" second | No such dialog. Every file row has one download icon, and it is a plain browser link to this device (`SiteBrowser.svelte:342`). "Download to <server>" is a button that appears only after a row's box is ticked (`:275`) | The default is the wrong way round |
| `web-site`: site toolbar, sidebar with each site and its activity, Server only by default, pane chrome, column "On home-server", footer | One "Sites" link in the sidebar (`App.svelte:419`), a row of plain buttons, one list. Breadcrumbs and stars exist; no filter, no footer; the column shows only with a linked folder | Sidebar sites, toolbar, pane chrome |
| `web-two`: the server's own folders in a left pane, gutter arrows | Does not exist | All of it |
| Context menu, double click | None; a file name is plain text, a folder opens by clicking its name | All of it |
| Transfers: slim bar that opens, tabs with counts, rows with the main list's columns, a folder row that opens, Retry all, Clear done | A flat list of cards under the sites page (`SitesScreen.svelte:228`), "Clear finished", no tabs, no expand, no Retry all | The whole list |
| `web-upload`: "Files on home-server / Files on this device", drop zone | A file picker for this device with a progress list; no choice, no drop zone | Dialog as drawn |
| `home`, `add-tiles`, `add-fields` | A page with the switch, rclone controls and the site list; a form with a Kind drop-down; Test on the list rows; no "open an address once" | As on the desktop |
| `trouble`, `delete`, `overwrite` | An error notice with a button; delete text is right; "Replace X?" for uploads only | Trouble in the pane; overwrite as drawn |
| `phone`, `phone-sheets`: sites list with activity, folder with tick boxes and an action bar, Transfers as a bottom sheet, sheets for Upload / Delete / trouble | The same page made narrow; no sheets, no action bar | All of it |

### The two web bugs

**1. A download goes to the browser, where the extension catches it.** Cause found in the code, not yet reproduced:
the only download control on a file row is `<a href="/api/sites/device?..." download>` (`SiteBrowser.svelte:342`), an
ordinary browser download. Downloading to the server exists but is hidden behind ticking a row first. Nothing in
bridge mode or the session's rights changes this. Fix in R6: Download means the server; "To this device" is the
second choice in the dialog, as drawn. Until R6 there is nothing to patch separately, because the fix is the dialog.

**2. Google Drive does not list.** Cause not found. The web UI sends the same `sites.browse` the window sends, and
nothing on the way treats Drive differently. The one web-only suspect: the list is keyed and selected by file name
(`{#each items as item (item.name)}`, `SiteBrowser.svelte:325`), and Drive allows two items with the same name in a
folder, which Svelte does not accept as keys. Not reproduced. R6 starts by reproducing it with the owner's Drive site
in a test instance and reading the browser console and the `/api/ipc` answer; the fix follows what that shows. Rows
get a key that is not the name either way, and the window is checked for the same two-names case.

## Decisions (the mockup is silent or cannot be built as drawn)

All nine decided by the owner on 2026-11-16 as "my pick" below.

1. **Double click on a file.** (a) Download it at once to the site's linked folder, or by category rules when it has
   none, with no dialog. (b) Open it: download to a temporary folder and open with its Windows program. My pick: (a),
   with "Open" in the context menu for (b). Page 1 asked this and it was never answered.
2. **Download without a dialog.** The mockup says there is nothing to aim at. So Download (button, Enter, double
   click, arrow) starts at once and the conflict question appears only when a file exists; "Download to…" in the
   context menu asks for a folder. My pick: as the mockup.
3. **Context menu, server pane.** Download, Download to…, Open, then Rename, Delete on server…, New folder, then Copy
   address, Star this folder (on a folder). Items the site cannot do are hidden. **This PC pane:** Upload, Open, Show
   in Explorer, Rename, Delete (Recycle Bin), New folder. **Empty space:** New folder, Upload…, Refresh, Paste address.
4. **A finished transfer.** Double click opens the file (a folder: opens the folder). Context menu: Open, Open
   folder, Show on the site (goes to the server folder and selects it), Retry (failed), Remove from list. For an
   upload, Open and Open folder act on the local source.
5. **The "Rights" column** of the Server only view. rclone's listing does not report permissions for any kind
   (`operations/list` gives name, size, time, type). My pick: leave the column out and say so; the alternative is an
   SFTP-only column through a second request per folder.
6. **Per-file progress inside an opened batch row.** The Host reports speed and bytes for the batch and only a state
   for each file. Showing a bar and rate on each running file needs rclone's `core/stats` transfers mapped to items.
   My pick: do it (R1); it is what makes the row look like the main list's.
7. **"Open an address once"** on All sites. Needs a site that is not saved (lives until the window leaves it). My
   pick: build it in R4 as drawn; a password is asked only when the server wants one.
8. **Linux Uno window.** It shares these files, so it gets the rework, but Linux is deferred: built, not walked.
9. **Version.** Ship as 0.9.0 when R7 passes, or 0.8.x after each stage? My pick: one release at the end, with test
   builds for the owner after each stage.

## Stages

Each stage: build (Sonnet subagents for the routine parts, reviewed by the main model), unit tests for the rules,
the automated walk, the walk by sight, the screenshot pairs, then the owner looks before the next stage.

### R0. Baseline (no product code)

- A test instance with three local sites from `rclone serve` (SFTP, WebDAV, FTP) and seeded folders that match the
  mockup's rows (a folder of many files, a file that exists locally, an older local copy).
- A script that opens each mockup screen in Chromium at 1360 x 800 and saves `<screen>-mockup.png` (21 screens).
- Screenshots of every screen as built today, beside them. This is the "before" set and corrects the gap tables
  above where reading the code was wrong.
- Done when: the pairs are in `compare/` and the gap tables are confirmed or corrected.

### R1. What the Host must add (small; kept in the main model: IPC)

- Per-file progress in `sites.transfer.get` and in `sites.transfer.changed` for running files: bytes done and speed.
- Time left for a batch (from bytes and speed; computed in the shared rules, not in each UI).
- `lastUsedAt` on a site (set when it is listed or a transfer starts), for All sites.
- An unsaved site for "Open an address once" (decision 7).
- A local "open / reveal" for a finished transfer goes through the existing shell open and reveal that the main list
  uses (`IHostPlatform`); no new machine action.
- Protocol version stays 1; fields are additive. Contract tests for each.

### R2. Desktop: the browse view (`browse`, `remote-only`, `trouble`)

- `SitesPanel` rebuilt as the mockup's pane: header (icon, name, kind chip, "Not encrypted" chip), path row (Up,
  breadcrumbs, Star, Filter), columns, footer with the selection summary.
- `LocalPane` gets the same chrome, so the two panes read as a pair.
- The view switch row with the linked-folders line; the gutter as drawn.
- Context menus (decision 3), double click and Enter on a file (decision 1), Download without a dialog (decision 2).
- Drag and drop: between the panes, from Explorer onto the server pane, onto a folder row.
- Toolbar becomes the mockup's nine buttons; Up, Star and Two panes move into the pane. Menu bar "Site" with the
  same commands and their keys.
- Trouble state in the pane, with Retry now and Edit site.
- Tests: `SitesBrowseRules` (filter, selection summary, menu items by capability), UI Automation walk with the keys.

### R3. Desktop: transfers (`transfers`, `failed`, `downloads`)

- The opened bar is a list with the main list's row template and column header: File Name (arrow by direction,
  chevron on a batch), Size, Status with the progress bar, Time left, Transfer rate, Description (from, to, why).
- A batch opens to its files in place (the package pattern the main list already has). The "Files" button and its
  dialog go.
- Selection, with a context menu and keys: Stop, Resume, Retry, Remove from list, Open, Open folder, Show on the site
  (decision 4). Double click on a done row opens it.
- Tabs as the mockup's segmented control with counts; "Clear done"; "Retry all" on Failed.
- The slim bar shows both speeds with arrows and the counts. The bar's height can be dragged and is remembered.
- A folder of thousands of files stays one row; its opened list is virtualized and paged.
- Tests: `SiteTransferRules` (row text, time left, which commands a row offers), a walk with a real upload and
  download and equal hashes, as SITES.4 had.

### R4. Desktop: sites themselves (`home`, `add-tiles`, `add-fields`, sidebar)

- "All sites" in the sidebar and its page: rows, last used, activity, "Open an address once", "Add a site".
- Add a site: tiles, then the short form for that kind, Advanced closed, Test with the result line. Edit site opens
  the same form. Options > Sites keeps the switch, rclone and updates, and links to All sites for the list.
- Sidebar entries as drawn: dot, arrows with counts, clock, progress line.
- Cloud kinds keep "Signs in through your browser" from the tile into the form.

### R5. Desktop: dialogs and the whole (`delete`, `overwrite`)

- Both dialogs compared and corrected. Local delete says Recycle Bin.
- A full pass over every desktop pair with the owner's notes from R2 to R4.

### R6. Web UI (`web-site`, `web-two`, `web-download`, `web-upload`, `phone`, `phone-sheets`)

- The two bugs first (above), each with a test that fails before the fix.
- Then the same screens as R2 to R5 in Svelte: pane chrome, context menu (right click, long press, "...",
  Shift+F10, as My TorBox has since D2d), double click, transfers list with the main list's row component, All
  sites, Add a site tiles.
- Download and Upload dialogs as drawn: the server first, this device second.
- Phone width: one pane, tick boxes, action bar, bottom sheets.
- Tests: vitest for the rules, the `npm run screens` walk extended with the Sites screens at 1360 and 390 px, and
  one run against a real browser with the extension installed, to see that a "to the server" download never reaches
  the browser.

### R7. Closing

- The owner walks a test build. Light mode pass. Linux Uno builds.
- `remote-sites.md`, `new-frontend-guide.md`, `PROGRESS.md`, `ROADMAP.md`, `CLAUDE.md` updated; the 21 final pairs
  stay in the repo as the acceptance evidence.
- Release.

## What could go wrong, and the guard

| Risk | Guard |
| --- | --- |
| The same miss again: built from words, not from the picture | Rules 1 to 3; the pairs are the acceptance evidence |
| Reusing the main list's row template drags download-only behaviour into transfers | A shared template with its own view model; R3 tests name the commands a transfer row may offer |
| Drag and drop in WinUI between two list views and from Explorer is fiddly, and Uno differs | Built last in R2, behind the same commands as the arrows, so it can slip without blocking the stage |
| Per-file progress from rclone does not map cleanly to our items (renamed targets, retries) | R1 is done and tested before R3 depends on it; fallback is the batch bar plus a state per file |
| Double click downloads something large by accident | Starts like any transfer, shows in the bar at once, one key to stop; no confirmation |
| Web and desktop drift apart again | R6 walks the same screen list; one gap table for both, kept in this file |
| The rework breaks what works (transfers, changes, certificates) | No Host behaviour changes outside R1; the existing end-to-end tests run at every stage |

## Out of scope

Android (its own mockups, `docs/plans/android-mockups/c3`, `c4`); the extension (owner, 2026-11-15: nothing of
Sites there); SITES.10's audit follow-ups; new providers; resume of half-moved files.
