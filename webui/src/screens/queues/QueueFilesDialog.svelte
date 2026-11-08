<script lang="ts">
  import Dialog from '../../components/Dialog.svelte';
  import { bytes, statusText } from '../../lib/format';
  import { canMoveFile, downloadsText, moveFile, orderedQueues, priorityText, queueMembers, sortQueueFiles } from '../../lib/queues';
  import { store } from '../../lib/store.svelte';
  import type { Job } from '../../lib/types';

  // The downloads of one queue in the order the host runs them. Changes are staged and saved with Save: moves and
  // removals first (job.moveToQueue), then the order (queue.action reorderfiles), as the desktop does.
  let { queueId, onclose }: { queueId: number; onclose: () => void } = $props();

  const queue = $derived(store.model.settings?.queues.find((q) => q.id === queueId));
  const others = $derived(orderedQueues((store.model.settings?.queues ?? []).filter((q) => q.id !== queueId)));

  // svelte-ignore state_referenced_locally
  const start = sortQueueFiles(queueMembers(Object.values(store.model.jobs), queueId), queue ?? { jobOrder: [] });
  let files = $state<Job[]>(start);
  let staged = $state<Record<string, number | null>>({});
  let orderDirty = $state(false);
  let selected = $state<string | null>(null);
  let target = $state<number | null>(null);
  let message = $state('');
  let busy = $state(false);

  const index = $derived(files.findIndex((j) => j.id === selected));
  const dirty = $derived(orderDirty || Object.keys(staged).length > 0);

  $effect(() => {
    if (target === null || !others.some((q) => q.id === target)) target = others[0]?.id ?? null;
  });

  function move(delta: -1 | 1) {
    if (!canMoveFile(files, index, delta)) return;
    files = moveFile(files, index, delta);
    orderDirty = true;
    message = 'File order unsaved. Click Save to save.';
  }

  function stage(destination: number | null) {
    if (index < 0) return;
    const job = files[index];
    staged[job.id] = destination;
    files = files.filter((j) => j.id !== job.id);
    selected = files[Math.min(index, files.length - 1)]?.id ?? null;
    message = destination === null ? 'Removal from this queue unsaved; the download and file will be kept.' : 'Queue move unsaved; the download and file will be kept.';
  }

  async function save() {
    if (busy) return;
    busy = true;
    const errors: string[] = [];
    try {
      const name = queue?.name ?? `Queue ${queueId}`;
      const current = new Set(queueMembers(Object.values(store.model.jobs), queueId).map((j) => j.id));
      const seen = new Set([...files.map((j) => j.id), ...Object.keys(staged)]);
      const sameMembers = current.size === seen.size && [...current].every((id) => seen.has(id));
      const failed = new Set<string>();
      for (const [id, destination] of Object.entries(staged)) {
        const job = store.model.jobs[id];
        if (!job || job.queueId !== queueId || job.isQueued === false) { errors.push(`A download in ${name} changed elsewhere; it was left untouched.`); failed.add(id); continue; }
        try {
          await store.ask('job.moveToQueue', destination === null ? { jobId: id, queueId: 0, detach: true } : { jobId: id, queueId: destination });
          delete staged[id];
        } catch (error) {
          errors.push(`Could not update queue membership for ${job.fileName}: ${error instanceof Error ? error.message : String(error)}`);
          failed.add(id);
        }
      }
      if (orderDirty && failed.size === 0) {
        if (!sameMembers) errors.push(`${name} membership changed while applying order. The order was left untouched; close this window and set it again.`);
        else {
          try {
            await store.ask('queue.action', { queueId, action: 'reorderfiles', jobOrder: files.map((j) => j.id) });
            orderDirty = false;
          } catch (error) {
            errors.push(`Could not save file order for ${name}: ${error instanceof Error ? error.message : String(error)}`);
          }
        }
      }
    } finally {
      busy = false;
    }
    if (errors.length) message = errors.join(' ');
    else { store.toast('Changes saved.'); onclose(); }
  }
</script>

<Dialog title="Downloads in {queue?.name ?? 'this queue'}" {onclose} wide>
  <p class="mb-2 text-[13px]">All downloads in this queue, also finished and failed ones. Set the order of waiting downloads with the same priority, or move a download to another queue. Running downloads go on. Save applies the changes.</p>
  {#if files.length === 0}
    <p class="card px-4 py-6 text-center text-[13px] text-muted">No downloads in this queue.</p>
  {:else}
    <ul class="card max-h-[40vh] divide-y divide-divider overflow-y-auto" aria-label="Files in download order">
      {#each files as job (job.id)}
        <li>
          <button type="button" class="flex w-full items-center gap-2 px-3 py-2 text-left text-[13px] hover:bg-hover {selected === job.id ? 'bg-highlight' : ''}" aria-pressed={selected === job.id} onclick={() => (selected = job.id)}>
            <span class="min-w-0 flex-1">
              <span class="block truncate">{job.fileName}</span>
              <span class="block text-[12px] text-muted">{bytes(job.totalBytes ?? job.receivedBytes)} · {statusText(job)}</span>
            </span>
            <span class="shrink-0 text-[12px] text-muted">{priorityText(job.priority)}</span>
          </button>
        </li>
      {/each}
    </ul>
  {/if}
  <div class="mt-3 flex flex-wrap gap-2">
    <button class="btn" disabled={!canMoveFile(files, index, -1)} onclick={() => move(-1)}>Move up</button>
    <button class="btn" disabled={!canMoveFile(files, index, 1)} onclick={() => move(1)}>Move down</button>
    <button class="btn" disabled={index < 0} onclick={() => stage(null)}>Remove from queue</button>
  </div>
  <div class="mt-3 flex flex-wrap items-end gap-2">
    <div class="min-w-40 flex-1">
      <label class="label" for="qf-target">Move to queue</label>
      <select id="qf-target" class="field" bind:value={target} disabled={index < 0 || others.length === 0}>
        {#each others as other (other.id)}<option value={other.id}>{other.name}</option>{/each}
      </select>
    </div>
    <button class="btn" disabled={index < 0 || target === null} onclick={() => stage(target)}>Move</button>
  </div>
  <p class="hint mt-3" aria-live="polite">{message || (dirty ? 'Changes not saved yet.' : `${downloadsText(files.length)} in this queue. The order counts among downloads with the same priority.`)}</p>
  {#snippet footer()}
    <button class="btn-primary" disabled={!dirty || busy} onclick={save}>Save</button>
    <button class="btn" onclick={onclose}>Close</button>
  {/snippet}
</Dialog>
