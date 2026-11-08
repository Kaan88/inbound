<script lang="ts">
  import { onMount } from 'svelte';
  import { tickUntil } from '../lib/ticker';

  // The one page a browser sees before signing in: set the password the first time, then sign in with it.
  interface Status {
    signInRequired: boolean;
    signedIn: boolean;
    setupNeeded: boolean;
    setupCodeNeeded: boolean;
    version?: string;
  }

  let status = $state<Status | null>(null);
  let problem = $state('');
  let password = $state('');
  let repeat = $state('');
  let setupCode = $state('');
  let busy = $state(false);
  let waitUntil = $state(0);
  let now = $state(Date.now());
  // The clock runs only while a wait counts down.
  $effect(() => tickUntil(waitUntil, 500, (time) => (now = time)));

  const waitLeft = $derived(Math.max(0, Math.ceil((waitUntil - now) / 1000)));

  async function load() {
    try {
      const response = await fetch('/api/auth/status', { credentials: 'same-origin', cache: 'no-store' });
      status = (await response.json()) as Status;
      if (!status.signInRequired || status.signedIn) location.replace('/');
    } catch {
      problem = "The server can't be reached. Try again in a moment.";
    }
  }
  onMount(load);

  async function send(path: string, body: Record<string, string>) {
    busy = true;
    problem = '';
    try {
      const response = await fetch(path, {
        method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      const result = (await response.json().catch(() => ({}))) as { ok?: boolean; error?: string; retryAfterSeconds?: number };
      if (response.ok && result.ok) {
        location.replace('/');
        return;
      }
      problem = result.error ?? `The server answered ${response.status}.`;
      if (result.retryAfterSeconds) waitUntil = Date.now() + result.retryAfterSeconds * 1000;
      if (response.status === 409) await load();
    } catch {
      problem = "The server can't be reached. Try again in a moment.";
    } finally {
      busy = false;
      password = '';
      repeat = '';
    }
  }

  function submit(event: SubmitEvent) {
    event.preventDefault();
    if (!status) return;
    if (status.setupNeeded) {
      if (password !== repeat) {
        problem = "The two passwords aren't the same.";
        return;
      }
      void send('/api/auth/setup', status.setupCodeNeeded ? { password, setupCode } : { password });
    } else {
      void send('/api/auth/signin', { password });
    }
  }
</script>

<main class="flex min-h-dvh items-center justify-center bg-chrome px-4 py-10">
  <div class="card w-full max-w-sm p-6 shadow-sm">
    <div class="mb-5 flex items-center gap-2">
      <img src="/favicon.svg" alt="" width="28" height="28" />
      <span class="text-[18px] font-semibold">Inbound</span>
    </div>

    {#if !status}
      <p class="text-[13px] text-muted">{problem || 'Loading…'}</p>
    {:else}
      <h1 class="mb-1 text-[16px] font-semibold">{status.setupNeeded ? 'Set a password' : 'Sign in'}</h1>
      <p class="hint mb-4">{status.setupNeeded
        ? 'This server has no password yet. The one you set here is needed to use it from any browser.'
        : 'Enter the password of this server.'}</p>

      <form class="grid gap-3" onsubmit={submit}>
        {#if status.setupNeeded && status.setupCodeNeeded}
          <div>
            <label class="label" for="setup-code">Setup code</label>
            <input id="setup-code" class="field font-mono uppercase tracking-widest" bind:value={setupCode} autocomplete="off" spellcheck="false" required />
            <p class="hint mt-1">This browser isn't on the server's own computer, so the server asks for the code it wrote in its log (docker logs).</p>
          </div>
        {/if}
        <!-- A user name the browser's password manager can store with the password; the server has one user. -->
        <input class="hidden" type="text" name="username" value="inbound" autocomplete="username" readonly tabindex="-1" aria-hidden="true" />
        <div>
          <label class="label" for="password">{status.setupNeeded ? 'Password (8 characters or more)' : 'Password'}</label>
          <!-- svelte-ignore a11y_autofocus -->
          <input id="password" class="field" type="password" bind:value={password} autocomplete={status.setupNeeded ? 'new-password' : 'current-password'} required autofocus minlength={status.setupNeeded ? 8 : undefined} />
        </div>
        {#if status.setupNeeded}
          <div>
            <label class="label" for="repeat">The same password again</label>
            <input id="repeat" class="field" type="password" bind:value={repeat} autocomplete="new-password" required />
          </div>
        {/if}
        {#if problem}<p class="text-[13px] text-error" role="alert">{problem}</p>{/if}
        {#if waitLeft > 0}<p class="text-[12px] text-muted" role="status">Wait {waitLeft} s before trying again.</p>{/if}
        <button class="btn-primary w-full" type="submit" disabled={busy || waitLeft > 0}>{status.setupNeeded ? 'Set the password' : 'Sign in'}</button>
      </form>
      {#if status.version}<p class="hint mt-5 text-center">Server {status.version}</p>{/if}
    {/if}
  </div>
</main>
