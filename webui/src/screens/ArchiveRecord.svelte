<script lang="ts">
  import Confirm from '../components/Confirm.svelte';
  import Dialog from '../components/Dialog.svelte';
  import { extractAgainQuestion, archiveRowView, planFor, removeQuestion, fullWithNote, targetOf, type ArchivePasswordTarget } from '../lib/archives';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet } from '../lib/types';

  // One extraction record: what the Host says about it, and everything that can be done with it. Any record opens
  // here, including failed, cancelled and finished ones.
  let { set, onclose, onpassword }: { set: ArchiveSet; onclose: () => void; onpassword: (target: ArchivePasswordTarget) => void } = $props();

  const view = $derived(archiveRowView(set));
  const plan = $derived(planFor([], [set], null));
  let asking = $state<'again' | 'remove' | null>(null);
  let busy = $state(false);

  async function run(type: string, done: string, close = false) {
    busy = true;
    const result = await store.attempt<{ ok: boolean }>(type, { setId: set.id }, done);
    busy = false;
    asking = null;
    if (result?.ok && close) onclose();
  }

  function retry() {
    if (set.state === 'completed') asking = 'again';
    else void run('archive.retry', 'Extraction asked for.');
  }
</script>

<Dialog title={set.name} {onclose}>
  <p class="mb-1 text-[13px]"><span class="tone-{view.tone} font-semibold">{view.text || set.stateText}</span></p>
  <p class="mb-3 text-[13px] text-muted">{fullWithNote(set)}</p>
  {#if set.missingVolumes.length}
    <p class="label">Missing parts</p>
    <ul class="mb-3 list-disc pl-5 font-mono text-[12px]">{#each set.missingVolumes as volume}<li>{volume}</li>{/each}</ul>
  {/if}
  {#if view.tooltip.split('\n').length > 1}
    <p class="hint">{view.tooltip.split('\n').slice(1).join(' ')}</p>
  {/if}

  {#snippet footer()}
    {#if plan.password}<button class="btn" onclick={() => onpassword(plan.password ?? targetOf(set))}>Archive Password…</button>{/if}
    {#if plan.retry.length}<button class="btn" disabled={busy} onclick={retry}>{set.state === 'completed' ? 'Extract again…' : 'Retry extraction'}</button>{/if}
    {#if plan.cancel.length}<button class="btn" disabled={busy} onclick={() => run('archive.cancel', 'Extraction cancelled.')}>Cancel extraction</button>{/if}
    {#if plan.remove.length}<button class="btn" disabled={busy} onclick={() => (asking = 'remove')}>Remove extraction record…</button>{/if}
    <button class="btn" onclick={onclose}>Close</button>
  {/snippet}
</Dialog>

{#if asking === 'again'}
  <Confirm title="Extract again?" message={extractAgainQuestion} primary="Extract Again" {busy} onclose={() => (asking = null)} onconfirm={() => run('archive.retry', 'Extraction asked for.')} />
{:else if asking === 'remove'}
  <Confirm title="Remove extraction record?" message={removeQuestion([set])} primary="Remove Record" danger {busy} onclose={() => (asking = null)} onconfirm={() => run('archive.remove', 'Taken off the list; no file was touched.', true)} />
{/if}
