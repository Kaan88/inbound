import { describe, expect, it } from 'vitest';
import { addressLabel, detailRows, pollsSegments, shownAddress } from '../src/lib/details';
import { fileCommands, jobView, planFileMenu, searchText, statusWords } from '../src/lib/list';
import { emptyModel } from '../src/lib/model';
import {
  addLines, addedWords, blocksAddressCommands, canAddLines, canChooseFiles, canChooseFilesFor, chipText, choiceIndexes, choiceSaid, chosenLines, defaultRoute,
  fileProgress, fileRowName, fileRowStatus, filesRefreshMs, filesSummary, filesTabNote, hereName, indexesOf, inspected, lineDetail, lineName, lineState, lineTip,
  magnetNoteApplies, magnetsIn, moreFiles, newLines, nextChoice, picks, primaryIsChooseFiles, primaryText, problem, providerInputs, selectAllState, setFileChoice,
  setRoute, shortMagnet, showsFiles, sidebarShows, statusLine, summary, syncPaste, toggleAll, torrentProvider, torrentStatusText, torrentsApply, treeFiles,
  wantedIds, mostLines, type TorrentLines,
} from '../src/lib/torrents';
import type { Job, RemoteAccount, Torrent, TorrentFile, TorrentInspectResult } from '../src/lib/types';
import { buildRows, counts, inView, parseViewKey, torrentFileRows } from '../src/lib/views';

const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';
const magnet = `magnet:?xt=urn:btih:${hash}&dn=debian.iso&tr=http%3A%2F%2Ftracker.example%3A6969%2Fannounce`;
const magnet2 = 'magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Sintel';

function torrent(extra: Partial<Torrent> = {}): Torrent {
  return { infoHash: hash, name: 'Big Buck Bunny', hasMetadata: true, awaitingFileChoice: false, fileCount: 4, wantedCount: 4, totalBytes: 2000, wantedBytes: 2000, peers: 41, seeds: 12, ...extra };
}

function job(id: string, state: string, extra: Partial<Job> = {}): Job {
  return {
    id, url: `https://example.com/${id}`, fileName: `${id}.bin`, destinationDirectory: '/d', finalFilePath: `/d/${id}.bin`, receivedBytes: 0,
    state, createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', queueId: 0, ...extra,
  };
}

function tjob(id: string, state: string, t: Partial<Torrent> = {}, extra: Partial<Job> = {}): Job {
  return job(id, state, { url: magnet, fileName: 'Big Buck Bunny', torrent: torrent(t), ...extra });
}

const files: TorrentFile[] = [
  { index: 0, path: 'Big Buck Bunny/bbb_1080p.mp4', size: 355, wanted: true, done: 250 },
  { index: 1, path: 'Big Buck Bunny/bbb_2160p.mp4', size: 671, wanted: true, done: 671 },
  { index: 2, path: 'Big Buck Bunny/bbb_stereo.mp4', size: 928, wanted: false, done: 0 },
  { index: 3, path: 'Big Buck Bunny/readme.txt', size: 1, wanted: true },
];

function account(extra: Partial<RemoteAccount> = {}): RemoteAccount {
  return { provider: 'torbox', providerName: 'TorBox', capabilities: ['torrents', 'hosters'], itemKinds: [], hasKey: true, keyUnreadable: false, premium: true, ...extra };
}

describe('when torrents show (TorrentRules.Applies)', () => {
  it('needs both the hello capability and the setting', () => {
    expect(torrentsApply(['jobs', 'torrents'], { torrentsEnabled: true })).toBe(true);
    expect(torrentsApply(['jobs', 'torrents'], { torrentsEnabled: false })).toBe(false);
    expect(torrentsApply(['jobs'], { torrentsEnabled: true })).toBe(false);
    expect(torrentsApply(null, null)).toBe(false);
  });

  it('keeps the Torrents view while torrent jobs are listed', () => {
    expect(sidebarShows(false, true)).toBe(true);
    expect(sidebarShows(true, false)).toBe(true);
    expect(sidebarShows(false, false)).toBe(false);
  });

  it('takes a provider only with a usable key and torrents', () => {
    expect(torrentProvider(true, [account()])?.providerName).toBe('TorBox');
    expect(torrentProvider(false, [account()])).toBeNull();
    expect(torrentProvider(true, [account({ hasKey: false })])).toBeNull();
    expect(torrentProvider(true, [account({ keyUnreadable: true })])).toBeNull();
    expect(torrentProvider(true, [account({ capabilities: ['hosters'] })])).toBeNull();
  });

  it('names the machine by the host kind', () => {
    expect(hereName('windows').short).toBe('This PC');
    expect(hereName('server').short).toBe('This server');
    expect(hereName(undefined).own).toBe("the server's");
  });
});

describe('a torrent row (TorrentRules)', () => {
  it('says peers while it runs, and the file choice it waits for', () => {
    expect(torrentStatusText(tjob('a', 'downloading'))).toBe('Downloading · 41 peers');
    expect(torrentStatusText(tjob('a', 'connecting', { hasMetadata: false, peers: 1 }))).toBe('Getting file list · 1 peer');
    expect(torrentStatusText(tjob('a', 'paused', { awaitingFileChoice: true }))).toBe('Choose files to download');
    expect(torrentStatusText(tjob('a', 'paused'))).toBeNull();
    expect(torrentStatusText({ ...tjob('a', 'paused'), failure: { kind: 'diskFull', message: 'The disk is full' } as Job['failure'] })).toBe('Paused · The disk is full');
    expect(torrentStatusText(job('a', 'downloading'))).toBeNull();
    expect(statusWords(tjob('a', 'downloading'))).toBe('Downloading · 41 peers');
    expect(statusWords(tjob('a', 'paused'))).toBe('Paused');
  });

  it('summarises the files', () => {
    expect(filesSummary(torrent({ fileCount: 11, wantedCount: 3 }))).toBe('3 of 11 files');
    expect(filesSummary(torrent({ fileCount: 11, wantedCount: 11 }))).toBe('11 files');
    expect(filesSummary(torrent({ fileCount: 1, wantedCount: 1 }))).toBe('1 file');
    expect(filesSummary(torrent({ hasMetadata: false }))).toBe('File list not known yet');
  });

  it('never shows the trackers of the magnet', () => {
    const t = tjob('a', 'downloading', { fileCount: 11, wantedCount: 3 });
    const v = jobView(t);
    expect(v.detail).toBe(shortMagnet(t.torrent!));
    expect(v.detail).not.toContain('tr=');
    expect(v.description).toBe('3 of 11 files');
    expect(searchText(v, t)).not.toContain('tracker.example');
    expect(shownAddress(t)).toBe(`magnet:?xt=urn:btih:${hash}`);
    expect(addressLabel(t)).toBe('Magnet');
  });

  it('keeps the user’s own description', () => {
    expect(jobView(tjob('a', 'downloading', {}, { description: 'mine' })).description).toBe('mine');
  });

  it('names a torrent without a file name by its torrent name, never by its magnet', () => {
    expect(jobView(tjob('a', 'connecting', {}, { fileName: '' })).name).toBe('Big Buck Bunny');
    expect(jobView(tjob('a', 'connecting', { name: '' }, { fileName: '' })).name).toBe('');
  });

  it('counts the active torrents and their peers for the chip', () => {
    expect(chipText([tjob('a', 'downloading', { peers: 41 }), tjob('b', 'connecting', { peers: 6 }), tjob('c', 'paused'), job('d', 'downloading')])).toBe('Torrents: 2 active · 47 peers');
    expect(chipText([tjob('c', 'paused'), job('d', 'downloading')])).toBeNull();
  });

  it('opens only with several known files', () => {
    expect(showsFiles(torrent())).toBe(true);
    expect(showsFiles(torrent({ fileCount: 1 }))).toBe(false);
    expect(showsFiles(torrent({ hasMetadata: false }))).toBe(false);
    expect(showsFiles(undefined)).toBe(false);
  });

  it('opens the file choice on Enter when it waits for one', () => {
    expect(primaryIsChooseFiles(tjob('a', 'paused', { awaitingFileChoice: true }))).toBe(true);
    expect(primaryIsChooseFiles(tjob('a', 'paused'))).toBe(false);
    expect(primaryIsChooseFiles(job('a', 'paused'))).toBe(false);
  });

  it('offers Choose files for one unfinished torrent with its files', () => {
    expect(canChooseFiles([tjob('a', 'downloading')])).toBe(true);
    expect(canChooseFiles([tjob('a', 'completed')])).toBe(false);
    expect(canChooseFiles([tjob('a', 'cancelled')])).toBe(false);
    expect(canChooseFiles([tjob('a', 'connecting', { hasMetadata: false })])).toBe(false);
    expect(canChooseFiles([tjob('a', 'paused'), tjob('b', 'paused')])).toBe(false);
    expect(canChooseFiles([job('a', 'paused')])).toBe(false);
  });

  it('hides Move/Rename, Redownload and Refresh address for a torrent', () => {
    const t = tjob('a', 'completed');
    const commands = fileCommands(t, planFileMenu(true, false, 0, 0), 1, blocksAddressCommands([t]));
    for (const command of [commands.move, commands.redownload, commands.refresh]) {
      expect(command.hidden).toBe(true);
      expect(command.enabled).toBe(false);
    }
    const plain = fileCommands(job('b', 'completed'), planFileMenu(true, false, 0, 0), 1);
    expect(plain.move.hidden).toBe(false);
    expect(plain.redownload.enabled).toBe(true);
  });
});

describe('the list with torrents', () => {
  function model(...jobs: Job[]) {
    const m = emptyModel();
    for (const j of jobs) m.jobs[j.id] = j;
    return m;
  }

  it('has a Torrents view in every state', () => {
    expect(parseViewKey('torrents')).toBe('torrents');
    expect(inView(tjob('a', 'completed'), 'torrents')).toBe(true);
    expect(inView(job('b', 'completed'), 'torrents')).toBe(false);
    const m = model(tjob('a', 'downloading'), tjob('b', 'completed'), job('c', 'downloading'));
    expect(counts(m).torrents).toBe(2);
    expect(buildRows(m, 'torrents', '', new Set()).map((r) => r.id).sort()).toEqual(['a', 'b']);
  });

  it('lists an open torrent’s files under it, and asks for them first', () => {
    const m = model(tjob('a', 'downloading'));
    expect(buildRows(m, 'all', '', new Set()).map((r) => r.kind)).toEqual(['job']);
    const waiting = buildRows(m, 'all', '', new Set(['a']));
    expect(waiting.map((r) => r.view.name)).toEqual(['Big Buck Bunny', 'Getting the file list…']);
    const rows = buildRows(m, 'all', '', new Set(['a']), undefined, { a: { files } });
    expect(rows.map((r) => r.view.name)).toEqual(['Big Buck Bunny', 'bbb_1080p.mp4', 'bbb_2160p.mp4', 'bbb_stereo.mp4', 'readme.txt']);
    expect(rows.slice(1).map((r) => r.view.status)).toEqual(['70%', 'Done', 'Skipped', 'Downloading']);
    const failed = buildRows(m, 'all', '', new Set(['a']), undefined, { a: { error: 'gone' } });
    expect(failed[1].view.name).toBe("Couldn't get the file list: gone");
  });

  it('does not open a torrent of one file', () => {
    const m = model(tjob('a', 'downloading', { fileCount: 1, wantedCount: 1 }));
    expect(buildRows(m, 'all', '', new Set(['a']), undefined, { a: { files: [files[0]] } })).toHaveLength(1);
  });

  it('caps the file rows and says how many more', () => {
    const many: TorrentFile[] = Array.from({ length: 205 }, (_, index) => ({ index, path: `t/f${index}.bin`, size: 10, wanted: true }));
    const rows = torrentFileRows(tjob('a', 'downloading', { fileCount: 205, wantedCount: 205 }), { files: many });
    expect(rows).toHaveLength(201);
    expect(rows[200].view.name).toBe('5 more files: open Progress Details');
    expect(moreFiles(1)).toBe('1 more file: open Progress Details');
  });
});

describe('file progress (TorrentRules.FileProgress)', () => {
  it('reads skipped, done and percent', () => {
    expect(fileProgress(files[0])).toBe('70%');
    expect(fileProgress(files[1])).toBe('Done');
    expect(fileProgress(files[2])).toBe('Skipped');
    expect(fileProgress(files[3])).toBe('');
    expect(fileProgress(files[3], true)).toBe('Done');
    expect(fileProgress({ index: 9, path: 'x', size: 100, wanted: true, done: 999 })).toBe('Done');
    expect(fileProgress({ index: 9, path: 'x', size: 1000, wanted: true, done: 999 })).toBe('99%');
    expect(fileRowStatus(files[3], 'Paused')).toBe('Paused');
  });

  it('names a file row by the last part of its path', () => {
    expect(fileRowName('a/b/c.txt')).toBe('c.txt');
    expect(fileRowName('c.txt')).toBe('c.txt');
    expect(fileRowName('a/b/')).toBe('b');
  });

  it('reads big lists less often', () => {
    expect(filesRefreshMs(1000)).toBe(3000);
    expect(filesRefreshMs(1001)).toBe(30000);
  });

  it('says what a choice does', () => {
    expect(choiceSaid('Sintel', 3, 11)).toBe('Sintel: downloading 3 files of 11.');
    expect(choiceSaid('Sintel', 11, 11)).toBe('Sintel: downloading all 11 files.');
    expect(choiceSaid('One', 1, 1)).toBe('One: downloading all 1 file.');
  });

  it('keeps at least one file', () => {
    expect(nextChoice(new Set([1]), 1, false)).toBeNull();
    expect(nextChoice(new Set([1]), 0, true)).toEqual([0, 1]);
    expect(nextChoice(new Set([0, 1]), 0, false)).toEqual([1]);
  });

  it('says what the Files tab waits for', () => {
    expect(filesTabNote(tjob('a', 'connecting', { hasMetadata: false }), false)).toBe('The file list is not in yet. It comes from the peers.');
    expect(filesTabNote(tjob('a', 'paused'), true)).toBe('Choose the files to download, then Start.');
    expect(filesTabNote(tjob('a', 'paused'), false)).toBe('');
  });
});

describe('the file tree and the choice', () => {
  it('uses the index as the id', () => {
    expect(treeFiles(files).map((f) => f.fileId)).toEqual(['0', '1', '2', '3']);
    expect([...wantedIds(files)]).toEqual(['0', '1', '3']);
    expect(indexesOf(new Set(['3', '0']), files)).toEqual([0, 3]);
    expect(indexesOf(new Set(['x']), files)).toBeNull();
    expect(indexesOf(new Set(['42']), files)).toEqual([]);
  });

  it('counts every file chosen when every file with bytes is ticked', () => {
    const withEmpty = [...files, { index: 4, path: 'Big Buck Bunny/empty', size: 0, wanted: true }];
    expect(choiceIndexes(new Set(['0', '1', '2', '3']), withEmpty)).toEqual([0, 1, 2, 3, 4]);
    expect(choiceIndexes(new Set(['0']), withEmpty)).toEqual([0]);
  });
});

describe('the details', () => {
  it('shows the torrent summary', () => {
    const t = tjob('a', 'downloading', { peers: 41, seeds: 12, fileCount: 11, wantedCount: 3, wantedBytes: 1000 }, { receivedBytes: 400, totalBytes: 1000, bytesPerSecond: 1024 });
    const rows = Object.fromEntries(detailRows(t).map((row) => [row.label, row.value]));
    expect(rows.Status).toBe('Downloading · 41 peers');
    expect(rows.Peers).toBe('41 (12 seeds)');
    expect(rows.Files).toBe('3 of 11 files');
    expect(rows['Info hash']).toBe(hash);
    expect(rows.Downloaded).toBe('400 B of 1000 B');
    expect(rows['Resume capability']).toBeUndefined();
    expect(pollsSegments(t)).toBe(false);
    expect(pollsSegments(job('b', 'downloading'))).toBe(true);
  });

  it('says a magnet’s size is not known yet', () => {
    const rows = Object.fromEntries(detailRows(tjob('a', 'connecting', { hasMetadata: false, wantedBytes: 0 })).map((row) => [row.label, row.value]));
    expect(rows.Size).toBe('Not known yet');
    expect(rows.Files).toBe('File list not known yet');
  });
});

describe('the add dialog (TorrentAddLines)', () => {
  const ok = (extra: Partial<TorrentInspectResult> = {}): TorrentInspectResult => ({ ok: true, kind: 'magnet', infoHash: hash, name: 'debian.iso', ...extra });
  const fileResult = (extra: Partial<TorrentInspectResult> = {}): TorrentInspectResult =>
    ok({ kind: 'torrentFile', infoHash: 'aa'.repeat(20), name: 'Big Buck Bunny', totalBytes: 1955, files, ...extra });

  function start(route = 'ask', chooseFiles = true, provider: string | null = null): TorrentLines {
    return newLines(defaultRoute(route, provider !== null), chooseFiles, provider);
  }

  it('picks the default route from the setting, only with a provider', () => {
    expect(defaultRoute('ask', true)).toBe('unset');
    expect(defaultRoute('thisPc', true)).toBe('thisPc');
    expect(defaultRoute('provider', true)).toBe('provider');
    expect(defaultRoute('provider', false)).toBe('thisPc');
    expect(newLines('unset', true, null).defaultRoute).toBe('thisPc');
  });

  it('finds magnets in pasted text, each once', () => {
    expect(magnetsIn(`see ${magnet}, and ${magnet2}\n${magnet}`)).toEqual([magnet, magnet2]);
    expect(magnetsIn('https://example.com/a.torrent')).toEqual([]);
  });

  it('follows the paste box and keeps uploaded files', () => {
    let s = addLines(start(), [{ input: 'upload:abc', label: 'bbb.torrent' }], false).state;
    s = syncPaste(s, `${magnet}\n${magnet2}`).state;
    expect(s.lines.map((l) => l.input)).toEqual(['upload:abc', magnet, magnet2]);
    s = syncPaste(s, magnet2).state;
    expect(s.lines.map((l) => l.input)).toEqual(['upload:abc', magnet2]);
  });

  it('ignores what is no magnet or upload, and caps the lines', () => {
    expect(addLines(start(), [{ input: 'https://example.com/x' }], true).added).toHaveLength(0);
    const many = Array.from({ length: mostLines + 3 }, (_, i) => ({ input: `upload:${i}` }));
    const s = addLines(start(), many, false).state;
    expect(s.lines).toHaveLength(mostLines);
    expect(s.capped).toBe(true);
    expect(statusLine(s)).toContain(`Only the first ${mostLines} are listed.`);
  });

  it('reads a line: checking, then its size and files', () => {
    let s = addLines(start(), [{ input: magnet }, { input: 'upload:abc', label: 'bbb.torrent' }], true).state;
    expect(lineDetail(s.lines[0])).toBe('Magnet · Checking…');
    expect(lineDetail(s.lines[1])).toBe('.torrent file · Reading…');
    expect(lineName(s.lines[0])).toBe('Magnet link');
    expect(lineName(s.lines[1])).toBe('bbb.torrent');
    expect(canAddLines(s)).toBe(false);
    s = inspected(s, magnet, ok());
    s = inspected(s, 'upload:abc', fileResult());
    expect(lineDetail(s.lines[0])).toBe('Magnet · Size not known yet');
    expect(lineDetail(s.lines[1])).toBe('.torrent file · 1.91 KB · 4 files');
    expect(lineTip(s.lines[0])).toBe(`magnet:?xt=urn:btih:${hash}`);
    expect(canAddLines(s)).toBe(true);
    expect(primaryText(s)).toBe('Add 2');
    expect(summary(s)).toBe('Selected 1.91 KB in 2 torrents');
  });

  it('marks failures, torrents already listed and the same torrent twice', () => {
    let s = addLines(start(), [{ input: magnet }, { input: 'upload:a' }, { input: 'upload:b' }, { input: 'upload:c' }], false).state;
    s = inspected(s, magnet, ok());
    s = inspected(s, 'upload:a', ok({ kind: 'torrentFile' }));
    s = inspected(s, 'upload:b', { ok: false, error: 'This is not a .torrent file.' });
    s = inspected(s, 'upload:c', fileResult({ existingJobId: 'job-1' }));
    expect(s.lines.map(lineState)).toEqual(['ready', 'duplicate', 'failed', 'listed']);
    expect(lineDetail(s.lines[1])).toBe('.torrent file · Same torrent as above');
    expect(lineDetail(s.lines[2])).toBe('This is not a .torrent file.');
    expect(lineDetail(s.lines[3])).toBe('.torrent file · Already in the list');
    expect(chosenLines(s).map((l) => l.input)).toEqual([magnet]);
    expect(inspected(s, 'upload:zz', ok())).toBe(s);
  });

  it('asks for a route per line with a provider on Ask each time', () => {
    let s = addLines(start('ask', true, 'TorBox'), [{ input: magnet }], true).state;
    s = inspected(s, magnet, ok());
    expect(canAddLines(s)).toBe(false);
    expect(problem(s)).toBe('Choose This PC or TorBox for each ticked torrent.');
    s = setRoute(s, magnet, 'provider');
    expect(canAddLines(s)).toBe(true);
    expect(picks(s)).toEqual([{ input: magnet, name: 'debian.iso', route: 'provider', files: 'all', fileIndexes: null, priorities: null }]);
    expect(providerInputs(picks(s))).toEqual([magnet]);
    expect(setRoute(start(), magnet, 'provider')).toEqual(start());
  });

  it('sends a magnet with askWhenReady when files are chosen first, and all otherwise', () => {
    let s = inspected(addLines(start(), [{ input: magnet }], true).state, magnet, ok());
    expect(magnetNoteApplies(s)).toBe(true);
    expect(picks(s)[0].files).toBe('askWhenReady');
    s = inspected(addLines(start('ask', false), [{ input: magnet }], true).state, magnet, ok());
    expect(magnetNoteApplies(s)).toBe(false);
    expect(picks(s)[0].files).toBe('all');
  });

  it('lets a multi-file .torrent choose its files', () => {
    let s = inspected(addLines(start(), [{ input: 'upload:abc' }], false).state, 'upload:abc', fileResult());
    expect(canChooseFilesFor(s, s.lines[0])).toBe(true);
    s = setFileChoice(s, 'upload:abc', new Set(['0', '1']));
    expect(lineDetail(s.lines[0])).toBe('.torrent file · 1.00 KB of 1.91 KB · 2 of 4 files');
    expect(picks(s)[0]).toMatchObject({ files: 'chosen', fileIndexes: [0, 1] });
    s = setFileChoice(s, 'upload:abc', new Set());
    expect(canAddLines(s)).toBe(false);
    expect(problem(s)).toBe('Choose at least one file of Big Buck Bunny.');
    s = setFileChoice(s, 'upload:abc', new Set(['0', '1', '2', '3']));
    expect(s.lines[0].chosen).toBeNull();
    expect(picks(s)[0].files).toBe('all');
  });

  it('chooses no files with the setting off, or for one file, or on the provider', () => {
    const one = fileResult({ files: [files[0]] });
    expect(canChooseFilesFor(start('ask', false), inspected(addLines(start('ask', false), [{ input: 'upload:a' }], false).state, 'upload:a', fileResult()).lines[0])).toBe(false);
    const s = inspected(addLines(start(), [{ input: 'upload:a' }], false).state, 'upload:a', one);
    expect(canChooseFilesFor(s, s.lines[0])).toBe(false);
  });

  it('ticks all, none and some', () => {
    let s = addLines(start(), [{ input: magnet }, { input: magnet2 }], true).state;
    s = inspected(inspected(s, magnet, ok()), magnet2, ok({ infoHash: 'dd'.repeat(20), name: 'Sintel' }));
    expect(selectAllState(s)).toBe(true);
    s = toggleAll(s);
    expect(selectAllState(s)).toBe(false);
    expect(summary(s)).toBe('No torrents selected');
    expect(canAddLines(s)).toBe(false);
    s = toggleAll(s);
    expect(selectAllState(s)).toBe(true);
  });

  it('words the result as the desktop does', () => {
    expect(addedWords(0, 0)).toBeNull();
    expect(addedWords(1, 0)).toBe('Added 1 torrent.');
    expect(addedWords(3, 0)).toBe('Added 3 torrents.');
    expect(addedWords(0, 1)).toBe('That torrent is already in the list.');
    expect(addedWords(0, 2)).toBe('Those 2 torrents are already in the list.');
    expect(addedWords(2, 1)).toBe('Added 2 torrents; 1 were already in the list.');
  });
});
