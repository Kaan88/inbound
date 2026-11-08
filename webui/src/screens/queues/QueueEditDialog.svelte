<script lang="ts">
  import Dialog from '../../components/Dialog.svelte';
  import { checkQueueForm, queueForm, type QueuePolicy } from '../../lib/queues';
  import type { Queue } from '../../lib/types';

  // "New queue" and "Edit queue" are the same form, as on the desktop: a new one opens prefilled with "Queue {n+1}", 1 at a time.
  let { queue, count, onsave, onclose }: { queue: Queue | null; count: number; onsave: (policy: QueuePolicy) => Promise<string | null>; onclose: () => void } = $props();

  // svelte-ignore state_referenced_locally
  let form = $state(queueForm(queue, count));
  let error = $state('');
  let busy = $state(false);

  async function submit() {
    const checked = checkQueueForm(form);
    if ('error' in checked) { error = checked.error; return; }
    error = '';
    busy = true;
    const failure = await onsave(checked.policy);
    busy = false;
    if (failure) error = failure;
  }
</script>

<Dialog title={queue ? 'Edit queue' : 'New queue'} {onclose}>
  <form id="queue-form" class="grid gap-3" onsubmit={(event) => { event.preventDefault(); void submit(); }}>
    <div><label class="label" for="qe-name">Queue name</label><input id="qe-name" class="field" maxlength="80" bind:value={form.name} /></div>
    <div><label class="label" for="qe-max">Download files at the same time (1–16)</label><input id="qe-max" class="field" inputmode="numeric" bind:value={form.max} /></div>
    <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={form.quotaEnabled} /> Enable download limit</label>
    <div><label class="label" for="qe-mb">Download no more than (MB; max 10,485,760)</label><input id="qe-mb" class="field" inputmode="decimal" bind:value={form.quotaMb} disabled={!form.quotaEnabled} /></div>
    <div><label class="label" for="qe-hours">Every (hours; 1–8,760)</label><input id="qe-hours" class="field" inputmode="numeric" bind:value={form.quotaHours} disabled={!form.quotaEnabled} /></div>
    {#if queue}<p class="hint">Saving changes the limits only. How much of the quota is used, and when it starts again, stay as they are.</p>{/if}
    {#if error}<p class="text-[13px] tone-failed" role="alert">{error}</p>{/if}
  </form>
  {#snippet footer()}
    <button class="btn-primary" type="submit" form="queue-form" disabled={busy}>{queue ? 'Save' : 'Create'}</button>
    <button class="btn" onclick={onclose}>Cancel</button>
  {/snippet}
</Dialog>
