# Android handoff — Codex, 2026-11-19

## Stop state and workspace

The owner requested a pause after the current step, then cleared the thread goal.
The S24 background/reboot test batch is finished and cleaned up. This handoff is
documentation only: do not resume implementation, tests or a new goal without a
new owner request. Project GOAL.md still describes project scope; clearing the
thread goal did not delete or complete the project.

- Worktree: `C:/Users/kaank/Downloads/idm-android-trial`, branch `android-app`.
- Latest implementation/evidence checkpoint: `c694d0fa`, pushed to origin/android-app.
  This handoff is a subsequent documentation-only change.
- Main checkout: `C:/Users/kaank/Downloads/idm`. Do not switch it or merge Android
  into main without the owner's instruction.
- Read `CLAUDE.md` first, then authoritative `GOAL.md`, `ROADMAP.md`, `PROGRESS.md`.
  Detailed evidence/history: `ANDROID-CODEX-PROGRESS.md` and
  `ANDROID-CODEX-FABLE-REVIEW.md`. Also read `docs/plans/android-build-plan.md`,
  `docs/plans/android-app.md` and `docs/new-frontend-guide.md` before frontend work.
- Respect the current sandbox and project build limits. Do not disturb unrelated
  builds/installed Hosts, including other workspaces. Never kill by process name
  or shut down global build servers; verify exact owned PID/path first.

## Subagent usage — explicit owner requirement

Use subagents proactively when work resumes. Delegation is authorized; these are
work assignments, not claims that the models have identical capabilities.

| Work | Requested model | Reasoning |
| --- | --- | --- |
| Searches, mechanical edits, small fixes, documentation, test triage, bounded reviews | GPT-6 Luna, preferred cheaper worker | High |
| Scoped implementation, debugging and visual verification | GPT-6.1 Terra | High |
| Major milestone code audit after implementation and consolidated checks | Astra | High |
| Architecture, ambiguous/cross-cutting problems, concurrency, durability, security, IPC/service lifecycle and final integration | Main agent, Sol role | Main-owned |

**The main Sol agent must personally read and check every subagent code change.**
Verify findings against actual code/artifacts and run the relevant checks before
integrating. A worker's PASS is input to review, not acceptance evidence by itself.

- Discover exact model IDs from the current offered catalog; do not guess.
  Repository preference is `openai/gpt-6-luna`. This session's offered model list
  omitted Luna, so bounded work used explicitly assigned `gpt-6.1-terra`/high.
  Earlier historical notes claiming Luna availability do not prove availability
  in the next session. Disclose an unavailable preferred model and use an
  available cheaper capable substitute, or ask if none is available.
- Verified offered IDs here included `gpt-6.1-terra` and `gpt-6.2-astra`; rediscover
  before reuse. Record requested configuration separately from any worker model
  self-report; do not fabricate a Luna spawn or runtime identity.
- Specify each worker's model and effort explicitly. Give clear ownership/files,
  sufficient context, constraints and verifiable acceptance criteria.
- Delegate independent work in parallel within the live concurrency limit; keep
  dependent work sequential and prevent overlapping edits.
- Use stronger workers only when warranted. The owner requested fewer Astra
  exchanges: audit major milestones, not each small increment. Main verifies and
  fixes confirmed issues, runs checks, then follows up with the same auditor on
  those fixes until PASS. Do not silently substitute another milestone auditor.
- Keep `ANDROID-CODEX-FABLE-REVIEW.md` updated as **Implementer: Codex**. Its legacy
  filename/IDs remain, but the owner reassigned the later independent review to
  Astra. Historical verdicts retain attribution. That later review remains OPEN;
  an earlier milestone source PASS does not close it.

## Design and implementation constraints

The mockup is the UI specification. Do not invent new flows. The owner approved
`docs/plans/android-mockups/android6-flow-review.html` (28-flow C1/C2/C7 board),
including dialogs reviewed alongside the existing screen images. New unspecified
flows need mockups alongside existing references in HTML before implementation.
The compact update-dialog design in `android-update-prompt-review.html` was also
approved, with the same changelog available in Settings.

Preserve one Host authority, local-phone versus selected-remote ownership,
capability-aware controls, queue ID/generation/CAS guards, schedule-parent checks,
execution-job lineage and fresh per-run execution handles. Do not expose inert
or desktop-only settings. `Notify and finish` emits the queue summary and lets an
idle Android execution lease end; it does not stop the queue or exit the Host.

Keep shared-runtime refresh filename reservations, durable candidate commit,
caller-policy checks, cancellation semantics and lock ordering intact. Captured
remote cookies need explicit selected-server consent before probes/admission,
are transient/same-origin, and must not enter saved state/logs. Preserve observed
GET provenance and stable request/owner identity on admission retries.

## Last verified release

- Android `0.0.6`, code6, source `edd073b09472bab45c9c18adb4ae4161ad08be35`.
- Release: https://github.com/Kaan88/inbound/releases/tag/android-v0.0.6
- APK: https://github.com/Kaan88/inbound/releases/download/android-v0.0.6/Inbound-android-0.0.6-arm64.apk
- APK: 20,664,711 bytes; SHA-256
  `30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`.
- Public versioned assets and android-latest feed were verified against staged
  bytes; S24 installed APK matched. Desktop latest stayed v0.9.0. Do not overwrite
  immutable versioned Android assets or promote Android as desktop latest.
- Permanent-key arm64 NativeAOT, min31/target35, non-debuggable/no test hooks.
  Stage: `artifacts/android-app/release-0.0.6/`.
- Approved signed-changelog prompt ships; notes have a separate bounded signature
  while preserving the existing APK signature format. Legacy/no-notes fallback
  and lifecycle-restored install-offer identity must remain intact.
- Latest batch changed only optional fixture request tracing and documentation;
  no new app APK/feed release was needed or produced.

## Acceptance evidence already obtained

Do not rerun these merely because this is a new session; inspect artifacts and
scope first. Do not promote a scoped check to full milestone completion.

- S24 FE/API37, production0.0.6: local schedule create/save/reopen, disabled
  schedules, selective deletion; earlier 1MiB queued transfer hash matched.
- S24 light/dark C1/C2/C7 and empty dynamic-colour Downloads spot checks, with
  saved mockup pairs under `artifacts/android-app/pairs/`. Owner reported basic
  TalkBack navigation moves and reads useful labels. Full visual/accessibility
  coverage remains open. Inconsistent image previews were not confirmed UI
  defects; compare actual PNGs/hashes before inferring missing content.
- S24 direct-LAN scheduled ordinary job4100: daily12:58, 64KB/s, one connection;
  natural start12:58:53, 8MiB complete13:01:04, hash matched. Phone was locked and
  noninteractive/AOD Dozing; sampled deep idle was INACTIVE, not deep Doze.
- Separate S24 reboot: exactly one freshly approved reboot of a user-started UIDT
  job1. Verified partial prefix and active job before reboot; new boot ID, fresh
  job1 start after unlock, timestamped postboot Range from byte1,114,112, final
  16MiB hash matched. No automated app launch/resume/forced job caused recovery;
  continuous foreground surveillance was not claimed. This is not scheduled
  queue4100 reboot acceptance. Evidence: `artifacts/android-app/android6-s24-background/`.
- API33 NativeAOT emulator: actual Android Wi-Fi metering and battery-saver holds
  at zero bytes, automatic hash-matched completion when conditions cleared;
  simulated charging gate; abrupt idle process death plus forced-Doze hold and
  natural hash-matched transfer after unforce. Evidence:
  `artifacts/android-app/android6-acceptance-0.0.5/` (includes later0.0.6 checks).
- QA trap: `am kill` was classified as reason10/user-stop on this emulator and
  correctly user-paused queued work. Corrected abrupt-death test used SIGKILL
  only on verified owned idle emulator PID/no services, then confirmed reason2.
  Never apply that method/root access to the daily phone.
- Source5 major Astra SOURCE PASS and historical consolidated Core2373,
  Engine214, Server208, Integration2095+32 optional skips are distinct checkpoints.
  Decimal-rate fix: Mobile528, Debug0errors/36warnings, solution0errors/87warnings,
  NativeAOT x64/arm64 and actual numeric UI checks. See progress for exact logs.
- Latest fixture change: `tools/AndroidTrialServer/Program.cs`, opt-in
  `--trace-requests`, correlated GUID/UTC-ms/range begin/end without URL/header
  logging; old SENT format preserved. Main reviewed Terra's patch, built with
  zero errors/warnings, and passed actual HEAD/GET/Range/correlation smoke checks.

## Cleanup, devices and test safety

Latest batch cleanup verified both test jobs/files/parts and temporary
queue/schedule removed, baseline Main/empty Downloads restored. Original phone
appearance, resume preferences, TalkBack state, adaptive brightness and timeout
were preserved/restored across the relevant batches. Adaptive mode may change
numeric brightness after its baseline value is written back; do not claim a
frozen brightness or byte-identical private app state.

Owned LAN fixture47618 and older copied QA server were stopped. The owner wanted
the emulator used normally in **visible hardware mode**; inbound33 was left
running with host NVIDIA GPU rendering and non-root adb. Rediscover current
process/device state rather than reuse historical PIDs or endpoints.

- Phone preference: use Wi-Fi ADB. Rediscover with devices/mdns, verify S24 FE
  SM-S721B/API37, then pin every command to its verified serial. A port changed
  at reboot; do not reuse stale wireless addresses. Do not root/wipe/uninstall
  the daily phone. One reboot approval was consumed, not standing permission.
- Preserve existing user authorizations and project phone-safety rules. Capture
  and restore temporary settings. A54 was lent for one trial: ask before reuse.
  Mi8 Lite/API30 needs a designated test build, never a lowered release minimum.
- Launch Android executables from Git Bash, `MSYS_NO_PATHCONV=1`; use bounded adb
  calls. SDK: `C:/Users/kaank/AppData/Local/Android/Sdk`.
  JDK: `C:/Users/kaank/tools/jdk/jdk-21.0.13+9`.
- Before UI actions/captures verify Inbound is foreground. The artifact phone
  helper now checks mCurrentFocus and refuses other screens; use explicit
  `am start -W` when a cold cleanup launch needs time to become foreground.
  During unattended acceptance observations do not launch the app to inspect it.
- Filter observations to Inbound/task-owned data; never save unrelated UI,
  notification content, credentials, headers or broad logcat. Read existing test
  scripts before using them: some install/wipe/use hooks/reboot. The latest scheduled/reboot
  phone tests used no hooks or adb reverse; direct LAN survives reboot.
- Build Android separately via `scripts/build-android.ps1`; see
  `docs/android-builds.md`. Keep API30/hooks/x64 outputs test-only. Check every
  NativeAOT output for provider/TLS/icon; a successful publish alone is not proof.
  Stale architecture intermediates previously caused a missing runtime provider.
- Reuse permanent signing inputs; never regenerate/print/export secrets. Use
  password-file references, existing signing/staging tools and the pinned signer.
  Never deliver an x64 test APK or stale arm64 output. Android arm64 permission
  does not extend to other project components.

## Remaining work, only after an explicit new request

ANDROID.6/.7 remain PARTIAL. Continue from implemented approved flows, not a new
implementation. Remaining gates include physical-phone deep Doze/network/power,
long-duration background checks, complete visual/dynamic-colour/TalkBack and
local/remote behavior (including publicly trusted TLS/phone HTTPS pairing).
ANDROID.4/.5 still have full screen/link-catching and real-account compatibility
acceptance gaps. ANDROID.10 public newer-offer/system-installer acceptance remains
open despite release publication and the installed0.0.6 APK.

ANDROID.8 remote files/rclone and ANDROID.9 TorBox/torrents/archives each need their
own go-ahead and agreed designs. Later independent Astra review remains OPEN.
Update the Codex progress/review queue and authoritative tracking at meaningful
increments; push coherent android-app commits, with no main merge unless asked.
