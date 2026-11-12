<script lang="ts">
  import { tick } from 'svelte';
  import { copyText } from '../lib/clipboard';
  import { flip } from 'svelte/animate';
  import ArchiveNoticeBar from '../components/ArchiveNoticeBar.svelte';
  import ArchivePassword from '../components/ArchivePassword.svelte';
  import ContextMenu, { type MenuItem } from '../components/ContextMenu.svelte';
  import Dialog from '../components/Dialog.svelte';
  import Icon from '../components/Icon.svelte';
  import ProgressBar from '../components/ProgressBar.svelte';
  import {
    extractAgainQuestion, extractSummary, leftExtracting, planForRows, removeQuestion as archiveRemoveQuestion, targetOf, type ArchivePasswordTarget,
  } from '../lib/archives';
  import {
    archivePasswordText, archivePrimary, archiveRetryText, archiveRemovable, cancelTargets, canGroup, connectionChoices, deleteBatches, deleteCompletedText, deletePlan,
    downloadChoices, emptyText, fileCommands, isTypingTarget, megabyte, menuLabel, moveCursor, nextSort, notCurrentView, offersDownloadAll, parseSort,
    pauseTargets, planFileMenu, priorityTargets, queueText, remoteEnterAction, resumeTargets,
    selectAfterClick, sharedQueueId, speedChoices, speedIsPreset, speedLimitLabel, suggestPackageName, type SortKey, type SortState,
  } from '../lib/list';
  import { listState } from '../lib/listState.svelte';
  import {
    choose, columnMenu, listChoicesKey, listColumns, listFirstShown, listNoRoom, listTemplate, parseChoices, phoneSortColumns, shownKeys, type ColumnChoices, type ListColumnKey,
  } from '../lib/columns';
  import { LongPress } from '../lib/longpress';
  import { canStartQueue, canStopQueue, orderedQueues, queueStartedText, queueStateText } from '../lib/queues';
  import { actionsOf, dequeueQuestion, isInProviderQueue, providerNameOf, removeQuestion as remoteRemoveQuestion } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet, DownloadGroup, Job, RemoteJob, Settings } from '../lib/types';
  import { foldTools } from '../lib/toolbar';
  import { buildRows, isTorrentView, selectedJobIds, viewHash, type Row, type TorrentFilesView, type ViewKey } from '../lib/views';
  import {
    extraViewShows, knownListenPort, magnetToCopy, seedClockMs, sortForColumns, sortMenuTitles, torrentCounts, torrentExtraCounts, torrentExtraViews, torrentFilters, torrentSummary,
  } from '../lib/torrentList';
  import { blocksAddressCommands, canChooseFiles, filesRefreshMs, isActive as torrentRuns, opensAsTorrent, primaryIsChooseFiles, showsFiles } from '../lib/torrents';
  import type { TorrentFilesResult } from '../lib/types';
  import TorrentChooseFiles from './TorrentChooseFiles.svelte';
  import { hasTorrentControls, mayRun, seedSaid, seedTargets } from '../lib/torrentDetails';
  import { announceSaid, canAnnounce, canRecheck, recheckSaid } from '../lib/torrentControls';
  import { watchTorrents } from '../lib/torrentWatch.svelte';
  import { everyWhileVisible } from '../lib/ticker';
  import { infoJob, infoKey, infoMinWidth, infoOn, infoReserved } from '../lib/torrentInfo';
  import TorrentInfoPane from '../components/TorrentInfoPane.svelte';
  import ArchiveRecord from './ArchiveRecord.svelte';
  import ChangeCategory from './ChangeCategory.svelte';
  import MoveRename from './MoveRename.svelte';
  import RefreshAddress from './RefreshAddress.svelte';
  import ExtractFromDisk from './ExtractFromDisk.svelte';
  import JobDetails from './JobDetails.svelte';
  import PackageDialog from './PackageDialog.svelte';
  import RemoteActions from './RemoteActions.svelte';

  let { view, search, onadd, onopentorrent }: { view: ViewKey; search: string; onadd: () => void; onopentorrent?: (job: Job) => void } = $props();

  type PackageRow = Extract<Row, { kind: 'package' }>;
  type RemoteRow = Extract<Row, { kind: 'remote' }>;
  type ArchiveRow = Extract<Row, { kind: 'archive' }>;
  type JobRow = Extract<Row, { kind: 'job' }>;

  function load(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  function save(key: string, value: string) {
    try {
      localStorage.setItem(key, value);
    } catch {
      // A private window: the choice lasts until the page closes.
    }
  }

  function loadExpanded(): Set<string> {
    try {
      const value = JSON.parse(load('inbound.expandedPackages') ?? '[]') as unknown;
      return new Set(Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : []);
    } catch {
      return new Set();
    }
  }

  let selected = $state(new Set<string>());
  let cursor = $state<string | null>(null);
  let anchor = $state<string | null>(null);
  let expanded = $state(loadExpanded());
  let sort = $state<SortState>(parseSort(load('inbound.listSort')));
  let details = $state<string | null>(null);
  /** Details opened to run Redownload at once (its questions live there). */
  let detailsStart = $state<'redownload' | undefined>(undefined);
  /** T3: details opened on a torrent's Options tab (Set Limits…). */
  let detailsTab = $state<'options' | undefined>(undefined);
  let moving = $state<Job | null>(null);
  /** T1: the torrent whose Choose files dialog is open. */
  let choosingFiles = $state<string | null>(null);
  /** T1: the files of the open torrent rows, as torrent.files last said (never in job events: a torrent can have 100,000). */
  let torrentFiles = $state<Record<string, TorrentFilesView>>({});
  let refreshing = $state<Job | null>(null);
  let recategorizing = $state<Job[] | null>(null);
  let packageOpen = $state<string | null>(null);
  // A package with just one listed file opens that file's own window (ProgressDetailRules.OnlyFile).
  function openPackage(group: { id: string; jobIds: string[] }) {
    const listed = group.jobIds.filter((id) => store.model.jobs[id]);
    if (listed.length === 1) details = listed[0];
    else packageOpen = group.id;
  }
  let remoteOpen = $state<RemoteJob | null>(null);
  let archiveOpen = $state<ArchiveSet | null>(null);
  let passwordTarget = $state<ArchivePasswordTarget | null>(null);
  let extractFromDisk = $state(false);
  let menu = $state<{ x: number; y: number; items: MenuItem[]; label: string } | null>(null);
  let grid: HTMLDivElement | undefined = $state();
  let menuOpenedAt = 0;
  const longPress = new LongPress();

  /** One dialog at a time that asks a yes or no: the Delete questions, Cancel Package, taking provider rows off the list. */
  interface Confirm {
    title: string;
    text: string;
    ok: string;
    cancel?: string;
    box?: { label: string; checked: boolean };
    onok: (box: boolean) => void | Promise<void>;
  }
  let confirm = $state<Confirm | null>(null);
  let boxChecked = $state(false);
  let nameDialog = $state<{ title: string; initial: string; onok: (name: string) => void | Promise<void> } | null>(null);
  let nameText = $state('');
  let cleanup = $state(false);
  let speedDialog = $state(false);
  let speedText = $state('');

  // The column is there whenever there is a set to talk about, and the Host answers archive.list.
  const showExtraction = $derived(store.model.archivesAnswered && Object.keys(store.model.archives).length > 0);
  // The Extractions view lists archives, which have no queue, size, status, time left or rate of their own (U-WU-20).
  const onlyExtraction = $derived(view === 'extractions' && showExtraction);
  // The Torrents views show Seeds, Peers, Up and Ratio in place of Last Try Date (WEB-T2), and on a phone the views
  // as chips with a line of what the torrents move (WEB-T1, WEB-T3).
  const torrentCols = $derived(isTorrentView(view) && !onlyExtraction);

  // UI.R42: the header's menu (a right-click, Shift+F10 or the menu key on a header, or More > Columns) shows and hides
  // columns. The torrent views keep their own choice, apart from every other view's, as in the Windows app. A phone keeps
  // its own two or three lines whatever is chosen, and the Extractions view its own columns.
  let listChoices = $state<ColumnChoices>(parseChoices(listColumns, load(listChoicesKey(false))));
  let torrentListChoices = $state<ColumnChoices>(parseChoices(listColumns, load(listChoicesKey(true))));
  const columnAvailable = (key: ListColumnKey) => key !== 'extraction' || showExtraction;
  const chosenColumns = $derived(shownKeys(listColumns, torrentCols ? torrentListChoices : listChoices, columnAvailable, listFirstShown(torrentCols)));
  const shownColumns = $derived(new Set<string>(onlyExtraction ? ['name', 'extraction', 'lastTry', 'description'] : chosenColumns));
  // The grid's tracks below 1024 px (and beside Torrent Info) and from 1024 px; the Extractions view keeps its own.
  const colsMd = $derived(listTemplate(chosenColumns, false));
  const colsLg = $derived(listTemplate(chosenColumns, true));
  const off = (key: ListColumnKey) => !shownColumns.has(key);

  function chooseColumns(next: ColumnChoices) {
    if (torrentCols) torrentListChoices = next;
    else listChoices = next;
    save(listChoicesKey(torrentCols), JSON.stringify(next));
  }

  function columnItems(): MenuItem[] {
    return columnMenu(listColumns, shownColumns, columnAvailable,
      (key, on) => chooseColumns(choose(listColumns, torrentCols ? torrentListChoices : listChoices, key, on)),
      () => chooseColumns({}),
      (key) => listNoRoom(key, wideRoom));
  }

  function headerMenu(event: MouseEvent) {
    event.preventDefault();
    if (onlyExtraction) return;
    // From the keyboard (Shift+F10, the menu key) the event has no point: the menu opens under the header cell.
    const box = (event.target as HTMLElement).getBoundingClientRect();
    const keyboard = event.clientX === 0 && event.clientY === 0;
    // Focus goes back to the header that opened it when the menu closes, as in Progress Details (review of 0.7.2, ux-2).
    const head = event.currentTarget as HTMLElement;
    const target = event.target instanceof Element ? event.target.closest('button') : null;
    columnsOpener = { head, button: target ?? (document.activeElement instanceof HTMLElement && head.contains(document.activeElement) ? document.activeElement : null) };
    menuOpenedAt = Date.now();
    menu = { x: keyboard ? box.left : event.clientX, y: keyboard ? box.bottom : event.clientY, items: columnItems(), label: 'Columns' };
  }

  let columnsOpener: { head: HTMLElement; button: HTMLElement | null } | null = null;

  // UI.R42: columns are a wide window's; a phone's list is two or three lines, so More has no Columns there.
  let columnsWide = $state(typeof window === 'undefined' || typeof window.matchMedia !== 'function' ? true : window.matchMedia('(min-width: 768px)').matches);
  $effect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const query = window.matchMedia('(min-width: 768px)');
    const changed = () => (columnsWide = query.matches);
    query.addEventListener('change', changed);
    return () => query.removeEventListener('change', changed);
  });
  // A sort by a column that doesn't show here (a torrent column outside the torrent views, one hidden from the menu)
  // waits for it; the default holds meanwhile (WEB-T2). A phone has no header, so the choices don't apply there: it
  // sorts by the columns the list always had (phoneSortColumns).
  const sortColumns = $derived(columnsWide ? shownColumns : phoneSortColumns(torrentCols));
  const shownSort = $derived(sortForColumns(sort, sortColumns));

  // UI.R42: a seeding torrent's Seeding time counts on while the column shows, though an idle one gets no updates.
  let seedClock = $state(0);
  const rows = $derived((void seedClock, buildRows(store.model, view, search, expanded, shownSort, torrentFiles)));
  const settings = $derived(store.model.settings);
  const providerName = $derived(store.model.accounts[0]?.providerName ?? 'TorBox');

  /** The rows that can be selected: a torrent's file rows stand for no download. */
  const selectableCount = $derived(rows.filter((row) => row.kind !== 'torrentFile').length);
  const selectedRows = $derived(rows.filter((row) => selected.has(row.id)));
  const packageRows = $derived(selectedRows.filter((row): row is PackageRow => row.kind === 'package'));
  const remoteRows = $derived(selectedRows.filter((row): row is RemoteRow => row.kind === 'remote'));
  const archiveRows = $derived(selectedRows.filter((row): row is ArchiveRow => row.kind === 'archive'));
  /** Selected files that a selected package row does not already stand for. */
  const looseJobs = $derived.by(() => {
    const covered = new Set(packageRows.map((row) => row.group.id));
    return selectedRows.filter((row): row is JobRow => row.kind === 'job' && !(row.packageId && covered.has(row.packageId))).map((row) => row.job);
  });
  const selectedIds = $derived(selectedJobIds(rows, selected));
  const selectedJobs = $derived(selectedIds.map((id) => store.model.jobs[id]).filter((job): job is Job => !!job));
  // T7: Torrent Info, the one selected torrent's facts beside the list (on unless turned off, kept per browser).
  let torrentInfoOn = $state(infoOn(load(infoKey)));
  function setTorrentInfo(on: boolean) {
    torrentInfoOn = on;
    save(infoKey, on ? 'on' : 'off');
  }
  const infoTorrent = $derived(store.has('torrents') ? infoJob(torrentInfoOn, selectedJobs) : null);
  // The pane shows inline only from infoMinWidth; narrower, More › Torrent Info says it needs a wider window.
  let infoWide = $state(typeof window === 'undefined' || typeof window.matchMedia !== 'function' ? true : window.matchMedia(`(min-width: ${infoMinWidth}px)`).matches);
  $effect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const query = window.matchMedia(`(min-width: ${infoMinWidth}px)`);
    const changed = () => (infoWide = query.matches);
    query.addEventListener('change', changed);
    return () => query.removeEventListener('change', changed);
  });
  // The wide columns (Description, Last Try Date, the torrent columns) show from 1024 px and not beside Torrent Info; the
  // header's menu greys them out with "Wider window" otherwise.
  let lgWide = $state(typeof window === 'undefined' || typeof window.matchMedia !== 'function' ? true : window.matchMedia('(min-width: 1024px)').matches);
  $effect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const query = window.matchMedia('(min-width: 1024px)');
    const changed = () => (lgWide = query.matches);
    query.addEventListener('change', changed);
    return () => query.removeEventListener('change', changed);
  });
  function hideTorrentInfo() {
    setTorrentInfo(false);
    // The pane's × goes with it: the list keeps the keyboard, as in the desktop window.
    grid?.focus();
  }
  const localRows = $derived(selectedRows.filter((row): row is JobRow | PackageRow => row.kind === 'job' || row.kind === 'package'));
  const firstLocal = $derived(localRows[0]);
  const removableSideRows = $derived(remoteRows.length + archiveRows.filter((row) => archiveRemovable(row.set)).length);

  const allJobs = $derived(Object.values(store.model.jobs));
  const canResume = $derived(looseJobs.some((job) => job.canResume) || packageRows.some((row) => resumeTargets(row.group.id, row.jobs).length > 0));
  const canPause = $derived(looseJobs.some((job) => job.canPause) || packageRows.some((row) => pauseTargets(row.jobs).length > 0));
  const canCancelPackage = $derived(packageRows.some((row) => cancelTargets(row.jobs).length > 0));
  const canDelete = $derived(selectedJobs.length > 0 || removableSideRows > 0);
  const connected = $derived(store.connection === 'open');
  const completedIds = $derived(allJobs.filter((job) => job.state === 'completed').map((job) => job.id));
  const queues = $derived(orderedQueues(settings?.queues ?? []));

  const archiveByJob = $derived.by(() => {
    const map = new Map<string, ArchiveSet>();
    for (const set of Object.values(store.model.archives)) for (const id of set.jobIds) map.set(id, set);
    return map;
  });
  const torrentCount = $derived(torrentCols ? torrentCounts(Object.values(store.model.jobs)) : null);
  // T7: the extra views' chips, each while it holds a torrent or is the view shown (as in the sidebar).
  const torrentExtraCount = $derived(torrentCols ? torrentExtraCounts(Object.values(store.model.jobs)) : null);
  const torrentLine = $derived(torrentCols ? torrentSummary(Object.values(store.model.jobs), knownListenPort(store.model.settings, store.model.hello?.hostKind)) : '');

  // A toast's Show opens that download.
  $effect(() => {
    if (store.showJob) {
      details = store.showJob;
      store.showJob = null;
    }
  });

  // What the archive commands take for the selection (ArchiveRules.Plan), for the toolbar and the row menu.
  const archivePlan = $derived(planForRows(rows, selected, (id) => archiveByJob.get(id), connected));
  // ---- T1: the files of an open torrent row, read when it opens and again while it downloads ----

  const openTorrents = $derived(Object.values(store.model.jobs).filter((job) => expanded.has(job.id) && showsFiles(job.torrent)));
  const filesRead = new Map<string, number>();
  const filesReading = new Set<string>();

  async function readTorrentFiles(jobId: string) {
    if (filesReading.has(jobId)) return;
    filesReading.add(jobId);
    try {
      const result = await store.ask<TorrentFilesResult>('torrent.files', { jobId });
      torrentFiles = { ...torrentFiles, [jobId]: { files: result.files ?? [] } };
    } catch (error) {
      // An earlier list stays; only a first read shows the problem.
      const before = torrentFiles[jobId];
      if (!before || 'error' in before) torrentFiles = { ...torrentFiles, [jobId]: { error: error instanceof Error ? error.message : String(error) } };
    } finally {
      filesRead.set(jobId, Date.now());
      filesReading.delete(jobId);
    }
  }

  $effect(() => {
    if (!connected) return;
    const now = Date.now();
    for (const job of openTorrents) {
      const at = filesRead.get(job.id);
      // Read once on opening; then, while it runs, at most every 3 s (30 s for a big list), as the job events come.
      // A first read that failed is tried again on the next change after that wait too.
      const failed = !!torrentFiles[job.id] && 'error' in torrentFiles[job.id];
      const due = at === undefined || ((torrentRuns(job) || failed) && now - at >= filesRefreshMs(job.torrent?.fileCount ?? 0));
      if (due) void readTorrentFiles(job.id);
    }
  });
  // A closed row forgets its files, so opening it again reads them fresh.
  $effect(() => {
    const open = new Set(openTorrents.map((job) => job.id));
    const stale = Object.keys(torrentFiles).filter((id) => !open.has(id));
    if (stale.length === 0) return;
    const next = { ...torrentFiles };
    for (const id of stale) {
      delete next[id];
      filesRead.delete(id);
    }
    torrentFiles = next;
  });

  const canCheckRemote = $derived(Object.values(store.model.remoteJobs).some((job) => actionsOf(job).checkNow));

  async function extractSelection() {
    const plan = archivePlan;
    if (!plan.canExtract) return;
    const words: string[] = [];
    if (plan.extractJobIds.length > 0) {
      const result = await store.attempt<{ sets?: ArchiveSet[]; ignored?: { reason?: string }[] }>('archive.extract', { jobIds: plan.extractJobIds });
      if (result) words.push(extractSummary(result));
    }
    for (const set of plan.extractSets) {
      if (await store.attempt('archive.retry', { setId: set.id })) words.push(`Extraction asked for: ${set.name}.`);
    }
    if (words.length) store.toast(words.join(' '));
  }

  /** Retry Extraction, or Extract Again for a finished set, which asks first (RetryArchivesAsync). */
  function retryArchives(sets: ArchiveSet[]) {
    if (sets.length === 0) return;
    const run = () => each(sets, 'archive.retry', (s) => ({ setId: s.id }), 'Extraction asked for again.');
    if (sets.some((set) => set.state === 'completed')) confirm = { title: 'Extract again?', text: extractAgainQuestion, ok: 'Extract Again', onok: run };
    else void run();
  }

  function askRemoveRecords(sets: ArchiveSet[]) {
    if (sets.length === 0) return;
    confirm = {
      title: 'Delete extraction record?',
      text: archiveRemoveQuestion(sets),
      ok: 'Delete',
      onok: () => each(sets, 'archive.remove', (s) => ({ setId: s.id }), sets.length === 1 ? 'Extraction record removed.' : 'Extraction records removed.'),
    };
  }

  // A row that left the view leaves the selection.
  $effect(() => {
    const present = new Set(rows.map((row) => row.id));
    if ([...selected].some((id) => !present.has(id))) selected = new Set([...selected].filter((id) => present.has(id)));
    if (cursor && !present.has(cursor)) cursor = null;
    if (anchor && !present.has(anchor)) anchor = null;
  });

  // The status bar reads these.
  $effect(() => {
    listState.selectedFiles = selectedJobs.length;
    const only = selectedRows.length === 1 && selectedRows[0].kind !== 'job' && selectedRows[0].kind !== 'package' ? selectedRows[0] : null;
    listState.sideNote = only && only.view.description ? `${only.view.name}: ${only.view.description}` : '';
  });
  $effect(() => () => {
    listState.selectedFiles = 0;
    listState.sideNote = '';
  });

  const queueTarget = $derived(
    firstLocal?.kind === 'job' ? firstLocal.job.queueId : firstLocal?.kind === 'package' ? (sharedQueueId(firstLocal.jobs) ?? settings?.defaultQueueId ?? 0) : (settings?.defaultQueueId ?? 0),
  );
  const targetQueue = $derived(queues.find((queue) => queue.id === queueTarget));

  // ---- Selecting ----

  /** A touch screen has no double-click: there a tap opens the download, and the box on the left selects. */
  function touchFirst(): boolean {
    try {
      return matchMedia('(pointer: coarse)').matches && !matchMedia('(pointer: fine)').matches;
    } catch {
      return false;
    }
  }

  function domId(id: string): string {
    return `row-${id.replace(/[^a-zA-Z0-9_-]/g, '_')}`;
  }

  function reveal(id: string | null) {
    if (id) document.getElementById(domId(id))?.scrollIntoView({ block: 'nearest' });
  }

  function click(row: Row, event: MouseEvent) {
    if (longPress.takeClick()) return;
    // A torrent's file row stands for no download: nothing to select. The last one opens the details.
    if (row.kind === 'torrentFile') {
      cursor = row.id;
      if (row.more || touchFirst()) open(row);
      return;
    }
    const ctrl = event.ctrlKey || event.metaKey;
    cursor = row.id;
    if (touchFirst() && !event.shiftKey && !ctrl) {
      open(row);
      return;
    }
    selected = selectAfterClick(selected, rows.map((r) => r.id), row.id, anchor, { shift: event.shiftKey, ctrl });
    if (!event.shiftKey) anchor = row.id;
    grid?.focus({ preventScroll: true });
  }

  function toggle(row: Row) {
    if (row.kind === 'torrentFile') return;
    const next = new Set(selected);
    if (next.has(row.id)) next.delete(row.id);
    else next.add(row.id);
    anchor = row.id;
    cursor = row.id;
    selected = next;
    grid?.focus({ preventScroll: true });
  }

  function toggleAll() {
    const all = rows.filter((row) => row.kind !== 'torrentFile');
    selected = selected.size === all.length ? new Set() : new Set(all.map((row) => row.id));
  }

  function toggleExpanded(id: string) {
    const next = new Set(expanded);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expanded = next;
    save('inbound.expandedPackages', JSON.stringify([...next]));
  }

  function applySort(next: SortState) {
    sort = next;
    save('inbound.listSort', JSON.stringify(next));
  }

  function headerSort(key: SortKey) {
    applySort(nextSort(shownSort, key));
  }

  // ---- What a double-click or Enter does ----

  function open(row: Row) {
    if (row.kind === 'torrentFile') { if (row.more) details = row.parentId; return; }
    // A torrent waiting for its file choice opens that choice (TorrentRules.PrimaryIsChooseFiles).
    if (row.kind === 'job' && primaryIsChooseFiles(row.job)) choosingFiles = row.job.id;
    else if (row.kind === 'job') details = row.job.id;
    else if (row.kind === 'package') toggleExpanded(row.group.id);
    else if (row.kind === 'remote') void runRemote(remoteEnterAction([row.remote]), [row.remote], true);
    else if (row.kind === 'archive') {
      const primary = archivePrimary(row.set);
      if (primary === 'retry') retryArchives([row.set]);
      else if (primary === 'password') passwordTarget = targetOf(row.set);
      else if (primary === 'copyFolder') void copy(row.set.destination ?? '', 'Folder path copied.');
    }
  }

  async function runRemote(action: ReturnType<typeof remoteEnterAction>, jobs: RemoteJob[], dialogFallback = false) {
    switch (action) {
      case 'approve': await each(jobs, 'remote.retry', (j) => ({ jobId: j.id, approveUncached: true }), 'Approved.'); break;
      case 'retry': await each(jobs, 'remote.retry', (j) => ({ jobId: j.id }), 'Trying again.'); break;
      case 'checkNow': await each(jobs, 'remote.checkNow', (j) => ({ jobId: j.id }), 'Looking at it now.'); break;
      case 'selectFiles':
      case 'enterLinkPassword': remoteOpen = jobs[0]; break;
      default: if (dialogFallback) remoteOpen = jobs[0];
    }
  }

  /** T3: Force Recheck on every selected torrent it may apply to (the Host says why for one it doesn't). */
  async function recheckSelection() {
    const targets = selectedJobs.filter(canRecheck);
    let ok = 0;
    for (const job of targets) if (await store.attempt('torrent.recheck', { jobId: job.id })) ok++;
    if (ok) store.toast(targets.length === 1 ? recheckSaid(mayRun(targets[0])) : `A check of the files was asked for ${ok} torrents.`);
  }

  /** T3: Announce Now for every selected torrent that runs. */
  async function announceSelection() {
    let ok = 0;
    for (const job of selectedJobs.filter(canAnnounce)) if (await store.attempt('torrent.announce', { jobId: job.id })) ok++;
    if (ok) store.toast(announceSaid);
  }

  // T3: while a torrent is on the list in view or its details are open, and the page is visible, the server is told
  // (torrent.watch) so its progress comes every second; otherwise every 5 s. No torrent shown: nothing is sent.
  // Its room is kept while the list shows torrents, whatever is selected, so the columns don't change on each click.
  const infoRoom = $derived(infoReserved({ on: torrentInfoOn, offered: store.has('torrents'), wide: infoWide, listsTorrents: rows.some((row) => row.kind === 'job' && !!row.job.torrent) }));
  // The header's menu greys the wide columns out with "Wider window" while they have no room (lgWide above).
  const wideRoom = $derived(lgWide && !infoRoom);
  // Read again on each tick too, so the clock slows to half a minute once the newest seeding passes its first hour.
  const seedClockEvery = $derived((void seedClock, seedClockMs(wideRoom && shownColumns.has('seedTime'), Object.values(store.model.jobs))));
  $effect(() => {
    const every = seedClockEvery;
    if (every === null) return;
    return everyWhileVisible(every, () => (seedClock += 1));
  });
  watchTorrents(() => rows.some((row) => row.kind === 'job' && !!row.job.torrent) || (!!details && !!store.model.jobs[details]?.torrent));

  /** T2: Start seeding or Stop seeding on every selected torrent it applies to (torrent.seed, one each). */
  async function seedSelection(on: boolean) {
    let ok = 0;
    for (const job of seedTargets(selectedJobs, on)) if (await store.attempt('torrent.seed', { jobId: job.id, on })) ok++;
    if (ok) store.toast(seedSaid(on, ok));
  }

  async function each<T>(items: T[], type: string, payload: (item: T) => Record<string, unknown>, done: string) {
    let ok = 0;
    for (const item of items) if (await store.attempt(type, payload(item))) ok++;
    if (ok) store.toast(done);
  }

  // ---- Commands on the selection: a package is one Host action, any other file by itself ----

  interface FileAction {
    eligible: (job: Job) => boolean;
    type: string;
    payload: (job: Job) => Record<string, unknown>;
  }

  async function runOnSelection(label: string, packageAction: { action: string; queueId?: number; priority?: number }, file?: FileAction) {
    const problems: string[] = [];
    const notes: string[] = [];
    const fail = (name: string, error: unknown) => problems.push(`${name}: ${error instanceof Error ? error.message : String(error)}`);
    for (const row of packageRows) {
      try {
        const result = await store.ask<{ ok: boolean; leftCancelled?: number; groups?: DownloadGroup[] }>('group.action', { groupId: row.group.id, ...packageAction });
        if (result.leftCancelled) notes.push(`${row.view.name}: ${result.leftCancelled} cancelled file(s) stayed cancelled. Resume them one by one to bring them back.`);
      } catch (error) {
        fail(row.view.name, error);
      }
    }
    if (file) {
      for (const job of looseJobs.filter(file.eligible)) {
        try {
          await store.ask(file.type, file.payload(job));
        } catch (error) {
          fail(job.fileName, error);
        }
      }
    }
    if (problems.length) store.toast(`${label} incomplete. ${problems.slice(0, 5).join(' ')}`, 'error');
    else if (notes.length) store.toast(notes.slice(0, 2).join('  '));
  }

  const resume = () => runOnSelection('Resume', { action: 'resume' }, { eligible: (j) => !!j.canResume, type: 'job.resume', payload: (j) => ({ jobId: j.id }) });
  const stop = () => runOnSelection('Stop', { action: 'pause' }, { eligible: (j) => !!j.canPause, type: 'job.pause', payload: (j) => ({ jobId: j.id }) });
  const addToQueue = (queueId: number) => runOnSelection('Add to Queue', { action: 'queue', queueId }, { eligible: () => true, type: 'job.moveToQueue', payload: (j) => ({ jobId: j.id, queueId }) });
  const removeFromQueue = () => runOnSelection('Remove from Queue', { action: 'unqueue' }, { eligible: () => true, type: 'job.moveToQueue', payload: (j) => ({ jobId: j.id, detach: true }) });
  const setPriority = (priority: number) => runOnSelection('Priority', { action: 'priority', priority }, { eligible: (j) => j.state !== 'completed', type: 'job.update', payload: (j) => ({ jobId: j.id, priority }) });

  function askCancelPackage() {
    const targets = packageRows.filter((row) => cancelTargets(row.jobs).length > 0);
    if (targets.length === 0) return;
    confirm = {
      title: targets.length === 1 ? 'Cancel package?' : `Cancel ${targets.length} packages?`,
      text: `${targets.length === 1 ? `Cancel the unfinished files in '${targets[0].view.name}'?` : 'Cancel the unfinished files in these packages?'} Completed files and partial data are kept. Resume can undo this only if the cancel stopped every file in the package.`,
      ok: 'Cancel package',
      cancel: 'Keep downloading',
      onok: () => runOnSelection('Cancel Package', { action: 'cancel' }),
    };
  }

  async function groupAction(title: string, request: Record<string, unknown>) {
    try {
      const result = await store.ask<{ ok: boolean; groups?: DownloadGroup[] }>('group.action', request);
      if (Array.isArray(result.groups)) store.model.groups = result.groups;
    } catch (error) {
      store.toast(`${title}: ${error instanceof Error ? error.message : String(error)}`, 'error');
    }
  }

  function askName(title: string, initial: string, onok: (name: string) => void | Promise<void>) {
    nameText = initial;
    nameDialog = { title, initial, onok };
  }

  function groupSelected() {
    if (selectedJobs.length < 2) {
      store.toast('Select at least two downloads to put in a package.', 'error');
      return;
    }
    const ids = selectedJobs.map((job) => job.id);
    askName('Group into package', suggestPackageName(selectedJobs[0].fileName), (name) => groupAction('Group into package', { action: 'create', name, jobIds: ids }));
  }

  /** The package a file is listed in: the groups with at least two files on the list. */
  function packageOf(id: string): DownloadGroup | undefined {
    return store.model.groups.find((group) => group.jobIds.includes(id) && group.jobIds.filter((j) => j in store.model.jobs).length >= 2);
  }

  async function ungroupSelected() {
    const byPackage = new Map<string, string[]>();
    for (const job of selectedJobs) {
      const group = packageOf(job.id);
      if (group) byPackage.set(group.id, [...(byPackage.get(group.id) ?? []), job.id]);
    }
    for (const [groupId, ids] of byPackage) {
      const group = store.model.groups.find((g) => g.id === groupId);
      if (!group) continue;
      // Taking every file out of a package dissolves it; the downloads stay.
      await groupAction('Remove from package', ids.length >= group.jobIds.length ? { action: 'delete', groupId } : { action: 'detach', groupId, jobIds: ids });
    }
  }

  function renamePackage() {
    const group = firstLocal?.kind === 'package' ? firstLocal.group : firstLocal?.kind === 'job' ? packageOf(firstLocal.job.id) : undefined;
    if (!group) return;
    askName('Rename package', group.name, (name) => groupAction('Rename package', { action: 'rename', groupId: group.id, name }));
  }

  // ---- Deleting ----

  function askDelete(deleteFromDisk: boolean) {
    if (selectedJobs.length === 0) {
      void removeSideRows();
      return;
    }
    const jobs = selectedJobs;
    const plan = deletePlan(jobs);
    const ids = jobs.map((job) => job.id);
    boxChecked = plan.offerFiles ? deleteFromDisk : plan.deleteFile;
    confirm = {
      title: plan.title,
      text: plan.text,
      ok: 'Delete',
      box: plan.offerFiles ? { label: 'Delete from disk: the downloaded files', checked: deleteFromDisk } : undefined,
      onok: async (box) => {
        const deleteFinished = plan.offerFiles && box;
        // UI.B19: unfinished downloads always lose their partial data; the box decides only the finished files.
        let ok = true;
        for (const batch of deleteBatches(jobs, deleteFinished)) {
          const result = await store.attempt<{ ok: boolean }>('job.remove', { jobId: batch.ids[0], jobIds: batch.ids, deleteFile: batch.deleteFile });
          ok &&= result?.ok === true;
        }
        if (ok) {
          store.toast(`Deleted ${ids.length} download${ids.length === 1 ? '' : 's'}${deleteFinished ? ' and their files' : ''}.`);
          selected = new Set();
        }
      },
    };
  }

  function askDeleteCompleted() {
    const ids = completedIds;
    if (ids.length === 0) return;
    boxChecked = false;
    confirm = {
      title: 'Delete completed downloads?',
      text: deleteCompletedText(ids.length, allJobs.filter((job) => job.state === 'completed' && job.torrent?.seeding === true).length),
      ok: 'Delete',
      // Only the list: the downloaded files stay on disk.
      onok: async () => {
        const result = await store.attempt<{ ok: boolean }>('job.remove', { jobId: ids[0], jobIds: ids, deleteFile: false });
        if (result?.ok) store.toast(`Removed ${ids.length} completed download${ids.length === 1 ? '' : 's'} from the list.`);
      },
    };
  }

  /** Delete when no local file is selected: provider rows and extraction records come off the list. */
  async function removeSideRows() {
    const jobs = remoteRows.map((row) => row.remote);
    const sets = archiveRows.map((row) => row.set).filter(archiveRemovable);
    const left = leftExtracting(archiveRows.length - sets.length);
    const run = async () => {
      for (const job of jobs) await store.attempt('remote.remove', { jobId: job.id });
      for (const set of sets) await store.attempt('archive.remove', { setId: set.id });
      selected = new Set();
      if (left) store.toast(left);
    };
    if (jobs.length > 0 && sets.length > 0) {
      confirm = { title: 'Delete from the list?', text: `${remoteRemoveQuestion(jobs)} ${archiveRemoveQuestion(sets)}`, ok: 'Delete', onok: run };
    } else if (jobs.length > 0) {
      confirm = { title: 'Delete from the list?', text: remoteRemoveQuestion(jobs), ok: 'Delete', onok: run };
    } else if (sets.length > 0) {
      confirm = { title: 'Delete extraction record?', text: archiveRemoveQuestion(sets), ok: 'Delete', onok: run };
    }
    if (left && jobs.length + sets.length === 0) store.toast(left);
  }

  /** A download still waiting in the provider's own queue comes out of it; nothing else is deleted. */
  function askDequeue(jobs: RemoteJob[]) {
    const queued = jobs.filter(isInProviderQueue);
    if (queued.length === 0) return;
    const provider = providerNameOf(queued[0]);
    confirm = {
      title: `Remove from ${provider}'s queue?`,
      text: dequeueQuestion(queued),
      ok: 'Remove from queue',
      onok: async () => {
        for (const job of queued) await store.attempt('remote.deleteRemote', { jobId: job.id, confirmed: true });
        selected = new Set();
      },
    };
  }

  async function answer(ok: boolean) {
    const current = confirm;
    const box = boxChecked;
    confirm = null;
    if (ok && current) await current.onok(box);
    grid?.focus({ preventScroll: true });
  }

  async function submitName() {
    const current = nameDialog;
    const name = nameText.trim();
    if (!current || !name) return;
    nameDialog = null;
    await current.onok(name);
  }

  // ---- Queues and limits ----

  async function queueAction(action: 'start' | 'stop', queueId: number) {
    const queue = queues.find((q) => q.id === queueId);
    await store.attempt('queue.action', { queueId, action }, queueStartedText(queue?.name ?? 'The queue', action === 'start'));
  }

  function saveLimit(change: (settings: Settings) => void) {
    void store.saveSettings(change, '');
  }

  async function runCleanup(remove: boolean) {
    cleanup = false;
    const result = await store.attempt<{ ok: boolean; processed?: string[]; failed?: string[] }>('host.cleanOrphans', { delete: remove });
    if (!result) return;
    const failed = result.failed?.length ?? 0;
    store.toast(`${remove ? 'Deleted' : 'Quarantined'} ${result.processed?.length ?? 0} file(s).${failed ? ` ${failed} could not be cleaned (in use?).` : ''}`, failed ? 'error' : 'info');
  }

  async function setCustomSpeed() {
    const value = Number(speedText.replace(',', '.'));
    if (!Number.isFinite(value) || value < 0.1 || value > 100_000) return;
    speedDialog = false;
    const bytesPerSecond = Math.round(value * megabyte);
    saveLimit((s) => { s.globalSpeedLimitBytesPerSecond = bytesPerSecond; });
  }

  // ---- Menus ----

  // The columns that show, the torrent views' Seeds, Peers, Up and Ratio among them: below 1024 px this menu is the only way
  // to sort by a wide column.
  const columnTitles = $derived(sortMenuTitles(sortColumns));

  // ---- The toolbar ----

  interface Tool {
    id: string;
    label: string;
    icon: string;
    tone: string;
    title: string;
    disabled?: boolean;
    run?: () => void;
    href?: string;
    /** A caret beside it opens this menu (Start a queue, Stop a queue). */
    menu?: () => MenuItem[];
    menuLabel?: string;
    group: number;
    /** Lower is kept on the bar longer when the width runs short. */
    keep: number;
  }

  const offline = ' Not available until the server can be reached again.';
  const tools = $derived.by((): Tool[] => {
    const why = connected ? '' : offline;
    const list: Tool[] = [
      { id: 'add', label: 'Add URL', icon: 'add', tone: 'text-icon-info', title: `Add a new download URL.${why}`, disabled: !connected, run: onadd, group: 0, keep: 2 },
      { id: 'resume', label: 'Resume', icon: 'play', tone: 'text-icon-start', title: `Resume the selected download.${why}`, disabled: !connected || !canResume, run: () => void resume(), group: 1, keep: 1 },
      { id: 'stop', label: 'Stop', icon: 'stop', tone: 'text-icon-pause', title: `Stop the selected download (partial data is kept).${why}`, disabled: !connected || !canPause, run: () => void stop(), group: 1, keep: 1 },
      { id: 'stopAll', label: 'Stop All', icon: 'pause', tone: 'text-icon-pause', title: `Stop all active downloads.${why}`, disabled: !connected || !allJobs.some((job) => job.canPause), run: () => void store.attempt('jobs.pauseAll', {}, 'Every download was stopped.'), group: 1, keep: 3 },
    ];
    if (packageRows.length > 0) list.push({ id: 'cancelPackage', label: 'Cancel Package', icon: 'close', tone: 'text-icon-danger', title: `Cancel the unfinished files in the selected package (partial data is kept).${why}`, disabled: !connected || !canCancelPackage, run: askCancelPackage, group: 1, keep: 3 });
    list.push(
      { id: 'delete', label: 'Delete', icon: 'trash', tone: 'text-icon-danger', title: (selectedJobs.length === 0 && removableSideRows > 0 ? 'Delete the selected rows from the list.' : 'Delete the selected download and its partial data.') + why, disabled: !connected || !canDelete, run: () => askDelete(false), group: 2, keep: 1 },
      { id: 'deleteCompleted', label: 'Delete Completed', icon: 'sweep', tone: 'text-icon-danger', title: `Delete completed downloads from the list (their files stay).${why}`, disabled: !connected || completedIds.length === 0, run: askDeleteCompleted, group: 2, keep: 4 },
      { id: 'extract', label: 'Extract', icon: 'archive', tone: 'text-icon-media', title: archivePlan.extractText, disabled: !archivePlan.canExtract, run: () => void extractSelection(), group: 3, keep: 7 },
      { id: 'archivePassword', label: 'Archive password', icon: 'key', tone: 'text-icon-pause', title: 'Set the password for the selected archive', disabled: !archivePlan.password, run: () => (passwordTarget = archivePlan.password), group: 3, keep: 7 },
      { id: 'extractDisk', label: 'Extract archive…', icon: 'archive', tone: 'text-icon-media', title: `Extract an archive that is already there.${why}`, disabled: !connected, run: () => (extractFromDisk = true), group: 3, keep: 8 },
    );
    if (view === 'remote') list.push({ id: 'checkNow', label: 'Check now', icon: 'refresh', tone: 'text-icon-info', title: (canCheckRemote ? 'Ask the provider about your waiting items now, without waiting for the next check.' : 'Nothing is waiting on a provider, so there is nothing to check.') + why, disabled: !connected || !canCheckRemote, run: () => void store.attempt('remote.checkNow', {}, 'Checking now…'), group: 4, keep: 2 });
    list.push(
      { id: 'options', label: 'Options', icon: 'settings', tone: 'text-icon-neutral', title: "Open Inbound's options", href: '#/settings', group: 5, keep: 6 },
      { id: 'scheduler', label: 'Scheduler', icon: 'calendar', tone: 'text-icon-info', title: 'Manage scheduled queue runs', href: '#/queues', group: 5, keep: 6 },
      { id: 'startQueue', label: 'Start Queue', icon: 'play', tone: 'text-icon-start', title: (targetQueue ? `Start the ${targetQueue.name} queue (${queueStateText(targetQueue, allJobs).toLowerCase()}).` : 'Start the selected queue.') + why, disabled: !connected || !targetQueue || !canStartQueue(targetQueue, allJobs), run: () => void queueAction('start', queueTarget), menu: queues.length > 1 ? () => queueMenu('start') : undefined, menuLabel: 'Start a queue', group: 6, keep: 5 },
      { id: 'stopQueue', label: 'Stop Queue', icon: 'stop', tone: 'text-icon-pause', title: (targetQueue ? `Stop the ${targetQueue.name} queue and pause its downloads.` : 'Stop the selected queue and pause its downloads.') + why, disabled: !connected || !targetQueue || !canStopQueue(targetQueue), run: () => void queueAction('stop', queueTarget), menu: queues.length > 1 ? () => queueMenu('stop') : undefined, menuLabel: 'Stop a queue', group: 6, keep: 5 },
    );
    return list;
  });

  let bar: HTMLDivElement | undefined = $state();
  let measure: HTMLDivElement | undefined = $state();
  let barWidth = $state(0);
  let widths = $state<Record<string, number>>({});

  $effect(() => {
    if (!bar) return;
    const observer = new ResizeObserver(() => (barWidth = bar?.clientWidth ?? 0));
    observer.observe(bar);
    barWidth = bar.clientWidth;
    return () => observer.disconnect();
  });
  // Every label change (a package selected adds Cancel Package) and every width change is measured again.
  $effect(() => {
    void tools.map((tool) => tool.label + (tool.menu ? '+' : '')).join('|');
    void barWidth;
    if (!measure) return;
    const next: Record<string, number> = {};
    for (const element of measure.querySelectorAll<HTMLElement>('[data-tool]')) next[element.dataset.tool!] = element.getBoundingClientRect().width;
    if (Object.keys(next).some((id) => Math.abs((widths[id] ?? -1) - next[id]) > 0.5)) widths = next;
  });

  const folded = $derived.by(() => {
    if (barWidth <= 0 || !widths.more) return new Set<string>();
    // The bar's side padding (16) and More with its gap.
    const available = barWidth - 16 - widths.more - 2;
    return foldTools(tools.map((tool) => ({ id: tool.id, width: widths[tool.id] ?? 64, keep: tool.keep, group: tool.group })), available);
  });
  const shownTools = $derived(tools.filter((tool) => !folded.has(tool.id)));

  /** The buttons that didn't fit, as the first entries of More. */
  function foldedItems(): MenuItem[] {
    return tools.filter((tool) => folded.has(tool.id)).map((tool): MenuItem => ({
      label: tool.label,
      disabled: tool.disabled,
      items: tool.menu ? [{ label: 'The selected queue', disabled: tool.disabled, onselect: tool.run }, { separator: true }, ...tool.menu()] : undefined,
      onselect: tool.href ? () => { location.hash = tool.href!; } : tool.run,
    }));
  }

  function moreItems(): MenuItem[] {
    const extra = foldedItems();
    return extra.length ? [...extra, { separator: true }, ...settingItems()] : settingItems();
  }

  function settingItems(): MenuItem[] {
    const s = settings;
    const limit = s?.globalSpeedLimitBytesPerSecond && s.globalSpeedLimitBytesPerSecond > 0 ? s.globalSpeedLimitBytesPerSecond : 0;
    return [
      {
        label: `Sort by: ${columnTitles.find(([key]) => key === shownSort.key)?.[1] ?? ''}`,
        items: columnTitles.map(([key, label]) => ({
          label,
          checked: shownSort.key === key,
          hint: shownSort.key === key ? (shownSort.ascending ? 'Ascending' : 'Descending') : undefined,
          onselect: () => applySort(nextSort(shownSort, key)),
        })),
      },
      // UI.R42: the same as a right-click on the list's header.
      { label: 'Columns', hidden: !columnsWide || onlyExtraction, items: columnItems() },
      // T7: the selected torrent's facts beside the list, on a window 1440 px wide or more.
      { label: 'Torrent Info', checked: torrentInfoOn && infoWide, hidden: !store.has('torrents'), disabled: !infoWide, hint: infoWide ? undefined : 'Wider window', onselect: () => setTorrentInfo(!torrentInfoOn) },
      { separator: true },
      {
        label: `Simultaneous Downloads: ${s?.maxConcurrentDownloads ?? ''}`,
        disabled: !s,
        items: downloadChoices.map((n) => ({ label: `${n} at a time`, checked: s?.maxConcurrentDownloads === n, onselect: () => saveLimit((x) => { x.maxConcurrentDownloads = n; }) })),
      },
      {
        label: `Connections per Download: ${s?.defaultConnections ?? ''}`,
        disabled: !s,
        items: connectionChoices.map((n) => ({ label: n === 1 ? '1 connection' : `${n} connections`, checked: s?.defaultConnections === n, onselect: () => saveLimit((x) => { x.defaultConnections = n; }) })),
      },
      {
        label: `Speed Limit: ${speedLimitLabel(limit)}`,
        disabled: !s,
        items: [
          { label: 'Unlimited', checked: limit === 0, onselect: () => saveLimit((x) => { x.globalSpeedLimitBytesPerSecond = undefined; }) },
          ...speedChoices.map((mb) => ({ label: `${mb} MB/s`, checked: limit === mb * megabyte, onselect: () => saveLimit((x) => { x.globalSpeedLimitBytesPerSecond = mb * megabyte; }) })),
          { label: 'Other…', checked: !speedIsPreset(limit), onselect: () => { speedText = limit ? String(limit / megabyte) : ''; speedDialog = true; } },
        ],
      },
      { separator: true },
      { label: 'Clean Up Leftover Partial Files…', onselect: () => (cleanup = true) },
    ];
  }

  function queueMenu(action: 'start' | 'stop'): MenuItem[] {
    return queues.map((queue) => ({ label: `${action === 'start' ? 'Start' : 'Stop'} ${queue.name} download queue`, disabled: action === 'start' ? !canStartQueue(queue, allJobs) : !canStopQueue(queue), onselect: () => void queueAction(action, queue.id) }));
  }

  function rowMenuItems(): MenuItem[] {
    const items: MenuItem[] = [];
    const remote = remoteRows.map((row) => row.remote);
    const single = firstLocal?.kind === 'job' ? firstLocal.job : null;
    const sideRows = remoteRows.length + archiveRows.length;
    const plan = planFileMenu(!!single, firstLocal?.kind === 'package', sideRows, removableSideRows, single?.fileRemovedAfterExtraction === true);

    if (remote.length > 0) {
      const actions = remote.map(actionsOf);
      const queuedHere = remote.every(isInProviderQueue);
      const name = providerNameOf(remote[0]);
      items.push(
        { label: 'Select Files…', hidden: !remote.every((j) => actionsOf(j).selectFiles), onselect: () => (remoteOpen = remote[0]) },
        { label: 'Download All Files', hidden: !offersDownloadAll(remote), onselect: () => void each(remote.filter((j) => j.state === 'awaitingSelection'), 'remote.select', (j) => ({ jobId: j.id, all: true }), 'Files chosen.') },
        { label: `Add to ${name}`, hidden: !remote.every((j) => actionsOf(j).approve), onselect: () => void runRemote('approve', remote) },
        { label: 'Enter Link Password…', hidden: !(actions.length === 1 && actions[0].enterLinkPassword), onselect: () => (remoteOpen = remote[0]) },
        { label: 'Retry', hidden: !actions.some((a) => a.retry), onselect: () => void runRemote('retry', remote.filter((j) => actionsOf(j).retry)) },
        { label: 'Check Now', hint: 'F5', hidden: !actions.some((a) => a.checkNow), onselect: () => void runRemote('checkNow', remote.filter((j) => actionsOf(j).checkNow)) },
        { label: `Start Now on ${name}`, hidden: !queuedHere, onselect: () => void each(remote, 'remote.startQueued', (j) => ({ jobId: j.id }), 'Started at the provider.') },
        { label: 'Remove from Queue…', hidden: !queuedHere, onselect: () => askDequeue(remote) },
        { label: 'Details…', hidden: remote.length !== 1, onselect: () => (remoteOpen = remote[0]) },
        { label: 'Remove from List…', hint: 'Delete', onselect: () => void removeSideRows() },
      );
    }

    const archive = archivePlan;
    const records = archiveRows.map((row) => row.set);
    const onlyRecord = localRows.length === 0 && records.length === 1 ? records[0] : null;
    // As ArchiveMenu.Apply: nothing at all for a selection with no archive in it.
    const showsArchive = records.length > 0 || !['no archive selected', 'select an archive download first'].includes(archive.extractReason ?? '');
    if (showsArchive && localRows.length === 0) {
      if (items.length) items.push({ separator: true });
      items.push(...archiveItems(archive, records, onlyRecord));
    }

    if (localRows.length > 0) {
      if (items.length) items.push({ separator: true });
      const unfinished = selectedJobs.filter((job) => job.state !== 'completed');
      const sizeOnly = single?.finalFilePath && !single.fileRemovedAfterExtraction;
      const commands = fileCommands(single, plan, selectedJobs.length, blocksAddressCommands(selectedJobs));
      const torrentJobs = selectedJobs.filter((job) => job.torrent);
      // T3's items only where the server has them; an older server's torrents keep the T2 menu.
      const controlJobs = torrentJobs.filter(hasTorrentControls);
      items.push(
        { label: menuLabel('Copy path', plan.openReason), disabled: plan.openReason !== null || !sizeOnly, onselect: () => void copy(single?.finalFilePath ?? '', 'Path copied.') },
        { label: menuLabel('Copy folder path', plan.openReason), disabled: plan.openReason !== null || !single?.destinationDirectory, onselect: () => void copy(single?.destinationDirectory ?? '', 'Folder path copied.') },
        // WEB-T4: the info hash and the name; the trackers stay with the server.
        { label: 'Copy Magnet Link', hidden: !magnetToCopy(single?.torrent), onselect: () => void copy(magnetToCopy(single?.torrent) ?? '', 'Magnet link copied.') },
        // T10: one finished download that is a .torrent file goes to the torrent dialog.
        { label: 'Open as Torrent…', hidden: !onopentorrent || selectedJobs.length !== 1 || !opensAsTorrent(single), onselect: () => { if (single) onopentorrent?.(single); } },
        { label: commands.move.label, hint: 'Ctrl+M', hidden: commands.move.hidden, disabled: !commands.move.enabled, onselect: () => (moving = single) },
        { separator: true },
        ...(showsArchive ? [...archiveItems(archive, records, null), { separator: true } as MenuItem] : []),
        { label: 'Resume', disabled: !canResume, onselect: () => void resume() },
        { label: 'Stop', disabled: !canPause, onselect: () => void stop() },
        { label: 'Cancel Package…', hidden: packageRows.length === 0, disabled: !canCancelPackage, onselect: askCancelPackage },
        // T1: one torrent whose files are known and that hasn't finished.
        { label: 'Choose Files…', hidden: torrentJobs.length === 0, disabled: !canChooseFiles(selectedJobs), onselect: () => { if (single) choosingFiles = single.id; } },
        // T2: a finished torrent shares its pieces until the seeding goal; these start or stop it for the selection.
        { label: 'Start Seeding', hidden: seedTargets(selectedJobs, true).length === 0, disabled: !connected, onselect: () => void seedSelection(true) },
        { label: 'Stop Seeding', hidden: seedTargets(selectedJobs, false).length === 0, disabled: !connected, onselect: () => void seedSelection(false) },
        // T3: check the files again, ask the trackers for peers now, and the torrent's own limits (its Options tab).
        { label: 'Force Recheck', hidden: controlJobs.length === 0, disabled: !connected || !controlJobs.some(canRecheck), onselect: () => void recheckSelection() },
        { label: 'Announce Now', hidden: controlJobs.length === 0, disabled: !connected || !controlJobs.some(canAnnounce), onselect: () => void announceSelection() },
        { label: 'Set Limits…', hidden: controlJobs.length === 0, disabled: selectedJobs.length !== 1 || !hasTorrentControls(single), onselect: () => { if (single?.torrent) { detailsTab = 'options'; details = single.id; } } },
        { label: commands.redownload.label, hidden: commands.redownload.hidden, disabled: !commands.redownload.enabled, onselect: () => { if (single) { detailsStart = 'redownload'; details = single.id; } } },
        { label: commands.refresh.label, hidden: commands.refresh.hidden, disabled: !commands.refresh.enabled, onselect: () => (refreshing = single) },
        { separator: true },
        { label: 'Add to Queue', items: queues.map((queue) => ({ label: queue.name, onselect: () => void addToQueue(queue.id) })) },
        { label: 'Remove from Queue', disabled: !selectedJobs.some((job) => queueText(job) !== ''), onselect: () => void removeFromQueue() },
        {
          label: 'Priority',
          disabled: unfinished.length === 0,
          items: [[1, 'High'], [0, 'Normal'], [-1, 'Low']].map(([priority, label]) => ({
            label: label as string,
            checked: unfinished.length > 0 && unfinished.every((job) => (job.priority ?? 0) === priority),
            onselect: () => void setPriority(priority as number),
          })),
        },
        { separator: true },
        { label: 'Delete…', hint: 'Delete', onselect: () => askDelete(false) },
        { separator: true },
        // As the desktop: on a package row it opens the package's window, otherwise the file's.
        firstLocal?.kind === 'package' && localRows.length === 1
          ? { label: 'Progress Details…', onselect: () => openPackage(firstLocal.group) }
          : { label: menuLabel('Progress Details…', plan.fileReason), disabled: plan.fileReason !== null, onselect: () => (details = single!.id) },
        { label: 'Group into Package…', hidden: !canGroup(selectedJobs.length, localRows.length), onselect: groupSelected },
        { label: 'Remove from Package', hidden: !selectedJobs.some((job) => packageOf(job.id)), onselect: () => void ungroupSelected() },
        { label: 'Rename Package…', hidden: !(firstLocal?.kind === 'package' || (firstLocal?.kind === 'job' && !!packageOf(firstLocal.job.id))), onselect: renamePackage },
        { label: commands.category.label, disabled: !commands.category.enabled, onselect: () => (recategorizing = selectedJobs) },
        { label: menuLabel('Properties', plan.fileReason), disabled: plan.fileReason !== null, onselect: () => (details = single!.id) },
      );
    }
    return items;
  }

  /** ArchiveMenu.Apply: Extract (saying why when it can't run, unless Retry already offers it), the password, retry, cancel and remove. */
  function archiveItems(archive: typeof archivePlan, records: ArchiveSet[], onlyRecord: ArchiveSet | null): MenuItem[] {
    return [
      { label: archive.extractText, hidden: !archive.showExtract, disabled: !archive.canExtract, onselect: () => void extractSelection() },
      { label: archivePasswordText(archive.password?.needed === true), hidden: !archive.password, onselect: () => (passwordTarget = archive.password) },
      { label: archiveRetryText(archive.retry), hidden: archive.retry.length === 0, onselect: () => retryArchives(archive.retry) },
      { label: 'Cancel Extraction', hidden: archive.cancel.length === 0, onselect: () => void each(archive.cancel, 'archive.cancel', (s) => ({ setId: s.id }), 'Extraction cancelled.') },
      { label: 'Copy Folder Path', hidden: !(onlyRecord?.state === 'completed' && onlyRecord.destination), onselect: () => void copy(onlyRecord?.destination ?? '', 'Folder path copied.') },
      { label: 'Details…', hidden: !onlyRecord, onselect: () => (archiveOpen = onlyRecord) },
      records.length > 0
        ? { label: 'Delete Extraction Record…', hint: 'Delete', disabled: !records.some(archiveRemovable), onselect: () => void removeSideRows() }
        : { label: 'Delete Extraction Record…', hidden: archive.remove.length === 0, onselect: () => askRemoveRecords(archive.remove) },
    ];
  }

  async function copy(text: string, done: string) {
    if (await copyText(text)) store.toast(done);
    else store.toast("The browser would not copy it. Select the text in the download's details instead.", 'error');
  }


  function openMenu(row: Row, x: number, y: number) {
    if (row.kind === 'torrentFile') return;
    columnsOpener = null;
    if (!selected.has(row.id)) {
      selected = new Set([row.id]);
      anchor = row.id;
    }
    cursor = row.id;
    menuOpenedAt = Date.now();
    menu = { x, y, items: rowMenuItems(), label: `Actions for ${row.view.name}` };
  }

  function contextmenu(row: Row, event: MouseEvent) {
    event.preventDefault();
    if (Date.now() - menuOpenedAt < 700) return;
    openMenu(row, event.clientX, event.clientY);
  }

  function menuButton(row: Row, event: MouseEvent) {
    event.stopPropagation();
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    openMenu(row, box.left, box.bottom);
  }

  function pointerdown(row: Row, event: PointerEvent) {
    // Every press, mouse included, ends the swallow of an earlier long press whose click never came.
    longPress.down(event.pointerType, event.clientX, event.clientY, () => openMenu(row, event.clientX, event.clientY));
  }

  function toolbarMenu(event: MouseEvent, label: string, items: MenuItem[]) {
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    columnsOpener = null;
    menuOpenedAt = Date.now();
    menu = { x: box.left, y: box.bottom + 2, items, label };
  }

  async function closeMenu() {
    menu = null;
    const opener = columnsOpener;
    columnsOpener = null;
    if (!opener) {
      grid?.focus({ preventScroll: true });
      return;
    }
    // The header's menu: back to the header button it opened from, or the nearest one shown when that column was just
    // hidden. The choice is made after the menu closes, so wait for the header to show it.
    await tick();
    if (document.activeElement && document.activeElement !== document.body) return;
    const back = opener.button?.isConnected ? opener.button : opener.head.isConnected ? opener.head.querySelector<HTMLElement>('button') : null;
    (back ?? grid)?.focus({ preventScroll: true });
  }

  // ---- Keys ----

  function focusSearch() {
    const box = document.getElementById('list-search') as HTMLInputElement | null;
    box?.focus();
    box?.select();
  }

  function moveTo(id: string | null, extend: boolean, keepSelection: boolean) {
    if (!id) return;
    cursor = id;
    const fileRow = (key: string) => rows.find((r) => r.id === key)?.kind === 'torrentFile';
    // The cursor may rest on a torrent's file row, but that row never joins the selection.
    if (extend) selected = new Set([...selectAfterClick(selected, rows.map((r) => r.id), id, anchor ?? id, { shift: true, ctrl: false })].filter((key) => !fileRow(key)));
    else if (fileRow(id)) {
      if (!keepSelection) selected = new Set();
    } else if (!keepSelection) {
      selected = new Set([id]);
      anchor = id;
    }
    reveal(id);
  }

  function findNext(step: 1 | -1) {
    if (rows.length === 0) return;
    const at = cursor ? rows.findIndex((r) => r.id === cursor) : -1;
    const to = at < 0 ? (step === 1 ? 0 : rows.length - 1) : (at + step + rows.length) % rows.length;
    moveTo(rows[to].id, false, false);
    grid?.focus({ preventScroll: true });
  }

  function gridKey(event: KeyboardEvent) {
    const target = event.target as HTMLElement;
    const onControl = target !== grid && (target.tagName === 'BUTTON' || target.tagName === 'INPUT');
    const ctrl = event.ctrlKey || event.metaKey;
    const index = cursor ? rows.findIndex((r) => r.id === cursor) : -1;
    const to = moveCursor(event.key, index, rows.length);

    if (to !== null && !event.altKey) {
      event.preventDefault();
      moveTo(rows[to]?.id ?? null, event.shiftKey, ctrl);
      return;
    }
    if (ctrl && event.key.toLowerCase() === 'a') {
      event.preventDefault();
      selected = new Set(rows.filter((r) => r.kind !== 'torrentFile').map((r) => r.id));
      return;
    }
    // A box or button that has the focus answers Space and Enter itself; every other key is the list's.
    if (onControl && (event.key === ' ' || event.key === 'Enter')) return;
    if (event.key === ' ') {
      event.preventDefault();
      const row = rows[index];
      if (row) toggle(row);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      runEnter();
    } else if (event.key === 'Delete') {
      event.preventDefault();
      if (selected.size > 0) askDelete(event.shiftKey);
    } else if (event.key === 'F5' && remoteRows.length > 0) {
      event.preventDefault();
      void runRemote('checkNow', remoteRows.map((r) => r.remote).filter((j) => actionsOf(j).checkNow));
    } else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const row = rows[index] ?? rows[0];
      if (!row) return;
      const box = document.getElementById(domId(row.id))?.getBoundingClientRect();
      openMenu(row, (box?.left ?? 0) + 40, (box?.bottom ?? 0));
    }
  }

  /** Enter does the next thing the selected rows wait for. */
  function runEnter() {
    if (selectedRows.length === 0) {
      const row = rows.find((r) => r.id === cursor);
      if (row) open(row);
      return;
    }
    if (remoteRows.length > 0 && localRows.length === 0 && archiveRows.length === 0) {
      const jobs = remoteRows.map((r) => r.remote);
      void runRemote(remoteEnterAction(jobs), jobs, jobs.length === 1);
      return;
    }
    if (selectedRows.length === 1) open(selectedRows[0]);
  }

  function windowKey(event: KeyboardEvent) {
    if (event.defaultPrevented) return;
    if (document.querySelector('[role="dialog"], [role="menu"]')) return;
    const ctrl = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();
    if (ctrl && !event.shiftKey && !event.altKey && key === 'f') {
      event.preventDefault();
      focusSearch();
      return;
    }
    if (isTypingTarget(event.target)) return;
    if (event.key === 'F3') {
      event.preventDefault();
      findNext(event.shiftKey ? -1 : 1);
    } else if (event.altKey && !ctrl && !event.shiftKey && key === 'n') {
      // Ctrl+N belongs to the browser; Alt+N opens Add URL.
      event.preventDefault();
      onadd();
    } else if (ctrl && event.shiftKey && key === 's') {
      event.preventDefault();
      applySort({ key: 'size', ascending: false });
    } else if (ctrl && !event.shiftKey && key === 'm') {
      if (firstLocal?.kind === 'job' && firstLocal.job.state === 'completed' && !firstLocal.job.fileRemovedAfterExtraction && !firstLocal.job.torrent) {
        event.preventDefault();
        moving = firstLocal.job;
      }
    }
  }

  const sortMark = (key: SortKey) => (shownSort.key === key ? (shownSort.ascending ? '▲' : '▼') : '');
  const ariaSort = (key: SortKey) => (shownSort.key === key ? (shownSort.ascending ? 'ascending' : 'descending') : 'none') as 'ascending' | 'descending' | 'none';
</script>

<!-- The finger may lift on the menu the long press opened, not on the row: the swallow still runs out. -->
<svelte:window onkeydown={windowKey} onpointerup={() => longPress.up()} onpointercancel={() => longPress.cancelled()} />

<!-- Toolbar: Add URL | Resume, Stop, Stop All, [Cancel Package] | Delete, Delete Completed | Extract | [Check now] | Options, Scheduler | Start Queue, Stop Queue | More.
     What doesn't fit the width goes into More, least needed first (U-WU-2, U-WU-9); the bar never scrolls sideways. -->
{#snippet toolFace(tool: Tool)}<Icon name={tool.icon} class={tool.tone} /><span>{tool.label}</span>{/snippet}
<div bind:this={bar} class="relative flex items-stretch gap-0.5 overflow-hidden border-b border-divider bg-chrome px-2 py-1" role="toolbar" aria-label="Download actions">
  {#each shownTools as tool, index (tool.id)}
    {#if index > 0 && tool.group !== shownTools[index - 1].group}<span class="toolsep"></span>{/if}
    {#if tool.href}
      <a class="toolbtn" title={tool.title} href={tool.href}>{@render toolFace(tool)}</a>
    {:else if tool.menu}
      <span class="inline-flex shrink-0">
        <button class="toolbtn" title={tool.title} disabled={tool.disabled} onclick={tool.run}>{@render toolFace(tool)}</button>
        <button class="toolcaret" aria-label={tool.menuLabel} title={tool.menuLabel} onclick={(event) => toolbarMenu(event, tool.menuLabel ?? '', tool.menu?.() ?? [])}><Icon name="chevron-down" size={12} /></button>
      </span>
    {:else}
      <button class="toolbtn" title={tool.title} disabled={tool.disabled} onclick={tool.run}>{@render toolFace(tool)}</button>
    {/if}
  {/each}
  <span class="toolsep"></span>
  <button class="toolbtn" title={folded.size ? 'More actions, sort, limits, clean up leftover files' : 'Sort, limits, clean up leftover files'} aria-haspopup="menu" onclick={(event) => toolbarMenu(event, 'More', moreItems())}><Icon name="more" /><span>More</span></button>
  <!-- The same buttons, unseen, to know each one's width. -->
  <div bind:this={measure} class="tool-measure" aria-hidden="true" inert>
    {#each tools as tool (tool.id)}
      {#if tool.menu}<span class="inline-flex shrink-0" data-tool={tool.id}><span class="toolbtn">{@render toolFace(tool)}</span><span class="toolcaret"><Icon name="chevron-down" size={12} /></span></span>
      {:else}<span class="toolbtn" data-tool={tool.id}>{@render toolFace(tool)}</span>{/if}
    {/each}
    <span class="toolbtn" data-tool="more"><Icon name="more" /><span>More</span></span>
  </div>
</div>

<ArchiveNoticeBar onpassword={(target) => (passwordTarget = target)} onshow={(set) => (archiveOpen = set)} />

{#snippet head(key: SortKey, label: string, cls: string)}
  <div role="columnheader" aria-sort={ariaSort(key)} class={cls}>
    <button class="header-cell w-full" onclick={() => headerSort(key)} title="Sort by {label}">{label}<span class="sort-mark" aria-hidden="true">{sortMark(key)}</span></button>
  </div>
{/snippet}

{#if torrentCols && torrentCount}
  <!-- A phone has no sidebar in view: the torrent views are chips, with what the torrents move under them. -->
  <nav aria-label="Torrent views" class="flex gap-1.5 overflow-x-auto border-b border-divider bg-chrome px-3 py-2 md:hidden">
    <a href={viewHash('torrents')} class="torrent-chip" aria-current={view === 'torrents' ? 'page' : undefined}>All <span class="tabular">{torrentCount.all}</span></a>
    {#each torrentFilters as filter (filter.key)}
      <a href={viewHash(`torrents-${filter.key}`)} class="torrent-chip" aria-current={view === `torrents-${filter.key}` ? 'page' : undefined}>{filter.label} <span class="tabular">{torrentCount[filter.key]}</span></a>
    {/each}
    {#each torrentExtraViews as extra (extra.key)}
      {#if extraViewShows(torrentExtraCount?.[extra.key], `torrents-${extra.key}`, view)}
        <a href={viewHash(`torrents-${extra.key}`)} class="torrent-chip" aria-current={view === `torrents-${extra.key}` ? 'page' : undefined}>{extra.label} <span class="tabular">{torrentExtraCount?.[extra.key] ?? 0}</span></a>
      {/if}
    {/each}
  </nav>
  {#if torrentLine}<p class="tabular border-b border-divider px-3 py-1 text-[12px] text-muted md:hidden">{torrentLine}</p>{/if}
{/if}

<!-- T7: the list, and Torrent Info on its right on a wide window (it narrows the list's columns, never scrolls it sideways). -->
<div class="flex items-start">
<div class="min-w-0 flex-1">
<!-- The header's buttons take the focus; a right-click, Shift+F10 or the menu key on one opens the columns' menu (UI.R42). -->
<!-- svelte-ignore a11y_interactive_supports_focus -->
<div class="list-grid sticky top-0 z-10 hidden border-b border-divider bg-chrome text-[12px] font-semibold md:grid" class:with-extraction={showExtraction} class:only-extraction={onlyExtraction} class:torrent-cols={torrentCols} class:with-info={infoRoom} role="row" style:--cols-md={colsMd} style:--cols-lg={colsLg} oncontextmenu={headerMenu}>
  <label class="flex items-center justify-center self-stretch"><input type="checkbox" aria-label="Select all" checked={selectableCount > 0 && selected.size === selectableCount} onchange={toggleAll} /></label>
  <!-- UI.R42: every column has its header; one hidden from the header's menu stays out (col-off). -->
  {#each listColumns.defs as def (def.key)}
    {#if def.key === 'extraction' ? showExtraction : !onlyExtraction || shownColumns.has(def.key)}
      {@render head(def.key, def.title, `text-${def.align ?? 'left'}${def.wide ? ' hidden lg:block wide-col' : ''}${off(def.key) ? ' col-off' : ''}`)}
    {/if}
  {/each}
</div>

<!-- One tab stop for the whole list: the arrows move, Shift extends, Space ticks, Enter does the row's main thing. -->
<div
  bind:this={grid}
  role="grid"
  tabindex="0"
  aria-label="Downloads"
  aria-multiselectable="true"
  aria-activedescendant={cursor ? domId(cursor) : undefined}
  class="list-body pb-24 outline-none md:pb-6"
  class:stale={!connected && store.model.snapshots > 0}
  onkeydown={gridKey}
  onfocus={() => { if (!cursor && rows.length > 0) cursor = rows[0].id; }}
>
  {#each rows as row (row.id)}
    {@const v = connected || store.model.snapshots === 0 ? row.view
      : notCurrentView(row.view, row.kind === 'job' ? [row.job] : row.kind === 'package' ? row.jobs : [])}
    <!-- The grid above handles the keys for every row. -->
    <!-- svelte-ignore a11y_click_events_have_key_events -->
    <div
      id={domId(row.id)}
      animate:flip={{ duration: 180 }}
      class="list-row group border-b border-divider/60 text-[12.5px] {selected.has(row.id) ? 'bg-selected' : 'hover:bg-hover'}"
      class:with-extraction={showExtraction}
      class:only-extraction={onlyExtraction}
      class:torrent-cols={torrentCols}
      class:with-info={infoRoom}
      style:--cols-md={colsMd}
      style:--cols-lg={colsLg}
      class:is-cursor={cursor === row.id}
      role="row"
      aria-selected={selected.has(row.id)}
      tabindex="-1"
      onclick={(event) => click(row, event)}
      ondblclick={() => open(row)}
      oncontextmenu={(event) => contextmenu(row, event)}
      onpointerdown={(event) => pointerdown(row, event)}
      onpointermove={(event) => longPress.move(event.clientX, event.clientY)}
    >
      <!-- The whole cell is the box's target, so a finger can tick it (U-WU-8). -->
      <!-- svelte-ignore a11y_no_noninteractive_element_interactions, a11y_click_events_have_key_events -->
      {#if row.kind === 'torrentFile'}
        <span class="cell-check"></span>
      {:else}
      <label class="cell-check flex items-center justify-center self-stretch" onclick={(event) => event.stopPropagation()}>
        <input type="checkbox" tabindex="-1" aria-label="Select {v.name}" checked={selected.has(row.id)} onclick={(event) => event.stopPropagation()} onchange={() => toggle(row)} />
      </label>
      {/if}

      <span class="cell-name flex min-w-0 items-center gap-1.5 {(row.kind === 'job' && row.depth) || row.kind === 'torrentFile' ? 'pl-6' : ''} {row.kind === 'remote' ? 'italic' : ''}">
        {#if row.kind === 'job' && row.job.torrent}
          <!-- T1: a torrent row opens to list its files when it has several; the swarm tells it from a package. -->
          {#if showsFiles(row.job.torrent)}
            <button tabindex="-1" class="row-expand rounded p-0.5 hover:bg-hover" aria-label={expanded.has(row.job.id) ? 'Hide files' : 'Show files'} title="Show or hide files" aria-expanded={expanded.has(row.job.id)} onclick={(event) => { event.stopPropagation(); toggleExpanded(row.job.id); }}>
              <Icon name={expanded.has(row.job.id) ? 'chevron-down' : 'chevron-right'} size={14} />
            </button>
          {/if}
          <Icon name={row.job.torrent.hasMetadata ? 'swarm' : 'link'} size={16} class="shrink-0 text-icon-info" />
        {:else if row.kind === 'torrentFile'}
          <Icon name={row.more ? 'info' : row.problem ? 'alert' : row.file ? 'file' : 'queue'} size={14} class="shrink-0 {row.problem ? 'text-icon-danger' : 'text-icon-neutral'}" />
        {:else if row.kind === 'package'}
          <button tabindex="-1" class="row-expand rounded p-0.5 hover:bg-hover" aria-label={row.expanded ? 'Collapse' : 'Expand'} aria-expanded={row.expanded} onclick={(event) => { event.stopPropagation(); toggleExpanded(row.group.id); }}>
            <Icon name={row.expanded ? 'chevron-down' : 'chevron-right'} size={14} />
          </button>
          <Icon name="package" size={16} class="shrink-0 text-icon-media" />
        {:else if row.kind === 'remote'}
          <Icon name="cloud" size={16} class="shrink-0 text-icon-info" />
        {:else if row.kind === 'archive'}
          <Icon name="archive" size={16} class="shrink-0 text-icon-media" />
        {:else}
          <Icon name="file" size={15} class="shrink-0 text-icon-neutral" />
        {/if}
        <span class="min-w-0 flex-1 truncate {row.kind === 'package' || (row.kind === 'job' && row.job.torrent) ? 'font-semibold' : ''} {row.kind === 'torrentFile' && !row.file ? 'text-muted' : ''}" title={row.kind === 'archive' ? row.set.firstVolume : row.kind === 'torrentFile' && row.file ? row.file.path : v.name}>{v.name}</span>
        {#if row.kind !== 'torrentFile'}<button tabindex="-1" class="row-menu" aria-label="Actions for {v.name}" aria-haspopup="menu" onclick={(event) => menuButton(row, event)}><Icon name="more" size={16} /></button>{/if}
      </span>

      <span class="cell-queue text-center text-muted" class:col-off={off('queue')} title={v.queueTip}>{v.queue}</span>
      <span class="cell-size tabular text-right" class:col-off={off('size')}>{v.sizeText}</span>
      <span class="cell-status min-w-0" class:col-off={off('status')}>
        <span class="block truncate tone-{v.statusTone}" title={v.statusTip}>{v.status}</span>
        {#if v.bar !== null}<span class="mt-0.5 block"><ProgressBar value={v.bar} label={row.kind === 'package' ? 'Package progress' : v.swarm?.goal !== null && v.swarm?.goal !== undefined ? 'Seeding goal' : 'Progress'} /></span>{/if}
        {#if row.kind === 'remote'}
          <button tabindex="-1" class="mt-0.5 rounded border border-divider px-1.5 py-0 text-[11px] not-italic text-text hover:bg-hover" onclick={(event) => { event.stopPropagation(); remoteOpen = row.remote; }}>Actions</button>
        {/if}
      </span>
      {#if showExtraction}
        <span class="cell-extract truncate tone-{v.extractionTone}" class:col-off={off('extraction')} title={v.extractionTip}>
          {v.extraction}
          {#if row.kind === 'job'}
            {@const set = archiveByJob.get(row.job.id)}
            {#if set && set.needsPassword && !set.hasPassword}
              <button tabindex="-1" class="ml-1 align-middle" aria-label="Enter the archive password" onclick={(event) => { event.stopPropagation(); passwordTarget = targetOf(set); }}><Icon name="key" size={13} /></button>
            {/if}
          {:else if row.kind === 'archive' && row.set.needsPassword && !row.set.hasPassword}
            <button tabindex="-1" class="ml-1 align-middle" aria-label="Enter the archive password" onclick={(event) => { event.stopPropagation(); passwordTarget = targetOf(row.set); }}><Icon name="key" size={13} /></button>
          {/if}
        </span>
      {/if}
      <span class="cell-eta tabular text-right" class:col-off={off('eta')}>{connected ? v.etaText : ''}</span>
      <span class="cell-rate tabular text-right" class:col-off={off('rate')}>{#if connected}{v.rateText}{#if v.etaText}<span class="phone-eta">{v.rateText ? ' · ' : ''}{v.etaText} left</span>{/if}{/if}</span>
      <!-- The torrent columns: blank for a row that isn't a torrent. Peers counts every connected peer, the seeds among
           them; Up is known while a torrent seeds. UI.R42: each can be shown or hidden in any view. -->
      <span class="cell-swarm tabular text-right" class:col-off={off('seeds')} title="Seeds connected">{connected ? v.swarm?.seedsText ?? '' : ''}</span>
      <span class="cell-swarm tabular text-right" class:col-off={off('peers')} title="Peers connected, seeds included">{connected ? v.swarm?.peersText ?? '' : ''}</span>
      <span class="cell-swarm tabular text-right" class:col-off={off('up')}>{connected ? v.swarm?.upText ?? '' : ''}</span>
      <span class="cell-swarm tabular truncate text-right" class:col-off={off('ratio')} title={v.swarm?.goal !== null && v.swarm?.goal !== undefined ? 'On the way to the seeding goal of Options › Torrents' : undefined}>{v.swarm?.ratioText ?? ''}</span>
      <span class="cell-swarm tabular text-right" class:col-off={off('uploaded')} title={v.swarm ? 'Uploaded in all' : undefined}>{v.swarm?.uploadedText ?? ''}</span>
      <span class="cell-swarm tabular text-right" class:col-off={off('seedTime')} title={v.swarm?.seedTimeText ? 'Seeding for' : undefined}>{connected ? v.swarm?.seedTimeText ?? '' : ''}</span>
      <span class="cell-swarm truncate" class:col-off={off('seedGoal')} title={v.swarm?.goalText || undefined}>{v.swarm?.goalText ?? ''}</span>
      <span class="cell-added hidden text-muted lg:block" class:col-off={off('lastTry')}>{v.lastTryText}</span>
      <span class="cell-desc truncate text-muted" class:col-off={off('description')} title={v.description}>{v.description}{#if row.kind === 'job' && torrentCols && v.swarm && connected}<span class="phone-swarm">{v.description ? ' · ' : ''}{v.swarm.line}</span>{/if}</span>
    </div>
  {:else}
    <div class="px-6 py-16 text-center text-muted">
      {#if store.model.snapshots === 0}
        Connecting to the server…
      {:else if search}
        Nothing matches “{search}”.
      {:else}
        <p class="mb-3 whitespace-pre-line">{emptyText(view, providerName)}</p>
        {#if view !== 'remote'}<button class="btn-primary" onclick={onadd}><Icon name="add" size={16} /> Add URL</button>{/if}
      {/if}
    </div>
  {/each}
</div>
</div>
{#if infoRoom}
  <div class="sticky top-0 hidden max-h-[calc(100dvh-7rem)] w-80 shrink-0 self-start min-[1440px]:flex">
    {#if infoTorrent}
      <TorrentInfoPane job={infoTorrent} onmore={() => { details = infoTorrent?.id ?? null; }} onrecheck={() => void recheckSelection()} onhide={hideTorrentInfo} />
    {:else}
      <aside class="flex min-h-[8rem] w-full flex-col border-l border-divider bg-surface text-[12.5px]" aria-label="Torrent info">
        <div class="flex h-[27px] shrink-0 items-center justify-between border-b border-divider bg-chrome pl-3 pr-1 text-[12px] font-semibold">
          <span>Torrent Info</span>
          <button class="btn-icon h-6 w-6" title="Hide Torrent Info. More › Torrent Info shows it again." aria-label="Hide Torrent Info" onclick={hideTorrentInfo}><Icon name="close" size={13} /></button>
        </div>
        <p class="px-3 py-2.5 text-muted">Select one torrent to see its info here.</p>
      </aside>
    {/if}
  </div>
{/if}
</div>

{#if menu}
  <ContextMenu items={menu.items} x={menu.x} y={menu.y} label={menu.label} onclose={closeMenu} />
{/if}

{#if confirm}
  <Dialog title={confirm.title} onclose={() => answer(false)}>
    <p class="text-[13px]">{confirm.text}</p>
    {#if confirm.box}
      <label class="mt-3 flex items-start gap-2 text-[13px]">
        <input class="mt-0.5" type="checkbox" bind:checked={boxChecked} />
        <span>{confirm.box.label}</span>
      </label>
    {/if}
    {#snippet footer()}
      <button class="btn-danger" onclick={() => answer(true)}>{confirm?.ok}</button>
      <button class="btn" data-autofocus onclick={() => answer(false)}>{confirm?.cancel ?? 'Cancel'}</button>
    {/snippet}
  </Dialog>
{/if}

{#if nameDialog}
  <Dialog title={nameDialog.title} onclose={() => (nameDialog = null)}>
    <form id="package-name-form" onsubmit={(event) => { event.preventDefault(); void submitName(); }}>
      <label class="label" for="package-name">Package name</label>
      <input id="package-name" class="field" maxlength="100" bind:value={nameText} autocomplete="off" onfocus={(event) => event.currentTarget.select()} />
    </form>
    {#snippet footer()}
      <button class="btn-primary" type="submit" form="package-name-form" disabled={!nameText.trim()}>OK</button>
      <button class="btn" onclick={() => (nameDialog = null)}>Cancel</button>
    {/snippet}
  </Dialog>
{/if}

{#if cleanup}
  <Dialog title="Clean up leftover partial files" onclose={() => (cleanup = false)}>
    {@const known = settings && store.model.status?.orphanPartFiles ? store.model.status.orphanPartFiles : []}
    <p class="mb-2 text-[13px]">
      {known.length === 0
        ? 'No leftover partial files were found at the last scan. Inbound looks again before cleaning.'
        : `${known.length} partial file(s) are not attached to any download:`}
    </p>
    {#if known.length > 0}
      <ul class="mb-2 max-h-40 overflow-y-auto break-all font-mono text-[12px]">
        {#each known.slice(0, 8) as path}<li>{path}</li>{/each}
        {#if known.length > 8}<li>…</li>{/if}
      </ul>
    {/if}
    <p class="hint">Quarantine moves them into a .dm-orphans folder next to where they were found. Partial files of existing downloads are never touched.</p>
    {#snippet footer()}
      <button class="btn-primary" onclick={() => runCleanup(false)}>Quarantine</button>
      <button class="btn-danger" onclick={() => runCleanup(true)}>Delete</button>
      <button class="btn" onclick={() => (cleanup = false)}>Cancel</button>
    {/snippet}
  </Dialog>
{/if}

{#if speedDialog}
  <Dialog title="Speed limit" onclose={() => (speedDialog = false)}>
    <form id="speed-form" onsubmit={(event) => { event.preventDefault(); void setCustomSpeed(); }}>
      <label class="label" for="speed-limit">Total speed for all downloads (MB/s)</label>
      <input id="speed-limit" class="field" type="number" min="0.1" max="100000" step="any" bind:value={speedText} />
    </form>
    {#snippet footer()}
      <button class="btn-primary" type="submit" form="speed-form">Set</button>
      <button class="btn" onclick={() => (speedDialog = false)}>Cancel</button>
    {/snippet}
  </Dialog>
{/if}

{#if packageOpen}
  <PackageDialog groupId={packageOpen} onclose={() => (packageOpen = null)} onopenfile={(id) => { details = id; }} />
{/if}

{#if details}
  <!-- Keyed: a toast's Show for another download makes a new dialog, not this one with the last download's state. -->
  {#key details}
  <JobDetails jobId={details} start={detailsStart} startTab={detailsTab} set={archiveByJob.get(details)} onclose={() => { details = null; detailsStart = undefined; detailsTab = undefined; }} onpassword={(target) => { details = null; detailsStart = undefined; detailsTab = undefined; passwordTarget = target; }} />
  {/key}
{/if}

{#if moving}
  <MoveRename job={store.model.jobs[moving.id] ?? moving} onclose={() => (moving = null)} />
{/if}

{#if choosingFiles}
  <TorrentChooseFiles jobId={choosingFiles} onclose={() => (choosingFiles = null)} />
{/if}

{#if refreshing}
  <RefreshAddress job={store.model.jobs[refreshing.id] ?? refreshing} onclose={() => (refreshing = null)} />
{/if}

{#if recategorizing}
  <ChangeCategory jobs={recategorizing} onclose={() => (recategorizing = null)} />
{/if}


{#if remoteOpen}
  <RemoteActions remote={store.model.remoteJobs[remoteOpen.id] ?? remoteOpen} onclose={() => (remoteOpen = null)} />
{/if}

{#if archiveOpen}
  <ArchiveRecord set={store.model.archives[archiveOpen.id] ?? archiveOpen} onclose={() => (archiveOpen = null)} onpassword={(target) => { archiveOpen = null; passwordTarget = target; }} />
{/if}

{#if passwordTarget}
  <ArchivePassword target={passwordTarget} onclose={() => (passwordTarget = null)} />
{/if}

{#if extractFromDisk}
  <ExtractFromDisk onclose={() => (extractFromDisk = false)} />
{/if}

<style>
  .toolbtn {
    display: inline-flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2px;
    min-width: 64px;
    /* The bar scrolls sideways when its buttons are too many for the width; they must not shrink into one another. */
    flex-shrink: 0;
    padding: 4px 8px;
    border-radius: 4px;
    font-size: 11.5px;
    white-space: nowrap;
    background: transparent;
    color: inherit;
    text-decoration: none;
  }
  .toolbtn:hover:not(:disabled) { background: var(--hover); }
  .toolbtn:disabled { opacity: 0.4; cursor: default; }
  .toolcaret {
    display: inline-flex;
    flex-shrink: 0;
    align-items: center;
    padding: 0 2px;
    margin-left: -2px;
    border-radius: 4px;
    background: transparent;
    color: var(--muted);
  }
  .toolcaret:hover { background: var(--hover); }
  .toolsep { width: 1px; margin: 6px 4px; background: var(--divider); flex-shrink: 0; }
  /* Unseen copies of the buttons, to measure them; the bar's own overflow hides them. */
  .tool-measure { position: absolute; left: 0; top: 0; display: flex; gap: 2px; visibility: hidden; pointer-events: none; }
  /* A phone keeps the words under the icons, smaller (U-WU-9); what doesn't fit is in More. */
  @media (max-width: 767px) {
    .toolbtn { min-width: 52px; padding: 4px 5px; font-size: 10.5px; }
  }
  @media (pointer: coarse) {
    .toolbtn { min-height: 44px; }
    .toolcaret { min-width: 28px; justify-content: center; }
  }

  .list-grid, .list-row {
    display: grid;
    align-items: center;
    column-gap: 10px;
    padding: 0 10px 0 4px;
  }
  /* UI.R42: the tracks are those of the columns that show (listTemplate): --cols-md below 1024 px and beside Torrent
     Info, --cols-lg from 1024 px. */
  .list-grid { grid-template-columns: var(--cols-md); height: 30px; }
  .header-cell { padding: 0; background: transparent; white-space: nowrap; font: inherit; color: inherit; text-align: inherit; }
  .header-cell:hover { text-decoration: underline; }
  .sort-mark { margin-left: 3px; font-size: 9px; color: var(--muted); }

  /* A phone: each download is two lines, name and size over status and speed. The header keeps only the box that
     selects all and the name. */
  @media (pointer: coarse) { .list-row, .list-grid { --check-column: 44px; } }
  @media (max-width: 767px) {
    .list-grid { grid-template-columns: var(--check-column, 28px) 1fr; }
    .list-grid > :nth-child(n + 3) { display: none; }
  }
  .list-body:focus-visible .list-row.is-cursor { outline: 2px solid var(--accent); outline-offset: -2px; }
  /* The server can't be reached: what the rows say is from before, so it is dimmed (U-WU-3). */
  .list-body.stale { opacity: 0.6; }
  .phone-eta { color: var(--muted); }
  @media (min-width: 768px) { .phone-eta, .phone-swarm { display: none; } }
  .torrent-chip {
    display: inline-flex;
    flex-shrink: 0;
    align-items: center;
    gap: 6px;
    border: 1px solid var(--divider);
    border-radius: 999px;
    padding: 4px 12px;
    font-size: 12.5px;
    white-space: nowrap;
    color: var(--text);
  }
  .torrent-chip span { color: var(--muted); }
  .torrent-chip[aria-current='page'] { border-color: var(--accent); background: var(--selected); font-weight: 600; }
  @media (pointer: coarse) { .torrent-chip { min-height: 36px; } }
  .list-row.only-extraction :is(.cell-queue, .cell-size, .cell-status, .cell-eta, .cell-rate) { display: none !important; }
  /* A touch screen: the box column, the expand arrow and the row's menu are a finger wide (U-WU-8). */
  @media (pointer: coarse) {
    .list-row .cell-check, .list-grid > :first-child { min-width: 44px; }
    .row-expand { min-width: 36px; min-height: 36px; display: inline-flex; align-items: center; justify-content: center; }
  }
  .list-row {
    grid-template-columns: var(--check-column, 28px) 1fr auto;
    grid-template-areas: 'check name size' 'check status rate' 'check desc desc';
    padding-top: 6px;
    padding-bottom: 6px;
    row-gap: 2px;
    cursor: default;
    scroll-margin-top: 30px;
    /* A double-click opens the download; it shouldn't also select the words. */
    user-select: none;
    -webkit-user-select: none;
    -webkit-touch-callout: none;
  }
  .list-row .cell-check { grid-area: check; }
  .list-row .cell-name { grid-area: name; }
  .list-row .cell-status { grid-area: status; }
  .list-row .cell-rate { grid-area: rate; font-size: 11.5px; color: var(--muted); }
  .list-row .cell-size { grid-area: size; font-size: 11.5px; color: var(--muted); text-align: right; }
  .list-row .cell-desc { grid-area: desc; font-size: 11.5px; }
  .list-row .cell-eta, .list-row .cell-extract, .list-row .cell-added, .list-row .cell-queue, .list-row .cell-swarm { display: none; }
  .list-row .cell-desc:empty { display: none; }

  /* The menu button: always there on a touch screen, on hover or selection with a mouse. */
  .row-menu { display: inline-flex; border-radius: 4px; padding: 2px; color: var(--muted); opacity: 0; }
  .row-menu:hover { background: var(--hover); color: var(--text); }
  .list-row:hover .row-menu, .list-row.is-cursor .row-menu, .row-menu:focus-visible { opacity: 1; }
  @media (pointer: coarse) { .row-menu { opacity: 1; min-width: 40px; min-height: 40px; justify-content: center; align-items: center; color: var(--text); } }

  @media (min-width: 768px) {
    .list-row {
      grid-template-areas: none;
      grid-template-columns: var(--cols-md);
      min-height: 30px;
      padding-top: 2px;
      padding-bottom: 2px;
    }
    .list-row > * { grid-area: auto !important; }
    .list-row .cell-size, .list-row .cell-eta, .list-row .cell-extract, .list-row .cell-queue { display: block; }
    .list-row .cell-rate, .list-row .cell-size { font-size: inherit; color: inherit; }
    .list-row .cell-desc { display: none; }
  }

  @media (min-width: 768px) {
    .list-grid.only-extraction, .list-row.only-extraction { grid-template-columns: 28px minmax(180px, 1fr) 220px; }
  }
  @media (min-width: 1024px) {
    .list-grid, .list-row { grid-template-columns: var(--cols-lg); }
    .list-grid.only-extraction, .list-row.only-extraction { grid-template-columns: 28px minmax(200px, 2fr) 260px 150px minmax(160px, 1.5fr); }
    .list-row .cell-desc, .list-row .cell-added, .list-row .cell-swarm { display: block; font-size: inherit; }
  }
  /* UI.R42: a column hidden from the header's menu. A phone keeps its own lines whatever is chosen. */
  @media (min-width: 768px) {
    .list-grid > :global(.col-off), .list-row > .col-off { display: none !important; }
  }
  /* T7: beside Torrent Info the list keeps the columns it has between 768 and 1023 px, so it never scrolls sideways;
     the pane shows the swarm, the ratio and the rest for the selected torrent. */
  @media (min-width: 1440px) {
    .list-grid.with-info, .list-row.with-info { grid-template-columns: var(--cols-md); }
    .list-grid.with-info.only-extraction, .list-row.with-info.only-extraction { grid-template-columns: 28px minmax(180px, 1fr) 220px; }
    .list-grid.with-info :global(.wide-col), .list-row.with-info .cell-desc, .list-row.with-info .cell-added, .list-row.with-info .cell-swarm { display: none; }
  }
</style>
