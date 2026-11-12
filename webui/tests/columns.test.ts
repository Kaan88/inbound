// UI.R42 in the web UI: the column chooser of the download list and of the details' Peers and Trackers tables.
import listScreenSource from '../src/screens/ListScreen.svelte?raw';
import swarmSource from '../src/components/TorrentSwarmPanel.svelte?raw';
import { describe, expect, it } from 'vitest';
import {
  canChoose, choose, columnMenu, isShown, listChoicesKey, listColumns, listFirstShown, listNoRoom, listTemplate, parseChoices, peerColumns, phoneSortColumns, shownKeys,
  sortWhileShown, torrentListColumns, trackerColumns, type ListColumnKey,
} from '../src/lib/columns';
import { seedClockMs, sortForColumns, sortMenuTitles } from '../src/lib/torrentList';
import contextMenuSource from '../src/components/ContextMenu.svelte?raw';
import { sortedPeerRows, sortedTrackerRows, startsAscending } from '../src/lib/detailsSort';
import { peerRows, trackerMessageText, trackerRowsInOrder, type PeerDto, type TrackerDto } from '../src/lib/torrentDetails';

describe('the download list columns (UI.R42)', () => {
  it('start as before: the usual columns, the torrent ones hidden; the torrent views show Seeds, Peers, Up and Ratio in place of Last Try Date', () => {
    expect(shownKeys(listColumns, {})).toEqual(['name', 'queue', 'size', 'status', 'extraction', 'eta', 'rate', 'lastTry', 'description']);
    expect(shownKeys(listColumns, {}, (key) => key !== 'extraction', listFirstShown(true)))
      .toEqual(['name', 'queue', 'size', 'status', 'eta', 'rate', 'seeds', 'peers', 'up', 'ratio', 'description']);
    for (const key of torrentListColumns) expect(isShown(listColumns, {}, key)).toBe(false);
    expect(torrentListColumns).toEqual(['seeds', 'peers', 'up', 'ratio', 'uploaded', 'seedTime', 'seedGoal']);
  });

  it('never hide the File Name', () => {
    expect(canChoose(listColumns, 'name')).toBe(false);
    const before = {};
    expect(choose(listColumns, before, 'name', false)).toBe(before);
    expect(isShown(listColumns, { name: false }, 'name')).toBe(true);
    expect(parseChoices(listColumns, '{"name":false}')).toEqual({});
  });

  it('show and hide a column, and Reset columns brings back the start', () => {
    let choices = choose(listColumns, {}, 'size', false);
    choices = choose(listColumns, choices, 'uploaded', true);
    expect(shownKeys(listColumns, choices)).not.toContain('size');
    expect(shownKeys(listColumns, choices)).toContain('uploaded');
    // A choice wins over what a view shows at first.
    expect(isShown(listColumns, { seeds: false }, 'seeds', () => true, listFirstShown(true))).toBe(false);
    expect(isShown(listColumns, { lastTry: true }, 'lastTry', () => true, listFirstShown(true))).toBe(true);
    // Reset columns is an empty set of choices.
    expect(shownKeys(listColumns, {})).toEqual(shownKeys(listColumns, parseChoices(listColumns, '{}')));
  });

  it('keep what was chosen in this browser, one set for the torrent views and one for the rest; anything broken or unknown is left out', () => {
    expect(listChoicesKey(false)).toBe('inbound.columns');
    expect(listChoicesKey(true)).toBe('inbound.columns.torrents');
    expect(parseChoices(listColumns, JSON.stringify({ size: false, uploaded: true }))).toEqual({ size: false, uploaded: true });
    expect(parseChoices(listColumns, '{"size":"no","nope":true,"rate":false}')).toEqual({ rate: false });
    expect(parseChoices(listColumns, 'not json')).toEqual({});
    expect(parseChoices(listColumns, '[true]')).toEqual({});
    expect(parseChoices(listColumns, null)).toEqual({});
    expect(listScreenSource).toMatch(/save\(listChoicesKey\(torrentCols\), JSON\.stringify\(next\)\)/);
  });

  it("don't offer a column the page has nothing for (Extraction with no archive)", () => {
    expect(isShown(listColumns, { extraction: true }, 'extraction', (key) => key !== 'extraction')).toBe(false);
    const menu = columnMenu(listColumns, new Set(['name']), (key) => key !== 'extraction', () => {}, () => {});
    expect(menu.map((entry) => entry.label)).not.toContain('Extraction');
  });

  it('menu: every column ticked when it shows, File Name greyed out, then Reset columns', () => {
    const chosen: [ListColumnKey, boolean][] = [];
    let reset = 0;
    const shown = new Set(['name', 'size']);
    const menu = columnMenu(listColumns, shown, () => true, (key, on) => chosen.push([key, on]), () => reset++);
    expect(menu[0]).toMatchObject({ label: 'File Name', checked: true, disabled: true });
    expect(menu.find((entry) => entry.label === 'Queue')).toMatchObject({ checked: false, disabled: false });
    expect(menu.find((entry) => entry.label === 'Upload rate')).toBeDefined();
    expect(menu.at(-2)).toEqual({ separator: true });
    expect(menu.at(-1)?.label).toBe('Reset columns');
    menu.find((entry) => entry.label === 'Size')?.onselect?.();
    menu.find((entry) => entry.label === 'Seeding goal')?.onselect?.();
    menu.at(-1)?.onselect?.();
    expect(chosen).toEqual([['size', false], ['seedGoal', true]]);
    expect(reset).toBe(1);
  });

  it('menu entries are checkboxes to a screen reader, not radio items', () => {
    const menu = columnMenu(listColumns, new Set(['name']), () => true, () => {}, () => {});
    expect(menu.filter((entry) => entry.label && entry.label !== 'Reset columns').every((entry) => entry.toggle === true)).toBe(true);
    expect(menu.at(-1)?.toggle).toBeUndefined();
    expect(contextMenuSource).toMatch(/role=\{item\.checked === undefined \? 'menuitem' : item\.toggle \? 'menuitemcheckbox' : 'menuitemradio'\}/);
  });

  it('grey out the wide columns with "Wider window" where they have no room (below 1024 px, beside Torrent Info)', () => {
    const shown = new Set(['name', 'size', 'lastTry']);
    const narrow = columnMenu(listColumns, shown, () => true, () => {}, () => {}, (key) => listNoRoom(key, false));
    for (const label of ['Seeds', 'Upload rate', 'Uploaded', 'Seeding time', 'Seeding goal', 'Last Try Date', 'Description']) {
      expect(narrow.find((entry) => entry.label === label)).toMatchObject({ disabled: true, hint: 'Wider window' });
    }
    expect(narrow.find((entry) => entry.label === 'Last Try Date')?.checked).toBe(true);
    expect(narrow.find((entry) => entry.label === 'Size')).toMatchObject({ disabled: false, hint: undefined });
    expect(narrow[0]).toMatchObject({ label: 'File Name', disabled: true, hint: undefined });
    const wide = columnMenu(listColumns, shown, () => true, () => {}, () => {}, (key) => listNoRoom(key, true));
    expect(wide.filter((entry) => entry.hint)).toEqual([]);
    expect(listScreenSource).toMatch(/\(key\) => listNoRoom\(key, wideRoom\)/);
    expect(listScreenSource).toMatch(/const wideRoom = \$derived\(lgWide && !infoRoom\)/);
  });

  it("leave a phone's More > Sort by as it was, whatever a wider window chose", () => {
    // Below 768 px there is no header: the sort menu has the list's columns as always, the swarm ones in the torrent views.
    const plain = sortMenuTitles(phoneSortColumns(false)).map(([key]) => key);
    expect(plain).toEqual(['name', 'queue', 'size', 'status', 'extraction', 'eta', 'rate', 'lastTry', 'description', 'added']);
    const torrents = sortMenuTitles(phoneSortColumns(true)).map(([key]) => key);
    expect(torrents).toEqual(['name', 'queue', 'size', 'status', 'extraction', 'eta', 'rate', 'seeds', 'peers', 'up', 'ratio', 'lastTry', 'description', 'added']);
    // A saved Last Try Date sort holds in a torrent view on a phone, and a Size sort holds though a wide window hid Size.
    const byLastTry = { key: 'lastTry' as const, ascending: false };
    expect(sortForColumns(byLastTry, phoneSortColumns(true))).toEqual(byLastTry);
    const bySize = { key: 'size' as const, ascending: true };
    expect(sortForColumns(bySize, phoneSortColumns(false))).toEqual(bySize);
    expect(sortForColumns({ key: 'ratio', ascending: false }, phoneSortColumns(false)).key).toBe('added');
    expect(listScreenSource).toMatch(/const sortColumns = \$derived\(columnsWide \? shownColumns : phoneSortColumns\(torrentCols\)\)/);
  });

  it('lay out the tracks of the columns that show; the wide ones only from 1024 px', () => {
    const start = shownKeys(listColumns, {}, (key) => key !== 'extraction');
    expect(listTemplate(start, false)).toBe('28px minmax(180px, 1fr) 34px 80px 150px 80px 90px');
    expect(listTemplate(start, true)).toBe('28px minmax(200px, 2fr) 34px 80px 150px 80px 90px 150px minmax(120px, 1fr)');
    const torrents = shownKeys(listColumns, {}, (key) => key !== 'extraction', listFirstShown(true));
    expect(listTemplate(torrents, true)).toBe('28px minmax(150px, 2fr) 34px 80px 150px 80px 90px 48px 48px 72px 110px minmax(80px, 1fr)');
    expect(listTemplate(['name', 'size'], true)).toBe('28px minmax(200px, 2fr) 80px');
  });

  it('are wired to the header: a right-click or Shift+F10 opens the menu, More has Columns, and a phone keeps its lines', () => {
    expect(listScreenSource).toMatch(/oncontextmenu=\{headerMenu\}/);
    expect(listScreenSource).toMatch(/\{ label: 'Columns', hidden: !columnsWide \|\| onlyExtraction, items: columnItems\(\) \}/);
    expect(listScreenSource).toMatch(/@media \(min-width: 768px\) \{\s*\.list-grid > :global\(\.col-off\), \.list-row > \.col-off \{ display: none !important; \}/);
    expect(listScreenSource).toMatch(/grid-template-columns: var\(--cols-lg\)/);
    expect(listScreenSource).toMatch(/style:--cols-md=\{colsMd\}/);
    // The phone's two or three lines are untouched.
    expect(listScreenSource).toMatch(/grid-template-areas: 'check name size' 'check status rate' 'check desc desc';/);
  });
});

describe('the details tables (UI.R42)', () => {
  const peers: PeerDto[] = [
    { address: '203.0.113.5', port: 51413, client: 'qBittorrent', downloadRate: 10, uploadRate: 0, downloaded: 2_000_000, uploaded: 0, progress: 1 },
    { address: '198.51.100.7', port: 6881, client: 'Transmission', downloadRate: 5, uploadRate: 0, downloaded: 50_000, uploaded: 3_000_000, progress: 0.5 },
    { address: '192.0.2.9', port: 6881, client: '', downloadRate: 1, uploadRate: 0, progress: 0 },
  ];
  const trackers: TrackerDto[] = [
    { url: 'https://tracker.example/announce/abcdefabcdefabcdef', tier: 1, status: 'working', message: 'ok', seeds: 3, peers: 4 },
    { url: 'udp://open.example:1337/announce', tier: 0, status: 'error', message: 'refused by https://tracker.example/announce/abcdefabcdefabcdef?x=1', seeds: -1, peers: -1 },
  ];

  it('add Downloaded and Uploaded per peer, and Tier and Message per tracker, hidden until chosen', () => {
    expect(shownKeys(peerColumns, {})).toEqual(['peer', 'client', 'flags', 'has', 'down', 'up']);
    expect(shownKeys(trackerColumns, {})).toEqual(['tracker', 'status', 'seeds', 'peers', 'next']);
    expect(canChoose(peerColumns, 'peer')).toBe(false);
    expect(canChoose(trackerColumns, 'tracker')).toBe(false);
    expect(shownKeys(peerColumns, { received: true, sent: true })).toContain('sent');
  });

  it('fill them from what the server sends, and never show a passkey in a message', () => {
    const rows = peerRows(peers);
    expect(rows[0].received).toMatch(/MB$/);
    expect(rows[2].received).toMatch(/^0/);
    const trackerRows = trackerRowsInOrder(trackers);
    expect(trackerRows.map((row) => row.tier)).toEqual(['1', '0']);
    expect(trackerMessageText(trackers[1].message)).not.toContain('abcdefabcdefabcdef');
    expect(trackerMessageText(trackers[1].message)).toContain('tracker.example');
    expect(trackerMessageText(null)).toBe('');
  });

  it('sort by the new columns, and a hidden column sorts as the table does', () => {
    expect(sortedPeerRows(peers, { key: 'sent', ascending: false }, undefined)[0].client).toBe('Transmission');
    expect(sortedPeerRows(peers, { key: 'received', ascending: false }, undefined)[0].client).toBe('qBittorrent');
    expect(sortedTrackerRows(trackers, { key: 'tier', ascending: false })[0].tier).toBe('1');
    expect(sortedTrackerRows(trackers, { key: 'message', ascending: true })[0].message).toBe('ok');
    expect(startsAscending('tier')).toBe(true);
    expect(startsAscending('received')).toBe(false);
    expect(sortWhileShown({ key: 'sent', ascending: false }, new Set(['peer']))).toEqual({ key: null, ascending: false });
    expect(sortWhileShown({ key: 'down', ascending: false }, new Set(['down']))).toEqual({ key: 'down', ascending: false });
  });

  it('open the same menu from their headers and keep the choices in this browser', () => {
    expect(swarmSource).toMatch(/'inbound\.columns\.peers'/);
    expect(swarmSource).toMatch(/'inbound\.columns\.trackers'/);
    expect(swarmSource).toMatch(/oncontextmenu=/);
  });
});

describe('focus after the details tables column menu (UI.R42)', () => {
  it('goes back to the header button that opened it, or the first one when that column was hidden', () => {
    expect(swarmSource).toMatch(/onclose=\{closeColumns\}/);
    expect(swarmSource).toMatch(/opener\.button\?\.isConnected \? opener\.button : opener\.head\.isConnected \? opener\.head\.querySelector<HTMLElement>\('button'\) : null/);
    // A click elsewhere keeps the focus it gave.
    expect(swarmSource).toMatch(/if \(document\.activeElement && document\.activeElement !== document\.body\) return;/);
  });
});

describe('the Seeding time clock (UI.R42)', () => {
  const now = Date.parse('2026-11-12T12:00:00Z');
  const seeding = (minutesAgo: number) => ({ torrent: { seeding: true, seedingSince: new Date(now - minutesAgo * 60_000).toISOString() } }) as never;
  const plain = { torrent: undefined } as never;

  it('ticks every second while one has seeded under an hour, every half minute after, and not at all otherwise', () => {
    expect(seedClockMs(true, [seeding(10)], now)).toBe(1000);
    expect(seedClockMs(true, [seeding(90)], now)).toBe(30_000);
    expect(seedClockMs(true, [seeding(90), seeding(5)], now)).toBe(1000);
    expect(seedClockMs(false, [seeding(10)], now)).toBeNull();
    expect(seedClockMs(true, [plain], now)).toBeNull();
    expect(seedClockMs(true, [{ torrent: { seeding: false, seedingSince: new Date(now).toISOString() } } as never], now)).toBeNull();
  });

  it('is wired to the list only while the column shows, and stops while the tab is hidden', () => {
    expect(listScreenSource).toMatch(/seedClockMs\(wideRoom && shownColumns\.has\('seedTime'\), Object\.values\(store\.model\.jobs\)\)/);
    expect(listScreenSource).toMatch(/return everyWhileVisible\(every, \(\) => \(seedClock \+= 1\)\);/);
    expect(listScreenSource).toMatch(/const rows = \$derived\(\(void seedClock, buildRows\(/);
  });
});

// Review of 0.7.2, ux-2: the list's column menu gives focus back to the header it opened from, as Progress Details does.
describe('the download list column menu and focus', () => {
  it('remembers the header it opened from and focuses it again, not the rows', () => {
    expect(listScreenSource).toMatch(/function headerMenu\(event: MouseEvent\) \{[\s\S]*?columnsOpener = \{ head, button:/);
    expect(listScreenSource).toMatch(/async function closeMenu\(\) \{[\s\S]*?if \(!opener\) \{\s*grid\?\.focus\(\{ preventScroll: true \}\);\s*return;\s*\}[\s\S]*?await tick\(\);[\s\S]*?opener\.button\?\.isConnected \? opener\.button : opener\.head\.isConnected/);
    // A row or toolbar menu still gives focus to the list.
    expect(listScreenSource).toMatch(/function openMenu\(row: Row, x: number, y: number\) \{[\s\S]*?columnsOpener = null;/);
    expect(listScreenSource).toMatch(/function toolbarMenu\([\s\S]*?columnsOpener = null;/);
  });
});
