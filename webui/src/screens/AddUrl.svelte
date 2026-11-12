<script lang="ts">
  import { onDestroy, onMount, untrack } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import {
    addressCheckDelay, basicAuthorization, categoryChoices, checkButtonText, emptyInfoWords, fileInfoProblem, conflictOptions, customCategories, destinationFor, findExisting,
    handoffWords, metadataText, metadataTitle, nameFromUrl, notALinkWords, oneLine, providerWords, readAddress, recentAddresses, serverNameToShow, validUserName,
    checkWithCredentialsWords, credentialsClearedWords, leavesSite, noTorrentsWords, siteOfAddress,
    type ConflictPolicy, type FileInfo,
  } from '../lib/addflow';
  import { ftpNotSupported, hideAddresses } from '../lib/addview';
  import { magnetsIn } from '../lib/torrents';
  import { statusText } from '../lib/format';
  import { newUuid } from '../lib/ids';
  import { submitsOnEnter } from '../lib/keys';
  import { store } from '../lib/store.svelte';
  import type { ClassifyResult, Job } from '../lib/types';

  // Add URL in one dialog, as the desktop's (UI.R38): the address at the top; once it stops changing, the dialog works out
  // what it is by itself and fills in the name, size, category and folder below, with Start download, Download later and
  // Cancel at the bottom. Several links, a numbered address, a magnet, or a link a provider handles go on to the dialog
  // made for them, which Continue opens.
  // ontorrent: set while the server downloads torrents itself (T1); a magnet then goes to the torrent dialog.
  // onlinks: several links, or a numbered address, for Add Links.
  let { initial = '', onclose, onprovider, ontorrent, onlinks }: {
    initial?: string;
    onclose: () => void;
    onprovider: (input: string, result: ClassifyResult) => void;
    ontorrent?: (magnets: string[]) => void;
    onlinks: (links: string[]) => void;
  } = $props();

  const settings = $derived(store.model.settings);
  const providerAdd = $derived(store.has('remote') && store.model.accounts.some((a) => a.hasKey && !a.keyUnreadable));
  const providerName = $derived(store.model.accounts[0]?.providerName ?? 'the provider');
  const queues = $derived([...(settings?.queues ?? [])].sort((a, b) => a.order - b.order));
  const recents = $derived(recentAddresses(settings));

  let step = $state<'add' | 'duplicate'>('add');
  let address = $state(untrack(() => initial));
  // 'Site' while the box shows only the site of a link that holds an account key.
  let addressLabel = $state('URL');
  let addressStatus = $state('');
  let addressProblem = $state(false);
  let checking = $state(false);
  // A link nobody could classify: Try again, or tick the box for a direct download.
  let cantTell = $state(false);
  let direct = $state(false);
  let cantTellLink: { text: string; result: ClassifyResult | null } = { text: '', result: null };
  // The address this dialog adds, once it is ready; empty before.
  let url = $state('');
  let ready = $state(false);
  // What Continue opens, for an address another dialog takes.
  let handoff = $state<(() => void) | null>(null);
  let result = $state<ClassifyResult | null>(null);
  let existing = $state<Job | null>(null);
  // The box's text that what is shown is about.
  let evaluated: string | null = null;
  let checkGeneration = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  // Start, Enter or Download later was pressed before the server answered: which one (review of 0.7.2, ux-3).
  let goWhenReady: 'start' | 'later' | null = null;
  // Review of 0.7.2, security-1: the site the user name and password were typed for. An address on another site clears
  // them, and the file check that runs by itself never sends them; Check again and Start do.
  let credentialsSite: string | null = null;
  let credentialsDrops = 0;
  let dropsWhenPressed = 0;

  // File information
  let info = $state(emptyInfoWords);
  let infoBusy = $state(false);
  let infoTitle = $state<string | undefined>(undefined);
  let checkedOnce = $state(false);
  let knownSize = $state<number | null>(null);
  let generation = 0;
  let name = $state('');
  let nameEdited = false;

  let categoryId = $state<string | null>(null);
  const categories = $derived(categoryChoices(settings));
  let destination = $state('');
  let customDestination = false;
  let autoDestination = '';
  let remember = $state(true);
  let description = $state('');
  let queueId = $state(store.model.settings?.defaultQueueId ?? 0);
  let conflict = $state<ConflictPolicy>('rename');

  let newCategoryName = $state('');
  let newCategoryExtensions = $state('');
  let categoryError = $state('');
  let creating = $state(false);

  let useAuth = $state(false);
  let user = $state('');
  let password = $state('');
  let authError = $state('');
  let saving = $state(false);
  let failure = $state('');

  const torrentInputs = $derived(Boolean(ontorrent) || providerAdd);
  const primaryText = $derived(saving ? 'Adding…' : handoff ? 'Continue' : cantTell && !direct ? 'Try again' : 'Start download');

  onMount(() => {
    useCategoryFolder(true);
    if (address.trim()) evaluate(address);
  });
  onDestroy(() => {
    clearTimeout(timer);
    checkGeneration++;
    generation++;
  });

  // ---- The address ----

  function say(words: string, problem = false) {
    addressStatus = words;
    addressProblem = problem;
  }

  function reset() {
    ready = false;
    handoff = null;
    cantTell = false;
    direct = false;
  }

  function showNoLink(words: string) {
    reset();
    say(words, words !== '');
    if (url) {
      url = '';
      result = null;
      generation++;
      infoBusy = false;
    }
    info = emptyInfoWords;
    infoTitle = undefined;
  }

  function showHandoff(words: string, next: () => void) {
    showNoLink('');
    handoff = () => next();
    say(words);
  }

  function showDirect(text: string, answer: ClassifyResult | null, keepCantTell = false) {
    const changed = text !== url;
    if (!keepCantTell) {
      reset();
      say('');
    }
    ready = true;
    handoff = null;
    url = text;
    result = answer;
    // A direct link holds an account key, which the server keeps out of everything shown or saved; the box shows its site
    // instead, as the old second step did, and the link itself stays here.
    if (answer?.kind === 'providerDirectLink') showInBox(answer.displayHost?.trim() || siteOf(text), 'Site');
    if (changed && useAuth && leavesSite(credentialsSite, text)) {
      password = '';
      useAuth = false;
      credentialsSite = null;
      credentialsDrops++;
      say(credentialsClearedWords, true);
    }
    if (changed) {
      knownSize = null;
      if (!nameEdited) name = '';
      useCategoryFolder();
      void previewMetadata(true);
    }
  }

  function showCantTell(words: string, text: string, answer: ClassifyResult | null) {
    showNoLink('');
    cantTell = true;
    cantTellLink = { text, result: answer };
    say(`${words} So it isn't treated as an ordinary link unless you say so.`, true);
  }

  function directChanged() {
    if (!cantTell) return;
    if (direct) showDirect(cantTellLink.text, cantTellLink.result, true);
    else {
      ready = false;
      url = '';
      generation++;
      infoBusy = false;
      info = emptyInfoWords;
    }
  }

  function siteOf(text: string): string {
    try { return new URL(text).hostname || 'This link'; } catch { return 'This link'; }
  }

  // Puts text in the box that is already worked out, so it isn't checked again; editing it checks it as usual.
  function showInBox(text: string, label: string) {
    evaluated = text.trim();
    address = text;
    addressLabel = label;
  }

  // Works out what the text is; returns the server's check when one is needed.
  function evaluate(text: string): Promise<void> | null {
    clearTimeout(timer);
    const mine = ++checkGeneration;
    evaluated = text.trim();
    addressLabel = 'URL';
    // An earlier Enter was about the text before (go sets it again for this text).
    goWhenReady = null;
    checking = false;
    const input = readAddress(text, providerAdd);
    switch (input.kind) {
      case 'empty':
        showNoLink('');
        return null;
      case 'notALink':
        showNoLink(notALinkWords(torrentInputs));
        return null;
      case 'ftp':
        showNoLink(ftpNotSupported);
        return null;
      case 'severalLinks': {
        // A numbered address, or pasted text with several links, is several downloads, which Add Links lists one by one.
        const links = input.links;
        showHandoff(handoffWords(input, false), () => onlinks(links));
        return null;
      }
      case 'torrent':
        // T1: magnets go to the torrent dialog, which offers This PC and the provider per line (TorrentAddDialog).
        if (ontorrent) {
          const magnets = magnetsIn(input.address);
          showHandoff(handoffWords(input, true), () => ontorrent(magnets));
          return null;
        }
        if (!providerAdd) {
          // As the desktop says: the text is right, torrents are what's off (review of 0.7.2, ux-4).
          showNoLink(noTorrentsWords);
          return null;
        }
        return check(input.address, mine);
    }

    // The server answers at once for an address on its own computer or network, which no provider is asked about (UI.B27).
    if (!providerAdd) {
      showDirect(input.address, null);
      return null;
    }
    return check(input.address, mine);
  }

  async function check(text: string, mine: number) {
    showNoLink('');
    checking = true;
    say('Checking this address…');
    let answer: ClassifyResult;
    try {
      // What it says goes on to the provider's add dialog, which may offer its files.
      answer = await store.ask<ClassifyResult>('remote.classify', { text, withFiles: true });
    } catch (error) {
      if (mine !== checkGeneration) return;
      checking = false;
      showCantTell(error instanceof Error ? error.message : String(error), text, null);
      return;
    }
    if (mine !== checkGeneration) return;
    checking = false;
    const provider = answer.providerName?.trim() || providerName;
    if (answer.ok === false) showCantTell(answer.error ?? "The server couldn't check this address.", text, answer);
    else {
      switch (answer.kind) {
        case 'ordinaryLink':
        case 'providerDirectLink':
          showDirect(text, answer);
          break;
        case 'folderLink':
        case 'magnet':
        case 'torrentFile':
        case 'hosterLink':
          showHandoff(providerWords(answer.kind, answer.hosterName, provider), () => onprovider(text, answer));
          break;
        case 'undetermined':
          showCantTell(hideAddresses(answer.reason) || `${provider} couldn't tell what this is.`, text, answer);
          break;
        default:
          showNoLink(answer.reason?.trim() ? hideAddresses(answer.reason) : "This app can't use this.");
      }
    }

    // Download later doesn't open another dialog: when the answer is one, its line says Continue does. When the user name
    // and password were just cleared for this address, nothing goes on until the user has seen why.
    if (goWhenReady) {
      const wanted = goWhenReady;
      goWhenReady = null;
      if (credentialsDrops !== dropsWhenPressed) return;
      if (handoff) { if (wanted === 'start') handoff(); }
      else if (ready) void start(wanted === 'later');
    }
  }

  // The address stopped changing for a moment: check it.
  function schedule() {
    clearTimeout(timer);
    if (address.trim() === evaluated) return;
    // What was found is about the text before, so it can't be started until this text is checked, and an Enter pressed
    // for it doesn't start this one. Nothing counts as checked now, so going back to the same text checks it again rather
    // than leaving the dropped check's 'Checking' behind.
    checkGeneration++;
    evaluated = null;
    goWhenReady = null;
    checking = false;
    timer = setTimeout(() => { if (address.trim() !== evaluated) void evaluate(address); }, addressCheckDelay);
  }

  // Start download, Download later, Continue, and Enter.
  function go(later: boolean) {
    const text = address.trim();
    const drops = credentialsDrops;
    if (text !== evaluated || checking) {
      const pending = text !== evaluated ? evaluate(text) : null;
      if (pending || checking) {
        // The server is asked first; the button pressed (Start or Download later) goes on by itself once it has answered.
        goWhenReady = later ? 'later' : 'start';
        dropsWhenPressed = drops;
        return;
      }
    }
    // This address is on another site than the user name and password were typed for, so they were just cleared: nothing
    // is sent until the user has seen that.
    if (credentialsDrops !== drops) return;
    if (handoff) { handoff(); return; }
    if (cantTell && !direct) { void check(cantTellLink.text, ++checkGeneration); return; }
    if (!ready) {
      say(addressStatus && addressProblem ? addressStatus : notALinkWords(torrentInputs), true);
      document.getElementById('url-box')?.focus();
      return;
    }
    void start(later);
  }

  // ---- The download ----

  // The category's folder, unless the folder was typed by hand; picking a category always applies its folder.
  function useCategoryFolder(force = false) {
    if (customDestination && !force) return;
    const fileName = name.trim() || (url ? nameFromUrl(url) : '');
    autoDestination = destinationFor(settings, fileName, categoryId);
    destination = autoDestination;
    customDestination = false;
  }

  $effect(() => {
    if (destination !== autoDestination) customDestination = true;
  });

  function authHeaders(): Record<string, string> | null {
    if (!useAuth) return {};
    if (!validUserName(user)) {
      authError = 'Enter a user name without colons or line breaks.';
      return null;
    }

    return { Authorization: basicAuthorization(user, password) };
  }

  async function previewMetadata(automatic = false) {
    if (!url) return;
    if (automatic && useAuth) {
      generation++;
      infoBusy = false;
      checkedOnce = true;
      infoTitle = undefined;
      info = checkWithCredentialsWords;
      return;
    }
    const mine = ++generation;
    const checkingUrl = url;
    infoBusy = true;
    checkedOnce = true;
    infoTitle = undefined;
    info = 'Checking the server… You can start without waiting.';
    try {
      const headers = useAuth ? authHeaders() : {};
      if (headers === null) throw new Error('Enter a user name without colons or line breaks, then check again.');
      if (useAuth) credentialsSite ??= siteOfAddress(checkingUrl);
      const found = await store.ask<FileInfo>('file.metadata', { url: checkingUrl, headers });
      if (mine !== generation) return;
      if (found.ok === false) throw new Error(found.error ?? 'File information is unavailable.');
      knownSize = found.totalBytes ?? null;
      const server = serverNameToShow(found.fileName, checkingUrl, result?.kind === 'providerDirectLink');
      info = metadataText(found, server, nameEdited ? name : server);
      infoTitle = metadataTitle(found);
      if (!nameEdited && server) { name = server; useCategoryFolder(); }
    } catch (error) {
      if (mine === generation) info = `${fileInfoProblem(error instanceof Error ? error.message : String(error))} You can still start the download.`;
    } finally {
      if (mine === generation) infoBusy = false;
    }
  }

  function credentialsChanged() {
    authError = '';
    credentialsSite = useAuth && url ? siteOfAddress(url) : null;
    if (!url) return;
    generation++;
    infoBusy = false;
    info = 'Credentials changed. Check again to refresh the file info.';
  }

  async function createCategory() {
    categoryError = '';
    const label = newCategoryName.trim();
    if (!label) { categoryError = 'Enter a name for the category.'; return; }
    creating = true;
    // newUuid: the browser's own UUID call is missing over plain http from another computer (audit 4 M9).
    const id = newUuid().replace(/-/g, '');
    const extensions = newCategoryExtensions.split(/[\s,;]+/).map((e) => e.replace(/^\./, '')).filter(Boolean);
    let saved = false;
    try {
      saved = await store.saveSettings((s) => {
        s.customCategories = [...customCategories(s), { id, name: label, destinationDirectory: destination.trim(), extensions }];
      }, '');
    } finally {
      creating = false;
    }
    if (!saved) { categoryError = "The category wasn't saved."; return; }
    // The new category reaches the list with the next snapshot; wait for it so the box doesn't show blank.
    for (let wait = 0; wait < 20 && !categories.some((c) => c.id === id); wait++) await new Promise((r) => setTimeout(r, 100));
    categoryId = id;
    useCategoryFolder(true);
    newCategoryName = '';
    newCategoryExtensions = '';
  }

  async function start(later: boolean) {
    failure = '';
    authError = '';
    const headers = authHeaders();
    if (headers === null) return;
    if (useAuth) credentialsSite ??= siteOfAddress(url);
    // A download already on the list asks first, as on the desktop.
    const found = findExisting(Object.values(store.model.jobs), url);
    if (found) { existing = found; step = 'duplicate'; pendingLater = later; return; }
    await send(later, headers);
  }

  let pendingLater = false;

  async function send(later: boolean, headers = authHeaders() ?? {}) {
    saving = true;
    try {
      const reply = await store.ask<{ accepted: boolean; duplicate?: boolean; error?: string; job?: { id: string } }>('job.add', {
        url,
        fileName: name.trim() || undefined,
        categoryId: categoryId ?? undefined,
        destinationDirectory: destination.trim() || undefined,
        startMode: later ? 'later' : 'startNow',
        description: description.trim() || undefined,
        rememberDestination: remember,
        conflictPolicy: conflict,
        source: 'web',
        queueId,
        headers: Object.keys(headers).length ? headers : undefined,
      });
      if (!reply.accepted) throw new Error(reply.error ?? "The server didn't add it and didn't say why. Try again.");
      store.toast(reply.duplicate ? 'That download was already queued.' : 'Added 1 download.');
      password = '';
      onclose();
    } catch (error) {
      failure = error instanceof Error ? error.message : String(error);
      step = 'add';
    } finally {
      saving = false;
    }
  }

  function selectExisting() {
    if (existing) {
      store.revealId = existing.id;
      store.toast(`It is already in the list as ${existing.fileName}.`);
      location.hash = '#/all';
    }

    onclose();
  }

  // The browser glues the lines of a pasted list into one broken address; the lines go in side by side instead, so a list
  // still goes on to Add links.
  function paste(event: ClipboardEvent) {
    const text = event.clipboardData?.getData('text');
    if (!text || !/[\r\n]/.test(text)) return;
    event.preventDefault();
    const box = event.currentTarget as HTMLInputElement;
    const pasted = oneLine(text);
    const start = box.selectionStart ?? address.length;
    const end = box.selectionEnd ?? start;
    address = address.slice(0, start) + pasted + address.slice(end);
    const caret = start + pasted.length;
    queueMicrotask(() => box.setSelectionRange(caret, caret));
    schedule();
  }

  function keydown(event: KeyboardEvent) {
    if (!submitsOnEnter(event) || saving || creating || step !== 'add') return;
    event.preventDefault();
    go(false);
  }
</script>

<Dialog title={step === 'duplicate' ? 'Download already exists' : 'Add download'} {onclose}>
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div onkeydown={keydown}>
  {#if step === 'duplicate'}
    <p class="text-[13px]">{existing?.fileName} ({existing ? statusText(existing) : ''}) is already in the list for this URL.</p>
  {:else}
    {#if failure}<p class="mb-3 rounded border border-icon-danger/50 bg-highlight px-3 py-2 text-[13px] text-error" role="alert">{failure}</p>{/if}
    <label class="label" for="url-box">{addressLabel}</label>
    <div class="flex gap-2">
      <input id="url-box" class="field font-mono" data-autofocus bind:value={address} list="recent-urls" spellcheck="false" autocomplete="off"
        placeholder={providerAdd || ontorrent ? 'https://example.com/file.zip, or a magnet' : 'https://example.com/file.zip'}
        oninput={schedule} onchange={schedule} onpaste={paste} />
      <button class="btn shrink-0" type="button" title="Paste the address from the clipboard"
        onclick={async () => { try { const clip = oneLine((await navigator.clipboard.readText()).trim()); if (clip && clip.length < 4096) { address = clip; void evaluate(clip); } } catch { store.toast("The browser wouldn't hand over the clipboard. Paste into the box instead.", 'error'); } }}><Icon name="link" size={15} /> Paste</button>
    </div>
    <datalist id="recent-urls">{#each recents as recent (recent)}<option value={recent}></option>{/each}</datalist>
    {#if addressStatus}<p class="mt-2 text-[12.5px] {addressProblem ? 'text-error' : 'text-muted'}" role={addressProblem ? 'alert' : 'status'}>{addressStatus}</p>{/if}
    {#if cantTell}
      <label class="mt-2 flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={direct} onchange={directChanged} /> Download directly, not through {providerName}</label>
    {/if}

    {#if !handoff}
    <div class="mt-3 space-y-3">
      {#if ready && result?.note}<p class="text-[12.5px] text-muted">{hideAddresses(result.note)}</p>{/if}
      <p class="whitespace-pre-line text-[12.5px]" role="status" aria-live="polite" title={infoTitle}>{info}</p>
      {#if ready}
        <button class="btn" type="button" disabled={infoBusy} onclick={() => previewMetadata()}>{infoBusy ? 'Checking…' : checkButtonText(checkedOnce)}</button>
      {/if}

      <div>
        <label class="label" for="file-name">File name (optional)</label>
        <input id="file-name" class="field" bind:value={name} placeholder="Resolved from the URL or server" oninput={() => { nameEdited = true; useCategoryFolder(); }} />
      </div>
      <div>
        <label class="label" for="category">Category</label>
        <select id="category" class="field" value={categoryId ?? ''} onchange={(event) => { const v = (event.currentTarget as HTMLSelectElement).value; categoryId = v === '' ? null : v; useCategoryFolder(true); }}>
          {#each categories as category (category.id ?? 'auto')}<option value={category.id ?? ''}>{category.name}</option>{/each}
        </select>
      </div>
      <div>
        <FolderField id="destination" label="Destination folder" bind:value={destination} showSpace neededBytes={knownSize} />
        <label class="mt-1 flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={remember} /> {categoryId ? 'Use this folder for this category next time too' : 'Use this folder next time too'}</label>
      </div>

      <details class="card px-3 py-2">
        <summary class="cursor-pointer text-[13px]">Create a category</summary>
        <div class="mt-2 space-y-2">
          <div><label class="label" for="cat-name">New category name</label><input id="cat-name" class="field" bind:value={newCategoryName} /></div>
          <div><label class="label" for="cat-ext">File extensions (optional)</label><input id="cat-ext" class="field" bind:value={newCategoryExtensions} placeholder="zip 7z rar" /></div>
          <p class="hint">Create a saved category using the destination folder above. Matching extensions apply to future downloads; leave blank for manual selection. Creating it saves immediately, even if this download is cancelled.</p>
          <button class="btn" type="button" disabled={creating} onclick={createCategory}>Create category</button>
          {#if categoryError}<p class="text-[12px] text-error" role="alert">{categoryError}</p>{/if}
        </div>
      </details>

      <div>
        <label class="label" for="description">Description (optional)</label>
        <input id="description" class="field" bind:value={description} />
      </div>

      <details class="card px-3 py-2" open={authError !== '' || undefined}>
        <summary class="cursor-pointer text-[13px]">Authentication (optional)</summary>
        <div class="mt-2 space-y-2">
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={useAuth} onchange={credentialsChanged} /> Use a user name and password for this download</label>
          <div><label class="label" for="auth-user">User name</label><input id="auth-user" class="field" bind:value={user} disabled={!useAuth} autocomplete="off" oninput={credentialsChanged} /></div>
          <div><label class="label" for="auth-pass">Password</label><input id="auth-pass" class="field" type="password" bind:value={password} disabled={!useAuth} autocomplete="off" oninput={credentialsChanged} /></div>
          <p class="hint">HTTP Basic authentication. Credentials apply only to this download and are protected on the server when saved.</p>
          {#if authError}<p class="text-[12px] text-error" role="alert">{authError}</p>{/if}
        </div>
      </details>

      <details class="card px-3 py-2">
        <summary class="cursor-pointer text-[13px]">Queue and file conflicts</summary>
        <div class="mt-2 space-y-2">
          <div><label class="label" for="queue">Queue</label>
            <select id="queue" class="field" bind:value={queueId}>{#each queues as queue (queue.id)}<option value={queue.id}>{queue.name}</option>{/each}</select></div>
          <div><label class="label" for="conflict">If the file exists</label>
            <select id="conflict" class="field" bind:value={conflict}>{#each conflictOptions as option (option.value)}<option value={option.value}>{option.label}</option>{/each}</select></div>
        </div>
      </details>
    </div>
    {/if}
  {/if}
  </div>

  {#snippet footer()}
    {#if step === 'duplicate'}
      <button class="btn-primary" disabled={saving} onclick={() => send(pendingLater)}>Download again</button>
      <button class="btn" onclick={selectExisting}>Select existing</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {:else}
      <button class="btn-primary" disabled={saving || creating || !address.trim()} onclick={() => go(false)}>{primaryText}</button>
      {#if !handoff}
        <button class="btn" disabled={saving || creating || !ready} onclick={() => go(true)}>Download later</button>
      {/if}
      <button class="btn" onclick={onclose}>Cancel</button>
    {/if}
  {/snippet}
</Dialog>
