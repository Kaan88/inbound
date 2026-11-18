# Building and signing Inbound for Android

Android is built separately from DownloadManager.slnx. The app uses .NET 10
NativeAOT for release phones, native Material 3 views, minimum Android 12/API 31,
target API 35 and arm64. API 30 and adb hooks are only for designated test builds.

## Build modes

Run from the android-app worktree. Project convention is to start Android tools
from Git Bash. Replace SDK/JDK locations if the machine changes.

```bash
# Day-to-day emulator: Debug/Mono, x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build-android.ps1 -Mode emulator -AndroidSdkDirectory C:/Users/kaank/AppData/Local/Android/Sdk

# Permanent-key release: Release/NativeAOT, arm64, verified before delivery
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build-android.ps1 -Mode phone -ReleaseSigning -AndroidSdkDirectory C:/Users/kaank/AppData/Local/Android/Sdk -JavaSdkDirectory C:/Users/kaank/tools/jdk/jdk-21.0.13+9
```

- -TestBuild enables API 30 and test hooks, using separate bin/obj paths.
- -Rid android-x64 supports a NativeAOT emulator build with -Mode phone.
- -ReleaseSigning refuses emulator mode, TestBuild and a non-arm64 RID.
- Without -ReleaseSigning, a filename ending Signed.apk may still use the debug
  key. Do not hand it out as a permanently signed release.

## Permanent signing

Signing inputs default to the owner's Downloads/secrets directory; override with
-SigningDirectory if necessary. Required files are inbound-android-release.jks
and inbound-android-release.pass. The existing PKCS12 alias is inbound and its
store/key passwords match. Do not generate a replacement key for routine builds.
The secrets repo README describes encrypted recovery; never add these inputs or
the encryption key to this repo. Verification does not need signing secrets.

The wrapper publishes with explicit InboundTestBuild=false and AndroidKeyStore=false
into a fresh artifacts/android-app/signed-builds/<unique-id>/publish directory.
This build is an intermediate debug-signed package; no release password enters
MSBuild. Only afterward, release-apk.py runs SDK apksigner with a password-file
reference. It signs to a temporary file, disables the unused v4 sidecar, verifies,
then atomically publishes Inbound-android-release.apk without overwriting an
existing output. On failure there is no final APK at that output filename.
The output volume must support same-directory hard links (the current NTFS worktree does).

The verifier pins the public release certificate SHA-256:

```text
3746bf5a8975e623e8acd8dee6ad2665c3a26ffbf6f34ac6f33380f65d8f0457
```

It runs apksigner's cryptographic verification, requires one signer with that
certificate, checks package ID, API 31/35, arm64-only and non-debuggable flags,
and runs the existing check-apk.py marker/resource checks. A verified APK does
not establish on-device startup or UI acceptance; the NativeAOT packaging
checker is not a DEX/ELF semantic parser. The csproj separately checks the TLS export.

## Verification and regression checks

```bash
python -I tools/AndroidApp/test-release-apk.py
python -I tools/AndroidApp/release-apk.py verify '<release.apk>' --android-sdk C:/Users/kaank/AppData/Local/Android/Sdk --java-sdk C:/Users/kaank/tools/jdk/jdk-21.0.13+9
```

The verifier selects the newest complete stable build-tools version. Parsing
supports the old/new Windows signer and minimum-SDK formats. The regression
checks use only fixtures and temporary files; no devices or signing secrets.
Evidence belongs under artifacts/android-app/, with APK hash, build options,
code checkpoint, actual validation and remaining limitations recorded in progress.

## Installation and later updates

The old APK uses a debug key; Android cannot update it in place with this release
identity. The owner's one-time reinstall removes app data. Coordinate that step
and do not silently uninstall the daily phone's app. Once installed under the
permanent key, keep using that key and increase the app version code for updates.
Automatic APK download/install offers and public release publication are later work.

The current Codex milestone audit and later Fable queue are separate:
ANDROID-CODEX-PROGRESS.md and ANDROID-CODEX-FABLE-REVIEW.md. Do not claim a
Fable approval from an Astra pass.

References: [Android apksigner](https://developer.android.com/tools/apksigner),
[.NET Android build properties](https://learn.microsoft.com/dotnet/android/building-apps/build-properties).
