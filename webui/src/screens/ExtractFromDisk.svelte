<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import { archiveNameProblem, extractSummary, joinArchivePath } from '../lib/archives';
  import { bytes } from '../lib/format';
  import { latest } from '../lib/latest';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet, FolderBrowseResult } from '../lib/types';

  // "Extract Archive…": an archive that is already on the server's disk, without a download behind it.
  let { onclose }: { onclose: () => void } = $props();

  let folder = $state(store.model.settings?.defaultDownloadDirectory ?? '');
  let name = $state('');
  let busy = $state(false);
  let problem = $state('');
  // The archive files in the folder (folders.browse with files: 'archives'), to pick one instead of typing its name.
  let files = $state<{ name: string; size: number }[]>([]);
  let truncated = $state(false);
  const listings = latest();
  let listTimer: ReturnType<typeof setTimeout> | undefined;

  // 400 ms after the folder box stops changing; only the newest answer is shown. A folder it can't list shows none.
  $effect(() => {
    const path = folder.trim();
    clearTimeout(listTimer);
    files = [];
    truncated = false;
    if (!path || !store.has('folders')) return;
    const current = listings.begin();
    listTimer = setTimeout(async () => {
      try {
        const result = await store.ask<FolderBrowseResult>('folders.browse', { path, files: 'archives' });
        if (!current()) return;
        files = (result.files ?? []).map((file) => ({ name: file.name, size: file.size }));
        truncated = result.truncated;
      } catch {
        // Not a folder it may list, or a server from before the file list: the name is typed instead.
      }
    }, 400);
    return () => clearTimeout(listTimer);
  });

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
    {#if files.length > 0}
      <div>
        <p class="label" id="extract-files-label">Archives in this folder</p>
        <ul class="max-h-40 overflow-y-auto rounded border border-divider" aria-labelledby="extract-files-label">
          {#each files as file (file.name)}
            <li>
              <button type="button" class="flex w-full items-center gap-2 px-2 py-1 text-left text-[13px] hover:bg-hover {file.name === name ? 'bg-selected font-semibold' : ''}"
                aria-pressed={file.name === name} onclick={() => (name = file.name)}>
                <span class="min-w-0 flex-1 truncate font-mono">{file.name}</span>
                <span class="tabular shrink-0 text-[12px] text-muted">{bytes(file.size)}</span>
              </button>
            </li>
          {/each}
        </ul>
        {#if truncated}<p class="hint mt-1">Only the first ones by name are listed; type the name of another.</p>{/if}
      </div>
    {/if}
    {#if problem}<p class="text-[12.5px] text-error" role="alert">{problem}</p>{/if}
    <p class="hint">The file is on the server. Where the files go is set on the Extraction page in Options.</p>
  </form>
  {#snippet footer()}
    <button class="btn" onclick={onclose}>Cancel</button>
    <button class="btn-primary" disabled={busy || !name.trim()} onclick={extract}>Extract</button>
  {/snippet}
</Dialog>
