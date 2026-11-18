# Android updates

Inbound checks for an Android update once when a user opens a fresh app process.
Warm resumes and Activity recreation do not start another launch check. A separate
persisted Android network job (ID 4001) requests a daily check; Android may delay
it under Doze or other system restrictions, and force-stop prevents background
work until the user opens the app again. The app automatically downloads a newer
verified update into its private files and offers installation. It does not install
without the user's action and Android's confirmation.

The app verifies signed update metadata, the downloaded file's size and SHA-256,
and the APK's package, version, pinned signing certificate and arm64 ABI before
making it available. Settings shows the check/download state and an Install update
button for a ready APK, including while a refresh is in progress or the network is
unavailable. A ready prompt is limited to once per version per 24 hours; choosing
Later does not remove the Settings install action. The APK is rechecked before
handoff to the Android installer.

Android may first require the user to allow installs from Inbound. After that
permission screen, the app can continue the selected version and hash for up to
10 minutes, rechecking the file before opening the installer. Android still asks
the user to approve the installation.

## Release feed and preparation

The production feed is the signed `manifest-android-arm64.json` on the GitHub
release `android-latest` in `Kaan88/inbound`. Each manifest points to its
versioned APK on `android-v<version>`. Publish the versioned APK and manifest on
that versioned release, and the same signed manifest on `android-latest`. Set
`latest=false` on both releases so they do not displace the desktop latest feed.
Android 0.0.2 and its feed have been published. An emulator checked the real HTTPS feed and reported it was current; a public newer-offer upgrade remains to be tested at a future release.

For each release, increment the Android `ApplicationVersion` (version code) and
set the matching version name in the Android project. For example, version 0.0.2
uses code 2. Build the permanent-key arm64 APK as described in
[Android builds](android-builds.md), then stage the verified release locally:

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build-android.ps1 -Mode phone -ReleaseSigning -AndroidSdkDirectory C:/Users/kaank/AppData/Local/Android/Sdk -JavaSdkDirectory C:/Users/kaank/tools/jdk/jdk-21.0.13+9

powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-android-release.ps1 -ApkPath '<verified-release.apk>' -VersionCode 2 -Version 0.0.2 -AndroidSdkDirectory C:/Users/kaank/AppData/Local/Android/Sdk -JavaSdkDirectory C:/Users/kaank/tools/jdk/jdk-21.0.13+9
```

The preparation script verifies the staged APK and requested version, signs the
manifest and writes `SHA256SUMS`; it stages locally and does not publish. The
update signing private key defaults to `Downloads/secrets/update-signing.pem`;
`-PrivateKeyPath` overrides its location. Signing keys stay outside this repository;
use the secrets repository README for recovery. Never publish test builds or x64
fixture APKs as production assets. See the build guide for APK signing and its
one-time debug-to-release reinstall requirement.

## Validation and remaining checks

A NativeAOT emulator test updated a version-code 1 baseline to code 2 using a
signed HTTP fixture and reached the actual Android installer. The first fixture,
which used an adb-pushed external file, was blocked; the test transport was fixed.
That was a fixture issue, not a production-feed result. Runtime/core and release
preparation received Astra PASS. The full Mobile suite passed 373 tests, including
19 updater and 14 HTTP tests. The initial broader project run found an existing
persistence source-count guard mismatch; the test-only correction passed all nine
focused guard tests. The broader .NET rerun passed 6221 tests (61 optional skips).
Extension 339 and web 717 tests plus both typechecks passed after locked dependencies were installed.
The release was installed on the S24 with data preserved; installed hash and launch were checked.

The fixture upgrade does not establish a public newer-offer production upgrade. Record subsequent device checks and release evidence in the project tracking documents.
