// The torrent parts of the web UI's list (docs/plans/torrent-mockups, a-main-window.html and e-webui-phone.html):
// the Downloading / Seeding / Stopped views, the Seeds, Peers, Up and Ratio cells, a seeding row's way to its goal,
// the phone's summary line and the notice when a torrent's file list is in. Nothing here touches the page, so the
// tests call it directly. Nothing here asks the server anything: it all comes from the jobs and the settings.

import { duration, speed } from './format';
import { isActive, peersText, shortMagnet } from './torrents';
import { mayRun, ratioText } from './torrentDetails';
import { defaultSort, type SortKey, type SortState } from './list';
import type { Job, Settings, Torrent } from './types';

// ---- The views under Torrents ----

export type TorrentFilter = 'downloading' | 'seeding' | 'stopped';

export const torrentFilters: { key: TorrentFilter; label: string; icon: string }[] = [
  { key: 'downloading', label: 'Downloading', icon: 'download' },
  { key: 'seeding', label: 'Seeding', icon: 'upload' },
  { key: 'stopped', label: 'Stopped', icon: 'pause' },
];

/**
 * Which of the three a torrent is in; null for a job that is no torrent. Each torrent is in exactly one: seeding,
 * downloading (running, retrying or waiting in its queue), or stopped (paused, failed, finished and not seeding).
 */
export function torrentFilterOf(job: Pick<Job, 'state' | 'torrent'>): TorrentFilter | null {
  if (!job.torrent) return null;
  if (job.torrent.seeding === true) return 'seeding';
  if (['connecting', 'downloading', 'verifying', 'retryWaiting', 'retrying', 'queued'].includes(job.state)) return 'downloading';
  return 'stopped';
}

export interface TorrentCounts {
  all: number;
  downloading: number;
  seeding: number;
  stopped: number;
}

export function torrentCounts(jobs: Iterable<Pick<Job, 'state' | 'torrent'>>): TorrentCounts {
  const result: TorrentCounts = { all: 0, downloading: 0, seeding: 0, stopped: 0 };
  for (const job of jobs) {
    const filter = torrentFilterOf(job);
    if (!filter) continue;
    result.all++;
    result[filter]++;
  }
  return result;
}

// ---- T7: the views other torrent clients have (MainWindow.Torrents, TorrentListRules) ----

/**
 * Shown under the three only while they hold a torrent, or while they are the view shown. A torrent can be in several,
 * or in none. Built only from what the jobs carry.
 */
export type TorrentExtraView = 'active' | 'stalled' | 'checking' | 'files' | 'completed' | 'errored';

export const torrentExtraViews: { key: TorrentExtraView; label: string; icon: string }[] = [
  { key: 'active', label: 'Active', icon: 'play' },
  { key: 'stalled', label: 'Stalled', icon: 'info' },
  { key: 'checking', label: 'Checking', icon: 'refresh' },
  { key: 'files', label: 'Waiting for files', icon: 'queue' },
  { key: 'completed', label: 'Completed', icon: 'check' },
  { key: 'errored', label: 'Errored', icon: 'alert' },
];

type ViewJob = Pick<Job, 'state' | 'torrent' | 'bytesPerSecond' | 'failure'> & Partial<Pick<Job, 'startedAt'>>;

/** How long a torrent runs before it can count as stalled, so a start (finding peers) isn't one (TorrentListRules.StallGrace). */
export const stallGraceMs = 60_000;

/** What it receives now, while it downloads; 0 otherwise. */
function receiving(job: ViewJob): number {
  const rate = job.bytesPerSecond ?? 0;
  return isActive(job) && Number.isFinite(rate) && rate > 0 ? rate : 0;
}

/** What it sends now, while it downloads or seeds; 0 otherwise. */
function sending(job: ViewJob): number {
  const rate = job.torrent?.uploadRate ?? 0;
  return mayRun(job) && Number.isFinite(rate) && rate >= 1 ? rate : 0;
}

/**
 * Active: it moves data now (receives, or sends while it downloads or seeds). Stalled: it should be downloading
 * (connecting or downloading), receives nothing (under 1 B/s), has no peer connected (peers and seeds 0) and first
 * started at least a minute ago (no startedAt: no grace), apart from a file check and a wait for the file choice. The
 * peer count changes far less often than the rate, so a dip doesn't move a torrent in and out; one that sends has a
 * peer, so Active and Stalled don't overlap. Checking: the worker checks its files now. Waiting for files:
 * its file list is in and the choice is open. Completed: finished and not seeding. Errored: it failed, or the server
 * paused it because the disk is full.
 */
export function inTorrentExtraView(view: TorrentExtraView, job: ViewJob, now: number = Date.now()): boolean {
  const torrent = job.torrent;
  if (!torrent) return false;
  switch (view) {
    case 'active': return receiving(job) >= 1 || sending(job) >= 1;
    case 'stalled':
      return (job.state === 'connecting' || job.state === 'downloading') && torrent.checking !== true
        && !(torrent.hasMetadata && torrent.awaitingFileChoice) && receiving(job) < 1
        && (torrent.peers ?? 0) <= 0 && (torrent.seeds ?? 0) <= 0 && ranLongEnough(job.startedAt, now);
    case 'checking': return torrent.checking === true;
    case 'files': return torrent.hasMetadata && torrent.awaitingFileChoice && job.state !== 'completed' && job.state !== 'cancelled';
    case 'completed': return job.state === 'completed' && torrent.seeding !== true;
    case 'errored': return job.state === 'failed' || (job.state === 'paused' && job.failure?.kind === 'diskFull');
  }
}

function ranLongEnough(startedAt: string | undefined, now: number): boolean {
  const started = startedAt ? Date.parse(startedAt) : NaN;
  return !Number.isFinite(started) || now - started >= stallGraceMs;
}

export function torrentExtraCounts(jobs: Iterable<ViewJob>, now: number = Date.now()): Record<TorrentExtraView, number> {
  const result: Record<TorrentExtraView, number> = { active: 0, stalled: 0, checking: 0, files: 0, completed: 0, errored: 0 };
  for (const job of jobs) {
    if (!job.torrent) continue;
    for (const view of torrentExtraViews) if (inTorrentExtraView(view.key, job, now)) result[view.key]++;
  }
  return result;
}

/** An extra view is in the sidebar while it holds a torrent, or while it is the view shown, so it doesn't vanish under the user. */
export function extraViewShows(count: number | undefined, key: string, current: string): boolean {
  return (count ?? 0) > 0 || key === current;
}

// ---- The seeding goal (Options > Torrents) ----

export interface SeedGoal {
  /** 0 is no ratio limit. */
  ratio: number;
  /** 0 is no time limit. */
  hours: number;
}

/** The goal from the settings; the Host's defaults (ratio 1, 48 h) when the server leaves them out. */
export function seedGoalOf(settings: Pick<Settings, 'torrentSeedRatio' | 'torrentSeedHours'> | null | undefined): SeedGoal {
  const ratio = settings?.torrentSeedRatio;
  const hours = settings?.torrentSeedHours;
  return {
    ratio: Number.isFinite(ratio) && (ratio as number) >= 0 ? (ratio as number) : 1,
    hours: Number.isFinite(hours) && (hours as number) >= 0 ? (hours as number) : 48,
  };
}

/** "1.0", "1.5", "2.25": the goal's ratio as Options shows it. */
export function goalRatioText(ratio: number): string {
  return ratio.toFixed(2).replace(/0$/, '');
}

function seededHours(torrent: Pick<Torrent, 'seedingSince'>, now: number): number | null {
  const since = torrent.seedingSince ? Date.parse(torrent.seedingSince) : NaN;
  return Number.isNaN(since) ? null : Math.max(0, (now - since) / 3_600_000);
}

/** How far a seeding torrent is on its way to the goal, 0 to 100: the nearer of the ratio and the hours. */
export function goalPercent(torrent: Pick<Torrent, 'ratio' | 'seedingSince'>, goal: SeedGoal, now = Date.now()): number {
  const byRatio = goal.ratio > 0 ? Math.max(0, torrent.ratio ?? 0) / goal.ratio : 0;
  const hours = seededHours(torrent, now);
  const byHours = goal.hours > 0 && hours !== null ? hours / goal.hours : 0;
  return Math.max(0, Math.min(100, Math.max(byRatio, byHours) * 100));
}

/**
 * The Ratio cell: "0.08", or while it seeds the way to the goal that comes first: "0.71 → 1.0" by ratio, or
 * "1.38 · 31h 4m left" by time.
 */
export function ratioCell(torrent: Pick<Torrent, 'ratio' | 'seeding' | 'seedingSince'>, goal: SeedGoal, now = Date.now()): string {
  const ratio = ratioText(torrent.ratio);
  if (torrent.seeding !== true) return ratio;
  const byRatio = goal.ratio > 0 ? Math.max(0, torrent.ratio ?? 0) / goal.ratio : -1;
  const hours = seededHours(torrent, now);
  const byHours = goal.hours > 0 && hours !== null ? hours / goal.hours : -1;
  if (byRatio < 0 && byHours < 0) return ratio;
  if (byRatio >= byHours) return `${ratio} → ${goalRatioText(goal.ratio)}`;
  const left = duration(Math.max(0, Math.ceil((goal.hours - (hours ?? 0)) * 3600)));
  return left ? `${ratio} · ${left} left` : ratio;
}

// ---- The Seeds, Peers and Up cells ----

export interface SwarmCells {
  seeds: number;
  peers: number;
  up: number;
  ratio: number;
  seedsText: string;
  peersText: string;
  upText: string;
  ratioText: string;
  /** A seeding torrent's way to its goal, 0 to 100; null otherwise. */
  goal: number | null;
  /** The phone's line under the name: "41 peers (12 seeds) · ratio 0.08". */
  line: string;
}

/**
 * A torrent row's swarm cells. Peers counts every connected peer, seeds among them (the Host's TorrentDto). The
 * upload rate is known only while it seeds; a torrent that isn't running shows dashes.
 */
/**
 * T3: the goal that holds for this torrent: its own (torrent.setOptions) when it has one, the settings' otherwise; a
 * torrent set never to stop has none.
 */
export function goalFor(torrent: { seedGoal?: string | null; seedGoalValue?: number | null } | null | undefined, goal: SeedGoal): SeedGoal {
  const value = typeof torrent?.seedGoalValue === 'number' && Number.isFinite(torrent.seedGoalValue) && torrent.seedGoalValue > 0 ? torrent.seedGoalValue : 0;
  switch (torrent?.seedGoal) {
    case 'ratio': return { ratio: value, hours: 0 };
    case 'hours': return { ratio: 0, hours: value };
    case 'never': return { ratio: 0, hours: 0 };
    default: return goal;
  }
}

export function swarmCells(job: Pick<Job, 'state' | 'torrent'>, goal: SeedGoal, now = Date.now()): SwarmCells | undefined {
  const torrent = job.torrent;
  if (!torrent) return undefined;
  goal = goalFor(torrent, goal);
  const running = mayRun(job);
  const seeding = torrent.seeding === true;
  // T8: a downloading torrent sends too; its figure shows once it sends something.
  const up = running ? Math.max(0, torrent.uploadRate ?? 0) : 0;
  const seeds = Math.max(0, torrent.seeds);
  const peers = Math.max(0, torrent.peers);
  const ratio = ratioCell(torrent, goal, now);
  const swarm = running ? `${peersText(peers)} (${seeds === 1 ? '1 seed' : `${seeds} seeds`})` : '';
  return {
    seeds: running ? seeds : -1,
    peers: running ? peers : -1,
    up,
    ratio: Math.max(0, torrent.ratio ?? 0),
    seedsText: running ? String(seeds) : '–',
    peersText: running ? String(peers) : '–',
    upText: seeding ? speed(up) || '0 B/s' : up >= 1 ? speed(up) : '–',
    ratioText: ratio,
    goal: seeding && (goal.ratio > 0 || goal.hours > 0) ? goalPercent(torrent, goal, now) : null,
    line: [swarm, `ratio ${ratio}`].filter(Boolean).join(' · '),
  };
}

// ---- Sorting by the swarm columns ----

/** The columns only the torrent views have. */
export const swarmSortKeys: SortKey[] = ['seeds', 'peers', 'up', 'ratio'];

/** The More > Sort by menu: the usual columns, and in the torrent views Seeds, Peers, Up and Ratio too. */
export function sortMenuTitles(torrentCols: boolean): [SortKey, string][] {
  const titles: [SortKey, string][] = [
    ['name', 'File Name'], ['queue', 'Q'], ['size', 'Size'], ['status', 'Status'], ['extraction', 'Extraction'], ['eta', 'Time left'],
    ['rate', 'Transfer rate'], ['lastTry', 'Last Try Date'], ['description', 'Description'], ['added', 'Order of addition'],
  ];
  if (!torrentCols) return titles;
  return [...titles.slice(0, 7), ['seeds', 'Seeds'], ['peers', 'Peers'], ['up', 'Up'], ['ratio', 'Ratio'], ...titles.slice(7)];
}

/**
 * The sort a view uses: the saved one, but a view without the swarm columns doesn't sort by one of them. It uses the
 * default there and the saved sort comes back in the torrent views.
 */
export function sortForView(sort: SortState, torrentCols: boolean): SortState {
  return !torrentCols && swarmSortKeys.includes(sort.key) ? defaultSort : sort;
}

// ---- The listen port ----

/**
 * The port the torrents listen on, when the page knows it: on the Windows app the Host listens on the port in
 * Options. The Docker server listens on DOWNLOADMANAGER_TORRENT_PORT instead and doesn't report it, so nothing.
 */
export function knownListenPort(settings: Pick<Settings, 'torrentListenPort'> | null | undefined, hostKind: string | null | undefined): number {
  if (hostKind !== 'windows') return 0;
  const port = settings?.torrentListenPort ?? 0;
  return Number.isInteger(port) && port > 0 && port <= 65535 ? port : 0;
}

// ---- The phone's summary line ----

/**
 * "↓ 25 MB/s · ↑ 2.2 MB/s · port 51413": what the torrents move now, and the listen port once one is chosen. Empty
 * when no torrent moves anything and no port is known.
 */
export function torrentSummary(jobs: Iterable<Pick<Job, 'state' | 'torrent' | 'bytesPerSecond'>>, listenPort: number | null | undefined): string {
  let down = 0;
  let up = 0;
  let any = false;
  for (const job of jobs) {
    if (!job.torrent) continue;
    any = true;
    if (isActive(job)) down += Math.max(0, job.bytesPerSecond ?? 0);
    if (mayRun(job)) up += Math.max(0, job.torrent.uploadRate ?? 0);
  }
  if (!any) return '';
  const parts = [`↓ ${speed(down) || '0 B/s'}`, `↑ ${speed(up) || '0 B/s'}`];
  if (listenPort && listenPort > 0) parts.push(`port ${listenPort}`);
  return parts.join(' · ');
}

/** The status bar's chip on a phone: "2 active · 47 peers" without the "Torrents:" word; null when none runs. */
export function chipCompact(jobs: readonly Pick<Job, 'state' | 'torrent'>[]): string | null {
  const active = jobs.filter((job) => job.torrent && isActive(job));
  if (active.length === 0) return null;
  return `${active.length} active · ${peersText(active.reduce((sum, job) => sum + (job.torrent?.peers ?? 0), 0))}`;
}

// ---- Copy magnet link ----

/**
 * The magnet the row menu copies: the info hash and the name, never the trackers (a private tracker's address can
 * hold a passkey). Null when the info hash isn't known.
 */
export function magnetToCopy(torrent: Pick<Torrent, 'infoHash' | 'name'> | null | undefined): string | null {
  if (!torrent?.infoHash) return null;
  const name = torrent.name?.trim();
  return name ? `${shortMagnet(torrent)}&dn=${encodeURIComponent(name)}` : shortMagnet(torrent);
}

// ---- Ready to choose files ----

/** The torrent waits for a choice of its files now, and did not before: the page says so once. */
export function becameReadyToChoose(before: Pick<Job, 'torrent'> | null | undefined, after: Pick<Job, 'torrent' | 'state'>): boolean {
  const now = after.torrent;
  if (!now || !now.hasMetadata || !now.awaitingFileChoice) return false;
  if (after.state === 'completed' || after.state === 'cancelled') return false;
  return !(before?.torrent?.hasMetadata && before.torrent.awaitingFileChoice);
}

export function readyToChooseText(name: string): string {
  const shown = name.trim() || 'A torrent';
  return `${shown}: ready to choose files.`;
}

/** How many torrents wait for their file choice, for the badge on Torrents. */
export function waitingForChoice(jobs: Iterable<Pick<Job, 'torrent' | 'state'>>): number {
  let count = 0;
  for (const job of jobs) if (becameReadyToChoose(null, job)) count++;
  return count;
}

export function waitingForChoiceText(count: number): string {
  return count === 1 ? '1 torrent is ready to choose files' : `${count} torrents are ready to choose files`;
}

/** "Save to" in a torrent's overview: its folder. */
export function saveToText(job: Pick<Job, 'destinationDirectory'>): string {
  return job.destinationDirectory?.trim() || '—';
}

/** A torrent's "Seeding goal" in its overview, from the settings. */
export function seedGoalLine(settings: Pick<Settings, 'torrentSeedAfterDownload' | 'torrentSeedRatio' | 'torrentSeedHours'> | null | undefined, seeding: boolean, goalText: string): string {
  if (!seeding && settings?.torrentSeedAfterDownload === false) return 'No seeding after the download';
  return goalText;
}

