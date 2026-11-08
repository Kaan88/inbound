<script lang="ts">
  import { onMount } from 'svelte';
  import Icon from './components/Icon.svelte';
  import { speed } from './lib/format';
  import { store, type Theme } from './lib/store.svelte';
  import { counts, views, type ViewKey } from './lib/views';
  import AddLinks from './screens/AddLinks.svelte';
  import ListScreen from './screens/ListScreen.svelte';
  import QueuesScreen from './screens/QueuesScreen.svelte';
  import SettingsScreen from './screens/SettingsScreen.svelte';
  import TorBoxScreen from './screens/TorBoxScreen.svelte';

  // The shell: header, sidebar of views, the screen picked by the address's #hash, and the toasts.
  type Screen = { kind: 'list'; view: ViewKey } | { kind: 'queues' } | { kind: 'settings' } | { kind: 'provider' };

  function parse(hash: string): Screen {
    const key = hash.replace(/^#\/?/, '');
    if (key === 'queues') return { kind: 'queues' };
    if (key === 'settings') return { kind: 'settings' };
    if (key === 'provider') return { kind: 'provider' };
    const view = (['all', 'unfinished', 'active', 'queued', 'failed', 'finished', 'remote', 'extractions'] as ViewKey[]).find((v) => v === key);
    return { kind: 'list', view: view ?? 'all' };
  }

  let screen = $state<Screen>(parse(location.hash));
  let drawer = $state(false);
  let adding = $state(false);
  let search = $state('');

  onMount(() => {
    store.start();
    const changed = () => {
      screen = parse(location.hash);
      drawer = false;
    };
    addEventListener('hashchange', changed);
    return () => removeEventListener('hashchange', changed);
  });

  const count = $derived(counts(store.model));
  const providerName = $derived(store.model.accounts[0]?.providerName ?? 'TorBox');
  const totalSpeed = $derived(Object.values(store.model.jobs).reduce((sum, job) => sum + (job.state === 'downloading' ? job.bytesPerSecond ?? 0 : 0), 0));
  const active = $derived(Object.values(store.model.jobs).filter((job) => job.state === 'downloading').length);
  const current = $derived(screen.kind === 'list' ? screen.view : screen.kind);

  const connectionText: Record<string, string> = {
    connecting: 'Connecting…', open: 'Connected', reconnecting: 'Reconnecting…', signedOut: 'Signed out',
    incompatible: 'This page is older than the server. Reload it.', closed: 'Not connected',
  };

  const themes: { key: Theme; icon: string; label: string }[] = [
    { key: 'system', icon: 'monitor', label: 'Follow the system' },
    { key: 'light', icon: 'sun', label: 'Light' },
    { key: 'dark', icon: 'moon', label: 'Dark' },
  ];
  const nextTheme = $derived(themes[(themes.findIndex((t) => t.key === store.theme) + 1) % themes.length]);
  const themeNow = $derived(themes.find((t) => t.key === store.theme) ?? themes[0]);
</script>

{#snippet link(href: string, key: string, icon: string, label: string, n?: number)}
  <a {href} class="flex items-center gap-2 rounded px-3 py-1.5 text-[13px] {current === key ? 'bg-selected font-semibold' : 'hover:bg-hover'}" aria-current={current === key ? 'page' : undefined}>
    <Icon name={icon} size={16} class="shrink-0 text-icon-neutral" />
    <span class="min-w-0 flex-1 truncate">{label}</span>
    {#if n}<span class="tabular text-[12px] text-muted">{n}</span>{/if}
  </a>
{/snippet}

<div class="flex h-dvh flex-col bg-surface text-text">
  <header class="flex h-12 shrink-0 items-center gap-2 border-b border-divider bg-chrome px-3">
    <button class="btn-icon md:hidden" aria-label="Menu" aria-expanded={drawer} onclick={() => (drawer = !drawer)}><Icon name="menu" size={18} /></button>
    <a href="#/all" class="flex items-center gap-2 font-semibold"><img src="/favicon.svg" alt="" width="22" height="22" /> <span class="hidden sm:inline">Inbound</span></a>
    {#if screen.kind === 'list'}
      <label class="relative ml-2 min-w-0 max-w-sm flex-1">
        <span class="sr-only">Search the list</span>
        <Icon name="search" size={15} class="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-muted" />
        <input class="field h-8 pl-7" type="search" placeholder="Search" bind:value={search} />
      </label>
    {:else}
      <span class="flex-1"></span>
    {/if}
    <span class="flex-1"></span>
    <span class="hidden items-center gap-1.5 text-[12px] text-muted sm:flex" role="status" aria-live="polite">
      <span class="inline-block size-2 rounded-full {store.connection === 'open' ? 'bg-status-active' : store.connection === 'reconnecting' || store.connection === 'connecting' ? 'bg-status-waiting' : 'bg-status-failed'}"></span>
      {connectionText[store.connection]}
    </span>
    <button class="btn-icon" title="Theme: {themeNow.label}. Switch to {nextTheme.label.toLowerCase()}." aria-label="Theme: {themeNow.label}" onclick={() => store.setTheme(nextTheme.key)}><Icon name={themeNow.icon} size={18} /></button>
    {#if store.signInRequired}
      <button class="btn-icon" title="Sign out" aria-label="Sign out" onclick={() => store.signOut()}><Icon name="logout" size={18} /></button>
    {/if}
  </header>

  {#if store.connection !== 'open' && store.connection !== 'connecting'}
    <div class="border-b border-divider bg-highlight px-4 py-1.5 text-[12.5px] sm:hidden" role="status">{connectionText[store.connection]}</div>
  {/if}

  <div class="relative flex min-h-0 flex-1">
    {#if drawer}<button class="fixed inset-0 z-20 bg-black/30 md:hidden" aria-label="Close the menu" onclick={() => (drawer = false)}></button>{/if}
    <nav aria-label="Views" class="z-30 w-60 shrink-0 overflow-y-auto border-r border-divider bg-chrome p-2 max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:shadow-xl max-md:transition-transform {drawer ? '' : 'max-md:-translate-x-full'}">
      <button class="btn-primary mb-3 w-full justify-center" onclick={() => { adding = true; drawer = false; }}><Icon name="add" size={16} /> Add links</button>
      <p class="px-3 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted">Downloads</p>
      {#each views as view (view.key)}
        {@render link(`#/${view.key}`, view.key, view.key === 'all' ? 'download' : view.key === 'finished' ? 'check' : view.key === 'failed' ? 'alert' : view.key === 'active' ? 'play' : view.key === 'queued' ? 'queue' : 'pause', view.label, count[view.key])}
      {/each}
      {#if store.has('remote')}{@render link('#/remote', 'remote', 'cloud', `Waiting at ${providerName}`, count.remote)}{/if}
      {@render link('#/extractions', 'extractions', 'archive', 'Extractions', count.extractions)}
      <p class="px-3 pb-1 pt-4 text-[11px] font-semibold uppercase tracking-wide text-muted">Manage</p>
      {@render link('#/queues', 'queues', 'calendar', 'Queues and scheduler')}
      {#if store.has('remote')}{@render link('#/provider', 'provider', 'key', `My ${providerName}`)}{/if}
      {@render link('#/settings', 'settings', 'settings', 'Settings')}
    </nav>

    <main class="min-w-0 flex-1 overflow-y-auto">
      {#if !store.model.settings}
        <div class="flex h-full items-center justify-center text-[13px] text-muted">{connectionText[store.connection]}</div>
      {:else if screen.kind === 'list'}
        <ListScreen view={screen.view} {search} onadd={() => (adding = true)} />
      {:else if screen.kind === 'queues'}
        <QueuesScreen />
      {:else if screen.kind === 'provider'}
        <TorBoxScreen />
      {:else}
        <SettingsScreen />
      {/if}
    </main>
  </div>

  <footer class="flex h-7 shrink-0 items-center gap-4 border-t border-divider bg-chrome px-3 text-[12px] text-muted">
    <span class="tabular">{active} downloading</span>
    <span class="tabular">{speed(totalSpeed)}</span>
    {#if store.model.status}<span class="tabular hidden sm:inline">{store.model.status.totalJobs} in the list</span>{/if}
    <span class="flex-1"></span>
    {#if store.model.hello}<span class="hidden sm:inline">Server {store.model.hello.hostVersion}</span>{/if}
  </footer>
</div>

{#if adding}<AddLinks onclose={() => (adding = false)} />{/if}

<div class="pointer-events-none fixed bottom-10 right-4 z-50 flex max-w-[calc(100vw-2rem)] flex-col items-end gap-2" aria-live="polite">
  {#each store.toasts as toast (toast.id)}
    <div class="pointer-events-auto flex max-w-md items-start gap-2 rounded-md border border-divider bg-surface px-3 py-2 text-[13px] shadow-lg {toast.tone === 'error' ? 'border-l-4 border-l-icon-danger' : ''}" role={toast.tone === 'error' ? 'alert' : 'status'}>
      <Icon name={toast.tone === 'error' ? 'alert' : 'info'} size={16} class="mt-0.5 shrink-0 {toast.tone === 'error' ? 'text-icon-danger' : 'text-icon-info'}" />
      <span class="min-w-0 flex-1">{toast.text}</span>
      <button class="text-muted hover:text-text" aria-label="Dismiss" onclick={() => store.dismiss(toast.id)}><Icon name="close" size={14} /></button>
    </div>
  {/each}
</div>
