<script lang="ts">
  import { spaceLine } from '../lib/addflow';
  import { store } from '../lib/store.svelte';
  import type { FolderBrowseResult, FolderSpace } from '../lib/types';
  import FolderPicker from './FolderPicker.svelte';

  // A folder on the server: typed (and checked by the server when you leave the box) or chosen with Browse….
  // With showSpace, a line under the box says how much room the server has there (and warns when a file of neededBytes won't fit).
  let { value = $bindable(''), label, id, hint = '', disabled = false, placeholder = '', showSpace = false, neededBytes = null }: { value?: string; label: string; id: string; hint?: string; disabled?: boolean; placeholder?: string; showSpace?: boolean; neededBytes?: number | null } = $props();

  let picking = $state(false);
  let problem = $state('');
  let space = $state<FolderSpace | null>(null);
  let measured = $state(false);
  let measureTimer: ReturnType<typeof setTimeout> | undefined;
  let measureRound = 0;

  // 400 ms after the box stops changing, as on the desktop; only the newest answer is shown.
  $effect(() => {
    if (!showSpace) return;
    const folder = value.trim();
    clearTimeout(measureTimer);
    if (!folder) { measured = false; return; }
    const round = ++measureRound;
    measureTimer = setTimeout(async () => {
      let result: FolderSpace | null = null;
      try {
        const reply = await store.ask<{ folders?: FolderSpace[] }>('folders.space', { paths: [folder] });
        result = reply.folders?.[0] ?? null;
      } catch {
        result = null;
      }

      if (round !== measureRound) return;
      space = result;
      measured = true;
    }, 400);
    return () => clearTimeout(measureTimer);
  });

  const line = $derived(measured ? spaceLine(space, neededBytes) : null);

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
    <input {id} class="field font-mono" bind:value onblur={check} {disabled} {placeholder} spellcheck="false" autocomplete="off" aria-invalid={problem ? 'true' : undefined} />
    <button class="btn shrink-0" type="button" onclick={() => (picking = true)} {disabled}>Browse…</button>
  </div>
  {#if problem}<p class="mt-1 text-[12px] text-error" role="alert">{problem}</p>{:else if hint}<p class="hint mt-1">{hint}</p>{/if}
  {#if line}<p class="mt-1 text-[12px] {line.problem ? 'text-error' : 'text-muted'}" role="status">{line.text}</p>{/if}
</div>

{#if picking}
  <FolderPicker initial={value} onclose={() => (picking = false)} onpick={(path) => { value = path; problem = ''; picking = false; }} />
{/if}
