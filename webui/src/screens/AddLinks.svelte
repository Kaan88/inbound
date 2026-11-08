<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import { kindWord, linesOf, routeOf, showsAddress, withoutAdded, type AddLine } from '../lib/addlines';
  import { bytes, siteOf } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { ClassifyResult, RemoteFile } from '../lib/types';

  // Add links: one or many lines (addresses, magnets, TorBox links), or a .torrent file uploaded to the server.
  // Each line is classified by the server first; nothing a provider handles is ever requested directly.
  let { onclose }: { onclose: () => void } = $props();

  let tab = $state<'links' | 'torrent'>('links');
  let text = $state('');
  let lines = $state<AddLine[]>([]);
  let destination = $state(store.model.settings?.defaultDownloadDirectory ?? '');
  let queueId = $state(store.model.settings?.defaultQueueId ?? 0);
  let startMode = $state<'startNow' | 'queue' | 'later'>('startNow');
  let packageName = $state('');
  let linkPassword = $state('');
  let archivePassword = $state('');
  // Off until ticked: the site's password and the archive's are often different (owner, 2026-11-08).
  let sameForBoth = $state(false);
  // A tick hidden with the hoster lines or the Links tab doesn't come back ticked.
  $effect(() => { if (!(tab === 'links' && hasHoster)) sameForBoth = false; });
  let checking = $state(false);
  let adding = $state(false);
  let addedRemote = $state<string[]>([]);
  let approve = $state(new Set<string>());
  let summary = $state('');

  // Torrent file
  let torrentPath = $state<string | null>(null);
  let torrentName = $state('');
  let torrentInfo = $state<ClassifyResult | null>(null);
  let torrentChosen = $state(new Set<string>());
  let uploadError = $state('');

  const remoteOn = $derived(store.has('remote'));
  const account = $derived(store.model.accounts[0]);
  const providerName = $derived(account?.providerName ?? 'the provider');
  const queues = $derived(store.model.settings?.queues ?? []);
  const addable = $derived(lines.filter((line) => routeOf(line) !== 'none' && routeOf(line) !== 'checking'));
  const hasHoster = $derived(lines.some((line) => line.result?.kind === 'hosterLink'));
  const waitingApproval = $derived(addedRemote.map((id) => store.model.remoteJobs[id]).filter((job) => job?.approvalNeeded));

  async function classifyAll() {
    const inputs = linesOf(text);
    const known = new Map(lines.map((line) => [line.input, line]));
    lines = inputs.map((input, index) => {
      const old = known.get(input);
      return old ? { ...old, position: index + 1 } : { input, position: index + 1, result: null, error: null, direct: false };
    });
    if (!remoteOn) {
      lines = lines.map((line) => ({ ...line, result: line.result ?? localClassify(line.input) }));
      return;
    }

    checking = true;
    const pending = lines.filter((line) => !line.result);
    let next = 0;
    async function worker() {
      while (next < pending.length) {
        const line = pending[next++];
        try {
          const result = await store.ask<ClassifyResult>('remote.classify', { text: line.input });
          update(line.input, { result, error: null });
        } catch (error) {
          update(line.input, { result: { ok: false, kind: 'undetermined', checkIncomplete: false, error: String(error) } as ClassifyResult, error: error instanceof Error ? error.message : String(error) });
        }
      }
    }
    await Promise.all([worker(), worker(), worker()]);
    checking = false;
  }

  function localClassify(input: string): ClassifyResult {
    return /^(https?|ftp):\/\//i.test(input)
      ? { ok: true, kind: 'ordinaryLink', checkIncomplete: false }
      : { ok: true, kind: 'unsupported', reason: 'Only http, https and ftp addresses can be added without a provider account.', checkIncomplete: false };
  }

  function update(input: string, change: Partial<AddLine>) {
    lines = lines.map((line) => (line.input === input ? { ...line, ...change } : line));
  }

  async function retry(line: AddLine) {
    update(line.input, { result: null, error: null });
    try {
      update(line.input, { result: await store.ask<ClassifyResult>('remote.classify', { text: line.input }) });
    } catch (error) {
      update(line.input, { result: { ok: false, kind: 'undetermined', checkIncomplete: false } as ClassifyResult, error: error instanceof Error ? error.message : String(error) });
    }
  }

  async function add() {
    adding = true;
    summary = '';
    const many = addable.length > 1;
    const key = many ? `web-${Date.now().toString(36)}` : undefined;
    const name = many ? packageName.trim() || undefined : undefined;
    let ok = 0;
    const failures: string[] = [];
    const remoteIds: string[] = [];
    const done: string[] = [];
    for (const line of addable) {
      const route = routeOf(line);
      try {
        if (route === 'direct') {
          const result = await store.ask<{ accepted: boolean; duplicate: boolean; error?: string }>('job.add', {
            url: line.input, destinationDirectory: destination || undefined, startMode, queueId, source: 'web',
            packageKey: key, packageName: name,
          });
          if (!result.accepted) throw new Error(result.error ?? 'Not added.');
        } else {
          const r = line.result!;
          const payload: Record<string, unknown> = r.kind === 'folderLink' && r.itemId && r.provider && r.itemKind
            ? { provider: r.provider, itemKind: r.itemKind, itemId: r.itemId }
            : { input: line.input };
          const result = await store.ask<{ ok: boolean; job?: { id: string } }>('remote.add', {
            ...payload, destination: destination || undefined, queueId, startMode,
            linkPassword: r.kind === 'hosterLink' ? (sameForBoth ? archivePassword : linkPassword) || undefined : undefined,
            archivePassword: archivePassword || undefined,
          });
          if (result.job) remoteIds.push(result.job.id);
        }
        ok++;
        done.push(line.input);
      } catch (error) {
        failures.push(`Line ${line.position}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }
    adding = false;
    addedRemote = remoteIds;
    // A retry must not send the lines that were already added a second time.
    if (failures.length) ({ lines, text } = withoutAdded(lines, text, done));
    // Out of the boxes once sent, whatever came of it.
    linkPassword = '';
    archivePassword = '';
    sameForBoth = false;
    summary = `${ok} added${failures.length ? `, ${failures.length} not added` : ''}.`;
    if (failures.length) store.toast(failures.slice(0, 3).join(' '), 'error');
    if (ok) store.toast(`Added ${ok} link${ok === 1 ? '' : 's'}.`);
    if (!failures.length && remoteIds.length === 0) onclose();
  }

  async function upload(event: Event) {
    const file = (event.currentTarget as HTMLInputElement).files?.[0];
    uploadError = '';
    torrentInfo = null;
    torrentPath = null;
    if (!file) return;
    torrentName = file.name;
    try {
      const response = await fetch('/api/upload/torrent', { method: 'POST', body: file, credentials: 'same-origin', headers: { 'Content-Type': 'application/x-bittorrent' } });
      // A reference to the uploaded file (upload:…), not its path on the server.
      const body = (await response.json()) as { ok: boolean; upload?: string; error?: string };
      if (!body.ok || !body.upload) throw new Error(body.error ?? 'The upload failed.');
      torrentPath = body.upload;
      if (remoteOn) {
        torrentInfo = await store.ask<ClassifyResult>('remote.classify', { text: body.upload, withFiles: true });
        torrentChosen = new Set((torrentInfo.files ?? []).map((f) => f.fileId));
      }
    } catch (error) {
      uploadError = error instanceof Error ? error.message : String(error);
    }
  }

  async function addTorrent() {
    if (!torrentPath) return;
    adding = true;
    const files = torrentInfo?.files ?? [];
    const all = torrentChosen.size === files.length;
    const filePaths = all ? undefined : files.filter((f) => torrentChosen.has(f.fileId)).map((f) => ({ path: f.relativePath, size: f.size }));
    const result = await store.attempt<{ ok: boolean; job?: { id: string } }>('remote.add', {
      input: torrentPath, destination: destination || undefined, queueId, startMode, filePaths,
      archivePassword: archivePassword || undefined,
    }, 'Torrent added.');
    adding = false;
    if (result?.job) addedRemote = [result.job.id];
    else if (result) onclose();
  }

  async function approveChosen() {
    for (const id of approve) await store.attempt('remote.retry', { jobId: id, approveUncached: true });
    store.toast('Approved.');
    onclose();
  }

  function toggleFile(id: string) {
    const next = new Set(torrentChosen);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    torrentChosen = next;
  }
</script>

<Dialog title={addedRemote.length ? 'Added' : 'Add links'} {onclose} wide>
  {#if addedRemote.length}
    <p class="mb-2 text-[13px]">{summary || 'Added.'} {providerName} is looking at {addedRemote.length === 1 ? 'it' : 'them'} now; the list shows how it goes.</p>
    {#if waitingApproval.length}
      <p class="mb-2 text-[13px]">Not cached at {providerName}, so it would have to fetch {waitingApproval.length === 1 ? 'it' : 'these'} first. Tick what it should fetch; closing this leaves them waiting, and nothing is lost.</p>
      <ul class="card mb-2 divide-y divide-divider">
        {#each waitingApproval as job (job.id)}
          <li><label class="flex items-center gap-2 px-3 py-2 text-[13px]">
            <input type="checkbox" checked={approve.has(job.id)} onchange={() => { const n = new Set(approve); if (n.has(job.id)) n.delete(job.id); else n.add(job.id); approve = n; }} />
            <span class="truncate">{job.name ?? job.displayHost ?? 'Item'}</span>
          </label></li>
        {/each}
      </ul>
    {:else}
      <p class="hint">If something isn't cached there, it asks here before fetching it.</p>
    {/if}
  {:else}
    <div class="mb-3 flex gap-1 border-b border-divider" role="tablist">
      <button role="tab" aria-selected={tab === 'links'} class="-mb-px border-b-2 px-3 py-1.5 text-[13px] {tab === 'links' ? 'border-accent font-semibold' : 'border-transparent text-muted'}" onclick={() => (tab = 'links')}>Links and magnets</button>
      <button role="tab" aria-selected={tab === 'torrent'} class="-mb-px border-b-2 px-3 py-1.5 text-[13px] {tab === 'torrent' ? 'border-accent font-semibold' : 'border-transparent text-muted'}" onclick={() => (tab = 'torrent')}>Torrent file</button>
    </div>

    {#if tab === 'links'}
      <label class="label" for="add-text">One link per line: addresses, magnets{remoteOn ? `, ${providerName} and hoster links` : ''}</label>
      <textarea id="add-text" class="field h-28 resize-y font-mono text-[12px]" bind:value={text} spellcheck="false" placeholder="https://example.com/file.iso&#10;magnet:?xt=urn:btih:…" onblur={() => text.trim() && classifyAll()}></textarea>
      <div class="mt-2 flex items-center gap-2">
        <button class="btn" onclick={classifyAll} disabled={checking || !text.trim()}><Icon name="search" size={15} /> {checking ? 'Checking…' : 'Check links'}</button>
        {#if lines.length}<span class="hint">{lines.length} line{lines.length === 1 ? '' : 's'}, {addable.length} can be added</span>{/if}
      </div>

      {#if lines.length}
        <ul class="card mt-3 max-h-56 divide-y divide-divider overflow-y-auto text-[12.5px]">
          {#each lines as line (line.input)}
            {@const route = routeOf(line)}
            <li class="flex flex-wrap items-center gap-x-2 gap-y-1 px-3 py-1.5">
              <span class="w-14 shrink-0 text-muted">Line {line.position}</span>
              <span class="w-36 shrink-0 font-semibold {route === 'none' ? 'tone-failed' : route === 'checking' ? 'text-muted' : 'tone-active'}">{kindWord(line, providerName)}</span>
              <span class="min-w-0 flex-1 truncate font-mono text-[12px]" title={showsAddress(line) ? line.input : ''}>
                {showsAddress(line) ? line.input : line.result?.displayHost ?? siteOf(line.input)}
              </span>
              {#if line.result?.kind === 'undetermined' || (line.result && !line.result.ok)}
                <button class="btn py-0 text-[12px]" onclick={() => retry(line)}>Retry</button>
                <label class="flex items-center gap-1 text-[12px]"><input type="checkbox" checked={line.direct} onchange={() => update(line.input, { direct: !line.direct })} /> Download directly</label>
              {/if}
              {#if line.result?.reason && route === 'none'}<span class="basis-full pl-16 text-[12px] text-muted">{line.result.reason}</span>{/if}
              {#if line.result?.note}<span class="basis-full pl-16 text-[12px] text-muted">{line.result.note}</span>{/if}
              {#if line.result?.kind === 'hosterLink' && (line.result.hosterAvailable === false)}<span class="basis-full pl-16 text-[12px] text-error">{providerName} says this site is down right now.</span>{/if}
            </li>
          {/each}
        </ul>
      {/if}
    {:else}
      <label class="label" for="torrent-file">A .torrent file from this computer; it is sent to the server</label>
      <input id="torrent-file" type="file" accept=".torrent,application/x-bittorrent" class="field" onchange={upload} />
      {#if uploadError}<p class="mt-1 text-[12px] text-error" role="alert">{uploadError}</p>{/if}
      {#if torrentPath && !remoteOn}<p class="hint mt-2">Torrents go through a provider account, and none is set up on this server.</p>{/if}
      {#if torrentInfo}
        {#if torrentInfo.ok === false || torrentInfo.kind !== 'torrentFile'}
          <p class="mt-2 text-[12px] text-error">{torrentInfo.reason ?? torrentInfo.error ?? 'This is not a torrent file the provider can take.'}</p>
        {:else}
          <p class="mt-2 text-[13px]"><span class="font-semibold">{torrentInfo.name ?? torrentName}</span> <span class="text-muted">· goes through {providerName}</span></p>
          {#if torrentInfo.files?.length}
            <div class="mt-1 flex justify-between text-[12px] text-muted"><span>{torrentChosen.size} of {torrentInfo.files.length} files</span>
              <button class="underline" onclick={() => (torrentChosen = torrentChosen.size ? new Set() : new Set(torrentInfo!.files!.map((f) => f.fileId)))}>{torrentChosen.size ? 'None' : 'All'}</button></div>
            <ul class="card mt-1 max-h-48 divide-y divide-divider overflow-y-auto text-[12.5px]">
              {#each torrentInfo.files as file (file.fileId)}
                <li><label class="flex items-center gap-2 px-3 py-1">
                  <input type="checkbox" checked={torrentChosen.has(file.fileId)} onchange={() => toggleFile(file.fileId)} />
                  <span class="min-w-0 flex-1 truncate">{(file as RemoteFile).relativePath}</span><span class="tabular text-muted">{bytes(file.size)}</span>
                </label></li>
              {/each}
            </ul>
          {/if}
        {/if}
      {/if}
    {/if}

    <div class="mt-4 grid gap-3 sm:grid-cols-2">
      <div class="sm:col-span-2"><FolderField id="add-destination" label="Save to (a folder on the server)" bind:value={destination} /></div>
      <div>
        <label class="label" for="add-queue">Queue</label>
        <select id="add-queue" class="field" bind:value={queueId}>
          {#each queues as queue (queue.id)}<option value={queue.id}>{queue.name}</option>{/each}
        </select>
      </div>
      <div>
        <label class="label" for="add-start">Start</label>
        <select id="add-start" class="field" bind:value={startMode}>
          <option value="startNow">Now</option>
          <option value="queue">When the queue gets to it</option>
          <option value="later">Later, by hand</option>
        </select>
      </div>
      {#if tab === 'links' && addable.length > 1}
        <div class="sm:col-span-2">
          <label class="label" for="add-package">Package name (the links are kept together)</label>
          <input id="add-package" class="field" bind:value={packageName} placeholder="Optional" />
        </div>
      {/if}
      {#if remoteOn}
        <div>
          <label class="label" for="add-archive-password">Archive password (used when the archives are extracted)</label>
          <input id="add-archive-password" class="field" type="password" bind:value={archivePassword} autocomplete="off" placeholder="Optional" />
          {#if tab === 'links' && hasHoster}
            <label class="mt-1 flex items-center gap-2 text-[12px]"><input type="checkbox" bind:checked={sameForBoth} onchange={() => { if (sameForBoth) linkPassword = ''; }} /> Use it as the site password too</label>
          {/if}
        </div>
      {/if}
      {#if tab === 'links' && hasHoster && !(remoteOn && sameForBoth)}
        <div>
          <label class="label" for="add-link-password">Site password (only if the link's page asks for one)</label>
          <input id="add-link-password" class="field" type="password" bind:value={linkPassword} autocomplete="off" placeholder="Optional" />
        </div>
      {/if}
    </div>
  {/if}

  {#snippet footer()}
    {#if addedRemote.length}
      {#if waitingApproval.length}<button class="btn-primary" disabled={approve.size === 0} onclick={approveChosen}>Fetch the ticked ones</button>{/if}
      <button class="btn" onclick={onclose}>Close</button>
    {:else if tab === 'links'}
      <button class="btn-primary" disabled={adding || checking || addable.length === 0} onclick={add}>{adding ? 'Adding…' : addable.length > 1 ? `Add ${addable.length}` : 'Add'}</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {:else}
      <button class="btn-primary" disabled={adding || !torrentPath || !remoteOn || torrentInfo?.kind !== 'torrentFile' || (torrentInfo?.files?.length ? torrentChosen.size === 0 : false)} onclick={addTorrent}>{adding ? 'Adding…' : 'Add torrent'}</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {/if}
  {/snippet}
</Dialog>
