<script lang="ts">
  import { untrack } from 'svelte';
  import Dialog from '../../components/Dialog.svelte';
  import FolderField from '../../components/FolderField.svelte';
  import { store } from '../../lib/store.svelte';
  import { newForm, saveRequest, type Site, type SiteKind, type SiteResult } from '../../lib/sites';

  // Add a site / Edit a site (SITES.7): the fields of the kind as the server lists them (sites.kinds), the same dialog the
  // desktop draws. A secret left blank keeps the saved one. onsaved gets the site the server kept.
  let { kinds, existing = null, onsaved, onclose }: {
    kinds: SiteKind[];
    existing?: Site | null;
    onsaved: (site: Site) => void;
    onclose: () => void;
  } = $props();

  // The form starts from the site as it was when the dialog opened.
  let form = $state(untrack(() => newForm(kinds.find((kind) => kind.kind === (existing?.kind ?? 'sftp')) ?? kinds[0], existing ?? undefined)));
  let busy = $state(false);
  let problem = $state('');

  const kind = $derived(kinds.find((item) => item.kind === form.kind));
  const has = (field: string) => kind?.fields.includes(field) ?? false;
  const choices = (field: string) => kind?.choices?.[field] ?? [];
  const plain = $derived(form.kind === 'ftp' && form.tls === 'none');

  function changeKind(next: string) {
    const fresh = newForm(kinds.find((item) => item.kind === next));
    form = { ...fresh, name: form.name, remoteFolder: form.remoteFolder, localFolder: form.localFolder };
  }

  async function save() {
    if (!form.name.trim()) {
      problem = 'Give the site a name.';
      return;
    }
    busy = true;
    problem = '';
    try {
      const result = await store.ask<SiteResult>('sites.save', saveRequest(form, kind));
      if (!result.ok || !result.site) {
        problem = result.message ?? 'The site was not saved.';
        return;
      }
      onsaved(result.site);
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title={existing ? `Edit ${existing.name}` : 'Add a site'} {onclose} wide>
  <div class="grid gap-3 text-[13px] sm:grid-cols-2">
    <div class="sm:col-span-2">
      <label class="label" for="site-kind">Kind</label>
      <select id="site-kind" class="field" value={form.kind} disabled={!!existing} onchange={(event) => changeKind((event.currentTarget as HTMLSelectElement).value)}>
        {#each kinds as item (item.kind)}<option value={item.kind}>{item.title}</option>{/each}
      </select>
      {#if kind?.note}<p class="hint mt-1">{kind.note}</p>{/if}
    </div>
    <div class="sm:col-span-2">
      <label class="label" for="site-name">Name</label>
      <input id="site-name" class="field" bind:value={form.name} data-autofocus autocomplete="off" />
    </div>

    {#if has('url')}
      <div class="sm:col-span-2"><label class="label" for="site-url">Address</label><input id="site-url" class="field" bind:value={form.url} placeholder="https://cloud.example.com/remote.php/dav/files/me/" autocomplete="off" /></div>
    {/if}
    {#if has('vendor')}
      <div class="sm:col-span-2"><label class="label" for="site-vendor">Server</label><select id="site-vendor" class="field" bind:value={form.vendor}>{#each choices('vendor') as choice (choice.value)}<option value={choice.value}>{choice.title}</option>{/each}</select></div>
    {/if}
    {#if has('provider')}
      <div class="sm:col-span-2"><label class="label" for="site-provider">Service</label><select id="site-provider" class="field" bind:value={form.provider}>{#each choices('provider') as choice (choice.value)}<option value={choice.value}>{choice.title}</option>{/each}</select></div>
    {/if}
    {#if has('endpoint')}
      <div><label class="label" for="site-endpoint">Endpoint</label><input id="site-endpoint" class="field" bind:value={form.endpoint} placeholder={form.provider === 'AWS' ? 'Not needed for Amazon S3' : 'https://s3.example.com'} autocomplete="off" /></div>
    {/if}
    {#if has('region')}
      <div><label class="label" for="site-region">Region</label><input id="site-region" class="field" bind:value={form.region} placeholder="Optional" autocomplete="off" /></div>
    {/if}
    {#if has('host')}
      <div><label class="label" for="site-host">Server</label><input id="site-host" class="field" bind:value={form.host} placeholder="example.com" autocomplete="off" /></div>
    {/if}
    {#if has('port')}
      <div><label class="label" for="site-port">Port</label><input id="site-port" class="field" type="number" min="1" max="65535" bind:value={form.port} /></div>
    {/if}
    {#if has('tls')}
      <div class="sm:col-span-2">
        <label class="label" for="site-tls">Encryption</label>
        <select id="site-tls" class="field" bind:value={form.tls}>{#each choices('tls') as choice (choice.value)}<option value={choice.value}>{choice.title}</option>{/each}</select>
        {#if plain}<p class="hint mt-1 tone-failed">Not encrypted: the password and every file cross the network as they are.</p>{/if}
      </div>
    {/if}
    {#if has('user')}
      <div><label class="label" for="site-user">{kind?.userLabel ?? 'User name'}</label><input id="site-user" class="field" bind:value={form.user} autocomplete="off" /></div>
    {/if}
    {#if has('key') && (kind?.authMethods.length ?? 0) > 1}
      <div>
        <label class="label" for="site-auth">Sign in with</label>
        <select id="site-auth" class="field" bind:value={form.authMethod}><option value="password">Password</option><option value="key">Private key</option></select>
      </div>
    {/if}
    {#if has('password') && form.authMethod !== 'key'}
      <div>
        <label class="label" for="site-password">{kind?.passwordLabel ?? 'Password'}</label>
        <input id="site-password" class="field" type="password" bind:value={form.password} autocomplete="new-password" placeholder={existing?.hasPassword ? 'Saved; type to change' : ''} />
      </div>
    {/if}
    {#if has('key') && form.authMethod === 'key'}
      <div class="sm:col-span-2">
        <label class="label" for="site-key">Private key (the text of the key file)</label>
        <textarea id="site-key" class="field font-mono text-[12px]" rows="4" bind:value={form.privateKey} placeholder={existing?.hasPrivateKey ? 'Saved; paste to change' : '-----BEGIN OPENSSH PRIVATE KEY-----'}></textarea>
      </div>
      <div>
        <label class="label" for="site-passphrase">Key passphrase</label>
        <input id="site-passphrase" class="field" type="password" bind:value={form.keyPassphrase} autocomplete="new-password" placeholder={existing?.hasKeyPassphrase ? 'Saved; type to change' : 'If the key has one'} />
      </div>
    {/if}
    {#if has('signIn')}
      <div class="sm:col-span-2"><p class="hint">Your own app registration is optional; leave these empty to use rclone's.</p></div>
      <div><label class="label" for="site-client">Client ID</label><input id="site-client" class="field" bind:value={form.clientId} autocomplete="off" /></div>
      <div>
        <label class="label" for="site-secret">Client secret</label>
        <input id="site-secret" class="field" type="password" bind:value={form.clientSecret} autocomplete="new-password" placeholder={existing?.hasClientSecret ? 'Saved; type to change' : ''} />
      </div>
    {/if}

    <div class="sm:col-span-2">
      <label class="label" for="site-folder">Start in folder on the site</label>
      <input id="site-folder" class="field" bind:value={form.remoteFolder} placeholder="/" autocomplete="off" />
    </div>
    <div class="sm:col-span-2">
      <FolderField id="site-local" label="Folder for downloads on {store.texts.hostName}" bind:value={form.localFolder} hint="Optional. Downloads to {store.texts.hostName} go here unless you choose another folder." />
    </div>
  </div>
  {#if problem}<p class="mt-3 text-[12.5px] text-error" role="alert">{problem}</p>{/if}
  {#snippet footer()}
    <button class="btn" onclick={onclose}>Cancel</button>
    <button class="btn-primary" disabled={busy} onclick={save}>{busy ? 'Saving…' : 'Save'}</button>
  {/snippet}
</Dialog>
