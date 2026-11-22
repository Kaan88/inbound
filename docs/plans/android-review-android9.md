# Fable review of ANDROID.9 and the Remote Files speed fixes (2026-11-22)

Range `772cd2c0..8d26b536`. Two Fable agents, read-only, nothing built or run: one on shared code (Runtime, Contracts,
Core, Host dispatcher) including a check of the fixes for the [earlier review](android-review-49f5663c-c745317f.md);
one on the Android app and Mobile models. Severity: **I** important, **m** minor. Fixes and their status are in
[What was done](#what-was-done).

## Shared code

| # | Sev | Conf | Where | Finding | Suggested fix |
| --- | --- | --- | --- | --- | --- |
| F1 | I | confirmed | `ArchiveDtos.cs` (`DeleteArchives`, `SaveForOtherArchives`, `archive.passwords.*`); `HostCommandDispatcher.cs:140`; `ArchivePasswordSheet.cs:49,93` | A new phone paired with an older Host: the old Host omits `ArchiveSetDto.DeleteArchives`, so the sheet shows "Delete the archive after extracting" unticked while the Host's setting is on; the old Host ignores the field and deletes. "Save for other archives" is silently not saved; the saved-passwords sheet gets "Unknown message type". | A hello capability beside `archives`; without it the phone hides the delete box, the save box and the saved-passwords entry. |
| F2 | m | confirmed | `HostCommandDispatcher.cs:1044-1062` (`RefuseArchiveFor`); `ArchiveCoordinator.Commands.cs:317` | A web session may not turn on the delete-archives setting, but can send `archive.setPassword` with `DeleteArchives: true` for a set in its folders. `archive.passwords.add`/`.remove` are unscoped: a web session can remove every saved password. | Refuse `DeleteArchives == true` from a scoped caller; decide whether password add/remove are pipe-only. |
| F3 | m | confirmed in Host | `SitesCommands.cs:827-834`; `ForgetListings` callers | A finished transfer that writes to a site (upload, relay copy) does not forget held listings, so a phone shows the folder without the new file for up to 60 s. | `ForgetListings(siteId)` when a batch item that writes to the site finishes. |
| F4 | m | confirmed | `SitesCommands.ListFolderAsync`, post-list `BuildAsync`, `StoreListing` | A token renewed during the list saves the site and changes `Revision`, but the listing is stamped with the old revision: the counts follow-up re-lists the server and "load more" gets `ListingChanged` (earlier M5, now on the lazy-counts path). | Stamp the held listing with the revision after the post-list build. |
| F5 | m | plausible | `SiteFilePublication.cs:22-41` | S2 fixed for Linux only: on macOS `renamex_np(RENAME_EXCL)` on SMB/exFAT returns ENOTSUP and throws while the capability is advertised. | CreateNew fallback on macOS for ENOTSUP and EINVAL. |
| F6 | m | plausible | `TorrentConditions.cs:84-123`; `TorrentSeeding.cs:274-287` | Android only: the charger is plugged in while `HoldSeedingAsync` is inside `StopSeedingAsync`; the engine's stopped callback then finds the id not held and clears `Seeding`/`SeedingSince`, so the seeding-hours limit restarts. | Keep a holding marker until `HoldSeedingAsync` returns, or restore `SeedingSince` on resume. |
| F7 | m | confirmed | `ArchiveCoordinator.Commands.cs:268-272`, `:277,305,314` | With "Save for other archives", the password is saved (and asking sets resumed) before the target is validated; "No such set" still leaves it saved. | Validate the target first. |

**Earlier fixes, verdicts:** S1, S3, M2, M3, P1 lazy counts (`sitesCachedFirstPage`), counts 4 at a time, P2, rc job
expiry interval, P4 hold. S2 holds for Linux, incomplete for macOS (F5). P3 holds with side effect F4. P5 holds with F3.
M2 residual: a kept staging file is published even if the remote file changed while it waited.

**Checked and sound:** no hold on Windows or Linux from the torrent Wi-Fi/charging settings (only Android reports
network or power); no new timers or polling; lock ordering (`_seedingGate` then `_sync`; archive `_gate` then `_sync`;
`_listings` a leaf); saved archive passwords sealed with the existing protector, written atomically, never returned,
broadcast or logged; newer schema quarantined; try-next loop finite; per-archive delete saved before the run; TorBox
Host change produces the same JSON bodies and logs no key or link; DTO changes additive; placement journal still reads.

## Android app

Verdict: not ready to release. Native binaries (ELF alignment, hashes) were not inspected.

| # | Sev | Conf | Where | Finding | Suggested fix |
| --- | --- | --- | --- | --- | --- |
| A9-1 | I | confirmed | `Runtime/ConcurrencyLimits.cs:101,123`, `WhyNotResumable` `:363` | A torrent that has received any data keeps running on mobile data: the torrent preempt skips a job whose `WhyNotPreemptable` is set, and that says "its server hasn't said whether it can resume" when `ReceivedBytes > 0` and `ResumeSupported` is null (only the HTTP engine sets it). The test's magnet has 0 bytes. | A torrent always resumes from its pieces: preempt it unless finishing; test with `ReceivedBytes > 0`. |
| A9-2 | I | confirmed | `ArchivePasswordSheet.cs:47-49`, `TorBoxSettingsPage.cs:249`, `ArchiveClient.cs` | Same as F1. | |
| A9-3 | I | confirmed | `TorBoxKeySheet.cs:100`, `ArchivePasswordSheet.cs`, `SavedPasswordsSheet.cs`; cf. `RemoteFilesScreen.cs:766` | The TorBox key and archive passwords are sent over a plain-http pairing; Remote Files refuses site passwords unless the Host is the phone or HTTPS. | Same rule in the three sheets. |
| A9-4 | I | confirmed | `AddTorrentScreen.cs:219-240`; `MainActivity` OnStop | `WaitForFileList` ticks every second with no end and is not paused when the app is hidden. | Stop on stop, restart on start, end on a hold. |
| A9-5 | I | confirmed | `TorrentAddModel.cs:189-215` | On mobile data with Wi-Fi only on, a pasted magnet says "Finding the file list" forever: the model reads only `Peers`, not `Torrent.WaitingFor`. | Show "Waiting for Wi-Fi", stop waiting; test. |
| A9-6 | I | plausible | `ArchiveIpcDispatcher.cs:88-99`, `RefuseArchiveFor` | Same as F2 (saved passwords from a folder-limited web session). | |
| A9-7 | m | confirmed | `AndroidHost.cs:136` | The TorBox key and archive passwords are sealed with an AES key file in the app's private folder, not the Keystore (as site logins already are). Backup and transfer are excluded. | Wrap the key file with an AndroidKeyStore key, or record the decision. |
| A9-8 | m | confirmed | `AddTorrentScreen.cs:196-200`, `Dispose` | Closing Add torrent while `torrent.add` is in flight leaves a stray "Choose files" job. | Remove the job when the reply arrives after dispose. |
| A9-9 | m | confirmed | `TorBoxKeySheet.cs:14-31` (`TorBoxUi.Run`) | An `OperationCanceledException` calls neither callback (buttons stay disabled); an `ObjectDisposedException` shows raw text. | Fail on cancellation; map disposed to "the Host did not answer". |
| A9-10 | m | confirmed | `InProcessHostConnection.cs:178` vs `RemoteHostConnection` `SlowTypes` | The phone's own Host gives slow TorBox requests 60 s (a paired PC 6 min). | Same slow timeout in process. |
| A9-11 | m | confirmed | `MainActivity.cs:541-549`, `MainActivity.ImportList.cs` | A chooser result after the activity was recreated is dropped silently. | Save the pending chooser in instance state. |
| A9-12 | m | confirmed | `TorBoxKeySheet.cs:86` | Backing out of the key sheet mid-save still opens the TorBox page on success. | Not cancelable while busy. |
| A9-13 | m | confirmed | `TorBoxScreen.cs:68,171` | Stale after a paired PC reconnects; every push rebuilds all cards, also while hidden. | Reload on hello; don't render while stopped. |
| A9-14 | m | confirmed | `TorrentText.cs:91`, `TorrentDetailsScreen` `StartTicking` | A seeding torrent held for Wi-Fi or charging counts as running: 2 s poll and watch lease. | Held seeding is not running. |
| A9-15 | m | plausible | `BackgroundLogic.cs:96` | `TEMPORARILY_NOT_METERED` counts as unmetered, so on an unmetered 5G plan torrents run on cellular under "Wi-Fi only". | Require a Wi-Fi/Ethernet transport for torrents, or reword. |
| A9-16 | m | confirmed | `tools/AndroidApp/check-apk.py:18` | The release hook list omits `metered_override` and `wifi_only`; bundled 7-Zip/worker checks are opt-in; no 16 KiB alignment check of the bundled `.so` files. | Add the names; `--release` implies the bundled checks; check `PT_LOAD` `p_align >= 0x4000`. |
| A9-17 | m | plausible | `AndroidHost.cs:243-249` | A remote-jobs start failure other than IO/access fails the whole phone Host start. | Start without TorBox on any non-cancellation failure. |
| A9-18 | m | confirmed | `AndroidHost.cs:279` | rclone stays 3 minutes after the last browse (review P2 trade). | Keep, or stop when the app is hidden with no transfer. |
| A9-19 | m | confirmed | `password_field.xml`, the three secret sheets | No `FLAG_SECURE` on the key and password dialogs; the field can be revealed. | Secure window while shown. |

Outside the diff: `RemoteFilesScreen.cs:480` runs a 3 s `PeriodicTimer` for the life of the screen; the request is
skipped when hidden, the wake-up isn't.

**Checked and sound:** every torrent start goes through `ScheduleCore` (apart from A9-1); a held torrent stays queued
across a restart; magnet inspect does no network work; the seeding hold is checked before the worker; secrets wiped
from fields, never read back, no clipboard/intents/extras, autofill off, backup excluded; logs masked; the TorBox API
override needs two process variables; older-Host handling for `askWhenReady`, inspect data, listing tokens and lazy
counts; reconnect and host switch; listing cache bounds; AndroidHost start/stop order; native items arm64-only with
SHA-256 pins; models match their tests.

## What was done

Fixed in `65264734` (shared code, Opus) and the following commit (Android; Opus for A9-3/4/5/8/9/12/19, Sonnet for
A9-10/13/14/16/17, each diff read and merged by Opus):

| # | Fix | Tests |
| --- | --- | --- |
| A9-1 | A torrent always counts as resumable, so every network or queue rule can send it back to its queue. | `ATorrentThatHasDataAlsoGoesBackToItsQueueWhenWifiIsLost` |
| F1 / A9-2 | Hosts announce `archiveSavedPasswords` with `archives`; without it the phone hides the save and delete boxes and Saved passwords, and sends neither field. | hello capability tests (web, server) |
| F2 / A9-6 | A web session can't make a set delete its archives while the Host's setting is off. | none |
| F3 | An upload whose files-done count moves forgets that site's held listings. | none |
| F4 | A listing is stamped with the site's revision after a sign-in renewal during the list. | none |
| F5 | macOS falls back to CreateNew for EINVAL, ENOTSUP and EOPNOTSUPP. | none (no macOS run) |
| F6 | A "holding" mark covers the worker stop, so its stopped callback can't clear the seeding mark. | none |
| F7 | The set or package is checked before a password is saved for other archives. | none |
| A9-3 | The TorBox key, archive password and saved-password fields are disabled, with a note, unless the Host is the phone or reached over HTTPS (the Remote Files rule). | none (UI) |
| A9-4 | Add torrent's file-list tick stops while the app is hidden, and runs every 5 s while the Host holds the magnet. | none (UI) |
| A9-5 | A magnet held for Wi-Fi says "Waiting for Wi-Fi" instead of "Finding the file list"; it goes on when the hold ends. | `A_magnet_held_for_wifi_says_so_and_looks_again_when_the_hold_ends` |
| A9-8 | The magnet's `torrent.add` isn't cancelled by closing the page; the job it made is removed when the reply comes. | `A_screen_closed_while_the_Host_makes_the_magnets_job_still_gets_the_job_back` |
| A9-9 | A cancelled or disposed TorBox/archive request ends with "The Host did not answer" instead of leaving buttons disabled. | none |
| A9-10 | The phone's own Host gives `SlowTypes` the paired PC's 6-minute timeout. | none |
| A9-12 | The key sheet can't be dismissed while a save is in flight. | none |
| A9-13 | The TorBox page reloads on a new hello and builds no cards while the app is hidden. | none (UI) |
| A9-14 | A seeding torrent held for Wi-Fi or charging doesn't count as running (no 2 s poll or watch lease). | `A_seeding_torrent_held_for_wifi_or_charging_may_not_be_running` |
| A9-16 | `check-apk.py`: full hook list (adds `metered_override`, `wifi_only` and 12 more), `--release` implies the bundled checks, every `lib/arm64-v8a/*.so` must have 16 KiB `PT_LOAD` alignment. The last signed release APK passes. | `test-check-apk.py` (5) |
| A9-17 | Any non-cancellation failure starting TorBox jobs leaves the phone Host running without TorBox. | none |
| A9-19 | The three secret dialogs use `FLAG_SECURE`. | none |
| A1, A2, A8, S6 | Earlier review, idle: unchanged power reports do nothing; the queue job is re-armed only when visibility, sessions or the crash guard change; its notification is posted only on a text change; the queue speed cap waits once per chunk (the "16 s after lifting the cap" part didn't hold: the limiter re-reads the cap every 100 ms). | existing engine and integration suites |

Also fixed: `PersistenceJsonContextTests` failed at HEAD because ANDROID.9 registered `ArchiveSavedPasswords` without
listing it.

**Decisions made while the owner was away (2026-11-22, "do what's best for the app, note decisions"):**
- F2/A9-6: saved-password add and remove stay open to web sessions. A paired phone reaches the desktop Host as a web
  session, so refusing them would break t6 on a paired PC; the passwords are never read back.
- A9-7: kept the AES key file in the app's private folder (as site logins already use; backup and transfer
  excluded). A Keystore wrap is a follow-up for all secrets together, not this release.
- A9-15: kept `TEMPORARILY_NOT_METERED` as unmetered (Android's own meaning). Torrents on an unmetered 5G plan are
  allowed; the label stays. Raise with the owner.
- A9-18: kept rclone's 3-minute idle on the phone (review P2).
- Earlier A4 (battery-saver-held queue wakes every 15 min): not changed. Dropping the retry could leave a queue
  waiting until the app is opened, since nothing wakes a dead process when saver turns off.

**Fable's check of the fixes (same two agents, 2026-11-22, at `82c551c3`):** all fixes approved; the Android agent
"approved for release from the Android side" and accepted the four decisions, A9-15 on condition that the owner says
yes or the subtitle "Peers never use mobile data" is reworded (it is approved mockup text, so it goes to the owner).
Follow-ups fixed in the next commit:
- A paired PC refuses turning a set's delete on (F2), so the password sheet shows the delete box there only when the
  set already deletes, and sends the choice only when it changed.
- Add torrent: no second tick chain when the app comes back while a tick is inside its poll.
- F3 narrowed: a finished upload file drops only the held listings of its folder, the folders inside it and the
  folders above it, so paging elsewhere on the site goes on; a listing being read meanwhile isn't kept.
- A9-11: which chooser is open is kept across a recreate; a list file then opens the review page, a torrent file
  asks to be chosen again.
- Remote Files' 3 s transfer look stops while the app is hidden.

Accepted as they are (Fable's notes): F6 when the worker stop exceeds `SeedStopWait` (rare, Android only); S6's
"left this queue" check runs only for the waiter at the head of the limiter's gate; F7 for a `FilePath` target (a
path that isn't an archive leaves the password saved); a folder-limited browser session can remove saved passwords.

**Still open:** earlier A3, A5-A7, S4, S5, S7, S8-S10, T1-T8 and P6/M4. The fixes go back to the same two
Fable agents for approval.
