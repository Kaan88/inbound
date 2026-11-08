<script lang="ts">
  import { onMount } from 'svelte';
  import Icon from './Icon.svelte';
  import { speed } from '../lib/format';
  import { graphCeiling, SpeedHistory, speedSample } from '../lib/list';
  import { store } from '../lib/store.svelte';

  // The collapsible bar under the list: the total speed of the active downloads, a sample a second. It opens upward.
  // A gap is a moment the server could not be reached; a download that went quiet counts as zero.
  const width = 600;
  const height = 100;
  const history = new SpeedHistory(width);
  let version = $state(0);
  let open = $state(readOpen());

  function readOpen(): boolean {
    try {
      return localStorage.getItem('inbound.speedGraph') !== 'closed';
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
  $effect(() => {
    if (!open || !visible) return;
    // Time that went by unsampled shows as a gap.
    if (history.samples.length > 0) history.add(null);
    const timer = setInterval(() => {
      history.add(speedSample(Object.values(store.model.jobs), store.connection === 'open', store.model.snapshots > 0, Date.now()));
      version++;
    }, 1000);
    return () => clearInterval(timer);
  });

  const now = $derived.by(() => {
    void version;
    const last = history.samples[history.samples.length - 1];
    return last ?? null;
  });
  const ceiling = $derived.by(() => {
    void version;
    return graphCeiling(history.peak());
  });
  const shape = $derived.by(() => {
    void version;
    const samples = history.samples;
    // Newest at the right edge; the line starts where the history does.
    const step = width / Math.max(1, history.limit - 1);
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
        class="size-full overflow-visible rounded border border-divider bg-surface"
        viewBox="0 0 {width} {height}"
        preserveAspectRatio="none"
        role="img"
        aria-label="Total download speed over the last {history.samples.length} seconds. Now {now === null ? 'no data' : speed(now) || '0 B/s'}; the top of the graph is {speed(ceiling)}."
      >
        <path d={shape.area} fill="var(--progress)" fill-opacity="0.22" />
        <path d={shape.line} fill="none" stroke="var(--progress)" stroke-width="1.5" vector-effect="non-scaling-stroke" />
      </svg>
      <span class="tabular pointer-events-none absolute right-5 top-1 text-[11px] text-muted">{speed(ceiling)}</span>
    </div>
  {/if}
</div>
