<script lang="ts">
  import { onMount } from 'svelte';
  import Icon from '../../components/Icon.svelte';
  import { latest } from '../../lib/latest';
  import { foldersFooter } from '../../lib/sitesBrowse';
  import { store } from '../../lib/store.svelte';
  import type { FolderBrowseResult } from '../../lib/types';

  // The left pane of "Two panes" (web-two): the server's own folders, under its allowed roots only. The server lists
  // folders here, not files (folders.browse), so this pane is where a download goes and where an upload comes from.
  // `path` is the folder the pane shows ('' for its list of roots), `selected` the folder chosen in it, `roots` the
  // folders the server may use. SiteBrowser turns them into the one download target (sitesBrowse.downloadTarget).
  let { path = $bindable(''), selected = $bindable(null), roots = $bindable([]), initial = '' }:
    { path?: string; selected?: string | null; roots?: string[]; initial?: string } = $props();

  let listing = $state<FolderBrowseResult | null>(null);
  let error = $state('');
  let loading = $state(false);
  const requests = latest();

  async function browse(next: string | null) {
    const current = requests.begin();
    loading = true;
    error = '';
    try {
      const result = await store.ask<FolderBrowseResult>('folders.browse', { path: next });
      if (!current()) return;
      listing = result;
      path = result.path ?? '';
      if (result.roots?.length) roots = result.roots;
      selected = null;
    } catch (failure) {
      if (current()) error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      if (current()) loading = false;
    }
  }

  onMount(() => void browse(initial || null));

  const lines = $derived(listing?.path ? listing.folders : (listing?.roots ?? []).map((root) => ({ name: root, path: root })));
  const crumbs = $derived((listing?.path ?? '').split(/[\\/]/).filter(Boolean));
  function crumbPath(index: number): string {
    const full = listing?.path ?? '';
    const parts = full.split(/(?<=[\\/])/);
    return parts.slice(0, index + 1).join('');
  }
</script>

<section class="card flex min-w-0 flex-1 flex-col overflow-hidden" aria-label="{store.texts.hostName}'s folders">
  <header class="flex items-center gap-2 border-b border-divider px-3 py-2 text-[13px]">
    <Icon name="server" size={16} class="text-muted" />
    <span class="font-semibold">{store.texts.hostName}</span>
    <span class="rounded-full border border-divider px-2 text-[11.5px] text-muted">Inbound's folders</span>
  </header>
  <nav class="flex flex-wrap items-center gap-1 border-b border-divider px-2 py-1.5 text-[13px]" aria-label="Folder on {store.texts.hostName}">
    <button class="btn-icon" title="Up one folder" aria-label="Up one folder" disabled={!listing?.path || loading} onclick={() => browse(listing?.parent ?? null)}><Icon name="up" size={16} /></button>
    {#each crumbs as crumb, index (index)}
      {#if index > 0}<span class="text-muted">/</span>{/if}
      <button class="link max-w-[10rem] truncate" onclick={() => browse(crumbPath(index))}>{crumb}</button>
    {/each}
  </nav>
  {#if error}<p class="px-3 py-3 text-[13px] text-error" role="alert">{error}</p>{/if}
  <ul class="min-h-[8rem] flex-1 divide-y divide-divider text-[13px]" aria-label="Folders">
    {#if listing?.parent}
      <li><button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover" onclick={() => browse(listing?.parent ?? null)}><Icon name="folder" size={16} class="text-muted" />..</button></li>
    {/if}
    {#each lines as line (line.path)}
      <li>
        <button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover {selected === line.path ? 'bg-selected' : ''}" aria-pressed={selected === line.path}
          onclick={() => (selected = selected === line.path ? null : line.path)} ondblclick={() => browse(line.path)}>
          <Icon name="folder" size={16} class="shrink-0 text-muted" /><span class="truncate">{line.name}</span>
        </button>
      </li>
    {/each}
  </ul>
  <footer class="border-t border-divider px-3 py-1.5 text-[12px] text-muted">{selected ? `${selected} selected` : foldersFooter(lines.length)}</footer>
</section>
