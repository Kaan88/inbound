# Android link-intake parity capture (B1–B6)

This capture is separate from the core A/C runner and uses the existing
disposable HTTP fixture at `tools/AndroidApp/link-fixture/`. It is for the
dedicated `inbound33` API 33 emulator and an explicitly supplied
`app.inbound.android.rcloneprobe` APK. It does not build, clear app data, reset
the AVD, touch the regular app package, or send a synthetic share intent.
Do not run while another UI test owns the emulator.

## Host-only preparation

Build/install nothing for this procedure. Have an already-built isolated probe
APK available and Pillow installed for side-by-side PNGs. The script creates a
new evidence directory and starts the Python link fixture on loopback, then
adds an ADB reverse only for its dynamically selected port:

```powershell
python -m pip install Pillow
python tools/AndroidApp/capture-link-intake-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\link-intake-parity-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

### Opt-in unattended capture

Add `--unattended` to opt into prompt-free capture of **B4 only**.
The runner opens Inbound's browser through the Add menu, navigates to the
loopback `/page`, opens **Links on page**, and selects only Document and Image.
It waits for each real UI anchor and pairs the capture with its approved
reference; it does not drive Chrome, submit downloads, or create an Android share intent.
The selected page-link state is functional evidence, not the approved B5
screen. B5 requires selecting existing Downloads rows and opening the bulk
overflow; capture it with the download-state fixture. B1's chooser/share,
B2, B3 and B6 remain untaken unless separately captured in guided mode.

Add `--capture-caught` with `--unattended` to capture the anonymous B2 and
synthetically signed-in B3 confirmations before submission. The run only
uses the loopback fixture and does not submit a download.

Add `--capture-b6` with `--unattended` to opt into the B6 loopback run. It
creates a unique output name and refuses if that exact Downloads path already
exists. It adds the `token=gone` URL through Inbound's UI, captures the failed
row and Details, opens Refresh and captures the sheet before entering
`token=new`, then checks the completed output's SHA-256 against the fixture.
Cleanup removes only that unique path and only after a second matching hash
check. A hash mismatch or incomplete transfer is preserved for inspection;
existing and baseline paths are never removed. This is a 410-from-zero refresh
flow, not evidence of partial-byte resume.

Use `--capture-b6-partial` instead to opt into a separate, guided real-app
partial-expiry lane (do not combine it with `--capture-b6`). It serves a
16 MiB deterministic body at `/partial/old.zip` and `/partial/new.zip`, both
with the same stable ETag. The runner sets and verifies the real download's
Connections control to 1. Wait until Details visibly reports nonzero progress,
pause that task in the app, then confirm the prompt. Only after the pause does
the fixture make the old URL return 410. The same task must show its saved-byte
count on the B6 refresh sheet. Replace the URL with `/partial/new.zip`; the
runner requires an observed nonzero `Range` request to that URL and the final
whole-body SHA-256 before removing only the unique hash-verified output path.
`partial-b6-requests.txt` records range evidence. The lane never uses file
size/stat as a progress signal, app hooks, shell-created job state, or
shared-storage cleanup. The prompt at the pause checkpoint is intentional.
Example invocation:

```powershell
python tools/AndroidApp/capture-link-intake-parity.py `
  --adb "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" `
  --serial emulator-5554 `
  --apk "C:\path\to\Inbound-rcloneprobe.apk" `
  --evidence "$env:LOCALAPPDATA\Temp\opencode\link-intake-b6-partial-$(Get-Date -Format yyyyMMdd-HHmmss)" `
  --unattended --capture-b6-partial
```

After the final hash and Range assertions, the runner removes only the uniquely
identified job through the normal Details → Delete flow with **Remove from the list only**
selected, verifies that row is gone and the output remains, then rechecks the
hash and removes that exact output path. If a hash/range/UI assertion fails,
the output is preserved for inspection rather than deleted.

The runner refuses a non-emulator, anything other than `inbound33`/API 33, an
invalid probe APK, an existing probe install, or an existing evidence folder.
It captures original-resolution PNGs and UI hierarchy XML, paired with the
unchanged approved references. On exit it stops only its fixture, removes only
its own reverse, and uninstalls only the probe package it installed. Review
`flow.txt`, `fixture.txt`, device baseline and the fixture's request log; remove
only uniquely identified task-created jobs/files after checking hashes.

To verify **A6 Stop** through the same real transfer, also add
`--verify-details-stop`. It checks enabled Stop and hidden Download again
in active Details and taps Stop. It requires the existing Details to show
Cancelled with nonzero progress, then uses Retry. The existing expiry,
nonzero Range, hash and exact cleanup checks still apply.

Use --capture-b6-browser with --unattended --capture-b6-partial to
exercise the real browser confirmation. The disposable WebView opens a
loopback page with a direct replacement link. The test first cancels and
checks that the same expired row and visible partial progress remain, with no
new range request during the observation. It then catches the link again,
confirms, verifies the nonzero replacement range and final hash, and performs
the same owned-file cleanup.

## Guided capture sequence

The script prints the actual fixture port. In Chrome, open
`http://127.0.0.1:<port>/page`. For **B1**, use the page's actual Share link
action and choose Inbound from Android's system chooser. The first capture is
the OS chooser; the second is Inbound's caught-link sheet. Do not replace this
with `am start` or an `ACTION_SEND` injection. Cancel should create no job.

Continue through the prompts:

1. **B2** — Add file from the anonymous caught-link sheet; verify one new job
   and the payload SHA-256 recorded in `fixture.txt`.
2. **B3** — In Inbound Browser, use fixture `/signin` (sets a fake local
   `inbound_test=yes` cookie), then `/page`. Capture the sign-in/cookie consent
   state and try decline before accept. Decline must produce no authenticated
   `/auth.zip` request/job; acceptance is disposable fixture behavior only.
3. **B4** — Open Links on page and capture the full list/filter.
4. **B5** — Select only Document and Image, capture the selected count/action,
   then submit. Confirm only the selected files were added and their hashes
   match the fixture.
5. **B6** — Add `/expired.zip?token=gone` (410), capture the expired/refresh
   UI, replace it with `/expired.zip?token=new`, and verify the same job
   completes. The optional unattended flow does this with `--capture-b6`.
   This is not partial-byte resume evidence.

For each pair inspect the original-size capture and record visible differences
and flow outcomes in `docs/plans/android-mockup-acceptance.md`. Generated pairs
are evidence aids, not visual acceptance. No real credentials, public host,
private URL, or real account is in scope.

## Supported and unverified

The fixture supports an anonymous page and payloads (`a.zip`, `b.pdf`, `c.jpg`),
a fake-cookie sign-in and protected `auth.zip`, a deterministic 410/refresh
route, a throttled deterministic same-validator partial-expiry pair, and a slow endpoint. Historical notes already support Android's actual
Chrome chooser flow, selection of two links, fake-cookie decline/accept, and
410-to-success refresh. This procedure organizes fresh B1–B6 captures around
those supported local flows.

Real-account/browser compatibility is unverified and explicitly out of scope.
The fixture does not prove arbitrary-site sign-in, unsupported request methods,
real authenticated downloads, or partial-byte refresh. No device run or visual
acceptance is implied by host-only validation. The partial-expiry fixture
contract test proves fixture behavior only; it does not claim the real-app
lane passed.

Host-only checks:

```powershell
python -m unittest tools/AndroidApp/test_link_intake_parity.py
python -m py_compile tools/AndroidApp/capture-link-intake-parity.py
```
