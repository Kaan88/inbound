<script lang="ts">
  import { flip } from 'svelte/animate';
  import { fade } from 'svelte/transition';
  import Dialog from '../components/Dialog.svelte';
  import Icon from '../components/Icon.svelte';
  import ProgressBar from '../components/ProgressBar.svelte';
  import { archiveShort, archiveTone, bytes, duration, dateTime, percent, showsBar, speed, stateWord, statusText, timeSpanSeconds, tone } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet, Job, RemoteJob } from '../lib/types';
  import { buildRows, selectedJobIds, type Row, type SortKey, type ViewKey } from '../lib/views';
  import ArchivePassword from './ArchivePassword.svelte';
  import JobDetails from './JobDetails.svelte';
  import PackageDialog from './PackageDialog.svelte';
  import RemoteActions from './RemoteActions.svelte';

  let { view, search, onadd }: { view: ViewKey; search: string; onadd: () => void } = $props();

  let selected = $state(new Set<string>());
  let expanded = $state(new Set<string>());
  let sort = $state<SortKey>('added');
  let lastClicked = $state<string | null>(null);
  let confirmDelete = $state<{ ids: string[]; completedOnly: boolean } | null>(null);
  let deleteFile = $state(false);
  let details = $state<string | null>(null);
  let packageOpen = $state<string | null>(null);
  // A package with just one listed file opens that file's own window (ProgressDetailRules.OnlyFile).
  function openPackage(group: { id: string; jobIds: string[] }) {
    const listed = group.jobIds.filter((id) => store.model.jobs[id]);
    if (listed.length === 1) details = listed[0];
    else packageOpen = group.id;
  }
  let remoteOpen = $state<RemoteJob | null>(null);
  let passwordFor = $state<ArchiveSet | null>(null);

  const rows = $derived(buildRows(store.model, view, search, expanded, sort));
  const selectedIds = $derived(selectedJobIds(rows, selected));
  const selectedJobs = $derived(selectedIds.map((id) => store.model.jobs[id]).filter((job): job is Job => !!job));
  const canResume = $derived(selectedJobs.some((job) => job.canResume || job.state === 'failed'));
  const canPause = $derived(selectedJobs.some((job) => job.canPause));
  const completedIds = $derived(Object.values(store.model.jobs).filter((job) => job.state === 'completed').map((job) => job.id));
  const archiveByJob = $derived.by(() => {
    const map = new Map<string, ArchiveSet>();
    for (const set of Object.values(store.model.archives)) for (const id of set.jobIds) map.set(id, set);
    return map;
  });
  const showExtraction = $derived(archiveByJob.size > 0);
  const needPassword = $derived(Object.values(store.model.archives).filter((set) => set.needsPassword && !set.hasPassword && set.state !== 'completed'));
  const defaultQueue = $derived(store.model.settings?.queues.find((queue) => queue.id === (store.model.settings?.defaultQueueId ?? 0)));

  // A toast's Show opens that download.
  $effect(() => {
    if (store.showJob) {
      details = store.showJob;
      store.showJob = null;
    }
  });

  // A row that left the view leaves the selection.
  $effect(() => {
    const present = new Set(rows.map((row) => row.id));
    let changed = false;
    for (const id of selected) if (!present.has(id)) changed = true;
    if (changed) selected = new Set([...selected].filter((id) => present.has(id)));
  });

  /** A touch screen has no double-click: there a tap opens the download, and the box on the left selects. */
  function touchFirst(): boolean {
    try {
      return matchMedia('(pointer: coarse)').matches && !matchMedia('(pointer: fine)').matches;
    } catch {
      return false;
    }
  }

  function click(row: Row, event: MouseEvent) {
    if (touchFirst() && !event.shiftKey && !event.ctrlKey && !event.metaKey) {
      open(row);
      return;
    }
    const next = new Set(selected);
    if (event.shiftKey && lastClicked) {
      const from = rows.findIndex((r) => r.id === lastClicked);
      const to = rows.findIndex((r) => r.id === row.id);
      if (from >= 0 && to >= 0) {
        for (let i = Math.min(from, to); i <= Math.max(from, to); i++) next.add(rows[i].id);
        selected = next;
        return;
      }
    }
    if (event.ctrlKey || event.metaKey) {
      if (next.has(row.id)) next.delete(row.id);
      else next.add(row.id);
    } else {
      next.clear();
      next.add(row.id);
    }
    lastClicked = row.id;
    selected = next;
  }

  function toggle(row: Row) {
    const next = new Set(selected);
    if (next.has(row.id)) next.delete(row.id);
    else next.add(row.id);
    lastClicked = row.id;
    selected = next;
  }

  function toggleAll() {
    selected = selected.size === rows.length ? new Set() : new Set(rows.map((row) => row.id));
  }

  function toggleExpanded(id: string) {
    const next = new Set(expanded);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expanded = next;
  }

  function open(row: Row) {
    if (row.kind === 'job') details = row.job.id;
    else if (row.kind === 'package') toggleExpanded(row.group.id);
    else if (row.kind === 'remote') remoteOpen = row.remote;
    else if (row.kind === 'archive' && row.set.needsPassword) passwordFor = row.set;
  }

  async function each(type: string, jobs: Job[], done: string) {
    let ok = 0;
    for (const job of jobs) {
      const kind = type === 'job.resume' && job.state === 'failed' ? 'job.retry' : type;
      if (await store.attempt(kind, { jobId: job.id })) ok++;
    }
    if (ok) store.toast(`${done} ${ok} download${ok === 1 ? '' : 's'}.`);
  }

  async function remove() {
    if (!confirmDelete) return;
    const { ids } = confirmDelete;
    const file = deleteFile;
    confirmDelete = null;
    deleteFile = false;
    const result = await store.attempt<{ ok: boolean }>('job.remove', { jobId: ids[0], jobIds: ids, deleteFile: file });
    if (result?.ok) {
      store.toast(`Deleted ${ids.length} download${ids.length === 1 ? '' : 's'}${file ? ' and their files' : ''}.`);
      selected = new Set();
    }
  }

  function remaining(job: Job): string {
    if (tone(job.state) !== 'active') return '';
    return duration(timeSpanSeconds(job.estimatedRemaining));
  }

  function description(job: Job): string {
    if (job.failure?.message && job.state === 'failed') return job.failure.message;
    return job.statusNote ?? job.description ?? '';
  }
</script>

<!-- Toolbar: Add | Resume, Pause, Stop all | Delete, Delete completed | Start queue, Stop queue -->
<div class="flex items-stretch gap-0.5 overflow-x-auto border-b border-divider bg-chrome px-2 py-1" role="toolbar" aria-label="Download actions">
  <button class="toolbtn" onclick={onadd}><Icon name="add" class="text-icon-info" /><span>Add links</span></button>
  <span class="toolsep"></span>
  <button class="toolbtn" disabled={!canResume} onclick={() => each('job.resume', selectedJobs.filter((j) => j.canResume || j.state === 'failed'), 'Resumed')}><Icon name="play" class="text-icon-start" /><span>Resume</span></button>
  <button class="toolbtn" disabled={!canPause} onclick={() => each('job.pause', selectedJobs.filter((j) => j.canPause), 'Paused')}><Icon name="pause" class="text-icon-pause" /><span>Pause</span></button>
  <button class="toolbtn" onclick={() => store.attempt('jobs.pauseAll', {}, 'Every download was paused.')}><Icon name="stop" class="text-icon-pause" /><span>Stop all</span></button>
  <span class="toolsep"></span>
  <button class="toolbtn" disabled={selectedIds.length === 0} onclick={() => { deleteFile = false; confirmDelete = { ids: selectedIds, completedOnly: false }; }}><Icon name="trash" class="text-icon-danger" /><span>Delete</span></button>
  <button class="toolbtn" disabled={completedIds.length === 0} onclick={() => { deleteFile = false; confirmDelete = { ids: completedIds, completedOnly: true }; }}><Icon name="sweep" class="text-icon-danger" /><span>Delete completed</span></button>
  {#if defaultQueue}
    <span class="toolsep"></span>
    <button class="toolbtn" disabled={defaultQueue.isRunning} onclick={() => store.attempt('queue.action', { queueId: defaultQueue.id, action: 'start' }, `${defaultQueue.name} queue started.`)}><Icon name="play" class="text-icon-start" /><span>Start queue</span></button>
    <button class="toolbtn" disabled={!defaultQueue.isRunning} onclick={() => store.attempt('queue.action', { queueId: defaultQueue.id, action: 'stop' }, `${defaultQueue.name} queue stopped.`)}><Icon name="stop" class="text-icon-pause" /><span>Stop queue</span></button>
  {/if}
</div>

{#if needPassword.length > 0}
  <div class="flex flex-wrap items-center gap-2 border-b border-divider bg-highlight px-3 py-1.5 text-[13px]" role="status" transition:fade={{ duration: 150 }}>
    <Icon name="key" size={16} class="text-icon-pause" />
    <span class="flex-1">{needPassword.length === 1 ? `${needPassword[0].name} needs a password to extract.` : `${needPassword.length} archives need a password to extract.`}</span>
    <button class="btn py-0.5" onclick={() => (passwordFor = needPassword[0])}>Enter password</button>
  </div>
{/if}

<div class="list-grid sticky top-0 z-10 hidden border-b border-divider bg-chrome text-[12px] font-semibold md:grid" class:with-extraction={showExtraction} role="row">
  <span class="flex items-center justify-center"><input type="checkbox" aria-label="Select all" checked={rows.length > 0 && selected.size === rows.length} onchange={toggleAll} /></span>
  <button class="header-cell text-left" onclick={() => (sort = 'name')} aria-pressed={sort === 'name'}>File name {sort === 'name' ? '▾' : ''}</button>
  <button class="header-cell text-right" onclick={() => (sort = 'size')} aria-pressed={sort === 'size'}>Size {sort === 'size' ? '▾' : ''}</button>
  <button class="header-cell text-left" onclick={() => (sort = 'status')} aria-pressed={sort === 'status'}>Status {sort === 'status' ? '▾' : ''}</button>
  {#if showExtraction}<span class="header-cell">Extraction</span>{/if}
  <span class="header-cell text-right">Time left</span>
  <span class="header-cell text-right">Transfer rate</span>
  <button class="header-cell hidden text-left lg:block" onclick={() => (sort = 'added')} aria-pressed={sort === 'added'}>Added {sort === 'added' ? '▾' : ''}</button>
  <span class="header-cell hidden lg:block">Description</span>
</div>

<div role="grid" aria-label="Downloads" aria-multiselectable="true" class="pb-24 md:pb-6">
  {#each rows as row (row.id)}
    <div
      animate:flip={{ duration: 180 }}
      class="list-row group border-b border-divider/60 text-[12.5px] {selected.has(row.id) ? 'bg-selected' : 'hover:bg-hover'}"
      class:with-extraction={showExtraction}
      role="row"
      aria-selected={selected.has(row.id)}
      tabindex="0"
      onclick={(event) => click(row, event)}
      ondblclick={() => open(row)}
      onkeydown={(event) => { if (event.key === 'Enter') open(row); if (event.key === ' ') { event.preventDefault(); toggle(row); } }}
    >
      <span class="cell-check flex items-center justify-center">
        {#if row.kind === 'job' || row.kind === 'package'}
          <input type="checkbox" aria-label="Select" checked={selected.has(row.id)} onclick={(event) => event.stopPropagation()} onchange={() => toggle(row)} />
        {/if}
      </span>

      {#if row.kind === 'remote'}
        {@const remote = row.remote}
        <span class="cell-name flex min-w-0 items-center gap-1.5 italic">
          <Icon name="cloud" size={16} class="shrink-0 text-icon-info" />
          <span class="truncate" title={remote.name ?? remote.displayHost}>{remote.name ?? remote.displayHost ?? 'Waiting at the provider'}</span>
        </span>
        <span class="cell-size"></span>
        <span class="cell-status truncate {remote.state === 'failed' ? 'tone-failed' : remote.state === 'blocked' || remote.state === 'awaitingSelection' || remote.approvalNeeded ? 'tone-waiting' : 'tone-active'}">{remote.status}</span>
        {#if showExtraction}<span class="cell-extract"></span>{/if}
        <span class="cell-eta"></span>
        <span class="cell-rate"></span>
        <span class="cell-added hidden text-muted lg:block">{dateTime(remote.createdAt)}</span>
        <span class="cell-desc truncate text-muted">
          {remote.providerName}{remote.stage ? `: ${remote.stage}` : ''}{remote.progress > 0 && remote.progress < 1 ? ` ${Math.round(remote.progress * 100)}% (provider's)` : ''}{remote.failureMessage ? ` · ${remote.failureMessage}` : ''}
          <button class="ml-1 rounded border border-divider px-1.5 py-0 text-[11px] not-italic text-text hover:bg-hover" onclick={(event) => { event.stopPropagation(); remoteOpen = remote; }}>Actions</button>
        </span>
      {:else if row.kind === 'archive'}
        {@const set = row.set}
        <span class="cell-name flex min-w-0 items-center gap-1.5">
          <Icon name="archive" size={16} class="shrink-0 text-icon-media" />
          <span class="truncate" title={set.firstVolume}>{set.name}</span>
        </span>
        <span class="cell-size"></span>
        <span class="cell-status truncate tone-{archiveTone(set)}" title={set.stateText}>{archiveShort(set)}</span>
        {#if showExtraction}<span class="cell-extract"></span>{/if}
        <span class="cell-eta"></span>
        <span class="cell-rate"></span>
        <span class="cell-added hidden text-muted lg:block">{dateTime(set.updatedAt)}</span>
        <span class="cell-desc truncate text-muted" title={set.stateText}>{set.stateText}</span>
      {:else if row.kind === 'package'}
        {@const summary = row.summary}
        <span class="cell-name flex min-w-0 items-center gap-1">
          <button class="rounded p-0.5 hover:bg-hover" aria-label={row.expanded ? 'Collapse' : 'Expand'} aria-expanded={row.expanded} onclick={(event) => { event.stopPropagation(); toggleExpanded(row.group.id); }}>
            <Icon name={row.expanded ? 'chevron-down' : 'chevron-right'} size={14} />
          </button>
          <Icon name="package" size={16} class="shrink-0 text-icon-media" />
          <span class="truncate font-semibold" title={row.group.name}>{row.group.name}</span>
          <span class="shrink-0 text-[11px] text-muted">{summary.done}/{summary.count}</span>
          <button class="shrink-0 rounded border border-divider px-1.5 text-[11px] hover:bg-hover" onclick={(event) => { event.stopPropagation(); openPackage(row.group); }}>Progress details</button>
        </span>
        <span class="cell-size tabular text-right">{bytes(summary.totalBytes)}</span>
        <span class="cell-status min-w-0">
          <span class="block truncate tone-{tone(summary.state)}">{stateWord(summary.state)}{summary.progress !== null && summary.state !== 'completed' && summary.progress > 0 ? ` ${summary.progress.toFixed(0)}%` : ''}</span>
          {#if (tone(summary.state) === 'active' || summary.state === 'paused') && summary.progress}<span class="mt-0.5 block"><ProgressBar value={summary.progress} label="Package progress" /></span>{/if}
        </span>
        {#if showExtraction}<span class="cell-extract"></span>{/if}
        <span class="cell-eta"></span>
        <span class="cell-rate tabular text-right">{speed(summary.bytesPerSecond)}</span>
        <span class="cell-added hidden lg:block"></span>
        <span class="cell-desc truncate text-muted">{summary.count} files</span>
      {:else}
        {@const job = row.job}
        {@const set = archiveByJob.get(job.id)}
        <span class="cell-name flex min-w-0 items-center gap-1.5 {row.depth ? 'pl-6' : ''}">
          <Icon name="file" size={15} class="shrink-0 text-icon-neutral" />
          <span class="truncate" title={job.fileName}>{job.fileName || (job.addressHidden ? '' : job.url)}</span>
        </span>
        <span class="cell-size tabular text-right">{bytes(job.totalBytes)}</span>
        <span class="cell-status min-w-0">
          <span class="block truncate tone-{tone(job.state)}" title={job.failure?.message ?? ''}>{statusText(job)}</span>
          {#if showsBar(job)}<span class="mt-0.5 block"><ProgressBar value={percent(job)} /></span>{/if}
        </span>
        {#if showExtraction}
          <span class="cell-extract truncate {set ? `tone-${archiveTone(set)}` : ''}" title={set?.stateText ?? ''}>
            {#if set}
              {archiveShort(set)}
              {#if set.needsPassword && !set.hasPassword}
                <button class="ml-1 align-middle" aria-label="Enter the archive password" onclick={(event) => { event.stopPropagation(); passwordFor = set; }}><Icon name="key" size={13} /></button>
              {/if}
            {/if}
          </span>
        {/if}
        <span class="cell-eta tabular text-right">{remaining(job)}</span>
        <span class="cell-rate tabular text-right">{tone(job.state) === 'active' ? speed(job.bytesPerSecond) : ''}</span>
        <span class="cell-added hidden text-muted lg:block">{dateTime(job.createdAt)}</span>
        <span class="cell-desc truncate text-muted" title={description(job)}>{description(job)}</span>
      {/if}
    </div>
  {:else}
    <div class="px-6 py-16 text-center text-muted">
      {#if store.model.snapshots === 0}
        Connecting to the server…
      {:else if search}
        Nothing matches “{search}”.
      {:else}
        <p class="mb-3">No downloads here yet.</p>
        <button class="btn-primary" onclick={onadd}><Icon name="add" size={16} /> Add links</button>
      {/if}
    </div>
  {/each}
</div>

{#if confirmDelete}
  <Dialog title={confirmDelete.completedOnly ? 'Delete completed downloads' : 'Delete downloads'} onclose={() => (confirmDelete = null)}>
    <p class="mb-3 text-[13px]">
      {confirmDelete.completedOnly ? `Take the ${confirmDelete.ids.length} completed download${confirmDelete.ids.length === 1 ? '' : 's'} off the list?` :
        `Delete ${confirmDelete.ids.length} download${confirmDelete.ids.length === 1 ? '' : 's'} from the list?`}
    </p>
    <label class="flex items-center gap-2 text-[13px]">
      <input type="checkbox" bind:checked={deleteFile} />
      Delete the file{confirmDelete.ids.length === 1 ? '' : 's'} too
    </label>
    <p class="hint mt-1 pl-6">Left unticked, files already on the server's disk stay where they are. Unfinished parts are removed either way.</p>
    {#snippet footer()}
      <button class="btn-danger" onclick={remove}>Delete</button>
      <button class="btn" onclick={() => (confirmDelete = null)}>Cancel</button>
    {/snippet}
  </Dialog>
{/if}

{#if packageOpen}
  <PackageDialog groupId={packageOpen} onclose={() => (packageOpen = null)} onopenfile={(id) => { details = id; }} />
{/if}

{#if details}
  <JobDetails jobId={details} set={archiveByJob.get(details)} onclose={() => (details = null)} onpassword={(set) => { details = null; passwordFor = set; }} />
{/if}


{#if remoteOpen}
  <RemoteActions remote={store.model.remoteJobs[remoteOpen.id] ?? remoteOpen} onclose={() => (remoteOpen = null)} />
{/if}

{#if passwordFor}
  <ArchivePassword set={store.model.archives[passwordFor.id] ?? passwordFor} onclose={() => (passwordFor = null)} />
{/if}

<style>
  .toolbtn {
    display: inline-flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2px;
    min-width: 64px;
    padding: 4px 8px;
    border-radius: 4px;
    font-size: 11.5px;
    white-space: nowrap;
    background: transparent;
  }
  .toolbtn:hover:not(:disabled) { background: var(--hover); }
  .toolbtn:disabled { opacity: 0.4; cursor: default; }
  .toolsep { width: 1px; margin: 6px 4px; background: var(--divider); flex-shrink: 0; }
  @media (max-width: 767px) {
    .toolbtn { min-width: 44px; padding: 6px; }
    /* Icons only on a phone; the words stay for screen readers and as the button's name. */
    .toolbtn { position: relative; }
    .toolbtn span { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  }

  .list-grid, .list-row {
    display: grid;
    align-items: center;
    column-gap: 10px;
    padding: 0 10px 0 4px;
  }
  .list-grid { grid-template-columns: 28px minmax(180px, 1fr) 80px 150px 80px 90px; height: 30px; }
  .list-grid.with-extraction { grid-template-columns: 28px minmax(180px, 1fr) 80px 150px 120px 80px 90px; }
  .header-cell { padding: 0; background: transparent; white-space: nowrap; }

  /* A phone: each download is two lines, name and size over status and speed. The header keeps only the box that
     selects all and the name. */
  @media (max-width: 767px) {
    .list-grid { grid-template-columns: 28px 1fr; }
    .list-grid > :nth-child(n + 3) { display: none; }
  }
  .list-row {
    grid-template-columns: 28px 1fr auto;
    grid-template-areas: 'check name size' 'check status rate' 'check desc desc';
    padding-top: 6px;
    padding-bottom: 6px;
    row-gap: 2px;
    cursor: default;
    /* A double-click opens the download; it shouldn't also select the words. */
    user-select: none;
    -webkit-user-select: none;
  }
  .list-row .cell-check { grid-area: check; }
  .list-row .cell-name { grid-area: name; }
  .list-row .cell-status { grid-area: status; }
  .list-row .cell-rate { grid-area: rate; font-size: 11.5px; color: var(--muted); }
  .list-row .cell-size { grid-area: size; font-size: 11.5px; color: var(--muted); text-align: right; }
  .list-row .cell-desc { grid-area: desc; font-size: 11.5px; }
  .list-row .cell-eta, .list-row .cell-extract, .list-row .cell-added { display: none; }
  .list-row .cell-desc:empty { display: none; }

  @media (min-width: 768px) {
    .list-row {
      grid-template-areas: none;
      grid-template-columns: 28px minmax(180px, 1fr) 80px 150px 80px 90px;
      min-height: 30px;
      padding-top: 2px;
      padding-bottom: 2px;
    }
    .list-row.with-extraction { grid-template-columns: 28px minmax(180px, 1fr) 80px 150px 120px 80px 90px; }
    .list-row > * { grid-area: auto !important; }
    .list-row .cell-size, .list-row .cell-eta, .list-row .cell-extract { display: block; }
    .list-row .cell-rate, .list-row .cell-size { font-size: inherit; color: inherit; }
    .list-row .cell-desc { display: none; }
  }

  @media (min-width: 1024px) {
    .list-grid, .list-row { grid-template-columns: 28px minmax(200px, 2fr) 80px 150px 80px 90px 130px minmax(120px, 1fr); }
    .list-grid.with-extraction, .list-row.with-extraction { grid-template-columns: 28px minmax(200px, 2fr) 80px 150px 120px 80px 90px 130px minmax(120px, 1fr); }
    .list-row .cell-desc, .list-row .cell-added { display: block; font-size: inherit; }
  }
</style>
