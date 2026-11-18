# Android progress — Codex

## Ownership and scope

Taken over on 2026-11-18 at the owner's request: "basic app functionality is in"
and "you'll work on this project now." Continue the existing Android app.

This report records Codex's work and handoffs. `GOAL.md`, `ROADMAP.md` and
`PROGRESS.md` remain the authoritative project tracking documents. Read
`CLAUDE.md`, `docs/plans/android-build-plan.md` and the latest shared handoff
alongside this report; retain the owner's decisions and acceptance requirements.

## Starting checkpoint

- Branch: `android-app`; worktree: `C:\Users\kaank\Downloads\idm-android-trial`.
- Starting commit: `49f5663c` (Android release key made and backed up; cleanup recorded).
- Local branch and live GitHub branch matched; worktree was clean before these notes.
- Main checkout: `C:\Users\kaank\Downloads\idm`, branch `main`; left unchanged.
- Android and main have diverged. No merge to main without the owner's request.

## Inherited implementation and evidence

- Native Android Material 3 views, .NET 10 NativeAOT arm64, Android 12/API 31 minimum
  (API 30 only for designated test builds), target API 35.
- Shared .NET engine/runtime; platform-independent screen logic in
  `DownloadManager.Mobile`; Android UI, in-process host and background lifecycle
  in `DownloadManager.Android`.
- ANDROID.3 phone host complete. ANDROID.4 core screens and ANDROID.7 remote hosts
  built and used on the owner's S24, with acceptance gaps below.
- Shared handoff records approval of all 16 Fable findings at `b104f6eb` and
  passing Integration 813, Mobile 340 and Server 208 tests. These are inherited
  records, not fresh Codex test results.
- NativeAOT TLS callback export and stale release-build startup crash were fixed.
  Run `tools/AndroidApp/check-apk.py` on every APK before delivery or installation.
- Permanent release signing key generated and encrypted backup verified, per
  shared handoff. At takeover, signing the next APK remained to do; completed below.

## Open work and acceptance gaps

1. Signed release APK built, verified, reinstalled and launched on the S24 FE (2026-11-18); full UI and download acceptance remain open.
2. Android 0.0.2 and feed published; S24 updated preserving data, emulator real-feed check current. Public newer-offer upgrade remains open for a future release.
3. ANDROID.5: link catching (share sheet, built-in browser, page links, expired links).
4. ANDROID.6: queues, scheduler and remaining settings.
5. Complete ANDROID.10 distribution and production update acceptance;
   then ANDROID.11 web UI recommendation for the Android app.
6. Existing acceptance gaps: phone light mode, dynamic colour and TalkBack;
   HTTPS pairing on a phone and a publicly trusted HTTPS server.
7. Review follow-ups: Keystore read in OnCreate; InboundBackground.Evaluate lock
   scope; user-start detection when queue/group controls arrive. Launcher/icon and
   APK checker changes came after Fable's latest approval.
8. ANDROID.8 Remote files and ANDROID.9 TorBox/torrents/archives remain deferred;
   do not infer they are part of the next increment.

Older roadmap headings and review-queue waiting notes conflict with later dated
completion/approval entries. Use the newest explicit entry and linked evidence;
do not turn a documentation claim into new acceptance evidence.

## Codex work log

### 2026-11-18 — Orientation and takeover notes

- Read project conventions, authoritative tracking, Android plans and review queue;
  inspected the branch history and key Android/Mobile architecture files.
- Verified Android worktree cleanliness and GitHub branch tip with read-only Git.
- Created this report and linked it from CLAUDE and the shared PROGRESS handoff.
- Routine documentation spot check delegated to available `gpt-6.1-terra` because
  the requested `openai/gpt-6-luna` is unavailable in this session; findings verified.
- No build, tests or device checks rerun. No app behavior, secrets or phone state changed.

## Reporting convention

For each increment record the change, commit/checkpoint, checks actually run,
raw evidence locations, unresolved limitations and next action. Keep inherited
evidence distinct from fresh checks and synchronize meaningful status changes
with shared project tracking.

## Codex coordination and milestone audit workflow

Owner instruction, 2026-11-18: Codex is the coordinator. Delegate simple work to
lesser agents; implement other work in the main agent. After major milestones,
have Astra audit the code, fix the findings in the main agent, and have Astra
verify again until it passes.

- Delegate routine implementation, documentation, checks and small reviews to a
  cheaper capable available agent. Follow AGENTS.md: preferred openai/gpt-6-luna;
  if unavailable, disclose the available substitute and verify its output.
- Keep complex implementation, architecture, concurrency, durability, security,
  IPC and service lifecycle work with the coordinating main agent.
- Finish implementation and consolidated checks first; at each major milestone, request a code audit from the available Astra model,
  currently gpt-6.2-astra (the owner explicitly requested this model role). Give
  it the milestone scope, relevant code/diff, requirements and validation evidence.
- Verify the audit findings against the code. The main agent fixes confirmed
  issues itself and runs the relevant checks. Send the resulting changes back
  to Astra for verification; repeat fixes and verification until Astra passes
  the milestone. Keep the same auditor through the loop where possible.
- Record the audit scope, findings, fixes, checks, final verdict and remaining
  acceptance limitations here and in the shared tracking at the milestone.
  An audit pass does not replace required tests or phone/visual acceptance.
- Do not silently substitute another model for Astra. If unavailable, report it
  and obtain the owner's direction before calling the milestone audit complete.

Owner clarification (2026-11-18): "use astra a bit less, on major milestones. this seems too much back and forth." Do not request Astra reviews for small increments; after the milestone audit, follow up only to verify confirmed fixes until PASS.

This is the current Codex workflow. It adapts the earlier Claude-specific
Sonnet/Opus/Fable role instructions; previous reviews remain historical evidence.

## Codex operating notes

Owner requested these notes on 2026-11-18: keep what I need about how the project
works, emulators, adb and the owner's phones. The following is read from the
repository, not a fresh inventory of connected devices or installed tools.
Rediscover live device IDs, SDK versions and running processes before using them.

### Architecture and invariants

- The Windows app has one Host writer; the phone instead composes the shared
  DownloadManagerService and HostCommandDispatcher in its own process. Screens
  send the same IPC envelopes through IHostConnection, implemented by either
  InProcessHostConnection or RemoteHostConnection. Keep UI logic out of the engine.
- AndroidHost composes engine, atomic JSON stores, runtime and dispatcher.
  InboundBackground coordinates process lifetime, network policy and notifications.
  MainActivity/Navigator own the Android screen lifecycle. Mobile holds testable
  models and rules without Android types. Remote host connections use the existing
  server API/WebSocket, with pairing, certificate checks and Keystore-held secrets.
- Payload bytes belong in a destination-local part file; only metadata/checkpoints
  belong in app state. Completion renames in place. Preserve a job's stored part
  path when claiming a new final name; failed-job requeues claim names under the
  Runtime's mutation lock (recent Fable fixes).
- Shared Download/ directly is the default, not Download/Inbound. Do not assume
  arbitrary picked folders work by path: SAF grants need their own engine work.
  Reinstall can affect access to files created by the previous installation.
- Android 14/API 34+ uses a persisted user-initiated transfer job. Android 12/13
  uses a dataSync foreground service (API 30 is test-only). Read the current code
  and step-2 design section 8: early drafts are superseded. BootReceiver leaves
  API 34+ to the persisted job; on older versions it reads state and posts a resume
  notice without starting the Host. A force stop requires the user to reopen.
- Wi-Fi only means not metered. Pausing for network is not a failed transfer.
  User stop and crash-guard state must survive process death; starting a new screen
  or switching hosts must not accidentally clear them.
- App ID: app.inbound.android; title: Inbound. Trial app ID: app.inbound.trial.
  Target SDK 35; production minimum API 31; designated test minimum API 30.

### SDK, shells and adb

- Recorded SDK: %LOCALAPPDATA%\Android\Sdk; adb is platform-tools\adb.exe,
  emulator is emulator\emulator.exe. adb is not assumed on PATH.
  Historical inventory: SDK platforms 34/36/36.1, NDK 28.2, Android workload
  36.1.118. These are recorded versions, not installation requirements or a fresh check.
- Project convention: start Android exes from Git Bash, not PowerShell. Invoke
  existing .sh scripts from Bash. Scripts commonly set MSYS_NO_PATHCONV=1 so
  /sdcard and other Android paths are not rewritten into Windows paths.
- Discover with adb devices -l, identify the device/model/API, then always pin
  ANDROID_SERIAL (or explicitly use adb -s). Never choose the first device just
  because it is listed. Wireless addresses/ports and emulator numbers can change.
- Read-only Git Bash examples (replace the placeholder after discovery):

```bash
export MSYS_NO_PATHCONV=1
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
"$ADB" devices -l
export ANDROID_SERIAL='<verified device serial>'
"$ADB" shell getprop ro.product.model
"$ADB" shell getprop ro.build.version.sdk
"$ADB" shell dumpsys window | grep isKeyguardShowing
"$ADB" shell dumpsys power | grep mWakefulness
```

- Timing needs the phone awake AND unlocked; isKeyguardShowing=false and
  mWakefulness=Awake. Missing/renamed output is not proof it is unlocked. Ask the
  owner to unlock before timing. Prior locked/dozing runs made starts much slower.
- Give adb operations timeouts; am start -W has hung before. Use our Inbound /
  InboundTest log tags and a time boundary. Do not clear all device logcat or
  capture unrelated private logs/notifications into project evidence.
- adb reverse is temporary and does not survive reboot. Reboot tests need the LAN
  server address. Remove only forwards created by this run; preserve existing ones.

### Phones and emulators

| Device | Recorded role/state | Working rule |
| --- | --- | --- |
| Galaxy S24 FE | Owner's daily phone, Android 17/API 37, arm64; permanent-key APK installed and started 2026-11-18 | Rediscover wireless serial and endpoint; preserve daily data and app state. |
| Mi 8 Lite | Slow phone, LineageOS Android 11/API 30, serial 63316e92, arm64 | Only designated API-30 test builds; never lower the release minimum. |
| Galaxy A54 5G | Android 16; lent for one trial only | Ask before using it again. |
| kweb35 AVD | Android 15/API 35, x86-64; original trial emulator | Discover AVDs/running instances; suitable for the API 34+ path. |
| inbound33 AVD | Android 13/API 33; recent checks used emulator-5554, x64 NativeAOT test build | Exercises the older foreground-service path. emulator-5554 is historical, not reserved. |

No device was inspected or contacted for this documentation update. The last
handoff says emulators and the step-3 test server were stopped and S24 reverse
forwards/test files cleaned up. Do not infer they are still running or connected.

Install/remove only our apps; delete only our identified files. Phone setting
changes require the owner's permission, must be restored, and must be recorded.
Do not treat earlier consent to reboot, uninstall or change brightness for one
experiment as standing permission for the daily phone. The debug-to-release-key
transition needs a one-time reinstall: coordinate it rather than wiping silently.

### Builds and APK checks

- Android stays outside the default solution build. Use scripts/build-android.ps1:
  -Mode emulator builds Debug/Mono/android-x64;
  -Mode phone publishes Release/NativeAOT/android-arm64;
  -Rid android-x64 overrides the RID for a NativeAOT emulator build;
  -TestBuild enables adb hooks and minimum API 30, with separate bin/obj paths.
  Invoke the script from Bash through PowerShell if using the wrapper; Android
  exe launches and device scripts still follow the Bash convention.
- Production output: src/DownloadManager.Android/bin/Release/net10.0-android/
  android-arm64/publish/app.inbound.android-Signed.apk. A filename ending Signed
  does not prove the permanent release key was used: earlier builds used debug signing.
- NativeAOT sets DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1 (also in code,
  since AndroidEnvironment may not reach NativeAOT). Keep the TLS JNI callback
  export workaround and post-link defined-symbol check in the csproj.
- Stale Release obj once omitted NativeAotRuntimeProvider from generated Java,
  causing a startup crash. A clean Release rebuild fixed it. Inspect failures;
  delete only verified build output directories inside this worktree if needed.
- Before delivery/install of a NativeAOT APK, run:

```text
python -I tools/AndroidApp/check-apk.py <apk> --release
```

  Omit --release for a designated test APK. The checker is NativeAOT-specific;
  it checks provider/TLS/icon and, in release mode, absence of test hooks. It does
  not verify signer identity, installability, or every JNI callback. Verify signing
  and ABI separately and launch-check after installation. The csproj's readelf
  check proves the TLS symbol is defined; a mere string in the APK does not.
- Key material is outside the repo in the established secrets location and encrypted
  backup. Never put keys/passwords/tokens/cookies/profiles in source, logs or reports.
  Do not reproduce secret file contents here. Key generation/backup is already done.

### Test tools, evidence and cleanup

- Current app device scripts: tools/AndroidApp/run-gate.sh, run-background.sh,
  run-swipe-reboot.sh, run-wifi-only.sh. Read each script before running it and
  check its chosen APK, RID, target, settings changes, file cleanup and stop rules.
- run-gate.sh explicitly requires WIPE=1 because it uninstalls the app and deletes
  its jobs and key. Do not use it as a harmless smoke test on the daily phone.
- Controlled server: tools/AndroidTrialServer, --lan --port 5601 for a phone over
  Wi-Fi; scripts also use other ports (e.g. 8765), so inspect before binding.
  Keep server/Host state under artifacts with a test-only instance/data folder;
  never accidentally start a development Host against the installed app's real state.
- Emulator-to-PC networking has used 10.0.2.2; phone tests use LAN or adb reverse.
  Chrome on the S24 failed this test server with ERR_TOO_MANY_RETRIES; Edge worked.
  Owner explicitly accepted Edge for the battery comparison; no Chrome rerun is owed.
- Host-independent checks: tests/DownloadManager.Mobile.Tests. Shared code changes
  also need the relevant Core/Engine/Integration/Server suites and Windows checks.
  Recorded tests are not fresh passes until rerun. Use focused checks for changed
  behavior and complete the required consolidated checks at a milestone.
- Raw device output belongs under artifacts/android-app/ (git-ignored), with commit,
  APK type/signing/ABI, device/API, commands and limitations. Existing comparison
  images are in step3/; new screen acceptance should use mockup/built pairs under
  artifacts/android-app/pairs/ and an actual visual review.
- Mockup images in docs/plans/android-mockups/ are the screen spec; owner's later
  decisions override them. Native screenshot/tap checks complement model tests;
  neither emulator evidence nor a button's existence proves phone acceptance.
- Stop only the test processes started by this run; clean only its own files and
  forwards; record any phone settings changed and restoration. Do not use global
  process-name kills, broad Downloads cleanup or whole-log clearing.

### How I continue this Claude Code project

- Keep CLAUDE conventions and authoritative tracking; this Codex report adds a
  continuation journal and operational notes, not a competing roadmap.
- Follow AGENTS model delegation rules with available tools. Luna is unavailable;
  routine review has used gpt-6.1-terra. Use the Codex coordination and Astra audit workflow above for new work.
  Claude/Sonnet/Opus/Fable agents from the
  previous session cannot be assumed available or resumable here; do not claim
  their review for new changes. Keep complex lifecycle/security/durability work
  with the main agent and verify delegated results.
- Existing Fable approval covers its recorded fixes, not later launcher/APK-checker
  changes or future implementation. Preserve that distinction in reports.
- The local approval reviewer rejected the combined commit/push attempt for the
  takeover notes, citing export authorization. Nothing from that command executed.
  Files remain local; do not retry or route around it without explicit approval.
- Historical takeover-only scope: the owner initially requested documentation, not to
  start a signing build or alter a phone. Superseded by the later instruction to begin work;
  the signing milestone and owner-authorized phone reinstall below are now complete.

### 2026-11-18 — Operating notes added

Expanded this report with architecture, SDK/adb/shell usage, device roles,
API-specific background behavior, build/check commands, evidence rules and
known failure modes. Sources: Android build plan, current Background code,
app test scripts and dated PROGRESS entries. No fresh runtime/device checks.

## Later Fable review queue

Owner requested a separate queue on 2026-11-18. Keep [ANDROID-CODEX-FABLE-REVIEW.md](ANDROID-CODEX-FABLE-REVIEW.md) current with important Codex changes, decisions, Astra findings/fixes/verdicts and unverified checks. Signing is the first open entry; no new Fable approval is claimed.

## 2026-11-18 — Permanent signing milestone

- Local implementation after 49f5663c; no commit, push or public publication.
  scripts/build-android.ps1 adds explicit ReleaseSigning. New release-apk.py
  signs after a fresh GUID publish, checks the pinned public certificate,
  package/min31/target35/arm64/non-debuggable and existing packaging markers,
  then publishes atomically without clobbering. Password-file reference only;
  final design gives no release secrets to MSBuild. See docs/android-builds.md.
- Read Kaan88/secrets README with GitHub connector. Existing local key/password
  were present: no decryption, regeneration, secrets-repo write or upload.
- Astra gpt-6.2-astra audited; main fixed round 1 (pin/provenance/Windows formats/
  password environment) and round 2 (actual old signer label, atomic publication).
  Reverification verdict: PASS. Later Fable review remains OPEN in its separate queue.
- Ten regression tests passed; real SDK matrix 34.0.0/35.0.0/36.0.0/36.1.0/37.0.0
  accepted release signer/manifest and rejected the preserved debug signer.
  All three invalid ReleaseSigning mode combinations refused in preflight.
- Final end-to-end build passed even with InboundTestBuild=true deliberately in
  the parent environment: global false won; release packaging has no test hooks.
  Build log: artifacts/android-app/codex-signing/build-release-final.txt.
- APK: artifacts/android-app/signed-builds/a7a5c9246dbb46e492fe24bae5e74c05/Inbound-android-release.apk,
  20,061,237 bytes. SHA-256 cf8c3667622e7af6f0b73ea9203f5c9cfa345081dc3918ac31f96adc291b315d.
  Desktop copy: C:/Users/kaank/Desktop/Inbound-android-0.0.1-release.apk, hash matched.
- Existing NativeAOT trim/AOT and SDK-target mismatch warnings remain in logs;
  no claim of a warning-free build. No application source behavior changed.
- Initial discarded signing attempt used password env references for MSBuild.
  Eight reusable workers from that attempt were identified by PID, parent, command
  and first-build creation time and stopped after builds finished; no broad process
  kill or unrelated app stop. Final design avoids that environment inheritance.
- No phone contacted, app uninstalled or installed, device setting changed or server
  launched at that checkpoint. The later owner-authorized reinstall and startup are recorded below. At that checkpoint ANDROID.5 was next; auto-update now precedes it. Link/UI requirements
  must be checked against the mockups and later owner decisions before building.

Signing recovery reference supplied by owner: https://github.com/Kaan88/secrets, README.md; encryption key file C:/Users/kaank/Downloads/secrets/key.txt. README specifies AES-256-CBC with PBKDF2 600000 iterations for the encrypted Inbound bundle. Local signing inputs were present, so this run did not read the encryption key or decrypt backups. Store only locations and public fingerprints in notes, never secret values.

## 2026-11-18 — Owner-authorized S24 reinstall and startup

The owner authorized adb connection, installation and deletion of the old app. On Samsung S24 FE SM-S721B, API 37, uninstall of app.inbound.android and installation of the Desktop permanent-key APK succeeded; the old app's private state was reset. Installed APK SHA-256 cf8c3667622e7af6f0b73ea9203f5c9cfa345081dc3918ac31f96adc291b315d; version code 1, name 0.0.1, min API 31, target 35. Launch status ok, MainActivity top resumed, PID 29872 remained alive. Cold TotalTime 377 ms, WaitTime 380 ms; own log fullyDrawn 317 ms, zero rows. No keyguard check: timings are not a controlled benchmark.

Wireless adb at this run used serial adb-R5CY82E95PP-D2pd2N._adb-tls-connect._tcp and endpoint 192.168.1.222:44253; both are historical and must be rediscovered. Evidence: artifacts/android-app/codex-signing/s24-release-install.txt. No settings changed, downloads deleted, other apps touched, jobs added or real download tested. Full UI, TalkBack and download acceptance remain open. Astra code audit PASS; later Fable review OPEN. At that checkpoint ANDROID.5 was next; auto-update now precedes it.

## 2026-11-18 — Auto-update priority

Owner requires Android auto-update before ANDROID.5: check once daily or on each genuine fresh process launch (killed and reopened, not Activity recreation), automatically download, then prompt the user to install. Planned design starting point: trusted GitHub Android feed, signed metadata/hash and pinned APK certificate, private cache, daily OS-scheduled network job and process-start check, installation only on user action. Throttling and metering remain under evaluation; no implementation or acceptance is claimed.

## 2026-11-18 — Auto-update implementation and validation

- Owner: "implement update first then continue android.5" and "and then put the updatable apk on the desktop"; schedule: "once a day or if the app is launched completely for the first time. (from being killed and opened etc.)" Auto-download and an install prompt are implemented. Next: ANDROID.5.
- Astra code audit PASS after main-agent fixes and reverification, including release preparation; later Fable review remains OPEN. NativeAOT emulator: signed test baseline code 1 checked a signed HTTP fixture for code 2, automatically downloaded and verified metadata/RSA, size/hash, APK certificate and ABI, showed Ready; Install → Open Settings → allow → return opened the Android system update, which installed and opened code 2. Warm resume with the same PID caused no extra check; force-stop/reopen with a new PID checked again. Persisted job 4001 has a 24-hour period and two-hour flex; two forced runs completed without CTS failure.
- Full Mobile: 373 passed (19 updater, 14 HTTP). A pre-existing HEAD persistence count guard missed the two ReadOnlyState uses of already-registered types; test-only 21→23 correction passed nine focused tests. The full project test rerun remains in progress, not a pass claim. NativeAOT diagnostic linker file locking was avoided with separate intermediates and no reusable servers.
- Desktop: C:/Users/kaank/Desktop/Inbound-android-0.0.2-updatable.apk, 20,135,037 bytes, SHA-256 ec52f1ff71bf5ea6d29af8f8764ae32dea01058e87e312af52929cd1caf42276; copy hash matched. The signed local bundle artifacts/android-app/release-0.0.2 contains the APK, manifest-android-arm64.json and SHA256SUMS; it is not published. S24 remains on 0.0.1 at this checkpoint; app source defaults to 0.0.2.
- Evidence under artifacts/android-app/codex-signing/: updater-arm64-release-build.txt, updater-release-prepare-final.txt, updater-system-install-opened.txt, updater-schedule-launch-tests.txt and emulator-http-ready screenshot. Test-only local HTTP transport replaced an adb-pushed external input correctly blocked by Android permissions; this was not a production updater failure. Production feed 404 handles no published release; no public newer-offer update has been tested.


### 2026-11-18 — Updater delivery and final regression results

- Desktop: Inbound-android-0.0.2-updatable.apk. S24 FE updated with adb install -r,
  preserving app data; code 2/name 0.0.2, installed SHA-256 matches the Desktop APK:
  ec52f1ff71bf5ea6d29af8f8764ae32dea01058e87e312af52929cd1caf42276.
  App launched; own process logs show cold check and fullyDrawn (not a controlled
  timing benchmark). Evidence: artifacts/android-app/codex-signing/s24-updater-install.txt.
- Required .NET suites passed: Core 2373; Engine 210; Integration 2048 (+32 skipped);
  TorBox 323; Archives 401; Containers 188; Torrents 94 (+29 skipped); Server 208;
  Tray 3; Mobile 373. Total 6221 passed, 61 skipped. Integration took 9m57s; it
  finished naturally before a guarded stop could match its old PID. No test process
  or unrelated app was stopped by that attempted diagnostic.
- The first full run found an already-stale persistence count guard (21 vs 23):
  HEAD's ReadOnlyState already adds two uses for registered DownloadJob/AppSettings.
  Test-only count/comment corrected, nine focused checks passed, then Engine/full
  .NET checks passed. No Persistence/Core/Engine production behavior changed.
- The runner then found Chrome dependencies absent and npx selected the wrong tsc
  package. scripts/test.ps1 now installs missing locked Chrome dependencies and
  uses --no-install for typechecks. Remaining checks completed separately: extension
  339/339 + tsc; web 717/717 + svelte-check (0 errors/warnings). npm deprecation/
  install and test ExperimentalWarnings remain documented; do not call the entire
  run warning-free. Logs: updater-project-tests-final.txt, updater-frontend-tests.txt.
- Prepared bundle: artifacts/android-app/release-0.0.2. Manifest SHA-256:
  bb5510151d7a5d696cdaa0371ecad844609df9b5a3b6fcc208562762db4a40a6.
  The first preparation lacked Get-FileHash in this PowerShell environment;
  staging cleanup left no final directory. Scoped .NET hashing fixed it and retry
  passed. See updater-release-prepare-final.txt. No public release/upload yet.
- ANDROID.5 next: mockups B2/B3/B4/B6 read; official Android docs confirm generic
  web Open-with cannot be promised for arbitrary domains on API31+. Use Share and
  the in-app browser. The owner subsequently chose per-download consent before cookies go to the named selected server. Pure link parsing/selection was reviewed and copied into source/tests; native UI remains unintegrated. Browser/security implementation stays main-agent work.

### 2026-11-18 — Owner decisions after updater review

- Owner chose "Ask before sending cookies to the selected server" for ANDROID.5.
  Authenticated remote downloads must name the selected target and ask per download
  before forwarding credentials. Update B3's old phone-only words accordingly; do
  not imply remote cookies remain on the phone. Main owns this security-sensitive flow.
- Owner approved "Publish Android release and update feed" for the reviewed 0.0.2
  artifacts and android-app source. Commit/push the audited updater checkpoint and
  publish version-specific APK/metadata/checksums plus rolling Android manifest;
  keep desktop latest unchanged. This approval does not merge Android into main.

## 2026-11-18 — Published feed and ANDROID.5 checkpoint

- Owner approved publication and chose "Ask before sending cookies to the selected server": name the target and obtain consent per download before forwarding cookies. ANDROID.5 share/browser/page selection and expired-link refresh remain in progress; pure PageLinks model and nine tests were reviewed, fixed for single-URL punctuation, passed staged tests and copied into source/tests. Native UI is not integrated.
- Android source 0.0.2 at d2e06d27 and CI guard 178fdb412196f76aa7fa3257a051cfd8f73c3a0c were committed and pushed on android-app only; main unchanged. Owner-approved prereleases [android-v0.0.2](https://github.com/Kaan88/inbound/releases/tag/android-v0.0.2) (APK, manifest, SHA256SUMS) and [android-latest](https://github.com/Kaan88/inbound/releases/tag/android-latest) (feed manifest) target 178fdb4, both latest=false. Public asset hashes match APK ec52f1ff71bf5ea6d29af8f8764ae32dea01058e87e312af52929cd1caf42276 and manifest bb5510151d7a5d696cdaa0371ecad844609df9b5a3b6fcc208562762db4a40a6. Android-tag Linux/Docker jobs skipped; desktop latest v0.9.0 remained unchanged.
- NativeAOT emulator 0.0.2 checked the real HTTPS production feed and displayed "Inbound is up to date" (emulator-public-feed-before.xml). The signed HTTP fixture established the 1→2 installer flow; an upgrade from a newer public offer remains for a future release. S24 updated to 0.0.2 with data preserved (s24-updater-install.txt). Evidence under artifacts/android-app/codex-signing/. Main stopped its loopback fixture server, reset the emulator install-source permission to default and removed its pushed fixture files. Final combined Astra PASS covers updater, preparation/helper, runner and persistence guard; Fable review remains OPEN.


## 2026-11-18 — ANDROID.5 in-progress checkpoint

- Work remains in progress. The first native signed-in file download was checked with a matching hash. Mobile suite passed 408 tests (two existing xUnit analyzer warnings). An earlier set of 309 Host/IPC tests passed before the later runtime rewrite; the 13 audit-focused checks are not yet final evidence for the current state.
- Initial Astra findings prompted fixes; verification is pending. Fable-CODEX-3 remains OPEN, including shared-runtime refresh/security/durability and browser attribution. This checkpoint is not milestone acceptance.


### Signing backup verification (2026-11-18)

At the owner's request, the encrypted archive in the private secrets repository (blob 18161c733eefccc296f1d5add65f658e95233f82) was decrypted entirely in memory and SHA-256 compared against local inputs: Android release keystore, password file and update signing key all matched. No extraction, remote changes or secret values disclosed.

### Refresh gate correction (in progress)

The initial global settings-gate wrapper around Pause/Cancel delayed urgent Cancel behind QueueAction and failed the consolidated regression. Main is replacing it with per-job AddressMutationGate: refresh takes settings then the job gate before persistence; PauseAll takes ordered job gates; urgent Cancel is not globally gated. Verification remains in progress.

### ANDROID.5 native and audit checkpoint (2026-11-19)

- Debug and NativeAOT x64 caught an authenticated fixture file with SHA-256 fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83. Native authenticated refresh reused the existing `auth(3)` job (total 7, Finished 7, Failed 0), with the same hash. Native picker/insets shown in `android5-native-links-final.png`.
- Debug Archives filtering excluded PDF/image; selected a.zip and b.pdf completed after ACK. Later auth1 stayed absent until Resume, then completed. Closing the browser during six-second metadata left Main on All five without a resurrected sheet. A subframe POST was logged by WebView with no Host GET or new job. Remote cookie consent named host and origin and warned about HTTP; cancel left server jobs at zero.
- The signed arm64 version-3 initial build matched the signing fingerprint but is stale after the latest Runtime fix: do not deliver it or treat its hash as final. Focused queue policy 10 passed; the exact old failure and two new regressions passed 3. Full Integration 2059 passed/32 skipped/1 failed was before the queue fix and is not a full green result.
- New confirmed Astra fixes reserve candidate final paths through commit and schedule when `AddressRefreshPending` clears, including failed jobs. Two deterministic tests are running; audit is not yet PASS. Fable-CODEX-3 remains OPEN. Common real-account sign-in compatibility, TLS, renderer, rotation, TalkBack and broader visual checks are not established. The controlled loopback fixture is now `tools/AndroidApp/link-fixture/`.

### ANDROID.5 audited fixture checkpoint (2026-11-19)

- Source audit: Astra final PASS after the reserved-name and refresh-wakeup fixes. The auditor verified both controlled tests force the original races and read the focused 16-pass log; this was source/read-only review, not an independent device run. Fable-CODEX-3 remains OPEN.
- NativeAOT authenticated refresh completed the existing `auth(3)` job: All 7, Finished 7, Failed 0, same fixture hash. GitHub's public sign-in form rendered in the Native WebView; no account or credentials were entered, so GitHub sign-in itself is unverified. Unknown, ambiguous or redirect-provenance downloads may need a manually supplied URL; observed POST and blob are not replayed as GET. No TLS bypass or browser-profile import.
- Core 2373, Engine 210, Mobile 408 passed. Full Integration 2059 passed, 32 skipped, one failed before the queue fix; afterward the exact failure plus two new regressions passed 3, queue policy passed 10, and reservation-focused checks passed 16. There is no clean full Integration rerun after that fix. The final phone APK and install are recorded in the 0.0.3 delivery entry (`android5-arm64-final.txt`, `android5-s24-install.txt`).
- ANDROID.5 source is audited and native fixture flows have evidence, but exhaustive acceptance is open: real accounts, broader light-mode, TalkBack and rotation checks. Do not mark the milestone fully accepted yet.


### ANDROID.5 0.0.3 delivered (2026-11-19)

- Final permanent-key arm64 APK: `artifacts/android-app/signed-builds/0b2a487b31e743d68d7402399642a7aa/Inbound-android-release.apk`, 19,986,573 bytes, SHA-256 `e1dc3d59cbf5d5f5ccf3518ebc4a69002a6ec416cdcf5fa3e8b68fa09cf93bc7`; certificate matched the pinned permanent identity. Release preparation verified version 0.0.3/code 3 and made signed local bundle `artifacts/android-app/release-0.0.3`, not published. Desktop copy `C:/Users/kaank/Desktop/Inbound-android-0.0.3-updatable.apk` matched the hash.
- S24 FE SM-S721B: pre-install check actually reported 0.0.1/code 1, despite earlier 0.0.2 notes; cause not established. The 1→3 `install -r` succeeded, preserving data without uninstall or settings changes. Installed APK hash matched, package reported 0.0.3/code 3, launch status ok, PID 5891, WaitTime 3033 ms. This is not a controlled performance benchmark. Evidence: `artifacts/android-app/codex-signing/android5-s24-install.txt`. GitHub release/feed remains 0.0.2; 0.0.3 is not published.
- NativeAOT picker and caught-sheet light-mode spot checks: `android5-native-links-light.png` and `android5-native-caught-light.png`; emulator app theme restored to Follows System. These do not establish exhaustive visual, TalkBack or rotation acceptance.

### Test cleanup (2026-11-19)

Stopped only the verified task-owned Python fixture and isolated Inbound server,
removed the task's emulator UI dump files, and closed the verified inbound33 AVD.
No test Host process remained in the Android worktree. The installed Windows Host
was left alone. The emulator app theme had been restored to Follows System;
no physical-phone settings were changed. Raw evidence remains under the ignored
artifacts/android-app/codex-signing directory; reusable fixture source is tracked.

### Recorded source checkpoint (2026-11-19)

ANDROID.5 implementation commit: `d0816724208a0ae543cf2d84a02e4ee32f218de2` on `android-app`.
The commit includes the shared-runtime fixes and regression tests audited with
the milestone; no merge to main. The 0.0.3 APK and signed local bundle remain
unpublished; public Android release/feed remain 0.0.2. Later Fable review is OPEN.
Final model result: `artifacts/android-app/codex-signing/android5-mobile-final.txt`
(408 passed); final controlled race checks: `android5-reservation-wakeup-tests.txt`
(16 passed). Full-run limitations remain recorded above.
