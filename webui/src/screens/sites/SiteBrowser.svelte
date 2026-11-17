<script lang="ts">
  import { onDestroy, onMount, tick, untrack } from 'svelte';
  import AskDialog from '../../components/AskDialog.svelte';
  import ContextMenu, { type MenuItem } from '../../components/ContextMenu.svelte';
  import Dialog from '../../components/Dialog.svelte';
  import FolderField from '../../components/FolderField.svelte';
  import FolderPicker from '../../components/FolderPicker.svelte';
  import Icon from '../../components/Icon.svelte';
  import { copyText } from '../../lib/clipboard';
  import { bytes, dateTime } from '../../lib/format';
  import { LongPress } from '../../lib/longpress';
  import { store } from '../../lib/store.svelte';
  import {
    crumbs, deviceDownloadUrl, hereText, joinPath, nameProblem, parentPath, problems, starLabel, uploadToSite,
    type Site, type SiteBrowseResult, type SiteItem, type SiteResult,
  } from '../../lib/sites';
  import {
    activation, addressesOf, autoRetryAllowed, commandKey, commandLabel, connectionKey, deviceDownloadAllowed, filterMatches, fixButtonText,
    fixable, footerText, hereTone, isCantReach, kindChip, lastListedText, linkedLine, nextSort, notRetryingText, pathFromText, remoteMenu,
    retryAtText, retryDelaySeconds, rowsOf, serverDownload, serverFolder, serverUpload, sizeText, sortRows, troubleTitle, troubleWhy,
    type RemoteCommand, type Row, type SortKey,
  } from '../../lib/sitesBrowse';
  import ServerFolders from './ServerFolders.svelte';
  import SiteFix from './SiteFix.svelte';

  // One remote's files (SITES.7, reworked in SITES.11 R6a to the agreed mockup `web-site` and `web-two`): browse, move
  // files to and from the server Inbound runs on (the default) or this device, and change the remote.
  let { site, onedit }: { site: Site; onedit?: () => void } = $props();

  let path = $state(untrack(() => site.remoteFolder || '/'));
  let items = $state<SiteItem[]>([]);
  let total = $state(0);
  let listing = $state<string | null>(null);
  let loading = $state(false);
  let error = $state('');
  let problem = $state<string | null>(null);
  let fixing = $state(false);
  let listedAt = $state(0);
  let everListed = $state(false);

  // Rows are keyed and selected by a key that is not the name: Drive allows two items of one name in a folder.
  let selected = $state<Set<string>>(new Set());
  let focusKey = $state<string | null>(null);
  let anchorKey: string | null = null;
  let sortKey = $state<SortKey>('name');
  let ascending = $state(true);
  let filter = $state('');
  let view = $state<'two' | 'server'>(readView());
  let leftPath = $state('');
  let leftPane: ServerFolders | undefined = $state();

  function readView(): 'two' | 'server' {
    try { return localStorage.getItem('inbound.sites.view') === 'two' ? 'two' : 'server'; } catch { return 'server'; }
  }
  function setView(next: 'two' | 'server') {
    view = next;
    try { localStorage.setItem('inbound.sites.view', next); } catch { /* a convenience only */ }
  }

  const rows = $derived(rowsOf(items));
  const shown = $derived(sortRows(rows.filter((row) => filterMatches(row.item.name, filter)), sortKey, ascending));
  const chosenRows = $derived(shown.filter((row) => selected.has(row.key)));
  const chosen = $derived(chosenRows.map((row) => row.item));
  const showsHere = $derived(!!site.localFolder?.trim());
  const starred = $derived(site.stars.includes(path));
  const canMakeFolder = $derived(site.capabilities.includes('newFolder'));
  const defaultFolder = $derived(serverFolder(site, store.model.settings?.defaultDownloadDirectory));
  const hostName = $derived(store.texts.hostName);
  const footer = $derived(footerText({
    shown: shown.length, total: Math.max(total, items.length), selected: chosen.length, selectedBytes: chosen.reduce((sum, item) => sum + (item.isFolder || item.size < 0 ? 0 : item.size), 0),
    same: items.filter((item) => item.here === 'same').length, linked: showsHere, where: (view === 'two' && leftPath) || defaultFolder || null, hostName, format: bytes,
  }));
  let starring = $state(false);

  // One dialog at a time.
  let nameBox = $state<{ title: string; value: string; action: 'mkdir' | 'rename'; from?: string } | null>(null);
  let deleting = $state<SiteItem[] | null>(null);
  let deleteCount = $state<{ files: number; folders: number; sizeUnknown: boolean; bytes: number } | null>(null);
  let dialogError = $state('');
  let busy = $state(false);
  let downloadBox = $state<{ items: SiteItem[]; where: 'server' | 'device'; folder: string; conflict: string } | null>(null);
  let uploadBox = $state<{ where: 'server' | 'device'; files: File[]; serverFolder: string; dropping: boolean } | null>(null);
  let pickingServerFolder = $state(false);
  let replacing = $state<{ file: File; folder: string }[]>([]);

  interface Upload { id: number; name: string; size: number; sent: number; state: 'sending' | 'done' | 'failed'; error?: string; abort?: () => void }
  let uploads = $state<Upload[]>([]);
  let uploadId = 0;
  let picker: HTMLInputElement | undefined = $state();

  // ---- Reading a folder ----

  // Only the newest browse is shown: a slow answer for a folder left since must not replace the one asked for after it.
  let round = 0;
  let failures = 0;
  let spellStart = 0;
  let retryAt = $state<Date | null>(null);
  let retryTimer: ReturnType<typeof setTimeout> | undefined;

  function stopRetry() {
    if (retryTimer) clearTimeout(retryTimer);
    retryTimer = undefined;
    retryAt = null;
  }

  function scheduleRetry() {
    stopRetry();
    if (!isCantReach(problem)) return;
    if (!spellStart) spellStart = Date.now();
    if (!autoRetryAllowed(failures - 1, Date.now() - spellStart)) return;
    const seconds = retryDelaySeconds(failures - 1);
    retryAt = new Date(Date.now() + seconds * 1000);
    retryTimer = setTimeout(() => void open(path, true), seconds * 1000);
  }

  async function open(next: string, refresh = false, more = false) {
    if (more && next !== path) return;
    const mine = ++round;
    stopRetry();
    loading = true;
    try {
      const result = await store.ask<SiteBrowseResult>('sites.browse', {
        id: site.id, path: next, offset: more ? items.length : 0, limit: 500, refresh, listing: more ? listing : undefined,
      });
      if (mine !== round) return;
      if (!result.ok) {
        error = result.message ?? 'The folder could not be read.';
        problem = result.problem ?? null;
        if (problem === problems.listingChanged) return open(next, true);
        failures++;
        scheduleRetry();
        return;
      }
      error = '';
      problem = null;
      failures = 0;
      spellStart = 0;
      path = result.path;
      items = more ? [...items, ...result.items] : result.items;
      total = result.total;
      listing = result.listing ?? null;
      listedAt = Date.now();
      everListed = true;
      if (!more) { selected = new Set(); focusKey = null; anchorKey = null; filter = ''; }
    } catch (failure) {
      if (mine === round) {
        error = failure instanceof Error ? failure.message : String(failure);
        problem = null;
      }
    } finally {
      if (mine === round) loading = false;
    }
  }

  onMount(() => void open(path));

  // A change of what the connection depends on while the folder cannot be read may be the fix: read it again.
  let lastConnection = untrack(() => connectionKey(site));
  $effect(() => {
    const key = connectionKey(site);
    untrack(() => {
      if (key === lastConnection) return;
      lastConnection = key;
      if (error) void open(path, true);
    });
  });

  // An upload belongs to this page: leaving the remote stops it, and says so, rather than leaving it running out of sight.
  onDestroy(() => {
    stopRetry();
    for (const upload of uploads) {
      if (upload.state !== 'sending') continue;
      upload.abort?.();
      store.toast(`Uploading ${upload.name} stopped because you left ${site.name}.`, 'error');
    }
  });

  // ---- Stars ----

  async function toggleStar(target = path, on = !site.stars.includes(path)) {
    starring = true;
    try {
      const result = await store.ask<SiteResult>('sites.star', { id: site.id, path: target, starred: on });
      if (!result.ok) store.toast(result.message ?? 'The star was not changed.', 'error');
      else if (result.site) {
        const saved = result.site;
        store.model.sites = store.model.sites.map((item) => (item.id === saved.id ? saved : item));
      }
    } catch (failure) {
      store.toast(failure instanceof Error ? failure.message : String(failure), 'error');
    } finally {
      starring = false;
    }
  }

  // ---- Selection ----

  function select(row: Row, event?: { ctrlKey?: boolean; metaKey?: boolean; shiftKey?: boolean }) {
    if (event?.shiftKey && anchorKey) {
      const from = shown.findIndex((r) => r.key === anchorKey);
      const to = shown.findIndex((r) => r.key === row.key);
      if (from >= 0 && to >= 0) {
        const [a, b] = from < to ? [from, to] : [to, from];
        selected = new Set(shown.slice(a, b + 1).map((r) => r.key));
      }
    } else if (event?.ctrlKey || event?.metaKey) {
      const next = new Set(selected);
      if (next.has(row.key)) next.delete(row.key);
      else next.add(row.key);
      selected = next;
      anchorKey = row.key;
    } else {
      selected = new Set([row.key]);
      anchorKey = row.key;
    }
    focusKey = row.key;
  }

  function toggleBox(row: Row) {
    const next = new Set(selected);
    if (next.has(row.key)) next.delete(row.key);
    else next.add(row.key);
    selected = next;
    anchorKey = row.key;
    focusKey = row.key;
  }

  function toggleAll() {
    selected = selected.size === shown.length ? new Set() : new Set(shown.map((row) => row.key));
  }

  function rowEl(key: string | null): HTMLElement | null {
    return key === null ? null : (document.querySelector(`[data-rowkey="${CSS.escape(key)}"]`) as HTMLElement | null);
  }

  function names(list: SiteItem[]) {
    return list.map((item) => ({ name: item.name, isFolder: item.isFolder }));
  }

  // ---- Commands ----

  function closeDialogs() {
    nameBox = null;
    deleting = null;
    downloadBox = null;
    uploadBox = null;
    pickingServerFolder = false;
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

  async function askDelete(list: SiteItem[]) {
    dialogError = '';
    deleteCount = null;
    deleting = list;
    try {
      const counted = await store.ask<{ ok: boolean; message?: string; files: number; folders: number; bytes: number; sizeUnknown: boolean }>(
        'sites.count', { id: site.id, path, items: names(list) });
      if (deleting !== list) return;
      if (counted.ok) deleteCount = counted;
      else dialogError = counted.message ?? '';
    } catch (failure) {
      if (deleting === list) dialogError = failure instanceof Error ? failure.message : String(failure);
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

  /** Download means the server: into its folder, going on if this tab is closed (bug 1). */
  async function toServer(list: SiteItem[], folder: string, conflict = 'keepBoth'): Promise<boolean> {
    const result = await store.ask<{ ok: boolean; message?: string }>('sites.transfer.start', serverDownload(site.id, path, folder, list, conflict));
    if (!result.ok) {
      dialogError = result.message ?? 'The download did not start.';
      store.toast(dialogError, 'error');
      return false;
    }
    store.toast(`Downloading to ${hostName}. Its progress is under Transfers.`);
    return true;
  }

  async function download(list: SiteItem[]) {
    if (!list.length) return;
    const folder = view === 'two' && leftPath ? leftPath : defaultFolder;
    // No folder to name: ask, so the choice and the folder are in front of the person.
    if (!folder) return downloadTo(list);
    await toServer(list, folder);
  }

  function downloadTo(list: SiteItem[]) {
    if (!list.length) return;
    dialogError = '';
    downloadBox = { items: list, where: 'server', folder: view === 'two' && leftPath ? leftPath : defaultFolder, conflict: 'keepBoth' };
  }

  function confirmDownload(current: NonNullable<typeof downloadBox>) {
    if (current.where === 'device') {
      // One file, through this browser: it stops when the tab is closed, which the dialog said.
      const item = current.items[0];
      const link = document.createElement('a');
      link.href = deviceDownloadUrl(site.id, joinPath(path, item.name));
      link.download = item.name;
      document.body.appendChild(link);
      link.click();
      link.remove();
      closeDialogs();
      return;
    }
    void run(() => toServer(current.items, current.folder, current.conflict));
  }

  function openUpload() {
    dialogError = '';
    uploadBox = { where: 'device', files: [], serverFolder: '', dropping: false };
  }

  function confirmUpload(current: NonNullable<typeof uploadBox>) {
    if (current.where === 'device') {
      for (const file of current.files) send(file);
      closeDialogs();
      return;
    }
    const start = serverUpload(site.id, path, current.serverFolder);
    if (!start) {
      dialogError = 'Choose a folder on the server.';
      return;
    }
    void run(async () => {
      const result = await store.ask<{ ok: boolean; message?: string }>('sites.transfer.start', start);
      if (!result.ok) {
        dialogError = result.message ?? 'The upload did not start.';
        return false;
      }
      store.toast(`Uploading from ${hostName}. Its progress is under Transfers.`);
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

  function staged(files: FileList | File[] | null | undefined) {
    if (!uploadBox) return;
    uploadBox.files = [...uploadBox.files, ...Array.from(files ?? [])];
  }

  async function copyAddress() {
    const text = addressesOf(site, path, chosen.map((item) => item.name));
    store.toast(await copyText(text) ? 'Address copied.' : 'The browser would not copy it.');
  }

  async function pasteAddress() {
    let text = '';
    try { text = await navigator.clipboard.readText(); } catch { /* not allowed here */ }
    const next = pathFromText(site, text);
    if (next) void open(next);
    else store.toast("The clipboard has no address of this remote's.", 'error');
  }

  function newFolder() {
    dialogError = '';
    nameBox = { title: 'New folder', value: '', action: 'mkdir' };
  }

  function rename(item: SiteItem) {
    dialogError = '';
    nameBox = { title: `Rename ${item.name}`, value: item.name, action: 'rename', from: item.name };
  }

  function command(which: RemoteCommand) {
    switch (which) {
      case 'download': void download(chosen); break;
      case 'downloadTo': downloadTo(chosen); break;
      case 'rename': if (chosen.length === 1) rename(chosen[0]); break;
      case 'delete': if (chosen.length) void askDelete(chosen); break;
      case 'newFolder': newFolder(); break;
      case 'upload': openUpload(); break;
      case 'refresh': void open(path, true); break;
      case 'pasteAddress': void pasteAddress(); break;
      case 'copyAddress': void copyAddress(); break;
      case 'starFolder': if (chosen.length === 1 && chosen[0].isFolder) void toggleStar(joinPath(path, chosen[0].name), !site.stars.includes(joinPath(path, chosen[0].name))); break;
    }
  }

  function activate() {
    const what = activation(chosen);
    if (what === 'openFolder') void open(joinPath(path, chosen[0].name));
    else if (what === 'download') void download(chosen);
  }

  // ---- The menu: right click, long press, the "..." button and Shift+F10 (as My TorBox's list) ----

  let menu = $state<{ x: number; y: number; items: MenuItem[]; label: string } | null>(null);
  let menuOpenedAt = 0;
  const longPress = new LongPress();

  function openMenu(row: Row | null, x: number, y: number) {
    if (row && !selected.has(row.key)) select(row);
    if (!row) { selected = new Set(); }
    menuOpenedAt = Date.now();
    const groups = remoteMenu(site.capabilities, row ? chosen : []);
    const entries: MenuItem[] = [];
    groups.forEach((group, index) => {
      if (index > 0) entries.push({ separator: true });
      for (const which of group) {
        entries.push({
          label: which === 'starFolder' && site.stars.includes(joinPath(path, chosen[0]?.name ?? '')) ? 'Remove the star' : commandLabel(which),
          hint: commandKey(which) || undefined, onselect: () => command(which),
        });
      }
    });
    menu = { x, y, items: entries, label: row ? `Actions for ${row.item.name}` : `Actions for ${path}` };
  }

  function closeMenu() {
    const key = focusKey;
    menu = null;
    void tick().then(() => { if (key && !document.querySelector('[role="dialog"]')) rowEl(key)?.focus(); });
  }

  function contextmenu(row: Row | null, event: MouseEvent) {
    event.preventDefault();
    if (Date.now() - menuOpenedAt < 700) return; // the long press already opened it
    openMenu(row, event.clientX, event.clientY);
  }

  function moreButton(row: Row, event: MouseEvent) {
    event.stopPropagation();
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    openMenu(row, box.left, box.bottom + 2);
  }

  function pointerdown(row: Row, event: PointerEvent) {
    longPress.down(event.pointerType, event.clientX, event.clientY, () => openMenu(row, event.clientX, event.clientY));
  }

  function rowClick(row: Row, event: MouseEvent) {
    if (longPress.takeClick()) return;
    select(row, event);
  }

  function move(delta: number, extend: boolean) {
    if (!shown.length) return;
    const at = shown.findIndex((row) => row.key === focusKey);
    const next = shown[Math.max(0, Math.min(shown.length - 1, at < 0 ? (delta > 0 ? 0 : shown.length - 1) : at + delta))];
    if (extend) select(next, { shiftKey: true });
    else select(next);
    void tick().then(() => rowEl(next.key)?.focus());
  }

  function listKey(event: KeyboardEvent) {
    const typing = (event.target as HTMLElement | null)?.closest('input, select, textarea');
    if (typing) return;
    const ctrl = event.ctrlKey || event.metaKey;
    const current = shown.find((row) => row.key === focusKey) ?? null;
    if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const box = (rowEl(focusKey) ?? (event.currentTarget as HTMLElement)).getBoundingClientRect();
      openMenu(current, box.left + 24, box.bottom);
    } else if (event.key === 'ArrowDown') { event.preventDefault(); move(1, event.shiftKey); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); move(-1, event.shiftKey); }
    else if (event.key === 'Home') { event.preventDefault(); move(-shown.length, event.shiftKey); }
    else if (event.key === 'End') { event.preventDefault(); move(shown.length, event.shiftKey); }
    else if (event.key === 'Enter') { event.preventDefault(); activate(); }
    else if (event.key === ' ' && current) { event.preventDefault(); toggleBox(current); }
    else if (ctrl && event.key.toLowerCase() === 'a') { event.preventDefault(); toggleAll(); }
    else if (event.key === 'F2' && chosen.length === 1) { event.preventDefault(); rename(chosen[0]); }
    else if (event.key === 'Delete' && chosen.length) { event.preventDefault(); void askDelete(chosen); }
    else if (event.key === 'F5') { event.preventDefault(); void open(path, true); }
    else if (event.key === 'Backspace' && path !== '/') { event.preventDefault(); void open(parentPath(path)); }
    else if (ctrl && event.shiftKey && event.key.toLowerCase() === 's') { event.preventDefault(); downloadTo(chosen); }
    else if (ctrl && event.shiftKey && event.key.toLowerCase() === 'n') { event.preventDefault(); newFolder(); }
    else if (ctrl && event.shiftKey && event.key.toLowerCase() === 'c') { event.preventDefault(); void copyAddress(); }
    else if (ctrl && event.key.toLowerCase() === 'u') { event.preventDefault(); openUpload(); }
  }

  function sortBy(key: SortKey) {
    ({ key: sortKey, ascending } = nextSort(sortKey, ascending, key));
  }
  const arrow = (key: SortKey) => (sortKey === key ? (ascending ? ' ▲' : ' ▼') : '');

  const linked = $derived(linkedLine(site.localFolder, site.remoteFolder));
  const inTrouble = $derived(!!error && (!everListed || !items.length));
</script>

<div class="space-y-3">
  <!-- The toolbar: the same commands as the window's, minus those that need this PC. -->
  <div class="flex flex-wrap items-center gap-1.5" role="toolbar" aria-label="Remote files">
    <button class="btn" disabled={!chosen.length} onclick={() => download(chosen)}><Icon name="download" size={15} /> Download</button>
    <button class="btn" onclick={openUpload}><Icon name="upload" size={15} /> Upload</button>
    <span class="mx-1 h-5 w-px bg-divider" aria-hidden="true"></span>
    {#if canMakeFolder}<button class="btn" onclick={newFolder}><Icon name="folder-plus" size={15} /> New folder</button>{/if}
    <button class="btn" disabled={chosen.length !== 1} onclick={() => rename(chosen[0])}><Icon name="document" size={15} /> Rename</button>
    <button class="btn-danger" disabled={!chosen.length} onclick={() => askDelete(chosen)}><Icon name="trash" size={15} /> Delete on server</button>
    <span class="mx-1 h-5 w-px bg-divider" aria-hidden="true"></span>
    <button class="btn" disabled={loading} onclick={() => open(path, true)}><Icon name="refresh" size={15} /> Refresh</button>
    {#if onedit}<button class="btn" onclick={onedit}><Icon name="settings" size={15} /> Edit remote</button>{/if}
    <input bind:this={picker} type="file" multiple class="hidden" onchange={(event) => { staged((event.currentTarget as HTMLInputElement).files); (event.currentTarget as HTMLInputElement).value = ''; }} />
  </div>

  <div class="flex flex-wrap items-center gap-3">
    <div class="segmented" role="group" aria-label="View">
      <button aria-pressed={view === 'two'} onclick={() => setView('two')}>Two panes</button>
      <button aria-pressed={view === 'server'} onclick={() => setView('server')}>Remote only</button>
    </div>
    {#if linked}<span class="ml-auto flex min-w-0 items-center gap-1 text-[12.5px] text-muted"><Icon name="link" size={14} /><span class="truncate">{linked}</span></span>{/if}
  </div>

  {#if site.stars.length}
    <div class="flex flex-wrap items-center gap-1.5 text-[12.5px]" aria-label="Starred folders">
      {#each site.stars as star (star)}
        <button class="btn {star === path ? 'text-accent' : ''}" title={star} aria-current={star === path ? 'page' : undefined} onclick={() => open(star)}>
          <Icon name="star" size={13} class="fill-current" /> {starLabel(star, site.stars)}
        </button>
      {/each}
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

  <div class="flex items-stretch gap-3">
    {#if view === 'two'}
      <ServerFolders bind:this={leftPane} bind:path={leftPath} initial={defaultFolder} />
      <div class="flex flex-col justify-center gap-2" role="group" aria-label="Move between the panes">
        <button class="btn-icon" title="Download the selection into the folder on the left" aria-label="Download the selection into the folder on the left"
          disabled={!chosen.length || !leftPane?.target()} onclick={() => { const folder = leftPane?.target(); if (folder) void toServer(chosen, folder); }}><Icon name="chevron-left" size={16} /></button>
        <button class="btn-icon" title="Upload the folder selected on the left into this folder" aria-label="Upload the folder selected on the left into this folder"
          disabled={!leftPane?.target()} onclick={() => { const folder = leftPane?.target(); const start = folder ? serverUpload(site.id, path, folder) : null; if (start) void store.ask('sites.transfer.start', start).then(() => store.toast(`Uploading from ${hostName}. Its progress is under Transfers.`)); }}><Icon name="chevron-right" size={16} /></button>
      </div>
    {/if}

    <section class="card flex min-w-0 flex-1 flex-col overflow-hidden" aria-label="Files on {site.name}">
      <header class="flex items-center gap-2 border-b border-divider px-3 py-2 text-[13px]">
        <Icon name={site.kind === 'sftp' || site.kind === 'ftp' ? 'server' : 'cloud'} size={16} class="text-muted" />
        <span class="min-w-0 truncate font-semibold">{site.name}</span>
        {#if kindChip(site.kind)}<span class="rounded-full border border-divider px-2 text-[11.5px] text-muted">{kindChip(site.kind)}</span>{/if}
      </header>

      <nav class="flex flex-wrap items-center gap-1 border-b border-divider px-2 py-1.5 text-[13px]" aria-label="Folder">
        <button class="btn-icon" title="Up one folder" aria-label="Up one folder" disabled={path === '/' || loading} onclick={() => open(parentPath(path))}><Icon name="up" size={16} /></button>
        <span class="flex min-w-0 flex-1 flex-wrap items-center gap-1">
          {#each crumbs(path) as crumb, index (crumb.path)}
            {#if index > 1}<span class="text-muted">/</span>{/if}
            <button class="link max-w-[12rem] truncate" onclick={() => open(crumb.path)}>{index === 0 ? site.name : crumb.name}</button>
          {/each}
        </span>
        <button class="btn-icon {starred ? 'text-accent' : ''}" disabled={starring || path === '/'} aria-pressed={starred}
          title={starred ? 'Remove the star' : 'Star this folder'} aria-label={starred ? 'Remove the star from this folder' : 'Star this folder'}
          onclick={() => toggleStar()}><Icon name="star" size={16} class={starred ? 'fill-current' : ''} /></button>
        <label class="flex items-center gap-1 rounded border border-control-stroke px-2 text-[12.5px]">
          <Icon name="search" size={14} class="text-muted" />
          <input class="w-28 bg-transparent py-1 outline-none" type="search" placeholder="Filter" aria-label="Filter the files in this folder" bind:value={filter} />
        </label>
      </nav>

      {#if inTrouble}
        <div class="flex flex-col items-center gap-2 px-4 py-8 text-center" role="alert">
          <Icon name="alert" size={22} class="tone-failed" />
          <h2 class="text-[15px] font-semibold">{troubleTitle(problem, site.name)}</h2>
          <p class="max-w-prose text-[13px] text-muted">{troubleWhy(problem, error === 'The folder could not be read.' ? '' : error)}</p>
          <p class="text-[12.5px] text-muted">{isCantReach(problem) ? (retryAt ? retryAtText(retryAt) : notRetryingText) : ''}</p>
          <div class="mt-1 flex flex-wrap justify-center gap-2">
            {#if fixable(problem)}<button class="btn-primary" onclick={() => (fixing = true)}>{fixButtonText(problem)}</button>{/if}
            <button class="btn {fixable(problem) ? '' : 'btn-primary'}" disabled={loading} onclick={() => open(path, true)}>Retry now</button>
            {#if onedit}<button class="btn" onclick={onedit}>Edit remote</button>{/if}
          </div>
        </div>
      {:else}
        {#if error}
          <div class="notice notice-error m-2" role="alert"><Icon name="alert" size={16} /><span class="notice-text">{error}</span>
            {#if fixable(problem)}<button class="btn" onclick={() => (fixing = true)}>{fixButtonText(problem)}</button>{/if}
            <button class="btn" onclick={() => open(path, true)}>Retry now</button></div>
        {/if}
        <div class="flex items-center gap-2 border-b border-divider px-3 py-1.5 text-[12px] text-muted" role="presentation">
          <input type="checkbox" aria-label="Select all" checked={shown.length > 0 && selected.size === shown.length} onchange={toggleAll} />
          <button class="flex-1 text-left" onclick={() => sortBy('name')}>Name{arrow('name')}</button>
          <button class="hidden w-24 text-right sm:block" onclick={() => sortBy('size')}>Size{arrow('size')}</button>
          <button class="hidden w-40 text-left md:block" onclick={() => sortBy('modified')}>Modified{arrow('modified')}</button>
          {#if showsHere}<button class="hidden w-28 text-left sm:block" title="Compared with {site.localFolder}" onclick={() => sortBy('here')}>On {hostName}{arrow('here')}</button>{/if}
          <span class="w-9"></span>
        </div>
        {#if path !== '/'}
          <button class="flex w-full items-center gap-2 border-b border-divider px-3 py-1.5 text-left text-[13px] hover:bg-hover" aria-label="Up one folder, .." ondblclick={() => open(parentPath(path))} onclick={() => open(parentPath(path))}>
            <span class="w-4"></span><Icon name="folder" size={16} class="shrink-0 text-muted" /><span>..</span>
          </button>
        {/if}
        {#if loading && !items.length}
          <p class="px-3 py-4 text-[13px] text-muted">Reading the folder…</p>
        {:else if !shown.length && !error}
          <p class="px-3 py-4 text-[13px] text-muted">{filter.trim() ? 'Nothing here matches the filter.' : 'This folder is empty.'}</p>
        {/if}
        <ul class="min-h-[6rem] divide-y divide-divider text-[13px]" role="listbox" aria-multiselectable="true" aria-label="Files in {path}" tabindex="-1" onkeydown={listKey} oncontextmenu={(event) => { if (event.target === event.currentTarget) contextmenu(null, event); }}>
          {#each shown as row (row.key)}
            {@const item = row.item}
            <!-- svelte-ignore a11y_click_events_have_key_events -->
            <li data-rowkey={row.key} role="option" aria-selected={selected.has(row.key)} tabindex={focusKey === row.key || (focusKey === null && row === shown[0]) ? 0 : -1}
              class="item-row flex items-center gap-2 px-3 py-1.5 {selected.has(row.key) ? 'bg-selected' : 'hover:bg-hover'}"
              onclick={(event) => rowClick(row, event)}
              ondblclick={() => { select(row); activate(); }}
              oncontextmenu={(event) => contextmenu(row, event)}
              onpointerdown={(event) => pointerdown(row, event)}
              onpointermove={(event) => longPress.move(event.clientX, event.clientY)}
              onpointerup={() => longPress.up()}
              onpointercancel={() => longPress.cancelled()}>
              <input type="checkbox" tabindex="-1" aria-label="Select {item.name}" checked={selected.has(row.key)} onclick={(event) => event.stopPropagation()} onchange={() => toggleBox(row)} />
              <span class="flex min-w-0 flex-1 items-center gap-2">
                <Icon name={item.isFolder ? 'folder' : 'file'} size={16} class="shrink-0 text-muted" />
                <span class="truncate">{item.name}</span>
                {#if showsHere && hereText(item.here)}<span class="shrink-0 text-[12px] text-muted sm:hidden">· {hereText(item.here)}</span>{/if}
              </span>
              <span class="tabular hidden w-24 text-right text-muted sm:block">{sizeText(item, bytes)}</span>
              <span class="hidden w-40 text-muted md:block">{item.modified ? dateTime(item.modified) : ''}</span>
              {#if showsHere}<span class="hidden w-28 truncate sm:block {hereTone(item.here) === 'same' ? 'tone-done' : hereTone(item.here) === 'older' ? 'tone-waiting' : 'text-muted'}">{hereText(item.here)}</span>{/if}
              <span class="flex w-9 shrink-0 justify-end">
                <button class="row-menu" aria-label="Actions for {item.name}" aria-haspopup="menu" title="More actions" onclick={(event) => moreButton(row, event)}><Icon name="more" size={16} /></button>
              </span>
            </li>
          {/each}
        </ul>
        {#if items.length < total}
          <div class="border-t border-divider px-3 py-2"><button class="btn" disabled={loading} onclick={() => open(path, false, true)}>Show more ({total - items.length} left)</button></div>
        {/if}
      {/if}

      <footer class="border-t border-divider px-3 py-1.5 text-[12px] text-muted">
        {#if inTrouble}{everListed ? lastListedText(listedAt, Date.now()) : 'Not listed yet'}{:else}{footer}{/if}
      </footer>
    </section>
  </div>
</div>

{#if menu}
  <ContextMenu items={menu.items} x={menu.x} y={menu.y} label={menu.label} onclose={closeMenu} />
{/if}

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
    text={(list.length === 1 ? `${list[0].name}${list[0].isFolder ? ' and everything in it' : ''}` : `${list.length} items, and everything in their folders,`) + ` will be deleted on the server. This can't be undone.`
      + (deleteCount ? ` That is ${deleteCount.files.toLocaleString()} file${deleteCount.files === 1 ? '' : 's'}${deleteCount.sizeUnknown || !deleteCount.bytes ? '' : ` (${bytes(deleteCount.bytes)})`}.` : dialogError ? '' : ' Counting what is in it…')
      + (dialogError ? ` ${dialogError}` : '')}
    onconfirm={() => remove(list)} onclose={closeDialogs} />
{:else if downloadBox}
  {@const box = downloadBox}
  {@const sizeSum = box.items.reduce((sum, item) => sum + (item.isFolder || item.size < 0 ? 0 : item.size), 0)}
  <Dialog title={box.items.length === 1 ? `Download ${box.items[0].name}` : `Download ${box.items.length} items`} onclose={closeDialogs}>
    <div class="space-y-3 text-[13px]">
      <p>{sizeSum > 0 ? `${bytes(sizeSum)} from ` : 'From '}<strong>{site.name}</strong>.</p>
      <label class="flex items-start gap-2"><input type="radio" name="download-where" value="server" bind:group={downloadBox.where} />
        <span>To {hostName}{box.folder ? `, in ${box.folder}` : ''} <span class="text-muted">(keeps going if you close this tab)</span></span></label>
      <label class="flex items-start gap-2 {deviceDownloadAllowed(box.items) ? '' : 'opacity-60'}"><input type="radio" name="download-where" value="device" bind:group={downloadBox.where} disabled={!deviceDownloadAllowed(box.items)} />
        <span>To this device, through the browser <span class="text-muted">({deviceDownloadAllowed(box.items) ? 'stops if you close this tab' : 'one file at a time'})</span></span></label>
      {#if box.where === 'server'}
        <FolderField id="site-to-host" label="Folder on {hostName}" bind:value={downloadBox.folder} showSpace />
        <div>
          <label class="label" for="site-conflict">When a file is already there</label>
          <select id="site-conflict" class="field" bind:value={downloadBox.conflict}>
            <option value="keepBoth">Keep both</option>
            <option value="skip">Skip it</option>
            <option value="replace">Replace it</option>
          </select>
        </div>
      {/if}
      {#if dialogError}<p class="text-[12.5px] text-error" role="alert">{dialogError}</p>{/if}
    </div>
    {#snippet footer()}
      <button class="btn-primary" disabled={busy || (box.where === 'server' && !box.folder.trim())} onclick={() => downloadBox && confirmDownload(downloadBox)}><Icon name="download" size={15} /> Download</button>
      <button class="btn" onclick={closeDialogs}>Cancel</button>
    {/snippet}
  </Dialog>
{:else if uploadBox}
  {@const box = uploadBox}
  <Dialog title="Upload to {path}" onclose={closeDialogs}>
    <div class="space-y-3 text-[13px]">
      <label class="flex items-center gap-2"><input type="radio" name="upload-where" value="server" bind:group={uploadBox.where} />
        <span>A folder on {hostName} <span class="text-muted">(Inbound's folders)</span></span></label>
      <label class="flex items-center gap-2"><input type="radio" name="upload-where" value="device" bind:group={uploadBox.where} /> <span>Files on this device</span></label>
      {#if box.where === 'device'}
        <div class="flex flex-col items-center gap-2 rounded border border-dashed border-control-stroke px-4 py-6 text-center {box.dropping ? 'bg-hover' : ''}" role="group" aria-label="Drop files here"
          ondragover={(event) => { event.preventDefault(); if (uploadBox) uploadBox.dropping = true; }}
          ondragleave={() => { if (uploadBox) uploadBox.dropping = false; }}
          ondrop={(event) => { event.preventDefault(); if (uploadBox) uploadBox.dropping = false; staged(event.dataTransfer?.files); }}>
          <Icon name="upload" size={22} />
          <strong>Drop files here</strong>
          <button class="btn" onclick={() => picker?.click()}>Choose files…</button>
          <span class="text-[12px] text-muted">{box.files.length ? `${box.files.length} chosen: ${box.files.map((file) => file.name).join(', ')}` : 'Keep this tab open until they finish.'}</span>
        </div>
      {:else}
        <div class="flex items-center gap-2">
          <span class="min-w-0 flex-1 truncate rounded border border-control-stroke px-2 py-1.5">{box.serverFolder || 'No folder chosen'}</span>
          <button class="btn" onclick={() => (pickingServerFolder = true)}>Choose a folder…</button>
        </div>
        <p class="text-[12px] text-muted">The server lists its folders, not their files, so a whole folder goes up. It keeps going if you close this tab.</p>
      {/if}
      {#if dialogError}<p class="text-[12.5px] text-error" role="alert">{dialogError}</p>{/if}
    </div>
    {#snippet footer()}
      <button class="btn-primary" disabled={busy || (box.where === 'device' ? !box.files.length : !box.serverFolder)} onclick={() => uploadBox && confirmUpload(uploadBox)}><Icon name="upload" size={15} /> Upload</button>
      <button class="btn" onclick={closeDialogs}>Cancel</button>
    {/snippet}
  </Dialog>
  {#if pickingServerFolder}
    <FolderPicker initial={box.serverFolder} onpick={(picked) => { if (uploadBox) uploadBox.serverFolder = picked; pickingServerFolder = false; }} onclose={() => (pickingServerFolder = false)} />
  {/if}
{:else if replacing.length}
  {@const file = replacing[0].file}
  <AskDialog title="Replace {file.name}?" confirm="Replace" keep="Keep the old one" danger
    text="{file.name} is already in this folder on {site.name}. Replacing it removes the old one."
    onconfirm={() => { const [next, ...rest] = replacing; replacing = rest; send(next.file, true, next.folder); }} onclose={() => (replacing = replacing.slice(1))} />
{/if}

<style>
  .row-menu { display: inline-flex; border-radius: 4px; padding: 4px; color: var(--muted); opacity: 0; }
  .row-menu:hover { background: var(--hover); color: var(--text); }
  .item-row:hover .row-menu, .item-row:focus-within .row-menu, .row-menu:focus-visible { opacity: 1; }
  @media (pointer: coarse) { .row-menu { opacity: 1; padding: 8px; } }
</style>
