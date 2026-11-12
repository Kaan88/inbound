import { describe, expect, it } from 'vitest';
import {
  basicAuthorization, categoryChoices, categoryOf, checkButtonText, metadataTitle, chunkTorrentEntries, destinationFor, findExisting, importSummary, isTorrentEntry, metadataText, nameFromUrl, packageNameFor, parseList,
  serializeList, serverNameToShow, spaceLine, validUserName,
} from '../src/lib/addflow';
import { routeOf, type AddLine } from '../src/lib/addlines';
import {
  addReport, addSummary, addedWords, alreadyListed, canAdd, chooseLaterApplies, chosen, defaultSelected, extractInputs, fileChoiceFrom, filesToChoose,
  isFullPath, lineWords, newLine, removeInputs, selectAllState, spaceSize, toggleAll, withDefaults,
} from '../src/lib/addview';
import { opensAddUrl, submitsOnEnter } from '../src/lib/keys';
import type { ClassifyResult, Job, Settings } from '../src/lib/types';

function answered(input: string, result: Partial<ClassifyResult>, extra: Partial<AddLine> = {}): AddLine {
  return { ...newLine(input, 1), result: { ok: true, kind: 'ordinaryLink', checkIncomplete: false, ...result } as ClassifyResult, ...extra };
}

describe('extractInputs (A12)', () => {
  it('finds links and magnets in free text and trims trailing punctuation', () => {
    const text = 'See https://a.example/f.zip, and (https://b.example/x?y=1). Also magnet:?xt=urn:btih:abc&dn=n! or ftp://c.example/z;';
    expect(extractInputs(text)).toEqual(['https://a.example/f.zip', 'https://b.example/x?y=1', 'magnet:?xt=urn:btih:abc&dn=n', 'ftp://c.example/z']);
  });

  it('lists each once, keeps case-different links apart and ignores plain text', () => {
    expect(extractInputs('https://h/aB https://h/AB https://h/aB\njust words\n')).toEqual(['https://h/aB', 'https://h/AB']);
    expect(extractInputs('nothing here')).toEqual([]);
    expect(extractInputs(null)).toEqual([]);
  });

  it('takes at most 1000', () => {
    expect(extractInputs(Array.from({ length: 1200 }, (_, i) => `https://h/${i}`).join(' '))).toHaveLength(1000);
  });
});

describe('ticks (A11)', () => {
  it('starts hosters reported down and listed direct links unticked', () => {
    expect(defaultSelected(answered('x', { kind: 'hosterLink', hosterAvailable: false }), true)).toBe(false);
    expect(defaultSelected(answered('x', { kind: 'hosterLink', hosterDailyLinksLeft: 0 }), true)).toBe(false);
    expect(defaultSelected(answered('x', { kind: 'hosterLink', hosterAvailable: true }), true)).toBe(true);
    expect(defaultSelected(answered('x', { kind: 'folderLink', itemId: '1', itemKind: 'torrent', provider: 'p' }), true)).toBe(true);
    expect(defaultSelected(answered('https://a/b', { kind: 'ordinaryLink' }, { listed: true }), true)).toBe(false);
    expect(defaultSelected(answered('https://a/b', { kind: 'ordinaryLink' }, { listed: true }), false)).toBe(true);
    expect(defaultSelected({ ...newLine('x', 1) }, true)).toBe(true);
    expect(defaultSelected(answered('x', { kind: 'unsupported' }), true)).toBe(false);
  });

  it('keeps what the user ticked when an answer arrives, but never leaves an unusable line ticked', () => {
    const ticked = answered('x', { kind: 'hosterLink', hosterAvailable: false }, { touched: true, selected: true });
    expect(withDefaults([ticked], true)[0].selected).toBe(true);
    const gone = answered('y', { kind: 'unsupported' }, { touched: true, selected: true });
    expect(withDefaults([gone], true)[0].selected).toBe(false);
  });

  it('counts only ticked lines that go somewhere and waits for ticked ones still being checked', () => {
    const a = answered('https://a/1', { kind: 'ordinaryLink' });
    const b = { ...newLine('https://a/2', 2) };
    expect(canAdd([a])).toBe(true);
    expect(canAdd([a, b])).toBe(false);
    expect(canAdd([{ ...b, selected: false }, { ...a, selected: false }])).toBe(false);
    expect(chosen([a, b, answered('z', { kind: 'unsupported' }, { selected: true })])).toEqual([a]);
  });

  it('Select all is mixed in between and goes all on from mixed', () => {
    const on = answered('https://a/1', {});
    const off = answered('https://a/2', {}, { selected: false });
    expect(selectAllState([on, on])).toBe(true);
    expect(selectAllState([off, off])).toBe(false);
    expect(selectAllState([on, off])).toBeNull();
    expect(toggleAll([on, off]).every((line) => line.selected)).toBe(true);
    expect(toggleAll([on, on]).every((line) => !line.selected)).toBe(true);
    expect(selectAllState([answered('z', { kind: 'unsupported' })])).toBe(false);
  });
});

describe('summary and words (A11, A13)', () => {
  it('writes the status line like the desktop', () => {
    expect(addSummary([], true).text).toBe('No links were found in this text.');
    expect(addSummary([], true, true).text).toBe('No magnet was found in this text.');
    expect(addSummary([], false).text).toBe('');
    expect(addSummary([newLine('a', 1), newLine('b', 2)], true).text).toBe('Checking 2 of 2…');
    const lines = [answered('https://a/1', {}), answered('z', { kind: 'unsupported' }, { selected: false })];
    expect(addSummary(lines, true).text).toBe("1 of 2 selected. 1 can't be added as it is.");
  });

  it('says what a line is, with the provider named', () => {
    const hoster = lineWords(answered('https://h.example/f/KEY', { kind: 'hosterLink', providerName: 'TorBox', hosterAvailable: false, displayHost: 'h.example' }), 'TorBox', true);
    expect(hoster.word).toBe('Hoster through TorBox');
    expect(hoster.problem).toBe(true);
    expect(hoster.canTakeLinkPassword).toBe(true);
    expect(hoster.detail).toContain("isn't available right now");
    expect(hoster.name).not.toContain('KEY');
    const ordinary = lineWords(answered('https://a.example/dir/file%20one.zip', { kind: 'ordinaryLink' }, { listed: true }), 'TorBox');
    expect(ordinary).toMatchObject({ name: 'file one.zip', word: 'Ordinary link', detail: 'Already in the list' });
    expect(lineWords(answered('x', { kind: 'magnet', name: 'Disc' }), 'TorBox').name).toBe('Disc');
    expect(lineWords({ ...answered('upload:abc', { kind: 'torrentFile' }), label: 'a.torrent' }, 'TorBox').name).toBe('a.torrent');
  });

  it('offers a direct download only for a web link nobody could classify, and says so after it is chosen', () => {
    const unsure = answered('https://a/b', { kind: 'undetermined', reason: 'No answer.' });
    const words = lineWords(unsure, 'TorBox');
    expect(words).toMatchObject({ word: "Can't tell", canRetry: true, canChooseDirect: true, problem: true });
    expect(lineWords(answered('magnet:?x', { kind: 'undetermined' }), 'TorBox').canChooseDirect).toBe(false);
    const chosenDirect = lineWords({ ...unsure, direct: true }, 'TorBox');
    expect(chosenDirect.detail).toBe('It will download directly, not through TorBox, because you chose that.');
    expect(chosenDirect.problem).toBe(false);
    expect(routeOf({ ...unsure, direct: true })).toBe('direct');
  });
});

describe('files before adding (A18)', () => {
  const files = [
    { fileId: '1', relativePath: 'a/one.bin', size: 10, infected: false, selectable: true },
    { fileId: '2', relativePath: 'a/two.bin', size: 20, infected: false, selectable: true },
  ];

  it('asks for the files of a magnet or torrent that lists more than one', () => {
    expect(filesToChoose(answered('magnet:?x', { kind: 'magnet', files }))).toHaveLength(2);
    expect(filesToChoose(answered('magnet:?x', { kind: 'magnet', files: files.slice(0, 1) }))).toBeNull();
    expect(filesToChoose(answered('https://h/f', { kind: 'hosterLink', files }))).toBeNull();
  });

  it('sends all, ids for a magnet, and path and size for a torrent file', () => {
    const magnet = answered('magnet:?x', { kind: 'magnet', files });
    expect(fileChoiceFrom(magnet, new Set(['1', '2']))).toEqual({ selection: 'all' });
    expect(fileChoiceFrom(magnet, new Set(['2']))).toEqual({ selection: 'chosen', fileIds: ['2'] });
    const torrent = answered('upload:x', { kind: 'torrentFile', files });
    expect(fileChoiceFrom(torrent, new Set(['1']))).toEqual({ selection: 'chosen', filePaths: [{ path: 'a/one.bin', size: 10 }] });
  });

  it('offers "choose when done" only for a ticked magnet or torrent whose files are not known and not cached', () => {
    expect(chooseLaterApplies(answered('magnet:?x', { kind: 'magnet' }))).toBe(true);
    expect(chooseLaterApplies(answered('magnet:?x', { kind: 'magnet', cached: true }))).toBe(false);
    expect(chooseLaterApplies(answered('magnet:?x', { kind: 'magnet', files }))).toBe(false);
    expect(chooseLaterApplies(answered('magnet:?x', { kind: 'magnet' }, { selected: false }))).toBe(false);
    expect(chooseLaterApplies(answered('https://h/f', { kind: 'hosterLink' }))).toBe(false);
  });
});

describe('the words after an add (A25)', () => {
  it('counts downloads and provider items', () => {
    expect(addedWords(0, 0, 'TorBox')).toBe('');
    expect(addedWords(1, 0, 'TorBox')).toBe('Added 1 download.');
    expect(addedWords(3, 2, 'TorBox')).toBe('Added 3 downloads and 2 items for TorBox.');
    expect(addedWords(0, 1, 'TorBox')).toBe('Added 1 item for TorBox.');
  });

  it('lists every problem in one message and never calls a repeat "added"', () => {
    const problems = ['a: no', 'b: no', 'c: no', 'd: no', 'e: no'];
    expect(addReport('Added 1 download.', ['note'], problems).split('\n')).toEqual(['note', 'Added 1 download.', ...problems]);
    expect(alreadyListed('x', null)).toBe('x: already on the list.');
    expect(alreadyListed('x', 'It was already on the list.')).toBe('x: It was already on the list.');
  });

  it('takes the links that were handled out of the text and keeps the rest as typed', () => {
    const text = 'first https://a/1\nhttps://a/2\n\nkeep this https://a/3 line';
    expect(removeInputs(text, ['https://a/2', 'https://a/3'])).toBe('first https://a/1\n\nkeep this  line');
  });

  it('accepts only a full path for the provider', () => {
    expect(isFullPath('/downloads')).toBe(true);
    expect(isFullPath('D:\\Downloads')).toBe(true);
    expect(isFullPath('\\\\nas\\share')).toBe(true);
    expect(isFullPath('downloads')).toBe(false);
  });
});

describe('packages and duplicates (A10, A15)', () => {
  it('names a package of archive parts after the archive and otherwise leaves it to the server', () => {
    expect(packageNameFor(['https://h/Big.part1.rar', 'https://h/Big.part2.rar'])).toBe('Big');
    expect(packageNameFor(['https://h/Big.part1.rar', 'https://h/Other.part1.rar'])).toBeUndefined();
    expect(packageNameFor(['https://h/Big.part1.rar', 'https://h/readme.txt'])).toBeUndefined();
    expect(packageNameFor(['https://h/a.zip', 'https://h/A.ZIP'])).toBe('a');
  });

  it('finds a download already on the list unless it was cancelled', () => {
    const jobs = [
      { id: '1', url: 'https://h/a', state: 'cancelled' },
      { id: '2', url: 'HTTPS://H/b', state: 'completed' },
    ] as Job[];
    expect(findExisting(jobs, 'https://h/a')).toBeUndefined();
    expect(findExisting(jobs, 'https://h/B')?.id).toBe('2');
  });
});

describe('categories and folders (A5, A6)', () => {
  const settings = {
    defaultDownloadDirectory: '/downloads',
    useCategoryFolders: true,
    categoryFolders: { video: '/media/video' },
    customCategories: [{ id: 'books', name: 'Books', destinationDirectory: '/media/books', extensions: ['epub2', '.mobi'] }],
  } as unknown as Settings;

  it('lists Automatic, the built-in categories, Other and the custom ones', () => {
    expect(categoryChoices(settings).map((c) => c.name)).toEqual(['Automatic (file extension)', 'Compressed', 'Documents', 'Music', 'Programs', 'Video', 'Other', 'Books']);
  });

  it('picks the category from the file name, custom extensions first', () => {
    expect(categoryOf('a.MP4')).toBe('video');
    expect(categoryOf('a.zip')).toBe('compressed');
    expect(categoryOf('a.unknown')).toBe('other');
    expect(categoryOf('noext')).toBe('other');
    expect(categoryOf('x.mobi', [{ id: 'books', name: 'Books', extensions: ['.mobi'] }])).toBe('books');
  });

  it("uses the category's folder when the setting is on, else the default", () => {
    expect(destinationFor(settings, 'movie.mkv', null)).toBe('/media/video');
    expect(destinationFor(settings, 'x.mobi', null)).toBe('/media/books');
    expect(destinationFor(settings, 'movie.mkv', 'books')).toBe('/media/books');
    expect(destinationFor(settings, 'song.mp3', null)).toBe('/downloads');
    expect(destinationFor({ ...settings, useCategoryFolders: false } as Settings, 'movie.mkv', null)).toBe('/downloads');
  });

  it('reads the file name from an address', () => {
    expect(nameFromUrl('https://h/dir/my%20file.zip?x=1')).toBe('my file.zip');
    expect(nameFromUrl('not an address')).toBe('');
  });
});

describe('authentication (A8)', () => {
  it('refuses a user name with a colon or a line break, or none', () => {
    expect(validUserName('kaan')).toBe(true);
    expect(validUserName('')).toBe(false);
    expect(validUserName('   ')).toBe(false);
    expect(validUserName('a:b')).toBe(false);
    expect(validUserName('a\nb')).toBe(false);
  });

  it('writes a Basic header from UTF-8 text', () => {
    expect(basicAuthorization('user', 'pass')).toBe('Basic dXNlcjpwYXNz');
    expect(basicAuthorization('üser', 'pä:ss')).toBe('Basic w7xzZXI6cMOkOnNz');
  });
});

describe('free space and file info (A3, A24)', () => {
  it('writes sizes the way the folder line does', () => {
    expect(spaceSize(2048)).toBe('2 KB');
    expect(spaceSize(5 * 1024 * 1024)).toBe('5 MB');
    expect(spaceSize(1.5 * 1024 ** 3)).toBe('1.5 GB');
    expect(spaceSize(2 * 1024 ** 4)).toBe('2.0 TB');
  });

  it('warns about too little room and about a folder that is not on a mounted volume', () => {
    const gb = 1024 ** 3;
    expect(spaceLine(null)).toEqual({ text: 'Free space unknown', problem: false });
    expect(spaceLine({ path: '/d', freeBytes: 10 * gb, totalBytes: 100 * gb })).toEqual({ text: '10.0 GB free of 100.0 GB', problem: false });
    expect(spaceLine({ path: '/d', freeBytes: 1 * gb }, 5 * gb)).toEqual({ text: 'Not enough space: needs 5.0 GB, 1.0 GB free', problem: true });
    expect(spaceLine({ path: '/d', freeBytes: 1 * gb }, 0).problem).toBe(false);
    expect(spaceLine({ path: '/d', freeBytes: 1 * gb, insideContainer: true }).problem).toBe(true);
  });

  it('writes the size as the list does, and the server name only when the box says something else (review 5:A2a, A5)', () => {
    expect(metadataText({ ok: true, totalBytes: 80_000_000, contentType: 'application/zip' }, 'a.zip', 'a.zip')).toBe('76.3 MB');
    expect(metadataText({ ok: true, totalBytes: 1234567, contentType: 'application/zip' }, 'a.zip', 'mine.zip')).toBe('1.18 MB\nThe server names it a.zip');
    expect(metadataText({ ok: true, totalBytes: 1234567 }, 'a.zip', '')).toBe('1.18 MB');
    expect(metadataText({ ok: true }, null, 'x')).toBe('Size unknown');
    expect(metadataTitle({ ok: true, totalBytes: 1234567, contentType: 'application/zip' })).toBe('1,234,567 bytes · application/zip');
    expect(metadataTitle({ ok: true })).toBe('Type unknown');
    expect(checkButtonText(false)).toBe('Check file info');
    expect(checkButtonText(true)).toBe('Check again');
  });

  it('does not show a name that is only the end of a key address', () => {
    expect(serverNameToShow('a.zip', 'https://h/a.zip', false)).toBe('a.zip');
    expect(serverNameToShow('a.zip', 'https://h/KEY/a.zip', true)).toBeNull();
    expect(serverNameToShow('other.zip', 'https://h/KEY/a.zip', true)).toBe('other.zip');
    expect(serverNameToShow(undefined, 'https://h', false)).toBeNull();
  });
});

describe('download lists (A21)', () => {
  const jobs = [
    { id: '1', url: 'https://h/a', fileName: 'a', destinationDirectory: '/d', queueId: 1, requestedConnections: 4, startMode: 'startNow', conflictPolicy: 'rename', receivedBytes: 0, state: 'completed' },
    { id: '2', url: 'https://h/b', isRemote: true, queueId: 1 },
    { id: '3', url: 'https://h/***', addressHidden: true, queueId: 1 },
    { id: '4', url: '', queueId: 1 },
  ] as Job[];

  it('leaves out provider files, masked addresses and empty ones', () => {
    const entries = JSON.parse(serializeList(jobs)) as { url: string }[];
    expect(entries.map((e) => e.url)).toEqual(['https://h/a']);
  });

  it('reads what it wrote and fills the gaps', () => {
    expect(parseList(serializeList(jobs))).toEqual([expect.objectContaining({ url: 'https://h/a', fileName: 'a', queueId: 1, requestedConnections: 4 })]);
    expect(parseList('[{"url":"https://h/x"},{"url":" "},{"nope":1}]')).toEqual([{ url: 'https://h/x', queueId: 0, requestedConnections: 0, startMode: 'later', conflictPolicy: 'rename' }]);
  });

  it('keeps the hand-off content type, so a stream is added as one again (review 1 N6)', () => {
    const stream = [{ id: '5', url: 'https://h/play?id=7', queueId: 0, handoffContentType: 'application/dash+xml', receivedBytes: 0, state: 'paused' }] as Job[];
    expect(parseList(serializeList(stream))).toEqual([expect.objectContaining({ url: 'https://h/play?id=7', contentType: 'application/dash+xml' })]);
    expect(JSON.parse(serializeList(jobs))[0]).not.toHaveProperty('contentType');
  });

  it('refuses a file that is not a list', () => {
    expect(() => parseList('{')).toThrow('The download list is not valid JSON');
    expect(() => parseList('{"a":1}')).toThrow('The download list is empty.');
  });

  it('writes the summary in the desktop words', () => {
    expect(importSummary(3, 3, 0, undefined, 0)).toBe('Added 3 of 3 downloads.');
    expect(importSummary(2, 4, 1, 'Nope', 1)).toBe("Added 2 of 4 downloads directly; 1 failed. Nope 1 entry isn't added yet: the next dialog shows what can be done with it. Saved names, folders and checksums from the file don't carry over to it.");
  });

  it('says how many torrents were skipped and why (T5)', () => {
    expect(importSummary(1, 3, 0, undefined, 0, 2, 'Already in the list.')).toBe('Added 1 of 3 downloads. 2 torrents skipped: Already in the list.');
    expect(importSummary(0, 2, 1, 'No.', 0, 1, 'Torrents are turned off.')).toBe('Added 0 of 2 downloads; 1 failed. No. 1 torrent skipped: Torrents are turned off.');
  });

  it('keeps a torrent with its part, or its magnet alone from an older server (T5)', () => {
    const hash = 'a'.repeat(40);
    const torrent = { infoHash: hash, name: 'Album', hasMetadata: true, awaitingFileChoice: false, fileCount: 3, wantedCount: 2, totalBytes: 3, wantedBytes: 2, peers: 0, seeds: 0 };
    const torrents = [
      { id: 't1', url: `magnet:?xt=urn:btih:${hash}&dn=Album`, queueId: 1, torrent },
      { id: 't2', url: `magnet:?xt=urn:btih:${hash}`, queueId: 1, torrent },
    ] as Job[];
    const text = serializeList([...jobs, ...torrents], { t1: { infoHash: hash, magnet: torrents[0].url, data: 'ZGF0YQ==', fileIndexes: [0, 2] } });
    const read = parseList(text);
    expect(read.map((entry) => isTorrentEntry(entry))).toEqual([false, true, true]);
    expect(read[1].torrent).toEqual({ infoHash: hash, magnet: torrents[0].url, data: 'ZGF0YQ==', fileIndexes: [0, 2] });
    expect(read[2].torrent).toEqual({ infoHash: hash, name: 'Album', magnet: torrents[1].url });
    expect(text).not.toContain('"torrent": null');
    expect(isTorrentEntry({ url: ' MAGNET:?xt=urn:btih:' + hash })).toBe(true);
  });
});

describe('keys (A27)', () => {
  const input = (type = 'text') => ({ tagName: 'INPUT', type });

  it('submits on Enter in single-line fields only', () => {
    expect(submitsOnEnter({ key: 'Enter', target: input() })).toBe(true);
    expect(submitsOnEnter({ key: 'Enter', target: input('password') })).toBe(true);
    expect(submitsOnEnter({ key: 'Enter', target: input('checkbox') })).toBe(false);
    expect(submitsOnEnter({ key: 'Enter', target: input('file') })).toBe(false);
    expect(submitsOnEnter({ key: 'Enter', target: { tagName: 'TEXTAREA' } })).toBe(false);
    expect(submitsOnEnter({ key: 'Enter', target: { tagName: 'BUTTON' } })).toBe(false);
    expect(submitsOnEnter({ key: 'Enter', isComposing: true, target: input() })).toBe(false);
    expect(submitsOnEnter({ key: 'Enter', shiftKey: true, target: input() })).toBe(false);
    expect(submitsOnEnter({ key: 'a', target: input() })).toBe(false);
  });

  it('opens Add URL with Alt+N and leaves the browser its own Ctrl+N', () => {
    expect(opensAddUrl({ key: 'n', code: 'KeyN', altKey: true })).toBe(true);
    expect(opensAddUrl({ key: 'ñ', code: 'KeyN', altKey: true })).toBe(true);
    expect(opensAddUrl({ key: 'n', code: 'KeyN', ctrlKey: true })).toBe(false);
    expect(opensAddUrl({ key: 'n', code: 'KeyN', altKey: true, ctrlKey: true })).toBe(false);
    expect(opensAddUrl({ key: 'n', code: 'KeyN', altKey: true, shiftKey: true })).toBe(false);
    expect(opensAddUrl({ key: 'n', code: 'KeyN' })).toBe(false);
  });
});

describe('review fixes', () => {
  it('removes only whole links, so a handled prefix cannot cut a failed link', () => {
    expect(removeInputs('https://x/a\nhttps://x/a/b', ['https://x/a'])).toBe('https://x/a/b');
    expect(removeInputs('see https://x/a/b https://x/a', ['https://x/a'])).toBe('see https://x/a/b');
  });

  it('keeps a closing bracket that belongs to the address', () => {
    expect(extractInputs('go to https://site/Page_(film).')).toEqual(['https://site/Page_(film)']);
    expect(extractInputs('(see https://site/a)')).toEqual(['https://site/a']);
  });

  it('finds an existing job even when another has no url', () => {
    const jobs = [{ id: '1', state: 'queued' }, { id: '2', url: 'https://x/f', state: 'queued' }] as never[];
    expect((findExisting(jobs, 'https://x/f') as { id: string }).id).toBe('2');
  });

  it('sends ids when a torrent file has no path, never an empty chosen list', () => {
    const line = { input: 'x', position: 1, direct: false, error: null, result: { ok: true, kind: 'torrentFile', files: [
      { fileId: 'a', size: 1, name: 'a' }, { fileId: 'b', size: 2, name: 'b', relativePath: 'd/b' }, { fileId: 'c', size: 3, name: 'c', relativePath: 'd/c' }] } } as never;
    expect(fileChoiceFrom(line, new Set(['a', 'b']))).toEqual({ selection: 'chosen', fileIds: ['a', 'b'] });
    expect(fileChoiceFrom(line, new Set(['b', 'c']))).toEqual({ selection: 'chosen', filePaths: [{ path: 'd/b', size: 2 }, { path: 'd/c', size: 3 }] });
  });
});

describe('torrent import requests', () => {
  const hash = '0123456789abcdef0123456789abcdef01234567';
  const entry = (dataChars: number) => ({
    url: `magnet:?xt=urn:btih:${hash}`, queueId: 0, requestedConnections: 0, startMode: 'later', conflictPolicy: 'rename',
    torrent: { infoHash: hash, magnet: `magnet:?xt=urn:btih:${hash}`, data: 'A'.repeat(dataChars) },
  });

  it('splits a list over the size limit and sends a too-large .torrent as its magnet', () => {
    const limit = 10_000;
    const huge = entry(limit + 1);
    const chunks = chunkTorrentEntries([entry(limit / 2), entry(limit / 2), entry(10), huge], limit);
    expect(chunks.map((chunk) => chunk.length)).toEqual([1, 2, 1]);
    expect(chunks[2][0].torrent?.data).toBeUndefined();
    expect(chunks[2][0].torrent?.magnet).toBe(huge.torrent.magnet);
    expect(huge.torrent.data).toHaveLength(limit + 1);
  });
});
