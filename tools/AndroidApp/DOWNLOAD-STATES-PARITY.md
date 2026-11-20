# Android download-state parity capture (A2/A3/A6/A7)

`--active-only` is an unattended A2-only smoke. It starts the runner's
loopback fixture download through the isolated APK's test hook, requires
two increasing nonterminal byte samples, captures the named download row,
and checks the completed file hash. Queued A2, A3, A6 and A7 are not covered.

`--mixed-a2` is a separate explicit opt-in. It supplies four unique
active/queued/paused/finished fixture URLs, asks the operator to add them with
the app's normal Add flow and arrange each state with app controls, then captures
each state and a graph-visible Downloads screen. It never creates jobs or changes
state synthetically. After capture it waits for all four real GETs to complete,
records hashes and request history, and retains uniquely named payloads in
shared Downloads for review. Remove only those named files/jobs through the app
when authorized. This is guided capture, not unattended UI automation.
Optionally combine `--mixed-a2 --capture-b5` to capture the B5 Downloads
multiselect state using three of those real fixture rows and the actual bulk
overflow menu. B5 is not PageLinksScreen selection; selection/menu setup stays
operator-driven.

`--unattended-b5` is a separate standalone opt-in lane and cannot be combined
with the A2 flags. It uses exactly five B5 endpoints and uniquely named jobs;
each endpoint serves distinct deterministic bytes. The finished row is added
first through the guarded isolated-probe hook, allowed to complete, and its
exact shared-Download SHA-256 is verified before active work is seeded. Three
active jobs are then started sequentially and held after their first 16 KiB.
The paused row is produced with its ordinary row Pause action. The queued row
is admitted through the normal Add control → Link or several links → New
download form → Later flow, and must remain visibly Queued. The runner does
not infer queued state from a capacity waiter after Pause. Before selection it
requires this exact visible state mix:

* selected candidates: `selected-active` downloading, `selected-paused` paused,
  and `selected-queued` queued;
* unselected controls: `other-active` downloading and `other-finished` finished.

It fails closed if the Later form does not expose its expected address and
controls, a state is absent, a row is not unique, or the selection bar does
not report exactly `3 selected`. It opens only the uniquely identified enabled
bulk-overflow control and requires all five named rows plus their selection
semantics in selection mode. The menu anchor accepts `Move to queue` (the new
B5 parity label) and legacy `Select all`, then captures against `b5-select.png`.
No bulk-menu action, delete, cancel-selected, or other destructive job action
is invoked. The finished fixture file is removed only during cleanup after its
exact path matches the B5-specific fixture SHA-256; if already absent, cleanup
is a no-op. A mismatch is preserved and reported without replacing the original
capture diagnostic. Active/paused/queued partial files are not deleted. The
fixture responses are released during
cleanup and only the installed probe and this runner's reverse mapping are
removed.

The fixture does not alter app scheduling or synthesize job states. The
unselected finished row is an actual completed transfer, not a mocked label.

This separate guided run prepares a throttled, disposable HTTP download fixture
and captures A2 progress/queued, A3 completion snackbar, A6 active/finished
Details, and A7 ongoing notification states. It uses only an explicitly supplied
`app.inbound.android.rcloneprobe` APK on the dedicated `inbound33` API 33 AVD.
The runner reuses `smoke-remote-files-ui.py` for ADB/UI-tree access and
`capture-remote-files-parity.py` for side-by-side composition; neither shared
helper is modified. It does not build an APK, wipe app data, reset the AVD, or
touch the regular `app.inbound.android` package. Do not run while another test
owns the emulator.

## Before running

1. Have the owner authorize the emulator run. Verify the intended AVD is
   `inbound33` and no other capture runner is using it. This script refuses
   physical-device serials and any AVD/API other than `inbound33`/33.
2. Prepare a fresh isolated probe APK with application ID
   `app.inbound.android.rcloneprobe` using the separately authorized Android
   build workflow. This runner does not build or install over an existing
   package; it refuses if the probe package is already installed.
3. Ensure Pillow is available for pairing and choose a new evidence directory:

```powershell
python -m pip install Pillow
python tools/AndroidApp/capture-download-states-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\download-states-parity-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

The runner validates the AVD, API, APK package ID, package absence, and records
the pre-run `/sdcard/Download` inventory and APK SHA-256. It starts a loopback
fixture and adds a temporary ADB reverse mapping. Each deterministic payload is
16 MiB, streamed in 16 KiB chunks with a 100 ms pause (about 50 KiB/s), long
enough to capture progress and notification states. It prints unique
`active`/`queued` URLs and task-specific filenames before launching the probe
without bypassing first-run setup.

For mixed A2, append `--mixed-a2` to the command. Do not combine it with
`--active-only`; the former uses normal app UI rather than the test-hook add.
Append `--capture-b5` as well only when the three-row B5 capture is wanted.
For the standalone mixed-state B5 lane, use a fresh command with
`--unattended-b5` instead; do not add either A2 flag. It captures while four
real transfers are held after a small initial chunk, so row state is stable
without waiting for them to finish. It verifies the target row states and
selection count before the screenshot. A timeout or state mismatch is a
fixture/runtime limitation, not a successful capture.

By default all six checkpoints remain operator-driven. The optional
`--unattended-a7-navigation` flag automates **only** the A7 navigation: it sends
HOME and asks Android to expand notifications, then the existing notification
shade check must succeed before capture. The operator must still grant any
notification permission and verify the ongoing notice is actually present.
This does not synthesize downloads, app states, or notification contents; all
A2/A3/A6 setup and capture prompts remain manual.

## Guided capture sequence

Use Inbound's own screens and actions only. Add the printed `active` URL first.
Once that transfer has nonzero progress, add the `queued` URL. If the default
queue permits parallel transfers, set its concurrency to one before adding the
jobs; do not use an unrelated pre-existing job. Confirm both unique fixture
names in the app and ensure the second remains Queued while the first runs.

At each prompt, arrange the exact state and press Enter. The runner waits for a
visible `Downloads` or `Details` UI anchor; for A7 it verifies the notification
shade is foregrounded. It then saves the original-resolution screenshot, UI
hierarchy, app-scoped logcat, and a labelled side-by-side PNG against the
unchanged approved reference.

1. **A2 progress** — show the active fixture row after progress is nonzero and
   changing. A tap-triggered/details overlay is not a substitute for Downloads.
2. **A2 queued** — keep the active job transferring and show the other fixture
   row explicitly Queued. This second capture is paired with the same approved
   A2 reference.
3. **A6 active Details** — open Details for the active fixture and capture its
   active state, then return to Downloads.
4. **A7 ongoing notification** — background Inbound using the normal device
   navigation gesture/button, expand the notification shade, and capture the
   ongoing transfer notification with its progress and controls. Do not tap a
   notification action unless recording that separate flow. If Android asks
   for notification permission, grant it only for this disposable probe and
   record the prompt/outcome; uninstalling the probe restores its package
   permission state. Use the dedicated emulator notification shade and do not
   dismiss unrelated notices. Return to Inbound.
5. **A3 completion snackbar** — when the script reports the active transfer
   completed, immediately leave the transient snackbar visible and press Enter.
   If it has already disappeared, record that fact and do not recreate it by
   synthesizing an intent or posting a notification. The screenshot still
   captures the finished row, but does not prove snackbar parity.
6. **A6 finished Details** — open the completed active fixture's Details and
   capture its finished state/actions.

The runner waits up to ten minutes for each transfer, verifies both exact
task-owned files under `/sdcard/Download` against the deterministic fixture
SHA-256, and records fixture request/byte counts. A final hash match proves
fixture integrity only; it does not establish UI correctness or parity.

## Evidence boundary and cleanup

Evidence includes `preflight.json`, `apk.sha256`, `flow.txt`, fixture request and
hash records, and for each state `<checkpoint>-capture.png`, `-ui.xml`,
`-logcat.txt`, and `-pair.png`. The approved mockups and original captures are
not modified. The full-screen notification-shade capture can include other
emulator notices; inspect it for unrelated content before sharing it. Inspect
each pair at native size, record layout/state/flow
differences in `docs/plans/android-mockup-acceptance.md`, and obtain Astra's
independent code-plus-comparison verdict before claiming acceptance. Generated
pairs and successful downloads are not themselves a PASS.

On normal exit or capture failure, the script releases its held fixture
responses, stops its own fixture, removes only its own ADB reverse mapping,
and uninstalls only the probe package it installed. In B5 it removes only the
uniquely named finished payload, after exact-path SHA-256 verification; it
leaves unfinished partial files untouched for operator review. Other lanes do
not automatically delete shared `Download/` files. Verify the app returns to
the captured baseline before ending.
Unexpected files, jobs, package state, reverse mappings, or AVD state are a
stop-and-report condition, not permission to broaden cleanup.

Host-only checks (no device/build action):

```powershell
python -m unittest tools/AndroidApp/test_download_states_parity.py
python -m py_compile tools/AndroidApp/capture-download-states-parity.py
```

## Completion snackbar Open

Run `capture-a3-snackbar-open.py` with the same explicit ADB path, serial,
probe APK and new evidence directory. The isolated run requires Photos as the
unique PNG viewer. It finishes a nonce-named synthetic PNG, taps the
filename-matched snackbar action, checks the viewer and image pixel, and
verifies the saved file hash. It removes its exact hash-matched file after
uninstalling the probe. The result records metadata without saving the viewer
image or its hierarchy.
