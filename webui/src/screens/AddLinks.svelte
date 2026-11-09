<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import { conflictOptions, findExisting, packageNameFor, type ConflictPolicy } from '../lib/addflow';
  import { routeOf, showsAddress, type AddLine } from '../lib/addlines';
  import {
    addReport, addSummary, addedWords, alreadyListed, canAdd, canSelect, chooseLaterApplies, chosen, extractInputs, fileChoiceFrom, filesToChoose,
    isFullPath, isMagnet, isUpload, lineWords, newLine, refused, removeInputs, selectAllState, toggleAll, withDefaults, type FileChoice,
  } from '../lib/addview';
  import { bytes } from '../lib/format';
  import { submitsOnEnter } from '../lib/keys';
  import { store } from '../lib/store.svelte';
  import type { ClassifyResult, RemoteFile } from '../lib/types';

  // Add links, Add magnet, Import torrent file and the review of an imported list: the desktop's one dialog for
  // many inputs. Every line says what it is as soon as the server does, and only then can it be added. A hoster,
  // folder or magnet link only ever goes to the provider; an ordinary link goes straight to a download.
  type Mode = 'links' | 'magnet' | 'torrent' | 'imported';
  let { mode = 'links', seed = [], onclose }: { mode?: Mode; seed?: { input: string; result: ClassifyResult }[]; onclose: () => void } = $props();

  interface Passwords { has: boolean; archive: string; site: string; same: boolean }

  const settings = $derived(store.model.settings);
  const account = $derived(store.model.accounts[0]);
  const providerName = $derived(account?.providerName ?? 'the provider');
  // The server answers the archive commands (its hello names "archives"); without them there is no password to give.
  const archivesSupported = $derived(store.has('archives'));
  const queues = $derived([...(settings?.queues ?? [])].sort((a, b) => a.order - b.order));

  let remoteOn = $state(store.has('remote'));
  let text = $state('');
  let lines = $state<AddLine[]>([]);
  // Lines that don't come from the text box: uploaded .torrent files and the entries of an imported list.
  let extra = $state(new Set<string>());
  let typing = $state(false);
  let skipListed = $state(true);
  let keepPackage = $state(true);
  let packageName = $state('');
  let archivePassword = $state('');
  let chooseLater = $state(false);
  let conflict = $state<ConflictPolicy>('rename');
  let destination = $state(store.model.settings?.defaultDownloadDirectory ?? '');
  let destinationError = $state('');
  let queueId = $state(store.model.settings?.defaultQueueId ?? 0);
  let startMode = $state<'startNow' | 'queue' | 'later'>('startNow');
  let passwords = $state<Record<string, Passwords>>({});
  let adding = $state(false);
  let report = $state('');
  let uploadProblems = $state<string[]>([]);
  let uploading = $state(false);
  let dragging = $state(false);

  // The file choice that comes straight after Add, one item at a time.
  let choosing = $state<{ line: AddLine; name: string; files: RemoteFile[]; picked: Set<string> } | null>(null);
  let chosenResolve: ((answer: FileChoice | null) => void) | null = null;

  $effect(() => { destination; destinationError = ''; });

  const many = $derived(lines.length > 1);
  const summary = $derived(addSummary(lines, text.trim().length > 0, mode === 'magnet'));
  const ticked = $derived(lines.filter((line) => line.selected && canSelect(line)).length);
  const allState = $derived(selectAllState(lines));
  const directTicked = $derived(lines.filter((line) => line.selected && routeOf(line) === 'direct'));
  const showChooseLater = $derived(lines.some(chooseLaterApplies));
  const ready = $derived(!typing && !adding && canAdd(lines));
  const title = $derived(mode === 'links' && seed.length === 1 ? 'Add link' : mode === 'magnet' ? 'Add magnet' : mode === 'torrent' ? 'Import torrent file' : mode === 'imported' ? 'Import download list' : 'Add links');
  const single = $derived(mode === 'links' && seed.length > 0);
  const showPaste = $derived((mode === 'links' && !single) || mode === 'magnet');
  // .torrent files are uploaded first, so only where the server takes uploads (the hello's "uploads").
  const showUpload = $derived(remoteOn && store.has('uploads') && (mode === 'torrent' || (mode === 'links' && !single)));

  function pwOf(input: string): Passwords {
    return passwords[input] ?? { has: false, archive: '', site: '', same: false };
  }

  function setPw(input: string, change: Partial<Passwords>) {
    const next = { ...pwOf(input), ...change };
    if (!next.has) { next.archive = ''; next.site = ''; next.same = false; }
    passwords = { ...passwords, [input]: next };
  }

  function clearPasswords() {
    passwords = {};
    archivePassword = '';
  }

  function refreshDefaults() {
    lines = withDefaults(lines, skipListed);
  }

  function update(input: string, change: Partial<AddLine>) {
    lines = lines.map((line) => (line.input === input ? { ...line, ...change } : line));
    refreshDefaults();
  }

  function renumber(list: AddLine[]): AddLine[] {
    return list.map((line, index) => ({ ...line, position: list.length > 1 ? index + 1 : 0 }));
  }

  // ---- Lines from the text: 400 ms after typing stops, as on the desktop ----

  let timer: ReturnType<typeof setTimeout> | undefined;
  function typed() {
    typing = true;
    clearTimeout(timer);
    timer = setTimeout(() => applyInputs(extractInputs(text)), 400);
  }

  function applyInputs(inputs: string[]) {
    typing = false;
    const known = new Map(lines.map((line) => [line.input, line]));
    const kept = lines.filter((line) => extra.has(line.input));
    const next: AddLine[] = [...kept];
    const fresh: string[] = [];
    for (const input of inputs) {
      if (extra.has(input)) continue;
      const old = known.get(input);
      if (old) { next.push(old); continue; }
      const line = newLine(input, 0, findExisting(Object.values(store.model.jobs), input) !== undefined);
      if (mode === 'magnet' && !isMagnet(input)) line.result = { ok: true, kind: 'unsupported', reason: "This isn't a magnet. Use Add links for other links.", checkIncomplete: false };
      next.push(line);
      if (!line.result) fresh.push(input);
    }

    lines = withDefaults(renumber(next), skipListed);
    for (const input of fresh) enqueue(input);
  }

  // ---- Asking the server what each line is: three at a time ----

  const queue: string[] = [];
  let workers = 0;

  function localClassify(input: string): ClassifyResult {
    return /^(https?|ftp):\/\//i.test(input)
      ? { ok: true, kind: 'ordinaryLink', checkIncomplete: false }
      : { ok: true, kind: 'unsupported', reason: 'Only http, https and ftp addresses can be added without a provider account.', checkIncomplete: false };
  }

  async function classify(input: string): Promise<ClassifyResult> {
    if (!remoteOn) return localClassify(input);
    // The files of a magnet or .torrent file are asked for now, so they can be chosen straight after Add.
    return store.ask<ClassifyResult>('remote.classify', { text: input, withFiles: isMagnet(input) || isUpload(input) });
  }

  function enqueue(input: string) {
    queue.push(input);
    while (workers < 3 && queue.length > 0) {
      workers++;
      void work();
    }
  }

  async function work() {
    try {
      while (queue.length > 0) {
        const input = queue.shift()!;
        if (!lines.some((line) => line.input === input && !line.result)) continue;
        try {
          update(input, { result: await classify(input), error: null });
        } catch (error) {
          const message = error instanceof Error ? error.message : String(error);
          update(input, { result: { ok: false, kind: 'undetermined', checkIncomplete: false, error: message } as ClassifyResult, error: message });
        }
      }
    } finally {
      workers--;
    }
  }

  function retry(line: AddLine) {
    update(line.input, { result: null, error: null, direct: false, touched: false });
    enqueue(line.input);
  }

  // ---- Starting points: an imported list's answers, and uploaded .torrent files ----

  let seeded = false;
  $effect(() => {
    if (seeded || seed.length === 0) return;
    seeded = true;
    const next = seed.map((entry, index) => ({ ...newLine(entry.input, index + 1, findExisting(Object.values(store.model.jobs), entry.input) !== undefined), result: entry.result }));
    extra = new Set(seed.map((entry) => entry.input));
    lines = withDefaults(renumber(next), skipListed);
  });

  async function uploadFiles(files: File[]) {
    if (files.length === 0) return;
    uploading = true;
    uploadProblems = [];
    for (const file of files) {
      try {
        const response = await fetch('/api/upload/torrent', { method: 'POST', body: file, credentials: 'same-origin', headers: { 'Content-Type': 'application/x-bittorrent' } });
        // A reference to the uploaded file (upload:…), not its path on the server.
        const body = (await response.json()) as { ok: boolean; upload?: string; error?: string };
        if (!body.ok || !body.upload) throw new Error(body.error ?? 'The upload failed.');
        if (lines.some((line) => line.input === body.upload)) continue;
        extra = new Set([...extra, body.upload]);
        const line = { ...newLine(body.upload, 0), label: file.name };
        lines = withDefaults(renumber([...lines, line]), skipListed);
        enqueue(body.upload);
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

  async function paste() {
    // Only on this click: the clipboard is never read unasked.
    try {
      const clip = await navigator.clipboard.readText();
      if (!clip.trim()) return;
      text = text.trim() ? `${text.trimEnd()}\n${clip}` : clip;
      typed();
    } catch {
      store.toast("The browser wouldn't hand over the clipboard. Paste into the box instead.", 'error');
    }
  }

  // ---- Adding ----

  function ask(line: AddLine, name: string, files: RemoteFile[]): Promise<FileChoice | null> {
    const picked = new Set(files.filter((file) => file.selectable !== false).map((file) => file.fileId));
    choosing = { line, name, files, picked };
    return new Promise((resolve) => { chosenResolve = resolve; });
  }

  function answerChoice(answer: FileChoice | null) {
    const resolve = chosenResolve;
    chosenResolve = null;
    choosing = null;
    resolve?.(answer);
  }

  function togglePicked(id: string) {
    if (!choosing) return;
    const next = new Set(choosing.picked);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    choosing = { ...choosing, picked: next };
  }

  interface RemoteAdded { ok: boolean; existing?: boolean; note?: string; job?: { id: string; name?: string; displayHost?: string } }

  async function setPackagePassword(firstJobId: string | undefined, problems: string[]) {
    if (!archivePassword) return;
    if (!firstJobId) {
      problems.push("The archive password wasn't saved, because no download of these links was added.");
      return;
    }

    // The package exists once its first job does; the list of packages follows a moment later.
    let group = store.model.groups.find((g) => g.jobIds.includes(firstJobId));
    for (let wait = 0; !group && wait < 10; wait++) {
      await new Promise((resolve) => setTimeout(resolve, 200));
      group = store.model.groups.find((g) => g.jobIds.includes(firstJobId));
    }

    if (!group) {
      problems.push("The archive password wasn't saved, because these links didn't end up in one package.");
      return;
    }

    try {
      await store.ask('archive.setPassword', { packageId: group.id, password: archivePassword });
    } catch (error) {
      problems.push(`The archive password wasn't saved: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  async function add() {
    destinationError = '';
    const picks = chosen(lines);
    const folder = destination.trim();
    if (picks.some((line) => routeOf(line) === 'provider') && folder && !isFullPath(folder)) {
      destinationError = 'The destination has to be a full path, such as /downloads or D:\\Downloads.';
      return;
    }

    adding = true;
    report = '';
    const problems: string[] = [];
    const messages: string[] = [];
    const handled: string[] = [];
    let downloads = 0;
    let sent = 0;
    const jobs: string[] = [];
    // The folder is remembered for manual downloads only when it isn't just the default one.
    const remember = !!folder && folder !== settings?.defaultDownloadDirectory;

    // Ordinary links first, one job.add each.
    const direct = picks.filter((line) => routeOf(line) === 'direct');
    if (direct.length > 0) {
      const urls = direct.map((line) => line.input);
      const together = keepPackage && urls.length >= 2;
      const key = together ? `web-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}` : undefined;
      const name = together ? packageName.trim() || packageNameFor(urls) : undefined;
      let accepted = 0, duplicates = 0, failed = 0;
      let firstError: string | undefined;
      let firstJob: string | undefined;
      for (const line of direct) {
        try {
          const result = await store.ask<{ accepted: boolean; duplicate?: boolean; error?: string; job?: { id: string } }>('job.add', {
            url: line.input, destinationDirectory: folder || undefined, rememberDestination: remember, startMode, queueId, conflictPolicy: conflict,
            source: 'web', packageKey: key, packageName: name,
          });
          if (!result.accepted) throw new Error(result.error ?? 'The server rejected the request.');
          accepted++;
          if (result.duplicate) duplicates++;
          firstJob ??= result.job?.id;
          handled.push(line.input);
        } catch (error) {
          failed++;
          firstError ??= error instanceof Error ? error.message : String(error);
        }
      }

      if (failed > 0 || duplicates > 0) {
        messages.push(`Added ${accepted} of ${urls.length} downloads${duplicates > 0 ? ` (${duplicates} already queued)` : ''}${failed > 0 ? `; ${failed} failed. ${firstError ?? ''}`.trimEnd() : '.'}`);
      } else {
        downloads = accepted;
      }

      if (together && archivesSupported) await setPackagePassword(firstJob, problems);
    }

    // The files of magnets and .torrent files are chosen first, one after another; nothing is sent until all are chosen.
    const provider = picks.filter((line) => routeOf(line) === 'provider');
    const toSend: { line: AddLine; files: FileChoice | null }[] = [];
    for (const line of provider) {
      const folderLink = line.result?.kind === 'folderLink';
      const files = !folderLink ? filesToChoose(line) : null;
      if (!files) {
        toSend.push({ line, files: folderLink || (chooseLater && chooseLaterApplies(line)) ? { selection: 'ask' } : null });
        continue;
      }

      const name = lineWords(line, providerName, many).name;
      const answer = await ask(line, name, files);
      if (answer) toSend.push({ line, files: answer });
    }

    for (const { line, files } of toSend) {
      const r = line.result!;
      const words = lineWords(line, providerName, many);
      const pw = pwOf(line.input);
      const hoster = r.kind === 'hosterLink';
      const payload: Record<string, unknown> = r.kind === 'folderLink' && r.itemId && r.provider && r.itemKind
        ? { provider: r.provider, itemKind: r.itemKind, itemId: r.itemId }
        : { input: line.input };
      Object.assign(payload, {
        destination: folder || undefined, queueId, startMode, approveUncached: true,
        selection: files?.selection, fileIds: files?.fileIds, filePaths: files?.filePaths,
      });
      if (hoster && pw.has) {
        // The archive password comes first and the site's is a box of its own; "use it as the site password too" sends one for both.
        payload.linkPassword = (archivesSupported && pw.same ? pw.archive : pw.site) || undefined;
        payload.archivePassword = archivesSupported ? pw.archive || undefined : undefined;
      }

      try {
        const result = await store.ask<RemoteAdded>('remote.add', payload);
        handled.push(line.input);
        if (result.existing) problems.push(alreadyListed(words.name, result.note));
        else if (result.job) { sent++; jobs.push(result.job.id); }
      } catch (error) {
        problems.push(refused(words.name, error instanceof Error ? error.message : String(error), providerName));
      }
    }

    adding = false;
    clearPasswords();
    const added = addedWords(downloads, sent, providerName);
    if (messages.length === 0 && problems.length === 0) {
      if (added) store.toast(added);
      // One provider item and nothing direct: the list selects it and scrolls to it.
      if (jobs.length === 1 && direct.length === 0) store.revealId = jobs[0];
      onclose();
      return;
    }

    // Every problem in one message, and what failed stays here to be tried again.
    report = addReport(added, messages, problems);
    text = removeInputs(text, handled);
    extra = new Set([...extra].filter((input) => !handled.includes(input)));
    lines = withDefaults(renumber(lines.filter((line) => !handled.includes(line.input))), skipListed);
  }

  function close() {
    clearPasswords();
    answerChoice(null);
    onclose();
  }

  function keydown(event: KeyboardEvent) {
    if (ready && !choosing && submitsOnEnter(event)) { event.preventDefault(); void add(); }
  }
</script>

<Dialog title={choosing ? `Choose files: ${choosing.name}` : title} onclose={close} wide>
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div onkeydown={keydown}>
  {#if choosing}
    <p class="mb-2 text-[13px] text-muted">{providerName} lists these files. Tick the ones to download.</p>
    <div class="mb-1 flex justify-between text-[12px] text-muted"><span>{choosing.picked.size} of {choosing.files.length} files</span>
      <button class="underline" onclick={() => { if (choosing) choosing = { ...choosing, picked: choosing.picked.size ? new Set() : new Set(choosing.files.filter((f) => f.selectable !== false).map((f) => f.fileId)) }; }}>{choosing.picked.size ? 'None' : 'All'}</button></div>
    <ul class="card max-h-72 divide-y divide-divider overflow-y-auto text-[12.5px]">
      {#each choosing.files as file (file.fileId)}
        <li><label class="flex items-center gap-2 px-3 py-1.5 {file.selectable === false ? 'opacity-50' : ''}">
          <input type="checkbox" disabled={file.selectable === false} checked={choosing.picked.has(file.fileId)} onchange={() => togglePicked(file.fileId)} />
          <span class="min-w-0 flex-1 truncate" title={file.relativePath ?? ''}>{file.relativePath ?? 'A name that is not safe to use'}</span><span class="tabular text-muted">{bytes(file.size)}</span>
        </label></li>
      {/each}
    </ul>
  {:else}
    {#if report}
      <p class="mb-3 whitespace-pre-line rounded border border-icon-danger/50 bg-highlight px-3 py-2 text-[13px]" role="alert">{report}</p>
    {/if}

    {#if showPaste}
      <label class="label" for="add-text">{mode === 'magnet' ? 'Paste magnet links' : 'Paste links or any text containing links'}</label>
      <textarea id="add-text" class="field h-28 resize-y font-mono text-[12px]" bind:value={text} spellcheck="false"
        placeholder={mode === 'magnet' ? 'magnet:?xt=urn:btih:…' : `Links, folder links and magnets, one per line`} oninput={typed}></textarea>
      <div class="mt-1"><button class="btn py-0.5 text-[12px]" type="button" onclick={paste}><Icon name="link" size={14} /> Paste</button></div>
    {/if}

    {#if showUpload}
      <!-- svelte-ignore a11y_no_static_element_interactions -->
      <div class="mt-2 rounded border border-dashed px-3 py-2 text-[12.5px] {dragging ? 'border-accent bg-highlight' : 'border-divider'}"
        ondragover={(event) => { event.preventDefault(); dragging = true; }} ondragleave={() => (dragging = false)} ondrop={dropped}>
        <label class="label" for="torrent-files">{mode === 'torrent' ? '.torrent files from this computer (several allowed, or drop them here); they are sent to the server' : 'Or add .torrent files from this computer; they are sent to the server'}</label>
        <input id="torrent-files" type="file" multiple accept=".torrent,application/x-bittorrent" class="field" disabled={uploading}
          onchange={(event) => { const input = event.currentTarget as HTMLInputElement; void uploadFiles([...(input.files ?? [])]); input.value = ''; }} />
        {#each uploadProblems as problem}<p class="mt-1 text-[12px] text-error" role="alert">{problem}</p>{/each}
      </div>
    {:else if mode === 'torrent'}
      <p class="hint">Torrents go through a provider account, and none is set up on this server.</p>
    {/if}

    {#if summary.text}<p class="mt-2 text-[12.5px] {summary.problem ? 'text-error' : 'text-muted'}" role="status">{summary.text}</p>{/if}

    {#if lines.length >= 2}
      <label class="mt-1 flex items-center gap-2 text-[13px]"><input type="checkbox" checked={allState === true} indeterminate={allState === null} onchange={() => { lines = toggleAll(lines); }} /> Select all</label>
    {/if}

    {#if lines.length}
      <ul class="card mt-2 max-h-72 divide-y divide-divider overflow-y-auto text-[12.5px]">
        {#each lines as line (line.input)}
          {@const words = lineWords(line, providerName, many)}
          {@const pw = pwOf(line.input)}
          <li class="px-3 py-1.5">
            <div class="flex items-start gap-2">
              <input type="checkbox" class="mt-1" checked={!!line.selected} disabled={!words.canSelect} aria-label={`${words.name}, ${words.word}`}
                title={showsAddress(line) ? line.input : undefined}
                onchange={(event) => update(line.input, { selected: (event.currentTarget as HTMLInputElement).checked, touched: true })} />
              <div class="min-w-0 flex-1">
                <div class="truncate" title={showsAddress(line) ? line.input : words.name}>{words.name}</div>
                <div class="text-[12px] font-semibold {words.problem ? 'tone-failed' : words.word === 'Checking…' ? 'text-muted' : ''}">{words.word}</div>
                {#if words.detail}<div class="text-[12px] text-muted">{words.detail}</div>{/if}
              </div>
            </div>
            {#if words.canRetry || words.canChooseDirect || words.canTakeLinkPassword}
              <div class="ml-7 mt-1 space-y-1.5">
                {#if words.canRetry}<button class="btn py-0 text-[12px]" aria-label={`Try again: ${words.name}`} onclick={() => retry(line)}>Try again</button>{/if}
                {#if words.canChooseDirect}
                  <label class="flex items-center gap-2 text-[12px]"><input type="checkbox" checked={line.direct} onchange={() => update(line.input, { direct: !line.direct, touched: false })} /> Download directly, not through {providerName}</label>
                {/if}
                {#if words.canTakeLinkPassword}
                  <label class="flex items-center gap-2 text-[12px]"><input type="checkbox" checked={pw.has} onchange={(event) => setPw(line.input, { has: (event.currentTarget as HTMLInputElement).checked })} /> It needs a password</label>
                  {#if pw.has}
                    {#if archivesSupported}
                      <div>
                        <label class="label" for={`apw-${line.position}`}>Archive password: used when the downloaded archives are extracted</label>
                        <input id={`apw-${line.position}`} class="field max-w-md" type="password" autocomplete="off" placeholder="Optional" value={pw.archive} oninput={(event) => setPw(line.input, { archive: (event.currentTarget as HTMLInputElement).value })} />
                      </div>
                      <label class="flex items-center gap-2 text-[12px]"><input type="checkbox" checked={pw.same} onchange={(event) => setPw(line.input, { same: (event.currentTarget as HTMLInputElement).checked })} /> Use it as the site password too</label>
                    {/if}
                    {#if !(archivesSupported && pw.same)}
                      <div>
                        <label class="label" for={`spw-${line.position}`}>Site password: only if the link's page asks for one before it shows the file</label>
                        <input id={`spw-${line.position}`} class="field max-w-md" type="password" autocomplete="off" placeholder="Optional" value={pw.site} oninput={(event) => setPw(line.input, { site: (event.currentTarget as HTMLInputElement).value })} />
                      </div>
                    {/if}
                  {/if}
                {/if}
              </div>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}

    {#if showChooseLater}
      <label class="mt-2 flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={chooseLater} /> Let me choose files when {providerName} is done</label>
    {/if}

    <div class="mt-4 grid gap-3 sm:grid-cols-2">
      <div class="sm:col-span-2"><FolderField id="add-destination" label="Destination folder" bind:value={destination} showSpace /></div>
      {#if destinationError}<p class="text-[12px] text-error sm:col-span-2" role="alert">{destinationError}</p>{/if}
      <div>
        <label class="label" for="add-start">Start</label>
        <select id="add-start" class="field" bind:value={startMode}>
          <option value="startNow">Start now</option>
          <option value="queue">When the queue gets to it</option>
          <option value="later">Download later</option>
        </select>
      </div>
      <div>
        <label class="label" for="add-queue">Queue</label>
        <select id="add-queue" class="field" bind:value={queueId}>
          {#each queues as queue (queue.id)}<option value={queue.id}>{queue.name}</option>{/each}
        </select>
      </div>
    </div>

    {#if mode === 'links' && !single}
    <fieldset class="mt-4 space-y-2 {directTicked.length ? '' : 'opacity-60'}" disabled={directTicked.length === 0}>
      <legend class="mb-1 text-[12px] text-muted">For ordinary links</legend>
      <div>
        <label class="label" for="add-conflict">If a file exists</label>
        <select id="add-conflict" class="field" bind:value={conflict}>
          {#each conflictOptions as option (option.value)}<option value={option.value}>{option.label}</option>{/each}
        </select>
      </div>
      <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={skipListed} onchange={refreshDefaults} /> Skip URLs that are already in the list</label>
      <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={keepPackage} /> Keep as one package</label>
      {#if directTicked.length >= 2 && keepPackage}
        <div>
          <label class="label" for="add-package">Package name (optional)</label>
          <input id="add-package" class="field" bind:value={packageName} placeholder="Named from the file names when left empty" />
        </div>
        {#if archivesSupported}
          <div>
            <label class="label" for="add-archive-password">Archive password for this package (optional)</label>
            <input id="add-archive-password" class="field max-w-md" type="password" bind:value={archivePassword} autocomplete="off" />
            <p class="hint mt-1">For RAR archives in this package, used when they are extracted. It is not a link password, and it never leaves the server.</p>
          </div>
        {/if}
      {/if}
    </fieldset>
    {/if}
  {/if}
  </div>

  {#snippet footer()}
    {#if choosing}
      <button class="btn-primary" disabled={choosing.picked.size === 0} onclick={() => choosing && answerChoice(fileChoiceFrom(choosing.line, choosing.picked))}>Add</button>
      <button class="btn" onclick={() => answerChoice(null)}>Skip this one</button>
    {:else}
      <button class="btn-primary" disabled={!ready} onclick={add}>{adding ? 'Adding…' : ticked > 1 ? `Add ${ticked}` : 'Add'}</button>
      <button class="btn" onclick={close}>{report && lines.length === 0 ? 'Close' : 'Cancel'}</button>
    {/if}
  {/snippet}
</Dialog>
