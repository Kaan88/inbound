# Possible performance gains: the window (WinUI and Uno) and the web UI

Written 2026-11-14 from a read of the code, as the companion to `performance-areas.md` (Host, engine, saves,
IPC). **Nothing here is measured.** Each item is a candidate: take the baseline first (ROADMAP PERF.15) and drop
what shows nothing.

How the three relate:

- **WinUI (Windows)** and **Uno (Linux AppImage)** are the same source. `DownloadManager.Desktop.Uno.csproj`
  compiles `DownloadManager.Desktop.WinUI\**\*.cs` and `*.xaml` and adds four shims. So anything in section 1
  helps both; sections 2 and 3 are what differs per toolkit.
- **The web UI** (`webui`, Svelte 5 + Vite + Tailwind) is separate code with the same flows.

## 1. Shared window code (helps WinUI and Uno)

The pattern: every job event is handled on its own, and several steps in the handler cost time in proportion to
the whole list. With a few active downloads the Host sends a few events every 300 ms, so on a long list the
window does list-sized work several times a second.

| Area | What the code does now | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| One UI-thread call per event | `MainWindow.Servers.cs:84`: each `JobChanged` is its own `DispatcherQueue.TryEnqueue` and runs `ApplyJobEvent` in full | Gather events and apply once per frame (or per 100 ms), keeping only the newest per job; the rest of this table then runs once per tick, not once per event | Medium: completion windows and "removed" must not be lost or reordered |
| Linear lookups per event | `ApplyJobEvent` (`MainWindow.xaml.cs:532`): `_visibleJobs.Contains(vm)` scans the list; `PackageOf` scans every group and counts its members; `TakeWhile` on insert | A set of visible ids and a job-to-package map make these constant time | Low |
| Status bar and buttons per event | `UpdateStatusText` counts all jobs three times and all visible rows once; `UpdateButtons` scans all jobs twice and syncs the queue, archive and torrent menus; both run at the end of every event | Run once per tick; keep running counts by state instead of recounting | Low |
| Full rebuild | `RebuildVisible` (`:864`) re-filters and re-sorts everything, and its in-place sync searches forward for each misplaced row (quadratic when many rows move); it runs on every groups change, on package membership changes and for open torrents | Coalesce with the tick above; index the old positions once instead of searching per row | Medium: selection and row containers must be kept, which is why it syncs in place today |
| Row bindings | `MainWindow.xaml` has 59 `{Binding}` and no `{x:Bind}`; the row template alone has about 16 elements bound by reflection | Compiled bindings in the row template: less CPU per property change and per row created while scrolling | Medium: needs `x:DataType` on the template; check the same XAML still builds under Uno |
| Row view model | `JobViewModel.Update` (708 lines) maps a whole `JobDto` per event | Check that formatted strings are only rebuilt when their inputs change; goes away partly if the Host sends a small progress message (PERF.13) | Low, after measuring |
| Snapshot apply | At 5,000 jobs the snapshot takes 890 to 935 ms of the 1,065 to 1,129 ms open (PERF.5 figures) | Show the window frame first and fill the list after; build view models off the UI thread | Medium |
| Speed graph | `DownloadSpeedGraph.Draw` rebuilds the canvas each second while visible (PERF.4 stopped it while hidden) | Update the polyline's points instead of rebuilding labels and grid lines | Low; small gain |

## 2. WinUI only (Windows)

| Area | What is known | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| Memory | 215 to 235 MB private with an empty list (PERF.1). The toolkit, not our code | None from us; PERF.5 already ends the process when closed | - |
| Cold start | 650 ms empty (PERF.5). `scripts/publish.ps1` passes no ReadyToRun | ReadyToRun (PERF.11) | Low |
| Secondary windows | Progress Details moved in-process (T11: 145 ms); other windows not checked | Build the Options and Scheduler pages when first shown, if they are built up front | Check first |
| Native AOT | Not tried for WinUI; Windows App SDK supports it in principle | Faster start, less memory | High: reflection JSON everywhere, `{Binding}` by reflection. Only after PERF.10 and compiled bindings |

## 3. Uno only (Linux)

We have no numbers of our own for the Uno window: start time, memory and idle CPU are all unmeasured. The vendor
figures in `gui-toolkits.md` are for other apps.

| Area | What the code does now | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| Publish mode | `scripts/package-appimage.sh` publishes self-contained, with no ReadyToRun and no AOT | ReadyToRun is the cheap first step for start time | Low |
| Native AOT | A trial exists in the project file (`PublishAot` keeps reflection JSON on and roots the app and Contracts assemblies) but the AppImage does not use it | Vendor reports 870 to 350 ms start for one of their apps; ours unknown | Medium: the trial has to be finished and every screen tried, since trimming faults show only at run time |
| Renderer | `Program.cs`: X11 host with OpenGL by default, software rendering behind `DOWNLOADMANAGER_UNO_SOFTWARE=1` | Pick per measurement: idle wake-ups and scroll smoothness on each; on Windows the GPU driver was the idle cost (PERF.4) | Low to measure |
| Idle while closed | Whether closing the Linux window ends its process as on Windows (PERF.5) was not checked here | If not, the same saving as on Windows | Check first |
| List rows | Uno's `ListViewItem` uses the expanded template; `UnoListRows.Trim` walks each container's visual tree to collapse unused parts | Rows cost more than on WinUI; a slimmer `ItemContainerStyle` for Uno removes the parts instead of hiding them | Medium |
| Wayland host | D7.W waits for the owner; rendering through XWayland adds a copy | Unknown | With D7.W |

## 4. Web UI

| Area | What the code does now | Possible gain | Effort / risk |
| --- | --- | --- | --- |
| No list virtualisation | `ListScreen.svelte:1272`: `{#each rows as row (row.id)}` renders every row into the page | The largest item here: with thousands of jobs the page holds every row's elements. A windowed list renders only what is on screen | Medium to high: variable content (packages, torrent files), keyboard selection and "reveal this job" must keep working. `content-visibility: auto` on rows is a cheap partial step to try first |
| Rows rebuilt per event | `rows = $derived(buildRows(store.model, ...))`: any job event re-filters and re-sorts the whole list (sorting by name uses `localeCompare` with `numeric`), and about ten more derived lists (`selectedRows`, `allJobs`, `completedIds`, ...) follow | Apply socket events once per animation frame; cache the sort key; create one `Intl.Collator` instead of calling `localeCompare` with options | Low to medium |
| Deep reactive model | `store.svelte.ts`: `model = $state<Model>(...)` makes every job a deep proxy | `$state.raw` for the jobs map with explicit replacement cuts proxy cost on a large snapshot | Medium: every place that mutates a job in place must change |
| Snapshot over the network | The snapshot is sent as one JSON text frame (8.5 MB at 5,040 jobs, PERF.7) with no compression; unlike the pipe, this can cross a slow link | Compress it (see the caution below), or send a slim list and fetch details on demand | Medium |
| Static files | `WebFront.cs:259`: `UseStaticFiles` with no compression and no cache headers found | Pre-compressed assets and long cache lifetimes for the hashed files under `assets/`; faster first load on a remote server | Low |
| Serialised per client | `WebSocketTransport.cs:193`: each session serialises each message itself | Serialise once per broadcast and share the bytes (joins PERF.13) | Low to medium |
| Bundle | One Vite build, `target: es2022`, no source maps; size not looked at | Check the size once; lazy-load the settings, TorBox and torrent screens if the main chunk is large | Low |

**Caution on WebSocket compression:** per-message deflate on a connection that carries secrets next to
attacker-influenced text (file names, URLs) is the setting for compression side-channel attacks. If compression
is added, limit it to the snapshot or keep secrets out of compressed frames. This is a security decision, not
only a performance one.

## Baseline to take first (PERF.15)

- **Window, both toolkits:** with 500 and 5,000 jobs and 1, 8 and 20 active downloads: UI-thread CPU per minute,
  dropped frames while scrolling, time to open. `scripts/perf-window.sh` and the soak driver cover Windows; the
  Linux window needs the same three numbers plus memory and idle CPU, on OpenGL and on software rendering.
- **Web UI:** the same job counts in a browser's performance panel: scripting time per second while downloads
  run, element count, time from sign-in to a usable list, bytes transferred on first load and for the snapshot.

## What not to expect

- A WinUI window much under 200 MB.
- A web UI gain from a different framework: the costs above are list size and update rate, not Svelte.

## Suggested order

Tracked as ROADMAP PERF.15 to PERF.20.

1. The baseline (PERF.15).
2. Event coalescing and constant-time lookups in the shared window code (PERF.16): one change, both toolkits.
3. Web UI: per-frame updates, then the windowed list (PERF.19).
4. Linux: ReadyToRun, renderer choice, then the AOT trial (PERF.18).
5. Compiled bindings (PERF.17) and web serving (PERF.20).
