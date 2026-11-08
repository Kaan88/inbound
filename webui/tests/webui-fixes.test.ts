import { describe, expect, it } from 'vitest';
import { archiveShort } from '../src/lib/format';
import type { ArchiveSet } from '../src/lib/types';
import { withoutAdded, type AddLine } from '../src/lib/addlines';
import { newUuid } from '../src/lib/ids';
import { latest } from '../src/lib/latest';

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newUuid', () => {
  it('makes a Guid the server can parse when randomUUID is missing (plain http from another computer)', () => {
    const insecure = { getRandomValues: globalThis.crypto.getRandomValues.bind(globalThis.crypto) };
    const ids = new Set(Array.from({ length: 50 }, () => newUuid(insecure)));
    expect(ids.size).toBe(50);
    for (const id of ids) expect(id).toMatch(guid);
  });

  it('sets the version and variant bits even from all-ones bytes', () => {
    expect(newUuid({ getRandomValues: ((array: Uint8Array) => array.fill(0xff)) as Crypto['getRandomValues'] })).toBe('ffffffff-ffff-4fff-bfff-ffffffffffff');
  });

  it('uses randomUUID where the page has it', () => {
    expect(newUuid({ randomUUID: () => 'from-randomUUID', getRandomValues: (a) => a as never })).toBe('from-randomUUID');
  });
});

describe('withoutAdded', () => {
  const line = (input: string, position: number): AddLine => ({ input, position, result: { ok: true, kind: 'ordinaryLink', checkIncomplete: false }, error: null, direct: false });

  it('leaves only the lines that failed, so pressing Add again does not resend the added ones', () => {
    const inputs = ['https://a/1', 'https://a/2', 'https://a/3', 'https://a/4', 'https://a/5'];
    const text = inputs.join('\r\n');
    const after = withoutAdded(inputs.map((input, i) => line(input, i + 1)), text, ['https://a/1', 'https://a/2', 'https://a/4', 'https://a/5']);
    expect(after.lines.map((l) => [l.input, l.position])).toEqual([['https://a/3', 1]]);
    expect(after.text).toBe('https://a/3');
  });

  it('keeps lines it did not add as typed, and changes nothing when nothing was added', () => {
    const lines = [line('https://a/1', 1), line('magnet:?x', 2)];
    const text = '  https://a/1  \n\nmagnet:?x\nnot checked yet';
    expect(withoutAdded(lines, text, [])).toEqual({ lines, text });
    expect(withoutAdded(lines, text, ['https://a/1'])).toEqual({ lines: [{ ...lines[1], position: 1 }], text: '\nmagnet:?x\nnot checked yet' });
  });
});

describe('latest', () => {
  it('drops a reply that arrives after a newer request started (folder A answered after folder B)', async () => {
    const requests = latest();
    let shown = '';
    const resolvers: Record<string, (path: string) => void> = {};
    async function browse(path: string) {
      const current = requests.begin();
      const result = await new Promise<string>((resolve) => { resolvers[path] = resolve; });
      if (current()) shown = result;
    }
    const a = browse('A');
    const b = browse('B');
    resolvers.B('B');
    await b;
    resolvers.A('A');
    await a;
    expect(shown).toBe('B');
  });

  it('lets the newest request apply its reply', () => {
    const requests = latest();
    const first = requests.begin();
    expect(first()).toBe(true);
    const second = requests.begin();
    expect(first()).toBe(false);
    expect(second()).toBe(true);
  });
});

describe('archiveShort for a set still downloading (UI.R23)', () => {
  const set = (change: Partial<ArchiveSet>): ArchiveSet => ({
    state: 'downloading',
    downloadingVolumes: ['m.part1.rar', 'm.part2.rar'],
    ...change,
  }) as ArchiveSet;

  it('says the parts are downloading while any of them runs', () => {
    expect(archiveShort(set({}))).toBe('Downloading parts');
    expect(archiveShort(set({ pausedVolumes: ['m.part1.rar'] }))).toBe('Downloading parts');
  });

  it('names paused, not started and waiting parts, and a mix of them', () => {
    expect(archiveShort(set({ pausedVolumes: ['m.part1.rar', 'M.PART2.RAR'] }))).toBe('Parts paused');
    expect(archiveShort(set({ heldVolumes: ['m.part1.rar', 'm.part2.rar'] }))).toBe('Parts not started');
    expect(archiveShort(set({ queuedVolumes: ['m.part1.rar', 'm.part2.rar'] }))).toBe('Parts waiting to download');
    expect(archiveShort(set({ pausedVolumes: ['m.part1.rar'], queuedVolumes: ['m.part2.rar'] }))).toBe('Parts not downloading');
  });

  it('reads a server that sends no such lists as before', () => {
    expect(archiveShort(set({ pausedVolumes: undefined, heldVolumes: undefined, queuedVolumes: undefined }))).toBe('Downloading parts');
  });
});
