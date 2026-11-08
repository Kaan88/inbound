<script lang="ts">
  import type { Segment } from '../lib/types';

  // The segment map: start positions and progress by connection, over the whole file (SegmentMapBar).
  let { segments, total, fraction = 0 }: { segments: Segment[]; total: number; fraction?: number } = $props();
  const whole = $derived(Math.max(1, total));
</script>

<div
  class="relative h-3.5 w-full overflow-hidden rounded-sm bg-track"
  role="progressbar"
  aria-label="Start positions and download progress by connection"
  aria-valuemin="0"
  aria-valuemax="100"
  aria-valuenow={Math.round(segments.length ? (segments.reduce((sum, s) => sum + s.completed, 0) * 100) / whole : fraction * 100)}
  title="Start positions and download progress by connection"
>
  {#if segments.length === 0}
    <div class="absolute inset-y-0 left-0 bg-progress" style="width: {Math.max(0, Math.min(100, fraction * 100))}%"></div>
  {:else}
    {#each segments as segment, index (segment.start)}
      <div
        class="absolute inset-y-0 border-l border-surface/70 bg-progress/25"
        style="left: {(segment.start / whole) * 100}%; width: {((segment.end - segment.start + 1) / whole) * 100}%"
        class:border-l-0={index === 0}
      >
        <div class="h-full bg-progress" style="width: {Math.max(0, Math.min(100, (segment.completed * 100) / Math.max(1, segment.end - segment.start + 1)))}%"></div>
      </div>
    {/each}
  {/if}
</div>
