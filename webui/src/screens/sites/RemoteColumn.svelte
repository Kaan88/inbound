<script lang="ts">
  import { onMount, tick, untrack } from 'svelte';
  import ContextMenu, { type MenuItem } from '../../components/ContextMenu.svelte';
  import Icon from '../../components/Icon.svelte';
  import { copyText } from '../../lib/clipboard';
  import { bytes } from '../../lib/format';
  import { latest } from '../../lib/latest';
  import { LongPress } from '../../lib/longpress';
  import { crumbs, joinPath, parentPath, type Site, type SiteBrowseResult, type SiteItem } from '../../lib/sites';
  import { actionable, addressesOf, countText, pathsOf, rowKeys, sortRows } from '../../lib/sitesBrowse';
  import { isMenuKey, paneMenuItems, remotePaneMenu, type PaneCommand } from '../../lib/sitesPaneMenu';
  import { store } from '../../lib/store.svelte';
  import PaneSwitch from './PaneSwitch.svelte';

  // The left pane of "Two panes" when it shows another remote (or the same one in another folder): a plain list with a
  // folder to open and rows to tick. `picked` are the ticked items and `path` the folder shown; SiteBrowser copies them to
  // the right pane's remote with the arrow (sites.transfer.start, direction 'between').
  let { site, path = $bindable('/'), picked = $bindable([]), refresh = 0, source, onsource, oncopy, onpreview, copyProblem }:
    { site: Site; path?: string; picked?: SiteItem[]; refresh?: number; source: string; onsource: (value: string) => void;
      /** The menu's Copy to the right: these items of this pane go to the remote on the right. */
      oncopy: (list: SiteItem[]) => void; onpreview: (item: SiteItem, folder: string) => void; copyProblem: (list: SiteItem[]) => string | null } = $props();

  let items = $state<SiteItem[]>([]);
  let total = $state(0);
  let listing = $state<string | null>(null);
  let error = $state('');
  let loading = $state(false);
  let ticked = $state<Set<string>>(new Set());
  const requests = latest();

  const rows = $derived(sortRows(rowKeys(items).map((key, index) => ({ key, item: items[index] })), 'name', true));
  $effect(() => { picked = actionable(rows.filter((row) => ticked.has(row.key)).map((row) => row.item)); });

  async function open(next: string, keep = false, more = false) {
    const current = requests.begin();
    loading = true;
    try {
      const result = await store.ask<SiteBrowseResult>('sites.browse', {
        id: site.id, path: next, offset: more ? items.length : 0, limit: 500, refresh: keep, listing: more ? listing : undefined,
      });
      if (!current()) return;
      if (!result.ok) { error = result.message ?? 'The folder could not be read.'; return; }
      error = '';
      const same = result.path === path;
      path = result.path;
      items = more ? [...items, ...result.items] : result.items;
      total = result.total;
      listing = result.listing ?? null;
      if (!more && !same) ticked = new Set();
    } catch (failure) {
      if (current()) error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      if (current()) loading = false;
    }
  }

  onMount(() => void open(path === '/' ? (site.lastFolder || site.remoteFolder || '/') : path));

  // Another remote chosen: the folder starts afresh. A transfer finished: list the same folder again.
  let lastSite = untrack(() => site.id);
  $effect(() => {
    const id = site.id;
    untrack(() => {
      if (id === lastSite) return;
      lastSite = id;
      items = []; ticked = new Set(); error = '';
      void open(site.remoteFolder || '/');
    });
  });
  let seen = untrack(() => refresh);
  $effect(() => {
    const now = refresh;
    untrack(() => {
      if (now === seen) return;
      seen = now;
      void open(path, true);
    });
  });

  // ---- The row menu: right click, long press, the "..." button and Shift+F10 / the menu key ----

  type PaneRow = { key: string; item: SiteItem };
  let menu = $state<{ x: number; y: number; items: MenuItem[]; label: string; back: HTMLElement | null } | null>(null);
  let menuOpenedAt = 0;
  const longPress = new LongPress();

  function openMenu(row: PaneRow | null, x: number, y: number, back: HTMLElement | null) {
    menuOpenedAt = Date.now();
    if (row && !ticked.has(row.key)) ticked = new Set([row.key]);
    if (!row) ticked = new Set();
    const chosen = row ? actionable(rows.filter((one) => (one.key === row.key || ticked.has(one.key))).map((one) => one.item)) : [];
    const names = chosen.map((one) => one.name);
    const run = (command: PaneCommand) => {
      const say = (ok: boolean, done: string) => store.toast(ok ? done : 'The browser would not copy it.');
      switch (command) {
        case 'open': if (chosen.length === 1 && chosen[0].isFolder) void open(joinPath(path, chosen[0].name)); break;
        case 'copyRight': oncopy(chosen); break;
        case 'preview': if (chosen.length === 1) onpreview(chosen[0], path); break;
        case 'copyPath': void copyText(pathsOf(path, names)).then((ok) => say(ok, 'Path copied.')); break;
        case 'copyAddress': void copyText(addressesOf(site, path, names)).then((ok) => say(ok, 'Address copied.')); break;
        case 'refresh': void open(path, true); break;
      }
    };
    const items = paneMenuItems(remotePaneMenu(chosen, chosen.length ? copyProblem(chosen) : null), run);
    menu = { x, y, items, label: row ? `Actions for ${row.item.name}` : `Actions for ${path}`, back };
  }

  function closeMenu() {
    const back = menu?.back;
    menu = null;
    void tick().then(() => back?.focus());
  }

  function contextmenu(row: PaneRow | null, event: MouseEvent) {
    event.preventDefault();
    if (Date.now() - menuOpenedAt < 700) return; // the long press already opened it
    openMenu(row, event.clientX, event.clientY, null);
  }

  function pressed(row: PaneRow, event: PointerEvent) {
    longPress.down(event.pointerType, event.clientX, event.clientY, () => openMenu(row, event.clientX, event.clientY, null));
  }

  function rowKey(row: PaneRow, event: KeyboardEvent) {
    if (!isMenuKey(event)) return;
    event.preventDefault();
    const target = event.target as HTMLElement;
    const box = target.getBoundingClientRect();
    openMenu(row, box.left + 24, box.bottom, target);
  }

  function moreButton(row: PaneRow, event: MouseEvent) {
    event.stopPropagation();
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    openMenu(row, box.left, box.bottom + 2, event.currentTarget as HTMLElement);
  }

  function toggle(key: string) {
    if (longPress.takeClick()) return;
    const next = new Set(ticked);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    ticked = next;
  }
</script>

<section class="card @container flex min-w-0 flex-1 flex-col overflow-hidden" aria-label="Left pane: files on {site.name}">
  <header class="flex items-center gap-2 border-b border-divider px-3 py-2 text-[13px]">
    <Icon name="cloud" size={16} class="shrink-0 text-muted" />
    <PaneSwitch value={source} onchange={onsource} />
  </header>
  <nav class="flex flex-wrap items-center gap-1 border-b border-divider px-2 py-1.5 text-[13px]" aria-label="Folder on {site.name}">
    <button class="btn-icon" title="Up one folder" aria-label="Up one folder" disabled={path === '/' || loading} onclick={() => open(parentPath(path))}><Icon name="up" size={16} /></button>
    {#each crumbs(path) as crumb, index (crumb.path)}
      {#if index > 1}<span class="text-muted">/</span>{/if}
      <button class="link max-w-[10rem] truncate" onclick={() => open(crumb.path)}>{index === 0 ? site.name : crumb.name}</button>
    {/each}
    <button class="btn-icon ml-auto" title="List again" aria-label="List {site.name} again" disabled={loading} onclick={() => open(path, true)}><Icon name="refresh" size={16} /></button>
  </nav>
  {#if error}<p class="px-3 py-3 text-[13px] text-error" role="alert">{error}</p>{/if}
  <ul class="min-h-[8rem] flex-1 divide-y divide-divider text-[13px]" aria-label="Files in {path} on {site.name}" oncontextmenu={(event) => { if (event.target === event.currentTarget) contextmenu(null, event); }}>
    {#if path !== '/'}
      <li><button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover" onclick={() => open(parentPath(path))}><span class="w-4"></span><Icon name="folder" size={16} class="text-muted" />..</button></li>
    {/if}
    {#each rows as row (row.key)}
      <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
      <li class="item-row flex items-center gap-2 px-3 py-1.5 {ticked.has(row.key) ? 'bg-selected' : 'hover:bg-hover'}"
        oncontextmenu={(event) => contextmenu(row, event)} onkeydown={(event) => rowKey(row, event)}
        onpointerdown={(event) => pressed(row, event)} onpointermove={(event) => longPress.move(event.clientX, event.clientY)}
        onpointerup={() => longPress.up()} onpointercancel={() => longPress.cancelled()}>
        <input type="checkbox" aria-label="Select {row.item.name}" checked={ticked.has(row.key)} onchange={() => toggle(row.key)} />
        <button class="flex min-w-0 flex-1 items-center gap-2 text-left" onclick={() => toggle(row.key)} ondblclick={() => { if (row.item.isFolder) void open(joinPath(path, row.item.name)); }}>
          <Icon name={row.item.isFolder ? 'folder' : 'file'} size={16} class="shrink-0 text-muted" /><span class="min-w-0 flex-1 truncate">{row.item.name}</span>
          <span class="tabular hidden shrink-0 text-[12px] text-muted @xs:block">{row.item.isFolder || row.item.size < 0 ? '' : bytes(row.item.size)}</span>
        </button>
        {#if row.item.isFolder}<button class="btn-icon" title="Open {row.item.name}" aria-label="Open {row.item.name}" onclick={() => open(joinPath(path, row.item.name))}><Icon name="chevron-right" size={14} /></button>{/if}
        <button class="row-menu" aria-label="Actions for {row.item.name}" aria-haspopup="menu" title="More actions" onclick={(event) => moreButton(row, event)}><Icon name="more" size={16} /></button>
      </li>
    {/each}
    {#if loading && !items.length}<li class="px-3 py-3 text-muted">Reading the folder…</li>{:else if !items.length && !error}<li class="px-3 py-3 text-muted">This folder is empty.</li>{/if}
  </ul>
  {#if items.length < total}
    <div class="border-t border-divider px-3 py-2"><button class="btn" disabled={loading} onclick={() => open(path, false, true)}>Show more ({total - items.length} left)</button></div>
  {/if}
  <footer class="border-t border-divider px-3 py-1.5 text-[12px] text-muted">{picked.length ? `${picked.length} selected` : countText(rows.length, total)}</footer>
</section>

{#if menu}
  <ContextMenu items={menu.items} x={menu.x} y={menu.y} label={menu.label} onclose={closeMenu} />
{/if}

<style>
  .row-menu { display: inline-flex; border-radius: 4px; padding: 4px; color: var(--muted); opacity: 0; }
  .row-menu:hover { background: var(--hover); color: var(--text); }
  .item-row:hover .row-menu, .item-row:focus-within .row-menu, .row-menu:focus-visible { opacity: 1; }
  @media (pointer: coarse) { .row-menu { opacity: 1; padding: 8px; } }
</style>
