# Android handoff

Updated 2026-11-22 (after the Fable review). Claude leads the Android work again. Read `CLAUDE.md`, then
`GOAL.md`, `ROADMAP.md` and `PROGRESS.md`, then the
[operating notes](../android-operating-notes.md).

## Where things stand

- **Branch (2026-11-23):** Android is on `main` now (merged both ways, owner: "yes, then to
  main"; Android made an official part of the project). New work goes on a short branch cut
  from `main` in a worktree, then fast-forwards `main`. The old worktree
  `C:/Users/kaank/.local/share/opencode/worktree/08edba/android-remote-files-parity` and its
  branch are retired after 0.1.3. Never switch the branch of the shared `idm` checkout.
- **ANDROID.9 is built and checked (2026-11-22):** torrents on the phone (Wi-Fi only,
  seeding on Wi-Fi while charging), TorBox (add, cache hit and miss, My TorBox, "On
  TorBox" rows in Downloads), archives (password sheet, saved passwords, per-archive
  delete), Import a list (i1-i4, approved mockups), the paired-PC pass with its five
  findings fixed, and the t1-t7 side-by-side check. PROGRESS has each step, the
  matrix has the case results.
- **Releases:** the last public Android release is 0.0.9. 0.0.10 was installed on the
  owner's S24 but not published. The newest APK is a local test build, not a release.
- **Redesign:** Remote Files r1-r6 were built on `android-remote-files-redesign`
  (pushed, kept apart). It merges only after the release (owner, 2026-11-21).
- **Against `main`:** merged (2026-11-23). No desktop release without the owner's say-so.
- **Local state here:** `SiteTransferDtos.cs` shows as modified with no content change
  (line endings) and `.cortexkit/` is untracked. Never stage either.
- **`android-app` working copy** (`C:/Users/kaank/Downloads Archive/2026-11-20/Projects/Inbound/idm-android-trial`):
  older uncommitted ANDROID.9 attempt. ANDROID.9 was built here instead; don't copy from it.

## Next work, in order

1. **Start the 0.0.11 release on the S24 once, then publish it.** Fable reviewed ANDROID.9 and approved every fix
   (2026-11-22, [review](android-review-android9.md)). The signed APK is staged, not published:
   `artifacts/android-app/release-0.0.11/` (APK SHA-256 `034244f63bea3c904caeb751a7ee2925ee78a93b59b8f447ac872ae310a68c41`,
   versionCode 11, signed manifest and notes from `docs/android-release-notes-0.0.11.txt`; built from a clean Release
   output). `check-apk.py --release` and the release verifier passed. The S24 start passed on 2026-11-22 (installed in place over
   0.0.10, opened on Downloads with data kept; PROGRESS). Still owed: the owner's OK to publish. Then publish per `docs/android-updates.md`: APK and manifest on
   `android-v0.0.11`, the same manifest on `android-latest`, `latest=false` on both.
2. **Merge the redesign branch** after the release, then compare r1-r6 on real data and on the owner's phone.
   A trial merge on 2026-11-22 had two capability-list conflicts and passed its checks (PROGRESS has the resolutions);
   after the real merge, run the full Integration suite from this worktree, not from a folder under `%TEMP%`.
3. Every review finding is fixed or decided (2026-11-22, see
   [the earlier review](android-review-49f5663c-c745317f.md#step-6-and-the-remaining-items-2026-11-22)); the A6 and A9-15
   answers are the owner's. Later work: ANDROID.5/.6 real-account capture and deep Doze (ANDROID.14); expanded
   accessibility is optional (ANDROID.13).

## Ask the owner

- The decisions in [the review](android-review-android9.md#what-was-done) made while the owner was away (A9-6, A9-7,
  A9-15, A9-18, earlier A4) and in [the earlier review](android-review-49f5663c-c745317f.md#step-6-and-the-remaining-items-2026-11-22) (S4, S5, S7, S8, S10, M4).

## Remind the owner (after the release)

The owner kept these as built on 2026-11-22 and asked to be reminded later:
1. t3 draws Downloads without the speed card; the app keeps it (as a1-a3).
2. t3's On TorBox row has no pause button, no row for "Not sent", and can't be selected.
3. t5 on a paired PC: Ready rows have no file count or size (needs the PC Host to send them).
4. t6: the mockup extracts into a folder named after the archive; the Host extracts into
   the download folder itself.
Also known, not yet raised as decisions: Import a list records the source as "android",
mixed torrent results show "A torrent in the list", and closing the page mid-send doesn't
stop the send. Not seen on real data: t5 Get/Fetching/Failed rows, t3 Extracting, a magnet
with folders on the phone.

## Phone QA on the S24 (quick reference)

Record the display baseline first (mode 1, brightness about 60, timeout never: don't
change it), set mode 0 and brightness 1 for the check, restore and verify after. Ask
the owner to unlock; never bypass the keyguard. Never touch other apps (banking,
camera, Bitwarden) or the owner's own server in the host list; delete any capture
showing a notification or a password. Get tap targets from `uiautomator dump` bounds:
blind coordinates have already started a download by mistake. The paired test Host is
`tools/AndroidApp/paired-host-parity-fixture.py` over `adb reverse`; remove the pairing
afterwards.

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
