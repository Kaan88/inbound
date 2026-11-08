# Plan: headless server, web UI and Docker (D1 to D4)

Status: plan, 2026-11-07. D1a (the shared command dispatcher) and D1b (the server and the Linux replacements) are
built on `stream-b`, 2026-11-08; what D1b holds is described in the next section. D2 to D4 are not started. ROADMAP D1
to D4 and PERF.1 to PERF.3 track it.

## D1b: what was built (2026-11-08)

`src/DownloadManager.Server` (`net10.0`, ASP.NET Core) runs the Runtime, Engine, Persistence, Archives, Containers
and the TorBox provider behind the D1a `HostCommandDispatcher`, with no Windows project referenced.

- **Endpoints:** `GET /api/ws` (WebSocket, one JSON envelope per text message, hello and snapshot then events),
  `POST /api/ipc` (one envelope in, one reply out), `GET /health` (no check of any kind), and static files from
  `wwwroot` (a labelled placeholder page until D2). The framing is in `docs/ipc-protocol.md`.
- **Configuration:** `DOWNLOADMANAGER_DATA` (state, default `/data`), `DOWNLOADMANAGER_ROOTS` (the folders the web UI
  may browse, default `/downloads`, several separated by `:` or `;`), `DOWNLOADMANAGER_URLS` or `ASPNETCORE_URLS`
  (default `http://0.0.0.0:8080`), `DOWNLOADMANAGER_HOSTS` (host names a browser may use besides IP addresses and
  `localhost`). On its first start the server sets the default download folder to the first root and turns off the
  desktop-only settings (notifications, sound, clipboard watching, start at sign-in).
- **What the server can't do:** no window, folder dialog, tray, exit on request, updater or programs named by a client
  (`PlatformCapabilities` with only `SupportsFolderBrowse` on). Those commands answer that they are not available.
  Instead it answers `folders.browse` and `folders.create`, limited to the roots, with every path resolved through
  its links before the check.
- **Origin check:** a browser request to `/api/` must come from the server's own origin and must name the server by
  an IP address, `localhost` or a name in `DOWNLOADMANAGER_HOSTS`. That stops another web page, including one that
  points its own name at the server (DNS rebinding), from driving it through the user's browser.

**There is no sign-in yet (D2).** Anyone who can reach the port can do everything a client can: add and delete
downloads, write files anywhere the server's user may write (a job's destination is not limited to the roots, only
the folder browser is), change settings, enter or replace the TorBox key and site logins. The server therefore refuses
to listen on anything but loopback unless `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1` is set, and logs a warning when
it is. The default address `0.0.0.0:8080` needs that variable. Until D2, keep the port on a trusted home network.

### The Linux replacements

| Windows part | On Linux now | Evidence |
| --- | --- | --- |
| `SecretProtector` (DPAPI) | `ISecretProtector`. The server uses `AesGcmSecretProtector`: AES-256-GCM, key in `<data>/secrets.key`, 32 random bytes, created with mode 600; a looser mode is reset to 600 with a warning. Blobs are `aesgcm:v1:`. The desktop keeps DPAPI unchanged. | `SecretKeyFileTests` (Server.Tests) |
| Bundled `7z.exe` | `7zz` (or `7z`) found by `DOWNLOADMANAGER_7ZIP`, then a `7zz` next to the program, then `PATH`. The tool status names the path used, or says how to install it. | `SevenZipToolTests`, the Archives and Integration extraction tests on Linux with the official 7-Zip 26.04 `7zz` |
| Hard-link check after extraction (`GetFileInformationByHandle`) | `lstat` from the C library on Linux (x64 and arm64 layouts), so an extracted hard link is still refused. | `StagingWalkTests.RefusesAHardLink` on Linux |
| Password on 7-Zip's stdin ending in CR LF | LF only on Linux: 7-Zip for Linux keeps a CR as part of the password. | `ToolPasswordTests`, the RAR4 and 7z/zip password tests on Linux |
| Junction checks | The same checks catch folder symlinks; the tests make a symlink where Windows makes a junction. | `StagingWalkTests.RefusesAJunction`, `FinalizerTests.AJunctionInTheDestinationIsRefused` on Linux |
| `ToolLifetime` (job object) | The tool's process tree is killed on cancel, on dispose and when the server exits normally. | `ToolProcessTests` incl. `OffWindowsATiedToolIsKilledWhenThisProcessExits` |
| FFmpeg path | `ffmpeg` on `PATH` (no `.exe`). | `FfmpegDiscoveryTests` |
| Completion program check | A full path; `.exe` is only required on Windows. The server refuses completion programs anyway. | `HostCommandDispatcherTests.TheDesktopAcceptsProgramSettings` on Linux |
| Path comparisons | `PathNames`: case-insensitive on Windows, case-sensitive elsewhere. Used for extraction staging and output sets, archive set keys, package folders, renames and the folder browser. | `PathNamesTests`, `ExtractionSafetyTests` |
| Free space and same-volume checks | `Volumes`: the drive on Windows; the longest matching mount point on Linux, so `/downloads` on its own volume is measured as itself. | `PathNamesTests`, `DownloadFoldersTests` |
| Mark of the web, `SparseFile`, `ExtractionDurability.FlushDirectory` | Skip off Windows (checked in the code: each returns at once there). Files are still flushed with `fsync` before an archive is deleted. | code review; the extraction tests pass on Linux |

### Decisions taken without the owner (listed in PROGRESS as questions)

- **Case on Linux:** extraction no longer refuses `a.txt` next to `A.TXT` on Linux, because Linux keeps them apart. The
  check stays on Windows. A name with a backslash is refused on Linux, since it would become a folder on Windows if
  the files were ever copied there.
- **The secret key file** is part of the data volume. A backup of `/data` carries every secret with its key; losing
  the key file makes saved site logins, proxy passwords and the TorBox key unreadable, and they must be entered again
  (each is read as empty, with a warning naming the key file). Secrets copied from a Windows install (DPAPI) can't be
  read on the server, for the same reason.
- **Not done on Linux:** a directory `fsync` after the renames (Windows flushes the folder; Linux skips it, so a power
  cut just after an extraction could lose the renames though not the file data); a kill of the server with SIGKILL
  leaves a running `7zz` to finish on its own (there is no job object). Both are noted for D3.
- **Tests on Linux:** the tests that start the Windows Host, use named pipes, NTFS streams, junctions, share locks,
  drive letters as such, Explorer or the updater carry `[Trait("Platform", "Windows")]`, and the Linux run uses
  `--filter "Platform!=Windows"`. The Host, CLI and NativeHost references of the integration tests are Windows-only.


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
- **Case-sensitive file systems:** the check that refuses `a.txt` next to `A.TXT` stays on Windows only (D1b).
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
