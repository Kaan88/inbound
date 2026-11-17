<script lang="ts">
  import { sourceChoices } from '../../lib/sitesExtras';
  import { store } from '../../lib/store.svelte';

  // The left pane of Two panes switches between the server's own folders and any remote, the right pane's remote
  // included (SITES.14). The right pane is always the remote the page is on.
  let { value, onchange }: { value: string; onchange: (value: string) => void } = $props();
  const choices = $derived(sourceChoices(store.model.sites, store.texts.hostName));
</script>

<select class="field w-auto min-w-0 max-w-[14rem] flex-1 py-0.5 text-[13px] font-semibold" aria-label="What the left pane shows" {value}
  onchange={(event) => onchange(event.currentTarget.value)}>
  {#each choices as choice (choice.value)}<option value={choice.value}>{choice.label}</option>{/each}
</select>
