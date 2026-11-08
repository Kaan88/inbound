<script lang="ts">
  import { onMount } from 'svelte';
  import { bytes } from '../lib/format';
  import { latest } from '../lib/latest';
  import { store } from '../lib/store.svelte';
  import type { FolderBrowseResult, FolderSpace } from '../lib/types';
  import Dialog from './Dialog.svelte';
  import Icon from './Icon.svelte';

  // The server's folders, under its roots only (folders.browse / folders.create): favorites with their free space,
  // a typed path checked by the server, and a new folder made where you are.
  let { initial = '', onpick, onclose }: { initial?: string; onpick: (path: string) => void; onclose: () => void } = $props();

  let listing = $state<FolderBrowseResult | null>(null);
  let typed = $state('');
  let error = $state('');
  let busy = $state(false);
  let newName = $state('');
  let creating = $state(false);
  let spaces = $state<Record<string, FolderSpace>>({});
  // Only the newest browse or create shows its reply (lib/latest.ts).
  const requests = latest();

  const favorites = $derived.by(() => {
    const settings = store.model.settings;
    const list: { name: string; path: string }[] = [];
    if (settings?.defaultDownloadDirectory) list.push({ name: 'Download folder', path: settings.defaultDownloadDirectory });
    for (const favorite of settings?.favoriteFolders ?? []) {
      if (!list.some((item) => item.path === favorite.path)) list.push(favorite);
    }
    return list;
  });

  // Recently used destinations that are not favorites, as the desktop's folder menu lists them.
  const recent = $derived.by(() => {
    const saved = favorites.map((f) => f.path.toLowerCase());
    const list = (store.model.settings?.recentDestinationDirectories ?? []).filter((path) => !saved.includes(path.toLowerCase()));
    return list.slice(0, 10);
  });

  const isFavorite = $derived(!!listing?.path && (store.model.settings?.favoriteFolders ?? []).some((f) => f.path === listing!.path));

  async function browse(path: string | null | undefined) {
    const current = requests.begin();
    busy = true;
    error = '';
    try {
      const result = await store.ask<FolderBrowseResult>('folders.browse', { path: path || null });
      if (!current()) return;
      listing = result;
      typed = result.path ?? '';
    } catch (e) {
      if (current()) error = e instanceof Error ? e.message : String(e);
    } finally {
      if (current()) busy = false;
    }
  }

  async function create() {
    if (!listing?.path || !newName.trim()) return;
    const current = requests.begin();
    busy = true;
    error = '';
    try {
      const result = await store.ask<FolderBrowseResult>('folders.create', { parent: listing.path, name: newName.trim() });
      if (!current()) return;
      listing = result;
      typed = result.path ?? '';
      newName = '';
      creating = false;
    } catch (e) {
      if (current()) error = e instanceof Error ? e.message : String(e);
    } finally {
      if (current()) busy = false;
    }
  }

  async function toggleFavorite() {
    if (!listing?.path) return;
    const path = listing.path;
    await store.saveSettings((settings) => {
      const present = settings.favoriteFolders.some((f) => f.path === path);
      settings.favoriteFolders = present
        ? settings.favoriteFolders.filter((f) => f.path !== path)
        : [...settings.favoriteFolders, { name: path.split(/[\\/]/).filter(Boolean).pop() ?? path, path }];
    }, isFavorite ? 'Removed from favorites.' : 'Added to favorites.');
  }

  onMount(() => {
    void browse(initial || null);
    const paths = favorites.map((f) => f.path);
    if (paths.length) {
      store.ask<{ folders: FolderSpace[] }>('folders.space', { paths }).then((result) => {
        const map: Record<string, FolderSpace> = {};
        for (const space of result.folders ?? []) map[space.path] = space;
        spaces = map;
      }).catch(() => undefined);
    }
  });
</script>

<Dialog title="Choose a folder on the server" {onclose} wide>
  <form class="mb-3 flex gap-2" onsubmit={(event) => { event.preventDefault(); void browse(typed); }}>
    <input class="field font-mono" bind:value={typed} placeholder="Type a path, such as /downloads/movies" aria-label="Folder path" spellcheck="false" autocomplete="off" />
    <button class="btn shrink-0" type="submit" disabled={busy}>Check</button>
  </form>
  {#if error}
    <p class="mb-3 rounded border border-icon-danger/50 bg-highlight px-3 py-2 text-[13px] text-error" role="alert">{error}</p>
  {/if}

  <div class="grid grid-cols-1 gap-3 sm:grid-cols-[200px_minmax(0,1fr)]">
    <aside class="min-w-0">
      <h3 class="label">Favorites</h3>
      <ul class="space-y-0.5">
        {#each favorites as favorite (favorite.path)}
          <li>
            <button class="w-full rounded px-2 py-1.5 text-left hover:bg-hover" onclick={() => browse(favorite.path)}>
              <span class="flex items-center gap-1.5 text-[13px]"><Icon name="folder" size={15} class="text-icon-info" /> <span class="truncate">{favorite.name}</span></span>
              <span class="block truncate text-[11px] text-muted">
                {#if spaces[favorite.path]?.freeBytes !== undefined}{bytes(spaces[favorite.path].freeBytes)} free{/if}
              </span>
            </button>
          </li>
        {/each}
      </ul>
      {#if recent.length}
        <h3 class="label mt-3">Recent</h3>
        <ul class="space-y-0.5">
          {#each recent as path (path)}
            <li><button class="w-full truncate rounded px-2 py-1.5 text-left font-mono text-[12px] hover:bg-hover" title={path} onclick={() => browse(path)}>{path}</button></li>
          {/each}
        </ul>
      {/if}
      {#if listing?.roots?.length}
        <h3 class="label mt-3">Allowed folders</h3>
        <ul class="space-y-0.5">
          {#each listing.roots as root (root)}
            <li><button class="w-full truncate rounded px-2 py-1.5 text-left font-mono text-[12px] hover:bg-hover" onclick={() => browse(root)}>{root}</button></li>
          {/each}
        </ul>
      {/if}
    </aside>

    <section class="min-w-0">
      <div class="mb-2 flex items-center gap-2">
        <button class="btn px-2" onclick={() => browse(listing?.parent ?? null)} disabled={busy || !listing?.path} title="Up one folder" aria-label="Up one folder"><Icon name="up" size={15} /></button>
        <div class="min-w-0 flex-1 truncate font-mono text-[12px]" title={listing?.path ?? ''}>{listing?.path ?? 'The folders this server may use'}</div>
        {#if listing?.path}
          <button class="btn px-2" onclick={toggleFavorite} title={isFavorite ? 'Remove from favorites' : 'Add to favorites'} aria-pressed={isFavorite}>
            {isFavorite ? '★' : '☆'}
          </button>
        {/if}
      </div>
      {#if listing?.freeBytes !== undefined && listing?.path}
        <p class="hint mb-2">{bytes(listing.freeBytes)} free of {bytes(listing.totalBytes)} on this volume</p>
      {/if}
      <ul class="card max-h-[45vh] min-h-32 divide-y divide-divider overflow-y-auto">
        {#each listing?.folders ?? [] as folder (folder.path)}
          <li>
            <button class="flex w-full items-center gap-2 px-3 py-2 text-left text-[13px] hover:bg-hover" ondblclick={() => onpick(folder.path)} onclick={() => browse(folder.path)}>
              <Icon name="folder" size={16} class="shrink-0 text-icon-info" /><span class="truncate">{folder.name}</span>
            </button>
          </li>
        {:else}
          <li class="px-3 py-6 text-center text-[13px] text-muted">{busy ? 'Loading…' : listing?.path ? 'No folders inside this one.' : 'No folders.'}</li>
        {/each}
      </ul>
      {#if listing?.truncated}<p class="hint mt-1">Only the first 2,000 folders by name are listed. Type a path to open another.</p>{/if}

      {#if listing?.path}
        {#if creating}
          <form class="mt-2 flex gap-2" onsubmit={(event) => { event.preventDefault(); void create(); }}>
            <input class="field" bind:value={newName} placeholder="New folder name" aria-label="New folder name" />
            <button class="btn-primary shrink-0" type="submit" disabled={busy || !newName.trim()}>Create</button>
            <button class="btn shrink-0" type="button" onclick={() => (creating = false)}>Cancel</button>
          </form>
        {:else}
          <button class="btn mt-2" onclick={() => (creating = true)}><Icon name="folder-plus" size={15} class="text-icon-info" /> New folder</button>
        {/if}
      {/if}
    </section>
  </div>

  {#snippet footer()}
    <button class="btn-primary" disabled={!listing?.path} onclick={() => listing?.path && onpick(listing.path)}>Use this folder</button>
    <button class="btn" onclick={onclose}>Cancel</button>
  {/snippet}
</Dialog>
