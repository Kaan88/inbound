// Remote files extras (SITES.13/14) in the web UI: remember the folder and sort, preview, the left pane of Two panes,
// copy between remotes, sync and schedules. The Host's words are in docs/ipc-protocol.md "Remote files extras".
// No UI, so the tests call it directly.
import { joinPath, type Site, type SiteItem, type SitePreview, type SiteSyncPlan, type SiteTransfer } from './sites';
import type { SortKey, TransferStart } from './sitesBrowse';

// ---- Remember the last folder and the sort ----------------------------------------------------------------------------

/** The sort the Host keeps (name, size, modified, type); "Already on the server" is a view of this page only. */
export function sortToHost(key: SortKey): string | null {
  return key === 'here' ? null : key;
}

/** The sort the page starts with: the remembered one, and only a column this page has (type reads as name). */
export function sortFromSite(site: Pick<Site, 'sort' | 'sortDescending'>): { key: SortKey; ascending: boolean } {
  const key: SortKey = site.sort === 'size' || site.sort === 'modified' ? site.sort : 'name';
  return { key, ascending: !(site.sortDescending && (site.sort === 'name' || site.sort === 'size' || site.sort === 'modified' || site.sort === 'type')) };
}

/** The folder a remote opens in: the last one it was left in, else its start folder, else the top. */
export function startFolder(site: Pick<Site, 'lastFolder' | 'remoteFolder'>): string {
  return site.lastFolder?.trim() || site.remoteFolder?.trim() || '/';
}

/** The request that remembers a folder; an address opened once is never saved, so it is never remembered. */
export function rememberFolder(site: Pick<Site, 'id' | 'unsaved' | 'lastFolder'>, folder: string): { id: string; lastFolder: string } | null {
  if (site.unsaved || !folder || (site.lastFolder ?? '') === folder) return null;
  return { id: site.id, lastFolder: folder };
}

export function rememberSort(site: Pick<Site, 'id' | 'unsaved'>, key: SortKey, ascending: boolean): { id: string; sort: string; sortDescending: boolean } | null {
  const sort = sortToHost(key);
  return site.unsaved || !sort ? null : { id: site.id, sort, sortDescending: !ascending };
}

// ---- Preview ----------------------------------------------------------------------------------------------------------

/** A row menu's Preview: one file, not a folder. */
export function canPreview(selected: readonly Pick<SiteItem, 'isFolder'>[]): boolean {
  return selected.length === 1 && !selected[0].isFolder;
}

/** The text of a preview: its bytes (base64) read as UTF-8. Empty when there are none. */
export function previewText(preview: Pick<SitePreview, 'data'>): string {
  if (!preview.data) return '';
  const binary = atob(preview.data);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder('utf-8').decode(bytes);
}

/** An image's address for <img>. Only the image types the Host names are drawn; SVG comes as text and is never drawn. */
export function previewImageSrc(preview: Pick<SitePreview, 'kind' | 'mimeType' | 'data'>): string | null {
  const mime = (preview.mimeType ?? '').toLowerCase();
  return preview.kind === 'image' && preview.data && /^image\/(png|jpeg|gif|webp|bmp)$/.test(mime) ? `data:${mime};base64,${preview.data}` : null;
}

export function previewNote(preview: SitePreview, size: (n: number) => string): string {
  if (!preview.ok) return preview.message ?? 'That file could not be previewed.';
  if (preview.kind === 'none') return preview.message ?? 'There is no preview for this kind of file.';
  return preview.truncated ? `Showing the first part of ${size(preview.size)}.` : size(preview.size);
}

// ---- The left pane of Two panes ---------------------------------------------------------------------------------------

/** What the left pane shows: the server's own folders, or one of the remotes (the right pane's too). */
export type LeftSource = { kind: 'server' } | { kind: 'remote'; siteId: string };

export const serverSource: LeftSource = { kind: 'server' };

export const sourceValue = (source: LeftSource): string => (source.kind === 'server' ? 'server' : `remote:${source.siteId}`);

export function sourceFromValue(value: string, sites: readonly Pick<Site, 'id'>[]): LeftSource {
  const id = value.startsWith('remote:') ? value.slice(7) : '';
  return id && sites.some((site) => site.id === id) ? { kind: 'remote', siteId: id } : serverSource;
}

/** The choices of the left pane's switch: the server first, then every saved remote, the right pane's own included. */
export function sourceChoices(sites: readonly Pick<Site, 'id' | 'name' | 'unsaved'>[], hostName: string): { value: string; label: string }[] {
  return [
    { value: 'server', label: `${hostName}'s folders` },
    ...sites.filter((site) => !site.unsaved).map((site) => ({ value: `remote:${site.id}`, label: site.name })),
  ];
}

const readSource = (): string => { try { return localStorage.getItem('inbound.sites.left') ?? 'server'; } catch { return 'server'; } };
export const savedSource = readSource;
export function saveSource(value: string) {
  try { localStorage.setItem('inbound.sites.left', value); } catch { /* a convenience only */ }
}

/** A copy from one remote to another (or to another folder of the same one). */
export function betweenCopy(fromSiteId: string, fromFolder: string, toSiteId: string, toFolder: string, picked: readonly Pick<SiteItem, 'name' | 'isFolder'>[], conflict = 'keepBoth'):
  (Omit<TransferStart, 'direction'> & { direction: 'between'; targetSiteId: string; targetFolder: string }) | null {
  const names = picked.filter((item) => item.name && item.name !== '.' && item.name !== '..').map((item) => ({ name: item.name, isFolder: item.isFolder }));
  if (!names.length || !toFolder) return null;
  return { siteId: fromSiteId, direction: 'between', remoteFolder: fromFolder, localFolder: '', names, conflict, targetSiteId: toSiteId, targetFolder: toFolder };
}

/** Copying a folder into itself or its own subfolder on one remote would never end; the Host may refuse, the page says why first. */
export function betweenProblem(fromSiteId: string, fromFolder: string, toSiteId: string, toFolder: string, picked: readonly Pick<SiteItem, 'name' | 'isFolder'>[]): string | null {
  if (fromSiteId !== toSiteId) return null;
  const trim = (path: string) => path.replace(/\/+$/, '') || '/';
  if (trim(fromFolder) === trim(toFolder)) return 'Choose another folder: the files are already here.';
  for (const item of picked) {
    if (!item.isFolder) continue;
    const inside = trim(joinPath(fromFolder, item.name));
    if (trim(toFolder) === inside || trim(toFolder).startsWith(`${inside}/`)) return `A folder can't be copied into itself (${item.name}).`;
  }
  return null;
}

/** The server's folder listing now has files: a file picked there goes up as a name in its parent, a folder as before. */
export function pathParts(path: string): { parent: string; name: string } | null {
  const trimmed = path.replace(/[\\/]+$/, '');
  const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  if (cut < 0 || cut === trimmed.length - 1) return null;
  return { parent: trimmed.slice(0, cut + 1).length > 1 && !/^[A-Za-z]:[\\/]$/.test(trimmed.slice(0, cut + 1)) ? trimmed.slice(0, cut) : trimmed.slice(0, cut + 1), name: trimmed.slice(cut + 1) };
}

/** True when a transfer that was running or waiting is finished now: the panes list again. */
export function finishedSince(before: readonly SiteTransfer[], after: readonly SiteTransfer[]): SiteTransfer[] {
  const was = new Map(before.map((t) => [t.id, t.state]));
  return after.filter((t) => t.state === 'done' && ['running', 'paused'].includes(was.get(t.id) ?? ''));
}

/** Which listings a finished transfer changed: a download changes the server's folder, an upload or copy a remote's folder. */
export function touches(t: SiteTransfer): { server: boolean; remoteIds: string[] } {
  if (t.direction === 'download') return { server: true, remoteIds: [] };
  if (t.direction === 'between') return { server: false, remoteIds: t.targetSiteId ? [t.targetSiteId] : [] };
  return { server: false, remoteIds: [t.siteId] };
}

// ---- Sync -------------------------------------------------------------------------------------------------------------

export type SyncDirection = 'download' | 'upload';

export function syncDirectionLabel(direction: SyncDirection, hostName: string, siteName: string): string {
  return direction === 'download' ? `${siteName} to ${hostName}` : `${hostName} to ${siteName}`;
}

/** "12 files to copy (3.4 GB): 9 new, 3 changed. 40 already match. 2 are newer there and are left alone." */
export function planSummary(plan: SiteSyncPlan, size: (n: number) => string): string {
  if (!plan.ok) return plan.message ?? 'The folders could not be compared.';
  const n = (value: number) => value.toLocaleString('en-US');
  if (plan.count === 0) return plan.newerThere > 0 ? `Nothing to copy. ${n(plan.newerThere)} newer there ${plan.newerThere === 1 ? 'is' : 'are'} left alone.` : 'Nothing to copy: the folders match.';
  const fresh = plan.files.filter((file) => file.reason === 'new').length;
  const changed = plan.files.filter((file) => file.reason === 'changed').length;
  const parts = [`${n(plan.count)} ${plan.count === 1 ? 'file' : 'files'} to copy${plan.bytes > 0 ? ` (${size(plan.bytes)})` : ''}`];
  if (plan.files.length === plan.count) parts[0] += `: ${n(fresh)} new, ${n(changed)} changed`;
  const text = [`${parts[0]}.`];
  if (plan.same > 0) text.push(`${n(plan.same)} already match.`);
  if (plan.newerThere > 0) text.push(`${n(plan.newerThere)} newer there ${plan.newerThere === 1 ? 'is' : 'are'} left alone.`);
  return text.join(' ');
}

/** Nothing is deleted by a sync, and the dialog says so. */
export const syncNote = 'Only new and changed files are copied. Nothing is deleted on either side, and a file that is newer at the target is left alone.';

export function syncRequest(site: Pick<Site, 'id'>, direction: SyncDirection, remoteFolder: string, localFolder: string) {
  return { siteId: site.id, direction, remoteFolder, localFolder: localFolder.trim() };
}

export const planShown = 200;

// ---- Schedules --------------------------------------------------------------------------------------------------------

export const waitsForSchedule = (t: Pick<SiteTransfer, 'waitsForSchedule'>): boolean => t.waitsForSchedule === true;

/** A batch that waits for a schedule can start now (Resume) or be held for one; a running or paused one can be made to wait. */
export function canWait(t: Pick<SiteTransfer, 'state' | 'waitsForSchedule'>): boolean {
  return t.state === 'paused' && !t.waitsForSchedule;
}
