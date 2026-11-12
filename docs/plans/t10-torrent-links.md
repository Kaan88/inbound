# T10 proposal: links to .torrent files and magnet links

## What happens today

- **Extension (all builds).** There is one source tree, `extensions/chrome/src`, and `scripts/build.mjs` builds it for two targets: `dist` for Chrome and Edge, and `dist-firefox`. Neither build handles torrents, and `extensions/chrome/tests/no-torrent-handling.test.cjs` keeps it that way:
  - it fails if torrent, magnet, btih, peer or seed wording appears in the source, pages, manifests, store listing or built folders;
  - it fails if either manifest has `protocol_handlers`;
  - it checks that `decide('ubuntu.torrent','application/x-bittorrent')` gives the same result as an ordinary `.bin` file;
  - it checks that a magnet link is never caught and that Download All never collects one.

  The reason is in `docs/plans/native-torrent-roadmap.md` rule 5 and in the "Legal and store policy" section of `native-torrent-research.md`.
- **Intake to a job.** The extension sends the URL, cookies, referrer, user agent, `FileName` and `ContentType`. These arrive as an `AddJobRequest` (`Contracts/Dtos.cs`), and `DownloadManagerService.AddJobAsync` adds the job. It keeps the content type as `HandoffContentType` (`Runtime/DownloadManagerService.cs:703`, `Core/Models.cs:199`).
- **The progress window.** `Runtime/DownloadWindows.cs` asks for a Progress Details window when a browser job starts with `startNow`. It asks again when the job finishes, if the completion dialog is on. `Host/Program.cs` then calls `HostController.OpenDownload`, which starts a new window process with `--job <id>`.
- **Result.** Clicking a `.torrent` link gives a small file in Downloads, a progress window that opens and closes (and possibly a completion dialog), and nothing else. To use the file, the user has to drag it in or use Import Torrent File.
- **The app's torrent intake already exists.**
  - `MainWindow.TorrentAdd.cs` (`AddTorrentsAsync`, `RunTorrentChoiceAsync`) opens `TorrentAddDialog`, where each line can go to This PC or TorBox. With torrents off, lines go to TorBox if there is an account. Otherwise the user sees `TorrentsOffText` and an Open Options button.
  - `Remote/AddLines.cs:185-187` (`IsMagnet`, `IsTorrentPath`) and `TorrentRules.IsTorrentInput` accept only a magnet or a local path. A `.torrent` address with `://` is treated as an ordinary file.
  - On the Host side, `Runtime/TorrentCommands.cs` (`ReadTorrentInputAsync`) reads a local `.torrent` path, an `upload:` reference, or base64 `TorrentAddRequest.Data`. It uses `Runtime/Remote/TorrentMetadata.cs`, which has a cap of `MaxFileBytes` = 16 MB. The torrent keeps the file's bytes, not its path. A torrent that is already in the list returns `Existing`.
- **Explorer and browser launches.**
  - `Host/TorrentHandlerRegistration.cs` registers `Inbound.Torrent` and `Inbound.Magnet` to run the window exe with `--open-torrent "%1"`. This is off until the user ticks it in Options › Torrents. Linux uses `Host.Linux/LinuxTorrentHandler.cs`.
  - `Remote/TorrentOpenRequests.cs` writes each request to the `open-requests` folder and signals the running window. `MainWindow` collects everything waiting into one dialog. Requests that arrive while another dialog is open are held (`_heldTorrentInputs`, `holdWhenBlocked`) and opened in a later dialog. A request older than 2 minutes is dropped.
  - A magnet link reaches the app only through this association, after the browser's own "Open Inbound?" prompt. If the association is off, a magnet click does nothing in Inbound.
- **Web UI and server.**
  - `webui/src/lib/torrentIntake.ts` and `screens/TorrentAdd.svelte` handle magnets and `.torrent` uploads.
  - `Server/TorrentUploads.cs` turns an upload into an `upload:` reference.
  - TorBox takes a `.torrent` upload (`Providers.TorBox/TorBoxClient.cs`).
  - The server has no window: `ServerPlatform.SupportsOpenUi = false`.

## Options

### A. Download as usual, then the Host offers the file as a torrent (recommended)

The extension does not change and the guard test stays green. The download itself works as it does today, with the browser's cookies, so links behind a login keep working.

1. **Detection (Runtime, when a job completes).** A new `Runtime/TorrentFileRule.cs` runs only when the job looks like a torrent: `HandoffContentType` or the response type is `application/x-bittorrent`, or the final file name ends in `.torrent`. The final name can come from `FileName`, Content-Disposition or the URL. When the job looks like a torrent, the rule checks two more things:
   - the size is at most `TorrentMetadata.MaxFileBytes`, the cap the app already uses;
   - the file parses with the existing `TorrentMetadata` parser.

   Other files are never opened, so the rule has no idle cost.
2. **Windows.** If the add already looks like a torrent (content type, file name or URL), `DownloadWindows` opens no progress window. If the rule passes at completion, it opens no completion dialog either. When only Content-Disposition revealed the name, the progress window may already be open. It closes at completion, as it does when the completion dialog is off. This needs a check in the app.
3. **What the user sees.**
   - **Torrents on, or a TorBox account:** the existing torrent dialog opens with this file as a line, showing the route and the file list.
     - **Added:** the finished row and its `.torrent` file are removed. The torrent already holds the bytes.
     - **Cancelled:** nothing changes. The row stays as a plain finished download, and its right-click menu gets "Open as torrent".
   - **Torrents off and no TorBox account:** the behaviour is the same as today, with no new note or setting. "Open as torrent" on the row shows the existing `TorrentsOffText` with Open Options.
4. **Getting the dialog up.**
   - The Host calls a new `IHostPlatform.OpenTorrent(jobId)`, implemented in `HostController` and `LinuxHostPlatform`.
   - It starts the window exe with `--open-torrent "<path>"`, the same entry point the Explorer association uses. If a window is already running, that launch only queues the request and signals the window.
   - Linux checks wait, as the owner deferred them.
   - The Host waits about one second after the last such completion and then sends all of them at once. That way Download All of ten torrent links fills one dialog instead of one dialog plus held ones. The wait runs only after a torrent completes, so there is no timer when idle.
5. **The add carries the job.**
   - Add `TorrentAddRequest.FromJobId`. The Host reads the finished file itself, adds the torrent, and on success removes the job and the file. On `Existing`, it removes them and shows the existing row.
   - This works the same from the window, the web UI and the CLI, and no path needs to be passed around.
   - If the window is managing another server (D9), the request is not shown there, and the row action remains.
6. **Web UI and server.** The same rule runs in the server's Runtime. With no window, the finished row gets "Open as torrent", which opens `TorrentAdd.svelte` with `FromJobId`. There is no extra notice. The paired-server extension and its popup do not change.
7. **Settings.** None. The rule uses `TorrentsEnabled`, the TorBox account and `TorrentRoute`.
8. **Edge cases.**
   - **Login-only link:** handled by the normal cookie handoff.
   - **Too big or not parseable:** stays a plain file, and no dialog opens.
   - **Duplicate:** `Existing` removes the copy and shows the existing row.
   - **Another dialog is open:** the request is held and opened after it.
   - **The user does not decide within 2 minutes, or the Host restarts:** the queued request is dropped, but the file and the "Open as torrent" row action remain.
   - **HTML error page saved as `x.torrent`:** fails the parse and stays a plain file.
   - **Download All of ten links:** one dialog, because of the wait in step 4.
9. **Implementation outline.**
   - Runtime:
     - `TorrentFileRule` in the completion path, modelled on `ArchiveCoordinator`;
     - `JobDto.TorrentFileReady`;
     - `FromJobId` in `TorrentCommands`;
     - the window exemptions in `DownloadWindows`.
   - Platform: `OpenTorrent` with the wait.
   - WinUI: the row action.
   - Web UI: the row action.
   - Tests:
     - Runtime: content type, name, octet-stream with a `.torrent` name, the cap, a bad file, and other files left unopened;
     - no progress or completion window for a detected torrent;
     - `FromJobId` add, duplicate and failure;
     - the wait (ten completions give one launch);
     - an Integration test where TestServer serves a `.torrent` behind a cookie;
     - a WinUI rules test and a web UI vitest;
     - the extension guard, unchanged.
   - **Effort:** about 2 to 3 days.

### B. Detect from the response headers and keep the bytes out of Downloads

The engine keeps a torrent-typed response under the cap in memory and passes it as `Data`.

- **No file and no window:** neither appears.
- **Engine write path:** it changes, and the bytes would sit in memory or the state folder, which breaks the payload-placement rule.
- **Host restart:** the bytes are lost if the Host restarts before the user decides.
- **Octet-stream:** a response without a `.torrent` name is still missed.
- **Effort:** about 4 to 5 days plus engine fault tests. Not recommended now.

### C. Detection in the extension

- **Store rule:** this breaks rule 5 and the store guard, because it adds torrent wording and magnet catching. It would need a separate, unlisted build with its own ID registered in the native host's allowed origins.
- **Little benefit:** the gain over A is small, because the Host already receives every download.
- **Not recommended.**

## Recommendation

**A.** The extension and the store listings do not change. It uses the existing dialog, `--open-torrent`, `TorrentMetadata` and the TorBox route, adds no setting and no idle work, and removes the stray file and window. If the brief `.torrent` row still feels awkward, B can be added later for responses whose headers already say torrent.

Magnet links stay as they are. When the association in Options › Torrents is on, the browser hands the link to the OS, the association runs `--open-torrent`, and the same dialog opens. When it is off, a magnet click does not reach Inbound, and the user can paste the link into Add URL.

