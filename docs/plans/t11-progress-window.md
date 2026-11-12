# T11 proposal: open Progress Details inside the running window process

## 1. Why it is a separate process today

- **The main window starts the exe again.** `MainWindow.OpenDownloadWindow` and `OpenPackageWindow` (`MainWindow.xaml.cs:1933–1958`) call `Process.Start(Environment.ProcessPath, WindowInstance.Arguments("job"|"package", id, target, manual))`. `PackageWindow.cs:849` does the same for a file inside a package. Three other callers go through the same two methods:
  - a double-click on a list row (`MainWindow.xaml.cs:1186`, 1928–1930);
  - a torrent file row (`MainWindow.Torrents.cs:458`);
  - the main window's own completion-window burst (`MainWindow.Servers.cs:42–55`). This is how CLI `add` jobs and a remote server's finished jobs get their completion windows.

  The arguments are `--job/--package <id> [--manual] [--server <id>]`, built by `WindowInstance` in `Servers/RemoteRules.cs`.
- **The Host starts the exe too.** `Runtime/DownloadWindows` decides that a download or package needs its window: it started, or it finished with the completion dialog on. `Host/Program.cs:363–371` then calls `HostController.OpenDownload/OpenPackage`, and `LaunchUi` (`HostController.cs:82–120`) starts the exe with only `--job/--package` (no `--manual`, no `--server`). The Host has no UI thread of its own. With PERF.5 on by default ("Closing the window ends it", owner 2026-11-08), there is often no main window process to ask. Starting a process works whether or not one is running.
- **One window per download, per process.** In `App` (`App.xaml.cs:68–77`), each launch takes the activation event from `WindowInstance.EventName(...)`: `Local\DownloadManager.UI.Show.<user>.Job.<id>[.Server.<id>]`, with `.Uno` added on Uno.
  - A second launch for the same job signals that event and exits.
  - A `--job` launch makes `OnLaunched` (`App.xaml.cs:191–205`) build only a `DownloadWindow`, never the main window.
  - Closing that window runs `Exit()`.
- **Each window opens its own connection.** `DownloadWindow` creates `new HostConnection(target)` (`DownloadWindow.xaml.cs:171`). For this PC it passes no `client.hello` (`HostConnection.cs:670` sends one only for remote servers), so it connects on the main pipe and receives the **full snapshot**. It then picks out its one job from `snapshot.Jobs` (line 201). This is why UI.B13 happened: each progress or completion window held its own pipe client, and the Host ran out at 8 (the limit is now 64).
- **Side benefit:** a crash in one window does not close the others or the main window. Nothing in ROADMAP or PROGRESS names this as a goal. The design follows from "the Host has no UI, and the main window may not be running".
- **Precedent for the alternative:** Statistics and Scheduler already open inside the main window's process, each with its own `HostConnection(_connection.Target)` (`MainWindow.xaml.cs:1972`, 4286). Progress Details is the odd one out.

### Where the delay goes

Statistics, Scheduler, Options, Add URL and the dialogs are built in the running process. Progress Details pays for a whole cold start.

| Step | Cost | Measured or estimated |
|---|---|---|
| Process + .NET + WinUI/WinAppSDK start + `App` ctor + first window, 0–500 jobs | ~0.55–0.6 s | **Measured for the main window only** (PERF.5: 644–688 ms from `host open` to the first frame with the list). That figure includes ~0.1 s of CLI start and may include Options opening (TEST.4). No ui-trace timing exists for `DownloadWindow`. |
| Pipe connect + full `jobs.snapshot` (JSON parse, then a search for one job) | <50 ms at 500 jobs; ~0.4 s at 5,000 | **Estimated** from PERF.5 (5,000 jobs: snapshot applied at 890–935 ms) and PERF.7 (8.5 MB at 5,040 jobs) |
| Speed-history fetch, segments, first layout of a 640×640 window | ~50–100 ms | **Estimated** |
| In-process open (new `Window` on the running UI thread, like Scheduler) | ~0.1–0.2 s | **Estimated**: the warm show of an existing window (289–329 ms) minus ~0.1 s of CLI start. Building a new window costs somewhat more than showing one. |

So the gap the owner sees when clicking a row is about 0.4–0.5 s with a normal list, and up to about 1 s with thousands of jobs.

Windows the Host opens by itself (on start or on completion) appear without a click, so nobody is waiting on that delay in the same way.

## 2. Proposal: open it in the main window's process, like Scheduler

- **Open in-process.** `OpenDownloadWindow`/`OpenPackageWindow` build `new DownloadWindow(id, manual, target)` / `new PackageWindow(id, target)` on the same UI thread.
  - A dictionary in `MainWindow`, keyed by `(kind, id, serverId)`, replaces the named event for single-instance. If the window is already open, call `ShowWindow()` on it.
  - Each window keeps **its own** `HostConnection`, as Scheduler and Statistics do now. That costs one snapshot per open, which is small at 500 jobs.
  - A shared, ref-counted connection pool is **not** needed for the first step. The main window replaces and disposes `_connection` when it switches servers (`MainWindow.Servers.cs:363`), so sharing it would need a pool first.
- **Lifetime under PERF.5 (the part D cannot skip).** Today `OnWindowClosing` (`MainWindow.xaml.cs:343–357`) calls `Application.Current.Exit()` (and `Environment.Exit(0)` on Uno), which would now also close every progress window. Change it so that:
  - With "ends it" on and progress windows still open: hide the main window and keep it, then exit when the last progress window closes. A counter in `MainWindow`, and the Uno `Environment.Exit(0)` moves there.
  - The main window is **not** released and rebuilt later. The process keeps owning the main activation event, so a tray click or a second launch still shows the same window. The cost is the hidden main window's memory while a progress window is open. There is no extra idle cost, because the process and its GPU-driver threads (PERF.4) are alive for the progress window anyway.
  - Menu Exit and `host.exiting` close everything, as now.
- **In-window events that end the process.** `DownloadWindow` and `PackageWindow` call `Application.Current.Exit()` on `HostExiting` (`DownloadWindow.xaml.cs:193`, `PackageWindow.cs:189`). In-process, they must close themselves instead. Scheduler and Statistics (`SchedulerWindow.cs:231`, `StatisticsWindow.cs:102`) already do the in-process Exit. Leave them as they are; that is only correct because the whole process is exiting with the Host.
- **`ShownServer` must be fixed in this step, not audited later.** `DownloadWindow.xaml.cs:172/198` and `PackageWindow.cs:103/193` write the static `ShownServer.Target`. In-process, opening this PC's progress window while the main window shows a remote server would switch the main window's pickers, path checks and icons to the wrong server (`Pickers.cs:37`, `AddLinksDialog.cs:285`, `FileIconProvider.cs:17`, `MainWindow.xaml.cs:1652`, `ProviderPanel.cs:1449`).
  - Fix: progress windows stop writing `ShownServer`, which then means "the main window's server".
  - Pass the window's own target to the helpers they reach: `JobCompletionEditor.cs:122` and `FileIconProvider`.
- **Completion bursts.** `OpenCompletionWindows` can open many windows in one go. In-process they are built one after another on the UI thread, which can freeze the list for a moment. Build one per dispatcher turn, at low priority.
- **Torrent file choice.** `MainWindow.Torrents.cs:754` (`ProgressWindowName`) checks the named event to see whether a progress window is open. It should check the dictionary first and keep the named-event check for Host-started windows. `DownloadWindow.MainWindowRuns` (line 265) needs no change, because the main window still owns its event.
- **Host-started windows stay as they are.** They are still separate processes with their own per-job events, so those event names stay. This needs no IPC change, no `client.hello` role and no Linux Host change.
- **Crash isolation is lost** for windows opened from the main window. An unhandled exception in Progress Details now takes down the main window and its other progress windows. Mitigation: the existing `ui-crash.log`, and the Host keeps downloading. It is the same exposure Scheduler and Statistics already have.
- **Idle and memory.** Every progress window opened from the list stops being its own process. Each such process carries .NET, WinUI and the GPU driver's two ~95/s wake-up threads (PERF.4); the per-process size is **estimated**, not measured. One fewer pipe client (UI.B13). No resident process or timer is added.
- **Uno/Linux.** The same `MainWindow` change applies. Linux checks are deferred (owner, 2026-11-12), so the Uno multi-window check on the VM waits. PROGRESS already notes that the Host-started progress window did not show under Xvfb.

## 3. Further options (only after measuring step 1)

- **A. Snapshot-free start for the windows that still cold-start** (Host-started windows, and every window's own connection). The window would connect on the hello pipe with `snapshot:false, events:true` and ask for one job plus settings.
  - Today `JobsQueryRequest` (`Contracts/Dtos.cs:396`) has only `Limit`. This needs a job/package filter that also returns settings: an additive IPC change.
  - Gain: ~0.4 s at 5,000 jobs, <50 ms at 500 (est.). It does nothing for the process start.
- **B. ReadyToRun.** No project or `publish.ps1` turns it on today. It might save ~100–200 ms of JIT on every cold start, the main window included. That is an **estimate; measure** it against the larger install folder (REV.3 trimmed it to ~200 MB).
- **C. Host asks a running window process to open the window.** This means a `ui.openWindow` event with an ack, falling back to `LaunchUi`. Points to handle:
  - The main window connects on the main pipe without `client.hello`, so a role registration means moving it to the hello pipe or adding a command.
  - The Host must call `AllowSetForegroundWindow` for the window's process, or the window opens behind others (UI.B4).
  - Exit races under PERF.5.

  It only speeds up windows that appear unasked, at ~1.5–2 days plus Uno checks. **Not recommended unless the owner wants it.**
- **D. A pre-warmed spare process.** It keeps a resident process with GPU-driver wake-ups, which goes against "idle usage over memory" and PERF.5. **Rejected.**

## 4. Recommendation

Do **section 2 only**, measure, then decide on A or B.

- It removes the lag where the owner clicks, follows the Scheduler/Statistics pattern, and needs no IPC or Host change.
- C and a connection pool are optional and not needed for the visible fix.

**Implementation outline:**

- `MainWindow.xaml.cs`:
  - `OpenDownloadWindow`/`OpenPackageWindow` go through a window dictionary.
  - The burst opens one window per dispatcher turn.
  - The PERF.5 closing path, the Uno `Environment.Exit` and Menu Exit follow the open-window count.
- `DownloadWindow.xaml.cs` and `PackageWindow.cs`:
  - No `ShownServer` writes.
  - `HostExiting` closes the window.
  - The target is passed to `JobCompletionEditor` and `FileIconProvider`.
- `PackageWindow.cs:849`: open the job window through the main window's dictionary when the package window is in-process. Keep `Process.Start` when it is a Host-started process.
- `MainWindow.Torrents.cs:754`: check the dictionary, then the named event.
- `App.xaml.cs`: unchanged; the `--job/--package` paths remain for the Host.

**Tests:**

- Desktop unit tests:
  - single-instance per `(kind, id, server)`;
  - closing the main window with "ends it" on keeps open progress windows, and the process ends after the last one;
  - a progress window for this PC leaves `ShownServer` at the main window's remote server.
- Measure: add a ui-trace `First frame` line to `DownloadWindow` and compare before and after with `scripts/perf-window.sh` at 0, 500 and 5,000 jobs.
- Check in the real WinUI app (dark mode). The Uno check is deferred with Linux.

**Effort:** section 2 ~1–1.5 days. A ~0.5 day if wanted later.

**Risks:**

- Lost crash isolation for list-opened windows.
- `ShownServer` call sites missed.
- A long completion burst on the UI thread.
- Hidden main-window memory kept while a progress window is open.

