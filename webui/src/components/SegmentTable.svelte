<script lang="ts">
  import type { SegmentRowView } from '../lib/details';
  import ProgressBar from './ProgressBar.svelte';

  // The "Segment details" expander: N., Progress, Downloaded, Speed, Info (JobDetailsPanel).
  let { rows, problem = '' }: { rows: SegmentRowView[]; problem?: string } = $props();
  let open = $state(false);
</script>

<details class="mt-4 rounded border border-divider" bind:open>
  <summary class="cursor-pointer px-3 py-1.5 text-[13px] font-semibold">Segment details</summary>
  <div class="overflow-x-auto px-3 pb-3 pt-1">
    {#if problem}<p class="mb-2 text-[12px] text-error">{problem}</p>{/if}
    {#if rows.length === 0}
      <p class="text-[12px] text-muted">No segment information yet.</p>
    {:else}
      <table class="w-full min-w-[420px] text-[12px]">
        <thead>
          <tr class="text-left text-muted">
            <th class="w-8 py-1 font-semibold">N.</th><th class="font-semibold">Progress</th>
            <th class="w-36 text-right font-semibold">Downloaded</th><th class="w-20 text-right font-semibold">Speed</th><th class="w-32 pl-3 font-semibold">Info</th>
          </tr>
        </thead>
        <tbody>
          {#each rows as row (row.segment.start)}
            <tr class="border-t border-divider/60">
              <td class="py-1 text-muted">{row.number}</td>
              <td class="pr-3"><ProgressBar value={row.percent} label="Segment {row.number}" /></td>
              <td class="tabular text-right">{row.downloaded}</td>
              <td class="tabular text-right {row.speed === '—' ? 'text-muted' : ''}">{row.speed}</td>
              <td class="pl-3 {row.info === 'Waiting' ? 'text-muted' : ''}">{row.info}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  </div>
</details>
