import addSource from '../src/screens/TorrentAdd.svelte?raw';
import jobDetailsSource from '../src/screens/JobDetails.svelte?raw';
import listScreenSource from '../src/screens/ListScreen.svelte?raw';
import settingsSource from '../src/screens/SettingsScreen.svelte?raw';
import swarmPanelSource from '../src/components/TorrentSwarmPanel.svelte?raw';
import filesPanelSource from '../src/components/TorrentFilesPanel.svelte?raw';
import watchSource from '../src/lib/torrentWatch.svelte.ts?raw';
import { describe, expect, it } from 'vitest';
import { buildFileTree } from '../src/lib/remote';
import { applyForm, formFrom, friendlyRefusal, peersFrom, peersProblem, validateAll } from '../src/lib/settingsRules';
import {
  addPriorities, canAnnounce, canRecheck, draftFrom, FileReadGate, freeText, globalGoalText, goalText, limitsText, mostLimitKb, nodePriority, nodeWanted, optionsRequest,
  optionsSaid, overviewRows, priorityChanges, priorityLabel, priorityMap, priorityOf, prioritiesKnown, prioritySummary, recheckSaid, setAllWanted,
  setNodePriority, setNodeWanted, spaceProblem, wantedIndexes, wasteText, watchWanted,
} from '../src/lib/torrentControls';
import { trackerColumns } from '../src/lib/columns';
import { hasTorrentControls, nextAnnounceText, torrentTabs, trackerRows } from '../src/lib/torrentDetails';
import {
  addLines, inspected, lineSpaceProblem, lineState, spaceRechecked, newLines, picks, problem, setLinePriorities, torrentStatusText, treeFiles,
} from '../src/lib/torrents';
import type { Job, Settings, Torrent, TorrentFile } from '../src/lib/types';

const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';

function torrent(extra: Partial<Torrent> = {}): Torrent {
  return { infoHash: hash, name: 'Blender', hasMetadata: true, awaitingFileChoice: false, fileCount: 4, wantedCount: 4, totalBytes: 4000, wantedBytes: 4000, peers: 3, seeds: 1, seedGoal: 'global', ...extra };
}

function tjob(state: string, t: Partial<Torrent> = {}): Job {
  return {
    id: 'a', url: `magnet:?xt=urn:btih:${hash}`, fileName: 'Blender', destinationDirectory: '/d', finalFilePath: '/d/Blender', receivedBytes: 10, totalBytes: 4000,
    state, createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', queueId: 0, torrent: torrent(t),
  };
}

function settings(extra: Partial<Settings> = {}): Settings {
  return {
    defaultDownloadDirectory: '/data/downloads', maxConcurrentDownloads: 3, defaultQueueId: 0, queues: [], schedules: [],
    defaultConnections: 8, useCategoryFolders: true, maxConnectionsPerHost: 8, connectionProfiles: [],
    proxy: { mode: 'none', hasPassword: false }, siteLogins: [], allowLocalNetwork: true, autoResumeUnfinishedOnStartup: true,
    browserIntegrationEnabled: true, allowSystemCompletionActions: false, favoriteFolders: [], recentDestinationDirectories: [], downloadGroups: [],
    excludedFileExtensions: [], excludedHosts: [], torrentsEnabled: true, torrentSeedAfterDownload: true, torrentSeedRatio: 1, torrentSeedHours: 48, torrentUploadLimit: 0,
    torrentDht: true, torrentPex: true, torrentLsd: false, torrentEncryption: 'prefer', torrentUseProxy: false,
    torrentSeedsAtOnce: 0, torrentConnectionsPerTorrent: 0, torrentMaxConnections: 0,
    ...extra,
  };
}

const files: TorrentFile[] = [
  { index: 0, path: 'Charge/charge.mkv', size: 3000, wanted: true, priority: 'high' },
  { index: 1, path: 'Charge/subtitles.zip', size: 100, wanted: true, priority: 'normal' },
  { index: 2, path: 'Spring/spring.mkv', size: 800, wanted: false, priority: 'skip' },
  { index: 3, path: 'readme.txt', size: 100, wanted: true, priority: 'low' },
];

describe('file priorities (T3)', () => {
  it('reads each file as Skip, Low, Normal or High, and an older server by its tick', () => {
    expect(files.map(priorityOf)).toEqual(['high', 'normal', 'skip', 'low']);
    expect(priorityOf({ wanted: true })).toBe('normal');
    expect(priorityOf({ wanted: false })).toBe('skip');
    expect(priorityOf({ wanted: false, priority: 'high' })).toBe('skip');
    expect(prioritiesKnown(files)).toBe(true);
    expect(prioritiesKnown([{ priority: undefined }])).toBe(false);
    expect(priorityLabel('mixed')).toBe('Mixed');
    expect(priorityLabel('skip')).toBe('Skip');
  });

  it('gives a folder the priority its files share, or Mixed, and sets every file under it', () => {
    const tree = buildFileTree(treeFiles(files));
    const map = priorityMap(files);
    const charge = tree.roots.find((node) => node.name === 'Charge')!;
    const spring = tree.roots.find((node) => node.name === 'Spring')!;
    expect(nodePriority(charge, map)).toBe('mixed');
    expect(nodePriority(spring, map)).toBe('skip');
    expect(nodeWanted(charge, map)).toBe(true);
    expect(nodeWanted(spring, map)).toBe(false);

    const high = setNodePriority(charge, map, 'high');
    expect(nodePriority(charge, high)).toBe('high');
    expect(priorityChanges(map, high)).toEqual([{ index: 1, priority: 'high' }]);

    // A tick makes a skipped file Normal and keeps the others' priorities; an untick skips them all.
    const on = setNodeWanted(spring, map, true);
    expect(on.get('2')).toBe('normal');
    expect(setNodeWanted(charge, map, false).get('0')).toBe('skip');
    expect(wantedIndexes(setAllWanted(tree, map, false))).toEqual([]);
    expect(wantedIndexes(setAllWanted(tree, map, true))).toEqual([0, 1, 2, 3]);
    expect(setAllWanted(tree, map, true).get('0')).toBe('high');
  });

  it('sends only what changed, and the add sends its High and Low files', () => {
    const map = priorityMap(files);
    expect(priorityChanges(map, map)).toEqual([]);
    expect(addPriorities(map)).toEqual([{ index: 0, priority: 'high' }, { index: 3, priority: 'low' }]);
    expect(addPriorities(new Map([['0', 'normal']]))).toBeNull();
    expect(addPriorities(null)).toBeNull();
    expect(prioritySummary(files, map)).toBe('3 of 4 files · 3.13 KB');
  });

  it('the add dialog: priorities become the choice, and the add carries them', () => {
    const input = 'upload:abc';
    let state = addLines(newLines('thisPc', true, null), [{ input, label: 'blender.torrent' }], false).state;
    state = inspected(state, input, { ok: true, kind: 'torrentFile', infoHash: hash, name: 'Blender', totalBytes: 4000, files: files.map((f) => ({ ...f, wanted: true, priority: 'normal' })) });
    state = setLinePriorities(state, input, priorityMap(files));
    expect(state.lines[0].chosen).toEqual([0, 1, 3]);
    expect(picks(state)[0]).toMatchObject({ files: 'chosen', fileIndexes: [0, 1, 3], priorities: [{ index: 0, priority: 'high' }, { index: 3, priority: 'low' }] });

    // Every file wanted: "all", with the priorities still sent.
    state = setLinePriorities(state, input, new Map([['0', 'high'], ['1', 'normal'], ['2', 'normal'], ['3', 'normal']]));
    expect(state.lines[0].chosen).toBeNull();
    expect(picks(state)[0]).toMatchObject({ files: 'all', fileIndexes: null, priorities: [{ index: 0, priority: 'high' }] });
  });
});

describe('free space in the add dialog (T3)', () => {
  it('says the free space where it goes, and why a torrent does not fit', () => {
    expect(freeText({ destination: '/data/downloads', freeBytes: 2048 })).toBe('2.00 KB free in /data/downloads');
    expect(freeText({ freeBytes: null })).toBe('');
    expect(spaceProblem({ freeBytes: 1000, spaceProblem: 'It needs 3.9 KB; 1000 B is free.' }, 4000, false)).toBe('It needs 3.9 KB; 1000 B is free.');
    // With only some files chosen the page measures them itself: the Host's words are for every file.
    expect(spaceProblem({ freeBytes: 1000, spaceProblem: 'It needs 3.9 KB.' }, 900, true)).toBeNull();
    expect(spaceProblem({ freeBytes: 1000 }, 3200, true)).toBe('The chosen files need 3.13 KB, and only 1000 B is free.');
  });

  it('names the torrent that does not fit under the list', () => {
    const input = 'upload:abc';
    let state = addLines(newLines('thisPc', true, null), [{ input, label: 'blender.torrent' }], false).state;
    state = inspected(state, input, { ok: true, kind: 'torrentFile', infoHash: hash, name: 'Blender', totalBytes: 4000, files, freeBytes: 100, destination: '/d', spaceProblem: "It doesn't fit." });
    expect(lineSpaceProblem(state.lines[0])).toBe("It doesn't fit.");
    expect(problem(state)).toBe("Blender: It doesn't fit.");
  });

  it('a new Save to folder changes only the free space of a line that was read, and a failed re-check keeps the line ready', () => {
    const input = 'upload:abc';
    let state = addLines(newLines('thisPc', true, null), [{ input, label: 'blender.torrent' }], false).state;
    state = inspected(state, input, { ok: true, kind: 'torrentFile', infoHash: hash, name: 'Blender', totalBytes: 4000, files: files.map((f) => ({ ...f, wanted: true, priority: 'normal' })), freeBytes: 100, destination: '/a', spaceProblem: "It doesn't fit." });
    state = setLinePriorities(state, input, priorityMap(files));
    const chosen = state.lines[0].chosen;
    expect(lineState(state.lines[0])).toBe('ready');

    // The new folder's answer: the space moves, the files and the choice stay.
    state = spaceRechecked(state, input, { ok: true, kind: 'torrentFile', infoHash: hash, name: 'Other', totalBytes: 1, files: [], freeBytes: 9_000_000, destination: '/b', spaceProblem: null });
    expect(state.lines[0].result).toMatchObject({ ok: true, name: 'Blender', totalBytes: 4000, freeBytes: 9_000_000, destination: '/b', spaceProblem: null });
    expect(state.lines[0].result?.files).toHaveLength(4);
    expect(state.lines[0].chosen).toEqual(chosen);
    expect(lineSpaceProblem(state.lines[0])).toBeNull();

    // The connection dropped, or the upload expired: the line stays ready, only the old folder's space is forgotten.
    state = spaceRechecked(state, input, { ok: false, error: 'That upload is gone.' });
    expect(lineState(state.lines[0])).toBe('ready');
    expect(state.lines[0].result).toMatchObject({ ok: true, name: 'Blender', freeBytes: null, destination: null, spaceProblem: null });
    expect(state.lines[0].chosen).toEqual(chosen);
    state = spaceRechecked(state, input, null);
    expect(lineState(state.lines[0])).toBe('ready');

    // A line not read yet takes the whole answer.
    let fresh = addLines(newLines('thisPc', true, null), [{ input, label: 'blender.torrent' }], false).state;
    fresh = spaceRechecked(fresh, input, { ok: true, kind: 'torrentFile', infoHash: hash, name: 'Blender', totalBytes: 4000, files });
    expect(lineState(fresh.lines[0])).toBe('ready');
    expect(addSource).toContain('spaceRechecked(lines, input, result, folder)');
  });
});

describe("a torrent's own options (T3)", () => {
  it('reads the options into the tab and back, keeping an untouched limit to the byte', () => {
    const draft = draftFrom({ downloadLimit: 1536, uploadLimit: null, seedGoal: 'ratio', seedGoalValue: 2, sequential: true });
    expect(draft).toMatchObject({ limitDown: true, downKb: 1, limitUp: false, upKb: null, goal: 'ratio', ratio: 2, hours: 48, sequential: true });
    const request = optionsRequest('a', draft);
    expect(request).toEqual({ ok: true, request: { jobId: 'a', downloadLimit: 1536, uploadLimit: 0, seedGoal: 'ratio', seedGoalValue: 2, sequential: true } });

    draft.downKb = 500;
    draft.limitUp = true;
    draft.upKb = 100;
    draft.goal = 'global';
    expect(optionsRequest('a', draft)).toMatchObject({ ok: true, request: { downloadLimit: 512000, uploadLimit: 102400, seedGoal: 'global', seedGoalValue: null } });
    expect(draftFrom(null)).toMatchObject({ limitDown: false, limitUp: false, goal: 'global', sequential: false });
  });

  it("refuses what the Host refuses, in the Host's words", () => {
    const draft = draftFrom(null);
    draft.limitDown = true;
    draft.downKb = 0;
    expect(optionsRequest('a', draft)).toMatchObject({ ok: false, field: 'opt-down' });
    draft.downKb = mostLimitKb + 1;
    expect(optionsRequest('a', draft)).toMatchObject({ ok: false, field: 'opt-down' });
    draft.downKb = mostLimitKb;
    expect(optionsRequest('a', draft).ok).toBe(true);
    draft.goal = 'ratio';
    draft.ratio = 0;
    expect(optionsRequest('a', draft)).toEqual({ ok: false, error: 'Enter a seeding ratio from 0.01 to 100.', field: 'opt-ratio' });
    draft.goal = 'hours';
    draft.hours = 9000;
    expect(optionsRequest('a', draft)).toEqual({ ok: false, error: 'Enter seeding hours from 0.1 to 8760.', field: 'opt-hours' });
    draft.goal = 'never';
    expect(optionsRequest('a', draft)).toMatchObject({ ok: true, request: { seedGoal: 'never', seedGoalValue: null } });
  });

  it('says the goal, the limits and how the save went', () => {
    expect(globalGoalText(settings())).toBe('Options › Torrents: ratio 1.0 or 48 h, whichever comes first');
    expect(globalGoalText(settings({ torrentSeedAfterDownload: false }))).toBe('Options › Torrents: no seeding after the download');
    expect(goalText({ seedGoal: 'ratio', seedGoalValue: 2 }, settings())).toBe("Ratio 2.0 (this torrent's own)");
    expect(goalText({ seedGoal: 'hours', seedGoalValue: 12 }, settings())).toBe("12 h (this torrent's own)");
    expect(goalText({ seedGoal: 'never' }, settings())).toBe("Don't seed (this torrent's own)");
    expect(goalText({}, settings())).toBe(globalGoalText(settings()));
    expect(limitsText({ downloadLimit: 512000, uploadLimit: null })).toBe('↓ 500 KB/s');
    expect(limitsText({})).toBe('');
    expect(optionsSaid(true, null)).toBe('Saved and applied now.');
    expect(optionsSaid(false, null)).toBe('Saved. They apply when the torrent starts.');
    expect(optionsSaid(true, 'The torrent program in this installation is too old for that. Update the app.')).toMatch(/^Saved\. The torrent program/);
  });
});

describe('Force recheck and Announce now (T3)', () => {
  it('offers a check where there may be files on disk, and an announce only while it runs', () => {
    expect(canRecheck(tjob('downloading'))).toBe(true);
    expect(canRecheck(tjob('paused'))).toBe(true);
    expect(canRecheck(tjob('completed'))).toBe(false);
    expect(canRecheck(tjob('completed', { seeding: true }))).toBe(true);
    expect(canRecheck(tjob('downloading', { hasMetadata: false }))).toBe(false);
    expect(canRecheck(tjob('downloading', { checking: true }))).toBe(false);
    expect(canRecheck(tjob('cancelled'))).toBe(false);
    expect(canAnnounce(tjob('downloading'))).toBe(true);
    expect(canAnnounce(tjob('completed', { seeding: true }))).toBe(true);
    expect(canAnnounce(tjob('paused'))).toBe(false);
    expect(canAnnounce(tjob('queued'))).toBe(false);
    expect(recheckSaid(true)).toBe('Checking the files now.');
    expect(recheckSaid(false)).toBe('The files are checked when the torrent starts.');
  });

  it('an older server without T3 keeps the T2 tabs and gets no T3 actions', () => {
    const old = tjob('downloading', { seedGoal: undefined });
    expect(hasTorrentControls(old)).toBe(false);
    expect(hasTorrentControls(tjob('downloading'))).toBe(true);
    expect(hasTorrentControls(null)).toBe(false);
    expect(canRecheck(old)).toBe(false);
    expect(canAnnounce(old)).toBe(false);
    expect(torrentTabs(false, false).map(([key]) => key)).toEqual(['status', 'files', 'peers', 'trackers', 'pieces', 'speed', 'completion']);
    expect(torrentTabs(false, false)[0][1]).toBe('Download status');
    expect(torrentTabs(true, false).map(([key]) => key)).toEqual(['status', 'files', 'peers', 'trackers', 'pieces']);
    expect(torrentTabs(false).map(([key]) => key)).toEqual(['status', 'files', 'peers', 'trackers', 'pieces', 'options', 'completion']);
    expect(jobDetailsSource).toContain("torrentTabs(job?.state === 'completed', controls)");
    expect(jobDetailsSource).toContain('{#if job.torrent && controls}');
    expect(listScreenSource).toContain('const controlJobs = torrentJobs.filter(hasTorrentControls)');
    expect(swarmPanelSource).toContain('{#if answered !== null && hasTorrentControls(job)}');
  });

  it("an Announce refusal stays by its button, and a refused details read isn't a read error", () => {
    expect(swarmPanelSource).toContain('announceProblem = error instanceof Error');
    expect(swarmPanelSource).not.toMatch(/catch \(error\) \{\s*problem = error[^}]*\}\s*finally \{\s*announcing/);
    const details = swarmPanelSource.slice(swarmPanelSource.indexOf("'torrent.details'") - 200, swarmPanelSource.indexOf("'torrent.details'") + 300);
    expect(details).toContain('} catch {');
  });

  it('the Files tab drops a read asked before a change, and reads again', () => {
    const gate = new FileReadGate();
    // A periodic read is on its way when the user changes a priority.
    const periodic = gate.begin();
    expect(periodic).toBe(0);
    gate.changed(); // sending
    gate.changed(); // taken
    // The send's own read finds one on its way: it is asked once that one is back.
    expect(gate.begin()).toBeNull();
    // The periodic answer has the old priorities: dropped.
    expect(gate.fresh(periodic!)).toBe(false);
    expect(gate.end()).toBe(true);
    // The read asked again sees the change.
    const again = gate.begin();
    expect(again).not.toBeNull();
    expect(gate.fresh(again!)).toBe(true);
    expect(gate.end()).toBe(false);
    // Nothing changed meanwhile: one read, no second.
    const plain = gate.begin();
    expect(gate.fresh(plain!)).toBe(true);
    expect(gate.end()).toBe(false);
    expect(filesPanelSource).toContain('if (reads.fresh(asked))');
    expect(filesPanelSource).toContain('if (reads.end()) void read();');
  });

  it('a row being checked says so', () => {
    expect(torrentStatusText(tjob('downloading', { checking: true }))).toBe('Checking files');
  });

  it('the row menu, the details and the Trackers tab offer them', () => {
    for (const label of ['Force Recheck', 'Announce Now', 'Set Limits…']) expect(listScreenSource).toContain(`label: '${label}'`);
    expect(listScreenSource).toContain("detailsTab = 'options'");
    expect(jobDetailsSource).toContain('Force recheck');
    expect(jobDetailsSource).toContain('Announce now');
    expect(jobDetailsSource).toContain('<TorrentOptionsPanel');
    // UI.R42: the Trackers tab's headers come from trackerColumns.
    expect(trackerColumns.defs.map((def) => def.title)).toContain('Next announce');
    expect(swarmPanelSource).toContain('trackerColumns.defs');
    expect(swarmPanelSource).toContain("'torrent.announce'");
    expect(swarmPanelSource).toContain("'torrent.details'");
  });
});

describe('the Overview, Trackers and Pieces facts (T3)', () => {
  it('lists what the Host keeps, and while it runs what the engine says', () => {
    const t = torrent({ source: 'file', seedGoal: 'never', downloadLimit: 1024, recheckPending: true });
    const stopped = overviewRows({ running: false, source: 'file', infoHashV2: 'b'.repeat(64), private: true, pieceCount: 15820, pieceLength: 262144 }, t, settings());
    expect(stopped).toEqual([
      { label: 'Added from', value: 'A .torrent file' },
      { label: 'Info hash v2', value: 'b'.repeat(64), wide: true },
      { label: 'Private', value: 'Yes · DHT, PEX and local peers are off for it' },
      { label: 'Pieces', value: '15,820 × 256 KB' },
      { label: 'Seeding goal', value: "Don't seed (this torrent's own)" },
      { label: 'Own limits', value: '↓ 1.00 KB/s' },
      { label: 'File check', value: 'When the torrent starts' },
    ]);
    const running = overviewRows({ running: true, private: false, connections: 12, connectionsLimit: 50, distributedCopies: 1.5, nextAnnounce: 1440, pex: true, dhtNodes: -1 }, torrent(), settings());
    expect(running.map((row) => `${row.label}: ${row.value}`)).toEqual([
      'Added from: A magnet link',
      'Private: No',
      `Seeding goal: ${globalGoalText(settings())}`,
      'Connections: 12 of 50',
      'Copies among peers: 1.50',
      'Next announce: 24m 0s',
      'Peer exchange: On',
      'DHT nodes: DHT is off',
    ]);
    // A seed has no copies figure (-1).
    expect(overviewRows({ running: true, distributedCopies: -1 }, torrent(), settings()).some((row) => row.label === 'Copies among peers')).toBe(false);
  });

  it('a tracker row says when it announces next', () => {
    expect(nextAnnounceText(0)).toBe('Now');
    expect(nextAnnounceText(-1)).toBe('');
    expect(nextAnnounceText(45)).toBe('45s');
    const rows = trackerRows([
      { url: 'http://t.example/announce', tier: 0, status: 'working', seeds: 1, peers: 2, nextAnnounce: 90 },
      { url: 'udp://u.example:1337', tier: 1, status: 'notContacted', seeds: -1, peers: -1, nextAnnounce: -1 },
      { url: 'udp://old.example:1', tier: 2, status: 'working', seeds: 0, peers: 0 },
    ]);
    expect(rows.map((row) => row.next)).toEqual(['1m 30s', '–', '–']);
  });

  it('says what was thrown away', () => {
    expect(wasteText({ wasted: 0, hashFailures: 0 })).toBe('Wasted 0 B · hash failures 0');
    expect(wasteText({ wasted: 2048, hashFailures: 3 })).toBe('Wasted 2.00 KB · hash failures 3');
    expect(wasteText(null)).toBe('');
  });
});

describe('torrent.watch (T3, G5)', () => {
  it('is held only while the page is visible, connected, the server has torrents and a torrent shows', () => {
    const all = { visible: true, connected: true, capable: true, showsTorrents: true };
    expect(watchWanted(all)).toBe(true);
    for (const key of Object.keys(all) as (keyof typeof all)[]) expect(watchWanted({ ...all, [key]: false })).toBe(false);
  });

  it('is renewed with a lease, ended when torrents stop showing, and only over the page connection', () => {
    expect(watchSource).toContain("'torrent.watch'");
    expect(watchSource).toContain('leaseSeconds: watchLeaseSeconds');
    expect(watchSource).toContain('{ on: false }');
    expect(watchSource).toContain("store.connection !== 'open'");
    expect(listScreenSource).toContain('watchTorrents(');
  });
});

describe('Options › Torrents peers and connections (T3)', () => {
  it('reads them, and an older server without them gets no boxes', () => {
    expect(peersFrom(settings({ torrentLsd: true, torrentEncryption: 'require', torrentSeedsAtOnce: 5, torrentConnectionsPerTorrent: 50 }))).toEqual({
      dht: true, pex: true, lsd: true, encryption: 'require', useProxy: false, seedsAtOnce: 5, perTorrent: 50, total: null,
    });
    const older = settings();
    delete older.torrentDht;
    delete older.torrentSeedsAtOnce;
    expect(peersFrom(older)).toBeNull();
    expect(formFrom(older).peers).toBeNull();
  });

  it("checks the numbers with the Host's words", () => {
    const peers = peersFrom(settings())!;
    expect(peersProblem(peers)).toBeNull();
    expect(peersProblem({ ...peers, seedsAtOnce: 1001 })?.field).toBe('tor-seeds');
    expect(peersProblem({ ...peers, perTorrent: 1 })).toEqual({ message: 'Enter connections per torrent from 2 to 2000, or 0 for no limit of its own.', field: 'tor-per' });
    expect(peersProblem({ ...peers, perTorrent: 2 })).toBeNull();
    expect(peersProblem({ ...peers, total: 9 })?.field).toBe('tor-total');
    expect(peersProblem({ ...peers, total: 20001 })?.field).toBe('tor-total');
    expect(peersProblem({ ...peers, total: 10 })).toBeNull();
    const form = formFrom(settings());
    form.peers!.perTorrent = 1;
    expect(validateAll(form).map((p) => p.page)).toEqual(['torrents']);
  });

  it('writes them back, empty boxes as 0', () => {
    const current = settings();
    const form = formFrom(current);
    form.peers = { ...form.peers!, dht: false, encryption: 'off', useProxy: true, seedsAtOnce: 3, perTorrent: null, total: 500 };
    applyForm(current, form);
    expect([current.torrentDht, current.torrentEncryption, current.torrentUseProxy, current.torrentSeedsAtOnce, current.torrentConnectionsPerTorrent, current.torrentMaxConnections])
      .toEqual([false, 'off', true, 3, 0, 500]);
  });

  it("from the Windows app's web UI the peer settings are greyed with a note, and a refusal names them", () => {
    expect(settingsSource).toContain("disabled={!form.torrentsEnabled || pcOnly}");
    expect(settingsSource).toContain('Finding peers and privacy');
    expect(friendlyRefusal('These settings can only be changed in the app on this PC: torrentDht, torrentUseProxy.'))
      .toBe('These settings can only be changed in the app on this PC: DHT, Use the proxy for torrents.');
  });
});

describe('the add dialog and Files tab use the priority tree (T3)', () => {
  it('sends the folder for the free space and the priorities with the add', () => {
    expect(addSource).toContain("'torrent.inspect', { ...inputFields(input), destination: folder }");
    expect(addSource).toContain('priorities: pick.priorities');
    expect(addSource).toContain('<TorrentFileTree');
    expect(filesPanelSource).toContain('<TorrentFileTree');
    expect(filesPanelSource).toContain('fileIndexes: [], priorities: changes');
  });
});
