# Torrent T1: design (2026-11-11)

T1 of `native-torrent-roadmap.md`: the smallest useful slice. Owner decisions are in that file's section 6 (item 7 is
the UI, from the mockups at https://claude.ai/artifact/Lky3Wvsdq76aCjNqRTmRH6). This file says how T1 is built.
Seeding stays off; outgoing connections only; no firewall rule.

## 1. A torrent is one job

One `DownloadJob` per torrent, not one per file. It takes one download slot, sits in one queue, and Resume, Stop,
Stop All, Delete, the scheduler, quotas, completion actions and sleep prevention apply to it unchanged, because they
already work on jobs. The list shows it like a package (a parent row whose children are its files), but the children are
display rows built from the torrent's file list, not jobs.

`DownloadJob.Torrent` (new, `TorrentJobInfo`, null for every other job) holds:

- `InfoHash` (v1, lower-case hex), `Name`, `Magnet` (the address as given, or null for a .torrent file), `HasMetadata`.
- `Files`: index, relative path, size, `Wanted`; filled when the metadata is known. `FileCount` and `WantedBytes`.
- `AwaitingFileChoice`: the metadata arrived and the user wants to choose files first; the transfer holds until they do.
- `Peers`, `Seeds`: last reported, for the status text (not persisted on every change).

`Url` is the magnet (a .torrent job gets a magnet built from its hash and name), so de-duplication, export and the
description keep working. `TryValidateUrl` accepts `magnet:` only when torrents are on and the Host has the worker.

Per-file progress is not stored on the job; the details window and an expanded row ask for it (`torrent.files`).

## 2. Payload placement (an adaptation of the part-file rule)

The rule says one part file `<final>.<jobId>.part` in the destination, renamed on completion. A torrent has many files
and libtorrent writes each one at its own offsets, so T1 applies the rule per file: every wanted file is written as
`<path>.<jobId:N>.part` inside the destination, through libtorrent's own file renaming, and on completion the worker
renames each back (libtorrent `rename_file`, so its resume data stays correct). Pieces that overlap unwanted files go to
libtorrent's `.parts` file in the same folder. Nothing goes to the state directory except metadata and resume blobs.
A single-file torrent's file is saved straight in the destination; a multi-file torrent's under a folder named after
the torrent (libtorrent's layout), so `FinalFilePath` is that folder.

## 3. Engine and supervisor

- `TorrentDownloadEngine : IDownloadEngine` (in `DownloadManager.Torrents`). `ExecuteAsync` attaches the job to the
  shared worker, maps worker progress to `TransferProgress` (done/wanted bytes, rate, ETA, peers as connections, phase
  `Getting file list`, `Checking files`, `Waiting for file choice`, `Downloading`), and returns when the torrent completes
  (after the final renames) or the token is cancelled (graceful pause, last resume blob saved, torrent removed from the
  worker). `DownloadManagerService` picks it when `job.Torrent != null`, beside the FTP choice.
- `TorrentWorkerSupervisor`: the only owner of the worker process. States Off, Starting, Active, Draining. It starts the
  worker on the first attach, sets listen (all interfaces, port 0, DHT on, LSD/UPnP/NAT-PMP off), limits and the
  progress interval, and closes it 30 s after the last detach (Draining; a new attach in that window reuses it). A worker
  that dies is started again once per 2 minutes and every attached torrent is added back from its last resume blob
  (gate 14); a second death in that window fails the attached jobs with a retryable error.
- Updates the job needs (metadata, file list, resume blobs) go through `ITorrentJobStore`, which the service implements:
  it saves the info dictionary and resume blob under `torrents/` in the data folder (atomic binary writes) and updates
  `job.Torrent` under the service lock, then raises `JobChanged`.
- Speed limits: the shared download budget's limit is sent to the worker (`setLimits`); per-job limits via
  `setTorrentLimits`. The worker's own transfer is not throttled by the HTTP budget object.
- Worker changes for T1: `add` takes `partSuffix` (renames every file) and `hold` (stay in upload mode after metadata
  until `setFilePriorities`); new `finalize` (rename every wanted file to its own name, reply when libtorrent confirms).
  Review change (2026-11-11): the top-level name is picked at `finalize` (`rootName`), not on add, as a free name when
  the disk or another download has the torrent's; `finalize` refuses when any target exists; an add of a finalized
  torrent sends `finalized` so its files keep their own names whatever the resume data says.

## 4. Settings, capability, kill switch

- `AppSettings`/`SettingsDto`: `TorrentsEnabled` (false), `TorrentRoute` (`ask`, `thisPc`, `provider`; default `ask`,
  shown only with a provider account), `TorrentChooseFiles` (true; multi-file torrents only), `TorrentHandler` (false).
  The mockup's "Speed limit for torrents" field is left out of T1: it would only repeat the global limit.
- Capability `torrents` is appended to the hello list when the Host has the worker and `DOWNLOADMANAGER_TORRENTS` is not
  `off`. With the switch off the Host refuses torrent commands and never starts the worker, whatever the setting says.
- Off (the default): no process, socket, timer or file (gate 1). Turning it off with torrents in the list stops them.

## 5. IPC

- `torrent.inspect` `{input}`: a magnet or a .torrent (path on this PC, or upload reference for another server) →
  `{ok, error, kind, infoHash, name, totalSize, files[], existingJobId}`. A .torrent is parsed by the Host
  (`TorrentMetadata`); a magnet returns only its hash and name (the files come later).
- `torrent.add` `{input, destination, queueId, startMode, files: all | chosen indexes | askWhenReady}` → `{ok, error,
  job}`. Existing hash → `existing` with that job.
- `torrent.setFiles` `{jobId, wanted indexes}` (also ends `AwaitingFileChoice`), `torrent.files` `{jobId}` → files
  with `done` bytes (asks the worker when the torrent is attached, else the saved file list).
- `JobDto.Torrent`: `infoHash`, `name`, `hasMetadata`, `awaitingFileChoice`, `fileCount`, `wantedCount`, `peers`,
  `seeds`. No file list in job events (a torrent can have 100,000 files).

## 6. Windows app

As the mockups: Options › Torrents page; Tasks › Add Magnet… / Import Torrent File… also without a provider; Add URL,
drag and drop and Ctrl+V lead to the torrent dialog; the dialog lists lines (reusing `AddLineSession`/`AddLinksDialog`
patterns), a This PC / TorBox switch per line when an account exists, and the TorBox cache result on every magnet and
.torrent line (`remote.classify` with files; the check never picks the route); the file tree reuses
`RemoteFileTreeView`; Torrents sidebar filter (only while on); torrent rows as package-like rows; a status-bar chip while
the worker runs; Progress Details shows the summary and a Files list for a torrent. The magnet and .torrent handler is a
per-user registration (HKCU) made when the option is ticked and removed when unticked or on uninstall.

## 7. Order of work

1. Worker additions (`partSuffix`, `hold`, `finalize`) with tests.
2. Core/Contracts/Persistence: `TorrentJobInfo`, DTOs, settings, binary blob store, capability and kill switch.
3. Supervisor and engine, wired into the service and the three Host compositions; integration test against a loopback
   seeder (`DownloadManager.Torrents.Tests` has the seeder harness).
4. IPC commands and dispatcher tests.
5. Windows app: Options, menu, dialog, rows, filter, chip, details.
6. TorBox cache check per line; magnet/.torrent handler.
7. Packaging: Setup, update zip, AppImage, Docker builder stage; kill-switch test (no process, no socket); idle
   comparison with the feature compiled in and off.
   As built: `scripts\publish.ps1` copies `artifacts\torrent-worker\win-x64\inbound-torrent.exe` (or `-TorrentWorker`)
   next to the Host and stops before building when it is missing; Setup and the update zip take the published folder,
   and `stop-for-setup.ps1` closes a worker that outlives the Host. `package-appimage.sh` puts
   `artifacts/torrent-worker/linux-x64/inbound-torrent` (or `TORRENT_WORKER`) next to `inbound-host` and stops without
   it. The `Dockerfile` builds the worker in a `torrent-worker` stage (the steps of `Dockerfile.build`), which
   `--build-context torrent-worker=<folder>` can replace with a worker built before. The kill-switch test
   (`TorrentKillSwitchHostTests`) runs the real Host with a stand-in worker: with `DOWNLOADMANAGER_TORRENTS=off` no
   `torrents` capability and the commands are refused even with the setting on; with the setting off the capability is
   there and add/inspect say torrents are off; in both no worker process starts.
