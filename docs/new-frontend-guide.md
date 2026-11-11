# Building another frontend for Inbound

For anyone, human or LLM, adding a new GUI (Avalonia, GTK, Qt, a web shell). The Host already owns the
engine, persistence, scheduler and tray. A frontend is a client of it, as the WinUI window, the CLI and the web
UI are. This page says what a new GUI has to do and which rules it must keep. It links to the other docs
instead of repeating them.

Read first: `CLAUDE.md`, `docs/architecture.md`, `docs/ipc-protocol.md`, `docs/design-guidelines.md`. For the
toolkit choice see `docs/plans/gui-toolkits.md`.

## 1. What a frontend is, and is not

- It is a client. It connects, shows the Host's state and sends commands. It never touches the engine, the
  state directory or a part file, and it never writes a job, setting or checkpoint itself.
- Machine actions (open a file, reveal a folder, pick a folder, shut down, run on completion) go through the
  Host, so `IHostPlatform` decides what this machine can do. `server.hello` lists capabilities; look a name up in
  that set, do not rely on its position. Hide or grey what the Host says it cannot do.
- Closing the window must not stop downloads. The Host keeps running.
- Reuse the C# client code when the toolkit is .NET: `DownloadManager.Contracts` (DTOs, `MessageTypes`,
  `IpcClient`) and the UI-free rule classes in `DownloadManager.Desktop.WinUI` (for example `FileMenuRules`,
  `SelectionCommands.cs`, `DurationText`, `UiFormat`). The Uno build compiles the whole WinUI folder
  (`src/DownloadManager.Desktop.Uno`), so these are already proven off Windows. Outside .NET, mirror the
  Contracts DTOs and follow the protocol doc.

## 2. The connection

1. Connect (named pipe on Windows; the Linux Host and the server have their own transports, see the protocol doc).
2. The Host sends `server.hello` (protocol version, capabilities, `hostKind`), then `jobs.snapshot`.
3. After that you get `job.event` and other broadcasts for everyone, plus replies to your own requests
   (`replyTo` carries your request's `id`). Ignore message types you don't know; protocol version is 1.
4. The Host handles one connection's commands strictly in order. Calls that wait on the network
   (`remote.*` lookups, `job.refreshUrl` with a new address, `remote.container.sendToJd`) go on a second
   connection, so the main one stays quick. The list is in the protocol doc, "One command at a time per connection".
5. Reconnect when the Host goes away or restarts, and show that state. Do not treat a lost pipe as an empty list.

The commands you will use most: `job.add`, `file.metadata`, `job.pause|resume|cancel|retry|remove`,
`jobs.pauseAll|resumeAll`, `job.update`, `job.moveToQueue`, `queue.action`, `group.action`, `settings.get|update`,
`statistics.get`, `speed.history`, `folders.*`, `host.openUi|shutdown|pickFolder`. The full list is `MessageTypes`
in `src/DownloadManager.Contracts/Protocol.cs`; the meaning of each is in
`src/DownloadManager.Runtime/Ipc/HostCommandDispatcher.cs`.

## 3. Feature checklist ("done" means these)

The WinUI window is the reference (`CLAUDE.md`, "UI structure"). The web UI review
(`docs/plans/webui-desktop-review.md`) is a list of what a second frontend tends to miss. Check each:

- **List:** columns File Name, Q, Size, Status, Time left, Transfer rate, Last Try Date, Description; sortable,
  resizable, with layout remembered; packages expand; state views (Unfinished, Active, Queued, Failed, Finished)
  and Queues in the sidebar; search; multiselect with bulk actions; keyboard (Delete, Shift+Delete, Ctrl+A,
  Ctrl+F, arrows).
- **States:** `queued`, `connecting`, `downloading`, `retryWaiting`, `verifying`, `paused`, `completed`, `failed`,
  `cancelled` (`DownloadState`). A job's `isTerminal` says it is over. Which actions apply to which state, and the
  reasons an item is off, come from the rule classes above. Copy those rules, do not invent new ones.
- **Packages:** group, ungroup, rename, cancel; package status comes from `DownloadGroupSummary`, not from your own
  sums. Pause and Resume on a package go through `group.action`.
- **Adding:** single Add URL with a file-info check, batch/numbered URLs, clipboard monitoring, description,
  category and last-folder memory, duplicate policy (Rename, Overwrite, Fail), authenticated downloads.
- **Delete:** the choice to delete the file or only the list entry. Delete Completed clears the list and never
  deletes files.
- **Progress:** details panel or window with segment map, speed and graph (`speed.history` starts full, so a
  graph opened later is not empty).
- **Queues and Scheduler:** queues, priority, schedules, quotas, completion actions, sleep prevention.
- **Options:** the settings pages, saved with one OK/Cancel/Apply, with unsaved edits on other pages kept.
- **Shell:** open file, open folder, reveal; tray and notifications, completion dialog and sound, updater.
- **Providers:** TorBox and the containers/archives flows, if the Host reports `remote` and `archives`.
- **Servers:** managing another Inbound server (see `docs/docker.md` and the sign-in flow in the protocol doc).
- **Accessibility:** names and live regions for status changes, keyboard reachable everything.

A frontend may ship without some of these only if the gap is written down in `PROGRESS.md` as `partial`.

## 4. Rules

- **No stubs and no do-nothing settings.** An unfinished control is hidden or says why it is off.
- **Windows-only options stay out on other platforms.** Examples: sleep prevention, shell integration,
  native-host repair, Windows service behaviour. The Linux window needed this fix in 0.6.6; ask the Host's
  capabilities, not the OS name alone.
- **Never show secrets.** Show the address a job carries as the Host sends it (`pageUrl` as a site alone, hidden
  addresses by `addressHidden`). Don't log cookies, passwords, API keys or URLs with keys in them. Store a server
  password only with the platform's secret store (DPAPI, Secret Service) as the existing code does.
- **Theme before first frame.** Read the system theme at start, so a dark desktop never flashes light.
- **Single instance.** A second launch activates the existing window instead of opening another.
- **Own branding and assets only.** Follow `docs/design-guidelines.md` for colour tokens, spacing and the
  per-window rules, adapted to the toolkit's own look.
- **Async and cancellable I/O.** No `.Result` or `.Wait()` on I/O; the UI thread never waits on the Host.
- **Nothing durable in the frontend** except its own layout and view state (`UiStateStore` is the example).

## 5. Testing a frontend

1. Use an isolated Host. Set `DOWNLOADMANAGER_INSTANCE` (for example `uiqa`) and `DOWNLOADMANAGER_DATA` to a
   scratch folder, so you never touch the real profile or downloads (`docs/design-guidelines.md` section 7).
2. Serve content from `tools/TestServer` (throttled, failing, resumable files) so every status and colour appears:
   a slow download, a failing one and a small one that completes.
3. Check both themes, a narrow and a wide window, a lost and restored Host connection, and a Host that lacks a
   capability.
4. Keep UI-free logic out of views so it can be unit-tested (see the `*Rules` classes and their tests).
5. Record what you ran, and what you did not, in `PROGRESS.md`. Do not mark a feature `implemented` without
   linked evidence.
