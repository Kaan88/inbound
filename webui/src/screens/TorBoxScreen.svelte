<script lang="ts">
  import Confirm from '../components/Confirm.svelte';
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import RemoteFileTree from '../components/RemoteFileTree.svelte';
  import { dateTime } from '../lib/format';
  import {
    actionsOf, buildFileTree, canBrowse, canDeleteFromProvider, checkable, choiceOf, choosableIds, countedItems, deleteQuestion, downloadStarted, emptyKind,
    failureText, filterHosters, hosterDetails, hostersUp, isFullPath, itemSortLabels, itemView, jobFor, kindHeading, localFilesOf, localWords,
    needsTheRest, providerNameOf, restoreChoice, sortItems, type FileTree, type ItemSortKey,
  } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import type { Hoster, RemoteAddResult, RemoteFile, RemoteItem } from '../lib/types';

  // My <provider>: what is in the account (one tab per kind of item the provider reports), with a file tree and
  // Download for the chosen item, and the file hosters it can fetch from. The key lives on the provider's page in Settings.
  let { provider: wanted = '' }: { provider?: string } = $props();

  const account = $derived(store.model.accounts.find((a) => a.provider === wanted) ?? store.model.accounts.find(canBrowse) ?? store.model.accounts[0]);
  const name = $derived(account ? providerNameOf(account) : 'provider');
  const browsable = $derived(!!account && canBrowse(account));
  const kinds = $derived(account?.itemKinds ?? []);

  let tab = $state('');
  const current = $derived(tab === 'hosters' ? 'hosters' : kinds.includes(tab) ? tab : (kinds[0] ?? 'hosters'));

  // ---- Items, one page per kind ----
  interface Page { items: RemoteItem[]; hasMore: boolean; offset: number; loaded: boolean; problem: string; lastCount: number; fresh: boolean }
  const blank = (): Page => ({ items: [], hasMore: false, offset: 0, loaded: false, problem: '', lastCount: 0, fresh: false });
  let pages = $state<Record<string, Page>>({});
  let loads = $state(0);
  let selectedId = $state<string | null>(null);
  let sortKey = $state<ItemSortKey>('default');
  let ascending = $state(true);
  let status = $state('');
  let statusIsProblem = $state(false);
  let acting = $state(false);
  let confirmDelete = $state(false);

  const page = $derived(pages[current] ?? blank());
  const jobs = $derived(Object.values(store.model.remoteJobs));

  function view(item: RemoteItem) {
    const job = jobFor(jobs, item);
    const local = job?.state === 'localStarted' ? localWords(localFilesOf(job, store.model.groups, store.model.jobs)) : null;
    return itemView(item, name, job, local);
  }
  const shown = $derived(sortItems(page.items, (item) => item, view, sortKey, ascending));
  const selected = $derived(page.items.find((item) => item.itemId === selectedId));
  const selectedJob = $derived(selected ? jobFor(jobs, selected) : undefined);
  const selectedView = $derived(selected ? view(selected) : undefined);
  const activeJob = $derived(selectedJob && selectedJob.state !== 'failed' ? selectedJob : undefined);

  async function load(kind: string, more = false, fresh = false) {
    if (!account || !browsable) return;
    const base = pages[kind] ?? blank();
    const from = more ? base.offset : 0;
    const epoch = store.keyEpoch;
    loads++;
    try {
      const result = await store.ask<{ items: RemoteItem[]; hasMore: boolean }>('remote.item.get', { provider: account.provider, kind, offset: from, limit: 50, fresh: fresh || base.fresh });
      if (epoch !== store.keyEpoch) return; // read with a key that has since changed
      const items = from === 0 ? result.items : [...base.items, ...result.items.filter((item) => !base.items.some((known) => known.itemId === item.itemId))];
      pages[kind] = { items, hasMore: result.hasMore, offset: from + result.items.length, loaded: true, problem: '', lastCount: result.items.length, fresh: false };
    } catch (error) {
      if (epoch !== store.keyEpoch) return;
      pages[kind] = { ...base, loaded: true, problem: failureText(error instanceof Error ? error.message : String(error), null, name), lastCount: 0 };
    } finally {
      loads--;
    }
    // A column's order means little over part of a list, so the rest is read.
    const done = pages[kind];
    if (kind === current && loads === 0 && needsTheRest(sortKey, done.hasMore, done.lastCount)) void load(kind, true);
  }

  $effect(() => {
    if (browsable && current !== 'hosters' && !(pages[current]?.loaded) && loads === 0) void load(current);
  });

  // A saved or cleared key can be another account's: everything read with the old key goes (P6).
  let seenEpoch = store.keyEpoch;
  $effect(() => {
    const epoch = store.keyEpoch;
    if (epoch === seenEpoch) return;
    seenEpoch = epoch;
    pages = {};
    selectedId = null;
    files = {};
    for (const kind of kinds) pages[kind] = { ...blank(), fresh: true };
    status = '';
  });
  $effect(() => {
    if (!browsable && Object.keys(pages).length) {
      pages = {};
      selectedId = null;
    }
  });

  // ---- The chosen item's files ----
  interface Files { tree: FileTree; chosen: Set<string>; problem: string; reading: boolean }
  let files = $state<Record<string, Files>>({});
  const filesOf = $derived(selected ? files[selected.itemId] : undefined);

  async function select(item: RemoteItem) {
    selectedId = item.itemId;
    status = '';
    if (files[item.itemId]?.tree.files.length || files[item.itemId]?.reading) return;
    files[item.itemId] = { tree: buildFileTree([]), chosen: new Set(), problem: '', reading: true };
    try {
      const result = await store.ask<{ items: RemoteItem[] }>('remote.item.get', { provider: item.provider, kind: item.kind, itemId: item.itemId });
      const found = result.items?.[0];
      const tree = buildFileTree((found?.files ?? []) as RemoteFile[]);
      const earlier = files[item.itemId]?.chosen ?? new Set<string>();
      // The first time an item's files are shown, they all start chosen.
      files[item.itemId] = { tree, chosen: earlier.size ? restoreChoice(tree, earlier) : new Set(choosableIds(tree)), problem: '', reading: false };
      if (found) updateItem(found);
    } catch (error) {
      files[item.itemId] = { tree: buildFileTree([]), chosen: new Set(), problem: failureText(error instanceof Error ? error.message : String(error), null, name), reading: false };
    }
  }

  function updateItem(item: RemoteItem) {
    const p = pages[item.kind];
    if (!p) return;
    const index = p.items.findIndex((known) => known.itemId === item.itemId);
    if (index >= 0) p.items[index] = { ...item, files: undefined };
  }

  // ---- The destination ----
  let freeDestination = $state(String(store.model.settings?.lastDestinationDirectory || store.model.settings?.defaultDownloadDirectory || ''));
  let lastRemembered = $state('');
  // Settings can arrive after this screen opens (a reload on this page): fill the box until the user types.
  $effect(() => {
    const remembered = String(store.model.settings?.lastDestinationDirectory || store.model.settings?.defaultDownloadDirectory || '');
    if (remembered && (freeDestination === '' || freeDestination === lastRemembered)) freeDestination = remembered;
    lastRemembered = remembered;
  });
  let destinationHint = $state('');
  const destination = $derived(activeJob ? (activeJob.destination ?? '') : freeDestination);

  async function download(all: boolean) {
    if (!selected || acting || !account) return;
    const item = selected;
    const tree = files[item.itemId]?.tree;
    const picked = files[item.itemId]?.chosen ?? new Set<string>();
    let choice = tree && tree.files.length ? choiceOf(tree, picked) : { all: true, fileIds: [] as string[] };
    if (!all && tree && tree.files.length && choice.fileIds.length === 0 && !choice.all) return;
    if (all) choice = { all: true, fileIds: [] };

    acting = true;
    status = 'Sending your choice…';
    statusIsProblem = false;
    try {
      // An add past a job on the list would only return that job and ignore the choice.
      if (activeJob) {
        await store.ask('remote.select', choice.all ? { jobId: activeJob.id, all: true } : { jobId: activeJob.id, fileIds: choice.fileIds });
        status = choice.all ? 'All its files will download.' : 'Your choice will download.';
        return;
      }
      const folder = freeDestination.trim();
      if (folder && !isFullPath(folder)) {
        destinationHint = `Enter a full folder path, such as ${store.texts.destinationExample}, or leave this empty for the default folder.`;
        status = '';
        return;
      }
      destinationHint = '';
      status = 'Adding to the download list…';
      const result = await store.ask<RemoteAddResult>('remote.add', {
        provider: item.provider, itemKind: item.kind, itemId: item.itemId, destination: folder || undefined,
        queueId: store.model.settings?.defaultQueueId ?? 0, startMode: 'startNow', selection: choice.all ? 'all' : 'chosen', fileIds: choice.all ? undefined : choice.fileIds,
      });
      if (folder) void store.saveSettings((settings) => { settings.lastDestinationDirectory = folder; }, '');
      if (downloadStarted(result)) {
        status = `Added '${view(item).name}' to the download list. It starts once ${name} has the files.`;
      } else if (result.note?.trim()) {
        status = result.note;
        statusIsProblem = true;
      } else {
        status = failureText(result.job?.failureMessage, result.job?.failureKind, name);
        statusIsProblem = true;
      }
    } catch (error) {
      status = failureText(error instanceof Error ? error.message : String(error), null, name);
      statusIsProblem = true;
    } finally {
      acting = false;
    }
  }

  async function deleteFromProvider() {
    if (!selected || !selectedJob || !canDeleteFromProvider(selectedJob)) return;
    const item = selected;
    const label = selectedView?.name ?? item.itemId;
    acting = true;
    confirmDelete = false;
    try {
      await store.ask('remote.deleteRemote', { jobId: selectedJob.id, confirmed: true });
      const p = pages[item.kind];
      if (p) p.items = p.items.filter((known) => known.itemId !== item.itemId);
      selectedId = null;
      status = `Deleted '${label}' from ${name}.`;
      statusIsProblem = false;
    } catch (error) {
      status = failureText(error instanceof Error ? error.message : String(error), null, name);
      statusIsProblem = true;
    } finally {
      acting = false;
    }
  }

  const canCheck = $derived(!!account && checkable(jobs, account.provider));

  async function checkNow() {
    if (!account) return;
    const one = selectedJob && actionsOf(selectedJob).checkNow ? selectedJob : undefined;
    const result = await store.attempt('remote.checkNow', one ? { jobId: one.id } : { provider: account.provider });
    if (result) {
      status = `Checking ${name} now. The status above changes when it answers, unless ${name} has asked this app to wait.`;
      statusIsProblem = false;
    }
  }

  // ---- Hosters ----
  let hosters = $state<Hoster[] | null>(null);
  let hosterProblem = $state('');
  let fetchedAt = $state<string | undefined>(undefined);
  let hosterFilter = $state('');
  let downOnly = $state(false);
  let hosterBusy = $state(false);
  async function loadHosters() {
    hosterBusy = true;
    try {
      const result = await store.ask<{ hosters: Hoster[]; problem?: string; fetchedAt?: string }>('remote.hosters', {});
      hosters = result.hosters;
      hosterProblem = result.problem ?? '';
      fetchedAt = result.fetchedAt;
    } catch (error) {
      hosterProblem = failureText(error instanceof Error ? error.message : String(error), null, name);
      hosters = hosters ?? [];
    } finally {
      hosterBusy = false;
    }
  }
  $effect(() => {
    if (current === 'hosters' && browsable && hosters === null) void loadHosters();
  });
  const shownHosters = $derived(filterHosters(hosters ?? [], hosterFilter, downOnly));

  function changeSort(event: Event) {
    sortKey = (event.currentTarget as HTMLSelectElement).value as ItemSortKey;
    if (needsTheRest(sortKey, page.hasMore, page.lastCount) && loads === 0) void load(current, true);
  }

  const toneClass = { active: 'tone-active', done: 'tone-done', failed: 'tone-failed', waiting: 'tone-waiting', neutral: 'text-muted' } as const;
</script>

<div class="mx-auto max-w-5xl px-4 py-4">
  {#if !store.has('remote')}
    <p class="card p-4 text-[13px] text-muted">This server has no download provider.</p>
  {:else if !account}
    <p class="card p-4 text-[13px] text-muted">Reading the account…</p>
  {:else}
    <h1 class="mb-3 text-[18px] font-semibold">My {name}</h1>

    {#if !browsable}
      <div class="card p-4 text-[13px]">
        <p class="mb-2">{account.keyUnreadable ? `The ${name} API key is saved, but the server can't read it.` : account.hasKey ? `${name} doesn't offer a list of what is on its account.` : `No usable ${name} API key is saved. Add it in Options.`}</p>
        {#if !account.hasKey || account.keyUnreadable}
          <p class="mb-3 text-muted">Add the key under Options, {name} account, to see what is in your account here.</p>
          <a class="btn-primary inline-flex" href="#/settings" onclick={() => (store.settingsPage = `provider:${account.provider}`)}>Open {name} account</a>
        {/if}
      </div>
    {:else}
      <div class="mb-4 flex gap-1 overflow-x-auto border-b border-divider" role="tablist">
        {#each kinds as kind (kind)}
          <button role="tab" aria-selected={current === kind} class="-mb-px shrink-0 border-b-2 px-3 py-2 text-[13px] {current === kind ? 'border-accent font-semibold' : 'border-transparent text-muted hover:text-text'}" onclick={() => { tab = kind; selectedId = null; status = ''; }}>{kindHeading(kind)}</button>
        {/each}
        <button role="tab" aria-selected={current === 'hosters'} class="-mb-px shrink-0 border-b-2 px-3 py-2 text-[13px] {current === 'hosters' ? 'border-accent font-semibold' : 'border-transparent text-muted hover:text-text'}" onclick={() => { tab = 'hosters'; selectedId = null; status = ''; }}>Hosters</button>
      </div>

      {#if current !== 'hosters'}
        <div class="mb-3 flex flex-wrap items-end gap-2">
          <div>
            <label class="label" for="item-sort">Sort by</label>
            <div class="flex gap-1">
              <select id="item-sort" class="field" value={sortKey} onchange={changeSort}>
                {#each itemSortLabels as [key, label] (key)}<option value={key}>{label}</option>{/each}
              </select>
              <button class="btn" disabled={sortKey === 'default'} aria-label={ascending ? 'Ascending' : 'Descending'} title={ascending ? 'Ascending' : 'Descending'} onclick={() => (ascending = !ascending)}><Icon name={ascending ? 'chevron-up' : 'chevron-down'} size={14} /></button>
            </div>
          </div>
          <button class="btn" disabled={loads > 0} onclick={() => load(current, false, true)}><Icon name="refresh" size={15} class="text-icon-info" /> Refresh</button>
          <button class="btn" disabled={acting || !canCheck} title={canCheck ? `Ask ${name} about your waiting items now, without waiting for the next check` : `Nothing is waiting on ${name}, so there is nothing to check`} onclick={checkNow}>Check now</button>
          <span class="hint ml-auto" aria-live="polite">{page.loaded ? countedItems(page.items.length, page.hasMore) : ''}</span>
        </div>
        {#if page.problem}<p class="mb-2 text-[13px] text-error" role="alert">{page.problem}{page.items.length === 0 ? ` This list could not be read. Select Refresh to ask ${name} again.` : ''}</p>{/if}

        <ul class="card divide-y divide-divider" aria-label={kindHeading(current)}>
          {#each shown as item (item.itemId)}
            {@const v = view(item)}
            <li>
              <button class="flex w-full flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2 text-left text-[13px] {selectedId === item.itemId ? 'bg-selected' : 'hover:bg-hover'}" aria-pressed={selectedId === item.itemId} onclick={() => select(item)}>
                <Icon name="cloud" size={16} class="text-icon-info" />
                <span class="min-w-0 flex-1">
                  <span class="block truncate font-semibold" title={v.name}>{v.name}</span>
                  <span class="block text-[12px] text-muted">{v.details}</span>
                  {#if v.problem}<span class="block text-[12px] text-error">{v.problem}</span>{/if}
                </span>
                <span class="shrink-0 text-[12.5px] {toneClass[v.tone]}">{v.status}</span>
              </button>
            </li>
          {:else}
            <li class="px-3 py-6 text-center text-[13px] text-muted">{loads > 0 && !page.loaded ? 'Loading…' : page.problem ? '' : emptyKind(current, name)}</li>
          {/each}
        </ul>
        {#if page.hasMore}<button class="btn mt-2" disabled={loads > 0} onclick={() => load(current, true)}>Load more</button>{/if}

        {#if selected && selectedView}
          <section class="card mt-4 p-3" aria-label="The chosen item">
            <h2 class="truncate text-[14px] font-semibold" title={selectedView.name}>{selectedView.name}</h2>
            <p class="text-[12.5px] text-muted">{selectedView.details}</p>
            <p class="mb-2 text-[12.5px] {toneClass[selectedView.tone]}">{selectedView.status}</p>
            {#if selectedView.problem}<p class="mb-2 text-[12.5px] text-error" role="alert">{selectedView.problem}</p>{/if}

            {#if filesOf?.reading}
              <p class="mb-2 text-[13px] text-muted">Asking {name} for the files…</p>
            {:else if filesOf?.problem}
              <p class="mb-2 text-[13px] text-error" role="alert">{filesOf.problem}</p>
            {:else if filesOf && filesOf.tree.files.length > 0}
              <RemoteFileTree tree={filesOf.tree} bind:chosen={files[selected.itemId].chosen} disabled={acting} label="Files of the selected item" />
            {:else if filesOf}
              <p class="mb-2 text-[13px] text-muted">{selected.stage === 'ready' ? `${name} lists no files for this.` : !selectedView.canDownload ? `This item failed on ${name}, so there are no files to download.` : `${name} doesn't list the files yet. Download all still works: it starts when ${name} has them.`}</p>
            {/if}

            <div class="mt-3 max-w-xl">
              <FolderField id="item-dest" label="Destination folder" bind:value={freeDestination} disabled={!!activeJob} hint={activeJob ? (activeJob.state === 'localStarted' ? 'These downloads already have this folder. New files you select go there too.' : 'This item is already on the download list with this folder.') : destinationHint || 'Empty uses the default download folder.'} />
              {#if activeJob && activeJob.destination}<p class="hint mt-1 break-all font-mono">{activeJob.destination}</p>{/if}
            </div>

            <div class="mt-3 flex flex-wrap gap-2">
              <button class="btn-primary" disabled={acting || !selectedView.canDownload || !filesOf || filesOf.chosen.size === 0} title="Starts once {name} has the files." onclick={() => download(false)}>Download selected</button>
              <button class="btn" disabled={acting || !selectedView.canDownload} title="Starts once {name} has the files." onclick={() => download(true)}>Download all</button>
              {#if canDeleteFromProvider(selectedJob)}<button class="btn-danger" disabled={acting} onclick={() => (confirmDelete = true)}>Delete from {name}…</button>{/if}
            </div>
            {#if status}<p class="mt-2 text-[12.5px] {statusIsProblem ? 'text-error' : 'text-muted'}" role="status">{status}</p>{/if}
          </section>
        {/if}
      {:else}
        <div class="mb-3 flex flex-wrap items-end gap-2">
          <div class="min-w-56 flex-1"><label class="label" for="hoster-filter">Search hosts</label><input id="hoster-filter" class="field" bind:value={hosterFilter} placeholder="Name or domain" /></div>
          <label class="flex items-center gap-2 pb-2 text-[13px]"><input type="checkbox" bind:checked={downOnly} /> Down only</label>
          <button class="btn" disabled={hosterBusy} title="Read the list again. It is kept for an hour, so this asks the provider only when it is older." onclick={loadHosters}><Icon name="refresh" size={15} class="text-icon-info" /> Reload</button>
        </div>
        {#if hosterProblem}<p class="mb-2 text-[13px] text-error" role="alert">{hosterProblem}{fetchedAt ? ` Read at ${dateTime(fetchedAt)}.` : ''}</p>
        {:else if hosters && hosters.length}<p class="hint mb-2">{hostersUp(hosters)}{fetchedAt ? ` Read at ${dateTime(fetchedAt)}.` : ''}</p>{/if}
        <ul class="card divide-y divide-divider" aria-label="File hosts">
          {#each shownHosters as hoster (hoster.name)}
            <li class="flex items-start gap-3 px-3 py-2 text-[13px]">
              <span class="min-w-0 flex-1">
                <span class="block truncate font-semibold">{hoster.name}</span>
                {#if hosterDetails(hoster)}<span class="block text-[12px] text-muted">{hosterDetails(hoster)}</span>{/if}
                {#if hoster.note}<span class="block text-[12px] text-muted">{hoster.note}</span>{/if}
              </span>
              <span class="shrink-0 {hoster.available ? 'tone-done' : 'tone-failed'}">{hoster.available ? 'Up' : 'Down'}</span>
            </li>
          {:else}
            <li class="px-3 py-6 text-center text-[13px] text-muted">{hosterBusy ? 'Reading the host list…' : hosters && hosters.length ? 'No host matches.' : 'No hosts.'}</li>
          {/each}
        </ul>
      {/if}
    {/if}
  {/if}
</div>

{#if confirmDelete && selected}
  <Confirm title="Delete from {name}?" message={deleteQuestion(selectedView?.name ?? '', name)} primary="Delete from {name}" danger busy={acting} onclose={() => (confirmDelete = false)} onconfirm={deleteFromProvider} />
{/if}
