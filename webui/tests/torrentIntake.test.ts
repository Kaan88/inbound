import { describe, expect, it } from 'vitest';
import {
  cacheBadge, cacheChecked, cacheQuery, cacheReason, cacheText, isCachedWords, dragMayHoldTorrents, intake, intakeStep, isEditableTarget, noTorrentsOnClipboard, onlyTorrentsDrop, pageTakes,
  startCacheChecks, torrentFilesIn, uploadTorrent,
} from '../src/lib/torrentIntake';
import { addLines, inspected, newLines, removeLine, setRoute, type TorrentLines } from '../src/lib/torrents';
import type { TorrentInspectResult } from '../src/lib/types';

const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';
const magnet = `magnet:?xt=urn:btih:${hash}&dn=debian.iso&tr=http%3A%2F%2Ftracker.example%3A6969%2Fannounce`;
const magnet2 = 'magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Sintel';

function read(state: TorrentLines, input: string, result: Partial<TorrentInspectResult> = {}): TorrentLines {
  return inspected(state, input, { ok: true, infoHash: hash, name: 'Debian', ...result });
}

describe('Ctrl+V and drops on the page', () => {
  it('leaves a paste in a text field to the field', () => {
    expect(isEditableTarget({ tagName: 'INPUT', type: 'text' })).toBe(true);
    expect(isEditableTarget({ tagName: 'INPUT' })).toBe(true);
    expect(isEditableTarget({ tagName: 'TEXTAREA' })).toBe(true);
    expect(isEditableTarget({ tagName: 'DIV', isContentEditable: true })).toBe(true);
    expect(isEditableTarget({ tagName: 'INPUT', type: 'checkbox' })).toBe(false);
    expect(isEditableTarget({ tagName: 'BUTTON' })).toBe(false);
    expect(isEditableTarget({ tagName: 'BODY' })).toBe(false);
    expect(isEditableTarget(null)).toBe(false);
  });

  it('takes them only when torrents can be added and no dialog is open', () => {
    expect(pageTakes(true, false)).toBe(true);
    expect(pageTakes(false, false)).toBe(false);
    expect(pageTakes(true, true)).toBe(false);
    expect(pageTakes(true, false, true)).toBe(false);
  });

  it('looks at drags of files and text only', () => {
    expect(dragMayHoldTorrents(['Files'])).toBe(true);
    expect(dragMayHoldTorrents(['text/uri-list'])).toBe(true);
    expect(dragMayHoldTorrents(['text/plain', 'text/html'])).toBe(true);
    expect(dragMayHoldTorrents(['text/html'])).toBe(false);
    expect(dragMayHoldTorrents(undefined)).toBe(false);
  });

  it('finds the magnets once and the .torrent files', () => {
    const files = [{ name: 'a.torrent' }, { name: 'notes.txt' }, { name: 'B.TORRENT' }];
    expect(torrentFilesIn(files).map((f) => f.name)).toEqual(['a.torrent', 'B.TORRENT']);
    const found = intake(`see ${magnet}\n${magnet} and ${magnet2}`, files, false);
    expect(found.magnets).toEqual([magnet, magnet2]);
    expect(found.files).toHaveLength(2);
    expect(found.empty).toBeNull();
  });

  it('says what was wrong when nothing fits, in the Windows words', () => {
    expect(intake('just words', [], false).empty).toBe(noTorrentsOnClipboard);
    expect(intake('', [{ name: 'x.iso' }], true).empty).toBe(onlyTorrentsDrop);
    expect(noTorrentsOnClipboard).toBe('The clipboard holds no link, magnet link or .torrent file.');
    expect(onlyTorrentsDrop).toBe('Only links, magnet links and .torrent files can be dropped here.');
  });

  // Review 5:A6: ordinary links dropped or pasted on the page go to Add Links, as in the Windows app.
  it('takes http, https and ftp links for Add Links, with torrents on or off', () => {
    const found = intake(`https://example.com/a.zip and ftp://example.com/b.iso ${magnet}`, [], false);
    expect(found.links).toEqual(['https://example.com/a.zip', 'ftp://example.com/b.iso']);
    expect(found.magnets).toEqual([magnet]);
    expect(found.empty).toBeNull();
    expect(intakeStep(0, 2, false)).toEqual({ torrents: false, links: true, problem: null });
    expect(intakeStep(1, 1, true)).toEqual({ torrents: true, links: true, problem: null });
    expect(intakeStep(1, 0, false).problem).toBe('Torrents are turned off. Turn them on in Options > Torrents.');
  });

  it('uploads a .torrent file and keeps only the reference and its name', async () => {
    const calls: { url: string; init?: RequestInit }[] = [];
    const send = (async (url: string, init?: RequestInit) => {
      calls.push({ url, init });
      return new Response(JSON.stringify({ ok: true, upload: 'upload:abc' }));
    }) as typeof fetch;
    const file = Object.assign(new Blob([new Uint8Array([1, 2])]), { name: 'debian.torrent' });
    expect(await uploadTorrent(file, send)).toEqual({ input: 'upload:abc', label: 'debian.torrent' });
    expect(calls[0].url).toBe('/api/upload/torrent');
    expect(calls[0].init?.method).toBe('POST');

    const refuse = (async () => new Response(JSON.stringify({ ok: false, error: 'This file is too large to be a .torrent file.' }))) as typeof fetch;
    await expect(uploadTorrent(file, refuse)).rejects.toThrow('too large');
  });
});

describe('the TorBox cache check of the torrent dialog', () => {
  it('asks with a bare magnet of the v1 hash, never trackers', () => {
    expect(cacheQuery(hash.toUpperCase())).toBe(`magnet:?xt=urn:btih:${hash}`);
    expect(cacheQuery('abc')).toBeNull();
    expect(cacheQuery(null)).toBeNull();
    expect(cacheQuery('1220' + 'a'.repeat(60))).toBeNull();
  });

  it('asks nothing without an account that takes torrents', () => {
    let state = newLines('thisPc', true, null);
    state = addLines(state, [{ input: magnet }], true).state;
    state = read(state, magnet);
    expect(startCacheChecks(state, {}).start).toEqual([]);
  });

  it('asks once per line that was read and is new, and never changes the route', () => {
    let state = newLines('unset', true, 'TorBox');
    state = addLines(state, [{ input: magnet }, { input: magnet2 }, { input: 'upload:x', label: 'x.torrent' }], true).state;
    // Still being read: not asked yet.
    let { states, start } = startCacheChecks(state, {});
    expect(start).toEqual([]);

    state = read(state, magnet);
    state = read(state, magnet2, { infoHash: hash }); // the same torrent as above: not asked
    state = read(state, 'upload:x', { infoHash: '1220' + 'b'.repeat(60) }); // a v2-only hash: can't be asked
    ({ states, start } = startCacheChecks(state, states));
    expect(start).toEqual([{ input: magnet, query: `magnet:?xt=urn:btih:${hash}` }]);
    expect(states).toEqual({ [magnet]: 'checking', 'upload:x': 'unknown' });
    expect(cacheText(states[magnet], 'TorBox')).toBe('Checking TorBox…');
    expect(cacheText(states['upload:x'], 'TorBox')).toBe("Couldn't check TorBox");

    // Asked once only.
    expect(startCacheChecks(state, states).start).toEqual([]);

    const routes = state.lines.map((line) => line.route);
    states = cacheChecked(states, magnet, { ok: true, cached: true });
    expect(cacheText(states[magnet], 'TorBox')).toBe('Cached on TorBox');
    expect(state.lines.map((line) => line.route)).toEqual(routes);
  });

  it('says not cached, or that it could not check', () => {
    const states = { a: 'checking' as const };
    expect(cacheText(cacheChecked(states, 'a', { ok: true, cached: false }).a, 'TorBox')).toBe('Not cached on TorBox');
    expect(cacheText(cacheChecked(states, 'a', { ok: false }).a, 'TorBox')).toBe("Couldn't check TorBox");
    expect(cacheText(cacheChecked(states, 'a', { ok: true }).a, 'TorBox')).toBe("Couldn't check TorBox");
    expect(cacheText(cacheChecked(states, 'a', null).a, 'TorBox')).toBe("Couldn't check TorBox");
    expect(cacheText(undefined, 'TorBox')).toBe('');
    expect(cacheText('cached', null)).toBe('');
  });

  it('shows the answer as a badge next to the provider choice (T9 W3)', () => {
    expect(cacheBadge('cached', 'TorBox')).toEqual({ kind: 'cached', text: 'Cached on TorBox', note: 'TorBox has it, so it downloads right away', tip: null });
    expect(cacheBadge('notCached', 'TorBox')).toEqual({ kind: 'notCached', text: 'Not cached on TorBox', note: '', tip: null });
    expect(cacheBadge('checking', 'TorBox')).toEqual({ kind: 'checking', text: 'Checking TorBox…', note: '', tip: null });
    expect(cacheBadge('unknown', 'TorBox', 'TorBox refused the API key.')).toEqual({ kind: 'unknown', text: "Couldn't check TorBox", note: '', tip: 'TorBox refused the API key.' });
    expect(cacheBadge('unknown', 'TorBox')?.tip).toBe("TorBox didn't answer the check.");
    expect(cacheBadge(undefined, 'TorBox')).toBeNull();
    expect(cacheBadge('cached', null)).toBeNull();
  });

  it('keeps why a check could not say, for the badge tooltip', () => {
    expect(cacheReason({ ok: true, cached: true }, 'TorBox')).toBeNull();
    expect(cacheReason({ ok: true, cached: false }, 'TorBox')).toBeNull();
    expect(cacheReason({ ok: false, error: 'TorBox could not be reached.' }, 'TorBox')).toBe('TorBox could not be reached.');
    expect(cacheReason(null, 'TorBox', 'The connection closed.')).toBe('The connection closed.');
    expect(cacheReason({ ok: true }, 'TorBox')).toBe("TorBox didn't say whether it has it.");
    expect(cacheReason(null, 'TorBox')).toBeNull();
    // Addresses never show, as on the desktop.
    expect(cacheReason({ ok: false, error: 'No answer for magnet:?xt=urn:btih:abc&tr=udp://t' }, 'TorBox')).toBe('No answer for an address');
    expect(cacheReason(null, 'TorBox', 'Failed https://api.example/x?k=1')).toBe('Failed an address');
  });

  it('shows a cached status as the same badge', () => {
    expect(isCachedWords('Cached on TorBox')).toBe(true);
    expect(isCachedWords('Ready to download')).toBe(false);
    expect(isCachedWords('Not cached on TorBox')).toBe(false);
    expect(isCachedWords(undefined)).toBe(false);
  });

  it('drops a line that went, and an answer for it changes nothing', () => {
    let state = newLines('thisPc', true, 'TorBox');
    state = addLines(state, [{ input: magnet }], true).state;
    state = setRoute(read(state, magnet), magnet, 'provider');
    let { states } = startCacheChecks(state, {});
    state = removeLine(state, magnet);
    ({ states } = startCacheChecks(state, states));
    expect(states).toEqual({});
    expect(cacheChecked(states, magnet, { ok: true, cached: true })).toEqual({});
  });
});
