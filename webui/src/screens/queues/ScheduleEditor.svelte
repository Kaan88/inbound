<script lang="ts">
  import { DAY_NAMES, ALL_DAYS, tickedDays, toggleDay, type ScheduleDraft } from '../../lib/queues';
  import { store } from '../../lib/store.svelte';
  import type { Queue } from '../../lib/types';

  // One schedule's form, in the desktop Scheduler's order: Queue, Schedule name, Enable schedule, Start/Stop time, Days,
  // One-time date, "When the queue finishes", Queue run options. Each open form keeps its own draft.
  let {
    draft = $bindable(),
    queues,
    error,
    busy = false,
    onsave,
    oncancel,
  }: { draft: ScheduleDraft; queues: Queue[]; error: string; busy?: boolean; onsave: () => void; oncancel: () => void } = $props();

  const uid = $props.id();
  const picked = $derived(tickedDays(draft.schedule.days));
  function queueChanged() {
    // Like the desktop: the retry fields follow the chosen queue.
    const limit = queues.find((q) => q.id === draft.schedule.queueId)?.retryLimit;
    draft.options.retryEnabled = limit != null;
    draft.options.retryLimit = String(limit ?? 3);
    draft.retryInitial = limit ?? null;
  }
  const everyDay = $derived(picked.size === DAY_NAMES.length);
</script>

<form class="mt-2 grid gap-3 rounded-md border border-divider bg-chrome p-3 sm:grid-cols-2" onsubmit={(event) => { event.preventDefault(); onsave(); }}>
  <div>
    <label class="label" for="{uid}-queue">Queue</label>
    <select id="{uid}-queue" class="field" bind:value={draft.schedule.queueId} onchange={queueChanged} disabled={queues.length <= 1}>
      {#each queues as queue (queue.id)}<option value={queue.id}>{queue.name}</option>{/each}
    </select>
  </div>
  <div>
    <label class="label" for="{uid}-name">Schedule name</label>
    <input id="{uid}-name" class="field" maxlength="80" bind:value={draft.schedule.name} />
  </div>
  <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={draft.schedule.enabled} /> Enable schedule</label>

  <div>
    <label class="label" for="{uid}-start">Start time</label>
    <input id="{uid}-start" class="field" type="time" bind:value={draft.schedule.startTime} />
  </div>
  <div>
    <label class="label" for="{uid}-stop">Stop time (optional)</label>
    <input id="{uid}-stop" class="field" type="time" bind:value={draft.schedule.stopTime} />
  </div>

  <div class="sm:col-span-2">
    <p class="label">Days</p>
    <div class="flex flex-wrap items-center gap-1" role="group" aria-label="Days">
      {#each DAY_NAMES as day (day)}
        <button type="button" class="rounded border px-2.5 py-1 text-[12px] {picked.has(day) ? 'border-accent bg-highlight font-semibold' : 'border-divider'}" aria-pressed={picked.has(day)} onclick={() => (draft.schedule.days = toggleDay(draft.schedule.days, day))}>{day}</button>
      {/each}
      <button type="button" class="px-2 py-1 text-[12px] text-accent underline disabled:no-underline disabled:opacity-50" disabled={everyDay} onclick={() => (draft.schedule.days = ALL_DAYS)}>Every day</button>
    </div>
  </div>
  <div>
    <label class="label" for="{uid}-date">One-time date (optional)</label>
    <input id="{uid}-date" class="field" type="date" bind:value={draft.schedule.startDate} />
    {#if !draft.schedule.startDate}<p class="hint mt-1">Repeat on the days above</p>{/if}
  </div>

  <div class="sm:col-span-2">
    <p class="label">When the queue finishes</p>
    <label class="flex items-center gap-2 text-[13px] opacity-60"><input type="checkbox" disabled checked={false} /> Open a file, exit, or turn off the computer</label>
    <p class="hint pl-7">{store.texts.queueEndNote}</p>
  </div>

  <fieldset class="grid gap-2 sm:col-span-2">
    <legend class="label">Queue run options</legend>
    <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={draft.options.startOnHostStartup} /> Start this queue when the host starts</label>
    <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={draft.options.timedStartEnabled} /> Start this queue at the scheduled time</label>
    <div class="flex flex-wrap items-center gap-2">
      <label class="flex min-w-0 flex-1 items-center gap-2 text-[13px] {draft.options.timedStartEnabled ? '' : 'opacity-60'}">
        <input type="checkbox" bind:checked={draft.options.repeatEnabled} disabled={!draft.options.timedStartEnabled} /> Periodically check completed files for changes
      </label>
      <input class="field w-24" inputmode="numeric" aria-label="Minutes between checks" bind:value={draft.options.repeatMinutes} disabled={!draft.options.timedStartEnabled || !draft.options.repeatEnabled} />
      <span class="text-[12px] text-muted">min</span>
    </div>
    <p class="hint">Periodic checks compare completed HTTP files on the server. Changed files, or files without usable server validators, are refreshed. The schedule’s existing start and stop times define the active window.</p>
    <div class="flex flex-wrap items-center gap-2">
      <label class="flex min-w-0 flex-1 items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={draft.options.retryEnabled} /> Limit retries after a file fails</label>
      <input class="field w-24" inputmode="numeric" aria-label="Retry limit" bind:value={draft.options.retryLimit} disabled={!draft.options.retryEnabled} />
    </div>
    <p class="hint">Uncheck the retry limit for unlimited retries. A limit of 0 disables retries; 1–100 sets the number of retries after a file failure.</p>
  </fieldset>

  {#if error}<p class="text-[13px] tone-failed sm:col-span-2" role="alert">{error}</p>{/if}
  <div class="flex justify-end gap-2 sm:col-span-2">
    <button class="btn-primary" type="submit" disabled={busy}>{draft.isNew ? 'Add schedule' : 'Save schedule'}</button>
    <button class="btn" type="button" onclick={oncancel}>Cancel</button>
  </div>
</form>
