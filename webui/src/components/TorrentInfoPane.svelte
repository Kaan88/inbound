<script lang="ts">
  import { untrack } from 'svelte';
  import { copyText } from '../lib/clipboard';
  import { store } from '../lib/store.svelte';
  import { canRecheck, type TorrentDetails } from '../lib/torrentControls';
  import { hasTorrentControls, type TrackerDto } from '../lib/torrentDetails';
  import { describeInfo, detailsDue, detailsKey, infoMinWidth, infoReadKey, infoSeen, trackersDue } from '../lib/torrentInfo';
  import { magnetToCopy } from '../lib/torrentList';
  import type { Job } from '../lib/types';
  import Icon from './Icon.svelte';
  import ProgressBar from './ProgressBar.svelte';

  // T7: the one selected torrent's facts beside the list (the desktop window's Torrent Info pane). Shown inline from
  // 1440 px wide; it reads the trackers' summary on the torrent's own updates (at most every 10 s while it runs) and the
  // details for the v2 hash once per state until it is known. No timer: hidden, narrow or a background tab, nothing is read.
  let { job, onmore, onrecheck, onhide }: { job: Job; onmore: () => void; onrecheck: () => void; onhide: () => void } = $props();

  let visible = $state(typeof document === 'undefined' ? true : document.visibilityState !== 'hidden');
  let wide = $state(typeof window === 'undefined' || typeof window.matchMedia !== 'function' ? true : window.matchMedia(`(min-width: ${infoMinWidth}px)`).matches);
  $effect(() => {
    const changed = () => (visible = document.visibilityState !== 'hidden');
    document.addEventListener('visibilitychange', changed);
    if (typeof window.matchMedia !== 'function') return () => document.removeEventListener('visibilitychange', changed);
    const query = window.matchMedia(`(min-width: ${infoMinWidth}px)`);
    const resized = () => (wide = query.matches);
    query.addEventListener('change', resized);
    return () => {
      document.removeEventListener('visibilitychange', changed);
      query.removeEventListener('change', resized);
    };
  });

  const connected = $derived(store.connection === 'open');
  const offered = $derived(store.has('torrents'));
  const seen = $derived(infoSeen({ visible, wide, job }));

  let trackers = $state<TrackerDto[] | null>(null);
  let infoHashV2 = $state<string | null>(null);
  let shownId = '';
  let lastKey = '';
  let lastReadAt = -Infinity;
  let readingTrackers = false;
  let detailsFor = '';
  // Bumped when a read ends after another torrent was selected, so the one shown now is read without waiting for an update.
  let shownChanged = $state(0);

  async function readTrackers(asked: string) {
    readingTrackers = true;
    lastReadAt = Date.now();
    try {
      const result = await store.ask<{ ok?: boolean; running?: boolean; trackers?: TrackerDto[] }>('torrent.trackers', { jobId: asked });
      if (asked !== shownId) return;
      trackers = result.ok !== false && result.running ? result.trackers ?? [] : null;
    } catch {
      // The next update tries again once it is due.
    } finally {
      readingTrackers = false;
      if (asked !== shownId) shownChanged++;
    }
  }

  let readingDetails = false;
  async function readDetails(asked: string, key: string) {
    // Keyed by its state too: the server gives the v2 hash only while it runs, so a start asks again.
    detailsFor = key;
    readingDetails = true;
    try {
      const result = await store.ask<TorrentDetails>('torrent.details', { jobId: asked });
      if (asked !== shownId) return;
      if (result.infoHashV2 && result.infoHashV2.length === 64) infoHashV2 = result.infoHashV2;
    } catch {
      if (asked === shownId) detailsFor = '';
    } finally {
      readingDetails = false;
      if (asked !== shownId) shownChanged++;
    }
  }

  // Runs on each of the torrent's updates (the job object is new each time), never on a timer.
  $effect(() => {
    const current = job;
    const isSeen = seen;
    const isConnected = connected;
    const isOffered = offered;
    void shownChanged;
    untrack(() => {
      if (current.id !== shownId) {
        shownId = current.id;
        trackers = null;
        infoHashV2 = null;
        lastKey = '';
        detailsFor = '';
      }
      if (!isSeen || !isConnected) return;
      const key = infoReadKey(current);
      if (!readingTrackers && trackersDue({ seen: isSeen, connected: isConnected, offered: isOffered, job: current, keyChanged: key !== lastKey, sinceMs: Date.now() - lastReadAt })) {
        lastKey = key;
        void readTrackers(current.id);
      }
      const readFor = detailsKey(current);
      if (!readingDetails && detailsDue({ seen: isSeen, connected: isConnected, controls: hasTorrentControls(current), readForThisKey: detailsFor === readFor, v2Known: !!infoHashV2 })) {
        void readDetails(current.id, readFor);
      }
    });
  });

  const view = $derived(describeInfo(job, store.model.settings, infoHashV2, trackers, offered));
  const magnet = $derived(magnetToCopy(job.torrent));

  async function copy(text: string, done: string) {
    if (await copyText(text)) store.toast(done);
  }
</script>

<aside class="flex min-h-0 w-full flex-col border-l border-divider bg-surface text-[12.5px]" aria-label="Torrent info">
  <div class="flex h-[27px] shrink-0 items-center justify-between border-b border-divider bg-chrome pl-3 pr-1 text-[12px] font-semibold">
    <span>Torrent Info</span>
    <button class="btn-icon h-6 w-6" title="Hide Torrent Info. More › Torrent Info shows it again." aria-label="Hide Torrent Info" onclick={onhide}><Icon name="close" size={13} /></button>
  </div>
  <div class="min-h-0 flex-1 overflow-y-auto px-3 py-2.5">
    <p class="break-words text-[13px] font-semibold" title={view.name}>{view.name}</p>
    <p class="mt-1">{view.status}</p>
    <div class="mt-1.5"><ProgressBar value={view.percent} label="Downloaded" /></div>
    <p class="tabular mt-1 text-[12px] text-muted">{view.progress}</p>
    <dl class="mt-3 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1.5">
      <dt class="text-[12px] text-muted">Down</dt><dd class="tabular">{view.down}</dd>
      <dt class="text-[12px] text-muted">Up</dt><dd class="tabular">{view.up}</dd>
      <dt class="text-[12px] text-muted">Connected</dt><dd>{view.swarm}</dd>
      <dt class="text-[12px] text-muted">Time left</dt><dd class="tabular">{view.timeLeft}</dd>
      <dt class="text-[12px] text-muted">Ratio</dt><dd class="tabular">{view.ratio}</dd>
      <dt class="text-[12px] text-muted">Seeding goal</dt><dd>{view.goal}</dd>
      <dt class="text-[12px] text-muted">Trackers</dt><dd>{view.trackers}</dd>
      <dt class="text-[12px] text-muted">Save to</dt><dd class="break-all">{view.folder}</dd>
      <dt class="text-[12px] text-muted">Added</dt><dd>{view.added}</dd>
      <dt class="text-[12px] text-muted">Info hash</dt>
      <dd class="min-w-0">
        <span class="break-all font-mono text-[12px]">{view.infoHash}</span>
        {#if job.torrent?.infoHash}<button class="ml-1 text-[12px] text-accent hover:underline" onclick={() => void copy(infoHashV2 ? `${job.torrent!.infoHash}\n${infoHashV2}` : job.torrent!.infoHash, 'Info hash copied.')}>Copy</button>{/if}
      </dd>
    </dl>
    <div class="mt-3 flex flex-wrap gap-1.5">
      {#if magnet}<button class="btn h-7 py-0 text-[12px]" onclick={() => void copy(magnet ?? '', 'Magnet link copied.')}>Copy Magnet Link</button>{/if}
      {#if canRecheck(job)}<button class="btn h-7 py-0 text-[12px]" disabled={!connected} title="Check the torrent's files on disk again" onclick={onrecheck}>Force Recheck</button>{/if}
      <button class="btn h-7 py-0 text-[12px]" title="Open Progress Details: files, peers, trackers, pieces and options" onclick={onmore}>More details…</button>
    </div>
  </div>
</aside>
