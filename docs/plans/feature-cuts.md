# Inbound: features we could cut

> **Work in progress (2026-11-12).** The owner is still deciding; ticks in "Decisions for you" are provisional. Live copy: https://claude.ai/artifact/PoMsbp4zfiMLNyabAF2yzY. Nothing here is built.

## Summary

Five features can go with little loss: stream (HLS/DASH) downloading with its FFmpeg step, the Statistics window, queue byte quotas, scheduler file sync, and per-site connection limits. Together they are about 3,700 source lines and 1,600 test lines that nobody asked for and no one is recorded using. Two larger cuts, link container import (about 7,250 lines with tests) and FTP, need your word first, because you named them in the brief or chose to keep them.

1. **Stream downloading (HLS/DASH, FFmpeg, DRM/live checks):** about 2,600 source lines, 38 % of the download engine, and the area with the most audit findings. Media detection already left the extension on 2026-11-06.
2. **Statistics window:** about 480 lines, plus a settings list that grows with every removed download.
3. **Queue byte quotas:** about 350 lines across ten files and four clients.
4. **Scheduler file sync** ("repeat every N minutes"): about 160 lines and one recurring wake-up in an idle Host.
5. **Per-site connection limits:** about 150 lines across six projects and the web UI.

Next come smaller trims and housekeeping: the custom WAV picker, test-only code in the shipped torrent assembly, two leftover tools and stale docs.

The list was built by six readers, one per area, who measured 92 features. A second reader then argued against each of the 34 cut or trim proposals, and that changed 16 verdicts.

## How candidates were judged

A feature is a cut candidate when what it costs to keep is out of proportion to who would miss it. Each feature area was read in the code at `torrent-t1`, and every cut or trim proposal was then argued against by a second reader.

| Measure | What was looked at |
| --- | --- |
| Size | Source and test lines (`wc -l`, no build output) and commits touching its paths |
| Upkeep | Bug and review entries in PROGRESS, the AUDIT.1 and Fable reports, platform work it forced |
| Idle cost | Timers, polling, extra processes or disk work while nothing downloads (ranked above memory) |
| Origin | Whether the owner asked for it, quoted from PROGRESS, ROADMAP or memory |
| Overlap | What else in the app, Windows or the browser already does the same job |
| Who notices | The one private user on Windows, the Docker server user, or nobody |

An owner-requested feature is marked "owner decides" rather than cut, unless its cost is clearly out of proportion. Core download flows (add, resume, queue, browser handoff) were not candidates.

## Strong candidates

Each of these was proposed for a cut and held up when a second reader argued to keep it. None has an owner request on record.

### 1. Stream downloading: HLS/DASH, FFmpeg muxing, DRM and live checks

What it does: a pasted or handed-off `.m3u8` or `.mpd` address is fetched fragment by fragment, decrypted (HLS AES-128) and written as one media file. Split audio and video are joined by an FFmpeg found on PATH or set through the CLI only.

- **Size:** about 2,070 lines in `HttpDownloadEngine.cs` (38 % of its 5,449), 470 for FFmpeg, 50 for the DRM and live checks; about 1,100 test lines, 304 TestServer lines and binary `.ts` fixtures.
- **Upkeep:** the area with the most audit findings (1 high, 5 medium, including H1 and H2 on fragments), and Review 1's open medium N1: a damaged checkpoint restarts a kept track empty.
- **Why it can go:** you removed media detection from the extension on 2026-11-06. Nothing in PROGRESS shows a stream being added by hand since. The FFmpeg path is a setting no window can edit.
- **What goes:** the media path from `ExecuteWithMediaRefreshAsync` to `ExpandDashTemplate`, media checkpoints, segment-token refresh, `FfmpegMediaMuxer`, the `FfmpegPath` setting, the TestServer media routes and the media tests.
- **What stays:** a short check at job start that fails a playlist address with a clear message, such as "Streaming playlists aren't downloaded; use a video downloader", instead of saving the playlist text as the file. Keep `FailureKind.DrmProtected` so old job files still load.
- **Migration:** `ffmpegPath` is ignored on load and dropped at the next save. A media job still in the list fails with the new message. No IPC message goes.
- **Caution:** the record says "The app still downloads stream addresses it is given" (ROADMAP Phase 9), so this reverses a deliberate keep. The DRM and live checks must not be cut on their own while streams stay.

### 2. Statistics window

What it does: Tasks › Statistics and the web Statistics screen show bytes and counts downloaded overall and today, failed and cancelled counts, and the current rate.

- **Size:** about 480 source lines (WinUI 267, web UI 104, runtime 52, plus DTO and archive plumbing) and 60 test lines.
- **Upkeep:** every removed download is copied into `AppSettings.ArchivedDownloads`, which grows without limit and costs a settings write per removal.
- **What goes:** the window, the menu item, the web screen, `statistics.get`, `DownloadStatistics` and the archive record.
- **What breaks:** lifetime and today totals that include removed downloads exist nowhere else. One groups test asserts on statistics and needs a new check.
- **Migration:** `archivedDownloads` is ignored on load and dropped at the next save. The `statistics.get` message is refused like any unknown one.

### 3. Queue byte quotas

What it does: a queue can be limited to N MB per H hours. Reaching it pauses the queue's downloads until the window rolls over.

- **Size:** about 350 source lines across 10 files and the web UI, about 200 test lines.
- **Upkeep:** audit M9 (usage lost on restart, settings saves overwriting it), a special case in settings rebasing and in torrent seeding.
- **What goes:** the five quota fields on `DownloadQueue`, the counting, `ResetQuota`, the quota fields in the queue editors (WinUI, Scheduler, web) and `dm queue quota`.
- **Migration:** a queue that a quota had stopped stays stopped until started. The fields are ignored on load. `QueueDto` loses six optional fields, which older clients ignore.

### 4. Scheduler file sync

What it does: a schedule with "repeat every N minutes" re-checks the finished files of its queue and downloads a file again when the server copy changed.

- **Size:** about 160 source lines and 150 test lines.
- **Upkeep:** a recurring wake-up in an otherwise idle Host, which you rank above memory.
- **What goes:** `RepeatEveryMinutes`, `SynchronizeQueueAsync` and its wake, the repeat box in the Scheduler.
- **Migration:** a schedule that had it runs as a plain daily or one-time schedule. `ScheduleDto` loses one optional field.
- **Note:** Review 2's open medium M1 (a schedule edited inside its window acts up to an hour late) is not caused by this feature and stays to fix.

### 5. Per-site connection limits

What it does: lines such as `*.example.com=2` in Options give matching hosts fewer connections than the global per-host limit.

- **Size:** about 150 source lines across six projects plus the web UI, about 90 test lines.
- **What goes:** `ConnectionProfile`, its lookup, the Options box, the web field, `dm profile` and its tests.
- **What breaks:** a saved pattern stops lowering that host's connections; the global per-host limit applies.
- **Before cutting:** look at Options › Connection › Per-site limits on your install. If it is empty, nothing changes for you.
- **Care:** the live push of a new connection count to running downloads must keep working for the global limit.

### Housekeeping that needs no decision

| Item | What to do | Saves |
| --- | --- | --- |
| `src/DownloadManager.Desktop.Qt.HostReader` | Delete the folder. It is git-ignored build output of the Qt prototype; the source stays on its branch | 914 KB on disk |
| `tools/HookSpy` | Delete. Not in the solution, nothing refers to it | 16 lines |
| `FakeTorrentEngine` | Move from the shipped torrent assembly into a shared test project | 352 lines out of the install |
| `tools/HostChecks` | Move its pipe-ACL check (current user and LocalSystem only) into an Integration test first, then delete the tool | about 400 lines |
| `scripts/prepare-inbound-icon.ps1` | Keep: it is the first step of the icon pipeline, not a duplicate | none |

**Done (branch `cut-housekeeping`, 2026-11-12):** HookSpy is deleted. `FakeTorrentEngine` lives in `tests/DownloadManager.TestSupport`, which only the Torrents and Integration tests reference. The pipe check is `tests/DownloadManager.Integration.Tests/PipeAclTests.cs`: it reads the real Host's two pipes as a client and requires full control for this user and LocalSystem, no other Allow rule, and this user as owner. HostChecks' tray menu check is `tests/DownloadManager.Tray.Tests`; its right-click on the real tray icon, and `scripts/open-tray-overflow.ps1`, which only it used, are gone, so that part of U5 rests on manual evidence. HostChecks is deleted. The Qt leftover folder was not in that worktree; it is still in the main checkout and is deleted there by hand, since it is git-ignored.

## Worth a look: trims and cuts that need your word

These are worth cutting or trimming, but each reverses something you asked for, approved or chose to keep, so none should go without your answer.

### Large: you named or kept them

- **Link container import (RSDF, CCF, DLC hand-off to JDownloader):** about 3,260 source and 3,990 test lines, a whole project, six IPC messages and a server upload route. RSDF and CCF are obsolete and DLC is refused anyway; no import of a real file is recorded. But your TorBox brief lists "Importing compatible JDownloader container files (`.dlc`, `.ccf`, `.rsdf`)" among the requirements.
- **FTP downloads:** about 640 source lines, 660 test lines and a 474-line TestServer FTP server. It carries an open audit finding (a password in the address stored in the clear) and the only obsolete .NET API in the code. Your scope note of 2026-11-05 says FTP is out of scope but "existing backend support is retained".
- **The web UI's full parity rule (D2c):** the web UI itself stays, but mirroring every desktop feature has been about half of all recent web UI commits. You could limit the web UI to what a phone or the Docker server needs and port new desktop features only when asked. This would not remove what is already built, since you asked for torrents "fully" in the web UI on 2026-11-11.

### Torrent extras

- **Per-torrent options:** own speed limits, own seeding goal, download in order, High/Low priority and Announce Now. About 1,800 source and 1,700 test lines across the Host, both windows and the C++ worker. They appear in the mockups you approved on 2026-11-11 ("I agree with everything else"), so cutting them is your call. File Skip/Download and Force Recheck stay either way.
- **Peers and Pieces & Speed tabs in Progress Details:** about 800 to 900 source lines. Trackers, Files and Overview stay. The per-torrent speed graph lives on the Pieces tab and would move to Overview.
- **Six extra torrent sidebar views (T7):** Active, Stalled, Checking, Waiting for files, Completed and Errored beside Downloading, Seeding and Stopped. About 200 source lines would go. They are what T7's "torrent related sort categories" delivered, so ask first whether the three original views were what you meant.
- **Advanced torrent network toggles:** DHT, PEX, LSD, encryption, connection limits and seeds at once in Options. Listen port, firewall rule, UPnP and proxy stay. If they go, today's values must be fixed in code: LSD, UPnP and NAT-PMP are off and all torrents seed at once, unlike libtorrent's own defaults.

### Small trims

- **Custom sound files:** keep the "Play a sound when a download finishes" checkbox with the Windows sound; drop the per-event WAV picker and the failed and queue-finished sounds. About 400 source lines and one Options page.
- **Per-download completion actions:** run a program, open the file or shut down after one download. About 300 source lines. The schedule's when-done actions and the post-download hook stay. An empty per-download program is today the only way to skip the global hook for one download, and the `CompletionActionsStarted` marker is shared with the hook, so it must stay.
- **The CLI's TorBox, torrent and archive commands:** about 1,700 source lines, and every new feature stops needing a CLI twin. Keep `settings set`: it recovered your update key on 0.4.0. Scripts and agent walks use some of these commands.
- **Recent-URL history:** about 90 lines and a settings write per added download. A user reported on the dropdown and you checked the fix (UI.R18), so it stays unless the settings write per add matters to you.
- **Install from the update zip:** stop shipping `install.ps1` and `uninstall.ps1` in the zip, about 300 script lines. Keep `install.ps1` as a developer tool for isolated test installs.
- **Stale docs:** fold `browser-integration-scope.md` (19 lines, wrong about Edge today) into `browser-integration.md`. Move finished plans in `docs/plans` to an archive once the torrent track is merged; about 25 code and doc comments still point at them.

## Keep

These looked cuttable to the first reader or are often cut in other apps, but the second reader found a reason they stay.

| Feature | Why it stays |
| --- | --- |
| Updater feed and key settings | The editable key is how your 0.4.0 install got its update key back (`settings set updatekeyfile`); the key was nearly lost on 2026-11-11 |
| CLI `settings set` and settings export/import | The only owner-facing recovery path that needs no window; `soak.sh` uses export/import to change a setting the CLI has no key for |
| CLI torrent commands | Removes nothing from the Host or IPC, since the windows use the same messages; agent walks repeat them |
| SOCKS4/4a proxies | About 15 lines; .NET does the work. Rewriting a saved `socks4://` to SOCKS5 would break a SOCKS4-only server |
| DRM and live checks | Only safe to remove together with stream downloading; alone, live or DRM streams would finish as broken files marked Completed |
| Web UI list export/import | The CLI can't reach a Docker or remote server, so the web UI is the only export there |
| Saved layouts (`list-layout.json`) | Already your open decision 3; without it, Progress Details' column widths and sort reset on every open |
| UI automation scripts | One is the only check of an open gap (the proxy Options page). `open-tray-overflow.ps1`, which only fed `HostChecks`, went with it |
| Plan documents | The torrent track is not merged; about 25 code and doc comments point at them |
| Soak and window-timing scripts | `perf-window.sh` is the measuring step of T11, which is not timed yet |
| Site logins | Cheap to keep and covers Basic-auth servers; tighten Review 1 N4 (send a bare-host login over https only) |
| Segmented transfers, expired-link refresh, speed limits | Core download flows you use and verified |
| TorBox, archive extraction, post-download hook, clipboard, Download All, numbered URLs | Part of everyday adding and finishing; TorBox and extraction were your own requests |
| Server, Docker, D8 bridge, D9, Linux Host, Uno, AppImage, tray, Firefox, Edge | Owner requests on record; Linux checks are deferred, not dropped |

## Every feature measured

The line counts are the whole feature as the area readers measured it, not what a trim saves; the sections above give the saving. Some counts were corrected by the second reader, for example per-torrent options are about 1,830 source and 1,690 test lines.

| Feature | Area | Source lines | Test lines | Verdict |
| --- | --- | --- | --- | --- |
| HLS/DASH media assembly | Transfers | 2,070 | 855 | Cut |
| FFmpeg muxing | Transfers | 470 | 190 | Cut with streams |
| DRM and live-stream detection | Transfers | 45 | 62 | Cut with streams |
| Statistics window | Desktop extras | 480 | 60 | Cut |
| Queue byte quotas | Desktop extras | 350 | 200 | Cut |
| Scheduler periodic file synchronization | Desktop extras | 160 | 150 | Cut |
| Connection profiles | Transfers | 150 | 90 | Cut |
| HookSpy | Infrastructure | 16 | 0 | Cut |
| Qt prototype leftover | Platforms | 0 | 0 | Cut |
| CLI (trim TorBox, torrent, archive commands) | Platforms | 4,309 | 1,810 | Trim |
| Reference docs | Infrastructure | 2,977 | 0 | Trim |
| Peers and Pieces & Speed tabs | Torrents | 1,596 | 600 | Trim |
| Schedule and per-download completion actions | Desktop extras | 600 | 550 | Trim |
| Event sounds | Desktop extras | 470 | 0 | Trim |
| HostChecks | Infrastructure | 402 | 0 | Trim |
| FakeTorrentEngine in the shipped assembly (move to tests) | Torrents | 352 | 0 | Trim |
| Script install path | Infrastructure | 307 | 60 | Trim |
| Torrent network settings (trim the advanced toggles) | Torrents | 259 | 578 | Trim |
| Web UI full parity rule (D2c) | Platforms | 19,930 | 6,660 | Owner decides |
| Link container import | Integrations | 3,260 | 3,990 | Owner decides |
| Per-torrent options (limits, goal, order, priority, Announce Now) | Torrents | 1,705 | 1,804 | Owner decides |
| T7 extra torrent sidebar views | Torrents | 1,607 | 1,077 | Owner decides |
| FTP downloads | Transfers | 700 | 660 | Owner decides |
| Persisted layouts | Desktop extras | 363 | 150 | Owner decides |
| TorBox provider integration | Integrations | 15,200 | 16,150 | Keep |
| Archive extraction | Integrations | 7,220 | 8,970 | Keep |
| Native libtorrent worker and its supervisor | Torrents | 5,508 | 3,168 | Keep |
| TestServer | Infrastructure | 4,727 | 0 | Keep |
| Plan documents and mockups | Infrastructure | 4,522 | 0 | Keep |
| Headless server | Platforms | 3,877 | 5,468 | Keep |
| Torrent jobs in the Runtime/Host | Torrents | 3,654 | 2,047 | Keep |
| Desktop window manages another Inbound server | Platforms | 3,281 | 1,770 | Keep |
| Web UI bridge in the Windows app | Platforms | 3,270 | 2,758 | Keep |
| External CLI control | Integrations | 3,270 | 600 | Keep |
| Add Links dialog | Desktop extras | 2,700 | 900 | Keep |
| Add Torrent dialog, Add Magnet, Import Torrent File | Torrents | 2,564 | 1,221 | Keep |
| Linux Host | Platforms | 2,498 | 179 | Keep |
| Segmented transfers | Transfers | 2,380 | 1,950 | Keep |
| Bottom speed graph | Desktop extras | 1,752 | 972 | Keep |
| Torrents in the web UI and Docker server | Torrents | 1,662 | 1,030 | Keep |
| Installer-only in-app updater | Infrastructure | 1,600 | 545 | Keep |
| UI automation check scripts | Infrastructure | 1,208 | 0 | Keep |
| Package progress window | Desktop extras | 1,090 | 200 | Keep |
| Inno Setup installer, repair, uninstall and stop-for-setup | Infrastructure | 1,000 | 400 | Keep |
| Seeding and seeding goals | Torrents | 856 | 1,425 | Keep |
| Extension paired-server mode | Platforms | 850 | 680 | Keep |
| T7 Torrent Info pane | Torrents | 747 | 495 | Keep |
| Download All link collection | Integrations | 680 | 250 | Keep |
| JSON download list export/import | Desktop extras | 630 | 250 | Keep |
| Build, publish and dev registration scripts | Infrastructure | 618 | 0 | Keep |
| Store packaging and publishing scripts | Infrastructure | 608 | 0 | Keep |
| Magnet and .torrent association | Torrents | 595 | 513 | Keep |
| Speed limits | Transfers | 590 | 400 | Keep |
| CLI torrent commands | Torrents | 585 | 135 | Keep |
| Firefox build | Platforms | 540 | 156 | Keep |
| Soak and window-performance scripts | Infrastructure | 532 | 0 | Keep |
| Proxy support | Transfers | 530 | 600 | Keep |
| Web UI host switcher | Platforms | 484 | 215 | Keep |
| Completion window and notifications | Desktop extras | 450 | 300 | Keep |
| Category destination rules | Desktop extras | 450 | 300 | Keep |
| Clipboard monitoring | Integrations | 400 | 150 | Keep |
| Site logins | Transfers | 400 | 350 | Keep |
| Uno Linux window | Platforms | 389 | 0 | Keep |
| Expired-URL refresh | Transfers | 360 | 600 | Keep |
| Favourite folders | Desktop extras | 340 | 170 | Keep |
| Batch / numbered URLs | Integrations | 330 | 300 | Keep |
| Docker image and GHCR workflow | Platforms | 316 | 0 | Keep |
| TorBox hoster status window and hoster checks | Integrations | 310 | 120 | Keep |
| TorrentWorkerProbe | Infrastructure | 309 | 0 | Keep |
| T10 .torrent link handling | Torrents | 297 | 1,291 | Keep |
| AppImage packaging, self-update and GitHub workflow | Platforms | 272 | 92 | Keep |
| Window cascade | Desktop extras | 257 | 80 | Keep |
| Windows tray | Platforms | 225 | 0 | Keep |
| Post-download hook | Integrations | 200 | 250 | Keep |
| Sleep prevention | Desktop extras | 200 | 60 | Keep |
| Kill/restart matrix | Infrastructure | 181 | 0 | Keep |
| Duplicate file policies | Desktop extras | 150 | 120 | Keep |
| Torrent list export/import | Torrents | 123 | 80 | Keep |
| T8 upload graph | Torrents | 120 | 250 | Keep |
| Segment map bar | Desktop extras | 110 | 0 | Keep |
| UpdateSigner | Infrastructure | 98 | 0 | Keep |
| Recent-URL history | Desktop extras | 90 | 60 | Keep |
| AppImage packaging scripts | Infrastructure | 91 | 0 | Keep |
| T14 torrent category folders | Torrents | 73 | 419 | Keep |
| Settings transfer | Desktop extras | 70 | 60 | Keep |
| Edge support | Platforms | 60 | 80 | Keep |
| UI trace log | Infrastructure | 43 | 0 | Keep |
| Help > Open Diagnostics Folder | Infrastructure | 40 | 0 | Keep |
| DST handling in schedules | Desktop extras | 30 | 110 | Keep |
| WinUI crash log | Infrastructure | 10 | 0 | Keep |
| Golden pipe frames | Infrastructure | 0 | 264 | Keep |
| Changelog popup | Desktop extras | 0 | 0 | Keep |

## Decisions for you

Tick what you agree to; anything ticked can be built as one cut per branch, each with its settings migration and tests.

- [x] Cut stream downloading (HLS/DASH, FFmpeg, DRM and live checks), leaving a clear failure for playlist addresses (done 2026-11-12, branch cut-streams-ftp)
- [ ] Cut the Statistics window and the archive of removed downloads
- [ ] Cut queue byte quotas
- [ ] Cut scheduler file sync ("repeat every N minutes")
- [ ] Cut per-site connection limits (after checking the Options box is empty)
- [x] Do the housekeeping: delete the Qt leftover folder and HookSpy, move FakeTorrentEngine to tests, move the pipe-ACL check into a test and delete HostChecks
- [ ] Cut link container import, although the TorBox brief lists it
- [x] Cut FTP, reversing the 2026-11-05 "backend support is retained" note (done 2026-11-12, branch cut-streams-ftp; closes Review 1 L11). Reversed again by the owner the same day: FTP returns with the Sites work (ROADMAP SITES.2), so this tick should not be merged to main as final.
- [ ] Limit the web UI's parity rule to what a phone and the Docker server need
- [ ] Trim the torrent extras: per-torrent options, Peers and Pieces tabs, the six extra sidebar views, the advanced network toggles (say which)
- [ ] Trim custom sound files to the one completion-sound checkbox
- [ ] Trim per-download completion actions
- [x] Trim the CLI's TorBox, torrent and archive commands. Narrowed by the owner on 2026-11-12 (CUT.2): `dm torrent` stays; `dm remote` (TorBox) and `dm archive` are cut
- [ ] Stop shipping the script install in the update zip

Each cut also needs its PROGRESS and ROADMAP rows updated, the golden pipe frames re-recorded if a settings field goes, and a line in the changelog for anything a user could notice. No cut is planned on `torrent-t1` until the open review fixes there are merged.
