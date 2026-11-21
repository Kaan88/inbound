# Review of 49f5663c..c745317f (2026-11-21)

The 136 commits from `49f5663c` to `c745317f` were made outside Claude sessions. Four Opus agents reviewed them
read-only on 2026-11-21, split by area: shared code (Runtime, Contracts, Core, Engine, Persistence, Sites), Android
Remote Files, the rest of the Android app, and `tools/AndroidApp` scripts. Nothing was built, run or measured on a
device. The lead checked the findings marked **verified** against the code; the rest are as the reviewer reported.
Fable then reviews this report and the code it cites ([verdict](#fable-verdict)).

Severity: **I** important, **m** minor. Confidence: confirmed in code, or plausible.

## 1. Remote Files is slow on the phone (owner item 15)

Phone and desktop use the same `SitesCommands.BrowseAsync`, `RcloneProcess` and rclone flags. The phone asks for more
work and often starts rclone cold.

| # | Finding | Where | Sev | Conf |
| --- | --- | --- | --- | --- |
| P1 | The phone always asks for folder child counts; desktop never does. After the listing returns, the Host lists up to 8 subfolders one after another (whole child list each, a second `BuildAsync` and lease) within a 2 s budget. Up to +2 s on every folder, Back and breadcrumb tap. **Verified.** | `RemoteFilesScreen.cs:238`; `SitesCommands.cs:889-917` | I | confirmed |
| P2 | rclone stops after 60 s idle, so most opens are cold: `rclone version` spawn, `rcd` spawn and `rc/noop` poll of an 88 MB binary, `config/create`, token refresh, listing, counts. **Verified (60 s).** | `RcloneSupervisor.cs:22`; `SitesCommands.cs:1116` | I | plausible |
| P3 | A renewed cloud token is written back only on the next `BuildAsync` on the same rclone; if rclone idle-stops first it is lost, so every cold start refreshes again (OneDrive may lose a rotated refresh token). Shared code. | `SiteRemote.cs:211-224` | I | confirmed |
| P4 | Cancelling a browse during a cold start (Back, another location) cancels the supervisor start and kills the half-started rclone; the next try starts from zero. | `RcloneSupervisor.cs:139-144`; `RcloneProcess.cs:250` | m | confirmed |
| P5 | The 1-minute listing cache serves only offset > 0; Back and breadcrumb taps always re-list. **Verified.** | `SitesCommands.cs:813` | m | confirmed |
| P6 | SFTP/FTP/WebDAV obscure the password on every build with a random IV, so rclone's fs cache never matches: a new SSH/TLS handshake on every call; `RcloneClient._secrets` grows without bound. Shared code. | `SiteRemote` Sftp/Ftp/WebDav builders; `RcloneClient.cs:66` | I | plausible |
| P7 | UI thread: every selection tap rebuilds all rows (cards in a ScrollView, no RecyclerView); `RenderPhoneFiles` queries all of MediaStore Downloads on the UI thread; the first `Process.Start` can run on the UI thread. | `Folder.cs:400`, `:483`; `InProcessHostConnection.RequestAsync` | m | confirmed |

Ruled out: the managed HTTP handler from `5c12a1af` keeps pooling. Confirm with Stopwatch logs around acquire, build,
list and counts, one cold and one warm open on the S24 FE, before and after fixing.

## 2. Remote Files bugs

| # | Finding | Where | Sev | Conf |
| --- | --- | --- | --- | --- |
| R1 | SFTP trust saves only the first offered host key; if rclone negotiates another algorithm the browse fails and the next tap asks again as "key changed" (likely owner item 4). | `RemoteFilesScreen.Sftp.cs:23`; `RemoteFilesScreen.cs:255` | I | plausible |
| R2 | rclone is not tied to the app process on Android; after the app is killed it keeps running with tokens in memory until the next start sweeps it; Android 12+ phantom-process killing can end it mid-transfer. | `RcloneProcess` / supervisor | m | plausible |
| R3 | Breadcrumb chips come from absolute path segments and can browse above an SFTP site's configured root folder. | `Folder.cs:276-327` | m | confirmed |
| R4 | Phone-originated upload exists only in test builds (`INBOUND_RCLONE_PROBE`); the production app has no upload. | `Upload.cs:15-26` | info | confirmed |

No credential leak found: random loopback password via environment, redacted errors, password field not saved.

## 3. The 15 owner items: code responsible

| Item | Cause |
| --- | --- |
| 1 Banner always shown | `Locations.cs:85` always adds it; `:244` writes "No active transfers". |
| 2 Raw names, one icon | `Locations.cs:286` shows `site.Name`; `LocationIcon` (`:377`) maps all clouds to one icon; `:384-386` shows only sign-in state. |
| 3 No SFTP free space | Fetched only during a browse, inside the same 2 s budget as acquire, build and a fresh SSH handshake (P6); never fetched for the list. |
| 4 Trust asked again; wrapping | R1, plus `Locations.cs:306`; fingerprint is plain dialog text (`Sftp.cs:39`). |
| 5, 15 Slow loading | Section 1. |
| 6 Breadcrumb | Chips only from path segments, no root chip (`Folder.cs:276-327`). |
| 7 Bottom bar always shown | `UpdateFolderFooter` (`Folder.cs:405`) always renders; disabled delete only changes tint (`:436`). |
| 8 "To Downloads" | Hard-coded (`Folder.cs:418-419`); `AndroidHost` sets `UseCategoryFolders = false` on first run. |
| 9 Unsorted files | Sorted oldest first by full date (`SitesCommands.cs:840-842`), but rows print `d MMM` without the year (`Folder.cs:369`), so it reads as unsorted (Fable, verified). |
| 10 Counts missing | Only the first 8 folders, inside the 2 s budget, failing silently (P1). |
| 11 Tap only selects; short menu | `Folder.cs:393-401`; `ShowFolderMenu` (`:572-582`) has two items. |
| 12 Middle-cut names | Single-line middle ellipsis set in code (`Folder.cs:364-365`). |
| 13 This phone is a folder picker | `RenderPhoneFiles` (`Folder.cs:479-505`); upload is test-only (R4). |
| 14 Cards, broken glyph | `CreateLocalRow` (`Folder.cs:526-569`) uses cards and the text glyph "▱" (`:551`) instead of `ic_remote_folder`. |

## 4. Shared code (affects desktop and the headless server)

| # | Finding | Where | Sev | Conf |
| --- | --- | --- | --- | --- |
| S1 | Queue `RequireCharging` / `RequireBatterySaverOff` are enforced on every platform, but only Android reports power, so on Windows or the server such a queue waits forever; the dispatcher refuses every condition change there, including clearing it. Reachable via settings files, copied data, or copying settings between servers. **Verified.** | `QueueConditions.cs:14-19`; `HostCommandDispatcher.cs:690`, `:736` | I | confirmed |
| S2 | Safe single download fails on Linux filesystems without `RENAME_NOREPLACE` (CIFS, NFS, older FUSE); the fallback is Android-only, yet the Linux server advertises `sitesSafeSingleDownload`. **Verified.** | `SiteFilePublication.cs:18-42`; `HostCommandDispatcher.cs:100-104` | I | confirmed |
| S3 | Editing a site's username, TLS mode, vendor, provider, region or auth method silently drops its stored password and key; the save succeeds and fails later at sign-in. Changes desktop and web UI too. | `SitesCommands.cs:603-629` | m | confirmed |
| S4 | Settings compare-and-swap is incomplete: several writers save `_settings` without bumping `Revision`, so `settingsCompareAndSwap` overpromises. | `DownloadManagerService.cs` save sites | m | confirmed |
| S5 | Queue create/delete round-trip the whole settings DTO through `UpdateSettingsCoreAsync` (`caller: null`): an unrelated invalid setting blocks them, and proxy/torrent/engine limits are re-applied. | `QueuePolicyCommands.cs:12-29`, `:70-81` | m | plausible |
| S6 | Per-chunk queue speed cap allocates a linked CTS and a 100 ms timer per chunk; lifting the cap does not wake a delay computed at the old rate (16 s at 1 KB/s). Idle CPU. | `HttpDownloadEngine.cs:31-51` | m | confirmed |
| S7 | File rename and listeners run under locks (`PublishSafeAsync` under `_sync`; `RefreshUrlAsync` raises and schedules while holding gates). | `SiteTransfers.cs:1215-1221`; `DownloadManagerService.cs` RefreshUrl | m | confirmed |
| S8 | Test scaffolding in production assemblies behind false flags: `WebDavConditionalUpload.cs`, S3 fixture branch hard-coded to `127.0.0.1`, `CreateS3FileAsync`, `afterExclusiveOpen`, `TorrentClassificationForTesting`; `sites.device.createOpen` is accepted from any client. | `SitesCommands.Device.cs:81-109`, `:274-322` | m | confirmed |
| S9 | Dead `create`/`delete` branches left in `QueueActionCoreAsync`. | `DownloadManagerService.cs:~1985-2031` | m | confirmed |
| S10 | WebDAV staging file orphaned on a 200/204 PUT or a cancelled PUT (test path). | `WebDavConditionalUpload.SendAsync` | m | confirmed |

Checked and sound: lock ordering (settings gate, address gate, persist gate; no cycle), the RefreshUrl transaction,
`BrowserRefreshRequest` cookie and referrer rules, queue-generation migration, `SiteFilePublication` flags on Windows,
macOS and Android, WebDAV `MOVE Overwrite: F`, SFTP endpoint re-probe, scheduler refactor equivalence, and DTO
back-compatibility (nullable fields, appended capabilities). BOMs were stripped from three files (diff noise).

## 5. Rest of the Android app

| # | Finding | Where | Sev | Conf |
| --- | --- | --- | --- | --- |
| A1 | Every `ACTION_BATTERY_CHANGED` (about once a minute on a charger) re-runs requeue, `SettingsChanged`, `Schedule()` and a JobScheduler re-arm; `SetQueuePowerStateAsync` has no unchanged check, unlike `SetPlatformTransfersAllowedAsync`. Idle wake-ups. **Verified.** | `QueuePowerMonitor.cs:28-53`; `QueueConditions.cs:51-64` | I | confirmed |
| A2 | During transfers the queue job is re-armed about once a second (snapshot clone of all jobs plus binder calls) because every coalesced activity tick runs `Evaluate` then `RequestArm`. | `InboundBackground.Evaluate`; `QueueWorkScheduler.cs:43-71` | I | confirmed |
| A3 | Downloads behind a redirect are probably refused by the built-in browser: evidence is keyed by the final URL and WebView does not report redirect hops; the link fixture never redirects a download. | `BrowserScreen.Capture`; `link-fixture/server.py:110` | I | plausible |
| A4 | A queue blocked only by battery saver has no JobScheduler constraint, so the phone wakes every 15 min while saver is on; the 15 min back-off also follows a clean finish. | `QueueWakePlan.cs:18-24`; `QueueWorkScheduler.Finish` | m | confirmed |
| A5 | A keep-alive that is scheduled but never attached leaves its crash-guard lease open, so a later ordinary process death can count toward the crash-loop guard. | `InboundBackground.cs` `UidtKeepAlive.Start`; `CrashGuardStore` | m | plausible |
| A6 | A downloaded update stays offered even if the signed feed later points back to an older version (pulled release). Downgrade/replay protection itself is correct. Owner decision. | `AndroidUpdater.CheckCoreAsync` | m | confirmed |
| A7 | The daily update job is scheduled only when none is pending, so a later version's changed job definition never replaces it. | `AndroidUpdates.cs:103-111` | m | confirmed |
| A8 | The queue job's ongoing notification is re-posted on every activity tick. | `QueueWorkScheduler.UpdateRun` | m | plausible |

No unit coverage for the Android-only pieces (`QueueWorkScheduler`, `JobRunIdentity`, `BrowserScreen.Capture` cookie
gating, `CrashGuardStore` leases). Checked and sound: release signing (pinned signer, single signer, apksigner first,
password only as a file reference, fresh publish folder, atomic staging), update manifest and notes signatures, URL and
redirect limits, pre-install APK checks, test hooks fully behind `INBOUND_TEST_BUILD` / `INBOUND_RCLONE_PROBE`,
link-catching cookie rules and paired-Host consent, WebView hardening, settings CAS on the phone side, and UI-thread
marshalling.

## 6. Test scripts (`tools/AndroidApp`, about 19k lines)

| # | Finding | Where | Sev | Conf |
| --- | --- | --- | --- | --- |
| T1 | `run-remote-files-host-probe.ps1` accepts any serial, so it would install the debuggable probe on the owner's phone without the display baseline. All other runners require `emulator-*`, AVD `inbound33`, API 33 and the probe package. **Verified.** | `run-remote-files-host-probe.ps1:1-33` | m | confirmed |
| T2 | Fixtures copy the mockups' content (`media/recordings/2026`, "Home NAS", queue "Night", host "Parity Host"), and every run used the emulator with a probe APK, so parity measured the fixture. | `remote-files-parity-fixture.py:12-52`; `capture-remote-files-parity.py:396-397` | m | confirmed |
| T3 | With `--manual-live-auth`, a real Drive token sits in `%TEMP%\inbound-c3-expired-*`; a hard kill leaves it there. | `capture-c3-paired-expiry.py:59-60`, `:170-192` | m | confirmed |
| T4 | A fixture pairing password is printed by a standalone `main()` and typed via `input text`. Throwaway loopback values. | `paired-host-parity-fixture.py:338`; `capture-c4-paired-download.py:152` | m | confirmed |
| T5 | `check-apk.py --release` lists 7 of about 20 test-hook extra names (the build-flag guard is the real protection). **Verified.** | `check-apk.py:18-20` | m | confirmed |
| T6 | `.gitignore` has no `__pycache__/`. **Verified.** | `.gitignore` | m | confirmed |
| T7 | One-off runners load other scripts' classes with `SourceFileLoader` and patch them; untested coupling. | `capture-a7-actions.py:9-23` and others | m | confirmed |
| T8 | About half the Python "unit tests" only search source text; `test_core_screen_parity.py:43-59` tests argparse itself. | listed in the reviewer's report | m | confirmed |

No device-safety problem in the other runners: no `pm clear`, `logcat -c`, `settings put`, reboot, root or first-device
selection. Recommendation: keep release tooling and the behavioural loopback fixtures; merge the reusable harness
helpers; delete the one-off 0.0.9 case runners and per-case runbooks once the matrix links their evidence (git keeps
them); label staged fixtures as staged.

## Fable verdict

Fable reviewed this report against the code on 2026-11-21 (read-only).

**Verdicts.** Confirmed by reading code: P1, P4, P5, P6 (pre-existing, worse on the phone: one SFTP browse builds the
remote three times), P7 (row rebuild), R1 (raise to confirmed), R3, S1, S2, S3, S6, A1. Adjusted:
- P2: `rclone version` runs once per app process, not per cold start; each cold start is `rcd` spawn, noop poll,
  `config/create`, new TLS and a possible token refresh.
- P3: minor, and not introduced in this range; on the phone the counts' second build usually saves the token.
- S2: narrower; only `sites.transfer.safeDownload` (sent only by the phone) reaches it, so it hits a phone paired with
  a Linux server saving to CIFS/NFS. It throws inside `lock (_sync)`.
- S8: `sites.device.createOpen` is refused unless the probe flag is set; it is dead test code that holds `_gate`
  across an HTTP upload.
Not checked by Fable: R2, R4, S4, S5, S7, S9, S10, A2-A8, T1-T8. A3 needs a redirecting fixture before it is believed.

**Missed findings.**

| # | Finding | Where | Sev |
| --- | --- | --- | --- |
| M1 | The "SFTP server key changed" dialog shows only the new fingerprint and one tap replaces the whole pinned set. With R1 causing false "changed" prompts, the owner learns to accept them and a real interception looks the same. Fix with R1: trust all offered keys, show old and new, make the changed case harder than one tap. | `RemoteFilesScreen.Sftp.cs:36-43`; `SitesCommands.TrustAsync` `:752-757` | I (security) |
| M2 | Safe download deletes the finished temporary file in `finally` even when publication returns `Waiting`, so an execution revoke at that moment downloads the file again. | `SiteTransfers.cs` safe-download branch | m |
| M3 | Where shared storage rejects `RENAME_NOREPLACE`, every safe download is written twice with no flush. Unmeasured on the S24. | `SiteFilePublication` Android fallback | m |
| M4 | The idle stop holds the supervisor gate through `core/quit` (up to 5 s); a browse at the 60 s mark waits, then cold-starts. | `RcloneSupervisor.cs:301-349` | m |
| M5 | Every site save (including a renewed token) changes `Revision`, so "load more" after a renewal fails with `ListingChanged` and re-lists with counts. | `SiteStore.SaveAsync` | m |

No IPC back-compat break found in the dispatcher or DTO diffs.

**Slow loading.** P1 explains the 4-5 s subfolders (about 2 s of counts on a 1-2 s Drive listing, on every open, Back
and breadcrumb tap). P1 and P2 do **not** explain 20 s: the code accounts for about 5-8 s on a first open. Measure
before the redesign: version spawn, `rcd` start to first noop, `BuildAsync`, `operations/list`, counts, reply to first
frame; one cold and one warm open on the S24. Ranking: P1, P2 with P4, P5, P7; P6 for SFTP only; P3 marginal.

**Fix order.**
1. Timing logs and one cold and one warm S24 run.
2. Host-side performance (one batch, shared code, tests): P1 counts and free space out of the browse reply, fetched
   lazily after rows show (also items 3 and 10); P5 keep visited listings; P2 keep rclone warm while the screen is open
   and start it when the tab opens; P4; M4; P6; P3.
3. Remote Files redesign against c3/c4 as one UI milestone: items 1, 2, 6, 7, 8, 9, 11-14, a progress indicator for
   item 5, P7 (RecyclerView), R1 with M1 (item 4), R3, and the owner's decision on R4 (upload).
4. Shared regressions for desktop and server: S1, S2, S3 (refuse instead of dropping), M2, M3.
5. Idle wake-ups: A1, A2, A4, A8, S6.
6. The rest: A3 with a redirecting fixture first, A5-A7, S4, S5, S7, test code removal (S8-S10), tooling (T1, T5, T6).
