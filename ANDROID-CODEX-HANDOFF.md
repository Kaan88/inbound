# Android handoff — Codex, 2026-11-19

Android acceptance checkpoint (2026-11-19, Codex): the owner's S24 FE was rediscovered over Wi-Fi ADB (SM-S721B/API37). Its installed 0.0.6 APK SHA-256 exactly matches the public release. Real-phone UI created two disabled schedules, saved/reopened Mon/Wed/Fri, canceled deletion once, then deleted only the second and verified the first remained. A 1 MiB task download waited in the stopped queue, completed after explicit queue start, and matched fixture SHA-256 `b13261ceffb27a120f88c3466a3917bfb2875493d5f802422b433a3599407be6`; transport was Wi-Fi ADB reverse, not direct LAN. Task queue/schedule/job/payload/reverse removed; baseline Main and empty downloads verified. Main personally viewed C1/C2/C7 phone/mockup pairs; Terra checked evidence and visual scope. Owner manually confirmed TalkBack moves between controls and reads useful labels in Downloads. Original TalkBack/accessibility, brightness and timeout values were restored and verified; this does not establish exhaustive accessibility acceptance. Emulator NativeAOT API33 additionally held a charging-only queue without payload requests, then completed with matching hash after simulated AC; a persisted Android job naturally recreated an absent process and started the queue at11:48:18 for11:47 (78seconds late). Emulator task data removed, recorded baseline queue fields restored, non-root adb; full settings hash differs after create/delete, so no byte-identical restoration claim. ANDROID.6/.7 remain PARTIAL; phone Doze/network/reboot, full visual/accessibility and broader remote gates remain OPEN.

Android 0.0.6 published (2026-11-19, Codex): https://github.com/Kaan88/inbound/releases/tag/android-v0.0.6 from source `edd073b09472bab45c9c18adb4ae4161ad08be35`. Permanent-key NativeAOT arm64 APK:20,664,711 bytes, SHA-256 `30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`; manifest SHA-256 `fdc5c2666cd8037a6255529bb5c7603f16eb8f5ba13eecd619f27d4acc005266`. Downloaded public APK/manifest/checksums match staged bytes; android-latest matches the same versionCode6/versionName0.0.6 signed manifest. Desktop latest remains v0.9.0. Main reviewed worker output and final source; NativeAOT rate checks, Mobile528 and solution build passed as scoped in the candidate checkpoint below. Queue/global KB/s correction is shipped; no new Astra milestone audit claimed. ANDROID.6/.7 phone/TalkBack, broader remote and Doze/network/reboot acceptance and later independent review remain OPEN. Main checkout untouched; no merge to main.

Android 0.0.6 candidate (2026-11-19, Codex): remote QA exposed binary speed inputs versus decimal rate summaries. Queue and overall KB/s inputs now use 1000 B/s; unchanged legacy raw limits are preserved. Main personally reviewed the complete diff; Terra scoped source/evidence PASS. Mobile528, Debug build0errors/36warnings, Release solution0errors/87warnings passed. NativeAOT x64 install verified 32 and256 inputs persist as32000 and256000 B/s and render accordingly. Permanent-key arm64 packaging checks passed (20,664,711 bytes, SHA-256 `30411689649b22ab6ff24ae0f520a3b924178917fa728bf1a2715d706f722685`); staged locally, not yet published. Isolated remote schedule Mon/Wed23:58–23:59 and restart persistence verified; task queue/schedule removed and global limit reset toNone, local settings hash unchanged. Hardware emulator remains visible as requested. Public feed remains0.0.5; ANDROID.6/.7 phone/TalkBack, broader remote and Doze/network/reboot acceptance remain OPEN. Evidence: `artifacts/android-app/android6-acceptance-0.0.5/`; later independent review remains OPEN.

## Current continuation after 0.0.5 publication (2026-11-19, Codex)

Public prerelease: https://github.com/Kaan88/inbound/releases/tag/android-v0.0.5.
Direct APK: https://github.com/Kaan88/inbound/releases/download/android-v0.0.5/Inbound-android-0.0.5-arm64.apk.
Source `87555b993ae8f3de7e61a171c87a932a8d6a4a2b` is pushed on android-app; no merge to main.
APK 20,664,711 bytes, SHA-256 `1cf087a69c56744e1ee7b0bd5e3c29bce1fe7cad5db371f78ada35fac8805691`.
Manifest SHA-256 `a3b4500e68aeb7a59251a54121cc18d38a98d3cb2c65ae224d158a4d8ec3459c`.
Versioned assets and android-latest feed were downloaded and matched staged files.
Desktop latest remains v0.9.0. Stage `artifacts/android-app/release-0.0.5`;
production `signed-builds/e069627be21342819f9be8f74719568a/Inbound-android-release.apk`.
Package/pinned signer/min31/target35/arm64/nondebug/provider/TLS/no-hooks checks passed.

Main Sol personally checked delegated source and artifacts; Astra SOURCE PASS includes
signed notes, execution handles/API30 cleanup and the final modal queue picker.
Mobile528/release-tool10 and current solution0errors/53warnings passed. Earlier
Core2373/Engine214/Server208/Integration2095+32skip are separate consolidated checkpoints.
Final NativeAOT API33 job-stop observation passed (`final-0.0.5-native-stop.json`).
One injected native crash and explicit forced restart recorded count0->1/sessionpresent,
resumed queue2 and completed 1MiB with fixture SHA
`fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83`;
final marker count0/sessionfalse and ongoing1001 absent. See
`final-native-crash-recovery.json` and `final-native-recovery-payload-sha.txt`.
Native keyboard selection persisted after a landscape/portrait cycle; landscape value
was offscreen, not directly asserted. Short/no-notes/dark dialog previews were viewed;
their illustrative 0.0.6 offer was test-only and never installed.

Logs: `updater-picker-native-clean-build.txt`, `update-prompt-production-build.txt`,
`update-prompt-release-prepare.txt`, `public-verification-0.0.5/` under android6 artifacts.
Earlier bad incremental native artifact was quarantined and never installed/distributed;
architecture-specific intermediate caches were safely renamed as backups. The build
script now checks unsigned NativeAOT outputs too. Never deliver test x64 APKs.

Cleanup completed: task queues Android6-QA/Night-QA stopped; emulator theme Follows
the system, battery reset, font_scale absent, free rotation1/user_rotation0 and adbd
unroot verified uid2000. Owned fixture57556/listener47612 and emulator5554 stopped.
S24 stayed disconnected/untouched; no daily-phone settings changed. Earlier isolated
remote launcher16664/child39568 were already stopped; unrelated builds were untouched.

NEXT: ANDROID.6/.7 remain partial for phone/TalkBack/pairs, broader remote workflows
and remaining Doze/network/reboot acceptance. An optional async request asks the owner
to connect/unlock S24; no reply yet. Restart owned inbound33/fixtures as needed while
preserving app data. Remote evidence currently covers create/rename/switch, not every
C1/C2/C7 workflow. ANDROID.8/.9 still need separate go-ahead/design; later independent
review remains OPEN. Publication/source PASS does not complete all milestones.

## Historical checkpoint before 0.0.5 publication

Owner approved the compact update dialog with the same signed changelog in Settings.
Android 0.0.4 is the immutable public prerelease; the separate 0.0.5/code5 source
is local and uncommitted at this checkpoint. Worktree android-app only; no merge to main.

Current source: optional separately signed notes (8,192 characters, unchanged base
APK signature), shared prompt/card, lifecycle restoration and no-notes fallback;
owner-aware crash marker, scheduled-job lineage plus fresh per-execution handles,
and API30 diagnostic guard. Main Sol personally reviewed every delegated code change.
Mobile528 and release-tool10 tests passed; signer0warnings/errors. Final solution
update-prompt-solution-final.txt passed0errors/53warnings. Earlier 0.0.4 consolidated
Core2373/Engine214/Server208/Integration2095+32skip are separate checkpoint evidence.

Astra SOURCE PASS covered signed notes/UI lifecycle, fresh execution handles and
API30 cleanup. Keyboard device QA then exposed additional widget behavior; the
latest picker is a readonly outlined field opening a native modal ListPopupWindow,
with immutable ID/generation snapshots and a stale-popup version guard. Main read
that worker change, added click-handler cleanup and verified it. Final picker
re-audit SOURCE PASS; Debug build0errors/2warnings. Keyboard selection, delayed
metadata, both rotations and queue2 admission passed on the emulator. All temporary picker test diagnostics were removed from source.

Native API33 stop evidence: stop-identity-after.json reports detached=True,
queued queue2 membership, queue running/user pause false, checkpoint897859/1048576
unchanged2sec and crash marker empty. After explicit force-run, the same job finished
with expected fixture SHA fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83
(stop-resume-payload-sha.txt). This predates the newest callback/picker fixes and is
observed stop/resume evidence, not automatic restart, final durability, reboot,
Doze or phone acceptance. Touch selection earlier survived delayed metadata and
admitted slow(5).zip to queue2; the modal picker now passed keyboard/metadata/rotation and queue2 admission.
Task job8d5a2a50-8062-40f3-93ac-3deab48b8d63 (slow(6).zip) is user-paused
at12806bytes and can be resumed for final NativeAOT stop/hash checks.

IMPORTANT: updater-picker-native-final-build.txt produced an INVALID incremental
NativeAOT APK: runtime provider missing from dex. Checker rejected it; it was NOT
installed or distributed. Quarantined artifacts/android-app/android6/rejected-native-missing-provider.apk.
The exact Release-test/net10.0-android/android-x64 intermediate directory was
safely renamed to android-x64-missing-provider-backup-20261119 (not deleted).
Build fresh once picker source is stable. build-android.ps1 now automatically
checks unsigned/debug-key NativeAOT outputs too; permanent-key signing already
runs its release checker. Never use an APK just because dotnet publish succeeded.

Current owned emulator5554/API33 is ROOTED, fake-unplugged battery, Light theme.
Only that emulator is connected; S24 is disconnected/untouched. Restore adb unroot,
dumpsys battery reset, Follows System theme and free rotation before cleanup.
Baseline font_scale absent (already restored). Fixture Python PID57556 listens on
loopback port47612; verify exact process before cleanup. Isolated remote launcher
16664/child39568 were stopped earlier. Do not touch unrelated Barbican builds.

Next: clean NativeAOT x64 build is running (updater-picker-native-clean-build.txt);
verify provider/TLS/icon and device stop/prompt/picker tests,
then clean permanent-key arm64 candidate with docs/android-release-notes-0.0.5.txt.
No0.0.5 publication yet. ANDROID.6/.7 remain partial for broad remote and physical
phone/accessibility/lifecycle/pairs acceptance; later review stays OPEN.
ANDROID.8/.9 still need separate go-ahead/design.

## Start here

Read CLAUDE.md first, then GOAL.md, ROADMAP.md and PROGRESS.md. This handoff is
orientation; those tracking documents remain authoritative. Also read
ANDROID-CODEX-PROGRESS.md and the current instructions at the top of
ANDROID-CODEX-FABLE-REVIEW.md, plus docs/plans/android-build-plan.md and
docs/new-frontend-guide.md before frontend work.

Worktree: C:/Users/kaank/Downloads/idm-android-trial, branch android-app.
Main checkout: C:/Users/kaank/Downloads/idm, branch main. Do not switch or merge
into main without the owner's instruction.

## Historical checkpoint (before Android 0.0.4 publication)

- Implementer/coordinator: Codex (OpenAI/GPT).
- HEAD: 50df32b8cd5a51079d054f6fe3d0bc6b14f05fe1, documentation of delivery/evidence.
- Implementation: d0816724208a0ae543cf2d84a02e4ee32f218de2, ANDROID.5 link catching
  plus shared Runtime refresh and queue-race fixes. Both commits were pushed to
  origin/android-app at the delivery checkpoint; no remote fetch was run for this handoff.
- Uncommitted work when writing: review-assignment clarification in CLAUDE.md,
  ANDROID-CODEX-FABLE-REVIEW.md, ANDROID-CODEX-PROGRESS.md, GOAL.md and PROGRESS.md.
  This handoff and its onboarding link are also local documentation changes.
  Preserve them. No application source changed in this handoff session.

## Next implementation

Next major milestone: ANDROID.6, queues, scheduler and settings (build-plan
step 5, mockups C1/C2/C7). Inspect existing Mobile models, Host contracts, Android
screens and agreed mockups before dividing implementation. Every displayed setting
must work on the phone; do not expose desktop-only options or inert controls.
Keep shared architecture, scheduling/concurrency, durability, security and final
integration with the main agent. Exercise local-phone and selected-remote-host
behavior, preserving the single Host authority.

After integration, finish the outstanding core-screen behavior recorded under
ANDROID.4: short failure reason in rows/full details, progress notification Open
and grouped per-file Finished notifications with Open, and hiding summary/chips
while selecting with a filter. Verify existing behavior before changing it.

ANDROID.5 source is implemented and milestone-audited, but acceptance remains
partial: real-account browser sign-in compatibility, rotation, TalkBack and broader
visual checks. Light-mode picker/caught-sheet spot checks are not full acceptance.
Do not mark ANDROID.4/.5/.7 fully complete on implementation alone. ANDROID.7
publicly trusted TLS and phone HTTPS pairing acceptance also remain open.

ANDROID.8 remote files depends on rclone packaging feasibility; ANDROID.9 TorBox,
torrents and archives have no agreed screens. These later scopes need their own
go-ahead. ANDROID.10 distribution/update acceptance is partly complete. Consult
the roadmap for subsequent web-UI recommendation work.

## Delegation and review

Owner explicitly authorized proactive delegation (2026-11-19). Specify each
worker's requested model and reasoning, ownership, context and verifiable
acceptance criteria. Luna is the preferred worker for simple bounded work, at high
reasoning; the exact available model ID `gpt-6-luna` was discovered in the host
catalog and successfully spawned in this session. The earlier note that Luna was
unavailable is outdated. Record requested configuration separately from any
worker self-report; do not infer one from the other.

Use `gpt-6.1-terra`, high reasoning, for scoped implementation, debugging and
visual work. The main Sol agent owns architecture, cross-cutting work and final
integration. It reads and checks all subagent code and verifies requirements and
checks before integrating delegated results. Keep complex concurrency,
durability, security, IPC protocol and architecture work with the main agent.

Astra (available gpt-6.2-astra) audits major milestones after implementation and
consolidated checks. Owner asked for fewer audit exchanges; do not audit each
small change. Fix confirmed findings, then verify the fixes until PASS.

Continue ANDROID-CODEX-FABLE-REVIEW.md: legacy filename and FABLE-CODEX identifiers
stay for traceability. Owner clarified on 2026-11-19 that Codex is the implementer
and Astra reviews implementations later. Older pending Fable assignments there
are superseded; historical verdicts retain their attribution. Record important
changes, commits, rationale, actual checks, findings/fixes and acceptance gaps.
Later independent review remains OPEN despite existing milestone Astra PASS.

## Delivery and updater

- Release 0.0.3/code 3, permanent-key arm64 NativeAOT, min API 31/target 35.
- Desktop: C:/Users/kaank/Desktop/Inbound-android-0.0.3-updatable.apk.
- SHA-256: e1dc3d59cbf5d5f5ccf3518ebc4a69002a6ec416cdcf5fa3e8b68fa09cf93bc7.
- Source APK: artifacts/android-app/signed-builds/0b2a487b31e743d68d7402399642a7aa/Inbound-android-release.apk.
- Prepared signed local bundle: artifacts/android-app/release-0.0.3; NOT published.
- Installed on S24 FE via install -r, data preserved, installed APK hash matched,
  launch succeeded. Pre-install package unexpectedly reported 0.0.1/code 1 despite
  earlier 0.0.2 evidence; cause unknown. Do not invent an explanation.
- Public Android release/feed remains 0.0.2 at the last verified checkpoint:
  android-v0.0.2 and android-latest in Kaan88/inbound. Desktop latest is unchanged.
  Earlier explicit publication approval named 0.0.2; obtain authorization for a
  newer public release rather than claiming it already exists.
- Updater checks on genuine cold process launches and daily, downloads a verified
  signed offer automatically and prompts for Android's system installer. Fixture
  1→2 install and real-feed current-version check passed; public newer-offer
  upgrade acceptance remains open. System install approval is still required.

## Security and shared-runtime traps

Remote cookies require explicit per-download consent naming the selected server
before metadata probes as well as admission. Changing host invalidates consent.
Cookies are transient, same-origin only; never persist them in UI saved state or
logs. Capture only proven GET requests; ambiguous/subframe POST/redirect provenance
must not become a guessed authenticated GET. Missing observed Referer stays absent.
Captured admission retries require both stable ClientRequestId and OwnerExtensionId.

Refresh uses a private candidate and durable save before publishing live state;
keep filename reservations, AddressRefreshPending, caller-policy rechecks and the
settings → per-job address gate → persistence → sync lock order intact. Cancel
must not wait behind the global settings gate. Once durable commit starts it
finishes despite cancellation. Clearing the refresh guard must wake scheduling
on success and rollback. Preserve the late failed-runner queue-retry fix.

## Evidence and test scope

Raw ignored evidence: artifacts/android-app/codex-signing/.
Reusable loopback browser fixture: tools/AndroidApp/link-fixture/.

- Final Mobile 408 passed (android5-mobile-final.txt), Core 2373 and Engine 210 passed.
- Full Integration: 2059 passed, 32 skipped, one failed BEFORE the queue fix.
  No clean full-suite rerun after that fix is claimed.
- Afterward: exact failure plus two regressions passed 3, queue policy passed 10,
  final refresh/reservation/wakeup/cancel checks passed 16
  (android5-reservation-wakeup-tests.txt).
- ANDROID.5 final Astra source audit PASS; auditor read source and recorded test
  evidence, did not independently run the phone acceptance tests.
- NativeAOT fixture authenticated catch and refresh matched payload hash; refresh
  reused the existing job. Cancellation, Later, filtering/selection, subframe POST
  refusal and remote-cookie-consent cancellation have recorded evidence.
- GitHub sign-in form rendered; no credentials entered, so actual sign-in unverified.

Run focused checks for routine changes, consolidated relevant checks at milestones.
Shared Host/runtime tests matter to both Android and Windows. Some full integration
tests launch isolated Windows Host processes; GUI/desktop notifications are not
required for routine Android checks. Failure/update notices bypass the completion
notification toggle: isolation gap is recorded for later review. Do not disturb
installed Windows Host processes or kill by name; verify owned PID/path first.

## Devices, tools and signing

Read the device/runbook details in ANDROID-CODEX-PROGRESS.md before using adb.
Start Android executables from Git Bash, pin ANDROID_SERIAL and use
MSYS_NO_PATHCONV=1. Rediscover serials/endpoints; do not reuse stale wireless IPs.
SDK: C:/Users/kaank/AppData/Local/Android/Sdk.
JDK: C:/Users/kaank/tools/jdk/jdk-21.0.13+9.

S24 FE SM-S721B is the daily phone. Preserve data/settings. Mi 8 Lite is Android 11;
only designated test builds may lower min API to 30. A54 was lent for one test;
ask before using again. inbound33 emulator and owned fixture servers were stopped
at the previous cleanup; rediscover actual state. Emulator theme restored to
Follows System. No physical-phone settings were changed.

Signing inputs already exist; do not regenerate keys. Encrypted backup in private
Kaan88/secrets was verified against local keystore/password/update key entirely
in memory. Follow its README and existing runbook; never print keys or write
plaintext exports. Sign through existing tooling using password-file references,
not secret command arguments or MSBuild environment variables. Always validate a
fresh production APK using the signing/build tooling and check-apk.py before
install/delivery. Never deliver the x64 test APK or stale arm64 artifacts.

## Handoff verification

This session changed documentation only. Check git diff --check and inspect the
actual diff before committing. No application tests or device actions are needed
for this handoff. Keep tracking notes and the review queue current as work resumes.


## ANDROID.6 board-approved implementation handoff (2026-11-19, Codex)

The owner reviewed `docs/plans/android-mockups/android6-flow-review.html` and said "looks good." The board is approved as design direction and implementation has resumed. This does not accept feature behavior or complete the milestone. The earlier design-first pause is historical; it no longer blocks implementation of the reviewed flows. Preserve pending/future capability labels and local-phone versus selected-Host ownership.

Ownership: Terra at high reasoning owns C2 editor/dialogs and C7 grouped root/Details implementation. Main Sol owns queue actions, shared completion behavior and integration, and personally reads/checks all worker code. Main verifies requirements and consolidated checks before integrating.

Completion note: the existing `NotifyWhenFinished` preference controls whether the queue emits its transient summary when all its jobs are terminal; it does not change `Queue.IsRunning`. Android releases its background execution lease when idle for both choices. Thus “Notify and finish” means notify, then let the idle background lease end; “Do nothing” suppresses the notice while the queue remains ready. Neither choice stops the queue, exits the app/Host or shuts down the device.
Current evidence: Full Integration 2,077 passed / 32 skipped / 0 failed; latest Mobile 464 and focused Engine queue 4 passed; latest Android Debug build 0 errors / 2 known SDK warnings. HTML-board browser QA and byte-identical C1/C2/C7 references verify the artifact only. Earlier Core/Engine/Mobile/Runtime and inbound33 results are historical checkpoints. NativeAOT, physical phone, selected remote Host, Doze/battery, TalkBack, theme/visual pairs and Astra audit remain pending. ANDROID.6 remains IN PROGRESS, local/uncommitted, ROADMAP unchecked; no feature acceptance, milestone completion, public release or Astra PASS is claimed. Public Android feed remains 0.0.2; local Debug 0.0.4 is not published.
## ANDROID.6 flow/retry checkpoint (2026-11-19, Codex)

Main Sol personally reviewed every worker change. Current reviewed flows cover C1 queue create/rename/delete/move, C2 sheets/times/limits/finish choice and supported C7 grouped pages. Shared protocol additions are `NotifyWhenFinished`, `RemoveSchedules` and `ExpectedQueueId`.

Focused approved-flow checks: 6 passed. Before the retry fix, deterministic StopThenStartWhileFailedRunIsSaving cases with limits 1 and 2 both failed (`failed` became `paused`; `artifacts/android-app/android6/retry-race-before-fix.txt`). Main fixed queue stop to preserve failed/cancelled terminal state; related focused checks passed 11 (`retry-race-fixed.txt`). Consolidated Core 2373, Engine 214 and Mobile 464 passed. Initial Integration had 2081 passed, 32 skipped and the exact retry test failed. Fixed full Integration rerun remains running (session 60371); Server suite has not run. Approved UI/icons Android Debug build passed with two known SDK warnings; latest Done-button rebuild runs as session 25681.

API 33 emulator verified blank-name validation, stopped Night-QA creation, daily 09:00 save, native time picker and custom 500 speed draft surviving two rotations, and free-rotation restoration. S24 FE untouched. ANDROID.6 remains IN PROGRESS, local/uncommitted and unchecked. No clean full rerun, Server consolidation, NativeAOT/phone acceptance, Astra audit or feature-completion claim.
## ANDROID.6 consolidated-checkpoint handoff (2026-11-19, Codex)

- Full Integration: 2,085 passed / 32 skipped / 0 failed. Core 2,373, Engine 214, Mobile 464 and Server 208 passed. Full Release solution build: 0 errors / 29 warnings. Evidence: `artifacts/android-app/android6/approved-integration-final-green.txt`, `approved-core-full.txt`, `approved-engine-full.txt`, `approved-mobile-full.txt`, `approved-server-full.txt`, `approved-solution-build.txt`.
- Checkpoint fix: controlled old-helper test returned the initial zero checkpoint with a nonzero save blocked; main changed synchronization to actual Runner-idle state while retaining checkpoint/payload-prefix assertions. Focused checkpoint/retry suite passed 7 (`checkpoint-wait-before-fix.txt`, `checkpoint-retry-final.txt`).
- Signed NativeAOT arm64 candidate passed `check-apk --release` (permanent certificate, min 31, target 35, arm64-only, non-debuggable): `artifacts/android-app/signed-builds/3e8a0b3ca7384b5d956da8e2d8e32d36/Inbound-android-release.apk`, 20,578,695 bytes, SHA-256 `d85721f7a1f7f82dc4c7d753fc58828a527184f16c4092ee431963d93b99d00d`. Pre-audit and unpublished. The one-copy Android timing-note fix was made after this build, so rebuild the final candidate.
- Separate NativeAOT Release-test x64 checker/install/launch passed on owned inbound33 API 33 (1,401 ms; not a benchmark). Isolated remote QA persisted create/rename and preserved local queues on switch-back. S24 FE untouched. Main NativeAOT/background schedule QA is ongoing.
- Astra audit `android6_astra_audit` is running (requested gpt-6.2-astra, high); main verifies findings. No Astra PASS, public release, physical-phone acceptance or feature-completion claim. Public feed remains 0.0.2; ANDROID.6 stays IN PROGRESS and unchecked.
