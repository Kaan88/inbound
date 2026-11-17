# Android app: reviews for Fable to check

Owner, 2026-11-16: "when, not if, fable hits a limit - use opus, but write notes for fable so that it can check when my
limit resets."

The build plan (section 2) asks for one standing Fable reviewer. When Fable is out of quota, an Opus agent reviews
instead and the review is written here. When the owner's limit resets, send Fable every open entry (the code it names,
not the Opus findings first, so its read is its own), then compare, act on what holds, and mark the entry done.

Each entry: date; what was reviewed (commit, or files if uncommitted); who reviewed; findings and what was done; what
Fable should look at.

## Open

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
