<script lang="ts">
  import { onMount } from 'svelte';
  import AskDialog from '../components/AskDialog.svelte';
  import Icon from '../components/Icon.svelte';
  import { bytes, speed } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import {
    problems, rcloneLine, rcloneOffer, siteSummary, transferFraction, transferLine,
    type Site, type SiteKind, type SiteResult, type SitesStatus,
  } from '../lib/sites';
  import SiteBrowser from './sites/SiteBrowser.svelte';
  import SiteDialog from './sites/SiteDialog.svelte';
  import SiteFix from './sites/SiteFix.svelte';

  // Sites (SITES.7): the servers and cloud storage Inbound reaches through rclone, as on the desktop. A list of sites,
  // one site's files (#/sites/<id>), and the transfers between sites and the computer Inbound runs on.
  let { siteId = undefined }: { siteId?: string } = $props();

  let kinds = $state<SiteKind[]>([]);
  let editing = $state<Site | null | undefined>(undefined); // undefined: closed; null: a new site
  let removing = $state<Site | null>(null);
  let fixing = $state<{ site: Site; problem: string } | null>(null);
  let testing = $state<string | null>(null);
  let switching = $state(false);

  const status = $derived(store.model.sitesStatus);
  const sites = $derived(store.model.sites);
  const site = $derived(siteId ? sites.find((item) => item.id === siteId) : undefined);
  const transfers = $derived([...store.model.siteTransfers].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)));
  const finished = $derived(transfers.some((transfer) => ['done', 'failed', 'canceled'].includes(transfer.state)));

  let kindsProblem = $state('');

  /** The kinds the form offers; asked again when Add or Edit is used after a failed first try. */
  async function loadKinds(): Promise<boolean> {
    if (kinds.length) return true;
    try {
      kinds = (await store.ask<{ kinds: SiteKind[] }>('sites.kinds', {})).kinds ?? [];
      kindsProblem = '';
    } catch (error) {
      kindsProblem = `The kinds of site could not be read: ${error instanceof Error ? error.message : String(error)}`;
    }
    return kinds.length > 0;
  }

  async function edit(item: Site | null) {
    if (await loadKinds()) editing = item;
  }

  onMount(() => void loadKinds());

  /**
   * A request answered with sites.status. Its `error` is either rclone's own trouble or this request's refusal, so it is
   * read here and said, as Options › Sites does on the desktop, rather than taken as a failed request; null when the
   * request itself did not get through.
   */
  async function statusRequest(type: string, payload: Record<string, unknown>): Promise<SitesStatus | null> {
    try {
      return (await store.raw<SitesStatus>(type, payload)).payload ?? null;
    } catch (error) {
      store.toast(error instanceof Error ? error.message : String(error), 'error');
      return null;
    }
  }

  async function setEnabled(enabled: boolean, box: HTMLInputElement) {
    switching = true;
    const result = await statusRequest('sites.setEnabled', { enabled });
    switching = false;
    // The box follows what the server kept, not the click, when the change did not go through.
    box.checked = (result ?? status)?.enabled ?? !enabled;
  }

  async function setAutoUpdate(on: boolean, box: HTMLInputElement) {
    const result = await statusRequest('sites.setEnabled', { enabled: true, autoUpdateRclone: on });
    box.checked = (result ?? status)?.autoUpdateRclone ?? !on;
  }

  async function test(item: Site) {
    testing = item.id;
    try {
      const result = await store.ask<SiteResult>('sites.test', { id: item.id });
      if (result.ok) store.toast(`${item.name} answered.`);
      else if (result.problem && result.problem !== problems.off) fixing = { site: item, problem: result.problem };
      else store.toast(result.message ?? `${item.name} did not answer.`, 'error');
    } catch (error) {
      store.toast(error instanceof Error ? error.message : String(error), 'error');
    } finally {
      testing = null;
    }
  }

  async function remove(item: Site) {
    const result = await store.attempt<SiteResult>('sites.remove', { id: item.id });
    if (result && !result.ok) store.toast(result.message ?? 'The site was not removed.', 'error');
    removing = null;
  }

  // SITES.6b: a way back from trusting a server's own certificate.
  let forgetting = $state<Site | null>(null);
  async function forget(item: Site) {
    const result = await store.attempt<SiteResult>('sites.trust', { id: item.id, forgetCertificates: true });
    if (result && !result.ok) store.toast(result.message ?? "The certificate couldn't be forgotten.", 'error');
    else if (result) store.toast(result.message?.trim() || `${item.name} no longer trusts its server's certificate.`);
    forgetting = null;
  }

  // The rclone update the Host found, as Options › Sites offers it on the desktop.
  const offer = $derived(status ? rcloneOffer(status) : null);
  let checking = $state(false);
  async function checkRclone() {
    checking = true;
    const result = await statusRequest('sites.rclone.check', {});
    checking = false;
    // A failed check is said under the button (rcloneUpdateError); only "nothing newer" needs words here.
    if (result && !result.rcloneUpdateError && !result.rcloneUpdate) store.toast('rclone is up to date.');
  }

  async function installRclone(version?: string) {
    const result = await statusRequest('sites.rclone.install', version ? { version } : {});
    if (!result) return;
    if (result.error?.trim()) store.toast(result.error.trim(), 'error');
    else store.toast(result.rcloneVersion ? `rclone ${result.rcloneVersion} is installed.` : 'rclone is installed.');
  }

  async function transfer(type: string, id: string) {
    const result = await store.attempt<{ ok: boolean; message?: string }>(type, { id });
    if (result && !result.ok) store.toast(result.message ?? 'That did not work.', 'error');
  }
</script>

<div class="mx-auto px-4 py-5 {siteId ? 'max-w-6xl' : 'max-w-4xl'}">
  {#if siteId}
    <div class="mb-3 flex items-center gap-2">
      <a class="btn-icon" href="#/sites" title="All remotes" aria-label="All remotes"><Icon name="chevron-left" size={18} /></a>
      <h1 class="min-w-0 flex-1 truncate text-[22px] font-semibold">{site?.name ?? 'Remote'}</h1>
    </div>
    {#if !status?.enabled}
      <p class="text-[13px] text-muted">Remote files are off. <a class="link" href="#/sites">Turn them on</a> first.</p>
    {:else if site}
      {#key site.id}<SiteBrowser {site} onedit={() => void edit(site)} />{/key}
    {:else}
      <p class="text-[13px] text-muted">That remote no longer exists. <a class="link" href="#/sites">All remotes</a></p>
    {/if}
  {:else}
    <h1 class="text-[22px] font-semibold">Sites</h1>
    <p class="mt-1 text-[12.5px] text-muted">Servers and cloud storage you reach through {store.texts.hostName}: SFTP, FTP, WebDAV, S3, Drive, OneDrive, Dropbox and B2.</p>

    {#if !status}
      <p class="mt-4 text-[13px] text-muted">Reading…</p>
    {:else}
      <section class="card mt-4 space-y-2 px-3 py-3 text-[13px]">
        <label class="flex items-center gap-2">
          <input type="checkbox" checked={status.enabled} disabled={switching} onchange={(event) => setEnabled(event.currentTarget.checked, event.currentTarget)} />
          Use Sites
        </label>
        {#if status.enabled}
          <p class="hint">{rcloneLine(status)}</p>
          {#if status.rclone === 'missing' && !status.rcloneInstalling}
            <button class="btn" onclick={() => installRclone()}><Icon name="download" size={15} /> Download rclone</button>
          {/if}
          {#if offer?.line}
            <div class="flex flex-wrap items-center gap-2">
              <p class="hint flex-1">{offer.line}</p>
              {#if offer.version}
                {@const version = offer.version}
                <button class="btn" disabled={status.rcloneInstalling} onclick={() => installRclone(version)}><Icon name="download" size={15} /> Install rclone {version}</button>
              {/if}
            </div>
          {/if}
          {#if status.rcloneSource !== 'own' && !status.ownRclonePath}
            <!-- Inbound never updates an rclone the user chose. -->
            <div class="flex flex-wrap items-center gap-2">
              <label class="flex flex-1 items-center gap-2">
                <input type="checkbox" checked={status.autoUpdateRclone} disabled={status.rcloneInstalling}
                  onchange={(event) => setAutoUpdate(event.currentTarget.checked, event.currentTarget)} />
                Update rclone automatically
              </label>
              {#if status.rclone !== 'missing'}
                <button class="btn" disabled={checking || status.rcloneInstalling} onclick={checkRclone}><Icon name="refresh" size={15} /> {checking ? 'Checking…' : 'Check now'}</button>
              {/if}
            </div>
          {/if}
          {#if offer?.lastCheck}<p class="hint">{offer.lastCheck}</p>{/if}
        {:else}
          <p class="hint">Off, nothing runs for Sites. On, Inbound starts rclone when a site is used.</p>
        {/if}
      </section>

      {#if status.enabled}
        <section class="mt-5">
          <div class="mb-2 flex items-center gap-2">
            <h2 class="flex-1 text-[14px] font-semibold">Your sites</h2>
            <button class="btn-primary" onclick={() => edit(null)}><Icon name="add" size={15} /> Add a site</button>
          </div>
          {#if kindsProblem}<p class="mb-2 text-[12.5px] text-error" role="alert">{kindsProblem}</p>{/if}
          {#if !sites.length}
            <p class="text-[13px] text-muted">No sites yet.</p>
          {:else}
            <ul class="card divide-y divide-divider">
              {#each sites as item (item.id)}
                <li class="flex flex-wrap items-center gap-2 px-3 py-2 text-[13px]">
                  <a class="flex min-w-0 flex-1 items-center gap-2 hover:underline" href="#/sites/{item.id}">
                    <Icon name={item.capabilities.includes('signIn') || item.kind === 's3' || item.kind === 'b2' ? 'cloud' : 'server'} size={18} class="shrink-0 text-muted" />
                    <span class="min-w-0">
                      <span class="block truncate font-medium">{item.name}</span>
                      <span class="block truncate text-[12px] text-muted">{siteSummary(item, kinds)}{item.encrypted ? '' : ' · Not encrypted'}</span>
                    </span>
                  </a>
                  {#if item.capabilities.includes('signIn') && !item.signedIn}
                    <button class="btn" onclick={() => (fixing = { site: item, problem: problems.signInNeeded })}>Sign in…</button>
                  {/if}
                  {#if item.trustedCertificates.length}
                    <button class="btn" onclick={() => (forgetting = item)} aria-label="Forget the certificate trusted for {item.name}">Forget certificate</button>
                  {/if}
                  <button class="btn" disabled={testing === item.id} onclick={() => test(item)}>{testing === item.id ? 'Testing…' : 'Test'}</button>
                  <button class="btn-icon" title="Edit" aria-label="Edit {item.name}" onclick={() => edit(item)}><Icon name="settings" size={16} /></button>
                  <button class="btn-icon" title="Remove" aria-label="Remove {item.name}" onclick={() => (removing = item)}><Icon name="trash" size={16} /></button>
                </li>
              {/each}
            </ul>
          {/if}
        </section>
      {/if}
    {/if}
  {/if}

  {#if transfers.length}
    <section class="mt-6">
      <div class="mb-2 flex items-center gap-2">
        <h2 class="flex-1 text-[14px] font-semibold">Transfers with {store.texts.hostName}</h2>
        {#if finished}<button class="btn" onclick={() => transfer('sites.transfer.clear', '')}><Icon name="sweep" size={15} /> Clear finished</button>{/if}
      </div>
      <ul class="card divide-y divide-divider text-[13px]">
        {#each transfers as item (item.id)}
          {@const fraction = transferFraction(item)}
          <li class="space-y-1 px-3 py-2">
            <div class="flex flex-wrap items-center gap-2">
              <Icon name={item.direction === 'upload' ? 'upload' : 'download'} size={16} class="shrink-0 text-muted" />
              <span class="min-w-0 flex-1 truncate" title={item.direction === 'upload' ? `${item.localFolder} → ${item.remoteFolder}` : `${item.remoteFolder} → ${item.localFolder}`}>
                {item.siteName}: {item.direction === 'upload' ? item.remoteFolder : item.localFolder}
              </span>
              {#if item.state === 'running'}
                <span class="tabular text-[12px] text-muted">{speed(item.speed)}</span>
                <button class="btn-icon" title="Pause" aria-label="Pause" onclick={() => transfer('sites.transfer.pause', item.id)}><Icon name="pause" size={15} /></button>
              {:else if item.state === 'paused'}
                <button class="btn-icon" title="Resume" aria-label="Resume" onclick={() => transfer('sites.transfer.resume', item.id)}><Icon name="play" size={15} /></button>
              {:else if item.state === 'failed'}
                <button class="btn-icon" title="Retry" aria-label="Retry" onclick={() => transfer('sites.transfer.retry', item.id)}><Icon name="retry" size={15} /></button>
              {/if}
              {#if item.state === 'running' || item.state === 'paused'}
                <button class="btn-icon" title="Cancel" aria-label="Cancel" onclick={() => transfer('sites.transfer.cancel', item.id)}><Icon name="stop" size={15} /></button>
              {/if}
            </div>
            {#if item.state === 'running' || item.state === 'paused'}
              <span class="progress {fraction === null ? 'indeterminate' : ''}"><span style={fraction === null ? '' : `width: ${Math.round(fraction * 100)}%`}></span></span>
            {/if}
            <p class="text-[12px] {item.state === 'failed' ? 'tone-failed' : 'text-muted'}">
              {transferLine(item)}{item.bytes > 0 ? ` · ${bytes(item.bytesDone) || '0 B'} of ${bytes(item.bytes)}` : ''}{item.message ? ` · ${item.message}` : ''}
            </p>
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</div>

{#if editing !== undefined}
  <SiteDialog {kinds} existing={editing} onclose={() => (editing = undefined)}
    onsaved={(saved) => {
      const added = !editing;
      editing = undefined;
      store.toast(`${saved.name} was saved.`);
      if (added && saved.capabilities.includes('signIn')) fixing = { site: saved, problem: problems.signInNeeded };
    }} />
{/if}

{#if removing}
  {@const item = removing}
  <AskDialog title="Remove {item.name}?" danger confirm="Remove"
    text="Inbound forgets this site and what it keeps for it (the password or sign-in). Nothing on the server is deleted."
    onconfirm={() => remove(item)} onclose={() => (removing = null)} />
{/if}

{#if forgetting}
  {@const item = forgetting}
  <AskDialog title={item.trustedCertificates.length === 1 ? 'Forget the certificate?' : 'Forget the certificates?'} confirm="Forget"
    text="Stop trusting {item.trustedCertificates.length === 1 ? 'the certificate' : `the ${item.trustedCertificates.length} certificates`} saved for {item.host || item.url || item.name}? The site then can't be used until you trust its certificate again (Test asks)."
    onconfirm={() => forget(item)} onclose={() => (forgetting = null)} />
{/if}

{#if fixing}
  {@const current = fixing}
  <SiteFix site={current.site} problem={current.problem} onclose={() => (fixing = null)}
    onfixed={() => { fixing = null; void test(current.site); }} />
{/if}
