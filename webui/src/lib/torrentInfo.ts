// T7: Torrent Info beside the list (the desktop window's View › Torrent Info, TorrentInfoRules). On a wide window the
// one selected torrent's facts show inline on the right of the list; Progress Details stays for its files, peers,
// trackers, pieces and options. It reads only the trackers' summary (on the torrent's own updates, at most every 10 s
// while it runs) and the details (the v2 info hash) once per state until it is known: never from a timer. Nothing here touches the page.

import { bytes, dateTime, duration, speed, stateWord, timeSpanSeconds } from './format';
import { goalText, sourceText } from './torrentControls';
import { mayRun, ratioText, type TrackerDto } from './torrentDetails';
import { saveToText } from './torrentList';
import { peersText, torrentStatusText } from './torrents';
import type { Job } from './types';

/** The pane shows inline from this window width (the list's min-[1440px] rules). Narrower, it stays away and the list keeps its room. */
export const infoMinWidth = 1440;

/** How often, at most, the trackers' summary is read again while the torrent runs and the pane is seen. */
export const infoReadMs = 10_000;

export const dash = '—';

/** A known zero speed, as the desktop's Torrent Info says it (TorrentInfoRules.ZeroRate). */
export const zeroRate = `${bytes(0)}/s`;

/** The localStorage key of the pane's on/off choice; on unless turned off. */
export const infoKey = 'inbound.torrentInfo';

export function infoOn(saved: string | null): boolean {
  return saved !== 'off';
}

/** The torrent the pane shows: it is on and exactly one download is selected, a torrent. */
export function infoJob(on: boolean, selection: readonly Job[]): Job | null {
  return on && selection.length === 1 && selection[0].torrent ? selection[0] : null;
}

/** The pane is seen: the page is visible, the window wide enough, and it shows a torrent. */
export function infoSeen(state: { visible: boolean; wide: boolean; job: Job | null }): boolean {
  return state.visible && state.wide && !!state.job;
}

/**
 * The pane's room beside the list is kept: it is on, the server has torrents, the window is wide enough and the list
 * shows a torrent. It doesn't follow the selection, so the list's columns don't change from click to click; with no one
 * torrent selected the room says so. (No watch of its own: the list already has the server report its torrents.)
 */
export function infoReserved(state: { on: boolean; offered: boolean; wide: boolean; listsTorrents: boolean }): boolean {
  return state.on && state.offered && state.wide && state.listsTorrents;
}

/**
 * What a details read holds for: the torrent, its state, whether it seeds and whether it has its metadata. The server
 * gives the v2 hash only while the torrent runs, so one first shown paused is asked again once it starts.
 */
export function detailsKey(job: Pick<Job, 'id' | 'state' | 'torrent'>): string {
  return `${job.id}|${job.state}|${job.torrent?.seeding === true}|${job.torrent?.hasMetadata === true}`;
}

/** The details are read now: seen, connected, with the torrent controls, not yet for this key, and the v2 hash isn't known. */
export function detailsDue(state: { seen: boolean; connected: boolean; controls: boolean; readForThisKey: boolean; v2Known: boolean }): boolean {
  return state.seen && state.connected && state.controls && !state.readForThisKey && !state.v2Known;
}

/** What a trackers' summary holds for: the torrent and its state. A change reads it again. */
export function infoReadKey(job: Pick<Job, 'id' | 'state' | 'torrent'>): string {
  return `${job.id}|${job.state}|${job.torrent?.seeding === true}`;
}

/**
 * The trackers' summary is read now: seen, connected to a server with torrents, the torrent may run, and either its
 * state changed since the last read (a new selection too) or {@link infoReadMs} passed.
 */
export function trackersDue(state: { seen: boolean; connected: boolean; offered: boolean; job: Job | null; keyChanged: boolean; sinceMs: number }): boolean {
  return state.seen && state.connected && state.offered && !!state.job && mayRun(state.job) && (state.keyChanged || state.sinceMs >= infoReadMs);
}

/** "2 of 3 working", or what the pane says while the list isn't known or the torrent doesn't run. */
export function trackersText(trackers: readonly Pick<TrackerDto, 'status'>[] | null, running: boolean, offered: boolean): string {
  if (!offered) return dash;
  if (!running) return 'Shown while the torrent runs';
  if (!trackers) return dash;
  if (trackers.length === 0) return 'None: peers come from the DHT and other peers';
  const working = trackers.filter((tracker) => tracker.status === 'working').length;
  return `${working.toLocaleString('en-US')} of ${trackers.length.toLocaleString('en-US')} working`;
}

export interface InfoView {
  name: string;
  status: string;
  percent: number;
  progress: string;
  down: string;
  up: string;
  swarm: string;
  timeLeft: string;
  ratio: string;
  goal: string;
  trackers: string;
  folder: string;
  added: string;
  infoHash: string;
}

const active = (job: Pick<Job, 'state'>) => ['connecting', 'downloading', 'verifying'].includes(job.state);

function wanted(job: Job): number {
  if (job.totalBytes && job.totalBytes > 0) return job.totalBytes;
  return Math.max(0, job.torrent?.wantedBytes ?? 0);
}

/** How much of the wanted files is in, 0 to 100; 100 once complete. */
export function infoPercent(job: Job): number {
  if (job.state === 'completed') return 100;
  const total = wanted(job);
  return total > 0 ? Math.max(0, Math.min(100, Math.floor((job.receivedBytes * 100) / total))) : 0;
}

/** "41% · 392 MB of 1.00 GB", "100% · 1.00 GB" once complete, "Getting file list" before the metadata. */
export function progressText(job: Job): string {
  if (job.torrent && !job.torrent.hasMetadata) return 'Getting file list';
  const total = wanted(job);
  if (job.state === 'completed') return total > 0 ? `100% · ${bytes(total)}` : '100%';
  const received = bytes(Math.max(0, job.receivedBytes)) || '0 B';
  return total > 0 ? `${infoPercent(job)}% · ${received} of ${bytes(total)}` : received;
}

/** Where its files go: the save folder, else the folder of its finished path. */
export function folderText(job: Pick<Job, 'destinationDirectory' | 'finalFilePath'>): string {
  const folder = saveToText(job);
  if (folder !== dash) return folder;
  const path = job.finalFilePath?.trim();
  if (!path) return dash;
  const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return cut > 0 ? path.slice(0, cut) : path;
}

/** "v1 3f9c…" and, for a hybrid or v2 torrent, " · v2 b07d…" (TorrentControlRules.HashText). */
export function hashText(infoHash: string, infoHashV2: string | null | undefined): string {
  const v1 = infoHash || dash;
  return infoHashV2 && infoHashV2.length === 64 ? `v1 ${v1} · v2 ${infoHashV2}` : v1;
}

/** The pane's words for one torrent. */
export function describeInfo(job: Job, settings: Parameters<typeof goalText>[1], infoHashV2: string | null, trackers: readonly Pick<TrackerDto, 'status'>[] | null, offered: boolean): InfoView {
  const torrent = job.torrent!;
  const running = mayRun(job);
  const downloading = active(job);
  const seeds = Math.max(0, torrent.seeds);
  const others = Math.max(0, torrent.peers - seeds);
  return {
    name: job.fileName || torrent.name || 'Torrent',
    status: torrentStatusText(job) ?? (job.state === 'completed' ? 'Completed' : stateWord(job.state)),
    percent: infoPercent(job),
    progress: progressText(job),
    // T7.B1: a complete torrent, seeding or not, has nothing left to download: a real 0, not "—".
    down: downloading ? speed(job.bytesPerSecond) || zeroRate : job.state === 'completed' ? zeroRate : dash,
    up: running ? speed(torrent.uploadRate) || zeroRate : dash,
    swarm: running ? `${seeds === 1 ? '1 seed' : `${seeds.toLocaleString('en-US')} seeds`} · ${peersText(others)}` : dash,
    timeLeft: (downloading && duration(timeSpanSeconds(job.estimatedRemaining))) || dash,
    ratio: `${ratioText(torrent.ratio)} · uploaded ${bytes(torrent.uploaded ?? 0) || '0 B'}`,
    goal: goalText(torrent, settings),
    trackers: trackersText(trackers, running, offered),
    folder: folderText(job),
    added: [dateTime(job.createdAt), `from ${sourceText(torrent.source).toLowerCase()}`].filter(Boolean).join(' · '),
    infoHash: hashText(torrent.infoHash, infoHashV2),
  };
}
