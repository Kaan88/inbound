<script lang="ts">
  import Icon from '../../components/Icon.svelte';
  import { speed } from '../../lib/format';
  import type { SiteTransfer } from '../../lib/sites';
  import { barSpeeds, barSummary, failedCount } from '../../lib/sitesTransfers';

  // The Transfers bar (SITES.11 R6b): one slim line under the remote files pages that says what moves and opens the list.
  let { transfers, open = $bindable(false) }: { transfers: SiteTransfer[]; open?: boolean } = $props();

  const speeds = $derived(barSpeeds(transfers));
  const summary = $derived(barSummary(transfers));
  const failed = $derived(failedCount(transfers));
  const total = $derived(speeds.down + speeds.up);
</script>

<button class="flex min-h-11 w-full shrink-0 items-center gap-3 border-t border-divider bg-chrome px-3 py-1.5 text-left text-[12.5px] hover:bg-hover" aria-expanded={open} aria-controls="remote-transfers" onclick={() => (open = !open)}>
  <strong class="font-semibold">Transfers</strong>
  <span class="tabular hidden items-center gap-1 text-icon-info sm:inline-flex"><Icon name="download" size={13} /> {speed(speeds.down) || '0 B/s'}</span>
  <span class="tabular hidden items-center gap-1 text-accent-orange sm:inline-flex"><Icon name="upload" size={13} /> {speed(speeds.up) || '0 B/s'}</span>
  <span class="min-w-0 truncate {summary.includes('waiting') && !summary.includes('running') ? 'tone-waiting' : 'text-muted'}">{summary}{total > 0 ? ` · ${speed(total)}` : ''}{failed ? ` · ${failed} failed` : ''}</span>
  <span class="flex-1"></span>
  <Icon name={open ? 'chevron-down' : 'chevron-up'} size={16} class="shrink-0 text-muted" />
</button>

<style>
  /* The upload arrow takes the status orange the sidebar and the mockup use for uploads. */
  .text-accent-orange { color: var(--status-waiting); }
</style>
