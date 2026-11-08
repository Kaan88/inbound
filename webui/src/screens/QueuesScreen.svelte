<script lang="ts">
  import Icon from '../components/Icon.svelte';
  import { bytes, dateTime } from '../lib/format';
  import { newUuid } from '../lib/ids';
  import { store } from '../lib/store.svelte';
  import type { Queue, Schedule } from '../lib/types';

  // Queues (queue.action) and the scheduler (the settings' schedules). The server has no shutdown, exit or
  // "open the file" at the end of a schedule: those stay off and say why.
  const queues = $derived([...(store.model.settings?.queues ?? [])].sort((a, b) => a.order - b.order || a.id - b.id));
  const schedules = $derived(store.model.settings?.schedules ?? []);
  const counts = $derived.by(() => {
    const map = new Map<number, number>();
    for (const job of Object.values(store.model.jobs)) if (job.state !== 'completed') map.set(job.queueId, (map.get(job.queueId) ?? 0) + 1);
    return map;
  });
  const days = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

  let editing = $state<Record<number, { name: string; max: number; quotaEnabled: boolean; quotaMb: number; quotaHours: number }>>({});
  let newQueue = $state('');
  let draft = $state<Schedule | null>(null);

  function edit(queue: Queue) {
    editing[queue.id] = {
      name: queue.name,
      max: queue.maxConcurrentDownloads,
      quotaEnabled: queue.quotaEnabled,
      quotaMb: Math.round(queue.quotaBytes / 1024 / 1024),
      quotaHours: queue.quotaWindowHours,
    };
  }

  async function savePolicy(queue: Queue) {
    const e = editing[queue.id];
    const result = await store.attempt('queue.action', {
      queueId: queue.id, action: 'policy', name: e.name, maxConcurrentDownloads: e.max,
      quotaEnabled: e.quotaEnabled, quotaBytes: Math.round(e.quotaMb * 1024 * 1024), quotaWindowHours: e.quotaHours,
    }, 'Queue saved.');
    if (result) delete editing[queue.id];
  }

  async function createQueue() {
    if (!newQueue.trim()) return;
    if (await store.attempt('queue.action', { queueId: 0, action: 'create', name: newQueue.trim(), maxConcurrentDownloads: 3 }, 'Queue created.')) newQueue = '';
  }

  function newSchedule(): Schedule {
    return {
      id: newUuid(),
      name: 'Nightly', queueId: store.model.settings?.defaultQueueId ?? 0, enabled: true, kind: 'daily',
      days: days.join(','), startTime: '02:00', stopTime: '07:00', timedStartEnabled: true, startOnHostStartup: false,
      openFileWhenDone: false, exitWhenDone: false, shutdownWhenDone: false, shutdownMode: 'shutdown', forceShutdown: false,
    };
  }

  async function saveSchedule() {
    if (!draft) return;
    const value = $state.snapshot(draft) as Schedule;
    if (!value.stopTime) delete value.stopTime;
    const ok = await store.saveSettings((settings) => {
      const index = settings.schedules.findIndex((s) => s.id === value.id);
      if (index >= 0) settings.schedules[index] = { ...settings.schedules[index], ...value };
      else settings.schedules.push(value);
    }, 'Schedule saved.');
    if (ok) draft = null;
  }

  async function removeSchedule(id: string) {
    await store.saveSettings((settings) => { settings.schedules = settings.schedules.filter((s) => s.id !== id); }, 'Schedule removed.');
  }

  function toggleDay(day: string) {
    if (!draft) return;
    const set = new Set(draft.days.split(',').filter(Boolean));
    if (set.has(day)) set.delete(day);
    else set.add(day);
    draft.days = days.filter((d) => set.has(d)).join(',');
  }

  function scheduleText(schedule: Schedule): string {
    const when = schedule.kind === 'oneTime' ? `once on ${schedule.startDate ?? 'a date'}` : schedule.days.split(',').length === 7 ? 'every day' : schedule.days.replaceAll(',', ', ');
    const queue = queues.find((q) => q.id === schedule.queueId)?.name ?? `queue ${schedule.queueId}`;
    return `${queue}: start ${schedule.timedStartEnabled ? `at ${schedule.startTime}` : 'not by time'}${schedule.stopTime ? `, stop at ${schedule.stopTime}` : ''}, ${when}${schedule.repeatEveryMinutes ? `, again every ${schedule.repeatEveryMinutes} min` : ''}${schedule.startOnHostStartup ? ', and when the server starts' : ''}`;
  }
</script>

<div class="mx-auto max-w-4xl px-4 py-4">
  <h1 class="mb-3 text-[18px] font-semibold">Queues</h1>
  <div class="space-y-2">
    {#each queues as queue (queue.id)}
      <section class="card p-3">
        <div class="flex flex-wrap items-center gap-2">
          <Icon name="queue" class="text-icon-info" />
          <h2 class="min-w-0 flex-1 text-[14px] font-semibold">{queue.name}{queue.id === store.model.settings?.defaultQueueId ? ' (default)' : ''}
            <span class="ml-2 whitespace-nowrap text-[12px] font-normal {queue.isRunning ? 'tone-active' : 'tone-waiting'}">{queue.isRunning ? 'Running' : 'Stopped'}</span>
            <span class="ml-1 whitespace-nowrap text-[12px] font-normal text-muted">{counts.get(queue.id) ?? 0} unfinished</span></h2>
          <span class="ml-auto flex shrink-0 gap-2">
          {#if queue.isRunning}
            <button class="btn" onclick={() => store.attempt('queue.action', { queueId: queue.id, action: 'stop' }, 'Queue stopped.')}><Icon name="stop" size={15} class="text-icon-pause" /> Stop</button>
          {:else}
            <button class="btn" onclick={() => store.attempt('queue.action', { queueId: queue.id, action: 'start' }, 'Queue started.')}><Icon name="play" size={15} class="text-icon-start" /> Start</button>
          {/if}
          <button class="btn px-2" title="Move up" aria-label="Move up" onclick={() => store.attempt('queue.action', { queueId: queue.id, action: 'moveUp' })}><Icon name="chevron-up" size={15} /></button>
          <button class="btn px-2" title="Move down" aria-label="Move down" onclick={() => store.attempt('queue.action', { queueId: queue.id, action: 'moveDown' })}><Icon name="chevron-down" size={15} /></button>
          {#if !editing[queue.id]}<button class="btn" onclick={() => edit(queue)}>Edit</button>{/if}
          </span>
        </div>
        <p class="hint mt-1">At most {queue.maxConcurrentDownloads} at a time{queue.quotaEnabled ? `; up to ${bytes(queue.quotaBytes)} per ${queue.quotaWindowHours} h, ${bytes(queue.quotaBytesUsed)} used` : ''}.</p>

        {#if editing[queue.id]}
          <form class="mt-3 grid gap-3 border-t border-divider pt-3 sm:grid-cols-2" onsubmit={(event) => { event.preventDefault(); void savePolicy(queue); }}>
            <div><label class="label" for="q-name-{queue.id}">Name</label><input id="q-name-{queue.id}" class="field" bind:value={editing[queue.id].name} /></div>
            <div><label class="label" for="q-max-{queue.id}">Downloads at a time</label><input id="q-max-{queue.id}" class="field" type="number" min="1" max="16" bind:value={editing[queue.id].max} /></div>
            <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={editing[queue.id].quotaEnabled} /> Limit how much this queue downloads</label>
            <div class="pl-7"><label class="label" for="q-quota-{queue.id}">Up to (MB)</label><input id="q-quota-{queue.id}" class="field" type="number" min="1" bind:value={editing[queue.id].quotaMb} disabled={!editing[queue.id].quotaEnabled} /></div>
            <div class="pl-7 sm:pl-0"><label class="label" for="q-hours-{queue.id}">In every (hours)</label><input id="q-hours-{queue.id}" class="field" type="number" min="1" max="8760" bind:value={editing[queue.id].quotaHours} disabled={!editing[queue.id].quotaEnabled} /></div>
            <div class="flex flex-wrap justify-end gap-2 sm:col-span-2">
              {#if queue.id !== store.model.settings?.defaultQueueId}
                <button class="btn-danger mr-auto" type="button" onclick={() => store.attempt('queue.action', { queueId: queue.id, action: 'delete' }, 'Queue deleted.')}>Delete queue</button>
              {/if}
              <button class="btn-primary" type="submit">Save</button>
              <button class="btn" type="button" onclick={() => delete editing[queue.id]}>Cancel</button>
            </div>
          </form>
        {/if}
      </section>
    {/each}
  </div>
  <form class="mt-3 flex gap-2" onsubmit={(event) => { event.preventDefault(); void createQueue(); }}>
    <input class="field max-w-xs" bind:value={newQueue} placeholder="New queue name" aria-label="New queue name" />
    <button class="btn shrink-0 whitespace-nowrap" type="submit" disabled={!newQueue.trim()}><Icon name="add" size={15} class="text-icon-info" /> Add queue</button>
  </form>

  <h1 class="mb-3 mt-8 flex items-center gap-2 text-[18px] font-semibold"><Icon name="calendar" class="text-icon-info" /> Scheduler</h1>
  <div class="space-y-2">
    {#each schedules as schedule (schedule.id)}
      <section class="card flex flex-wrap items-center gap-2 p-3">
        <div class="min-w-0 flex-1">
          <h2 class="text-[14px] font-semibold">{schedule.name} <span class="text-[12px] font-normal {schedule.enabled ? 'tone-done' : 'text-muted'}">{schedule.enabled ? 'On' : 'Off'}</span></h2>
          <p class="hint">{scheduleText(schedule)}</p>
          {#if schedule.lastRunAt}<p class="hint">Last ran {dateTime(schedule.lastRunAt)}{schedule.lastRunError ? `: ${schedule.lastRunError}` : ''}</p>{/if}
        </div>
        <button class="btn" onclick={() => (draft = JSON.parse(JSON.stringify(schedule)))}>Edit</button>
        <button class="btn-danger" onclick={() => removeSchedule(schedule.id)}>Remove</button>
      </section>
    {:else}
      <p class="card px-4 py-6 text-center text-[13px] text-muted">No schedules. A schedule starts and stops a queue at set times.</p>
    {/each}
  </div>
  {#if !draft}<button class="btn mt-3" onclick={() => (draft = newSchedule())}><Icon name="add" size={15} class="text-icon-info" /> Add schedule</button>{/if}

  {#if draft}
    <form class="card mt-3 grid gap-3 p-4 sm:grid-cols-2" onsubmit={(event) => { event.preventDefault(); void saveSchedule(); }}>
      <div><label class="label" for="s-name">Name</label><input id="s-name" class="field" bind:value={draft.name} /></div>
      <div>
        <label class="label" for="s-queue">Queue</label>
        <select id="s-queue" class="field" bind:value={draft.queueId}>{#each queues as queue (queue.id)}<option value={queue.id}>{queue.name}</option>{/each}</select>
      </div>
      <fieldset class="sm:col-span-2">
        <legend class="label">When</legend>
        <label class="mr-4 inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={draft.kind} value="daily" /> On these days</label>
        <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={draft.kind} value="oneTime" /> Once</label>
      </fieldset>
      {#if draft.kind === 'daily'}
        <div class="flex flex-wrap gap-1 sm:col-span-2" role="group" aria-label="Days">
          {#each days as day}
            <button type="button" class="rounded border px-2.5 py-1 text-[12px] {draft.days.split(',').includes(day) ? 'border-accent bg-highlight font-semibold' : 'border-divider'}" aria-pressed={draft.days.split(',').includes(day)} onclick={() => toggleDay(day)}>{day}</button>
          {/each}
        </div>
      {:else}
        <div><label class="label" for="s-date">Date</label><input id="s-date" class="field" type="date" bind:value={draft.startDate} /></div>
      {/if}
      <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={draft.timedStartEnabled} /> Start at a time</label>
      <div class="pl-7"><label class="label" for="s-start">Start at</label><input id="s-start" class="field" type="time" bind:value={draft.startTime} disabled={!draft.timedStartEnabled} /></div>
      <div class="pl-7 sm:pl-0"><label class="label" for="s-stop">Stop at (optional)</label><input id="s-stop" class="field" type="time" bind:value={draft.stopTime} /></div>
      <div><label class="label" for="s-repeat">Start again every (minutes, optional)</label><input id="s-repeat" class="field" type="number" min="1" bind:value={draft.repeatEveryMinutes} /></div>
      <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={draft.startOnHostStartup} /> Also start when the server starts</label>
      <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={draft.enabled} /> This schedule is on</label>
      <div class="sm:col-span-2">
        <p class="label">When the queue is done</p>
        <label class="flex items-center gap-2 text-[13px] opacity-60"><input type="checkbox" disabled checked={false} /> Open the file, exit, or shut the computer down</label>
        <p class="hint pl-7">Not available on the server: it has no desktop to open files on, and its life belongs to Docker or its service manager.</p>
      </div>
      <div class="flex justify-end gap-2 sm:col-span-2">
        <button class="btn-primary" type="submit">Save schedule</button>
        <button class="btn" type="button" onclick={() => (draft = null)}>Cancel</button>
      </div>
    </form>
  {/if}
</div>
