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
  only closes the UI; Kill/Exit is explicit.
- **Desktop.WinUI** is a client. It never touches the job store or engine. It
  connects, gets a snapshot, subscribes to events, and reconnects on failure.
- **NativeHost** is launched by Chrome per native-messaging connection. It
  validates/forwards frames, starts Host if needed, and exits when Chrome
  disconnects. Its stdout is protocol-only.
- **Cli** is a thin client for diagnostics and automation.

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

## Persistence layout

```
%LOCALAPPDATA%\DownloadManager\
  settings.json
  jobs\<jobId>.json
  jobs\<jobId>.part
  jobs\<jobId>.checkpoint.json
  history.jsonl
  logs\host-YYYYMMDD.log
  runtime\host.lock
```

- Job JSON writes use a temp file + `File.Replace`/atomic rename.
- `.part` is the contiguous payload; segmented data is written with
  `RandomAccess` at absolute offsets.
- Checkpoints carry validators (ETag/Last-Modified/length), completed ranges,
  and a schema version. They are written on pause and periodically during
  transfer.
- A startup reconciliation pass checks that metadata, checkpoint and part file
  agree; inconsistent jobs become recoverable failures with a clear reason.

## IPC

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
- Download URLs are restricted to http/https (FTP later) and cannot address
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
