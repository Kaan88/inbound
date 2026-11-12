import { describe, expect, it } from 'vitest';
import { applyForm, formFrom, friendlyRefusal, mergeEditedSettings, torrentSaveToPlaceholder } from '../src/lib/settingsRules';
import type { Settings } from '../src/lib/types';

// T14 (owner, 2026-11-12): torrent category folders apart from other downloads', both able to be turned off.
function settings(extra: Partial<Settings> = {}): Settings {
  return {
    defaultDownloadDirectory: '/data/downloads', maxConcurrentDownloads: 3, defaultQueueId: 0, queues: [], schedules: [],
    defaultConnections: 8, useCategoryFolders: true, maxConnectionsPerHost: 8, connectionProfiles: [],
    proxy: { mode: 'none', hasPassword: false }, siteLogins: [], allowLocalNetwork: true, autoResumeUnfinishedOnStartup: true,
    browserIntegrationEnabled: true, allowSystemCompletionActions: false, favoriteFolders: [], recentDestinationDirectories: [], downloadGroups: [],
    excludedFileExtensions: [], excludedHosts: [], ...extra,
  };
}

describe('torrent folders in Options', () => {
  it('reads and writes both settings, apart from folders by category', () => {
    const current = settings({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '/data/downloads/Torrents' });
    const form = formFrom(current);
    expect(form.useTorrentCategoryFolders).toBe(true);
    expect(form.torrentFolder).toBe('/data/downloads/Torrents');
    form.useTorrentCategoryFolders = false;
    form.useCategoryFolders = false;
    form.torrentFolder = '  /data/torrents  ';
    applyForm(current, form);
    expect(current.useTorrentCategoryFolders).toBe(false);
    expect(current.useCategoryFolders).toBe(false);
    expect(current.torrentCategoryFoldersRoot).toBe('/data/torrents');
  });

  it('turns each switch off without the other', () => {
    for (const [normal, torrent] of [[true, true], [true, false], [false, true], [false, false]] as const) {
      const current = settings({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '' });
      const form = formFrom(current);
      form.useCategoryFolders = normal;
      form.useTorrentCategoryFolders = torrent;
      applyForm(current, form);
      expect([current.useCategoryFolders, current.useTorrentCategoryFolders]).toEqual([normal, torrent]);
      // Empty stays empty: the same folders as other downloads.
      expect(current.torrentCategoryFoldersRoot).toBe('');
    }
  });

  it('hides the group from a server before T14 and sends it neither', () => {
    const current = settings();
    const form = formFrom(current);
    expect(form.useTorrentCategoryFolders).toBeNull();
    applyForm(current, form);
    expect('useTorrentCategoryFolders' in current).toBe(false);
    expect('torrentCategoryFoldersRoot' in current).toBe(false);
  });

  it('keeps a torrent folder changed elsewhere when only the switch was changed here', () => {
    const opened = settings({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '/a' });
    const formAtOpen = formFrom(opened);
    const draft = formFrom(opened);
    draft.useTorrentCategoryFolders = false;
    const current = settings({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '/b' });
    const merged = mergeEditedSettings(current, opened, formAtOpen, draft);
    expect(merged.useTorrentCategoryFolders).toBe(false);
    expect(merged.torrentCategoryFoldersRoot).toBe('/b');
  });

  it('names the torrent folder in a refusal', () => {
    expect(friendlyRefusal('These settings can only be changed in the app on this PC: torrentCategoryFoldersRoot. Nothing was saved.'))
      .toContain('Torrent folder');
  });
});

describe('the torrent dialog Save to', () => {
  it('says what an empty Save to means', () => {
    expect(torrentSaveToPlaceholder({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '/t' })).toBe("Each torrent's category folder");
    expect(torrentSaveToPlaceholder({})).toBe("Each torrent's category folder");
    expect(torrentSaveToPlaceholder(null)).toBe("Each torrent's category folder");
    expect(torrentSaveToPlaceholder({ useTorrentCategoryFolders: false, torrentCategoryFoldersRoot: '/t' })).toBe('The torrent folder');
    expect(torrentSaveToPlaceholder({ useTorrentCategoryFolders: false, torrentCategoryFoldersRoot: '' })).toBe('Same folder as other downloads');
    // No torrent folder: the other downloads' switch counts too, as on the server.
    expect(torrentSaveToPlaceholder({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '', useCategoryFolders: false })).toBe('Same folder as other downloads');
    expect(torrentSaveToPlaceholder({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '', useCategoryFolders: true })).toBe("Each torrent's category folder");
    // With a torrent folder it doesn't.
    expect(torrentSaveToPlaceholder({ useTorrentCategoryFolders: true, torrentCategoryFoldersRoot: '/t', useCategoryFolders: false })).toBe("Each torrent's category folder");
  });
});
