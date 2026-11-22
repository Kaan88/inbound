<script lang="ts">
  import { untrack } from 'svelte';
  import Dialog from '../../components/Dialog.svelte';
  import FolderField from '../../components/FolderField.svelte';
  import Icon from '../../components/Icon.svelte';
  import { store } from '../../lib/store.svelte';
  import { limitProblem, newForm, saveRequest, type Site, type SiteCertificate, type SiteHostKey, type SiteKind, type SiteResult } from '../../lib/sites';
  import { advancedLabel, draftTestText, formName, formSignature, needsTestBeforeSave, saveButtonText, hostLacks, kindOfTile, methodLabel, switchableKinds, tileOf, tilesFor, type Tile } from '../../lib/sitesHome';

  // Add a remote (SITES.11 R6b, `add-tiles` then `add-fields`): kind tiles first, then the short form for the kind with
  // "Sign in with", the folder on this PC, a closed Advanced row and a Test line that tries the form without saving it
  // (sites.testDraft). Edit remote opens the same form. A secret left blank keeps the saved one. onsaved gets the remote the
  // Host kept.
  let { kinds, existing = null, onsaved, onclose }: {
    kinds: SiteKind[];
    existing?: Site | null;
    onsaved: (site: Site) => void;
    onclose: () => void;
  } = $props();

  const tileList = $derived(tilesFor(kinds));
  let tile = $state<Tile | undefined>(untrack(() => (existing ? tileOf(existing.kind) : tilesFor(kinds)[0])));
  let step = $state<'tiles' | 'form'>(untrack(() => (existing ? 'form' : 'tiles')));
  let form = $state(untrack(() => newForm(kinds.find((kind) => kind.kind === (existing?.kind ?? (tile ? kindOfTile(tile, kinds) : 'sftp'))) ?? kinds[0], existing ?? undefined)));
  let busy = $state(false);
  let problem = $state('');
  let advanced = $state(false);

  const kind = $derived(kinds.find((item) => item.kind === form.kind));
  const has = (field: string) => kind?.fields.includes(field) ?? false;
  const choices = (field: string) => kind?.choices?.[field] ?? [];
  const plain = $derived(form.kind === 'ftp' && form.tls === 'none');
  const kindsOfTile = $derived(existing ? [] : switchableKinds(tile, kinds));
  const signs = $derived(kind?.capabilities.includes('signIn') ?? false);
  const title = $derived(existing ? `Edit ${existing.name}` : step === 'tiles' ? 'Add a remote' : (tile?.formTitle ?? 'Add a remote'));

  function choose(next: Tile) {
    tile = next;
  }

  function next() {
    if (!tile) return;
    const first = kinds.find((item) => item.kind === kindOfTile(tile!, kinds));
    form = newForm(first ?? kinds[0]);
    test = null;
    step = 'form';
  }

  function changeKind(value: string) {
    const fresh = newForm(kinds.find((item) => item.kind === value));
    form = { ...fresh, name: form.name, remoteFolder: form.remoteFolder, localFolder: form.localFolder };
    test = null;
  }

  // ---- Test: tries the form as it stands, without saving ----
  let test = $state<{ text: string; ok: boolean; keys?: SiteHostKey[]; certificate?: SiteCertificate } | null>(null);
  // The fingerprint of a certificate the user pressed Trust on: kept for the remote when it is saved (sites.trust).
  let trustedCertificate = $state<string | null>(null);
  let testing = $state(false);
  let trustedKeys = $state<string[]>([]);

  // Server keys accepted in the form are for the server and port they were read from: a change drops them.
  const where = $derived(`${form.host.trim().toLowerCase()}|${form.port}|${form.url.trim()}|${form.endpoint.trim()}|${form.tls}`);
  let lastWhere = untrack(() => where);
  $effect(() => {
    const now = where;
    untrack(() => {
      if (now === lastWhere) return;
      lastWhere = now;
      trustedKeys = [];
      test = null;
    });
  });

  const request = () => saveRequest({ ...form, name: formName(form.name, form.kind, form.host, form.url, form.endpoint) }, kind);

  // The form as last tested, and whether that test failed: Save tries a changed form first, and offers "Save anyway".
  let testedSignature = $state<string | null>(null);
  let testFailed = $state(false);

  async function runTest() {
    testing = true;
    problem = '';
    trustedCertificate = null;
    const started = performance.now();
    try {
      const result = await store.ask<SiteResult>('sites.testDraft', { site: request(), hostKeys: trustedKeys });
      const seconds = (performance.now() - started) / 1000;
      testedSignature = formSignature(request());
      testFailed = !result.ok;
      test = {
        text: draftTestText(result, signs, seconds), ok: result.ok,
        keys: !result.ok && result.problem === 'hostKeyNotAccepted' ? (result.offeredKeys ?? []) : undefined,
        certificate: !result.ok && result.problem === 'certificateNotTrusted' && result.certificate && !result.certificate.cannotTrust ? result.certificate : undefined,
      };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      testedSignature = formSignature(request());
      testFailed = true;
      test = { text: hostLacks(message) ? 'This server is older than the web page and cannot test a form. Save, then Test the remote.' : message, ok: false };
    } finally {
      testing = false;
    }
  }

  function trust() {
    if (!test?.keys?.length) return;
    trustedKeys = test.keys.map((key) => key.entry);
    void runTest();
  }

  function trustCertificate() {
    if (!test?.certificate) return;
    trustedCertificate = test.certificate.fingerprint;
    testFailed = false;
    test = { text: 'The certificate is trusted when you save, and the remote is tried then.', ok: true };
  }

  async function save() {
    const wrongLimit = limitProblem(form);
    if (wrongLimit) { problem = wrongLimit; advanced = true; return; }
    // Not tried since it changed: try it first. A failure shows its result and the button reads "Save anyway".
    if (needsTestBeforeSave(formSignature(request()), testedSignature, signs)) {
      await runTest();
      if (testFailed) return;
    }
    busy = true;
    problem = '';
    try {
      const result = await store.ask<SiteResult>('sites.save', request());
      if (!result.ok || !result.site) {
        problem = result.message ?? 'The remote was not saved.';
        return;
      }
      // A server key the user trusted in the form is kept for the remote now saved.
      if (trustedKeys.length && form.kind === 'sftp') {
        const trusted = await store.ask<SiteResult>('sites.trust', { id: result.site.id, entries: trustedKeys });
        if (!trusted.ok) store.toast(trusted.message ?? "The server's key wasn't kept; Test asks for it again.", 'error');
      }
      if (trustedCertificate) {
        const trusted = await store.ask<SiteResult>('sites.trust', { id: result.site.id, entries: [], certificateFingerprint: trustedCertificate });
        if (!trusted.ok) store.toast(trusted.message ?? "The server's certificate wasn't kept; Test asks for it again.", 'error');
      }
      onsaved(result.site);
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }

  async function readKey(files: FileList | null) {
    const file = files?.[0];
    if (!file) return;
    if (file.size > 64 * 1024) { problem = 'That file is too big to be a private key.'; return; }
    form.privateKey = await file.text();
  }
</script>

<Dialog {title} {onclose} wide>
  {#if step === 'tiles'}
    <p class="mb-3 text-[13px] font-semibold">What kind of place is it?</p>
    <div class="grid grid-cols-2 gap-2 sm:grid-cols-4" role="radiogroup" aria-label="Kind of remote">
      {#each tileList as item (item.id)}
        <button class="tile" role="radio" aria-checked={tile?.id === item.id} class:on={tile?.id === item.id} onclick={() => choose(item)} ondblclick={() => { choose(item); next(); }}>
          <Icon name={item.icon} size={16} class="text-icon-info" />
          <strong class="text-[13px]">{item.name}</strong>
          <span class="text-[12px] text-muted">{item.line}</span>
        </button>
      {/each}
    </div>
  {:else}
    <div class="grid gap-3 text-[13px] sm:grid-cols-2">
      {#if kindsOfTile.length > 1}
        <div class="sm:col-span-2">
          <label class="label" for="site-kind">Service</label>
          <select id="site-kind" class="field" value={form.kind} onchange={(event) => changeKind((event.currentTarget as HTMLSelectElement).value)}>
            {#each kindsOfTile as item (item.kind)}<option value={item.kind}>{item.title}</option>{/each}
          </select>
        </div>
      {/if}
      {#if kind?.note}<p class="hint sm:col-span-2">{kind.note}</p>{/if}

      {#if has('url')}
        <div class="sm:col-span-2"><label class="label" for="site-url">Address</label><input id="site-url" class="field" bind:value={form.url} data-autofocus placeholder="https://cloud.example.com/remote.php/dav/files/me/" autocomplete="off" /></div>
      {/if}
      {#if has('vendor')}
        <div class="sm:col-span-2"><label class="label" for="site-vendor">Server</label><select id="site-vendor" class="field" bind:value={form.vendor}>{#each choices('vendor') as choice (choice.value)}<option value={choice.value}>{choice.title}</option>{/each}</select></div>
      {/if}
      {#if has('provider')}
        <div class="sm:col-span-2"><label class="label" for="site-provider">Service</label><select id="site-provider" class="field" bind:value={form.provider}>{#each choices('provider') as choice (choice.value)}<option value={choice.value}>{choice.title}</option>{/each}</select></div>
      {/if}
      {#if has('endpoint')}
        <div class="sm:col-span-2"><label class="label" for="site-endpoint">Endpoint</label><input id="site-endpoint" class="field" bind:value={form.endpoint} placeholder={form.provider === 'AWS' ? 'Not needed for Amazon S3' : 'https://s3.example.com'} autocomplete="off" /></div>
      {/if}
      {#if has('host')}
        <div><label class="label" for="site-host">Server</label><input id="site-host" class="field" bind:value={form.host} data-autofocus placeholder="example.com" autocomplete="off" /></div>
      {/if}
      {#if has('tls')}
        <div>
          <label class="label" for="site-tls">Encryption</label>
          <select id="site-tls" class="field" bind:value={form.tls}>{#each choices('tls') as choice (choice.value)}<option value={choice.value}>{choice.title}</option>{/each}</select>
        </div>
        {#if plain}<p class="hint tone-failed sm:col-span-2">Not encrypted: the password and every file cross the network as they are.</p>{/if}
      {/if}
      {#if has('user')}
        <div><label class="label" for="site-user">{kind?.userLabel ?? 'User'}</label><input id="site-user" class="field" bind:value={form.user} autocomplete="off" /></div>
      {/if}

      {#if has('key') && (kind?.authMethods.length ?? 0) > 1}
        <div class="sm:col-span-2">
          <span class="label" id="site-auth-label">Sign in with</span>
          <div class="segmented w-full" role="group" aria-labelledby="site-auth-label">
            {#each kind?.authMethods ?? [] as method (method)}
              <button class="flex-1 text-center" aria-pressed={form.authMethod === method} onclick={() => { form.authMethod = method; test = null; }}>{methodLabel(method)}</button>
            {/each}
          </div>
        </div>
      {/if}
      {#if has('password') && form.authMethod !== 'key'}
        <div class="sm:col-span-2">
          <label class="label" for="site-password">{kind?.passwordLabel ?? 'Password'}</label>
          <input id="site-password" class="field" type="password" bind:value={form.password} autocomplete="new-password" placeholder={existing?.hasPassword ? 'Saved; type to change' : ''} />
        </div>
      {/if}
      {#if has('key') && form.authMethod === 'key'}
        <div class="sm:col-span-2">
          <label class="label" for="site-key">Key file (an OpenSSH or PuTTY .ppk key: paste its text, or choose the file)</label>
          <textarea id="site-key" class="field font-mono text-[12px]" rows="3" bind:value={form.privateKey} placeholder={existing?.hasPrivateKey ? 'Saved; paste to change' : '-----BEGIN OPENSSH PRIVATE KEY-----'}></textarea>
          <label class="btn mt-1 inline-flex cursor-pointer">Choose a key file…<input type="file" accept=".ppk,.pem,.key,.txt,text/plain,application/x-putty-private-key,*/*" class="hidden" onchange={(event) => { void readKey(event.currentTarget.files); event.currentTarget.value = ''; }} /></label>
        </div>
        <div class="sm:col-span-2">
          <label class="label" for="site-passphrase">Key passphrase</label>
          <input id="site-passphrase" class="field" type="password" bind:value={form.keyPassphrase} autocomplete="new-password" placeholder={existing?.hasKeyPassphrase ? 'Saved; type to change' : 'If the key has one'} />
        </div>
      {/if}
      {#if signs}
        <p class="hint sm:col-span-2">Your browser opens to sign in when you save.</p>
      {/if}

      <div class="sm:col-span-2">
        <FolderField id="site-local" label="Folder on {store.texts.hostName} that goes with it" bind:value={form.localFolder} hint="Optional. Downloads go here unless you choose another folder." />
      </div>

      <div class="sm:col-span-2">
        <button class="flex items-center gap-1 text-[13px] text-muted hover:text-text" aria-expanded={advanced} onclick={() => (advanced = !advanced)}>
          <Icon name={advanced ? 'chevron-down' : 'chevron-right'} size={14} /> {advancedLabel(has('port'))}
        </button>
      </div>
      {#if advanced}
        <div class="sm:col-span-2"><label class="label" for="site-name">Name</label><input id="site-name" class="field" bind:value={form.name} placeholder={formName('', form.kind, form.host, form.url, form.endpoint)} autocomplete="off" /></div>
        {#if has('port')}
          <div><label class="label" for="site-port">Port</label><input id="site-port" class="field" type="number" min="1" max="65535" bind:value={form.port} /></div>
        {/if}
        {#if has('region')}
          <div><label class="label" for="site-region">Region</label><input id="site-region" class="field" bind:value={form.region} placeholder="Optional" autocomplete="off" /></div>
        {/if}
        <div class={has('port') || has('region') ? '' : 'sm:col-span-2'}>
          <label class="label" for="site-folder">Start in folder on the remote</label>
          <input id="site-folder" class="field" bind:value={form.remoteFolder} placeholder="/" autocomplete="off" />
        </div>
        <div>
          <label class="label" for="site-down">Download speed limit (KB/s)</label>
          <input id="site-down" class="field" inputmode="numeric" bind:value={form.downLimit} placeholder="No limit" autocomplete="off" />
        </div>
        <div>
          <label class="label" for="site-up">Upload speed limit (KB/s)</label>
          <input id="site-up" class="field" inputmode="numeric" bind:value={form.upLimit} placeholder="No limit" autocomplete="off" />
        </div>
        <p class="hint sm:col-span-2">A remote with a limit moves one file at a time. {limitProblem(form) ?? ''}</p>
        {#if signs}
          <p class="hint sm:col-span-2">Your own app registration is optional; leave these empty to use rclone's.</p>
          <div><label class="label" for="site-client">Client ID</label><input id="site-client" class="field" bind:value={form.clientId} autocomplete="off" /></div>
          <div>
            <label class="label" for="site-secret">Client secret</label>
            <input id="site-secret" class="field" type="password" bind:value={form.clientSecret} autocomplete="new-password" placeholder={existing?.hasClientSecret ? 'Saved; type to change' : ''} />
          </div>
        {/if}
      {/if}

      {#if test}
        <div class="notice sm:col-span-2 {test.ok ? '' : 'notice-error'}" role="status">
          <Icon name={test.ok ? 'check' : 'alert'} size={16} class="shrink-0 {test.ok ? 'tone-done' : ''}" />
          <span class="notice-text">
            {test.text}
            {#if test.certificate}<span class="mt-1 block break-all font-mono text-[11.5px]">{test.certificate.subject} · {test.certificate.fingerprint}</span>{/if}
            {#each test.keys ?? [] as key (key.entry)}<span class="mt-1 block break-all font-mono text-[11.5px]">{key.type} {key.fingerprint}</span>{/each}
          </span>
          {#if test.keys?.length}<button class="btn" onclick={trust}>Trust</button>{/if}
          {#if test.certificate}<button class="btn" onclick={trustCertificate}>Trust</button>{/if}
        </div>
      {/if}
    </div>
  {/if}
  {#if problem}<p class="mt-3 text-[12.5px] text-error" role="alert">{problem}</p>{/if}
  {#snippet footer()}
    {#if step === 'tiles'}
      <button class="btn-primary" disabled={!tile} onclick={next}>Next</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {:else}
      <button class="btn-primary" disabled={busy || testing} onclick={save}>{saveButtonText(busy, testing, testFailed && testedSignature === formSignature(request()))}</button>
      {#if existing}<button class="btn" onclick={onclose}>Cancel</button>{:else}<button class="btn" onclick={() => { step = 'tiles'; problem = ''; }}>Back</button>{/if}
      <span class="flex-1"></span>
      <button class="btn" disabled={testing || busy} onclick={runTest}><Icon name="plug" size={15} /> {testing ? 'Testing…' : 'Test'}</button>
    {/if}
  {/snippet}
</Dialog>

<style>
  .tile { display: flex; flex-direction: column; align-items: flex-start; gap: 4px; border: 1px solid var(--divider); border-radius: 6px; background: var(--surface); padding: 10px 12px; text-align: left; min-height: 88px; }
  .tile:hover { background: var(--hover); }
  .tile.on { border-color: var(--accent); background: var(--selected); }
</style>
