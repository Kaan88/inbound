import appSource from '../src/App.svelte?raw';
import graphSource from '../src/components/SpeedGraph.svelte?raw';
import listSource from '../src/screens/ListScreen.svelte?raw';
// @ts-ignore: the project has no Node types; vitest runs on Node. A CSS file imported with ?raw comes back empty here.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { anySending, graphIdle, liveUpload, SpeedHistory, torrentsRun, uploadHourOf, uploadSample } from '../src/lib/list';
import { emptyModel } from '../src/lib/model';
import { extraViewShows, inTorrentExtraView, swarmCells, torrentExtraCounts, torrentExtraViews, torrentSummary } from '../src/lib/torrentList';
import { counts, inView, isTorrentView, parseViewKey, stateViews } from '../src/lib/views';
import type { Job, Torrent } from '../src/lib/types';

const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';
const at = '2026-11-12T10:00:00Z';
const now = Date.parse(at);

function torrent(extra: Partial<Torrent> = {}): Torrent {
  return { infoHash: hash, name: 'Film', hasMetadata: true, awaitingFileChoice: false, fileCount: 2, wantedCount: 2, totalBytes: 2000, wantedBytes: 2000, peers: 3, seeds: 1, ...extra };
}

let next = 0;
function tjob(state: string, t: Partial<Torrent> | null = {}, extra: Partial<Job> = {}): Job {
  return {
    id: `j${next++}`, url: `magnet:?xt=urn:btih:${hash}`, fileName: 'film.mkv', destinationDirectory: '/downloads', finalFilePath: '/downloads/film.mkv',
    receivedBytes: 1000, totalBytes: 2000, state, createdAt: at, updatedAt: at, queueId: 0, ...(t === null ? {} : { torrent: torrent(t) }), ...extra,
  };
}

describe('T7: the extra torrent views', () => {
  it('Active is a torrent moving data now', () => {
    expect(inTorrentExtraView('active', tjob('downloading', {}, { bytesPerSecond: 2048 }))).toBe(true);
    expect(inTorrentExtraView('active', tjob('downloading', { uploadRate: 500 }, { bytesPerSecond: 0 }))).toBe(true);
    expect(inTorrentExtraView('active', tjob('completed', { seeding: true, uploadRate: 500 }))).toBe(true);
    expect(inTorrentExtraView('active', tjob('completed', { seeding: true, uploadRate: 0 }))).toBe(false);
    expect(inTorrentExtraView('active', tjob('downloading', {}, { bytesPerSecond: 0.5 }))).toBe(false);
    expect(inTorrentExtraView('active', tjob('paused', { uploadRate: 500 }, { bytesPerSecond: 2048 }))).toBe(false);
  });

  it('Stalled is a download that should run, receives nothing and has no peer, apart from a check or a file choice', () => {
    const lone = { peers: 0, seeds: 0 };
    expect(inTorrentExtraView('stalled', tjob('downloading', lone))).toBe(true);
    expect(inTorrentExtraView('stalled', tjob('connecting', lone, { bytesPerSecond: 0 }))).toBe(true);
    expect(inTorrentExtraView('stalled', tjob('downloading', lone, { bytesPerSecond: 1 }))).toBe(false);
    expect(inTorrentExtraView('stalled', tjob('downloading', { ...lone, checking: true }))).toBe(false);
    expect(inTorrentExtraView('stalled', tjob('downloading', { ...lone, awaitingFileChoice: true }))).toBe(false);
    // A magnet without its file list yet still counts.
    expect(inTorrentExtraView('stalled', tjob('connecting', { ...lone, hasMetadata: false, awaitingFileChoice: true }))).toBe(true);
    for (const state of ['queued', 'paused', 'retryWaiting', 'completed']) expect(inTorrentExtraView('stalled', tjob(state, lone))).toBe(false);
  });

  it('a torrent with a peer is not stalled, so a dip in its rate does not move it; one that sends is Active only', () => {
    expect(inTorrentExtraView('stalled', tjob('downloading', { peers: 1, seeds: 0 }, { bytesPerSecond: 0 }))).toBe(false);
    expect(inTorrentExtraView('stalled', tjob('downloading', { peers: 0, seeds: 2 }, { bytesPerSecond: 0 }))).toBe(false);
    const sending = tjob('downloading', { peers: 1, seeds: 0, uploadRate: 300 }, { bytesPerSecond: 0 });
    expect(inTorrentExtraView('active', sending)).toBe(true);
    expect(inTorrentExtraView('stalled', sending)).toBe(false);
  });

  it('counts a torrent as stalled only after its first minute', () => {
    const lone = { peers: 0, seeds: 0 };
    const ago = (seconds: number) => new Date(now - seconds * 1000).toISOString();
    expect(inTorrentExtraView('stalled', tjob('connecting', lone, { startedAt: ago(5) }), now)).toBe(false);
    expect(inTorrentExtraView('stalled', tjob('connecting', lone, { startedAt: ago(59) }), now)).toBe(false);
    expect(inTorrentExtraView('stalled', tjob('connecting', lone, { startedAt: ago(60) }), now)).toBe(true);
    expect(inTorrentExtraView('stalled', tjob('downloading', lone, { startedAt: ago(7200) }), now)).toBe(true);
    expect(torrentExtraCounts([tjob('connecting', lone, { startedAt: ago(5) })], now).stalled).toBe(0);
    expect(torrentExtraCounts([tjob('connecting', lone, { startedAt: ago(300) })], now).stalled).toBe(1);
  });

  it('Checking, Waiting for files, Completed and Errored follow the jobs', () => {
    expect(inTorrentExtraView('checking', tjob('downloading', { checking: true }))).toBe(true);
    expect(inTorrentExtraView('checking', tjob('downloading'))).toBe(false);
    expect(inTorrentExtraView('files', tjob('paused', { awaitingFileChoice: true }))).toBe(true);
    expect(inTorrentExtraView('files', tjob('paused', { awaitingFileChoice: true, hasMetadata: false }))).toBe(false);
    expect(inTorrentExtraView('files', tjob('cancelled', { awaitingFileChoice: true }))).toBe(false);
    expect(inTorrentExtraView('completed', tjob('completed'))).toBe(true);
    expect(inTorrentExtraView('completed', tjob('completed', { seeding: true }))).toBe(false);
    expect(inTorrentExtraView('errored', tjob('failed'))).toBe(true);
    expect(inTorrentExtraView('errored', tjob('paused', {}, { failure: { kind: 'diskFull', message: 'x' } as Job['failure'] }))).toBe(true);
    expect(inTorrentExtraView('errored', tjob('paused'))).toBe(false);
    for (const view of torrentExtraViews) expect(inTorrentExtraView(view.key, tjob('failed', null))).toBe(false);
  });

  it('counts the same as the desktop, a torrent in several views or none', () => {
    const jobs = [
      tjob('downloading', { uploadRate: 100 }, { bytesPerSecond: 4096 }),
      tjob('downloading', { peers: 0, seeds: 0 }),
      tjob('downloading', { uploadRate: 300 }, { bytesPerSecond: 0 }),
      tjob('downloading', { checking: true }),
      tjob('paused', { awaitingFileChoice: true }),
      tjob('completed'),
      tjob('completed', { seeding: true, uploadRate: 50 }),
      tjob('failed'),
      tjob('paused', {}, { failure: { kind: 'diskFull', message: 'x' } as Job['failure'] }),
      tjob('failed', null),
    ];
    expect(torrentExtraCounts(jobs)).toEqual({ active: 3, stalled: 1, checking: 1, files: 1, completed: 1, errored: 2 });
    const model = emptyModel();
    for (const job of jobs) model.jobs[job.id] = job;
    const shown = counts(model);
    expect([shown['torrents-active'], shown['torrents-stalled'], shown['torrents-errored']]).toEqual([3, 1, 2]);
    expect(jobs.filter((job) => inView(job, 'torrents-stalled')).length).toBe(1);
  });

  it('are views of their own with an address, and show the swarm columns', () => {
    for (const view of torrentExtraViews) {
      const key = `torrents-${view.key}` as const;
      expect(stateViews).toContain(key);
      expect(parseViewKey(key)).toBe(key);
      expect(isTorrentView(key)).toBe(true);
    }
    expect(torrentExtraViews.map((view) => view.label)).toEqual(['Active', 'Stalled', 'Checking', 'Waiting for files', 'Completed', 'Errored']);
  });

  it('are chips on a phone too, under the same rule as the sidebar', () => {
    const chips = listSource.slice(listSource.indexOf('aria-label="Torrent views"'));
    expect(chips).toMatch(/\{#each torrentExtraViews as extra[\s\S]*extraViewShows\(torrentExtraCount\?\.\[extra\.key\], `torrents-\$\{extra\.key\}`, view\)/);
  });

  it('show in the sidebar only while they hold a torrent or are the view shown', () => {
    expect(extraViewShows(0, 'torrents-stalled', 'all')).toBe(false);
    expect(extraViewShows(undefined, 'torrents-stalled', 'all')).toBe(false);
    expect(extraViewShows(2, 'torrents-stalled', 'all')).toBe(true);
    expect(extraViewShows(0, 'torrents-stalled', 'torrents-stalled')).toBe(true);
    expect(appSource).toMatch(/#each torrentExtraViews as extra/);
    expect(appSource).toMatch(/extraViewShows\(count\[`torrents-\$\{extra\.key\}`\], `torrents-\$\{extra\.key\}`, current\)/);
  });
});

describe('T8: the upload on the speed graph', () => {
  const downloading = tjob('downloading', { uploadRate: 700 }, { bytesPerSecond: 9000 });
  const seeding = tjob('completed', { seeding: true, uploadRate: 1500 }, { updatedAt: '2026-11-12T07:00:00Z' });
  const plain = tjob('downloading', null, { bytesPerSecond: 5000 });

  it('samples a downloading torrent while its update is recent and a seeding one as last said', () => {
    expect(uploadSample([downloading, seeding, plain], true, true, now + 10_000)).toBe(2200);
    expect(uploadSample([downloading, seeding, plain], true, true, now + 11_000)).toBe(1500);
    expect(uploadSample([tjob('paused', { uploadRate: 800 }), tjob('completed', { uploadRate: 800 })], true, true, now)).toBe(0);
    expect(uploadSample([downloading], false, true, now)).toBeNull();
    expect(uploadSample([downloading], true, false, now)).toBeNull();
  });

  it('shows the upload line only while a torrent downloads or seeds', () => {
    expect(torrentsRun([downloading])).toBe(true);
    expect(torrentsRun([seeding])).toBe(true);
    expect(torrentsRun([tjob('retryWaiting')])).toBe(true);
    expect(torrentsRun([plain, tjob('paused'), tjob('completed'), tjob('queued')])).toBe(false);
  });

  it('keeps sampling while a seeding torrent sends, and only then', () => {
    expect(anySending([seeding])).toBe(true);
    expect(anySending([tjob('completed', { seeding: true, uploadRate: 0 })])).toBe(false);
    expect(anySending([downloading])).toBe(false); // a download keeps it going anyway (anyActive)
    // The graph's idle rule: it rests only while connected and nothing moves or sends.
    expect(graphIdle([seeding], true, true)).toBe(false);
    expect(graphIdle([downloading], true, true)).toBe(false);
    expect(graphIdle([tjob('completed', { seeding: true, uploadRate: 0 }), tjob('paused'), tjob('completed', null)], true, true)).toBe(true);
    expect(graphIdle([], false, true)).toBe(false); // offline: it samples a gap
    expect(graphIdle([], true, false)).toBe(false);
    expect(graphSource).toMatch(/graphIdle\(/);
  });

  it('says the upload now beside the title, and nothing while offline', () => {
    expect(liveUpload([downloading, seeding, plain], true, true)).toBe(2200);
    expect(liveUpload([downloading], false, true)).toBeNull();
    expect(graphSource).toMatch(/\{#if showUpload\}/);
    expect(graphSource).toMatch(/stroke="var\(--upload\)"/);
  });

  it("takes the server's upload hour only when it lines up with the download hour", () => {
    expect(uploadHourOf({ bytesPerSecond: [0, 1000, 2000], uploadBytesPerSecond: [5, 6, 7] })).toEqual([5, 6, 7]);
    expect(uploadHourOf({ bytesPerSecond: [0, 1000, 2000], uploadBytesPerSecond: [5, 6] })).toBeNull();
    expect(uploadHourOf({ bytesPerSecond: [0, 1000, 2000], uploadBytesPerSecond: null })).toBeNull();
    expect(uploadHourOf({ bytesPerSecond: [0, 1000, 2000] })).toBeNull();
  });

  it("blanks the upload line to the download line's length when the hour does not line up, so later samples stay aligned", () => {
    const down = new SpeedHistory();
    const up = new SpeedHistory();
    // This page sampled two seconds, then was hidden (one gap each).
    for (const [d, u] of [[100, 10], [200, 20]] as const) { down.add(d); up.add(u); }
    down.add(null); up.add(null);
    // An older server fills the download hour; it has no upload.
    down.replace([1, 2, 3, 4, 5, 6], '2026-11-12T10:00:00Z');
    const hour = uploadHourOf({ bytesPerSecond: [1, 2, 3, 4, 5, 6] });
    expect(hour).toBeNull();
    up.blank(down.samples.length);
    expect(up.samples).toEqual([null, null, null, null, null, null]);
    down.add(7); up.add(70);
    expect(up.samples.length).toBe(down.samples.length);
    expect(up.samples[up.samples.length - 1]).toBe(70);
    expect(up.samples.slice(0, -1).every((sample) => sample === null)).toBe(true);
  });

  it('has its own colour in both themes', () => {
    // Light, and both dark selectors; App.xaml's InboundUploadBrush has the same values.
    const tokens = readFileSync(new URL('../src/tokens.css', import.meta.url), 'utf8');
    expect(tokens.match(/--upload: #[0-9a-f]{6};/g)).toEqual(['--upload: #b35c00;', '--upload: #f0a35e;', '--upload: #f0a35e;']);
  });

  it('shows a downloading torrent\'s upload in its Up cell and in the phone line', () => {
    const goal = { ratio: 1, hours: 48 };
    expect(swarmCells(downloading, goal, now)!.upText).toBe('700 B/s');
    expect(swarmCells(tjob('downloading'), goal, now)!.upText).toBe('–');
    expect(swarmCells(tjob('paused', { uploadRate: 700 }), goal, now)!.upText).toBe('–');
    expect(torrentSummary([downloading, seeding], 0)).toMatch(/↑ 2\.15 KB\/s$/);
  });
});
