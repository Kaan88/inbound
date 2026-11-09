<script lang="ts">
  import { pathParts } from '../lib/paths';

  // A path that is cut in the middle when it doesn't fit: the start shrinks with an ellipsis, the last folder stays
  // (U-WU-5). The whole path is the tooltip and what a screen reader reads.
  let { path, class: className = '' }: { path: string; class?: string } = $props();
  const parts = $derived(pathParts(path));
</script>

<span class="flex min-w-0 font-mono {className}" title={path} aria-label={path}>
  {#if parts.head}<span class="min-w-0 truncate" aria-hidden="true">{parts.head}</span>{/if}
  <span class="max-w-[75%] shrink-0 truncate" aria-hidden="true">{parts.tail}</span>
</span>
