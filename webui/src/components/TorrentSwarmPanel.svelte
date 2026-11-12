<script lang="ts">
  import { untrack } from 'svelte';
  import { speed } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import {
    addSpeedPoint, cellNote, hasTorrentControls, mayRun, notRunningText, peersCount, pieceCells, piecesHad, piecesSummary, ratioText, shouldAsk, speedLine,
    swarmPollMs, uploadedText, type PeerDto, type PiecesDto, type SpeedPoint, type SwarmTab, type TrackerDto,
  } from '../lib/torrentDetails';
  import { announceSaid, canAnnounce, detailsPollMs, wasteText, type TorrentDetails } from '../lib/torrentControls';
  import {
    ariaSort, heldOrder, nextColumnSort, parseColumnSort, peerSortKeys, sortedPeerRows, sortedTrackerRows, sortMark, trackerSortKeys,
    type ColumnSort, type PeerSortKey, type TrackerSortKey,
  } from '../lib/detailsSort';
  import type { Job } from '../lib/types';

  // A torrent's Peers, Trackers or Pieces & Speed tab (T2, c-torrent-details.html). It asks the server only while it is
  // shown, the page is visible and the torrent may run, every 2 s; otherwise no timer runs at all.
  let { job, kind }: { job: Job; kind: SwarmTab } = $props();

  const cells = 200;
  let visible = $state(typeof document === 'undefined' ? true : document.visibilityState !== 'hidden');
  $effect(() => {
    const changed = () => (visible = document.visibilityState !== 'hidden');
    document.addEventListener('visibilitychange', changed);
    return () => document.removeEventListener('visibilitychange', changed);
  });

  const connected = $derived(store.connection === 'open');
  const running = $derived(mayRun(job));
  let busy = false;
  let answered = $state<boolean | null>(null);
  let problem = $state('');
  let peers = $state<PeerDto[]>([]);
  let trackers = $state<TrackerDto[]>([]);
  let pieces = $state<PiecesDto | null>(null);
  let points = $state<SpeedPoint[]>([]);
  /** T3: the Pieces & Speed tab's wasted bytes and hash failures (torrent.details), asked every 5 s with the pieces. */
  let details = $state<TorrentDetails | null>(null);
  let detailsAt = 0;
  let announcing = $state(false);
  /** The Host's answer when Announce now is refused, kept apart from the read errors so a later read doesn't clear it. */
  let announceProblem = $state('');

  const type = $derived(kind === 'peers' ? 'torrent.peers' : kind === 'trackers' ? 'torrent.trackers' : 'torrent.pieces');

  async function read() {
    if (!shouldAsk({ visible, connected, running, busy })) return;
    busy = true;
    // A reply belongs to the tab and torrent it was asked for; one that arrives after either changed is dropped.
    const askedKind = kind;
    const askedJob = job.id;
    try {
      const result = await store.ask<{ running?: boolean; peers?: PeerDto[]; trackers?: TrackerDto[] } & Partial<PiecesDto>>(type, { jobId: askedJob });
      if (askedKind !== kind || askedJob !== job.id) return;
      problem = '';
      answered = result.running === true;
      if (kind === 'peers') peers = result.peers ?? [];
      else if (kind === 'trackers') trackers = result.trackers ?? [];
      else pieces = result.running ? { running: true, count: result.count ?? 0, pieceLength: result.pieceLength ?? 0, have: result.have ?? 0, ranges: result.ranges, bitmap: result.bitmap } : null;
      if (kind === 'pieces' && result.running) points = addSpeedPoint(points, { down: job.bytesPerSecond ?? 0, up: job.torrent?.uploadRate ?? 0 });
      if (kind === 'pieces' && result.running && hasTorrentControls(job) && Date.now() - detailsAt >= detailsPollMs) {
        detailsAt = Date.now();
        // The pieces were read; a refused details read only leaves the wasted bytes out, it is not a read error.
        try {
          const facts = await store.ask<TorrentDetails>('torrent.details', { jobId: askedJob });
          if (askedKind === kind && askedJob === job.id) details = facts;
        } catch {
          if (askedKind === kind && askedJob === job.id) details = null;
        }
      }
    } catch (error) {
      if (askedKind !== kind || askedJob !== job.id) return;
      problem = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }

  // The timer exists only while every condition holds; a hidden page, a lost connection or a stopped torrent ends it.
  $effect(() => {
    if (!visible || !connected || !running) return;
    untrack(() => void read());
    const timer = setInterval(() => void read(), swarmPollMs);
    return () => clearInterval(timer);
  });

  const notRunning = $derived(!running || answered === false);
  const waste = $derived(wasteText(details));

  /** T3: Announce now asks the trackers (and the DHT) for peers at once; the list shows it on its next read. */
  async function announce() {
    announcing = true;
    announceProblem = '';
    try {
      await store.ask('torrent.announce', { jobId: job.id });
      store.toast(announceSaid);
    } catch (error) {
      announceProblem = error instanceof Error ? error.message : String(error);
    } finally {
      announcing = false;
    }
  }
  // T12: the tables sort by a click on a header (kept in this browser), and hold still while the pointer is over them.
  function load(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }
  function save(key: string, value: string) {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Private windows may refuse; the order still holds for this page.
    }
  }
  let peerSort = $state<ColumnSort<PeerSortKey>>(parseColumnSort(load('inbound.peerSort'), peerSortKeys));
  let trackerSort = $state<ColumnSort<TrackerSortKey>>(parseColumnSort(load('inbound.trackerSort'), trackerSortKeys));
  let holding = $state(false);
  // A table that goes (no peers left, the torrent stopped, another tab) takes the pointer's leave with it: let go then,
  // or later rows would keep held places until the pointer came back.
  const tableShown = $derived(
    !notRunning && answered !== null && (kind === 'peers' ? peers.length > 0 : kind === 'trackers' ? trackers.length > 0 : false),
  );
  $effect(() => {
    if (!tableShown) holding = false;
  });
  // A peer keeps the number it got when first seen on this torrent, so "Peer 3" stays one peer (as in the Windows app).
  const peerNumbers = new Map<string, number>();
  let numbersFor = '';
  function numbersOf(jobId: string): Map<string, number> {
    if (numbersFor !== jobId) {
      peerNumbers.clear();
      numbersFor = jobId;
    }
    return peerNumbers;
  }
  // The order on screen, which a held table keeps. Not state: it follows what was drawn.
  let shownPeers: string[] = [];
  let shownTrackers: string[] = [];
  const peerList = $derived.by(() => {
    const sorted = sortedPeerRows(peers, peerSort, numbersOf(job.id));
    const list = holding ? heldOrder(shownPeers, sorted, (row) => row.key) : sorted;
    shownPeers = list.map((row) => row.key);
    return list;
  });
  const trackerList = $derived.by(() => {
    const sorted = sortedTrackerRows(trackers, trackerSort);
    const list = holding ? heldOrder(shownTrackers, sorted, (row) => row.key) : sorted;
    shownTrackers = list.map((row) => row.key);
    return list;
  });
  function sortPeers(key: PeerSortKey) {
    const next = nextColumnSort(peerSort, key);
    // A click shows the new order at once, pointer or not.
    shownPeers = sortedPeerRows(peers, next, numbersOf(job.id)).map((row) => row.key);
    peerSort = next;
    save('inbound.peerSort', JSON.stringify(next));
  }
  function sortTrackers(key: TrackerSortKey) {
    const next = nextColumnSort(trackerSort, key);
    shownTrackers = sortedTrackerRows(trackers, next).map((row) => row.key);
    trackerSort = next;
    save('inbound.trackerSort', JSON.stringify(next));
  }
  const pieceBar = $derived(pieces ? pieceCells(piecesHad(pieces), cells) : []);
  const graphWidth = 300;
  const graphHeight = 60;
</script>

{#snippet peerHead(key: PeerSortKey, label: string, cls: string)}
  <th class="px-2 py-1 font-normal {cls}" aria-sort={ariaSort(peerSort, key)}>
    <button class="header-cell" onclick={() => sortPeers(key)} title="Sort by {label}">{label}<span class="sort-mark" aria-hidden="true">{sortMark(peerSort, key)}</span></button>
  </th>
{/snippet}

{#snippet trackerHead(key: TrackerSortKey, label: string, cls: string)}
  <th class="px-2 py-1 font-normal {cls}" aria-sort={ariaSort(trackerSort, key)}>
    <button class="header-cell" onclick={() => sortTrackers(key)} title="Sort by {label}">{label}<span class="sort-mark" aria-hidden="true">{sortMark(trackerSort, key)}</span></button>
  </th>
{/snippet}

<div class="grid gap-2 text-[13px]">
  {#if problem}
    <p class="text-error" role="alert">Couldn't read this from the server. {problem}</p>
  {/if}
  {#if notRunning && kind !== 'pieces'}
    <p class="text-muted" role="status">{notRunningText}</p>
  {:else if kind === 'peers'}
    {#if answered === null}
      <p class="text-muted">Reading…</p>
    {:else if peerList.length === 0}
      <p class="text-muted">No peers connected right now.</p>
    {:else}
      <p class="text-muted">{peersCount(peerList.length)}</p>
      <div class="max-h-72 overflow-auto rounded border border-divider" role="region" aria-label="Peers" onpointerenter={() => (holding = true)} onpointerleave={() => (holding = false)}>
        <table class="w-full min-w-[520px] text-left text-[12.5px]">
          <thead class="sticky top-0 bg-chrome text-muted">
            <tr>
              {@render peerHead('peer', 'Peer', '')}{@render peerHead('client', 'Client', '')}{@render peerHead('flags', 'Connection', '')}
              {@render peerHead('has', 'Has', 'text-right')}{@render peerHead('down', 'Down', 'text-right')}{@render peerHead('up', 'Up', 'text-right')}
            </tr>
          </thead>
          <tbody>
            {#each peerList as peer (peer.key)}
              <tr class="border-t border-divider">
                <td class="px-2 py-1 whitespace-nowrap" title={peer.address}>{peer.label}</td>
                <td class="max-w-40 truncate px-2 py-1" title={peer.client}>{peer.client}</td>
                <td class="px-2 py-1 whitespace-nowrap text-muted">{peer.flags}</td>
                <td class="tabular px-2 py-1 text-right">{peer.has}</td>
                <td class="tabular px-2 py-1 text-right whitespace-nowrap">{peer.down}</td>
                <td class="tabular px-2 py-1 text-right whitespace-nowrap">{peer.up}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
      <p class="hint">Peers see {store.model.hello?.hostKind === 'windows' ? "this PC's" : "the server's"} public IP address, and it sees theirs. A peer's address shows when you point at it.</p>
    {/if}
  {:else if kind === 'trackers'}
    {#if answered === null}
      <p class="text-muted">Reading…</p>
    {:else if trackerList.length === 0}
      <p class="text-muted">This torrent has no trackers. Peers are found through DHT.</p>
    {:else}
      <div class="max-h-72 overflow-auto rounded border border-divider" role="region" aria-label="Trackers" onpointerenter={() => (holding = true)} onpointerleave={() => (holding = false)}>
        <table class="w-full min-w-[520px] text-left text-[12.5px]">
          <thead class="sticky top-0 bg-chrome text-muted">
            <tr>
              {@render trackerHead('tracker', 'Tracker', '')}{@render trackerHead('status', 'Status', '')}{@render trackerHead('seeds', 'Seeds', 'text-right')}
              {@render trackerHead('peers', 'Peers', 'text-right')}{@render trackerHead('next', 'Next announce', 'text-right')}
            </tr>
          </thead>
          <tbody>
            {#each trackerList as tracker (tracker.key)}
              <tr class="border-t border-divider">
                <td class="max-w-64 truncate px-2 py-1 font-mono text-[12px]" title={tracker.url}>{tracker.url}</td>
                <td class="px-2 py-1 {tracker.failed ? 'text-error' : ''}">{tracker.status}</td>
                <td class="tabular px-2 py-1 text-right">{tracker.seeds}</td>
                <td class="tabular px-2 py-1 text-right">{tracker.peers}</td>
                <td class="tabular px-2 py-1 text-right whitespace-nowrap">{tracker.next}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
      <p class="hint">A tracker address that carries a key is shown up to the key.</p>
    {/if}
    {#if answered !== null && hasTorrentControls(job)}
      <div><button class="btn" disabled={!connected || announcing || !canAnnounce(job)} title={canAnnounce(job) ? 'Ask the trackers for peers now' : "The torrent isn't running"} onclick={() => void announce()}>{announcing ? 'Asking…' : 'Announce now'}</button></div>
      {#if announceProblem}<p class="text-error" role="alert">{announceProblem}</p>{/if}
    {/if}
  {:else}
    <div class="font-semibold">Pieces</div>
    {#if notRunning}
      <p class="text-muted" role="status">{notRunningText}</p>
    {:else if !pieces}
      <p class="text-muted">Reading…</p>
    {:else}
      <svg class="block h-[18px] w-full rounded" viewBox="0 0 {Math.max(1, pieceBar.length)} 1" preserveAspectRatio="none" role="img" aria-label={piecesSummary(pieces)}>
        <rect x="0" y="0" width={Math.max(1, pieceBar.length)} height="1" fill="var(--track)" />
        {#each pieceBar as value, i (i)}
          {#if value > 0}<rect x={i} y="0" width="1.02" height="1" fill="var(--progress)" fill-opacity={0.35 + 0.65 * value} />{/if}
        {/each}
      </svg>
      <p class="text-muted">{piecesSummary(pieces)}{cellNote(pieces.count, cells) ? ` · ${cellNote(pieces.count, cells)}` : ''}</p>
    {/if}
    <div class="mt-2 font-semibold">Speed</div>
    <dl class="grid grid-cols-[minmax(110px,auto)_1fr] gap-x-4 gap-y-1.5">
      <dt class="text-muted">Down</dt><dd class="tabular">{speed(job.bytesPerSecond ?? 0) || '0 B/s'}</dd>
      <dt class="text-muted">Up</dt><dd class="tabular">{speed(job.torrent?.uploadRate ?? 0) || '0 B/s'}</dd>
      <dt class="text-muted">Uploaded</dt><dd class="tabular">{job.torrent ? uploadedText(job.torrent) : `0 B · ratio ${ratioText(0)}`}</dd>
      {#if waste && !notRunning}<dt class="text-muted">Thrown away</dt><dd class="tabular">{waste}</dd>{/if}
    </dl>
    {#if points.length > 1}
      <svg class="block h-[60px] w-full" viewBox="0 0 {graphWidth} {graphHeight}" preserveAspectRatio="none" role="img" aria-label="Down and up speed while this tab was open">
        <polyline points={speedLine(points, (p) => p.down, graphWidth, graphHeight)} fill="none" stroke="var(--progress)" stroke-width="1.5" vector-effect="non-scaling-stroke" />
        <polyline points={speedLine(points, (p) => p.up, graphWidth, graphHeight)} fill="none" stroke="var(--icon-start)" stroke-width="1.5" vector-effect="non-scaling-stroke" />
      </svg>
      <p class="hint"><span style="color: var(--progress)">Down</span> and <span style="color: var(--icon-start)">up</span>, while this tab is open (the last 2 minutes).</p>
    {/if}
  {/if}
</div>

<style>
  /* The download list's headers (ListScreen): a plain button that sorts, with the arrow after its words. */
  .header-cell { padding: 0; background: transparent; white-space: nowrap; font: inherit; color: inherit; text-align: inherit; cursor: pointer; }
  .header-cell:hover { text-decoration: underline; }
  .sort-mark { margin-left: 3px; font-size: 9px; color: var(--muted); }
</style>
