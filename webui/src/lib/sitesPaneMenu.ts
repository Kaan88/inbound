// The context menu of the LEFT pane in "Two panes": the server's folders (ServerFolders) or another remote
// (RemoteColumn). The right pane's menu is remoteMenu (sitesBrowse.ts); these are the same idea for the left one. Pure,
// so the rules are tested without a browser (tests/sites-pane-menu.test.ts).
import { actionable } from './sitesBrowse';
import { canPreview } from './sitesExtras';
import type { MenuItem } from '../components/ContextMenu.svelte';
import type { SiteItem } from './sites';

export type PaneCommand = 'open' | 'upload' | 'copyRight' | 'preview' | 'copyPath' | 'copyAddress' | 'refresh';

export interface PaneEntry { command: PaneCommand; label: string; disabledWhy?: string }

/**
 * The server side. A row is a folder or a file; null is the empty part of the list. Nothing here deletes or downloads to
 * the device: the Host has no command for either on the server's own files. `uploadProblem` is why Upload cannot go
 * (it is shown dimmed, as the arrow's tooltip does).
 */
export function serverPaneMenu(row: { isFolder: boolean } | null, uploadProblem: string | null): PaneEntry[][] {
  if (!row) return [[{ command: 'copyPath', label: 'Copy path' }, { command: 'refresh', label: 'Refresh' }]];
  const upload: PaneEntry = { command: 'upload', label: 'Upload to the remote on the right', ...(uploadProblem ? { disabledWhy: uploadProblem } : {}) };
  return [
    row.isFolder ? [{ command: 'open', label: 'Open' }, upload] : [upload],
    [{ command: 'copyPath', label: 'Copy path' }, { command: 'refresh', label: 'Refresh' }],
  ];
}

/**
 * The remote side. `selected` are the ticked rows (the row the menu was opened on is ticked first); `copyProblem` is why
 * the copy to the right cannot go (the same folder of the same remote, say).
 */
export function remotePaneMenu(selected: readonly SiteItem[], copyProblem: string | null): PaneEntry[][] {
  const picked = actionable(selected);
  if (picked.length === 0) return [[{ command: 'refresh', label: 'Refresh' }]];
  const one = picked.length === 1;
  const copy: PaneEntry = { command: 'copyRight', label: 'Copy to the right', ...(copyProblem ? { disabledWhy: copyProblem } : {}) };
  const first: PaneEntry[] = [];
  if (one && picked[0].isFolder) first.push({ command: 'open', label: 'Open' });
  first.push(copy);
  if (canPreview(picked)) first.push({ command: 'preview', label: 'Preview' });
  return [first, [{ command: 'copyPath', label: 'Copy path' }, { command: 'copyAddress', label: 'Copy address' }, { command: 'refresh', label: 'Refresh' }]];
}

/** The groups as the ContextMenu's items: a separator between groups, an entry that cannot go dimmed with its reason. */
export function paneMenuItems(groups: readonly PaneEntry[][], run: (command: PaneCommand) => void): MenuItem[] {
  const items: MenuItem[] = [];
  groups.forEach((group, index) => {
    if (index > 0) items.push({ separator: true });
    for (const entry of group) {
      items.push(entry.disabledWhy
        ? { label: `${entry.label} (${entry.disabledWhy})`, disabled: true }
        : { label: entry.label, onselect: () => run(entry.command) });
    }
  });
  return items;
}

/** The key that opens a row's menu: the menu key, or Shift+F10. */
export function isMenuKey(event: { key: string; shiftKey: boolean }): boolean {
  return event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey);
}
