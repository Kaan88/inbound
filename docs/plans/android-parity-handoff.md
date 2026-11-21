# Android handoff

Updated 2026-11-21. Claude leads the Android work again. Read `CLAUDE.md`, then
`GOAL.md`, `ROADMAP.md` and `PROGRESS.md`, then the
[operating notes](../android-operating-notes.md).

## Where things stand

- **Branch:** `android-remote-files-parity`, pushed and even with origin at `c745317f`
  before the 2026-11-21 reorganization. Worktree:
  `C:/Users/kaank/.local/share/opencode/worktree/08edba/android-remote-files-parity`.
- **Releases:** the last public Android release is 0.0.9 (`d053242e`, recorded at
  `03572c06`). 0.0.10 (`5c12a1af`, Remote Files browse cancellation on the phone) was
  installed on the owner's S24 FE but not published.
- **Against `main`:** the branches split at `cb07f7b6` (2026-11-15). `main` has 192
  commits not here (desktop 0.9.0 and later, plus one unpushed local commit
  `470fbb86`); this branch has 235 Android commits not on `main`. No merge without
  the owner's say-so.
- **`android-app` working copy** (`C:/Users/kaank/Downloads Archive/2026-11-20/Projects/Inbound/idm-android-trial`,
  branch `android-app` at `5e8261eb`, one commit past origin): uncommitted ANDROID.9
  work (TorBox, paired torrents, paired archives screens and clients with tests, a
  TorBox runbook and fixture, `docs/plans/android-host-tools.md`) plus edits to
  `AddSheet`, `MainActivity`, `Navigator` and the tracking files. Not reviewed. Read
  it before reusing any of it; do not copy files blindly.
- **Local state here:** `MainActivity.cs` and `SiteTransferDtos.cs` show as modified
  with no content change (line endings). `.cortexkit/` and `__pycache__/` are
  untracked local state. Leave them alone.
- **Unreviewed range:** `49f5663c..c745317f` was made outside Claude sessions. Its
  earlier review verdicts are input only; it is the first open entry in the
  [Fable review queue](android-fable-review-queue.md).

## Next work, in order

1. **Remote Files redesign (ANDROID.8 reopened, owner 2026-11-21).** The owner:
   "remote files mockups do not match, remote files tab look, design and usage is
   not acceptable." The earlier pass compared emulator captures of fixtures that
   copied the mockup's names and folders (`media/recordings/2026`, a WebDAV
   "C3 mixed fixture" on a paired Host), so it never showed the tab as the owner
   uses it. The real tab was captured on the owner's phone on 2026-11-21 and the
   [14 differences](android-mockup-acceptance.md) are listed in the matrix.
   Agree the list with the owner before changing code. Then
   rebuild the screens to the mockups, compare on real data and on the phone,
   Opus review, and Fable review before release.
2. Inventory the `android-app` working copy, then continue ANDROID.9 under its plan.
3. Separate follow-ups: ANDROID.5/.6 real-account capture and natural deep Doze are
   deferred to ANDROID.14; expanded accessibility is optional (ANDROID.13);
   production phone-originated Remote Files upload is its own feature.

## Where the evidence lives

The [matrix](android-mockup-acceptance.md) holds per-case results. The 2026-11-20
evidence bundles are outside the repo under
`%USERPROFILE%\.codex\visualizations\2026\11\20\` (about 6.5 GB; the parity runs are in
`01a1f888-aeb4-75b3-b22e-55e9f54786c5` and `01a1f647-ee4e-7c32-b21f-14b0225c397c`).
Some older runs are under `%LOCALAPPDATA%\Temp\opencode\` or the worktree's ignored
`artifacts/android-app/pairs/`. Open an actual pair before relying on it. Keep new
sanitized pairs under `artifacts/android-app/pairs/`; no credentials, pairing
descriptors or raw private logs.

Earlier handoffs and the detailed evidence and review logs from 2026-11-18 to
2026-11-20 are in git history (`git show c745317f:<file>`).
