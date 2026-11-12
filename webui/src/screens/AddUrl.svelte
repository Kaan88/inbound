<script lang="ts">
  import { untrack } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import {
    basicAuthorization, categoryChoices, checkButtonText, fileInfoProblem, conflictOptions, customCategories, destinationFor, findExisting, metadataText, metadataTitle, nameFromUrl, recentAddresses,
    serverNameToShow, validUserName, type ConflictPolicy, type FileInfo,
  } from '../lib/addflow';
  import { hideAddresses, isMagnet } from '../lib/addview';
  import { magnetsIn } from '../lib/torrents';
  import { statusText } from '../lib/format';
  import { newUuid } from '../lib/ids';
  import { submitsOnEnter } from '../lib/keys';
  import { store } from '../lib/store.svelte';
  import type { ClassifyResult, Job } from '../lib/types';

  // Add URL, in the desktop's two steps: "Enter new address to download", then "Add download" with the
  // checks, the name, category, folder, description, sign-in and conflict choices. An address a provider handles
  // goes on to the multi-line dialog with that one line.
  // ontorrent: set while the server downloads torrents itself (T1); a magnet then goes to the torrent dialog.
  let { initial = '', onclose, onprovider, ontorrent }: { initial?: string; onclose: () => void; onprovider: (input: string, result: ClassifyResult) => void; ontorrent?: (magnets: string[]) => void } = $props();

  const settings = $derived(store.model.settings);
  const providerAdd = $derived(store.has('remote') && store.model.accounts.some((a) => a.hasKey && !a.keyUnreadable));
  const providerName = $derived(store.model.accounts[0]?.providerName ?? 'the provider');
  const queues = $derived([...(settings?.queues ?? [])].sort((a, b) => a.order - b.order));
  const recents = $derived(recentAddresses(settings));

  let step = $state<'address' | 'info' | 'duplicate'>('address');
  let address = $state(untrack(() => initial));
  let addressStatus = $state('');
  let addressProblem = $state(false);
  let checking = $state(false);
  let canChooseDirect = $state(false);
  let direct = $state(false);
  let url = $state('');
  let result = $state<ClassifyResult | null>(null);
  let existing = $state<Job | null>(null);

  // File information
  let info = $state('File information has not been checked.');
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

  // Like the desktop: only an address that can hold a provider's key is hidden; an ordinary one is shown in full.
  const shownAddress = $derived(result?.kind === 'providerDirectLink' ? result.displayHost ?? 'This link' : url);

  // ---- Step 1 ----

  async function next() {
    const text = address.trim();
    addressProblem = false;
    if (direct && canChooseDirect && text === url) { toInfo(text, result); return; }
    // T1: magnets go to the torrent dialog, which offers This PC and the provider per line (TorrentAddDialog).
    const magnets = ontorrent ? magnetsIn(text) : [];
    if (ontorrent && magnets.length > 0 && isMagnet(text)) { ontorrent(magnets); return; }
    if (!text) { addressStatus = providerAdd ? 'Enter an http or https address, a magnet, or the address of a .torrent file.' : 'Enter an absolute http or https URL.'; addressProblem = true; return; }
    const web = /^(https?|ftp):\/\//i.test(text);
    if (!providerAdd) {
      if (!/^https?:\/\//i.test(text) && !web) { addressStatus = 'Enter an absolute http or https URL.'; addressProblem = true; return; }
      toInfo(text, null);
      return;
    }

    if (!web && !isMagnet(text)) { addressStatus = 'Enter an http or https address, or a magnet.'; addressProblem = true; return; }
    checking = true;
    canChooseDirect = false;
    direct = false;
    addressStatus = 'Checking this address…';
    try {
      // What it says goes on to the add dialog, which may offer its files.
      const answer = await store.ask<ClassifyResult>('remote.classify', { text, withFiles: true });
      const provider = answer.providerName?.trim() || providerName;
      url = text;
      result = answer;
      if (answer.ok === false) return cantTell(answer.error ?? "The server couldn't check this address.", text);
      switch (answer.kind) {
        case 'ordinaryLink':
        case 'providerDirectLink':
          toInfo(text, answer);
          break;
        case 'folderLink':
        case 'magnet':
        case 'torrentFile':
        case 'hosterLink':
          onprovider(text, answer);
          break;
        case 'undetermined':
          cantTell(hideAddresses(answer.reason) || `${provider} couldn't tell what this is.`, text);
          break;
        default:
          addressStatus = answer.reason?.trim() ? hideAddresses(answer.reason) : "This app can't use this.";
          addressProblem = true;
      }
    } catch (error) {
      cantTell(error instanceof Error ? error.message : String(error), text);
    } finally {
      checking = false;
    }
  }

  function cantTell(words: string, text: string) {
    const web = /^https?:\/\//i.test(text);
    addressStatus = web ? `${words} So it isn't treated as an ordinary link unless you say so.` : words;
    addressProblem = true;
    canChooseDirect = web;
  }

  // ---- Step 2 ----

  function toInfo(text: string, answer: ClassifyResult | null) {
    url = text;
    result = answer;
    nameEdited = false;
    name = '';
    useCategoryFolder(true);
    step = 'info';
    void previewMetadata();
  }

  // The category's folder, unless the folder was typed by hand; picking a category always applies its folder.
  function useCategoryFolder(force = false) {
    if (customDestination && !force) return;
    const fileName = name.trim() || nameFromUrl(url);
    autoDestination = destinationFor(settings, fileName, categoryId);
    destination = autoDestination;
    customDestination = false;
  }

  $effect(() => {
    if (step === 'info' && destination !== autoDestination) customDestination = true;
  });

  function authHeaders(): Record<string, string> | null {
    if (!useAuth) return {};
    if (!validUserName(user)) {
      authError = 'Enter a user name without colons or line breaks.';
      return null;
    }

    return { Authorization: basicAuthorization(user, password) };
  }

  async function previewMetadata() {
    const mine = ++generation;
    infoBusy = true;
    checkedOnce = true;
    infoTitle = undefined;
    info = 'Checking the server… You can start without waiting.';
    try {
      const headers = useAuth ? authHeaders() : {};
      if (headers === null) throw new Error('Enter a user name without colons or line breaks, then check again.');
      const found = await store.ask<FileInfo>('file.metadata', { url, headers });
      if (mine !== generation) return;
      if (found.ok === false) throw new Error(found.error ?? 'File information is unavailable.');
      knownSize = found.totalBytes ?? null;
      const server = serverNameToShow(found.fileName, url, result?.kind === 'providerDirectLink');
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
    generation++;
    infoBusy = false;
    authError = '';
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
    // A download already on the list asks first, as on the desktop.
    if (step === 'info') {
      const found = findExisting(Object.values(store.model.jobs), url);
      if (found) { existing = found; step = 'duplicate'; pendingLater = later; return; }
    }

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
      step = 'info';
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

  function keydown(event: KeyboardEvent) {
    if (!submitsOnEnter(event) || checking || saving) return;
    event.preventDefault();
    if (step === 'address') void next();
    else if (step === 'info') void start(false);
  }
</script>

<Dialog title={step === 'address' ? 'Enter new address to download' : step === 'duplicate' ? 'Download already exists' : 'Add download'} {onclose}>
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div onkeydown={keydown}>
  {#if step === 'address'}
    <label class="label" for="url-box">URL</label>
    <div class="flex gap-2">
      <input id="url-box" class="field font-mono" data-autofocus bind:value={address} list="recent-urls" spellcheck="false" autocomplete="off"
        placeholder={providerAdd || ontorrent ? 'https://example.com/file.zip, or a magnet' : 'https://example.com/file.zip'}
        oninput={() => { canChooseDirect = false; direct = false; addressStatus = ''; }} />
      <button class="btn shrink-0" type="button" title="Paste the address from the clipboard"
        onclick={async () => { try { const clip = (await navigator.clipboard.readText()).trim(); if (clip) address = clip.length < 4096 ? clip : address; } catch { store.toast("The browser wouldn't hand over the clipboard. Paste into the box instead.", 'error'); } }}><Icon name="link" size={15} /> Paste</button>
    </div>
    <datalist id="recent-urls">{#each recents as recent (recent)}<option value={recent}></option>{/each}</datalist>
    {#if addressStatus}<p class="mt-2 text-[12.5px] {addressProblem ? 'text-error' : 'text-muted'}" role={addressProblem ? 'alert' : 'status'}>{addressStatus}</p>{/if}
    {#if canChooseDirect}
      <label class="mt-2 flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={direct} /> Download directly, not through {providerName}</label>
    {/if}
  {:else if step === 'duplicate'}
    <p class="text-[13px]">{existing?.fileName} ({existing ? statusText(existing) : ''}) is already in the list for this URL.</p>
  {:else}
    {#if failure}<p class="mb-3 rounded border border-icon-danger/50 bg-highlight px-3 py-2 text-[13px] text-error" role="alert">{failure}</p>{/if}
    <div class="space-y-3">
      <div>
        <div class="label">{result?.kind === 'providerDirectLink' ? 'Site' : 'URL'}</div>
        <div class="field truncate bg-chrome font-mono text-[12px]" title={shownAddress}>{shownAddress}</div>
      </div>
      {#if result?.note}<p class="text-[12.5px] text-muted">{hideAddresses(result.note)}</p>{/if}
      <p class="whitespace-pre-line text-[12.5px]" role="status" aria-live="polite" title={infoTitle}>{info}</p>
      <button class="btn" type="button" disabled={infoBusy} onclick={previewMetadata}>{infoBusy ? 'Checking…' : checkButtonText(checkedOnce)}</button>

      <div>
        <label class="label" for="file-name">File name (optional)</label>
        <input id="file-name" class="field" data-autofocus bind:value={name} placeholder="Resolved from the URL or server" oninput={() => { nameEdited = true; useCategoryFolder(); }} />
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

      <details class="card px-3 py-2">
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
  </div>

  {#snippet footer()}
    {#if step === 'address'}
      <button class="btn-primary" disabled={checking} onclick={next}>{checking ? 'Checking…' : canChooseDirect && !direct ? 'Try again' : 'Next'}</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {:else if step === 'duplicate'}
      <button class="btn-primary" disabled={saving} onclick={() => send(pendingLater)}>Download again</button>
      <button class="btn" onclick={selectExisting}>Select existing</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {:else}
      <button class="btn-primary" disabled={saving || creating} onclick={() => start(false)}>{saving ? 'Adding…' : 'Start download'}</button>
      <button class="btn" disabled={saving || creating} onclick={() => start(true)}>Download later</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {/if}
  {/snippet}
</Dialog>
