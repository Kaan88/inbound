import { describe, expect, it } from 'vitest';
import { connectionsPerDownloadLabel, serverConnectionOptions } from '../src/lib/list';

describe('connection limits menu (CONN.1)', () => {
  it('offers the presets and adds the current value when it is none of them', () => {
    expect(serverConnectionOptions(8)).toEqual([1, 2, 4, 8, 16, 32, 64]);
    expect(serverConnectionOptions(10)).toEqual([1, 2, 4, 8, 10, 16, 32, 64]);
    expect(serverConnectionOptions(undefined)).toEqual([1, 2, 4, 8, 16, 32, 64]);
  });

  it('says the per-server limit applies only when it is the lower one', () => {
    expect(connectionsPerDownloadLabel(8, 8)).toBe('Connections per Download: 8');
    expect(connectionsPerDownloadLabel(8, 16)).toBe('Connections per Download: 8');
    expect(connectionsPerDownloadLabel(16, 4)).toBe('Connections per Download: 16 (4 per server applies)');
    expect(connectionsPerDownloadLabel(undefined, undefined)).toBe('Connections per Download: ');
  });
});
