// T12 in the web UI: the details' Peers and Trackers tables sort by a click on their headers, as Progress Details does in
// the Windows app (DetailsSort.cs, TorrentDetailsSort): a first click sorts a column in its usual direction, a second
// reverses it, a third goes back to the table's own order (busiest peers first, trackers by tier). Blank cells stay last
// whichever way, equal rows keep their order, and while the pointer is over a table a refresh doesn't move its rows.
// Nothing here touches the page, so the tests call it directly.

import { peerAddress, peerFlagsText, peerRows, trackerRowsInOrder, trackerShown, trackerStatusText, type PeerDto, type PeerRow, type TrackerDto, type TrackerRow } from './torrentDetails';

export type PeerSortKey = 'peer' | 'client' | 'flags' | 'has' | 'down' | 'up' | 'received' | 'sent';
export type TrackerSortKey = 'tracker' | 'status' | 'seeds' | 'peers' | 'next' | 'tier' | 'message';

export const peerSortKeys: readonly PeerSortKey[] = ['peer', 'client', 'flags', 'has', 'down', 'up', 'received', 'sent'];
export const trackerSortKeys: readonly TrackerSortKey[] = ['tracker', 'status', 'seeds', 'peers', 'next', 'tier', 'message'];

/** A table's order: a column and its direction, or null for the table's own order. */
export interface ColumnSort<K extends string> {
  key: K | null;
  ascending: boolean;
}

/** Words and times read from the first; what a peer has, speeds and counts from the most (as in the download list). */
export function startsAscending(key: string): boolean {
  return ['peer', 'client', 'flags', 'tracker', 'status', 'next', 'tier', 'message'].includes(key);
}

export function nextColumnSort<K extends string>(current: ColumnSort<K>, clicked: K): ColumnSort<K> {
  if (current.key !== clicked) return { key: clicked, ascending: startsAscending(clicked) };
  return current.ascending === startsAscending(clicked) ? { key: clicked, ascending: !current.ascending } : { key: null, ascending: false };
}

/** A saved sort, or the table's own order when it is missing or not one of <paramref name="keys"/>. */
export function parseColumnSort<K extends string>(saved: string | null, keys: readonly K[]): ColumnSort<K> {
  try {
    const value = JSON.parse(saved ?? 'null') as { key?: unknown; ascending?: unknown } | null;
    if (value && keys.includes(value.key as K) && typeof value.ascending === 'boolean') return { key: value.key as K, ascending: value.ascending };
  } catch {
    // A broken value is the table's own order.
  }
  return { key: null, ascending: false };
}

export function ariaSort<K extends string>(sort: ColumnSort<K>, key: K): 'ascending' | 'descending' | 'none' {
  return sort.key === key ? (sort.ascending ? 'ascending' : 'descending') : 'none';
}

export function sortMark<K extends string>(sort: ColumnSort<K>, key: K): string {
  return sort.key === key ? (sort.ascending ? '▲' : '▼') : '';
}

const natural = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });

/** Stable: equal rows keep the order they came in. Rows <paramref name="blank"/> says are blank stay last either way. */
function orderBy<T>(items: readonly T[], compare: (a: T, b: T) => number, ascending: boolean, blank?: (item: T) => boolean): T[] {
  const filled = items.filter((item) => !blank?.(item));
  const empty = items.filter((item) => blank?.(item));
  const sorted = [...filled].sort((a, b) => (ascending ? compare(a, b) : compare(b, a)));
  return [...sorted, ...empty];
}

const byNumber = <T>(value: (item: T) => number) => (a: T, b: T) => value(a) - value(b);
const byText = <T>(value: (item: T) => string) => (a: T, b: T) => natural(value(a), value(b));

/** The peers' rows in the table's own order (busiest first, numbered so) or by a column. */
export function sortedPeerRows(peers: readonly PeerDto[], sort: ColumnSort<PeerSortKey>, numbers?: Map<string, number>): PeerRow[] {
  const rows = peerRows(peers, numbers);
  if (sort.key === null) return rows;
  const byKey = new Map(peers.map((peer) => [peerAddress(peer), peer]));
  // By the number a peer got when first seen (as the Windows app does), or by its place when no numbers are kept.
  const place = numbers ?? new Map(rows.map((row, at) => [row.key, at]));
  const peer = (row: PeerRow) => byKey.get(row.key) as PeerDto;
  const fraction = (row: PeerRow) => (Number.isFinite(peer(row).progress) ? Math.min(1, Math.max(0, peer(row).progress)) : 0);
  switch (sort.key) {
    case 'peer': return orderBy(rows, byNumber((row) => place.get(row.key) ?? 0), sort.ascending);
    case 'client': return orderBy(rows, byText((row) => peer(row).client?.trim() ?? ''), sort.ascending, (row) => !peer(row).client?.trim());
    case 'flags': return orderBy(rows, byText((row) => peerFlagsText(peer(row).flags)), sort.ascending);
    case 'has': return orderBy(rows, byNumber(fraction), sort.ascending);
    case 'down': return orderBy(rows, byNumber((row) => peer(row).downloadRate), sort.ascending);
    case 'up': return orderBy(rows, byNumber((row) => peer(row).uploadRate), sort.ascending);
    case 'received': return orderBy(rows, byNumber((row) => peer(row).downloaded ?? 0), sort.ascending);
    case 'sent': return orderBy(rows, byNumber((row) => peer(row).uploaded ?? 0), sort.ascending);
  }
}

/** A tracker by its host, then as shown: "udp://" and "https://" don't decide the order. */
function trackerName(url: string): string {
  try {
    return `${new URL(url).hostname} ${trackerShown(url)}`;
  } catch {
    return trackerShown(url);
  }
}

/** The trackers' rows by tier (in the order the server gave them within one) or by a column. */
export function sortedTrackerRows(trackers: readonly TrackerDto[], sort: ColumnSort<TrackerSortKey>): TrackerRow[] {
  const byTier = [...trackers].sort((a, b) => a.tier - b.tier);
  const next = (tracker: TrackerDto) => tracker.nextAnnounce ?? -1;
  let ordered: TrackerDto[];
  switch (sort.key) {
    case 'tracker': ordered = orderBy(byTier, byText((tracker) => trackerName(tracker.url)), sort.ascending); break;
    case 'status': ordered = orderBy(byTier, byText((tracker) => trackerStatusText(tracker.status)), sort.ascending); break;
    case 'seeds': ordered = orderBy(byTier, byNumber((tracker) => tracker.seeds), sort.ascending, (tracker) => !(tracker.seeds >= 0)); break;
    case 'peers': ordered = orderBy(byTier, byNumber((tracker) => tracker.peers), sort.ascending, (tracker) => !(tracker.peers >= 0)); break;
    case 'next': ordered = orderBy(byTier, byNumber(next), sort.ascending, (tracker) => !(next(tracker) >= 0)); break;
    case 'tier': ordered = orderBy(byTier, byNumber((tracker) => tracker.tier), sort.ascending); break;
    case 'message': ordered = orderBy(byTier, byText((tracker) => tracker.message?.trim() ?? ''), sort.ascending, (tracker) => !tracker.message?.trim()); break;
    default: ordered = byTier;
  }
  return trackerRowsInOrder(ordered);
}

/**
 * While the pointer is over a table: the rows keep the places they have, rows that went are left out, and new rows go
 * after them in sorted order. The sorted order comes back once the pointer leaves.
 */
export function heldOrder<T>(shownKeys: readonly string[], sorted: readonly T[], key: (item: T) => string): T[] {
  const byKey = new Map<string, T>();
  for (const item of sorted) if (!byKey.has(key(item))) byKey.set(key(item), item);
  const taken = new Set<string>();
  const result: T[] = [];
  for (const shown of shownKeys) {
    const item = byKey.get(shown);
    if (item !== undefined && !taken.has(shown)) {
      taken.add(shown);
      result.push(item);
    }
  }
  for (const item of sorted) {
    if (!taken.has(key(item))) {
      taken.add(key(item));
      result.push(item);
    }
  }
  return result;
}
