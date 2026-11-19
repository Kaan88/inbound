# Android progress — Codex

Android 0.0.4 published (2026-11-19, Codex): owner requested immediate APK publication ("just put the apk already"). Source commit b6c2f6668378c0fc0b16c8be5fa5f92397dd2ba7 is pushed to origin/android-app; no merge to main. Public prerelease https://github.com/Kaan88/inbound/releases/tag/android-v0.0.4 contains the permanent-key NativeAOT arm64 APK, signed manifest and SHA256SUMS. Both releases use latest=false; desktop latest remains v0.9.0. The android-latest feed now serves the identical 0.0.4 manifest. Public APK bytes and both manifest copies were downloaded and matched local checksums: APK cd49d3e2b0b3f729752642934a29f2a5ae4b577e3e50486a7b5107d0fe6bff65 (20,599,175 bytes), manifest 3343eabef3a601c116325203c366c210eb5eea64a104ad184f1bbbd248b5f979. Release/signature/API31/target35/ABI/no-test-hook checks passed. Final solution: 0 errors, 23 warnings; Mobile505, Core2373 and focused final Integration10 passed; remaining broad suites still running at publication. Astra source audit PASS. Final NativeAOT emulator install/launch preserves data; S24 is not attached. ANDROID.6 physical-device/accessibility and final broader acceptance remain OPEN; publication does not complete the milestone or later independent review queue. Evidence: artifacts/android-app/android6/final-production-clean-build.txt, final-release-prepare.txt, final-solution-build.txt, final-regressions.txt, audit-mobile-final.txt and public-verification/.


Historical pre-publication checkpoint (2026-11-19, superseded by the publication above): owner asked "could you make a release to github," authorizing Android 0.0.4 publication after verification. C1/C2/C7 follow the approved 28-flow mockup board. Initial major Astra audit returned CHANGES REQUIRED; eight verified findings have source fixes, with re-audit pending. Main Sol personally inspected delegated source and added schedule-parent identity checks. Mobile 489 and focused identity/scope/golden 8 passed; full Integration rerun and final Android builds/live checks are in progress. ANDROID.6 remains partial and the later independent review queue remains OPEN. No release has been published yet.


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

## Review queue ownership clarification (2026-11-19)

Implementer/coordinator: Codex. The owner clarified that the existing
ANDROID-CODEX-FABLE-REVIEW.md should continue as the record for Astra to review
Codex's implementations later. Its filename and historical entry IDs stay intact;
new entries must identify Codex, implementation commits, important decisions,
actual checks and remaining gaps. Earlier pending Fable assignments are superseded
by this direction. Historical milestone PASS results remain attributed as recorded;
the later independent Astra review stays OPEN. No new audit ran for this docs change.

## Continuation handoff (2026-11-19)

Codex wrote ANDROID-CODEX-HANDOFF.md and linked it from CLAUDE.md. It records the
source/local-doc checkpoint, next ANDROID.6 scope, delegation/review rules, delivery
state, test limitations and device/security boundaries. Documentation only; no
new application tests, publication or device actions.

## Owner direction for ANDROID.6 (2026-11-19)

The owner chose per-queue Wi-Fi/unmetered-network, charging and battery-saver-off
conditions, plus per-queue aggregate speed limits, for the C1/C2 queue and schedule
work. ANDROID.6 is IN PROGRESS and local/uncommitted. This records scope only: no
implementation, build, test, device check or acceptance result is claimed here.
The later independent Astra review remains OPEN.

## ANDROID.6 selected-Host queues, scheduler and settings (2026-11-19)

Status: IN PROGRESS, local/uncommitted. Implementer/coordinator: Codex, with main
Sol owning architecture, lifecycle, Runtime, IPC and final integration. Later
independent Astra review remains OPEN; no ANDROID.6 audit or completion is claimed.

The current implementation covers the selected-Host queue dashboard/editor and
schedules; nullable shared per-queue unmetered-network, charging and
battery-saver-off conditions; per-queue aggregate HTTP payload caps; Android phone
power monitoring; persisted ordinary Android queue jobs with a revocable Runtime
execution lease; compare-and-swap settings updates; and working settings subsets.
Queue selection/admission and Details assignment are still being integrated.
Remote torrent/rclone transfers are outside these policies. The owner asked which
background behavior would be best; Sol recommended and adopted Android-managed
automatic best-effort jobs that may be delayed by Doze/battery management, without
exact-alarm permission. This is a recommendation adopted in implementation, not an
explicit owner choice of exact scheduling behavior.

Sol read and reviewed all worker code, tests and documentation before integration,
and personally fixed the null-clear merge, stale `LastResult`, raw-action guard,
Android bindings, stable card updates, encoded text and UI labels/touch/keyboard/
discovery. Sol owns lifecycle, Runtime and IPC integration.

Validation actually completed before the latest admission/UI/membership integration:
Core 2373, Engine 213 and Mobile 457 passed; focused Runtime queue-condition and
lease checks passed 8; earlier queue-cap checks passed 3. Debug Android on the
dedicated inbound33 emulator exercised a daily 09:00 schedule, start/stop, rotation
and restored editor draft; C7 dark captures are under `artifacts/android-app/android6/`.
The daily S24 FE data was untouched. At this checkpoint Full Integration was still
running and expected exact Hello capability/golden compatibility failures were
being corrected; there is no full-suite green claim. NativeAOT, physical-phone,
selected-remote-host, Doze/battery, TalkBack, theme and visual-pair acceptance remain
pending. ANDROID.6 remains unchecked in ROADMAP.

### ANDROID.6 mockup alignment update (2026-11-19)

The owner asked whether the implementation follows the agreed mockup images. Sol acknowledged that the current C1/C2/C7 draft has deviations: extra Edit buttons, a schedule selector and expandable settings. Sol committed to restoring the pictured layouts and providing side-by-side screen pairs before calling the screens ready. C1/C2 visual correction is assigned to Terra at high reasoning with explicit source ownership; Sol owns C7 corrections and reviews the worker's code. For flows absent from the mockups, the owner directed the team to prepare additional mockups alongside the existing pictures in an HTML review board; main is preparing it. At that earlier checkpoint, implementation awaited the owner mockup review; the later approval and resumed work are recorded below. Known C1/C2 layout corrections may proceed, and no undefined dialogs will be added before the mockups are reviewed.

The focused capability/golden compatibility checks passed 14, and the ServerHello check passed 1; latest Mobile 464 and Engine queue 4 checks passed. The full Integration rerun is still running, so no full-suite green result is claimed. There is no visual acceptance or ANDROID.6 completion claim; final side-by-side pairs and the later independent Astra review remain pending/open.

### ANDROID.6 review-board and validation update (2026-11-19)

Update to the earlier in-progress test note: Full Integration now passed 2,077, skipped 32 and failed 0 (`artifacts/android-app/android6/integration-final.txt`). After the C1 corrections and C2 safe-header change, the Android Debug build passed with 0 errors and 2 known SDK warnings (`artifacts/android-app/android6/android-layout-check.txt`). Main Sol completed browser QA on the [ANDROID.6 flow review board](docs/plans/android-mockups/android6-flow-review.html), authored by Codex; it contains 28 proposed flows with the C1/C2/C7 originals embedded unchanged. Sol reviewed the worker source and removed an unwired C2 overflow control and a static wrong title.

At this earlier checkpoint, the design-first pause remained in effect; the later owner approval and resumption of implementation are recorded below. Browser QA completed for all 28 flows at 320, 390, 736, 1024 and 1440 px: no horizontal overflow, clipped dialog/content or script errors; reference zoom opens and closes. Sol inspected all rendered proposals and Terra independently reviewed the contact sheets. Embedded references were verified byte-identical to C1/C2/C7. This is artifact verification, not device/accessibility acceptance. These results do not establish visual acceptance or completion; ANDROID.6 remains IN PROGRESS and later Astra review remains OPEN. No release claim is made.

### ANDROID.6 board approval and resumed implementation (2026-11-19)

Owner reviewed the 28-flow board and said "looks good," approving it as design direction and resuming implementation. This is not feature acceptance. Terra owns C2 editor/dialogs and C7 grouped root/Details. Main Sol owns queue actions, shared completion behavior and integration, and personally checks worker code. Keep board-marked future capabilities pending. Queue completion note: the existing queue `NotifyWhenFinished` preference controls a transient summary only; Android releases its background execution lease once idle for either choice. The queue remains ready, and neither choice exits the app/Host or shuts down the device. Full Integration 2077 passed / 32 skipped / 0 failed; latest Debug build 0 errors / 2 known SDK warnings. Prior detailed acceptance gaps and historical checkpoints remain; ANDROID.6 stays IN PROGRESS, no visual acceptance or Astra audit is claimed.
### ANDROID.6 approved-flow and retry-race checkpoint (2026-11-19)

Main Sol personally reviewed every worker change. The current reviewed flow set covers C1 queue creation/rename/delete/move; C2 sheets, schedule times, limits and finish choice; and supported C7 grouped pages. Shared protocol work adds `NotifyWhenFinished`, `RemoveSchedules` and `ExpectedQueueId`. These are implementation checkpoints, not feature or milestone acceptance.

Focused approved-flow checks passed 6 (`approved-flow-tests-final.txt`). The deterministic `StopThenStartWhileFailedRunIsSavingPreservesTheOwedRetry` cases for limits 1 and 2 failed against the old behavior, which changed expected `failed` to actual `paused` (`retry-race-before-fix.txt`). Main fixed queue stop to preserve failed/cancelled terminal states; 11 related focused checks passed (`retry-race-fixed.txt`). First consolidated suites passed Core 2,373, Engine 214 and Mobile 464 (`approved-core-full.txt`, `approved-engine-full.txt`, `approved-mobile-full.txt`). The first full Integration run had 2,081 passed, 32 skipped and one exact retry test failure: `StartingTheQueueRetriesAQueuedFailureOnlyWhileItsRetriesLast` (`approved-integration-full.txt`). A fixed full rerun is still running, session 60371; Server consolidation has not started.

The approved UI/icons Debug build completed with 0 errors and 2 known Android SDK warnings (`approved-ui-final-icons-build.txt`). The latest rebuild for the Done button is still running, session 25681. On API 33, emulator QA confirmed blank-name validation, creation of stopped `Night-QA`, saving a daily 09:00 schedule, and persistence of the native 09:00 time-picker plus custom 500 speed-limit draft through both rotations; free rotation restored. Evidence: `approved-create-invalid.xml`, `approved-queues.png`, `approved-time-restored.xml`, `approved-speed-restored.xml`. The S24 FE was untouched.

ANDROID.6 remains IN PROGRESS and unchecked. No clean fixed full Integration result, Server consolidation, NativeAOT/phone acceptance, Astra review or feature-completion claim yet.
### ANDROID.6 consolidated verification and pre-audit candidate (2026-11-19)

The subsequent full Integration run is green: 2,085 passed, 32 skipped, 0 failed in 10m18s (`approved-integration-final-green.txt`). Core 2,373, Engine 214, Mobile 464 and Server 208 passed. Full Release solution build completed with 0 errors and 29 warnings (`approved-solution-build.txt`). A second run had one checkpoint helper failure before its corruption test; controlled `CheckpointSettlementTests` reproduced the old helper returning the initial zero checkpoint while a nonzero save was blocked. Main replaced the two equal reads with actual Runner-idle internal test synchronization and retained checkpoint/payload-prefix assertions; focused checkpoint/retry tests passed 7 (`checkpoint-wait-before-fix.txt`, `checkpoint-retry-final.txt`).

A signed NativeAOT arm64 production candidate was built with the existing permanent certificate and checked: min API 31, target 35, arm64-only, non-debuggable, all `check-apk --release` checks pass. Candidate: `artifacts/android-app/signed-builds/3e8a0b3ca7384b5d956da8e2d8e32d36/Inbound-android-release.apk`, 20,578,695 bytes, SHA-256 `d85721f7a1f7f82dc4c7d753fc58828a527184f16c4092ee431963d93b99d00d`. This is a pre-audit candidate, not a publication; a one-copy Android timing-note fix landed after the build, so a final rebuild is expected.

Separate NativeAOT Release-test x64 build passed its checker and was installed with `install -r` on the owned inbound33 API 33 emulator; launch was OK at 1,401 ms, not a performance benchmark. Paired isolated remote QA created and renamed `Remote-QA` to `Remote-QA-checked`, with state persisted by the test server; phone-only power and summary rows were hidden on the remote Host. Switching back showed local `Night-QA` and `Android6-QA` intact. Evidence is under `android6/` (`android-approved-native-test-build.txt`, `native-remote-*.xml`, `approved-remote-*.xml`). S24 FE was untouched.

Main's NativeAOT/background-schedule QA continues. Astra audit `android6_astra_audit` is running with requested model gpt-6.2-astra at high reasoning; main will inspect and verify any findings. ANDROID.6 remains IN PROGRESS and unchecked; no Astra PASS, public release, physical-phone acceptance or feature-completion claim. Public Android feed remains 0.0.2.
## ANDROID.6 update prompt and changelog direction (2026-11-19)

The owner asked to “finish milestones as you go” and “implement a bit nicer update prompt with changelog.” The [Android update-prompt review board](docs/plans/android-mockups/android-update-prompt-review.html) is built around the approved C7 reference. Main reviewed the compact update-ready dialog in dark/short and light/long variants; 72 render combinations had no overflow or script failure. The owner said, “Use the proposed dialog,” approving the design direction and putting the same changelog notes in Settings. This is not feature acceptance.

Android 0.0.4 at commit `b6c2f6668378c0fc0b16c8be5fa5f92397dd2ba7` is published and immutable. The nicer prompt is separate work in progress for a future release. The Android update protocol is being extended with separately signed notes, preserving the old v1 manifest signature and fallback behavior for older feeds; plain note text is bounded to 8,192 characters. The Mobile suite passed 526 (`artifacts/android-app/android6/update-notes-mobile.txt`); signer tools built with 0 warnings and 0 errors (`artifacts/android-app/android6/update-notes-signer-build.txt`).

ANDROID.6 checks: Core 2,373, Engine 214 and Server 208 passed. Full Integration remains in progress. ANDROID.6 stays IN PROGRESS; no milestone completion, UI acceptance or newer release is claimed. The physical S24 is not attached; emulator5554 is the only connected device.
### ANDROID.6 update prompt and JobScheduler stop checkpoint (2026-11-19)

Implementer: Codex. Owner approved the proposed update dialog; implementation for
Android 0.0.5/code 5 remains local, uncommitted and unpublished. The signed-notes
Mobile suite passed 528 (artifacts/android-app/android6/update-notes-mobile-final.txt);
signer build completed with 0 warnings and 0 errors. The full Integration run passed
2,095, skipped 32 and failed 0 (artifacts/android-app/android6/final-integration-full.txt);
the accompanying Core 2,373, Engine 214 and Server 208 results predate this prompt
increment. Main reviewed all delegated source.

A dedicated API 33 NativeAOT stop observation passed for the tested build:
stop-identity-after.json recorded stop reason 3, detached=true, persisted state
queued, queue 2 still running, IsQueued=true, PausedByUser=false, checkpoint
897,859 / 1,048,576 bytes unchanged across two seconds, and an empty crash-marker
session. This is one controlled stop observation, not proof of durability across
reboot, Doze, network changes or physical-phone use. That test build included
JobRunIdentity and the owner-requested crash-guard changes, but not the newest
per-execution callback ownership, API 30 stop-reason and picker fixes.

A new Astra source pass raised three findings in callback ownership across executions,
the API 30 stop-reason guard, and Enter/D-pad confirmation in the open queue picker.
Main and Luna have made source fixes; Astra follow-up is pending. The picker now
handles Enter/D-pad only while its dropdown is closed, allowing native confirmation
when open. The latest Debug compile is still running after main corrected binding
errors. A rebuild containing all fresh fixes, final NativeAOT/arm64 candidate and
device acceptance remain pending. Android 0.0.4 is immutable; 0.0.5 is not released.
ANDROID.6 remains IN PROGRESS and unchecked; the later independent review remains OPEN.
### ANDROID.6 stop-resume and Astra follow-up update (2026-11-19)

Update to the preceding checkpoint: Astra follow-up recorded SOURCE PASS for all
three new findings (per-execution callback ownership, API 30 stop-reason handling,
and keyboard confirmation in the open queue picker). This is a source review, not
milestone completion. The picker Debug build passed with 0 errors / 5 warnings
(picker-debug-build.txt). A final Debug rerun remains in progress and includes
explicit e.Handled=false plus the documentation-comment fix
(picker-debug-final-build.txt).

After the stop observation, the same job resumed only after main explicitly forced
ordinary job 4100. It completed 1 MiB; payload SHA-256
fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83 matched the
fixture (stop-resume-payload-sha.txt). This does not claim automatic restart or
broader lifecycle acceptance. Android 0.0.5 remains unpublished; ANDROID.6 remains
IN PROGRESS and unchecked, with final release rebuild and device acceptance pending.
### ANDROID.6 modal picker Debug checkpoint (2026-11-19)

Implementer: Codex. Astra follow-up recorded SOURCE PASS for the final modal-picker
source after main read the full worker change and added event unsubscription before
disposal. On API 33 Debug, keyboard Tab/Enter/Down/Enter selected `Night-QA`; the
selection survived delayed metadata (6 seconds), landscape rotation and portrait
restoration. Submission persisted `slow (6).zip` as queue 2, `IsQueued=true`, start
mode `queue` (picker-modal-keyboard-result.xml, picker-modal-metadata.xml,
picker-modal-landscape.xml, picker-modal-restored.xml, picker-modal-admission.jsonl).
Explicit Pause persisted `PausedByUser=true` at 12,806 bytes.

The latest picker Debug build passed with 0 errors / 2 warnings
(artifacts/android-app/android6/picker-modal-final-build.txt); full solution build
passed with 0 errors / 53 warnings
(artifacts/android-app/android6/update-prompt-solution-final.txt). Mobile 528 and
release-tool 10 are prior final results. A clean NativeAOT x64 rebuild remains in
progress (artifacts/android-app/android6/updater-picker-native-clean-build.txt),
so no NativeAOT PASS is claimed. An earlier incremental NativeAOT artifact failed
the provider checker and was quarantined, not installed or distributed. Android
0.0.5 remains unpublished; ANDROID.6 remains IN PROGRESS and unchecked, and the
later independent review remains OPEN.

### ANDROID.6 Android 0.0.5 publication and NativeAOT checkpoint (2026-11-19)

Implementer: Codex. Source commit `87555b993ae8f3de7e61a171c87a932a8d6a4a2b`
was pushed to `android-app` and published as the public prerelease
[android-v0.0.5](https://github.com/Kaan88/inbound/releases/tag/android-v0.0.5).
The permanent-key NativeAOT arm64 APK is 20,664,711 bytes with SHA-256
`1cf087a69c56744e1ee7b0bd5e3c29bce1fe7cad5db371f78ada35fac8805691`; manifest SHA-256
is `a3b4500e68aeb7a59251a54121cc18d38a98d3cb2c65ae224d158a4d8ec3459c`. Public APK,
manifest and SHA256SUMS matched local bytes. Android-latest now serves the same
407-character signed-notes manifest. Both Android releases use `latest=false`;
desktop latest remains 0.9.0 and no merge to main occurred. Certificate pin is
unchanged. Release verification covered min API 31/target 35, arm64, non-debug/no
hooks, provider, TLS and icon. Evidence: `artifacts/android-app/android6/public-verification-0.0.5/`,
`update-prompt-production-build.txt` and `update-prompt-release-prepare.txt`.
Astra source audit passed the confirmed fixes; Mobile 528, release-tool 10, and the
solution build (0 errors / 53 warnings) are recorded. Main reviewed source/artifacts.

Final NativeAOT API 33 stop evidence (`final-0.0.5-native-stop.json`) recorded a
detached job with partial checkpoint 20,520 / 1,048,576 bytes retained in queued
queue 2, queue still running, not user-paused, and crash-marker session empty across
two seconds. One injected native crash recovered after an explicit forced job run;
the transfer resumed and completed with fixture SHA-256
`fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83`. Final marker
state was count 0/session false and ongoing notification absent
(`final-native-crash-recovery.json`, `final-native-recovery-payload-sha.txt`).
Native keyboard selection/restoration was verified in portrait after a landscape
cycle; landscape selection itself was not directly asserted. Short and no-notes
update previews were viewed; 0.0.6 was illustrative only and not installed.

Physical phone was unavailable. This does not establish handset/TalkBack, natural
scheduled wake, reboot, Doze, network-change or broader remote-host acceptance.
ANDROID.6 remains IN PROGRESS and unchecked; ANDROID.7 and the later independent
review remain OPEN. Publication does not complete these milestones.

Cleanup verified: task queues stopped; emulator theme Follows the system, battery
reset, font_scale absent, free rotation and uid2000/non-root adbd restored. Owned
fixture57556 and emulator5554 stopped; S24 remained disconnected and untouched.
Main viewed the final NativeAOT dark-dialog preview as well.

### ANDROID.6 remote-host rate consistency checkpoint (2026-11-19)

Implementer: Codex. At the owner's direction QA used the normal inbound33 NVIDIA
4070 Ti Super host. An isolated copy of server binaries used the loopback fixture
127.0.0.1:47616. Remote UI created `Remote-005` (queue ID 2, generation
`80187f31-53db-4165-a33d-c0f7678e9734`) and `Evening-005` (schedule ID
`ac4c2b12-f061-457d-849b-78911d0f15fb`, daily 23:58/all days), concurrency 2.
Queue/schedule IDs and limit/settings survived server restart and UI reconnect; local
settings SHA-256 remained identical:
`a0b603b23f2fc210ce9e136c76782c972397b12c89dbdcfbc948480c7d194a94`.

The run found that old queue speed input 64 stored 65,536 B/s but rendered as
65.5 KB/s on the card. Main chose consistent decimal KB/s input semantics for queue
and Host speed limits, using the shared formatter; inputs 64 and 128 now store
64,000 and 128,000 B/s. Existing saved byte limits are not migrated. Terra's scoped
source PASS preceded main's final exact-long `Fmt.Rate(long)` overload, which avoids
lossy conversion; main reviewed the overload and complete change. No Astra audit of
this rate fix is claimed.

Mobile 528 Debug checks passed; Debug build passed 0 errors / 36 warnings and Release
solution build passed 0 errors / 87 warnings. Clean NativeAOT x64 packaging checks passed (21,647,750 bytes); installed 0.0.6
verified queue 32 KB/s = 32,000 B/s and global 256 KB/s = 256,000 B/s. Signed
arm64 packaging checks passed (20,664,711 bytes, SHA-256
`30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`). Android 0.0.5 remains public; candidate 0.0.6
is local/uncommitted/unpublished. Evidence is under
`artifacts/android-app/android6-acceptance-0.0.5/` (`legacy-limit-preserved.json`,
`host-isolation-and-rate-result.json`, `server-restart-persistence.json`,
`rate-fix-queue-saved.xml`, `remote-global-saved.xml`, `local-after-remote-edits.xml`,
`rate-fix-mobile-final.txt`, and build logs).

Broader remote workflows, physical-phone/TalkBack, Doze, network and reboot gates,
ANDROID.6/ANDROID.7 acceptance and the later independent review remain open. This
is not milestone completion or a full Astra PASS.

Main verified the final source diff and Terra scoped follow-up PASS. Remote schedule
editing saved Mon/Wed 23:58 to 23:59; the empty task queue and its schedule were
then removed through the UI, and the remote global limit restored to None. Final
local settings hash still matches the baseline (`native6-cleanup-result.json`).
Hardware emulator remains visible with host GPU rendering, as requested.

### Android 0.0.6 publication follow-up (2026-11-19, Codex)

Android 0.0.6 published (2026-11-19, Codex): https://github.com/Kaan88/inbound/releases/tag/android-v0.0.6 from source `edd073b09472bab45c9c18adb4ae4161ad08be35`. Permanent-key NativeAOT arm64 APK:20,664,711 bytes, SHA-256 `30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`; manifest SHA-256 `fdc5c2666cd8037a6255529bb5c7603f16eb8f5ba13eecd619f27d4acc005266`. Downloaded public APK/manifest/checksums match staged bytes; android-latest matches the same versionCode6/versionName0.0.6 signed manifest. Desktop latest remains v0.9.0. Main reviewed worker output and final source; NativeAOT rate checks, Mobile528 and solution build passed as scoped in the candidate checkpoint below. Queue/global KB/s correction is shipped; no new Astra milestone audit claimed. ANDROID.6/.7 phone/TalkBack, broader remote and Doze/network/reboot acceptance and later independent review remain OPEN. Main checkout untouched; no merge to main.

Live QA state: inbound33 emulator5554 remains visible in hardware mode (NVIDIA
GeForce RTX4070TiSUPER confirmed by SurfaceFlinger), normal uid2000/non-root adb;
This phone selected. Main viewed `hardware-native6-final.png`. Emulator launcher
PID40268/session2498 and copied QA server PID166924/session23630 were left running;
rediscover before use. Remote task queue/schedule removed, global cap None; final
local settings hash unchanged. S24 remained disconnected and untouched.
