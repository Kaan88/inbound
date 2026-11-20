# Android mockup acceptance matrix — draft

Owner decisions (2026-11-20): B2 shows the selected Host's current unmetered rule. The user changes it in Settings. In A6, Stop keeps partial bytes and restart-from-zero is omitted for active downloads. C2 uses “Notify when the queue finishes” for the local completion setting.

On 2026-11-20 the owner accepted the verified isolated S3 upload flow for C4 screen parity. Production upload remains a separate feature. This closes the upload-flow scope decision; the remaining C4 screen comparisons still require Astra review.

Owner approval, 2026-11-20: C2 uses “Unmetered network” and enforces the existing unmetered rule. This approval covers that condition only.

**Status:** Scoped Astra approvals are recorded below; full screen and milestone acceptance remains open.

Scope is the implemented Android UI in `src/DownloadManager.Android/Screens/`
and its linked flows, compared with the approved screen exports in
[`android-app.md`](android-app.md) / `android-mockups/`. The owner requires
mockups to be treated as acceptance criteria, with side-by-side device captures
and flow checks; functional tests, source review, or a release do not establish
visual acceptance. ANDROID.9 remains paused and is excluded.

## Evidence notation and capture protocol

- **Precedent** records evidence mentioned in project notes, not fresh review.
  Paths are historical references and may be outside this checkout; availability
  was not established here. A named test or functional smoke is not a visual pass.
- For each row, capture the named mockup and the matching app state at the same
  device resolution/orientation and comparable system-bar state. Save original
  app captures under `artifacts/android-app/pairs/` beside the reference (do not
  edit either source image). Include device/API, APK/version, selected host,
  theme/dynamic-colour state, exact setup/steps, and any fixture/test ID.
- Recreate on the API 33 `inbound33` emulator only after rediscovering its live
  state and following the relevant runbook/script. Prefer disposable local
  fixtures (`tools/AndroidTrialServer`); use the loopback/ADB-reverse setup only
  where needed, and clean up only task-owned jobs, files, pairings and listeners.
  Do not install, wipe, or alter a device as part of this draft. For actual
  acceptance, inspect each pair at native size and record differences, flow
  outcomes, and Astra's independent code-plus-comparison verdict.
- Mockup filenames are relative to `docs/plans/android-mockups/`. `A`, `B`, and
  `C` refer to the sections in `docs/plans/android-app.md`. `C8` is the one
  light-theme reference, not a full light-theme screen inventory.
- A0/A1/A4/A5/C7/C8 have a [guided capture runner](../../tools/AndroidApp/CORE-SCREEN-PARITY.md);
  C1/C2/C5/C6 have a separate [queue/host capture runner](../../tools/AndroidApp/QUEUE-HOST-SCREEN-PARITY.md).
  Their presence and host-only tests do not establish device coverage.
- B1–B6 have a separate [link-intake capture procedure](../../tools/AndroidApp/LINK-INTAKE-PARITY.md)
  and guided runner. B1 requires Chrome's real Android Share chooser; the
  existing disposable link fixture supports the other documented local flows.
  Host-only validation is not device coverage or visual acceptance.
- A2/A3/A6/A7 have a separate [download-state capture procedure](../../tools/AndroidApp/DOWNLOAD-STATES-PARITY.md)
  and timed fixture runner. Its existence and host-only validation are not
  device coverage or visual acceptance.

## Approved screen inventory

| ID / implemented screen | Approved reference and state to compare | Precedent evidence / fixture or test known from records | Emulator recreation and current gap |
| --- | --- | --- | --- |
| A0 — First run | `a0-first-run.png` | Astra passed first launch before either permission in `core-a0-a5-c7-retry-20261120-045033`. | Astra passed native notification Allow and Deny and the battery Deny return in `a0-native-deny-20261120-053443` and its paired notification Allow run. Battery exemption grant and persistence remain open. |
| A1 — `DownloadsScreen`, empty/populated, search/filter, selection | `a1-downloads.png`; also `a2-downloads-later.png`, `a3-downloads-finished.png`; C8 for light | API 33 completion/selection, multi-select, notification and hash-matched fixture precedents in `PROGRESS.md`; phone/mockup precedent `artifacts/android-app/pairs/a1-downloads-s24-dark-0.0.6.png` is empty state only. API33 evidence under `android6-acceptance-0.0.5`, `inbound-talkback-20261119`, `inbound-notice-smoke-5` and `idm-android-resume-20261119-134123` | On disposable `inbound33`, prepare empty, active, queued, finished, failed and selected states with the owned fixture/scripts; capture each independently. Existing evidence is partial and does not cover full parity or state colours. |
| A2 — Downloads later/in-progress | `a2-downloads-later.png` | In a3-a6-a7-reviewed-20261120-032339, a real active fixture advanced from 32,768 to 98,304 bytes and finished with the expected hash. | Astra passed the representative dark list and live graph in `a1-a2-dark-mixed-20261120-054308`. Astra passed Night 07:00 matching the Host and generic Mobile in `a2-reported-scheduled-row-20261120-055710`. Other fixture variants remain open. |
| A3 — finished item/snackbar | `a3-downloads-finished.png` | Astra approved `a3-a6-present-20261120-042101`: Add visible, 16 px navigation gap, outlined folder action, and hash-matched completion. | Astra passed direct Open in `a3-snackbar-open-20261120-044317`; Photos rendered the expected PNG and its file hash matched. Mixed Downloads list remains open. |
| A4 — Add | `a4-add.png` | Astra passed the idle five-source sheet in `core-a0-a5-c7-retry-20261120-045033`. Available and unavailable routes were identified. | Clipboard offer and action routing require separate coverage. |
| A5 — New Download | `a5-new-download.png` | `core-a0-a5-c7-retry-20261120-045033` populated the exact loopback filename, 6.1 GB, byte-range support and eight requested connections. | Astra passed the revised slider and buttons in `a5-controls-final-20261120-050123`. Actual submission and populated queue/destination remain separate. |
| A6 — Details | `a6-details.png` | Astra approved `a3-a6-present-20261120-042101`: active one-connection Downloads wording and finished state, with matching payload hash. A6 Stop passed `b6-stop-retry-20261120-034215`. | Astra passed two independent ranges, the nested destination, the queue-specific rule and final hash in `a6-details-pause-final-20261120-054830`. Metered-network waiting remains open. |
| A7 — background notification | `a7-background.png` | Astra passed the expanded ongoing notice in a3-a6-a7-reviewed-20261120-032339. Its count, rate, name, ETA, progress, Pause all and Open were visible during a real transfer. | Astra passed the actual ongoing Open and Pause all actions in `a7-owned-record-actions-20261120-054749`. Finished and network-wait notices remain open. Android controls the surrounding notification shade. |
| B1 — share target / `CaughtDownloadSheet` | `b1-share.png`; Android share chooser followed by caught sheet | S24 actual Chrome share chooser and API33 Chrome chooser precedents; `inbound-chrome-share-20261119`, S24 `inbound-s24-links-007`; no named comparison pair | Open fixture page in Chrome, use Share link → Android chooser → Inbound; capture chooser and each caught-sheet state; Cancel must admit no job. |
| B2 — Caught link | `b2-caught.png` | Phone and paired-Host child destinations both stored the exact fixture payload in `b234-dest-20261120-025429` and `paired-b2-resumed-20261120-020635`. Astra confirmed More options opens and closes without submission, with range metadata and timing controls visible. | Selected-Host network-rule presentation and exact final styling remain open. |
| B3 — Signed-in browser download | `b3-signed-in.png` | Astra approved the compact local consent sheet in `b234-dest-20261120-025429`, including the synthetic report, local sign-in, disclosure and background dim. | Astra passed the completed local authenticated report and final hash in `b3-real-auth-20261120-053929`. Paired-Host consent and authenticated resume remain open. |
| B4 — page links (`PageLinksScreen`) | `b4-all-links.png`; link list/filter | `b4-present-20261120-042149` verified eight links, six checked archives, 689 KB totals and search restoring all eight; Astra passed controls and CTA. | Astra approved the final card inset and spacing in `b4-archive-final-20261120-043155`. Download submission remains open. |
| B5 — Downloads multiselect | `b5-select.png` | Astra passed the scoped five-job mix and three selections in `b5-mixed-retry-20261120-045121`. Active/paused bytes and the finished hash verified. Download again was disabled as approved. | Astra passed the final icons in the Light rerun `c8-actions-final-20261120-051650`. Named scheduled queue and bulk activation remain open. |
| B6 — expired link | `b6-expired-link.png` | Astra approved `b6-recatch-20261120-040546`: Cancel preserved the same 7% job; real WebView Confirm resumed that job at byte 1,310,720 and its completed hash matched. Earlier pasted-address recovery and A6 Stop passed `b6-stop-retry-20261120-034215`. | Other untested expired-link states and any remaining screen-wide review remain open. |
| C1 — `QueuesScreen` | `c1-queues.png`; empty/ready queue and queue actions | `c1-dropdown-20261120-035933` established running Main, scheduled Night, stopped Mobile and queue actions; Astra passed those workload states. | Astra approved the revised cards and sheet in `c1-present-20261120-042308`. Tapping the chart opened the same Host's live graph. The real Night queue started at 05:30 in `c1-schedule-fires-20261120-051952` while Mobile stayed stopped. The probe stayed foreground during this run. |
| C2 — queue and schedule editor | c2-schedule.png and the approved flow board | c2-clean-boot-20261120-035129 shows saved and reopened Mon–Fri 01:00–07:00 with charging and unmetered, a disabled schedule, and the visible Host footer. Astra passed the configured local state. | Review the no-schedule and remote editor, added dialogs and validation, plus save, cancellation and deletion paths. |
| C3 — Remote Files locations | `c3-sites.png` | Astra passed the SFTP unavailable-space state in c3-sftp-space-20261120-032528. Trust and browse succeeded and the post-browse location card showed its endpoint without a capacity value. | Astra passed 205 GB free in `c3-sftp-capacity-20261120-044216` and two active downloads in `c3-c4-active-20261120-043917`. Astra passed the three browsable protocols in `c3-three-protocols-20261120-045409`. Astra passed the final outlines and button in `c3-icons-rerun-20261120-050646`. Cloud sign-in expiry and concurrent uploads/downloads remain open. |
| C4 — Remote Files folder | `c4-site-browser.png` | Astra approved the nested WebDAV list, seven ordered rows, 12/48 folder counts, two selected files and destination in `c3-c4-active-20261120-043917`. Both hashes and delete Cancel/Confirm passed. The isolated S3 upload passed separately in `c4-conditional-ui-20261120-043617`. | Paired-Host destination and additional folder action states remain open. |
| C5 — host switcher (`HostSwitcherSheet`) | `c5-switch-host.png`; This phone and paired host choices | Astra scoped functional PASS in `c5-c6-pulses-20261120-041845`: three actual Host choices and exclusive selection. | Astra passed three revised selections and the active Host A state at 76.8 KB/s in `c5-c6-final-20261120-043359`. The queued Host variant remains open. |
| C6 — Downloads on a remote Host | `c6-remote-host.png`; selected Host, destination banner, graph and populated remote rows | Astra scoped functional PASS in `c5-c6-pulses-20261120-041845`: running and finished rows, matching completed hash and live graph; active bytes advanced 655,360 → 917,504 over four seconds. | Astra approved the final mixed layout and both Pause glyphs in `c5-c6-final-20261120-043359`. Touch filter selection, reset, Back from Queues and closing search passed. Hardware-keyboard, Pause activation and other row variants remain open. |
| C7 — Settings | `c7-settings.png` | Astra passed the dark root in `core-a0-a5-c7-retry-20261120-045033`. Search narrowed to Catching links and Site logins was present. | Other appearance states and subsection navigation remain open. |
| C8 — Light Downloads | `c8-light.png` | Astra approved the final five-job Light view in `c8-actions-final-20261120-051650` with Dynamic colour off, actual progress and the final Pause/Resume controls. | Named schedule, varied file types, the larger reference workload and longer chart history remain open. |

## Implemented secondary screens and dialogs

These are implemented UI surfaces linked from the approved screens. Where no
dedicated approved export exists, the approved Android flow-review board is the
reference for the relevant C1/C2/C7 addition; retain the original screens as
context and do not treat the board's approval as acceptance.

| Surface | Reference / predecessor evidence | Recreate and unresolved acceptance |
| --- | --- | --- |
| `QueueEditorScreen` (queue create/edit, conditions, caps, completion) | C1 plus approved C1/C2 flows in `android6-flow-review.html`; queue persistence/tests and S24 schedule/queue walkthroughs exist | Capture create, edit, populated conditions/caps, save/cancel and queue actions. Verify phone-only versus selected-Host fields. No complete comparison set identified. |
| Schedule editor/dialogs and schedule deletion confirmation | C2 plus approved flow board; S24 save/reopen, cancel deletion and selective deletion precedent | Capture new/edit/disabled/configured schedule, validation, cancel and confirm-delete. Compare dialog bounds and scroll/footer. Existing evidence is functional, not all visual states. |
| `FolderSheet` / destination picker | A4/A5; Android system folder UI where applicable | Use an emulator-owned disposable destination; capture Inbound sheet and system picker separately. Verify returned destination and cancel path. No specific visual pair located. |
| `RefreshLinkSheet` / refresh-address confirmation | B6 | B6 browser confirmation and cancellation passed in `b6-recatch-20261120-040546`; other untested states remain open. |
| `PairingSheet` and host switcher subflows | C5/C6 | Capture normal and certificate-warning/trust paths against approved host flows; only disposable endpoint, no actual credentials. |
| Remote Files location add/edit/fix dialogs and actions | C3/C4 and approved ANDROID.8 flow/reference states | Capture location types and validation/errors only where implemented and fixture-supported; compare local vs remote boundaries. Existing functional WebDAV evidence is not a complete screen inventory. |
| `AppUpdatesScreen` / update-ready dialog | `android-update-prompt-review.html`, approved compact update design; C7 Settings location | Historical render review and updater tests exist; capture installed app's offer, notes, cancel/install handoff and Settings changelog on emulator. HTML render QA is not device parity. |
| `HostSettingsScreen` / remote host details | C5/C6 and Android app flow plan | Capture selected-host identity, connection state and local/remote settings distinction. No specific pair located. |

## Known inventory limits / next pass

The following notes are historical checkpoints; the current table above supersedes their earlier open-state assertions.

API 33 captures `android-c1-c2-20261120-005257` cover the empty Main
queue and its no-schedule editor, without creating or changing a queue.
`android-a2-active-20261120-005316` captures live A2 progress (fixture
16,384 → 180,224 bytes before capture); the completed 16 MiB file
matched SHA-256 `341aacac661ccb210720bedaa9ead5d668fe5ea41a73532fc147c71e34040df1`
and was removed by exact path after hash verification. Configured C2,
running/scheduled C1, queued A2, A3, A6 and A7 still need matching
captures. `android-c1-disposable-20261120-011021` adds a stopped
Night queue and its no-schedule editor. `android-b4-search-20261120-011210`
exercises header search and link selection without submitting; B5 is
still open. `android-host-local-20261120-011334` captures the unpaired
Host screen and disabled blank form without attempting a connection;
neither screenshot is the C6 remote Downloads reference.

The API 33 core run `android-core-a4-c7-v2-20261120-002905` produced A4 and
C7 pairs and passed the source-order, unavailable-action, Site logins row,
and Settings-search checks. A4 was captured without a clipboard offer or
active downloads; C7's Site logins row leads to Remote Files rather than
an independent browser-credential store. Neither pair has Astra approval.
The rebuilt probe produced another set in
`android-core-refined-20261120-003857`, with the A4/C7 functional checks
still passing. Its A5 pair verifies that the rounded outlines and
destination icon render, but the form is blank with the keyboard open;
it does not compare the populated mockup state.
The follow-up `android-a5-metadata-20261120-004228/a5-populated-pair.png`
shows the populated form with the keyboard dismissed. The disposable
fixture returned 200 to HEAD and the probe showed the expected filename
and successful metadata state without submitting a download. This is
not yet a visual approval.

`android-c4-upload-ui-20261120-003402` captures This phone selection,
confirmation and the resulting remote listing. Cancel sent no request;
Confirm sent exactly one conditional PUT with the MediaStore source bytes.
This is API 33 loopback evidence from the isolated probe, not production
upload availability or visual acceptance.

First C3/C4 captures: `remote-files-parity-20261119-230316` under
`%LOCALAPPDATA%\Temp\opencode`. Both selected files matched their fixture
SHA-256, but the three idle WebDAV cards and root Downloads destination do
not reproduce all reference states. Astra reviewed the initial pairs and
held sign-off; see `PROGRESS.md` for the fixes and evidence.
The later `remote-files-delete-20261119-231933` pairs show the compact
amber transfer bar and cloud navigation icon, but still do not show the
active upload/download, expired grant, or SFTP/S3 reference states.
The same run verified that cancelling delete did nothing and confirming
it removed only the fixture's `notes.pdf`. This is flow evidence, not a
parity approval.
The `remote-files-nested-20261119-232442` C4 pair shows the selected
`Downloads/Inbound/Music` destination. Both files hash-matched there.
The newer `remote-files-c4-match-20261119-233226` pair also matches row
ordering, dates, and direct child counts. The fixture file sizes are
smaller than the reference data; icon and spacing differences remain.
The separate `remote-files-sftp-only-20261119-233503` API 33 run verifies
first-use SFTP: the displayed fingerprint matched, Cancel sent no login,
and Trust led to an authenticated listing. This does not establish the SFTP
card's visual parity or cover changed keys and paired Hosts.
The `remote-files-c4-outline-20261119-233816/c4-pair.png` comparison shows
the outlined audio, document, archive and download glyphs and the revised
breadcrumb and header. The same run passed listing, nested download hashes,
delete and cleanup. It still showed compressed vertical spacing down the
rows and a lower footer action. The follow-up
`remote-files-c4-spacing-20261119-234214/c4-pair.png` has the adjusted
row heights and footer; order, child counts, nested downloads, delete and
cleanup passed again. Neither pair covers phone-to-site upload or has
Astra approval.

The first fresh A0 pair is `android-core-parity-20261119-234502/a0-pair.png`.
It captured real setup before granting either permission. The icon artwork
and top spacing differed; a follow-up changes those without changing the
permission flow or download destination. The reference's illustrative
`Downloads/Inbound` location is not the app default: the owner selected
the shared `Download/` folder directly (see `android-build-plan.md`).
The unattended run did not cover A1, A4, A5, C7 or C8.
The updated A0 pair, `android-core-parity-v11-20261119-234824/a0-pair.png`,
shows the outline icons and aligned top section; card spacing and the
post-permission state still need review. C3 S3 fixture onboarding passed
in `remote-files-s3-only-20261119-234721` (Backups card and `pub/readme.txt`);
it is not the combined C3 mockup state or an Astra verdict.
The extended unattended run, `android-core-more-20261119-235310`, produced
fresh API 33 pairs for A0, **empty** A1, A4 **without** a clipboard offer,
**blank** A5 and the C7 Settings root. It cleaned up only the probe.
These captures are ready for comparison, not visual approvals; populated
A1, the A4 clipboard offer, filled A5 and light C8 remain open.

1. Source file names and historical notes establish implemented surfaces; they do
   not prove every possible state or control was inspected. The Android app source
   and historical capture directories are not all present in this worktree, so
   cited evidence availability and pixel-level details must be rechecked by the
   milestone owner before relying on them.
2. Existing evidence is strongest for functional fixtures and four dark/empty
   phone pairs (C1/C2/C7/A1), plus selected A/B flow checks. It does not establish
   populated C1/C2/C7, all dialogs, all A/B states, or C3/C4 visual parity.
3. C3/C4 acceptance was reopened despite ANDROID.8 functional/release checks and
   its earlier Astra implementation PASS. Capture both against the approved
   references, walk all relevant controls and flows, then obtain a new Astra
   review of source and side-by-side comparisons before closing.
4. Resolve the full inventory against current `src/DownloadManager.Android`
    source and add any implemented surface missing above before claiming coverage.
    Any proposed deviation requires explicit owner approval; a renamed reference
    label (“Sites” → “Remote Files”) is not itself a layout approval.
5. The current C3/C4 runner's three WebDAV cards are a functional fixture,
   not a substitute for C3's active summary, NAS, cloud sign-in and S3
   states. C4 also needs independent evidence for the This phone tab,
   deletion confirmation, and the selected-Host destination. Record these
   separately from the two-file hash check.
