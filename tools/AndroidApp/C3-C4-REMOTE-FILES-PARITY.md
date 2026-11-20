# Android Remote Files C3/C4 parity capture

This is a separate, explicitly invoked UI run. It uses only the dedicated
`inbound33` API 33 emulator and an explicitly supplied
`app.inbound.android.rcloneprobe` APK. It refuses a non-emulator, a different
AVD/API level, an already-installed probe, and pre-existing output files. It
does not install or modify the regular Inbound package or build an APK.

Run from the repository root in PowerShell after building the isolated probe
APK separately and ensuring Pillow is installed:

```powershell
python -m pip install Pillow
python tools/AndroidApp/capture-remote-files-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\remote-files-parity-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

The script reuses the validation, UI-tree, tap, text-entry, current
content-description selectors and isolated-probe cleanup helpers from
`smoke-remote-files-ui.py`. That existing smoke keeps its publication-conflict
and process-kill coverage, with its navigation and file-selection selectors
updated for the current UI. The parity run adds three loopback WebDAV
locations to populate the C3 saved-card list, captures it, selects
`session-0412.flac` and `session-0419.flac` in the nested
`media/recordings/2026` folder, and captures the breadcrumb/selection state
before downloading.
The C4 comparison uses the fixture's modification dates for actual file
ordering and its nonrecursive folder listings for child counts. On a slow
or limited Host, counts can be unavailable; the screen shows `Folder`
rather than substituting a guessed count.
It then checks that the fixture received exactly those two file GETs and that
both files saved in shared Downloads match their independent SHA-256 values.
It also checks that opening the This phone destination picker and tapping
the already-active Site tab behave as expected, then cancels one remote
delete and confirms another for the one disposable `notes.pdf` fixture file.
By default the destination check uses the Downloads root. Add
`--nested-destination` to have the runner create an empty `Downloads/Inbound/Music`,
choose it in the UI, verify the footer and both downloaded hashes there,
and remove only the directories it created. The run refuses an existing
`Downloads/Inbound` rather than reusing or cleaning it.

With Paramiko installed, add `--verify-sftp` to verify first-use SFTP. The
runner compares the shown fingerprint to the generated fixture key, verifies
that cancelling sends no login, then trusts the key and checks the
authenticated listing. Use `--sftp-only` to retry just that walkthrough
without re-running the WebDAV checks. This does not cover a changed key or a
paired Host.

## S3-only UI walkthrough

The S3 branch is independent of the WebDAV C3/C4 run. It launches the installed
Windows rclone executable passed through `--rclone`, using the same
`rclone serve s3 <served-directory> --addr 127.0.0.1:<port> --auth-key
<access>,<secret> --cache-dir <temporary-cache> --config "" --dir-cache-time
0s` shape as `SiteKindsEndToEndTests.AnS3SiteWorksWithoutEmptyFolders`. The
served tree contains only the disposable `pub/readme.txt` fixture. Synthetic
test keys are used; never supply real S3 credentials. The listener binds only
to loopback and is exposed to the dedicated emulator through a port-specific,
owned ADB reverse.

Run with the same probe APK, emulator, and fresh evidence-directory rules:

```powershell
python tools/AndroidApp/capture-remote-files-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --rclone "C:\path\to\installed\rclone.exe" `
  --s3-only `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\remote-files-s3-parity-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

The UI flow selects location type **S3**, provider **Other S3-compatible
service**, enters the loopback endpoint and region `eu-central`, accepts the
standard HTTP warning for this disposable fixture, and saves **Backups**.
It asserts the saved **Backups / S3 · eu-central** card and browses bucket
`pub` to `readme.txt`. Captures and UI hierarchy XML are written to the evidence
directory. The runner stops only its rclone child, deletes only its temporary
fixture/cache, removes only its own ADB reverse, and uninstalls only the probe
package. This is an operator-run UI procedure; host-only tests do not establish
device or visual acceptance.

Evidence includes the original-size emulator captures (`c3-capture.png`,
`c4-capture.png`), UI hierarchy XML, fixture and device hash manifests, and
Pillow-generated `c3-pair.png` / `c4-pair.png` side-by-side comparisons with
the unchanged originals at `docs/plans/android-mockups/c3-sites.png` and
`c4-site-browser.png`. The test removes only the two output files if each still
matches its fixture hash, uninstalls only the probe package, and removes only
its own ADB reverse. Cleanup failures fail the run. Keep the emulator idle and
do not run another UI test on it concurrently.

Host-only preflight tests, with no emulator actions:

```powershell
python -m unittest tools/AndroidApp/test_remote_files_parity.py
python -m unittest tools/AndroidApp/test_remote_files_fixture.py
```

The existing publication-conflict and process-kill regression remains in
`smoke-remote-files-ui.py`; this parity tool does not replace or alter that run.
# Conditional upload Host probe

Use `capture-remote-files-parity.py` with the usual explicit probe APK,
ADB serial, and new evidence directory, plus `--upload-host-only`.
This isolated API 33 check uses a disposable WebDAV server. It checks
the uploaded bytes, refusal to replace an existing fixture file, and
cleanup of remote staging. It does not exercise the C4 upload controls.

Use `--s3-upload-host-only` instead for the S3 commit-race probe. Its
loopback fixture pauses the final `PutObject`, inserts the competing
object independently, and verifies the winner's bytes and both signed
conditional requests. It does not use a real account, test arbitrary
S3-compatible endpoints, or exercise the C4 upload controls.

Use `--s3-upload-ui` for the separate C4 interaction check. It saves the
loopback fixture as a location, publishes a synthetic MediaStore source,
and verifies that **Cancel** sends no PUT and **Upload** sends exactly one
conditional PUT containing the source bytes. It captures the selection,
confirmation, and completed listing, then removes the exact MediaStore row.
These controls are compiled only into the isolated probe.

## Active summary, capacity and mixed locations

Add `--verify-c3-active` to the regular WebDAV run. The same two test files
arrive in pulses so the location view can report a real rate while bytes
advance. The run still verifies both hashes and uses its exact cleanup.

Run `--sftp-only --sftp-capacity` to browse the pinned read-only SFTP fixture
and check 205 GB free. The number comes from the fixture's SFTP response.

Use standalone `--c3-mixed` to browse WebDAV, S3 and capacity-enabled SFTP
while all three owned loopback services remain available. This lane creates
no Downloads files.
