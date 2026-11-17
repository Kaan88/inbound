<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import Icon from '../../components/Icon.svelte';
  import { bytes, dateTime } from '../../lib/format';
  import { latest } from '../../lib/latest';
  import { foldersFooter } from '../../lib/sitesBrowse';
  import { store } from '../../lib/store.svelte';
  import type { FolderBrowseResult } from '../../lib/types';
  import PaneSwitch from './PaneSwitch.svelte';

  // The left pane of "Two panes" (web-two) when it shows the server: its own folders and files, under its allowed roots
  // only (folders.browse with files 'all'; before SITES.14 it listed folders only, so a finished download never showed).
  // `path` is the folder the pane shows ('' for its list of roots), `selected` the folder chosen in it, `file` the file
  // chosen in it (one or the other), `roots` the folders the server may use. SiteBrowser turns them into the one download
  // target (sitesBrowse.downloadTarget). `refresh` counts up when a transfer finished: the pane lists again.
  let { path = $bindable(''), selected = $bindable(null), file = $bindable(null), roots = $bindable([]), initial = '', refresh = 0, source, onsource }:
    { path?: string; selected?: string | null; file?: string | null; roots?: string[]; initial?: string; refresh?: number; source: string; onsource: (value: string) => void } = $props();

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

  const folders = $derived(listing?.path ? listing.folders : (listing?.roots ?? []).map((root) => ({ name: root, path: root })));
  const files = $derived(listing?.path ? (listing.files ?? []) : []);
  const crumbs = $derived((listing?.path ?? '').split(/[\\/]/).filter(Boolean));
  function crumbPath(index: number): string {
    const full = listing?.path ?? '';
    const parts = full.split(/(?<=[\\/])/);
    return parts.slice(0, index + 1).join('');
  }
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
      <button class="link max-w-[10rem] truncate" onclick={() => browse(crumbPath(index))}>{crumb}</button>
    {/each}
    <button class="btn-icon ml-auto" title="List again" aria-label="List {store.texts.hostName}'s folder again" disabled={loading} onclick={() => browse(listing?.path ?? null, true)}><Icon name="refresh" size={16} /></button>
  </nav>
  {#if error}<p class="px-3 py-3 text-[13px] text-error" role="alert">{error}</p>{/if}
  <ul class="min-h-[8rem] flex-1 divide-y divide-divider text-[13px]" aria-label="Folders and files">
    {#if listing?.parent}
      <li><button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover" onclick={() => browse(listing?.parent ?? null)}><Icon name="folder" size={16} class="text-muted" />..</button></li>
    {/if}
    {#each folders as line (line.path)}
      <li>
        <button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover {selected === line.path ? 'bg-selected' : ''}" aria-pressed={selected === line.path}
          onclick={() => { file = null; selected = selected === line.path ? null : line.path; }} ondblclick={() => browse(line.path)}>
          <Icon name="folder" size={16} class="shrink-0 text-muted" /><span class="truncate">{line.name}</span>
        </button>
      </li>
    {/each}
    {#each files as line (line.path)}
      <li>
        <button class="flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover {file === line.path ? 'bg-selected' : ''}" aria-pressed={file === line.path}
          onclick={() => { selected = null; file = file === line.path ? null : line.path; }}>
          <Icon name="file" size={16} class="shrink-0 text-muted" /><span class="min-w-0 flex-1 truncate">{line.name}</span>
          <span class="tabular shrink-0 text-[12px] text-muted">{line.size >= 0 ? bytes(line.size) : ''}</span>
          <span class="hidden shrink-0 text-[12px] text-muted @xl:block">{line.modified ? dateTime(line.modified) : ''}</span>
        </button>
      </li>
    {/each}
    {#if !loading && listing?.path && !folders.length && !files.length}<li class="px-3 py-3 text-muted">This folder is empty.</li>{/if}
  </ul>
  <footer class="border-t border-divider px-3 py-1.5 text-[12px] text-muted">{footer}</footer>
</section>
