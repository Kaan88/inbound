<script lang="ts">
  import { onMount } from 'svelte';
  import AskDialog from '../components/AskDialog.svelte';
  import Icon from '../components/Icon.svelte';
  import { bytes, speed } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import {
    problems, rcloneLine, siteSummary, transferFraction, transferLine,
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

  async function setEnabled(enabled: boolean) {
    switching = true;
    await store.attempt<SitesStatus>('sites.setEnabled', { enabled });
    switching = false;
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

  async function transfer(type: string, id: string) {
    const result = await store.attempt<{ ok: boolean; message?: string }>(type, { id });
    if (result && !result.ok) store.toast(result.message ?? 'That did not work.', 'error');
  }
</script>

<div class="mx-auto max-w-4xl px-4 py-5">
  {#if siteId}
    <div class="mb-3 flex items-center gap-2">
      <a class="btn-icon" href="#/sites" title="All sites" aria-label="All sites"><Icon name="chevron-left" size={18} /></a>
      <h1 class="min-w-0 flex-1 truncate text-[22px] font-semibold">{site?.name ?? 'Site'}</h1>
    </div>
    {#if !status?.enabled}
      <p class="text-[13px] text-muted">Sites are off. <a class="link" href="#/sites">Turn them on</a> first.</p>
    {:else if site}
      {#key site.id}<SiteBrowser {site} />{/key}
    {:else}
      <p class="text-[13px] text-muted">That site no longer exists. <a class="link" href="#/sites">All sites</a></p>
    {/if}
  {:else}
    <h1 class="text-[22px] font-semibold">Sites</h1>
    <p class="mt-1 text-[12.5px] text-muted">Servers and cloud storage you reach through {store.texts.hostName}: SFTP, FTP, WebDAV, S3, Drive, OneDrive, Dropbox and B2.</p>

    {#if !status}
      <p class="mt-4 text-[13px] text-muted">Reading…</p>
    {:else}
      <section class="card mt-4 space-y-2 px-3 py-3 text-[13px]">
        <label class="flex items-center gap-2">
          <input type="checkbox" checked={status.enabled} disabled={switching} onchange={(event) => setEnabled((event.currentTarget as HTMLInputElement).checked)} />
          Use Sites
        </label>
        {#if status.enabled}
          <p class="hint">{rcloneLine(status)}</p>
          {#if status.rclone === 'missing' && !status.rcloneInstalling}
            <button class="btn" onclick={() => store.attempt('sites.rclone.install', {})}><Icon name="download" size={15} /> Download rclone</button>
          {/if}
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

{#if fixing}
  {@const current = fixing}
  <SiteFix site={current.site} problem={current.problem} onclose={() => (fixing = null)}
    onfixed={() => { fixing = null; void test(current.site); }} />
{/if}
