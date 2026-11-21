# Android operating notes

How the Android app is put together and how to work with the SDK, adb, the owner's
phones and the emulators. These notes come from the repository and earlier sessions,
not a fresh device inventory: rediscover device serials, SDK versions and running
processes before using them. Phone QA rules in `CLAUDE.md` still apply.

## Architecture and invariants

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
  Runtime's mutation lock (Fable fixes).
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

## SDK, shells and adb

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

## Phones and emulators

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

## Builds and APK checks

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

## Test tools, evidence and cleanup

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
