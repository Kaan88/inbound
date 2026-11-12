// The download list's rows: the state views of the desktop app (All Downloads, Unfinished, Active, Queued, Failed,
// Finished), the categories, packages with their files under them, and the provider's own work and the extraction
// records above the local rows.

import type { Model } from './model';
import {
  archiveHasOwnRow, archiveView, builtInCategories, categoryOf, defaultSort, jobView, naturalCompare, packageView, remoteView, searchText,
  sortRows, summarizeGroup, type GroupSummary, type RowView, type SortState,
} from './list';
import { isRow } from './remote';
import { fileRowName, fileRowStatus, mostFileRows, moreFiles, showsFiles } from './torrents';
import { inTorrentExtraView, seedGoalOf, torrentExtraViews, torrentFilterOf, type TorrentExtraView } from './torrentList';
import { bytes, stateWord, tone as toneOf } from './format';
import type { ArchiveSet, CategoryDefinition, DownloadGroup, Job, RemoteJob, TorrentFile } from './types';

export type StateView = 'all' | 'unfinished' | 'active' | 'queued' | 'failed' | 'finished' | 'remote' | 'extractions' | 'torrents'
  | 'torrents-downloading' | 'torrents-seeding' | 'torrents-stopped' | `torrents-${TorrentExtraView}`;
/** A state view, or a category: `cat:compressed`, `cat:<custom id>`. */
export type ViewKey = StateView | `cat:${string}`;

export const stateViews: StateView[] = [
  'all', 'unfinished', 'active', 'queued', 'failed', 'finished', 'remote', 'extractions', 'torrents', 'torrents-downloading', 'torrents-seeding', 'torrents-stopped',
  // T7: shown only while they hold a torrent (App.svelte, extraViewShows).
  ...torrentExtraViews.map((view) => `torrents-${view.key}` as const),
];

/** The Torrents view and the three under it, which show the Seeds, Peers, Up and Ratio columns. */
export function isTorrentView(view: ViewKey): boolean {
  return view === 'torrents' || view.startsWith('torrents-');
}

/** The desktop's sidebar labels. */
export const views: { key: ViewKey; label: string }[] = [
  { key: 'all', label: 'All Downloads' },
  { key: 'unfinished', label: 'Unfinished' },
  { key: 'active', label: 'Active' },
  { key: 'queued', label: 'Queued' },
  { key: 'failed', label: 'Failed' },
  { key: 'finished', label: 'Finished' },
];

/** What a hash like `#/cat/music` or `#/active` names; null when it is not a list view. */
export function parseViewKey(key: string): ViewKey | null {
  if (key.startsWith('cat/') && key.length > 4) return `cat:${decodeURIComponent(key.slice(4))}`;
  return stateViews.find((v) => v === key) ?? null;
}

export function viewHash(view: ViewKey): string {
  return view.startsWith('cat:') ? `#/cat/${encodeURIComponent(view.slice(4))}` : `#/${view}`;
}

/** The categories the sidebar lists, in the desktop's order: the built-in ones, the custom ones, Other. */
export function categoryList(custom: readonly CategoryDefinition[] = []): { key: ViewKey; label: string }[] {
  return [
    ...builtInCategories.map((c) => ({ key: `cat:${c.id}` as ViewKey, label: c.label })),
    ...custom.map((c) => ({ key: `cat:${c.id}` as ViewKey, label: c.name })),
    { key: 'cat:other', label: 'Other' },
  ];
}

/** The same rules as the desktop list (MainWindow's category filter). */
export function inView(job: Job, view: ViewKey, custom: readonly CategoryDefinition[] = []): boolean {
  if (view.startsWith('cat:')) return categoryOf(job, custom) === view.slice(4);
  switch (view) {
    case 'all': return true;
    case 'finished': return job.state === 'completed';
    case 'unfinished': return job.state !== 'completed' && job.state !== 'cancelled';
    case 'active': return ['connecting', 'downloading', 'verifying', 'retryWaiting'].includes(job.state);
    case 'queued': return job.state === 'queued' || job.state === 'paused';
    case 'failed': return job.state === 'failed';
    // T1: the torrents, in every state. The sidebar shows it while torrents are on or any is listed.
    case 'torrents': return !!job.torrent;
    case 'torrents-downloading': return torrentFilterOf(job) === 'downloading';
    case 'torrents-seeding': return torrentFilterOf(job) === 'seeding';
    case 'torrents-stopped': return torrentFilterOf(job) === 'stopped';
    case 'torrents-active': case 'torrents-stalled': case 'torrents-checking': case 'torrents-files': case 'torrents-completed': case 'torrents-errored':
      return inTorrentExtraView(view.slice('torrents-'.length) as TorrentExtraView, job);
    default: return false;
  }
}

export type PackageSummary = GroupSummary;
export { summarizeGroup as summarize };

export type Row =
  | { kind: 'remote'; id: string; remote: RemoteJob; view: RowView }
  | { kind: 'archive'; id: string; set: ArchiveSet; view: RowView }
  | { kind: 'package'; id: string; group: DownloadGroup; jobs: Job[]; shown: Job[]; summary: GroupSummary; expanded: boolean; view: RowView }
  | { kind: 'job'; id: string; job: Job; depth: 0 | 1; packageId?: string; view: RowView }
  /** T1: one of an open torrent row's files, or a line about them. A side row: it stands for no download. */
  | { kind: 'torrentFile'; id: string; parentId: string; file: TorrentFile | null; more: boolean; problem: boolean; view: RowView };

/**
 * What an open torrent row lists under it: its files (from torrent.files), or the words for why there are none yet.
 * Absent: not asked yet, so the row lists "Getting the file list…".
 */
export type TorrentFilesView = { files: TorrentFile[] } | { error: string };

function blankView(name: string, detail = ''): RowView {
  return {
    name, queue: '', queueTip: '', size: 0, sizeText: '', status: '', statusTone: 'neutral', statusTip: '', bar: null, eta: null, etaText: '',
    rate: 0, rateText: '', lastTry: 0, lastTryText: '', description: '', extraction: '', extractionTone: 'neutral', extractionTip: '', created: 0, detail,
  };
}

/** The rows under an open torrent row (MainWindow.Torrents): at most {@link mostFileRows} files, then a line for the rest. */
export function torrentFileRows(job: Job, listed: TorrentFilesView | undefined): Row[] {
  const parentId = job.id;
  const note = (text: string, problem: boolean): Row => ({ kind: 'torrentFile', id: `tfile:${parentId}:note`, parentId, file: null, more: false, problem, view: blankView(text) });
  if (!listed) return [note('Getting the file list…', false)];
  if ('error' in listed) return [note(`Couldn't get the file list: ${listed.error}`, true)];
  const complete = job.state === 'completed';
  const word = stateWord(job.state);
  const rows: Row[] = listed.files.slice(0, mostFileRows).map((file) => {
    const status = fileRowStatus(file, word, complete);
    const v = blankView(fileRowName(file.path), file.path);
    v.size = Math.max(0, file.size);
    v.sizeText = file.size > 0 ? bytes(file.size) : '';
    v.status = status;
    v.statusTone = !file.wanted ? 'neutral' : status === 'Done' ? 'done' : toneOf(job.state);
    v.statusTip = file.path;
    return { kind: 'torrentFile', id: `tfile:${parentId}:${file.index}`, parentId, file, more: false, problem: false, view: v };
  });
  if (listed.files.length > mostFileRows) {
    rows.push({ kind: 'torrentFile', id: `tfile:${parentId}:more`, parentId, file: null, more: true, problem: false, view: blankView(moreFiles(listed.files.length - mostFileRows)) });
  }
  return rows;
}

export type { SortKey, SortState } from './list';

/** The package a job is listed in, when that package has at least two files on the list. */
function packagesOf(model: Model): Map<string, DownloadGroup> {
  const inPackage = new Map<string, DownloadGroup>();
  for (const group of model.groups) {
    const present = group.jobIds.filter((id) => id in model.jobs);
    if (present.length < 2) continue;
    for (const id of present) inPackage.set(id, group);
  }
  return inPackage;
}

function setsByJob(model: Model): Map<string, ArchiveSet> {
  const map = new Map<string, ArchiveSet>();
  for (const set of Object.values(model.archives)) for (const id of set.jobIds) map.set(id, set);
  return map;
}

/**
 * The rows for a view. A package of two or more files in the list is one row, with its files under it when expanded; a
 * package of one file is an ordinary row. A package shows in a view when any of its files does, and then lists only
 * those. Remote work without local files yet, and the extraction records with no file on the list, come first in All
 * Downloads, in their own order.
 */
export function buildRows(
  model: Model, view: ViewKey, search: string, expanded: ReadonlySet<string>, sort: SortState = defaultSort,
  torrentFiles: Readonly<Record<string, TorrentFilesView>> = {},
): Row[] {
  const rows: Row[] = [];
  const term = search.trim().toLocaleLowerCase();
  const custom = model.settings?.customCategories ?? [];
  const sets = setsByJob(model);
  const goal = seedGoalOf(model.settings);
  const now = Date.now();
  const matchesView = (text: string) => !term || text.includes(term);

  if (view === 'all' || view === 'remote') {
    const remotes = Object.values(model.remoteJobs).filter(isRow).sort((a, b) => b.createdAt.localeCompare(a.createdAt) || a.id.localeCompare(b.id));
    for (const remote of remotes) {
      const v = remoteView(remote);
      if (!matchesView(searchText(v))) continue;
      rows.push({ kind: 'remote', id: `remote:${remote.id}`, remote, view: v });
    }
  }

  if (view === 'all' || view === 'extractions') {
    const records = Object.values(model.archives)
      .filter((set) => view === 'extractions' || archiveHasOwnRow(set, model.jobs, model.snapshots > 0))
      .sort((a, b) => Date.parse(b.createdAt ?? b.updatedAt) - Date.parse(a.createdAt ?? a.updatedAt) || a.id.localeCompare(b.id));
    for (const set of records) {
      const v = archiveView(set);
      if (!matchesView(searchText(v))) continue;
      rows.push({ kind: 'archive', id: `archive:${set.id}`, set, view: v });
    }
  }

  if (view === 'remote' || view === 'extractions') return rows;

  const inPackage = packagesOf(model);
  const jobView0 = new Map<string, RowView>();
  const jobViewOf = (job: Job) => {
    let v = jobView0.get(job.id);
    if (!v) jobView0.set(job.id, (v = jobView(job, sets.get(job.id), goal, now)));
    return v;
  };
  const matches = (job: Job) => inView(job, view, custom) && matchesView(searchText(jobViewOf(job), job));

  // The package rows and the files that belong to no package are sorted together, each by its own cells.
  type Top = { group: DownloadGroup; members: Job[]; v: RowView } | { job: Job; v: RowView };
  const top: Top[] = [];
  for (const group of model.groups) {
    const members = group.jobIds.map((id) => model.jobs[id]).filter((m): m is Job => !!m).sort((a, b) => naturalCompare(a.fileName, b.fileName));
    if (members.length < 2) continue;
    if (!members.some(matches)) continue;
    const memberSets = [...new Set(members.map((m) => sets.get(m.id)).filter((set): set is ArchiveSet => !!set))];
    top.push({ group, members, v: packageView(group, members, memberSets) });
  }
  for (const job of Object.values(model.jobs)) {
    if (inPackage.has(job.id) || !matches(job)) continue;
    top.push({ job, v: jobViewOf(job) });
  }

  for (const entry of sortRows(top, (t) => t.v, sort)) {
    if ('job' in entry) {
      rows.push({ kind: 'job', id: entry.job.id, job: entry.job, depth: 0, view: entry.v });
      // An open torrent row lists its files under it; they are no downloads of their own.
      if (expanded.has(entry.job.id) && showsFiles(entry.job.torrent)) rows.push(...torrentFileRows(entry.job, torrentFiles[entry.job.id]));
      continue;
    }
    const { group, members, v } = entry;
    const shown = members.filter(matches);
    const isExpanded = expanded.has(group.id);
    rows.push({ kind: 'package', id: `package:${group.id}`, group, jobs: members, shown, summary: summarizeGroup(members), expanded: isExpanded, view: v });
    if (isExpanded) {
      // Under the default order the files read by name; under any other they follow the sort.
      const children = sort.key === 'added' ? shown : sortRows(shown, jobViewOf, sort);
      for (const member of children) rows.push({ kind: 'job', id: member.id, job: member, depth: 1, packageId: group.id, view: jobViewOf(member) });
    }
  }

  return rows;
}

/**
 * The ids of the downloads a selection stands for: a package's row stands for its files in this view, so deleting a
 * package from Finished leaves its unfinished files alone.
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

export function counts(model: Model): Record<string, number> {
  const custom = model.settings?.customCategories ?? [];
  const result: Record<string, number> = {
    all: 0, unfinished: 0, active: 0, queued: 0, failed: 0, finished: 0, remote: 0, extractions: 0, torrents: 0,
    'torrents-downloading': 0, 'torrents-seeding': 0, 'torrents-stopped': 0,
  };
  for (const view of torrentExtraViews) result[`torrents-${view.key}`] = 0;
  for (const entry of categoryList(custom)) result[entry.key] = 0;
  for (const job of Object.values(model.jobs)) {
    for (const view of views) if (inView(job, view.key, custom)) result[view.key]++;
    if (job.torrent) {
      result.torrents++;
      result[`torrents-${torrentFilterOf(job)}`]++;
      for (const view of torrentExtraViews) if (inTorrentExtraView(view.key, job)) result[`torrents-${view.key}`]++;
    }
    const category = `cat:${categoryOf(job, custom)}`;
    if (category in result) result[category]++;
  }
  result.remote = Object.values(model.remoteJobs).filter(isRow).length;
  result.extractions = Object.keys(model.archives).length;
  return result;
}
