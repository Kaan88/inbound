<script lang="ts">
  import { untrack } from 'svelte';
  import TorrentFileTree from './TorrentFileTree.svelte';
  import { buildFileTree, type FileNode } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import { addPriorities, FileReadGate, keepOneFile, priorityChanges, priorityMap, prioritiesKnown, prioritySummary, wantedIndexes, type FilePriority } from '../lib/torrentControls';
  import {
    choiceSaid, fileProgress, filesFixed, filesRefreshMs, filesTabNote, isActive, mostDetailFiles, treeFiles,
  } from '../lib/torrents';
  import type { Job, TorrentFile, TorrentFilesResult } from '../lib/types';

  // A torrent's Files in the details (T1, DownloadWindow's Files tab; T3 priorities, mockup c): the files as folders, each
  // with a tick box, how far it is and its priority. While the torrent waits for its choice the changes stay here until
  // Start; otherwise each one is sent at once (torrent.setFiles) and the running torrent takes it.
  let { job }: { job: Job } = $props();

  let files = $state<TorrentFile[] | null>(null);
  let problem = $state('');
  let notice = $state('');
  /** The user's choice while the torrent waits for it; otherwise the Host's. */
  let local = $state<Map<string, FilePriority> | null>(null);
  /** A change on its way to the Host, shown meanwhile. */
  let pending = $state<Map<string, FilePriority> | null>(null);
  let sending = $state(false);
  let readAt = 0;
  /** Keeps the reads in order with the changes sent, so a read asked before a change can't undo it on screen. */
  const reads = new FileReadGate();

  const torrent = $derived(job.torrent);
  const complete = $derived(job.state === 'completed');
  const fixed = $derived(filesFixed(job));
  const choosing = $derived(!!torrent?.awaitingFileChoice && !fixed);
  const connected = $derived(store.connection === 'open');
  const known = $derived(prioritiesKnown(files ?? []));
  const hostMap = $derived(priorityMap(files ?? []));
  const current = $derived(local ?? pending ?? hostMap);
  const shown = $derived((files ?? []).slice(0, mostDetailFiles));
  const tree = $derived(buildFileTree(treeFiles(shown)));
  const byId = $derived(new Map(shown.map((file) => [String(file.index), file])));
  const note = $derived(filesTabNote(job, choosing));

  async function read() {
    if (!connected || !torrent?.hasMetadata) return;
    const asked = reads.begin();
    if (asked === null) return;
    try {
      const result = await store.ask<TorrentFilesResult>('torrent.files', { jobId: job.id });
      if (reads.fresh(asked)) {
        files = result.files ?? [];
        problem = '';
        // The Host's list now has the change, so the tree shows the Host's own priorities again.
        pending = null;
      }
    } catch (error) {
      problem = `The file list could not be read. ${error instanceof Error ? error.message : String(error)}`;
    } finally {
      readAt = Date.now();
      if (reads.end()) void read();
    }
  }

  // Read when shown and when the file list arrives; then, while the torrent runs, at most every 3 s (30 s for a big list).
  $effect(() => {
    void torrent?.hasMetadata;
    untrack(() => void read());
  });
  $effect(() => {
    void job.receivedBytes;
    void job.state;
    if (!untrack(() => isActive(job))) return;
    if (Date.now() - readAt >= filesRefreshMs(torrent?.fileCount ?? 0)) untrack(() => void read());
  });
  // Once the choice is no longer waited for, the Host's choice is shown again.
  $effect(() => {
    if (!choosing && local) local = null;
  });

  function progress(node: FileNode): string {
    const file = node.fileId ? byId.get(node.fileId) : undefined;
    return file ? fileProgress({ ...file, wanted: (current.get(node.fileId!) ?? 'normal') !== 'skip' }, complete) : '';
  }

  async function send(payload: Record<string, unknown>, wantedBefore: number, wantedAfter: number): Promise<boolean> {
    sending = true;
    notice = '';
    reads.changed();
    try {
      await store.ask('torrent.setFiles', { jobId: job.id, ...payload });
      reads.changed();
      if (wantedAfter !== wantedBefore || choosing) store.toast(choiceSaid(torrent?.name || job.fileName || 'This torrent', wantedAfter, files?.length ?? wantedAfter));
      await read();
      return true;
    } catch (error) {
      notice = error instanceof Error ? error.message : 'The Host did not take the change.';
      // Refused: the tree goes back to the Host's choice.
      pending = null;
      return false;
    } finally {
      sending = false;
    }
  }

  async function change(next: Map<string, FilePriority>) {
    const wanted = wantedIndexes(next);
    if (wanted.length === 0) {
      notice = choosing ? 'Choose at least one file.' : keepOneFile;
      return;
    }
    notice = '';
    if (choosing) {
      local = next;
      return;
    }
    const before = wantedIndexes(hostMap).length;
    if (known) {
      // Only what changed: every other file keeps its own priority, also one changed meanwhile elsewhere.
      const changes = priorityChanges(hostMap, next);
      if (changes.length === 0) return;
      pending = next;
      await send({ fileIndexes: [], priorities: changes }, before, wanted.length);
    } else {
      pending = next;
      await send({ fileIndexes: wanted }, before, wanted.length);
    }
  }

  async function start() {
    const map = local ?? hostMap;
    const wanted = wantedIndexes(map);
    if (wanted.length === 0) {
      notice = 'Choose at least one file.';
      return;
    }
    const priorities = known ? addPriorities(map) : null;
    if (await send({ fileIndexes: wanted, ...(priorities ? { priorities } : {}) }, -1, wanted.length)) local = null;
  }
</script>

{#if note}<p class="mb-2 text-[13px] {choosing ? 'font-semibold' : 'text-muted'}">{note}</p>{/if}
{#if files && files.length > 0}
  <TorrentFileTree {tree} priorities={current} showPriority={known} {progress} disabled={fixed || sending || !connected} label="Files" onchange={(next) => void change(next)} />
  <p class="hint mt-1" aria-live="polite">{prioritySummary(files, current)}{files.length > shown.length ? ` · the first ${shown.length} are listed` : ''}</p>
  {#if known && !fixed}<p class="hint">A skipped file can still get a few bytes from pieces it shares with a neighbouring file.</p>{/if}
  {#if choosing}<div class="mt-2"><button class="btn-primary" disabled={sending || !connected || wantedIndexes(current).length === 0} onclick={start}>{sending ? 'Starting…' : 'Start'}</button></div>{/if}
{:else if torrent?.hasMetadata && !problem}
  <p class="text-[13px] text-muted">The file list is still loading.</p>
{/if}
{#if problem}<p class="mt-2 text-[12.5px] text-error" role="alert">{problem}</p>{/if}
{#if notice}<p class="mt-2 text-[12.5px] text-error" role="status">{notice}</p>{/if}
