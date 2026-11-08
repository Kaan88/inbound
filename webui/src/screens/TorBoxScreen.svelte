<script lang="ts">
  import FolderField from '../components/FolderField.svelte';
  import Icon from '../components/Icon.svelte';
  import { bytes, dateTime, speed } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { Hoster, RemoteAccount, RemoteItem } from '../lib/types';

  // The provider account (key, plan), what is in it, and which file hosters it can fetch from.
  type Tab = 'account' | 'items' | 'hosters';
  let tab = $state<Tab>('account');

  const account = $derived<RemoteAccount | undefined>(store.model.accounts[0]);
  const kinds = $derived(account?.itemKinds?.length ? account.itemKinds : ['torrents', 'webdl', 'usenet']);
  const kindWords: Record<string, string> = { torrents: 'Torrents', webdl: 'Web downloads', usenet: 'Usenet' };

  let key = $state('');
  let busy = $state(false);

  async function accountCall(type: string, payload: Record<string, unknown>, done: string) {
    busy = true;
    const result = await store.attempt<{ ok: boolean; error?: string }>(type, { provider: account?.provider, ...payload }, done);
    busy = false;
    return result;
  }

  async function saveKey() {
    if (!key.trim()) return;
    const result = await accountCall('remote.account.setKey', { key: key.trim() }, 'Key saved on the server.');
    key = '';
    if (result) await accountCall('remote.account.validate', {}, 'The provider accepted the key.');
  }

  // Items
  let kind = $state('torrents');
  let items = $state<RemoteItem[] | null>(null);
  let hasMore = $state(false);
  let offset = $state(0);
  let itemsProblem = $state('');
  let destination = $state('');

  async function loadItems(fresh = false, from = 0) {
    busy = true;
    itemsProblem = '';
    try {
      const result = await store.ask<{ items: RemoteItem[]; hasMore: boolean; offset: number }>('remote.item.get', { provider: account?.provider, kind, offset: from, limit: 50, fresh });
      items = from === 0 ? result.items : [...(items ?? []), ...result.items];
      hasMore = result.hasMore;
      offset = from + result.items.length;
    } catch (error) {
      itemsProblem = error instanceof Error ? error.message : String(error);
      if (from === 0) items = [];
    } finally {
      busy = false;
    }
  }

  $effect(() => {
    if (tab === 'items' && account?.hasKey && items === null) void loadItems();
  });

  async function download(item: RemoteItem) {
    await store.attempt('remote.add', {
      provider: item.provider, itemKind: item.kind, itemId: item.itemId,
      destination: destination.trim() || undefined, queueId: store.model.settings?.defaultQueueId ?? 0, startMode: 'startNow',
    }, `Downloading ${item.name ?? 'it'}.`);
  }

  // Hosters
  let hosters = $state<Hoster[] | null>(null);
  let hosterProblem = $state('');
  let fetchedAt = $state<string | undefined>(undefined);
  let filter = $state('');
  async function loadHosters() {
    busy = true;
    try {
      const result = await store.ask<{ hosters: Hoster[]; problem?: string; fetchedAt?: string }>('remote.hosters', {});
      hosters = result.hosters;
      hosterProblem = result.problem ?? '';
      fetchedAt = result.fetchedAt;
    } catch (error) {
      hosterProblem = error instanceof Error ? error.message : String(error);
      hosters = [];
    } finally {
      busy = false;
    }
  }
  $effect(() => {
    if (tab === 'hosters' && hosters === null) void loadHosters();
  });
  const shownHosters = $derived((hosters ?? []).filter((h) => !filter.trim() || h.name.toLowerCase().includes(filter.trim().toLowerCase()) || h.domains.some((d) => d.includes(filter.trim().toLowerCase()))));

  function limit(used: number | undefined, max: number | undefined, unit: (n: number) => string, word = ''): string {
    if (!max) return '';
    return word ? `${used ?? '?'} of ${max} ${word}` : `${used != null ? unit(used) : '?'} of ${unit(max)}`;
  }

  /** The provider's stage names ("metadataPending") as words. */
  function words(stage: string): string {
    return stage.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  }
</script>

<div class="mx-auto max-w-5xl px-4 py-4">
  {#if !store.has('remote')}
    <p class="card p-4 text-[13px] text-muted">This server has no download provider.</p>
  {:else}
    <h1 class="mb-3 text-[18px] font-semibold">My {account?.providerName ?? 'provider'}</h1>
    <div class="mb-4 flex gap-1 border-b border-divider" role="tablist">
      {#each [['account', 'Account'], ['items', 'In my account'], ['hosters', 'Hoster status']] as [key, label] (key)}
        <button role="tab" aria-selected={tab === key} class="-mb-px border-b-2 px-3 py-2 text-[13px] {tab === key ? 'border-accent font-semibold' : 'border-transparent text-muted hover:text-text'}" onclick={() => (tab = key as Tab)}>{label}</button>
      {/each}
    </div>

    {#if tab === 'account'}
      {#if account}
        <dl class="mb-4 grid grid-cols-[minmax(130px,auto)_1fr] gap-x-4 gap-y-1.5 text-[13px]">
          <dt class="text-muted">Key</dt>
          <dd>{account.keyUnreadable ? 'Saved, but the server can no longer read it. Enter it again.' : account.hasKey ? 'Saved on the server' : 'None yet'}</dd>
          {#if account.hasKey}
            <dt class="text-muted">Plan</dt><dd>{account.plan ?? (account.checkedAt ? 'Unknown' : 'Not checked yet')}{account.premium ? ' (premium)' : ''}</dd>
            {#if account.premiumUntil}<dt class="text-muted">Premium until</dt><dd>{dateTime(account.premiumUntil)}</dd>{/if}
            {#if account.cooldownUntil && Date.parse(account.cooldownUntil) > Date.now()}<dt class="text-muted">Waiting until</dt><dd class="tone-waiting">{dateTime(account.cooldownUntil)}</dd>{/if}
            <dt class="text-muted">Checked</dt><dd>{account.checkedAt ? dateTime(account.checkedAt) : 'Not yet'}</dd>
          {/if}
        </dl>
      {/if}
      <form class="mb-3 flex max-w-xl flex-wrap items-end gap-2" onsubmit={(event) => { event.preventDefault(); void saveKey(); }}>
        <div class="min-w-56 flex-1">
          <label class="label" for="provider-key">{account?.hasKey ? 'Replace the key' : 'Key'}</label>
          <input id="provider-key" class="field font-mono" type="password" bind:value={key} autocomplete="off" spellcheck="false" />
        </div>
        <button class="btn-primary" type="submit" disabled={busy || !key.trim()}>Save key</button>
      </form>
      <p class="hint mb-4">The key is kept sealed on the server and is never sent back to a browser.</p>
      {#if account?.hasKey}
        <div class="flex flex-wrap gap-2">
          <button class="btn" disabled={busy} onclick={() => accountCall('remote.account.validate', {}, 'The provider accepted the key.')}><Icon name="check" size={15} class="text-icon-start" /> Check the key</button>
          <button class="btn" disabled={busy} onclick={() => accountCall('remote.account.get', { refresh: true }, 'Account refreshed.')}><Icon name="refresh" size={15} class="text-icon-info" /> Refresh</button>
          <button class="btn-danger" disabled={busy} onclick={() => accountCall('remote.account.clearKey', {}, 'Key removed from the server.')}>Remove the key</button>
        </div>
      {/if}
    {:else if tab === 'items'}
      {#if !account?.hasKey}
        <p class="text-[13px] text-muted">Save a key first.</p>
      {:else}
        <div class="mb-3 flex flex-wrap items-end gap-2">
          <div>
            <label class="label" for="item-kind">Show</label>
            <select id="item-kind" class="field" bind:value={kind} onchange={() => loadItems()}>
              {#each kinds as k (k)}<option value={k}>{kindWords[k] ?? k}</option>{/each}
            </select>
          </div>
          <button class="btn" disabled={busy} onclick={() => loadItems(true)}><Icon name="refresh" size={15} class="text-icon-info" /> Refresh</button>
          <div class="min-w-64 flex-1"><FolderField id="item-dest" label="Download to" bind:value={destination} hint="Empty uses the default download folder." /></div>
        </div>
        {#if itemsProblem}<p class="mb-2 text-[13px] text-error" role="alert">{itemsProblem}</p>{/if}
        <ul class="card divide-y divide-divider">
          {#each items ?? [] as item (item.itemId)}
            <li class="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2 text-[13px]">
              <Icon name="cloud" size={16} class="text-icon-info" />
              <span class="min-w-0 flex-1">
                <span class="block truncate font-semibold" title={item.name ?? ''}>{item.name ?? '(no name)'}</span>
                <span class="block text-[12px] text-muted">
                  {bytes(item.size)} · {item.fileCount} {item.fileCount === 1 ? 'file' : 'files'}
                  {#if item.stage} · {words(item.stage)}{item.progress > 0 && item.progress < 1 ? ` ${Math.round(item.progress * 100)}%` : ''}{/if}
                  {#if item.speedBytesPerSecond > 0} · {speed(item.speedBytesPerSecond)}{/if}
                  {#if item.expiresAt} · until {dateTime(item.expiresAt)}{/if}
                </span>
                {#if item.error}<span class="block text-[12px] text-error">{item.error}</span>{/if}
              </span>
              <button class="btn" disabled={!item.cached} title={item.cached ? '' : 'The provider has not finished it yet.'} onclick={() => download(item)}><Icon name="download" size={15} class="text-icon-info" /> Download</button>
            </li>
          {:else}
            <li class="px-3 py-6 text-center text-[13px] text-muted">{busy ? 'Loading…' : 'Nothing here.'}</li>
          {/each}
        </ul>
        {#if hasMore}<button class="btn mt-2" disabled={busy} onclick={() => loadItems(false, offset)}>Show more</button>{/if}
      {/if}
    {:else}
      <div class="mb-3 flex flex-wrap items-end gap-2">
        <div class="min-w-56 flex-1"><label class="label" for="hoster-filter">Find a site</label><input id="hoster-filter" class="field" bind:value={filter} placeholder="Name or domain" /></div>
        <button class="btn" disabled={busy} onclick={loadHosters}><Icon name="refresh" size={15} class="text-icon-info" /> Refresh</button>
      </div>
      {#if hosterProblem}<p class="mb-2 text-[13px] text-error" role="alert">{hosterProblem}</p>{/if}
      {#if fetchedAt}<p class="hint mb-2">As of {dateTime(fetchedAt)}.</p>{/if}
      <div class="card overflow-x-auto">
        <table class="w-full text-left text-[12.5px]">
          <thead class="border-b border-divider text-muted">
            <tr><th class="px-3 py-1.5 font-semibold">Site</th><th class="px-3 py-1.5 font-semibold">Status</th><th class="hidden px-3 py-1.5 font-semibold sm:table-cell">Daily use</th><th class="hidden px-3 py-1.5 font-semibold md:table-cell">Per file</th></tr>
          </thead>
          <tbody class="divide-y divide-divider">
            {#each shownHosters as hoster (hoster.name)}
              <tr>
                <td class="px-3 py-1.5"><span class="block font-semibold">{hoster.name}</span><span class="block truncate text-[11.5px] text-muted">{hoster.domains.slice(0, 3).join(', ')}</span></td>
                <td class="px-3 py-1.5 {hoster.available ? 'tone-done' : 'tone-failed'}">{hoster.available ? 'Working' : 'Not working'}{hoster.note ? ` · ${hoster.note}` : ''}</td>
                <td class="tabular hidden px-3 py-1.5 sm:table-cell">{[limit(hoster.dailyLinksUsed, hoster.dailyLinkLimit, String, 'links'), limit(hoster.dailyBandwidthUsed, hoster.dailyBandwidthLimit, bytes)].filter(Boolean).join('; ') || '—'}</td>
                <td class="tabular hidden px-3 py-1.5 md:table-cell">{hoster.perLinkSizeLimit ? bytes(hoster.perLinkSizeLimit) : '—'}</td>
              </tr>
            {:else}
              <tr><td colspan="4" class="px-3 py-6 text-center text-muted">{busy ? 'Loading…' : 'No sites.'}</td></tr>
            {/each}
          </tbody>
        </table>
      </div>
    {/if}
  {/if}
</div>
