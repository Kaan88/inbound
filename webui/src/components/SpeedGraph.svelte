<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import Icon from './Icon.svelte';
  import { speed } from '../lib/format';
  import { GraphScale } from '../lib/graphScale';
  import { agoText, anyActive, hoverPoint, liveSpeed, ServerClock, SpeedHistory, speedGraphWindow, speedSample } from '../lib/list';
  import { store } from '../lib/store.svelte';
  import type { SpeedHistoryResult } from '../lib/types';

  // The collapsible bar under the list: the total speed of the active downloads, a sample a second, the last ten minutes. It opens upward.
  // A gap is a moment the server could not be reached; a download that went quiet counts as zero.
  const width = 600;
  const height = 100;
  const history = new SpeedHistory();
  const clock = new ServerClock();
  let version = $state(0);
  let open = $state(readOpen());

  // Open by default, except on a phone, where it would take a sixth of the height (U-WU-22); a choice made is kept.
  function readOpen(): boolean {
    let saved: string | null = null;
    try {
      saved = localStorage.getItem('inbound.speedGraph');
    } catch {
      // A private window: the default.
    }
    if (saved === 'open') return true;
    if (saved === 'closed') return false;
    try {
      return !matchMedia('(max-width: 767px)').matches;
    } catch {
      return true;
    }
  }

  function toggle() {
    open = !open;
    try {
      localStorage.setItem('inbound.speedGraph', open ? 'open' : 'closed');
    } catch {
      // A private window: the choice lasts until the page closes.
    }
  }

  // A timer runs only while the graph is open and the page is showing, so a closed graph or a hidden tab costs nothing.
  let visible = $state(typeof document === 'undefined' ? true : !document.hidden);
  onMount(() => {
    const changed = () => (visible = !document.hidden);
    document.addEventListener('visibilitychange', changed);
    return () => document.removeEventListener('visibilitychange', changed);
  });

  // Whenever the graph shows with a connection (opened, the tab visible again, the connection back) the server's own last
  // hour replaces what this page sampled, so time spent hidden or offline is filled in. A server without speed.history
  // answers an error, and the graph then fills as before.
  const canAsk = $derived(open && visible && store.connection === 'open' && store.model.snapshots > 0);
  $effect(() => {
    if (!canAsk) return;
    let stale = false;
    untrack(() => {
      void store.ask<SpeedHistoryResult>('speed.history', {}).then((result) => {
        if (stale || result.intervalSeconds !== 1 || !Array.isArray(result.bytesPerSecond)) return;
        history.replace(result.bytesPerSecond, result.start);
        refit = true;
        version++;
      }).catch(() => undefined);
    });
    return () => (stale = true);
  });

  // Idle: connected, and nothing transfers. Then no timer runs (audit 4 M11): the line stops at 0, and the seconds that
  // went by idle are added as zeros when something starts, the graph is closed or the tab hidden.
  const idle = $derived(store.connection === 'open' && store.model.snapshots > 0 && !anyActive(Object.values(store.model.jobs)));
  let afterIdle = false;
  $effect(() => {
    if (!open || !visible) return;
    if (idle) {
      const since = Date.now();
      untrack(() => {
        history.add(0);
        version++;
      });
      return () => {
        const seconds = Math.min(history.limit, Math.floor((Date.now() - since) / 1000));
        for (let i = 0; i < seconds; i++) history.add(0);
        version++;
        afterIdle = true;
      };
    }
    // Time that went by unsampled (hidden, closed) shows as a gap; idle time was filled in just above.
    if (history.samples.length > 0 && !afterIdle) history.add(null);
    afterIdle = false;
    const timer = setInterval(() => {
      const jobs = Object.values(store.model.jobs);
      const now = Date.now();
      clock.observe(jobs, now);
      history.add(speedSample(jobs, store.connection === 'open', store.model.snapshots > 0, now, clock.offsetMs));
      version++;
    }, 1000);
    return () => clearInterval(timer);
  });

  // The point under the pointer (mouse, pen or a finger): set only while one is over the graph, so nothing runs otherwise.
  let hoverX = $state<number | null>(null);
  function point(event: PointerEvent) {
    const box = (event.currentTarget as Element).getBoundingClientRect();
    const element = event.currentTarget as HTMLElement;
    // Inside the border: the drawing fills the content box.
    const inner = box.width - element.clientLeft * 2;
    if (inner <= 0) return;
    hoverX = ((event.clientX - box.left - element.clientLeft) / inner) * width;
  }
  function leave() {
    hoverX = null;
  }
  const hover = $derived.by(() => {
    void version;
    if (hoverX === null) return null;
    const found = hoverPoint(history.recent(), hoverX, width, speedGraphWindow);
    if (!found) return null;
    const step = width / (speedGraphWindow - 1);
    const x = width - found.age * step;
    const clock = new Date(Date.now() - found.age * 1000).toLocaleTimeString([], { hour12: false });
    const text = found.rate === null ? `No figure · ${clock}` : `${speed(found.rate) || '0 B/s'} · ${clock} · ${agoText(found.age)}`;
    const y = found.rate === null ? null : height - Math.min(1, found.rate / ceiling) * height;
    return { x, y, text, flip: x / width > 0.6, left: (x / width) * 100 };
  });

  // The figure beside the title is the one the status bar shows, worked out from the same downloads at the same moment
  // (U-WU-14); the line is drawn from the samples, one a second.
  const now = $derived(liveSpeed(Object.values(store.model.jobs), store.connection === 'open', store.model.snapshots > 0));
  // history is not reactive state; version is, so the label follows the samples as shape and ceiling do.
  const shownSeconds = $derived.by(() => {
    void version;
    return Math.min(history.samples.length, speedGraphWindow);
  });
  // UI.B23: grows at once, shrinks only after a hold and a step at a time; the server's history fits it afresh.
  const scale = new GraphScale();
  let refit = true;
  const ceiling = $derived.by(() => {
    void version;
    const peak = history.peak();
    if (!refit) return scale.update(peak, Date.now());
    refit = false;
    return scale.reset(peak);
  });
  const shape = $derived.by(() => {
    void version;
    const samples = history.recent();
    // Newest at the right edge; the line starts where the history does.
    const step = width / (speedGraphWindow - 1);
    const offset = width - (samples.length - 1) * step;
    let line = '';
    let area = '';
    let run: string[] = [];
    const flush = () => {
      if (run.length > 1) {
        line += `M${run.join('L')}`;
        const first = run[0].split(',')[0];
        const last = run[run.length - 1].split(',')[0];
        area += `M${first},${height}L${run.join('L')}L${last},${height}Z`;
      }
      run = [];
    };
    samples.forEach((sample, index) => {
      if (sample === null) return flush();
      run.push(`${(offset + index * step).toFixed(1)},${(height - Math.min(1, sample / ceiling) * height).toFixed(1)}`);
    });
    flush();
    return { line, area };
  });
</script>

<div class="shrink-0 border-t border-divider bg-chrome">
  <button class="flex h-6 w-full items-center gap-2 px-3 text-left text-[12px] text-muted hover:bg-hover" aria-expanded={open} aria-controls="speed-graph" onclick={toggle}>
    <Icon name={open ? 'chevron-down' : 'chevron-up'} size={14} />
    <span>Speed graph</span>
    <span class="tabular text-text">{now === null ? 'no data' : (speed(now) || '0 B/s')}</span>
  </button>
  {#if open}
    <div id="speed-graph" class="relative h-28 px-3 pb-2">
      <svg
        class="size-full touch-pan-y overflow-visible rounded border border-divider bg-surface"
        onpointerdown={point}
        onpointermove={point}
        onpointerleave={leave}
        onpointercancel={leave}
        viewBox="0 0 {width} {height}"
        preserveAspectRatio="none"
        role="img"
        aria-label="Total download speed over the last {shownSeconds} seconds. Now {now === null ? 'no data' : speed(now) || '0 B/s'}; the top of the graph is {speed(ceiling)}."
      >
        <path d={shape.area} fill="var(--progress)" fill-opacity="0.22" />
        <path d={shape.line} fill="none" stroke="var(--progress)" stroke-width="1.5" vector-effect="non-scaling-stroke" />
        {#if hover}
          <line x1={hover.x} x2={hover.x} y1="0" y2={height} stroke="var(--muted)" stroke-opacity="0.6" stroke-width="1" vector-effect="non-scaling-stroke" />
        {/if}
      </svg>
      {#if hover}
        {#if hover.y !== null}
          <span class="pointer-events-none absolute size-[7px] rounded-full bg-progress" style="left: calc(0.75rem + 1px + (100% - 1.5rem - 2px) * {hover.left / 100} - 3.5px); top: calc(1px + (100% - 0.5rem - 2px) * {hover.y / height} - 3.5px)"></span>
        {/if}
        <span class="tabular pointer-events-none absolute top-0 whitespace-nowrap rounded border border-divider bg-chrome px-1.5 py-0.5 text-[11px] text-text" style="left: calc(0.75rem + 1px + (100% - 1.5rem - 2px) * {hover.left / 100}); transform: translateX({hover.flip ? 'calc(-100% - 8px)' : '8px'})">{hover.text}</span>
      {/if}
      <span class="tabular pointer-events-none absolute right-5 top-1 text-[11px] text-muted">{speed(ceiling)}</span>
    </div>
  {/if}
</div>
