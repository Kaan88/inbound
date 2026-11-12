// Audit 4 H1: Options sends only what it changed, onto the settings the server holds now.
import { describe, expect, it } from 'vitest';
import { apply, emptyModel } from '../src/lib/model';
import type { Envelope } from '../src/lib/protocol';
import { formFrom, mergeEditedSettings } from '../src/lib/settingsRules';
import type { Settings } from '../src/lib/types';

function settings(extra: Partial<Settings> = {}): Settings {
  return {
    defaultDownloadDirectory: '/data/downloads', maxConcurrentDownloads: 3, defaultQueueId: 0, queues: [], schedules: [],
    defaultConnections: 8, useCategoryFolders: true, maxConnectionsPerHost: 8, connectionProfiles: [{ pattern: '*.a.com', maxConnections: 2 }],
    proxy: { mode: 'none', hasPassword: false }, siteLogins: [], allowLocalNetwork: true, autoResumeUnfinishedOnStartup: true,
    browserIntegrationEnabled: true, allowSystemCompletionActions: false, favoriteFolders: [], recentDestinationDirectories: [], downloadGroups: [],
    excludedFileExtensions: [], excludedHosts: [], ...extra,
  };
}

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

describe('mergeEditedSettings', () => {
  it('sends only the fields the form changed, onto the settings the server holds now', () => {
    const opened = settings({ queues: [{ id: 0, name: 'Main', isRunning: true, maxConcurrentDownloads: 3 }] as unknown as Settings['queues'] });
    const formAtOpen = formFrom(clone(opened));
    const draft = clone(formAtOpen);
    draft.defaultConnections = 4;
    // Meanwhile, elsewhere: the queue stopped, a schedule and a favorite added, the limit and a profile changed.
    const current = settings({
      maxConcurrentDownloads: 5,
      queues: [{ id: 0, name: 'Main', isRunning: false, maxConcurrentDownloads: 3 }] as unknown as Settings['queues'],
      schedules: [{ id: 's' }] as unknown as Settings['schedules'],
      favoriteFolders: [{ name: 'New', path: '/new' }],
      connectionProfiles: [{ pattern: '*.b.com', maxConnections: 4 }],
    });

    const sent = mergeEditedSettings(current, opened, formAtOpen, draft);

    expect(sent.defaultConnections).toBe(4);
    expect(sent.maxConcurrentDownloads).toBe(5);
    expect(sent.queues[0]).toMatchObject({ isRunning: false });
    expect(sent.schedules).toEqual([{ id: 's' }]);
    expect(sent.favoriteFolders).toEqual([{ name: 'New', path: '/new' }]);
    expect(sent.connectionProfiles).toEqual([{ pattern: '*.b.com', maxConnections: 4 }]);
  });

  it('writes an edited list onto the current one, keeping what was added elsewhere', () => {
    const opened = settings({ favoriteFolders: [{ name: 'A', path: '/a' }] });
    const formAtOpen = formFrom(clone(opened));
    const draft = clone(formAtOpen);
    draft.favorites = draft.favorites.map((row) => ({ ...row, name: 'Renamed' }));
    const current = settings({ favoriteFolders: [{ name: 'A', path: '/a' }, { name: 'B', path: '/b' }] });

    const sent = mergeEditedSettings(current, opened, formAtOpen, draft);

    expect(sent.favoriteFolders).toEqual([{ name: 'Renamed', path: '/a' }, { name: 'B', path: '/b' }]);
  });

  it('sends the settings as they are now when nothing was edited', () => {
    const opened = settings();
    const formAtOpen = formFrom(clone(opened));
    const current = settings({ maxConcurrentDownloads: 7, defaultDownloadDirectory: '/elsewhere' });
    expect(mergeEditedSettings(current, opened, formAtOpen, clone(formAtOpen))).toEqual(current);
  });
});

describe('settings.changed', () => {
  const message = (type: string, payload: unknown): Envelope => ({ v: 1, type, id: 'e', payload });

  it('takes the settings a server sends when they change, alone or as { settings }', () => {
    const model = emptyModel();
    expect(apply(model, message('settings.changed', settings({ maxConcurrentDownloads: 6 })))).toBe(true);
    expect(model.settings?.maxConcurrentDownloads).toBe(6);
    expect(apply(model, message('settings.changed', { settings: settings({ maxConcurrentDownloads: 2, downloadGroups: [{ id: 'g', name: 'G', jobIds: [] }] }) }))).toBe(true);
    expect(model.settings?.maxConcurrentDownloads).toBe(2);
    expect(model.groups).toHaveLength(1);
    expect(apply(model, message('settings.changed', {}))).toBe(false);
    expect(model.settings?.maxConcurrentDownloads).toBe(2);
  });
});
