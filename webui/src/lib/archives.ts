// The words and rules for archive sets, ported from the desktop (Remote/ArchiveRows.cs). The Host's own stateText
// is the full version of each short label. Nothing here touches the DOM.

import type { Tone } from './format';
import { hideAddresses, isFullPath } from './remote';
import type { ArchiveSet, Job } from './types';
import type { Row } from './views';

export interface ArchiveRowView {
  text: string;
  tooltip: string;
  tone: Tone;
  needsPassword: boolean;
}

const mostNamesInText = 5;

/** The Host puts its label in front of a reason that can start with the same label. Once is enough. */
export function withoutRepeatedLabel(text: string): string {
  const colon = text.indexOf(': ');
  if (colon <= 0) return text;
  const label = text.slice(0, colon + 2);
  const rest = text.slice(colon + 2);
  return rest.toLowerCase().startsWith(label.toLowerCase()) ? rest : text;
}

export function fullText(set: ArchiveSet): string {
  return withoutRepeatedLabel(hideAddresses(set.stateText));
}

/** That sentence and what the Host notes about the set, such as files kept because the tool didn't read them. */
export function fullWithNote(set: ArchiveSet): string {
  const full = fullText(set);
  const note = hideAddresses(set.note).trim();
  if (!note) return full;
  if (!full) return note;
  return full.endsWith('.') ? `${full} ${note}` : `${full}. ${note}`;
}

export function shortVolume(name: string): string {
  const part = /\.(part\d+)\.rar$/i.exec(name);
  if (part) return part[1];
  const old = /(\.[r-z]\d{2})$/i.exec(name);
  return old ? old[1] : name;
}

function parts(names: string[]): string {
  switch (names.length) {
    case 0: return '';
    case 1: return shortVolume(names[0]);
    case 2: return `${shortVolume(names[0])}, ${shortVolume(names[1])}`;
    default: return `${names.length} parts`;
  }
}

const includes = (list: string[] | undefined, name: string) => (list ?? []).some((item) => item.toLowerCase() === name.toLowerCase());

/** What the unfinished parts are doing: "downloading" while any runs; otherwise each kind there is, joined with "or". */
function unfinishedWord(set: ArchiveSet): string {
  const paused = set.downloadingVolumes.filter((name) => includes(set.pausedVolumes, name));
  const held = set.downloadingVolumes.filter((name) => !includes(set.pausedVolumes, name) && includes(set.heldVolumes, name));
  const queued = set.downloadingVolumes.filter((name) => !includes(set.pausedVolumes, name) && !includes(set.heldVolumes, name) && includes(set.queuedVolumes, name));
  if (paused.length + held.length + queued.length < set.downloadingVolumes.length) return 'downloading';
  const words: string[] = [];
  if (paused.length) words.push('paused');
  if (held.length) words.push('not started');
  if (queued.length) words.push('queued');
  return words.join(' or ');
}

/** A set whose last volume wasn't chosen stops rather than waits; it goes on by itself once that part arrives. */
export function isWaitingForNamedParts(set: ArchiveSet): boolean {
  return set.state === 'failed' && set.failureKind === 'missingVolume' && set.missingVolumes.length > 0;
}

/** The short label for a set, with its tone. Blank while volumes are still coming and nobody asked for extraction. */
export function archiveLabel(set: ArchiveSet): { text: string; tone: Tone } {
  switch (set.state) {
    case 'downloading':
      if (set.downloadingVolumes.length > 0) return { text: '', tone: 'neutral' };
      // Review 5:L2: a one-volume archive (a single .zip) whose download failed: its row says Failed already.
      if (set.failedVolumes.length === 1 && set.volumes.length <= 1 && set.missingVolumes.length === 0) return { text: '', tone: 'neutral' };
      return set.failedVolumes.length > 0
        ? { text: `${parts(set.failedVolumes)} didn't download`, tone: 'waiting' }
        : { text: 'Not downloaded yet', tone: 'neutral' };
    case 'downloaded': {
      const gaps = [...set.missingVolumes, ...set.failedVolumes];
      return gaps.length > 0 ? { text: `Downloaded, ${parts(gaps)} missing`, tone: 'waiting' } : { text: 'Downloaded', tone: 'neutral' };
    }
    case 'waiting':
      if (set.downloadingVolumes.length > 0) return { text: `Waiting: ${parts(set.downloadingVolumes)} ${unfinishedWord(set)}`, tone: 'waiting' };
      if (set.failedVolumes.length > 0) return { text: `Waiting: ${parts(set.failedVolumes)} didn't download`, tone: 'waiting' };
      if (set.missingVolumes.length > 0) return { text: `Waiting: ${parts(set.missingVolumes)} missing`, tone: 'waiting' };
      return { text: 'Waiting for extraction', tone: 'waiting' };
    case 'extracting': {
      const text = set.phase === 'verifying' ? 'Extracting: checking'
        : set.phase === 'finalizing' ? 'Extracting: moving files'
        : set.percent !== undefined && set.percent !== null ? `Extracting ${set.percent}%` : 'Extracting';
      return { text, tone: 'active' };
    }
    case 'completed':
      return { text: 'Extraction completed', tone: 'done' };
    case 'failed':
      if (isWaitingForNamedParts(set)) return { text: `Waiting: ${parts(set.missingVolumes)} missing`, tone: 'waiting' };
      switch (set.failureKind) {
        case 'passwordRequired': return { text: 'Password required', tone: 'waiting' };
        case 'wrongPassword': return { text: 'Wrong password', tone: 'failed' };
        case 'missingVolume': return { text: 'Missing volume', tone: 'failed' };
        case 'corrupted': return { text: 'Corrupted archive', tone: 'failed' };
        case 'unsupportedVersion': return { text: 'Unsupported archive version', tone: 'failed' };
        case 'toolUnavailable': return { text: '7-Zip missing', tone: 'failed' };
        case 'diskSpace': return { text: 'Not enough disk space', tone: 'failed' };
        case 'permission': return { text: 'Permission refused', tone: 'failed' };
        case 'unsafePath': return { text: 'Unsafe archive', tone: 'failed' };
        case 'cancelled': return { text: 'Extraction cancelled', tone: 'neutral' };
        default: return { text: 'Extraction failed', tone: 'failed' };
      }
    default:
      // A newer Host's state. Its own words are all anyone has for it.
      return { text: fullText(set), tone: 'neutral' };
  }
}

function tooltipOf(set: ArchiveSet): string {
  const lines = [fullText(set)];
  if (set.note?.trim()) lines.push(hideAddresses(set.note));
  if (set.needsPassword) lines.push('Enter the archive\'s password to try again.');
  else if (set.hasPassword && set.state !== 'completed') lines.push('A password is saved for this archive.');
  if (set.state === 'failed' && set.failureKind !== 'cancelled' && set.failureKind !== 'missingVolume') lines.push('The downloaded files are fine, and they are kept.');
  return lines.join('\n');
}

export function archiveRowView(set: ArchiveSet): ArchiveRowView {
  const { text, tone } = archiveLabel(set);
  return { text, tooltip: tooltipOf(set), tone, needsPassword: set.needsPassword };
}

function packageLabel(sets: ArchiveSet[]): { text: string; tone: Tone } {
  const asking = sets.filter((set) => set.needsPassword).length;
  if (asking > 0) return { text: asking === 1 ? '1 needs a password' : `${asking} need a password`, tone: 'waiting' };
  const running = sets.find((set) => set.state === 'extracting');
  if (running) return { text: archiveLabel(running).text, tone: 'active' };
  const failed = sets.filter((set) => set.state === 'failed' && set.failureKind !== 'cancelled' && !isWaitingForNamedParts(set)).length;
  if (failed > 0) return { text: failed === 1 ? '1 extraction problem' : `${failed} extraction problems`, tone: 'failed' };
  const done = sets.filter((set) => set.state === 'completed').length;
  if (done === sets.length) return { text: 'Extraction completed', tone: 'done' };
  if (sets.some((set) => set.state === 'waiting' || isWaitingForNamedParts(set))) return { text: 'Waiting for extraction', tone: 'waiting' };
  if (done > 0) return { text: `Extracted ${done} of ${sets.length}`, tone: 'neutral' };
  if (sets.every((set) => set.state === 'downloaded')) return { text: 'Downloaded', tone: 'neutral' };
  return { text: '', tone: 'neutral' };
}

/** A package: one set says what that set says, several say what most needs attention. */
export function packageArchiveView(sets: ArchiveSet[]): ArchiveRowView | null {
  if (sets.length === 0) return null;
  if (sets.length === 1) return archiveRowView(sets[0]);
  const { text, tone } = packageLabel(sets);
  const lines = sets.slice(0, mostNamesInText).map((set) => `${set.name}: ${fullWithNote(set)}`);
  if (sets.length > mostNamesInText) lines.push(`and ${sets.length - mostNamesInText} more`);
  return { text, tooltip: lines.join('\n'), tone, needsPassword: sets.some((set) => set.needsPassword) };
}

// ---- Rules -------------------------------------------------------------------------------------------

/** The Host decides for real; this is only for what the buttons offer, and it matches the Host's test of a name. */
export function looksLikeArchive(fileName: string | undefined | null): boolean {
  if (!fileName) return false;
  return /\.rar$/i.test(fileName) || /\.[r-z]\d{2}$/i.test(fileName)
    || /(?:\.7z|\.zip|\.tar|\.tar\.gz|\.tgz|\.tar\.bz2|\.tbz2?|\.tar\.xz|\.txz|\.tar\.zst|\.tzst|\.(?:7z|zip|rar|tar)\.\d{3})$/i.test(fileName);
}

/** A set nobody asked for yet, or one that stopped and could go again without anything new from the user. */
export function canAskFor(set: ArchiveSet): boolean {
  switch (set.state) {
    case 'downloading':
    case 'downloaded': return true;
    case 'failed': return !set.needsPassword || set.hasPassword;
    default: return false;
  }
}

export function canRetry(set: ArchiveSet): boolean {
  switch (set.state) {
    case 'failed': return !set.needsPassword || set.hasPassword;
    // Its archives are gone, so another run could only fail.
    case 'completed': return !set.archivesDeleted;
    default: return false;
  }
}

export interface ArchiveFile {
  jobId: string;
  fileName: string;
  filePath: string;
  set?: ArchiveSet;
}

export interface ArchivePasswordTarget {
  setId?: string;
  packageId?: string;
  filePath?: string;
  name: string;
  needed: boolean;
  wasWrong: boolean;
  hasPassword: boolean;
}

export interface ArchivePlan {
  extractReason: string | null;
  extractJobIds: string[];
  extractSets: ArchiveSet[];
  password: ArchivePasswordTarget | null;
  retry: ArchiveSet[];
  cancel: ArchiveSet[];
  remove: ArchiveSet[];
  canExtract: boolean;
  /** Retry already says it for the same sets; two buttons for one command read as a fault. */
  showExtract: boolean;
  extractText: string;
}

function whyNotAgain(sets: ArchiveSet[]): string {
  if (sets.some((set) => set.state === 'extracting')) return 'already extracting';
  if (sets.some((set) => set.state === 'waiting')) return 'already waiting to extract';
  if (sets.some((set) => set.needsPassword)) return 'enter the archive\'s password first';
  return 'already extracted';
}

export function targetOf(set: ArchiveSet): ArchivePasswordTarget {
  return { setId: set.id, name: set.name, needed: set.needsPassword, wasWrong: set.failureKind === 'wrongPassword', hasPassword: set.hasPassword };
}

/** A package's password also serves the archives it hasn't got yet. */
export function packageTarget(packageId: string, name: string, sets: ArchiveSet[]): ArchivePasswordTarget {
  const asking = sets.filter((set) => set.needsPassword);
  return {
    packageId,
    name,
    needed: asking.length > 0,
    wasWrong: asking.length > 0 && asking.every((set) => set.failureKind === 'wrongPassword'),
    hasPassword: sets.some((set) => set.hasPassword),
  };
}

/** What a notice asks a password for. When several sets of one package ask at once, one answer can serve them all. */
export function noticeTarget(set: ArchiveSet, sets: ArchiveSet[], packageName: string | undefined): ArchivePasswordTarget {
  if (!set.packageId || !packageName) return targetOf(set);
  const members = sets.filter((candidate) => candidate.packageId === set.packageId);
  return members.filter((candidate) => candidate.needsPassword).length > 1 ? packageTarget(set.packageId, packageName, members) : targetOf(set);
}

function passwordFor(archiveFiles: ArchiveFile[], sets: ArchiveSet[], packageAlone: { id: string; name: string } | null): ArchivePasswordTarget | null {
  if (packageAlone) return packageTarget(packageAlone.id, packageAlone.name, sets);
  // A run has its password already, and a finished set has no more use for one.
  if (sets.length === 1 && archiveFiles.every((file) => file.set?.id === sets[0].id)) {
    return sets[0].state === 'completed' || sets[0].state === 'extracting' ? null : targetOf(sets[0]);
  }
  // A download with no record yet can still get a password in advance, by its file's path.
  if (sets.length === 0 && archiveFiles.length === 1 && archiveFiles[0].filePath && isFullPath(archiveFiles[0].filePath)) {
    return { filePath: archiveFiles[0].filePath, name: archiveFiles[0].fileName, needed: false, wasWrong: false, hasPassword: false };
  }
  return null;
}

/**
 * What the selection allows. A disabled Extract says why in extractText. `files` are the selected downloads (a
 * selected package contributes all of its files); `otherSets` are sets selected by other means, such as a record's
 * own row.
 */
export function planFor(files: ArchiveFile[], otherSets: ArchiveSet[], packageAlone: { id: string; name: string } | null, connected = true): ArchivePlan {
  const byId = new Map<string, ArchiveSet>();
  for (const set of [...files.map((file) => file.set).filter((set): set is ArchiveSet => !!set), ...otherSets]) if (!byId.has(set.id)) byId.set(set.id, set);
  const sets = [...byId.values()];
  const archiveFiles = files.filter((file) => file.set || looksLikeArchive(file.fileName));
  const listedSets = sets.filter((set) => files.every((file) => file.set?.id !== set.id));

  const jobIds = archiveFiles.filter((file) => !file.set || canAskFor(file.set)).map((file) => file.jobId);
  const ownSets = listedSets.filter(canAskFor);

  const reason = !connected ? 'Inbound\'s background program isn\'t connected'
    : files.length === 0 && sets.length === 0 ? 'select an archive download first'
    : archiveFiles.length === 0 && sets.length === 0 ? 'no archive selected'
    : jobIds.length > 0 || ownSets.length > 0 ? null
    : whyNotAgain(sets);

  const retry = sets.filter(canRetry);
  const repeats = retry.length > 0 && (reason === null
    ? archiveFiles.every((file) => file.set && retry.includes(file.set)) && ownSets.every((set) => retry.includes(set))
    : sets.every((set) => set.state === 'completed'));

  return {
    extractReason: reason,
    extractJobIds: reason === null ? jobIds : [],
    extractSets: reason === null ? ownSets : [],
    password: passwordFor(archiveFiles, sets, packageAlone),
    retry,
    cancel: sets.filter((set) => set.state === 'waiting' || set.state === 'extracting'),
    remove: sets.filter((set) => set.state !== 'extracting'),
    canExtract: reason === null,
    showExtract: !repeats,
    extractText: reason === null ? 'Extract' : `Extract (${reason})`,
  };
}

// ---- Dialog words ------------------------------------------------------------------------------------

export function passwordTitle(target: ArchivePasswordTarget): string {
  return target.wasWrong ? 'Wrong archive password' : 'Archive password';
}

export function passwordPrimary(target: ArchivePasswordTarget): string {
  return target.needed ? 'Extract with this password' : 'Save password';
}

/** What the password dialog says about the archive it asks for. */
export function passwordHint(target: ArchivePasswordTarget): string {
  const notTheDownload = 'This is for the archive itself, not for the download or the site it came from.';
  if (target.packageId) {
    return target.needed
      ? `An archive in "${target.name}" needs its password before it can be extracted. ${notTheDownload} It is used for every archive in this package.`
      : `The password for the archives in "${target.name}". ${notTheDownload} It is used for every archive in this package, including ones still downloading.`;
  }
  if (target.wasWrong) return `The password tried for "${target.name}" was wrong. Enter the right one and extraction runs again. ${notTheDownload}`;
  return target.needed
    ? `"${target.name}" needs its password before it can be extracted. ${notTheDownload}`
    : `The password for "${target.name}". ${notTheDownload} It is saved for when this archive is extracted.`;
}

/** What each way out of the password dialog says afterwards. */
export function passwordDone(target: ArchivePasswordTarget, cleared: boolean): string {
  if (cleared) return `The saved archive password for "${target.name}" is gone.`;
  if (target.needed) return `Trying to extract "${target.name}" with this password…`;
  return `Archive password saved for "${target.name}".`;
}

export const extractAgainQuestion = 'This extracts the archive again. The Extraction page in Options decides what happens to files that are already there.';

export function removeQuestion(sets: ArchiveSet[]): string {
  return sets.length === 1
    ? `Forget the extraction record for "${sets[0].name}", and any archive password saved for it? No file is deleted, neither the archive nor what was extracted.`
    : `Forget the extraction records for these ${sets.length} archives, and any archive passwords saved for them? No file is deleted, neither the archives nor what was extracted.`;
}

/** Why selected records stayed on the list. Null when none did. */
export function leftExtracting(count: number): string | null {
  if (count <= 0) return null;
  return count === 1
    ? 'An archive that is still extracting stays on the list. Cancel the extraction first.'
    : `${count} archives that are still extracting stay on the list. Cancel the extraction first.`;
}

/** Says what came of asking for extraction. */
export function extractSummary(result: { sets?: ArchiveSet[]; ignored?: { reason?: string }[] }): string {
  const sets = result.sets ?? [];
  const started = sets.filter((set) => set.state === 'waiting' || set.state === 'extracting');
  const left = [...new Set((result.ignored ?? []).map((item) => hideAddresses(item.reason)))].filter(Boolean).slice(0, 3);
  const words = started.length === 0 ? '' : started.length === 1 ? `Extraction asked for: ${started[0].name}.` : `Extraction asked for: ${started.length} archives.`;
  return [words, ...left].filter(Boolean).join(' ');
}

// ---- The notice above the list -----------------------------------------------------------------------

export interface ArchiveNotice {
  kind: 'password' | 'completed';
  setId: string;
  text: string;
  actionText: string;
}

function isStillWorking(set: ArchiveSet): boolean {
  switch (set.state) {
    case 'extracting': return true;
    case 'waiting': return set.downloadingVolumes.length > 0 || (set.failedVolumes.length === 0 && set.missingVolumes.length === 0);
    case 'downloading': return set.downloadingVolumes.length > 0;
    default: return false;
  }
}

const noticeKey = (set: ArchiveSet) => `${set.id}:${set.attempts ?? 0}:${set.state}:${set.failureKind ?? ''}`;

/**
 * Decides what the notice above the list says. A set asking for a password always counts, because it waits for the
 * user. A completion counts only if it happened since this page opened. Dismissing lasts until the set has news.
 */
export class ArchiveNoticeBoard {
  private seen = new Map<string, string>();
  private dismissed = new Set<string>();
  private completed: string[] = [];

  update(sets: ArchiveSet[], watchingSince: Date, notifyOnCompletion = true): ArchiveNotice | null {
    for (const set of sets) {
      const key = noticeKey(set);
      const isNews = this.seen.get(set.id) !== key;
      this.seen.set(set.id, key);
      if (isNews && notifyOnCompletion && set.state === 'completed' && set.completedAt && new Date(set.completedAt) >= watchingSince && !this.completed.includes(set.id)) this.completed.push(set.id);
    }
    for (const id of [...this.seen.keys()]) if (!sets.some((set) => set.id === id)) this.seen.delete(id);
    this.completed = this.completed.filter((id) => sets.some((set) => set.id === id && set.state === 'completed'));

    const asking = sets.filter((set) => set.needsPassword && !this.dismissed.has(noticeKey(set))).sort((a, b) => a.updatedAt.localeCompare(b.updatedAt));
    if (asking.length > 0) {
      const first = asking[0];
      const more = asking.length - 1;
      let words = first.failureKind === 'wrongPassword'
        ? `The password for "${first.name}" was wrong, so it wasn't extracted. The download is fine.`
        : `"${first.name}" needs a password to extract. The download is fine.`;
      if (more > 0) words += more === 1 ? ' 1 more archive needs one too.' : ` ${more} more archives need one too.`;
      return { kind: 'password', setId: first.id, text: words, actionText: 'Enter Password…' };
    }

    // A package's sets share a line once the last of them is done, rather than one line after another.
    const ready = this.completed
      .map((id) => sets.find((set) => set.id === id)!)
      .filter((done) => !done.packageId || !sets.some((set) => set.packageId === done.packageId && isStillWorking(set)));
    if (ready.length === 0) return null;
    const latest = ready[ready.length - 1];
    const others = ready.length - 1;
    const text = others === 0 ? `Extraction completed: "${latest.name}".` : others === 1 ? `Extraction completed: "${latest.name}" and 1 more.` : `Extraction completed: "${latest.name}" and ${others} more.`;
    return { kind: 'completed', setId: latest.id, text, actionText: 'Show' };
  }

  dismiss(notice: ArchiveNotice, sets: ArchiveSet[]): void {
    if (notice.kind === 'completed') {
      this.completed = [];
      return;
    }
    const set = sets.find((candidate) => candidate.id === notice.setId);
    if (set) this.dismissed.add(noticeKey(set));
  }
}

// ---- Extract an archive from disk (P24) --------------------------------------------------------------

/** The archive's full path from a folder on the server and a file name. */
export function joinArchivePath(folder: string, name: string): string {
  const base = folder.trim();
  const file = name.trim();
  const separator = base.includes('\\') && !base.includes('/') ? '\\' : '/';
  return base.endsWith('/') || base.endsWith('\\') ? `${base}${file}` : `${base}${separator}${file}`;
}

/** Why a typed archive name can't be used; null when it can. */
export function archiveNameProblem(folder: string, name: string): string | null {
  if (!folder.trim()) return 'Choose the folder the archive is in.';
  if (!name.trim()) return 'Enter the archive\'s file name.';
  if (/[\/]/.test(name)) return 'Enter the file name only; the folder is chosen above.';
  if (!looksLikeArchive(name.trim())) return 'That doesn\'t look like an archive. Use a .rar, .7z, .zip, .tar, .gz, .tgz, .bz2, .xz or .zst file, or the first .001 piece.';
  return null;
}

// ---- What a list selection stands for ----------------------------------------------------------------

/**
 * The archive commands' inputs for the rows selected in the list: downloads (a package row stands for its files in
 * this view), records selected by their own rows, and the one package row when nothing else is selected.
 */
export function planForRows(rows: Row[], selected: ReadonlySet<string>, setOfJob: (jobId: string) => ArchiveSet | undefined, connected = true): ArchivePlan {
  const files = new Map<string, ArchiveFile>();
  const addJob = (job: Job) => files.set(job.id, { jobId: job.id, fileName: job.fileName, filePath: job.finalFilePath, set: setOfJob(job.id) });
  const records: ArchiveSet[] = [];
  let packageAlone: { id: string; name: string } | null = null;
  const chosen = rows.filter((row) => selected.has(row.id));
  for (const row of chosen) {
    if (row.kind === 'job') addJob(row.job);
    else if (row.kind === 'package') for (const job of row.shown) addJob(job);
    else if (row.kind === 'archive') records.push(row.set);
  }
  if (chosen.length === 1 && chosen[0].kind === 'package') packageAlone = { id: chosen[0].group.id, name: chosen[0].group.name };
  return planFor([...files.values()], records, packageAlone, connected);
}

/** One board for the page's lifetime, so a dismissed notice stays dismissed and a finish while away still counts. */
export const noticeBoard = new ArchiveNoticeBoard();
export const noticeWatchingSince = new Date();
