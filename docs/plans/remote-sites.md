# Sites: integration plan (FTP, FTPS, SFTP, WebDAV and cloud storage through rclone)

Naming (owner, 2026-11-17, SITES.12): what people see is "Remote files"; one place is "a remote" (menu "Remote", "Edit remote", "Add a remote", "All remotes"), so it is not mistaken for site logins. The words "site" and "Sites" below, and `sites.*`, `dm sites`, the class and file names, are the working names and stay in the code.

Status: plan only, 2026-11-13. Nothing is built. Tracked as ROADMAP SITES.1 to SITES.9.

Order together with the performance work (PERF.8 to PERF.20), and the rules that work sets for these phases:
`sites-and-performance-order.md` (2026-11-14).

## What this is

A Sites view in Inbound: saved remote places, a file browser for each, and uploads, downloads, delete, rename and new
folder. Every protocol runs through one `rclone` process that the Host controls (downloaded from Options > Remote files on Windows and Linux desktops, included in the Docker image). The agreed design is the
fourth mockup page (owner, 2026-11-13: "I completely agree with your mockups and suggestions").

## Owner decisions

| Date | Decision |
| --- | --- |
| 2026-11-12 | FileZilla-style browsing, not link or folder downloads. FTP returns (reverses the FTP tick in CUT.1). WebDAV and rclone are wanted. |
| 2026-11-12 | Uploads exist. Remote delete, rename and new folder are in version one. rclone is bundled in Setup. Plain FTP is allowed. The Docker image ships rclone. |
| 2026-11-12 | SFTP and rclone share one UI. Site transfers probably do not belong in the main downloads list. |
| 2026-11-13 | "uploads and all the needs that come with it are a must. if rclone can do it all, that's fantastic." |
| 2026-11-13 | Sites in the sidebar show that a download or upload is active there. |
| 2026-11-13 | The fourth mockup page is agreed. Sites is switchable. rclone is a separate process that runs only while needed. |
| 2026-11-15 | rclone is checked for updates by the desktop apps, and the Docker server updates it too; on the desktop updates install automatically by default (replaces "the pinned rclone version changes only with Inbound releases"). |
| 2026-11-15 | Open questions answered: site transfers have their own limit (1); a web session may add and edit sites only as admin (2); no Sites line or anything else of Sites in the extension (4); rclone is **not in Setup or the update zip**: Options > Sites downloads it, and the Docker image includes it (5, replaces "rclone is bundled in Setup" of 2026-11-12); FTPS self-signed certificates go in one CA file Inbound manages (6); no resume is accepted for now (7). Question 3: "to this device" and "from this device" are in the first web UI release, not server side only (owner, 2026-11-15). |

## The agreed design in short

- Selecting a site in the sidebar replaces the download toolbar with a site toolbar: Download, Upload, New folder,
  Rename, Delete on server, Refresh, Pause transfers (stop and restart: rclone has no pause), Edit remote, Options.
- There is no Connect or Disconnect. Selecting a site lists it. Trouble shows in the pane with Retry and Edit remote.
- Two views: Server only (the default) and Two panes, with arrow buttons between the panes, breadcrumbs, starred
  folders and a linked local folder per site.
- An "On this PC" column: Same, Older here, Some of it, Not here. It compares name, size and time only.
- Transfers live in a slim bar that opens upward, with tabs Running and waiting, Failed, Done. One row per action; a
  folder is one row that opens.
- Site transfers never appear in the main downloads list, the Finished view or the extension popup. The status
  bar has one chip, and each sidebar site shows arrows with counts, a clock for waiting ones and a progress line.
- "Delete on server" always asks, with the count and size. Local delete goes to the Recycle Bin.
- Add a remote by picking a tile, then four fields; the rest is under Advanced, with a Test button.
- Web UI: the same view. "Here" means the machine Inbound runs on. Download and Upload ask "the server or this
  device"; both are in the first web UI release (owner, 2026-11-15). Phone: one pane, bottom sheets. The extension shows nothing of Sites (owner, 2026-11-15).

## Architecture

```
WinUI / Uno window, web UI, CLI
        |  sites.* IPC (named pipe, or WebSocket through Server)
Host:  SitesService (Runtime)  --  SiteStore, SiteTransferStore (Persistence)
        |  HTTP on 127.0.0.1, random port, random user and password
rclone rcd   (child process, started on demand, stopped when idle)
        |
FTP, FTPS, SFTP, WebDAV, Drive, OneDrive, Dropbox, S3, B2, ...
```

The Host stays the single owner. Clients never talk to rclone. rclone holds no saved state of ours.

### New code, by project

| Project | What is added |
| --- | --- |
| `DownloadManager.Core` | `Site`, `SiteKind`, `SiteCapabilities`, `SiteTransfer` (a batch), `SiteTransferItem`, states. `AppSettings.SitesEnabled`. |
| `DownloadManager.Contracts` | `SiteDtos.cs` with `SiteMessageTypes` and `IsRequest()`, following `TorrentDtos.cs:4`. Hello capability string `sites`. `Protocol.Version` stays 1. |
| `DownloadManager.Persistence` | `JsonSiteStore` and `JsonSiteTransferStore` with schema versions and the move-to-corrupt path; folders `sites` and `sites/transfers` in `StoragePaths` (`JsonStores.cs:11`). One more `SecretProtector` purpose for site secrets. |
| `DownloadManager.Sites` (new, `net10.0`) | `RcloneProcess`, `RcloneSupervisor`, `RcloneClient` (rc calls), `RcloneLocator`. Modelled on `DownloadManager.Torrents` (`TorrentWorkerProcess.cs`, `TorrentWorkerSupervisor.cs`, `TorrentSetup.cs`). No UI. |
| `DownloadManager.Runtime` | `SitesService` (sites, listings, transfers, remote changes, trust), `HostCommandDispatcher.Sites.cs`, a share in the speed budget. |
| `DownloadManager.Host`, `Host.Linux`, `Server` | Wiring in each `Program.cs` and `ServerApp.cs`, start off the start path, stop at shutdown before the pipe closes. |
| `DownloadManager.Desktop.WinUI` (shared with Uno) | `MainWindow.Sites.cs`, a `SitesPanel` control hosted like `ProviderFullHost`, site toolbar, Add a remote dialog, Options page. |
| `webui` | `screens/Sites.svelte`, `lib/sites.ts`, a `sites` route in `App.svelte`. |
| `DownloadManager.Cli` | `dm sites ...`. Not `dm site`: that already means site logins (`Program.cs:606`). |

### Why transfers are not `DownloadJob`s

Everything that lists jobs would show them: the main list, Finished, history, `jobs.query` for the extension popup
(`NativeBridgePolicy.cs`), exports and statistics. A site transfer also has a direction and belongs to a site, and
rclone moves its bytes. So a transfer is its own record in its own store, and `SitesService` is its own scheduler.
The price is that these places must be taught about it on purpose:

- **Speed:** rclone's share is added to the split in `TorrentSpeedBudget.cs` / `SpeedBudgetSplit.cs`, as torrent
  download and upload are today, and applied with rclone's `core/bwlimit`.
- **How many at once:** a per-site "transfers at once" setting and one overall Sites limit. They do not count against
  `MaxConcurrentDownloads` (`DownloadManagerService.cs:4368`). Owner to confirm.
- **Scheduler:** timed stop and start windows (`SchedulerAutomation.cs`) pause and resume site transfers too.
- **Sleep prevention and tray:** a running site transfer keeps the PC awake and shows in the tray tooltip.

## rclone as a child process

- **Separate process, not a library.** rclone can be built as a DLL (`librclone`), but a crash or hang would take the
  Host down, we would have to build it ourselves, and "use your own rclone" would not work. Rejected.
- **Start:** `rclone rcd --rc-addr 127.0.0.1:0`-style loopback bind with a random user and password made for each
  start, `--config ""` so nothing is read from or written to a config file (confirmed in the run, on Linux), and no web GUI.
  The docs call rc access "equivalent to shell access", so the secret never goes on the command line where another
  process's command line shows it: it is passed in `RCLONE_RC_USER` and `RCLONE_RC_PASS` (run point 2). On Linux
  the environment is readable by the owner only; on Windows other processes of the same user can read it (from the
  docs, not run), so this guards against other users and process lists, not malware running as the user. rclone is never started
  with `-vv`, which logs the password. `--cache-dir` and `--temp-dir` point into our state folder.
- **Lifetime:** started when a site is selected, when a transfer is running or waiting to retry, or when the
  scheduler starts one. Stopped after an idle delay once no client has a site open and nothing is transferring. With
  Sites off it never starts. Restart with growing delays and a cap, as `WebUiSupervisor` does (`MaxRestarts = 5`).
- **Never outlives the Host:** rclone itself keeps running, and keeps transferring, when its parent dies (seen in the
  run), so the Host must tie it: a Windows job object (kill on close; not yet run on Windows) and on Linux
  `PR_SET_PDEATHSIG` (the run used `setpriv --pdeathsig`; by the man page it fires when the spawning *thread*
  exits, so rclone is started from a long-lived thread).
  At shutdown: `core/quit`, wait a few seconds, then kill. At start the Host stops an rclone left by a crashed Host
  (pid file). `TorrentWorkerSupervisor` is the pattern to copy.
- **Which binary:** the path set in Options ("Use my own rclone"), or `INBOUND_RCLONE` for tests, or the copy Options
  downloaded (`<state>\tools\rclone\<version>\rclone(.exe)`, see "Getting rclone"), or in the Docker image the one
  the image ships, as `TorrentSetup.cs` does for the worker. A version check at start; too old shows a
  clear message, not a broken view. The oldest accepted for SFTP is 1.75.0, the first with `host_keys` by the
  changelog (run point 3; 1.75.0 itself was not run). An older rclone is refused for SFTP, never used unchecked.
- **Idle cost:** none when not running. No polling of sites in the background. Sidebar activity comes from the
  Host's own transfer records, not from asking rclone.

## Credentials and trust

- A site's settings are stored by Inbound in `sites/{id}.json`. Secrets (password, key passphrase, OAuth token,
  access keys) are sealed with `SecretProtector` (DPAPI on Windows, AES-GCM with `secrets.key` or the keyring
  elsewhere). No password is ever part of an address; an address with one is refused, as today.
- For each call the Host builds the remote from the stored site and passes it in the rc call's `fs` field as a JSON
  object of backend options, with absolute paths through `_root` (run point 1). Secrets exist in rclone's memory
  only. Every password and key passphrase is obscured with `core/obscure` first. rc error bodies repeat the secrets
  they were given, so they are never logged or shown as they are.
- **The user's own `rclone.conf` is never read or written in version one.** Importing remotes from it can come later
  as an explicit action.
- **SFTP host keys.** rclone does not check the host key by default and never asks. So Inbound does it:
  1. On first use the Host reads the server's public key with a small SSH handshake of its own (key exchange only,
     no login). rclone cannot report a key before a login, and its pin flag sends the credentials first (run
     point 4; a 50-line probe did this in the run).
  2. The window shows the fingerprint and asks. No credentials are sent, and nothing is stored, before the user
     says yes (the handshake itself sends only the key exchange).
  3. The key is stored with the site and passed to rclone as `host_keys` on every call.
  4. If rclone reports a different key, the site stops with both fingerprints and a hard warning. There is no
     "continue anyway" button; the user must open Edit remote and replace the key. rclone's refusal has two message
     forms, one for a changed key and one when the key's algorithm no longer matches; both must be recognised.
- **FTPS certificates.** rclone offers a CA file or "do not verify", not pinning, and there is **no per-site CA**:
  the CA file is read once when rclone starts (`RCLONE_CA_CERT`; run point 6). A certificate that fails verification
  is shown with its fingerprint; if the user trusts it, it is stored with the site. It then reaches rclone
  through one Host-managed CA file of accepted certificates, read by rclone at start, and rclone is restarted when
  it changes (owner, 2026-11-15, question 6). Turning rclone's check off for the site and checking the fingerprint
  in the Host was rejected: it leaves a gap between our check and rclone's connection. A CA file trusts the certificate for every
  remote of that rclone process, though name checking still applies (run point 6). "Do not verify" is never offered
  to the user.
- **Plain FTP** is a per-site choice with a warning in the dialog and a permanent "Not encrypted" chip in the pane.
- **OAuth kinds** (Drive, OneDrive, Dropbox): sign-in through the browser with rclone's own flow; on a server with no
  browser, the `rclone authorize` paste flow from the earlier mockups. The Host stores the token sealed and must
  write back a refreshed token after each use. Each such site is one **named** in-memory remote (`config/create`
  with `--config ""`; an on-the-fly `fs` object has no name to write a new token back to). After each batch and
  before rclone stops, the Host reads the token with `config/get` and re-seals it if it changed. Seen with rclone
  1.76.0 (2026-11-15, `scripts/sites-oauth-refresh-check.sh --from-conf`, the owner's own Drive and Dropbox remotes):
  with the expiry moved into the past, the first list refreshed the token and `config/get` returned the new access
  token and expiry; neither provider rotated the refresh token. A grant that no longer works (a OneDrive token unused
  since 2025) fails the call with `invalid_grant` and leaves the stored token as it was: the site then needs a
  "Sign in again" state, not a generic error.
  App registration (owner, 2026-11-14): rclone's shared app IDs are used by default, and the site's settings have an
  optional field for the user's own client ID and secret. Other providers are added one at a time.
- **Key files:** passphrase-protected keys in the current OpenSSH format work in 1.76.0, as do old PEM and
  unencrypted keys (run point 5, Linux), so the Host's rclone needs no conversion step. Which version made the
  new format work is not known; "Use my own rclone" with a version between 1.75.0 and 1.76.0 needs a test first. A key passed inline (`key_pem`) needs its line
  breaks as literal `\n`.
- **Web sessions** keep their folder limit: local paths in any `sites.*` command go through `CallerScope` and
  `PathRoots` (`WebSessionProfile.cs:85`). Remote paths are not limited. Whether a web session may add or edit sites
  follows `SessionRights`: admin only (owner, 2026-11-15).

## Capabilities

Each site kind has a fixed capability set, corrected by what rclone reports for the remote (`operations/fsinfo`):
download, upload, delete, rename, new folder, empty folders, server-side move, checksums, free space, rights. The UI
hides a command the site cannot do (design rule: hide what does not apply). Object stores get the two explanations
from the earlier mockups: folders exist only while they hold something, and rename is copy then delete.

## Transfers

- **A batch** is one user action: site, direction, source and target folders, conflict policy, items, totals, state.
  It is written to `sites/transfers/{id}.json` atomically before anything starts, so a Host restart resumes or
  restarts it.
- **Who moves the bytes:** rclone, in both directions (`operations/copyfile` and `sync/copy` as async rc jobs,
  progress from `core/stats` grouped per batch). This is one code path, and uploads need it anyway.
- **Payload placement.** Downloads land in the destination folder as `name.<hex>.partial` and are renamed in place,
  so there is still no temp directory and no cross-volume copy. The name is rclone's, not `<final>.<jobId>.part`;
  that convention stays as it is for download jobs. SFTP and FTP uploads also use `.partial` and a rename; WebDAV
  uploads have no temporary name, so a cut WebDAV upload leaves a truncated file under the final name until the
  next try overwrites it (run point 7); the Host marks that file as not done and never reports it as complete.
  After a crash the Host removes stray `.partial` files of its batches.
- **Resume.** A stopped batch continues with the files not yet done. A half-done file starts again from zero
  (run point 7: SFTP, FTP, WebDAV and local, mostly against `rclone serve`; cloud kinds not tried); "Pause" is stop and start again, and the UI says so. Accepted for now (owner, 2026-11-15, question 7); to look at again later. rc job results
  expire after a minute by default, so the Host reads them promptly and keeps its own record.
- **Conflicts** are decided before the batch starts where possible (the listing is already known), with the dialog
  from the mockups: Skip first, then Replace, then Keep both, and "do this for the others".
- **Failures.** A failed file does not stop its batch. It goes to the Failed tab with a plain reason and Retry all.
  Automatic retries use a growing pause and stop after five tries.
- **Remote changes** (delete, rename, new folder) are Host commands that answer with a result per item. Delete of a
  folder first counts what is inside so the dialog can say it.
- **"On this PC"** is computed in the Host from the linked local folder and the remote listing. No checksums unless
  the user asks ("Compare contents"), which is not in version one.
- **Verification after transfer:** rclone checks size and, where both sides have a common hash, the hash. The Done
  tab says "checked" only when a hash was compared.

## IPC

New `sites.*` messages, routed early in `DispatchAsync` (`HostCommandDispatcher.cs:311`) and answered in
`HostCommandDispatcher.Sites.cs`. Documented in a new `sites.*` section of `docs/ipc-protocol.md`.

| Message | Purpose |
| --- | --- |
| `sites.list`, `sites.save`, `sites.remove`, `sites.test` | Saved sites. Secrets go in, never come back out. |
| `sites.kinds` | The tiles and each kind's fields, so every client draws the same dialog. |
| `sites.browse` | One folder listing, paged for large folders, with the "on this PC" state per item. |
| `sites.trust` | Answer to a host-key or certificate question. |
| `sites.transfer.start`, `.pause`, `.resume`, `.cancel`, `.retry`, `.clear` | Batches. `.pause` stops the files in flight and `.resume` starts them again from zero. |
| `sites.mkdir`, `sites.rename`, `sites.delete`, `sites.count` | Remote changes and the count for the delete dialog. |
| `sites.authorize.start`, `.finish` | OAuth sign-in. |
| events `sites.changed`, `sites.activity`, `sites.transfer.changed`, `sites.trust.needed` | `sites.activity` is the small per-site summary the sidebar and status chip use; batched like the groups broadcast (`ScheduleGroupsBroadcast`). |

The snapshot on connect carries the saved sites and the activity summary only. Listings and transfer details are
sent to clients that have a site open.

## Clients

- **WinUI and Uno.** A Sites section is inserted into `CategoryList` by tag, as `SyncRemoteEntryPoints` does
  (`MainWindow.Remote.cs:155`). Selecting a site shows `SitesPanel` in place of the list, as `ShowProviderFull` does
  (`MainWindow.ProviderViews.cs:133`), and swaps the toolbar. A full toolbar swap has no precedent
  (`ProviderToolGroup` and `CancelPackageGroup` only add a group), so it is built as two button sets in
  `ToolbarPanel` with one visible. The speed graph bar is hidden in the Sites view; the Transfers bar takes its
  place. Options gets a Sites page through `SettingsWindow.AddPage`. Uno shares the files, so it follows.
- **Web UI.** A `sites` screen beside `TorBoxScreen.svelte`. "To this device" and "from this device" need two Server
  endpoints that stream between the browser and rclone; they follow the upload precedent in `TorrentUploads.cs` and
  the same auth and origin checks in `WebFront.cs`. The device side is in the first web UI release
  (owner, 2026-11-15). In bridge mode the stream passes through the Host's pipe; that is measured first, and if it
  is too slow the stream gets a faster path (for example the Server talking to rclone's own loopback address with
  credentials the Host hands it) rather than being left out.
- **Rework (SITES.11):** the screens above were rebuilt to the agreed mockup; `docs/plans/sites-rework.md` has the
  stages, what is not as drawn, and the picture pairs.
- **CLI.** `dm sites list|add|remove|test`, `dm sites ls <site>:<path>`, `dm sites get`, `dm sites put`,
  `dm sites rm`, `dm sites mv`, `dm sites mkdir`, `dm sites transfers`.
- **Extension.** Nothing (owner, 2026-11-15): no popup line and no new native-host message.

## The switch

`Options > Remote files > Use Remote files`, off until the user turns it on. It is also the place for "Use my own rclone", the
transfers-at-once limit and the default view.

- Off: no Sites section, no site toolbar, no chip, no rclone process, `sites.*` commands answer "Sites is off".
  Saved sites and their sealed secrets stay on disk and return when it is turned on.
- Turning it off while transfers run asks first, then stops them and rclone.
- A web session may add and edit sites only as admin (owner, 2026-11-15); flipping the switch follows the same rule
  (proposed). Until SITES.7 the Host refuses web sessions entirely.
- Site transfers have their own "transfers at once" limit here, apart from "downloads at once" (owner, 2026-11-15).
- If rclone is missing (not downloaded yet, or a bad own path), Sites shows one screen saying so with a Download
  rclone button (or Choose for an own path).

## Setup, updates, Docker, licences

- **Getting rclone (owner, 2026-11-15):** not in Setup and not in the update zip. Options > Remote files has "Download
  rclone": the Host downloads the official zip for this OS and CPU from `downloads.rclone.org`, checks its SHA-256
  against the value built into this Inbound release (never one read from the server), unpacks only the binary into
  `<state>\tools\rclone\<version>\` (a temp name, then a rename, so a cut download leaves nothing usable), and
  checks `rclone version` before using it. Per user, so no admin rights. Size: about 32 MB to download, 86 MB on
  disk (run point 10). Turning Sites on with no rclone offers the download first.
- **rclone updates (owner, 2026-11-15):** the desktop apps (WinUI and Linux) check for a newer rclone, and the Docker
  server updates its rclone too. The design below is proposed; the owner decided only that both update.
  - **Check:** the Host reads `https://downloads.rclone.org/version.txt` (`rclone vX.Y.Z`) once a day while Sites is
    on, and never with Sites off. The check reuses the Inbound updater's schedule and its offline handling.
  - **Verification, since a newer rclone can't have its SHA-256 built into an older Inbound:** the release's
    `SHA256SUMS` is fetched from `downloads.rclone.org` and from the GitHub release, and both copies must be
    identical. Its clearsigned OpenPGP signature must verify with rclone's release key, built into Inbound
    (`FBF737ECE9F8AB18604BD2AC93935E02FF3B54FA`, Nick Craig-Wood). The zip's SHA-256 must match the line for this
    OS and CPU. Checked on 2026-11-15 with gpg for 1.76.0: good signature, and the two copies are identical.
    The key is DSA-1024 and the file is signed with a SHA-1 digest, which is weak today. So the signature is one
    check among three, not the only one.
  - **Not too new:** a release is offered only once it is 7 days old (proposed), so a bad or withdrawn release is
    usually replaced first. A new major version (2.x) is never installed by itself; Options says so and asks.
  - **Install:** unpacked into a new `<state>/tools/rclone/<version>/` folder, then `rclone version` is checked. A
    short self-test follows: `rcd` starts, answers `core/version`, and `core/quit` ends it. Only then does it become
    the current version, at the next rclone start, never while a transfer runs. The previous version's folder is
    kept for a rollback: if the new one fails to start twice, the Host goes back and stops offering that version.
    Older folders are removed.
  - **Desktop:** a notice in Options > Remote files and in the app's notifications, "Update rclone" to install, and an
    "Update rclone automatically" switch, on by default (owner, 2026-11-15). The Inbound version's own pinned rclone stays the floor:
    an update never goes below it.
  - **Docker:** the image keeps its pinned rclone, which is used until a newer, checked one sits in the data volume
    (`/data/tools/rclone/<version>/`). The server checks and installs in the same way, automatically by default,
    because nobody is there to click. The web UI shows the notice to admins. A new image resets nothing: a
    downloaded version newer than the image's is kept, and one older than the image's is removed.
  - "Use my own rclone" is never updated by Inbound.
- `third_party\rclone\` holds the licence and a `SOURCE.md` with the pinned version, addresses and SHA-256 values per
  OS and CPU, like `third_party\7-Zip\`, and rclone's release signing key. The licence ships with Inbound (it is shown before the download).
- Uninstall treats the downloaded copy like the rest of the state, which it keeps (as uninstall does today).
- Docker: a pinned, checksummed download stage like the 7-Zip one (`Dockerfile:28-50`), linux/amd64 only (arm64 is
  on hold).
- There is no central third-party notices file today. Add `THIRD-PARTY-NOTICES.md` (7-Zip, rclone, the torrent
  worker's libraries) and link it from Help.
- The pinned rclone version is the floor and the first download. Newer ones come through the rclone update check
  above. "Use my own rclone" is the way to run any other version.

## Release notes draft (for the version that ships Sites)

For `changelog.json` when the version is set. No test status goes into release notes.

- New: Sites. Browse and change files on your own servers and cloud storage (SFTP, FTP and FTPS, WebDAV, S3 and
  S3-compatible storage, Google Drive, OneDrive, Dropbox and Backblaze B2), and move files between them and this PC.
  Turn it on in Options › Sites; Inbound downloads rclone, which does the work, the first time. Tags: all.
- New: Sites in the web UI, on a phone too, with files straight to and from the device the browser runs on. Tags:
  server, all.
- New: `dm sites` adds, tests, browses and changes sites, and starts and controls transfers. Tags: all.
- New: Help › About links the third-party notices. Tags: windows, linux.

## Release notes draft for the rework (SITES.11, SITES.12; used for 0.9.0, released 2026-11-18)

Rework R7 is closed and the draft below was used for the 0.9.0 release notes.

- Changed: Sites is now called Remote files. Each server or cloud account is a remote; `dm remotes` works as `dm sites`.
  Tags: all.
- New: All remotes, with "Open an address once", and Add a remote, which starts from the kind of remote and tests it
  before saving. Tags: all.
- New: browse a remote beside this PC's folder or in two panes, with right-click menus, double click to download or
  open, drag and drop, and "Some of it" on folders partly here. Tags: windows, linux.
- New: a transfers list like the downloads list, with stop, resume and retry for each transfer and per-file progress.
  Tags: all.
- New: Delete asks with the number of files; on this PC it goes to the Recycle Bin, and Shift+Delete deletes for
  good. Overwrite asks file by file, with sizes and dates. Tags: windows.
- New: the web UI has the same pages, made for a phone too: tick boxes, an action bar and bottom sheets. Downloads go
  to the server; "To this device" is a choice in the Download dialog. Tags: server, all.
- Changed: the extension's toolbar ring is easier to see, and a count shows downloads that finished since the popup
  was last opened. Tags: extension.

## FTP: undoing part of CUT.1

FTP comes back as a kind of site, not as a download address. So most of the cut stays:

- `ftp://` in Add URL, the clipboard, batch lines, import and the browser hand-off is still refused. The text changes
  from "FTP addresses aren't supported." to one that points at Sites when Sites is on. Proposed: "Add this server
  under Sites to use FTP."
- The old FTP engine code is not restored. `FtpAddresses.cs` and its refusal tests stay.
- The audit finding that drove the cut (a password inside a stored address) cannot recur, because a site never
  stores an address with a password.
- `docs/plans/feature-cuts.md` gets this as a note on the FTP line; the tick stays valid for FTP downloads.

## Phases

Each phase ends with evidence linked in PROGRESS; nothing is marked implemented without it.

| Phase | ROADMAP | Content | Done when |
| --- | --- | --- | --- |
| 0 | SITES.0 | rclone run: the list below. Measure sizes. Decide the open items. | Results in `remote-sites-rclone-run.md`, the changes here, a note in PROGRESS. |
| 1 | SITES.1 | `DownloadManager.Sites`: locate, start, supervise, stop rclone; rc client; idle stop; never outlives the Host. Download from Options (pinned version and SHA-256); in the Docker image; update check on the desktop and in Docker (signed checksums, rollback). | Supervisor tests like `WebUiSupervisorTests`; a kill test; idle shows no process. |
| 2 | SITES.2 | Site model, stores, sealed secrets, `sites.list/save/remove/test/kinds`, the Options switch, SFTP host-key trust. | Dispatcher tests; a site saved, Host restarted, tested again; changed key refused. |
| 3 | SITES.3 | `sites.browse` and the desktop Server only view: toolbar swap, breadcrumbs, stars, sidebar section. Read-only. | Walked in the app against a local test SFTP server, dark mode. |
| 4 | SITES.4 | Transfers: download and upload batches, stores, Transfers bar, conflicts, failures, retry, speed share, scheduler, sidebar activity, status chip. | Hash-matched files both ways; Host killed mid-batch and restarted; limits respected. |
| 5 | SITES.5 | Remote changes: new folder, rename, delete with count; "On this PC"; Two panes view and linked folders. | Each change checked on the server side; delete of a counted folder. |
| 6 | SITES.6 | Kinds beyond SFTP: FTP, FTPS (certificate trust, plain-FTP warning), WebDAV presets, OAuth kinds, S3 and B2, capability differences. | One real or emulated server per kind; no provider claims without a source. |
| 7 | SITES.7 | Web UI and phone, including "this device" streams; Docker image with rclone. | Web tests; a container run on the Docker test VM when Linux work resumes. |
| 8 | SITES.8 | CLI `dm sites`. | CLI integration tests. |
| 9 | SITES.9 | Notices file, docs (`ipc-protocol.md`, `architecture.md`, `new-frontend-guide.md`, `docker.md`), release notes. | Docs match the code. |

Phases 1 to 5 with SFTP alone are a usable first release behind the switch. Complex parts (the supervisor, trust,
batch durability, the IPC surface) stay with the main model; dialogs, CLI text and docs can be delegated.

## Tests

- **A test SFTP, FTP and WebDAV server without new tools:** rclone can serve all three from a local folder
  (`rclone serve sftp|ftp|webdav`), so integration tests start a second rclone as the server; this was enough for
  most run checks. A host-key change and key formats need an OpenSSH `sshd` (an unprivileged one works, key auth
  only); explicit FTPS needs a real FTP server (pure-ftpd in a container worked; `rclone serve ftp --cert` is
  implicit TLS). Fault injection (slow, drop mid-file, refuse writes) needs either a small proxy in
  `tools\TestServer` or read-only folders; to be decided in phase 1.
- Host-level tests follow `HostCommandDispatcherTests.cs` (real service over temporary `StoragePaths`) and
  `HostCommandDispatcherTorrentWorkerTests.cs` (a real child process).
- Tests that need rclone skip with a reason when the binary is absent, as the TorBox live tests do without a key.

## The rclone run's questions (phase 0, answered)

Read from rclone's documentation on 2026-11-13. Run on Linux on 2026-11-14 with rclone 1.76.0: the answers, with what
was run and seen, are in `remote-sites-rclone-run.md`. Nothing was run on Windows.

Each question with its answer (run point in brackets) and where the plan now says it.

1. `fs` as a JSON object with a password, with `--config ""`: works, and no file appears anywhere (1; "Credentials").
2. How the rc user and password are passed unseen: `RCLONE_RC_USER`/`RCLONE_RC_PASS`, never flags (2; "Start").
3. Host keys: unchecked by default; `host_keys` works from 1.75.0; a changed key is refused, in two message forms
   (3; "SFTP host keys", "Which binary").
4. Reading the host key for the first prompt: a small SSH handshake of our own; rclone's pin flag logs in first
   (4; "SFTP host keys").
5. Key files: unencrypted, old PEM and current OpenSSH with a passphrase all work in 1.76.0 (5; "Key files").
6. FTPS with a self-signed certificate: there is no per-site CA, only one CA file per rclone process or no check
   (6; "FTPS certificates"; one Host-managed CA file, owner 2026-11-15).
7. Partial files: `.partial` and rename except WebDAV uploads; nothing resumes (7; "Transfers"; accepted for now, owner 2026-11-15).
8. Progress per group, stopping one batch, `core/bwlimit` live: all work; there is no pause; per-batch speed limits
   not settled (8; "Transfers", IPC).
9. OAuth refresh with an in-memory config: a named remote and `config/get`; a real refresh seen for Drive and Dropbox, a
   dead grant fails with `invalid_grant` (9; "OAuth").
10. Start about 25 ms, idle about 69 MB, no idle CPU; 86 MB unpacked, 32 MB zipped; Linux, loaded machine
    (10; "Lifetime", "Setup").

## What the run changed (2026-11-14)

Summary. Since 2026-11-15 the sections above carry these changes; this list is kept as the record.

- **Version:** rclone 1.75.0 is the oldest that has `host_keys`. Inbound refuses an older rclone for SFTP.
- **Start:** the rc user and password go in `RCLONE_RC_USER` and `RCLONE_RC_PASS`, never in flags (flags show in the
  process list). rclone is never run with `-vv`, which logs them. `--cache-dir` and `--temp-dir` point into our state
  folder. `--config ""` reads and writes no file.
- **Secrets:** every password and key passphrase is obscured with `core/obscure` before use. rc error bodies repeat
  the secrets they were given, so they are never logged or shown as they are.
- **First-use host key:** rclone cannot report a server's key before a login, and its pin flag sends the credentials
  first. Inbound reads the key with a small SSH handshake of its own (key exchange only, no login), shows the
  fingerprint, and passes the accepted key in `host_keys`. A 50-line probe did this in the run.
- **Key files:** passphrase keys in the current OpenSSH format work. The conversion step is dropped.
- **FTPS:** there is no per-site CA. One Host-managed CA file (owner, 2026-11-15).
- **Resume:** no kind resumes a cut transfer; the file starts again from zero. Downloads, and SFTP and FTP uploads,
  go to `name.<hex>.partial` and are renamed at the end; WebDAV uploads have no temporary name. "Pause" is stop and
  start again, and the UI says so. Stray `.partial` files are removed after a crash. Accepted for now (owner, 2026-11-15).
- **Lifetime:** rclone keeps running, and keeps transferring, when its parent dies. The Host ties it to itself: a
  job object on Windows, `PR_SET_PDEATHSIG` on Linux.
- **Sign-in kinds:** one named in-memory remote per site; the token is read back with `config/get` after each batch
  and stored if it changed. A real refresh was seen for Drive and Dropbox on 2026-11-15 (see "Credentials and trust").
- **Size:** rclone.exe is about 86 MB unpacked and 32 MB zipped, about 43% on top of the current install. So it is
  downloaded from Options instead of shipped in Setup (owner, 2026-11-15).
- **Not known:** everything on Windows; timings on a quiet machine; per-batch speed limits
  (only the global limit was seen to work); a cut upload to a real WebDAV, FTP or cloud server.

## Open questions for the owner

None. All seven were answered on 2026-11-15 (see Owner decisions): 1 own limit; 2 admin only; 3 "this device" in the
first web UI release; 4 nothing in the extension; 5 downloaded from Options, in the Docker image; 6 one Host-managed
CA file; 7 no resume, for now.

## Rejected

- **A protocol library per kind** (SSH.NET for SFTP, FluentFTP, our own WebDAV): three code paths, three sets of
  trust and resume rules, and still no cloud storage.
- **rclone as an in-process library:** see above.
- **Transfers as rows in the main list:** see "Why transfers are not `DownloadJob`s".
- **The HTTP engine downloading through `rclone serve`** (the "adapter" in the second mockup page): it keeps
  segmented downloads and the `.part` naming, but only for downloads, and doubles the paths. Kept as a later option
  if the run shows rclone's own resume is poor.
- **Reading the user's `rclone.conf`:** it would put secrets we do not own in play. Later, as an explicit import.
