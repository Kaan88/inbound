# Architecture

## Goals

- A durable, headless-capable download engine that is testable without a UI.
- A replaceable WinUI 3 frontend with no access to engine internals.
- One authoritative per-user state owner.
- Browser handoff that never loses a download on a transient failure.
- Clear process and protocol boundaries that a future frontend or integration
  can target.

## Process model

```
┌──────────────────────────────┐        named pipe         ┌──────────────────────────┐
│ DownloadManager.Host.exe     │◀─────────────────────────▶│ Desktop.WinUI (client)   │
│  - engine + scheduler        │  JSON snapshot + events   │ Cli (client)             │
│  - persistence (single writer)│                          │ NativeHost (client)      │
│  - tray icon/menu            │                           └──────────────────────────┘
│  - named-pipe server         │
└──────────────┬───────────────┘
               │ HTTPS
               ▼
        remote HTTP servers
```

- **Host** is the only process that mutates jobs/settings. It is started at
  sign-in (optional) or on demand by the native host/CLI/UI. A per-user mutex
  enforces one instance.
- **Tray** is a library hosted inside Host. The menu can open the UI, pause all,
  resume all, show integration status, open settings, and exit. Closing the UI
  only closes the UI, never the Host; Kill/Exit is explicit. By default closing
  the window also ends the window's process (PERF.5, "Closing the window ends
  it"); the tray, a second launch or the Start menu entry starts it again.
- **Desktop.WinUI** is a client. It never touches the job store or engine. It
  connects, gets a snapshot, subscribes to events, and reconnects on failure.
- **NativeHost** is launched by Chrome per native-messaging connection. It
  validates/forwards frames, starts Host if needed, and exits when Chrome
  disconnects. Its stdout is protocol-only.
- **Cli** is a thin client for diagnostics and automation. It has `dm torrent` (inspect, add, files, priority,
  options, recheck, announce, details, seed, peers, trackers, pieces, network), over the same `torrent.*` messages as
  the windows. It has no TorBox (`remote`) or archive commands (owner's cut CUT.1, narrowed by CUT.2, 2026-11-12);
  those are handled in the window and the web UI, over the same IPC messages, which stay. `dm --help` lists what it
  has; an unknown command gets a one-line error and exit code 2.

- **rclone** (Sites, SITES.1 to SITES.9; `docs/plans/remote-sites.md`) is a child process of the Host, like the
  torrent worker: `rclone rcd` on a random loopback port with a per-start user and password, `--config ""` (nothing
  of rclone's own configuration is read or written) and `--rc-serve` (its served files, used for "this device"). It
  runs only while Sites is on and something uses it, and stops after a minute idle. Saved sites, with their secrets
  sealed, are Inbound's (`sites\<id>.json`); each use hands rclone the site as an in-memory remote. The desktop apps
  download rclone from Options › Sites (not in Setup); the Docker image ships it. `DownloadManager.Sites` holds the
  process, the client and the updater; `Runtime/Sites` holds the commands and transfer batches.

There is deliberately no separate engine process yet: a single writer simplifies
crash-safe persistence and lifecycle. The engine/persistence assemblies remain
independent and can move behind IPC later without contract changes.

## Project dependencies

```
Core            (no dependencies)
Contracts       (no dependencies except Core DTO mapping if needed)
Persistence ──▶ Core
Engine      ──▶ Core, Persistence (checkpoint interfaces)
Host        ──▶ Core, Contracts, Engine, Persistence, Tray
Tray        ──▶ Core, Contracts (UI only)
Desktop     ──▶ Contracts (plus Windows App SDK)
NativeHost  ──▶ Contracts
Cli         ──▶ Contracts
Sites       ──▶ Persistence (rclone process, client, updater, site stores)
Runtime     ──▶ Core, Contracts, Engine, Persistence, Sites (Sites commands and transfers)
Tests       ──▶ all of the above
```

Core/Engine/Persistence/Contracts target `net10.0` and have no WinUI or Windows
App SDK references. Windows-specific code lives in Host/Tray/Desktop/NativeHost.

## State model

Jobs have an explicit state machine:

```
Queued ─▶ Connecting ─▶ Downloading ─▶ Verifying ─▶ Completed
   │           │             │
   │           ├─▶ Retrying ─┘
   │           ├─▶ Paused ──▶ Queued
   │           └─▶ Failed (recoverable | permanent)
   └─▶ Cancelled
```

- **Paused** keeps partial data and is resumable.
- **Cancelled** is a user decision; partial data is removed by default.
- **Failed** carries a category and whether retry/resume is possible.
- **Completed** is only set after the expected byte count is satisfied and any
  configured hash check passes.

Every transition is persisted before it is published as an event. A crash
during a transition must reconcile to a safe state, never to `Completed`.

### Connection count changes on running downloads (UI.R21)

A changed "connections per download" reaches running segmented HTTP
transfers: a higher count adds workers that take an unclaimed range or split a
running one (the same steal path an idle worker takes), a lower one lets
workers above the count stop after their current range, or before taking one
if they were still waiting for a host connection. Every worker still
queues for the per-host fair limiter, and the checkpoint format is unchanged.

The per-host limit (`MaxConnectionsPerHost`) is shown as "Connections per server, all downloads" in Options, the
Downloads menu and the web UI (CONN.1); the connection profiles ("Per-site limits") only cap each download from a site.

- Ordinary and site-login downloads follow it unless their count was chosen
  by hand.
- Provider (TorBox) jobs always follow it when they run segmented (see the
  single-stream exception below), clamped by the provider's per-file
  cap (16) and the host limit of the current run's link, both kept in memory
  for the run. A change before the run has its link is taken when the link
  comes. A connection change never asks for or counts as a link refresh.
- Protected (pasted provider) links follow it unless chosen by hand, clamped
  by the same cap of 16.
- A single-stream run of any kind (HTTP, provider or protected) is not split
  while it runs: it started from no checkpoint with a count of one, it
  resumed a one-segment checkpoint, the server refused ranges, or the file was
  too small to split. Its record takes
  the new count, but the transfer keeps one connection. Once a single-stream
  run has saved bytes, its checkpoint has one segment and later runs resume it
  single-stream whatever the count says; only a run that starts with no saved
  bytes (or from a segmented checkpoint) uses the new count.
- The count is reconciled when any of its inputs changes: connections per
  download, the per-host limit, or a connection profile. A lower per-host
  limit alone therefore also lowers a running provider job's record.

## Persistence layout

```
%LOCALAPPDATA%\Programs\Inbound Download Manager\   program (app\, extension\, native-host\, install.json)
%LOCALAPPDATA%\Inbound Download Manager\             user data (kept on uninstall)
  settings.json
  jobs\<jobId>.json
  jobs\<jobId>.part
  jobs\<jobId>.checkpoint.json
  history.jsonl
  logs\host-YYYYMMDD.log
  runtime\host.lock
  sites\config.json                                 the Sites switch and options
  sites\<siteId>.json                               a saved site (secrets sealed)
  sites\transfers\<transferId>.json                 a transfer batch (SITES.4)
  tools\rclone\<version>\rclone.exe                  rclone, downloaded and checked (SITES.1)
  sites\rclone\                                     rclone's working folder (trusted certificates)
```

Site transfers keep the same payload rule: a download from a site is written by rclone straight into the folder the
user chose, never into the state directory.

- Job JSON writes use a temp file + `File.Replace`/atomic rename.
- `.part` is the contiguous payload; segmented data is written with
  `RandomAccess` at absolute offsets.
- Checkpoints carry validators (ETag/Last-Modified/length), completed ranges,
  and a schema version. They are written on pause and periodically during
  transfer.
- A startup reconciliation pass checks that metadata, checkpoint and part file
  agree; inconsistent jobs become recoverable failures with a clear reason.

### Deleting a download (UI.B19)

- **Files.** With "delete the file" the Host deletes the partial data (`.part` and the media side files
  `.part.video`, `.part.audio`, `.part.mux.mp4`) and the checkpoints. It deletes the file at the final path only
  when the download wrote it: a completed download's, or one recorded as written (`WrittenFinalPath`). An
  unfinished Overwrite download's final path names the user's own file, which stays. Without "delete the file"
  everything is kept, whether or not the transfer was still running.
- **The app's window and web UI.** An unfinished download always loses its partial data. "Delete from disk" is
  offered only when a finished file is selected and decides only the finished files; with both kinds selected the
  dialog says so. The extension popup asks only for a finished download, too, and an unfinished one's Delete removes
  only its partial data (a Host older than this rule could also delete a file at its final path). The command line
  keeps partial data with `--keep-file`.
- **Folders.** Only a provider file is put below the folder it was given (`Pack\sub\a.bin` under the package
  folder). The Host records on the job the folders it created for it, when it creates them (`MadeFolders`), and
  removes only those, once they are empty, deepest first and never recursively. A folder that already existed, the
  folder a download was given, and any folder the user typed or chose are never recorded. A recorded folder also
  stays when it is or holds a download, category, favorite, last-used, recent, extraction or server folder, when it
  is a drive or share root, when it is or lies under a link or junction, or when it is outside the folders of a
  limited caller (a web session on the desktop Host) that deleted the download. One that another download still
  uses passes to that download's record and goes with the last one. Paths are compared as the machine compares
  them; on Windows also by what they lead to (`GetFinalPathNameByHandle`), so a mapped drive and its share, an 8.3
  name and a `\\?\` form are one folder. A folder that can't be identified stays. A cancel that deletes the data
  removes the folders too, and a later start makes and records them again. A removal a crash interrupted is finished
  at the next start, folders included. Records written before this rule have no folders listed, so nothing is
  removed for them.
- **Leftover partial files.** `dm host clean-orphans` (and Clean up leftover partial files in the window) finds
  `.part` files no download owns in the download folders. Quarantine moves them into a `.dm-orphans` folder next to
  them; Delete deletes them. What is already in `.dm-orphans` is kept for the user: it is never rescanned or deleted.
  The folder is removed once it is empty, at that command (only inside a limited caller's folders) and at each
  start, and when it is all that is left in a folder made for a download that is deleted.

## IPC

Message-level details, including the remote provider messages and why slow calls use their own
connection, are in `docs/ipc-protocol.md`.

- Pipe name includes the current user SID hash:
  `\\.\pipe\DownloadManager-<sidHash>`.
- `PipeSecurity` grants full control to the current user SID and SYSTEM only;
  everyone else is denied.
- Frames are 4-byte little-endian length + UTF-8 JSON. Max frame size enforced.
- Every message has `v` (protocol version), `type`, `id`, and optional
  `correlationId`.
- On connect the server sends `server.hello` then `jobs.snapshot`; subsequent
  `job.event` messages carry deltas. A client that disconnects can reconnect and
  get a fresh snapshot. No client-specific authoritative state.
- Version mismatch returns `server.incompatible` with supported range and
  closes; clients show an actionable upgrade message.

## Browser integration

See `docs/browser-integration.md`. The extension uses a durable request id; Host
dedupes `(extensionId, requestId)` and only ACKs after the job is durably
recorded. Failure leaves Chrome's download intact.

## Security

- No listening TCP ports for local control; named pipes only.
- Native host validates message shape, length, and origin; rejects unknown
  types; never executes URLs/commands from messages.
- Credentials are kept out of logs and diagnostics; optional DPAPI protection
  for stored site credentials.
- Destinations are resolved by Host policy; path traversal and reserved names
  are rejected.
- Sites: rclone listens on loopback only, with credentials made for each start and given through its environment,
  not its command line. A site's password, key, token or client secret is sealed like every other stored secret
  (`SecretProtector`: DPAPI on Windows, the AES-GCM key file in the server's data folder, the Linux Host's own choice) and never logged; a server's SSH key or self-signed certificate is trusted
  only after the user saw its fingerprint. A web session or a server caller is held to its folders on the computer
  Inbound runs on, and can't choose a program for Sites to run (`docs/ipc-protocol.md`, "Sites").
- A finished download from the network is handed to Windows as a browser hands over its own (L3,
  `Runtime/DownloadScan`, `IAttachmentExecute::Save`): the registered antivirus checks it and the internet mark
  (`Zone.Identifier`, zone 3, the site only) is written, before anything extracts or opens it. A file the antivirus takes
  away fails the download, which is not retried by itself. Off Windows, or when the check can't run, only the mark is
  written (REV.4).
- Download URLs are restricted to http/https (FTP was cut in 0.7.2: an
  ftp:// address is refused with "FTP addresses aren't supported.", and an FTP
  download an older version left fails with it) and cannot address
  `file://`, `localhost` administrative endpoints via browser handoff.

## Replaceability checklist

A future frontend must be able to:

1. connect to the pipe and negotiate version;
2. obtain a snapshot;
3. subscribe to events;
4. issue add/pause/resume/cancel/reorder/settings commands;
5. never need the job store or engine assemblies.

The WinUI project is the only project that references Windows App SDK. Replacing
it must not change Engine, Persistence, Contracts, or Host behavior.
