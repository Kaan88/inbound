import { describe, expect, it } from 'vitest';
import { isMenuKey, remotePaneMenu, serverPaneMenu } from '../src/lib/sitesPaneMenu';
import type { SiteItem } from '../src/lib/sites';

const item = (name: string, isFolder = false): SiteItem => ({ name, isFolder, size: 1, modified: '' }) as SiteItem;
const commands = (groups: { command: string }[][]) => groups.map((group) => group.map((entry) => entry.command));

describe('the left pane menu, server side', () => {
  it('a folder can be opened and uploaded', () => {
    expect(commands(serverPaneMenu({ isFolder: true }, null))).toEqual([['open', 'upload'], ['copyPath', 'refresh']]);
  });
  it('a file is uploaded but not opened, and nothing deletes or downloads it', () => {
    const all = commands(serverPaneMenu({ isFolder: false }, null)).flat();
    expect(all).toEqual(['upload', 'copyPath', 'refresh']);
    expect(all).not.toContain('delete');
  });
  it('says why Upload cannot go, instead of hiding it', () => {
    const entry = serverPaneMenu({ isFolder: true }, 'Choose a folder inside D:\\Downloads.')[0].find((e) => e.command === 'upload');
    expect(entry?.disabledWhy).toBe('Choose a folder inside D:\\Downloads.');
  });
  it('the empty part of the list has only what is about the folder', () => {
    expect(commands(serverPaneMenu(null, 'x'))).toEqual([['copyPath', 'refresh']]);
  });
});

describe('the left pane menu, remote side', () => {
  it('one folder: Open, Copy to the right, no Preview', () => {
    expect(commands(remotePaneMenu([item('a', true)], null))).toEqual([['open', 'copyRight'], ['copyPath', 'copyAddress', 'refresh']]);
  });
  it('one file: Copy to the right and Preview', () => {
    expect(commands(remotePaneMenu([item('a.txt')], null))[0]).toEqual(['copyRight', 'preview']);
  });
  it('several rows: Copy only', () => {
    expect(commands(remotePaneMenu([item('a'), item('b', true)], null))[0]).toEqual(['copyRight']);
  });
  it('the ".." line is not an item; nothing left is just Refresh', () => {
    expect(commands(remotePaneMenu([item('..', true)], null))).toEqual([['refresh']]);
    expect(commands(remotePaneMenu([], null))).toEqual([['refresh']]);
  });
  it('carries the copy problem on the entry', () => {
    expect(remotePaneMenu([item('a')], 'Choose another folder')[0][0].disabledWhy).toBe('Choose another folder');
  });
});

describe('the menu keys', () => {
  it('the menu key and Shift+F10, not a plain F10', () => {
    expect(isMenuKey({ key: 'ContextMenu', shiftKey: false })).toBe(true);
    expect(isMenuKey({ key: 'F10', shiftKey: true })).toBe(true);
    expect(isMenuKey({ key: 'F10', shiftKey: false })).toBe(false);
  });
});
