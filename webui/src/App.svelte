<script lang="ts">
  import { onMount } from 'svelte';
  import AddMenu, { type AddKind } from './components/AddMenu.svelte';
  import Icon from './components/Icon.svelte';
  import SpeedGraph from './components/SpeedGraph.svelte';
  import { serializeList } from './lib/addflow';
  import { opensAddUrl } from './lib/keys';
  import type { ClassifyResult } from './lib/types';
  import { speed } from './lib/format';
  import { countJobs, providerEntries, statusBarText } from './lib/list';
  import { listState } from './lib/listState.svelte';
  import { store, type Theme } from './lib/store.svelte';
  import { categoryList, counts, parseViewKey, viewHash, views, type ViewKey } from './lib/views';
  import { noticeBoard, noticeWatchingSince } from './lib/archives';
  import AddLinks from './screens/AddLinks.svelte';
  import AddUrl from './screens/AddUrl.svelte';
  import ImportContainer from './screens/ImportContainer.svelte';
  import ImportList from './screens/ImportList.svelte';
  import ListScreen from './screens/ListScreen.svelte';
  import QueuesScreen from './screens/QueuesScreen.svelte';
  import SettingsScreen from './screens/SettingsScreen.svelte';
  import StatisticsScreen from './screens/StatisticsScreen.svelte';
  import TorBoxScreen from './screens/TorBoxScreen.svelte';

  // The shell: header, sidebar of views, the screen picked by the address's #hash, and the toasts.
  type Screen = { kind: 'list'; view: ViewKey } | { kind: 'queues' } | { kind: 'settings' } | { kind: 'provider'; provider: string } | { kind: 'statistics' };

  function parse(hash: string): Screen {
    const key = hash.replace(/^#\/?/, '');
    if (key === 'queues') return { kind: 'queues' };
    if (key === 'settings') return { kind: 'settings' };
    // `#/provider/<id>` is that account's My provider; `#/provider` alone is the first one that can browse.
    if (key === 'provider' || key.startsWith('provider/')) return { kind: 'provider', provider: decodeURIComponent(key.slice('provider/'.length)) };
    if (key === 'statistics') return { kind: 'statistics' };
    return { kind: 'list', view: parseViewKey(key) ?? 'all' };
  }

  let screen = $state<Screen>(parse(location.hash));
  let drawer = $state(false);
  // Which add dialog is open: Add URL, the many-inputs dialog (optionally with lines already answered), or the list import.
  type Adding = { kind: 'url' } | { kind: 'many'; mode: 'links' | 'magnet' | 'torrent' | 'imported'; seed?: { input: string; result: ClassifyResult }[] } | { kind: 'list' } | { kind: 'container' };
  let adding = $state<Adding | null>(null);

  function openAdd(kind: AddKind) {
    drawer = false;
    adding = kind === 'url' ? { kind: 'url' } : kind === 'list' ? { kind: 'list' } : kind === 'container' ? { kind: 'container' } : { kind: 'many', mode: kind };
  }

  // Download the list as a file; the browser's own Save dialog says where it goes.
  function exportList() {
    drawer = false;
    const text = serializeList(Object.values(store.model.jobs));
    const count = (JSON.parse(text) as unknown[]).length;
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
    link.download = 'download-manager-list.json';
    link.click();
    setTimeout(() => URL.revokeObjectURL(link.href), 10_000);
    store.toast(`Exported ${count} download${count === 1 ? '' : 's'}.`);
  }

  function keydown(event: KeyboardEvent) {
    if (opensAddUrl(event) && !adding) { event.preventDefault(); openAdd('url'); }
  }
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

  // The extraction notices are kept up to date on every screen, not only while the list shows.
  $effect(() => {
    noticeBoard.update(Object.values(store.model.archives), noticeWatchingSince, store.model.settings?.notifyOnCompletion !== false);
  });
  const count = $derived(counts(store.model));
  const providers = $derived(providerEntries(store.model.accounts, Object.values(store.model.remoteJobs)));
  const totalSpeed = $derived(Object.values(store.model.jobs).reduce((sum, job) => sum + (job.state === 'downloading' ? job.bytesPerSecond ?? 0 : 0), 0));
  const listCounts = $derived(countJobs(Object.values(store.model.jobs)));
  // A provider's page is marked by its account: `#/provider` alone stands for the first one that can browse.
  const current = $derived(
    screen.kind === 'list' ? screen.view
      : screen.kind === 'provider' ? `provider:${screen.provider || (providers.find((entry) => entry.showBrowse)?.provider ?? '')}`
      : screen.kind,
  );
  const categories = $derived(categoryList(store.model.settings?.customCategories ?? []));
  const statusText = $derived(screen.kind === 'list' && listState.sideNote ? listState.sideNote : statusBarText(listCounts, screen.kind === 'list' ? listState.selectedFiles : 0));
  // The Host answers archive.list, or has no extraction to show.
  const showExtractions = $derived(store.model.archivesAnswered);

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

{#snippet link(href: string, key: string, icon: string, label: string, n?: number, indent = 0)}
  <a {href} class="flex items-center gap-2 rounded px-3 py-1.5 text-[13px] {current === key ? 'bg-selected font-semibold' : 'hover:bg-hover'}" style={indent ? `padding-left: ${12 + indent * 16}px` : undefined} aria-current={current === key ? 'page' : undefined}>
    <Icon name={icon} size={16} class="shrink-0 text-icon-neutral" />
    <span class="min-w-0 flex-1 truncate">{label}</span>
    {#if n}<span class="tabular text-[12px] text-muted">{n}</span>{/if}
  </a>
{/snippet}

<svelte:window onkeydown={keydown} />

<div class="flex h-dvh flex-col bg-surface text-text">
  <header class="flex h-12 shrink-0 items-center gap-2 border-b border-divider bg-chrome px-3">
    <button class="btn-icon md:hidden" aria-label="Menu" aria-expanded={drawer} onclick={() => (drawer = !drawer)}><Icon name="menu" size={18} /></button>
    <a href="#/all" class="flex items-center gap-2 font-semibold"><img src="/favicon.svg" alt="" width="22" height="22" /> <span class="hidden sm:inline">Inbound</span></a>
    {#if screen.kind === 'list'}
      <label class="relative ml-2 min-w-0 max-w-sm flex-1">
        <span class="sr-only">Search the list</span>
        <Icon name="search" size={15} class="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-muted" />
        <input id="list-search" class="field h-8 pl-7" type="search" placeholder="Search" title="Search (Ctrl+F). F3 goes to the next row." bind:value={search} />
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
      <AddMenu onpick={openAdd} onexport={exportList} canProvider={store.has('remote')} canUpload={store.has('uploads')} />
      <p class="px-3 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted">Categories</p>
      {@render link(viewHash('all'), 'all', 'download', 'All Downloads', count.all)}
      {#each categories as category (category.key)}
        {@render link(viewHash(category.key), category.key, 'folder', category.label, count[category.key], 1)}
      {/each}
      <div class="my-2 border-t border-divider"></div>
      {@render link(viewHash('unfinished'), 'unfinished', 'queue', 'Unfinished', count.unfinished)}
      {@render link(viewHash('active'), 'active', 'play', 'Active', count.active, 1)}
      {@render link(viewHash('queued'), 'queued', 'pause', 'Queued', count.queued, 1)}
      {@render link(viewHash('failed'), 'failed', 'alert', 'Failed', count.failed, 1)}
      {@render link(viewHash('finished'), 'finished', 'check', 'Finished', count.finished)}
      {#if showExtractions}{@render link('#/extractions', 'extractions', 'archive', 'Extractions', count.extractions)}{/if}
      {@render link('#/queues', 'queues', 'calendar', 'Queues')}
      {#if store.has('remote')}
        {#each providers as entry (entry.provider)}
          {#if entry.showWaiting}{@render link('#/remote', 'remote', 'cloud', entry.waiting > 0 ? `Waiting for ${entry.name} (${entry.waiting})` : `Waiting for ${entry.name}`)}{/if}
          {#if entry.showBrowse}{@render link(`#/provider/${encodeURIComponent(entry.provider)}`, `provider:${entry.provider}`, 'key', `My ${entry.name}`)}{/if}
        {/each}
      {/if}
      <p class="px-3 pb-1 pt-4 text-[11px] font-semibold uppercase tracking-wide text-muted">Manage</p>
      {@render link('#/statistics', 'statistics', 'chart', 'Statistics')}
      {@render link('#/settings', 'settings', 'settings', 'Options')}
    </nav>

    <main class="min-w-0 flex-1 overflow-y-auto">
      {#if !store.model.settings}
        <div class="flex h-full items-center justify-center text-[13px] text-muted">{connectionText[store.connection]}</div>
      {:else if screen.kind === 'list'}
        <ListScreen view={screen.view} {search} onadd={() => openAdd('url')} />
      {:else if screen.kind === 'queues'}
        <QueuesScreen />
      {:else if screen.kind === 'provider'}
        <TorBoxScreen provider={screen.provider} />
      {:else if screen.kind === 'statistics'}
        <StatisticsScreen />
      {:else}
        <SettingsScreen />
      {/if}
    </main>
  </div>

  <SpeedGraph />

  <footer class="flex h-7 shrink-0 items-center gap-4 border-t border-divider bg-chrome px-3 text-[12px] text-muted">
    <span class="tabular min-w-0 truncate" title={statusText}>{statusText}</span>
    <span class="tabular shrink-0">{speed(totalSpeed)}</span>
    <span class="flex-1"></span>
    {#if store.model.hello}<span class="hidden sm:inline">Server {store.model.hello.hostVersion}</span>{/if}
  </footer>
</div>

{#if adding?.kind === 'url'}
  <AddUrl onclose={() => (adding = null)} onprovider={(input, result) => (adding = { kind: 'many', mode: 'links', seed: [{ input, result }] })} />
{:else if adding?.kind === 'many'}
  <AddLinks mode={adding.mode} seed={adding.seed} onclose={() => (adding = null)} />
{:else if adding?.kind === 'list'}
  <ImportList onclose={() => (adding = null)} onreview={(seed) => (adding = { kind: 'many', mode: 'imported', seed })} />
{:else if adding?.kind === 'container'}
  <ImportContainer onclose={() => (adding = null)} />
{/if}

<div class="pointer-events-none fixed bottom-10 right-4 z-50 flex max-w-[calc(100vw-2rem)] flex-col items-end gap-2" aria-live="polite">
  {#each store.toasts as toast (toast.id)}
    <div class="pointer-events-auto flex max-w-md items-start gap-2 rounded-md border border-divider bg-surface px-3 py-2 text-[13px] shadow-lg {toast.tone === 'error' ? 'border-l-4 border-l-icon-danger' : ''}" role={toast.tone === 'error' ? 'alert' : 'status'}>
      <Icon name={toast.tone === 'error' ? 'alert' : 'info'} size={16} class="mt-0.5 shrink-0 {toast.tone === 'error' ? 'text-icon-danger' : 'text-icon-info'}" />
      <span class="min-w-0 flex-1">{toast.text}</span>
      {#if toast.jobId}<button class="text-accent hover:underline" onclick={() => { if (screen.kind !== 'list') location.hash = '#/all'; store.showJob = toast.jobId ?? null; store.dismiss(toast.id); }}>Show</button>{/if}
      <button class="text-muted hover:text-text" aria-label="Dismiss" onclick={() => store.dismiss(toast.id)}><Icon name="close" size={14} /></button>
    </div>
  {/each}
</div>
