# Android delivery handoff

## Claude continuation checkpoint — 2026-11-20

Read `CLAUDE.md`, then `GOAL.md`, `ROADMAP.md` and `PROGRESS.md`. This handoff supersedes the earlier pause/resumption instructions. This documentation pass does not start ANDROID.9 or publish another release.

- **Current branch:** `android-remote-files-parity`, implementation HEAD `5c12a1afcfba05fc50c1ac58b5fde3ca9a637662`. Current worktree: `C:/Users/kaank/.local/share/opencode/worktree/08edba/android-remote-files-parity`.
- **Last Claude-attributed ancestor:** `49f5663c` (Claude Opus 5.5 coauthor). The [post-Claude review index](../../ANDROID-CODEX-FABLE-REVIEW.md#changes-since-the-last-claude-commit) covers `49f5663c..5c12a1af`.
- **Published checkpoint:** Android 0.0.9 from `d053242e`, publication recorded at `03572c06`. Astra High passed source, visuals and flows for all four parity batches. ANDROID.8 is closed for the [matrix's recorded scope](android-mockup-acceptance.md). Do not repeat the completed parity workflow merely because older entries say pending.
- **Phone hotfix:** 0.0.10 at `5c12a1af`, managed loopback HTTP cancellation and an explanation for skipped transfers. Signed APK was installed in place and verified on the S24 FE. Public release/feed publication is not recorded; the last documented public Android release is 0.0.9. Exact fingerprint and checks: [PROGRESS](../../PROGRESS.md#remote-files-on-the-owners-galaxy-s24-fe-2026-11-20).
- **Preserve local state:** initial `git status` reports `MainActivity.cs` and `SiteTransferDtos.cs` modified, but `git diff HEAD` reports no content hunks (including a line-ending warning for the DTO). Do not reset or normalize them as part of handoff. Untracked `.cortexkit/` and Python `__pycache__/` directories are local state, not reviewed deliverables; left untouched.

## Next work, in order

1. Inspect `git status`, branch and diff before changing anything. Read the current review index and matrix; retain their scoped evidence and outstanding acceptance gaps.
2. Inventory the **separate `android-app` worktree** before ANDROID.9. `git worktree list` now registers it at `C:/Users/kaank/Downloads Archive/2026-11-20/Projects/Inbound/idm-android-trial`, HEAD `5e8261eb` (routing documentation). Its unfinished paired-Host TorBox/torrent/archive changes were recorded previously. This session could not inspect that archived directory because filesystem access was denied; its current dirty-file inventory is unverified. Do not assume the old `C:/Users/kaank/Downloads/idm-android-trial` path or a clean checkout. Reuse valid work after inspecting it; do not overwrite it or copy files blindly.
3. Continue ANDROID.9 under the existing plan and owner direction, after resolving the working-copy inventory. Keep `main` at its own checkout and branch. Do not merge or publish as a side effect of this documentation handoff.
4. For each UI milestone, compare native captures against the approved mockups early, finish implementation and consolidated checks, then obtain the required combined code/visual Astra audit. Recheck confirmed fixes with that reviewer. Keep actions on one emulator sequential; independent verification may run in parallel.
5. Preserve separate follow-ups: ANDROID.5/.6 real-account capture and natural deep Doze were deferred to ANDROID.14; expanded accessibility is optional (.13). Production phone-originated Remote Files upload remains separate from accepted C4 fixture parity. The later independent Astra queue (legacy Fables filename) remains OPEN despite milestone PASS results.

## Where the evidence lives

The current [matrix](android-mockup-acceptance.md) holds scoped results. The November 20 resumption bundles are under `%USERPROFILE%\.codex\visualizations\2026\11\20\01a1f888-aeb4-75b3-b22e-55e9f54786c5\`. The pre-pause evidence checkpoint was `34a07e8d`, with earlier bundles under the sibling `01a1f647-ee4e-7c32-b21f-14b0225c397c`. Some older runs are under `%LOCALAPPDATA%\Temp\opencode\` or the worktree's `artifacts/android-app/pairs/`.

All 85 date-stamped IDs in the two inventory tables at `af7ec829` resolved to local directories. The chat folder holds 82. The earlier `b234-dest-20261120-025429`, `idm-android-resume-20261119-134123` and `paired-b2-resumed-20261120-020635` runs are in the temporary folder. Some functional runs have no image. Open the actual pair and verify what it shows before using it for visual acceptance.

For example, `c2-paired-limits-board-20261120-125855/c2-paired-limit-board-pair.png` shows the reviewed native dialog beside the board. `c2-paired-transfer-20261120-130425/c2-paired-limits-transfer-proof.json` records the live cap and queued third transfer. `c2-queue-notification-run-path.txt` locates the interrupted run. Keep reviewable, sanitized pairs and results for the candidate in the worktree's ignored `artifacts/android-app/pairs/` or link their verified retained paths. Exclude credentials, pairing descriptors and raw private logs.

## Why the workflow changed

Between `66270193` and `af7ec829`, 36 of 59 commit subjects began with “Verify”. The handoff changed in 58 commits, the matrix and PROGRESS in 57 each, and ROADMAP in 26. In the November 20 chat “Review Android parity handoff”, 24 of 28 turns recorded activity from one of two Astra review agents. This counts turns with reviewer activity, not independent audits or elapsed review time. In two successive commits (`2992d249`, `34a07e8d`) the C2 limit editor and its live enforcement had separate review checkpoints. Both results feed the same queue flow. The C3/C4 reopening after 0.0.8 also showed the cost of signing off on working behavior before comparing the design. Count completed flow batches, named gaps and candidate findings. Commit counts measure activity only.

Record a case result once in the matrix. Update PROGRESS with a batch outcome and a link. Change GOAL or ROADMAP when scope, sequence, an owner decision or milestone status changes. Update this handoff on an actual handover. The [later Astra review queue](../../ANDROID-CODEX-FABLE-REVIEW.md) retains its separate owner-requested remit. Link important implementation batches there when appropriate. For the earlier handoff, including interrupted-run cleanup details and historical artifact IDs, see `git show af7ec829:docs/plans/android-parity-handoff.md`.
