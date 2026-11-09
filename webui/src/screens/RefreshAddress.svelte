<script lang="ts">
  import { untrack } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import { cantTell, routeOf } from '../lib/addlines';
  import { refreshStart } from '../lib/details';
  import { store } from '../lib/store.svelte';
  import type { ClassifyResult, Job } from '../lib/types';

  // "Refresh download address…" (D13): a box prefilled with the address. An empty submit on a hidden address retries
  // the saved one. "Try again / Download directly" is asked only when the server can't tell what the new address is.
  let { job, onclose }: { job: Job; onclose: () => void } = $props();

  const hidden = untrack(() => !!job.addressHidden);
  let address = $state(untrack(() => refreshStart(job)));
  let busy = $state(false);
  let message = $state('');
  let question = $state<string | null>(null);

  const isWebLink = (value: string) => /^https?:\/\//i.test(value);

  async function send(next: string, explicitDirect: boolean) {
    await store.ask('job.refreshUrl', { jobId: job.id, url: next, explicitDirect });
    // A finished download stays finished with its file; Redownload uses the new address.
    store.toast(job.state === 'completed' ? 'The address was changed. Redownload uses it.' : 'The address was refreshed.');
    onclose();
  }

  async function submit() {
    message = '';
    const given = address.trim();
    if (!given && !hidden) return;
    const next = given || job.url;
    busy = true;
    try {
      // The address it already has stays usable: refusing it would strand a retry of a direct download.
      if (next !== job.url && isWebLink(next) && store.has('remote')) {
        const result = await store.ask<ClassifyResult>('remote.classify', { text: next });
        const provider = result.providerName || 'Your provider';
        const line = { input: next, position: 1, result, error: null, direct: false };
        if (routeOf(line) === 'provider') {
          message = `${provider} handles this link, so it can't replace this download's address. The address wasn't changed. Use Add links to send it through ${provider}.`;
          return;
        }
        if (cantTell(line) || result.checkIncomplete) {
          question = result.checkIncomplete && result.note
            ? `${result.note} Download it directly only if you know it isn't one of those sites.`
            : "This app couldn't tell what this address is, so it isn't treated as an ordinary link unless you say so. Try the check again, or download directly.";
          return;
        }
      }
      await send(next, false);
    } catch (error) {
      message = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }

  async function direct() {
    question = null;
    busy = true;
    try {
      await send(address.trim() || job.url, true);
    } catch (error) {
      message = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Refresh download address" {onclose}>
  <form id="refresh-form" onsubmit={(event) => { event.preventDefault(); void submit(); }}>
    <label class="label" for="refresh-address">New download address</label>
    <input id="refresh-address" class="field font-mono" bind:value={address} placeholder={hidden ? 'Paste the new link, or leave empty to try the saved one' : ''} autocomplete="off" spellcheck="false" />
    {#if message}<p class="mt-2 text-[12px] text-error" role="alert">{message}</p>{/if}
  </form>
  {#snippet footer()}
    <button class="btn" type="button" onclick={onclose}>Cancel</button>
    <button class="btn-primary" type="submit" form="refresh-form" disabled={busy || (!address.trim() && !hidden)}>Refresh</button>
  {/snippet}
</Dialog>

{#if question}
  <Dialog title="Refresh download address" onclose={() => (question = null)}>
    <p class="text-[13px]">{question}</p>
    {#snippet footer()}
      <button class="btn" onclick={() => (question = null)}>Cancel</button>
      <button class="btn" onclick={() => { question = null; void submit(); }}>Try again</button>
      <button class="btn-primary" onclick={direct}>Download directly</button>
    {/snippet}
  </Dialog>
{/if}
