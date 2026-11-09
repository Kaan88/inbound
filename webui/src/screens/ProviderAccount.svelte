<script lang="ts">
  import Confirm from '../components/Confirm.svelte';
  import Icon from '../components/Icon.svelte';
  import { keyDraft } from '../lib/keydraft.svelte';
  import { accountView, failureText, providerNameOf, validated } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import type { RemoteAccount } from '../lib/types';

  // "<Provider> account": the API key (kept sealed on the server, never sent back) and what the provider says about the account.
  let { account }: { account: RemoteAccount } = $props();

  const name = $derived(providerNameOf(account));
  const view = $derived(accountView(account));
  const key = $derived(keyDraft[account.provider] ?? '');
  let busy = $state(false);
  let result = $state('');
  let resultIsProblem = $state(false);
  let lastFailure = '';
  let confirmClear = $state(false);

  function show(text: string, problem = false) {
    result = text;
    resultIsProblem = problem;
  }

  async function call<R extends { ok?: boolean; error?: string; failureKind?: string; accounts?: RemoteAccount[] }>(type: string, payload: Record<string, unknown>, working: string): Promise<R | null> {
    busy = true;
    show(working);
    try {
      const reply = await store.ask<R>(type, { provider: account.provider, ...payload });
      return reply;
    } catch (error) {
      lastFailure = failureText(error instanceof Error ? error.message : String(error), null, name);
      show(lastFailure, true);
      return null;
    } finally {
      busy = false;
    }
  }

  async function save() {
    const typed = key.trim();
    if (!typed) return;
    const saved = await call('remote.account.setKey', { key: typed }, 'Saving the key…');
    if (!saved) return; // the typed key stays in the box so it can be tried again
    keyDraft[account.provider] = '';
    store.keyEpoch++;
    const checked = await call<{ accounts?: RemoteAccount[] }>('remote.account.validate', {}, `Asking ${name}…`);
    if (checked) show(`Key saved. ${validated(checked.accounts?.[0] ?? account)}`);
    else show(`Key saved, but ${name} didn't accept it yet. ${lastFailure}`, true);
  }

  async function validate() {
    const checked = await call<{ accounts?: RemoteAccount[] }>('remote.account.validate', {}, `Asking ${name}…`);
    if (checked) show(validated(checked.accounts?.[0] ?? account));
  }

  async function refresh() {
    if (await call('remote.account.get', { refresh: true }, `Asking ${name}…`)) show('Account refreshed.');
  }

  async function clear() {
    confirmClear = false;
    if (await call('remote.account.clearKey', {}, 'Clearing the key…')) {
      store.keyEpoch++;
      show('Key cleared.');
    }
  }
</script>

<h1 class="mb-3 text-[18px] font-semibold">{view.pageTitle}</h1>

<h2 class="mb-1 text-[15px] font-semibold">API key</h2>
<p class="mb-3 text-[13px] {view.keyStatusIsProblem ? 'text-error' : 'text-muted'}">{view.keyStatus}</p>
<form class="mb-2 flex max-w-xl flex-wrap items-end gap-2" onsubmit={(event) => { event.preventDefault(); void save(); }}>
  <div class="min-w-56 flex-1">
    <label class="label" for="provider-key-{account.provider}">{view.keyBoxHeader}</label>
    <input id="provider-key-{account.provider}" class="field font-mono" type="password" value={key} oninput={(event) => (keyDraft[account.provider] = event.currentTarget.value)} autocomplete="off" spellcheck="false" disabled={busy} aria-label="{name} API key" />
  </div>
</form>
<div class="mb-2 flex flex-wrap gap-2">
  <button class="btn-primary" disabled={busy || !key.trim()} onclick={save}>Save key</button>
  <button class="btn" disabled={busy || !view.canValidate} aria-label="Validate the saved key with {name}" onclick={validate}><Icon name="check" size={15} class="text-icon-start" /> Validate</button>
  <button class="btn" disabled={busy || !view.canValidate} onclick={refresh}><Icon name="refresh" size={15} class="text-icon-info" /> Refresh</button>
  <button class="btn-danger" disabled={busy || !view.canClear} aria-label="Clear the saved {name} API key" onclick={() => (confirmClear = true)}>Clear key…</button>
</div>
{#if key.trim()}<p class="mb-2 text-[12.5px] tone-waiting" role="status">This key isn't saved yet. Select Save key to keep it.</p>{/if}
{#if result}<p class="mb-2 text-[12.5px] {resultIsProblem ? 'text-error' : 'text-muted'}" role="status">{result}</p>{/if}
<p class="hint mb-5">These buttons take effect straight away. The key is kept sealed on the server and is never sent back to a browser.</p>

<h2 class="mb-1 text-[15px] font-semibold">Account</h2>
{#if view.accountDetails.length}
  <p class="text-[13px]">{#each view.accountDetails as line}<span class="block">{line}</span>{/each}</p>
{:else}
  <p class="text-[13px] text-muted">Nothing to show until a key is saved and validated.</p>
{/if}

{#if confirmClear}
  <Confirm title="Clear the {name} API key?" message="This app can't reach your {name} account until a key is saved again, so work waiting on {name} stops being checked. Nothing on your {name} account is deleted." primary="Clear key" danger onclose={() => (confirmClear = false)} onconfirm={clear} />
{/if}
