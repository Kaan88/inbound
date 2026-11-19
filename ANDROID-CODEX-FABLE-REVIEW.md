# Android implementations by Codex — later Astra review

Status: OPEN for later independent Astra review. Implementer/coordinator: **Codex
(OpenAI/GPT)**. Owner clarification (2026-11-19): continue this existing review
record, identify Codex as the implementer, and prepare it for Astra to review the
implementations later. No new review has been performed by this documentation update.

The filename and FABLE-CODEX entry identifiers are retained for existing links
and historical traceability. Earlier references to pending Fable review describe
the previous assignment; the later reviewer for this queue is now **Astra**.
Historical reviewer attributions and completed milestone verdicts remain unchanged.

Read CLAUDE.md and ANDROID-CODEX-PROGRESS.md first. The earlier Claude work and
its completed reviews remain in docs/plans/android-fable-review-queue.md.
This queue covers implementations after the Codex takeover. Existing Astra
milestone PASS results do not close the later independent review queue.

## Review procedure

For each new entry identify **Implementer: Codex**, scope and exact commit (or
explicitly local/uncommitted), decisions and risks, validation actually run,
raw evidence, completed milestone audit findings/fixes/verdict, and remaining
acceptance gaps. Record delegated work and Codex's verification where applicable.
Give Astra the code and requirements for an independent read before the previous
findings. Record its later findings, Codex's fixes, verification and final verdict
separately; keep entries OPEN until that review actually happens. Continue adding
important implementation changes and decisions as work proceeds.

## Codex review handoff (2026-11-19)

- Implementer/coordinator: Codex. Later independent reviewer: Astra.
- Current source: android-app implementation commit d0816724208a0ae543cf2d84a02e4ee32f218de2;
  delivery/evidence documentation checkpoint 50df32b8cd5a51079d054f6fe3d0bc6b14f05fe1.
- Scope queued below: permanent Android signing, GitHub updater and ANDROID.5
  link catching, including shared Runtime refresh/durability and queue-race fixes.
- Existing milestone audits remain recorded as PASS where established. Later
  independent review and the documented device/acceptance gaps remain OPEN.

## FABLE-CODEX-1 — Permanent Android release signing

- Status: OPEN for later Fable review; Astra code audit PASSED; S24 reinstall/startup passed; broader phone acceptance pending.
- Baseline: android-app at 49f5663c. Signing/updater implementation was committed in
  d2e06d27; publication guard in 178fdb4. Android 0.0.2 and its feed are public;
  there is no merge to main. The later 0.0.3 bundle remains local.
- Scope: scripts/build-android.ps1, tools/AndroidApp/release-apk.py,
  tools/AndroidApp/test-release-apk.py, .gitignore and the signing documentation.
- Owner direction: sign the next APK with the already-created permanent key;
  one-time reinstall replaces debug signing. The owner identified Kaan88/secrets
  and the local encryption-key location. Its README was read through GitHub.
  Existing local signing inputs were present at that checkpoint, so no archive was decrypted then and
  no key generated, changed, uploaded or added to this repo.

### What Fable should inspect

- The certificate pinned in release-apk.py is the intended permanent Android
  identity (public digest starts 3746bf5a and ends 8f0457). The verifier must
  fail on debug signing, wrong keys, multiple signers or unrecognized tool output.
- No password goes to command arguments, MSBuild properties, build subprocess
  environments, checked-in files or diagnostics. Only apksigner receives the
  existing password-file path. This PKCS12 uses the same store/key password;
  --key-pass is omitted so apksigner reuses the store password.
- Build provenance: fresh GUID publish directory, explicit InboundTestBuild=false
  and AndroidKeyStore=false; sign only this invocation's APK. A preexisting
  production APK must never make a failed/redirected/test publish appear valid.
- Windows build-tools versions differ: CRLF output, Signer #1 vs V3.0 Signer,
  sdkVersion vs minSdkVersion. Signature verification must run apksigner first;
  parsing a digest alone is not cryptographic verification.
- Manifest must be app.inbound.android, minimum API 31, target 35, arm64-only and
  non-debuggable; NativeAOT packaging checks must reject test hooks.
- check-apk.py is an existing marker check, not a DEX definition/ELF symbol parser
  or a startup test. The csproj separately checks the defined TLS export.
  Do not treat APK validation or audit approval as device launch acceptance.
- Signing output is a new file distinct from the input; inspect error handling,
  partial files after failure, tool choice and whether only verified output is
  offered for delivery. Ignore rules protect APKs and signing material.
- Future distribution: signer identity must remain stable across updates; version
  increments, updater/download trust and installer behavior are later work.

### Astra audit history and evidence

- Round 1: changes required. Findings: unpinned caller-supplied certificate;
  fixed-path stale output under inherited test-build settings; CRLF/new SDK
  parsing failures; password environment inherited by build servers.
- Main-agent fixes: public certificate pinned; unique publish destination and
  explicit release flags; tool-output normalization and old/new field handling;
  signing moved after publishing using apksigner's password-file reference.
- Regression tests: seven Python tests passed for Windows formats and rejecting
  wrong/source-stamp-only certs, multiple/missing signers, wrong package/API/ABI,
  and debuggable flag. Initial build produced a permanently signed APK, but the
  first verifier rejected its newer SDK output; it was not delivered.
- Final Astra verdict: PASS after main-agent fixes and re-audits. Ten regression tests passed.
  Real SDKs 34.0.0/35.0.0/36.0.0/36.1.0/37.0.0 accepted the release and rejected debug signing.
  Final build also passed with an inherited InboundTestBuild=true deliberately overridden.
- Raw evidence: artifacts/android-app/codex-signing/build-release.txt (first attempt),
  build-release-audit-fixes.txt (revised end-to-end build), prior debug-signed APK
  retained there only for negative validation. All artifacts are git-ignored.
- At that audit checkpoint device launch/reinstall, public publication and updater acceptance had not run. See the S24 update below; public publication and updater acceptance remain open.

## Fable review results

Pending. Record reviewer, code checkpoint, findings, main-agent fixes, checks and
verification verdict here when the later review actually occurs.

### Final signing checkpoint (2026-11-18)

- Final code audit: Astra PASS after both fix rounds. Main also fixed SDK 34-36's
  no-colon signer label and verifies a same-directory temporary APK before atomic,
  no-clobber publication; v4 sidecars disabled. Ten tests cover output formats,
  invalid metadata/signers, sign/verify failure cleanup and concurrent output creation.
- Final build: artifacts/android-app/codex-signing/build-release-final.txt;
  signed-builds/a7a5c9246dbb46e492fe24bae5e74c05/Inbound-android-release.apk
  under artifacts/android-app/. SHA-256 cf8c3667622e7af6f0b73ea9203f5c9cfa345081dc3918ac31f96adc291b315d.
- Earlier MSBuild password-env attempt was discarded. Its eight reusable workers
  were verified as this run's children and stopped; final signing uses only the
  existing password file, after the build. Fable should check this design boundary.
- Desktop copy made and hash matched. No phone action/public release. Fable status
  remains OPEN; record later reviewer findings and approval, not Astra's verdict alone.

### S24 acceptance update (2026-11-18)

Owner authorized removal of the existing app and installation. On the S24 FE (SM-S721B, API 37), uninstall and permanent-key APK install succeeded; old private app state reset. Installed APK SHA-256 cf8c3667622e7af6f0b73ea9203f5c9cfa345081dc3918ac31f96adc291b315d, version 0.0.1/code 1, min 31/target 35. Startup returned ok, MainActivity was top resumed and PID 29872 remained alive. TotalTime 377 ms, WaitTime 380 ms, fullyDrawn 317 ms with zero rows; no keyguard check, so this is not a controlled benchmark. Evidence: artifacts/android-app/codex-signing/s24-release-install.txt. No settings changed, downloads deleted, other apps touched, jobs added or real download tested. Full UI, TalkBack and download acceptance remain open. Astra code PASS remains separate from Fable review, which is OPEN.

## FABLE-CODEX-2 — Android auto-update

Status: OPEN for later Fable review. Implemented and published; Astra code audit PASS after main fixes and reverification, including release preparation. Owner: "implement update first then continue android.5" and "and then put the updatable apk on the desktop"; schedule "once a day or if the app is launched completely for the first time. (from being killed and opened etc.)"; auto-download and prompt install.

Review lifecycle and cancellation leases, daily job/process start, prompt and permission continuation, private cache and exact code/hash revalidation/retention, signed metadata and APK verification, feed redirect boundaries, and release preparation. Main fixes include per-caller cancellation leases, usable cached ready during refresh, exact selected-version sidecars and fallback, stale-selection preservation, handoff retention, and fixture transport handling. Check current code independently before comparing findings.

NativeAOT emulator signed baseline 1 → signed HTTP fixture 2 reached Ready, Android install permission, system update and opened code 2; warm resume did not recheck, force-stop/reopen did, and job 4001 forced twice completed. Full Mobile 373 passed (19 updater, 14 HTTP); pre-existing count guard corrected test-only with nine focused passes; full project rerun in progress. Local HTTP was test-only; adb-pushed external input was correctly blocked by Android permissions, not a production failure. Evidence: artifacts/android-app/codex-signing/updater-system-install-opened.txt, updater-schedule-launch-tests.txt, updater-arm64-release-build.txt, updater-release-prepare-final.txt and emulator-http-ready screenshot.

Desktop 0.0.2 APK SHA-256 ec52f1ff71bf5ea6d29af8f8764ae32dea01058e87e312af52929cd1caf42276 (20,135,037 bytes); local signed bundle artifacts/android-app/release-0.0.2 is unpublished. Production 404/no-release behavior is handled; no public newer-offer update tested. S24 remains on 0.0.1 at this checkpoint. Fable findings, fixes and approval remain pending.

### FABLE-CODEX-2 final validation update (2026-11-18)

- Desktop and S24 now run the updater release 0.0.2/code2, SHA-256
  ec52f1ff71bf5ea6d29af8f8764ae32dea01058e87e312af52929cd1caf42276.
  S24 install -r kept state; APK hash and launch/cold-check log verified. No phone
  permission or setting was changed. Production release publication still pending.
- Full .NET validation 6221 pass (+61 skips), extension339 +web717 and typechecks
  pass; existing warnings remain. Tests guard count correction and runner's locked
  Chrome dependency bootstrap are additional review scope (no prod serialization change).
- Prep script's first missing-Get-FileHash failure cleaned its private stage;
  .NET streaming SHA256 helper fixes that environment issue. Prepared bundle has
  signed Android metadata and checksums. Fable should review exact staged-byte
  verification/signing, atomic no-overwrite directory publication, and scoped cleanup.
- Later Fable verdict remains OPEN; Astra milestone pass is separately recorded.

### 2026-11-18 — Owner decisions after updater review

- Owner chose "Ask before sending cookies to the selected server" for ANDROID.5.
  Authenticated remote downloads must name the selected target and ask per download
  before forwarding credentials. Update B3's old phone-only words accordingly; do
  not imply remote cookies remain on the phone. Main owns this security-sensitive flow.
- Owner approved "Publish Android release and update feed" for the reviewed 0.0.2
  artifacts and android-app source. Commit/push the audited updater checkpoint and
  publish version-specific APK/metadata/checksums plus rolling Android manifest;
  keep desktop latest unchanged. This approval does not merge Android into main.

### FABLE-CODEX-2 publication update (2026-11-18)

Owner approved publication. Source d2e06d27 and CI guard 178fdb412196f76aa7fa3257a051cfd8f73c3a0c were pushed to android-app only; main unchanged. Prereleases [android-v0.0.2](https://github.com/Kaan88/inbound/releases/tag/android-v0.0.2) and [android-latest](https://github.com/Kaan88/inbound/releases/tag/android-latest) target 178fdb4 with latest=false. Public APK and metadata hashes matched ec52f1ff71bf5ea6d29af8f8764ae32dea01058e87e312af52929cd1caf42276 and bb5510151d7a5d696cdaa0371ecad844609df9b5a3b6fcc208562762db4a40a6. Android-tag Linux/Docker jobs skipped; desktop latest v0.9.0 unchanged. NativeAOT emulator checked real HTTPS feed and reported current (emulator-public-feed-before.xml); public newer-offer upgrade awaits a future release. Fixture 1→2 system installer flow passed. S24 updated 0.0.2 preserving data (s24-updater-install.txt). Final combined Astra PASS covers updater, preparation/helper, runner and persistence guard; Fable remains OPEN.

## FABLE-CODEX-3 — Android link catching

Status: OPEN for independent Fable review. ANDROID.5 source passed the Astra milestone audit and 0.0.3 was delivered locally; broader acceptance remains open. Review share intake, built-in browser, bounded page-link selection, expired-link refresh, and credential/referrer boundaries. Owner chose "Ask before sending cookies to the selected server": name the server and obtain per-download consent before transfer. Main owns security-sensitive backend/credential work. Native UI and Host integration are now present. Review the shared refresh transaction, filename reservation, stop/refresh synchronization and late queue-retry fix too. Current delivery and evidence are recorded below; no Fable approval is claimed.

### FABLE-CODEX-3 audit scope and evidence update (2026-11-18)

Remain OPEN. Review the added shared-runtime browser-refresh context and caller authorization/durability, name claims, observed referrer provenance and absent-referrer behavior, document/frame attribution, source/selection lifecycle, captured-admission idempotency and old-Host capability fallback. Owner's per-download remote cookie consent remains required. The first native signed-in file download matched its hash. Mobile 408 passed; the 309 Host/IPC tests predate the later runtime rewrite and the 13 audit-focused checks are not yet final. Initial Astra findings have fixes awaiting reverification; no milestone acceptance or Fable approval claimed.

### Signing backup verification (2026-11-18)

At the owner's request, the encrypted archive in the private secrets repository (blob 18161c733eefccc296f1d5add65f658e95233f82) was decrypted entirely in memory and SHA-256 compared against local inputs: Android release keystore, password file and update signing key all matched. No extraction, remote changes or secret values disclosed.

### Milestone audit frequency and gate correction (2026-11-18)

Owner: "use astra a bit less, on major milestones. this seems too much back and forth." Main finishes implementation and consolidated checks before each major-milestone Astra audit; followups verify confirmed fixes until PASS, not small increments. Fable-CODEX-3 remains OPEN. The initial global settings-gate wrapper delayed urgent Cancel behind QueueAction and failed regression; main is replacing it with per-job AddressMutationGate, with refresh taking settings then the job gate before persistence and PauseAll taking ordered job gates. Urgent Cancel is not globally gated. Verification is pending.

### FABLE-CODEX-3 test isolation follow-up

Owner observed desktop processes and notifications during Host/IPC regression tests. ReadSandboxHost uses a separate root and instance and disables completion notification/sound, but Host Program RemoteNotices failures and update notices bypass that completion toggle. Review test isolation and notification behavior later; no production quiet-mode change is included in this milestone. Windows GUI/notifications are not required for routine focused tests; the full Host suite is reserved for milestone validation.

### ANDROID.5 native and audit checkpoint (2026-11-19)

- Debug and NativeAOT x64 caught an authenticated fixture file with SHA-256 fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83. Native authenticated refresh reused the existing `auth(3)` job (total 7, Finished 7, Failed 0), with the same hash. Native picker/insets shown in `android5-native-links-final.png`.
- Debug Archives filtering excluded PDF/image; selected a.zip and b.pdf completed after ACK. Later auth1 stayed absent until Resume, then completed. Closing the browser during six-second metadata left Main on All five without a resurrected sheet. A subframe POST was logged by WebView with no Host GET or new job. Remote cookie consent named host and origin and warned about HTTP; cancel left server jobs at zero.
- The signed arm64 version-3 initial build matched the signing fingerprint but is stale after the latest Runtime fix: do not deliver it or treat its hash as final. Focused queue policy 10 passed; the exact old failure and two new regressions passed 3. Full Integration 2059 passed/32 skipped/1 failed was before the queue fix and is not a full green result.
- New confirmed Astra fixes reserve candidate final paths through commit and schedule when `AddressRefreshPending` clears, including failed jobs. Two deterministic tests are running; audit is not yet PASS. Fable-CODEX-3 remains OPEN. Common real-account sign-in compatibility, TLS, renderer, rotation, TalkBack and broader visual checks are not established. The controlled loopback fixture is now `tools/AndroidApp/link-fixture/`.

### ANDROID.5 audited fixture checkpoint (2026-11-19)

- Source audit: Astra final PASS after the reserved-name and refresh-wakeup fixes. The auditor verified both controlled tests force the original races and read the focused 16-pass log; this was source/read-only review, not an independent device run. Fable-CODEX-3 remains OPEN.
- NativeAOT authenticated refresh completed the existing `auth(3)` job: All 7, Finished 7, Failed 0, same fixture hash. GitHub's public sign-in form rendered in the Native WebView; no account or credentials were entered, so GitHub sign-in itself is unverified. Unknown, ambiguous or redirect-provenance downloads may need a manually supplied URL; observed POST and blob are not replayed as GET. No TLS bypass or browser-profile import.
- Core 2373, Engine 210, Mobile 408 passed. Full Integration 2059 passed, 32 skipped, one failed before the queue fix; afterward the exact failure plus two new regressions passed 3, queue policy passed 10, and reservation-focused checks passed 16. There is no clean full Integration rerun after that fix. The final phone APK and install are recorded in the 0.0.3 delivery entry (`android5-arm64-final.txt`, `android5-s24-install.txt`).
- ANDROID.5 source is audited and native fixture flows have evidence, but exhaustive acceptance is open: real accounts, broader light-mode, TalkBack and rotation checks. Do not mark the milestone fully accepted yet.


### ANDROID.5 0.0.3 delivered (2026-11-19)

- Final permanent-key arm64 APK: `artifacts/android-app/signed-builds/0b2a487b31e743d68d7402399642a7aa/Inbound-android-release.apk`, 19,986,573 bytes, SHA-256 `e1dc3d59cbf5d5f5ccf3518ebc4a69002a6ec416cdcf5fa3e8b68fa09cf93bc7`; certificate matched the pinned permanent identity. Release preparation verified version 0.0.3/code 3 and made signed local bundle `artifacts/android-app/release-0.0.3`, not published. Desktop copy `C:/Users/kaank/Desktop/Inbound-android-0.0.3-updatable.apk` matched the hash.
- S24 FE SM-S721B: pre-install check actually reported 0.0.1/code 1, despite earlier 0.0.2 notes; cause not established. The 1→3 `install -r` succeeded, preserving data without uninstall or settings changes. Installed APK hash matched, package reported 0.0.3/code 3, launch status ok, PID 5891, WaitTime 3033 ms. This is not a controlled performance benchmark. Evidence: `artifacts/android-app/codex-signing/android5-s24-install.txt`. GitHub release/feed remains 0.0.2; 0.0.3 is not published.
- NativeAOT picker and caught-sheet light-mode spot checks: `android5-native-links-light.png` and `android5-native-caught-light.png`; emulator app theme restored to Follows System. These do not establish exhaustive visual, TalkBack or rotation acceptance.

### Test cleanup (2026-11-19)

Stopped only the verified task-owned Python fixture and isolated Inbound server,
removed the task's emulator UI dump files, and closed the verified inbound33 AVD.
No test Host process remained in the Android worktree. The installed Windows Host
was left alone. The emulator app theme had been restored to Follows System;
no physical-phone settings were changed. Raw evidence remains under the ignored
artifacts/android-app/codex-signing directory; reusable fixture source is tracked.

### Recorded source checkpoint (2026-11-19)

ANDROID.5 implementation commit: `d0816724208a0ae543cf2d84a02e4ee32f218de2` on `android-app`.
The commit includes the shared-runtime fixes and regression tests audited with
the milestone; no merge to main. The 0.0.3 APK and signed local bundle remain
unpublished; public Android release/feed remain 0.0.2. Later Fable review is OPEN.
Final model result: `artifacts/android-app/codex-signing/android5-mobile-final.txt`
(408 passed); final controlled race checks: `android5-reservation-wakeup-tests.txt`
(16 passed). Full-run limitations remain recorded above.

## FABLE-CODEX-4 — ANDROID.6 queues, scheduler and settings

- Status: OPEN; implementation IN PROGRESS, local/uncommitted. Implementer: Codex.
  Later independent reviewer: Astra. No ANDROID.6 audit or acceptance is claimed.
- Owner direction (2026-11-19): proactively delegate bounded work using the
  current host-catalog model IDs/configuration recorded in the handoff; main Sol
  owns architecture, cross-cutting work and final integration, and checks delegated
  code and requirements before integration.
- Scope: build-plan step 5 and mockups C1/C2/C7. The owner chose per-queue
  Wi-Fi/unmetered-network, charging and battery-saver-off conditions, and
  per-queue aggregate speed limits. Every displayed setting must work on the
  phone; desktop-only settings must stay out. Exercise the phone Host and a
  selected remote Host while preserving the selected Host as the authority for
  host-owned queue/schedule settings; scope Android platform-only controls to the
  phone.
- Current local implementation (uncommitted, IN PROGRESS): selected-Host queue
  dashboard/editor and schedules; nullable shared per-queue unmetered-network,
  charging and battery-saver-off conditions; per-queue aggregate HTTP payload caps;
  Android power monitoring; persisted ordinary Android queue jobs with a revocable
  Runtime execution lease; compare-and-swap settings updates; working settings
  subsets. Queue selection/admission and Details assignment are being integrated.
  Remote torrent/rclone transfers are outside these policies.
- Background behavior decision: the owner asked which behavior would be best. Sol
  recommended and adopted Android-managed automatic best-effort jobs that may be
  delayed by Doze/battery management, without exact-alarm permission. This records
  Sol's recommendation, not an explicit owner choice of exact scheduling behavior.
- Sol read/reviewed all worker code, tests and docs and personally corrected the
  null-clear merge, stale `LastResult`, raw-action guard, Android bindings, stable
  card updates, encoded text and UI labels/touch/keyboard/discovery. Sol owns
  lifecycle, Runtime and IPC integration.
- Checks completed before the latest queue admission/UI/membership integration:
  Core 2373, Engine 213, Mobile 457; focused Runtime condition/lease checks 8;
  earlier queue-cap checks 3. Debug Android on dedicated inbound33 exercised a
  daily 09:00 schedule, start/stop, rotation and restored editor draft; C7 dark
  captures are under `artifacts/android-app/android6/`. The daily S24 FE data was
  untouched. Full Integration was still running; expected exact Hello capability/
  golden compatibility failures were being corrected. No full green result is
  claimed. Raw artifact details and final checks belong here as they complete.
- Pending acceptance: final NativeAOT and phone runs; selected remote-host behavior;
  Doze/battery behavior; TalkBack; theme and visual pairs. ANDROID.6 remains
  unchecked; no Astra audit has run and this entry stays OPEN.

### ANDROID.6 mockup alignment update (2026-11-19)

The owner asked whether the screens follow the mockup images. Sol acknowledged draft deviations in C1/C2/C7 (extra Edit buttons, a schedule selector and expandable settings) and committed to restore the pictured layouts with side-by-side pairs before calling them ready. Terra at high reasoning owns C1/C2 visual correction; Sol owns C7 correction and reviews the worker's code. For flows absent from the mockups, the owner directed the team to prepare additional mockups alongside the existing pictures in an HTML review board; main is preparing it. Implementation of undefined flows is paused until the owner reviews those mockups. Known C1/C2 layout corrections may proceed, and no undefined dialogs will be added before review. Focused capability/golden checks passed 14, ServerHello passed 1, latest Mobile passed 464 and Engine queue passed 4. Full Integration rerun is still running. No visual acceptance or milestone completion is claimed; ANDROID.6 remains OPEN for later Astra review.

### ANDROID.6 review-board and validation update (2026-11-19)

Update to the earlier in-progress test note: Full Integration passed 2,077, skipped 32 and failed 0 (`artifacts/android-app/android6/integration-final.txt`). The latest Android Debug build after C1 corrections/C2 safe-header passed with 0 errors and 2 known SDK warnings (`artifacts/android-app/android6/android-layout-check.txt`). The [ANDROID.6 flow review board](docs/plans/android-mockups/android6-flow-review.html), authored by Codex, has 28 proposed flows with the C1/C2/C7 originals embedded unchanged; Browser QA completed for all 28 flows at 320, 390, 736, 1024 and 1440 px: no horizontal overflow, clipped dialog/content or script errors; reference zoom opens and closes. Sol inspected all rendered proposals and Terra independently reviewed the contact sheets. Embedded references were verified byte-identical to C1/C2/C7. This is artifact verification, not device/accessibility acceptance. Sol reviewed worker source and removed an unwired C2 overflow control and static wrong title.

At the time of this checkpoint, owner review was pending; the owner's later approval and resumption of implementation are recorded in the newer entry below. No visual acceptance, Astra audit, ANDROID.6 completion or release claim is made; this entry remains OPEN.

### ANDROID.6 board approval and resumed implementation (2026-11-19)

The owner reviewed the 28-flow board and said "looks good," approving the proposed flows as design direction and resuming implementation. This does not accept implementation or close the later review queue. Terra owns C2 editor/dialogs and C7 grouped root/Details; main Sol owns queue actions, shared completion behavior and integration, and personally checks worker code. Future-capability items stay pending. Queue completion note: the existing queue `NotifyWhenFinished` preference controls a transient summary only; Android releases its background execution lease once idle for either choice. The queue remains ready, and neither choice exits the app/Host or shuts down the device. Full Integration passed 2,077, skipped 32, failed 0; latest Debug build passed with 0 errors and 2 known SDK warnings. ANDROID.6 remains IN PROGRESS; NativeAOT/phone/remote/Doze/accessibility/theme/visual acceptance and Astra review remain open.
### FABLE-CODEX-4 — ANDROID.6 latest flow and retry checkpoint (2026-11-19)

Implementer: Codex. Main Sol personally reviewed every worker change. The reviewed flow set covers C1 queue create/rename/delete/move, C2 sheets/times/limits/finish choice and supported C7 grouped pages. Shared protocol work adds `NotifyWhenFinished`, `RemoveSchedules` and `ExpectedQueueId`. Status remains OPEN; this is not feature acceptance.

Approved-flow focused checks: 6 passed. Deterministic retry-race cases for limits 1 and 2 failed on the old code (`failed` became `paused`); main fixed queue stop to preserve failed/cancelled terminal states and 11 related focused checks passed (`retry-race-before-fix.txt`, `retry-race-fixed.txt`). First consolidated Core 2,373 / Engine 214 / Mobile 464 passed. Initial full Integration: 2,081 passed, 32 skipped, 1 exact retry test failed. Fixed full rerun remains running (session 60371); Server consolidation not started. Approved UI/icons Debug build passed with 0 errors and 2 known SDK warnings; latest Done-button rebuild remains running (session 25681).

API 33 emulator exercised blank-name validation, stopped `Night-QA` creation, daily 09:00 schedule save, native time-picker/custom-500 draft across both rotations and restoration after free rotation. S24 FE untouched. NativeAOT, physical-phone, remote-Host, Doze, accessibility, visual acceptance and Astra review remain open. No ANDROID.6 completion claim.
### FABLE-CODEX-4 — ANDROID.6 consolidated verification and pre-audit candidate (2026-11-19)

Implementer: Codex. The later independent Astra audit is running (`android6_astra_audit`, requested gpt-6.2-astra/high); no audit PASS is claimed. Main will verify findings.

Final full Integration passed 2,085, skipped 32, failed 0; Core 2,373, Engine 214, Mobile 464 and Server 208 passed. Full Release solution build: 0 errors, 29 warnings. A second run exposed one checkpoint helper failure before corruption coverage. The controlled test reproduced the old helper returning an initial zero checkpoint while the nonzero save was blocked; main changed it to synchronize on actual Runner-idle state, retaining checkpoint/payload-prefix assertions. Focused checkpoint/retry coverage passed 7 (`checkpoint-wait-before-fix.txt`, `checkpoint-retry-final.txt`).

A signed NativeAOT arm64 production candidate passes `check-apk --release`, min API 31 / target 35 / arm64-only / non-debuggable. Path `artifacts/android-app/signed-builds/3e8a0b3ca7384b5d956da8e2d8e32d36/Inbound-android-release.apk`; SHA-256 `d85721f7a1f7f82dc4c7d753fc58828a527184f16c4092ee431963d93b99d00d`; 20,578,695 bytes. It is pre-audit and unpublished; a one-copy Android timing-note fix followed the build, so rebuild is expected. Separate NativeAOT test-x64 checker/install/launch passed on owned inbound33 API 33 (1,401 ms, not a benchmark). Paired isolated remote-host create/rename persisted; local queue state remained intact after switching back. S24 FE untouched.

Main's NativeAOT/background schedule QA continues. Physical-phone acceptance and further device/accessibility checks remain open. ANDROID.6 and this review entry remain OPEN; no publication or feature-completion claim.
### ANDROID.6 release request and audit-fix checkpoint (2026-11-19)

Implementer: Codex; current source is local and uncommitted. The owner asked, “could you make a release to github,” authorizing publication of Android 0.0.4 once the release artifact is verified. This is permission to publish after verification, not evidence that a release is built, accepted or published. Main Sol reviews worker code and requirements before integration.

The initial major Astra audit returned CHANGES REQUIRED with eight confirmed findings: queue wake starvation; weekday parsing of empty/full-name values; execution-lease stop paths; a delete-scope TOCTOU; reused queue IDs; stale remote CAS/admission state; uncertain recovery locking; and notifications for stopped jobs. Fixes for these findings are implemented; at this checkpoint the Astra follow-up was pending (see the later source-review update below). Focused Mobile checks passed 489 (`audit-mobile.txt`); focused identity/scope/golden boundary checks passed 8 (`audit-boundaries.txt`). These checks do not establish an audit PASS.

Earlier consolidated results are checkpoints only: Core 2,373, Engine 214, Mobile 464, full Integration 2,085 passed / 32 skipped / 0 failed, Server 208, and the full Release solution build with 0 errors / 29 warnings. They predate the current audit fixes and do not verify the final candidate. At this checkpoint, final builds, live/device acceptance, Astra follow-up and GitHub publication remained pending. The later independent review queue remains OPEN, separate from the milestone audit; no milestone completion or release publication is claimed.

### ANDROID.6 Astra source-review follow-up (2026-11-19)

Astra's follow-up on the same major milestone recorded SOURCE PASS: all eight original findings are resolved, with no blocker in source review. The follow-up also reviewed stable migration of missing/empty queue generations, remote compare-and-swap identity gates, and the atomic torrent-completion fix. The final Mobile suite passed 505 (`artifacts/android-app/android6/audit-mobile-final.txt`).

The migration and completion regression tests have been source-reviewed but not yet executed. An initial full Integration rerun has one failure in completed-torrent readiness; main fixed the issue and Astra reviewed the source fix as PASS. The final Integration rerun, Release build, and device validation are pending. This source-review PASS is not a full milestone or release PASS. The later independent review queue remains OPEN, and no feature completion or GitHub publication is claimed.

### ANDROID.6 public APK delivery (2026-11-19)

Implementer: Codex. Android 0.0.4 published (2026-11-19, Codex): owner requested immediate APK publication ("just put the apk already"). Source commit b6c2f6668378c0fc0b16c8be5fa5f92397dd2ba7 is pushed to origin/android-app; no merge to main. Public prerelease https://github.com/Kaan88/inbound/releases/tag/android-v0.0.4 contains the permanent-key NativeAOT arm64 APK, signed manifest and SHA256SUMS. Both releases use latest=false; desktop latest remains v0.9.0. The android-latest feed now serves the identical 0.0.4 manifest. Public APK bytes and both manifest copies were downloaded and matched local checksums: APK cd49d3e2b0b3f729752642934a29f2a5ae4b577e3e50486a7b5107d0fe6bff65 (20,599,175 bytes), manifest 3343eabef3a601c116325203c366c210eb5eea64a104ad184f1bbbd248b5f979. Release/signature/API31/target35/ABI/no-test-hook checks passed. Final solution: 0 errors, 23 warnings; Mobile505, Core2373 and focused final Integration10 passed; remaining broad suites still running at publication. Astra source audit PASS. Final NativeAOT emulator install/launch preserves data; S24 is not attached. ANDROID.6 physical-device/accessibility and final broader acceptance remain OPEN; publication does not complete the milestone or later independent review queue. Evidence: artifacts/android-app/android6/final-production-clean-build.txt, final-release-prepare.txt, final-solution-build.txt, final-regressions.txt, audit-mobile-final.txt and public-verification/.


### ANDROID.6 updater prompt and changelog work (2026-11-19)

The owner set the next goal: “finish milestones as you go. and implement a bit nicer update prompt with changelog.” The new [Android update-prompt review board](docs/plans/android-mockups/android-update-prompt-review.html) uses the approved C7 reference. Main reviewed the compact ready dialog in dark/short and light/long variants; 72 rendered combinations had no overflow or script failures. The owner explicitly said, “Use the proposed dialog,” approving this design direction and the same notes in Settings. This is design approval, not UI acceptance.

Implementer: Codex; Android 0.0.4 at `b6c2f6668378c0fc0b16c8be5fa5f92397dd2ba7` is already published and immutable. The nicer prompt/changelog work is a separate implementation in progress and is not in 0.0.4. The Android updater is being extended to carry separately signed notes while preserving the existing v1 manifest signature for older clients, following the desktop updater pattern; note text is bounded to 8,192 characters with fallback for older feeds. Mobile tests passed 526 (`artifacts/android-app/android6/update-notes-mobile.txt`); signer tools built with 0 warnings and 0 errors (`artifacts/android-app/android6/update-notes-signer-build.txt`). These results do not establish UI acceptance or a new release.

For ANDROID.6, Core 2,373, Engine 214 and Server 208 passed; the full Integration run remains in progress. ANDROID.6 remains IN PROGRESS and this later independent review remains OPEN. Physical S24 acceptance is unavailable because the phone is not attached; emulator5554 is the only connected device. No full milestone completion or update-prompt release is claimed.
### FABLE-CODEX-4 — ANDROID.6 current-source follow-up (2026-11-19)

Implementer: Codex; current 0.0.5 source is local and uncommitted. The owner approved
the proposed update dialog as design direction. Signed-notes Mobile passed 528
(update-notes-mobile-final.txt); signer build had 0 warnings/errors. Full Integration
passed 2,095 / 32 skipped / 0 failed (final-integration-full.txt); Core 2,373,
Engine 214 and Server 208 are accompanying prior consolidated results. Main reviewed
all delegated source.

A dedicated API 33 NativeAOT stop observation (stop-identity-after.json) recorded
stop reason 3, detached=true, queued state, queue 2 still running, queued membership
retained, no user pause, a stable 897,859 / 1,048,576-byte checkpoint over two seconds,
and an empty crash-marker session. This observation applies to that test-build
revision only; it does not establish reboot, Doze, network-change, or physical-phone
acceptance. That build included JobRunIdentity and the owner-requested crash-guard
changes, but not the newest callback ownership, API 30 StopReason, and picker fixes.

Astra's new source audit identified those three findings. Main and Luna have made
source fixes; the Astra follow-up remains pending. The newest Debug compile is still
running after binding corrections. Tests/builds for all latest changes, final
NativeAOT/arm64 rebuild, and device acceptance remain pending. Public Android 0.0.4
is immutable; 0.0.5 is unpublished. ANDROID.6 stays IN PROGRESS and this later
independent review stays OPEN; no milestone or release PASS is claimed.
### FABLE-CODEX-4 — Astra follow-up and stop-resume update (2026-11-19)

Update to the preceding checkpoint: Astra follow-up recorded SOURCE PASS for all
three source fixes. This is not milestone completion and the later independent
review queue stays OPEN. The picker Debug build passed 0 errors / 5 warnings
(picker-debug-build.txt); a final rerun remains in progress with explicit
e.Handled=false and its documentation-comment fix (picker-debug-final-build.txt).

After the stop observation, the same job resumed only after main explicitly forced
ordinary job 4100, then completed 1 MiB. Its payload hash
fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83 matched the
fixture (stop-resume-payload-sha.txt). This is not evidence of automatic resumption,
reboot/Doze behavior or physical-phone acceptance. Android 0.0.5 remains unpublished;
ANDROID.6 stays IN PROGRESS with final rebuild/acceptance pending.
### FABLE-CODEX-4 — modal picker SOURCE PASS and Debug checkpoint (2026-11-19)

Implementer: Codex. Astra follow-up recorded SOURCE PASS for the modal picker after
main read the full worker change and added event unsubscription before disposal.
On API 33 Debug, keyboard Tab/Enter/Down/Enter selected `Night-QA`; delayed metadata
(6 seconds), landscape rotation and portrait restoration kept that selection, and
submission persisted `slow (6).zip` as queue 2, `IsQueued=true`, start mode `queue`
(picker-modal-keyboard-result.xml, picker-modal-metadata.xml,
picker-modal-landscape.xml, picker-modal-restored.xml,
picker-modal-admission.jsonl). Explicit Pause persisted `PausedByUser=true` at
12,806 bytes.

The latest picker Debug build passed with 0 errors / 2 warnings
(picker-modal-final-build.txt); the full solution build passed with 0 errors /
53 warnings (update-prompt-solution-final.txt). Mobile 528 and release-tool 10 are
prior final results. A clean NativeAOT x64 rebuild is still running
(updater-picker-native-clean-build.txt); no NativeAOT PASS is claimed. An earlier
incremental NativeAOT artifact failed the provider checker and was quarantined,
not installed or distributed. Android 0.0.5 remains unpublished, ANDROID.6 remains
IN PROGRESS, and the later independent review remains OPEN.

### FABLE-CODEX-4 — Android 0.0.5 publication checkpoint (2026-11-19)

Implementer: Codex. Source commit `87555b993ae8f3de7e61a171c87a932a8d6a4a2b`
was pushed to `android-app` and published as the public Android 0.0.5 prerelease:
[android-v0.0.5](https://github.com/Kaan88/inbound/releases/tag/android-v0.0.5).
The permanent-key NativeAOT arm64 APK is 20,664,711 bytes, SHA-256
`1cf087a69c56744e1ee7b0bd5e3c29bce1fe7cad5db371f78ada35fac8805691`; the signed
manifest SHA-256 is `a3b4500e68aeb7a59251a54121cc18d38a98d3cb2c65ae224d158a4d8ec3459c`.
Published APK, manifest and SHA256SUMS matched local bytes; android-latest serves
the same signed manifest containing 407 characters of release notes. Both prereleases use `latest=false`;
desktop latest remains 0.9.0 and Android was not merged to main. Certificate pin
is unchanged. Release checks covered min API 31, target 35, arm64, non-debug/no
hooks, provider, TLS and icon. Main reviewed source and artifacts. Astra source audit
passed the confirmed fixes; Mobile 528, release-tool 10 and solution build (0 errors,
53 warnings) are recorded in the release evidence.

Final NativeAOT API 33 checks recorded a detached stopped job retaining a partial
20,520-byte checkpoint in queued queue 2, still running and not user-paused, with an
empty crash-marker session observed for two seconds. One injected native crash was
recovered after an explicit forced job run; the download resumed and completed with
fixture SHA-256 `fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83`.
Final crash-marker state was count 0/session false and the ongoing notification was
absent (`final-0.0.5-native-stop.json`, `final-native-crash-recovery.json`,
`final-native-recovery-payload-sha.txt`). Native keyboard selection/restoration was
checked in portrait after a landscape cycle; landscape selection itself was not
directly asserted. Short and no-notes update previews were viewed; 0.0.6 was only an
illustrative preview and was not installed.

These checks do not establish physical-phone/TalkBack acceptance, natural scheduled
wake, or reboot/Doze/network-change acceptance. Physical phone was unavailable;
broader remote-host acceptance remains open. ANDROID.6/ANDROID.7 and the later
independent review remain OPEN; publication does not close a milestone or review.

Cleanup verified: task queues stopped; emulator theme restored to Follows the
system, battery reset, font_scale absent, free rotation and non-root adbd. The
owned fixture and emulator were stopped; S24 stayed disconnected and untouched.
Main also viewed the final NativeAOT dark-dialog preview (`update-final-native-dark.png`).

### FABLE-CODEX-4 — remote queue rate consistency checkpoint (2026-11-19)

Implementer: Codex. The owner requested normal hardware/emulator operation; QA
verified the inbound33 NVIDIA 4070 Ti Super host. An isolated copy of the server
binaries ran against the fixture on 127.0.0.1:47616. Remote UI created queue
`Remote-005` (ID 2, generation `80187f31-53db-4165-a33d-c0f7678e9734`) and schedule
`Evening-005` (ID `ac4c2b12-f061-457d-849b-78911d0f15fb`, daily 23:58, all days),
with concurrency 2. The remote edit and server restart/reconnect preserved queue,
schedule and Host settings; the local settings SHA-256 stayed identical at
`a0b603b23f2fc210ce9e136c76782c972397b12c89dbdcfbc948480c7d194a94`.

The remote workflow exposed inconsistent speed units: old input `64` stored 65,536
B/s but the queue card showed 65.5 KB/s. Main chose decimal KB/s input semantics for
both queue and Host limits, matching shared rate formatting; new input `64` stores
64,000 B/s and `128` stores 128,000 B/s. Existing raw 65,536 B/s values are preserved
without migration. Terra gave a scoped source PASS before main added the final exact-
long `Fmt.Rate(long)` overload to avoid lossy conversion; main reviewed that overload
and the complete change. This checkpoint is not an Astra audit of the new rate fix.

Current Mobile 528 Debug checks passed; Debug build passed with 0 errors / 36 warnings,
and Release solution build with 0 errors / 87 warnings. Clean NativeAOT x64 packaging checks passed (21,647,750 bytes); installed candidate
0.0.6 saved 32 KB/s as 32,000 B/s and 256 KB/s as 256,000 B/s, matching the UI.
Permanent-key arm64 packaging checks also passed (20,664,711 bytes; SHA-256
`30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`). Android 0.0.5 remains the public release; version 0.0.6 is local,
uncommitted and unpublished. Artifacts are under
`artifacts/android-app/android6-acceptance-0.0.5/`, including
`legacy-limit-preserved.json`, `host-isolation-and-rate-result.json`,
`server-restart-persistence.json`, `rate-fix-queue-saved.xml`,
`remote-global-saved.xml`, `local-after-remote-edits.xml`,
`rate-fix-mobile-final.txt` and the build logs.

Broader remote workflows, physical-phone/TalkBack acceptance, Doze, network and
reboot checks, ANDROID.6/ANDROID.7 milestone acceptance, and the later independent
review remain OPEN. This scoped source/UI/fixture result does not close them.

Main verified the final source diff and Terra scoped follow-up PASS. Remote schedule
editing saved Mon/Wed 23:58 to 23:59; the empty task queue and its schedule were
then removed through the UI, and the remote global limit restored to None. Final
local settings hash still matches the baseline (`native6-cleanup-result.json`).
Hardware emulator remains visible with host GPU rendering, as requested.
