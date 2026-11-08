<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import { bytes, dateTime } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { RemoteFile, RemoteJob } from '../lib/types';

  // Work on the provider's side with no local file yet: what it waits for, and what can be done about it.
  let { remote, onclose }: { remote: RemoteJob; onclose: () => void } = $props();

  let linkPassword = $state('');
  let archivePassword = $state('');
  let files = $state<RemoteFile[] | null>(null);
  let filesPending = $state(false);
  let chosen = $state(new Set<string>());
  let confirmDelete = $state(false);
  let busy = $state(false);

  async function loadFiles() {
    busy = true;
    const result = await store.attempt<{ ok: boolean; pending: boolean; files: RemoteFile[] }>('remote.files', { jobId: remote.id });
    busy = false;
    if (!result) return;
    filesPending = result.pending;
    files = result.files ?? [];
    chosen = new Set(files.filter((file) => file.selectable).map((file) => file.fileId));
  }

  async function choose() {
    busy = true;
    const all = files !== null && chosen.size === files.filter((file) => file.selectable).length;
    const result = await store.attempt('remote.select', all ? { jobId: remote.id, all: true } : { jobId: remote.id, fileIds: [...chosen] }, 'Files chosen.');
    busy = false;
    if (result) onclose();
  }

  async function act(type: string, payload: Record<string, unknown>, done: string, close = true) {
    busy = true;
    const result = await store.attempt(type, { jobId: remote.id, ...payload }, done);
    busy = false;
    if (result && close) onclose();
  }
</script>

<Dialog title={remote.name ?? remote.displayHost ?? 'Provider job'} {onclose} wide>
  <dl class="mb-3 grid grid-cols-[minmax(110px,auto)_1fr] gap-x-4 gap-y-1.5 text-[13px]">
    <dt class="text-muted">Provider</dt><dd>{remote.providerName}</dd>
    <dt class="text-muted">Status</dt><dd>{remote.status}</dd>
    {#if remote.stage}<dt class="text-muted">Stage</dt><dd>{remote.stage}{remote.progress > 0 ? `, ${Math.round(remote.progress * 100)}% (the provider's own progress)` : ''}</dd>{/if}
    {#if remote.displayHost}<dt class="text-muted">Site</dt><dd>{remote.displayHost}</dd>{/if}
    {#if remote.destination}<dt class="text-muted">Folder</dt><dd class="break-all font-mono text-[12px]">{remote.destination}</dd>{/if}
    {#if remote.nextCheckAt}<dt class="text-muted">Next check</dt><dd>{dateTime(remote.nextCheckAt)}</dd>{/if}
    {#if remote.failureMessage}<dt class="text-muted">Problem</dt><dd class="text-error">{remote.failureMessage}</dd>{/if}
    {#each remote.notes as note}<dt class="text-muted">Note</dt><dd>{note}</dd>{/each}
  </dl>

  {#if remote.approvalNeeded}
    <div class="card mb-3 bg-highlight p-3 text-[13px]">
      <p class="mb-2">{remote.heldForApproval ? 'Nothing has been sent for this yet.' : 'This is not cached at the provider, so it has to fetch it first.'} Fetch it?</p>
      <button class="btn-primary" disabled={busy} onclick={() => act('remote.retry', { approveUncached: true }, 'Approved.')}>Approve fetching it</button>
    </div>
  {/if}

  {#if remote.state === 'awaitingSelection'}
    <div class="mb-3">
      {#if files === null}
        <button class="btn" disabled={busy} onclick={loadFiles}>Choose files…</button>
      {:else if filesPending}
        <p class="text-[13px] text-muted">The provider doesn't know the files yet. Try again in a moment.</p>
      {:else}
        <div class="mb-1 flex items-center justify-between text-[12px] text-muted">
          <span>{chosen.size} of {files.length} chosen</span>
          <button class="underline" onclick={() => (chosen = chosen.size ? new Set() : new Set(files!.filter((f) => f.selectable).map((f) => f.fileId)))}>{chosen.size ? 'None' : 'All'}</button>
        </div>
        <ul class="card max-h-64 divide-y divide-divider overflow-y-auto text-[12.5px]">
          {#each files as file (file.fileId)}
            <li>
              <label class="flex items-center gap-2 px-3 py-1.5 {file.selectable ? '' : 'opacity-50'}">
                <input type="checkbox" disabled={!file.selectable} checked={chosen.has(file.fileId)}
                  onchange={() => { const next = new Set(chosen); if (next.has(file.fileId)) next.delete(file.fileId); else next.add(file.fileId); chosen = next; }} />
                <span class="min-w-0 flex-1 truncate" title={file.relativePath ?? ''}>{file.relativePath ?? 'A name that is not safe to use'}</span>
                <span class="tabular shrink-0 text-muted">{bytes(file.size)}</span>
              </label>
            </li>
          {/each}
        </ul>
        <button class="btn-primary mt-2" disabled={busy || chosen.size === 0} onclick={choose}>Download the chosen files</button>
      {/if}
    </div>
  {/if}

  {#if remote.state === 'failed' || remote.failureKind === 'linkPasswordRequired'}
    <form class="mb-3 flex flex-wrap items-end gap-2" onsubmit={(event) => { event.preventDefault(); void act('remote.retry', linkPassword ? { linkPassword } : {}, 'Trying again.'); }}>
      <div class="min-w-48 flex-1">
        <label class="label" for="link-password">Site password (the link's page asked for one)</label>
        <input id="link-password" class="field" type="password" bind:value={linkPassword} autocomplete="off" />
      </div>
      <button class="btn" type="submit" disabled={busy}>Retry</button>
    </form>
  {/if}

  <form class="mb-1 flex flex-wrap items-end gap-2" onsubmit={(event) => { event.preventDefault(); void act('remote.archivePassword', { password: archivePassword }, archivePassword ? 'Archive password saved.' : 'Archive password cleared.', false); archivePassword = ''; }}>
    <div class="min-w-48 flex-1">
      <label class="label" for="remote-archive-password">Archive password {remote.hasArchivePassword ? '(one is saved)' : ''}</label>
      <input id="remote-archive-password" class="field" type="password" bind:value={archivePassword} autocomplete="off" />
    </div>
    <button class="btn" type="submit" disabled={busy || (!archivePassword && !remote.hasArchivePassword)}>{archivePassword || !remote.hasArchivePassword ? 'Save' : 'Clear'}</button>
  </form>
  <p class="hint mb-3">For the archives this download brings. It never leaves the server.</p>

  {#if confirmDelete}
    <div class="card border-icon-danger p-3 text-[13px]">
      <p class="mb-2">Delete this from your {remote.providerName} account for good? Files already downloaded here stay.</p>
      <button class="btn-danger" disabled={busy} onclick={() => act('remote.deleteRemote', { confirmed: true }, 'Deleted at the provider.')}>Delete at {remote.providerName}</button>
      <button class="btn ml-1" onclick={() => (confirmDelete = false)}>Keep it</button>
    </div>
  {/if}

  {#snippet footer()}
    <button class="btn" disabled={busy} onclick={() => act('remote.checkNow', {}, 'Looking at it now.', false)}>Check now</button>
    {#if remote.stage === 'queued'}<button class="btn" disabled={busy} onclick={() => act('remote.startQueued', {}, 'Started at the provider.')}>Start at provider</button>{/if}
    <button class="btn" disabled={busy} onclick={() => act('remote.remove', {}, 'Taken off the list. Nothing was deleted at the provider.')}>Remove from list</button>
    <button class="btn-danger" disabled={busy} onclick={() => (confirmDelete = true)}>Delete at provider…</button>
    <button class="btn" onclick={onclose}>Close</button>
  {/snippet}
</Dialog>
