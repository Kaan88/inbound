<script lang="ts">
  import { onDestroy, untrack } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import TorrentFileTree from '../components/TorrentFileTree.svelte';
  import { submitsOnEnter } from '../lib/keys';
  import { buildFileTree } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import {
    addLines, addedWords, canAddLines, canChooseFilesFor, canSelectLine, defaultRoute, hereName, inspected, spaceRechecked, isMagnetLine, isSelected, lineDetail, lineName,
    lineSpaceProblem, lineState, lineTip, magnetNoteApplies, newLines, picks, primaryText, removeLine, selectAllState, selectLine, setLinePriorities, setRoute,
    statusLine, syncPaste, toggleAll, torrentProvider, treeFiles, type TorrentLine, type TorrentLines, type TorrentPick,
  } from '../lib/torrents';
  import { freeText, normalMap, prioritiesKnown, prioritySummary } from '../lib/torrentControls';
  import { cacheChecked, cacheText, startCacheChecks, uploadTorrent, type CacheStates } from '../lib/torrentIntake';
  import type { ClassifyResult, TorrentAddResult, TorrentInspectResult } from '../lib/types';

  // The torrent dialog (T1, TorrentAddDialog): Add magnet, Import torrent file, and magnets from Add URL. One line per
  // magnet or uploaded .torrent file, each asked about with torrent.inspect. With a provider account each line goes to
  // This PC or the provider; provider lines go on to the provider's own add dialog. A magnet is never shown whole.
  type Mode = 'magnet' | 'torrent' | 'mixed';
  let { mode = 'mixed', initial = [], onclose, onprovider }: {
    mode?: Mode;
    initial?: { input: string; label?: string }[];
    onclose: () => void;
    /** The lines that go to the provider, handed on to its add dialog. */
    onprovider: (inputs: { input: string; label?: string }[]) => void;
  } = $props();

  const settings = $derived(store.model.settings);
  const queues = $derived([...(settings?.queues ?? [])].sort((a, b) => a.order - b.order));
  const provider = torrentProvider(store.has('remote'), store.model.accounts);
  const providerName = provider?.providerName ?? null;
  const here = $derived(hereName(store.model.hello?.hostKind));
  const canUpload = $derived(store.has('uploads'));

  let lines = $state<TorrentLines>(newLines(defaultRoute(store.model.settings?.torrentRoute, !!provider), store.model.settings?.torrentChooseFiles !== false, providerName));
  let text = $state('');
  let destination = $state('');
  let startMode = $state<'startNow' | 'queue' | 'later'>('startNow');
  let queueId = $state(store.model.settings?.defaultQueueId ?? 0);
  let adding = $state(false);
  let report = $state('');
  let uploading = $state(false);
  let uploadProblems = $state<string[]>([]);
  let dragging = $state(false);
  /** The line whose file tree is open. */
  let openFiles = $state<string | null>(null);

  const title = $derived(mode === 'magnet' ? 'Add magnet' : mode === 'torrent' ? 'Import torrent file' : 'Add torrents');
  const showPaste = $derived(mode !== 'torrent');
  const showUpload = $derived(mode !== 'magnet' && canUpload);
  const status = $derived(statusLine(lines));
  const ready = $derived(!adding && !uploading && canAddLines(lines));
  const allState = $derived(selectAllState(lines));
  const labels = new Map<string, string>();

  // ---- Lines ----

  function asked(added: TorrentLine[]) {
    for (const line of added) void inspect(line.input);
  }

  async function inspect(input: string, recheck = false) {
    let result: TorrentInspectResult;
    // T3: the folder it goes to, so the answer says the free space there and whether it fits.
    const folder = destination.trim() || null;
    try {
      result = await store.ask<TorrentInspectResult>('torrent.inspect', { input, destination: folder });
    } catch (error) {
      result = { ok: false, error: error instanceof Error ? error.message : String(error) };
    }
    // An answer for a folder that was changed meanwhile is left for the newer one.
    if ((destination.trim() || null) !== folder) return;
    lines = recheck ? spaceRechecked(lines, input, result) : inspected(lines, input, result);
  }

  // T3: a new Save to folder asks again about the lines that were read, for the free space there.
  let spaceTimer: ReturnType<typeof setTimeout> | undefined;
  let spaceFor = '';
  $effect(() => {
    const folder = destination.trim();
    if (folder === spaceFor) return;
    clearTimeout(spaceTimer);
    spaceTimer = setTimeout(() => {
      spaceFor = folder;
      for (const line of untrack(() => lines.lines)) if (!line.result || line.result.ok) void inspect(line.input, !!line.result);
    }, 600);
    return () => clearTimeout(spaceTimer);
  });
  const free = $derived(freeText(lines.lines.find((line) => lineState(line) === 'ready' && typeof line.result?.freeBytes === 'number')?.result));

  let started = false;
  $effect(() => {
    if (started) return;
    started = true;
    const magnets = initial.filter((entry) => !entry.input.startsWith('upload:'));
    const files = initial.filter((entry) => entry.input.startsWith('upload:'));
    for (const entry of files) if (entry.label) labels.set(entry.input, entry.label);
    if (files.length > 0) {
      const next = addLines(lines, files, false);
      lines = next.state;
      asked(next.added);
    }
    if (magnets.length > 0) {
      text = magnets.map((entry) => entry.input).join('\n');
      follow();
    }
  });

  let timer: ReturnType<typeof setTimeout> | undefined;
  function typed() {
    clearTimeout(timer);
    timer = setTimeout(follow, 400);
  }

  function follow() {
    const next = syncPaste(lines, text);
    lines = next.state;
    if (openFiles && !lines.lines.some((line) => line.input === openFiles)) openFiles = null;
    asked(next.added);
  }

  async function paste() {
    // Only on this click: the clipboard is never read unasked.
    try {
      const clip = await navigator.clipboard.readText();
      if (!clip.trim()) return;
      text = text.trim() ? `${text.trimEnd()}\n${clip}` : clip;
      follow();
    } catch {
      store.toast("The browser wouldn't hand over the clipboard. Paste into the box instead.", 'error');
    }
  }

  async function uploadFiles(files: File[]) {
    if (files.length === 0) return;
    uploading = true;
    uploadProblems = [];
    for (const file of files) {
      try {
        // A reference to the uploaded file (upload:…), not its path on the server.
        const uploaded = await uploadTorrent(file);
        labels.set(uploaded.input, file.name);
        const next = addLines(lines, [uploaded], false);
        lines = next.state;
        asked(next.added);
      } catch (error) {
        uploadProblems = [...uploadProblems, `${file.name}: ${error instanceof Error ? error.message : String(error)}`];
      }
    }
    uploading = false;
  }

  function dropped(event: DragEvent) {
    event.preventDefault();
    dragging = false;
    void uploadFiles([...(event.dataTransfer?.files ?? [])].filter((file) => /\.torrent$/i.test(file.name)));
  }

  // ---- The provider's cache, one question per line that was read (TorrentAddLines.StartCacheChecks) ----

  let cache = $state<CacheStates>({});
  let closed = false;
  onDestroy(() => (closed = true));
  // One question at a time, so a long list doesn't send the provider a burst.
  let queue: Promise<void> = Promise.resolve();

  $effect(() => {
    const { states, start } = startCacheChecks(lines, untrack(() => cache));
    if (JSON.stringify(states) !== JSON.stringify(untrack(() => cache))) cache = states;
    for (const { input, query } of start) queue = queue.then(() => checkCache(input, query));
  });

  async function checkCache(input: string, query: string) {
    if (closed) return;
    let result: ClassifyResult | null;
    try {
      result = await store.ask<ClassifyResult>('remote.classify', { text: query, withFiles: true });
    } catch {
      result = null;
    }
    if (!closed) cache = cacheChecked(cache, input, result);
  }

  // ---- A .torrent file's files ----

  function toggleFiles(line: TorrentLine) {
    openFiles = openFiles === line.input ? null : line.input;
  }

  const openLine = $derived(openFiles ? lines.lines.find((line) => line.input === openFiles) : undefined);
  const openTree = $derived(openLine?.result?.files ? buildFileTree(treeFiles(openLine.result.files)) : null);
  // T3: each file's priority (all Normal until changed); Skip leaves a file out, as an untick does.
  const openPriorities = $derived(openLine?.result?.files ? openLine.priorities ?? normalMap(openLine.result.files) : null);

  // ---- Adding ----

  async function add() {
    if (!ready) return;
    const chosen = picks(lines);
    const folder = destination.trim();
    adding = true;
    report = '';
    const problems: string[] = [];
    let added = 0;
    let existing = 0;
    let last: string | undefined;
    const done = new Set<string>();
    for (const pick of chosen.filter((p) => p.route === 'thisPc')) {
      try {
        const result = await store.ask<TorrentAddResult>('torrent.add', {
          input: pick.input, destination: folder || null, queueId, startMode, files: pick.files, fileIndexes: pick.fileIndexes,
          ...(pick.priorities ? { priorities: pick.priorities } : {}),
        });
        if (result.existing) existing++;
        else added++;
        last = result.job?.id ?? last;
        done.add(pick.input);
      } catch (error) {
        problems.push(`${pick.name}: ${error instanceof Error ? error.message : "It couldn't be added."}`);
      }
    }
    adding = false;

    const words = addedWords(added, existing);
    if (words) store.toast(words);
    if (last) store.revealId = last;

    const toProvider: TorrentPick[] = chosen.filter((p) => p.route === 'provider');
    if (toProvider.length > 0) {
      for (const problem of problems) store.toast(problem, 'error');
      onprovider(toProvider.map((p) => ({ input: p.input, label: labels.get(p.input) })));
      return;
    }
    if (problems.length === 0) {
      onclose();
      return;
    }
    // What was added leaves the dialog; what wasn't stays, with why.
    for (const input of done) lines = removeLine(lines, input);
    report = problems.join('\n');
  }

  function keydown(event: KeyboardEvent) {
    if (ready && submitsOnEnter(event)) { event.preventDefault(); void add(); }
  }

  function routeLabel(line: TorrentLine): string {
    return `Download ${lineName(line)} with`;
  }
</script>

<Dialog {title} {onclose} wide>
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div onkeydown={keydown}>
    {#if report}
      <p class="mb-3 whitespace-pre-line rounded border border-icon-danger/50 bg-highlight px-3 py-2 text-[13px]" role="alert">{report}</p>
    {/if}

    {#if showPaste}
      <label class="label" for="torrent-text">Paste magnet links</label>
      <textarea id="torrent-text" class="field h-24 resize-y font-mono text-[12px]" bind:value={text} spellcheck="false" placeholder="magnet:?xt=urn:btih:…" oninput={typed}></textarea>
      <div class="mt-1"><button class="btn py-0.5 text-[12px]" type="button" onclick={paste}><Icon name="link" size={14} /> Paste</button></div>
    {/if}

    {#if showUpload}
      <!-- svelte-ignore a11y_no_static_element_interactions -->
      <div class="mt-2 rounded border border-dashed px-3 py-2 text-[12.5px] {dragging ? 'border-accent bg-highlight' : 'border-divider'}"
        ondragover={(event) => { event.preventDefault(); dragging = true; }} ondragleave={() => (dragging = false)} ondrop={dropped}>
        <label class="label" for="torrent-files">.torrent files from this device (several allowed, or drop them here); they are sent to {here.the}</label>
        <input id="torrent-files" type="file" multiple accept=".torrent,application/x-bittorrent" class="field" disabled={uploading}
          onchange={(event) => { const input = event.currentTarget as HTMLInputElement; void uploadFiles([...(input.files ?? [])]); input.value = ''; }} />
        {#each uploadProblems as problem}<p class="mt-1 text-[12px] text-error" role="alert">{problem}</p>{/each}
      </div>
    {:else if mode === 'torrent'}
      <p class="hint">{here.upper} doesn't take uploaded files, so a .torrent file can't be sent to it from a browser.</p>
    {/if}

    {#if lines.lines.length >= 2}
      <label class="mt-2 flex items-center gap-2 text-[13px]"><input type="checkbox" aria-label="Select all torrents" checked={allState === true} indeterminate={allState === null} onchange={() => (lines = toggleAll(lines))} /> Select all</label>
    {/if}

    {#if lines.lines.length}
      <ul class="card mt-2 max-h-80 divide-y divide-divider overflow-y-auto text-[12.5px]" aria-label="Torrents">
        {#each lines.lines as line (line.input)}
          {@const name = lineName(line)}
          {@const detail = lineDetail(line)}
          {@const failed = lineState(line) === 'failed'}
          {@const cached = cacheText(cache[line.input], lines.providerName)}
          {@const space = lineSpaceProblem(line)}
          <li class="px-3 py-1.5">
            <div class="flex items-start gap-2">
              <input type="checkbox" class="mt-1" checked={isSelected(line)} disabled={!canSelectLine(line)} aria-label={`${name}, ${detail}`}
                onchange={(event) => (lines = selectLine(lines, line.input, (event.currentTarget as HTMLInputElement).checked))} />
              <Icon name={isMagnetLine(line) ? 'link' : 'file'} size={15} class="mt-0.5 shrink-0 text-icon-neutral" />
              <div class="min-w-0 flex-1">
                <div class="truncate" title={lineTip(line)}>{name}</div>
                <div class="text-[12px] {failed ? 'text-error' : 'text-muted'}">{detail}{#if cached} · <span aria-live="polite">{cached}</span>{/if}</div>
                {#if space && isSelected(line)}<div class="text-[12px] text-error">{space}</div>{/if}
              </div>
              {#if lines.providerName && lineState(line) === 'ready'}
                <div class="flex shrink-0 overflow-hidden rounded border border-divider text-[12px]" role="radiogroup" aria-label={routeLabel(line)}>
                  <button role="radio" aria-checked={line.route === 'thisPc'} class="px-2 py-0.5 {line.route === 'thisPc' ? 'bg-selected font-semibold' : 'hover:bg-hover'}" onclick={() => (lines = setRoute(lines, line.input, 'thisPc'))}>{here.short}</button>
                  <button role="radio" aria-checked={line.route === 'provider'} class="border-l border-divider px-2 py-0.5 {line.route === 'provider' ? 'bg-selected font-semibold' : 'hover:bg-hover'}" onclick={() => (lines = setRoute(lines, line.input, 'provider'))}>{lines.providerName}</button>
                </div>
              {/if}
            </div>
            {#if canChooseFilesFor(lines, line)}
              <div class="ml-7 mt-1">
                <button class="btn py-0 text-[12px]" aria-expanded={openFiles === line.input} aria-label={openFiles === line.input ? `Hide the files of ${name}` : `Choose the files of ${name}`} onclick={() => toggleFiles(line)}>{openFiles === line.input ? 'Hide files' : 'Choose files…'}</button>
                {#if openFiles === line.input && openTree && openPriorities && line.result?.files}
                  <div class="mt-2">
                    <TorrentFileTree tree={openTree} priorities={openPriorities} showPriority={prioritiesKnown(line.result.files)} label="Files of {name}"
                      onchange={(next) => (lines = setLinePriorities(lines, line.input, next))} />
                    <p class="hint mt-1" aria-live="polite">{prioritySummary(line.result.files, openPriorities)}</p>
                  </div>
                {/if}
              </div>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}

    {#if status}<p class="mt-2 text-[12.5px] text-muted" role="status" aria-label="Torrents status">{status}</p>{/if}
    {#if magnetNoteApplies(lines)}<p class="hint mt-1">A magnet asks which files to download once its file list arrives.</p>{/if}
    {#if lines.providerName}
      <p class="hint mt-1">{here.upper} downloads from other peers with {here.own} own address. {lines.providerName} downloads it on {lines.providerName}'s side, then fetches the files over HTTPS like any {lines.providerName} download.</p>
    {/if}

    <div class="mt-4 grid gap-3 sm:grid-cols-2">
      <div class="sm:col-span-2">
        <FolderField id="torrent-destination" label="Save to" placeholder="Each torrent's category folder" bind:value={destination} />
        {#if free}<p class="hint mt-1" aria-label="Free space">{free}</p>{/if}
      </div>
      <div>
        <label class="label" for="torrent-start">Start</label>
        <select id="torrent-start" class="field" bind:value={startMode}>
          <option value="startNow">Start now</option>
          <option value="queue">When the queue gets to it</option>
          <option value="later">Download later</option>
        </select>
      </div>
      <div>
        <label class="label" for="torrent-queue">Queue</label>
        <select id="torrent-queue" class="field" bind:value={queueId}>
          {#each queues as queue (queue.id)}<option value={queue.id}>{queue.name}</option>{/each}
        </select>
      </div>
    </div>
  </div>

  {#snippet footer()}
    <button class="btn-primary" disabled={!ready} onclick={add}>{adding ? 'Adding…' : primaryText(lines)}</button>
    <button class="btn" onclick={onclose}>Cancel</button>
  {/snippet}
</Dialog>
