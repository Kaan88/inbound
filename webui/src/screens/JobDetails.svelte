<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import ProgressBar from '../components/ProgressBar.svelte';
  import { archiveShort, archiveTone, bytes, dateTime, duration, percent, siteOf, speed, statusText, timeSpanSeconds, tone } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet, Job } from '../lib/types';

  let { job, set, onclose, onpassword }: { job: Job; set?: ArchiveSet; onclose: () => void; onpassword: (set: ArchiveSet) => void } = $props();

  let newAddress = $state('');
  let explicitDirect = $state(false);
  let refreshing = $state(false);

  const p = $derived(percent(job));

  async function refresh() {
    refreshing = true;
    const result = await store.attempt<{ ok: boolean }>('job.refreshUrl', { jobId: job.id, url: newAddress.trim(), explicitDirect }, 'The address was replaced.');
    refreshing = false;
    if (result?.ok) newAddress = '';
  }
</script>

<Dialog title={job.fileName || 'Download'} {onclose} wide>
  <div class="mb-3">
    <div class="mb-1 flex items-baseline justify-between gap-3 text-[13px]">
      <span class="tone-{tone(job.state)} font-semibold">{statusText(job)}</span>
      <span class="tabular text-muted">{bytes(job.receivedBytes)}{job.totalBytes ? ` of ${bytes(job.totalBytes)}` : ''}</span>
    </div>
    <ProgressBar value={job.state === 'completed' ? 100 : p} height={8} />
  </div>

  <dl class="grid grid-cols-[minmax(110px,auto)_1fr] gap-x-4 gap-y-1.5 text-[13px]">
    {#if tone(job.state) === 'active'}
      <dt class="text-muted">Transfer rate</dt><dd class="tabular">{speed(job.bytesPerSecond) || '—'}</dd>
      <dt class="text-muted">Time left</dt><dd class="tabular">{duration(timeSpanSeconds(job.estimatedRemaining)) || '—'}</dd>
      <dt class="text-muted">Connections</dt><dd class="tabular">{job.activeConnections ?? 0} of {job.requestedConnections ?? '—'}{job.waitingForHost ? `, waiting for ${job.waitingForHost}` : ''}</dd>
    {/if}
    <dt class="text-muted">Address</dt>
    <dd class="min-w-0 break-all font-mono text-[12px]">{job.addressHidden ? `${siteOf(job.url)} (the address holds a key and stays on the server)` : job.url}</dd>
    <dt class="text-muted">Folder</dt><dd class="min-w-0 break-all font-mono text-[12px]">{job.destinationDirectory}</dd>
    {#if job.finalFilePath}<dt class="text-muted">File</dt><dd class="min-w-0 break-all font-mono text-[12px]">{job.finalFilePath}</dd>{/if}
    <dt class="text-muted">Queue</dt><dd>{job.queueName ?? job.queueId}</dd>
    <dt class="text-muted">Added</dt><dd>{dateTime(job.createdAt)}</dd>
    {#if job.completedAt}<dt class="text-muted">Completed</dt><dd>{dateTime(job.completedAt)}</dd>{/if}
    {#if job.statusNote}<dt class="text-muted">Note</dt><dd>{job.statusNote}</dd>{/if}
    {#if job.failure && job.state === 'failed'}<dt class="text-muted">Why it failed</dt><dd class="text-error">{job.failure.message}</dd>{/if}
    {#if set}
      <dt class="text-muted">Extraction</dt>
      <dd>
        <span class="tone-{archiveTone(set)}">{archiveShort(set)}</span>
        <span class="block text-[12px] text-muted">{set.stateText}</span>
      </dd>
    {/if}
  </dl>

  {#if !job.addressHidden && job.state !== 'completed'}
    <form class="mt-4 border-t border-divider pt-3" onsubmit={(event) => { event.preventDefault(); void refresh(); }}>
      <label class="label" for="refresh-address">Replace an expired address</label>
      <div class="flex gap-2">
        <input id="refresh-address" class="field font-mono" bind:value={newAddress} placeholder="https://…" autocomplete="off" spellcheck="false" />
        <button class="btn shrink-0" type="submit" disabled={refreshing || !newAddress.trim()}>Replace</button>
      </div>
      <label class="mt-1.5 flex items-center gap-2 text-[12px]"><input type="checkbox" bind:checked={explicitDirect} /> Request it directly even if nobody can tell what it is</label>
    </form>
  {/if}

  {#snippet footer()}
    {#if job.canPause}<button class="btn" onclick={() => store.attempt('job.pause', { jobId: job.id })}>Pause</button>{/if}
    {#if job.canResume}<button class="btn" onclick={() => store.attempt(job.state === 'failed' ? 'job.retry' : 'job.resume', { jobId: job.id })}>{job.state === 'failed' ? 'Retry' : 'Resume'}</button>{/if}
    {#if set?.needsPassword}<button class="btn" onclick={() => onpassword(set)}>Archive password…</button>{/if}
    {#if set && (set.state === 'downloaded' || set.state === 'failed')}
      <button class="btn" onclick={() => store.attempt('archive.extract', { jobIds: [job.id] }, 'Extraction asked for.')}>Extract</button>
    {/if}
    <button class="btn" onclick={onclose}>Close</button>
  {/snippet}
</Dialog>
