# How to take the PERF.15 baseline (web UI, Linux window; what is left for Windows)

Tooling for ROADMAP PERF.15 and stage A2 of `sites-and-performance-order.md`. It measures; it changes no product code.
The numbers come from running these scripts on a **quiet machine**: a build, a test run or another agent on the
same cores moves every figure here (the smoke runs made while writing the scripts ran at a load average above 200 and
are not a baseline).

| Script | What it does |
| --- | --- |
| `scripts/perf-seed.sh` | Builds the binaries once; starts an isolated instance with N held jobs and A active downloads; stops it |
| `scripts/perf-webui.sh` + `perf-webui.mjs` | Web UI: load, scripting time per second, DOM size, heap, bytes, snapshot size |
| `scripts/perf-window-linux.sh` | Uno window: time to open, CPU (process, UI thread, render thread), RSS, idle, on both renderers |
| `scripts/perf-proc-sample.py`, `perf-window-summary.py`, `perf-seed-active.mjs` | Helpers of the above |

Needs, all already on the machine the scripts were written on: the .NET SDK in `~/.dotnet`, Node 22 or later (its own
WebSocket and fetch; no npm package is installed for the measuring), `google-chrome-stable`, `python3`, `curl`; for the window
also `kwin_wayland`, `Xwayland`, `dbus-run-session`, `xprop` (and `glxinfo` for one header line). Nothing is installed
system-wide and no browser is downloaded. `npm ci` runs once, in a copy of `webui/` under `artifacts/perf`, to build
the web UI into the server (the checkout gets no `node_modules`).

## Isolation

Everything runs apart from a running Inbound of yours:

- `DOWNLOADMANAGER_INSTANCE` is unique per run, so the Host's socket names differ, and `DOWNLOADMANAGER_DATA` is a folder under
  `artifacts/perf/inst/<name>/` (git-ignored; `PERF_OUT` or `--out` moves it). Host, window and CLI all read these two.
- The server listens on a random port on `127.0.0.1` with sign-in on and a random password. The TestServer has its own random port.
- Only processes started by the script are stopped, found by pid, and only when their command line is a binary under the
  build folder. SIGTERM first, SIGKILL after 3 s (the TestServer otherwise waits for its open slow downloads).
- Chrome is headless with a temp profile; nothing opens on your desktop.
- The Linux window needs a display. By default the script starts its own: `kwin_wayland --virtual --xwayland` in a private
  runtime dir with its own session bus and config dir (see "Display" below). `--display :N` uses one you give instead, and
  then the window **does** open there.

## Seeding (`perf-seed.sh`)

```sh
scripts/perf-seed.sh build [--skip-webui] [--skip-window]    # publishes linux-x64 as package-appimage.sh does, into artifacts/perf/bin
scripts/perf-seed.sh start --name x --jobs 5000 --active 8 [--target host|server]
scripts/perf-seed.sh status --name x      # ports and pids (not the password)
scripts/perf-seed.sh stop --name x [--purge]
```

- **Jobs**: N held jobs (`add-batch` against the TestServer, `--start later`, 1 MiB, in batches of 1,000; they show as
  "Queued" and never run) plus A active downloads. The list has N + A rows. This is the same method as `scripts/perf-window.sh`
  on Windows, which also adds held jobs by `add-batch`.
- **Active downloads**: `/slow/active-<i>.bin?size=268435456&bps=262144` from `tools/TestServer`: 256 MiB at 256 KiB/s on one
  connection each, so each stays active for about 17 minutes (a measurement is 70 s). `ACTIVE_BPS` and `ACTIVE_SIZE` change
  this. The Host's `maxconcurrent` is set to A + 2 so all of them run at once, and above 8 the per-host limit
  (`maxConnectionsPerHost`, 8 by default; all of them come from 127.0.0.1) is raised to A + 2 as well. The product runs at most
  16 downloads at once, so `--active 20` gives 16 running and 4 waiting; check `activeSeen`. Completion windows are turned off.
- `--target host` (the Linux Host holds the jobs; for the window) or `--target server`: the held jobs are seeded through the
  Host, which then stops, the headless `DownloadManager.Server` starts on the same data folder, and the active downloads
  are added over HTTP with the signed-in session (`perf-seed-active.mjs`, `POST /api/ipc` `job.add`, as the web UI does).
- Sourcing the file (`. scripts/perf-seed.sh`) gives `perf_build`, `perf_start NAME JOBS ACTIVE TARGET`, `perf_stop`.

## Web UI (`perf-webui.sh`)

```sh
scripts/perf-webui.sh                      # the full matrix: 500 and 5,000 jobs x 1, 8, 20 active; about 20 minutes
scripts/perf-webui.sh --jobs 500 --active 1 --duration 10 --warmup 4     # smoke run
# options: --jobs, --active, --duration (60), --warmup (10), --out, --results, --chrome, --keep
```

Writes `artifacts/perf/results/webui-<time>.csv` and `.md` (one row per combination; the CSV has more columns than the table).
Per combination: a fresh server and a fresh Chrome. The page is driven through the DevTools protocol: it opens `/`, lands on the
sign-in page, types the password, presses Enter.

| Number | Meaning |
| --- | --- |
| `signinPageMs` | `Page.navigate` to the password box present (the sign-in page's own cost) |
| `firstRowMs`, `usableMs`, `fullListMs` | From the Enter key to: the first `role=row` in the downloads grid; two animation frames after it (what "usable" means here); the last change to the row count. The list is not virtualised, so all rows appear in one batch and the three are close. The clock is the browser's own, so it includes the sign-in request, the reload, the scripts, the WebSocket hello and snapshot, and the render |
| `rowsRendered` | Rows in the grid (N + A when the default view shows everything) |
| `longTasks`, `longTaskMs`, `maxLongTaskMs` | Main-thread tasks over 50 ms during that load (PerformanceObserver `longtask`) |
| `load*Bytes`, `loadBytesTransferred`, `loadBytesDecoded` | The same load, by resource type: bytes on the wire (`encodedDataLength`) and decoded bytes. The server does not compress, so the two are close |
| `snapshotBytes`, `wsFirstFrames` | Size of the snapshot frame on `/api/ws` (UTF-8 bytes of the text message; the transport is one JSON text frame per message, not SSE) and the first frames as `type:bytes` |
| `scriptMsPerSec`, `taskMsPerSec`, `layoutMsPerSec`, `recalcStyleMsPerSec` | `Performance.getMetrics` deltas (`ScriptDuration`, `TaskDuration`, `LayoutDuration`, `RecalcStyleDuration`) over `--duration` seconds, divided by the seconds, in ms per second, starting `--warmup` seconds after the list was usable. `taskMsPerSec` is the renderer main thread's busy time per second |
| `layoutsPerSec`, `recalcsPerSec` | The counts of layouts and style recalculations per second |
| `wsMessagesPerSec`, `wsKBPerSec` | What the page's WebSocket received in the same window (counted in the page; characters, close to bytes). Network capture is off during this window so it does not add cost |
| `domElements`, `domNodes` | `getElementsByTagName('*')` and the `Nodes` metric |
| `jsHeapMB`, `jsHeapAfterGcMB` | `JSHeapUsedSize` at the end of the window, and the heap after a forced collection (the live size) |
| `activeSeen` | Rows whose text says "Downloading" at the end; it must equal A or the downloads did not stay active |
| `reloadUsableMs`, `reloadBytesTransferred` | The same load again with the session cookie and a warm cache (a returning visitor) |

Covered: everything the "Web UI" bullet of "Baseline to take first" asks for. Not covered: the browser's compositor and GPU
time (`Performance.getMetrics` has the main thread only; headless Chrome draws in software), other browsers, and phones.
Chrome's own CPU can also be read from `/proc` while the run goes on if the question is the whole browser.

## Linux (Uno) window (`perf-window-linux.sh`)

```sh
scripts/perf-window-linux.sh                       # 500, 5,000 jobs x 0, 1, 8, 20 active x default and software rendering: 16 combinations, about 45 minutes
scripts/perf-window-linux.sh --jobs 500 --active 1 --renderer default --duration 10 --warmup 3 --opens 2   # smoke run
# options: --jobs, --active, --renderer, --duration (60), --warmup (10), --opens (3), --display :N, --out, --results, --no-build
```

Writes `artifacts/perf/results/window-<time>.csv`, `.md`, a `.meta.txt` header (KWin, Xwayland, GL renderer, load) and a
`series-*.csv` per combination (one row a second: CPU ticks, UI-thread ticks, render ticks, RSS).

**Display.** On this machine (CachyOS, KDE Plasma on Wayland) there is no Xvfb, Weston, Cage or Xephyr, but KWin can run
headless: `kwin_wayland --virtual --xwayland` renders to a virtual output and starts a rootless Xwayland. The script starts it
inside `dbus-run-session` with a private `XDG_RUNTIME_DIR`, `XDG_CONFIG_HOME` and no inherited session variables, runs
itself as KWin's session application (so the measuring runs inside that display), and removes every process of that
private session by its environment afterwards. The window ran there on the machine's GPU (Xwayland found the NVIDIA render node:
`glxinfo` in that session reports the NVIDIA renderer, which is the header's "GL" line). So the "default" renderer is
Uno's X11 host with OpenGL, through XWayland, as on the owner's desktop. If the GL line ever says `llvmpipe`, "default" was software OpenGL and the
comparison with "software" is not the one meant.

**Renderers.** `src/DownloadManager.Desktop.Uno/Platforms/Desktop/Program.cs` builds the host with `.UseX11()`, which draws with
Skia on OpenGL by default; `DOWNLOADMANAGER_UNO_SOFTWARE=1` sets `FeatureConfiguration.Rendering.UseOpenGLOnX11 = false` (Skia's CPU
raster). No other renderer switch exists in the project (`UseLinuxFrameBuffer` needs a console, not a display). The native Wayland host is
Uno pull request 25503 (D7.W), not in any release, so it cannot be measured yet; PERF.15 says to take the same numbers on it when
that trial runs.

| Number | Meaning |
| --- | --- |
| Time to open, `mainBeginMs` ... `firstFrameMs` | Milliseconds from just before the window executable starts (`date` in the script) to the lines the window writes to `data/logs/ui-trace.log` (the same lines the Windows script uses): `Uno Program.Main begin` (the runtime is up), `MainWindow activated`, `First snapshot applied`, `First frame with the list` (the number to quote). `mappedMs` is the first change of the X root's `_NET_CLIENT_LIST`, polled every 100 ms, so ±100 ms. The Host is already running, as in `perf-window.sh coldopen` on Windows. The window is opened `--opens` times; the table shows the median and the CSV lists each. The first open is the one the disk cache has not seen |
| `cpuPct`, `cpuMsPerMin` | The window process, in percent of one core and CPU milliseconds per minute, over `--duration` after `--warmup` |
| `uiPct`, `uiMsPerMin` | The thread named **`Uno Event Loop`**: Uno's X11 host runs the XAML work and the dispatcher there. The main thread (tid = pid) only sleeps (`mainThreadPct` is its share, about 0). The sampler prints the five busiest threads so a wrong name shows at once |
| `renderPct` | `X11RenderThread`: Skia's drawing and the buffer swap |
| `rssMB*` | Resident memory at the start, end and maximum of the window |
| `hostCpuPct`, `hostRssMB` | The Linux Host over the same window; with active downloads it carries the transfers and the events |
| `activeSeen` | Downloads reported as "Downloading" by the CLI after the window; must equal A |
| Idle | The rows with 0 active downloads |

**Warm-up matters.** The runtime's tiered JIT thread (".NET Tiered Compilation") is among the busiest right after start, because the AppImage
build has no ReadyToRun. `--warmup 10` leaves most of it out; use `--warmup 30` when comparing with a ReadyToRun build (PERF.11, stage A3)
and keep the same value for both.

**Not measured: dropped frames while scrolling.** Nothing in the window exposes frame timing, and nothing here can scroll it:
no `xdotool`, `ydotool` or `wtype` is installed, and in a headless session no one scrolls. What could be done without installing
a package: inject wheel events into the private display with XTEST (`libXtst.so` is on the machine; a short Python `ctypes` script
calling `XTestFakeButtonEvent` for buttons 4 and 5) and take the UI and render threads' CPU while scrolling, which measures cost
per scroll, not dropped frames. Real frame timing would need either a frame counter in the window (a product change, which
this tooling does not make) or compositor statistics (KWin's `--virtual` output does not report missed frames). Until
one of those exists the number stays unmeasured; on Windows the equivalent also has no script. Do not estimate it from the CPU figures.

## What the Linux scripts do not cover

- The WinUI window (Windows only).
- A real desktop session's compositor and GPU load: the window ran in KWin's virtual output on the same GPU, but with no real monitor,
  so presentation timing differs from a screen's refresh. For the Wayland-versus-X11 comparison and any scroll test, run on the real session
  with `--display :0` (this opens the window on your desktop, so do it when you are not working in it).
- Browsers other than Chrome; the Docker image (the server is run directly, as the image runs it, without the container).
- The extension, the native host and the tray.

## What is left for Windows (the WinUI window)

Nothing new has to be written for the first three numbers; the existing scripts take them from Git Bash on the Windows machine
(they start executables, so not from PowerShell; see their headers). Build first with `scripts\publish.ps1` or `dotnet build -c Release`
(the scripts copy from the `bin` folders).

1. **Time to open, 500 and 5,000 jobs:** `scripts/perf-window.sh coldopen OUTDIR 5`. It writes `coldopen.csv`
   (window shown, snapshot applied, first frame with the list; option "Closing the window ends it" on and off). This is the PERF.5
   method; the earlier figures are 650 ms empty and 1,065 to 1,129 ms at 5,000 jobs.
2. **Idle CPU with 0 active downloads, 500 and 5,000 jobs:** `scripts/perf-window.sh idle OUTDIR off 10 500` and `... off 10 5000`
   (window left open: option off, 10 minutes). `scripts/perf-idle-sample.ps1` is the sampler it runs; `scripts/perf-idle-summary.py` summarises its CSV.
3. **UI-thread CPU per minute with 1, 8 and 20 active downloads, 500 and 5,000 jobs, and memory:** nothing in `perf-window.sh` adds active
   downloads, and `scripts/soak.sh` (with `--ui` and `--jobs 5000`) adds a *stream* of mixed downloads, not a fixed count. What is needed:
   a copy of `perf-window.sh`'s `setup` + `add_jobs`, then A calls of the CLI
   `add "http://127.0.0.1:$PORT/slow/active-N.bin?size=268435456&bps=262144" --start now --connections 1` (set `maxconcurrent` to A + 2 first),
   `host open`, a 10 s wait and `perf-idle-sample.ps1 -Minutes 1` (it records per-thread counters and CPU per second). Pick the UI thread from
   the busiest thread of the window process (XAML runs on the main thread in WinUI). `scripts/soak-sample.ps1` gives private bytes and working set.
4. **Dropped frames while scrolling:** no script exists on either platform; see above.

Summarise with `scripts/perf-idle-summary.py` and `scripts/soak-summary.py` as PERF.5 and PERF.2 did, and write the numbers into PROGRESS next to the
Linux and web UI ones, with the machine and the load.
