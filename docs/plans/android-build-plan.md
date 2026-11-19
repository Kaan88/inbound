# Inbound for Android: build plan (for the agent that builds it)

Written 2026-11-16 after the first trial (ANDROID.2). It is for an Opus-led session to follow from top to bottom.
`docs/plans/android-app.md` stays the description of what the app is (decisions, mockup, feature map, Android
unknowns, motion). This file says in what order to build it, what "done" means at each step, and what must not be
guessed. Where the two disagree, this file is newer; fix the older one in the same commit.

Read first, in this order: `CLAUDE.md`, this file, `docs/plans/android-app.md`, PROGRESS "Android first trial
(ANDROID.2)", `docs/new-frontend-guide.md`, `docs/design-guidelines.md`.

## 1. What is decided

| Question | Decision | Source |
| --- | --- | --- |
| Engine | The existing .NET engine, no Rust port | Trial: PROGRESS, ANDROID.2 |
| Build type | **NativeAOT** with `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1`, arm64 | Owner, 2026-11-16 ("nativeaot it is unless you have a strong argument") |
| Screens | Native Android views with Google's Material 3 components (`Xamarin.Google.Android.Material`), no MAUI/Uno/Avalonia | Owner, 2026-11-16 |
| Oldest Android | 12 (API 31) | Owner, 2026-11-15 |
| Handing out | Signed APK on the releases page, the app checks there for updates | Owner, 2026-11-15 |
| Battery mark | Closed as met (better than Jelly on the Mi 8 Lite, equal to Edge on the S24 FE). Chrome is not owed | Owner, 2026-11-16 |
| Remaining measurements | Taken on the real app, not on more trial builds (section 6) | Owner, 2026-11-16 |

**No strong argument against NativeAOT was found**, but one thing is unproven and is the first gate (step 1): the
Material 3 screens have only run on the default (Mono) build, and the engine has only run on NativeAOT with a
text-log screen. They have not run together. NativeAOT for Android is marked experimental in .NET 10. The default
build is the fallback: on the S24 FE it draws a 200-job list 0.8 s after a cold start (the default Mono build), and it is the build to use on
the emulator and for day-to-day work anyway, because NativeAOT builds are slow.

**Answered by the owner in step 0 (2026-11-16):**

| Question | Answer |
| --- | --- |
| Application ID and name under the icon | `app.inbound.android`, "Inbound" |
| Default destination | The shared `Download/` folder by path, directly (no subfolder) |
| The Mi 8 Lite is Android 11, below the minimum | "Test builds min API 30": test builds (`-p:InboundTestBuild=true`, Release/NativeAOT with the adb hooks) go down to API 30 so it stays the slow phone; release builds stay at 31 |
| Which phone the 2 s cold mark is held to | "S24 FE + Mi 8 Lite, but only within reason." (My reading: "within reason" is for the Mi 8 Lite.) |

Answered 2026-11-17: plain `http://` stays allowed in release ("yeah"); targetSdk 35 ("15, unless you have a good
reason"); ship on NativeAOT ("Yes"). Milestones are reviewed by Fable, and commits are pushed as they are made.

**Still to ask when it comes up:** anything a mockup leaves open: long-press, what a finished row does when tapped,
empty states, errors. (The empty Downloads list is answered, 2026-11-17: "same ui with just no links", like the
desktop: A1's layout with no rows.) Ask, do not
skip and do not invent. Where a mockup contradicts an owner decision, the decision wins: A0's folder line says
"Downloads/Inbound", but the owner chose `Download/` directly. The known faults of the mockup are listed in
`android-app.md` section 2.

## 2. Rules for this work

- **Where:** worktree `C:\Users\kaank\Downloads\idm-android-trial`. Never work in or switch the branch of
  `C:\Users\kaank\Downloads\idm`; other sessions share it. Start a branch `android-app` from `android-trial` once
  step 0 is done. Push each commit to `origin android-app` (owner, 2026-11-16 and 2026-11-17: "push as you go"); no
  merge to main unless the owner asks.
- **Who builds:** the owner's method. A Sonnet agent builds each slice from a tight brief with a time limit; you
  (Opus) review the code critically and rewrite what falls short yourself, not by sending it back; you decide what
  is accepted and you re-run the checks yourself before recording a figure. After every major change or decision,
  the same Fable reviewer agent reviews it (spawn once, continue it with SendMessage); verify its findings before
  acting. **When Fable hits its usage limit** (owner, 2026-11-16: "when, not if"), an Opus agent reviews instead and
  the review is written to `docs/plans/android-fable-review-queue.md`, for Fable to check once the limit resets.
  Durability, concurrency, the service lifecycle and anything touching `src/DownloadManager.{Core,Engine,
  Persistence,Runtime,Contracts}` stay with you.
- **The mockup is the spec.** Build each screen from its image in `docs/plans/android-mockups/` (and the Figma file,
  linked in `android-app.md`), not from a text summary. A screen is done only when a screenshot from a phone sits
  beside the mockup image in `artifacts/android-app/pairs/<screen>.png` and you have looked at the pair. Show the
  owner the pairs at the end of each step.
- **Evidence.** Nothing is marked done in ROADMAP without a PROGRESS entry that names the raw files or the test.
  Say plainly what was not checked. No unlabelled stubs and no setting that does nothing: a screen that is not
  built yet is not reachable. `docs/new-frontend-guide.md` section 3 is the checklist the app is held to; a gap is
  written down as `partial`, not left out.
- **The shared engine.** The trial needed no change under `src/`. Keep it that way where possible. A change there
  must keep every existing test green on Windows and is called out in PROGRESS.
- **Record the owner's words** (reports, answers, requests) in PROGRESS and ROADMAP in the same turn.

### Phones and tools

- adb is `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`, not on PATH. Start exes from Bash, never PowerShell.
  Always pin `ANDROID_SERIAL`; an emulator (`kweb35`, API 35 x86-64) and two phones may be attached at once.
- Mi 8 Lite (LineageOS, Android 11, serial `63316e92`): the slow phone. It is below the supported version (12);
  the owner allowed test builds with min API 30 for it (2026-11-16). Never lower it in a release build.
- S24 FE (Android 17, wireless adb, the owner's daily phone): the owner's own phone. The A54 (Android 16) was lent
  for one test only; ask before using it again. Install and remove
  only our apps and delete only our files. Change no setting without asking; put back anything changed and say so.
  Ask the owner to unlock the phone before any timing: `dumpsys window | grep isKeyguardShowing` must say `false`.
  A locked phone turned a 0.8 s start into 2.5 to 3.0 s in the trial and went unnoticed for one run.
- Test server: `tools/AndroidTrialServer` (`--lan --port 5601` for a phone on Wi-Fi; without `--port` it picks
  another port). Chrome on the S24 FE cannot download from it (ERR_TOO_MANY_RETRIES, cause unknown); Edge can.
- Scripts from the trial: `tools/AndroidTrial/run-*.sh` (engine, host, service, storage, start time, battery) and
  `tools/AndroidToolkits/run-toolkits.sh` (start, memory, frames, idle; takes `RID=android-arm64`).

## 3. Shape of the code

- `src/DownloadManager.Mobile` (`net10.0`, no Android types): what the screens show and do, as plain classes fed by
  the host's snapshot and events and sending its commands (start from `tools/AndroidToolkits/Shared/
  DownloadsModel.cs`). Unit-tested in `tests/DownloadManager.Mobile.Tests` like the other test projects. The same
  classes serve a remote host later (step 6), so they talk to an interface, not to the in-process host directly.
- `src/DownloadManager.Android` (`net10.0-android`): the Android host (`IHostPlatform` for the phone, the service,
  notifications, storage, boot receiver) and the screens (activities, fragments or views, adapters, resources).
  Start from `tools/AndroidTrial/AndroidHost.cs`, `TrialService.cs` and `tools/AndroidToolkits/Native/`.
- **Keep both out of `DownloadManager.slnx`'s default build** unless the Windows build stays unaffected on a machine
  without the Android workload: `DownloadManager.Mobile` can go in; the `net10.0-android` project gets its own
  script (`scripts/build-android.ps1`: Debug/Mono for the emulator, Release/NativeAOT/arm64 for phones).
- The trial folders (`tools/AndroidTrial`, `tools/AndroidToolkits`) stay as they are for measurements. Do not grow
  the app inside them.

## 4. Steps

Each step ends with: tests green, the on-device checks re-run by you, pairs shown where there are screens, a Fable
review, a PROGRESS entry, the ROADMAP item ticked, a commit, and an installable APK the owner can try. Stop and ask
when a step's "ask" item comes up; carry on with the rest meanwhile.

**Step 0. Close the trial (ANDROID.2).** Merge branch `android-phone` into `android-trial` (four commits: trial scripts, the CPU probes and the test server's `--lan`).
Tick ANDROID.2 in ROADMAP with the decisions of section 1 and the deferred list of section 6. Update
`android-app.md` sections 1, 4 and 5 to the decisions. Ask the two questions of section 1 (ID and name; default
destination) and the Mi 8 Lite question.

**Step 1. Gate: Material 3 on NativeAOT with the host in process.** A new `src/DownloadManager.Android` with one
screen (Downloads, as in the toolkit trial) fed by the real in-process host, built Release/NativeAOT/arm64.
Pass: installs and starts on the S24 FE and one other phone; lists 200 seeded jobs; one real download completes
with the hash equal; kill and resume works; no trimming or AOT warning is left unexplained (the trial's NativeAOT build had 172 trim
warnings from reflection JSON and still ran; go through them by group, and source-generated JSON for Contracts and
settings is a change under `src/` that needs its own review); cold start to a drawn list is under 2 s on both phones, unlocked (held to the S24 FE and the Mi 8 Lite, answered in
step 0). Record APK
size and memory. **If it does not pass within about a day of effort, stop and bring the owner the evidence**; the
fallback is the default build, which already passes on the S24 FE. Do not start step 2 on an unproven build type.

**Step 2. The host on the phone (ANDROID.3).** The Android `IHostPlatform`; the service that keeps downloads going
with the screen off; the notification with progress, Pause and Open; network rules (Wi-Fi only waits, it does not
fail); resume after the system kills the process and after a reboot; default destination. **Picked folders are out of
this step:** the trial showed they work only through the picker's grant, which needs a small file-access interface in
the engine and a location field on the job and checkpoint (PROGRESS, "Picked folders"); that is an engine change and
its own item, to be confirmed with the owner together with the default destination. **Check the current Android rules in the documentation before choosing** between a data-sync foreground
service and a user-initiated data transfer job, per version from API 31 up; `android-app.md` section 6 point 2 is
from memory, and PROGRESS ("Android's rules, from the fetched pages") has an agent's reading that nobody re-read or
ran: a data-sync service is limited to 6 hours a day on Android 15+ and may not start from `BOOT_COMPLETED`, and a
transfer job must be scheduled while the app is visible. So "resume after a reboot" may have to mean "resumes when
the app is next opened, with a notification offering it"; find out what is allowed, then ask the owner which
behaviour they want. A force-stopped app stays stopped until the user opens it. If the A7 mockup's wording about the background has to change, ask. Pass, on a phone, with the test server throttled (`bps=`) so the download lasts longer than each wait: it goes on
through Home, swipe-away and 30 minutes of screen off; after a reboot and after a force-stop it resumes from its
checkpoint in the way agreed above; hash equal each time; the notification actions work; nothing but metadata is
written to the state folder. Also record: boot to resumed, and behaviour with battery set to restricted and to
unrestricted, and what Doze does to a scheduled start (the last one can wait for step 5).

**Order and reviews changed by the owner (2026-11-17).** "put an apk on my desktop for the first usable version, by
that I don't mean local downloads only, remote connections too." and, asked whether remote hosts move up, "Move remote
up": the order is now step 2, step 3, **step 6 (other hosts)**, then an APK of that build on the owner's desktop, then
steps 4, 5 and the rest. "from now on, hold fable reviews until completion. and use opus for reviews.": clarified: "until
completion meant all the milestones, until then use opus for single mileston reviews." An Opus agent reviews each
step; Fable reviews once after all steps (`android-fable-review-queue.md`).

**Step 3. Core screens, mockup row A (ANDROID.4).** Before starting: twelve of the mockup images are small captures
(`android-app.md` section 2). Export them and the board at full size from Figma, or ask the owner to, and commit
them; do not build a screen from a 236 px capture. First run, Downloads (all three states), Add, New download,
Details, the background note, multiselect, delete with the file choice. Dark and light, dynamic colour as a switch
(off by default), the motion of `android-app.md` section 7 including the container transform and "Remove
animations", TalkBack labels, back gesture. Pairs for every screen A0 to A7 and C8 (light).

**Step 4. Catching links, row B (ANDROID.5).** Share target and the Caught sheet, the built-in browser tab (check
what its download event really gives: address, cookies, referrer, user agent; and that the sign-in pages of common
sites work in a WebView at all, since some refuse it), all links on a page with filters
and selection, expired-link refresh. Check whether "Open with" can be offered for web links before building B1's
note; if not, tell the owner and drop the note.

**Step 5. Queues, scheduler and settings (ANDROID.6).** C1, C2, C7. Every setting shown does something on the phone;
desktop-only settings are not shown.

**Step 6. Other hosts (ANDROID.7).** Pairing, the host switcher (C5, C6), every built screen against a remote
Inbound server through the server's existing API.

**After that:** Remote Files (ANDROID.8, owner approved bundling rclone in the standard APK; see `ROADMAP.md`
for remaining work), TorBox, torrents and archives (ANDROID.9, screens not designed), release (ANDROID.10: signing key,
updates, third-party notices). Updates are not only a check (owner, 2026-11-17): the app downloads the new APK itself and
offers Install through the system installer. Ask the owner after step 3 whether a first signed build should go out before
steps 4 to 6 are finished.

## 5. What "tested" means here

- `DownloadManager.Mobile`: unit tests for every state a screen can show and every command it sends.
- On a device, scripted with adb in the style of `tools/AndroidTrial/run-*.sh`: download, kill and resume, reboot,
  network change, notification actions. Keep the scripts in `tools/AndroidApp/` and their raw output under
  `artifacts/android-app/` (git-ignored).
- By sight: the pairs, plus a walk through each screen's taps on a phone. A UI Automation pass that only proves a
  button exists is not a check of the screen.
- The existing suites (`scripts\test.ps1`) stay green on Windows after any change under `src/`.

## 6. Measurements left for the real app

Take each once the step that makes it meaningful is done, on a phone, unlocked where a screen is involved, and
record it against the owner's four marks.

| Measurement | Mark | When |
| --- | --- | --- |
| Idle CPU and wake-ups, screen off, service up, no jobs, 5 minutes or more | none measurable | End of step 2 |
| One hour of downloading with the screen off | completes, no stall | End of step 2 |
| Cold start to a usable list, unlocked, slowest supported phone available | 2 s | End of steps 1 and 3 |
| Warm start to a usable list | 1 s | End of step 3 |
| Rate at 1 and 8 connections against the link, once, as a check that the real app matches the trial (1 connection was at the edge of 10% on the Mi 8 Lite) | within 10% | End of step 2 |
| Boot to resumed downloads | note the figure | End of step 2 |
| Janky frames while scrolling with downloads running | note the figure | End of step 3 |
| Memory of the service, idle and while downloading (android-app.md section 5) | note the figure | End of step 2 |
| Size of the installed app (android-app.md section 5) | note the figure | End of step 3 |

Also owed from the trial: android-app.md section 6 point 2 (how long a service may run) was never re-read or run;
step 2 reads the current rules before it chooses.

Known from the trial, so not owed again unless the transfer path changes: CPU per GB and battery on NativeAOT (Mi 8
Lite and S24 FE; the method is in PROGRESS and `tools/AndroidTrial/run-battery.sh`), storage by path in shared
Downloads, resume after a kill.
