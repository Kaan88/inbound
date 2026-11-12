<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import Icon from '../components/Icon.svelte';
  import { bytes, dateTime } from '../lib/format';
  import { newUuid } from '../lib/ids';
  import {
    canStartQueue, canStopQueue, checkScheduleDraft, defaultMoveTarget, downloadsText, editScheduleDraft, isMember, newScheduleDraft, orderedQueues,
    queueStartedText, queueStateText, applyScheduleEdit, applyScheduleRemoval, retryConflict, scheduleConflict, scheduleSummary, schedulesOf, type QueuePolicy, type ScheduleDraft,
  } from '../lib/queues';
  import { store } from '../lib/store.svelte';
  import type { Queue, Schedule } from '../lib/types';
  import QueueEditDialog from './queues/QueueEditDialog.svelte';
  import QueueFilesDialog from './queues/QueueFilesDialog.svelte';
  import ScheduleEditor from './queues/ScheduleEditor.svelte';

  // Queues (queue.action) and the scheduler (the settings' schedules), as one screen like the desktop's Scheduler window:
  // each queue is a card with its schedules under it. The server has no shutdown, exit or "open the file" at the end of a
  // schedule: those stay off and say why.
  const queues = $derived(orderedQueues(store.model.settings?.queues ?? []));
  const schedules = $derived(store.model.settings?.schedules ?? []);
  const defaultQueueId = $derived(store.model.settings?.defaultQueueId ?? 0);
  const counts = $derived.by(() => {
    const map = new Map<number, number>();
    for (const job of Object.values(store.model.jobs)) {
      if (isMember(job) && job.state !== 'completed') map.set(job.queueId, (map.get(job.queueId) ?? 0) + 1);
    }
    return map;
  });
  const jobList = $derived(Object.values(store.model.jobs));
  const holding = $derived.by(() => {
    const map = new Map<number, number>();
    for (const job of Object.values(store.model.jobs)) if (isMember(job)) map.set(job.queueId, (map.get(job.queueId) ?? 0) + 1);
    return map;
  });

  // ---- queues -------------------------------------------------------------------------------------------------

  let editing = $state<{ queue: Queue | null } | null>(null);
  let filesFor = $state<number | null>(null);
  let deleting = $state<Queue | null>(null);
  let moveTo = $state<number | null>(null);

  async function saveQueue(policy: QueuePolicy): Promise<string | null> {
    const queue = editing?.queue ?? null;
    try {
      await store.ask('queue.action', queue
        ? { queueId: queue.id, action: 'policy', ...policy }
        : { queueId: 0, action: 'create', ...policy });
    } catch (error) {
      return error instanceof Error ? error.message : String(error);
    }
    store.toast(queue ? 'Queue saved.' : 'Queue created.');
    editing = null;
    return null;
  }

  function startDelete(queue: Queue) {
    moveTo = defaultMoveTarget(queues, queue.id, defaultQueueId);
    deleting = queue;
  }

  async function confirmDelete() {
    if (!deleting || moveTo === null) return;
    const ok = await store.attempt('queue.action', { queueId: deleting.id, action: 'delete', moveContentsToQueueId: moveTo }, 'Queue deleted.');
    if (ok) deleting = null;
  }

  async function runQueue(queue: Queue, action: 'start' | 'stop') {
    await store.attempt('queue.action', { queueId: queue.id, action }, queueStartedText(queue.name, action === 'start'));
  }

  // ---- schedules ----------------------------------------------------------------------------------------------

  // Every open form keeps its own draft, so opening another one never drops what was typed.
  let drafts = $state<Record<string, ScheduleDraft>>({});
  let origin = $state<Record<string, number>>({});
  let errors = $state<Record<string, string>>({});
  let saving = $state<string | null>(null);
  let removing = $state<{ schedule: Schedule; baseline: Schedule } | null>(null);

  const retryOf = (queueId: number) => queues.find((q) => q.id === queueId)?.retryLimit;

  function openNew(queue: Queue) {
    const id = newUuid();
    drafts[id] = newScheduleDraft(queue.id, id, queue.retryLimit);
    origin[id] = queue.id;
  }

  function openEdit(schedule: Schedule) {
    if (!drafts[schedule.id]) drafts[schedule.id] = editScheduleDraft(schedule, retryOf(schedule.queueId));
  }

  function closeDraft(id: string) {
    delete drafts[id];
    delete origin[id];
    delete errors[id];
  }

  async function saveDraft(id: string) {
    const draft = drafts[id];
    if (!draft || saving) return;
    const checked = checkScheduleDraft(draft, queues.map((q) => q.id));
    if ('error' in checked) { errors[id] = checked.error; return; }
    const conflict = scheduleConflict(schedules, draft);
    if (conflict) { errors[id] = conflict.error; return; }
    const retry = checked.retryLimit;
    // The retry limit belongs to a queue: it is written only when this form changed it, and to the queue the schedule targets.
    const retryChanged = retry !== draft.retryInitial;
    if (retryChanged) {
      const clash = retryConflict(queues.find((q) => q.id === checked.schedule.queueId), draft);
      if (clash) { errors[id] = clash.error; return; }
    }
    errors[id] = '';
    saving = id;
    const value = checked.schedule;
    // Rebased on the latest settings, and checked again on them: only this schedule and its queue's retry limit change.
    const ok = await store.saveSettings((settings) => applyScheduleEdit(settings, draft, value, retryChanged ? retry : undefined), 'Schedule saved.');
    saving = null;
    if (ok) closeDraft(id);
  }

  async function confirmRemove() {
    if (!removing) return;
    const id = removing.schedule.id;
    // The copy taken when the dialog opened: a change or removal made elsewhere since then is refused.
    const snapshot = removing.baseline;
    const conflict = scheduleConflict(schedules, { isNew: false, schedule: snapshot, baseline: snapshot }, true);
    removing = null;
    if (conflict) { store.toast(conflict.error, 'error'); return; }
    const ok = await store.saveSettings((settings) => applyScheduleRemoval(settings, snapshot), 'Schedule removed.');
    if (ok) closeDraft(id);
  }

  const newDraftsOf = (queueId: number) => Object.keys(drafts).filter((id) => origin[id] === queueId);
</script>

<div class="mx-auto max-w-4xl px-4 py-4">
  <div class="mb-3 flex flex-wrap items-center gap-2">
    <h1 class="flex-1 text-[18px] font-semibold">Queues and scheduler</h1>
    <button class="btn" onclick={() => (editing = { queue: null })}><Icon name="add" size={15} class="text-icon-info" /> New queue</button>
  </div>

  <div class="space-y-3">
    {#each queues as queue, position (queue.id)}
      <section class="card p-3" aria-label={queue.name}>
        <div class="flex flex-wrap items-center gap-2">
          <Icon name="queue" class="text-icon-info" />
          <h2 class="min-w-0 flex-1 text-[14px] font-semibold">{queue.name}{queue.id === defaultQueueId ? ' (default)' : ''}
            <span class="ml-2 whitespace-nowrap text-[12px] font-normal {queue.isRunning ? 'tone-active' : 'tone-waiting'}">{queueStateText(queue, jobList)}</span>
            <span class="ml-1 whitespace-nowrap text-[12px] font-normal text-muted">{counts.get(queue.id) ?? 0} unfinished</span></h2>
          <span class="ml-auto flex shrink-0 flex-wrap gap-2">
            {#if canStartQueue(queue, jobList)}
              <button class="btn" onclick={() => runQueue(queue, 'start')}><Icon name="play" size={15} class="text-icon-start" /> Start now</button>
            {/if}
            {#if canStopQueue(queue)}
              <button class="btn" onclick={() => runQueue(queue, 'stop')}><Icon name="stop" size={15} class="text-icon-pause" /> Stop</button>
            {/if}
            <button class="btn px-2" title="Move up" aria-label="Move up" disabled={position === 0} onclick={() => store.attempt('queue.action', { queueId: queue.id, action: 'moveUp' })}><Icon name="chevron-up" size={15} /></button>
            <button class="btn px-2" title="Move down" aria-label="Move down" disabled={position === queues.length - 1} onclick={() => store.attempt('queue.action', { queueId: queue.id, action: 'moveDown' })}><Icon name="chevron-down" size={15} /></button>
            <button class="btn" onclick={() => (filesFor = queue.id)}>Files</button>
            <button class="btn" onclick={() => (editing = { queue })}>Edit</button>
            {#if queue.id !== defaultQueueId}<button class="btn-danger" onclick={() => startDelete(queue)}>Delete</button>{/if}
          </span>
        </div>
        <p class="hint mt-1">{downloadsText(holding.get(queue.id) ?? 0)}; at most {queue.maxConcurrentDownloads} at a time{queue.quotaEnabled ? `; up to ${bytes(queue.quotaBytes)} per ${queue.quotaWindowHours} h, ${bytes(queue.quotaBytesUsed)} used` : ''}.</p>

        <div class="mt-3 border-t border-divider pt-2">
          <div class="flex flex-wrap items-center gap-2">
            <h3 class="flex-1 text-[13px] font-semibold"><Icon name="calendar" size={14} class="mr-1 inline text-icon-info" />Schedules</h3>
            <button class="btn" onclick={() => openNew(queue)}><Icon name="add" size={15} class="text-icon-info" /> New schedule</button>
          </div>
          <div class="mt-1 space-y-2">
            {#each schedulesOf(schedules, queue.id) as schedule (schedule.id)}
              <div class="rounded-md border border-divider p-2">
                <div class="flex flex-wrap items-center gap-2">
                  <div class="min-w-0 flex-1">
                    <h4 class="text-[13px] font-semibold">{schedule.name} <span class="text-[12px] font-normal {schedule.enabled ? 'tone-done' : 'text-muted'}">{schedule.enabled ? 'On' : 'Off'}</span></h4>
                    <p class="hint">{scheduleSummary(schedule)}</p>
                    {#if schedule.lastRunAt}<p class="hint">Last ran {dateTime(schedule.lastRunAt)}{schedule.lastRunError ? `: ${schedule.lastRunError}` : ''}</p>{/if}
                  </div>
                  {#if !drafts[schedule.id]}<button class="btn" onclick={() => openEdit(schedule)}>Edit</button>{/if}
                  <button class="btn-danger" onclick={() => (removing = { schedule, baseline: JSON.parse(JSON.stringify(schedule)) as Schedule })}>Remove</button>
                </div>
                {#if drafts[schedule.id]}
                  <ScheduleEditor bind:draft={drafts[schedule.id]} {queues} error={errors[schedule.id] ?? ''} busy={saving === schedule.id} onsave={() => saveDraft(schedule.id)} oncancel={() => closeDraft(schedule.id)} />
                {/if}
              </div>
            {/each}
            {#each newDraftsOf(queue.id) as id (id)}
              <div class="rounded-md border border-dashed border-divider p-2">
                <p class="text-[13px] font-semibold">New schedule</p>
                <ScheduleEditor bind:draft={drafts[id]} {queues} error={errors[id] ?? ''} busy={saving === id} onsave={() => saveDraft(id)} oncancel={() => closeDraft(id)} />
              </div>
            {/each}
            {#if schedulesOf(schedules, queue.id).length === 0 && newDraftsOf(queue.id).length === 0}
              <p class="hint">No schedules yet. Click New schedule to add one.</p>
            {/if}
          </div>
        </div>
      </section>
    {/each}
  </div>
</div>

{#if editing}
  <QueueEditDialog queue={editing.queue} count={queues.length} onsave={saveQueue} onclose={() => (editing = null)} />
{/if}

{#if filesFor !== null}
  <QueueFilesDialog queueId={filesFor} onclose={() => (filesFor = null)} />
{/if}

{#if deleting}
  <Dialog title="Delete {deleting.name}?" onclose={() => (deleting = null)}>
    <p class="mb-3 text-[13px]">Downloads and schedules will move to the selected queue. Downloaded files stay in their current folders. This keeps all download files.</p>
    <label class="label" for="qd-target">Move downloads and schedules to</label>
    <select id="qd-target" class="field" bind:value={moveTo}>
      {#each queues.filter((q) => q.id !== deleting?.id) as other (other.id)}<option value={other.id}>{other.name}</option>{/each}
    </select>
    {#snippet footer()}
      <button class="btn-danger" disabled={moveTo === null} onclick={confirmDelete}>Delete queue</button>
      <button class="btn" data-autofocus onclick={() => (deleting = null)}>Cancel</button>
    {/snippet}
  </Dialog>
{/if}

{#if removing}
  <Dialog title="Remove {removing.schedule.name}?" onclose={() => (removing = null)}>
    <p class="text-[13px]">This schedule will be removed. The queue and its downloads are not affected.</p>
    {#snippet footer()}
      <button class="btn-danger" onclick={confirmRemove}>Remove schedule</button>
      <button class="btn" data-autofocus onclick={() => (removing = null)}>Cancel</button>
    {/snippet}
  </Dialog>
{/if}
