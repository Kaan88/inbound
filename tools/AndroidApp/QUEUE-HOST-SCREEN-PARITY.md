# Android queue/host screen parity capture (C1/C2/C5/C6)

This is a separate guided capture runner for the approved C1/C2/C5/C6 exports
listed in `docs/plans/android-mockup-acceptance.md`. It reuses only the existing
probe smoke helper's ADB/UI-tree primitives and the existing unchanged-image
side-by-side compositor. It installs an explicitly supplied probe. Opt-in
workload lanes own their disposable loopback Hosts and ADB reverse mappings.
The runner preserves device settings, existing apps and shared files.

## Isolation and preparation

Use only the dedicated API 33 `inbound33` emulator and an explicitly supplied
`app.inbound.android.rcloneprobe` APK. The runner refuses a physical device,
another AVD/API, an APK with another package ID, and an already-installed probe.
Do not run it while another UI test owns the emulator. It installs the probe
fresh, launches normally, and uninstalls only that probe package after capture
(including on failure). The rest of the device is left alone.

Prepare any disposable paired Inbound Host separately. Use only a loopback or
emulator-reachable test endpoint, a fake test password, and a disposable host
name. Enter the address/password directly on the emulator; the runner never
accepts or writes the password. Do not use a public Host, account, or real
credentials. Stop the separately started fixture yourself after the run.

Install Pillow if needed, then run from the repository root in PowerShell:

```powershell
python -m pip install Pillow
python tools/AndroidApp/capture-queue-host-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\queue-host-parity-$(Get-Date -Format yyyyMMdd-HHmmss)" `
  --queue-name "Parity Queue" `
  --host-name "Parity Host"
```

By default, the runner pauses at each checkpoint. Prepare the matching state manually,
visually check it against the reference, then press Enter. `--unattended-c1-c2`
opts into C1 empty Queues, creation/capture of the named stopped disposable
queue, and C2's no-schedule editor for that queue. Selectors come from
`MainActivity.cs` (`nav_queues`), `queues_page.xml` (`queue_new`),
`QueuesScreen.cs` (queue-name dialog), and `queue_editor.xml` (`queue_editor_save`).
No existing queue is changed; no schedule or download is created. The probe is
uninstalled after capture, removing its isolated Host state. Ambiguous or absent
selectors fail closed.

The configured C2 reference is Mon–Fri 01:00–07:00 with unmetered and charging
conditions enabled where supported. `--unattended-configured-c2` is a separate,
explicitly opted-in lane; it does not alter the existing unattended or guided
modes. It requires the fresh API 33 probe and pins the queue name to `Night`.
It creates no downloads or other workload. It uses the observed native API 33
time-picker tree (`android:id/timePicker`, `hours`, `minutes`, `radial_picker`,
`am_label`, `pm_label`, and `button1`); virtual radial values are tapped by
their bounds only after exact ID/content-description and bounds checks. Any
unexpected or ambiguous tree aborts. Day chips and condition switches are
selected by the exact app resource IDs defined in `QueueEditorScreen.cs` and
`queue_editor_ids.xml`.

The lane creates a `Weekdays` schedule in `Night`, sets 01:00 and 07:00,
confirms 00 minutes and AM, unchecks Saturday and Sunday, enables charging and
unmetered, disables the `Weekdays` schedule
before save, then reopens and verifies the persisted time, weekday selection,
conditions and disabled schedule state. It captures the configured editor and
its footer as separate states. Only the disposable queue is modified; cleanup
uninstalls only the probe. It reports running/action C1, C5 and C6 unverified.
The configured editor and footer passed review on API 33 in
`c2-clean-boot-20261120-035129`. An unexpected picker hierarchy stops the
procedure before it tries to select a time.

Each checkpoint checks a UI-tree
anchor before saving the original-resolution screen capture, hierarchy XML,
scoped Inbound logcat, and side-by-side pair. A matching anchor proves only that
the intended screen text was present, not that the state is correct or visually
accepted.

## Disposable fixture inputs and capture order

Use these exact labels as entered in the app. The named queue and host are
labels only; they do not create fixtures automatically.

| Checkpoint | Fixture/state to prepare |
| --- | --- |
| `c1-empty` | This phone → Queues; empty/ready Main queue. |
| `c1-populated-stopped` | Create `Parity Queue`, leave it stopped, and show it beside Main. Do not add a real or unnecessary payload. |
| `c1-populated-running` | Start the same queue with no downloads; capture the ready/running state. |
| `c1-queue-actions` | Open its long-press actions sheet; do not select a destructive action. |
| `c2-no-schedule` | Open the disposable queue's schedule editor with no schedule. |
| `c2-configured` | In the separate unattended lane, save `Weekdays` in the disposable `Night` queue: Mon–Fri, 01:00–07:00, charging and unmetered on, schedule disabled. No workload. |
| `c2-editor-footer` | Reopen the schedule editor and scroll to the bottom/footer with the configured values still visible as intended by the reference. |
| `c6-unpaired` | Settings → Hosts and web UI before pairing. |
| `c6-pairing-form` | Open Connect to another Inbound and capture the blank address/password form. Enter the disposable endpoint, `Parity Host`, and fake password only after this capture. Capture any confirmation/warning states separately in notes; this runner does not infer their acceptance. |
| `c6-paired` | Pair to and select the disposable test Host. Capture its populated Downloads screen with the remote destination banner, not the pairing form. |
| `c5-phone-selected` | Open Where downloads run with both This phone and the paired test Host available; select This phone. |
| `c5-paired-selected` | Select the disposable paired Host and reopen the switcher to capture the remote selection. |

Do C1/C2 on This phone and create no actual downloads. Configure the test Host
independently with no unrelated jobs; do not edit the phone's queues while the
remote Host is selected. C5 requires the C6 pairing to have completed. If the
fixture Host cannot be safely paired, stop before C5/C6 and record those states
as blocked; do not substitute a public service. Removing the probe package
clears its test pairing and local app data; separately stop/remove only the
disposable Host fixture and its task-owned data.

## Evidence and limits

The new evidence directory contains `preflight.json`, `apk.sha256`, `flow.txt`,
and, per state, `<state>-capture.png` and `<state>-ui.xml`. States with an
approved reference also get `<state>-pair.png`. `--unattended-local-hosts`
captures unpaired Settings and the blank form without entering credentials
or connecting; both captures are supplemental, not paired C6 evidence.
`--paired-server-dll PATH` starts the explicit Server DLL with disposable
state and synthetic credentials, pairs over a loopback-only ADB reverse,
downloads one fixture to the Server's root, and captures C5 on both
selections and C6 with that finished row. The one-row C6 is a functional
baseline; the full mixed remote list and graph remain separate evidence.
Non-C5/C6 states also get a scoped `<state>-logcat.txt`; Host-state logcat is
intentionally omitted to avoid retaining connection metadata. Each pair uses the unchanged
reference `c1-queues.png`, `c2-schedule.png`, `c5-switch-host.png`, or
`c6-remote-host.png`; source reference and capture bytes are not modified.
`preflight.json` records the AVD/API, package, APK hash/version, display
resolution/density, and non-secret fixture labels. It does not record a
pairing address or password. The screen capture itself must not contain
credentials or private endpoint details.

Except for the explicitly enabled unattended lanes (which create the named
disposable queue, and configured C2 creates its single disabled `Weekdays`
schedule), this runner records screenshots and UI trees without creating
schedules, downloads, Hosts, or pairings. The C2 lane's persisted-state checks
verify only the local synthetic editor values; they are not an acceptance
result. Record setup and outcomes in `flow.txt`. Inspect every pair at native size and record differences in the
acceptance matrix. Captures and automated tests are evidence inputs, not a
visual PASS; Astra must review both code and comparisons, and only the owner
can approve a design deviation.

Host-only checks (no device action or build):

```powershell
python -m unittest tools/AndroidApp/test_queue_host_screen_parity.py
python -m py_compile tools/AndroidApp/capture-queue-host-parity.py
```

Configured C2 is a separate explicit invocation; no device/build operation is
performed by the host-only checks above:

```powershell
python tools/AndroidApp/capture-queue-host-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\queue-host-configured-c2-$(Get-Date -Format yyyyMMdd-HHmmss)" `
  --unattended-configured-c2
```

## Real C1 workload lane

`--unattended-c1-workload` is an independent opt-in on a fresh isolated probe.
It creates Night and Mobile, enables a daily Night schedule with a one-hour window starting two whole hours
after the emulator's current local hour, and uses the normal New download form to assign three
uniquely named synthetic loopback downloads to Main, Night and Mobile. It
starts Main if needed and captures three concurrently visible cards and the
Mobile long-press actions sheet. It records two increasing fixture byte counts
and card descriptions. It also taps the chart and verifies the same Host's live
Downloads graph. Schedule firing and queue-sheet actions require separate
checks. If the cards cannot all
fit or the native picker/queue selector is different, it fails rather than
claiming a matching capture.

The server and reverse mapping are task-owned; uninstall removes the fresh
probe's isolated state. Partial or completed task-named files in shared
Download are retained and need separate inspection before any removal.
Cleanup attempts are independent, with failures recorded; an interrupted or
timed-out installation or reverse request can need manual reconciliation.
The screenshots still require visual review. No real accounts or production
data are involved.

## Two paired Hosts and a live remote workload

Add --mixed-hosts to the existing --paired-server-dll invocation. The runner
creates two independently owned loopback Hosts and pairs them through the
isolated probe. It verifies all three switcher selections. It checks the
finished payload hash and requires both server bytes and the app's received
figure to advance on another transfer before capturing C6. Pairing forms,
password-bearing descriptors and app logs are excluded from the evidence.
Inspect the graph and switcher pairs before recording visual acceptance.
