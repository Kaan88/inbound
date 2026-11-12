<script lang="ts">
  import { untrack } from 'svelte';
  import { store } from '../lib/store.svelte';
  import { detailsPollMs, overviewRows, type TorrentDetails } from '../lib/torrentControls';
  import { mayRun } from '../lib/torrentDetails';
  import type { Job } from '../lib/types';

  // The Overview's facts of a torrent (T3, torrent.details; mockup c): how it was added, its v2 hash, whether it is
  // private, its pieces and seeding goal, and while it runs its connections and next announce. Asked once when shown, and
  // every 5 s only while the torrent runs and the page is visible.
  let { job }: { job: Job } = $props();

  let details = $state<TorrentDetails | null>(null);
  let problem = $state('');
  let busy = false;
  let visible = $state(typeof document === 'undefined' ? true : document.visibilityState !== 'hidden');
  $effect(() => {
    const changed = () => (visible = document.visibilityState !== 'hidden');
    document.addEventListener('visibilitychange', changed);
    return () => document.removeEventListener('visibilitychange', changed);
  });

  const connected = $derived(store.connection === 'open');
  const running = $derived(mayRun(job));

  async function read() {
    if (busy || !connected) return;
    busy = true;
    const asked = job.id;
    try {
      const result = await store.ask<TorrentDetails>('torrent.details', { jobId: asked });
      if (asked !== job.id) return;
      details = result;
      problem = '';
    } catch (error) {
      if (asked === job.id) problem = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }

  $effect(() => {
    if (!connected) return;
    untrack(() => void read());
    if (!visible || !running) return;
    const timer = setInterval(() => void read(), detailsPollMs);
    return () => clearInterval(timer);
  });

  const rows = $derived(job.torrent ? overviewRows(details, job.torrent, store.model.settings) : []);
</script>

<dl class="grid grid-cols-[minmax(110px,auto)_1fr] gap-x-4 gap-y-1.5 text-[13px]" aria-label="Torrent facts">
  {#each rows as row (row.label)}
    <dt class="text-muted">{row.label}</dt><dd class="min-w-0 {row.wide ? 'break-all font-mono text-[12px]' : ''}">{row.value}</dd>
  {/each}
</dl>
{#if problem}<p class="mt-1 text-[12.5px] text-error" role="alert">Couldn't read the torrent's details. {problem}</p>{/if}
