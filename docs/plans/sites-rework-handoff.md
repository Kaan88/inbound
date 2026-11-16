# Handoff: Sites UI rework (SITES.11) and what is open from 2026-11-16

Written 2026-11-16 for the session that picks this up. Read `CLAUDE.md` first, then this, then
`docs/plans/sites-rework.md` (the plan) and open `docs/plans/sites-mockups/4-agreed-design.html` in a browser.

## Where things stand

- **Sites UI rework: accepted, not started.** The owner accepted the plan and all nine decisions on 2026-11-16
  ("I accept all picks but don't start yet"), then asked for this handoff. Nothing of the rework is built. Start at
  R0 when the owner says so.
- **main** is at the merge of #15 and is pushed; the local checkout equals origin.
- **Open for tomorrow (owner: "keep these open for tomorrow"), the macOS build into the v0.8.0 release:**
  1. Download the two Mac builds: artifacts `macos-osx-arm64-0.8.0` and `macos-osx-x64-0.8.0` of the "macOS packages"
     run 43789035309 (main at `028ab64`, kept 14 days). A first download was stopped part way; nothing is uploaded.
  2. Sign the update files here with the release key (`scripts/prepare-macos-release.sh 0.8.0 <zip> <arm64|x64> <key>
     Kaan88/inbound v0.8.0`, once per architecture; the key's path is in the session memory, never print it), upload
     the `.dmg`, `.zip`, `manifest-macos-<arch>.json` and `SHA256SUMS-macos-<arch>` of both to the v0.8.0 release, and
     add the Mac lines to its notes. The workflow cannot do this itself: given a tag it checks out the tag, and
     v0.8.0 has no macOS code. The changelog already has the macOS lines under 0.8.0 (`028ab64`).
  3. Check that the latest CI and "macOS packages" runs on main passed. GitHub's Windows job is unreliable (ROADMAP
     CI.5): a local `scripts\test.ps1` pass decides (owner: "windows is windows. deal with ci later").
- **Then ask the owner** whether the merged branches `macos-0.8.0`, `deps-upkeep` and `extension-open-files` may be
  deleted on GitHub. Not asked yet.
- **Left open on purpose:** #13 (npm group: the extension's type check fails with the newer `@types/chrome`), #14
  (TypeScript 7 in the extension: six popup tests fail), #16 (TypeScript 7 in the web UI: `svelte-check` 4.7.8 does
  not accept it). Each has a comment saying so.

## What the owner said about Sites (2026-11-16)

"current implementation is TERRIBLE ... a tacked on feature that is borderline unusable"; "backend seems sound and it
seems to work but ui is not finished"; "I agreed on mockups for the design USE THEM"; "it's also clear you never
checked them".

Reported faults:

- Web UI: a download goes to the browser, where the extension catches it, instead of to the server.
- Web UI: Google Drive files are not listed.
- Desktop: no context menu over files; a double click on a file does nothing.
- Desktop: the transfers list is unlike the rest of the app, nothing can be done with a downloaded file, and the
  Files button is of no use.
- Desktop: the Sites page "leaves a lot to be desired".

## The nine decisions, all answered yes by the owner

1. **Double click on a file** downloads it at once, to the site's linked folder or by category rules, with no
   dialog. "Open" (download to a temporary folder and open) is in the context menu.
2. **Download has no dialog.** Button, Enter, double click and the arrow start at once; the conflict question shows
   only when a file exists. "Download to…" in the context menu asks for a folder.
3. **Context menus.** Server pane: Download, Download to…, Open | Rename, Delete on server…, New folder | Copy
   address, Star this folder. This PC pane: Upload, Open, Show in Explorer, Rename, Delete (Recycle Bin), New folder.
   Empty space: New folder, Upload…, Refresh, Paste address. What a site cannot do is hidden.
4. **A finished transfer.** Double click opens the file or folder. Menu: Open, Open folder, Show on the site, Retry
   (failed), Remove from list. For an upload, Open and Open folder act on the local source.
5. **No "Rights" column.** rclone's listing does not report permissions.
6. **Per-file progress** (bar and rate on each running file of an opened batch) is built, in R1.
7. **"Open an address once"** on All sites is built as drawn, in R4.
8. **Linux Uno window** gets the rework because it shares the files; built, not walked (Linux is deferred).
9. **One release at the end**, with a test build for the owner after each stage.

## How to work, so the first build's miss is not repeated

- The mockup file is the spec. Build each screen from it, not from a summary.
- Before a screen counts as done: a screenshot of the mockup screen and one of the built screen, same window size,
  saved as `docs/plans/sites-mockups/compare/<screen>-mockup.png` and `<screen>-built.png`. The owner sees each
  stage's pairs before the next stage starts. Dark mode; light mode at the end.
- Look at the running app by sight at the end of each stage; UI Automation walks alone are not enough.
- Transfer rows reuse the main list's row template, columns, colours and progress bar. No new look.
- Every pointer action has a key. Anything the mockup leaves open is asked, not guessed.
- Sonnet subagents build the routine parts; review what they return against the mockup before accepting it. Keep the
  IPC additions (R1) in the main model.
- The Host code stays as it is outside R1. The existing end-to-end tests run at every stage.

## Stages (details in the plan)

R0 baseline screenshots of all 21 mockup screens and the built ones (no product code); R1 Host additions (per-file
progress, time left, last used, an unsaved site); R2 desktop browse view; R3 desktop transfers; R4 All sites, Add a
site tiles, sidebar; R5 dialogs and a full desktop pass; R6 web UI (the two bugs first, then the screens, then
phone); R7 owner's walk, light mode, docs, release.

## Known before starting

- **Web download bug:** cause found in the code, not reproduced. The only download control on a file row is a browser
  link to this device (`webui/src/screens/sites/SiteBrowser.svelte:342`); "Download to <server>" appears only after
  a row is ticked (`:275`). The fix is the mockup's dialog, server first.
- **Drive not listing in the web UI:** cause not found. Same `sites.browse` request as the window. Suspect: rows
  keyed by file name (`SiteBrowser.svelte:325`) while Drive allows two items of one name. Reproduce first with the
  owner's Drive site in a test instance; read the browser console and the `/api/ipc` answer.
- **Desktop:** `SitesPanel.xaml` has no context menu; `ItemsList_DoubleTapped` only opens folders;
  `SiteTransfersBar.xaml` rows are cards with up to six buttons; Download always asks for a folder
  (`MainWindow.SiteTransfers.cs`, `SitesDownloadButton_Click`); sites are added in Options > Sites through one form
  with a Kind drop-down (`SiteDialog.cs`); there is no All sites page and no Site menu.
- The gap tables in the plan come from reading the code. R0's screenshots confirm or correct them.

## Also recorded today

- Firefox: the listed add-on, version 0.6.5, was approved at addons.mozilla.org (ROADMAP EXT.S5, PROGRESS AS).
- Pull requests merged: #9 `4633baf`, #11 `1ceaebe`, #10 `3c6a760`, #5 `ef82afb`, #7 `cf75467`, #8 `2ba7b6c`, #17
  `b6d52ce`, #15 `1de6d90`. Closed, not merged: #6 and #12 (Ubuntu 22.10). PROGRESS AT.
