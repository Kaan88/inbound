// The provider screens' rules and words, ported from the desktop window (Remote/RemoteRows.cs, RemoteText.cs,
// RemoteFileTree.cs). Nothing here touches the DOM, so the tests run it as it is.

import type { ContainerAddResult, ContainerPreview, Job, RemoteAccount, RemoteFile, RemoteItem, RemoteJob } from './types';
import type { Tone } from './format';

// ---- Words -------------------------------------------------------------------------------------------

/** Addresses never show; the server scrubs them already, and this does it again. */
export function hideAddresses(text: string | null | undefined): string {
  return (text ?? '').replace(/\b(?:https?|ftp|magnet):[^\s"'<>]+/gi, 'an address');
}

export function providerNameOf(source: { providerName?: string; provider: string }): string {
  return source.providerName?.trim() ? source.providerName : source.provider;
}

export function kindSingular(kind: string | undefined): string {
  switch (kind) {
    case 'torrents': return 'Torrent';
    case 'webdl': return 'Web download';
    case 'usenet': return 'Usenet';
    default: return kind ?? '';
  }
}

export function kindHeading(kind: string | undefined): string {
  switch (kind) {
    case 'torrents': return 'Torrents';
    case 'webdl': return 'Web Downloads';
    case 'usenet': return 'Usenet';
    default: return kind ?? '';
  }
}

function kindEmptyWords(kind: string | undefined): string {
  switch (kind) {
    case 'torrents': return 'torrents';
    case 'webdl': return 'web downloads';
    case 'usenet': return 'Usenet items';
    default: return 'items';
  }
}

export function emptyKind(kind: string | undefined, provider: string): string {
  return `There are no ${kindEmptyWords(kind)} on your ${provider} account.`;
}

/** The Host's own words when it sent some, otherwise plain words for the kind of failure. */
export function failureText(error?: string | null, failureKind?: string | null, providerName?: string | null): string {
  const words = hideAddresses(error).trim();
  if (words) return words;
  const provider = providerName?.trim() ? providerName : 'The provider';
  switch (failureKind) {
    case 'authentication': return `${provider} refused the API key. Check it, then save it again.`;
    case 'accountLimit': return `${provider} says your account doesn't allow this.`;
    case 'accountBusy': return `${provider} says your account is at its limit for now.`;
    case 'linkPasswordRequired': return 'This link needs a password from the site it came from. That isn\'t an archive password.';
    case 'rateLimited': return `${provider} is asking for fewer requests. Try again in a little while.`;
    case 'notFound': return `${provider} has no such item.`;
    case 'rejected': return `${provider} refused this.`;
    case 'serviceError': return `${provider} is having problems. Try again later.`;
    case 'network': return `${provider} could not be reached. Check your connection.`;
    case 'badResponse': return `${provider} sent an answer this app can't read.`;
    case 'notCached': return `This isn't cached on ${provider}.`;
    case 'alreadyExists': return `${provider} says this is already on your account.`;
    default: return 'That could not be done.';
  }
}

/** The desktop's byte format for the provider screens: blank when unknown, one decimal above bytes. */
export function providerBytes(value: number | null | undefined): string {
  if (!value || value <= 0) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  return unit === 0 ? `${value} B` : `${size.toFixed(1)} ${units[unit]}`;
}

function spanWords(seconds: number): string {
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (hours >= 1) return `${hours}h ${minutes}m`;
  return minutes >= 1 ? `${minutes}m ${Math.floor(seconds % 60)}s` : `${Math.floor(seconds % 60)}s`;
}

/** The provider's own progress, named as such. It says nothing about bytes reaching this computer. */
export function providerProgress(progress: number, bytesPerSecond: number, etaSeconds: number | undefined, provider: string, active = true): string {
  if (!active) return '';
  const parts: string[] = [];
  if (progress > 0 && progress < 1) parts.push(`${Math.round(progress * 100)}%`);
  if (bytesPerSecond > 0) parts.push(`${providerBytes(bytesPerSecond)}/s`);
  if (etaSeconds && etaSeconds > 0) parts.push(`about ${spanWords(etaSeconds)} left`);
  return parts.length === 0 ? '' : `${provider} reports ${parts.join(', ')}`;
}

function sameDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

const pad = (n: number) => String(n).padStart(2, '0');

export function nextCheck(job: RemoteJob, now: Date): string {
  if (!job.nextCheckAt || !['intended', 'submissionUnknown', 'waiting', 'blocked'].includes(job.state)) return '';
  if (job.approvalNeeded) return '';
  const next = new Date(job.nextCheckAt);
  if (Number.isNaN(next.getTime())) return '';
  if (next <= now) return 'Checking soon';
  const time = `${pad(next.getHours())}:${pad(next.getMinutes())}:${pad(next.getSeconds())}`;
  if (sameDay(next, now)) return `Next check ${time}`;
  const month = next.toLocaleString(undefined, { month: 'short' });
  return `Next check ${month} ${pad(next.getDate())} ${pad(next.getHours())}:${pad(next.getMinutes())}`;
}

function clean(words: string | undefined | null, fallback: string): string {
  return words?.trim() ? hideAddresses(words) : fallback;
}

export function jobName(job: RemoteJob): string {
  if (job.name?.trim()) return hideAddresses(job.name);
  if (job.displayHost?.trim()) return hideAddresses(job.displayHost);
  return `${kindSingular(job.kind)} on ${providerNameOf(job)}`;
}

/** The Host's words are the longer form; this one fits a list cell. */
export function shortStatus(job: RemoteJob): string {
  const provider = providerNameOf(job);
  switch (job.state) {
    case 'failed': return job.failureKind === 'linkPasswordRequired' ? 'Needs the link\'s password' : `Failed on ${provider}`;
    case 'blocked': return `${provider} account busy`;
    case 'awaitingSelection': return 'Choose files';
    case 'intended':
      if (job.heldForApproval) return 'Copied link';
      if (job.approvalNeeded) return 'Not started';
      return job.cached ? `Cached on ${provider}` : `Checking ${provider}…`;
    case 'submitting': return `Adding to ${provider}…`;
    case 'submissionUnknown': return `Checking ${provider}…`;
    case 'waiting':
      switch (job.stage) {
        case 'queued': return job.itemId ? `Waiting for ${provider}` : `In ${provider}'s queue`;
        case 'metadataPending': return `Waiting for ${provider}`;
        case 'processing': return `Processing on ${provider}`;
        case 'stalled': return `Stalled on ${provider}`;
        case 'ready': return 'Ready to download';
        default: return `Waiting for ${provider}`;
      }
    default: return clean(job.status, `Waiting for ${provider}`);
  }
}

function failureCause(job: RemoteJob): string {
  const provider = providerNameOf(job);
  if (job.failureMessage?.trim()) return failureText(job.failureMessage, job.failureKind, provider);
  if (job.state === 'failed' || job.failureKind) return failureText(null, job.failureKind, provider);
  return job.state === 'blocked' ? clean(job.status, '') : '';
}

export interface RemoteRowView {
  name: string;
  status: string;
  detail: string;
  tooltip: string;
  tone: Tone;
}

/** What the list shows for a job the provider still works on. The status is a stage word, never a local figure. */
export function jobRow(job: RemoteJob, now: Date = new Date()): RemoteRowView {
  const provider = providerNameOf(job);
  const name = jobName(job);
  const status = shortStatus(job);
  const parts: string[] = [];
  const cause = failureCause(job);
  if (cause) parts.push(cause);
  const progress = providerProgress(job.progress, job.speedBytesPerSecond, job.etaSeconds, provider, job.state === 'waiting');
  if (progress) parts.push(progress);
  if (job.approvalNeeded && job.state === 'intended') parts.push(job.heldForApproval ? `Enter adds it to ${provider}.` : `Enter starts it on ${provider}.`);
  const next = nextCheck(job, now);
  if (next) parts.push(next);

  const tip = [name, `${provider}: ${clean(job.status, status)}`, ...parts.filter((part) => part !== job.status)];
  if (job.rawState?.trim()) tip.push(`${provider}'s own word for it: ${hideAddresses(job.rawState)}`);
  tip.push(...(job.notes ?? []).filter((note) => note.trim()).slice(0, 5).map(hideAddresses));

  const tone: Tone =
    job.state === 'failed' ? 'failed' :
    ['waiting', 'blocked', 'awaitingSelection', 'intended', 'submissionUnknown', 'submitting'].includes(job.state) ? 'waiting' :
    'neutral';
  return { name, status, detail: parts.join(' · '), tooltip: tip.join('\n'), tone };
}

// ---- Rules -------------------------------------------------------------------------------------------

/** Once local downloads exist, those are the rows, and they are ordinary ones. */
export function isRow(job: RemoteJob): boolean {
  return job.state !== 'localStarted' && job.state !== 'removed';
}

export function sameProvider(a: string | undefined, b: string | undefined): boolean {
  return (a ?? '').toLowerCase() === (b ?? '').toLowerCase();
}

export function hasUsableKey(account: RemoteAccount): boolean {
  return account.hasKey && !account.keyUnreadable;
}

export function canBrowse(account: RemoteAccount): boolean {
  return hasUsableKey(account) && (account.itemKinds?.length ?? 0) > 0 && (account.capabilities ?? []).some((c) => c.toLowerCase() === 'catalog');
}

/** The accounts that get a Waiting entry: a usable key, or jobs left from before a key was cleared. */
export function showsWaiting(account: RemoteAccount, jobs: readonly RemoteJob[]): boolean {
  return hasUsableKey(account) || jobs.some((job) => isRow(job) && sameProvider(job.provider, account.provider));
}

export interface RemoteRowActions {
  checkNow: boolean;
  selectFiles: boolean;
  approve: boolean;
  enterLinkPassword: boolean;
  retry: boolean;
}

/**
 * The files can be chosen before the provider is done: the job would ask for them, and nothing holds it back. A
 * copied link isn't added yet, and a folder link asks by default, so a held one is left out.
 */
export function canChooseEarly(job: RemoteJob): boolean {
  return !job.heldForApproval && !job.approvalNeeded
    && (job.selectionMode === 'askWhenReady' || job.selectionMode === 'askIfCached')
    && ['intended', 'submitting', 'submissionUnknown', 'waiting'].includes(job.state);
}

/** What the Host would take for a job right now. */
export function actionsOf(job: RemoteJob): RemoteRowActions {
  return {
    // A look at a held copied link could submit it, so the Host refuses, and there is nothing to offer.
    checkNow: !job.heldForApproval && ['intended', 'submissionUnknown', 'waiting', 'blocked', 'awaitingSelection'].includes(job.state),
    selectFiles: job.state === 'awaitingSelection' || canChooseEarly(job),
    approve: job.approvalNeeded && job.state === 'intended',
    enterLinkPassword: job.state === 'failed' && job.failureKind === 'linkPasswordRequired',
    retry: job.state === 'failed' || job.state === 'blocked',
  };
}

/** The job that covers this item, or a failed one if that is all there is. */
export function jobFor(jobs: Iterable<RemoteJob>, item: Pick<RemoteItem, 'itemId' | 'kind' | 'provider'>): RemoteJob | undefined {
  let best: RemoteJob | undefined;
  for (const job of jobs) {
    if (job.itemId !== item.itemId || job.kind !== item.kind || !sameProvider(job.provider, item.provider)) continue;
    if (!best) {
      best = job;
      continue;
    }
    const failedA = best.state === 'failed';
    const failedB = job.state === 'failed';
    if (failedA !== failedB ? failedA : job.updatedAt > best.updatedAt) best = job;
  }
  return best;
}

/** remote.deleteRemote takes a job, and refuses one whose item isn't known. */
export function canDeleteFromProvider(job: RemoteJob | undefined): boolean {
  return !!job && !!job.itemId;
}

/** TB.S1: accepted by the provider and waiting in its queue, with no item yet. */
export function isInProviderQueue(job: RemoteJob | undefined): boolean {
  return !!job && job.state === 'waiting' && job.stage === 'queued' && !job.itemId;
}

export function downloadStarted(result: { ok?: boolean; note?: string; job?: { state: string } }): boolean {
  return !!result.ok && !!result.job && !result.note?.trim() && !['failed', 'blocked', 'removed'].includes(result.job.state);
}

/** Whether "Check now" has anything to look at on this provider. */
export function checkable(jobs: Iterable<RemoteJob>, provider: string): boolean {
  for (const job of jobs) if (sameProvider(job.provider, provider) && actionsOf(job).checkNow) return true;
  return false;
}

/** How a package's local downloads are doing, in the desktop's words. */
export function localWords(files: Pick<Job, 'state'>[]): string | null {
  if (files.length === 0) return null;
  const completed = files.filter((file) => file.state === 'completed').length;
  const failed = files.filter((file) => file.state === 'failed').length;
  const paused = files.filter((file) => file.state === 'paused').length;
  if (completed === files.length) return 'Completed';
  if (failed > 0) return `${failed} of ${files.length} local downloads failed`;
  if (files.some((file) => ['connecting', 'downloading', 'verifying', 'assembling', 'retryWaiting'].includes(file.state))) {
    return files.length === 1 ? 'Downloading locally' : `Downloading locally, ${completed} of ${files.length} done`;
  }
  return paused === files.length - completed ? 'Paused locally' : 'Waiting to download locally';
}

/** The local files behind a remote job: those in its package. */
export function localFilesOf(job: RemoteJob, groups: { id: string; jobIds: string[] }[], jobs: Record<string, Job>): Job[] {
  if (!job.packageId) return [];
  const group = groups.find((candidate) => candidate.id === job.packageId);
  return (group?.jobIds ?? []).map((id) => jobs[id]).filter((job): job is Job => !!job);
}

// ---- Items on the account ----------------------------------------------------------------------------

export interface RemoteItemView {
  name: string;
  type: string;
  status: string;
  size: string;
  files: string;
  added: string;
  expires: string;
  details: string;
  problem: string;
  tone: Tone;
  canDownload: boolean;
}

function day(value: string | undefined | null): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime()) || date.getFullYear() < 2000) return '';
  return date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: '2-digit' });
}

function itemStatus(item: RemoteItem, provider: string, failed: boolean): string {
  if (failed) return `Failed on ${provider}`;
  switch (item.stage) {
    case 'ready': return item.cached ? `Cached on ${provider}` : 'Ready to download';
    case 'queued': return `Waiting for ${provider}`;
    case 'metadataPending': return `Waiting for ${provider} to list the files`;
    case 'processing': return `Processing on ${provider}`;
    case 'stalled': return `Stalled on ${provider}`;
    default: return item.rawState?.trim() ? `${provider} reports: ${hideAddresses(item.rawState)}` : `Waiting for ${provider}`;
  }
}

/** The status a job already on the list gives its item: the local figure once files are coming, else its stage. */
function linkedStatus(job: RemoteJob, localStatus: string | null | undefined): string {
  return job.state === 'localStarted' ? localStatus ?? clean(job.status, 'Downloading locally') : shortStatus(job);
}

function itemTone(item: RemoteItem, job: RemoteJob | undefined, status: string, failed: boolean): Tone {
  if (failed || job?.state === 'failed') return 'failed';
  if (job?.state === 'localStarted') {
    if (status === 'Completed') return 'done';
    if (status.includes('failed')) return 'failed';
    if (status.startsWith('Downloading')) return 'active';
    return 'waiting';
  }
  return job || item.stage !== 'ready' ? 'waiting' : 'neutral';
}

export function itemView(item: RemoteItem, provider: string, job?: RemoteJob, localStatus?: string | null): RemoteItemView {
  const name = item.name?.trim() ? hideAddresses(item.name) : `${kindSingular(item.kind)} ${item.itemId}`;
  const type = kindSingular(item.kind);
  const failed = item.stage === 'failed' || !!item.error?.trim();
  const status = !job || job.state === 'failed' ? itemStatus(item, provider, failed) : linkedStatus(job, localStatus);
  const size = providerBytes(item.size);
  const files = item.fileCount > 0 ? String(item.fileCount) : '';
  const added = day(item.createdAt);
  const expires = day(item.expiresAt);
  const problem = failed ? failureText(item.error, null, provider) : job?.state === 'failed' ? failureCause(job) : '';

  const details = [type];
  if (size) details.push(size);
  if (item.fileCount > 0) details.push(item.fileCount === 1 ? '1 file' : `${files} files`);
  if (added) details.push(`Added ${added}`);
  const updated = day(item.updatedAt);
  if (updated) details.push(`Updated ${updated}`);
  if (expires) details.push(`Expires ${expires}`);
  const progress = providerProgress(item.progress, item.speedBytesPerSecond, item.etaSeconds, provider, ['queued', 'processing', 'stalled', 'metadataPending'].includes(item.stage ?? ''));
  if (progress) details.push(progress);
  if (item.rawState?.trim() && item.stage === 'unknown') details.push(`${provider}'s own word for it: ${hideAddresses(item.rawState)}`);

  return { name, type, status, size, files, added, expires, details: details.join(' · '), problem, tone: itemTone(item, job, status, failed), canDownload: !failed };
}

export type ItemSortKey = 'default' | 'name' | 'type' | 'status' | 'size' | 'files' | 'added' | 'expires';

export const itemSortLabels: [ItemSortKey, string][] = [
  ['default', 'As the provider lists them'], ['name', 'Name'], ['type', 'Type'], ['status', 'Status'],
  ['size', 'Size'], ['files', 'Files'], ['added', 'Added'], ['expires', 'Expires'],
];

const natural = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/** A column's order; a provider gives no size or date for some items, and those stay at the end either way. */
export function sortItems<T>(rows: T[], item: (row: T) => RemoteItem, view: (row: T) => RemoteItemView, key: ItemSortKey, ascending: boolean): T[] {
  if (key === 'default') return rows;
  const direction = ascending ? 1 : -1;
  const missing = (row: T): boolean => {
    const it = item(row);
    switch (key) {
      case 'size': return !(it.size > 0);
      case 'files': return !(it.fileCount > 0);
      case 'added': return !it.createdAt;
      case 'expires': return !it.expiresAt;
      default: return false;
    }
  };
  const value = (row: T): number | string => {
    const it = item(row);
    switch (key) {
      case 'size': return it.size;
      case 'files': return it.fileCount;
      case 'added': return Date.parse(it.createdAt ?? '') || 0;
      case 'expires': return Date.parse(it.expiresAt ?? '') || 0;
      case 'type': return view(row).type;
      case 'status': return view(row).status;
      default: return view(row).name;
    }
  };
  return rows
    .map((row, index) => ({ row, index }))
    .sort((a, b) => {
      const am = missing(a.row);
      const bm = missing(b.row);
      if (am !== bm) return am ? 1 : -1;
      const x = value(a.row);
      const y = value(b.row);
      const order = typeof x === 'string' ? natural.compare(x, y as string) : x - (y as number);
      return order !== 0 ? order * direction : a.index - b.index;
    })
    .map((entry) => entry.row);
}

/** A column's order means little over part of a list; a page that came back empty ends it even if there is more. */
export function needsTheRest(key: ItemSortKey, hasMore: boolean, lastPageCount: number): boolean {
  return key !== 'default' && hasMore && lastPageCount > 0;
}

export function countedItems(count: number, hasMore: boolean): string {
  const shown = count === 1 ? '1 item' : `${count} items`;
  return hasMore ? `${shown} so far. There may be more.` : shown;
}

// ---- Questions and hints -----------------------------------------------------------------------------

export function removeQuestion(jobs: RemoteJob[]): string {
  const providers = [...new Set(jobs.map((job) => providerNameOf(job).toLowerCase()))];
  const provider = providers.length === 1 ? providerNameOf(jobs[0]) : 'the provider';
  const what = jobs.length === 1 ? `'${jobName(jobs[0])}'` : `these ${jobs.length} items`;
  return `Take ${what} off this list? This doesn't touch ${provider}: anything on your account stays there, and this app just stops watching it.`;
}

export function deleteQuestion(itemName: string, provider: string): string {
  return `Delete '${itemName}' from your ${provider} account? This can't be undone. Files already on this computer are kept.`;
}

export function dequeueQuestion(jobs: RemoteJob[]): string {
  const provider = providerNameOf(jobs[0]);
  const what = jobs.length === 1 ? `'${jobName(jobs[0])}'` : `these ${jobs.length} items`;
  return `Take ${what} out of your ${provider} queue and off this list? ${provider} hasn't started them, so nothing else is deleted.`;
}

export function linkPasswordHint(job: RemoteJob): string {
  return `'${jobName(job)}' needs the password its source site set for the link. This isn't an archive password. It is kept encrypted.`;
}

export function addToWords(jobs: RemoteJob[]): string {
  const providers = [...new Set(jobs.map((job) => providerNameOf(job).toLowerCase()))];
  return providers.length === 1 ? `Add to ${providerNameOf(jobs[0])}` : 'Add';
}

export const downloadAllWhenReady = 'Download all when ready';

export function earlyChoice(all: boolean): string {
  return all ? 'All files will download when ready.' : 'Your choice will download when ready.';
}

/** A destination the server can use: a drive path, a network share or an absolute path. */
export function isFullPath(path: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(path) || /^\\\\[^\\]/.test(path) || path.startsWith('/');
}

// ---- The account page (Options) ----------------------------------------------------------------------

export interface ProviderAccountView {
  pageTitle: string;
  keyStatus: string;
  keyStatusIsProblem: boolean;
  keyBoxHeader: string;
  accountDetails: string[];
  canValidate: boolean;
  canClear: boolean;
}

function longDay(value: Date): string {
  return value.toLocaleDateString(undefined, { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
}

function moment(value: Date): string {
  return value.toLocaleString(undefined, { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

/**
 * What the key and account look like. "Windows account" in the desktop's words is "server" here: the key is sealed
 * on the server, and the site where the key lives is the provider's.
 */
export function accountView(account: RemoteAccount, now: Date = new Date()): ProviderAccountView {
  const name = providerNameOf(account);
  const pageTitle = `${name} account`;
  if (account.keyUnreadable) {
    return {
      pageTitle,
      keyStatus: `A ${name} API key is saved, but it can't be read on this server. That happens after the app's data was moved or restored from another account or computer. Enter the key again to replace it, or clear it.`,
      keyStatusIsProblem: true,
      keyBoxHeader: 'Enter the API key again',
      accountDetails: [],
      canValidate: false,
      canClear: true,
    };
  }
  if (!account.hasKey) {
    return {
      pageTitle,
      keyStatus: `No API key is saved. Enter your ${name} API key to use your account here. You'll find it in your account settings on the ${name} site.`,
      keyStatusIsProblem: false,
      keyBoxHeader: 'API key',
      accountDetails: [],
      canValidate: false,
      canClear: false,
    };
  }
  return {
    pageTitle,
    keyStatus: 'An API key is saved. It can\'t be shown again. Enter a new one to replace it.',
    keyStatusIsProblem: false,
    keyBoxHeader: 'Replace the API key',
    accountDetails: accountDetails(account, name, now),
    canValidate: true,
    canClear: true,
  };
}

function accountDetails(account: RemoteAccount, name: string, now: Date): string[] {
  if (!account.checkedAt) return [`Not checked since the key changed or the app started. Select Validate to ask ${name}.`];
  const lines: string[] = [];
  if (account.plan?.trim()) lines.push(`Plan: ${hideAddresses(account.plan)}`);
  if (account.premiumUntil) {
    const until = new Date(account.premiumUntil);
    lines.push(until > now ? `Premium until ${longDay(until)}` : `Premium ended on ${longDay(until)}`);
  } else {
    lines.push(account.premium ? 'Premium' : 'Not premium');
  }
  if (account.cooldownUntil && new Date(account.cooldownUntil) > now) lines.push(`${name} reports a cooldown until ${moment(new Date(account.cooldownUntil))}`);
  lines.push(`Checked ${moment(new Date(account.checkedAt))}`);
  return lines;
}

export function validated(account: RemoteAccount | undefined): string {
  return `${account?.providerName?.trim() ? account.providerName : 'The provider'} accepted this key.`;
}

// ---- Hosters ----------------------------------------------------------------------------------------

export interface HosterLike {
  name: string;
  domains: string[];
  available: boolean;
}

export function filterHosters<T extends HosterLike>(hosters: T[], term: string, downOnly: boolean): T[] {
  const needle = term.trim().toLowerCase();
  return hosters.filter((hoster) => {
    if (downOnly && hoster.available) return false;
    return !needle || hoster.name.toLowerCase().includes(needle) || hoster.domains.some((domain) => domain.toLowerCase().includes(needle));
  });
}

export function hostersUp(hosters: HosterLike[]): string {
  return `${hosters.filter((hoster) => hoster.available).length} of ${hosters.length} hosts up.`;
}

export function hosterDetails(hoster: { dailyLinkLimit?: number; dailyLinksUsed?: number; dailyBandwidthLimit?: number; dailyBandwidthUsed?: number; perLinkSizeLimit?: number }): string {
  const size = (n: number) => providerBytes(n);
  const parts: string[] = [];
  const linkLimit = hoster.dailyLinkLimit && hoster.dailyLinkLimit > 0 ? hoster.dailyLinkLimit : undefined;
  if (hoster.dailyLinksUsed !== undefined && hoster.dailyLinksUsed !== null) parts.push(linkLimit ? `Links today ${hoster.dailyLinksUsed} of ${linkLimit}` : `Links today ${hoster.dailyLinksUsed}`);
  else if (linkLimit) parts.push(`${linkLimit} links a day`);
  const bandwidth = hoster.dailyBandwidthLimit && hoster.dailyBandwidthLimit > 0 ? hoster.dailyBandwidthLimit : undefined;
  if (hoster.dailyBandwidthUsed !== undefined && hoster.dailyBandwidthUsed !== null) parts.push(bandwidth ? `Today ${size(hoster.dailyBandwidthUsed) || '0 B'} of ${size(bandwidth)}` : `Today ${size(hoster.dailyBandwidthUsed) || '0 B'}`);
  else if (bandwidth) parts.push(`${size(bandwidth)} a day`);
  if (hoster.perLinkSizeLimit && hoster.perLinkSizeLimit > 0) parts.push(`Up to ${size(hoster.perLinkSizeLimit)} a file`);
  return parts.join(' · ');
}

// ---- The file tree -----------------------------------------------------------------------------------

export interface FileNode {
  key: string;
  name: string;
  parent: FileNode | null;
  depth: number;
  isFolder: boolean;
  fileId?: string;
  note?: string;
  size: number;
  children: FileNode[];
  /** Files under this node that can be chosen (a file counts itself). */
  choosable: number;
}

export interface FileTree {
  roots: FileNode[];
  files: FileNode[];
}

function noteFor(file: RemoteFile, hasPath: boolean): string | undefined {
  const words = file.note?.trim() ? hideAddresses(file.note) : undefined;
  if (!hasPath) return words ?? 'Its path isn\'t safe to use.';
  if (file.infected) return words ?? 'Marked as infected.';
  if (file.size <= 0) return words ?? 'It is empty.';
  if (!file.fileId) return words ?? 'It can\'t be chosen.';
  return file.selectable ? undefined : words ?? 'It can\'t be chosen.';
}

/**
 * A provider item's files as folders, from the relative paths the Host gave. A file that is infected, empty or has
 * no safe path stays on show with its note and can never be chosen.
 */
export function buildFileTree(files: RemoteFile[]): FileTree {
  const roots: FileNode[] = [];
  const all: FileNode[] = [];
  const folders = new Map<string, FileNode>();
  const seen = new Set<string>();
  for (const file of files) {
    if (file.fileId && seen.has(file.fileId)) continue;
    if (file.fileId) seen.add(file.fileId);
    const parts = file.relativePath?.trim() ? hideAddresses(file.relativePath).split(/[\\/]/).filter(Boolean) : [];
    let parent: FileNode | null = null;
    for (let i = 0; i < parts.length - 1; i++) {
      // One folder on disk, however a provider spells it.
      const key: string = `${(parent as FileNode | null)?.key ?? ""}/${parts[i].toUpperCase()}`;
      let folder: FileNode | undefined = folders.get(key);
      if (!folder) {
        folder = { key, name: parts[i], parent, depth: parent ? parent.depth + 1 : 0, isFolder: true, size: 0, children: [], choosable: 0 };
        folders.set(key, folder);
        (parent?.children ?? roots).push(folder);
      }
      parent = folder;
    }
    const hasPath = parts.length > 0;
    const note = noteFor(file, hasPath);
    const node: FileNode = {
      key: `file:${file.fileId}:${all.length}`,
      name: hasPath ? parts[parts.length - 1] : 'File with no safe path',
      parent,
      depth: parent ? parent.depth + 1 : 0,
      isFolder: false,
      fileId: file.fileId,
      note,
      size: Math.max(0, file.size),
      children: [],
      choosable: note ? 0 : 1,
    };
    (parent?.children ?? roots).push(node);
    all.push(node);
  }

  const walk = (level: FileNode[]) => {
    level.sort((a, b) => (a.isFolder !== b.isFolder ? (a.isFolder ? -1 : 1) : natural.compare(a.name, b.name)));
    for (const node of level) {
      if (!node.isFolder) continue;
      walk(node.children);
      node.size = node.children.reduce((sum, child) => sum + child.size, 0);
      node.choosable = node.children.reduce((sum, child) => sum + child.choosable, 0);
    }
  };
  walk(roots);
  return { roots, files: all };
}

/** Every node in view order, a folder's contents only when it is open. */
export function visibleNodes(tree: FileTree, collapsed: ReadonlySet<string>): FileNode[] {
  const out: FileNode[] = [];
  const walk = (level: FileNode[]) => {
    for (const node of level) {
      out.push(node);
      if (node.isFolder && !collapsed.has(node.key)) walk(node.children);
    }
  };
  walk(tree.roots);
  return out;
}

export function leafFiles(node: FileNode): FileNode[] {
  return node.isFolder ? node.children.flatMap(leafFiles) : [node];
}

export function choosableIds(tree: FileTree): string[] {
  return tree.files.filter((file) => file.choosable > 0 && file.fileId).map((file) => file.fileId!);
}

/** A folder is true when all it can give is chosen, null when only some. */
export function checkedState(node: FileNode, chosen: ReadonlySet<string>): boolean | null {
  const files = leafFiles(node).filter((file) => file.choosable > 0 && file.fileId);
  const count = files.filter((file) => chosen.has(file.fileId!)).length;
  return count === 0 ? false : count === files.length ? true : null;
}

/** A folder takes everything under it that can be chosen; what can't be chosen stays as it is. */
export function setChecked(node: FileNode, chosen: ReadonlySet<string>, on: boolean): Set<string> {
  const next = new Set(chosen);
  for (const file of leafFiles(node)) {
    if (file.choosable === 0 || !file.fileId) continue;
    if (on) next.add(file.fileId);
    else next.delete(file.fileId);
  }
  return next;
}

/** Puts back an earlier choice after the list was read again; an id no longer there is simply gone. */
export function restoreChoice(tree: FileTree, earlier: Iterable<string>): Set<string> {
  const here = new Set(choosableIds(tree));
  return new Set([...earlier].filter((id) => here.has(id)));
}

export function treeSummary(tree: FileTree, chosen: ReadonlySet<string>): string {
  if (tree.files.length === 0) return '';
  const choosable = choosableIds(tree);
  const left = tree.files.length - choosable.length;
  const picked = choosable.filter((id) => chosen.has(id));
  const bytesChosen = tree.files.filter((file) => file.fileId && chosen.has(file.fileId) && file.choosable > 0).reduce((sum, file) => sum + file.size, 0);
  const text = `${picked.length} of ${choosable.length} ${choosable.length === 1 ? 'file' : 'files'} selected`;
  return text + (picked.length > 0 ? `, ${providerBytes(bytesChosen)}` : '') + (left > 0 ? `. ${left} can't be selected` : '');
}

/** What a choice sends: everything that can be chosen, or these ids. */
export function choiceOf(tree: FileTree, chosen: ReadonlySet<string>): { all: boolean; fileIds: string[] } {
  const choosable = choosableIds(tree);
  const picked = choosable.filter((id) => chosen.has(id));
  const all = choosable.length > 0 && picked.length === choosable.length;
  return { all, fileIds: all ? [] : picked };
}

// ---- Importing a container file (remote.container.preview / add) ----

/** The entries a container import starts with chosen: every one that can be added. */
export function containerChoice(preview: Pick<ContainerPreview, 'entries'>): number[] {
  return preview.entries.filter((entry) => entry.canAdd).map((entry) => entry.entryId);
}

/** What a container's entry is called in the list: its name, else its site, else its number. */
export function containerEntryName(entry: ContainerPreview['entries'][number]): string {
  return entry.displayName?.trim() || entry.displayHost?.trim() || `Link ${entry.entryId + 1}`;
}

/** How the add went, in one line, and what went wrong with each entry that wasn't added. */
export function containerAddSummary(result: ContainerAddResult, preview: Pick<ContainerPreview, 'entries'>, provider: string): { text: string; problems: string[] } {
  const added = result.outcomes.filter((outcome) => outcome.ok && !outcome.existing).length;
  const existing = result.outcomes.filter((outcome) => outcome.ok && outcome.existing).length;
  const problems = result.outcomes
    .filter((outcome) => !outcome.ok)
    .map((outcome) => {
      const entry = preview.entries.find((item) => item.entryId === outcome.entryId);
      return `${entry ? containerEntryName(entry) : `Link ${outcome.entryId + 1}`}: ${outcome.error || "it couldn't be added."}`;
    });
  const parts: string[] = [];
  if (added > 0) parts.push(`${added} sent to ${provider}.`);
  if (existing > 0) parts.push(`${existing} ${existing === 1 ? 'was' : 'were'} already listed.`);
  if (problems.length > 0) parts.push(`${problems.length} couldn't be added.`);
  return { text: parts.join(' ') || result.error || 'Nothing was added.', problems };
}
