<script lang="ts">
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import { dateTime } from '../lib/format';
  import { tickUntil } from '../lib/ticker';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSettings, Settings, SiteLogin } from '../lib/types';

  type Page = 'general' | 'folders' | 'logins' | 'proxy' | 'extraction' | 'programs' | 'signin';
  const pages: { key: Page; label: string }[] = [
    { key: 'general', label: 'Downloads' },
    { key: 'folders', label: 'Favorite folders' },
    { key: 'logins', label: 'Site logins' },
    { key: 'proxy', label: 'Proxy' },
    { key: 'extraction', label: 'Extraction' },
    { key: 'programs', label: 'Programs and updates' },
    { key: 'signin', label: 'Sign-in and browsers' },
  ];

  let page = $state<Page>('general');
  let draft = $state<Settings | null>(null);
  let speedKb = $state<number | null>(null);

  function reset() {
    const settings = store.model.settings;
    if (!settings) return;
    draft = JSON.parse(JSON.stringify(settings)) as Settings;
    speedKb = settings.globalSpeedLimitBytesPerSecond ? Math.round(settings.globalSpeedLimitBytesPerSecond / 1024) : null;
  }

  $effect(() => {
    if (!draft && store.model.settings) reset();
  });

  async function saveGeneral() {
    if (!draft) return;
    const d = $state.snapshot(draft) as Settings;
    const ok = await store.saveSettings((settings) => {
      settings.defaultDownloadDirectory = d.defaultDownloadDirectory;
      settings.useCategoryFolders = d.useCategoryFolders;
      settings.maxConcurrentDownloads = d.maxConcurrentDownloads;
      settings.defaultConnections = d.defaultConnections;
      settings.maxConnectionsPerHost = d.maxConnectionsPerHost;
      settings.globalSpeedLimitBytesPerSecond = speedKb && speedKb > 0 ? Math.round(speedKb * 1024) : undefined;
      settings.autoResumeUnfinishedOnStartup = d.autoResumeUnfinishedOnStartup;
      settings.allowLocalNetwork = d.allowLocalNetwork;
      settings.browserIntegrationEnabled = d.browserIntegrationEnabled;
    });
    if (ok) reset();
  }

  // Favorite folders
  let favoritePath = $state('');
  let favoriteName = $state('');
  async function addFavorite() {
    const path = favoritePath.trim();
    if (!path) return;
    const name = favoriteName.trim() || path.split(/[\\/]/).filter(Boolean).pop() || path;
    if (await store.saveSettings((s) => { s.favoriteFolders = [...s.favoriteFolders.filter((f) => f.path !== path), { name, path }]; }, 'Favorite added.')) {
      favoritePath = '';
      favoriteName = '';
    }
  }

  // Site logins: the password is write-only; an empty one keeps what is saved.
  let login = $state<SiteLogin | null>(null);
  async function saveLogin() {
    if (!login) return;
    const value = $state.snapshot(login) as SiteLogin;
    const ok = await store.saveSettings((s) => {
      const index = s.siteLogins.findIndex((l) => l.id === value.id);
      const entry: SiteLogin = { id: value.id, site: value.site.trim(), username: value.username.trim(), hasPassword: value.hasPassword, password: value.password || undefined };
      if (index >= 0) s.siteLogins[index] = entry;
      else s.siteLogins.push(entry);
    }, 'Login saved.');
    if (ok) login = null;
  }

  // Proxy
  let proxyPassword = $state('');
  let clearProxyPassword = $state(false);
  async function saveProxy() {
    if (!draft) return;
    const p = $state.snapshot(draft.proxy);
    const ok = await store.saveSettings((s) => {
      s.proxy = { mode: p.mode, address: p.address?.trim() || undefined, username: p.username?.trim() || undefined, hasPassword: p.hasPassword,
        password: proxyPassword || undefined, clearPassword: clearProxyPassword && !proxyPassword };
    }, 'Proxy saved.');
    if (ok) {
      proxyPassword = '';
      clearProxyPassword = false;
      reset();
    }
  }

  // Extraction (archive.settings.*)
  let archive = $state<ArchiveSettings | null>(null);
  let archiveFolder = $state('');
  let tool = $state<{ found: boolean; path?: string; message: string } | null>(null);
  async function loadArchive() {
    const result = await store.attempt<{ ok: boolean; settings: ArchiveSettings; tool: { found: boolean; path?: string; message: string } }>('archive.settings.get', {});
    if (result) {
      archive = result.settings;
      archiveFolder = result.settings.folder ?? '';
      tool = result.tool;
    }
  }
  async function saveArchive() {
    if (!archive) return;
    const value = { ...$state.snapshot(archive), folder: archiveFolder.trim() || undefined };
    const result = await store.attempt<{ ok: boolean; settings: ArchiveSettings }>('archive.settings.set', value, 'Extraction settings saved.');
    if (result?.settings) {
      archive = result.settings;
      archiveFolder = result.settings.folder ?? '';
    }
  }
  $effect(() => {
    if (page === 'extraction' && !archive) void loadArchive();
  });

  // Sign-in and paired apps
  let current = $state('');
  let next = $state('');
  let keepPaired = $state(false);
  let passwordFromEnvironment = $state(false);
  let pairing = $state<{ code: string; expiresAt: string } | null>(null);
  let now = $state(Date.now());
  let tokens = $state<{ id: string; name: string; createdAt: string; lastUsedAt?: string; role?: string }[]>([]);
  const roleNames: Record<string, string> = { extension: 'Browser extension', remoteClient: 'Desktop app' };
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
        ? `Password changed. Other browsers were signed out and ${unpaired} paired app${unpaired === 1 ? ' was' : 's were'} removed.`
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

<div class="mx-auto grid max-w-5xl grid-cols-1 gap-4 px-4 py-4 md:grid-cols-[200px_minmax(0,1fr)]">
  <nav aria-label="Settings pages">
    <select class="field md:hidden" bind:value={page} aria-label="Settings page">
      {#each pages as p (p.key)}<option value={p.key}>{p.label}</option>{/each}
    </select>
    <ul class="hidden space-y-0.5 md:block">
      {#each pages as p (p.key)}
        <li><button class="w-full rounded px-3 py-1.5 text-left text-[13px] {page === p.key ? 'bg-selected font-semibold' : 'hover:bg-hover'}" aria-current={page === p.key ? 'page' : undefined} onclick={() => (page = p.key)}>{p.label}</button></li>
      {/each}
    </ul>
  </nav>

  <section class="min-w-0">
    {#if !draft}
      <p class="text-muted">Loading…</p>
    {:else if page === 'general'}
      <h1 class="mb-3 text-[18px] font-semibold">Downloads</h1>
      <form class="grid gap-3 sm:grid-cols-2" onsubmit={(event) => { event.preventDefault(); void saveGeneral(); }}>
        <div class="sm:col-span-2"><FolderField id="set-folder" label="Download folder" bind:value={draft.defaultDownloadDirectory} hint="New downloads go here unless you choose another folder." /></div>
        <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={draft.useCategoryFolders} /> Put each kind of file in its category's folder</label>
        <div><label class="label" for="set-concurrent">Downloads at a time</label><input id="set-concurrent" class="field" type="number" min="1" max="16" bind:value={draft.maxConcurrentDownloads} /></div>
        <div><label class="label" for="set-connections">Connections per download</label><input id="set-connections" class="field" type="number" min="1" max="32" bind:value={draft.defaultConnections} /></div>
        <div><label class="label" for="set-host">Connections per site</label><input id="set-host" class="field" type="number" min="1" max="32" bind:value={draft.maxConnectionsPerHost} /></div>
        <div><label class="label" for="set-speed">Speed limit (KB/s, empty for none)</label><input id="set-speed" class="field" type="number" min="1" bind:value={speedKb} /></div>
        <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={draft.autoResumeUnfinishedOnStartup} /> Resume unfinished downloads when the server starts</label>
        <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={draft.allowLocalNetwork} /> Allow downloads from addresses on the local network</label>
        <label class="flex items-center gap-2 text-[13px] sm:col-span-2"><input type="checkbox" bind:checked={draft.browserIntegrationEnabled} /> Accept downloads sent by a paired browser</label>
        <div class="flex justify-end gap-2 sm:col-span-2"><button class="btn-primary" type="submit">Save</button><button class="btn" type="button" onclick={reset}>Undo changes</button></div>
      </form>
    {:else if page === 'folders'}
      <h1 class="mb-3 text-[18px] font-semibold">Favorite folders</h1>
      <p class="hint mb-3">They are offered first wherever a folder is chosen, with their free space.</p>
      <ul class="card mb-3 divide-y divide-divider">
        {#each store.model.settings?.favoriteFolders ?? [] as favorite (favorite.path)}
          <li class="flex items-center gap-2 px-3 py-2 text-[13px]">
            <Icon name="folder" size={16} class="text-icon-info" />
            <span class="min-w-0 flex-1"><span class="block truncate">{favorite.name}</span><span class="block truncate font-mono text-[11.5px] text-muted">{favorite.path}</span></span>
            <button class="btn-danger" onclick={() => store.saveSettings((s) => { s.favoriteFolders = s.favoriteFolders.filter((f) => f.path !== favorite.path); }, 'Favorite removed.')}>Remove</button>
          </li>
        {:else}
          <li class="px-3 py-6 text-center text-[13px] text-muted">No favorites yet.</li>
        {/each}
      </ul>
      <form class="grid gap-3 sm:grid-cols-[1fr_200px_auto] sm:items-end" onsubmit={(event) => { event.preventDefault(); void addFavorite(); }}>
        <FolderField id="fav-path" label="Folder" bind:value={favoritePath} />
        <div><label class="label" for="fav-name">Name</label><input id="fav-name" class="field" bind:value={favoriteName} placeholder="Optional" /></div>
        <button class="btn" type="submit" disabled={!favoritePath.trim()}>Add</button>
      </form>
    {:else if page === 'logins'}
      <h1 class="mb-3 text-[18px] font-semibold">Site logins</h1>
      <p class="hint mb-3">Used when a site asks for a user name and password. Passwords are sealed on the server and never shown again.</p>
      <ul class="card mb-3 divide-y divide-divider">
        {#each store.model.settings?.siteLogins ?? [] as entry (entry.id)}
          <li class="flex flex-wrap items-center gap-2 px-3 py-2 text-[13px]">
            <Icon name="lock" size={16} class="text-icon-neutral" />
            <span class="min-w-0 flex-1"><span class="block truncate font-semibold">{entry.site}</span><span class="block truncate text-muted">{entry.username}{entry.hasPassword ? ' · password saved' : ' · no password'}</span></span>
            <button class="btn" onclick={() => (login = { ...entry, password: '' })}>Edit</button>
            <button class="btn-danger" onclick={() => store.saveSettings((s) => { s.siteLogins = s.siteLogins.filter((l) => l.id !== entry.id); }, 'Login removed.')}>Remove</button>
          </li>
        {:else}
          <li class="px-3 py-6 text-center text-[13px] text-muted">No site logins.</li>
        {/each}
      </ul>
      {#if login}
        <form class="card grid gap-3 p-4 sm:grid-cols-3" onsubmit={(event) => { event.preventDefault(); void saveLogin(); }}>
          <div><label class="label" for="login-site">Site</label><input id="login-site" class="field" bind:value={login.site} placeholder="example.com" required /></div>
          <div><label class="label" for="login-user">User name</label><input id="login-user" class="field" bind:value={login.username} autocomplete="off" required /></div>
          <div><label class="label" for="login-pass">Password</label><input id="login-pass" class="field" type="password" bind:value={login.password} autocomplete="new-password" placeholder={login.hasPassword ? 'Leave empty to keep it' : ''} /></div>
          <div class="flex justify-end gap-2 sm:col-span-3"><button class="btn-primary" type="submit">Save login</button><button class="btn" type="button" onclick={() => (login = null)}>Cancel</button></div>
        </form>
      {:else}
        <button class="btn" onclick={() => (login = { id: '00000000-0000-0000-0000-000000000000', site: '', username: '', password: '', hasPassword: false })}><Icon name="add" size={15} class="text-icon-info" /> Add login</button>
      {/if}
    {:else if page === 'proxy'}
      <h1 class="mb-3 text-[18px] font-semibold">Proxy</h1>
      <form class="grid gap-3 sm:grid-cols-2" onsubmit={(event) => { event.preventDefault(); void saveProxy(); }}>
        <fieldset class="sm:col-span-2">
          <legend class="label">Connect</legend>
          <label class="mr-4 inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={draft.proxy.mode} value="none" /> Directly</label>
          <label class="mr-4 inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={draft.proxy.mode} value="system" /> Through the system's proxy</label>
          <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={draft.proxy.mode} value="manual" /> Through this proxy</label>
        </fieldset>
        <div class="sm:col-span-2 {draft.proxy.mode === 'manual' ? '' : 'opacity-50'}">
          <label class="label" for="proxy-address">Address</label>
          <input id="proxy-address" class="field font-mono" bind:value={draft.proxy.address} disabled={draft.proxy.mode !== 'manual'} placeholder="http://proxy:3128 or socks5://proxy:1080" />
          <p class="hint mt-1">http, socks4, socks4a or socks5.</p>
        </div>
        <div><label class="label" for="proxy-user">User name</label><input id="proxy-user" class="field" bind:value={draft.proxy.username} disabled={draft.proxy.mode !== 'manual'} autocomplete="off" /></div>
        <div>
          <label class="label" for="proxy-pass">Password</label>
          <input id="proxy-pass" class="field" type="password" bind:value={proxyPassword} disabled={draft.proxy.mode !== 'manual'} autocomplete="new-password" placeholder={draft.proxy.hasPassword ? 'Leave empty to keep it' : ''} />
          {#if draft.proxy.hasPassword}<label class="mt-1 flex items-center gap-2 text-[12px]"><input type="checkbox" bind:checked={clearProxyPassword} /> Remove the saved password</label>{/if}
        </div>
        <div class="flex justify-end gap-2 sm:col-span-2"><button class="btn-primary" type="submit">Save</button><button class="btn" type="button" onclick={reset}>Undo changes</button></div>
      </form>
    {:else if page === 'extraction'}
      <h1 class="mb-3 text-[18px] font-semibold">Extraction</h1>
      {#if tool}<p class="mb-3 text-[13px] {tool.found ? 'tone-done' : 'tone-failed'}">{tool.message}</p>{/if}
      {#if archive}
        <form class="grid gap-3" onsubmit={(event) => { event.preventDefault(); void saveArchive(); }}>
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={archive.autoExtract} /> Extract archives when their downloads finish</label>
          <fieldset>
            <legend class="label">Extract to</legend>
            <label class="mr-4 inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={archive.destination} value="nextToArchive" /> Next to the archive</label>
            <label class="mr-4 inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={archive.destination} value="subfolder" /> A folder named after it</label>
            <label class="inline-flex items-center gap-2 text-[13px]"><input type="radio" bind:group={archive.destination} value="folder" /> This folder</label>
          </fieldset>
          {#if archive.destination === 'folder'}
            <div class="pl-7"><FolderField id="archive-folder" label="Folder" bind:value={archiveFolder} /></div>
          {/if}
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={archive.preserveFolders} /> Keep the folders inside the archive</label>
          <div class="max-w-xs">
            <label class="label" for="archive-conflict">When a file is there already</label>
            <select id="archive-conflict" class="field" bind:value={archive.conflictPolicy}>
              <option value="rename">Keep both (rename the new one)</option>
              <option value="overwrite">Replace it</option>
              <option value="skip">Keep the old one</option>
            </select>
          </div>
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={archive.deleteArchives} /> Delete the archives after a checked extraction</label>
          <div class="flex justify-end"><button class="btn-primary" type="submit">Save</button></div>
        </form>
      {:else}
        <p class="text-muted">Loading…</p>
      {/if}
    {:else if page === 'programs'}
      <h1 class="mb-3 text-[18px] font-semibold">Programs and updates</h1>
      <div class="card mb-3 bg-highlight p-3 text-[13px]">
        <p class="flex items-start gap-2"><Icon name="lock" size={16} class="mt-0.5 shrink-0" />
          These settings make the server run a program or trust an installer, so they can't be changed from a browser. They are shown as they are. The server is updated by updating its image.</p>
      </div>
      <dl class="grid grid-cols-[minmax(150px,auto)_1fr] gap-x-4 gap-y-2 text-[13px]">
        <dt class="text-muted">After each download, run</dt><dd class="break-all font-mono text-[12px]">{draft.postDownloadCommand || '(nothing)'}</dd>
        <dt class="text-muted">FFmpeg</dt><dd class="break-all font-mono text-[12px]">{draft.ffmpegPath || '(found on PATH)'}</dd>
        <dt class="text-muted">Update source</dt><dd class="break-all font-mono text-[12px]">{draft.updateManifestUrl || '(none)'}</dd>
        <dt class="text-muted">Update signing key</dt><dd class="break-all font-mono text-[12px]">{draft.updatePublicKey ? 'set' : '(none)'}</dd>
        <dt class="text-muted">Shut down or sleep when done</dt><dd>{draft.allowSystemCompletionActions ? 'Allowed' : 'Not allowed'}</dd>
      </dl>
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
            <span>Keep the paired browser extensions<span class="hint block">Otherwise they are removed and pair again, in case someone who knew the old password paired one. Desktop apps are always removed and pair again with the new password.</span></span></label>
          <div class="sm:col-span-2"><button class="btn-primary" type="submit" disabled={!current || next.length < 8}>Change password</button>
            <span class="hint ml-2">Other browsers are signed out.</span></div>
        </form>
      {/if}

      <h2 class="mb-2 text-[15px] font-semibold">Paired apps</h2>
      <p class="hint mb-3">A browser extension can send downloads here and pause, resume or delete them. It can't see your settings or logins. A desktop app can do everything a signed-in browser can, settings and logins included. Remove any you don't know.</p>
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
              <span class="block text-[12px] text-muted">{token.role && roleNames[token.role] ? `${roleNames[token.role]} · ` : ''}Paired {dateTime(token.createdAt)}{token.lastUsedAt ? ` · last used ${dateTime(token.lastUsedAt)}` : ' · not used yet'}</span></span>
            <button class="btn-danger" onclick={() => revoke(token.id)}>Remove</button>
          </li>
        {:else}
          <li class="px-3 py-6 text-center text-[13px] text-muted">No app is paired.</li>
        {/each}
      </ul>

      {#if store.signInRequired}
        <button class="btn mt-6" onclick={() => store.signOut()}><Icon name="logout" size={15} /> Sign out</button>
      {/if}
    {/if}
  </section>
</div>
