<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import Icon from '../../components/Icon.svelte';
  import { bytes } from '../../lib/format';
  import { latest } from '../../lib/latest';
  import { crumbs, joinPath, parentPath, type Site, type SiteBrowseResult, type SiteItem } from '../../lib/sites';
  import { actionable, countText, rowKeys, sortRows } from '../../lib/sitesBrowse';
  import { store } from '../../lib/store.svelte';
  import PaneSwitch from './PaneSwitch.svelte';

  // The left pane of "Two panes" when it shows another remote (or the same one in another folder): a plain list with a
  // folder to open and rows to tick. `picked` are the ticked items and `path` the folder shown; SiteBrowser copies them to
  // the right pane's remote with the arrow (sites.transfer.start, direction 'between').
  let { site, path = $bindable('/'), picked = $bindable([]), refresh = 0, source, onsource }:
    { site: Site; path?: string; picked?: SiteItem[]; refresh?: number; source: string; onsource: (value: string) => void } = $props();

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

  function toggle(key: string) {
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
  <ul class="min-h-[8rem] flex-1 divide-y divide-divider text-[13px]" aria-label="Files in {path} on {site.name}">
    {#if path !== '/'}
      <li><button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover" onclick={() => open(parentPath(path))}><span class="w-4"></span><Icon name="folder" size={16} class="text-muted" />..</button></li>
    {/if}
    {#each rows as row (row.key)}
      <li class="flex items-center gap-2 px-3 py-1.5 {ticked.has(row.key) ? 'bg-selected' : 'hover:bg-hover'}">
        <input type="checkbox" aria-label="Select {row.item.name}" checked={ticked.has(row.key)} onchange={() => toggle(row.key)} />
        <button class="flex min-w-0 flex-1 items-center gap-2 text-left" onclick={() => toggle(row.key)} ondblclick={() => { if (row.item.isFolder) void open(joinPath(path, row.item.name)); }}>
          <Icon name={row.item.isFolder ? 'folder' : 'file'} size={16} class="shrink-0 text-muted" /><span class="min-w-0 flex-1 truncate">{row.item.name}</span>
          <span class="tabular hidden shrink-0 text-[12px] text-muted @xs:block">{row.item.isFolder || row.item.size < 0 ? '' : bytes(row.item.size)}</span>
        </button>
        {#if row.item.isFolder}<button class="btn-icon" title="Open {row.item.name}" aria-label="Open {row.item.name}" onclick={() => open(joinPath(path, row.item.name))}><Icon name="chevron-right" size={14} /></button>{/if}
      </li>
    {/each}
    {#if loading && !items.length}<li class="px-3 py-3 text-muted">Reading the folder…</li>{:else if !items.length && !error}<li class="px-3 py-3 text-muted">This folder is empty.</li>{/if}
  </ul>
  {#if items.length < total}
    <div class="border-t border-divider px-3 py-2"><button class="btn" disabled={loading} onclick={() => open(path, false, true)}>Show more ({total - items.length} left)</button></div>
  {/if}
  <footer class="border-t border-divider px-3 py-1.5 text-[12px] text-muted">{picked.length ? `${picked.length} selected` : countText(rows.length, total)}</footer>
</section>
