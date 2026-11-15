<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import AskDialog from '../../components/AskDialog.svelte';
  import Dialog from '../../components/Dialog.svelte';
  import FolderField from '../../components/FolderField.svelte';
  import Icon from '../../components/Icon.svelte';
  import { bytes, dateTime } from '../../lib/format';
  import { store } from '../../lib/store.svelte';
  import {
    crumbs, deviceDownloadUrl, joinPath, nameProblem, parentPath, problems, uploadToSite,
    type Site, type SiteBrowseResult, type SiteItem, type SiteResult,
  } from '../../lib/sites';
  import SiteFix from './SiteFix.svelte';

  // One site's files (SITES.7): browse, move files to and from this device or the computer Inbound runs on, and change
  // the site (new folder, rename, delete). The desktop's Sites view, for a browser and a phone.
  let { site }: { site: Site } = $props();

  let path = $state(untrack(() => site.remoteFolder || '/'));
  let items = $state<SiteItem[]>([]);
  let total = $state(0);
  let listing = $state<string | null>(null);
  let loading = $state(false);
  let error = $state('');
  let problem = $state<string | null>(null);
  let fixing = $state(false);
  let selected = $state<Set<string>>(new Set());

  // One dialog at a time; files whose names are taken wait in turn to be asked about.
  let nameBox = $state<{ title: string; value: string; action: 'mkdir' | 'rename'; from?: string } | null>(null);
  let deleting = $state<SiteItem[] | null>(null);
  let toHostBox = $state<{ names: SiteItem[]; folder: string; conflict: string } | null>(null);
  let replacing = $state<{ file: File; folder: string }[]>([]);
  let busy = $state(false);
  let dialogError = $state('');

  interface Upload { id: number; name: string; size: number; sent: number; state: 'sending' | 'done' | 'failed'; error?: string; abort?: () => void }
  let uploads = $state<Upload[]>([]);
  let uploadId = 0;
  let picker: HTMLInputElement | undefined = $state();

  const canMakeFolder = $derived(site.capabilities.includes('newFolder'));
  const fixable = (value: string | null | undefined) =>
    value === problems.hostKeyNotAccepted || value === problems.hostKeyChanged || value === problems.certificateNotTrusted || value === problems.signInNeeded;
  const chosen = $derived(items.filter((item) => selected.has(item.name)));

  async function open(next: string, refresh = false, more = false) {
    loading = true;
    error = '';
    problem = null;
    try {
      const result = await store.ask<SiteBrowseResult>('sites.browse', {
        id: site.id, path: next, offset: more ? items.length : 0, limit: 500, refresh, listing: more ? listing : undefined,
      });
      if (!result.ok) {
        error = result.message ?? 'The folder could not be read.';
        problem = result.problem ?? null;
        if (problem === problems.listingChanged) return open(next, true);
        return;
      }
      path = result.path;
      items = more ? [...items, ...result.items] : result.items;
      total = result.total;
      listing = result.listing ?? null;
      if (!more) selected = new Set();
    } catch (failure) {
      error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      loading = false;
    }
  }

  onMount(() => void open(path));

  function toggle(name: string) {
    const next = new Set(selected);
    if (next.has(name)) next.delete(name);
    else next.add(name);
    selected = next;
  }

  function toggleAll() {
    selected = selected.size === items.length ? new Set() : new Set(items.map((item) => item.name));
  }

  function names(list: SiteItem[]) {
    return list.map((item) => ({ name: item.name, isFolder: item.isFolder }));
  }

  function closeDialogs() {
    nameBox = null;
    deleting = null;
    toHostBox = null;
  }

  async function run(action: () => Promise<boolean>) {
    busy = true;
    dialogError = '';
    try {
      if (await action()) closeDialogs();
    } catch (failure) {
      dialogError = failure instanceof Error ? failure.message : String(failure);
    } finally {
      busy = false;
    }
  }

  function nameAction(current: NonNullable<typeof nameBox>) {
    const wrong = nameProblem(current.value);
    if (wrong) {
      dialogError = wrong;
      return;
    }
    void run(async () => {
      const result = current.action === 'mkdir'
        ? await store.ask<SiteResult>('sites.mkdir', { id: site.id, path, name: current.value.trim() })
        : await store.ask<SiteResult>('sites.rename', { id: site.id, path, name: current.from, newName: current.value.trim() });
      if (!result.ok) {
        dialogError = result.message ?? 'That did not work.';
        return false;
      }
      await open(path, true);
      return true;
    });
  }

  function remove(list: SiteItem[]) {
    void run(async () => {
      const result = await store.ask<{ ok: boolean; message?: string; items: { name: string; ok: boolean; message?: string }[] }>(
        'sites.delete', { id: site.id, path, items: names(list) });
      await open(path, true);
      const failed = (result.items ?? []).filter((item) => !item.ok);
      if (!result.ok || failed.length) {
        dialogError = result.message ?? failed.map((item) => `${item.name}: ${item.message ?? 'not deleted'}`).join('\n');
        return false;
      }
      store.toast(list.length === 1 ? `${list[0].name} was deleted.` : `${list.length} items were deleted.`);
      return true;
    });
  }

  function toHost(current: NonNullable<typeof toHostBox>) {
    void run(async () => {
      const result = await store.ask<{ ok: boolean; message?: string }>('sites.transfer.start', {
        siteId: site.id, direction: 'download', remoteFolder: path, localFolder: current.folder.trim(), names: names(current.names), conflict: current.conflict,
      });
      if (!result.ok) {
        dialogError = result.message ?? 'The download did not start.';
        return false;
      }
      store.toast(`Downloading to ${store.texts.hostName}. Its progress is under Transfers.`);
      selected = new Set();
      return true;
    });
  }

  function send(file: File, replace = false, folder = path) {
    const upload: Upload = { id: ++uploadId, name: file.name, size: file.size, sent: 0, state: 'sending' };
    uploads = [...uploads, upload];
    const started = uploadToSite(site.id, folder, file, replace, (sent) => update(upload.id, { sent }));
    update(upload.id, { abort: started.abort });
    void started.done.then(async (outcome) => {
      if (outcome.ok) {
        update(upload.id, { state: 'done', sent: file.size, abort: undefined });
        if (folder === path) await open(path, true);
        return;
      }
      if (outcome.status === 409 && outcome.problem === problems.exists) {
        uploads = uploads.filter((item) => item.id !== upload.id);
        replacing = [...replacing, { file, folder }];
        return;
      }
      update(upload.id, { state: 'failed', error: outcome.error, abort: undefined });
    });
  }

  function update(id: number, change: Partial<Upload>) {
    uploads = uploads.map((item) => (item.id === id ? { ...item, ...change } : item));
  }

  function picked(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    for (const file of Array.from(input.files ?? [])) send(file);
    input.value = '';
  }

  function defaultFolder() {
    return site.localFolder || store.model.settings?.defaultDownloadDirectory || '';
  }
</script>

<div class="space-y-3">
  <nav class="flex flex-wrap items-center gap-1 text-[13px]" aria-label="Folder">
    <button class="btn-icon" title="Up one folder" aria-label="Up one folder" disabled={path === '/' || loading} onclick={() => open(parentPath(path))}><Icon name="up" size={16} /></button>
    {#each crumbs(path) as crumb, index (crumb.path)}
      {#if index > 1}<span class="text-muted">/</span>{/if}
      <button class="link max-w-[12rem] truncate" onclick={() => open(crumb.path)}>{crumb.name}</button>
    {/each}
  </nav>

  <div class="flex flex-wrap gap-2">
    <button class="btn" disabled={loading} onclick={() => open(path, true)}><Icon name="refresh" size={15} /> Refresh</button>
    <button class="btn" onclick={() => picker?.click()}><Icon name="upload" size={15} /> Upload from this device</button>
    <input bind:this={picker} type="file" multiple class="hidden" onchange={picked} />
    {#if canMakeFolder}<button class="btn" onclick={() => { dialogError = ''; nameBox = { title: 'New folder', value: '', action: 'mkdir' }; }}><Icon name="folder-plus" size={15} /> New folder</button>{/if}
    {#if chosen.length}
      <button class="btn" onclick={() => { dialogError = ''; toHostBox = { names: chosen, folder: defaultFolder(), conflict: 'skip' }; }}><Icon name="download" size={15} /> Download to {store.texts.hostName}</button>
      <button class="btn-danger" onclick={() => { dialogError = ''; deleting = chosen; }}><Icon name="trash" size={15} /> Delete ({chosen.length})</button>
    {/if}
  </div>

  {#if error}
    <div class="notice notice-error" role="alert">
      <Icon name="alert" size={16} /><span class="notice-text">{error}</span>
      {#if fixable(problem)}<button class="btn" onclick={() => (fixing = true)}>{problem === problems.signInNeeded ? 'Sign in…' : 'Check…'}</button>{/if}
    </div>
  {/if}

  {#if uploads.length}
    <ul class="card divide-y divide-divider text-[13px]" aria-label="Uploads from this device">
      {#each uploads as upload (upload.id)}
        <li class="flex items-center gap-2 px-3 py-2">
          <span class="min-w-0 flex-1">
            <span class="block truncate">{upload.name}</span>
            {#if upload.state === 'sending'}
              <span class="progress mt-1"><span style="width: {upload.size ? Math.round((upload.sent / upload.size) * 100) : 0}%"></span></span>
            {:else if upload.state === 'failed'}<span class="tone-failed text-[12px]">{upload.error}</span>
            {:else}<span class="tone-done text-[12px]">Uploaded</span>{/if}
          </span>
          {#if upload.state === 'sending'}
            <span class="tabular text-[12px] text-muted">{bytes(upload.sent) || '0 B'} of {bytes(upload.size) || '0 B'}</span>
            <button class="btn-icon" title="Stop" aria-label="Stop uploading {upload.name}" onclick={() => upload.abort?.()}><Icon name="close" size={15} /></button>
          {:else}
            <button class="btn-icon" title="Remove from the list" aria-label="Remove {upload.name} from the list" onclick={() => (uploads = uploads.filter((item) => item.id !== upload.id))}><Icon name="close" size={15} /></button>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}

  <div class="card overflow-hidden">
    <div class="flex items-center gap-2 border-b border-divider px-3 py-2 text-[12px] text-muted">
      <input type="checkbox" aria-label="Select all" checked={items.length > 0 && selected.size === items.length} onchange={toggleAll} />
      <span class="flex-1">Name</span>
      <span class="hidden w-24 text-right sm:block">Size</span>
      <span class="hidden w-40 md:block">Modified</span>
      <span class="w-24"></span>
    </div>
    {#if loading && !items.length}
      <p class="px-3 py-4 text-[13px] text-muted">Reading the folder…</p>
    {:else if !items.length && !error}
      <p class="px-3 py-4 text-[13px] text-muted">This folder is empty.</p>
    {/if}
    <ul class="divide-y divide-divider text-[13px]">
      {#each items as item (item.name)}
        <li class="flex items-center gap-2 px-3 py-1.5">
          <input type="checkbox" aria-label="Select {item.name}" checked={selected.has(item.name)} onchange={() => toggle(item.name)} />
          <span class="flex min-w-0 flex-1 items-center gap-2">
            <Icon name={item.isFolder ? 'folder' : 'file'} size={16} class="shrink-0 text-muted" />
            {#if item.isFolder}
              <button class="truncate text-left hover:underline" onclick={() => open(joinPath(path, item.name))}>{item.name}</button>
            {:else}
              <span class="truncate">{item.name}</span>
            {/if}
          </span>
          <span class="tabular hidden w-24 text-right text-muted sm:block">{item.isFolder ? '' : bytes(item.size)}</span>
          <span class="hidden w-40 text-muted md:block">{item.modified ? dateTime(item.modified) : ''}</span>
          <span class="flex w-24 justify-end gap-0.5">
            {#if !item.isFolder}
              <a class="btn-icon" href={deviceDownloadUrl(site.id, joinPath(path, item.name))} download={item.name} title="Download to this device" aria-label="Download {item.name} to this device"><Icon name="download" size={15} /></a>
            {/if}
            <button class="btn-icon" title="Rename" aria-label="Rename {item.name}" onclick={() => { dialogError = ''; nameBox = { title: `Rename ${item.name}`, value: item.name, action: 'rename', from: item.name }; }}><Icon name="document" size={15} /></button>
            <button class="btn-icon" title="Delete" aria-label="Delete {item.name}" onclick={() => { dialogError = ''; deleting = [item]; }}><Icon name="trash" size={15} /></button>
          </span>
        </li>
      {/each}
    </ul>
    {#if items.length < total}
      <div class="border-t border-divider px-3 py-2"><button class="btn" disabled={loading} onclick={() => open(path, false, true)}>Show more ({total - items.length} left)</button></div>
    {/if}
  </div>
</div>

{#if fixing && problem}
  <SiteFix {site} {problem} onclose={() => (fixing = false)} onfixed={() => { fixing = false; void open(path, true); }} />
{/if}

{#if nameBox}
  <Dialog title={nameBox.title} onclose={closeDialogs}>
    <label class="label" for="site-item-name">Name</label>
    <input id="site-item-name" class="field" bind:value={nameBox.value} data-autofocus autocomplete="off"
      onkeydown={(event) => { if (event.key === 'Enter' && nameBox) nameAction(nameBox); }} />
    {#if dialogError}<p class="mt-2 text-[12.5px] text-error" role="alert">{dialogError}</p>{/if}
    {#snippet footer()}
      <button class="btn" onclick={closeDialogs}>Cancel</button>
      <button class="btn-primary" disabled={busy} onclick={() => nameBox && nameAction(nameBox)}>{nameBox?.action === 'mkdir' ? 'Make folder' : 'Rename'}</button>
    {/snippet}
  </Dialog>
{:else if deleting}
  {@const list = deleting}
  <AskDialog title="Delete on {site.name}?" danger busy={busy} confirm="Delete"
    text={(list.length === 1 ? `${list[0].name}${list[0].isFolder ? ' and everything in it' : ''}` : `${list.length} items, and everything in their folders,`) + ` will be deleted on the server. This can't be undone.` + (dialogError ? ` ${dialogError}` : '')}
    onconfirm={() => remove(list)} onclose={closeDialogs} />
{:else if toHostBox}
  <Dialog title="Download to {store.texts.hostName}" onclose={closeDialogs}>
    <div class="space-y-3 text-[13px]">
      <p>{toHostBox.names.length === 1 ? toHostBox.names[0].name : `${toHostBox.names.length} items`} from {site.name}</p>
      <FolderField id="site-to-host" label="Folder" bind:value={toHostBox.folder} showSpace />
      <div>
        <label class="label" for="site-conflict">When a file is already there</label>
        <select id="site-conflict" class="field" bind:value={toHostBox.conflict}>
          <option value="skip">Skip it</option>
          <option value="replace">Replace it</option>
          <option value="keepBoth">Keep both</option>
        </select>
      </div>
      {#if dialogError}<p class="text-[12.5px] text-error" role="alert">{dialogError}</p>{/if}
    </div>
    {#snippet footer()}
      <button class="btn" onclick={closeDialogs}>Cancel</button>
      <button class="btn-primary" disabled={busy || !toHostBox?.folder.trim()} onclick={() => toHostBox && toHost(toHostBox)}>Download</button>
    {/snippet}
  </Dialog>
{:else if replacing.length}
  {@const file = replacing[0].file}
  <AskDialog title="Replace {file.name}?" confirm="Replace" keep="Keep the old one" danger
    text="{file.name} is already in this folder on {site.name}. Replacing it removes the old one."
    onconfirm={() => { const [next, ...rest] = replacing; replacing = rest; send(next.file, true, next.folder); }} onclose={() => (replacing = replacing.slice(1))} />
{/if}
