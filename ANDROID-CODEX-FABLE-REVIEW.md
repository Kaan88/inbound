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
- Baseline: android-app at 49f5663c. Current implementation is local/uncommitted;
  update with the final commit if committed later. No merge or public release.
- Scope: scripts/build-android.ps1, tools/AndroidApp/release-apk.py,
  tools/AndroidApp/test-release-apk.py, .gitignore and the signing documentation.
- Owner direction: sign the next APK with the already-created permanent key;
  one-time reinstall replaces debug signing. The owner identified Kaan88/secrets
  and the local encryption-key location. Its README was read through GitHub.
  Existing local signing inputs were present, so no archive was decrypted and
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

Status: OPEN for later Fable review. Implemented locally; Astra code audit PASS after main fixes and reverification, including release preparation. Owner: "implement update first then continue android.5" and "and then put the updatable apk on the desktop"; schedule "once a day or if the app is launched completely for the first time. (from being killed and opened etc.)"; auto-download and prompt install.

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
