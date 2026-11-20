# Paired B2 folder-picker smoke

This is a separate, opt-in functional smoke for the caught-download **Change**
folder picker on the disposable paired Host. It does not modify or invoke the
existing capture runners and does not build an APK or start an emulator.

Run only on the dedicated `inbound33` API 33 emulator, with an explicitly
provided isolated `app.inbound.android.rcloneprobe` APK and an explicitly
provided built `DownloadManager.Server.dll`. The runner refuses another AVD,
API, or APK package. Do not use a production app/Host, public endpoint, account,
or real credentials. Do not run concurrently with another test owning inbound33.

```powershell
python tools/AndroidApp/smoke-paired-b2-folder-picker.py `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --server-dll "C:\path\to\DownloadManager.Server.dll" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\paired-b2-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

The runner starts the existing guarded loopback paired-Host fixture and the
existing synthetic link fixture. It pairs using only the fixture's generated
password, creates exactly two temporary children under that fixture's download
root, then opens the app's browser at the loopback fixture URL and taps
**Archive**. Once the caught-download form is visible, it taps **Change**, selects the visibly rendered
`B2-selected-child`, checks that the caught form displays the selected
destination, submits once, and requires the fixture payload's SHA-256 at
`B2-selected-child/a.zip` on the Host while the decoy stays empty.

If the caught form is unavailable, stop and report that reason; no fallback
submission is attempted. The runner intentionally does not save UI hierarchy or
logcat around pairing fields. Do not capture screenshots or share terminal
input containing pairing fields. Evidence text contains only destination,
payload hash, and result. Cleanup uninstalls only the isolated probe, removes
the runner's ADB reverses, and deletes the exact Host payload only after its
hash matches. The disposable Host's temporary state/root is removed by its
fixture context.

Host-only checks (no build or device action):

```powershell
python -m unittest tools/AndroidApp/test_paired_b2_folder_picker.py
python -m py_compile tools/AndroidApp/smoke-paired-b2-folder-picker.py
```
