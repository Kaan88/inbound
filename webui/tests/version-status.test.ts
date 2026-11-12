import { describe, expect, it } from 'vitest';
import { apply, emptyModel } from '../src/lib/model';
import type { Envelope } from '../src/lib/protocol';
import { compareVersions, versionWords } from '../src/lib/versionStatus';

function message(type: string, payload: unknown): Envelope {
  return { v: 1, type, id: 'x', payload };
}

// UI.R37: the server's version says whether it is the latest, as far as its own update check knows.
describe('version status', () => {
  it('says latest only when the server knows the latest', () => {
    expect(versionWords('0.7.0', '0.7.0')).toBe('0.7.0 (latest)');
    expect(versionWords('0.7.0', '0.7')).toBe('0.7.0 (latest)');
    expect(versionWords('0.7.0', '0.7.1')).toBe('0.7.0 · update available (0.7.1)');
    expect(versionWords('0.7.0', 'v0.7.1')).toBe('0.7.0 · update available (0.7.1)');
    // A newer build than the manifest isn't told to go back.
    expect(versionWords('0.7.2', '0.7.1')).toBe('0.7.2 (latest)');
    // No update check (Docker server, portable build, older server): the version alone.
    expect(versionWords('0.7.0', undefined)).toBe('0.7.0');
    expect(versionWords('0.7.0', null)).toBe('0.7.0');
    expect(versionWords('0.7.0', 'nonsense')).toBe('0.7.0');
    expect(versionWords('', '0.7.1')).toBe('');
  });

  it('compares versions by number', () => {
    expect(compareVersions('0.10.0', '0.9.0')).toBe(1);
    expect(compareVersions('0.6.3', '0.7.0')).toBe(-1);
    expect(compareVersions('1.0.0', '1.0.0.0')).toBe(0);
    expect(compareVersions('1.0.0', undefined)).toBeNull();
  });

  it('keeps the latest from the snapshot and from update.latest', () => {
    const model = emptyModel();
    // Nothing to put it on before the snapshot.
    expect(apply(model, message('update.latest', { latestVersion: '0.7.1' }))).toBe(false);
    apply(model, message('jobs.snapshot', { jobs: [], settings: { downloadGroups: [] }, status: { hostVersion: '0.7.0', latestVersion: '0.7.0' } }));
    expect(model.status?.latestVersion).toBe('0.7.0');
    expect(apply(model, message('update.latest', { latestVersion: '0.7.1' }))).toBe(true);
    expect(model.status?.latestVersion).toBe('0.7.1');
    expect(apply(model, message('update.latest', { latestVersion: '0.7.1' }))).toBe(false);
  });
});
