# Sites: integration plan (FTP, FTPS, SFTP, WebDAV and cloud storage through rclone)

Status: plan only, 2026-11-13. Nothing is built. Tracked as ROADMAP SITES.1 to SITES.9.

## What this is

A Sites view in Inbound: saved remote places, a file browser for each, and uploads, downloads, delete, rename and new
folder. Every protocol runs through one bundled `rclone` process that the Host controls. The agreed design is the
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

## The agreed design in short

- Selecting a site in the sidebar replaces the download toolbar with a site toolbar: Download, Upload, New folder,
  Rename, Delete on server, Refresh, Pause transfers, Edit site, Options.
- There is no Connect or Disconnect. Selecting a site lists it. Trouble shows in the pane with Retry and Edit site.
- Two views: Server only (the default) and Two panes, with arrow buttons between the panes, breadcrumbs, starred
  folders and a linked local folder per site.
- An "On this PC" column: Same, Older here, Some of it, Not here. It compares name, size and time only.
- Transfers live in a slim bar that opens upward, with tabs Running and waiting, Failed, Done. One row per action; a
  folder is one row that opens.
- Site transfers never appear in the main downloads list, the Finished view or the extension popup's list. The status
  bar has one chip, and each sidebar site shows arrows with counts, a clock for waiting ones and a progress line.
- "Delete on server" always asks, with the count and size. Local delete goes to the Recycle Bin.
- Add a site by picking a tile, then four fields; the rest is under Advanced, with a Test button.
- Web UI: the same view. "Here" means the machine Inbound runs on. Download and Upload ask "the server or this
  device". Phone: one pane, bottom sheets. Extension: at most one "Sites" line in the popup.

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
| `DownloadManager.Desktop.WinUI` (shared with Uno) | `MainWindow.Sites.cs`, a `SitesPanel` control hosted like `ProviderFullHost`, site toolbar, Add a site dialog, Options page. |
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
  start, `--config ""` so nothing is read from or written to a config file, and no web GUI. The docs call rc access
  "equivalent to shell access", so the secret never goes on the command line where another process can read it; it is
  passed by environment variable or an htpasswd file in the state folder (to be settled in the rclone run).
- **Lifetime:** started when a site is selected, when a transfer is running or waiting to retry, or when the
  scheduler starts one. Stopped after an idle delay once no client has a site open and nothing is transferring. With
  Sites off it never starts. Restart with growing delays and a cap, as `WebUiSupervisor` does (`MaxRestarts = 5`).
- **Never outlives the Host:** a Windows job object (kill on close) plus stopping it at shutdown; on Linux the parent
  death signal. `TorrentWorkerSupervisor` is the pattern to copy.
- **Which binary:** `tools\rclone\rclone.exe` next to the Host, or the path set in Options ("Use my own rclone"), or
  `INBOUND_RCLONE` for tests, as `TorrentSetup.cs` does for the worker. A version check at start; too old shows a
  clear message, not a broken view.
- **Idle cost:** none when not running. No polling of sites in the background. Sidebar activity comes from the
  Host's own transfer records, not from asking rclone.

## Credentials and trust

- A site's settings are stored by Inbound in `sites/{id}.json`. Secrets (password, key passphrase, OAuth token,
  access keys) are sealed with `SecretProtector` (DPAPI on Windows, AES-GCM with `secrets.key` or the keyring
  elsewhere). No password is ever part of an address; an address with one is refused, as today.
- For each call the Host builds the remote from the stored site and passes it in the rc call's `fs` field as a JSON
  object of backend options. rclone's documentation allows this. Secrets exist in rclone's memory only.
- **The user's own `rclone.conf` is never read or written in version one.** Importing remotes from it can come later
  as an explicit action.
- **SFTP host keys.** rclone does not check the host key by default and never asks. So Inbound does it:
  1. On first use the Host fetches the server's public key (how: see "To settle in the rclone run").
  2. The window shows the fingerprint and asks. Nothing is sent to the server before the user says yes.
  3. The key is stored with the site and passed to rclone as `host_keys` on every call.
  4. If rclone reports a different key, the site stops with both fingerprints and a hard warning. There is no
     "continue anyway" button; the user must open Edit site and replace the key.
- **FTPS certificates.** rclone offers a CA file or "do not verify", not pinning. Plan: a certificate that fails
  verification is shown with its fingerprint; if the user trusts it, it is stored with the site and passed as that
  site's CA. "Do not verify" is never offered. To be tried in the run.
- **Plain FTP** is a per-site choice with a warning in the dialog and a permanent "Not encrypted" chip in the pane.
- **OAuth kinds** (Drive, OneDrive, Dropbox): sign-in through the browser with rclone's own flow; on a server with no
  browser, the `rclone authorize` paste flow from the earlier mockups. The Host stores the token sealed and must
  write back a refreshed token after each use. How it learns of a refresh is not documented; see the run list.
- **Key files:** rclone reads passphrase-protected keys only in the old PEM format. The Add a site dialog must detect
  the current OpenSSH format with a passphrase and say what to do, or the Host converts a copy in memory. Open.
- **Web sessions** keep their folder limit: local paths in any `sites.*` command go through `CallerScope` and
  `PathRoots` (`WebSessionProfile.cs:85`). Remote paths are not limited. Whether a web session may add or edit sites
  follows `SessionRights`; the default proposed is admin only.

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
- **Payload placement.** Downloads land in the destination folder under a temporary name and are renamed in place
  (rclone's partial-file behaviour for local disks), so there is still no temp directory and no cross-volume copy.
  The name is rclone's, not `<final>.<jobId>.part`; that convention stays as it is for download jobs. Uploads use a
  temporary name on servers that can rename. Both to be confirmed in the run.
- **Resume.** A stopped batch continues with the files not yet done. Whether a half-done large file continues or
  starts over depends on rclone and the site kind; to be measured in the run and stated honestly in the UI.
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
| `sites.transfer.start`, `.pause`, `.resume`, `.cancel`, `.retry`, `.clear` | Batches. |
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
  the same auth and origin checks in `WebFront.cs`. In bridge mode the stream passes through the Host's pipe; if
  that is too slow, version one offers server-side transfers only in bridge mode. To be measured.
- **CLI.** `dm sites list|add|remove|test`, `dm sites ls <site>:<path>`, `dm sites get`, `dm sites put`,
  `dm sites rm`, `dm sites mv`, `dm sites mkdir`, `dm sites transfers`.
- **Extension.** One optional line in the popup from the activity summary, which means one new message on the
  native-host allow-list (`NativeBridgePolicy.cs:27`). Last, and only if still wanted.

## The switch

`Options > Sites > Use Sites`, off until the user turns it on. It is also the place for "Use my own rclone", the
transfers-at-once limit and the default view.

- Off: no Sites section, no site toolbar, no chip, no rclone process, `sites.*` commands answer "Sites is off".
  Saved sites and their sealed secrets stay on disk and return when it is turned on.
- Turning it off while transfers run asks first, then stops them and rclone.
- A web session may not flip it unless `WebSettingsRules.cs` lists it; proposed: admin only.
- If rclone is missing (unticked in Setup, or a bad path), Sites shows one screen saying so with a Repair button.

## Setup, updates, Docker, licences

- `third_party\rclone\` holds the official Windows build, its licence and a `SOURCE.md` with the version, address
  and SHA-256, like `third_party\7-Zip\`. It is linked into `tools\rclone\` and added to the required-file list in
  `scripts\publish.ps1:220`. The installer copies the whole publish folder, so no `.iss` change is needed for the
  file itself; a Setup component tick ("Sites support, rclone") is optional.
- The update zip (`scripts\package.ps1`) carries it like any other app file. Size against the 200 MB install is to
  be measured; the zip is about a third of the unpacked exe, by rclone's usual numbers, not ours.
- Docker: a pinned, checksummed download stage like the 7-Zip one (`Dockerfile:28-50`), linux/amd64 only (arm64 is
  on hold).
- There is no central third-party notices file today. Add `THIRD-PARTY-NOTICES.md` (7-Zip, rclone, the torrent
  worker's libraries) and link it from Help.
- rclone updates only with Inbound releases. "Use my own rclone" is the way to run a newer one.

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
| 0 | SITES.0 | rclone run: the list below. Measure sizes. Decide the open items. | Results written here and in PROGRESS. |
| 1 | SITES.1 | `DownloadManager.Sites`: locate, start, supervise, stop rclone; rc client; idle stop; never outlives the Host. Bundled in publish. | Supervisor tests like `WebUiSupervisorTests`; a kill test; idle shows no process. |
| 2 | SITES.2 | Site model, stores, sealed secrets, `sites.list/save/remove/test/kinds`, the Options switch, SFTP host-key trust. | Dispatcher tests; a site saved, Host restarted, tested again; changed key refused. |
| 3 | SITES.3 | `sites.browse` and the desktop Server only view: toolbar swap, breadcrumbs, stars, sidebar section. Read-only. | Walked in the app against a local test SFTP server, dark mode. |
| 4 | SITES.4 | Transfers: download and upload batches, stores, Transfers bar, conflicts, failures, retry, speed share, scheduler, sidebar activity, status chip. | Hash-matched files both ways; Host killed mid-batch and restarted; limits respected. |
| 5 | SITES.5 | Remote changes: new folder, rename, delete with count; "On this PC"; Two panes view and linked folders. | Each change checked on the server side; delete of a counted folder. |
| 6 | SITES.6 | Kinds beyond SFTP: FTP, FTPS (certificate trust, plain-FTP warning), WebDAV presets, OAuth kinds, S3 and B2, capability differences. | One real or emulated server per kind; no provider claims without a source. |
| 7 | SITES.7 | Web UI and phone, including "this device" streams; Docker image with rclone. | Web tests; a container run on the Docker test VM when Linux work resumes. |
| 8 | SITES.8 | CLI `dm sites`. | CLI integration tests. |
| 9 | SITES.9 | Extension popup line, notices file, docs (`ipc-protocol.md`, `architecture.md`, `new-frontend-guide.md`, `docker.md`), release notes. | Docs match the code. |

Phases 1 to 5 with SFTP alone are a usable first release behind the switch. Complex parts (the supervisor, trust,
batch durability, the IPC surface) stay with the main model; dialogs, CLI text and docs can be delegated.

## Tests

- **A test SFTP, FTP and WebDAV server without new tools:** rclone can serve all three from a local folder
  (`rclone serve sftp|ftp|webdav`), so integration tests start a second rclone as the server. Fault injection
  (slow, drop mid-file, refuse writes) needs either a small proxy in `tools\TestServer` or read-only folders; to be
  decided in phase 1.
- Host-level tests follow `HostCommandDispatcherTests.cs` (real service over temporary `StoragePaths`) and
  `HostCommandDispatcherTorrentWorkerTests.cs` (a real child process).
- Tests that need rclone skip with a reason when the binary is absent, as the TorBox live tests do without a key.

## To settle in the rclone run (phase 0)

Read from rclone's documentation on 2026-11-13; none of it has been run.

1. `fs` as a JSON object with a password, with `--config ""`: works, and no file appears anywhere.
2. How the rc user and password are best passed so they are not visible to other processes.
3. Host keys: unchecked by default; `host_keys` accepted; a changed key refused, and what the error looks like over
   rc. Which rclone version added `host_keys`.
4. How to fetch the host key for the first prompt: `ssh-keyscan` is not on every Windows PC, so either rclone's
   one-shot pin flag against an in-memory remote and then reading the key back, or a small SSH handshake of our own.
5. Key files: unencrypted, old PEM with a passphrase, current OpenSSH with a passphrase.
6. FTPS with a self-signed certificate passed as that site's CA.
7. Partial files on download and upload; what an interrupted large file does on resume.
8. Progress and per-batch stats over rc; pausing and cancelling one batch; `core/bwlimit` while running.
9. OAuth token refresh with an in-memory config: where the new token can be read.
10. Start time, idle memory, and the size of the exe unpacked and in the Setup.

## Open questions for the owner

1. Do site transfers count against "downloads at once", or have their own limit (proposed: their own)?
2. May a web session add and edit sites, or admin only (proposed: admin only)?
3. Is "to this device" and "from this device" in the first web UI release, or server-side only at first?
4. Is the extension popup line wanted at all?
5. Setup: is rclone always installed, or a component the user can untick?

## Rejected

- **A protocol library per kind** (SSH.NET for SFTP, FluentFTP, our own WebDAV): three code paths, three sets of
  trust and resume rules, and still no cloud storage.
- **rclone as an in-process library:** see above.
- **Transfers as rows in the main list:** see "Why transfers are not `DownloadJob`s".
- **The HTTP engine downloading through `rclone serve`** (the "adapter" in the second mockup page): it keeps
  segmented downloads and the `.part` naming, but only for downloads, and doubles the paths. Kept as a later option
  if the run shows rclone's own resume is poor.
- **Reading the user's `rclone.conf`:** it would put secrets we do not own in play. Later, as an explicit import.
