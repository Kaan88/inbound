# Remote Files, the rest: mockup notes (x1 to x33)

Draft for the owner's approval, 2026-11-22; revised the same day after two reviews. Source: `x-remote-files-rest.html`.
Each screen is rendered twice: `xN-name.png` (dark) and `xN-name-light.png` (light, the app's real light tokens from
`values/themes.xml` and `values/hosts.xml`; this is what the S24 shows). Same components as r1-r6, a1, c3, t- and i-.

Nothing here is built. Items marked **Proposal** change behaviour and need the owner's yes before they are built.
Everything else redraws a function that exists today. Host calls named below are in
`src/DownloadManager.Contracts/SiteDtos.cs`; the phone's own Host runs the same `HostCommandDispatcher`, so they are
answered there too.

**Which Host each screen shows.** Screens without a Host line are on this phone. Screens on a paired Host show a muted
"On Office PC" line under the title (x6, x14, x16) or a Host pill in the bar (x22, x24b, x25). On a paired Host the
wording names it: x4 "No transfers on Office PC", x13 "Remove Google Drive from Office PC?", x15 "On Office PC.",
x26/x27 "On Office PC".

## Transfers

| Id | Shows | Covers |
|---|---|---|
| x1 `x1-transfers` | App bar (back, "Transfers", ⋮), filter chips with counts, one card per transfer, newest first. Card: direction tile (down, up, copy; green finished, red failed, grey canceled), title, status line starting with the state word (coloured like Downloads), route "Source → Destination", progress bar while running or paused, one round action. Routes show the phone folder (`Download/Inbound/Music`) without "This phone ·"; when the route is too long the source is shortened first so the destination stays readable. | States running, queued ("Queued"; the Host reports no queue position), paused, failed ("Failed · 3 of 24 not copied"), done, done with skipped ("Finished · 14 files, 2 already there"), canceled. Pause (running), Resume (paused), Retry (`sites.transfer.retry`, failed), Cancel (queued). Raw words, IDs and `/storage/emulated/0` paths are gone. |
| x2 `x2-transfer-details` | Tap a card: details sheet (expanded). Reason banner, Retry and "Show in Home NAS" pills, From / To / Size and start time, the full transfer ID in monospace with a copy button, a Files section from `sites.transfer.get` (failed files first, state coloured, a folder button on each copied file), "All 24" opens the full list, then "Remove from list" (`sites.transfer.clear` with the id) and Cancel transfer. | Problem and Host `Message`, skip notes, transfer ID (only here), Retry, Cancel. Per-file list is the way to reach one file of a batch. |
| x2b `x2b-transfer-paused` | The same sheet for a paused transfer: amber banner "Unfinished files restart from the beginning on resume.", Resume pill, details with ID, Cancel transfer. | Paused note, Resume, Cancel. Its Files section is as in x2 (below the fold). |
| x3 `x3-cancel-transfer` | Confirm dialog: "Cancel this transfer?", the transfer in words, "A canceled transfer can't be resumed.", Keep transfer / Cancel transfer. | Existing cancel confirmation. |
| x4 `x4-transfers-empty` | Empty state "No transfers on this phone". | "No transfers on this Host." |
| x5 `x5-transfers-menu` | ⋮ menu: Refresh, "Clear ended transfers" with its count (clear-all glyph). | `sites.transfer.clear` with an empty id removes every finished batch, and the Host counts done, failed and canceled as finished, so one item clears all three (4 here). Moved files stay where they are. |
| x5b `x5b-transfers-refresh` | Pull-to-refresh spinner. | Refresh button becomes pull-to-refresh (and the menu item). |
| x6 `x6-transfers-error` | Paired Host: "On Office PC" line, red banner "Could not refresh transfers / Their current state is unknown. Shown as of 13:52." with a Try again text button. Rows keep full contrast (the banner says they are stale; their buttons still work). | Load error, "Host: … · State as of last refresh". Action errors use the same banner. |

Busy state (**Proposal**, not drawn): while an action is being sent, that card's round button shows a small spinner and
the other buttons stay; Back is not blocked (today all buttons hide and Back is consumed).

**Proposals (Transfers):**
- Bytes, speed and time left per transfer, and a progress bar (the DTO has `Bytes`, `BytesDone`, `Speed`, `Files`).
- Filter chips.
- Tap a card for the details sheet with its files (x2, x2b; `sites.transfer.get` exists).
- Retry on a failed card calls `sites.transfer.retry` (today the app reaches it only as Resume of a resumable failed batch).
- "Clear ended transfers" and "Remove from list" (`sites.transfer.clear` exists; Android does not offer it yet).
- Cancel on a queued transfer (today Cancel appears only for running, paused or resumable).
- Finished card's folder button: for a download to the phone it opens the This phone pane at that folder with the file
  marked (x9); for an upload or copy it opens the destination location at that folder (x2 "Show in Home NAS").

## Getting to the file ("it says it's in downloads, there needs to be an easy way to get there")

One verb per destination: **View transfers** opens Transfers; **Show in folder** opens the folder with the file marked;
**Open** opens the file. A snackbar carries one action (Material and `MainActivity.ShowMessage` support one).

| Id | Shows | Covers |
|---|---|---|
| x7 `x7-started` | After "To this phone": snackbar "2 files going to Download/Inbound/Music" with **View transfers**; a transfers button with a count badge in the folder app bar. | Replaces the "Added to Transfers." toast. Partial and failure messages use the same snackbar (with View transfers when some started). |
| x8 `x8-finished-snackbar` | A download to the phone finished: "session-0503.flac is on this phone" with **Show in folder**. | New finished notice. |
| x9 `x9-show-in-folder` | The This phone pane at Download/Inbound/Music. The file has a blue outline only (not the selection fill) that fades after about 3 s, and a tonal **Open** pill. Breadcrumbs keep the last chip at the 16 dp gutter and clip the first ones at the left. | Existing phone pane and `Outside.OpenFile`. |
| x10 `x10-downloads-added` | Main Downloads, drawn from a1: search pill, summary card with its sparkline, chips, cards, the **Add** FAB lifted above the snackbar. The new card is outlined and shows its folder line; snackbar "Download added · saving to Download/Inbound" with **Show in folder**. | The owner's case on the main tab. Torrent and TorBox "Added to Downloads" get the same action. |
| x10b `x10b-downloads-folder` | Where Show in folder lands from Downloads: the app's own folder page (Phone › Download › Inbound) over the Downloads tab, which stays selected. The file is outlined with Open. Back returns to the Downloads list. | Same page from the finished card's folder button and from Details "Show in folder". While the download still runs, its row shows its progress instead of Open. |
| x33 `x33-finished-elsewhere` | On the Remote Files tab: "setlist.pdf is on this phone" with **Show in folder**. | The finished snackbar shows on whichever tab is open. |

Rules:
- The folder line on a Downloads card shows on the new card while it is outlined (about 3 s), and always in Details.
- On a paired Host without `CanShowInFolder` / `CanOpenFiles`, the snackbars have no action and name the place:
  "Download added · saving on Office PC in D:\Downloads".
- When the app is in the background, a finished transfer to the phone posts a system notification "<file> is on this
  phone" with Show in folder (**Proposal**). The lasting route is the Transfers card's folder button and the
  transfers button with its badge.
- Spelling: the phone folder is "Download" everywhere. The existing bottom-bar caption "To Downloads/Inbound/Music"
  becomes "To Download/Inbound/Music".

**Proposals:** all of the above. The transfers button stays in the folder app bar (without a badge) when nothing runs,
so Transfers is always one tap away. Show in folder goes to the app's own folder view rather than the system Files app,
because `Outside.ShowDownloadFolder` can only open the top Download folder.

## Locations list

| Id | Shows | Covers |
|---|---|---|
| x11 `x11-locations` | Headline and cards placed as c3 (title at 16 dp, tiles 16 dp in), a neutral Transfers row when nothing runs ("Transfers · 3 finished today"), search and ⋮, all location kinds incl. "Not encrypted", Sign in pill, FAB "Add location". Menu closed. | Banner, subtitles, list. |
| x11b `x11b-locations-menu` | ⋮ open: Refresh, Transfers, Turn off Remote Files. | ⋮ menu (today only by long-pressing search). |
| x12 `x12-location-sheet` | Long-press on a cloud location: header "Google Drive / Signed in" (the kind is shown only when it differs from the name), Open, Edit location, Sign in again, Own client ID, Remove location. Icons in text colour; only Remove is red. | Remove, Own client ID. Sign in again opens the browser on this phone and the paste sheet (x29) on a paired Host. |
| x12b `x12b-sftp-location-sheet` | SFTP variant: Open, Edit location, Review server key, Remove location. | Review server key (x26/x27). |
| x13 `x13-remove-location` | "Remove location?" / "Remove Google Drive from this phone? Files in Google Drive are not deleted." Left-aligned like x3 and x15. | Remove dialog; the location is removed from the Host, for every client. |
| x14 `x14-remote-files-off` | Paired Host: "Remote Files is off on Office PC" with the filled "Turn on Remote Files". | Disabled state, Enable. |
| x14b `x14b-off-menu` | ⋮ when off: Refresh, Transfers, Turn on Remote Files. | Off-state menu. |
| x15 `x15-turn-off` | "Turn off Remote Files?" / "On this phone. Running transfers pause. Your locations stay saved." | Disable dialog. |
| x16 `x16-load-error` | Paired Host: "Could not load Remote Files from Office PC. Check the connection and try again." with a tonal Try again. | Error state, Host line. |
| x17 `x17-search-no-match` | Search open, no match. | Search, "No locations match your search." |

The amber banner while transfers run (x15, x33) is unchanged from c3. Light theme: it uses `host_strip_bg` #FBE3CC on
`host_strip_fg` #6B3A00 (hosts.xml), while warning banners use `state_waiting_container` #F6E6C4 (themes.xml); the two
are different on purpose (status strip versus warning). Loading: the same layout with a centred spinner; "Updating…"
becomes the pull-to-refresh spinner.

**Proposals:** Transfers row stays visible when nothing is active; visible ⋮ next to search; location sheet with "Edit
location", "Sign in again" and "Review server key"; "Disable"/"Enable" worded "Turn off"/"Turn on". Naming follows the
shipped app ("Remote Files", "Add location"), not c3's "Sites"/"Add site"; the owner should confirm which one stays.

## Add, edit and check a location

All full-screen forms use a5's footer: Cancel and the primary button on a 104 dp surface band. The type picker and Edit
use X (top-level); the type forms use back (step 2).

| Id | Shows | Covers |
|---|---|---|
| x18 `x18-add-type` | Type picker as group cards with the list's tiles. Glyphs follow the app's `LocationGlyph`: cloud for Drive, OneDrive and Dropbox; server for SFTP and FTP; globe for WebDAV; box for S3 and B2. | "Location type" dialog. |
| x19 `x19-sftp` | SFTP: name, server, port 22, user, password with eye, More options with starting folder (plain text field: nothing can be browsed before saving). | All SFTP fields. |
| x20 `x20-ftp` | FTP/FTPS: name, server, port, encryption (FTPS / Implicit FTPS / None), user, password, starting folder. Choosing Implicit FTPS changes the port to 990 unless it was edited. | **Proposal** (new on Android). |
| x20b `x20b-ftp-unencrypted` | None chosen: the amber "Allow FTP without encryption" banner with its checkbox, like WebDAV over HTTP; Add location stays refused until it is ticked. | **Proposal**. |
| x20c `x20c-certificate` | FTPS or https certificate not from a known authority (`sites.certificate`): issued to, issued by, valid until, SHA-256 fingerprint with copy, "Self-signed" warning, Cancel / Trust certificate. | SITES.6b certificate trust. |
| x21 `x21-webdav-errors` | WebDAV with validation: red outlined fields with the exact messages, the Allow HTTP banner with the checkbox as its leading control. | Address, user, password, Allow HTTP, validation messages. |
| x22 `x22-s3` | S3 on a paired Host (Host pill): provider, endpoint, region, access key ID, secret, starting folder, "Download folder on Office PC (optional)". | All S3 fields, Host download folder (only when the Host isn't the phone; on every kind). |
| x23 `x23-b2` | Backblaze B2: name, key ID, key, starting folder, hint. | **Proposal** (new on Android). |
| x23b `x23b-test-failed` | After Add location: the location is saved, then `sites.test` (which needs the saved Id) fails: "Saved, but it did not connect", the Host's message, Keep anyway / Edit. Edit keeps the form open as an edit of the saved location. On success the form closes and the list shows it. | **Proposal**. |
| x24 `x24-cloud` | Drive / OneDrive / Dropbox on this phone: name, an info banner "Sign in with your browser", the closed expander "Use your own app registration"; primary "Add and sign in". | Own client ID (closed), "Save the location, then choose Sign in". |
| x24b `x24b-cloud-paired` | The same on a paired Host with the expander open: "Office PC stores the authorization", client ID note, client ID and secret. Add and sign in leads to x29 (or x29b with an own client ID). | Own client ID fields and note, paired-Host sign-in. |
| x25 `x25-insecure-host` | Host connection not secure: amber banner with a "Host connection" text button (opens the Host / pairing settings), fields disabled, Add location disabled with its icon kept. | Insecure Host note, disabled fields. |
| x26 `x26-trust-key` | Over the filled SFTP form: "Trust these server keys?", "Home NAS · nas.local:22", "On this phone", each key selectable in monospace with a copy button, the compare advice, Cancel / Trust keys. | First case. |
| x26b `x26b-more-keys` | Over the list: "Trust more server keys?" with the MoreKeys text and the new key, Cancel / Trust key. | MoreKeys case. |
| x27 `x27-key-changed` | Over the list: red banner, Trusted before / Offered now with copy buttons, the confirmation checkbox, "Replace trusted key" disabled (icon kept) until ticked. | Changed case. |
| x28 `x28-signing-in` | Phone Host sign-in: card shows "Waiting", dialog with spinner, "Finish signing in with the browser", Open browser again and Cancel. | Signing in dialog. |
| x29 `x29-paste-token` | Paired Host sign-in, default client: the full command wrapped in monospace with a copy button, the token field with paste, Cancel / Sign in. | Paste-token dialog, default variant. |
| x29b `x29b-paste-token-own-id` | Own client ID variant: the command with `"<your client ID>" "<your client secret>"`, "Never paste your client secret here." | Paste-token dialog, own-ID variant. |
| x30 `x30-own-client-id` | Own client ID sheet over the list, with an inline validation error and "Client secret saved; type to replace". | Own client ID dialog. |
| x31 `x31-edit-location` | Edit an SFTP location (X to close): same form, password "Saved; type to replace", note that a new address or port asks for the key again, Remove location, Save. | **Proposal** for Android: `sites.save` with the Id already supports edits (a null secret keeps the stored one; a new host or port forgets the accepted keys). |

Banner kinds: ok (green), warn (amber), fail (red) and **info** (`--sel` background, `--on-sel` text, blue icon) for
neutral explanations (x24, x24b, x26, x26b). Placeholders use the muted text colour; the dim colour is only for
disabled button labels.

Errors from saving, trusting or signing in appear in the form's banner slot (fail style), or as a snackbar inside the
folder view, with the existing strings.

**Proposals (forms):**
- FTP/FTPS and B2 forms (x20, x20b, x23); the Host and desktop already support them.
- Save, then test (`sites.test`): SFTP leads to the key sheet (x26) at once; other kinds close on success or show x23b.
- "Add and sign in" for cloud kinds (x24, x24b): saving starts the sign-in at once.
- "Open browser again" in x28 (new; today the dialog has only Cancel).
- Password eye toggles; "Host connection" button in the insecure banner (x25).
- **Owner decision, parity gap:** `SiteSaveRequest` has `AuthMethod`, `PrivateKey` and `KeyPassphrase`, but no Android
  form offers SFTP key login. Not drawn until the owner decides whether Android gets it.

## Folder pane messages

The message sits in the pane as an empty state: round icon, title, text, and a tonal 36 dp pill for the action (the
filled 56 dp pill is kept for the one main action of a screen, x14).

| Id | Shows |
|---|---|
| x32 `x32-pane-messages` | "Sign-in has run out" / "Sign in again to browse this location." with Sign in. |
| x32b `x32b-pane-key-review` | Key problem: the Host's message with Review server key (amber icon). |
| x32c `x32c-pane-load-error` | "Could not load this folder" / "Check the connection and try again." with Try again (red icon). |
| x32d `x32d-pane-empty` | "This folder is empty." (no button). |

Other pane strings use the same layout (not drawn separately):

| String | Icon | Button |
|---|---|---|
| Could not browse this location. | alert | Try again |
| This server's key must be reviewed over a secure connection to the Host. | key | none |
| This Host did not provide a listing token. Refresh to try again. | alert | Try again |
| Could not load the rest of this folder. | alert, as a row at the end of the list | Try again |
| This folder is not available. (phone) | folder | none |
| Android did not return the Downloads list. (phone) | alert | Try again |
| Could not read the phone's scoped Downloads list. (phone) | alert | Try again |
| The selected Host changed. | phone or pc | none (the pane reloads) |

The seven disabled-reason captions in the bottom bar ("Open a folder in Download on this phone first", "This Host has
no download folder for the location", "Update this Host to download files without replacing existing files.",
"Uploads from this phone need this phone as the Host", "Open a folder in Download first", "Copying between two
locations isn't available yet", "Pick a location in the other tab to copy to") stay as r1-r6 draw the bottom bar:
muted caption under a disabled main button.
