<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import { categoryChoices } from '../lib/details';
  import { store } from '../lib/store.svelte';
  import type { Job } from '../lib/types';

  // Change category for one or more downloads (D7). Existing files stay in their current folders.
  let { jobs, onclose }: { jobs: Job[]; onclose: () => void } = $props();

  const choices = $derived(categoryChoices(store.model.settings?.customCategories as { id: string; name: string }[] | undefined));
  let chosen = $state('');
  let busy = $state(false);

  async function apply() {
    busy = true;
    const errors: string[] = [];
    for (const job of jobs) {
      try {
        await store.ask('job.update', { jobId: job.id, categoryId: chosen });
      } catch (error) {
        errors.push(`${job.fileName}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }
    busy = false;
    if (errors.length) store.toast(`Some categories were not changed. ${errors.slice(0, 3).join(' ')}`, 'error');
    onclose();
  }
</script>

<Dialog title="Change category" {onclose}>
  <p class="mb-3 text-[13px]">{(jobs.length === 1 ? `Change the category of '${jobs[0].fileName}'.` : `Change the category of ${jobs.length} downloads.`) + ' Existing files stay in their current folders.'}</p>
  <label class="label" for="change-category">Category</label>
  <select id="change-category" class="field" bind:value={chosen}>
    {#each choices as choice (choice.id)}<option value={choice.id}>{choice.label}</option>{/each}
  </select>
  {#snippet footer()}
    <button class="btn" onclick={onclose}>Cancel</button>
    <button class="btn-primary" disabled={busy} onclick={apply}>Apply</button>
  {/snippet}
</Dialog>
