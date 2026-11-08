<script lang="ts">
  import AskDialog from '../components/AskDialog.svelte';
  import Dialog from '../components/Dialog.svelte';
  import ProgressBar from '../components/ProgressBar.svelte';
  import SegmentMap from '../components/SegmentMap.svelte';
  import {
    barValue, cancelPackageText, cancelTargets, detailRows, fileToShow, mergeSegments, packageActionNotice, packageFigures, pauseTargets,
    pollsSegments, resumeTargets, sortedByName, splitPath,
  } from '../lib/details';
  import { bytes, percent, stateWord, tone } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { Job, Segment } from '../lib/types';
  import PackagePassword from './PackagePassword.svelte';

  // The package window (D15): a summary line, the overall bar, the files sorted by name, the selected file's details,
  // the package's archive password, and Pause all, Resume all and Cancel package.
  let { groupId, onclose, onopenfile }: { groupId: string; onclose: () => void; onopenfile: (jobId: string) => void } = $props();

  const group = $derived(store.model.groups.find((g) => g.id === groupId));
  const members = $derived(sortedByName((group?.jobIds ?? []).map((id) => store.model.jobs[id]).filter((job): job is Job => !!job)));
  const figures = $derived(packageFigures(members));
  const allDone = $derived(members.length > 0 && figures.completed === members.length);
  const connected = $derived(store.connection === 'open');

  let picked = $state<string | null>(null);
  const shownId = $derived(fileToShow(members, picked));
  const shown = $derived(members.find((m) => m.id === shownId));
  const rows = $derived(shown ? detailRows(shown) : []);

  let notice = $state('');
  let confirmCancel = $state(false);
  let busy = $state(false);

  let segments = $state<Segment[]>([]);
  const highWater = new Map<number, number>();
  let loading = false;
  let segmentsFor = '';

  async function loadSegments() {
    const current = shown;
    if (loading || !connected || !current || !pollsSegments(current)) return;
    loading = true;
    try {
      const saved = await store.ask<Segment[]>('job.segments', { jobId: current.id });
      const live = await store.ask<import('../lib/types').ConnectionInfo[]>('job.connections', { jobId: current.id });
      if (shown?.id === current.id) segments = mergeSegments(Array.isArray(saved) ? saved : [], Array.isArray(live) ? live : [], current, highWater);
    } catch {
      // Only a display; the next second looks again.
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    if (shownId !== segmentsFor) {
      segmentsFor = shownId ?? '';
      segments = [];
      highWater.clear();
    }
    const timer = setInterval(() => void loadSegments(), 1000);
    void loadSegments();
    return () => clearInterval(timer);
  });

  async function action(name: 'pause' | 'resume' | 'cancel') {
    busy = true;
    try {
      const result = await store.ask<{ ok: boolean; error?: string; failedJobs?: Record<string, string>; leftCancelled?: number }>('group.action', { groupId, action: name });
      notice = packageActionNotice(result);
    } catch (error) {
      notice = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }

  async function copyFolder() {
    const first = members.find((m) => m.finalFilePath);
    if (!first) return;
    try {
      await navigator.clipboard.writeText(splitPath(first.finalFilePath).directory);
      store.toast('The folder path was copied.');
    } catch {
      notice = 'The browser did not allow copying.';
    }
  }

  const title = $derived(group ? group.name : 'Package removed');
  const pauseOn = $derived(pauseTargets(members).length > 0);
  const resumeOn = $derived(group ? resumeTargets(group.id, members).length > 0 : false);
  const cancelOn = $derived(cancelTargets(members).length > 0);
</script>

<Dialog title={allDone ? `Package complete — ${title}` : `Package status — ${title}`} {onclose} wide>
  <div class="text-[17px] font-semibold">{title}</div>
  {#if !group}
    <p class="mt-1 text-[13px] text-muted">This package no longer exists. Its files are listed individually in the main window.</p>
  {:else}
    <div class="text-[13px] text-muted">{figures.summary}</div>
    <div class="my-2"><ProgressBar value={figures.bar} height={10} label="Package progress" /></div>
    <div class="tabular text-[13px] text-muted">{figures.detail}</div>

    <ul class="mt-3 max-h-56 overflow-y-auto rounded border border-divider" aria-label="Files in this package">
      {#each members as job (job.id)}
        <li class="border-b border-divider/60 last:border-b-0">
          <button
            class="grid w-full grid-cols-[1fr_auto] gap-x-3 gap-y-1 px-2 py-1.5 text-left text-[13px] {job.id === shownId ? 'bg-selected' : 'hover:bg-hover'}"
            aria-pressed={job.id === shownId}
            title="Double-click for this file's own window"
            onclick={() => (picked = job.id)}
            ondblclick={() => onopenfile(job.id)}
          >
            <span class="truncate">{job.fileName}</span>
            <span class="tabular text-right text-[12px] text-muted"><span class="tone-{tone(job.state)}">{stateWord(job.state)}</span> · {job.totalBytes && job.totalBytes > 0 ? `${bytes(job.receivedBytes)} / ${bytes(job.totalBytes)}` : bytes(job.receivedBytes) || '0 B'}</span>
            <span class="col-span-2"><ProgressBar value={barValue(job)} label="{job.fileName} progress" /></span>
          </button>
        </li>
      {:else}
        <li class="px-3 py-4 text-center text-[13px] text-muted">No files in this package yet.</li>
      {/each}
    </ul>

    <div class="mt-3 rounded border border-divider p-3">
      {#if shown}
        <div class="mb-2 flex items-center gap-2">
          <div class="min-w-0 flex-1 truncate text-[13px] font-semibold">{shown.fileName || 'Download'}</div>
          <button class="btn py-0.5" onclick={() => onopenfile(shown.id)}>Open this file</button>
        </div>
        {#if segments.length > 0 && shown.totalBytes}
          <SegmentMap {segments} total={shown.totalBytes} />
        {:else}
          <ProgressBar value={barValue(shown)} height={14} />
        {/if}
        <dl class="mt-2 grid grid-cols-[minmax(110px,auto)_1fr] gap-x-4 gap-y-1 text-[13px]">
          {#each rows as row (row.label)}
            <dt class="text-muted">{row.label}</dt><dd class="tabular">{row.value}</dd>
          {/each}
          {#if shown.finalFilePath}<dt class="text-muted">Save to</dt><dd class="select-text break-all">{shown.finalFilePath}</dd>{/if}
        </dl>
      {:else}
        <p class="text-[13px] text-muted">No file selected</p>
      {/if}
    </div>

    <div class="mt-3"><PackagePassword {groupId} {members} /></div>
  {/if}
  <p class="mt-3 min-h-5 text-[12.5px] text-muted" role="status">{notice}</p>

  {#snippet footer()}
    {#if group}
      {#if allDone}<button class="btn" onclick={copyFolder}>Copy folder path</button>{/if}
      <button class="btn" disabled={!connected || !pauseOn || busy} onclick={() => action('pause')}>Pause all</button>
      <button class="btn" disabled={!connected || !resumeOn || busy} onclick={() => action('resume')}>Resume all</button>
      <button class="btn" disabled={!connected || !cancelOn || busy} onclick={() => (confirmCancel = true)}>Cancel package</button>
    {/if}
    <button class="btn" onclick={onclose}>{allDone || !group ? 'Close' : 'Hide'}</button>
  {/snippet}
</Dialog>

{#if confirmCancel}
  <AskDialog title="Cancel package?" text={cancelPackageText} confirm="Cancel Package" keep="Keep Downloading" onconfirm={() => { confirmCancel = false; void action('cancel'); }} onclose={() => (confirmCancel = false)} />
{/if}
