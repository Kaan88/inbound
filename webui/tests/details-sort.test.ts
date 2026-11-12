import { describe, expect, it } from 'vitest';
import {
  ariaSort, heldOrder, nextColumnSort, parseColumnSort, peerSortKeys, sortedPeerRows, sortedTrackerRows, sortMark, trackerSortKeys,
  type ColumnSort, type PeerSortKey, type TrackerSortKey,
} from '../src/lib/detailsSort';
import type { PeerDto, TrackerDto } from '../src/lib/torrentDetails';

// T12: the details' Peers and Trackers tables sort as Progress Details' do in the Windows app (DetailsSortTests).

describe('a header click', () => {
  it('starts in the column\'s usual direction, reverses, then goes back to the table\'s own order', () => {
    let sort: ColumnSort<PeerSortKey> = { key: null, ascending: false };
    sort = nextColumnSort(sort, 'client');
    expect(sort).toEqual({ key: 'client', ascending: true });
    sort = nextColumnSort(sort, 'client');
    expect(sort).toEqual({ key: 'client', ascending: false });
    sort = nextColumnSort(sort, 'client');
    expect(sort).toEqual({ key: null, ascending: false });
    // Speeds and counts start from the most.
    expect(nextColumnSort(sort, 'down')).toEqual({ key: 'down', ascending: false });
    expect(nextColumnSort({ key: 'down', ascending: false }, 'up')).toEqual({ key: 'up', ascending: false });
    expect(nextColumnSort<TrackerSortKey>({ key: null, ascending: false }, 'next')).toEqual({ key: 'next', ascending: true });
  });

  it('is kept as JSON, and anything else is the table\'s own order', () => {
    expect(parseColumnSort(JSON.stringify({ key: 'has', ascending: true }), peerSortKeys)).toEqual({ key: 'has', ascending: true });
    expect(parseColumnSort(JSON.stringify({ key: 'nope', ascending: true }), peerSortKeys)).toEqual({ key: null, ascending: false });
    expect(parseColumnSort('{ broken', trackerSortKeys)).toEqual({ key: null, ascending: false });
    expect(parseColumnSort(null, trackerSortKeys)).toEqual({ key: null, ascending: false });
  });

  it('marks the sorted column for the eye and for a screen reader', () => {
    const sort: ColumnSort<PeerSortKey> = { key: 'down', ascending: false };
    expect(sortMark(sort, 'down')).toBe('▼');
    expect(sortMark(sort, 'up')).toBe('');
    expect(ariaSort(sort, 'down')).toBe('descending');
    expect(ariaSort({ ...sort, ascending: true }, 'down')).toBe('ascending');
    expect(ariaSort(sort, 'peer')).toBe('none');
  });
});

const peer = (address: string, down: number, client = '', progress = 0, up = 0): PeerDto =>
  ({ address, port: 6881, client, downloadRate: down, uploadRate: up, progress, flags: [] });

describe('peers', () => {
  const peers = [
    peer('10.0.0.1', 900, 'qBittorrent 4.6', 0.5),
    peer('10.0.0.2', 2_000_000, '', 1),
    peer('10.0.0.3', 0, 'Deluge 2', 0.1, 5_000),
  ];
  const order = (sort: ColumnSort<PeerSortKey>) => sortedPeerRows(peers, sort).map((row) => row.key.split(':')[0].slice(-1));

  it('are busiest first until a column is chosen, and speeds sort as numbers', () => {
    expect(order({ key: null, ascending: false })).toEqual(['2', '1', '3']);
    expect(order({ key: 'down', ascending: false })).toEqual(['2', '1', '3']);
    expect(order({ key: 'down', ascending: true })).toEqual(['3', '1', '2']);
    expect(order({ key: 'up', ascending: false })).toEqual(['3', '2', '1']);
    expect(order({ key: 'has', ascending: false })).toEqual(['2', '1', '3']);
  });

  it('keep their numbers when sorted, and a peer with no client stays last', () => {
    const rows = sortedPeerRows(peers, { key: 'client', ascending: true });
    expect(rows.map((row) => row.label)).toEqual(['Peer 3 · IPv4', 'Peer 2 · IPv4', 'Peer 1 · IPv4']);
    expect(order({ key: 'client', ascending: false })).toEqual(['1', '3', '2']);
    expect(order({ key: 'peer', ascending: false })).toEqual(['3', '1', '2']);
  });

  it('keep the number they were first seen with across reads, and sort by it, as in the Windows app', () => {
    const numbers = new Map<string, number>();
    const first = sortedPeerRows(peers, { key: null, ascending: false }, numbers);
    expect(first.map((row) => row.label)).toEqual(['Peer 2 · IPv4', 'Peer 1 · IPv4', 'Peer 3 · IPv4']);
    // Next read: rates changed and a new peer came; nobody is renumbered and the Peer sort doesn't move.
    const later = [peer('10.0.0.4', 9_000_000), peer('10.0.0.1', 0, 'qBittorrent 4.6'), peer('10.0.0.2', 5), peer('10.0.0.3', 7)];
    expect(sortedPeerRows(later, { key: 'peer', ascending: true }, numbers).map((row) => row.label))
      .toEqual(['Peer 1 · IPv4', 'Peer 2 · IPv4', 'Peer 3 · IPv4', 'Peer 4 · IPv4']);
    expect(sortedPeerRows(later, { key: null, ascending: false }, numbers).map((row) => row.label))
      .toEqual(['Peer 4 · IPv4', 'Peer 3 · IPv4', 'Peer 2 · IPv4', 'Peer 1 · IPv4']);
  });
});

describe('trackers', () => {
  const trackers: TrackerDto[] = [
    { url: 'udp://b.example:80/announce', tier: 1, status: 'working', seeds: 12, peers: 3, nextAnnounce: 1800 },
    { url: 'udp://a.example:80/announce', tier: 0, status: 'error', seeds: -1, peers: -1, nextAnnounce: -1 },
    { url: 'https://c.example/announce', tier: 0, status: 'updating', seeds: 100, peers: 40, nextAnnounce: 0 },
  ];
  const order = (sort: ColumnSort<TrackerSortKey>) => sortedTrackerRows(trackers, sort).map((row) => row.url.split('://')[1][0]);

  it('keep their tiers until sorted, and unknown counts and times stay last', () => {
    expect(order({ key: null, ascending: false })).toEqual(['a', 'c', 'b']);
    expect(order({ key: 'seeds', ascending: false })).toEqual(['c', 'b', 'a']);
    expect(order({ key: 'seeds', ascending: true })).toEqual(['b', 'c', 'a']);
    expect(order({ key: 'next', ascending: true })).toEqual(['c', 'b', 'a']);
    expect(order({ key: 'next', ascending: false })).toEqual(['b', 'c', 'a']);
    // By host: the scheme doesn't decide.
    expect(order({ key: 'tracker', ascending: true })).toEqual(['a', 'b', 'c']);
  });

  it('are keyed by address, so a sorted row stays on its tracker', () => {
    const rows = sortedTrackerRows([...trackers, trackers[0]], { key: null, ascending: false });
    expect(rows.map((row) => row.key)).toEqual([
      'udp://a.example:80/announce', 'https://c.example/announce', 'udp://b.example:80/announce', 'udp://b.example:80/announce#2',
    ]);
  });
});

describe('a held table', () => {
  it('keeps its rows where they are, drops the gone and adds the new after them', () => {
    expect(heldOrder(['c', 'a', 'gone', 'b'], ['a', 'new2', 'b', 'c', 'new1'], (key) => key)).toEqual(['c', 'a', 'b', 'new2', 'new1']);
    expect(heldOrder([], ['a', 'b'], (key) => key)).toEqual(['a', 'b']);
  });
});
