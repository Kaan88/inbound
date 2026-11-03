# CLAUDE.md — agent onboarding

Read this first, then `GOAL.md`, `ROADMAP.md`, `reference notes`, and `PROGRESS.md`.
`GOAL.md` and `ROADMAP.md` are the fallback for the unavailable Goal/Todo tools;
treat them as authoritative tracking. Update them after every meaningful
increment.

## What this project is

A private-use Windows download manager with verified functional parity against
the installed **another download manager 6.43.12.3** (`v6.43b12 Full`). It is a
new C#/.NET application with a WinUI 3 frontend, a separate background host and
download engine, and our own Manifest V3 Chrome extension + Native Messaging
host. It is **not** a patch, crack, or license bypass of reference app, and it does not
copy reference app assets or code.

Pinned reference details and hashes: `notes/reference-version.md`.
Never commit reference app binaries, cookies, credentials, browser profiles, or captures
containing secrets.

## Current status (2026-11-04)

Phase 0 (baseline/setup) is in progress. See `PROGRESS.md` for the exact handoff,
current commit, and next action. Do not trust stale summaries here; `PROGRESS.md`
and `ROADMAP.md` win.

## Architecture

One per-user **Host** process owns engine, persistence, scheduler, tray, and IPC.
The WinUI app, CLI, and Chrome native host are clients. This is deliberate:
single-writer durability and one lifecycle authority, while keeping the UI
replaceable.

| Project | Purpose | TFM / notes |
| --- | --- | --- |
| `DownloadManager.Core` | domain models, states, policies, interfaces | `net10.0`; no WinUI/Windows App SDK |
| `DownloadManager.Contracts` | versioned IPC DTOs/commands/events | `net10.0` |
| `DownloadManager.Engine` | HTTP transfers, segmentation, resume, retries, limits | `net10.0`; no presentation |
| `DownloadManager.Persistence` | atomic job/settings/history stores + migrations | `net10.0` |
| `DownloadManager.Host` | background host, scheduler, named-pipe server, tray | `net10.0-windows` |
| `DownloadManager.Tray` | tray icon/menu library | `net10.0-windows`, WinForms |
| `DownloadManager.Desktop.WinUI` | replaceable frontend | `net10.0-windows10.0.19041.0`, WinUI 3 |
| `DownloadManager.NativeHost` | Chrome Native Messaging bridge | `net10.0-windows` |
| `DownloadManager.Cli` | headless control/diagnostics | `net10.0` |
| `extensions/chrome` | MV3 TypeScript extension, own ID/assets | Node/tsc |
| `tests/*` | unit + integration + fault tests | xUnit |
| `tools/TestServer` | controlled HTTP fault-injection server | `net10.0` |

IPC: per-user named pipe, current-user-only ACL, JSON envelopes with
`protocolVersion`, snapshot on connect then events. Native Messaging stdout is
protocol-only; log to stderr/file.

Browser strategy: the extension observes eligible Chrome downloads and sends
URL, final URL, cookies, referrer, UA and relevant headers to our native host;
the host durably records and ACKs before Chrome’s download is cancelled. On any
failure Chrome’s download is resumed. POST bodies and unsupported request types
stay in Chrome. See `docs/browser-integration.md`.

## Commands

```powershell
dotnet build DownloadManager.sln -c Debug
dotnet test DownloadManager.sln -c Debug
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\test.ps1
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

Extension:

```powershell
cd extensions\chrome
npm install
npm run build
```

disassembler MCP is a headless server on `http://127.0.0.1:8081/mcp`, project
`reference_project`. Start it with
`C:\Users\kaank\tools\disassembler-mcp\start-reference app-headless.cmd`; only one session may
hold the project. Call `get_program_info` before trusting addresses. The loaded
program is 32-bit x86, image base `0x00400000`, SHA-256 pinned above.

## Conventions

- Nullable enabled, implicit usings, file-scoped namespaces, async APIs with
  `CancellationToken`, no `.Result`/`.Wait()` on I/O.
- Engine must remain UI-agnostic and testable against the local HTTP server.
- All durable writes: temp file in same directory + atomic replace + fsync where
  practical. Checkpoints must be recoverable after a kill at any point.
- Never mark a parity row `implemented` without linked acceptance evidence.
  Use `partial`, `blocked`, or `unverified` otherwise.
- Do not add unlabelled stubs or settings that do nothing.
- Own branding/assets only; do not copy reference app icons, strings, or binaries.
- Keep `PROGRESS.md` current at each checkpoint; commit coherent increments.

## Known traps

- The OpenCode environment has **no Goal/Todo tools**; use the markdown files.
- `dotnet package search` initially listed only an offline source. Verify
  `nuget.org` is configured before assuming a package restore will work.
- WinUI 3 unpackaged apps need the Windows App SDK runtime or self-contained
  deployment; test `dotnet build` before expanding UI work.
- disassembler project lock: one headless server only. MCP config points at the HTTP
  server, not stdio, to avoid duplicate process locks.
- Never dump registry values containing cookies into committed files; redact.
