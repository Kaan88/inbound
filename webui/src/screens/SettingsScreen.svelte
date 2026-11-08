<script lang="ts">
  import { onMount } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import { dateTime } from '../lib/format';
  import { newUuid } from '../lib/ids';
  import { askNotifyPermission, notifyEnabled, notifySupported, storeNotify } from '../lib/notify';
  import {
    applyForm, archiveChanges, archiveConflictLabels, archiveDestinationLabels, archiveValues, categoryLabel, categoryNames, formFrom, isUnsupportedReply,
    nextRowKey, proxyTypes, validateForm, type ArchiveValues, type OptionsForm, type OptionsPage,
  } from '../lib/settingsRules';
  import { tickUntil } from '../lib/ticker';
  import { providerNameOf } from '../lib/remote';
  import { store, type Theme } from '../lib/store.svelte';
  import type { ArchiveSettings, Settings } from '../lib/types';
  import ProviderAccount from './ProviderAccount.svelte';

  // Options: one draft for every page, saved together by OK or Apply and dropped by Cancel, like the desktop's window.
  // Sign-in and paired browsers act at once (they are not settings of the download manager).
  // A provider's account page is `provider:<id>`, one for each account the server has. Its buttons act at once.
  type Page = OptionsPage | `provider:${string}`;
  type PageEntry = { key: Page; label: string };

  // My provider can ask for its account page.
  let page = $state<Page>((store.settingsPage as Page | null) ?? 'general');
  store.settingsPage = null;
  let form = $state<OptionsForm | null>(null);
  let baseline = '';
  let themeChoice = $state<Theme>(store.theme);
  let notifyChoice = $state(notifyEnabled());
  let notifyBase = notifyEnabled();
  let message = $state('');
  let saving = $state(false);
  let leaveTo = $state<string | null>(null);

  // Extraction: read from the server when the screen opens; its page shows only where the server extracts.
  let archiveState = $state<'loading' | 'ready' | 'failed' | 'unsupported'>('loading');
  let archiveError = $state('');
  let archiveSaved = $state<ArchiveSettings | null>(null);
  let archiveForm = $state<ArchiveValues | null>(null);
  let archiveBase = '';
  let tool = $state<{ found: boolean; path?: string; message: string } | null>(null);

  const pages = $derived<PageEntry[]>([
    { key: 'general', label: 'General' },
    { key: 'saveto', label: 'Save to' },
    { key: 'downloads', label: 'Downloads' },
    { key: 'connection', label: 'Connection' },
    { key: 'proxy', label: 'Proxy' },
    { key: 'logins', label: 'Site logins' },
    { key: 'updates', label: 'Updates' },
    ...(store.has('remote') ? store.model.accounts.map((account) => ({ key: `provider:${account.provider}` as Page, label: `${providerNameOf(account)} account` })) : []),
    ...(archiveState === 'ready' || archiveState === 'failed' ? [{ key: 'extraction' as OptionsPage, label: 'Extraction' }] : []),
    { key: 'signin', label: 'Sign-in and browsers' },
  ]);
  $effect(() => {
    // An account page waits for the accounts to arrive before it counts as gone.
    if (!pages.some((p) => p.key === page) && !(page.startsWith('provider:') && store.model.accounts.length === 0)) page = 'general';
  });

  const dirty = $derived(
    !!form && (JSON.stringify(form) !== baseline || themeChoice !== store.theme || notifyChoice !== notifyBase || (archiveForm !== null && JSON.stringify(archiveForm) !== archiveBase)),
  );

  function loadForm() {
    const settings = store.model.settings;
    if (!settings) return;
    form = formFrom(JSON.parse(JSON.stringify(settings)) as Settings);
    baseline = JSON.stringify(form);
  }
  $effect(() => {
    if (!form && store.model.settings) loadForm();
  });
  // The header's theme button changes the theme too; the box follows it.
  $effect(() => {
    themeChoice = store.theme;
  });

  function showArchive(saved: ArchiveSettings) {
    archiveSaved = saved;
    archiveForm = archiveValues(saved);
    archiveBase = JSON.stringify(archiveForm);
  }

  let archiveBusy = $state(false);
  async function loadArchive() {
    // A retry keeps the page it is on; only the first read hides the page.
    if (archiveState !== 'failed' && archiveState !== 'ready') archiveState = 'loading';
    archiveBusy = true;
    archiveError = '';
    try {
      const result = await store.ask<{ settings?: ArchiveSettings; tool?: { found: boolean; path?: string; message: string } }>('archive.settings.get', {});
      if (!result.settings) throw new Error('The server sent no extraction settings.');
      showArchive(result.settings);
      tool = result.tool ?? null;
      archiveState = 'ready';
    } catch (error) {
      const text = error instanceof Error ? error.message : String(error);
      archiveError = text;
      archiveState = isUnsupportedReply(text) ? 'unsupported' : 'failed';
    } finally {
      archiveBusy = false;
    }
  }
  onMount(() => void loadArchive());

  function text(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
  }

  async function save(): Promise<boolean> {
    if (!form || saving) return false;
    message = '';
    saving = true;
    try {
      const draft = $state.snapshot(form) as OptionsForm;
      const archive = archiveSaved && archiveForm ? { saved: archiveSaved, values: $state.snapshot(archiveForm) as ArchiveValues } : null;
      const problem = validateForm(draft, archive);
      if (problem) {
        message = problem.message;
        page = problem.page;
        return false;
      }
      const copy = JSON.parse(JSON.stringify(store.model.settings)) as Settings;
      applyForm(copy, draft);
      try {
        await store.ask('settings.update', { settings: copy });
      } catch (error) {
        message = text(error);
        return false;
      }
      let extractionProblem = '';
      if (archive) {
        const { update } = archiveChanges(archive.saved, archive.values);
        if (update) {
          try {
            const result = await store.ask<{ settings?: ArchiveSettings }>('archive.settings.set', update);
            if (result.settings) showArchive(result.settings);
          } catch (error) {
            extractionProblem = text(error);
          }
        }
      }
      store.setTheme(themeChoice);
      storeNotify(notifyChoice);
      notifyBase = notifyChoice;
      // What the server kept (a moved default folder moves the category folders under it), not what was typed.
      loadForm();
      if (extractionProblem) {
        message = `The other settings were saved. The extraction settings were not: ${extractionProblem}`;
        page = 'extraction';
        return false;
      }
      store.toast('Settings saved.');
      return true;
    } finally {
      saving = false;
    }
  }

  async function ok() {
    if (await save()) go('#/all');
  }

  function discard() {
    loadForm();
    themeChoice = store.theme;
    notifyChoice = notifyBase;
    if (archiveSaved) showArchive(archiveSaved);
    message = '';
  }

  function go(to: string) {
    leaveTo = null;
    discard();
    location.hash = to;
  }

  function cancel() {
    if (dirty) leaveTo = '#/all';
    else go('#/all');
  }

  // Leaving with changes typed asks first: a click on a link to another screen, Escape, Cancel, or closing the tab.
  function guardLinks(event: MouseEvent) {
    if (!dirty || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const link = (event.target as Element | null)?.closest?.('a[href^="#/"]');
    const to = link?.getAttribute('href');
    if (!to || to === '#/settings') return;
    event.preventDefault();
    event.stopPropagation();
    leaveTo = to;
  }
  $effect(() => {
    document.addEventListener('click', guardLinks, true);
    return () => document.removeEventListener('click', guardLinks, true);
  });
  // Browser Back or Forward while there are unsaved changes: put the page address back and ask first.
  $effect(() => {
    if (!dirty) return;
    const back = (event: HashChangeEvent) => {
      const to = new URL(event.newURL).hash;
      if (to === '#/settings' || leaveTo) return;
      event.stopImmediatePropagation(); // the shell would otherwise swap the screen before the question is answered
      history.pushState(null, '', event.oldURL);
      leaveTo = to || '#/all';
    };
    addEventListener('hashchange', back, true);
    return () => removeEventListener('hashchange', back, true);
  });
  $effect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    addEventListener('beforeunload', warn);
    return () => removeEventListener('beforeunload', warn);
  });

  function keydown(event: KeyboardEvent) {
    if (event.key !== 'Escape' || event.defaultPrevented || leaveTo) return;
    if (document.querySelector('[role="dialog"]') || (event.target as HTMLElement | null)?.tagName === 'SELECT') return;
    cancel();
  }

  async function toggleNotify(on: boolean, box: HTMLInputElement) {
    if (on && !(await askNotifyPermission())) {
      notifyChoice = false;
      box.checked = false; // the state stays false, so Svelte would not untick the box itself
      message = notifySupported()
        ? 'This browser did not allow notifications. Allow them for this site in the browser, then tick the box again.'
        : "This browser can't show notifications for this page.";
      return;
    }
    notifyChoice = on;
  }

  // Favorite folders
  function moveFavorite(at: number, by: number) {
    if (!form) return;
    const to = at + by;
    if (to < 0 || to >= form.favorites.length) return;
    const [row] = form.favorites.splice(at, 1);
    form.favorites.splice(to, 0, row);
  }

  // Site logins: a password is write-only; an empty one keeps what is saved.
  function addLogin() {
    form?.logins.push({ id: newUuid(), site: '', username: '', password: '', hasPassword: false, isNew: true, removed: false, originalSite: '', originalUser: '' });
  }
  function removeLogin(index: number) {
    if (!form) return;
    const row = form.logins[index];
    if (row.isNew) form.logins.splice(index, 1);
    else {
      row.removed = !row.removed;
      if (row.removed) row.password = '';
    }
  }

  const manualProxy = $derived(form?.proxy.mode === 'manual');

  // Sign-in and paired browsers
  let current = $state('');
  let next = $state('');
  let keepPaired = $state(false);
  let passwordFromEnvironment = $state(false);
  let pairing = $state<{ code: string; expiresAt: string } | null>(null);
  let now = $state(Date.now());
  let tokens = $state<{ id: string; name: string; createdAt: string; lastUsedAt?: string }[]>([]);
  // The clock runs only while a pairing code is showing.
  $effect(() => tickUntil(pairing ? Date.parse(pairing.expiresAt) : 0, 1000, (time) => (now = time)));

  async function json<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(path, { credentials: 'same-origin', ...init, headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) } });
    const body = await response.json().catch(() => ({}));
    if (!response.ok || (body as { ok?: boolean }).ok === false) throw new Error((body as { error?: string }).error ?? `The server answered ${response.status}.`);
    return body as T;
  }

  async function loadSignIn() {
    try {
      const status = await json<{ passwordFromEnvironment: boolean }>('/api/auth/status');
      passwordFromEnvironment = status.passwordFromEnvironment;
      tokens = (await json<{ tokens: typeof tokens }>('/api/auth/tokens')).tokens;
    } catch (error) {
      store.toast(error instanceof Error ? error.message : String(error), 'error');
    }
  }
  $effect(() => {
    if (page === 'signin') void loadSignIn();
  });

  async function changePassword() {
    try {
      const result = await json<{ unpaired?: number }>('/api/auth/password', {
        method: 'POST', body: JSON.stringify({ current, password: next, keepPairedBrowsers: keepPaired }),
      });
      current = '';
      next = '';
      keepPaired = false;
      const unpaired = result.unpaired ?? 0;
      store.toast(unpaired > 0
        ? `Password changed. Other browsers were signed out and ${unpaired} paired browser${unpaired === 1 ? ' was' : 's were'} removed.`
        : 'Password changed. Other browsers were signed out.');
      void loadSignIn();
    } catch (error) {
      store.toast(error instanceof Error ? error.message : String(error), 'error');
    }
  }

  async function pair() {
    try {
      pairing = await json<{ code: string; expiresAt: string }>('/api/auth/pairing/code', { method: 'POST', body: '{}' });
    } catch (error) {
      store.toast(error instanceof Error ? error.message : String(error), 'error');
    }
  }

  async function revoke(id: string) {
    try {
      await json(`/api/auth/tokens/${encodeURIComponent(id)}`, { method: 'DELETE' });
      tokens = tokens.filter((t) => t.id !== id);
      store.toast('That browser can no longer reach the server.');
    } catch (error) {
      store.toast(error instanceof Error ? error.message : String(error), 'error');
    }
  }

  const pairLeft = $derived(pairing ? Math.max(0, Math.round((Date.parse(pairing.expiresAt) - now) / 1000)) : 0);
  $effect(() => {
    if (pairing && pairLeft === 0) {
      pairing = null;
      void loadSignIn();
    }
  });
</script>

<svelte:window onkeydown={keydown} />

{#snippet section(title: string)}<h2 class="mt-2 text-[14px] font-semibold">{title}</h2>{/snippet}

<div class="flex min-h-full flex-col">
<div class="mx-auto grid w-full max-w-5xl flex-1 grid-cols-1 content-start gap-4 px-4 py-4 md:grid-cols-[200px_minmax(0,1fr)]">
  <nav aria-label="Options pages">
    <select class="field md:hidden" bind:value={page} aria-label="Options page">
      {#each pages as p (p.key)}<option value={p.key}>{p.label}</option>{/each}
    </select>
    <ul class="hidden space-y-0.5 md:block">
      {#each pages as p (p.key)}
        <li><button class="w-full rounded px-3 py-1.5 text-left text-[13px] {page === p.key ? 'bg-selected font-semibold' : 'hover:bg-hover'}" aria-current={page === p.key ? 'page' : undefined} onclick={() => (page = p.key)}>{p.label}</button></li>
      {/each}
    </ul>
  </nav>

  <section class="min-w-0">
    {#if !form}
      <p class="text-muted">Loading…</p>
    {:else if page === 'general'}
      <h1 class="mb-3 text-[18px] font-semibold">General</h1>
      <div class="grid max-w-xl gap-3">
        {@render section('Startup')}
        <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={form.autoResume} /> Resume unfinished downloads when the server starts</label>
        {@render section('Window')}
        <div class="max-w-xs">
          <label class="label" for="set-theme">Theme</label>
          <select id="set-theme" class="field" bind:value={themeChoice}>
            <option value="system">Same as system</option>
            <option value="light">Light</option>
            <option value="dark">Dark</option>
          </select>
          <p class="hint mt-1">This browser only.</p>
        </div>
        {@render section('Capturing downloads')}
        <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={form.browserIntegration} /> Capture downloads from your browser</label>
      </div>
    {:else if page === 'saveto'}
      <h1 class="mb-3 text-[18px] font-semibold">Save to</h1>
      <div class="grid max-w-xl gap-3">
        <FolderField id="set-folder" label="Default download folder" bind:value={form.defaultDownloadDirectory} hint="New downloads go here unless you choose another folder." />
        {@render section('Folders by category')}
        <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={form.useCategoryFolders} /> Save downloads to a folder for their category</label>
        <p class="hint">Downloads of these types go to their own folder. Leave a field empty to use the default folder. Turned off, every download goes to the default folder.</p>
        {#each categoryNames as name (name)}
          <FolderField id="cat-{name}" label={categoryLabel(name)} bind:value={form.categoryFolders[name]} placeholder="Use the default folder" disabled={!form.useCategoryFolders} />
        {/each}

        {#if form.customCategories.length > 0}
          {@render section('Custom categories')}
          {#each form.customCategories as category (category.id)}
            <details class="card {category.removed ? 'opacity-55' : ''}">
              <summary class="cursor-pointer px-3 py-2 text-[13px] font-semibold">{category.name || 'Unnamed category'}{category.removed ? ' (will be removed)' : ''}</summary>
              <div class="grid gap-3 border-t border-divider p-3">
                <div><label class="label" for="cc-name-{category.id}">Name</label><input id="cc-name-{category.id}" class="field" bind:value={category.name} /></div>
                <div><label class="label" for="cc-ext-{category.id}">File extensions (space separated)</label><input id="cc-ext-{category.id}" class="field font-mono" bind:value={category.extensions} spellcheck="false" /></div>
                <FolderField id="cc-folder-{category.id}" label="Destination folder (optional)" bind:value={category.folder} />
                <div class="flex justify-end"><button class="btn-danger" type="button" onclick={() => (category.removed = !category.removed)}>{category.removed ? 'Undo removal' : 'Remove category'}</button></div>
              </div>
            </details>
          {/each}
        {/if}

        {@render section('Favorite folders')}
        <p class="hint">Named folders offered first wherever a folder is chosen. A name left empty becomes the folder's own name.</p>
        <ul class="grid gap-3">
          {#each form.favorites as favorite, i (favorite.key)}
            <li class="card grid gap-2 p-3 sm:grid-cols-[160px_minmax(0,1fr)_auto] sm:items-end">
              <div><label class="label" for="fav-name-{favorite.key}">Name</label><input id="fav-name-{favorite.key}" class="field" bind:value={favorite.name} /></div>
              <FolderField id="fav-path-{favorite.key}" label="Folder" bind:value={favorite.path} />
              <div class="flex gap-1">
                <button class="btn-icon border border-divider" type="button" title="Move up" aria-label="Move up" disabled={i === 0} onclick={() => moveFavorite(i, -1)}><Icon name="chevron-up" size={16} /></button>
                <button class="btn-icon border border-divider" type="button" title="Move down" aria-label="Move down" disabled={i === form.favorites.length - 1} onclick={() => moveFavorite(i, 1)}><Icon name="chevron-down" size={16} /></button>
                <button class="btn-icon border border-divider" type="button" title="Remove" aria-label="Remove favorite" onclick={() => form?.favorites.splice(i, 1)}><Icon name="trash" size={16} /></button>
              </div>
            </li>
          {:else}
            <li class="hint">No favorites yet.</li>
          {/each}
        </ul>
        <div><button class="btn" type="button" onclick={() => form?.favorites.push({ key: nextRowKey(), name: '', path: '' })}><Icon name="add" size={15} class="text-icon-info" /> Add favorite</button></div>
      </div>
    {:else if page === 'downloads'}
      <h1 class="mb-3 text-[18px] font-semibold">Downloads</h1>
      <div class="grid max-w-xl gap-3">
        {@render section('When a download finishes')}
        <div>
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" checked={notifyChoice} disabled={!notifySupported()} onchange={(event) => void toggleNotify(event.currentTarget.checked, event.currentTarget)} /> Show a notification when a download finishes</label>
          <p class="hint mt-1">This browser only, while this page is open. The browser asks to allow notifications when you turn it on.</p>
        </div>
        <div>
          <label class="label" for="set-post">Run this program after each download</label>
          <input id="set-post" class="field font-mono" readonly value={store.model.settings?.postDownloadCommand || ''} placeholder="(nothing)" />
          <p class="hint mt-1 flex items-start gap-1.5"><Icon name="lock" size={13} class="mt-0.5 shrink-0" /> This makes the server run a program, so it can't be changed from a browser.</p>
        </div>
      </div>
    {:else if page === 'connection'}
      <h1 class="mb-3 text-[18px] font-semibold">Connection</h1>
      <div class="grid max-w-xl gap-3 sm:grid-cols-2">
        <div><label class="label" for="set-concurrent">Maximum simultaneous downloads</label><input id="set-concurrent" class="field" type="number" min="1" max="16" required bind:value={form.maxConcurrentDownloads} aria-invalid={form.maxConcurrentDownloads === null ? 'true' : undefined} /></div>
        <div><label class="label" for="set-connections">Connections per download</label><input id="set-connections" class="field" type="number" min="1" max="32" required bind:value={form.defaultConnections} aria-invalid={form.defaultConnections === null ? 'true' : undefined} /></div>
        <div><label class="label" for="set-host">Connections per site</label><input id="set-host" class="field" type="number" min="1" max="64" required bind:value={form.maxConnectionsPerHost} aria-invalid={form.maxConnectionsPerHost === null ? 'true' : undefined} /></div>
        <div><label class="label" for="set-speed">Total speed limit, all downloads (KB/s)</label><input id="set-speed" class="field" type="number" min="1" max="10000000" placeholder="Unlimited" bind:value={form.speedKb} /></div>
        <p class="hint sm:col-span-2">Leave the speed limit empty for unlimited.</p>
        <div class="sm:col-span-2">
          {@render section('Per-site limits')}
          <label class="label mt-2" for="set-profiles">One host pattern=maximum connections per line</label>
          <textarea id="set-profiles" class="field h-24 font-mono" bind:value={form.profiles} placeholder="*.example.com=2" spellcheck="false"></textarea>
        </div>
        <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={form.allowLocalNetwork} /> Allow downloads from addresses on the local network</label>
      </div>
    {:else if page === 'proxy'}
      <h1 class="mb-3 text-[18px] font-semibold">Proxy</h1>
      <div class="grid max-w-xl gap-3 sm:grid-cols-2">
        <fieldset class="sm:col-span-2">
          <legend class="label">Connection</legend>
          <div class="grid gap-1.5">
            <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={form.proxy.mode} value="none" /> No proxy</label>
            <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={form.proxy.mode} value="system" /> Use the system's proxy settings</label>
            <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={form.proxy.mode} value="manual" /> Use this proxy server:</label>
          </div>
        </fieldset>
        <div class="grid gap-3 sm:col-span-2 sm:grid-cols-[150px_minmax(0,1fr)] {manualProxy ? '' : 'opacity-50'}">
          <div>
            <label class="label" for="proxy-type">Type</label>
            <select id="proxy-type" class="field" bind:value={form.proxy.type} disabled={!manualProxy}>
              {#each proxyTypes as name, i (name)}<option value={i}>{name}</option>{/each}
            </select>
          </div>
          <div><label class="label" for="proxy-address">Address (host:port)</label><input id="proxy-address" class="field font-mono" bind:value={form.proxy.host} disabled={!manualProxy} placeholder="proxy.example.com:8080" spellcheck="false" /></div>
        </div>
        <div class="sm:col-span-2 {manualProxy ? '' : 'opacity-50'}"><label class="label" for="proxy-user">User name (optional; SOCKS4/4a send it as the user ID)</label><input id="proxy-user" class="field" bind:value={form.proxy.username} disabled={!manualProxy} autocomplete="off" /></div>
        <div class="sm:col-span-2 {manualProxy && !form.proxy.forget ? '' : 'opacity-50'}">
          <label class="label" for="proxy-pass">{form.proxy.hasPassword ? 'Password (saved - leave empty to keep it)' : 'Password (optional)'}</label>
          <input id="proxy-pass" class="field" type="password" bind:value={form.proxy.password} disabled={!manualProxy || form.proxy.forget} autocomplete="new-password" />
        </div>
        {#if form.proxy.hasPassword}
          <label class="flex items-center gap-2 text-[13px] sm:col-span-2 {manualProxy ? '' : 'opacity-50'}"><input type="checkbox" bind:checked={form.proxy.forget} disabled={!manualProxy} /> Forget the saved proxy password</label>
        {/if}
      </div>
    {:else if page === 'logins'}
      <h1 class="mb-3 text-[18px] font-semibold">Site logins</h1>
      <p class="hint mb-3 max-w-xl">Credentials are sealed on the server and used for matching HTTP or FTP downloads. Re-enter the password when changing a saved site's address or user name.</p>
      <div class="grid max-w-xl gap-3">
        {#each form.logins as row, i (row.id)}
          <div class="card grid gap-3 p-3 sm:grid-cols-2 {row.removed ? 'opacity-55' : ''}">
            <div class="sm:col-span-2"><label class="label" for="login-site-{row.id}">Site or URL prefix</label><input id="login-site-{row.id}" class="field" bind:value={row.site} placeholder="https://example.com/private/ or example.com" disabled={row.removed} spellcheck="false" /></div>
            <div><label class="label" for="login-user-{row.id}">User name</label><input id="login-user-{row.id}" class="field" bind:value={row.username} autocomplete="off" disabled={row.removed} /></div>
            <div><label class="label" for="login-pass-{row.id}">{row.hasPassword ? 'Password (saved; leave empty to keep)' : 'Password'}</label><input id="login-pass-{row.id}" class="field" type="password" bind:value={row.password} autocomplete="new-password" disabled={row.removed} /></div>
            <div class="flex justify-end sm:col-span-2"><button class="btn-danger" type="button" onclick={() => removeLogin(i)}>{row.removed ? 'Undo removal' : 'Remove'}</button></div>
          </div>
        {:else}
          <p class="hint">No site logins.</p>
        {/each}
        <div><button class="btn" type="button" onclick={addLogin}><Icon name="add" size={15} class="text-icon-info" /> Add site login</button></div>
      </div>
    {:else if page === 'updates'}
      <h1 class="mb-3 text-[18px] font-semibold">Updates</h1>
      <div class="card mb-3 max-w-xl bg-highlight p-3 text-[13px]">
        <p class="flex items-start gap-2"><Icon name="lock" size={16} class="mt-0.5 shrink-0" />
          These settings make the server run a program or trust an installer, so they can't be changed from a browser. They are shown as they are. The server is updated by updating its image.</p>
      </div>
      <dl class="grid max-w-xl grid-cols-[minmax(150px,auto)_1fr] gap-x-4 gap-y-2 text-[13px]">
        <dt class="text-muted">FFmpeg</dt><dd class="break-all font-mono text-[12px]">{store.model.settings?.ffmpegPath || '(found on PATH)'}</dd>
        <dt class="text-muted">Shut down or sleep when done</dt><dd>{store.model.settings?.allowSystemCompletionActions ? 'Allowed' : 'Not allowed'}</dd>
      </dl>
    {:else if page === 'extraction'}
      <h1 class="mb-3 text-[18px] font-semibold">Extraction</h1>
      {#if archiveState === 'failed'}
        <p class="tone-failed mb-2 text-[13px]" role="alert">Couldn't read the extraction settings. {archiveError}</p>
        <button class="btn" type="button" disabled={archiveBusy} onclick={() => void loadArchive()}>{archiveBusy ? 'Reading the extraction settings...' : 'Try again'}</button>
      {:else if archiveForm}
        <div class="grid max-w-xl gap-3">
          {@render section('When a download finishes')}
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={archiveForm.autoExtract} /> Extract archives automatically after they finish downloading</label>
          <p class="hint">Applies to archives that finish after this is turned on. A multi-part archive waits for all its parts. Any finished archive can also be extracted by hand.</p>
          {@render section('Extractor')}
          <p class="hint">Inbound extracts with 7-Zip, which comes with it: RAR, 7z, zip and the other formats 7-Zip reads, with passwords and multi-part sets. 7-Zip is free software under the GNU LGPL, from www.7-zip.org.</p>
          {#if tool}<p class="text-[13px] {tool.found ? 'tone-done' : 'tone-failed'}">{tool.message}</p>{/if}
          {@render section('Where files go')}
          <fieldset>
            <legend class="label">Extract to</legend>
            <div class="grid gap-1.5">
              {#each archiveDestinationLabels as label, i (label)}
                <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={archiveForm.destination} value={i} /> {label}</label>
              {/each}
            </div>
          </fieldset>
          <div class="pl-7"><FolderField id="archive-folder" label="Extraction folder" bind:value={archiveForm.folder} disabled={archiveForm.destination !== 2} /></div>
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={archiveForm.preserveFolders} /> Keep the folders stored in the archive</label>
          <p class="hint">Turned off, every file goes straight into the destination.</p>
          <fieldset>
            <legend class="label">If a file with the same name is already there</legend>
            <div class="grid gap-1.5">
              {#each archiveConflictLabels as label, i (label)}
                <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={archiveForm.conflict} value={i} /> {label}</label>
              {/each}
            </div>
          </fieldset>
          {@render section('Archive files')}
          <fieldset>
            <legend class="label">After a successful extraction</legend>
            <div class="grid gap-1.5">
              <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={archiveForm.deleteArchives} value={false} /> Keep the archive files</label>
              <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={archiveForm.deleteArchives} value={true} /> Delete the archive files</label>
            </div>
          </fieldset>
          <p class="hint">Archives are deleted only after the extraction has succeeded and its files have passed verification. If it fails or is cancelled, they are kept.</p>
        </div>
      {:else}
        <p class="text-muted">Reading the extraction settings…</p>
      {/if}
    {:else if page.startsWith('provider:')}
      {@const account = store.model.accounts.find((candidate) => `provider:${candidate.provider}` === page)}
      {#if account}<ProviderAccount {account} />{:else}<p class="text-[13px] text-muted">This provider is not on the server any more.</p>{/if}
    {:else if page === 'signin'}
      <h1 class="mb-3 text-[18px] font-semibold">Sign-in</h1>
      {#if !store.signInRequired}
        <p class="card mb-4 bg-highlight p-3 text-[13px]">Sign-in is off on this server (DOWNLOADMANAGER_AUTH=off): anyone who can reach it can use it.</p>
      {:else if passwordFromEnvironment}
        <p class="mb-4 text-[13px] text-muted">The password comes from DOWNLOADMANAGER_PASSWORD or DOWNLOADMANAGER_PASSWORD_FILE. Change it there and restart the server.</p>
      {:else}
        <form class="mb-6 grid max-w-xl gap-3 sm:grid-cols-2" onsubmit={(event) => { event.preventDefault(); void changePassword(); }}>
          <div><label class="label" for="pw-current">Current password</label><input id="pw-current" class="field" type="password" bind:value={current} autocomplete="current-password" /></div>
          <div><label class="label" for="pw-new">New password (8 characters or more)</label><input id="pw-new" class="field" type="password" bind:value={next} autocomplete="new-password" minlength="8" /></div>
          <label class="flex items-start gap-2 text-[13px] sm:col-span-2"><input type="checkbox" class="mt-0.5" bind:checked={keepPaired} />
            <span>Keep the paired browsers<span class="hint block">Otherwise they are removed and pair again, in case someone who knew the old password paired one.</span></span></label>
          <div class="sm:col-span-2"><button class="btn-primary" type="submit" disabled={!current || next.length < 8}>Change password</button>
            <span class="hint ml-2">Other browsers are signed out.</span></div>
        </form>
      {/if}

      <h2 class="mb-2 text-[15px] font-semibold">Paired browsers</h2>
      <p class="hint mb-3">A paired browser extension can send downloads here and pause, resume or delete them. It can't see your settings or logins.</p>
      {#if pairing}
        <div class="card mb-3 p-4 text-center">
          <p class="mb-1 text-[13px]">In the extension's settings, choose this server and enter:</p>
          <p class="my-2 font-mono text-[28px] font-semibold tracking-[0.25em]" aria-label="Pairing code">{pairing.code.slice(0, 4)}&thinsp;{pairing.code.slice(4)}</p>
          <p class="hint">Works once, for {Math.floor(pairLeft / 60)}:{String(pairLeft % 60).padStart(2, '0')} more.</p>
          <button class="btn mt-2" onclick={() => { pairing = null; void fetch('/api/auth/pairing/code', { method: 'DELETE', credentials: 'same-origin' }); void loadSignIn(); }}>Done</button>
        </div>
      {:else}
        <button class="btn mb-3" onclick={pair}><Icon name="plug" size={15} class="text-icon-info" /> Pair a browser</button>
      {/if}
      <ul class="card divide-y divide-divider">
        {#each tokens as token (token.id)}
          <li class="flex flex-wrap items-center gap-2 px-3 py-2 text-[13px]">
            <span class="min-w-0 flex-1"><span class="block truncate font-semibold">{token.name}</span>
              <span class="block text-[12px] text-muted">Paired {dateTime(token.createdAt)}{token.lastUsedAt ? ` · last used ${dateTime(token.lastUsedAt)}` : ' · not used yet'}</span></span>
            <button class="btn-danger" onclick={() => revoke(token.id)}>Remove</button>
          </li>
        {:else}
          <li class="px-3 py-6 text-center text-[13px] text-muted">No browser is paired.</li>
        {/each}
      </ul>

      {#if store.signInRequired}
        <button class="btn mt-6" onclick={() => store.signOut()}><Icon name="logout" size={15} /> Sign out</button>
      {/if}
    {/if}
  </section>
</div>

<footer class="sticky bottom-0 z-10 flex flex-wrap items-center gap-2 border-t border-divider bg-chrome px-4 py-2.5">
  <div class="min-w-0 flex-1 text-[13px]">
    {#if message}<p class="tone-failed" role="alert">{message}</p>{:else if dirty}<p class="text-muted" role="status">You have changes that are not saved.</p>{/if}
  </div>
  <button class="btn-primary min-w-20" type="button" disabled={saving} onclick={() => void ok()}>OK</button>
  <button class="btn min-w-20" type="button" onclick={cancel}>Cancel</button>
  <button class="btn min-w-20" type="button" disabled={saving} onclick={() => void save()}>Apply</button>
</footer>
</div>

{#if leaveTo}
  {@const to = leaveTo}
  <Dialog title="Unsaved changes" onclose={() => (leaveTo = null)}>
    <p class="text-[13px]">Leave Options without saving your changes?</p>
    {#snippet footer()}
      <button class="btn-primary" type="button" onclick={() => (leaveTo = null)}>Stay</button>
      <button class="btn" type="button" onclick={() => go(to)}>Leave without saving</button>
    {/snippet}
  </Dialog>
{/if}
