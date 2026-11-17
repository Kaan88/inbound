<script lang="ts">
  import { onMount, tick, untrack } from 'svelte';
  import ContextMenu, { type MenuItem } from '../../components/ContextMenu.svelte';
  import Icon from '../../components/Icon.svelte';
  import { copyText } from '../../lib/clipboard';
  import { bytes, dateTime } from '../../lib/format';
  import { latest } from '../../lib/latest';
  import { LongPress } from '../../lib/longpress';
  import { isMenuKey, paneMenuItems, serverPaneMenu, type PaneCommand } from '../../lib/sitesPaneMenu';
  import { folderCrumbs, foldersFooter } from '../../lib/sitesBrowse';
  import { store } from '../../lib/store.svelte';
  import type { FolderBrowseResult } from '../../lib/types';
  import PaneSwitch from './PaneSwitch.svelte';

  // The left pane of "Two panes" (web-two) when it shows the server: its own folders and files, under its allowed roots
  // only (folders.browse with files 'all'; before SITES.14 it listed folders only, so a finished download never showed).
  // `path` is the folder the pane shows ('' for its list of roots), `selected` the folder chosen in it, `file` the file
  // chosen in it (one or the other), `roots` the folders the server may use. SiteBrowser turns them into the one download
  // target (sitesBrowse.downloadTarget). `refresh` counts up when a transfer finished: the pane lists again.
  let { path = $bindable(''), selected = $bindable(null), file = $bindable(null), roots = $bindable([]), initial = '', refresh = 0, source, onsource, onupload, uploadProblem }:
    { path?: string; selected?: string | null; file?: string | null; roots?: string[]; initial?: string; refresh?: number; source: string; onsource: (value: string) => void;
      /** The menu's Upload: this folder or file of the server goes to the remote on the right. */
      onupload: (serverPath: string, isFolder: boolean) => void; uploadProblem: (serverPath: string, isFolder: boolean) => string | null } = $props();

  let listing = $state<FolderBrowseResult | null>(null);
  let error = $state('');
  let loading = $state(false);
  const requests = latest();

  async function browse(next: string | null, keep = false) {
    const current = requests.begin();
    loading = true;
    error = '';
    try {
      const result = await store.ask<FolderBrowseResult>('folders.browse', { path: next, files: 'all' });
      if (!current()) return;
      listing = result;
      if (!result.ok && result.error) error = result.error;
      path = result.path ?? '';
      if (result.roots?.length) roots = result.roots;
      if (!keep) { selected = null; file = null; }
    } catch (failure) {
      if (current()) error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      if (current()) loading = false;
    }
  }

  onMount(() => void browse(initial || null));

  // A transfer finished: list the same folder again, keeping the choice.
  let seen = untrack(() => refresh);
  $effect(() => {
    const now = refresh;
    untrack(() => {
      if (now === seen) return;
      seen = now;
      void browse(listing?.path ?? null, true);
    });
  });

  // ---- The row menu: right click, long press, the "..." button and Shift+F10 / the menu key ----

  interface PaneRow { name: string; path: string; isFolder: boolean }
  let menu = $state<{ x: number; y: number; items: MenuItem[]; label: string; back: HTMLElement | null } | null>(null);
  let menuOpenedAt = 0;
  const longPress = new LongPress();

  function openMenu(row: PaneRow | null, x: number, y: number, back: HTMLElement | null) {
    menuOpenedAt = Date.now();
    if (row) { if (row.isFolder) { file = null; selected = row.path; } else { selected = null; file = row.path; } }
    const target = row?.path ?? listing?.path ?? '';
    const run = (command: PaneCommand) => {
      switch (command) {
        case 'open': if (row) void browse(row.path); break;
        case 'upload': if (row) onupload(row.path, row.isFolder); break;
        case 'copyPath': void copyText(target).then((ok) => store.toast(ok ? 'Path copied.' : 'The browser would not copy it.')); break;
        case 'refresh': void browse(listing?.path ?? null, true); break;
      }
    };
    const items = paneMenuItems(serverPaneMenu(row, row ? uploadProblem(row.path, row.isFolder) : null), run);
    menu = { x, y, items, label: row ? `Actions for ${row.name}` : `Actions for ${listing?.path || 'the folders'}`, back };
  }

  function closeMenu() {
    const back = menu?.back;
    menu = null;
    void tick().then(() => back?.focus());
  }

  function contextmenu(row: PaneRow | null, event: MouseEvent) {
    event.preventDefault();
    if (Date.now() - menuOpenedAt < 700) return; // the long press already opened it
    openMenu(row, event.clientX, event.clientY, row ? (event.currentTarget as HTMLElement) : null);
  }

  function pressed(row: PaneRow, event: PointerEvent) {
    const back = event.currentTarget as HTMLElement;
    longPress.down(event.pointerType, event.clientX, event.clientY, () => openMenu(row, event.clientX, event.clientY, back));
  }

  function rowKey(row: PaneRow, event: KeyboardEvent) {
    if (!isMenuKey(event)) return;
    event.preventDefault();
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    openMenu(row, box.left + 24, box.bottom, event.currentTarget as HTMLElement);
  }

  function moreButton(row: PaneRow, event: MouseEvent) {
    event.stopPropagation();
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    openMenu(row, box.left, box.bottom + 2, event.currentTarget as HTMLElement);
  }

  const folders = $derived(listing?.path ? listing.folders : (listing?.roots ?? []).map((root) => ({ name: root, path: root })));
  const files = $derived(listing?.path ? (listing.files ?? []) : []);
  const crumbs = $derived(folderCrumbs(listing?.path ?? ''));
  const footer = $derived(file ? `${file} selected` : selected ? `${selected} selected` : `${foldersFooter(folders.length)}${files.length ? ` · ${files.length === 1 ? '1 file' : `${files.length.toLocaleString('en-US')} files`}` : ''}${listing?.truncated ? ' (not all shown)' : ''}`);
</script>

<section class="card @container flex min-w-0 flex-1 flex-col overflow-hidden" aria-label="{store.texts.hostName}'s folders">
  <header class="flex items-center gap-2 border-b border-divider px-3 py-2 text-[13px]">
    <Icon name="server" size={16} class="shrink-0 text-muted" />
    <PaneSwitch value={source} onchange={onsource} />
    <span class="rounded-full border border-divider px-2 text-[11.5px] text-muted max-[1100px]:hidden">Inbound's folders</span>
  </header>
  <nav class="flex flex-wrap items-center gap-1 border-b border-divider px-2 py-1.5 text-[13px]" aria-label="Folder on {store.texts.hostName}">
    <button class="btn-icon" title="Up one folder" aria-label="Up one folder" disabled={!listing?.path || loading} onclick={() => browse(listing?.parent ?? null)}><Icon name="up" size={16} /></button>
    {#each crumbs as crumb, index (index)}
      {#if index > 0}<span class="text-muted">/</span>{/if}
      <button class="link max-w-[10rem] truncate" onclick={() => browse(crumb.path)}>{crumb.name}</button>
    {/each}
    <button class="btn-icon ml-auto" title="List again" aria-label="List {store.texts.hostName}'s folder again" disabled={loading} onclick={() => browse(listing?.path ?? null, true)}><Icon name="refresh" size={16} /></button>
  </nav>
  {#if error}<p class="px-3 py-3 text-[13px] text-error" role="alert">{error}</p>{/if}
  <ul class="min-h-[8rem] flex-1 divide-y divide-divider text-[13px]" aria-label="Folders and files" oncontextmenu={(event) => { if (event.target === event.currentTarget) contextmenu(null, event); }}>
    {#if listing?.parent}
      <li><button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover" onclick={() => browse(listing?.parent ?? null)}><Icon name="folder" size={16} class="text-muted" />..</button></li>
    {/if}
    {#each folders as line (line.path)}
      {@const row = { name: line.name, path: line.path, isFolder: true }}
      <li class="item-row flex items-center hover:bg-hover {selected === line.path ? 'bg-selected' : ''}">
        <button class="flex min-w-0 flex-1 items-center gap-2 px-3 py-1.5 text-left" aria-pressed={selected === line.path} aria-haspopup="menu"
          onclick={() => { if (longPress.takeClick()) return; file = null; selected = selected === line.path ? null : line.path; }} ondblclick={() => browse(line.path)}
          oncontextmenu={(event) => contextmenu(row, event)} onkeydown={(event) => rowKey(row, event)}
          onpointerdown={(event) => pressed(row, event)} onpointermove={(event) => longPress.move(event.clientX, event.clientY)}
          onpointerup={() => longPress.up()} onpointercancel={() => longPress.cancelled()}>
          <Icon name="folder" size={16} class="shrink-0 text-muted" /><span class="truncate">{line.name}</span>
        </button>
        <button class="row-menu mr-2" aria-label="Actions for {line.name}" aria-haspopup="menu" title="More actions" onclick={(event) => moreButton(row, event)}><Icon name="more" size={16} /></button>
      </li>
    {/each}
    {#each files as line (line.path)}
      {@const row = { name: line.name, path: line.path, isFolder: false }}
      <li class="item-row flex items-center hover:bg-hover {file === line.path ? 'bg-selected' : ''}">
        <button class="flex min-w-0 flex-1 items-center gap-2 px-3 py-1.5 text-left" aria-pressed={file === line.path} aria-haspopup="menu"
          onclick={() => { if (longPress.takeClick()) return; selected = null; file = file === line.path ? null : line.path; }}
          oncontextmenu={(event) => contextmenu(row, event)} onkeydown={(event) => rowKey(row, event)}
          onpointerdown={(event) => pressed(row, event)} onpointermove={(event) => longPress.move(event.clientX, event.clientY)}
          onpointerup={() => longPress.up()} onpointercancel={() => longPress.cancelled()}>
          <Icon name="file" size={16} class="shrink-0 text-muted" /><span class="min-w-0 flex-1 truncate">{line.name}</span>
          <span class="tabular shrink-0 text-[12px] text-muted">{line.size >= 0 ? bytes(line.size) : ''}</span>
          <span class="hidden shrink-0 text-[12px] text-muted @xl:block">{line.modified ? dateTime(line.modified) : ''}</span>
        </button>
        <button class="row-menu mr-2" aria-label="Actions for {line.name}" aria-haspopup="menu" title="More actions" onclick={(event) => moreButton(row, event)}><Icon name="more" size={16} /></button>
      </li>
    {/each}
    {#if !loading && listing?.path && !folders.length && !files.length}<li class="px-3 py-3 text-muted">This folder is empty.</li>{/if}
  </ul>
  <footer class="border-t border-divider px-3 py-1.5 text-[12px] text-muted">{footer}</footer>
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
