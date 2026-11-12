import listScreenSource from '../src/screens/ListScreen.svelte?raw';
import paneSource from '../src/components/TorrentInfoPane.svelte?raw';
import { describe, expect, it } from 'vitest';
import { bytes } from '../src/lib/format';
import {
  dash, describeInfo, detailsDue, detailsKey, folderText, hashText, infoJob, infoMinWidth, infoOn, infoPercent, infoReadKey, infoReadMs, infoReserved, infoSeen,
  progressText, trackersDue, trackersText, zeroRate,
} from '../src/lib/torrentInfo';
import type { Job, Settings, Torrent } from '../src/lib/types';

const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';

function torrent(extra: Partial<Torrent> = {}): Torrent {
  return {
    infoHash: hash, name: 'Big Buck Bunny', hasMetadata: true, awaitingFileChoice: false, fileCount: 1, wantedCount: 1, totalBytes: 4096, wantedBytes: 4096,
    peers: 10, seeds: 3, ratio: 0.5, uploaded: 2048, uploadRate: 1024, ...extra,
  };
}

function tjob(state: string, t: Partial<Torrent> = {}, extra: Partial<Job> = {}): Job {
  return {
    id: `job-${state}`, url: `magnet:?xt=urn:btih:${hash}`, fileName: 'bbb.mkv', destinationDirectory: '/data/downloads', finalFilePath: '/data/downloads/bbb.mkv',
    receivedBytes: 2048, totalBytes: 4096, bytesPerSecond: 2048, estimatedRemaining: '00:04:00', state,
    createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', queueId: 0, torrent: torrent(t), ...extra,
  };
}

const plain: Job = { ...tjob('downloading'), id: 'plain', torrent: undefined };

const settings = { torrentSeedAfterDownload: true, torrentSeedRatio: 2, torrentSeedHours: 0 } as Settings;

describe('when Torrent Info shows (T7)', () => {
  it('shows for exactly one selected torrent while it is on', () => {
    const one = tjob('downloading');
    expect(infoJob(true, [one])).toBe(one);
    expect(infoJob(false, [one])).toBeNull();
    expect(infoJob(true, [])).toBeNull();
    expect(infoJob(true, [one, tjob('paused')])).toBeNull();
    expect(infoJob(true, [plain])).toBeNull();
  });

  it('is on unless it was turned off in this browser', () => {
    expect(infoOn(null)).toBe(true);
    expect(infoOn('on')).toBe(true);
    expect(infoOn('off')).toBe(false);
  });

  it('is seen only on a visible, wide page with a torrent', () => {
    const one = tjob('downloading');
    expect(infoSeen({ visible: true, wide: true, job: one })).toBe(true);
    expect(infoSeen({ visible: false, wide: true, job: one })).toBe(false);
    expect(infoSeen({ visible: true, wide: false, job: one })).toBe(false);
    expect(infoSeen({ visible: true, wide: true, job: null })).toBe(false);
  });

  it('keeps its room while the list shows torrents, whatever is selected', () => {
    const room = { on: true, offered: true, wide: true, listsTorrents: true };
    expect(infoReserved(room)).toBe(true);
    expect(infoReserved({ ...room, on: false })).toBe(false);
    expect(infoReserved({ ...room, offered: false })).toBe(false);
    expect(infoReserved({ ...room, wide: false })).toBe(false);
    expect(infoReserved({ ...room, listsTorrents: false })).toBe(false);
  });

  it('has no watch of its own: the list has the server report its torrents', () => {
    expect(listScreenSource).toMatch(/watchTorrents\(\(\) => rows\.some\(\(row\) => row\.kind === 'job' && !!row\.job\.torrent\)/);
    expect(paneSource).not.toContain('watchTorrents');
  });
});

describe('when Torrent Info reads (T7)', () => {
  const base = { seen: true, connected: true, offered: true, job: tjob('downloading'), keyChanged: false, sinceMs: 0 };

  it('reads the trackers on a change, then at most every 10 s while it runs', () => {
    expect(trackersDue({ ...base, keyChanged: true })).toBe(true);
    expect(trackersDue({ ...base, sinceMs: infoReadMs - 1 })).toBe(false);
    expect(trackersDue({ ...base, sinceMs: infoReadMs })).toBe(true);
  });

  it('reads nothing while unseen, offline, without torrents, or for a torrent that does not run', () => {
    expect(trackersDue({ ...base, keyChanged: true, seen: false })).toBe(false);
    expect(trackersDue({ ...base, keyChanged: true, connected: false })).toBe(false);
    expect(trackersDue({ ...base, keyChanged: true, offered: false })).toBe(false);
    expect(trackersDue({ ...base, keyChanged: true, job: tjob('paused') })).toBe(false);
    expect(trackersDue({ ...base, keyChanged: true, job: tjob('completed') })).toBe(false);
    expect(trackersDue({ ...base, keyChanged: true, job: null })).toBe(false);
  });

  it('keys a read by the torrent and its state', () => {
    const one = tjob('downloading');
    expect(infoReadKey(one)).toBe(infoReadKey({ ...one }));
    expect(infoReadKey(one)).not.toBe(infoReadKey({ ...one, state: 'paused' }));
    expect(infoReadKey(tjob('completed'))).not.toBe(infoReadKey(tjob('completed', { seeding: true })));
  });

  it('reads the details once per state until the v2 hash is known', () => {
    const due = { seen: true, connected: true, controls: true, readForThisKey: false, v2Known: false };
    expect(detailsDue(due)).toBe(true);
    expect(detailsDue({ ...due, readForThisKey: true })).toBe(false);
    expect(detailsDue({ ...due, v2Known: true })).toBe(false);
    expect(detailsDue({ ...due, controls: false })).toBe(false);
    expect(detailsDue({ ...due, seen: false })).toBe(false);
    expect(detailsDue({ ...due, connected: false })).toBe(false);
  });

  it('asks a torrent first shown paused for its details again once it runs', () => {
    // The server gives the v2 hash only while the torrent runs.
    const paused = tjob('paused');
    const readWhilePaused = detailsKey(paused);
    expect(detailsKey({ ...paused })).toBe(readWhilePaused);
    expect(detailsKey({ ...paused, state: 'downloading' })).not.toBe(readWhilePaused);
    expect(detailsKey(tjob('downloading', { hasMetadata: false }))).not.toBe(detailsKey(tjob('downloading')));
    expect(paneSource).toMatch(/readForThisKey: detailsFor === readFor/);
  });

  it('never reads on a timer of its own', () => {
    expect(paneSource).not.toMatch(/setInterval|setTimeout/);
  });
});

describe('what Torrent Info says (T7)', () => {
  it('counts the working trackers', () => {
    const list = [{ status: 'working' }, { status: 'error' }, { status: 'working' }, { status: 'updating' }];
    expect(trackersText(list, true, true)).toBe('2 of 4 working');
    expect(trackersText([], true, true)).toBe('None: peers come from the DHT and other peers');
    expect(trackersText(null, true, true)).toBe(dash);
    expect(trackersText(list, false, true)).toBe('Shown while the torrent runs');
    expect(trackersText(list, true, false)).toBe(dash);
  });

  it('gives a downloading torrent its speeds, swarm and time left', () => {
    const view = describeInfo(tjob('downloading'), settings, null, null, true);
    expect(view.name).toBe('bbb.mkv');
    expect(view.status).toBe('Downloading · 10 peers');
    expect(view.percent).toBe(50);
    expect(view.progress).toBe(`50% · ${bytes(2048)} of ${bytes(4096)}`);
    expect(view.down).toBe(`${bytes(2048)}/s`);
    expect(view.up).toBe(`${bytes(1024)}/s`);
    expect(view.swarm).toBe('3 seeds · 7 peers');
    expect(view.timeLeft).toBe('4m 0s');
    expect(view.ratio).toBe(`0.50 · uploaded ${bytes(2048)}`);
    expect(view.folder).toBe('/data/downloads');
    expect(view.infoHash).toBe(hash);
    expect(view.added).toMatch(/· from a magnet link$/);
    expect(view.trackers).toBe(dash);
  });

  it('gives a paused torrent no live figures', () => {
    const view = describeInfo(tjob('paused'), settings, null, null, true);
    expect(view.status).toBe('Stopped');
    expect([view.down, view.up, view.swarm, view.timeLeft]).toEqual([dash, dash, dash, dash]);
    expect(view.trackers).toBe('Shown while the torrent runs');
  });

  it('gives a seeding torrent its upload, ratio and goal', () => {
    const view = describeInfo(tjob('completed', { seeding: true }, { receivedBytes: 4096 }), settings, null, [{ status: 'working' }], true);
    expect(view.status).toBe('Seeding · ratio 0.50');
    expect(view.progress).toBe(`100% · ${bytes(4096)}`);
    // T7.B1: nothing is left to download, which is a real 0, not "—".
    expect(view.down).toBe('0 B/s');
    expect(view.up).toBe(`${bytes(1024)}/s`);
    expect(view.timeLeft).toBe(dash);
    expect(view.goal).toContain('ratio 2');
    expect(view.trackers).toBe('1 of 1 working');
  });

  it('says 0 B/s for a known zero speed and "—" only when it is not known (T7.B1)', () => {
    expect(zeroRate).toBe('0 B/s');
    const idle = describeInfo(tjob('completed', { seeding: true, uploadRate: 0 }, { bytesPerSecond: 0 }), settings, null, null, true);
    expect([idle.down, idle.up]).toEqual(['0 B/s', '0 B/s']);
    const finished = describeInfo(tjob('completed', {}, { bytesPerSecond: 0 }), settings, null, null, true);
    expect([finished.down, finished.up]).toEqual(['0 B/s', dash]);
    const stalled = describeInfo(tjob('downloading', { uploadRate: 0 }, { bytesPerSecond: 0 }), settings, null, null, true);
    expect([stalled.down, stalled.up]).toEqual(['0 B/s', '0 B/s']);
    for (const state of ['paused', 'queued', 'failed']) expect(describeInfo(tjob(state), settings, null, null, true).down).toBe(dash);
  });

  it('says Complete for a finished torrent that does not seed', () => {
    expect(describeInfo(tjob('completed'), settings, null, null, true).status).toBe('Completed');
  });

  it('says a magnet is getting its file list', () => {
    const job = tjob('downloading', { hasMetadata: false, wantedBytes: 0 }, { totalBytes: undefined, receivedBytes: 0 });
    expect(progressText(job)).toBe('Getting file list');
    expect(infoPercent(job)).toBe(0);
  });

  it('shows the v2 hash beside the v1 one, and the finished path\'s folder when there is no save folder', () => {
    expect(hashText(hash, 'b'.repeat(64))).toBe(`v1 ${hash} · v2 ${'b'.repeat(64)}`);
    expect(hashText(hash, null)).toBe(hash);
    expect(hashText('', null)).toBe(dash);
    expect(folderText({ destinationDirectory: '', finalFilePath: '/data/done/bbb' })).toBe('/data/done');
    expect(folderText({ destinationDirectory: '', finalFilePath: 'D:\\Done\\Film' })).toBe('D:\\Done');
    expect(folderText({ destinationDirectory: '', finalFilePath: '' })).toBe(dash);
  });
});

describe('Torrent Info beside the list (T7)', () => {
  it('shows only on a wide window and narrows the list rather than scrolling it sideways', () => {
    expect(listScreenSource).toContain(`min-[${infoMinWidth}px]:flex`);
    expect(listScreenSource).toContain(`@media (min-width: ${infoMinWidth}px)`);
    // The columns follow the pane's room, not the selection, so they don't change from click to click.
    expect(listScreenSource).toMatch(/class:with-info=\{infoRoom\}/);
    expect(listScreenSource).not.toMatch(/class:with-info=\{!!infoTorrent\}/);
    expect(listScreenSource).toContain('Select one torrent to see its info here.');
  });

  it('can be turned off and on from More, which says when the window is too narrow for it', () => {
    expect(listScreenSource).toMatch(/label: 'Torrent Info', checked: torrentInfoOn && infoWide, hidden: !store\.has\('torrents'\), disabled: !infoWide, hint: infoWide \? undefined : 'Wider window'/);
  });

  it('gives the keyboard back to the list when its × hides it', () => {
    expect(listScreenSource).toMatch(/function hideTorrentInfo\(\) \{\s*setTorrentInfo\(false\);[\s\S]*?grid\?\.focus\(\);/);
    expect(listScreenSource).toMatch(/onhide=\{hideTorrentInfo\}/);
  });
});
