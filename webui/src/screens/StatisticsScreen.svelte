<script lang="ts">
  import { untrack } from 'svelte';
  import { exactBytes } from '../lib/details';
  import { anyActive } from '../lib/list';
  import { bytes, dateOnly, dateTime } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import { everyWhileVisible } from '../lib/ticker';
  import type { Statistics } from '../lib/types';

  // Download statistics (D20): the desktop's sections, read every 2 seconds while something downloads and the page shows.
  let stats = $state<Statistics | null>(null);
  let problem = $state('');
  let reading = false;

  async function read() {
    if (store.connection !== 'open') {
      stats = null;
      problem = 'Live statistics are unavailable while Inbound is not connected.';
      return;
    }
    if (reading || document.visibilityState === 'hidden') return;
    reading = true;
    try {
      stats = await store.ask<Statistics>('statistics.get', {});
      problem = '';
    } catch (error) {
      problem = `Could not refresh statistics: ${error instanceof Error ? error.message : String(error)}`;
    } finally {
      reading = false;
    }
  }

  // Whether anything transfers or is about to: only then do the numbers move on their own. `active` and the count change
  // with a state or the list, not with each progress event.
  const active = $derived(anyActive(Object.values(store.model.jobs)));
  const count = $derived(Object.keys(store.model.jobs).length);

  // Reads at once on mount, again when the connection comes back, when something starts or stops downloading (so a
  // download that just finished is counted) and when a download is added or removed.
  $effect(() => {
    void store.connection;
    void active;
    void count;
    untrack(() => void read());
  });

  // Every 2 seconds only while something downloads and this tab shows, and at once when it shows again; nothing while
  // it is hidden or while nothing downloads (audit 4 M11).
  $effect(() => {
    if (!active) return;
    return everyWhileVisible(2000, () => void read());
  });

  const number = (value: number) => value.toLocaleString('en-US');
  const sections = $derived(
    stats
      ? [
          { title: 'Transfer totals', rows: [
            ['Recorded progress', bytes(stats.recordedBytes) || '0 B', exactBytes(stats.recordedBytes)],
            ['Completed download data', bytes(stats.completedBytes) || '0 B', exactBytes(stats.completedBytes)],
            ['Completed today', bytes(stats.completedTodayBytes) || '0 B', exactBytes(stats.completedTodayBytes)],
            ['Current transfer rate', `${bytes(stats.bytesPerSecond) || '0 B'}/s`, `${exactBytes(stats.bytesPerSecond)} per second`],
          ] },
          { title: 'Download counts', rows: [
            ['Completed', number(stats.completedCount), ''],
            ['Completed today', number(stats.completedTodayCount), ''],
            ['Failed', number(stats.failedCount), ''],
            ['Cancelled', number(stats.cancelledCount), ''],
            ['Retained in list', number(stats.retainedCount), ''],
            ['Archived history entries', number(stats.archivedCount), ''],
          ] },
          { title: 'Current activity', rows: [
            ['Active', number(stats.activeCount), ''],
            ['Queued', number(stats.queuedCount), ''],
            ['Stopped', number(stats.pausedCount), ''],
          ] },
        ]
      : [],
  );
  const captured = $derived(
    stats
      ? `Updated ${dateTime(stats.capturedAt)}` + (stats.firstRecordedAt ? ` · Tracking since ${dateOnly(stats.firstRecordedAt)}` : ' · No recorded downloads yet')
      : 'Reading the statistics…',
  );
</script>

<div class="mx-auto max-w-2xl px-4 py-5">
  <h1 class="text-[22px] font-semibold">Download statistics</h1>
  <p class="mt-1 text-[12.5px] text-muted" role="status">{captured}</p>
  {#if problem}<p class="mt-2 text-[12.5px] text-error" role="alert">{problem}</p>{/if}

  {#each sections as section (section.title)}
    <section class="mt-5">
      <h2 class="mb-1.5 text-[14px] font-semibold">{section.title}</h2>
      <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 rounded border border-divider px-3 py-2 text-[13px]">
        {#each section.rows as [label, value, exact] (label)}
          <dt class="text-muted">{label}</dt><dd class="tabular text-right" title={exact || value}>{value}</dd>
        {/each}
      </dl>
    </section>
  {/each}

  <p class="mt-5 text-[12.5px] text-muted">Recorded data measures saved download progress, not network-interface traffic. Removed entries stay in the statistics.</p>
</div>
