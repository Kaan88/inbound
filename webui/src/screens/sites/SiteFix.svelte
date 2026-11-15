<script lang="ts">
  import { onMount } from 'svelte';
  import Dialog from '../../components/Dialog.svelte';
  import { store } from '../../lib/store.svelte';
  import { problems, type Site, type SiteCertificate, type SiteHostKey, type SiteResult } from '../../lib/sites';

  // What a site needs before it can be used (SITES.7), as the desktop asks it: the server's SSH host key, a certificate
  // no authority vouches for, or a browser sign-in. onfixed runs once the site is ready to try again.
  let { site, problem, onfixed, onclose }: {
    site: Site;
    problem: string;
    onfixed: () => void;
    onclose: () => void;
  } = $props();

  let busy = $state(false);
  let error = $state('');
  let keys = $state<SiteHostKey[] | null>(null);
  let certificate = $state<SiteCertificate | null>(null);
  let signIn = $state<{ url?: string | null; command?: string | null } | null>(null);
  let token = $state('');

  const hostKey = $derived(problem === problems.hostKeyNotAccepted || problem === problems.hostKeyChanged);
  const title = $derived(hostKey ? (problem === problems.hostKeyChanged ? 'The server key changed' : 'Trust this server?')
    : problem === problems.certificateNotTrusted ? 'Trust this certificate?' : `Sign in to ${site.name}`);

  // The site's own record follows sites.changed: a sign-in finished in a browser shows up here. It counts only once the
  // Host has said it is signing in and then that it has stopped (a site signed in before, whose access ran out, is
  // already "signed in" when this starts).
  const current = $derived(store.model.sites.find((item) => item.id === site.id) ?? site);
  let sawSigningIn = $state(false);
  $effect(() => {
    if (!signIn) return;
    if (current.signingIn) sawSigningIn = true;
    else if (sawSigningIn && current.signedIn) onfixed();
  });
  /** Only a web address is offered as a link. */
  const safeUrl = (value: string | null | undefined) => (value && /^https?:\/\//i.test(value) ? value : null);

  async function load() {
    busy = true;
    error = '';
    try {
      if (hostKey) {
        const result = await store.ask<{ ok: boolean; message?: string; keys?: SiteHostKey[] }>('sites.probe', { host: site.host, port: site.port });
        if (!result.ok) error = result.message ?? 'The server did not answer.';
        else keys = result.keys ?? [];
      } else if (problem === problems.certificateNotTrusted) {
        const result = await store.ask<{ ok: boolean; message?: string; certificate?: SiteCertificate }>('sites.certificate', { id: site.id });
        if (!result.ok || !result.certificate) error = result.message ?? 'The certificate could not be read.';
        else certificate = result.certificate;
      }
    } catch (failure) {
      error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      busy = false;
    }
  }

  onMount(() => void load());

  async function trust(payload: Record<string, unknown>) {
    busy = true;
    error = '';
    try {
      const result = await store.ask<SiteResult>('sites.trust', { id: site.id, ...payload });
      if (result.ok) onfixed();
      else error = result.message ?? 'It was not trusted.';
    } catch (failure) {
      error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      busy = false;
    }
  }

  async function startSignIn() {
    busy = true;
    error = '';
    try {
      const result = await store.ask<{ ok: boolean; message?: string; url?: string; authorizeCommand?: string }>('sites.signIn', { id: site.id });
      signIn = { url: result.url, command: result.authorizeCommand };
      if (!result.ok && result.message) error = result.message;
    } catch (failure) {
      error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      busy = false;
    }
  }

  async function paste() {
    busy = true;
    error = '';
    try {
      const result = await store.ask<{ ok: boolean; message?: string }>('sites.signIn', { id: site.id, token: token.trim() });
      if (result.ok) onfixed();
      else error = result.message ?? 'That did not sign in.';
    } catch (failure) {
      error = failure instanceof Error ? failure.message : String(failure);
    } finally {
      busy = false;
    }
  }

  function close() {
    if (signIn) void store.ask('sites.signInCancel', { id: site.id }).catch(() => undefined);
    onclose();
  }
</script>

<Dialog {title} onclose={close} wide>
  <div class="space-y-3 text-[13px]">
    {#if hostKey}
      {#if problem === problems.hostKeyChanged}
        <p class="tone-failed">The server {site.host} now shows a different key from the one you trusted. If nobody changed the server, someone may be between you and it: don't trust the new key.</p>
      {:else}
        <p>This is the first time {store.texts.hostName} connects to {site.host}. Check that the key below is the server's (its owner can tell you) before you trust it.</p>
      {/if}
      {#if keys}
        <ul class="space-y-1">
          {#each keys as key (key.entry)}<li class="break-all font-mono text-[12px]"><span class="text-muted">{key.type}</span> {key.fingerprint}</li>{/each}
        </ul>
      {:else if busy}<p class="text-muted">Asking the server for its key…</p>{/if}
    {:else if problem === problems.certificateNotTrusted}
      <p>The server's certificate is not vouched for by an authority {store.texts.hostName} trusts. Trust it for this site only if you know it is the server's own.</p>
      {#if certificate}
        <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
          <dt class="text-muted">Issued to</dt><dd class="break-all">{certificate.subject}</dd>
          <dt class="text-muted">Issued by</dt><dd class="break-all">{certificate.selfSigned ? 'Itself (self-signed)' : certificate.issuer}</dd>
          <dt class="text-muted">Valid until</dt><dd>{new Date(certificate.notAfter).toLocaleDateString()}</dd>
          <dt class="text-muted">Fingerprint</dt><dd class="break-all font-mono text-[12px]">{certificate.fingerprint}</dd>
        </dl>
        {#if certificate.cannotTrust}<p class="tone-failed">{certificate.cannotTrust}</p>{/if}
      {:else if busy}<p class="text-muted">Reading the certificate…</p>{/if}
    {:else}
      <p>{site.name} is used after you sign in through a browser. Inbound keeps only the access it is given.</p>
      {#if !signIn}
        <button class="btn-primary" disabled={busy} onclick={startSignIn}>{busy ? 'Starting…' : 'Sign in…'}</button>
      {:else}
        {#if safeUrl(signIn.url)}
          <p>If this browser runs on the same computer as Inbound, open <a class="link break-all" href={safeUrl(signIn.url)} target="_blank" rel="noopener noreferrer">the sign-in page</a> and allow access; this closes by itself when it is done.</p>
        {/if}
        {#if signIn.command}
          <p>From another device, run this on a computer that has rclone and a browser, then paste everything between <code>---&gt;</code> and <code>&lt;---End paste</code> here:</p>
          <pre class="overflow-x-auto rounded border border-divider p-2 font-mono text-[12px]">{signIn.command}</pre>
          <label class="label" for="site-token">Token</label>
          <textarea id="site-token" class="field font-mono text-[12px]" rows="3" bind:value={token}></textarea>
        {/if}
      {/if}
    {/if}
    {#if error}<p class="text-[12.5px] text-error" role="alert">{error}</p>{/if}
  </div>
  {#snippet footer()}
    <button class="btn" onclick={close}>Cancel</button>
    {#if hostKey && keys && keys.length && problem !== problems.hostKeyChanged}
      <button class="btn-primary" disabled={busy} onclick={() => trust({ entries: keys!.map((key) => key.entry) })}>Trust</button>
    {:else if hostKey && keys && keys.length}
      <button class="btn-danger" disabled={busy} onclick={() => trust({ entries: keys!.map((key) => key.entry) })}>Trust the new key</button>
    {:else if certificate && !certificate.cannotTrust}
      <button class="btn-primary" disabled={busy} onclick={() => trust({ certificateFingerprint: certificate!.fingerprint })}>Trust</button>
    {:else if signIn?.command}
      <button class="btn-primary" disabled={busy || !token.trim()} onclick={paste}>Sign in with token</button>
    {/if}
  {/snippet}
</Dialog>
