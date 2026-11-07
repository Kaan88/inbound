# Plan: headless server, web UI and Docker (D1 to D4)

Status: plan, 2026-11-07. Nothing here is built yet. ROADMAP D1 to D4 and PERF.1 to PERF.3 track it.

## Memory today (measured 2026-11-07, build 0.5.1)

Private bytes (memory the process holds for itself), separate test instance, local test server:

| Situation | Host (engine) | WinUI window |
| --- | --- | --- |
| Idle, empty list | 19 MB | 215 MB (owner's own copy, idle) |
| 500 queued jobs | 48 MB | - |
| 500 queued, 4 x 500 MB downloading at local speed | 69 to 72 MB, flat | - |
| Same, window open on 504 rows | 74 MB | 233 to 235 MB |

What this says:
- The engine is small, and its memory stays flat during transfers; payload bytes go straight to the part file.
- About three quarters of the total is the WinUI window. It costs the same with 0 rows or 504, so it is the
  WinUI runtime itself, not our list. Closing the window only hides it, so it keeps that memory all day.
- A Docker server has no WinUI at all, so it starts from the Host numbers.

Not measured: hours-long runs, real internet speeds with retries, thousands of jobs, TorBox polling, extraction.

### Optimisation steps

- **PERF.1 Close the window process when the window closes.** The Host keeps the tray, and opening the window
  starts the UI again (about 1 to 2 seconds, to measure). That saves about 215 MB while Inbound sits in the
  tray. It could be an option, "Keep the window loaded for faster opening", off by default. Needs the owner's call.
- **PERF.2 Soak test.** 8 hours of downloads in a test instance, recording Host and UI memory every minute, with
  5,000 jobs in the list. Growth means a leak, and shows where it is.
- **PERF.3 GC settings, only after measuring.** For the server: `DOTNET_GCHeapHardLimit` and `GCConserveMemory`
  in the image, and a check of the Host's GC mode. No change without numbers before and after.

## D1 Headless server

A new `DownloadManager.Server` project (`net10.0`, ASP.NET Core) runs what the Host runs today, minus the
Windows parts: Runtime, Engine, Persistence, Archives, Containers and the TorBox provider.

- **One command handler, two transports.** `HostPipeServer.DispatchAsync` already turns an IPC message into
  an answer. Move that into a transport-free `HostCommandDispatcher` that both use: the Host keeps its named
  pipe, and the Server serves the same JSON envelopes (with `protocolVersion`) over a WebSocket. The web UI then
  speaks the existing protocol, gets a snapshot on connect and events after, and no command is written twice.
- **Windows-only parts and what replaces them:**

| Today | On the server |
| --- | --- |
| `SecretProtector` (DPAPI) for site logins, proxy and TorBox key | AES-GCM with a key file in the state volume, mode 600. A backup of the volume carries the key; documented. |
| Bundled `7z.exe` | The official 7-Zip Linux build (`7zz`, x64 and arm64, same LGPL and unRAR terms), chosen by OS. |
| `ToolLifetime` (job object kills 7-Zip with the Host) | `Process.Kill(entireProcessTree)` on cancel and on shutdown. |
| `ExtractionDurability`, `SparseFile` | Already skip off Windows. Use `FileStream.Flush(true)` for fsync. |
| Mark of the web (zone streams) | Not on Linux; skipped. |
| Tray, sounds, clipboard, sleep prevention, shutdown actions, start at sign-in, native-host registration, updater | Not in the server. The image updates instead. |

- **Paths:** state in `/data` (`DOWNLOADMANAGER_DATA` already exists), downloads under `/downloads`.
- **Case-sensitive file systems:** the check that refuses `a.txt` next to `A.TXT` is for Windows. Decide
  whether it stays on Linux.
- **Tests:** Core, Engine, Persistence, Runtime, Archives, TorBox and Containers run on Linux in a container.
  The integration tests that talk to the Host run against the Server too.

## D2 Web UI

- **Technology:** TypeScript, as in the extension, with Preact and esbuild: a small bundle and a short
  toolchain. Served by the Server as static files. Owner decision.
- **Screens, in this order:**
  1. Download list with live progress, packages, the state views and bulk actions.
  2. Add links: single, batch, and TorBox links, magnets and torrent-file upload.
  3. Extraction status, and the password prompt.
  4. Queues and scheduler.
  5. Settings, site logins and proxy.
  6. My TorBox, and hoster status.
- **Folder browser,** the owner's main complaint about another download manager in Docker: the Server lists
  folders only under allowed roots (`DOWNLOADMANAGER_ROOTS`, for example `/downloads`). It shows favorites with
  free space (the UI.R9 and UI.R10 calls), can create a folder, and checks a typed path. Nothing outside the
  roots is reachable, with symlinks resolved before the check.
- **Sign-in:** one user. A password is set on first open, or from an environment variable, and stored hashed.
  Session cookie (`HttpOnly`, `SameSite=Strict`), origin check on every change, a short lock-out after failed
  tries. Only `/health` answers without signing in. TLS comes from a reverse proxy; the docs show one.
- **Browser extension:** native messaging cannot reach a container. Sending from Chrome to the server would
  need the extension to call it over HTTPS with a token. That comes later, as its own item.

## D3 Docker

- **Image:**
  - A multi-stage build: the .NET 10 SDK builds it, and the `aspnet:10.0` runtime image runs it (a chiseled
    variant if 7-Zip runs there).
  - It runs as non-root, with `PUID` and `PGID` so files on a NAS get the right owner.
  - Volumes `/data` and `/downloads`, port 8080, a `HEALTHCHECK` on `/health`.
  - Built for `linux/amd64` and `linux/arm64`.
- **Compose:** an example file with the two volumes and the password variable.
- **Publishing:** an image on GHCR (`ghcr.io/kaan88/inbound`) built on each tag, if the owner wants it
  public. That is a public step, so it waits for a yes.
- **Target:** under 100 MB of memory when idle in the container, measured in D4.

## D4 End-to-end test

As in ROADMAP: in the Linux container, through the web UI:
- TorBox link → queue → download → extract;
- expiring-link refresh;
- the image and its logs contain no token;
- container memory measured when idle and during a download.

## Order and size

1. **D1a:** extract `HostCommandDispatcher`; the Host keeps working (all tests).
2. **D1b:** the Server with a WebSocket and the Linux replacements, running on Linux in a container,
   driven by tests.
3. **D2a:** a first web UI (list, add links, folder browser, sign-in), in a container on the owner's machine.
4. **D3:** image and compose, then the rest of D2.
5. **D4:** the end-to-end run.

PERF.1 and PERF.2 are independent of these and can come first.

## Decisions for the owner

Answered 2026-11-08:

1. PERF.1: the window process closing with the window is a future idea (ROADMAP PERF.5), not part of this work.
2. Web UI technology: "whatever is fancy". Chosen: Svelte 5 with Vite and Tailwind CSS, built to static files the
   Server serves. Svelte's transitions and small bundles suit a live download list; nothing needs Node at run time.
3. Publish the image on GHCR: yes (`ghcr.io/kaan88/inbound`, built on each tag).
4. Sign-in: can be turned off. These installs stay on a home network and aren't opened to the internet, so sign-in
   is an option (on by default for a new install, off with one setting), not a requirement.

Test machine: a LAN-only Linux VM the owner provided (DietPi). Details stay out of tracked files.
