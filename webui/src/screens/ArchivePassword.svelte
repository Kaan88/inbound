<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import { archiveShort, archiveTone } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet } from '../lib/types';

  // One archive set: its state in the Host's words, the password prompt when it needs one, and its actions.
  let { set, onclose }: { set: ArchiveSet; onclose: () => void } = $props();

  let password = $state('');
  let show = $state(false);
  let busy = $state(false);

  async function submit() {
    busy = true;
    const result = await store.attempt<{ ok: boolean }>('archive.setPassword', { setId: set.id, password }, 'Password saved; the extraction runs again.');
    busy = false;
    if (result?.ok) onclose();
  }
</script>

<Dialog title={set.name} {onclose}>
  <p class="mb-1 text-[13px]"><span class="tone-{archiveTone(set)} font-semibold">{archiveShort(set)}</span></p>
  <p class="mb-3 text-[13px] text-muted">{set.stateText}</p>
  {#if set.missingVolumes.length}
    <p class="label">Missing parts</p>
    <ul class="mb-3 list-disc pl-5 font-mono text-[12px]">{#each set.missingVolumes as volume}<li>{volume}</li>{/each}</ul>
  {/if}

  {#if set.needsPassword || set.state !== 'completed'}
    <form onsubmit={(event) => { event.preventDefault(); void submit(); }}>
      <label class="label" for="archive-password">Archive password</label>
      <div class="flex gap-2">
        <input id="archive-password" class="field" type={show ? 'text' : 'password'} bind:value={password} autocomplete="off" spellcheck="false" />
        <button class="btn shrink-0" type="button" onclick={() => (show = !show)} aria-pressed={show}>{show ? 'Hide' : 'Show'}</button>
      </div>
      <p class="hint mt-1">It stays on the server and is never shown again.{set.hasPassword ? ' A password is on record; a new one replaces it, and an empty one clears it.' : ''}</p>
      <button class="btn-primary mt-3" type="submit" disabled={busy || (!password && !set.hasPassword)}>Save and extract</button>
    </form>
  {/if}

  {#snippet footer()}
    {#if set.state === 'failed' || set.state === 'completed'}<button class="btn" onclick={() => store.attempt('archive.retry', { setId: set.id }, 'Extraction asked for again.')}>Run again</button>{/if}
    {#if set.state === 'extracting' || set.state === 'waiting'}<button class="btn" onclick={() => store.attempt('archive.cancel', { setId: set.id }, 'Extraction cancelled.')}>Cancel extraction</button>{/if}
    <button class="btn" onclick={async () => { if (await store.attempt('archive.remove', { setId: set.id }, 'Taken off the list; no file was touched.')) onclose(); }}>Remove from list</button>
    <button class="btn" onclick={onclose}>Close</button>
  {/snippet}
</Dialog>
