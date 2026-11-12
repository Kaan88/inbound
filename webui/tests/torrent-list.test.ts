import appSource from '../src/App.svelte?raw';
import formatSource from '../src/lib/format.ts?raw';
import iconsSource from '../src/lib/icons.ts?raw';
import jobDetailsSource from '../src/screens/JobDetails.svelte?raw';
import listScreenSource from '../src/screens/ListScreen.svelte?raw';
import storeSource from '../src/lib/store.svelte.ts?raw';
import { describe, expect, it } from 'vitest';
import { defaultSort, emptyText, jobView, nextSort, sortKeys, sortRows } from '../src/lib/list';
import { emptyModel } from '../src/lib/model';
import {
  becameReadyToChoose, chipCompact, goalFor, knownListenPort, sortForView, sortMenuTitles, goalPercent, goalRatioText, magnetToCopy, ratioCell, readyToChooseText, saveToText, seedGoalLine, seedGoalOf, swarmCells,
  torrentCounts, torrentFilterOf, torrentSummary, waitingForChoice, waitingForChoiceText,
} from '../src/lib/torrentList';
import { buildRows, counts, inView, isTorrentView, parseViewKey, viewHash } from '../src/lib/views';
import type { Job, Settings, Torrent } from '../src/lib/types';

const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';
const now = Date.parse('2026-11-11T12:00:00Z');
const goal = { ratio: 1, hours: 48 };

function torrent(extra: Partial<Torrent> = {}): Torrent {
  return { infoHash: hash, name: 'Big Buck Bunny', hasMetadata: true, awaitingFileChoice: false, fileCount: 1, wantedCount: 1, totalBytes: 2000, wantedBytes: 2000, peers: 3, seeds: 1, ...extra };
}

function tjob(id: string, state: string, t: Partial<Torrent> | null = {}, extra: Partial<Job> = {}): Job {
  return {
    id, url: `magnet:?xt=urn:btih:${hash}&tr=https://tracker.example/announce/abcdefabcdefabcdef`, fileName: 'bbb.mkv', destinationDirectory: '/downloads/torrents',
    finalFilePath: '/downloads/torrents/bbb.mkv', receivedBytes: 1000, totalBytes: 2000, state, createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z',
    queueId: 0, ...(t === null ? {} : { torrent: torrent(t) }), ...extra,
  };
}

const downloading = tjob('d', 'downloading', { peers: 41, seeds: 12 }, { bytesPerSecond: 2_000_000 });
const queued = tjob('q', 'queued');
const seeding = tjob('s', 'completed', { seeding: true, ratio: 0.71, uploadRate: 640_000, seedingSince: '2026-11-11T11:00:00Z' });
const paused = tjob('p', 'paused');
const finished = tjob('f', 'completed', { seeding: false, ratio: 1.38 });
const failed = tjob('x', 'failed');
const plain = tjob('h', 'downloading', null, { url: 'https://example.com/file.zip', fileName: 'file.zip' });

describe('the views under Torrents (WEB-T1)', () => {
  it('puts each torrent in exactly one of Downloading, Seeding and Stopped', () => {
    expect(torrentFilterOf(downloading)).toBe('downloading');
    expect(torrentFilterOf(queued)).toBe('downloading');
    expect(torrentFilterOf(tjob('r', 'retryWaiting'))).toBe('downloading');
    expect(torrentFilterOf(tjob('r2', 'retrying'))).toBe('downloading');
    expect(torrentFilterOf(seeding)).toBe('seeding');
    expect(torrentFilterOf(paused)).toBe('stopped');
    expect(torrentFilterOf(finished)).toBe('stopped');
    expect(torrentFilterOf(failed)).toBe('stopped');
    expect(torrentFilterOf(plain)).toBeNull();
  });

  it('counts them, and the three add up to all of them; a plain download counts in none', () => {
    const c = torrentCounts([downloading, queued, seeding, paused, finished, failed, plain]);
    expect(c).toEqual({ all: 6, downloading: 2, seeding: 1, stopped: 3 });
    expect(c.downloading + c.seeding + c.stopped).toBe(c.all);
  });

  it('are list views with their own hashes, and the sidebar counts them', () => {
    for (const key of ['torrents-downloading', 'torrents-seeding', 'torrents-stopped'] as const) {
      expect(parseViewKey(key)).toBe(key);
      expect(viewHash(key)).toBe(`#/${key}`);
      expect(isTorrentView(key)).toBe(true);
    }
    expect(isTorrentView('torrents')).toBe(true);
    expect(isTorrentView('all')).toBe(false);
    expect(isTorrentView('cat:torrents-x')).toBe(false);
    expect(inView(seeding, 'torrents-seeding')).toBe(true);
    expect(inView(seeding, 'torrents-downloading')).toBe(false);
    expect(inView(plain, 'torrents-stopped')).toBe(false);

    const model = emptyModel();
    for (const job of [downloading, queued, seeding, paused, plain]) model.jobs[job.id] = job;
    const n = counts(model);
    expect(n.torrents).toBe(4);
    expect(n['torrents-downloading']).toBe(2);
    expect(n['torrents-seeding']).toBe(1);
    expect(n['torrents-stopped']).toBe(1);
    expect(buildRows(model, 'torrents-seeding', '', new Set()).map((row) => row.id)).toEqual(['s']);
  });

  it('say so when empty', () => {
    expect(emptyText('torrents-seeding', 'TorBox')).toBe('No torrents in this view.');
    expect(emptyText('torrents', 'TorBox')).toBe('No torrents in this view.');
  });

  it('show in the sidebar under Torrents and as chips on a phone', () => {
    expect(appSource).toMatch(/torrentFilters as filter/);
    expect(appSource).toMatch(/count\[`torrents-\$\{filter\.key\}`\]/);
    expect(listScreenSource).toMatch(/aria-label="Torrent views" class="[^"]*md:hidden/);
  });
});

describe('the seeding goal and the Ratio cell (WEB-T2)', () => {
  it('reads the goal from the settings, with the Host defaults when left out', () => {
    expect(seedGoalOf(null)).toEqual({ ratio: 1, hours: 48 });
    expect(seedGoalOf({ torrentSeedRatio: 2, torrentSeedHours: 0 } as Settings)).toEqual({ ratio: 2, hours: 0 });
    expect(goalRatioText(1)).toBe('1.0');
    expect(goalRatioText(1.5)).toBe('1.5');
    expect(goalRatioText(2.25)).toBe('2.25');
  });

  it('measures a seeding torrent by the nearer of the ratio and the hours', () => {
    // One hour of 48 is about 2%; a ratio of 0.71 of 1.0 is 71%.
    expect(goalPercent(seeding.torrent!, goal, now)).toBeCloseTo(71);
    expect(goalPercent({ ratio: 0.1, seedingSince: '2026-11-10T00:00:00Z' }, goal, now)).toBeCloseTo(75);
    expect(goalPercent({ ratio: 5, seedingSince: null }, goal, now)).toBe(100);
    expect(goalPercent({ ratio: 0.5 }, { ratio: 0, hours: 48 }, now)).toBe(0);
  });

  it("uses a torrent's own goal over the settings' (T3)", () => {
    expect(goalFor({ seedGoal: 'ratio', seedGoalValue: 4 }, goal)).toEqual({ ratio: 4, hours: 0 });
    expect(goalFor({ seedGoal: 'hours', seedGoalValue: 2 }, goal)).toEqual({ ratio: 0, hours: 2 });
    expect(goalFor({ seedGoal: 'never' }, goal)).toEqual({ ratio: 0, hours: 0 });
    expect(goalFor({ seedGoal: 'global' }, goal)).toBe(goal);
    expect(goalFor(undefined, goal)).toBe(goal);
    const own = { ...seeding, torrent: { ...seeding.torrent!, seedGoal: 'ratio', seedGoalValue: 4 } };
    expect(swarmCells(own, goal, now)?.ratioText).toBe('0.71 → 4.0');
    const never = { ...seeding, torrent: { ...seeding.torrent!, seedGoal: 'never' } };
    expect(swarmCells(never, goal, now)?.ratioText).toBe('0.71');
    expect(swarmCells(never, goal, now)?.goal).toBeNull();
  });

  it('says the way to the goal that comes first', () => {
    expect(ratioCell(seeding.torrent!, goal, now)).toBe('0.71 → 1.0');
    expect(ratioCell({ seeding: true, ratio: 0.1, seedingSince: '2026-11-10T00:00:00Z' }, goal, now)).toBe('0.10 · 12h 0m left');
    expect(ratioCell({ seeding: true, ratio: 0.3, seedingSince: '2026-11-11T11:00:00Z' }, { ratio: 0, hours: 2 }, now)).toBe('0.30 · 1h 0m left');
    expect(ratioCell({ seeding: false, ratio: 0.08 }, goal, now)).toBe('0.08');
    expect(ratioCell({ seeding: true, ratio: 0.2, seedingSince: null }, { ratio: 0, hours: 48 }, now)).toBe('0.20');
  });
});

describe('the swarm cells of a torrent row (WEB-T2)', () => {
  it('give seeds, peers, the upload rate while seeding, and the ratio', () => {
    const d = swarmCells(downloading, goal, now)!;
    expect([d.seedsText, d.peersText, d.upText, d.ratioText, d.goal]).toEqual(['12', '41', '–', '0.00', null]);
    expect(d.line).toBe('41 peers (12 seeds) · ratio 0.00');
    const s = swarmCells(seeding, goal, now)!;
    expect([s.seedsText, s.peersText, s.upText, s.ratioText]).toEqual(['1', '3', '625 KB/s', '0.71 → 1.0']);
    expect(s.goal).toBeCloseTo(71);
    expect(s.line).toBe('3 peers (1 seed) · ratio 0.71 → 1.0');
  });

  it('show dashes for a torrent that is not running, and nothing for a plain download', () => {
    const p = swarmCells(paused, goal, now)!;
    expect([p.seedsText, p.peersText, p.upText, p.seeds, p.peers]).toEqual(['–', '–', '–', -1, -1]);
    expect(p.line).toBe('ratio 0.00');
    expect(swarmCells(plain, goal, now)).toBeUndefined();
  });

  it('give a seeding row its bar toward the goal; a finished torrent that stopped has none', () => {
    expect(jobView(seeding, undefined, goal, now).bar).toBeCloseTo(71);
    expect(jobView(finished, undefined, goal, now).bar).toBeNull();
    expect(jobView(plain).swarm).toBeUndefined();
  });

  it('sort by Seeds, Peers, Up and Ratio, with the rows that have none last', () => {
    expect(sortKeys).toEqual(expect.arrayContaining(['seeds', 'peers', 'up', 'ratio']));
    const views = [paused, downloading, seeding].map((job) => ({ id: job.id, v: jobView(job, undefined, goal, now) }));
    const order = (key: 'seeds' | 'peers' | 'up' | 'ratio') => sortRows(views, (row) => row.v, { key, ascending: false }).map((row) => row.id);
    expect(order('seeds')).toEqual(['d', 's', 'p']);
    expect(order('peers')).toEqual(['d', 's', 'p']);
    expect(order('up')[0]).toBe('s');
    expect(order('ratio')[0]).toBe('s');
  });

  it('are columns in the torrent views only, in place of Last Try Date, and a line on a phone', () => {
    expect(listScreenSource).toMatch(/head\('seeds', 'Seeds'/);
    expect(listScreenSource).toMatch(/head\('ratio', 'Ratio'/);
    expect(listScreenSource).toMatch(/\{#if torrentCols\}[\s\S]*cell-swarm[\s\S]*\{:else\}\s*<span class="cell-added/);
    expect(listScreenSource).toMatch(/class="phone-swarm"/);
  });

  it('shows the phone line in the torrent views only', () => {
    expect(listScreenSource).toMatch(/\{#if row\.kind === 'job' && torrentCols && v\.swarm && connected\}<span class="phone-swarm">/);
  });

  it('are in the More > Sort by menu in the torrent views, where below 1024 px it is the only way to sort by them', () => {
    const plainKeys = sortMenuTitles(false).map(([key]) => key);
    expect(plainKeys).not.toContain('seeds');
    expect(plainKeys).not.toContain('ratio');
    const torrentTitles = sortMenuTitles(true);
    expect(torrentTitles.filter(([key]) => ['seeds', 'peers', 'up', 'ratio'].includes(key))).toEqual([['seeds', 'Seeds'], ['peers', 'Peers'], ['up', 'Up'], ['ratio', 'Ratio']]);
    for (const [key] of torrentTitles) expect(sortKeys).toContain(key);
    expect(listScreenSource).toMatch(/const columnTitles = \$derived\(sortMenuTitles\(torrentCols\)\)/);
  });

  it('sort only the torrent views; elsewhere the default holds and the menu names it', () => {
    const byRatio = { key: 'ratio' as const, ascending: false };
    expect(sortForView(byRatio, true)).toEqual(byRatio);
    expect(sortForView(byRatio, false)).toEqual(defaultSort);
    const byName = { key: 'name' as const, ascending: true };
    expect(sortForView(byName, false)).toEqual(byName);
    expect(sortMenuTitles(false).find(([key]) => key === sortForView(byRatio, false).key)?.[1]).toBe('Order of addition');
    // A click in All while Ratio is saved starts from the default, as if Ratio were never chosen.
    expect(nextSort(sortForView(byRatio, false), 'size')).toEqual({ key: 'size', ascending: false });
    expect(listScreenSource).toMatch(/buildRows\(store\.model, view, search, expanded, shownSort, torrentFiles\)/);
    expect(listScreenSource).toMatch(/label: `Sort by: \$\{columnTitles\.find\(\(\[key\]\) => key === shownSort\.key\)/);
  });
});

describe('the phone (WEB-T3)', () => {
  it('sums what the torrents move, with the port once one is chosen', () => {
    expect(torrentSummary([downloading, seeding, paused, plain], 51413)).toBe('↓ 1.91 MB/s · ↑ 625 KB/s · port 51413');
    expect(torrentSummary([paused], 0)).toBe('↓ 0 B/s · ↑ 0 B/s');
    expect(torrentSummary([plain], 51413)).toBe('');
  });

  it("shows the listen port only where it is the real one: the Windows app's, not the Docker server's", () => {
    // The server listens on DOWNLOADMANAGER_TORRENT_PORT; its settings hold a port it doesn't use.
    expect(knownListenPort({ torrentListenPort: 51413 }, 'windows')).toBe(51413);
    expect(knownListenPort({ torrentListenPort: 51413 }, 'server')).toBe(0);
    expect(knownListenPort({ torrentListenPort: 51413 }, undefined)).toBe(0);
    expect(knownListenPort({ torrentListenPort: 0 }, 'windows')).toBe(0);
    expect(knownListenPort(null, 'windows')).toBe(0);
    expect(listScreenSource).toMatch(/torrentSummary\(Object\.values\(store\.model\.jobs\), knownListenPort\(store\.model\.settings, store\.model\.hello\?\.hostKind\)\)/);
    expect(jobDetailsSource).toMatch(/const listenPort = \$derived\(knownListenPort\(store\.model\.settings, store\.model\.hello\?\.hostKind\)\)/);
    expect(jobDetailsSource).not.toMatch(/settings\?\.torrentListenPort/);
  });

  it('keeps the status bar chip, shorter', () => {
    expect(chipCompact([downloading, seeding, plain])).toBe('1 active · 41 peers');
    expect(chipCompact([seeding])).toBeNull();
    expect(appSource).toMatch(/<span class="sm:hidden">\{torrentChipShort\}<\/span>/);
    expect(appSource).not.toMatch(/hidden shrink-0 items-center gap-1 hover:text-text sm:inline-flex/);
  });
});

describe('Copy Magnet Link (WEB-T4)', () => {
  it('copies the info hash and the name, never the trackers', () => {
    expect(magnetToCopy(downloading.torrent)).toBe(`magnet:?xt=urn:btih:${hash}&dn=Big%20Buck%20Bunny`);
    expect(magnetToCopy({ infoHash: hash, name: ' ' })).toBe(`magnet:?xt=urn:btih:${hash}`);
    expect(magnetToCopy({ infoHash: '', name: 'x' })).toBeNull();
    expect(magnetToCopy(undefined)).toBeNull();
    expect(magnetToCopy(downloading.torrent)).not.toContain('tr=');
  });

  it('is in the row menu, for one torrent', () => {
    expect(listScreenSource).toMatch(/label: 'Copy Magnet Link', hidden: !magnetToCopy\(single\?\.torrent\)/);
  });
});

describe('the overview of a torrent (WEB-T8)', () => {
  it('says where it saves and the seeding goal, or that it is not seeded', () => {
    expect(saveToText(downloading)).toBe('/downloads/torrents');
    expect(saveToText({ destinationDirectory: '' })).toBe('—');
    expect(seedGoalLine({ torrentSeedAfterDownload: false }, false, 'Ratio 1.0 or 48 h, whichever comes first')).toBe('No seeding after the download');
    expect(seedGoalLine({ torrentSeedAfterDownload: false }, true, 'Ratio 1.0 or 48 h, whichever comes first')).toBe('Ratio 1.0 or 48 h, whichever comes first');
    expect(seedGoalLine({ torrentSeedAfterDownload: true }, false, 'G')).toBe('G');
    expect(jobDetailsSource).toMatch(/\{#if job\.torrent\}\s*<dt class="text-muted">Save to<\/dt>/);
  });
});

describe('ready to choose files (WEB-T12)', () => {
  const waiting = tjob('w', 'paused', { awaitingFileChoice: true, fileCount: 11 });

  it('is said once, when the file list comes in', () => {
    const before = tjob('w', 'connecting', { hasMetadata: false, awaitingFileChoice: false });
    expect(becameReadyToChoose(before, waiting)).toBe(true);
    expect(becameReadyToChoose(undefined, waiting)).toBe(true);
    expect(becameReadyToChoose(waiting, waiting)).toBe(false);
    expect(becameReadyToChoose(before, tjob('w', 'paused', { hasMetadata: false, awaitingFileChoice: true }))).toBe(false);
    expect(becameReadyToChoose(before, tjob('w', 'cancelled', { awaitingFileChoice: true }))).toBe(false);
    expect(becameReadyToChoose(before, plain)).toBe(false);
    expect(readyToChooseText('Linux ISOs')).toBe('Linux ISOs: ready to choose files.');
    expect(readyToChooseText('')).toBe('A torrent: ready to choose files.');
  });

  it('marks Torrents with how many wait', () => {
    expect(waitingForChoice([waiting, downloading, tjob('v', 'paused', { awaitingFileChoice: true })])).toBe(2);
    expect(waitingForChoiceText(1)).toBe('1 torrent is ready to choose files');
    expect(waitingForChoiceText(2)).toBe('2 torrents are ready to choose files');
    expect(storeSource).toMatch(/becameReadyToChoose\(earlier, event\.job\)/);
    expect(appSource).toMatch(/waitingForChoiceText\(choosing\)/);
  });
});

describe('the files the extension bundles', () => {
  it('say nothing of torrents', () => {
    const words = /torrent|magnet|btih|\bpeers?\b|\bseed(s|ing|er)?\b/i;
    expect(formatSource).not.toMatch(words);
    expect(iconsSource).not.toMatch(words);
  });
});
