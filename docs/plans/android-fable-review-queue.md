# Android app: reviews for Fable to check

Current state and next work: the [Android handoff](android-parity-handoff.md).

Owner, 2026-11-16: "when, not if, fable hits a limit - use opus, but write notes for fable so that it can check when my
limit resets."

The build plan (section 2) asks for one standing Fable reviewer. When Fable is out of quota, an Opus agent reviews
instead and the review is written here. When the owner's limit resets, send Fable every open entry (the code it names,
not the Opus findings first, so its read is its own), then compare, act on what holds, and mark the entry done.

Each entry: date; what was reviewed (commit, or files if uncommitted); who reviewed; findings and what was done; what
Fable should look at.

## Done: merge of main (0.9.0, SITES.13/14) into the Android branch (2026-11-23)

Fable reviewed `7941d412` (2026-11-23): not approved as it stood, one medium-high finding, five lows; everything else
checked and found correct. Fixed before Android 0.1.3:

- M1: `SiteStore.SaveAsync` gave every write a new `Revision`, so main's background writes (the last-used stamp after a
  first page, `sites.remember`, a sync schedule) made the listing just returned stale: delete, "load more", a cached first
  page and a no-replace upload could be refused as changed. Those writes now keep the revision (`keepRevision`).
  Test `ALastUsedStampAfterTheFirstPageKeepsItsListingUsable` fails without the fix.
- L3: messages branch on the condition that failed (draft: user changed or other sign-in setting; a key not carried to
  another server says "Choose the private key to sign in with.").
- L4: a passphrase typed for a kept unencrypted key now clears a stored one ("" instead of null).

Left, recorded: L2, copying into the folder the files are in is refused (main's rule; Android 0.1.x allowed it for files
with Keep both; said in the 0.1.3 notes). L5, `sitesRemoteCopy` now means `between` with legacy `copy` accepted; a
0.9.0 desktop Host takes `between` but does not advertise it, so the phone hides copy against it until main's next
desktop release. L6, the PuTTY conversion in `sites.testDraft` runs before `BuildRecord`'s checks (bounded by `_keyGate`).

What Opus asked Fable to check:

Owner, 2026-11-22: merge main into this branch, then this branch into main. Resolved by Opus, not reviewed. The two
branches had each built the same two features; review the merge commit's resolution in these places:

- Copy between remotes: main's `between` (SITES.13, `TargetSiteId`/`TargetFolder`) is kept; this branch's `copy`
  (ANDROID.8 r6, `Destination*`) is gone from the engine. `SiteTransfers.StartAsync` maps an Android 0.1.0 to 0.1.2
  request (`copy` + `Destination*`) to `between`, and `SiteTransferStore.LoadAll` migrates stored `copy` batches
  (`Target* ??= Destination*`). The per-file tail of `MoveAsync` builds the copyfile call in one `CopyTo(name)` used for
  both the safe single download's staging name and the normal name (`fs`/`local` cloned per call). Between records are
  written with schema 3. The Android client now sends `between`; its titles still say "copy".
- PuTTY keys: this branch's `PuttyKeyFile` (Argon2, format 3 with passphrase) is kept and main's `PuttyKey` deleted; its
  generated-key tests now run against `PuttyKeyFile` (`PuttyKeyFileGeneratedKeyTests`). The conversion is
  `SitesCommands.OpenPuttyKeyAsync`, called by both `sites.save` and main's `sites.testDraft` before `BuildRecord`.
- `BuildRecord` (main's, shared by save and draft) gained this branch's review S3 rule: a saved password or key is kept
  only for the same user, sign-in method and WebDAV vendor / S3 provider and region (`sameSignIn`), a key's kept
  passphrase does not follow a new key, and a passphrase typed for an unencrypted kept key is dropped. Check that main's
  desktop rule (a key may follow its remote to another server when not `OwnSignInOnly`) still holds with it.
- `SiteRecord.Revision` (this branch) is set on every `SiteStore.SaveAsync`; main's new SiteRecord fields kept.
- `ConcurrencyLimits`: this branch's preemption loop with main's `_savingHold`.
- Hello capabilities: this branch's site capabilities kept beside main's `savingPauses`; `TestDraft`, `Preview`,
  `SyncPlan`/`SyncRun` and `SafeDownloadStart`/`DeleteFiles`/`DeviceCreateOpen` all in the remote-session lists.

## Done: fixes released in 0.1.1 without Fable, 41ef14a8..5aa45d0a (2026-11-22)

Fable reviewed this entry and 0.1.2's `d1e414fb` on 2026-11-23: approved, three lows (fixed in `baf51405` and
`94faa3f5`; PROGRESS "SITES.10 (1)-(3), Android review fixes").

Owner, 2026-11-22: "no fable, release", then "note needed fable review for later". Fable approved the 0.1.0 code at
`41ef14a8`; the commits after it shipped in 0.1.1 unreviewed (a Fable review was started and stopped by the owner).
Review `git diff 41ef14a8..5aa45d0a -- src tools`:

- `2410c669`: crumb text colour per the mockups' --chiptext; `RetryAfterSignIn` retries only a transfer still listed as
  failed (Fable N1). Check the guard when `_transfers` is stale or not loaded.
- `c83735f1`: Transfers empty state (x4) placement and colour; `RemoteTransferText` reason trimming; a fixture time.
- `e50146b7` (found on the S24): `OpenTransfers` now calls `Render()` (the Transfers row did nothing until a redraw);
  `RefreshGap` tracks the last target per scroll view and cancels the running slide (a quick load left the list 56 dp
  down). Check `Render()` from the "View transfers" snackbar while a folder or form is open, and the gap fields when the
  Transfers chrome is rebuilt.
- `5aa45d0a`: version 0.1.1 (versionCode 13) and `docs/android-release-notes-0.1.1.txt`.
- `d1e414fb` (0.1.2): `UpdateReadyDialog` notes as BulletSpan paragraphs with a small blank line between them and a
  notes height of up to 45% of the screen; `app_updates.xml` progress bar without the stop dot and gap. Check the
  formatting with notes that hold blank lines, leading bullets or one very long line, and the Settings page that shares
  `CreateNotes`/`BindNotes` (the Tag cache).

## Done: ANDROID.9 and the speed fixes, 772cd2c0..8d26b536 (2026-11-22)

Two Fable agents reviewed the range and then approved every fix and follow-up (`65264734`, `82c551c3`, `6a0d25c3`):
[android-review-android9.md](android-review-android9.md). The fixes for the earlier range's S1-S3, M2, M3, P1-P5,
A1, A2, A8 and S6 were approved in the same pass.

## Open: Android work from 49f5663c to c745317f (2026-11-21)

**Fable, 2026-11-23:** the fixes for this range's findings are right; one new medium (F1, SFTP MoreKeys offered when
a trusted key was gone) fixed in `d86960f1`. Owner decisions, 2026-11-23: R2 (rclone can outlive a killed app on Android) is left as it is,
noted as a known limitation; the MoreKeys sheet stays as x26b, without the administrator tick. Closed.

**Reviewed 2026-11-21:** four Opus agents and then Fable reviewed this range; findings, verdicts and the fix order are in [android-review-49f5663c-c745317f.md](android-review-49f5663c-c745317f.md). It stays open until the findings are fixed and Fable checks the fixes.

Added 2026-11-21 when Claude took the Android work back. Commits `49f5663c..c745317f` (136 commits, 0.0.2 to the 0.0.10 hotfix) were made outside Claude sessions and have had no Fable or Opus review. Earlier milestone verdicts recorded for them in PROGRESS and the matrix came from another reviewer and are input, not acceptance. Review the code and requirements first; use `git diff --stat 49f5663c..c745317f` and `git log --reverse 49f5663c..c745317f`. Review shared Runtime, Contracts and Sites changes for desktop and server impact as well as Android behaviour. The detailed earlier review notes are in git history: `git show c745317f:ANDROID-CODEX-FABLE-REVIEW.md`.

| Work since baseline | Commit checkpoints | Evidence and review focus |
| --- | --- | --- |
| Permanent signing and Android updater, 0.0.2 | `d2e06d27`, `178fdb41` | signer identity, fresh build provenance, secret handling, signed feed and user-approved installation. |
| Link catching and authenticated refresh, 0.0.3 | `d0816724`, `50df32b8` | credential boundaries, durable refresh, consent, Host isolation and intake. |
| Queues, schedules, settings and execution, 0.0.4–0.0.6 | `b6c2f666`, `87555b99`, `edd073b0` | shared Runtime safeguards, execution leases, signed changelog, decimal speed limits. Review native and Host behavior together. |
| Core screens, HTTPS pairing, phone/background acceptance and 0.0.7 | `c694d0fa`, `d82cd893`, `00d26b52`, `563fad73`, `92defa31` | [PROGRESS](../../PROGRESS.md) records S24 hashes, reconnect/pause/resume, public in-place update and visual acceptance. ANDROID.4/.7/.10 closures retain their scope; remaining real-account capture and natural Doze moved to ANDROID.14. Interrupted/optional soaks are not full acceptance. |
| Bundled rclone and Remote Files, 0.0.8 | `15123d61`, `c446b39d`, `f4bbd735`, `60ed93ce`, `6f6a0279`, `577c4422`, `66270193` | Review executable packaging/pinning, 16 KiB alignment/device evidence, process and loopback lifecycle, provider credentials, trust, browse and transfer scope. [Matrix](android-mockup-acceptance.md) and PROGRESS retain evidence. Production phone-originated upload remains separate. |
| Mockup parity across implemented screens, 0.0.9 | `f3707bc4..d053242e`; publication `03572c06` | [Matrix](android-mockup-acceptance.md) is the per-case record. Includes download/intake/bulk/notification flows, queue editor and live limits, pairing/consent, cloud recovery, C3 tracked mixed transfers, C4 controls and Settings/update prompt. Final Mobile 567 and harness 66 passed. An earlier non-Fable review passed source, visuals and the four flow batches; the owner has since rejected the Remote Files tab (2026-11-21), see ROADMAP ANDROID.8. |
| Phone browse hotfix, 0.0.10 | `5c12a1af` | Managed HTTP for Android rclone loopback cancellation; explain skipped downloads under no-replace. Validation: [PROGRESS](../../PROGRESS.md#remote-files-on-the-owners-galaxy-s24-fe-2026-11-20). |

Signing (d2e06d27, 178fdb41), what to check:

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

## Done: steps 2 to 6 and the first APK (2026-11-17/18)

Fable reviewed what was still open here on 2026-11-23: `IsUserStart` and the CA-trusted https case closed; one new
medium-low (F2, other user starts did not lift a tripped crash guard) fixed in `baf51405`. Two lows left for later: the
Keystore read in `MainActivity.OnCreate` on the UI thread, and `InboundBackground.Evaluate`'s lock scope.

Owner, 2026-11-17: "from now on, hold fable reviews until completion. and use opus for reviews." Opus agents review
each slice and step; Fable reviews everything below once the Android work is complete.

- Step 2 from S2 on: 27cdcbb (keep-alive, notifications, wake lock, user-stop detection), 63b860f (device results), and
  every later step 2 commit (S3 on). Fable had seen S0, the spike, 9f02bcd and the design up to section 8; the S2 review
  was stopped unfinished when the owner gave the rule above.
  Step 2 closed 2026-11-17 at b2e5b2a: S3 (1a8da86), S5 (e9b91e4, 665729f), the Opus milestone review's fixes and the
  owner's S24 reports (46fb9bf, 05b6009, f4f65c0), same-name destinations (1532aa8, Runtime, desktop too). The Opus
  review's full result (15 confirmed, 6 refuted) is summarised in PROGRESS; Fable should look hardest at InboundBackground
  and Services (lifecycle), ResumeList, and the Runtime changes that ship on Windows.
- Step 3, core screens (ANDROID.4): 1eefe88 (screen logic in Mobile), 815c99ac (views, themes, motion, back), merged
  a3834898. Built by Sonnet agents (one stopped by accident and restarted); checked on the API 33 emulator only.
  Look at: MainActivity and Navigator lifecycle (recreate, process death, the step 2 Resume guard after the rewrite),
  selection and delete-with-file, motion with "Remove animations", TalkBack labels.
- Step 6, other hosts (ANDROID.7): 003c335 (RemoteHostConnection, host list), 06d219c (switcher, pairing, strip),
  c18c8ef5 (owner's answers: certificate pin at pairing, one-shot figures), integrated into step 3's screens in
  425d72d1. Security-sensitive, look hardest at: Contracts/ServerCertificates.cs (shared with the desktop D9 client:
  Accept, the per-pin HttpClient cache, the probe handshake), pairing and unpair in HostDirectory, KeystoreSecretStore,
  that no password or token reaches a log, file or backup; and RemoteHostConnection's reconnect diff and pending
  requests.
- Follow-ups after 425d72d1: fc43614d (short failure reason, Open on the progress notification and grouped Finished
  notices with Open through the FileProvider, server folder chooser over `folders.browse`, the server's own name in the
  pairing answer: ServerAuth.cs and RemotePairResult, middle-cut Save to), merged with the review fixes in 8f6c327b.
- The Opus milestone review of steps 3 and 6 (workflow, at 425d72d1): 28 confirmed, 7 refuted; all 28 fixed in
  e37cbad2 (branch `android-fix36`, PROGRESS.md "milestone review fixed"). Look hardest at: Navigator (page tokens,
  see-through overlay, accessibility hiding), RemoteHostConnection's keep-alive and reply-timeout drop, the shared
  Contracts change (keep-alive parameters, certificate words by pin), HostEntry.ConnectionStamp and the page restore.
- First S24 use against the owner's desktop Inbound (2026-11-17): af7f65c9 (summary rate shown once a second, not
  animated) and 9a6ec0ef (HTTPS in the NativeAOT build: the csproj keeps and exports
  `Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate`; every https download failed before).
  Look at: whether the exports target is robust (incremental builds, x64, a future SDK that exports it itself) and
  whether any other JNI callback of the runtime is missing the same way.
- The first usable APK, on the owner's desktop 2026-11-17: release build of 9a6ec0ef, arm64, min SDK 31, target 35,
  signed with the debug key (no release key yet), no test hooks in the binary.

Owner, 2026-11-17: "after first version finishes, have it review." Fable reviews everything above as soon as the first
usable APK is on the owner's desktop (not only after all milestones).
Owner, 2026-11-17: "from now on, have fable review fixes approved by same fable agent." The fixes for Fable's
findings go back to the same Fable agent for approval (record its agent id here when it starts).
Fable review of the Open entries started 2026-11-17 at 7573792b (worktree `idm-android-fable`); agent id
`a77883b7d95665eea` (continue it with SendMessage for fix approval).
Fixes for its 16 findings, 2026-11-17: 0f535a9f (Runtime: failed downloads free their name, owner's "Free the name"),
d51d5e4b (TLS export target fails loudly, post-link check, gate https probe), 7e3f69d7 (the app and Mobile fixes), with
the emulator https pass and per-finding notes in PROGRESS "Fable review fixed". Waiting for the same Fable agent's approval.
Fable approved 15 of 16; finding 6 second pass: 6b8a5be8 and e75fc325 (every requeue path claims the name in one lock, no part
file move). Waiting for Fable's approval of finding 6.
Owner, 2026-11-17: "update my phone and desktop apk after this fable approves": once Fable approves the fixes, install
the release build on the S24 and replace the APK on the owner's desktop.
Approved by that agent 2026-11-18 at b104f6eb (all 16 findings; PROGRESS "Fable approved the fixes"); S24 and desktop
APK updated the same night. Still open from it: the CA-trusted https case, the Keystore read in OnCreate and
InboundBackground.Evaluate's lock scope (low), `queue.action` start and `group.action` resume in `IsUserStart` once
the phone has those controls.
After the approval (2026-11-18): the launcher icon and `tools/AndroidApp/check-apk.py` (stale-obj crash of the release
APK, PROGRESS "Release APK crashed at start"); Fable has not seen them.

## Done (Fable reviewed it)

- **2026-11-17, step 0 and step 1 final, reviewed together by Fable** (owner: "together"). Fable read the code first,
  then the Opus entry below. It agreed with Opus on 1 to 7 and 9 and with rejecting 8 (adding that after a start that
  failed post-initialize, shutdown gives up on a runner after 10 s without throwing, so "never a second writer" is not
  strict; very unlikely on a first run). Its own findings, checked in the code and fixed in aaaced8 unless said: stale
  progress undoing a newer state (fixed: `UpdatedAt` per row, 5 s window for a clock set back); a ghost row from a
  progress frame for a job removed before the snapshot (fixed: progress never adds a row); the default folder after a
  half-done first start (fixed: decided by the setting); the gate's lock check failing open (fixed); no NativeAOT or
  commit check in the gate (fixed); a command after dispose reaching the Host, a failed open leaving a session, recycled
  rows still watched (fixed); events deserialized on the Host's thread before coalescing (kept: cheap per event; a later
  step can coalesce raw frames); PROGRESS overclaims on warnings and tests (reworded, outputs saved); step 0 docs:
  deferred list, the Rust decision's conditions, the owner's words, stale text, no ROADMAP line for step 1 (fixed).
  Judgement asked of Fable: no interleaving left delivers a frame to a closed screen or leaves a session registered;
  a removed row cannot come back within a session.

- **2026-11-17, step 1 final (the step-1 commit on `android-app`).** Owner, 2026-11-17: "fable can't be used. make a
  note for later." So the final step-1 review is Opus's. Fable already reviewed the code twice before this (see Done);
  since its second review these changed, and Fable has not seen them:
  - `src/DownloadManager.Android/Host/InProcessHostConnection.cs`: `SendDirectAsync` no longer catches a callback that
    throws (the snapshot failure fails the open); `Deliver` still catches for events.
  - `src/DownloadManager.Android/Host/AndroidHost.cs` and `InboundApplication.cs`: `HostLeftRunningException` when a
    failed start could not be shut down; the cached host task is then kept, so no second start in that process.
  - `src/DownloadManager.Android/MainActivity.cs`: start-failure Snackbar and "Try again"; nullable-correct insets listener.
  - `tests/DownloadManager.Integration.Tests/InProcessHostConnectionTests.cs`: the rewritten overlap test, the throw-on-
    snapshot tests.
  - `tools/AndroidApp/run-gate.sh`: server-byte proof for step 4, MARK2 timing, keep-awake paused during cold starts.
  Fable's own open points from review 2 not acted on: point 5 (the lazy connection's dispose window) and review 1 point 8
  (a late event bringing back a removed row). Ask Fable to judge those again.
  Opus findings (2026-11-17) and what was done, checked by me in the code before acting:
  1. A late progress event could bring back a removed row (the Host announces progress outside its lock); in one batch it
     overwrote the pending removal. Held. Fixed: `DownloadsModel` keeps the removed ids (ids are never reused: every job
     gets `Guid.NewGuid()`) and drops later events for them, at arrival and when a batch is applied. The old test
     `A_removed_job_that_comes_back_is_a_new_row` encoded the opposite and was replaced; three tests added.
  2. A dispose between the lazy connection creating the inner one and the inner open attaching its callback let the open
     re-attach it, so hello, snapshot and held events reached a disposed screen (no session leaked). Held. Fixed:
     `InProcessSession.Attach`/`Close` under its lock; an open after close does nothing; the model also ignores frames
     once disposed. Tests: `A_connection_disposed_before_it_is_opened_stays_closed` (reads the dispatcher's session
     table), two model tests.
  3. The test named "disposed while its session is being opened" disposed from the hello, not in that window. Held:
     renamed to `..._disposed_from_the_hello_...`; the window is covered by the new test in 2.
  4. Gate: an empty server or phone hash could compare equal; the server start was not checked. Held, fixed.
  5. Gate: an empty file name would make the cleanup pattern match other apps' part files. Held, fixed (`safe_name`).
  6. Gate: key presses reach the foreground app; the reverse was removed even if set before. Held: header reworded,
     a reverse set before the run is kept.
  7. Gate: the resume proof is sound; `AFTER < SIZE` is the real guard. Comment says so now.
  8. `HostLeftRunningException` after an ordinary start failure. Not held: on a service that never initialized,
     `ShutdownAsync` finds no jobs, the package check starts as a completed task, and both quiet saves return early.
  9. Add during a rotation at cold start gives a "not added" toast. Kept as is (rare, says so); step 2 moves the Host
     into a service anyway.
  Found by me while running the final gates (not by the review): the gate timed the Mi 8 Lite's cold starts with the
  screen dozing (no keyguard, so the lock check passed): 588-657 ms, against 391-424 ms awake. The gate now wakes the
  phone and refuses to time a start unless it is awake. Fable: check these fixes, and whether 1 and 2 are now airtight.

- **2026-11-16, step 1 (uncommitted, before the first step-1 commit).** Fable reviewed the step-1 code itself (no
  Opus stand-in). Findings and what was done are in PROGRESS, "Android app, step 1".
