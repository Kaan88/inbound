import { describe, expect, it } from 'vitest';
import {
  between, decide, decideAndRecord, decideSeen, hadWebState, recordSeen, displayDate, elsewhereText, filterChangelog, findVersion, readChangelog, releaseNotesUrl, seenKey,
  type ChangelogVersion, type KeyValue,
} from '../src/lib/whatsNew';

// UPD.N1: the same lines and rules as the desktop's ChangelogTests. A provider's id is its tag (torbox here).
const sample: ChangelogVersion = {
  version: '9.1.0',
  date: '2026-12-01',
  sections: [
    {
      title: 'New',
      items: [
        { text: 'Everywhere', tags: ['all'] },
        { text: 'Window only', tags: ['windows'] },
        { text: 'Web only', tags: ['webui'] },
        { text: 'Docker only', tags: ['docker'] },
        { text: 'Server only', tags: ['server'] },
        { text: 'Extension only', tags: ['extension'] },
        { text: 'Linux only', tags: ['linux'] },
        { text: 'Web torrents', tags: ['webui', 'torrents'] },
        { text: 'Web TorBox', tags: ['webui', 'torbox'] },
      ],
    },
    { title: 'Fixes', items: [{ text: 'Extension fix', tags: ['extension'] }] },
  ],
};

class Memory implements KeyValue {
  values = new Map<string, string>();
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
}

describe("what's new", () => {
  it('shows the web UI, server and Docker lines and folds the rest', () => {
    const view = filterChangelog([sample], { torrents: false, providers: [] });
    expect(view.versions).toHaveLength(1);
    expect(view.versions[0].sections).toEqual([{ title: 'New', lines: ['Everywhere', 'Web only', 'Docker only', 'Server only'] }]);
    expect(view.elsewhere.map((line) => `${line.place} · ${line.text}`)).toEqual(['Windows · Window only', 'Extension · Extension only', 'Extension · Extension fix']);
    expect(elsewhereText(view.elsewhere)).toBe('3 changes in the Windows app and the browser extension');
  });

  it('shows torrent lines only while torrents are on, and TorBox lines only with an account', () => {
    expect(filterChangelog([sample], { torrents: true, providers: [] }).versions[0].sections[0].lines).toContain('Web torrents');
    expect(filterChangelog([sample], { torrents: true, providers: [] }).versions[0].sections[0].lines).not.toContain('Web TorBox');
    expect(filterChangelog([sample], { torrents: false, providers: ['torbox'] }).versions[0].sections[0].lines).toContain('Web TorBox');
  });

  it('opens nothing when no line is about the web UI', () => {
    const only: ChangelogVersion = { version: '9.2.0', sections: [{ title: 'New', items: [{ text: 'x', tags: ['windows'] }, { text: 'y', tags: ['linux'] }] }] };
    expect(filterChangelog([only], { torrents: true, providers: ['torbox'] }).versions).toEqual([]);
  });

  it('joins the versions the server skipped, newest first', () => {
    const versions = ['0.8.0', '0.7.3', '0.7.2', '0.7.1'].map((version) => ({ version, sections: [] }));
    expect(between(versions, '0.7.1', '0.7.3').map((v) => v.version)).toEqual(['0.7.3', '0.7.2']);
    expect(between(versions, '0.7.3', '0.7.3')).toEqual([]);
    expect(findVersion(versions, '0.7.2.0')?.version).toBe('0.7.2');
  });

  it('opens once per browser per server after its version changed', () => {
    const storage = new Memory();
    const origin = 'http://192.168.1.20:8080';
    // A browser that never saw this server: nothing opens, the version is kept.
    expect(decideAndRecord(storage, origin, '0.7.1')).toEqual({ open: false, after: null, afterKnown: false, record: '0.7.1' });
    expect(storage.getItem(seenKey(origin))).toBe('0.7.1');
    expect(decideAndRecord(storage, origin, '0.7.1').open).toBe(false);
    // The server was updated: it opens once, from the version seen.
    expect(decideAndRecord(storage, origin, '0.7.3')).toEqual({ open: true, after: '0.7.1', afterKnown: true, record: '0.7.3' });
    expect(decideAndRecord(storage, origin, '0.7.3').open).toBe(false);
    // Another server keeps its own mark.
    expect(decideAndRecord(storage, 'https://nas.local', '0.7.3').open).toBe(false);
    // An older one at this address keeps the newer mark.
    expect(decide('0.7.3', '0.7.2')).toEqual({ open: false, after: null, afterKnown: false, record: null });
    expect(decide('0.7.1', 'not a version').open).toBe(false);
  });

  it('counts a browser that used the web UI before the mark as coming from 0.7.1', () => {
    // Web UI choices kept before this page loaded, and no mark: an upgrade from 0.7.1 or older.
    expect(decide(null, '0.7.2', true)).toEqual({ open: true, after: '0.7.1', afterKnown: false, record: '0.7.2' });
    expect(decide(null, '0.7.1', true)).toEqual({ open: false, after: null, afterKnown: false, record: '0.7.1' });
    // A browser new to the web UI only keeps the version.
    expect(decide(null, '0.7.2', false).open).toBe(false);
    // A kept mark wins over the guess.
    expect(decide('0.7.2', '0.7.2', true).open).toBe(false);

    const keys = (entries: string[]) => ({ length: entries.length, key: (i: number) => entries[i] ?? null });
    expect(hadWebState(keys(['inbound.theme']))).toBe(true);
    expect(hadWebState(keys(['inbound.whatsNewSeen:http://x', 'other']))).toBe(false);
    expect(hadWebState(keys([]))).toBe(false);
    expect(hadWebState({ get length(): number { throw new Error('blocked'); }, key: () => null })).toBe(false);
    expect(hadWebState(null)).toBe(false);
  });

  it('decides without writing, so notes a dialog kept off screen show next time', () => {
    const storage = new Memory();
    const origin = 'http://x';
    recordSeen(storage, origin, '0.7.1');
    expect(decideSeen(storage, origin, '0.7.2').open).toBe(true);
    // Not shown (another dialog was open), not recorded: still due.
    expect(decideSeen(storage, origin, '0.7.2').open).toBe(true);
    expect(recordSeen(storage, origin, '0.7.2')).toBe(true);
    expect(decideSeen(storage, origin, '0.7.2').open).toBe(false);
    expect(recordSeen({ getItem: () => null, setItem: () => { throw new Error('full'); } }, origin, '0.7.2')).toBe(false);
    expect(recordSeen(null, origin, '0.7.2')).toBe(false);
  });

  it('shows nothing when this browser keeps nothing', () => {
    const broken: KeyValue = { getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); } };
    expect(decideAndRecord(broken, 'http://x', '0.7.2').open).toBe(false);
    const readOnly: KeyValue = { getItem: () => '0.7.1', setItem: () => { throw new Error('full'); } };
    expect(decideAndRecord(readOnly, 'http://x', '0.7.2').open).toBe(false);
    expect(decideAndRecord(null, 'http://x', '0.7.2').open).toBe(false);
  });

  it('keeps only well-formed lines from the server', () => {
    const versions = readChangelog({ versions: [
      { version: '0.7.2', date: 5, sections: [{ title: 'New', items: [{ text: 'ok', tags: ['webui', 7] }, { text: 3 }, null] }, { title: 9 }] },
      { nope: true },
    ] });
    expect(versions).toEqual([{ version: '0.7.2', date: null, sections: [{ title: 'New', items: [{ text: 'ok', tags: ['webui'] }] }] }]);
    expect(readChangelog(null)).toEqual([]);
    expect(readChangelog({ versions: 'x' })).toEqual([]);
  });

  it('says the date and links the release notes', () => {
    expect(displayDate('2026-11-12')).toBe('12 Nov 2026');
    expect(displayDate(null)).toBeNull();
    expect(releaseNotesUrl('v0.7.2')).toBe('https://github.com/Kaan88/inbound/releases/tag/v0.7.2');
  });
});
