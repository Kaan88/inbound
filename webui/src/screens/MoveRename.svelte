<script lang="ts">
  import { untrack } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import { moveTarget, splitPath, stemLength } from '../lib/details';
  import { store } from '../lib/store.svelte';
  import type { Job } from '../lib/types';

  // Move/Rename for a finished file: the name (its stem preselected, like Explorer's rename) and the folder.
  let { job, onclose }: { job: Job; onclose: () => void } = $props();

  const start = splitPath(untrack(() => job.finalFilePath));
  let name = $state(start.name);
  let folder = $state(start.directory);
  let error = $state('');
  let busy = $state(false);
  let nameBox: HTMLInputElement | undefined = $state();
  let selected = false;

  $effect(() => {
    if (!nameBox || selected) return;
    selected = true;
    nameBox.focus();
    nameBox.setSelectionRange(0, stemLength(nameBox.value));
  });

  async function save() {
    error = '';
    if (!name.trim() || !folder.trim()) return;
    const target = moveTarget(job.finalFilePath, folder, name);
    if (target === null) {
      onclose();
      return;
    }
    busy = true;
    try {
      await store.ask('job.update', { jobId: job.id, newFilePath: target });
      onclose();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Move or rename" {onclose}>
  <form id="move-form" class="grid gap-3" onsubmit={(event) => { event.preventDefault(); void save(); }}>
    <div>
      <label class="label" for="move-name">File name</label>
      <input id="move-name" class="field" bind:this={nameBox} bind:value={name} autocomplete="off" spellcheck="false" />
    </div>
    <FolderField id="move-folder" label="Folder" bind:value={folder} />
    {#if error}<p class="text-[12px] text-error" role="alert">{error}</p>{/if}
  </form>
  {#snippet footer()}
    <button class="btn" type="button" onclick={onclose}>Cancel</button>
    <button class="btn-primary" type="submit" form="move-form" disabled={busy || !name.trim() || !folder.trim()}>Save</button>
  {/snippet}
</Dialog>
