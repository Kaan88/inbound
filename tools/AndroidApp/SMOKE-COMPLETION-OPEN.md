# Completion-notification Open smoke test

Use a dedicated Android 13 / API 33 test AVD with Inbound and Google Photos
installed. Start on **This phone → Downloads → All**, with no search or active
transfers. No physical device or app reinstall is needed. Use Python 3.10+
with Pillow. From the repository root in PowerShell:

```powershell
python tools/AndroidApp/smoke-completion-open.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 --avd inbound33 `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\inbound-open-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

The script sends one two-link batch to Inbound from a temporary loopback HTTP
server. It checks each saved PNG's SHA-256, expands the completion group, then
finds and taps each child’s **Open** by its unique title. It checks that Photos
is foregrounded and that the rendered image has the right color. The evidence
directory holds the result log, scoped notification records and per-image
screenshots. Keep the AVD idle during the run; it drives one UI at a time.

Cleanup dismisses only the fixture notifications, deletes only the generated
jobs and payloads, and restores the initial notification permission and Downloads
counts. Cleanup errors fail the test rather than reporting a clean pass.
