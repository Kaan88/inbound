// The download list's rows: the state views of the desktop app (All, Unfinished, Active, Queued, Failed, Finished),
// packages with their files under them, and the provider's own work above the local rows.

import { percent } from './format';
import type { Model } from './model';
import { matchesRemoteSearch } from './remote';
import type { ArchiveSet, DownloadGroup, Job, RemoteJob } from './types';

export type ViewKey = 'all' | 'unfinished' | 'active' | 'queued' | 'failed' | 'finished' | 'remote' | 'extractions';

export const views: { key: ViewKey; label: string }[] = [
  { key: 'all', label: 'All downloads' },
  { key: 'unfinished', label: 'Unfinished' },
  { key: 'active', label: 'Active' },
  { key: 'queued', label: 'Queued' },
  { key: 'failed', label: 'Failed' },
  { key: 'finished', label: 'Finished' },
];

/** The same rules as the desktop list (MainWindow's category filter). */
export function inView(job: Job, view: ViewKey): boolean {
  switch (view) {
    case 'all': return true;
    case 'finished': return job.state === 'completed';
    case 'unfinished': return job.state !== 'completed' && job.state !== 'cancelled';
    case 'active': return ['connecting', 'downloading', 'verifying', 'retryWaiting'].includes(job.state);
    case 'queued': return job.state === 'queued' || job.state === 'paused';
    case 'failed': return job.state === 'failed';
    default: return false;
  }
}

export interface PackageSummary {
  state: string;
  totalBytes: number | null;
  receivedBytes: number;
  progress: number | null;
  bytesPerSecond: number;
  done: number;
  count: number;
}

export type Row =
  | { kind: 'remote'; id: string; remote: RemoteJob }
  | { kind: 'archive'; id: string; set: ArchiveSet }
  | { kind: 'package'; id: string; group: DownloadGroup; jobs: Job[]; shown: Job[]; summary: PackageSummary; expanded: boolean }
  | { kind: 'job'; id: string; job: Job; depth: 0 | 1; packageId?: string };

export function summarize(jobs: Job[]): PackageSummary {
  let total: number | null = 0;
  let received = 0;
  let rate = 0;
  let done = 0;
  for (const job of jobs) {
    if (job.totalBytes === undefined || job.totalBytes === null) total = null;
    else if (total !== null) total += job.totalBytes;
    received += job.receivedBytes ?? 0;
    rate += job.bytesPerSecond ?? 0;
    if (job.state === 'completed') done++;
  }

  const states = jobs.map((job) => job.state);
  const state =
    states.some((s) => ['connecting', 'downloading', 'verifying'].includes(s)) ? 'downloading' :
    states.some((s) => s === 'retryWaiting') ? 'retryWaiting' :
    states.some((s) => s === 'failed') ? 'failed' :
    states.length > 0 && states.every((s) => s === 'completed') ? 'completed' :
    states.some((s) => s === 'queued') ? 'queued' :
    states.some((s) => s === 'paused') ? 'paused' :
    states.every((s) => s === 'cancelled') ? 'cancelled' : 'paused';
  const progress = total && total > 0 ? Math.min(100, (received / total) * 100) : jobs.length === 0 ? null : (done / jobs.length) * 100;
  return { state, totalBytes: total, receivedBytes: received, progress, bytesPerSecond: rate, done, count: jobs.length };
}

export type SortKey = 'added' | 'name' | 'size' | 'status';

function compare(a: Job, b: Job, sort: SortKey): number {
  switch (sort) {
    case 'name': return a.fileName.localeCompare(b.fileName, undefined, { numeric: true, sensitivity: 'base' });
    case 'size': return (b.totalBytes ?? -1) - (a.totalBytes ?? -1);
    case 'status': return a.state.localeCompare(b.state) || (percent(b) ?? 0) - (percent(a) ?? 0);
    default: return b.createdAt.localeCompare(a.createdAt);
  }
}

function matches(job: Job, search: string): boolean {
  if (!search) return true;
  const term = search.toLocaleLowerCase();
  return job.fileName.toLocaleLowerCase().includes(term) ||
    (!job.addressHidden && job.url.toLocaleLowerCase().includes(term)) ||
    (job.description ?? '').toLocaleLowerCase().includes(term);
}

/**
 * The rows for a view. A package of two or more files in the list is one row, with its files under it when
 * expanded; a package of one file is an ordinary row. A package shows in a view when any of its files does, and
 * then lists only those. Remote work without local files yet, and archives, come first in All downloads.
 */
export function buildRows(model: Model, view: ViewKey, search: string, expanded: ReadonlySet<string>, sort: SortKey = 'added'): Row[] {
  const rows: Row[] = [];
  const term = search.trim();

  if (view === 'all' || view === 'remote') {
    for (const remote of Object.values(model.remoteJobs).sort((a, b) => b.createdAt.localeCompare(a.createdAt))) {
      if (remote.state === 'localStarted') continue;
      if (term && !matchesRemoteSearch(remote, term)) continue;
      rows.push({ kind: 'remote', id: `remote:${remote.id}`, remote });
    }
  }

  if (view === 'extractions') {
    for (const set of Object.values(model.archives).sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))) {
      if (term && !set.name.toLocaleLowerCase().includes(term.toLocaleLowerCase())) continue;
      rows.push({ kind: 'archive', id: `archive:${set.id}`, set });
    }
    return rows;
  }

  if (view === 'remote') return rows;

  const inPackage = new Map<string, DownloadGroup>();
  for (const group of model.groups) {
    const present = group.jobIds.filter((id) => id in model.jobs);
    if (present.length < 2) continue;
    for (const id of present) inPackage.set(id, group);
  }

  const visible = Object.values(model.jobs).filter((job) => inView(job, view) && matches(job, term)).sort((a, b) => compare(a, b, sort));
  const placed = new Set<string>();
  for (const job of visible) {
    const group = inPackage.get(job.id);
    if (!group) {
      rows.push({ kind: 'job', id: job.id, job, depth: 0 });
      continue;
    }

    if (placed.has(group.id)) continue;
    placed.add(group.id);
    const members = group.jobIds.map((id) => model.jobs[id]).filter((member): member is Job => !!member);
    const shown = visible.filter((candidate) => inPackage.get(candidate.id)?.id === group.id);
    const isExpanded = expanded.has(group.id);
    rows.push({ kind: 'package', id: `package:${group.id}`, group, jobs: members, shown, summary: summarize(members), expanded: isExpanded });
    if (isExpanded) {
      for (const member of shown) rows.push({ kind: 'job', id: member.id, job: member, depth: 1, packageId: group.id });
    }
  }

  return rows;
}

/**
 * The ids of the downloads a selection stands for: a package's row stands for its files in this view, so deleting
 * a package from Finished leaves its unfinished files alone.
 */
export function selectedJobIds(rows: Row[], selected: ReadonlySet<string>): string[] {
  const ids = new Set<string>();
  for (const row of rows) {
    if (!selected.has(row.id)) continue;
    if (row.kind === 'job') ids.add(row.job.id);
    if (row.kind === 'package') for (const job of row.shown) ids.add(job.id);
  }
  return [...ids];
}

export function counts(model: Model): Record<ViewKey, number> {
  const result: Record<ViewKey, number> = { all: 0, unfinished: 0, active: 0, queued: 0, failed: 0, finished: 0, remote: 0, extractions: 0 };
  for (const job of Object.values(model.jobs)) {
    for (const view of views) if (inView(job, view.key)) result[view.key]++;
  }
  result.remote = Object.values(model.remoteJobs).filter((job) => job.state !== 'localStarted').length;
  result.extractions = Object.keys(model.archives).length;
  return result;
}
