# ANDROID.6 remaining acceptance

Run on the S24 FE with the current signed APK. Keep existing emulator
metering, saver and forced-idle results, and the phone's ordinary locked-screen
schedule and reboot evidence. Do not repeat those scenarios or use
`run-gate.sh` (it uninstalls the app). Expanded accessibility is ANDROID.13.

## Fixture and baseline

- Build `tools/AndroidTrialServer/AndroidTrialServer.csproj` in Release if
  needed. Run its DLL with `--port 47619 --log --trace-requests`. Use a scoped
  ADB reverse for UI-only checks. For genuine network checks, use `--lan`
  only on an authorized private network, with access limited to that network;
  use the PC's LAN address, not USB reverse. Confirm connectivity before
  starting the timed run. Do not change firewall policy without permission.
- The fixture serves `/bench.bin?size=N&bps=B` and `/sha256?size=N`. `bps`
  is **per connection**. Give each phone job a unique test filename. Save
  timestamped UI observations, fixture `TRACE`/`SENT` lines, and completed
  file size/hash. Metadata requests alone are not evidence of transfer.
- Record phone model, API, app version, app appearance, display settings,
  charging, power saver, idle state, network classification and existing
  jobs/queues. Use minimum brightness and a longer timeout for UI captures;
  explicitly turn the screen off for idle observations. Never read or
  change unrelated jobs, schedules or settings.

## Device acceptance matrix

| Check | Setup and expected evidence |
| --- | --- |
| Deep Doze | Use a disposable scheduled queue and a real LAN fixture. Observe the phone unplugged, locked and noninteractive, and verify deep `IDLE` with `adb shell dumpsys deviceidle` (Wi-Fi ADB if USB charging prevents idle). Record queue state and body requests around the scheduled time; then wake normally and observe automatic completion and matching SHA-256 without manually starting the queue. If deep idle isn't reached, mark it unobserved; a locked/AOD phone alone is not proof. Report natural and forced idle separately; do not force idle on the daily phone without owner approval. |
| Network and power holds | Isolate conditions one at a time: metered Wi-Fi vs allowed Wi-Fi, battery saver on vs off, and unplugged vs charging for a charging-only queue. Verify the actual device condition and the app's hold reason; verify zero payload bytes while blocked and automatic completion/hash after clearing. For a genuine connectivity-loss check, verify the LAN endpoint becomes unreachable rather than changing only ADB transport. Use owner-approved network/power changes and restore each immediately. |
| Sustained transfer | On the LAN fixture, use one connection at 65,536 B/s. Size the 30-minute and one-hour screen-off checks at 128 MiB (about 34 minutes) and 256 MiB (about 68 minutes), respectively. Start the clock when the screen goes off. Capture periodic download progress and OS state; a fixture request-start line is not a progress sample. Check elapsed time, exact final byte count and SHA-256. If testing a temporary disconnect, capture the same row before and after, then verify a resumed range starts at a nonzero offset. Distinguish a restarted transfer from a resumed one. |
| Selected host and visual scope | Pair only a disposable Inbound Server; reuse the pinned HTTPS relay when needed. Capture Queues/Schedule/Settings (C1/C2/C7) on the selected host and compare against mockups. Create a disposable queue and disabled schedule, reopen selections and limit, and add/start a fixture job on that queue. Verify job/queue/settings changes on the disposable host, then switch back and verify phone-local state was unaffected. Record unsupported controls rather than treating the remote as a phone with Android power rules. |

Suggested first phone batch: selected-host plus short condition checks,
then run the sustained transfer while the owner can spare the phone. Run
natural deep Doze separately; the clock time needed is device-dependent.
Do not substitute an emulator pass for a missing physical result.
Before choosing the scheduled minute, read the phone's current deep-idle
timeouts from `dumpsys deviceidle`. On the S24 both `inactive_to` and
`idle_after_inactive_to` were 30 minutes; allow time for both stages plus
motion sensing and location checks. Keep the phone stationary and record
deep `mState` before and after the scheduled minute. Do not start this
case while a separate screen-off transfer is in progress.

After locking, run `observe-natural-doze.ps1` with the Wi-Fi ADB `-Serial`,
the server-independent phone schedule time as `-Due` (include its UTC
offset), the fresh LAN `-FixtureLog`, and a new `-OutputDirectory`.
The observer only samples; it does not force idle, start jobs or wake
the phone. Inspect its JSON lines and fixture timestamps together,
then hash the final file separately. Payload completion alone is not
proof of natural deep idle.

The focused Server pairing/TLS suite already passed 5/5 on a fresh run
after its initial fixture-setup failure. Do not repeat it solely to clear
that historical blocker.

## Current S24 checkpoint (signed 0.0.7)

- Metered Wi-Fi, Battery Saver and physical charging-only holds passed:
  each stayed at 0 B with no payload requests and completed automatically
  with a matching hash once the condition cleared.
- The build plan's 30-minute screen-off check passed over LAN: the
  unplugged phone finished 128 MiB with a matching hash at 34:06.
  `IDLE_PENDING` did not establish natural deep Doze.
- Treat the separate one-hour measurement as soak coverage, not as a
  substitute for the 30-minute check or natural deep Doze. Start the
  screen-off clock only after observing nonzero foreground progress.
  If the observer restarts while the same request is still running,
  preserve the original clock and disclose the gap in samples.
- Selected-host checks passed for the 64 KB/s limit, disabled schedule
  persistence across restart, and manual queue start with a matching
  payload hash. Separately, an enabled timer on a disposable remote
  host released a stopped queue automatically and the same job completed
  with a matching hash. That check used IPC, not the phone UI.

## Cleanup and report

Delete only test-created jobs, files, queues and schedules; unpair the
disposable host; remove only the reverse mapping the run installed; stop
fixture listeners. Verify prior device network/power and app appearance,
brightness mode/value and timeout, and the original local UI baseline.
Log which cases passed, failed or were unobserved in `PROGRESS.md`; request
independent review before closing the milestone.
