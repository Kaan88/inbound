# Remote Files UI smoke test

Use only the dedicated `inbound33` API 33 emulator. The script refuses a
different AVD/API level, an already-installed probe package, an occupied
fixture reverse port, or a pre-existing file at its output path. It installs
only the explicitly supplied `app.inbound.android.rcloneprobe` APK; it does
not install or modify the regular Inbound app.

From the repository root in PowerShell:

```powershell
python tools/AndroidApp/smoke-remote-files-ui.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\remote-files-ui-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

It drives the Remote Files tab through enable, add a disposable HTTP WebDAV
location, browse, and download using the app UI. The existing
`remote-files-fixture.py` serves the `abc` payload; the script verifies its
SHA-256 and that no other file appeared in `/sdcard/Download`. It then holds
the next fixture GET, creates a competing file at the destination, and checks
that the transfer reports one skipped file and the competing hash is unchanged.
Finally, the isolated probe pauses after opening the Android destination but
before writing. The script force-stops only that package, reopens it and checks
that the transfer reports one failed file, no skips and a warning naming the
incomplete file. It verifies that recovery did not change the file.
Keep the emulator idle and do not run another UI test on it concurrently.

Cleanup uninstalls only the probe package, removes only the script's ADB
reverse, and deletes the fixture output only if it matches one of the test hashes.
Cleanup failures fail the run. The evidence folder records the explicit APK
hash, initial file list, result, and downloaded-file hash, plus UI and log
captures on failure when ADB is still available.
