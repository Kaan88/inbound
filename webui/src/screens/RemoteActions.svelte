<script lang="ts">
  import Confirm from '../components/Confirm.svelte';
  import Dialog from '../components/Dialog.svelte';
  import RemoteFileTree from '../components/RemoteFileTree.svelte';
  import { dateTime } from '../lib/format';
  import {
    actionsOf, addToWords, buildFileTree, choiceOf, choosableIds, dequeueQuestion, downloadAllWhenReady, earlyChoice, failureText,
    isInProviderQueue, jobRow, linkPasswordHint, providerNameOf, removeQuestion, restoreChoice, canDeleteFromProvider, deleteQuestion, type FileTree,
  } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import type { RemoteFile, RemoteJob } from '../lib/types';

  // Work on the provider's side with no local file yet: what it waits for, and what can be done about it. The same
  // commands as the desktop's row menu, each shown only when the Host would take it.
  let { remote: first, onclose }: { remote: RemoteJob; onclose: () => void } = $props();

  const remote = $derived(store.model.remoteJobs[first.id] ?? first);
  const provider = $derived(providerNameOf(remote));
  const row = $derived(jobRow(remote));
  const actions = $derived(actionsOf(remote));
  const early = $derived(remote.state !== 'awaitingSelection');
  const queued = $derived(isInProviderQueue(remote));

  let linkPassword = $state('');
  let forArchives = $state(false);
  let archivePassword = $state('');
  let tree = $state<FileTree | null>(null);
  let filesPending = $state(false);
  let filesProblem = $state('');
  let chosen = $state(new Set<string>());
  let asking = $state<'remove' | 'delete' | 'dequeue' | null>(null);
  let busy = $state(false);

  async function loadFiles() {
    busy = true;
    filesProblem = '';
    try {
      const result = await store.ask<{ pending: boolean; files: RemoteFile[] }>('remote.files', { jobId: remote.id });
      filesPending = result.pending;
      tree = buildFileTree(result.files ?? []);
      // What was found of an earlier choice starts ticked; the first time, everything does.
      const earlier = early ? new Set<string>() : new Set(remote.selectedFileIds ?? []);
      chosen = earlier.size > 0 ? restoreChoice(tree, earlier) : new Set(choosableIds(tree));
    } catch (error) {
      filesProblem = failureText(error instanceof Error ? error.message : String(error), null, provider);
    } finally {
      busy = false;
    }
  }

  async function choose(all: boolean) {
    busy = true;
    const choice = tree && !all ? choiceOf(tree, chosen) : { all: true, fileIds: [] as string[] };
    const result = await store.attempt('remote.select', choice.all ? { jobId: remote.id, all: true } : { jobId: remote.id, fileIds: choice.fileIds }, early ? earlyChoice(choice.all) : 'Files chosen.');
    busy = false;
    if (result) onclose();
  }

  async function act(type: string, payload: Record<string, unknown>, done: string, close = true) {
    busy = true;
    const result = await store.attempt(type, { jobId: remote.id, ...payload }, done);
    busy = false;
    asking = null;
    if (result && close) onclose();
    return result;
  }

  async function retryWithPassword() {
    const password = linkPassword;
    linkPassword = '';
    await act('remote.retry', { linkPassword: password, linkPasswordForArchives: forArchives && !remote.hasArchivePassword }, 'Trying again.');
  }

  async function sendArchivePassword(clear: boolean) {
    const password = clear ? '' : archivePassword;
    archivePassword = '';
    await act('remote.archivePassword', { password }, clear ? 'The saved archive password is gone.' : 'Archive password saved.', false);
  }
</script>

<Dialog title={row.name} {onclose} wide>
  <dl class="mb-3 grid grid-cols-[minmax(110px,auto)_1fr] gap-x-4 gap-y-1.5 text-[13px]">
    <dt class="text-muted">Provider</dt><dd>{provider}</dd>
    <dt class="text-muted">Status</dt><dd class="tone-{row.tone}">{row.status}</dd>
    {#if row.detail}<dt class="text-muted">Details</dt><dd>{row.detail}</dd>{/if}
    {#if remote.displayHost}<dt class="text-muted">Site</dt><dd>{remote.displayHost}</dd>{/if}
    {#if remote.destination}<dt class="text-muted">Folder</dt><dd class="break-all font-mono text-[12px]">{remote.destination}</dd>{/if}
    {#if remote.createdAt}<dt class="text-muted">Added</dt><dd>{dateTime(remote.createdAt)}</dd>{/if}
    {#each remote.notes as note}<dt class="text-muted">Note</dt><dd>{note}</dd>{/each}
  </dl>

  {#if actions.approve}
    <div class="card mb-3 bg-highlight p-3 text-[13px]">
      <p class="mb-2">{remote.heldForApproval ? `Nothing has been sent for this yet. Adding it puts it on ${provider}.` : `Nothing has been sent for this yet. Starting it puts it on ${provider}.`}</p>
      <button class="btn-primary" disabled={busy} onclick={() => act('remote.retry', { approveUncached: true }, `Sent to ${provider}.`)}>{addToWords([remote])}</button>
    </div>
  {/if}

  {#if actions.selectFiles}
    <div class="mb-3">
      <p class="label">Files</p>
      {#if tree === null}
        <button class="btn" disabled={busy} onclick={loadFiles}>Choose files…</button>
        {#if early}<button class="btn ml-1" disabled={busy} onclick={() => choose(true)}>{downloadAllWhenReady}</button>{/if}
        {#if filesProblem}<p class="mt-1 text-[12.5px] text-error" role="alert">{filesProblem}</p>{/if}
      {:else if tree.files.length === 0}
        <p class="mb-2 text-[13px] text-muted">{provider} doesn't list the files yet. {early ? 'You can download all of them when it does.' : 'Try again when it does.'}</p>
        <button class="btn" disabled={busy} onclick={loadFiles}>Read the files again</button>
        {#if early}<button class="btn ml-1" disabled={busy} onclick={() => choose(true)}>{downloadAllWhenReady}</button>{/if}
      {:else}
        {#if filesPending}<p class="mb-2 text-[13px] text-muted">{provider} is still working on this, so the list may change.</p>{/if}
        <RemoteFileTree {tree} bind:chosen disabled={busy} />
        <div class="mt-2 flex flex-wrap gap-2">
          <button class="btn-primary" disabled={busy || chosen.size === 0} onclick={() => choose(false)}>Download the chosen files</button>
          {#if early}<button class="btn" disabled={busy} onclick={() => choose(true)}>{downloadAllWhenReady}</button>{/if}
        </div>
      {/if}
    </div>
  {/if}

  {#if actions.enterLinkPassword}
    <form class="mb-3 grid gap-2" onsubmit={(event) => { event.preventDefault(); if (linkPassword) void retryWithPassword(); }}>
      <p class="label !mb-0">Enter the link's password</p>
      <p class="hint">{linkPasswordHint(remote)}</p>
      <input id="link-password" class="field" type="password" bind:value={linkPassword} autocomplete="off" aria-label="Site password" />
      {#if !remote.hasArchivePassword && store.has('archives')}
        <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={forArchives} /> Use it as the archive password too</label>
      {/if}
      <div><button class="btn-primary" type="submit" disabled={busy || !linkPassword}>Retry with this password</button></div>
    </form>
  {/if}

  {#if store.has('archives')}
    <form class="mb-1 flex flex-wrap items-end gap-2" onsubmit={(event) => { event.preventDefault(); if (archivePassword) void sendArchivePassword(false); }}>
      <div class="min-w-48 flex-1">
        <label class="label" for="remote-archive-password">Archive password {remote.hasArchivePassword ? '(one is saved)' : ''}</label>
        <input id="remote-archive-password" class="field" type="password" bind:value={archivePassword} autocomplete="off" />
      </div>
      <button class="btn" type="submit" disabled={busy || !archivePassword}>Save password</button>
      {#if remote.hasArchivePassword}<button class="btn" type="button" disabled={busy} onclick={() => sendArchivePassword(true)}>Forget saved password</button>{/if}
    </form>
    <p class="hint mb-3">For the archives this download brings. It never leaves the server.</p>
  {/if}

  {#snippet footer()}
    {#if actions.checkNow}<button class="btn" disabled={busy} onclick={() => act('remote.checkNow', {}, 'Checking now…', false)}>Check Now</button>{/if}
    {#if actions.retry && !actions.enterLinkPassword}<button class="btn" disabled={busy} onclick={() => act('remote.retry', {}, 'Trying again.')}>Retry</button>{/if}
    {#if queued}
      <button class="btn" disabled={busy} onclick={() => act('remote.startQueued', {}, 'Started. Checking now…')}>Start Now on {provider}</button>
      <button class="btn" disabled={busy} onclick={() => (asking = 'dequeue')}>Remove from {provider}'s Queue…</button>
    {/if}
    <button class="btn" disabled={busy} onclick={() => (asking = 'remove')}>Delete…</button>
    {#if canDeleteFromProvider(remote)}<button class="btn-danger" disabled={busy} onclick={() => (asking = 'delete')}>Delete from {provider}…</button>{/if}
    <button class="btn" onclick={onclose}>Close</button>
  {/snippet}
</Dialog>

{#if asking === 'remove'}
  <Confirm title="Delete from the list?" message={removeQuestion([remote])} primary="Delete" {busy} onclose={() => (asking = null)} onconfirm={() => act('remote.remove', {}, 'Taken off the list. Nothing was deleted at the provider.')} />
{:else if asking === 'dequeue'}
  <Confirm title="Remove from {provider}'s queue?" message={dequeueQuestion([remote])} primary="Remove from queue" danger {busy} onclose={() => (asking = null)} onconfirm={() => act('remote.deleteRemote', { confirmed: true }, `Taken out of ${provider}'s queue.`)} />
{:else if asking === 'delete'}
  <Confirm title="Delete from {provider}?" message={deleteQuestion(row.name, provider)} primary="Delete from {provider}" danger {busy} onclose={() => (asking = null)} onconfirm={() => act('remote.deleteRemote', { confirmed: true }, `Deleted from ${provider}.`)} />
{/if}
