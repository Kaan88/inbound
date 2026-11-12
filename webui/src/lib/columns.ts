// UI.R42 in the web UI: a table's columns can be shown or hidden from its header's menu (a right-click, Shift+F10 or the
// menu key on a header), with Reset columns, as the Windows app's lists do (ListColumns.cs, ColumnHeaderRow.cs). The
// name column always shows. Choices are kept in this browser. Nothing here touches the page, so the tests call it directly.

/** One column of a table with a header. */
export interface ColumnDef<K extends string = string> {
  key: K;
  title: string;
  /** What the menu says for a title that is only a letter or a short word ("Queue" for "Q"). */
  spoken?: string;
  /** Whether it shows until someone chooses otherwise. */
  shownAtFirst: boolean;
  /** The download list: shown only from 1024 px wide (and not beside Torrent Info), as Description and Last Try Date always were. */
  wide?: boolean;
  /** The download list: its CSS grid track. */
  track?: string;
  align?: 'left' | 'right' | 'center';
}

/** Columns shown (true) or hidden (false) from the menu, by key. A column left out shows as it does at first. */
export type ColumnChoices = Record<string, boolean>;

/** What one table needs to know to say which of its columns show. */
export interface ColumnSet<K extends string = string> {
  defs: readonly ColumnDef<K>[];
  /** The column that always shows. */
  nameKey: K;
}

/** The menu's entries, shaped as ContextMenu's items. */
export interface ColumnMenuEntry {
  label?: string;
  checked?: boolean;
  disabled?: boolean;
  separator?: boolean;
  /** Says it is a tick that turns one column on or off by itself (a checkbox to a screen reader), not one of several. */
  toggle?: boolean;
  /** Why it can't be chosen here ("Wider window"). */
  hint?: string;
  onselect?: () => void;
}

export function canChoose<K extends string>(set: ColumnSet<K>, key: string): boolean {
  return key !== set.nameKey && set.defs.some((def) => def.key === key);
}

/**
 * Whether a column shows: the page has something for it (<paramref name="available"/>), and it was chosen, or nobody chose
 * and it shows at first. <paramref name="firstOverride"/> is what the view shown says at first where it differs from the
 * column's own (the torrent views show Seeds, Peers, Up and Ratio).
 */
export function isShown<K extends string>(
  set: ColumnSet<K>,
  choices: ColumnChoices,
  key: K,
  available: (key: K) => boolean = () => true,
  firstOverride: Partial<Record<K, boolean>> = {},
): boolean {
  if (!available(key)) return false;
  if (key === set.nameKey) return true;
  const def = set.defs.find((d) => d.key === key);
  if (!def) return false;
  if (typeof choices[key] === 'boolean') return choices[key];
  return firstOverride[key] ?? def.shownAtFirst;
}

/** The keys that show, left to right. */
export function shownKeys<K extends string>(
  set: ColumnSet<K>,
  choices: ColumnChoices,
  available: (key: K) => boolean = () => true,
  firstOverride: Partial<Record<K, boolean>> = {},
): K[] {
  return set.defs.filter((def) => isShown(set, choices, def.key, available, firstOverride)).map((def) => def.key);
}

/** The choices after the menu showed or hid a column; the same object when the column can't be chosen. */
export function choose<K extends string>(set: ColumnSet<K>, choices: ColumnChoices, key: K, shown: boolean): ColumnChoices {
  if (!canChoose(set, key)) return choices;
  return { ...choices, [key]: shown };
}

/** Saved choices. Something else may have written there, so unknown columns, the name and non-booleans are left out. */
export function parseChoices<K extends string>(set: ColumnSet<K>, text: string | null | undefined): ColumnChoices {
  const result: ColumnChoices = {};
  try {
    const value = JSON.parse(text ?? 'null') as unknown;
    if (!value || typeof value !== 'object' || Array.isArray(value)) return result;
    for (const [key, shown] of Object.entries(value as Record<string, unknown>)) {
      if (typeof shown === 'boolean' && canChoose(set, key)) result[key] = shown;
    }
  } catch {
    // Nothing saved, or something broken: the columns as they start.
  }
  return result;
}

/**
 * The menu: every column the page has something for, ticked when it shows, the name greyed out with a tick, then Reset
 * columns. <paramref name="noRoom"/> says why a column can't show at this width (the download list's wide columns below
 * 1024 px and beside Torrent Info): it is greyed out with that word rather than taking a tick that changes nothing.
 */
export function columnMenu<K extends string>(
  set: ColumnSet<K>,
  shown: ReadonlySet<string>,
  available: (key: K) => boolean,
  onchoose: (key: K, shown: boolean) => void,
  onreset: () => void,
  noRoom: (key: K) => string | undefined = () => undefined,
): ColumnMenuEntry[] {
  const entries: ColumnMenuEntry[] = set.defs
    .filter((def) => available(def.key))
    .map((def) => {
      const hint = def.key === set.nameKey ? undefined : noRoom(def.key);
      return {
        label: def.spoken ?? def.title,
        checked: shown.has(def.key),
        toggle: true,
        disabled: def.key === set.nameKey || hint !== undefined,
        hint,
        onselect: () => onchoose(def.key, !shown.has(def.key)),
      };
    });
  return [...entries, { separator: true }, { label: 'Reset columns', onselect: onreset }];
}

/** A table's sort while its column shows; the table's own order (a null key) while the column is hidden. */
export function sortWhileShown<K extends string>(sort: { key: K | null; ascending: boolean }, shown: ReadonlySet<string>): { key: K | null; ascending: boolean } {
  return sort.key !== null && !shown.has(sort.key) ? { key: null, ascending: false } : sort;
}

// ---- The download list ----

export type ListColumnKey =
  | 'name' | 'queue' | 'size' | 'status' | 'extraction' | 'eta' | 'rate'
  | 'seeds' | 'peers' | 'up' | 'ratio' | 'uploaded' | 'seedTime' | 'seedGoal'
  | 'lastTry' | 'description';

/** Left to right, after the box that selects. The torrent columns start hidden (shown at first in the torrent views). */
export const listColumns: ColumnSet<ListColumnKey> = {
  nameKey: 'name',
  defs: [
    { key: 'name', title: 'File Name', shownAtFirst: true, track: 'minmax(180px, 1fr)', align: 'left' },
    { key: 'queue', title: 'Q', spoken: 'Queue', shownAtFirst: true, track: '34px', align: 'center' },
    { key: 'size', title: 'Size', shownAtFirst: true, track: '80px', align: 'right' },
    { key: 'status', title: 'Status', shownAtFirst: true, track: '150px', align: 'left' },
    { key: 'extraction', title: 'Extraction', shownAtFirst: true, track: '120px', align: 'left' },
    { key: 'eta', title: 'Time left', shownAtFirst: true, track: '80px', align: 'right' },
    { key: 'rate', title: 'Transfer rate', shownAtFirst: true, track: '90px', align: 'right' },
    { key: 'seeds', title: 'Seeds', shownAtFirst: false, wide: true, track: '48px', align: 'right' },
    { key: 'peers', title: 'Peers', shownAtFirst: false, wide: true, track: '48px', align: 'right' },
    { key: 'up', title: 'Up', spoken: 'Upload rate', shownAtFirst: false, wide: true, track: '72px', align: 'right' },
    { key: 'ratio', title: 'Ratio', shownAtFirst: false, wide: true, track: '110px', align: 'right' },
    { key: 'uploaded', title: 'Uploaded', shownAtFirst: false, wide: true, track: '80px', align: 'right' },
    { key: 'seedTime', title: 'Seeding time', shownAtFirst: false, wide: true, track: '90px', align: 'right' },
    { key: 'seedGoal', title: 'Seeding goal', shownAtFirst: false, wide: true, track: '120px', align: 'left' },
    { key: 'lastTry', title: 'Last Try Date', shownAtFirst: true, wide: true, track: '150px', align: 'left' },
    { key: 'description', title: 'Description', shownAtFirst: true, wide: true, track: 'minmax(120px, 1fr)', align: 'left' },
  ],
};

export const torrentListColumns: readonly ListColumnKey[] = ['seeds', 'peers', 'up', 'ratio', 'uploaded', 'seedTime', 'seedGoal'];

/**
 * WEB-T2: the torrent views show Seeds, Peers, Up and Ratio in place of Last Try Date until chosen otherwise. They keep
 * their own choices, apart from every other view's, as in the Windows app.
 */
export function listFirstShown(torrentView: boolean): Partial<Record<ListColumnKey, boolean>> {
  return torrentView ? { seeds: true, peers: true, up: true, ratio: true, lastTry: false } : {};
}

/**
 * Why a list column can't show at this width: the wide columns need 1024 px and no Torrent Info beside the list.
 * <paramref name="wideRoom"/> is whether they have that room now.
 */
export function listNoRoom(key: ListColumnKey, wideRoom: boolean): string | undefined {
  return !wideRoom && listColumns.defs.find((def) => def.key === key)?.wide ? 'Wider window' : undefined;
}

/**
 * The columns a phone (below 768 px) sorts by from More > Sort by. It has no header and so no column choices: the list
 * as it always was, the swarm columns in the torrent views only, whatever a wider window chose.
 */
export function phoneSortColumns(torrentView: boolean): Set<string> {
  const keys: ListColumnKey[] = ['name', 'queue', 'size', 'status', 'extraction', 'eta', 'rate', 'lastTry', 'description'];
  return new Set<string>(torrentView ? [...keys, 'seeds', 'peers', 'up', 'ratio'] : keys);
}

/** Where the list's columns are kept in this browser: one set for the torrent views, one for every other view. */
export function listChoicesKey(torrentView: boolean): string {
  return torrentView ? 'inbound.columns.torrents' : 'inbound.columns';
}

/**
 * The list's grid tracks for the columns that show, after the 28 px box. <paramref name="wide"/> is from 1024 px; below it
 * (and beside Torrent Info) the wide columns stay out, as they always did. With a torrent column in, the name and
 * Description give up some of their least room, as the torrent views always did.
 */
export function listTemplate(shown: readonly ListColumnKey[], wide: boolean): string {
  const keys = shown.filter((key) => wide || !listColumns.defs.find((def) => def.key === key)?.wide);
  const swarm = wide && keys.some((key) => torrentListColumns.includes(key));
  const tracks = keys.map((key) => {
    if (key === 'name') return !wide ? 'minmax(180px, 1fr)' : swarm ? 'minmax(150px, 2fr)' : 'minmax(200px, 2fr)';
    if (key === 'description') return swarm ? 'minmax(80px, 1fr)' : 'minmax(120px, 1fr)';
    return listColumns.defs.find((def) => def.key === key)?.track ?? 'auto';
  });
  return ['28px', ...tracks].join(' ');
}

// ---- Progress Details' tables ----

export type PeerColumnKey = 'peer' | 'client' | 'flags' | 'has' | 'down' | 'up' | 'received' | 'sent';
export type TrackerColumnKey = 'tracker' | 'status' | 'seeds' | 'peers' | 'next' | 'tier' | 'message';

export const peerColumns: ColumnSet<PeerColumnKey> = {
  nameKey: 'peer',
  defs: [
    { key: 'peer', title: 'Peer', shownAtFirst: true },
    { key: 'client', title: 'Client', shownAtFirst: true },
    { key: 'flags', title: 'Connection', shownAtFirst: true },
    { key: 'has', title: 'Has', shownAtFirst: true, align: 'right' },
    { key: 'down', title: 'Down', spoken: 'Download rate', shownAtFirst: true, align: 'right' },
    { key: 'up', title: 'Up', spoken: 'Upload rate', shownAtFirst: true, align: 'right' },
    // UI.R42: what came from this peer and went to it since it connected, hidden until chosen.
    { key: 'received', title: 'Downloaded', spoken: 'Downloaded from the peer', shownAtFirst: false, align: 'right' },
    { key: 'sent', title: 'Uploaded', spoken: 'Uploaded to the peer', shownAtFirst: false, align: 'right' },
  ],
};

export const trackerColumns: ColumnSet<TrackerColumnKey> = {
  nameKey: 'tracker',
  defs: [
    { key: 'tracker', title: 'Tracker', shownAtFirst: true },
    { key: 'status', title: 'Status', shownAtFirst: true },
    { key: 'seeds', title: 'Seeds', shownAtFirst: true, align: 'right' },
    { key: 'peers', title: 'Peers', shownAtFirst: true, align: 'right' },
    { key: 'next', title: 'Next announce', shownAtFirst: true, align: 'right' },
    // UI.R42: the tracker's tier and what it said last, hidden until chosen.
    { key: 'tier', title: 'Tier', shownAtFirst: false, align: 'right' },
    { key: 'message', title: 'Message', shownAtFirst: false },
  ],
};
