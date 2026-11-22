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

Pending.
