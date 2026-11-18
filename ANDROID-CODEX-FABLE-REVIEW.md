# Android Codex work — later Fable review

Status: OPEN. Requested by the owner on 2026-11-18. No Fable review of the
Codex continuation has happened yet. Astra audits milestones now; Fable reviews
this recorded work later. Keep findings/fixes/final approval distinct.

Read CLAUDE.md and ANDROID-CODEX-PROGRESS.md first. The earlier Claude work and
its completed reviews remain in docs/plans/android-fable-review-queue.md.
This queue covers new work after the Codex takeover; it does not reopen or
claim approval from the previous Fable agent.

## Review procedure

For each entry record scope and commit (or explicitly local/uncommitted), why it
matters, validation actually run, raw evidence, Astra findings/fixes/verdict,
remaining device checks, and Fable's eventual findings/fixes/approval. Give Fable
the code and requirements for an independent read before the previous findings.
Do not mark an entry reviewed merely because Astra passed it. Add material new
changes and decisions as work proceeds; update checkpoints before handoff.

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
