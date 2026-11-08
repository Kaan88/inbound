<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import { archiveNameProblem, extractSummary, joinArchivePath } from '../lib/archives';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet } from '../lib/types';

  // "Extract Archive…": an archive that is already on the server's disk, without a download behind it.
  let { onclose }: { onclose: () => void } = $props();

  let folder = $state(store.model.settings?.defaultDownloadDirectory ?? '');
  let name = $state('');
  let busy = $state(false);
  let problem = $state('');

  async function extract() {
    problem = archiveNameProblem(folder, name) ?? '';
    if (problem) return;
    busy = true;
    const result = await store.attempt<{ ok: boolean; sets?: ArchiveSet[]; ignored?: { reason?: string }[] }>('archive.extract', { filePath: joinArchivePath(folder, name) });
    busy = false;
    if (!result) return;
    const summary = extractSummary(result);
    if ((result.ignored?.length ?? 0) > 0) problem = summary;
    else {
      store.toast(summary || 'Extraction asked for.');
      // A file from disk has no download behind it, so only the Extractions list shows its record.
      if (location.hash !== '#/extractions') location.hash = '#/extractions';
      onclose();
    }
  }
</script>

<Dialog title="Extract archive" {onclose}>
  <form class="grid gap-3" onsubmit={(event) => { event.preventDefault(); void extract(); }}>
    <FolderField id="extract-folder" label="Folder the archive is in" bind:value={folder} />
    <div>
      <label class="label" for="extract-name">Archive file name</label>
      <input id="extract-name" class="field font-mono" bind:value={name} placeholder="example.part1.rar" autocomplete="off" spellcheck="false" />
    </div>
    {#if problem}<p class="text-[12.5px] text-error" role="alert">{problem}</p>{/if}
    <p class="hint">The file is on the server. Where the files go is set on the Extraction page in Options.</p>
  </form>
  {#snippet footer()}
    <button class="btn" onclick={onclose}>Cancel</button>
    <button class="btn-primary" disabled={busy || !name.trim()} onclick={extract}>Extract</button>
  {/snippet}
</Dialog>
