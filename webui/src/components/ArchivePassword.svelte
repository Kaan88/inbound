<script lang="ts">
  import Dialog from './Dialog.svelte';
  import { passwordDone, passwordHint, passwordPrimary, passwordTitle, type ArchivePasswordTarget } from '../lib/archives';
  import { store } from '../lib/store.svelte';

  // The archive password, for a set, a package or a download with no record yet. It stays on the server and is
  // never shown again. Nothing is sent for an empty box except "Forget Saved Password", which clears it.
  let { target, onclose }: { target: ArchivePasswordTarget; onclose: () => void } = $props();

  let password = $state('');
  let busy = $state(false);

  async function send(clear: boolean) {
    const text = clear ? '' : password;
    if (!clear && !text) return;
    busy = true;
    const result = await store.attempt<{ ok: boolean }>('archive.setPassword', {
      setId: target.setId, packageId: target.packageId, filePath: target.filePath, password: text,
    }, passwordDone(target, clear));
    busy = false;
    password = '';
    if (result?.ok) onclose();
  }
</script>

<Dialog title={passwordTitle(target)} {onclose}>
  <form onsubmit={(event) => { event.preventDefault(); void send(false); }}>
    <p class="mb-3 text-[13px]">{passwordHint(target)}</p>
    <label class="label" for="archive-password">Archive password</label>
    <input id="archive-password" class="field" type="password" bind:value={password} autocomplete="off" spellcheck="false" />
    <p class="hint mt-1">It stays on the server and is never shown again.</p>
  </form>
  {#snippet footer()}
    <button class="btn" onclick={onclose}>Cancel</button>
    {#if target.hasPassword}<button class="btn" disabled={busy} onclick={() => send(true)}>Forget Saved Password</button>{/if}
    <button class="btn-primary" disabled={busy || !password} onclick={() => send(false)}>{passwordPrimary(target)}</button>
  {/snippet}
</Dialog>
