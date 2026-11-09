// What the download list shows and does, as plain functions of the Host's data. Each one follows the desktop window's
// rule of the same name (JobViewModel, DownloadGroupSummary, PackageRules, SelectionCommands, ListSort, DurationText),
// so the two lists read alike. Nothing here touches the page, so the tests call it directly.

import { archiveRowView, canRetry, fullText, packageArchiveView } from './archives';
import { bytes, percent, speed as speedText, stateWord, timeSpanSeconds, tone as toneOf, type Tone } from './format';
import { actionsOf, canBrowse, isRow, jobRow, providerNameOf, sameProvider, showsWaiting } from './remote';
import type { ArchiveSet, CategoryDefinition, DownloadGroup, Job, RemoteAccount, RemoteJob } from './types';

// ---- Words for a download's cells ----

const removedWords = 'Archive removed after extraction';

/** A local file a provider supplies. Its address is a reference, not something to edit or send again. */
export function isProviderFile(job: Job): boolean {
  return job.isRemote === true || job.url.toLowerCase().startsWith('remote://');
}

/** What a running download waiting for a free connection to its host says. The host name only. */
export function waitingText(job: Job): string | null {
  return ['connecting', 'downloading', 'retryWaiting'].includes(job.state) && job.waitingForHost?.trim()
    ? `Waiting for a connection to ${job.waitingForHost}`
    : null;
}

/** The Host's words about a download beyond its state; null when it has nothing to say. */
export function jobNote(job: Job): string | null {
  if (waitingText(job) !== null) return job.fileRemovedAfterExtraction ? removedWords : null;
  if (job.statusNote?.trim()) return hideAddresses(job.statusNote, job);
  return job.fileRemovedAfterExtraction ? removedWords : null;
}

/** The note goes first, because the Description column is often cut short. */
export function describe(note: string | null | undefined, own: string | null | undefined): string {
  if (!note || !note.trim()) return own ?? '';
  return !own || !own.trim() ? note : `${note} · ${own}`;
}

/** The status column's words. */
export function statusWords(job: Job): string {
  if (job.queueRetryAt) return 'Queue retry scheduled';
  const waiting = waitingText(job);
  if (waiting) return waiting;
  if (isProviderFile(job) && job.state === 'downloading') return 'Downloading locally';
  return stateWord(job.state);
}

/** The Time left column: "1h 5m", "3m 20s", "45s"; blank when there is no estimate. */
export function timeLeft(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds <= 0) return '';
  const total = Math.floor(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = total % 60;
  if (hours >= 1) return `${hours}h ${minutes}m`;
  return minutes >= 1 ? `${minutes}m ${rest}s` : `${Math.max(1, rest)}s`;
}

const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** The Last Try Date column, "Nov 09 14:03:22 2026" in the viewer's time zone. */
export function lastTryText(value: string | number | Date | null | undefined): string {
  if (value === null || value === undefined || value === '') return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime()) || date.getFullYear() < 2000) return '';
  const two = (n: number) => String(n).padStart(2, '0');
  return `${months[date.getMonth()]} ${two(date.getDate())} ${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())} ${date.getFullYear()}`;
}

/** The queue's number as the Q column shows it; empty when the download is not in a queue. */
export function queueText(job: Pick<Job, 'isQueued' | 'queueId'>): string {
  return job.isQueued === false ? '' : String(job.queueId + 1);
}

export function queueName(job: Pick<Job, 'queueId' | 'queueName'>): string {
  return job.queueName ?? (job.queueId === 0 ? 'Main' : `Queue ${job.queueId}`);
}

// ---- Packages: DownloadGroupSummary and PackageRules ----

export const mixedQueueMarker = '*';

export type GroupState = 'empty' | 'active' | 'failed' | 'paused' | 'queued' | 'cancelled' | 'completed';

export interface GroupSummary {
  memberCount: number;
  completedCount: number;
  receivedBytes: number;
  totalBytes: number | null;
  bytesPerSecond: number;
  /** A fraction from 0 to 1; null when any file has an unknown size. */
  progress: number | null;
  state: GroupState;
  /** Seconds. */
  estimatedRemaining: number | null;
}

const activeStates = ['downloading', 'connecting', 'verifying', 'retryWaiting'];

/** The same arithmetic as the desktop's DownloadGroupSummary.Create. */
export function summarizeGroup(jobs: readonly Job[]): GroupSummary {
  if (jobs.length === 0) {
    return { memberCount: 0, completedCount: 0, receivedBytes: 0, totalBytes: null, bytesPerSecond: 0, progress: null, state: 'empty', estimatedRemaining: null };
  }

  let received = 0;
  let total = 0;
  let allKnown = true;
  let rate = 0;
  let completed = 0;
  let hasActive = false;
  let hasFailed = false;
  let hasPaused = false;
  let hasQueued = false;
  let hasCancelled = false;

  for (const job of jobs) {
    received += Math.max(0, job.receivedBytes ?? 0);
    if (typeof job.totalBytes === 'number' && job.totalBytes >= 0) total += job.totalBytes;
    else allKnown = false;

    const state = (job.state ?? '').trim().toLowerCase();
    if (activeStates.some((s) => s.toLowerCase() === state)) {
      hasActive = true;
      const r = job.bytesPerSecond;
      if (typeof r === 'number' && Number.isFinite(r) && r > 0) rate += r;
    } else if (state === 'failed') hasFailed = true;
    else if (state === 'paused') hasPaused = true;
    else if (state === 'queued') hasQueued = true;
    else if (state === 'cancelled') hasCancelled = true;
    else if (state === 'completed') completed++;
    else hasQueued = true;
  }

  const allCompleted = completed === jobs.length;
  const state: GroupState = hasActive ? 'active' : hasFailed ? 'failed' : hasPaused ? 'paused' : hasQueued ? 'queued' : hasCancelled ? 'cancelled' : allCompleted ? 'completed' : 'cancelled';
  const knownTotal = allKnown ? total : null;
  const progress = knownTotal === null ? null : knownTotal === 0 ? (allCompleted ? 1 : 0) : Math.min(1, received / knownTotal);

  let remaining: number | null = null;
  if (knownTotal !== null && rate > 0) remaining = Math.max(0, knownTotal - Math.min(received, knownTotal)) / rate;

  return { memberCount: jobs.length, completedCount: completed, receivedBytes: received, totalBytes: knownTotal, bytesPerSecond: rate, progress, state, estimatedRemaining: remaining };
}

/** Pause touches the files that can pause. */
export function pauseTargets(children: readonly Job[]): Job[] {
  return children.filter((child) => child.canPause);
}

/**
 * Resume restarts paused and failed files but leaves cancelled ones cancelled, because cancelling a file was a decision
 * about that file. The one exception undoes a package Cancel: when that Cancel stopped every file in the package, Resume
 * brings them all back.
 */
export function resumeTargets(packageId: string, children: readonly Job[]): Job[] {
  const same = (a?: string, b?: string) => !!a && !!b && a.toLowerCase() === b.toLowerCase();
  const undoCancel = children.length > 0 && children.every((child) => child.state === 'cancelled' && same(child.cancelledByPackageId, packageId));
  return children.filter((child) => child.canResume && (undoCancel || child.state !== 'cancelled'));
}

/** Finished and already cancelled files are left as they are. */
export function cancelTargets(children: readonly Job[]): Job[] {
  return children.filter((child) => !child.isTerminal && child.state !== 'cancelled');
}

/** A finished file has nothing left to schedule. */
export function priorityTargets(children: readonly Job[]): Job[] {
  return children.filter((child) => child.state !== 'completed');
}

function queueKey(job: Job): number {
  return job.isQueued === false ? -1 : job.queueId;
}

/** The queue every file is in; null when they differ or none is in a queue. */
export function sharedQueueId(children: readonly Job[]): number | null {
  const queues = new Set(children.map(queueKey));
  if (queues.size !== 1) return null;
  const only = [...queues][0];
  return only >= 0 ? only : null;
}

/** The Q column of a package: the shared queue's number, a marker when mixed, blank when none. */
export function packageQueueText(children: readonly Job[]): string {
  const queues = new Set(children.map(queueKey));
  if (queues.size === 0) return '';
  if (queues.size === 1) {
    const only = [...queues][0];
    return only >= 0 ? String(only + 1) : '';
  }
  return mixedQueueMarker;
}

/** The suggestion for a new package's name: the first file's name without its extension or part number. */
export function suggestPackageName(fileName: string): string {
  return fileName.replace(/(\.part\d+\.rar|\.r\d{2,3}|\.z\d{2}|\.\d{3})$|\.[^.]+$/i, '');
}

// ---- What each file menu command has to say for itself (FileMenuRules) ----

export interface FileMenuPlan {
  fileReason: string | null;
  localReason: string | null;
  removeReason: string | null;
  openReason: string | null;
}

export const fileRemovedReason = 'archive removed after extraction';

export function planFileMenu(singleFile: boolean, packageRow: boolean, sideRows: number, removableSideRows: number, fileRemoved = false): FileMenuPlan {
  const local = singleFile || packageRow;
  const noLocalRow = local ? null : sideRows > 0 ? 'no local file selected' : 'select a download first';
  const fileReason = singleFile ? null : packageRow ? 'select a file, not a package' : noLocalRow;
  return {
    fileReason,
    localReason: noLocalRow,
    removeReason: local || removableSideRows > 0 ? null : sideRows > 0 ? 'cancel the extraction first' : 'select a download first',
    openReason: fileReason ?? (fileRemoved ? fileRemovedReason : null),
  };
}

export function menuLabel(text: string, reason: string | null): string {
  return reason === null ? text : `${text} (${reason})`;
}

/** Grouping takes two files or more, in two rows or more. */
export function canGroup(files: number, localRows: number): boolean {
  return files >= 2 && localRows >= 2;
}

// ---- Deleting ----

export interface DeletePlan {
  title: string;
  /** The question and anything the Host will do on its own. */
  text: string;
  /** Whether to offer "Delete from disk". */
  offerFiles: boolean;
  /** What to send as deleteFile when the box isn't offered. */
  deleteFile: boolean;
}

export function nothingToDelete(count: number): string {
  return count === 1
    ? 'Its archive was removed after extraction, so no file is deleted.'
    : 'Their archives were removed after extraction, so no files are deleted.';
}

/** The Delete dialog: its title and words, and whether a finished file is there to delete. */
export function deletePlan(jobs: readonly Job[]): DeletePlan {
  const anyCompleted = jobs.some((job) => job.state === 'completed' && !job.fileRemovedAfterExtraction);
  const noFilesLeft = jobs.length > 0 && jobs.every((job) => job.fileRemovedAfterExtraction);
  const question = jobs.length === 1 ? `Remove '${jobs[0].fileName}' from the list?` : `Remove ${jobs.length} downloads from the list?`;
  return {
    title: jobs.length === 1 ? 'Delete download?' : `Delete ${jobs.length} downloads?`,
    text: noFilesLeft ? `${question} ${nothingToDelete(jobs.length)}` : anyCompleted ? question : `${question} Partial data is always removed.`,
    offerFiles: anyCompleted,
    deleteFile: !anyCompleted && !noFilesLeft,
  };
}

export function deleteCompletedText(count: number): string {
  return `Remove ${count} completed item(s) from the list? The downloaded files will stay on disk.`;
}

// ---- Categories ----

const builtIn: Record<string, string> = {};
for (const [category, extensions] of Object.entries({
  compressed: 'zip 7z rar gz gzip bz2 tar xz zst iso cab ace arj lzh',
  documents: 'pdf doc docx xls xlsx ppt pptx txt rtf epub odt ods odp csv',
  music: 'mp3 flac wav m4a aac ogg wma opus aif ra',
  programs: 'exe msi apk msix appx bat cmd deb rpm dmg pkg',
  video: 'mp4 mkv avi mov wmv webm m4v mpg mpeg flv ts 3gp rm rmvb',
})) for (const extension of extensions.split(' ')) builtIn[extension] = category;

/** FileCategories.For: the built-in category a file name belongs to. */
export function categoryForName(fileName: string | null | undefined): string {
  const dot = (fileName ?? '').lastIndexOf('.');
  if (dot < 0) return 'other';
  return builtIn[fileName!.slice(dot + 1).toLowerCase()] ?? 'other';
}

/** The category a download is listed under: the Host's choice, else by its file name and the custom categories. */
export function categoryOf(job: Pick<Job, 'categoryId' | 'fileName'>, custom: readonly CategoryDefinition[] = []): string {
  if (job.categoryId) return job.categoryId;
  const dot = job.fileName.lastIndexOf('.');
  const extension = dot >= 0 ? job.fileName.slice(dot + 1).toLowerCase() : '';
  const own = extension ? custom.find((category) => category.extensions.some((e) => e.replace(/^\./, '').toLowerCase() === extension)) : undefined;
  return own?.id ?? categoryForName(job.fileName);
}

export const builtInCategories: { id: string; label: string }[] = [
  { id: 'compressed', label: 'Compressed' },
  { id: 'documents', label: 'Documents' },
  { id: 'music', label: 'Music' },
  { id: 'programs', label: 'Programs' },
  { id: 'video', label: 'Video' },
];

// ---- Sorting: ListSort ----

export type SortKey = 'name' | 'queue' | 'size' | 'status' | 'extraction' | 'eta' | 'rate' | 'lastTry' | 'description' | 'added';

export interface SortState {
  key: SortKey;
  ascending: boolean;
}

export const defaultSort: SortState = { key: 'added', ascending: false };

export const sortKeys: SortKey[] = ['name', 'queue', 'size', 'status', 'extraction', 'eta', 'rate', 'lastTry', 'description', 'added'];

/** Words and times read from the first down. Sizes, speeds and dates read from the biggest or newest. */
export function startsAscending(key: SortKey): boolean {
  return key === 'name' || key === 'queue' || key === 'status' || key === 'extraction' || key === 'eta' || key === 'description';
}

/** A click on another column starts in its usual direction, and a second click reverses it. */
export function nextSort(current: SortState, clicked: SortKey): SortState {
  return current.key === clicked ? { key: clicked, ascending: !current.ascending } : { key: clicked, ascending: startsAscending(clicked) };
}

export function parseSort(text: string | null | undefined): SortState {
  try {
    const value = JSON.parse(text ?? '') as Partial<SortState> | null;
    if (value && sortKeys.includes(value.key as SortKey) && typeof value.ascending === 'boolean') return { key: value.key as SortKey, ascending: value.ascending };
  } catch {
    // Nothing saved, or something else wrote there: the default.
  }
  return defaultSort;
}

export function naturalCompare(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
}

/** Rows with nothing to compare stay at the end in either direction, and equal rows keep their order. */
export function orderBy<T>(rows: readonly T[], key: (row: T) => number | string, ascending: boolean, isBlank?: (row: T) => boolean): T[] {
  const sign = ascending ? 1 : -1;
  return rows
    .map((row, index) => ({ row, index, blank: isBlank?.(row) === true }))
    .sort((a, b) => {
      if (a.blank !== b.blank) return a.blank ? 1 : -1;
      const x = key(a.row);
      const y = key(b.row);
      const c = typeof x === 'string' && typeof y === 'string' ? naturalCompare(x, y) : (x as number) - (y as number);
      return c !== 0 ? c * sign : a.index - b.index;
    })
    .map((entry) => entry.row);
}

/** The values a row is sorted by, and what it shows. */
export interface RowView {
  name: string;
  /** The Q column. */
  queue: string;
  queueTip: string;
  /** Bytes; 0 when unknown. */
  size: number;
  sizeText: string;
  /** The words in the Status column. */
  status: string;
  statusTone: Tone;
  statusTip: string;
  /** 0 to 100 when the bar shows under the status. */
  bar: number | null;
  /** Seconds; null when there is no estimate. */
  eta: number | null;
  etaText: string;
  rate: number;
  rateText: string;
  lastTry: number;
  lastTryText: string;
  description: string;
  extraction: string;
  extractionTone: Tone;
  extractionTip: string;
  created: number;
  /** What the row says beyond its name: the failure or the address. Searched, and shown as a tooltip. */
  detail: string;
}

export function sortRows<T>(rows: readonly T[], view: (row: T) => RowView, sort: SortState): T[] {
  const a = sort.ascending;
  switch (sort.key) {
    case 'name': return orderBy(rows, (r) => view(r).name, a);
    case 'queue': return orderBy(rows, (r) => view(r).queue, a, (r) => view(r).queue.length === 0);
    case 'size': return orderBy(rows, (r) => view(r).size, a, (r) => view(r).size <= 0);
    case 'status': return orderBy(rows, (r) => view(r).status, a);
    case 'extraction': return orderBy(rows, (r) => view(r).extraction, a, (r) => view(r).extraction.length === 0);
    case 'eta': return orderBy(rows, (r) => view(r).eta ?? 0, a, (r) => view(r).eta === null);
    case 'rate': return orderBy(rows, (r) => view(r).rate, a, (r) => view(r).rate <= 0);
    case 'lastTry': return orderBy(rows, (r) => view(r).lastTry, a);
    case 'description': return orderBy(rows, (r) => view(r).description, a, (r) => view(r).description.length === 0);
    default: return orderBy(rows, (r) => view(r).created, a);
  }
}

// ---- The cells of each kind of row ----

const blankTone: Tone = 'neutral';

function ms(value: string | undefined): number {
  const t = value ? Date.parse(value) : NaN;
  return Number.isNaN(t) ? 0 : t;
}

/** Web addresses in a text, for a download whose own address stays hidden: the site only, as the desktop's UrlRedactor does. */
export function hideAddresses(text: string, job: Job): string {
  const hidden = job.addressHidden === true || isProviderFile(job);
  if (!hidden) return text;
  return text.replace(/[a-z][a-z0-9+.-]*:\/\/[^\s"'<>)]+/gi, (url) => (url.toLowerCase().startsWith('remote:') ? 'a provider link' : siteOnly(url) || 'a direct link'));
}

function shownAddress(job: Job): string {
  if (isProviderFile(job)) return '';
  return job.addressHidden ? (siteOnly(job.url) || 'A direct link') : job.url;
}

function siteOnly(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return '';
  }
}

/** A download's cells, as JobViewModel.Update shows them. */
export function jobView(job: Job, set?: ArchiveSet): RowView {
  const total = job.totalBytes && job.totalBytes > 0 ? job.totalBytes : job.receivedBytes;
  const rate = job.bytesPerSecond ?? 0;
  const eta = timeSpanSeconds(job.estimatedRemaining);
  const p = percent(job) ?? 0;
  const note = jobNote(job);
  const own = job.description ?? (job.pageUrl && (job.addressHidden || isProviderFile(job)) ? siteOnly(job.pageUrl) : job.pageUrl);
  const barStates = ['downloading', 'connecting', 'retryWaiting', 'verifying', 'paused'];
  const updated = job.completedAt ?? job.updatedAt;
  const archive = set ? archiveRowView(set) : null;
  return {
    name: job.fileName || (job.addressHidden || isProviderFile(job) ? '' : job.url),
    queue: queueText(job),
    queueTip: job.isQueued === false ? 'Not in a queue' : queueName(job),
    size: total > 0 ? total : 0,
    sizeText: total > 0 ? bytes(total) : '',
    status: statusWords(job),
    statusTone: toneOf(job.state),
    statusTip: job.failure?.message ?? note ?? '',
    bar: barStates.includes(job.state) && p > 0 ? p : null,
    eta,
    etaText: timeLeft(eta),
    rate,
    rateText: speedText(rate),
    lastTry: ms(updated),
    lastTryText: lastTryText(updated),
    description: describe(note, own),
    extraction: archive?.text ?? '',
    extractionTone: archive?.tone ?? blankTone,
    extractionTip: archive?.tooltip ?? '',
    created: ms(job.createdAt),
    detail: job.failure?.message ? job.failure.message : shownAddress(job),
  };
}

/** A package's cells, as JobViewModel.UpdatePackage shows them. */
export function packageView(group: DownloadGroup, members: readonly Job[], sets: ArchiveSet[] = []): RowView {
  const summary = summarizeGroup(members);
  const archive = packageArchiveView(sets);
  const state = summary.state === 'active' ? 'downloading' : summary.state;
  const word = state === 'downloading' && members.some(isProviderFile) ? 'Downloading locally' : stateWord(state);
  const total = summary.totalBytes ?? summary.receivedBytes;
  const queue = packageQueueText(members);
  const latest = members.length === 0 ? 0 : Math.max(...members.map((m) => ms(m.updatedAt)));
  const first = members.length === 0 ? 0 : Math.min(...members.map((m) => ms(m.createdAt)));
  const progress = summary.progress === null ? 0 : Math.max(0, Math.min(100, summary.progress * 100));
  return {
    name: group.name,
    queue,
    queueTip: queue.length === 0 ? 'None' : queue === mixedQueueMarker ? 'Files are in different queues' : (members[0]?.queueName ?? `Queue ${members[0]?.queueId}`),
    size: total > 0 ? total : 0,
    sizeText: total > 0 ? bytes(total) : '',
    status: `${word} · ${summary.completedCount}/${summary.memberCount}`,
    statusTone: state === 'downloading' ? 'active' : state === 'completed' ? 'done' : state === 'failed' ? 'failed' : state === 'paused' || state === 'queued' ? 'waiting' : 'neutral',
    statusTip: '',
    bar: (summary.state === 'active' || summary.state === 'paused') && progress > 0 ? progress : null,
    eta: summary.estimatedRemaining,
    etaText: timeLeft(summary.estimatedRemaining),
    rate: summary.bytesPerSecond,
    rateText: speedText(summary.bytesPerSecond),
    lastTry: latest,
    lastTryText: members.length === 0 ? '' : lastTryText(latest),
    description: `${summary.memberCount} files`,
    extraction: archive?.text ?? '',
    extractionTone: archive?.tone ?? blankTone,
    extractionTip: archive?.tooltip ?? '',
    created: first,
    detail: `${summary.memberCount} files`,
  };
}

/** A provider's job on the list (JobViewModel.UpdateRemote): its stage in words, never a local figure. */
export function remoteView(remote: RemoteJob, now: Date = new Date()): RowView {
  const row = jobRow(remote, now);
  return {
    name: row.name,
    queue: '', queueTip: '', size: 0, sizeText: '',
    status: row.status,
    statusTone: row.tone,
    statusTip: row.tooltip,
    bar: null, eta: null, etaText: '', rate: 0, rateText: '',
    lastTry: ms(remote.updatedAt),
    lastTryText: lastTryText(remote.updatedAt),
    description: row.detail,
    extraction: '', extractionTone: blankTone, extractionTip: '',
    created: ms(remote.createdAt),
    detail: row.detail,
  };
}

/** An extraction record whose files are not on the list (JobViewModel.UpdateArchiveRow). The status stays blank: the Extraction column says the rest. */
export function archiveView(set: ArchiveSet): RowView {
  const archive = archiveRowView(set);
  return {
    name: set.name,
    queue: '', queueTip: '', size: 0, sizeText: '',
    status: '', statusTone: blankTone, statusTip: '',
    bar: null, eta: null, etaText: '', rate: 0, rateText: '',
    lastTry: ms(set.updatedAt),
    lastTryText: lastTryText(set.updatedAt),
    description: fullText(set),
    extraction: archive.text,
    extractionTone: archive.tone,
    extractionTip: archive.tooltip,
    created: ms(set.createdAt ?? set.updatedAt),
    detail: archive.tooltip,
  };
}

/**
 * The words searched for a row: the desktop's MatchesSearch plus the address and details, and the status tooltip, which
 * for a provider's row holds the Host's own longer words for its state.
 */
export function searchText(view: RowView, job?: Job): string {
  const parts = [view.name, view.status, view.extraction, view.description, view.detail, view.statusTip];
  if (job && !job.addressHidden && !isProviderFile(job)) parts.push(job.url);
  return parts.join('\n').toLocaleLowerCase();
}

// ---- The status bar ----

export interface ListCounts {
  total: number;
  active: number;
  queued: number;
  retrying: number;
}

export function countJobs(jobs: readonly Job[]): ListCounts {
  let active = 0;
  let queued = 0;
  let retrying = 0;
  for (const job of jobs) {
    if (job.state === 'connecting' || job.state === 'downloading' || job.state === 'verifying') active++;
    else if (job.state === 'retryWaiting') retrying++;
    else if (job.state === 'queued') queued++;
  }
  return { total: jobs.length, active, queued, retrying };
}

export function statusBarText(counts: ListCounts, selected: number): string {
  let text = `${counts.total} downloads · ${counts.active} active · ${counts.queued} queued`;
  if (counts.retrying > 0) text += ` · ${counts.retrying} retrying`;
  if (selected > 1) text += `  (${selected} selected)`;
  return text;
}

// ---- Empty list ----

export const defaultEmptyText = 'No downloads in this view.\nUse Add URL or the browser extension to start one.';

export function emptyText(view: string, providerName: string): string {
  if (view === 'remote') return `Nothing is waiting for ${providerName}.\nItems show here until their files start downloading.`;
  if (view === 'extractions') return 'No archives yet.';
  return defaultEmptyText;
}

// ---- Keys: which row the arrows land on ----

/** Where the cursor goes for a navigation key; null when the key is not one. */
export function moveCursor(key: string, from: number, count: number, page = 10): number | null {
  if (count === 0) return null;
  const last = count - 1;
  const at = from < 0 ? -1 : from;
  switch (key) {
    case 'ArrowDown': return Math.min(last, at + 1);
    case 'ArrowUp': return Math.max(0, at < 0 ? 0 : at - 1);
    case 'Home': return 0;
    case 'End': return last;
    case 'PageDown': return Math.min(last, Math.max(0, at) + page);
    case 'PageUp': return Math.max(0, Math.max(0, at) - page);
    default: return null;
  }
}

/** A key typed in a field or a button belongs to it, not to the list. */
export function isTypingTarget(target: EventTarget | null): boolean {
  const element = target as { tagName?: string; isContentEditable?: boolean } | null;
  const tag = element?.tagName?.toLowerCase();
  return tag === 'input' || tag === 'textarea' || tag === 'select' || element?.isContentEditable === true;
}

// ---- Selecting ----

export interface ClickKeys {
  shift: boolean;
  ctrl: boolean;
}

/**
 * The selection after a click: a plain click replaces it, Ctrl toggles the row, Shift replaces it with the range from the
 * anchor, and Ctrl+Shift adds that range.
 */
export function selectAfterClick(selected: ReadonlySet<string>, ids: readonly string[], id: string, anchor: string | null, keys: ClickKeys): Set<string> {
  if (keys.shift && anchor) {
    const from = ids.indexOf(anchor);
    const to = ids.indexOf(id);
    if (from >= 0 && to >= 0) {
      const next = keys.ctrl ? new Set(selected) : new Set<string>();
      for (let i = Math.min(from, to); i <= Math.max(from, to); i++) next.add(ids[i]);
      return next;
    }
  }
  if (keys.ctrl) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    return next;
  }
  return new Set([id]);
}

// ---- Provider rows ----

export type RemoteAction = 'none' | 'selectFiles' | 'approve' | 'enterLinkPassword' | 'retry' | 'checkNow';

/** RemoteRowActions.Primary: one row's next step. */
function primary(job: RemoteJob): RemoteAction {
  const actions = actionsOf(job);
  return actions.selectFiles ? 'selectFiles' : actions.approve ? 'approve' : actions.enterLinkPassword ? 'enterLinkPassword' : actions.retry ? 'retry' : actions.checkNow ? 'checkNow' : 'none';
}

/** What Enter does with these rows (RemoteRules.EnterFor): one row's next step, or for several the one thing they all wait for. */
export function remoteEnterAction(jobs: readonly RemoteJob[]): RemoteAction {
  if (jobs.length === 0) return 'none';
  if (jobs.length === 1) return primary(jobs[0]);
  if (jobs.every((job) => actionsOf(job).approve)) return 'approve';
  if (jobs.every((job) => actionsOf(job).selectFiles)) return 'selectFiles';
  return 'none';
}

/** "Download All Files" is for several rows at once; one row's own file dialog does the same for it. */
export function offersDownloadAll(jobs: readonly RemoteJob[]): boolean {
  return jobs.filter((job) => job.state === 'awaitingSelection').length >= 2;
}

/** An extraction can be taken off the list unless it is still running. */
export function archiveRemovable(set: ArchiveSet): boolean {
  return set.state !== 'extracting';
}

export function archiveRetryText(sets: readonly ArchiveSet[]): string {
  const retry = sets.filter(canRetry);
  return retry.length > 0 && retry.every((set) => set.state === 'completed') ? 'Extract Again…' : 'Retry Extraction';
}

/** The password item says what the plan's target is waiting for. */
export function archivePasswordText(needed: boolean): string {
  return needed ? 'Enter Archive Password…' : 'Archive Password…';
}

/** What a double-click or Enter does on a record's own row (ArchiveRules.Primary). The web can't open a folder, so it copies its path. */
export function archivePrimary(set: ArchiveSet): 'password' | 'copyFolder' | 'retry' | 'none' {
  if (set.needsPassword) return 'password';
  if (set.state === 'completed' && set.destination?.trim()) return 'copyFolder';
  return canRetry(set) ? 'retry' : 'none';
}

/** A set is on the list as a row of its own when none of its files are there. */
export function archiveHasOwnRow(set: ArchiveSet, jobs: Record<string, Job>, snapshotsSeen: boolean): boolean {
  return snapshotsSeen && !set.jobIds.some((id) => id in jobs);
}

// ---- Limits for the quick menus ----

export const downloadChoices = [1, 2, 3, 4, 5, 6, 8, 10, 12, 16];
export const connectionChoices = [1, 2, 4, 6, 8, 12, 16, 24, 32];
export const speedChoices = [1, 2, 5, 10, 20, 30, 50, 70, 100, 200, 500];
export const megabyte = 1024 * 1024;

export function speedIsPreset(bytesPerSecond: number): boolean {
  return bytesPerSecond === 0 || speedChoices.some((mb) => mb * megabyte === bytesPerSecond);
}

export function speedLimitLabel(bytesPerSecond: number): string {
  if (bytesPerSecond <= 0) return 'None';
  const mb = bytesPerSecond / megabyte;
  return `${Number.isInteger(mb) ? mb : mb.toFixed(1)} MB/s`;
}

// ---- The speed graph ----

/** The speed history: one sample a second, with gaps (null) while the server is not reachable. */
export class SpeedHistory {
  samples: (number | null)[] = [];
  constructor(readonly limit = 600) {}

  add(rate: number | null): void {
    this.samples.push(rate === null ? null : Math.max(0, rate));
    if (this.samples.length > this.limit) this.samples.splice(0, this.samples.length - this.limit);
  }

  /**
   * The server's own figures (speed.history, one a second, the last the second under way), put before what this page
   * sampled since it opened. As many of the server's newest are left out as were sampled here, so no second shows twice.
   */
  seed(serverSamples: readonly number[]): void {
    const finished = serverSamples.slice(0, -1);
    const older = finished.slice(0, Math.max(0, finished.length - this.samples.length));
    this.samples = [...older.map((rate) => (Number.isFinite(rate) ? Math.max(0, rate) : 0)), ...this.samples];
    if (this.samples.length > this.limit) this.samples.splice(0, this.samples.length - this.limit);
  }

  peak(): number {
    let peak = 0;
    for (const sample of this.samples) if (sample !== null && sample > peak) peak = sample;
    return peak;
  }
}

/**
 * The rate of one tick: the active downloads (retrying ones included) updated in the last ten seconds. A download that has
 * gone quiet counts as 0, not unknown; a gap (null) means there is no data.
 */
export function speedSample(jobs: readonly Job[], connected: boolean, ready: boolean, now: number): number | null {
  if (!connected || !ready) return null;
  let sum = 0;
  for (const job of jobs) {
    if (!activeStates.includes(job.state)) continue;
    if (now - ms(job.updatedAt) > 10_000) continue;
    const r = job.bytesPerSecond ?? 0;
    if (Number.isFinite(r) && r > 0) sum += r;
  }
  return sum;
}

/** The scale the graph's top line is drawn at: 64 KB/s doubled until it clears the peak. */
export function graphCeiling(peak: number): number {
  let ceiling = 64 * 1024;
  while (ceiling < peak * 1.1) ceiling *= 2;
  return ceiling;
}

// ---- The sidebar's provider entries (SyncRemoteEntryPoints) ----

export interface ProviderEntry {
  provider: string;
  name: string;
  waiting: number;
  showWaiting: boolean;
  showBrowse: boolean;
}

/** "Waiting for X" shows with a usable key or while rows are left; "My X" only when the account can browse its catalog. */
export function providerEntries(accounts: readonly RemoteAccount[], remoteJobs: readonly RemoteJob[]): ProviderEntry[] {
  return accounts.map((account) => {
    const waiting = remoteJobs.filter((job) => isRow(job) && sameProvider(job.provider, account.provider)).length;
    return {
      provider: account.provider,
      name: providerNameOf(account),
      waiting,
      showWaiting: showsWaiting(account, remoteJobs),
      showBrowse: canBrowse(account),
    };
  });
}
