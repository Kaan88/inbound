// T2 in the web UI: seeding, and the details' Peers, Trackers and Pieces & Speed tabs (docs/plans/torrent-mockups,
// c-torrent-details.html and e-webui-phone.html). Nothing here touches the page, so the tests call it directly.

import { bytes, duration, speed } from './format';
import type { Job, Torrent } from './types';

// ---- Seeding ----

/** "0.71": a ratio as the rows show it. */
export function ratioText(ratio: number | null | undefined): string {
  return Number.isFinite(ratio) && (ratio ?? 0) > 0 ? (ratio as number).toFixed(2) : '0.00';
}

export function isSeeding(job: Pick<Job, 'torrent'> | null | undefined): boolean {
  return job?.torrent?.seeding === true;
}

/** The Status column of a seeding torrent: "Seeding · ratio 0.71". */
export function seedingStatus(torrent: Pick<Torrent, 'ratio'>): string {
  return `Seeding · ratio ${ratioText(torrent.ratio)}`;
}

/** The Transfer rate column of a seeding torrent: "↑ 640 KB/s", or empty while nothing is sent. */
export function uploadRateText(rate: number | null | undefined): string {
  const text = speed(rate ?? 0);
  return text ? `↑ ${text}` : '';
}

/** A finished torrent that isn't seeding can be seeded again (the Host checks the rest). */
export function canStartSeeding(job: Pick<Job, 'torrent' | 'state'>): boolean {
  return !!job.torrent && job.state === 'completed' && !job.torrent.seeding;
}

export function canStopSeeding(job: Pick<Job, 'torrent'>): boolean {
  return job.torrent?.seeding === true;
}

/** The selected jobs Start seeding (on) or Stop seeding (off) acts on. */
export function seedTargets<T extends Pick<Job, 'torrent' | 'state'>>(selection: readonly T[], on: boolean): T[] {
  return selection.filter((job) => (on ? canStartSeeding(job) : canStopSeeding(job)));
}

/** What the page says once torrent.seed went through for some of them. */
export function seedSaid(on: boolean, count: number): string {
  if (on) return count === 1 ? 'Seeding started.' : `Seeding started for ${count} torrents.`;
  return count === 1 ? 'Seeding stopped.' : `Seeding stopped for ${count} torrents.`;
}

/** "Ratio 1.0 or 48 h, whichever comes first", from the settings (0 is no limit, never both). */
export function seedGoalText(ratio: number | null | undefined, hours: number | null | undefined): string {
  const r = Number.isFinite(ratio) ? (ratio as number) : 1;
  const h = Number.isFinite(hours) ? (hours as number) : 48;
  const ratioPart = r > 0 ? `ratio ${r.toFixed(2).replace(/0$/, '')}` : '';
  const hoursPart = h > 0 ? `${Math.round(h * 10) / 10} h` : '';
  if (ratioPart && hoursPart) return `${capital(ratioPart)} or ${hoursPart}, whichever comes first`;
  return capital(ratioPart || hoursPart || 'no limit');
}

function capital(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** "204 MB · ratio 0.08". */
export function uploadedText(torrent: Pick<Torrent, 'uploaded' | 'ratio'>): string {
  return `${bytes(torrent.uploaded ?? 0) || '0 B'} · ratio ${ratioText(torrent.ratio)}`;
}

// ---- The live tabs ----

export type SwarmTab = 'peers' | 'trackers' | 'pieces';

/**
 * The server knows the T3 torrent messages (torrent.options, setOptions, details, recheck, announce). The Host sends no
 * capability for them, but a T3 server always sends a torrent's seeding goal ("global" when none is set), and an older
 * one leaves it out.
 */
export function hasTorrentControls(job: Pick<Job, 'torrent'> | null | undefined): boolean {
  return typeof job?.torrent?.seedGoal === 'string';
}

/**
 * The details tabs of a torrent, in the mockup's order. T3: a torrent has its own Options (limits, seeding goal, order)
 * in place of the speed limiter; a finished one has no completion options. An older server without them (`controls`
 * false) keeps the T2 tabs: Download status and the generic speed limiter.
 */
export function torrentTabs(complete: boolean, controls = true): [string, string][] {
  if (!controls) {
    const live: [string, string][] = [['files', 'Files'], ['peers', 'Peers'], ['trackers', 'Trackers'], ['pieces', 'Pieces & Speed']];
    return complete
      ? [['status', 'Download status'], ...live]
      : [['status', 'Download status'], ...live, ['speed', 'Speed limiter'], ['completion', 'Options on completion']];
  }
  const live: [string, string][] = [['status', 'Overview'], ['files', 'Files'], ['peers', 'Peers'], ['trackers', 'Trackers'], ['pieces', 'Pieces & Speed'], ['options', 'Options']];
  return complete ? live : [...live, ['completion', 'Options on completion']];
}

/** How often an open tab asks again. */
export const swarmPollMs = 2000;

/**
 * The torrent may be on the engine now: downloading, or seeding. Otherwise the tab says "Not running" without asking,
 * so a stopped torrent's details wake nothing.
 */
export function mayRun(job: Pick<Job, 'state' | 'torrent'>): boolean {
  if (!job.torrent) return false;
  return job.torrent.seeding === true || ['connecting', 'downloading', 'verifying'].includes(job.state);
}

/** One ask now: the tab is shown, the page is visible, the server is reachable, the torrent may run and none is out. */
export function shouldAsk(state: { visible: boolean; connected: boolean; running: boolean; busy: boolean }): boolean {
  return state.visible && state.connected && state.running && !state.busy;
}

export const notRunningText = 'Not running. Peers, trackers and pieces show while the torrent downloads or seeds.';

// ---- Peers ----

export interface PeerDto {
  address: string;
  port: number;
  client: string;
  flags?: string[];
  downloadRate: number;
  uploadRate: number;
  downloaded?: number;
  uploaded?: number;
  progress: number;
}

export interface PeerRow {
  key: string;
  /** "Peer 3 · IPv4": the address shows only on hover (the mockup's privacy rule). */
  label: string;
  /** "203.0.113.5:51413". */
  address: string;
  client: string;
  flags: string;
  has: string;
  down: string;
  up: string;
  /** UI.R42: what came from this peer and went to it since it connected. */
  received: string;
  sent: string;
}

export function ipFamily(address: string): 'IPv6' | 'IPv4' {
  return address.includes(':') ? 'IPv6' : 'IPv4';
}

/** "uTP · encrypted · incoming · seed": how the peer is connected, in words. */
export function peerFlagsText(flags: readonly string[] | null | undefined): string {
  const set = new Set(flags ?? []);
  const parts = [set.has('utp') ? 'uTP' : 'TCP'];
  if (set.has('encrypted')) parts.push('encrypted');
  if (set.has('incoming')) parts.push('incoming');
  if (set.has('seed')) parts.push('seed');
  if (set.has('snubbed')) parts.push('snubbed');
  return parts.join(' · ');
}

function percentText(fraction: number): string {
  if (!Number.isFinite(fraction) || fraction <= 0) return '0%';
  if (fraction >= 1) return '100%';
  return `${Math.min(99, Math.floor(fraction * 100))}%`;
}

/** "203.0.113.5:51413" or "[2001:db8::1]:51413": a peer's address, and its row's key. */
export function peerAddress(peer: Pick<PeerDto, 'address' | 'port'>): string {
  return ipFamily(peer.address) === 'IPv6' ? `[${peer.address}]:${peer.port}` : `${peer.address}:${peer.port}`;
}

/** The peers in the order the mockup lists them: fastest down first, then fastest up. */
/**
 * The peers busiest first. With `numbers` (kept by the caller across reads, as the Windows app does), a peer keeps the
 * number it got when first seen, so "Peer 3" stays the same peer and ties keep their order; without, peers are numbered
 * by their place.
 */
export function peerRows(peers: readonly PeerDto[], numbers?: Map<string, number>): PeerRow[] {
  if (numbers) {
    // A torrent open for days meets many peers: the numbers start again rather than grow without end.
    if (numbers.size > 5000) numbers.clear();
    for (const peer of peers) {
      const key = peerAddress(peer);
      if (!numbers.has(key)) numbers.set(key, numbers.size + 1);
    }
  }
  const numberOf = (peer: PeerDto) => numbers?.get(peerAddress(peer)) ?? 0;
  return [...peers]
    .sort((a, b) => (b.downloadRate - a.downloadRate) || (b.uploadRate - a.uploadRate) || (numberOf(a) - numberOf(b)))
    .map((peer, i) => {
      const address = peerAddress(peer);
      return {
        key: address,
        label: `Peer ${numbers ? numberOf(peer) : i + 1} · ${ipFamily(peer.address)}`,
        address,
        client: peer.client?.trim() || 'Unknown',
        flags: peerFlagsText(peer.flags),
        has: percentText(peer.progress),
        down: speed(peer.downloadRate) || '0',
        up: speed(peer.uploadRate) || '0',
        received: bytes(Math.max(0, peer.downloaded ?? 0)),
        sent: bytes(Math.max(0, peer.uploaded ?? 0)),
      };
    });
}

export function peersCount(count: number): string {
  return count === 1 ? '1 peer connected' : `${count} peers connected`;
}

// ---- Trackers ----

export interface TrackerDto {
  url: string;
  tier: number;
  status: string;
  message?: string;
  seeds: number;
  peers: number;
  /** T3: seconds to the next announce (0: due now); -1 when none is planned. Older servers leave it out. */
  nextAnnounce?: number;
}

export interface TrackerRow {
  key: string;
  url: string;
  status: string;
  failed: boolean;
  seeds: string;
  peers: string;
  /** "Now", "24m 0s" or "–". */
  next: string;
  /** UI.R42: the tracker's tier, and what it said last with any address cut short. */
  tier: string;
  message: string;
}

/** What a tracker said, with any address in it cut before a passkey (trackerShown). */
export function trackerMessageText(message: string | null | undefined): string {
  return (message ?? '').trim().replace(/[a-z][a-z0-9+.-]*:\/\/\S+/gi, (address) => trackerShown(address));
}

/** "Now", "24m 0s", or '' when none is planned (-1) or it isn't known. */
export function nextAnnounceText(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) return '';
  return seconds === 0 ? 'Now' : duration(seconds);
}

/**
 * A tracker's address as it may be shown: the scheme, host and port, and the path up to a part that looks like a
 * passkey (16 or more letters and digits), with no query. A private tracker's key is never shown on the page.
 */
export function trackerShown(url: string): string {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return 'A tracker';
  }
  const kept: string[] = [];
  let cut = false;
  for (const part of parsed.pathname.split('/').filter(Boolean)) {
    if (/[A-Za-z0-9]{16,}/.test(part) || /key|pass|token|auth/i.test(part)) {
      cut = true;
      break;
    }
    kept.push(part);
  }
  const host = `${parsed.protocol}//${parsed.host}`;
  const path = kept.length ? `/${kept.join('/')}` : '';
  return cut || parsed.search ? `${host}${path}/…` : `${host}${path}`;
}

export function trackerStatusText(status: string, message?: string | null): string {
  const words: Record<string, string> = { notContacted: 'Not contacted', updating: 'Updating', working: 'Working', error: 'Error' };
  const word = words[status] ?? (status || 'Not contacted');
  const said = message?.trim();
  return status === 'error' && said ? `${word}: ${said}` : word;
}

function countText(value: number): string {
  return Number.isFinite(value) && value >= 0 ? value.toLocaleString('en-US') : '–';
}

export function trackerRows(trackers: readonly TrackerDto[]): TrackerRow[] {
  return trackerRowsInOrder([...trackers].sort((a, b) => a.tier - b.tier));
}

/**
 * The rows for trackers already in the order to show. A row's key is its address (T12: a sorted list keeps each row on
 * its own tracker), with a number after an address that is there twice.
 */
export function trackerRowsInOrder(trackers: readonly TrackerDto[]): TrackerRow[] {
  const seen = new Map<string, number>();
  return trackers.map((tracker) => {
    const times = (seen.get(tracker.url) ?? 0) + 1;
    seen.set(tracker.url, times);
    return {
      key: times === 1 ? tracker.url : `${tracker.url}#${times}`,
      url: trackerShown(tracker.url),
      status: trackerStatusText(tracker.status, tracker.message),
      failed: tracker.status === 'error',
      seeds: countText(tracker.seeds),
      peers: countText(tracker.peers),
      next: nextAnnounceText(tracker.nextAnnounce) || '–',
      tier: Number.isFinite(tracker.tier) ? String(Math.max(0, tracker.tier)) : '',
      message: trackerMessageText(tracker.message),
    };
  });
}

// ---- Pieces ----

export interface PiecesDto {
  running: boolean;
  count: number;
  pieceLength: number;
  have: number;
  ranges?: number[][] | null;
  bitmap?: string | null;
}

/** Whether each piece is had, from the runs (first and last, inclusive) or the bitmap (piece 0 in the top bit). */
export function piecesHad(pieces: Pick<PiecesDto, 'count' | 'ranges' | 'bitmap'>): Uint8Array {
  const count = Math.max(0, Math.floor(pieces.count || 0));
  const had = new Uint8Array(count);
  if (pieces.ranges) {
    for (const run of pieces.ranges) {
      if (!Array.isArray(run) || run.length < 2) continue;
      const first = Math.max(0, Math.floor(run[0]));
      const last = Math.min(count - 1, Math.floor(run[1]));
      for (let i = first; i <= last; i++) had[i] = 1;
    }
    return had;
  }
  if (pieces.bitmap) {
    let raw = '';
    try {
      raw = atob(pieces.bitmap);
    } catch {
      return had;
    }
    for (let i = 0; i < count; i++) {
      const byte = raw.charCodeAt(i >> 3);
      if (Number.isNaN(byte)) break;
      if (byte & (0x80 >> (i & 7))) had[i] = 1;
    }
  }
  return had;
}

/**
 * The pieces bar: each of at most `cells` cells holds about the same number of pieces, and its value is the share
 * of them that is had (0 to 1).
 */
export function pieceCells(had: Uint8Array, cells: number): number[] {
  const count = had.length;
  if (count === 0) return [];
  const n = Math.max(1, Math.min(cells, count));
  const out: number[] = [];
  for (let c = 0; c < n; c++) {
    const from = Math.floor((c * count) / n);
    const to = Math.max(from + 1, Math.floor(((c + 1) * count) / n));
    let have = 0;
    for (let i = from; i < to; i++) have += had[i];
    out.push(have / (to - from));
  }
  return out;
}

/** "10,124 of 15,820 pieces · 256 KB each". */
export function piecesSummary(pieces: Pick<PiecesDto, 'count' | 'have' | 'pieceLength'>): string {
  const size = bytes(pieces.pieceLength);
  return `${pieces.have.toLocaleString('en-US')} of ${pieces.count.toLocaleString('en-US')} pieces${size ? ` · ${size} each` : ''}`;
}

/** "Each cell is about 40 pieces", when a cell holds more than one. */
export function cellNote(count: number, cells: number): string {
  if (count <= cells || cells <= 0) return '';
  return `Each cell is about ${Math.round(count / cells).toLocaleString('en-US')} pieces`;
}

// ---- Speed samples (the Pieces & Speed tab, while it is open) ----

export interface SpeedPoint {
  down: number;
  up: number;
}

/** The most samples the small graph keeps: two minutes at one every 2 s. */
export const mostSpeedPoints = 60;

export function addSpeedPoint(points: readonly SpeedPoint[], point: SpeedPoint): SpeedPoint[] {
  const next = [...points, { down: Math.max(0, point.down || 0), up: Math.max(0, point.up || 0) }];
  return next.length > mostSpeedPoints ? next.slice(next.length - mostSpeedPoints) : next;
}

/** An SVG polyline's points for one series, in a box of `width` by `height`, the top being the largest value of both. */
export function speedLine(points: readonly SpeedPoint[], pick: (point: SpeedPoint) => number, width: number, height: number): string {
  if (points.length === 0) return '';
  const top = Math.max(1, ...points.map((p) => Math.max(p.down, p.up)));
  const step = points.length > 1 ? width / (mostSpeedPoints - 1) : 0;
  const start = width - step * (points.length - 1);
  return points.map((p, i) => `${(start + i * step).toFixed(1)},${(height - (pick(p) / top) * height).toFixed(1)}`).join(' ');
}
