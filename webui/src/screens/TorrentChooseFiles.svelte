<script lang="ts">
  import { untrack } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import RemoteFileTree from '../components/RemoteFileTree.svelte';
  import { buildFileTree } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import { choiceIndexes, choiceSaid, filesFixed, treeFiles, wantedIds } from '../lib/torrents';
  import type { TorrentFile, TorrentFilesResult } from '../lib/types';

  // "Choose files…" for a torrent in the list (T1, MainWindow.Torrents): its files as a tree, ticked as the Host has
  // them; OK sends the choice (torrent.setFiles), which also starts a torrent that waited for it.
  let { jobId, onclose }: { jobId: string; onclose: () => void } = $props();

  const job = $derived(store.model.jobs[jobId]);
  const name = $derived(job?.torrent?.name || job?.fileName || 'This torrent');
  let files = $state<TorrentFile[] | null>(null);
  let problem = $state('');
  let chosen = $state(new Set<string>());
  let sending = $state(false);
  const tree = $derived(files ? buildFileTree(treeFiles(files)) : null);

  async function load() {
    problem = '';
    try {
      const result = await store.ask<TorrentFilesResult>('torrent.files', { jobId });
      files = result.files ?? [];
      chosen = wantedIds(files);
      if (files.length === 0) problem = "The file list isn't known yet.";
    } catch (error) {
      problem = `Couldn't get the file list: ${error instanceof Error ? error.message : String(error)}`;
    }
  }
  untrack(() => void load());

  async function send() {
    if (!files || !job) return;
    const indexes = choiceIndexes(chosen, files);
    if (indexes === null) {
      problem = "That choice couldn't be read. Choose the files again.";
      return;
    }
    if (indexes.length === 0) {
      problem = 'Choose at least one file.';
      return;
    }
    sending = true;
    try {
      await store.ask('torrent.setFiles', { jobId, fileIndexes: indexes });
      store.toast(choiceSaid(name, indexes.length, files.length));
      onclose();
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
    } finally {
      sending = false;
    }
  }
</script>

<Dialog title="Choose files" {onclose} wide>
  <p class="mb-2 truncate text-[13px] font-semibold" title={name}>{name}</p>
  {#if !job}
    <p class="text-[13px] text-muted">This torrent is no longer in the list.</p>
  {:else if filesFixed(job)}
    <p class="text-[13px] text-muted">This torrent has finished; its files can't change now.</p>
  {:else if tree && files && files.length > 0}
    <RemoteFileTree {tree} bind:chosen disabled={sending} label="Files of {name}" />
  {:else if !problem}
    <p class="text-[13px] text-muted">Getting the file list…</p>
  {/if}
  {#if problem}<p class="mt-2 text-[12.5px] text-error" role="alert">{problem}</p>{/if}

  {#snippet footer()}
    <button class="btn-primary" disabled={!job || filesFixed(job) || !files || files.length === 0 || sending || store.connection !== 'open'} onclick={send}>{sending ? 'Sending…' : 'OK'}</button>
    <button class="btn" onclick={onclose}>Cancel</button>
  {/snippet}
</Dialog>
