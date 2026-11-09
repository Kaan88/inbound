import { describe, expect, it } from 'vitest';
import { completionForSession } from '../src/lib/details';
import { arrival, checkHost, defaultName, handoffParam, handoffUrl, HostList, keptHandoff, normalizeHostAddress, parseHosts, readHandoff, type KeyValue, type SavedHost } from '../src/lib/hosts';
import { refusalLine, refusalText } from '../src/lib/signin';
import { featuresOf, hostKindOf, hostTexts } from '../src/lib/hostKind';

function memory(initial: string | null = null): KeyValue & { value: string | null } {
  return { value: initial, getItem() { return this.value; }, setItem(_key, value) { this.value = value; } };
}

describe('normalizeHostAddress loopback', () => {
  it('does not treat a name that starts with 127. as loopback', () => {
    expect(normalizeHostAddress('http://127.evil.example')).toMatchObject({ ok: true, cleartext: true });
    expect(normalizeHostAddress('http://127.0.0.1:8080')).toMatchObject({ ok: true, cleartext: false });
  });
});

describe('normalizeHostAddress', () => {
  it('defaults to http and drops the path, query and fragment', () => {
    expect(normalizeHostAddress('192.168.1.20:8080/some/path?x=1#y')).toEqual({ ok: true, origin: 'http://192.168.1.20:8080', cleartext: true });
  });
  it('keeps https and does not warn about it', () => {
    expect(normalizeHostAddress('https://nas.example.com/')).toEqual({ ok: true, origin: 'https://nas.example.com', cleartext: false });
  });
  it('does not warn about plain http on this PC', () => {
    for (const text of ['localhost:8080', '127.0.0.1:8080', 'http://[::1]:8080']) {
      const result = normalizeHostAddress(text);
      expect(result.ok && result.cleartext).toBe(false);
    }
  });
  it('refuses credentials, other schemes, empty and nonsense', () => {
    expect(normalizeHostAddress('http://me:secret@host:8080').ok).toBe(false);
    expect(normalizeHostAddress('user@host').ok).toBe(false);
    expect(normalizeHostAddress('ftp://host').ok).toBe(false);
    expect(normalizeHostAddress('javascript:alert(1)').ok).toBe(false);
    expect(normalizeHostAddress('   ').ok).toBe(false);
    expect(normalizeHostAddress('http://').ok).toBe(false);
  });
  it('does not put the refused password in its message', () => {
    const result = normalizeHostAddress('http://me:secret@host');
    expect(JSON.stringify(result)).not.toContain('secret');
  });
});

describe('checkHost', () => {
  const saved = [{ id: 'a', name: 'NAS', origin: 'http://10.0.0.5:8080' }];
  it('names a host by its address when the name is empty', () => {
    const result = checkHost([], '  ', 'nas.local:8080', 'http://pc:8080');
    expect(result.ok && result.host.name).toBe('nas.local:8080');
    expect(defaultName('https://x.org')).toBe('x.org');
  });
  it('refuses this host and a repeated address, but lets an edit keep its own', () => {
    expect(checkHost(saved, 'x', 'http://pc:8080/', 'http://pc:8080').ok).toBe(false);
    expect(checkHost(saved, 'x', '10.0.0.5:8080', 'http://pc:8080').ok).toBe(false);
    expect(checkHost(saved, 'NAS 2', '10.0.0.5:8080', 'http://pc:8080', 'a').ok).toBe(true);
  });
  it('shortens a long name', () => {
    const result = checkHost([], 'n'.repeat(100), 'host:1', 'http://pc');
    expect(result.ok && result.host.name.length).toBe(40);
  });
});

describe('HostList', () => {
  it('adds, edits, removes and keeps the list in storage', () => {
    const storage = memory();
    const list = new HostList(() => storage);
    const one = list.add({ name: 'NAS', origin: 'http://10.0.0.5:8080' });
    const two = list.add({ name: 'Cloud', origin: 'https://dl.example.com' });
    expect(one.id).not.toBe(two.id);
    list.update(one.id, { name: 'Home NAS', origin: 'http://10.0.0.5:8080' });
    list.remove(two.id);
    const again = new HostList(() => storage);
    expect(again.all).toEqual([{ id: one.id, name: 'Home NAS', origin: 'http://10.0.0.5:8080' }]);
  });
  it('works without storage or when storage throws', () => {
    const throwing: KeyValue = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } };
    for (const source of [() => null, () => throwing]) {
      const list = new HostList(source);
      expect(list.all).toEqual([]);
      list.add({ name: 'A', origin: 'http://a:1' });
      expect(list.all).toHaveLength(1);
    }
  });
  it('drops damaged entries, odd origins and duplicates when reading', () => {
    const text = JSON.stringify([
      { id: '1', name: 'Good', origin: 'http://a:1' },
      { id: '2', name: 'Script', origin: 'javascript:alert(1)' },
      { id: '3', name: 'Path', origin: 'http://b:1/x' },
      { id: '4', name: 'Again', origin: 'http://a:1' },
      { name: 'No id', origin: 'http://c:1' },
      'nonsense',
      { id: '5', origin: 'https://d.example.com' },
    ]);
    expect(parseHosts(text)).toEqual([
      { id: '1', name: 'Good', origin: 'http://a:1' },
      { id: '5', name: 'd.example.com', origin: 'https://d.example.com' },
    ]);
    expect(parseHosts('{not json')).toEqual([]);
    expect(parseHosts('{"a":1}')).toEqual([]);
    expect(parseHosts(null)).toEqual([]);
  });
});

describe('hostKind texts', () => {
  it('treats a missing or unknown kind as the server', () => {
    expect(hostKindOf(undefined)).toBe('server');
    expect(hostKindOf('other')).toBe('server');
    expect(hostKindOf('windows')).toBe('windows');
  });
  it('keeps container wording off the Windows texts', () => {
    const text = JSON.stringify(hostTexts('windows'));
    expect(text).not.toMatch(/docker|DOWNLOADMANAGER_|image|service manager|\/downloads|setup code/i);
    expect(hostTexts('windows').setupCodeHint).toBeNull();
    expect(hostTexts('windows').signInHint).toContain('Options, Web UI');
    expect(hostTexts('windows').folderPlaceholder).toMatch(/^Type a path, such as [A-Z]:\\/);
  });
  it('keeps the server texts as they were', () => {
    expect(hostTexts('server').setupCodeHint).toContain('docker logs');
    expect(hostTexts(undefined).folderPlaceholder).toContain('/downloads/movies');
  });
});

describe('featuresOf', () => {
  it('reads the hello names of the session', () => {
    expect(featuresOf(['jobs', 'folders', 'tray'])).toEqual({ programSettings: false, completionActions: false, openUi: false });
    expect(featuresOf(['programSettings', 'completionActions']).completionActions).toBe(true);
    expect(featuresOf(null).openUi).toBe(false);
  });
});

describe('completionForSession', () => {
  const stored = { programPath: 'C:\\x.exe', openFile: true, systemAction: 'exit', forceSystemAction: true };
  it('sends no program path when the session may only use completion actions', () => {
    expect(completionForSession(stored, false, true)).toEqual({ openFile: true, systemAction: 'exit', forceSystemAction: true });
  });
  it('sends no actions when the session may only set programs', () => {
    expect(completionForSession(stored, true, false)).toEqual({ programPath: stored.programPath });
  });
  it('sends nothing when it may set neither', () => {
    expect(completionForSession(stored, false, false)).toEqual({});
  });
});

describe('passing the list along (U-WU-1)', () => {
  const a = 'http://127.0.0.1:18851';
  const b = 'http://127.0.0.1:18852';
  const c = 'http://192.168.1.30:8080';
  const listOnA: SavedHost[] = [{ id: 'h1', name: 'Second box', origin: b }, { id: 'h2', name: 'Attic', origin: c }];

  it('puts only names and addresses in the fragment, never this host twice', () => {
    const link = handoffUrl(b, a, [...listOnA, { id: 'h3', name: 'Self', origin: a }]);
    expect(link.startsWith(`${b}/#${handoffParam}=`)).toBe(true);
    const back = readHandoff(link.slice(b.length + 1));
    expect(back).toEqual({ from: a, hosts: [{ name: 'Second box', origin: b }, { name: 'Attic', origin: c }] });
    expect(link).not.toMatch(/token|password|cookie/i);
  });

  it('host B offers the way back to A and the hosts it lacks, and learns its own name', () => {
    const handoff = readHandoff(new URL(handoffUrl(b, a, listOnA)).hash);
    const seen = arrival(handoff, b, []);
    expect(seen.back).toEqual({ name: '127.0.0.1:18851', origin: a });
    expect(seen.selfName).toBe('Second box');
    expect(seen.offered.map((host) => host.origin)).toEqual([a, c]);
  });

  it('uses the name B already has for A, and offers nothing B knows', () => {
    const handoff = readHandoff(new URL(handoffUrl(b, a, listOnA)).hash);
    const seen = arrival(handoff, b, [{ id: 'x', name: 'Main box', origin: a }, { id: 'y', name: 'Attic', origin: c }]);
    expect(seen.back?.name).toBe('Main box');
    expect(seen.offered).toEqual([]);
  });

  it('drops a malformed or hostile fragment', () => {
    expect(readHandoff('#/all')).toBeNull();
    expect(readHandoff(`#${handoffParam}=not-json`)).toBeNull();
    expect(readHandoff(`#${handoffParam}=${encodeURIComponent(JSON.stringify({ v: 1, f: 'javascript:alert(1)', h: [] }))}`)).toBeNull();
    const mixed = readHandoff(`#${handoffParam}=${encodeURIComponent(JSON.stringify({ v: 1, f: a, h: [{ n: 'x', o: 'javascript:alert(1)' }, { n: 'y', o: 'http://user:pw@evil.example' }, { n: 'ok', o: c }] }))}`);
    expect(mixed?.hosts).toEqual([{ name: 'ok', origin: c }]);
  });

  it('a page with no handoff, or one from itself, offers nothing', () => {
    expect(arrival(null, b, []).back).toBeNull();
    expect(arrival({ from: b, hosts: [] }, b, []).back).toBeNull();
  });

  it('a handoff kept for the tab is checked again on reading', () => {
    expect(keptHandoff(JSON.stringify({ from: a, hosts: [{ name: 'Attic', origin: c }] }))).toEqual({ from: a, hosts: [{ name: 'Attic', origin: c }] });
    expect(keptHandoff(JSON.stringify({ from: 'ftp://x', hosts: [] }))).toBeNull();
    expect(keptHandoff('{')).toBeNull();
  });
});

describe('sign-in refusal wording (U-WU-13)', () => {
  it('keeps one countdown: the server sentence with its number goes when the page counts', () => {
    expect(refusalText('Too many wrong tries. Wait 4 s and try again.', 4)).toBe('Too many wrong tries.');
    expect(refusalText('That password is wrong.', 8)).toBe('That password is wrong.');
    expect(refusalText('Too many wrong tries. Wait 4 s and try again.', null)).toBe('Too many wrong tries. Wait 4 s and try again.');
    expect(refusalLine('Too many wrong tries.', 3)).toBe('Too many wrong tries. Wait 3 s before trying again.');
    expect(refusalLine('That password is wrong.', 0)).toBe('That password is wrong.');
  });
});
