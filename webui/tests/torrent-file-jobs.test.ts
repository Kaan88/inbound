import appSource from '../src/App.svelte?raw';
import addSource from '../src/screens/TorrentAdd.svelte?raw';
import addLinksSource from '../src/screens/AddLinks.svelte?raw';
import listSource from '../src/screens/ListScreen.svelte?raw';
import { describe, expect, it } from 'vitest';
import { startCacheChecks } from '../src/lib/torrentIntake';
import {
  addLines, defaultRoute, inputFields, inspected, isJobInput, jobInput, listedCopyFields, newLines, openAsTorrentStep, opensAsTorrent, picks, setRoute, torrentFileJobOf, torrentsOffText,
} from '../src/lib/torrents';
import type { Job, TorrentInspectResult } from '../src/lib/types';

// T10 (docs/plans/t10-torrent-links.md, option A): a finished download that is a .torrent file gets Open as Torrent,
// which opens the torrent dialog with a line for it; the server reads the file by the download's id (fromJobId).

const id = '0b1f6a52-3c55-4a5e-9f31-0b6b0e4a2d11';
const hash = '7acf8fb590b2060dd9c3146ef770169d593433b0';

function job(extra: Partial<Job> = {}): Job {
  return {
    id, url: 'https://example.com/ubuntu.torrent', fileName: 'ubuntu.torrent', destinationDirectory: '/d', finalFilePath: '/d/ubuntu.torrent',
    receivedBytes: 100, state: 'completed', createdAt: '2026-11-12T10:00:00Z', updatedAt: '2026-11-12T10:00:00Z', queueId: 0, torrentFileReady: true, ...extra,
  };
}

const read: TorrentInspectResult = { ok: true, kind: 'torrentFile', infoHash: hash, name: 'ubuntu.iso', totalBytes: 1000, files: [{ index: 0, path: 'ubuntu.iso', size: 1000, wanted: true }] };

describe('T10: Open as Torrent on a finished .torrent download', () => {
  it('shows for one finished .torrent download only', () => {
    expect(opensAsTorrent(job())).toBe(true);
    expect(opensAsTorrent(job({ torrentFileReady: false }))).toBe(false);
    expect(opensAsTorrent(job({ torrentFileReady: undefined }))).toBe(false); // an older server
    expect(opensAsTorrent(job({ state: 'downloading' }))).toBe(false);
    expect(opensAsTorrent(job({ fileRemovedAfterExtraction: true }))).toBe(false);
    expect(opensAsTorrent(job({ isRemote: true }))).toBe(false);
    expect(opensAsTorrent(null)).toBe(false);
  });

  it('goes to the dialog with torrents on, to the provider with only an account, else to the note', () => {
    expect(openAsTorrentStep(true, true)).toBe('dialog');
    expect(openAsTorrentStep(true, false)).toBe('dialog');
    expect(openAsTorrentStep(false, true)).toBe('provider');
    expect(openAsTorrentStep(false, false)).toBe('off');
    expect(torrentsOffText).toBe('Torrents are turned off. Turn them on in Options > Torrents.');
  });

  it('names the download by its id, never by a path, in torrent.inspect and torrent.add', () => {
    expect(isJobInput(jobInput(id))).toBe(true);
    expect(isJobInput('job:../etc')).toBe(false);
    expect(isJobInput('magnet:?xt=urn:btih:' + hash)).toBe(false);
    expect(inputFields(jobInput(id))).toEqual({ input: '', fromJobId: id });
    expect(inputFields('upload:abc')).toEqual({ input: 'upload:abc' });
  });

  it("is a line of the server's own, added there and never asked of the provider", () => {
    let lines = newLines(defaultRoute('provider', true), true, 'TorBox');
    const added = addLines(lines, [{ input: jobInput(id), label: 'ubuntu.torrent' }], false);
    lines = added.state;
    expect(added.added).toHaveLength(1);
    expect(lines.lines[0].route).toBe('thisPc');
    expect(setRoute(lines, jobInput(id), 'provider').lines[0].route).toBe('thisPc');
    lines = inspected(lines, jobInput(id), read);
    expect(picks(lines)).toEqual([expect.objectContaining({ input: jobInput(id), route: 'thisPc', files: 'all' })]);
    expect(startCacheChecks(lines, {}).start).toEqual([]);
  });

  it('is wired: the row item, the dialog line and the note', () => {
    expect(listSource).toContain("label: 'Open as Torrent…'");
    expect(listSource).toContain('opensAsTorrent(single)');
    expect(appSource).toContain('onopentorrent={openAsTorrent}');
    expect(appSource).toContain('primary="Open Options"');
    expect(addSource).toContain("store.ask<TorrentInspectResult>('torrent.inspect', { ...inputFields(input)");
    expect(addSource).toContain('...inputFields(pick.input)');
    // A line whose torrent is already listed: its download is removed by an add the server answers with Existing.
    expect(addSource).toContain("lineState(l) === 'listed' && isJobInput(l.input)");
    expect(addSource).toContain("store.ask<TorrentAddResult>('torrent.add', listedCopyFields(line.input))");
    // Only a removal the server confirmed with Existing counts as done.
    expect(addSource).toContain('if (result.ok && result.existing) done.add(line.input);');
  });

  it('removes a listed copy with a request that can never add the torrent', () => {
    expect(listedCopyFields(jobInput(id))).toEqual({ input: '', fromJobId: id, onlyIfListed: true, startMode: 'later' });
  });

  it('removes the finished download once the provider took its .torrent file (torrents off, an account)', () => {
    const given = [{ input: '/d/ubuntu.torrent', label: 'ubuntu.torrent', fromJobId: id }, { input: 'magnet:?xt=urn:btih:' + hash }];
    expect(torrentFileJobOf(given, '/d/ubuntu.torrent')).toBe(id);
    expect(torrentFileJobOf(given, ' /d/ubuntu.torrent ')).toBe(id);
    expect(torrentFileJobOf(given, 'magnet:?xt=urn:btih:' + hash)).toBeUndefined();
    expect(torrentFileJobOf(given, '/d/other.torrent')).toBeUndefined();
    expect(appSource).toContain('fromJobId: job.id');
    expect(addLinksSource).toContain("store.attempt('job.remove', { jobId: finished, jobIds: [finished], deleteFile: true })");
  });
});
