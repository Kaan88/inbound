# Sites rework: mockup and built screens side by side (SITES.11)

`<screen>-mockup.png` is a screen of `../4-agreed-design.html`; `<screen>-built.png` is the app as built. R0
(2026-11-16) is the "before" set: 0.8.0 with no rework. Each later stage replaces the `-built` files of its screens.
All are dark mode; light mode comes at the end (R7).

## How they are made

- Mockups: `scripts/shoot-sites-mockups.ps1` (headless Chrome, 1360 x 800, the window only; `-Light` for light).
- Desktop: a test instance (`DOWNLOADMANAGER_INSTANCE=r0`) started with `scripts/run-on-hidden-desktop.ps1`, so its
  windows never show on the owner's screen and take no input, driven by `scripts/ui-drive-hidden.sh` (UI Automation
  only, no mouse or keys) and captured with PrintWindow. Size the window to `1376x808` and crop 8 px left, right and
  bottom (the invisible resize frame) to get 1360 x 800.
- Web UI: `webui/scripts/sites-r0.mjs` (Playwright) against a throwaway `DownloadManager.Server.exe`.
- Test sites: `rclone serve` on 127.0.0.1 (SFTP throttled with `--bwlimit 150k`, WebDAV, FTP, a read-only WebDAV and a
  closed port), with folders seeded to match the mockup's rows.

## R5 and R6a (2026-11-17)

`delete-built.png`, `overwrite-built.png` and `delete-permanently-built.png` (owner request, no mockup) are R5 at
1376 x 808. `web-site-built.png`, `web-two-built.png`, `web-download-built.png` and `web-upload-built.png` are R6a,
Chromium 1360 x 820, dark.

## R4 (2026-11-17)

`home-built.png` (All remotes), `add-tiles-built.png` (taken after Back from the form) and `add-fields-built.png` are
R4, 1600 x 1000, not cropped.

## R3 (2026-11-17)

`transfers-built.png`, `failed-built.png` and `downloads-built.png` are the R3 transfers list, taken on the final build
at 1600 x 1000 (not cropped to the mockup's 1360 x 800); new: `batch-built.png` (a batch opened in place) and
`done-built.png`. `transfers-done-built.png` is still R0's.

## R2b (2026-11-17)

`browse-built.png` and `remote-only-built.png` retaken with the R2b toolbar and "Remote" menu; new: `remote-menu-built.png`
(the menu open; the black margin is the popup's own capture), `conflict-built.png` (the "already there" question,
before its wording named the file and folder). No context-menu picture: UI Automation can't open it on the hidden
desktop. `trouble-built.png` is still R2a's (old toolbar).

## R2a (2026-11-17)

`browse-built.png`, `remote-only-built.png` and `trouble-built.png` are now the R2a panes (same method; the trouble
picture is `nas.home.lan` with its SFTP server stopped). The other built pictures are still R0's.

## The R0 pairs, and what each built picture is

| Screen | Built picture | Note |
| --- | --- | --- |
| downloads | All Downloads with two throttled downloads and one finished; Site transfers chip | |
| browse | `nas.home.lan`, two panes | |
| remote-only | the one-pane view | |
| transfers | the bar open on Running: an upload and a download moving | the folder download had already finished |
| failed | Failed tab after an upload to a read-only WebDAV | |
| trouble | a site on a closed port | |
| delete | Delete on server dialog | |
| overwrite | the Upload dialog | no equivalent: the app has no "which copy is newer" question; an older file over a newer one is skipped without asking (shown as "1 skipped" under Done) |
| add-tiles | Options › Sites › Add a site, Kind drop-down open | a 900 x 720 Options window, not a 1360 x 800 overlay; no tiles exist |
| add-fields | the New site form | 900 x 720, as above |
| home | Options › Sites (rclone settings; one saved site in view) | no All sites page exists; `home-web-built` is the web UI's Sites page |
| web-site | the web site view, one row ticked | |
| web-download | "Download to the server" dialog (reached only after a tick) | the row icon downloads to the browser instead |
| web-upload | after "Upload from this device" | the same as web-site: the browser's file chooser opens, there is no dialog |
| phone, phone-sheets | the site at 390 px wide, then with a row ticked | no sheets exist |
| web-two, ext, overview | none | web-two does not exist; ext and overview are not part of R0 |

Only `downloads` and `transfers` were cropped to 1360 x 800; the other desktop pictures are 1360 x 800 windows with an
8 px black frame (content 1344 x 792). `gdrive` and `b2-media` are WebDAV stand-ins and show "Not encrypted", which a
real Drive or B2 site would not.

Extra built pictures: `download-dialog`, `transfers-done`, `options-sites`.

The test machine's folder is repainted as `D:\Downloads\ISOs` in every built picture that showed it (the This PC box,
transfer routes and dialog folder boxes). Do the same for later stages before committing.
