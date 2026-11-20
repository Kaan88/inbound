# Android core-screen parity capture (A0/A1/A4/A5/C7/C8)

This is a guided capture run for the approved references in
`docs/plans/android-mockup-acceptance.md`. It is deliberately separate from the
C3/C4 runner and uses only an explicitly supplied
`app.inbound.android.rcloneprobe` APK on the dedicated `inbound33` API 33
emulator. It never builds, clears app data, resets the AVD, or touches the
regular `app.inbound.android` package. The probe must not already be installed;
the script refuses to replace it. Do not run it while another UI test owns the
emulator.

## Prepare

Build the probe APK separately through the established Android build workflow.
This runner does not build or modify APKs. Install Pillow for the side-by-side
images, then run from the repository root in PowerShell:

```powershell
python -m pip install Pillow
python tools/AndroidApp/capture-core-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\core-screen-parity-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

The runner validates the AVD/API, APK package ID, and fresh probe install,
launches without first-run bypass, then pauses at each checkpoint. Follow the
prompts on the emulator and press Enter only when the screen is in the state
described below. It checks a visible UI-tree anchor before writing the
original-resolution capture, UI hierarchy XML, and paired PNG. If a checkpoint
fails, it saves the current hierarchy and only the named Inbound log tags.

Manual mode remains the default. `--unattended` is opt-in. It captures A0 then
uses unique, visible, enabled app resource-ID selectors verified in
`MainActivity`, `FirstRunScreen`, `AddSheet`, and the screen layouts to open the
Downloads root, Add sheet, blank New download form and Settings root. A1 is
captured only when the visible Downloads filter confirms `All 0`. A4/A5 are
blank: the runner does not read or write clipboard content, enter a URL, submit
a transfer, create jobs, or change appearance. C8 (populated light-mode) remains
**UNVERIFIED** and is never synthesized. Unexpected/missing/ambiguous selectors
or anchors fail closed rather than falling back to coordinates or text matching.

## Capture sequence

1. **A0** — first-run screen before any permissions are requested.
2. **A1** — empty Downloads root, `All 0`.
3. **A4** — Add sheet with no clipboard offer.
4. **A5** — blank New download form; no URL or transfer.
5. **C7** — Settings root in the probe's default appearance. This does not
   establish manually configured Dark/Dynamic-colour state or subsection coverage.
6. **C8** — populated Light-mode state is not captured unattended.

In unattended mode the runner inspects the current hierarchy and derives tap
coordinates from an exact selected element's bounds. No input is sent if that
unique visible selector is absent. Manual mode retains operator-guided states.

## Evidence and cleanup

Each checkpoint produces `<id>-capture.png` (device original),
`<id>-ui.xml`, `<id>-logcat.txt` (only the named Inbound tags), and
`<id>-pair.png`, paired against the unchanged approved reference.
`preflight.json`, `apk.sha256`, `flow.txt`, and failure-only `failure-ui.xml` /
`failure-logcat.txt` record provenance and diagnostics.
Inspect each pair at native size and record differences and flow outcomes in
the acceptance matrix; the generated pair is not itself a visual PASS.

On exit, the runner uninstalls only the probe package it installed. App data
inside that disposable package is therefore removed by Android uninstall. It
does not delete shared Downloads files, clear package data, stop arbitrary
processes, remove ADB reverses, or reset the emulator. If fixture downloads
were made manually, remove them only after checking their unique names and
fixture hashes.

Host-only checks (no ADB/device actions):

```powershell
python -m unittest tools/AndroidApp/test_core_screen_parity.py
python -m py_compile tools/AndroidApp/capture-core-parity.py
```
