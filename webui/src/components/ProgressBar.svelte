<script lang="ts">
  // As InboundProgressBarStyle: 6px, rounded, blue fill on a neutral track; no stripes, no text over it.
  let { value = null, height = 6, label = 'Progress' }: { value?: number | null; height?: number; label?: string } = $props();
</script>

<div
  class="w-full overflow-hidden rounded-full bg-track"
  style="height: {height}px"
  role="progressbar"
  aria-label={label}
  aria-valuemin="0"
  aria-valuemax="100"
  aria-valuenow={value === null ? undefined : Math.round(value)}
>
  {#if value === null}
    <div class="h-full w-1/3 animate-pulse rounded-full bg-progress/60"></div>
  {:else}
    <!-- PERF.19: the fill is full width and clipped from the right, rounded end kept. A width transition re-laid out the
         whole page on every frame while progress came in (about 120 layouts a second with one download); a clip-path
         transition only repaints the bar. -->
    <div class="h-full rounded-full bg-progress transition-[clip-path] duration-500 ease-out" style="clip-path: inset(0 {100 - Math.max(0, Math.min(100, value))}% 0 0 round 9999px)"></div>
  {/if}
</div>
