# Android step 2 (ANDROID.3): the Host keeps downloading in the background

Status: design, revised after Fable's review (2026-11-17, section 6); waits on three owner questions before code. Rules:
`android-app.md` section 6 point 2 (read 2026-11-17 from the official pages, the load-bearing facts re-checked).
Owner's choices (PROGRESS, 2026-11-17): a user-initiated data transfer (UIDT) job on Android 14+, a dataSync
foreground service (FGS) on 12 and 13; after a reboot a notification with Resume; Wi-Fi only means "not metered".

## 1. What the rules allow, in short

| | API 31 to 33 | API 34+ |
|---|---|---|
| Keeps the process alive | dataSync FGS, no time cap | UIDT job, no cap, no quota |
| Start it | only while the app is visible (or a notification tap) | schedule only while visible (or a notification tap, which is a background-activity-launch exemption) |
| From BOOT_COMPLETED | an FGS start is allowed below targetSdk 35; we target 35, but the cap and the boot ban apply only on Android 15+ | not allowed (boot is no exemption) |
| Network wait | the app's own network callback | the job's required network; the system stops the job when it is lost and runs it again when it is back |

## 2. Shape

> **Superseded in part.** Items 2 (stop handling), 4 and 5 below are the first draft. The current design is
> section 8; sections 6 and 7 say why it changed.

One process, as today: the Host (`AndroidHost`) runs in the app's process. Nothing new talks to the Host except
through `DownloadManagerService`/`HostCommandDispatcher` as the screen does. New pieces, all under
`src/DownloadManager.Android/Background/` unless noted:

1. **`ActivityState`** (in `DownloadManager.Mobile`, plain .NET, unit tested). Fed every job change (`JobDto`, change
   type) and the snapshot. Answers: is anything *runnable* (connecting, downloading, verifying, retry-waiting, starting,
   or queued with a free slot); counts and bytes for the notification (active count, total rate, the one shown
   file, its bytes and ETA); which jobs finished (for the "Finished · Tap to open" notification); which jobs wait for
   an unmetered network. Raises `Changed` coalesced to at most once a second while running, nothing while idle (the
   owner ranks idle cost first: no timer runs while nothing changes).

2. **`KeepAlive`** (Android). Owns the one mechanism per API level, behind one interface:
   - `Start(bool unmeteredOnly)`, `Stop()`, `UpdateNotification(...)`.
   - API 34+: one UIDT job, id 1 ("the session job"), scheduled with `setUserInitiated(true)`,
     `setRequiredNetwork` (any internet, or `NOT_METERED` when Wi-Fi only is on), `setEstimatedNetworkBytes` from the
     remaining bytes. `onStartJob` posts the notification with `setNotification` and returns true; the job is finished
     with `jobFinished(params, false)` when nothing is runnable. `onStopJob`: if the stop reason is the network
     constraint, the Host pauses the jobs that need it and marks them waiting (section 4) and the method returns true
     (reschedule: the system runs it again when the network is back); for a user stop from Task Manager, pause all and
     return false.
   - API 31 to 33: a dataSync FGS started with `startForeground` while the app is visible; it stops itself when
     nothing is runnable. The network wait is the app's `ConnectivityManager` callback (section 4); the service stays
     up while downloads wait for Wi-Fi, with the "Waiting for Wi-Fi" notification.
   - When `Start` is called while the app is not visible and none is running (a queue moving on with the screen off
     is the case that matters), on API 34+ scheduling fails: the running session job already covers it, since it
     lives until nothing is runnable. A start with no session job and no visible app (a schedule at 01:00) is
     step 5's problem (plan: "what Doze does to a scheduled start can wait for step 5"); step 2 logs it and posts the
     "N downloads paused · Resume" notification instead of failing silently.

3. **Notifications** (A7), one channel each, all with the app's own icon:
   - Ongoing: "2 downloads · 16.1 MB/s", the first active file and its time left, a determinate bar for one file and
     the total for several; actions Pause all and Open. On API 34+ it is the job's notification; on 31 to 33 the
     service's.
   - Finished: one per file, "Finished · 640 MB · Tap to open" (opens the file with the system's chooser through a
     `FileProvider` URI; the shared Download folder file is ours, so no extra permission).
   - Waiting for Wi-Fi: "N downloads will resume", action "Use data" (section 4).
   - After boot: "N downloads paused", action Resume (section 5).
   - POST_NOTIFICATIONS (API 33+) is asked for on the first start (A0's Notifications row is the place; until A0 is
     built in step 3, ask when the first download is added). Denied: downloads still run (an FGS and a UIDT job do
     not need it; the system shows them in Task Manager), only the drawer entries are missing.

4. **Wi-Fi only** ("not metered", owner). Where the setting lives: **an Android setting kept by the Android
   platform in the state folder** (`android-settings.json`, atomic write), not in the shared `AppSettings`, because
   no other platform has it. C7 shows it under Network; until the Settings screen (step 5) there is no UI, and the
   test build sets it through the adb hook. Fable: is a platform-side setting right, or should it be a Host setting so
   a paired desktop can see it (ANDROID.7)?
   - On a metered network with the setting on: the running jobs are paused by the platform and their ids are kept in
     `android-settings.json` as "waiting for network" (so a user pause is never undone, and a death in between is
     survived); the Waiting notification shows.
   - When an unmetered network comes back: the waiting ids are resumed (only those still paused, and paused by us).
   - "Use data": resumes the waiting ids now and keeps them off the rule until each finishes (kept in the same file).
     The tap is a notification action, so on API 34+ it may schedule the session job (any network).
   - API 34+: the session job's network constraint does the waking; API 31 to 33: the network callback while the
     service is up.

5. **After a reboot** (owner: "Notify with Resume"). A `BOOT_COMPLETED` receiver (also `MY_PACKAGE_REPLACED`) reads
   the job list from the state folder *without starting the Host* (count of unfinished, not user-paused jobs), and if
   any, posts "N downloads paused · Resume". Resume's `PendingIntent` opens a translucent `ResumeActivity` that
   starts the Host, resumes those jobs, starts `KeepAlive` while visible, and finishes at once (no screen shown).
   Also tried and reported, not built on: whether a persisted UIDT job (`setPersisted(true)`) runs after a reboot
   without the app; if Android does it, the owner is asked whether to resume silently.

6. **The Android `IHostPlatform`.** Capabilities stay as now except: `SupportsReveal` true, with `OpenFile` and
   `RevealFile` through intents (`ACTION_VIEW` with a `FileProvider` URI; reveal opens the Downloads app at the
   folder). `OpenUi`/`ShowJob` bring the activity forward when visible, otherwise do nothing (no activity start from
   the background). Shutdown, tray, updater, folder picker: false, as now.

7. **Lifetime of the Host.** Unchanged: started on first need (screen, job, service, Resume), lives as long as the
   process. The session job or the FGS is what keeps the process alive; when nothing is runnable they end and the
   process may be cached or killed, which is fine (all state is on disk).

## 3. What is not in step 2

Picked folders (engine change, own item); the Settings screen (step 5); scheduled starts with the app closed
(step 5); "Open with" for links (step 4); the A0 screen itself (step 3).

## 4. Slices for Sonnet (each with tests where it is plain .NET, each reviewed by me, the step by Fable)

- S1 `ActivityState` + tests (Mobile).
- S2 `KeepAlive` for API 34+ (UIDT) and 31 to 33 (FGS), the ongoing notification with Pause all and Open; manifest
  permissions (`FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC`, `RUN_USER_INITIATED_JOBS`, `POST_NOTIFICATIONS`,
  `RECEIVE_BOOT_COMPLETED`, `ACCESS_NETWORK_STATE`).
- S3 Wi-Fi only: the platform setting, the network callback, the waiting set, "Use data".
- S4 Finished notifications, `FileProvider`, `IHostPlatform` open and reveal.
- S5 Boot receiver, Resume notification, `ResumeActivity`.

## 5. Pass (from the build plan), on both phones, test server throttled so each wait is shorter than the download

Goes on through Home, swipe-away and 30 minutes of screen off; after a reboot it resumes from its checkpoint through
the Resume notification; after a force-stop it resumes when the app is opened; hash equal each time; the
notification actions work; nothing but metadata is written to the state folder. Also record: boot to resumed;
behaviour with battery restricted and unrestricted; on the S24 (API 37) what a swipe does to the UIDT job and
whether a persisted UIDT job runs after a reboot. The 6-hour FGS cap cannot be hit on 31 to 33 (none there); the
Mi 8 Lite (API 30, test builds only) runs the FGS path.

## 6. Revision after Fable's review (2026-11-17)

Fable's findings, checked in the code where they are code claims (1, 2, 8 confirmed: `Schedule()` starts and
requeues jobs by itself and is gated only by `_updateQuiescing`, `DownloadManagerService.cs:4358`; `PauseAsync` and
`PauseAllAsync` set `PausedByUser`; restart behaviour depends on state, `PausedByExit`, `AutoResumeAfterRestart` and
`AutoResumeUnfinishedOnStartup`). What changes:

- **Wi-Fi only moves into the Host (replaces section 2.4).** A pause from the phone side comes after the Host has
  started a job on mobile data, and it looks like a user pause, so a waiting set could undo a user pause or lose a job.
  New slice **S0, a `src/` change with its own review**: an `AppSettings` flag (not-metered only) shown only where a
  new `PlatformCapabilities` flag says the platform can tell metered networks apart (the phone; the desktop does not
  show it); the platform tells the service whether the network is metered; while the rule holds, `Schedule()` starts
  nothing, and running jobs are stopped back to Queued (not user-paused, no new mark), so they start again by
  themselves when the network is not metered. No waiting set, no per-job mark. "Use data" lifts the rule for the
  session until the next not-metered network (session-wide, as A7 shows it: "3 downloads will resume · Use data").
  Desktop suites must stay green; the Sites rework on main also edits this file, so the merge is noted.
- **Wake lock on both paths.** An `ISleepPrevention` with a partial wake lock (and a Wi-Fi lock) for Android. The
  Runtime only calls it when `PreventSleepWhileDownloading` is on; the Android first run sets it on (C7 lists "sleep
  prevention" under Background and battery, so it stays a user setting). Screen-off checks run unplugged.
- **The session job against the system's stops.** `updateEstimatedNetworkBytes`/`updateTransferredNetworkBytes` as
  the session changes; every stop reason other than the network and the user's own is held as a system stop
  (nothing marked, return true); "runnable" includes every timed wait (`RetryWaiting`, `QueueRetryAt`). On a network
  stop with the rule on, nothing needs pausing (S0 does it); return true.
- **One constraint per job.** Toggling the rule or "Use data" reschedules id 1, which stops the running one with
  "cancelled by app": that reason is ignored (no pause). The session job keeps "any network" and S0 decides what
  runs, so mixed sessions need no second job; the job only ends when nothing is runnable or waiting.
- **Notification actions** all go through a direct activity `PendingIntent` to `ResumeActivity` (a plain `Activity`,
  translucent theme), which schedules or starts `KeepAlive` before `finish()`, after the Host has started. After
  Pause all, the ongoing notification becomes "N downloads paused · Resume".
- **Boot count (section 2.5)** uses one pure function in Runtime, "would this job resume at the next start", shared
  with reconcile and unit tested; the receiver reads the job store and writes nothing.
- **Spike first (S-1)**, on the S24 FE (API 37), before S2: what a swipe does to a running UIDT job; whether a
  persisted UIDT job runs after a reboot; what a Task Manager stop does; whether scheduling from a notification
  action works. The results may change sections 2.2 and 2.5 and go to Fable.
- **Pass list additions:** a Wi-Fi-only check ("waits, it does not fail"); the build plan's section 6 measurements
  owed at the end of step 2 (idle CPU and wake-ups, an hour with the screen off, rate at 1 and 8 connections).

Order: S-1 spike, S0 (Host network rule), S1 `ActivityState`, S2 `KeepAlive` and notifications, S3 the phone's
network signal and "Use data", S4 finished notifications and open/reveal, S5 boot and Resume.

**Owner's answers (2026-11-17):** a Task Manager Stop is the user's own pause ("Stay paused"): at the next start,
`ApplicationExitInfo` (reason user-requested) marks the jobs that were running as paused by the user. Android 12 and
13: an Android 13 emulator now ("Emulator now"). The Wi-Fi-only check may mark the home Wi-Fi metered on one test
phone, read first and restored ("Yes, set it back").

## 7. Spike S-1 results (S24 FE, Android 17 / API 37, 2026-11-17)

Test build with `Spike.cs` (a UIDT job that only logs a heartbeat every 15 s). Logs and screenshots in
`artifacts/android-app/step2/` (git-ignored).

| Question | What happened |
|---|---|
| Scheduled while visible | `schedule` success, `onStartJob` at once (15:40:14). |
| Screen off and locked | Kept running (heartbeats on through the lock). |
| Swipe from Recents | The activity was removed; the process (same pid) and the job went on (heartbeats 14 to 16 after the swipe). |
| Task Manager "Stop" (the Recents "active apps" sheet) | Process killed at once ("fully stop ... by user request"), **`onStopJob` not called**, job dropped ("because of user stop"). At the next start `ApplicationExitInfo` has reason 10 (user requested), sub-reason "STOP APP" (an adb force-stop gives "FORCE STOP"; a reinstall reason 16). So "Stay paused" can be built on it. |
| Schedule from a notification action, app not visible (launcher on top) | A **broadcast** action scheduled the UIDT job successfully and it started (15:47:01). The activity route was not needed. |
| Persisted UIDT job across a reboot | **It ran again by itself** about 50 s after boot (after the owner unlocked), without the app being opened. |
| `BOOT_COMPLETED` receiver | **Not called** within 3 minutes of boot on this Samsung (the receiver is registered; the broadcast was sent to others). Not relied on. |

What it changes:
- After a reboot, a persisted session job brings the process back by itself on API 34+, so downloads *can* resume
  silently. The owner chose "Notify with Resume"; the design said the owner is asked if a silent resume is possible
  (section 2.5). **Owner, 2026-11-17: "Resume silently"** (replaces "Notify with Resume" on API 34+): the session job
  is persisted; when it runs after a reboot it starts the Host, which resumes what reconcile resumes (user pauses and a
  Task Manager Stop stay paused), with the usual ongoing notification. On 31 to 33 the Resume notification stays.
- The boot path cannot depend on `BOOT_COMPLETED` on Samsung. On API 34+ the persisted job is the boot hook (it can
  post the Resume notification itself, or resume). On 31 to 33 there is no such job; the receiver stays, best effort.
- Notification actions may be broadcasts on API 37 (tested); `ResumeActivity` is only needed where an activity is
  wanted. Still to try on API 34 (no phone; emulator image not installed) and 33 (emulator).

## 8. Current design (2026-11-17, after Fable's second review; supersedes 2.2's stop handling, 2.4 and 2.5)

**Keep-alive.** API 34+: one persisted UIDT session job (id 1, any network, `setEstimatedNetworkBytes` from what is
left, refreshed with `updateEstimatedNetworkBytes` at the start of every run and as the session changes). API 31 to
33 (and the API 30 test phone): a dataSync FGS with immediate foreground behaviour. Both hold a partial wake lock and a
Wi-Fi lock through the Android `ISleepPrevention`. They run while `ActivitySummary.KeepAlive` is true (something can
transfer now) and end when it turns false (`jobFinished(false)` / `stopSelf`).

**Wi-Fi only.** The Host's rule (S0). The platform reports the network before `InitializeAsync` and on every change.
While the rule holds, nothing transfers, so the keep-alive ends; on API 34+ the platform schedules the session job
again with `NOT_METERED` (to be tried: whether a running job may do that from its own process when not visible, as a
broadcast action could), and the system starts it when such a network comes; on 31 to 33 the FGS stops and a network
callback registered while the app runs (best effort when the process is gone) starts it again. "Use data" is a
broadcast action (works from the background on API 37; `ResumeActivity` is the fallback where `schedule` fails). A
job that can't resume keeps running and the notification warns that stopping it starts over, with Stop (owner: "warn
about stopping").

**Stops.** `onStopJob`: cancelled-by-app (our own reschedule) is ignored; every other reason is a system stop:
nothing is marked, the Host keeps its state, return true. A user stop (Task Manager Stop or Settings Force stop) gives
no callback: at the next start, the newest `ApplicationExitInfo` that belongs to the last run (matched by a run id
saved with `setProcessStateSummary`) with reason 10 sets the Host's "stopped by the user" start option, so interrupted
downloads start Paused as the user's (owner: "Stay paused", and Force stop the same). Each record is acted on once.

**After a reboot.** API 34+: the persisted session job runs by itself (seen on the S24); `onStartJob` posts "Resuming
downloads" at once with `setNotification`, starts the Host (network reported first), and either carries on (owner:
"Resume silently") or calls `jobFinished(false)` when reconcile resumed nothing. API 31 to 33: a `BOOT_COMPLETED`
receiver (best effort; it did not arrive on the Samsung) posts "N downloads paused · Resume", counted with the
Runtime's pure "would resume at start" function, without starting the Host. Otherwise downloads resume when the app is
opened.

**Crash loop.** If the last three runs ended in a crash or ANR during a download, the session job is not rescheduled
and the notification says downloads are paused after repeated errors, with Resume.

**Still to try:** the same checks on API 33 (emulator), a held session overnight unplugged (battery, stop reasons),
whether a persisted job survives an app update, a reschedule from the job's own process with a new constraint.
