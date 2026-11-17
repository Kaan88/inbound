// The rules behind the remote files browse view (SITES.11 R6a), in the desktop window's words and decisions:
// SitesBrowseRules.cs, SitesCommandRules.cs. No UI, so they are tested without a browser.
import { crumbs, hereText, joinPath, parentPath, problems, type Site, type SiteItem } from './sites';

// ---- Keys: a list is keyed and selected by a key that is never the name alone ---------------------------------------

/**
 * One key per row. Google Drive allows two items of one name in a folder, so a name is not a key. The key is how many
 * items of that name came before it and the name, which stays the same when the list is sorted or filtered. The two are
 * joined by "/", which no name in a listing holds, and never by a control character: CSS.escape turns U+0000 into
 * U+FFFD, so a key with one could not be found again in the page.
 */
export function rowKeys(items: readonly { name: string }[]): string[] {
  const seen = new Map<string, number>();
  return items.map((item) => {
    const before = seen.get(item.name) ?? 0;
    seen.set(item.name, before + 1);
    return `${before}/${item.name}`;
  });
}

export interface Row {
  key: string;
  item: SiteItem;
}

/**
 * After a listing of the same folder is read again (Refresh, an upload finishing), the selected rows that are still
 * there stay selected. A listing of another folder starts with nothing selected.
 */
export function keptSelection(selected: ReadonlySet<string>, keys: readonly string[], sameFolder: boolean): Set<string> {
  if (!sameFolder) return new Set();
  const now = new Set(keys);
  return new Set([...selected].filter((key) => now.has(key)));
}

export function rowsOf(items: readonly SiteItem[]): Row[] {
  const keys = rowKeys(items);
  return items.map((item, index) => ({ key: keys[index], item }));
}

// ---- Chip, sorting, filter, footer -----------------------------------------------------------------------------------

export function kindChip(kind: string | null | undefined): string {
  switch (kind) {
    case undefined: case null: case '': return '';
    case 'sftp': return 'SFTP';
    case 'ftp': return 'FTP';
    case 'webdav': return 'WebDAV';
    case 's3': return 'S3';
    case 'drive': return 'Drive';
    case 'onedrive': return 'OneDrive';
    case 'dropbox': return 'Dropbox';
    case 'b2': return 'B2';
    default: return kind.toUpperCase();
  }
}

export type SortKey = 'name' | 'size' | 'modified' | 'here';

/** A click on a column: the same column turns the order round, another starts ascending. */
export function nextSort(key: SortKey, ascending: boolean, clicked: SortKey): { key: SortKey; ascending: boolean } {
  return key === clicked ? { key, ascending: !ascending } : { key: clicked, ascending: true };
}

const hereRank = (here: string | null | undefined) =>
  ({ notHere: 1, someOfIt: 2, olderHere: 3, different: 4, same: 5 } as Record<string, number>)[here ?? ''] ?? 0;

const byName = (a: string, b: string) => {
  const folded = a.localeCompare(b, undefined, { sensitivity: 'accent' });
  return folded !== 0 ? folded : a < b ? -1 : a > b ? 1 : 0;
};

/** Folders first whatever the direction; then the column; equal lines by name. Sorting never moves a row's key. */
export function sortRows(rows: readonly Row[], key: SortKey, ascending: boolean): Row[] {
  return [...rows].sort((a, b) => {
    if (a.item.isFolder !== b.item.isFolder) return a.item.isFolder ? -1 : 1;
    let column = 0;
    if (key === 'size') column = a.item.size - b.item.size;
    else if (key === 'modified') column = Date.parse(a.item.modified ?? '') - Date.parse(b.item.modified ?? '') || 0;
    else if (key === 'here') column = hereRank(a.item.here) - hereRank(b.item.here);
    else column = byName(a.item.name, b.item.name);
    if (!ascending) column = -column;
    return column !== 0 ? column : byName(a.item.name, b.item.name);
  });
}

/** The Filter box: part of a name, without regard to case; empty keeps everything. */
export function filterMatches(name: string, filter: string | null | undefined): boolean {
  const wanted = (filter ?? '').trim();
  return wanted === '' || name.toLowerCase().includes(wanted.toLowerCase());
}

export function sizeText(item: SiteItem, format: (n: number) => string): string {
  return item.isFolder || item.size < 0 ? '' : format(item.size);
}

/** "same", "older" or "other": the colour of an "On this PC" text. */
export function hereTone(here: string | null | undefined): 'same' | 'older' | 'other' {
  return here === 'same' ? 'same' : here === 'olderHere' ? 'older' : 'other';
}

export function countText(shown: number, total: number): string {
  if (total <= 0 && shown <= 0) return 'Empty folder';
  if (total > shown) return `Showing ${shown.toLocaleString('en-US')} of ${total.toLocaleString('en-US')} items`;
  const count = Math.max(shown, total);
  return count === 1 ? '1 item' : `${count.toLocaleString('en-US')} items`;
}

/**
 * The footer of the remote pane. Selected: "1 of 6 selected · 5.8 GB · downloads go to /downloads/ISOs"; nothing selected:
 * "6 items · 2 already on the server". `where` is the folder a download lands in, or null for none to name.
 */
export function footerText(opts: {
  shown: number; total: number; selected: number; selectedBytes: number; same: number; linked: boolean;
  where: string | null; hostName: string; format: (n: number) => string;
}): string {
  const parts: string[] = [];
  if (opts.selected > 0) {
    parts.push(`${opts.selected.toLocaleString('en-US')} of ${opts.shown.toLocaleString('en-US')} selected`);
    if (opts.selectedBytes > 0) parts.push(opts.format(opts.selectedBytes));
    parts.push(opts.where ? `downloads go to ${opts.where}` : 'downloads go by your category rules');
  } else {
    parts.push(countText(opts.shown, opts.total));
    if (opts.linked && opts.shown > 0) parts.push(`${opts.same.toLocaleString('en-US')} already on ${opts.hostName}`);
  }
  return parts.join(' · ');
}

/** The footer of the server's own folders pane (it lists folders only). */
export function foldersFooter(count: number): string {
  return count === 0 ? 'No folders' : count === 1 ? '1 folder' : `${count.toLocaleString('en-US')} folders`;
}

/** "Downloads from /media/isos go to D:\Downloads\ISOs", as on the desktop; null when the remote has no linked folder. */
export function linkedLine(localFolder: string | null | undefined, remoteFolder: string | null | undefined): string | null {
  return localFolder?.trim() ? `Downloads from ${remoteFolder || '/'} go to ${localFolder}` : null;
}

// ---- Menus, Enter and double click -----------------------------------------------------------------------------------

export type RemoteCommand =
  | 'download' | 'downloadTo' | 'rename' | 'delete' | 'newFolder' | 'upload' | 'refresh' | 'pasteAddress'
  | 'copyAddress' | 'copyPath' | 'starFolder' | 'preview';

/**
 * The context menu as groups (a separator goes between groups; empty groups are left out). Nothing selected is the
 * empty-space menu. "Open", "Show in Explorer" and the like are not here: they open a program on the computer the
 * server runs on, which a browser cannot do. Items the remote cannot do are left out.
 */
export function remoteMenu(capabilities: readonly string[] | undefined, selected: readonly SiteItem[]): RemoteCommand[][] {
  const canNewFolder = capabilities?.includes('newFolder') === true;
  const groups: RemoteCommand[][] = [];
  const picked = actionable(selected);
  if (picked.length === 0) {
    groups.push(canNewFolder ? ['newFolder', 'upload'] : ['upload']);
    groups.push(['refresh', 'pasteAddress']);
    return groups;
  }
  const one = picked.length === 1;
  groups.push(one && !picked[0].isFolder ? ['download', 'downloadTo', 'preview'] : ['download', 'downloadTo']);
  const change: RemoteCommand[] = [];
  if (one) change.push('rename');
  change.push('delete');
  if (canNewFolder) change.push('newFolder');
  groups.push(change);
  const last: RemoteCommand[] = ['copyPath', 'copyAddress'];
  if (one && picked[0].isFolder) last.push('starFolder');
  groups.push(last);
  return groups;
}

export function commandLabel(command: RemoteCommand): string {
  switch (command) {
    case 'download': return 'Download';
    case 'downloadTo': return 'Download to…';
    case 'rename': return 'Rename';
    case 'delete': return 'Delete on server…';
    case 'newFolder': return 'New folder';
    case 'upload': return 'Upload…';
    case 'refresh': return 'Refresh';
    case 'pasteAddress': return 'Paste address';
    case 'copyAddress': return 'Copy address';
    case 'copyPath': return 'Copy path';
    case 'starFolder': return 'Star this folder';
    case 'preview': return 'Preview';
  }
}

/**
 * The key shown beside a menu item. Only keys the list itself answers: Ctrl+U (view source), Ctrl+Shift+C (the
 * browser's inspector) and Ctrl+Shift+N (Chrome's incognito window) are left to the browser.
 */
export function commandKey(command: RemoteCommand): string {
  switch (command) {
    case 'download': return 'Enter';
    case 'downloadTo': return 'Ctrl+Shift+S';
    case 'rename': return 'F2';
    case 'delete': return 'Delete';
    case 'refresh': return 'F5';
    default: return '';
  }
}

/** A key or a double click on a control inside a row (its checkbox, its "…" button, a link) belongs to that control. */
export function onRowControl(target: { closest?: (selector: string) => unknown } | null | undefined): boolean {
  return !!target?.closest?.('button, input, select, textarea, a');
}

/** The ".." line and a "." or ".." name are never items to act on. */
export function actionable<T extends { name: string }>(list: readonly T[]): T[] {
  const seen = new Set<string>();
  return list.filter((item) => item.name !== '' && item.name !== '.' && item.name !== '..' && !seen.has(item.name) && !!seen.add(item.name));
}

export type Activation = 'none' | 'goUp' | 'openFolder' | 'download';

/**
 * Enter or a double click on the selection: ".." alone goes up, one folder opens, anything else with a file in it (or
 * several items) downloads at once to the server, with no dialog (sites-rework decisions 1 and 2).
 */
export function activation(selected: readonly SiteItem[], parentSelected = false): Activation {
  if (parentSelected && selected.length === 0) return 'goUp';
  const picked = actionable(selected);
  if (picked.length === 0) return 'none';
  return picked.length === 1 && picked[0].isFolder ? 'openFolder' : 'download';
}

// ---- Download and upload ---------------------------------------------------------------------------------------------

export type DownloadWhere = 'server' | 'device';

/**
 * "To this device" is a browser download of one file, one at a time: a folder or several files have no single
 * file to hand the browser. It stops if the tab is closed, which the dialog says.
 */
export function deviceDownloadAllowed(selected: readonly SiteItem[]): boolean {
  const picked = actionable(selected);
  return picked.length === 1 && !picked[0].isFolder;
}

/** The folder on the server a download lands in: the remote's linked folder, else the server's download folder. */
export function serverFolder(site: Pick<Site, 'localFolder'>, defaultDownloadDirectory: string | null | undefined): string {
  return site.localFolder?.trim() || defaultDownloadDirectory?.trim() || '';
}

/**
 * The one folder on the server every download control uses (Download, Enter, a double click, the menu, the arrow between
 * the panes, the dialog's first folder) and the footer names: the folder selected in the left pane; else the folder the
 * left pane shows (Two panes only, and not its list of roots); else the remote's linked folder; else the server's
 * download folder. Empty when there is none, and then the Download dialog asks.
 */
export function downloadTarget(opts: {
  view: 'two' | 'server'; leftSelected?: string | null; leftShown?: string | null;
  linkedFolder?: string | null; defaultDownloadDirectory?: string | null;
}): string {
  const two = opts.view === 'two';
  return (two && opts.leftSelected?.trim()) || (two && opts.leftShown?.trim()) || opts.linkedFolder?.trim()
    || opts.defaultDownloadDirectory?.trim() || '';
}

/** How a download was asked for. Only the dialog has a choice of where. */
export type DownloadTrigger = 'button' | 'enter' | 'doubleClick' | 'menu' | 'arrow' | 'dialog';

/**
 * Where a download goes (bug 1): always the server, unless it came from the Download dialog with "To this device"
 * picked, and then only for what the device choice allows (one file).
 */
export function downloadWhere(trigger: DownloadTrigger, selected: readonly SiteItem[], dialogChoice?: DownloadWhere): DownloadWhere {
  return trigger === 'dialog' && dialogChoice === 'device' && deviceDownloadAllowed(selected) ? 'device' : 'server';
}

export interface TransferStart {
  siteId: string;
  direction: 'download' | 'upload';
  remoteFolder: string;
  localFolder: string;
  names: { name: string; isFolder: boolean }[];
  conflict: string;
}

/**
 * Download means to the server, into its folder (bug 1: it used to be a browser download). No dialog, so a name already
 * there is kept as a second copy rather than skipped or replaced without being asked.
 */
export function serverDownload(siteId: string, remoteFolder: string, localFolder: string, selected: readonly SiteItem[], conflict = 'keepBoth'): TransferStart {
  return {
    siteId, direction: 'download', remoteFolder, localFolder: localFolder.trim(),
    names: actionable(selected).map((item) => ({ name: item.name, isFolder: item.isFolder })), conflict,
  };
}

/** A folder on the server picked for upload, as the parent the Host reads and the one name in it. Null for a drive root. */
export function splitServerFolder(path: string): { parent: string; name: string } | null {
  const trimmed = path.replace(/[\\/]+$/, '');
  const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  if (cut < 0) return null;
  const name = trimmed.slice(cut + 1);
  const parent = trimmed.slice(0, cut + 1);
  if (!name || /^[A-Za-z]:$/.test(name) || /^[A-Za-z]:$/.test(trimmed)) return null;
  return { parent: parent.length > 1 && !/^[A-Za-z]:[\\/]$/.test(parent) ? parent.replace(/[\\/]+$/, '') : parent, name };
}

const samePath = (a: string, b: string) => {
  const norm = (value: string) => value.trim().replace(/[\\/]+$/, '').replace(/\\/g, '/');
  const x = norm(a), y = norm(b);
  return /^[A-Za-z]:/.test(x) || /^[A-Za-z]:/.test(y) ? x.toLowerCase() === y.toLowerCase() : x === y;
};

/**
 * Why a server folder cannot be uploaded, or null when it can. The Host reads an upload as a name in its parent folder,
 * and the parent of an allowed folder (D:\Downloads gives D:\) is outside what it may use, so an allowed folder itself
 * cannot go up; one inside it can.
 */
export function uploadSourceProblem(serverPath: string | null | undefined, roots: readonly string[], isFolder = true): string | null {
  const path = (serverPath ?? '').trim();
  if (!path) return 'Choose a folder on the server.';
  const root = roots.find((item) => samePath(item, path));
  if (root) return `Choose a folder inside ${root}.`;
  if (!isFolder) return splitServerFolder(path) ? null : 'Choose a file on the server.';
  if (!splitServerFolder(path)) return 'Choose a folder on the server.';
  return null;
}

export function serverUpload(siteId: string, remoteFolder: string, serverPath: string, conflict = 'keepBoth', roots: readonly string[] = [], isFolder = true): TransferStart | null {
  if (uploadSourceProblem(serverPath, roots, isFolder)) return null;
  const split = splitServerFolder(serverPath);
  if (!split) return null;
  return { siteId, direction: 'upload', remoteFolder, localFolder: split.parent, names: [{ name: split.name, isFolder }], conflict };
}

// ---- Copy and paste an address ---------------------------------------------------------------------------------------

/** "sftp://user@host:2222/media/my%20files": a password is never part of it. A cloud drive has no address of its own. */
export function addressOf(site: Site, path: string): string {
  const host = (site.host ?? '').trim().replace(/^\[|\]$/g, '');
  const escaped = (value: string) => value.split('/').map(encodeURIComponent).join('/') || '/';
  const user = site.user ? `${encodeURIComponent(site.user)}@` : '';
  const port = (def: number) => (site.port > 0 && site.port !== def ? `:${site.port}` : '');
  const hostText = host.includes(':') ? `[${host}]` : host;
  switch (site.kind) {
    case 'sftp': return `sftp://${user}${hostText}${port(22)}${escaped(path)}`;
    case 'ftp': {
      const tls = site.tls ?? 'explicit';
      const implicit = tls === 'implicit';
      return `${tls === 'none' ? 'ftp' : 'ftps'}://${user}${hostText}${port(implicit ? 990 : 21)}${escaped(path)}`;
    }
    case 'webdav': {
      try {
        const url = new URL(site.url ?? '');
        const base = url.pathname.replace(/\/+$/, '');
        return `${url.protocol}//${site.user ? `${encodeURIComponent(site.user)}@` : ''}${url.host}${base}${path === '/' && base ? '' : escaped(path)}`;
      } catch { return escaped(path); }
    }
    default: return `${site.kind}://${host || site.name}${escaped(path)}`;
  }
}

export function addressesOf(site: Site, folder: string, names: readonly string[]): string {
  return names.length === 0 ? addressOf(site, folder) : names.map((name) => addressOf(site, joinPath(folder, name))).join('\n');
}

/** "Copy path": the path of each name in the folder, one a line; the folder's own when nothing is named. */
export function pathsOf(folder: string, names: readonly string[]): string {
  return names.length === 0 ? folder : names.map((name) => joinPath(folder, name)).join('\n');
}

/** "Paste address": the path of this remote that the text names, or null. A path from "/" is taken as it is. */
export function pathFromText(site: Site, text: string | null | undefined): string | null {
  const line = (text ?? '').split(/[\r\n]/).map((part) => part.trim().replace(/^"|"$/g, '')).find((part) => part.length > 0);
  if (!line) return null;
  const normal = (path: string) => {
    const kept: string[] = [];
    for (const part of path.split('/')) {
      if (!part || part === '.') continue;
      if (part === '..') kept.pop();
      else kept.push(part);
    }
    return `/${kept.join('/')}`;
  };
  if (line.startsWith('/')) return normal(line);
  let url: URL;
  try { url = new URL(line); } catch { return null; }
  if (!url.hostname) return null;
  const mine = addressOf(site, '/');
  let own: URL;
  try { own = new URL(mine.endsWith('/') ? mine : `${mine}/`); } catch { return null; }
  if (own.protocol !== url.protocol || own.hostname.toLowerCase() !== url.hostname.toLowerCase()) return null;
  if ((own.port || '') !== (url.port || '')) return null;
  const base = decodeURIComponent(own.pathname).replace(/\/+$/, '');
  const path = decodeURIComponent(url.pathname);
  if (base && !path.startsWith(base)) return null;
  return normal(path.slice(base.length));
}

// ---- Trouble ---------------------------------------------------------------------------------------------------------

export const cantReach = 'cantReach';

/** A listing that failed for no named reason is a server that did not answer, so the pane tries again by itself. */
export const isCantReach = (problem: string | null | undefined) => problem === cantReach;

/** Seconds before the next automatic try: 10, 30, 60, then 60 again. `attempt` counts failures from 0. */
export function retryDelaySeconds(attempt: number): number {
  return attempt <= 0 ? 10 : attempt === 1 ? 30 : 60;
}

export const maxAutoTries = 5;
export const maxAutoRetryMs = 10 * 60 * 1000;

export function autoRetryAllowed(triesDone: number, elapsedMs: number): boolean {
  return triesDone < maxAutoTries && elapsedMs < maxAutoRetryMs;
}

export function retryAtText(at: Date): string {
  const two = (n: number) => String(n).padStart(2, '0');
  return `Trying again at ${two(at.getHours())}:${two(at.getMinutes())}.`;
}

export const notRetryingText = 'Not trying again on its own; choose Retry now.';

export function troubleTitle(problem: string | null | undefined, name: string): string {
  return isCantReach(problem) ? `Can't reach ${name}` : `Can't connect to ${name}`;
}

/** The problems the user fixes with a button beside the message (the fix dialog of SiteFix). */
export function fixable(problem: string | null | undefined): boolean {
  return problem === problems.hostKeyNotAccepted || problem === problems.hostKeyChanged
    || problem === problems.certificateNotTrusted || problem === problems.signInNeeded;
}

export function fixButtonText(problem: string | null | undefined): string {
  switch (problem) {
    case problems.hostKeyChanged: case problems.hostKeyNotAccepted: return 'Check the key';
    case problems.certificateNotTrusted: return 'Check the certificate';
    case problems.signInNeeded: return 'Sign in';
    default: return '';
  }
}

/** The line under the title: the server's own words, or the known words for a problem the user fixes. */
export function troubleWhy(problem: string | null | undefined, message: string | null | undefined): string {
  const host = (message ?? '').trim();
  switch (problem) {
    case problems.hostKeyChanged:
      return "This server's host key is not the one you accepted, so Inbound did not connect. That can mean the server was reinstalled, or that someone is intercepting the connection. Check with the server's owner, then press Check the key to replace it.";
    case problems.hostKeyNotAccepted:
      return "This remote's server key has not been accepted yet. Press Check the key to see it and accept it.";
    case problems.off:
      return 'Remote files are off. Turn them on in Options > Remote files.';
    case problems.certificateNotTrusted:
      return "This server's certificate is not signed by an authority the server trusts, so Inbound did not connect. If it is your own server, press Check the certificate to see it and choose whether to trust it.";
    case problems.signInNeeded:
      return 'This remote is not signed in, or the service no longer accepts its sign-in. Press Sign in.';
    default:
      return host || (isCantReach(problem) ? 'The server did not answer.' : 'The folder could not be read.');
  }
}

/** What the browse view's connection depends on: when it changes while the folder cannot be read, read it again. */
export function connectionKey(site: Site | undefined): string {
  if (!site) return '';
  return [
    site.kind, site.host, site.port, site.user, site.authMethod, site.hasPassword, site.hasPrivateKey, site.hasKeyPassphrase,
    site.remoteFolder, site.url ?? '', site.endpoint ?? '', site.tls ?? '', site.provider ?? '', site.region ?? '', site.clientId ?? '',
    site.hasClientSecret, site.signedIn, site.hostKeys.map((key) => key.fingerprint).join(','), site.trustedCertificates.length,
  ].join('\n');
}

export function lastListedText(listedMs: number, nowMs: number): string {
  const age = nowMs - listedMs;
  if (age < 60_000) return 'Last listed just now';
  if (age < 3_600_000) return `Last listed ${Math.floor(age / 60_000)} min ago`;
  if (age < 86_400_000) return `Last listed ${Math.floor(age / 3_600_000)} h ago`;
  return `Last listed ${Math.floor(age / 86_400_000)} d ago`;
}

/** The delete question's words, with "N items (size)" once the server has counted (R7, the mockup's delete sheet). */
export function deleteText(
  list: readonly Pick<SiteItem, 'name' | 'isFolder'>[],
  count: { files: number; folders: number; bytes: number; sizeUnknown: boolean } | null,
  size: (n: number) => string,
): string {
  const items = count ? count.files + count.folders : 0;
  const sized = count && !count.sizeUnknown && count.bytes ? ` (${size(count.bytes)})` : '';
  const plural = (n: number) => `${n.toLocaleString()} item${n === 1 ? '' : 's'}`;
  let what: string;
  if (list.length === 1 && !list[0].isFolder) what = `${list[0].name}${sized}`;
  // The Host counts each chosen folder itself in `folders` (and the files under it, not its subfolders): not "inside it".
  else if (list.length === 1) what = count ? `${list[0].name} and the ${plural(Math.max(0, items - count.folders))} inside it${sized}` : `${list[0].name} and everything in it`;
  else what = count ? `${plural(list.length)}${items > list.length ? `, ${plural(items)} with what is inside them,` : ''}${sized}` : `${plural(list.length)}, and everything in their folders,`;
  return `${what} will be deleted on the server. This can't be undone.${count ? '' : ' Counting what is in it…'}`;
}

export { crumbs, hereText, parentPath };
