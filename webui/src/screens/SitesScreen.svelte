<script lang="ts">
  import { onMount } from 'svelte';
  import AskDialog from '../components/AskDialog.svelte';
  import Icon from '../components/Icon.svelte';
  import { store } from '../lib/store.svelte';
  import {
    problems, rcloneLine, rcloneOffer,
    type Site, type SiteKind, type SiteResult, type SitesStatus,
  } from '../lib/sites';
  import { allRemotes } from '../lib/sitesHome';
  import type { TransferTab } from '../lib/sitesTransfers';
  import RemotesHome from './sites/RemotesHome.svelte';
  import SiteBrowser from './sites/SiteBrowser.svelte';
  import SiteDialog from './sites/SiteDialog.svelte';
  import SiteFix from './sites/SiteFix.svelte';
  import TransferList from './sites/TransferList.svelte';
  import TransfersBar from './sites/TransfersBar.svelte';

  // Remote files (SITES.7, SITES.11 R6b): the servers and cloud storage Inbound reaches through rclone, as on the desktop.
  // All remotes (#/sites), one remote's files (#/sites/<id>), and the Transfers bar under both, which opens the list of
  // transfers between remotes and the computer Inbound runs on.
  let { siteId = undefined }: { siteId?: string } = $props();

  let transfersOpen = $state(false);
  let transfersTab = $state<TransferTab>('active');

  let kinds = $state<SiteKind[]>([]);
  let editing = $state<Site | null | undefined>(undefined); // undefined: closed; null: a new site
  let removing = $state<Site | null>(null);
  let fixing = $state<{ site: Site; problem: string } | null>(null);
  let testing = $state<string | null>(null);
  let switching = $state(false);

  const status = $derived(store.model.sitesStatus);
  const sites = $derived(store.model.sites);
  const site = $derived(siteId ? sites.find((item) => item.id === siteId) : undefined);
  const transfers = $derived(store.model.siteTransfers);

  function open(item: Site | string) {
    location.hash = `#/sites/${encodeURIComponent(typeof item === 'string' ? item : item.id)}`;
  }

  /** An address opened once is forgotten: its unfinished batches are cancelled by the Host. */
  async function close(item: Site) {
    const result = await store.attempt<SiteResult>('sites.close', { id: item.id });
    if (result && !result.ok) store.toast(result.message ?? 'It was not closed.', 'error');
    if (siteId === item.id) location.hash = '#/sites';
  }

  /** "Remember": an address opened once becomes a saved remote. */
  async function keep(item: Site) {
    const result = await store.attempt<SiteResult>('sites.keep', { id: item.id });
    if (result && !result.ok) store.toast(result.message ?? 'It was not remembered.', 'error');
    else if (result) store.toast(`${result.site?.name ?? item.name} was saved.`);
  }

  let kindsProblem = $state('');

  /** The kinds the form offers; asked again when Add or Edit is used after a failed first try. */
  async function loadKinds(): Promise<boolean> {
    if (kinds.length) return true;
    try {
      kinds = (await store.ask<{ kinds: SiteKind[] }>('sites.kinds', {})).kinds ?? [];
      kindsProblem = '';
    } catch (error) {
      kindsProblem = `The kinds of remote could not be read: ${error instanceof Error ? error.message : String(error)}`;
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
    if (result && !result.ok) store.toast(result.message ?? 'The remote was not removed.', 'error');
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
</script>

<div class="flex h-full min-h-0 flex-col">
  <div class="min-h-0 flex-1 overflow-y-auto {transfersOpen && status?.enabled ? 'max-md:hidden' : ''}">
    <div class="mx-auto px-4 py-4 {siteId ? 'max-w-6xl' : 'max-w-5xl'}">
      {#if siteId}
        <div class="mb-3 flex items-center gap-2">
          <a class="btn-icon" href="#/sites" title={allRemotes} aria-label={allRemotes}><Icon name="chevron-left" size={18} /></a>
          <h1 class="min-w-0 flex-1 truncate text-[20px] font-semibold">{site?.name ?? 'Remote'}</h1>
        </div>
        {#if !status?.enabled}
          <p class="text-[13px] text-muted">Remote files are off. <a class="link" href="#/sites">Turn them on</a> first.</p>
        {:else if site}
          {#key site.id}<SiteBrowser {site} onedit={site.unsaved ? undefined : () => void edit(site)} />{/key}
        {:else}
          <p class="text-[13px] text-muted">That remote no longer exists. <a class="link" href="#/sites">{allRemotes}</a></p>
        {/if}
      {:else}
        <h1 class="text-[20px] font-semibold">{allRemotes}</h1>
        <p class="mb-3 mt-1 text-[12.5px] text-muted">Servers and cloud storage you reach through {store.texts.hostName}: SFTP, FTP, WebDAV, S3, Drive, OneDrive, Dropbox and B2.</p>

        {#if !status}
          <p class="mt-4 text-[13px] text-muted">Reading…</p>
        {:else}
          {#if status.enabled}
            {#if kindsProblem}<p class="mb-2 text-[12.5px] text-error" role="alert">{kindsProblem}</p>{/if}
            <RemotesHome {sites} {transfers} {testing} onopen={open} onadd={() => edit(null)} onedit={(item) => edit(item)} ontest={test}
              onremove={(item) => (item.unsaved ? void close(item) : (removing = item))} onsignin={(item) => (fixing = { site: item, problem: problems.signInNeeded })}
              onforget={(item) => (forgetting = item)} onkeep={keep} />
          {/if}

          <details class="card mt-5 px-3 py-2 text-[13px]" open={!status.enabled}>
            <summary class="cursor-pointer select-none font-semibold">Remote files options</summary>
            <div class="mt-2 space-y-2">
              <label class="flex items-center gap-2">
                <input type="checkbox" checked={status.enabled} disabled={switching} onchange={(event) => setEnabled(event.currentTarget.checked, event.currentTarget)} />
                Use remote files
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
                <p class="hint">Off, nothing runs for remote files. On, Inbound starts rclone when a remote is used.</p>
              {/if}
            </div>
          </details>
        {/if}
      {/if}
    </div>
  </div>

  {#if status?.enabled}
    {#if transfersOpen}
      <section id="remote-transfers" class="flex h-[42%] min-h-[210px] flex-col border-t border-divider bg-surface max-md:h-auto max-md:min-h-0 max-md:flex-1" aria-label="Transfers with {store.texts.hostName}">
        <h2 class="border-b border-divider px-3 py-2 text-[15px] font-semibold md:hidden">Transfers</h2>
        <TransferList {transfers} bind:tab={transfersTab} onshow={open} />
      </section>
    {/if}
    <TransfersBar {transfers} bind:open={transfersOpen} />
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
  <AskDialog title="Remove {item.name}?" danger confirm="Remove" sheet
    text="Inbound forgets this remote and what it keeps for it (the password or sign-in). Nothing on the server is deleted."
    onconfirm={() => remove(item)} onclose={() => (removing = null)} />
{/if}

{#if forgetting}
  {@const item = forgetting}
  <AskDialog title={item.trustedCertificates.length === 1 ? 'Forget the certificate?' : 'Forget the certificates?'} confirm="Forget" sheet
    text="Stop trusting {item.trustedCertificates.length === 1 ? 'the certificate' : `the ${item.trustedCertificates.length} certificates`} saved for {item.host || item.url || item.name}? The remote then can't be used until you trust its certificate again (Test asks)."
    onconfirm={() => forget(item)} onclose={() => (forgetting = null)} />
{/if}

{#if fixing}
  {@const current = fixing}
  <SiteFix site={current.site} problem={current.problem} onclose={() => (fixing = null)}
    onfixed={() => { fixing = null; void test(current.site); }} />
{/if}
