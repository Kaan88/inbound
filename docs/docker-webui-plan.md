# Plan: headless server, web UI and Docker (D1 to D4)

Status: plan, 2026-11-07. D1a (the shared command dispatcher) and D1b (the server and the Linux replacements) are
built on `stream-b`, 2026-11-08; what D1b holds is described below. D2 (sign-in, browser pairing and the web UI) is
built on `webui`, 2026-11-08, in the next section. D3 and D4 are not started. ROADMAP D1 to D4 and PERF.1 to PERF.3
track it.

## D2: what was built (2026-11-08)

- **Sign-in** (`src/DownloadManager.Server/Auth`), on by default. One user. The password is set on the setup page the
  first time, or comes from `DOWNLOADMANAGER_PASSWORD` / `DOWNLOADMANAGER_PASSWORD_FILE` (then the page can't change
  it). PBKDF2-SHA256 with 600,000 iterations in `<data>/auth.json`. A browser that isn't on the server's own machine
  (or reaches it through a proxy) also needs the setup code the server writes to its log at start, so whoever reaches
  a fresh server first over the network can't claim it. Sessions: 256-bit ids in an `HttpOnly`, `SameSite=Strict`
  cookie (`Secure` over HTTPS; `X-Forwarded-Proto` believed only with `DOWNLOADMANAGER_TRUST_PROXY=1`), 24 hours idle
  and 7 days at most, kept in memory as hashes; sign-out and a password change end them and close their WebSockets.
  Wrong tries make the client address wait 1, 2, 4 ... seconds up to 5 minutes. Only `/health`, the sign-in page and
  its assets, `/favicon.svg` and `/theme.js` answer without a session. With sign-in on, the server may listen beyond
  loopback (the default `0.0.0.0:8080` works); `DOWNLOADMANAGER_AUTH=off` turns it off and brings back the D1b rule
  (loopback only unless `DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1`). The status call tells the version only to a
  signed-in browser. Evidence: `AuthTests` (11) in Server.Tests.
- **Browser pairing:** Settings, Sign-in and browsers, "Pair a browser" shows an 8-character code for ten minutes,
  once. The extension sends it to `POST /api/pair` and gets a long token (only its hash is kept), listed by name with
  when it was paired and last used, and removable. The token works only on `POST /api/ipc` and only for the
  extension's commands plus `folders.browse` (`BridgeRole.ServerExtension`); replies are filtered the same way, so it
  never sees settings or logins. CORS for our extension ids. Evidence: `PairingTests` (5), and the screens walk below
  (a real code from the page, a token refused `settings.get`, a removed token answered 401). The extension's side
  (choosing the server, entering the code, sending there) is Stream C's transport work and is not built here.
- **Torrent upload:** `POST /api/upload/torrent` keeps the file in `<data>/uploads` (removed after a day, at most 200),
  a folder only the provider coordinator may read besides the roots.
- **Web UI** (`webui/`, Svelte 5, Vite 8, Tailwind 4, exact versions in `package-lock.json`): built to
  `webui/dist`, which the server's build copies into `wwwroot` (`webui/README.md` has the order and the commands;
  `scripts/build.ps1` builds it first). The sign-in page is its own build under `/signin/`. Screens, in the planned
  order: the download list with live progress, packages, the state views and bulk pause, resume and delete (with
  "delete the files too" unticked by default); add links (single, batch, magnets, provider and hoster links, a
  `.torrent` upload with its files to choose, the not-cached approval); extraction status and the password prompt;
  queues and the scheduler; settings, favorite folders, site logins, proxy and extraction, with the program-running
  settings shown read-only and why; My TorBox (key, plan, what is in the account with Download, hoster status). The
  folder browser (favorites with free space, the allowed roots, create a folder, check a typed path) opens wherever
  a folder is chosen. Colors are the desktop's `App.xaml` tokens as CSS variables in both themes; the layout works at
  390 pixels wide. The protocol client reconnects with waits of 0.5 s doubling to 15 s, and sends the calls that wait
  on the network through `POST /api/ipc` so they don't hold up the WebSocket's queue.
  Evidence: vitest 34 (protocol client: replies, timeouts, reconnect waits, signed out, incompatible; model; views;
  add routing; format; store); `npm run check` clean; `npm run screens` over 26 screens at desktop and phone width in
  both themes with no console error, page error or sideways scroll, plus the actions it checks end to end (extraction
  after the password, pause and resume, delete without the file, torrent upload to the mock provider, site login and
  proxy saved write-only, pairing and revoking, sign-out), against the server with a temp data folder, the
  TestServer and its mock TorBox. Screenshots: `artifacts\webui\` (not committed).
- **Not done here:** the Dockerfile and compose (D3, which builds the web UI in a Node stage from the lockfile before
  `dotnet publish`), the run on the Linux VM and against the real TorBox (D4), and the extension's server transport.


## D1b: what was built (2026-11-08)

`src/DownloadManager.Server` (`net10.0`, ASP.NET Core) runs the Runtime, Engine, Persistence, Archives, Containers
and the TorBox provider behind the D1a `HostCommandDispatcher`, with no Windows project referenced.

- **Endpoints:** `GET /api/ws` (WebSocket, one JSON envelope per text message, hello and snapshot then events),
  `POST /api/ipc` (one envelope in, one reply out), `GET /health` (no check of any kind), and static files from
  `wwwroot` (a labelled placeholder page until D2). The framing is in `docs/ipc-protocol.md`.
- **Configuration:** `DOWNLOADMANAGER_DATA` (state, default `/data`), `DOWNLOADMANAGER_ROOTS` (the folders the
  server may use for its clients, default `/downloads`, several separated by `:` or `;`), `DOWNLOADMANAGER_URLS` or
  `ASPNETCORE_URLS` (default `http://0.0.0.0:8080`; these alone say where it listens, Kestrel's own
  `Kestrel:Endpoints` configuration is turned off), `DOWNLOADMANAGER_HOSTS` (host names a browser may use besides IP
  addresses and `localhost`), `DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS=1` (start although the saved secrets were
  sealed with another key file; they are lost). On its first start the server sets the default download folder to the first root and turns off the
  desktop-only settings (notifications, sound, clipboard watching, start at sign-in).
- **What the server can't do:** no window, folder dialog, tray, exit on request, updater or programs named by a client
  (`PlatformCapabilities` with only `SupportsFolderBrowse` on). Those commands answer that they are not available.
  Instead it answers `folders.browse` and `folders.create`, limited to the roots, with every path resolved through
  its links before the check.
- **Everything a client names stays in the roots** (review fix, 2026-11-08): a download's folder (asked for, or taken
  from the default, last, category or custom-category folder), Move/Rename of a finished download, a changed folder
  setting (the download folder always, because saving creates it), a container file to preview or send, a `.torrent`
  file given by its path, and an archive to extract, its password record or the "extract to this folder" setting.
  Each path is resolved through its links first and the resolved one is used. Outside the roots the answer is an
  error ("outside the folders this server may use") and nothing is written. The desktop Host has no roots.
- **One server per data folder:** it holds `<data>/server.lock` open with no sharing (an exclusive `flock` on Linux)
  for its whole life, as the desktop Host holds its mutex. A second server on the same folder stops with exit code 2.
  The kernel drops the lock when the process ends, killed or not. A file system without working locks (some network
  shares) can't give this guarantee, so the data belongs on a local volume.
- **Listening:** the loopback rule below is checked on the configured addresses before starting and again on the
  addresses Kestrel reports once it listens; a refusal stops it at once.
- **Origin check:** a browser request to `/api/` must come from the server's own origin and must name the server by
  an IP address, `localhost` or a name in `DOWNLOADMANAGER_HOSTS`. That stops another web page, including one that
  points its own name at the server (DNS rebinding), from driving it through the user's browser.

**Sign-in (D2) is on by default.** With it turned off (`DOWNLOADMANAGER_AUTH=off`), anyone who can reach the port can
do everything a client can: add and delete downloads, write files anywhere in the roots, change settings, enter or
replace the TorBox key and site logins. The server then refuses to listen on anything but loopback unless
`DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1` is set too, and logs a warning when it is.

### The Linux replacements

| Windows part | On Linux now | Evidence |
| --- | --- | --- |
| `SecretProtector` (DPAPI) | `ISecretProtector`. The server uses `AesGcmSecretProtector`: AES-256-GCM, key in `<data>/secrets.key`, 32 random bytes, created with mode 600; a looser mode is reset to 600 with a warning. Blobs are `aesgcm:v1:` and carry the key's id. Before any store opens, the data folder is searched for blobs: one sealed with another key, or a missing key file while blobs exist, stops the start with exit code 2 and a message naming the key file, and no new key is made. Starting anyway would read those secrets as empty and the stores would drop them on their next save. `DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS=1` is the explicit way to start without them. The desktop keeps DPAPI unchanged. | `SecretKeyFileTests`, `ServerStartGuardTests` (Server.Tests) |
| Bundled `7z.exe` | `7zz` (or `7z`) found by `DOWNLOADMANAGER_7ZIP`, then a `7zz` next to the program, then `PATH`. The tool status names the path used, or says how to install it. | `SevenZipToolTests`, the Archives and Integration extraction tests on Linux with the official 7-Zip 26.04 `7zz` |
| Hard-link check after extraction (`GetFileInformationByHandle`) | `lstat` from the C library on Linux, or `__lxstat` on a glibc older than 2.33 (x64 and arm64 layouts), so an extracted hard link is still refused. When the count can't be read (no such call, a failed call, another architecture) the extraction is refused with a message saying so, rather than the check being skipped. Windows keeps its rule (a failed call counts as one link). | `StagingWalkTests.RefusesAHardLink` and `TheLinkCountIsReadOnlyFromALayoutThatIsKnownAndNeverGuessedAsOne` on Linux |
| Password on 7-Zip's stdin ending in CR LF | LF only on Linux: 7-Zip for Linux keeps a CR as part of the password. | `ToolPasswordTests`, the RAR4 and 7z/zip password tests on Linux |
| Junction checks | The same checks catch folder symlinks; the tests make a symlink where Windows makes a junction. | `StagingWalkTests.RefusesAJunction`, `FinalizerTests.AJunctionInTheDestinationIsRefused` on Linux |
| `ToolLifetime` (job object) | The tool's process tree is killed on cancel, on dispose and when the server exits normally. | `ToolProcessTests` incl. `OffWindowsATiedToolIsKilledWhenThisProcessExits` |
| FFmpeg path | `ffmpeg` on `PATH` (no `.exe`). | `FfmpegDiscoveryTests` |
| Completion program check | A full path; `.exe` is only required on Windows. The server refuses completion programs anyway. | `HostCommandDispatcherTests.TheDesktopAcceptsProgramSettings` on Linux |
| Path comparisons | `PathNames`: case-insensitive on Windows, case-sensitive elsewhere. Used for extraction staging and output sets, archive set keys, package folders, renames and the folder browser, and (review fix) for every file name the archive code compares within one folder: volume grouping (`ArchiveVolumes`), the folder view's jobs by name, set members, the header cache, the volumes and jobs a run used, missing volumes. So on Linux `Movie.part1.rar` and `movie.part1.rar` are two sets, and one's download is never tied to the other's cleanup. File extensions and categories stay case-insensitive everywhere. | `PathNamesTests`, `ExtractionSafetyTests`, `ArchiveVolumesTests.NamesThatDifferOnlyByCaseAreOneFileOnWindowsAndTwoSetsOnLinux` |
| Free space and same-volume checks | `Volumes`: the drive on Windows; the longest matching mount point on Linux, so `/downloads` on its own volume is measured as itself. | `PathNamesTests`, `DownloadFoldersTests` |
| Mark of the web, `SparseFile`, `ExtractionDurability.FlushDirectory` | Skip off Windows (checked in the code: each returns at once there). Files are still flushed with `fsync` before an archive is deleted. | code review; the extraction tests pass on Linux |

### Decisions taken without the owner (listed in PROGRESS as questions)

- **Case on Linux:** extraction no longer refuses `a.txt` next to `A.TXT` on Linux, because Linux keeps them apart. The
  check stays on Windows. A name with a backslash is refused on Linux, since it would become a folder on Windows if
  the files were ever copied there.
- **The secret key file** is part of the data volume. A backup of `/data` carries every secret with its key. Losing
  the key file stops the server until it is put back, or until the owner starts it once with
  `DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS=1` and enters the site logins, proxy password and TorBox key again.
  Secrets copied from a Windows install (DPAPI) can never be read on the server; they are read as empty with a warning
  and don't stop the start, since no key file could open them.
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

- **Technology:** Svelte 5 with Vite and Tailwind CSS (owner, 2026-11-08: "whatever is fancy"), built to static
  files the Server serves. Built: see "D2: what was built" above.
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
  Session cookie (`HttpOnly`, `SameSite=Strict`), origin check on every change, a growing wait after failed
  tries. Only `/health` and the sign-in page answer without signing in. TLS comes from a reverse proxy (D3 docs).
- **Browser extension:** native messaging cannot reach a container. The server side is built: pairing by a one-time
  code, and a token for `POST /api/ipc` limited to the extension's commands. The extension's transport is its own
  item.

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
