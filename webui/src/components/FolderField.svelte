<script lang="ts">
  import { store } from '../lib/store.svelte';
  import type { FolderBrowseResult } from '../lib/types';
  import FolderPicker from './FolderPicker.svelte';

  // A folder on the server: typed (and checked by the server when you leave the box) or chosen with Browse….
  let { value = $bindable(''), label, id, hint = '', disabled = false }: { value?: string; label: string; id: string; hint?: string; disabled?: boolean } = $props();

  let picking = $state(false);
  let problem = $state('');

  async function check() {
    problem = '';
    if (!value.trim()) return;
    try {
      const result = await store.ask<FolderBrowseResult>('folders.browse', { path: value.trim() });
      if (result.path) value = result.path;
    } catch (e) {
      problem = e instanceof Error ? e.message : String(e);
    }
  }
</script>

<div>
  <label class="label" for={id}>{label}</label>
  <div class="flex gap-2">
    <input {id} class="field font-mono" bind:value onblur={check} {disabled} spellcheck="false" autocomplete="off" aria-invalid={problem ? 'true' : undefined} />
    <button class="btn shrink-0" type="button" onclick={() => (picking = true)} {disabled}>Browse…</button>
  </div>
  {#if problem}<p class="mt-1 text-[12px] text-error" role="alert">{problem}</p>{:else if hint}<p class="hint mt-1">{hint}</p>{/if}
</div>

{#if picking}
  <FolderPicker initial={value} onclose={() => (picking = false)} onpick={(path) => { value = path; problem = ''; picking = false; }} />
{/if}
