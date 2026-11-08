import { describe, expect, it } from 'vitest';
import { checkHost, defaultName, HostList, normalizeHostAddress, parseHosts, type KeyValue } from '../src/lib/hosts';
import { featuresOf, hostKindOf, hostTexts } from '../src/lib/hostKind';

function memory(initial: string | null = null): KeyValue & { value: string | null } {
  return { value: initial, getItem() { return this.value; }, setItem(_key, value) { this.value = value; } };
}

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
    expect(text).not.toMatch(/docker|DOWNLOADMANAGER_|image|service manager|\/downloads\/movies|setup code/i);
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
    expect(featuresOf(['jobs', 'folders'])).toEqual({ updater: false, programSettings: false, folderPicker: false, openUi: false, shutdown: false, completionActions: false });
    expect(featuresOf(['programSettings', 'completionActions']).completionActions).toBe(true);
    expect(featuresOf(null).updater).toBe(false);
  });
});
