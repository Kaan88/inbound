// The words and choices of All remotes, Add a remote and the sidebar entries (SITES.11 R6b), as the desktop has them:
// SitesHomeRules.cs. Names follow SITES.12: a place is "a remote", the whole is "Remote files". No UI.
import { isDownload, isStarted } from './sitesTransfers';
import type { Site, SiteItem, SiteKind, SiteResult, SiteTransfer } from './sites';
import { problems } from './sites';

export const heading = 'Remote files';
export const allRemotes = 'All remotes';
export const addRemote = 'Add a remote';
export const openHint = 'Open an address once, for example sftp://nas.home.lan/media';

/** One tile of Add a remote: what it is called, what it says, and the Host kinds it stands for. */
export interface Tile { id: string; name: string; line: string; icon: 'server' | 'cloud'; kinds: string[]; formTitle: string }

/** The tiles in the order drawn. "Ask each time" and "More…" are not here: the Host has no such sign-in or kind. */
export const tiles: readonly Tile[] = [
  { id: 'sftp', name: 'SFTP', line: 'A server you sign in to with SSH', icon: 'server', kinds: ['sftp'], formTitle: 'Add an SFTP remote' },
  { id: 'ftp', name: 'FTP', line: 'Older servers, with or without TLS', icon: 'server', kinds: ['ftp'], formTitle: 'Add an FTP remote' },
  { id: 'webdav', name: 'WebDAV', line: 'Nextcloud, ownCloud, Synology', icon: 'cloud', kinds: ['webdav'], formTitle: 'Add a WebDAV remote' },
  { id: 'drive', name: 'Google Drive', line: 'Signs in through your browser', icon: 'cloud', kinds: ['drive'], formTitle: 'Add a Google Drive remote' },
  { id: 'onedrive', name: 'OneDrive', line: 'Signs in through your browser', icon: 'cloud', kinds: ['onedrive'], formTitle: 'Add a OneDrive remote' },
  { id: 'dropbox', name: 'Dropbox', line: 'Signs in through your browser', icon: 'cloud', kinds: ['dropbox'], formTitle: 'Add a Dropbox remote' },
  { id: 's3b2', name: 'S3 or B2', line: 'Access key and bucket', icon: 'cloud', kinds: ['s3', 'b2'], formTitle: 'Add an S3 or B2 remote' },
];

/** The tiles whose kinds the Host offers (none listed: an older Host, which offers SFTP). */
export function tilesFor(hostKinds: readonly Pick<SiteKind, 'kind'>[]): Tile[] {
  const offered = new Set(hostKinds.map((kind) => kind.kind));
  if (offered.size === 0) offered.add('sftp');
  return tiles.filter((tile) => tile.kinds.some((kind) => offered.has(kind)));
}

export const tileOf = (kind: string): Tile | undefined => tiles.find((tile) => tile.kinds.includes(kind));

/**
 * The Host kind a tile opens its form with: the tile's first kind the Host offers. S3 or B2 starts as S3, and the form's
 * Kind choice then switches to B2.
 */
export function kindOfTile(tile: Tile, hostKinds: readonly Pick<SiteKind, 'kind'>[]): string | undefined {
  const offered = new Set(hostKinds.map((kind) => kind.kind));
  return tile.kinds.find((kind) => offered.has(kind)) ?? tile.kinds[0];
}

/** The kinds a form for this tile lets the user switch between: only a tile with two (S3 or B2). */
export const switchableKinds = (tile: Tile | undefined, hostKinds: readonly SiteKind[]): SiteKind[] =>
  tile && tile.kinds.length > 1 ? hostKinds.filter((kind) => tile.kinds.includes(kind.kind)) : [];

/** The name a remote gets when none is typed: its server, or the service's own name. */
export function defaultName(kind: string, host: string, url?: string | null, endpoint?: string | null): string {
  const hostOf = (address?: string | null) => {
    try { return address?.trim() ? new URL(address.trim()).hostname || null : null; } catch { return null; }
  };
  const address = host.trim() || hostOf(url) || hostOf(endpoint);
  return address || tileOf(kind)?.name || kind;
}

export const formName = (typed: string, kind: string, host: string, url?: string | null, endpoint?: string | null) =>
  typed.trim() ? typed.trim() : defaultName(kind, host, url, endpoint);

const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const pad = (n: number) => String(n).padStart(2, '0');

/** "Today 09:14", "Yesterday 22:15", "Nov 10" (with the year when it is not this one); "Never" before first use. */
export function lastUsedText(usedAt: string | null | undefined, now: Date = new Date()): string {
  const at = usedAt ? new Date(usedAt) : null;
  if (!at || Number.isNaN(at.getTime())) return 'Never';
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const gap = Math.round((day(now) - day(at)) / 86_400_000);
  const time = `${pad(at.getHours())}:${pad(at.getMinutes())}`;
  if (gap === 0) return `Today ${time}`;
  if (gap === 1) return `Yesterday ${time}`;
  return at.getFullYear() === now.getFullYear() ? `${months[at.getMonth()]} ${at.getDate()}` : `${months[at.getMonth()]} ${at.getDate()}, ${at.getFullYear()}`;
}

// ---- Sidebar entries and activity ---------------------------------------------------------------------------------------

/** What runs on a remote: files moving by direction, batches waiting, the progress of those that move, how many batches move. */
export interface Badge { down: number; up: number; waiting: number; progress: number | null; running: number }

export function badge(transfers: readonly SiteTransfer[], siteId: string): Badge {
  const running = transfers.filter((t) => t.siteId === siteId && t.state === 'running');
  if (!running.length) return { down: 0, up: 0, waiting: 0, progress: null, running: 0 };
  const started = running.filter(isStarted);
  const down = started.filter(isDownload).reduce((sum, t) => sum + (t.filesRunning ?? 0), 0);
  const up = started.filter((t) => !isDownload(t)).reduce((sum, t) => sum + (t.filesRunning ?? 0), 0);
  const total = started.reduce((sum, t) => sum + t.bytes, 0);
  const files = started.reduce((sum, t) => sum + t.files, 0);
  const clamp = (value: number) => Math.min(100, Math.max(0, value));
  const progress = started.length === 0 ? null
    : total > 0 ? clamp((100 * started.reduce((sum, t) => sum + t.bytesDone, 0)) / total)
    : files > 0 ? clamp((100 * started.reduce((sum, t) => sum + t.filesDone + t.filesSkipped + t.filesFailed, 0)) / files)
    : 0;
  return { down, up, waiting: running.length - started.length, progress, running: started.length };
}

/** The arrows part of a sidebar entry: "↓1 ↑1", or empty. */
export const arrowsText = (b: Badge) => [b.down > 0 ? `↓${b.down}` : '', b.up > 0 ? `↑${b.up}` : ''].filter(Boolean).join(' ');

/** The count beside the clock while batches wait, or empty (no clock then). */
export const waitingCount = (b: Badge) => (b.waiting > 0 ? String(b.waiting) : '');

/** The All remotes row's right-hand text: "2 transfers running" (batches, not files), "1 waiting"; null when idle. */
export function activityText(b: Badge): string | null {
  if (b.running > 0) return b.running === 1 ? '1 transfer running' : `${b.running} transfers running`;
  return b.waiting > 0 ? `${b.waiting} waiting` : null;
}

/** The screen reader's words for an entry; null when idle. */
export function activityWords(b: Badge): string | null {
  const parts = [b.down > 0 ? `${b.down} downloading` : '', b.up > 0 ? `${b.up} uploading` : '', b.waiting > 0 ? `${b.waiting} waiting` : ''].filter(Boolean);
  return parts.length ? parts.join(', ') : null;
}

export const footerText = (count: number) => (count === 0 ? 'No remotes yet' : count === 1 ? '1 remote' : `${count} remotes`);

/** The remotes that show in the sidebar and on All remotes: saved ones first in the Host's order, then those opened once. */
export const listed = (sites: readonly Site[]): Site[] => [...sites.filter((site) => !site.unsaved), ...sites.filter((site) => site.unsaved)];

const signsIn = (site: Site) => site.capabilities.includes('signIn');

/** The dot is a warning: the Host could not reach it last, or a sign-in is missing. */
export const inTrouble = (site: Site) => site.reach === 'trouble' || (signsIn(site) && !site.signedIn);

export function dotWords(site: Site): string {
  if (site.reach === 'trouble') return 'Could not be reached';
  if (signsIn(site) && !site.signedIn) return 'Not signed in yet';
  return 'Fine';
}

/** The phone's list line under a remote's name: its kind, as the page says it. */
export const kindLine = (site: Site, chip: string) => (site.unsaved ? `${chip} · opened once` : chip);

// ---- Open once -----------------------------------------------------------------------------------------------------------

/** The Host holds at most this many addresses opened once. */
export const mostOpenOnce = 8;
export const closeOneFirst = 'Close an opened address first.';

/** The remotes with batches that are not finished: running (waiting among them) or paused. */
export const busyRemotes = (transfers: readonly SiteTransfer[]): Set<string> =>
  new Set(transfers.filter((t) => t.state === 'running' || t.state === 'paused').map((t) => t.siteId));

/** How many batches on a remote are not finished (running, waiting or paused): Close on an address opened once cancels them. */
export const unfinishedOn = (transfers: readonly SiteTransfer[], siteId: string): number =>
  transfers.filter((t) => t.siteId === siteId && (t.state === 'running' || t.state === 'paused')).length;

/** The question before an address opened once is closed while it has unfinished transfers; null when nothing would be lost. */
export function closeQuestion(name: string, unfinished: number): { title: string; text: string } | null {
  if (unfinished <= 0) return null;
  const what = unfinished === 1 ? '1 transfer' : `${unfinished} transfers`;
  return { title: `Close ${name}?`, text: `${what} will be cancelled. Inbound forgets this address; nothing on the server is deleted.` };
}

/** The toast after the oldest addresses opened once were closed to make room for another: "Closed nas.home.lan to make room." */
export const closedToMakeRoom = (names: readonly string[]) => `Closed ${names.join(', ')} to make room.`;

/**
 * The unsaved remotes to forget before another address is opened, so no more than `most` are open: the oldest first,
 * never the one shown and never one with batches that are not finished. Null when not enough can be forgotten.
 */
export function forgetBeforeOpening(open: readonly Site[], shownId: string | null | undefined, busy: ReadonlySet<string>, most = mostOpenOnce): string[] | null {
  const unsaved = open.filter((site) => site.unsaved);
  const over = unsaved.length - (most - 1);
  if (over <= 0) return [];
  const free = unsaved.filter((site) => site.id !== shownId && !busy.has(site.id)).slice(0, over).map((site) => site.id);
  return free.length < over ? null : free;
}

/** What follows a browse of a remote: an unsaved one that needs its password asks for it. */
export const needsPassword = (problem: string | null | undefined, unsaved: boolean | undefined) => !!unsaved && problem === problems.passwordNeeded;

/** An address opened once is not edited (the Host keeps it in memory only); it can be remembered instead. */
export const canEdit = (site: Site) => !site.unsaved;

// ---- The form's test line ------------------------------------------------------------------------------------------------

/** The Test line's good outcome: "It works. Signed in and listed the start folder (6 items) in 0.4 s." */
export function testWorks(signs: boolean, seconds: number, items?: number | null): string {
  const count = items == null ? '' : items === 1 ? ' (1 item)' : ` (${items.toLocaleString('en-US')} items)`;
  return `It works. ${signs ? 'Signed in' : 'Connected'} and listed the start folder${count} in ${seconds.toFixed(1)} s.`;
}

/** The Test line for the Host's answer to the form's draft (sites.testDraft). */
export function draftTestText(result: SiteResult & { itemCount?: number | null; offeredKeys?: unknown[] | null; certificate?: { cannotTrust?: string | null } | null }, signs: boolean, seconds: number): string {
  if (result.ok) return testWorks(signs, seconds, result.itemCount);
  switch (result.problem) {
    case problems.hostKeyNotAccepted:
      return result.offeredKeys?.length
        ? "The server's key isn't trusted yet. Compare it with the one your server's administrator gave you, then press Trust."
        : "The server's key isn't trusted yet, and it couldn't be read.";
    case problems.hostKeyChanged: return 'The server shows a different key from the one you trusted. Save, then Test the remote to see it.';
    case problems.certificateNotTrusted:
      if (result.certificate?.cannotTrust) return `The server's certificate can't be used. ${result.certificate.cannotTrust}`;
      return result.certificate
        ? "The server's certificate isn't signed by an authority this PC trusts. Trust it only if it is your own server's; it is trusted when you save."
        : "The server's certificate isn't trusted yet: press Save, and Inbound shows it to you.";
    case problems.signInNeeded: return 'Not signed in yet: press Save, and your browser opens to sign in.';
    default: return result.message?.trim() ? result.message : 'The test failed.';
  }
}

/** True when the Host has no such request (it is older). */
export const hostLacks = (message: string | null | undefined) => !!message?.startsWith('Unknown Sites request');

/** "Sign in with" choices of a kind, by the Host's method names. */
export const methodLabel = (method: string) => (method === 'key' ? 'Key file' : 'Password');

export const advancedLabel = (hasPort: boolean) => (hasPort ? 'Advanced: name, port, starting folder' : 'Advanced: name, starting folder');

// ---- Folders: "Some of it" ----------------------------------------------------------------------------------------------

/** The folder names a page asks sites.here about: at most 100, folders only. */
export const hereNames = (items: readonly SiteItem[], most = 100): string[] => items.filter((item) => item.isFolder).slice(0, most).map((item) => item.name);

/** The listing with the Host's answer laid over the folders it compared; a folder it did not compare keeps the browse's own value. */
export function applyHere(items: readonly SiteItem[], folders: readonly { name: string; here?: string | null }[]): SiteItem[] {
  const byName = new Map(folders.filter((folder) => folder.here).map((folder) => [folder.name, folder.here as string]));
  if (!byName.size) return [...items];
  return items.map((item) => (item.isFolder && byName.has(item.name) ? { ...item, here: byName.get(item.name) } : item));
}

// ---- Phone width ---------------------------------------------------------------------------------------------------------

export type PhoneAction = 'download' | 'rename' | 'delete';

/** The buttons of the phone's action bar for the ticked rows: Download for any, Rename for one, Delete on the server for any. */
export function phoneActions(ticked: number): PhoneAction[] {
  if (ticked <= 0) return [];
  return ticked === 1 ? ['download', 'rename', 'delete'] : ['download', 'delete'];
}

/**
 * The line a phone row says under its name (`phone`): "Folder" or the size, then how it compares with the folder on the
 * computer Inbound runs on, in words ("already on home-server", "some of it on home-server"); nothing more when it is not there.
 */
export function phoneRowLine(item: SiteItem, size: string, here: string | null | undefined, hostName: string): string {
  const first = item.isFolder ? 'Folder' : size;
  const words: Record<string, string> = {
    same: 'already on', someOfIt: 'some of it on', olderHere: 'an older copy on', different: 'a different copy on', folderHere: 'a folder of that name on',
  };
  const where = here ? words[here] : undefined;
  return where ? `${first} · ${where} ${hostName}` : first;
}

/** The phone's All remotes subtitle: "5 remotes · 2 with transfers". */
export function phoneRemotesLine(remotes: number, withTransfers: number): string {
  const count = remotes === 1 ? '1 remote' : `${remotes} remotes`;
  return withTransfers ? `${count} · ${withTransfers} with transfers` : count;
}
