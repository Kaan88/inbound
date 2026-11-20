# Android mockup parity handoff

**Android parity resumed (2026-11-20).** The existing worktree is on branch `android-remote-files-parity` based on `66270193`. The owner requested Luna or Terra for routine work and Astra for independent source and visual approval. Terra handled the first visual fixes because Luna was unavailable in this session. ANDROID.9 and production Remote Files upload are separate feature work. See the checkpoints below for open states.

The owner approved “Notify when the queue finishes” for C2 on 2026-11-20. The private signed snapshot has been delivered. Full Android parity approval is open.

The isolated S3 upload writes through a device stream. The C3 Locations
summary counts tracked site transfers, so a genuine combined upload and
download summary depends on tracked upload support.

### Live Downloads, Details, and notification controls (2026-11-20)

- Astra approved native C3 location search in
  `c3-location-search-20261120-082854`. Mixed-case WebDAV and SFTP queries
  each returned only their exact location, including the SFTP capacity.
  An unmatched query showed the empty message. Closing search restored all
  three locations in their original order. The WebDAV fixture saw no
  file payload request, and the probe was removed.
- Astra approved the representative five-job Dark A1/A2 workload in
  `a1-a2-dark-mixed-20261120-054308`. Both active transfers advanced,
  the graph and native controls showed actual state, and live totals
  were 97.8 and 98.2 KB/s. In `a2-reported-scheduled-row-20261120-055710`,
  Astra approved the named Night row matching the Host's reported 07:00
  and the unscheduled Mobile row showing Queued. Queue reassignment,
  disabled and running schedules, and expiry are covered by mobile tests.
  The broader file types remain open.
- Astra approved the mixed A3 completion in `a3-mixed-formal-20261120-060116`.
  The exact completion snackbar and Open were visible alongside one active,
  one paused, one queued and two finished jobs. The newly finished file
  matched its SHA-256 and was removed after the probe was uninstalled.
- Astra approved the system chooser and probe dialog in
  `b1-native-share-final-20261120-060519`. Chrome shared the exact
  fixture Archive link to Inbound Probe. Cancel left the Downloads count
  at zero and created no new fixture-named file.
  In `b1-native-add-dismissal-20261120-072402`, Astra approved Add file
  prefilling the Archive URL without its fragment. Cancel closed the form,
  returned to the empty Downloads list and preserved the shared file
  inventory. In `b1-native-page-final-20261120-074410`, Astra approved
  Open page from the native chooser. The probe rendered the exact fixture
  page. Closing its browser returned to unobscured Downloads with zero jobs
  and an unchanged shared file inventory.
- Astra approved C5's queued Host B in `c5-queued-host-20261120-074518`.
  The switcher displayed 1 queued for B while idle Host A was selected.
  Switching to B showed its save banner and the exact queued file. The
  disposable Host reported zero received bytes. The fixture saw zero HEAD
  or GET requests and the output stayed absent through cleanup.
- Astra approved B2's timing controls and selected-Host network status in
  `b2-timing-network-unique-20261120-075214`. Start now and Later
  selected exclusively, all three labels fit with 48 dp targets, and the
  phone showed Any network with the rule off. The paired headless Host
  omitted that control in both sheet sizes. Both Hosts stayed at zero jobs.
  The checkmark sits farther from its label than in the reference.
- Astra approved B4 submission in `b4-real-submission-20261120-061608`.
  The actual page selected two of four links and Downloads showed exactly
  two finished jobs. The two output hashes matched distinct payloads, and
  neither unselected file was requested. Cleanup removed the two exact
  hash-matched outputs after the probe was uninstalled.
- Astra approved C1's real timed stop in
  `c1-timed-stop-final-20261120-075414`. Night started at 08:00 and
  paused at 08:05 without manual input. All Night responses closed and
  its partial count held at 23,330,816 bytes for four seconds.
  Mobile received zero bytes. Queues reported the next Night start the
  following day at 08:00. The isolated probe and fixture mapping were removed.
- Astra approved the empty C2 schedule editor in
  `c2-no-schedule-final-20261120-060648`. The empty times and seven
  disabled day controls matched the disposable queue's state. In
  `c2-named-delete-20261120-061717`, the confirmation named Weekdays,
  distinguished Delete in the theme's error colour and explained deferred
  save. Cancel preserved the one disabled schedule, its weekdays, times and
  conditions after saving and reopening. In
  `c2-positive-delete-20261120-064925`, Astra approved the real Delete
  action. Discard restored Weekdays. Repeating Delete and saving removed
  the schedule while the empty Night queue remained. Astra approved the
  paired Main editor in `c2-paired-editor-20261120-065822`. It showed
  the Host-local timing footer and headless Host controls. Back left the
  exact queue and schedule snapshot unchanged. In
  `c2-paired-save-live-20261120-080656`, Astra approved saving and reopening
  one disabled Monday to Friday 01:00 to 07:00 schedule on the paired Main
  queue. The draft left its Host snapshot intact before Save. The phone's
  Main remained unscheduled, and the disposable Host had zero jobs or files.
- Astra approved B5 bulk Pause in `b5-bulk-pause-quiescent-20261120-063258`.
  The action closed the selected active fixture's connection. Its fixture
  count held at 2,899,968 bytes for six seconds while the unselected
  active transfer advanced by 262,144 bytes. The actual rows showed two
  paused, one advancing, one queued and one finished. The probe and
  fixture mapping were removed. Unmatched partials stayed in place. In
  `b5-bulk-resume-live-20261120-080941`, Astra approved Resume selected.
  One selected paused job made a new GET and the fixture sent another
  278,528 bytes. The unselected paused job stayed at 1,474,560 cumulative
  bytes and two GETs. This fixture restarts from byte zero because it
  lacks Range support.
- Astra approved five file-type icons in the real C8 Light workload in
  `c8-typed-a1-failed-live-20261120-081102`. The Later video stayed queued
  without a file GET. A separate searched Downloads view showed a real
  404 failure and enabled Retry with no completed output. In
  `a1-retry-owned-20261120-083311`, Astra approved one explicit Retry.
  The same row moved from 404 to Finished, the fixture saw two additional
  GETs, and the 16 MiB output matched SHA-256. Cleanup removed the exact
  hash-matched output after stopping the probe writer.
- Astra approved a real paired C4 WebDAV download in
  `c4-paired-pinned-final-20261120-064240`. The probe matched and pinned
  the disposable Host's HTTPS certificate. Cancel retained the selection
  and made no payload request. Confirm made one request and wrote the
  1,048,576-byte FLAC with its expected hash in the exact Host child.
  The decoy child stayed empty and the file never appeared on the phone.
  The owned probe, reverse, relay, and fixture Servers were stopped.
- Astra approved C6 touch Pause in `c6-touch-pause-final-20261120-064414`.
  The selected paired Host reported 2,228,224 received bytes four seconds
  apart after one tap. The Android row offered Resume and the finished
  file kept its matching hash. In `c6-keyboard-integrated-20261120-071829`,
  Astra approved Tab to search, directional focus on Finished and Pause,
  and one centre-key activation. The Host's paused transfer held at
  3,276,800 received bytes for four seconds. A physical keyboard remains
  outside this emulator proof.
- Astra approved the C7 Settings search and Back route in
  `c7-settings-insets-20261120-063758`. Catching links showed its
  guidance and browser entry with the keyboard dismissed. Site logins
  opened the disabled local Remote Files screen below the status bar.
  The enabled Remote Files list kept its layout and showed the real
  205 GB SFTP capacity in `c3-enabled-insets-20261120-064627`.
- Astra approved the final A6 Details in
  `a6-details-pause-final-20261120-054830`. Two independent active ranges
  advanced from 262,144 to 3,014,656 bytes each, and the final hash matched.
  The nested Downloads path and queue-specific unmetered rule were shown
  with the global rule off. The owned file and directories were cleaned up.
  Metered-network waiting remains open.
- Astra approved the real ongoing notification actions in
  `a7-owned-record-actions-20261120-054749`. Open returned to Downloads
  and Pause all left the sole job paused with 2,097,152 bytes unchanged
  for four seconds. The isolated app and mapping were removed. The owned
  incomplete fixture was retained. In `a7-finished-proof-20261120-061054`,
  Astra approved a real background completion with the exact 156-byte PNG
  hash and confirmed that the notification's Open action displayed the
  expected colour in Photos. The hash-matched output was removed after
  uninstall. The network-wait notice remains open.
- B3 completed the protected fixture report after local consent and matched
  its SHA-256 in `b3-real-auth-20261120-053929`. The exact output was
  removed after stopping its writer. In `b3-paired-hash-final-20261120-080530`,
  Astra approved the exact pinned HTTPS recipient and consent actions.
  The paired Host saw zero report requests before consent and after
  Cancel. Send allowed one authenticated metadata request. Download
  created one Host job and its exact 1,379-byte report matched SHA-256
  inside the owned category folder. The phone received no output.
  In `b3-auth-resume-final-20261120-082402`, Astra approved local authenticated
  Resume. After a settled Pause, the same job requested an authenticated
  range at byte 2,097,152 and received the entire remaining suffix.
  The 16 MiB file matched SHA-256. Paired-Host authenticated resume remains open.

### Native first-run permission returns (2026-11-20)

Astra approved the notification Allow and Deny returns. The isolated probe
matched Android's permission state in `a0-permissions-allow-20261120-051604`
and `a0-permissions-deny-20261120-051518`. The final
`a0-native-deny-20261120-053443` run chose Deny in the battery prompt,
returned to first-run setup, and verified that the probe's battery exemption
stayed unchanged. The exact probe package was uninstalled after each run.
Battery exemption grant and persistence remain open.

The emulator APK is labeled Inbound Probe so Android's chooser can distinguish
it from the installed Inbound app. Both labels were verified in the packaged
probe, and the regular build resolves Inbound and app.inbound.android.

### Core screens and Downloads selection (2026-11-20)

- Astra passed first launch before the permission prompts, the idle Add sheet
  and the dark Settings root in `core-a0-a5-c7-retry-20261120-045033`. The
  Settings search narrowed to Catching links and the Site logins row was visible.
- The same run populated A5 from a loopback metadata response with a 6.1 GB
  size, byte-range support, exact fixture filename and eight requested
  connections. Astra passed the slimmer slider and 56dp button fills in
  `a5-controls-final-20261120-050123`. The capture submitted no download.
- `b5-mixed-retry-20261120-045121` verified three selected real jobs in active,
  paused and queued states, alongside an unselected active and finished job.
  The finished file matched its hash. Astra passed the revised B5 icons and
  C8's five-job Light view in `c8-actions-final-20261120-051650`. The probe
  verified Light with Dynamic colour off, slim Pause and neutral outlined
  Resume. The named schedule, varied file types and longer chart history
  remain open. Some owned fixture partials remain on the emulator.

### Verified Remote Files and completion actions (2026-11-20)

- Astra passed the direct A3 snackbar Open in `a3-snackbar-open-20261120-044317`.
  Photos displayed the expected synthetic PNG, the completed SHA-256 matched,
  and scoped cleanup passed. See the mixed-workload checkpoint above.
- Astra passed the C3 active WebDAV view in `c3-c4-active-20261120-043917`.
  It showed two downloads at 57.3 KB/s and fixture bytes advanced from
  1,638,400 to 1,900,544. The trusted SFTP browse in
  `c3-sftp-capacity-20261120-044216` showed 205 GB free from the fixture's
  204,800,000,000-byte response. Astra also passed all three browsable
  protocols together, then the outlined icons and full-height Add location in
  `c3-icons-rerun-20261120-050646`. Expired cloud sign-in and concurrent
  upload/download remain open.
- Astra passed the C4 nested WebDAV list in `c3-c4-active-20261120-043917`.
  Order and 12/48 folder counts matched, both selected FLAC hashes passed in
  Downloads/Inbound/Music, and delete Cancel preserved the fixture while
  Confirm removed only notes.pdf.

### Scoped parity evidence update (2026-11-20)

Pushed checkpoints: `f3707bc4` and `f085e394`. Full Android visual
acceptance remains open. Earlier entries document their original checkpoints.

- **A3/A6:** Astra approved `a3-a6-present-20261120-042101`: Add remained
  visible, the navigation gap was 16 px, and the folder action was outlined.
  Active Details used one connection and Downloads wording; the finished transfer
  matched its hash. A3 mixed-list/Open tap and A6 nested destination, constraints
  and multiple connections remain open. A6 Stop passed separately in
  `b6-stop-retry-20261120-034215`.
- **B4:** Astra approved the Archives state in
  `b4-archive-final-20261120-043155`. Final spacing aligns the eight rows and
  checkboxes. Six archives were checked, both totals showed 689 KB, and
  clearing search restored all eight links. Submission remains open.
- **B6:** Astra approved `b6-recatch-20261120-040546`: Cancel preserved the
  same 7% job; a real WebView Confirm resumed that same job at byte 1,310,720
  and its completed payload matched SHA-256.
- **C5/C6:** Astra gave a scoped functional PASS for
  `c5-c6-pulses-20261120-041845`: three exclusive Host choices and a C6
  running-plus-finished list, matching completed hash, and live graph; the
  active transfer advanced from 655,360 to 917,504 bytes over four seconds.
  Astra passed the live switcher and final C6 layout in
  `c5-c6-final-20261120-043359`. Host A showed 1 active at 76.8 KB/s. Finished
  excluded its active job, All restored both, Back from Queues preserved the
  filter session and closing search hid it. Hardware-keyboard and Pause
  activation remain open.
- **C1:** Astra approved the revised populated cards, action sheet and chart
  navigation in `c1-present-20261120-042308`. The chart opened the same Host's
  live Downloads graph. In `c1-schedule-fires-20261120-051952` Night
  started at 05:30 on the unchanged device clock and advanced from 131,072
  to 262,144 bytes. The stopped Mobile control stayed at zero. The probe
  cleaned up its app and mapping; shared fixture data was retained.
- **C4:** The scoped S3 upload rerun `c4-conditional-ui-20261120-043617`
  verified both Back paths, Cancel with zero PUTs, Confirm with one
  conditional PUT for the exact 34 MediaStore bytes, and owned cleanup.
  The separate C4 folder-list visual check remains open.

### C1 workload and A3/A6 recapture (2026-11-20)

- The C1 probe showed Main running, Night scheduled for its actual Host
  time, and Mobile stopped with one job apiece. The live Main transfer
  advanced from 262,144 to 393,216 bytes. The real queue dropdown admitted
  each fixture to its intended queue, and long press opened the Mobile
  queue actions. Astra passed those states. Chart navigation, Stop styling,
  progress track and action-sheet presentation still need visual work.
  Evidence: c1-dropdown-20261120-035933.
- The revised A3 completion message kept Add visible in the API 33 capture.
  Astra asked for less space above navigation and a neutral outlined folder
  action. A6 passed the revised active controls and captured finished state.
  The connection note still needs consistent public-folder wording. The
  transfer hash matched. Evidence: a3-a6-recapture-20261120-040258.
- The checkpoint is on android-remote-files-parity. It records scoped passes
  while the remaining states and all-screen review stay open.

### Configured C2 approval and link-intake follow-ups (2026-11-20)

- Astra passed the configured local Night queue editor and its Host footer.
  API 33 saved and reopened the disabled Weekdays schedule with Mon–Fri,
  01:00–07:00, charging and unmetered. The weekday gaps and battery row align.
  The footer fits in that viewport. Evidence: c2-clean-boot-20261120-035129.
  Other editor states and dialogs still need review.
- B4 now calculates the checked category's size independently of all selected
  files. The capture verifies the archive names, sizes and check states, the
  unchecked image and PDF, and both totals. B6 gained a real browser
  confirmation and cancellation fixture. These lanes are pending on-device
  verification. Host-only suites passed 555 mobile and 40 capture tests.

### B6 recovery and C1 scheduler checkpoint (2026-11-20)

- Astra approved the B6 expired-link sheet and recovery by pasted replacement.
  The emulator also verified A6 Stop on the same job. Retry and the replacement
  both requested byte 1,327,104, and the finished file matched SHA-256. The
  owned file, probe and reverse mapping were removed. Evidence in the Codex
  chat's local visualizations directory: b6-stop-retry-20261120-034215.
  Browser re-catch, confirmation and cancellation remain open.
- C1 cards now display the next timed start from the selected Host, including
  applicable repeats. The screen removes elapsed times and uses “Schedule
  enabled” when an exact start is unavailable. Astra passed source review.
  47 scheduler and Host regression checks passed. The isolated workload
  runner and C1 visual comparison remain to be exercised.

### Scoped API 33 checks (2026-11-20)

Evidence bundles below are in this Codex chat's local visualizations directory.

- Astra passed source and visual review for the expanded **A7 ongoing
  notification**. A real transfer populated its rate, progress, ETA, Pause all
  and Open. The run verified the finished payload hash. It did not tap these
  actions or cover the finished and network-wait notices.
  Evidence: a3-a6-a7-reviewed-20261120-032339.
- The same run captured **A3** completion and **A6** active and finished
  Details. The new completion snackbar uses the Downloads coordinator so Add
  stays accessible. A6 now preserves the local nested destination, uses the
  actual unmetered Host or queue rule, and adjusts the active action and bar
  layout. Astra passed the A6 source updates. The revised A3/A6 probe built.
  Fresh visual review is open.
- Astra passed the **C3** SFTP unavailable-space state. The isolated fixture
  proved no login before trust, authenticated browse afterward, and an accurate
  location card without a capacity value on return. C3 positive capacity and
  mixed and active states remain open. Evidence: c3-sftp-space-20261120-032528.
- **B6** retried the original address at byte 1,458,176, resumed the same job
  from its replacement at that byte, and matched the final hash. Astra passed
  that flow and the updated badge, actions and scrim. The follow-up reduced
  the action gap and bottom padding and matched the dark surface color.
  The separate A6 Stop attempt showed Cancelled at 8% and preserved a partial
  file. The runner now verifies that state and presses Retry. The new
  B6/Stop lane has not yet completed. Evidence: b6-reviewed-20261120-032108
  and b6-stop-final-20261120-032832.
- **C2** saved and reopened the revised local Weekdays schedule. The owner
  approved the completion wording, and Astra verified the times, selected
  weekdays, conditions, day size, limits and footer. The equal-gap and
  section-alignment follow-up built. The device-command timeout prevented
  a new C2 comparison. Evidence: c2-reviewed-20261120-031734.
- The mobile suite passed 554 tests and the host capture suite passed 40.
  The signed private snapshot delivered earlier predates these visual changes.

### Earlier verified checkpoint (superseded where noted above)

- B2: both the paired Host and the phone wrote the expected bytes into the chosen child folder. B3: Astra passed the compact signed-in sheet. B4: six checked archives and their combined 689 KB passed; Astra requested that the unselected files remain visible, the group total appear beside the selector, and the row layout change. The follow-up UI builds and still needs recapture. Evidence: `b234-dest-20261120-025429`.
- B5: the real five-job mix, exact three selections and disabled Download again passed. Astra approved selection styling/menu; visible transfer progress, a scheduled queue time and final toolbar-icon comparison remain open. Evidence: `b5-verified-20261120-023412`.
- B6: the old URL expired after a nonzero pause. The replacement resumed at byte 1,458,176; the finished file matched SHA-256 and the probe cleaned up. Evidence: `b6-pulsed-20261120-025722`. Final Astra review of this pair is pending.
- C2: configured weekdays and both conditions persisted, and the timing footer was visible. The updated day buttons/clock are captured in `c2-circles-20261120-030306`; final visual review is pending. The owner approved the unmetered condition and its accurate label.
- The owner accepted isolated S3 for C4's upload flow, approved B2 showing the selected Host's current unmetered rule with changes in Settings, and approved A6 Stop keeping partial bytes and omitting restart-from-zero for active downloads. B2/A6 implementations built in `parity-visual-20261120-030222`; the latest Stop and network affordances still need device verification. The C3 optional SFTP free-space lookup was added afterward and the Runtime project built. Rebuild Android and verify the real SFTP return or unavailable case.
- A6 active progress and rate passed scoped review. A3 completion, finished A6 and A7 expanded notification need verified captures. The most recent A7 capture attempt did not establish its notification actions; its runner has since been adjusted. C1 workload, C3/C4 locations and transfers, three-Host C5, active C6, and the other A/C states remain as tracked in the acceptance matrix. No all-screen sign-off or release.
- Latest completed suites: 551 mobile tests, six engine metadata tests, 39 host-side capture checks. The earlier focused Host suite passed 324 with four skipped. After adding Stop, the expanded suite found four capability expectation failures (344 passed, six skipped); the expectations were updated and the rerun was interrupted on pause. Do not count the rerun as passed.
- The dedicated inbound33 emulator was checked: probe package absent and no ADB reverse mappings. Earlier A-series tests may retain uniquely named synthetic outputs; use their preflight inventories and exact hashes if cleaning up. No production cleanup or device settings changes are needed. The independent review and test runs have been stopped. Nothing was committed, merged or published.


Owner decisions (2026-11-20): B2 shows the selected Host's current unmetered rule. The user changes it in Settings. In A6, Stop keeps partial bytes and restart-from-zero is omitted for active downloads.

On 2026-11-20 the owner accepted the verified isolated S3 upload flow for C4 screen parity. Production upload remains a separate feature. This closes the upload-flow scope decision; the remaining C4 screen comparisons still require Astra review.

**Earlier resumed checkpoint.** The previous pause checkpoint below is historical. The owner approved C2’s unmetered condition and label. The B5 stale-menu source recheck passed. The focused Host suite passed 324 tests with four skipped.

### Android parity evidence, 2026-11-20

- The C2 editor saved and reopened the disabled Weekdays schedule for Night. The Host timing footer was visible without scrolling on inbound33. Both pairs are in `c2-owner-20261120-023106`. The owner approved “Unmetered network” and its existing meaning.
- In `b5-verified-20261120-023412`, the real five-job fixture selected the active, paused and queued rows, left the other two unselected, and verified that Download again was disabled. Astra passed the selection styling and overflow. Measurable progress, a scheduled start and outline toolbar icons remain open for the full screen.
- In `b234-owner-20261120-024107`, B2 showed range metadata and the phone Change action. The B3 protected report and its HEAD request both used the synthetic session. Astra passed B3 controls/disclosure and B2 metadata/destination visibility. The next build addresses the signed-in scrim, disclosure length and B2 timing and queue actions.
- The focused Host suite passed 324 tests (4 skipped), the mobile suite passed 550 and 39 capture checks passed. B6 partial recovery, phone-folder payload verification, full-screen visual approval and feature resumption remain open.

ANDROID.9 feature
implementation (TorBox, torrents, archives) remains separately paused. The
parity work lives in the `android-remote-files-parity` worktree on the branch of
the same name, at `66270193` with substantial **uncommitted** changes. Preserve
them; do not reset this worktree or mix in the ANDROID.9 checkout. Nothing from
the parity worktree was merged or published. Android **0.0.8** remains the
published release; **0.0.9 is not release-ready**.

## Latest pause checkpoint

- B2 paired folder selection passed on inbound33/API 33 and Astra gave the
  scoped B2 source review a PASS. Full visual acceptance is open.
- C2 saved and reopened the disposable disabled Night weekday schedule
  (01:00–07:00, charging and unmetered). The configured editor pair is in
  `c2-configured-resumed-20261120-021759`. The distinct footer capture failed
  because the scroll assertion was not satisfied. The owned probe was removed.
- B5's menu, selection styling, exact selection restoration and atomic
  expected-unqueued precondition are in the dirty worktree. The signed
  `parity-b5-resumed-20261120-021617` build includes the lifecycle and queue
  fixes. The first mixed fixture attempt failed; the repaired runner passed
  its eight host tests and awaits a live rerun. The owner approved disabling
  Download again for unfinished-only selections.
- Astra withheld visual approval for B3, B6 and C5. Follow-up browser, refresh
  sheet and Host-switcher edits need a clean build and recapture. The B6
  partial-expiry fixture work was stopped at pause. The dispatcher suite had
  two positional capability-assertion failures (313 passed, 4 skipped).
  The assertions were updated and the broader rerun was stopped at pause.

## Start here in the next harness

- Open the existing worktree at
  `C:\Users\kaank\.local\share\opencode\worktree\08edba\android-remote-files-parity`.
  Read `AGENTS.md`, `CLAUDE.md`, `GOAL.md`, `ROADMAP.md`, and `PROGRESS.md`.
  Run `git status --short --branch`. Cloning the branch alone will not recover
  the uncommitted edits or the evidence under `%LOCALAPPDATA%\Temp\opencode`.
- **The emulator work already in flight at pause finished.** OpenCode shell
  `sh_1f605a33d001O1aJL395XEhKir` (PID at launch `207512`) was running paired
  B2 followed by B5 on `emulator-5554`. B2 stopped before submission because
  two WebView nodes matched `Archive`; reuse the clickable-only selector
  from `capture-link-intake-parity.py` before retrying. B5 passed on API 33:
  all three payloads matched SHA-256, exactly three rows were selected, and
  the bulk menu pair was captured in `android-b5-setup-20261120-015634`.
  B5 logged successful probe and reverse cleanup. No new runs were started
  after pause. Leave the physical device, production app, and unrelated
  Download files alone.
- **B2 checkpoint after resume:** Astra passed the scoped source recheck. The
  fresh signed API 33 probe and disposable Server smoke passed with the expected
  file hash in the selected child and an empty decoy. Evidence: `artifacts/android-app/pairs/paired-b2-resumed-20261120-020635/`.
  Full B2 visual parity and phone destination remain open.
- **B2 at the earlier pause:** the caught-download sheet exposes **Change** only on a paired
  Host that supports folder browsing. Astra found a wrong-folder race and
  dismissal gap; navigation now serializes, Save uses the successfully
  rendered path, and dismissal or route cancellation stops browsing and
  prevents applying late results. `android-parity-folder-safe` built at
  61,606,385 bytes including those fixes, but predates the final extra
  submit-while-picker-visible guard. Rebuild with fresh intermediates for
  final evidence and Astra's recheck. The
  [paired B2 smoke](../../tools/AndroidApp/PAIRED-B2-FOLDER-PICKER.md)
  must prove that the selected child gets the hash-matched file and the
  decoy stays empty.
- **B5:** all three files completed and matched hashes, but the first-run
  overlay was above Downloads and intercepted long presses. The latest
  runner taps the real **Start downloading** control first, recomputes row
  bounds after each gesture, asserts `3 selected`, and opens `sel_more`.
  The retry passed. Three finished rows prove the interaction, not the
  reference's mixed states. Run `015115`'s exact hash-verified payloads were
  removed. Run `android-b5-select-20261120-015316` retained three
  `inbound-b5-*-5435933055.bin` outputs: consult its `preflight.json` and
  `fixture-integrity.txt`, and delete only exact current hash matches if
  cleaning up. The successful `015634` run also retained its three
  `inbound-b5-*-0c9eba6827.bin` outputs; apply the same rule.
- **C2:** weekday/condition IDs were added, but the configured capture is
  still open. Verify the native time picker selectors before automating
  Mon–Fri 01:00–07:00, charging and unmetered, then save and reopen the
  disposable Night queue. Reading back the switches is not a scheduler or
  actual-metering test.

## Acceptance rule

The approved [mockups](android-mockups/) and [acceptance matrix](android-mockup-acceptance.md)
are the reference. Every implemented Android screen must match the approved
layout, states, controls, navigation and interactions. Only the owner can
approve a deviation. The **Remote Files** rename is approved; it did not
change the C3/C4 design. Keep flow verification, side-by-side comparisons,
and Astra's code and visual verdicts separate. No milestone closure, merge,
or release on the strength of a functional probe alone.

## Verified and still open

| Area | Verified | Still open |
|---|---|---|
| **C3/C4 Remote Files** | API 33 verified SFTP key confirmation and S3 onboarding against disposable fixtures; nested downloads matched both hashes; cancel/confirm delete worked. Host regressions cover stale delete confirmations and credential carryover after endpoint edits. C4 probe upload passed both Back paths, exact source cleanup, Cancel without a PUT, Confirm with one conditional PUT, and a separate competing-writer test. Astra passed that **isolated C4 upload recheck only**. | No full C3/C4 sign-off. Recheck C3 transfer summary, free space and expired cloud sign-in states; inspect remaining C3/C4 visual deltas. **Production upload stays disabled**: the S3 proof is fixture/probe-only and WebDAV `MOVE Overwrite: F` is not atomic no-replace. |
| **A0–A7 / C7–C8 core** | A0 initial setup, empty A1, one real active A2 transfer with a hash-matched finish, A4 source order and unavailable states, A5 blank/populated metadata/invalid-address states, and C7 settings/search were captured or checked on API 33. | A0 permission return, populated/mixed Downloads, completion snackbar and active/finished Details and notification states need fresh coverage as listed in the matrix. A4 clipboard offer is observe-only; no clipboard injection. C8 populated light mode is unverified. No overall visual pass. |
| **B1–B6 link intake** | B2/B3 have scoped fixture and consent evidence. B4 `b4-present-20261120-042149` verifies eight links, six archives, 689 KB and search; Astra passed controls/CTA. B6 `b6-recatch-20261120-040546` verifies cancellation and same-job WebView recovery with matching hash. | B1 real chooser and B3 visual review remain open. B4 final card inset/row spacing correction remains. B5 is a separate Downloads multiselect flow; its remaining visual/workload checks remain open. |
| **C1/C2 queues; C5/C6 Hosts** | C1 workload states passed in `c1-dropdown-20261120-035933`; configured local C2 passed in `c2-clean-boot-20261120-035129`. C5/C6 `c5-c6-pulses-20261120-041845` passed the scoped three-Host, mixed transfer and live graph checks. | Latest C1 style/chart comparison and Astra pass remain open, as do other C2 states, final C5/C6 layout, active switcher and other C6 row/action variants. |

## Resume safely

1. **Interrupted probe resolved on resume.** The revised
   B6/C5 build succeeded (`android-parity-b6-c5`, 61,598,193-byte probe).
   The chained recapture ended with a harness `Shell.NotFoundError`.
   `android-b6-styled-20261120-014012/result.txt` stops after launch; it
   records neither a pass nor cleanup. At handoff, read-only ADB checks
   found `app.inbound.android.rcloneprobe` still installed on
   `emulator-5554` and the run's reverse `tcp:8340 → tcp:8340` still present.
   On resume, the installed APK hash and shared `Download/` baseline
   matched; only that probe and mapping were removed. The other connected
   device and production Inbound were left alone.
2. **Revised C5 and B6 were recaptured successfully.** Finish and run the
   standalone B5 flow and obtain visual re-review of the revised screens.
   Use the explicit probe APK, `inbound33`/API 33, and the runbooks
   [LINK-INTAKE-PARITY.md](../../tools/AndroidApp/LINK-INTAKE-PARITY.md),
   [QUEUE-HOST-SCREEN-PARITY.md](../../tools/AndroidApp/QUEUE-HOST-SCREEN-PARITY.md),
   and [DOWNLOAD-STATES-PARITY.md](../../tools/AndroidApp/DOWNLOAD-STATES-PARITY.md).
   Paired capture requires the explicit disposable Server DLL. Never retain
   its password-bearing descriptor or pairing logcat in evidence.
3. **Close the remaining screen/state gaps** in the matrix. Do not add
   controls that only look functional. Use synthetic loopback credentials
   and fixtures; verify file hashes and remove only owned artifacts.
4. **Send Astra the code and current evidence**, fix findings, then
   recapture affected states and repeat until Astra signs off across the
   implemented screens. Reconcile [GOAL.md](../../GOAL.md),
   [ROADMAP.md](../../ROADMAP.md), and [PROGRESS.md](../../PROGRESS.md) before
   any release decision.

## Evidence pointers

These bundles are under `%LOCALAPPDATA%\Temp\opencode\`. Preserve useful
screenshots, reference pairs and scoped verification output in a durable
location before review; do not copy credentials or stale paired logcat.

- `android-c4-review-fix-20261120-005839` — scoped C4 upload recheck.
- `android-c5-c6-paired-20261120-013001` — both C5 selections and one
  completed remote C6 row; use screenshot/UI evidence, not the old C5 logs.
- `android-b6-only-20261120-013529` — verified B6 before the styling change.
- `android-b6-styled-20261120-014313` — revised B6, hash-matched same-job refresh.
- `android-c5-handle-20261120-014440` — revised C5 plus the one-row C6 check.
- `android-b2-options-20261120-013706` — current B2/B3/B4 captures before pause.
- `android-c1-disposable-20261120-011021` — stopped queue and no-schedule C2.
- Latest host checks: **549/549 Mobile**, **36/36 parity scripts**, and
  **3/3 paired B2 runner tests**; `git diff --check` passed.
