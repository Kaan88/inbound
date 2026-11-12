<script lang="ts">
  import { onMount } from 'svelte';
  import AddMenu, { type AddKind } from './components/AddMenu.svelte';
  import HostSwitcher from './components/HostSwitcher.svelte';
  import Icon from './components/Icon.svelte';
  import SpeedGraph from './components/SpeedGraph.svelte';
  import { serializeList, type TorrentListEntry } from './lib/addflow';
  import { opensAddUrl } from './lib/keys';
  import type { ClassifyResult } from './lib/types';
  import { clockTime, speed } from './lib/format';
  import { versionWords } from './lib/versionStatus';
  import { countJobs, liveSpeed, providerEntries, statusBarText } from './lib/list';
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
  import TorrentAdd from './screens/TorrentAdd.svelte';
  import { chipText, hereName, jobInput, openAsTorrentStep, sidebarShows, torrentProvider, torrentsApply, torrentsOffText } from './lib/torrents';
  import Confirm from './components/Confirm.svelte';
  import type { Job } from './lib/types';
  import { chipCompact, extraViewShows, torrentExtraViews, torrentFilters, waitingForChoice, waitingForChoiceText } from './lib/torrentList';
  import { dragMayHoldTorrents, intake, intakeStep, isEditableTarget, pageTakes, uploadTorrent } from './lib/torrentIntake';

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
  type Adding = { kind: 'url' } | { kind: 'many'; mode: 'links' | 'magnet' | 'torrent' | 'imported'; seed?: { input: string; result: ClassifyResult }[]; inputs?: TorrentInput[] }
    | { kind: 'list' } | { kind: 'container' } | { kind: 'torrent'; mode: 'magnet' | 'torrent' | 'mixed'; initial?: TorrentInput[] };
  // T10: fromJobId, a finished .torrent download the provider's add removes once the provider took it.
  type TorrentInput = { input: string; label?: string; fromJobId?: string };
  let adding = $state<Adding | null>(null);
  // T1: the server downloads torrents itself, so magnets and .torrent files go to the torrent dialog.
  const torrentsOn = $derived(torrentsApply(store.model.hello?.capabilities, store.model.settings));

  function openAdd(kind: AddKind) {
    drawer = false;
    if ((kind === 'magnet' || kind === 'torrent') && torrentsOn) {
      adding = { kind: 'torrent', mode: kind };
      return;
    }
    adding = kind === 'url' ? { kind: 'url' } : kind === 'list' ? { kind: 'list' } : kind === 'container' ? { kind: 'container' } : { kind: 'many', mode: kind };
  }

  // The torrent dialog's provider lines go on to the provider's add dialog, as on the desktop (RunTorrentChoiceAsync).
  function toProvider(inputs: TorrentInput[]) {
    const mode = inputs.every((entry) => entry.input.startsWith('upload:')) ? 'torrent' : inputs.length === 1 && inputs[0].input.toLowerCase().startsWith('magnet:') ? 'magnet' : 'links';
    adding = { kind: 'many', mode, inputs };
  }

  // ---- T10: a finished download that is a .torrent file, from its row's Open as Torrent ----

  let torrentsOff = $state(false);

  function openAsTorrent(job: Job) {
    if (dialogOpen()) return;
    const step = openAsTorrentStep(torrentsOn, torrentProvider(store.has('remote'), store.model.accounts) !== null);
    if (step === 'dialog') adding = { kind: 'torrent', mode: 'mixed', initial: [{ input: jobInput(job.id), label: job.fileName }] };
    // The provider reads the file on the server by its path, as a .torrent file given by its path.
    else if (step === 'provider') toProvider([{ input: job.finalFilePath, label: job.fileName, fromJobId: job.id }]);
    else torrentsOff = true;
  }

  // ---- T3: magnets and .torrent files pasted (Ctrl+V) or dropped anywhere on the page, as in the Windows app ----
  // Review 5:A6: http, https and ftp links pasted or dropped the same way go to Add Links.

  /** The server downloads them, or the account that also takes torrents does (MainWindow.TorrentsCanBeAdded). */
  const torrentsTaken = $derived(store.connection === 'open' && (torrentsOn || torrentProvider(store.has('remote'), store.model.accounts) !== null));
  const connected = $derived(store.connection === 'open');
  const dialogOpen = () => adding !== null || document.querySelector('[role="dialog"]') !== null;
  let dropping = $state(false);
  /** Links that came with torrents: Add Links opens with them once the torrent dialog closes. */
  let pendingLinks: TorrentInput[] = [];

  function closeAdding() {
    const next = pendingLinks;
    pendingLinks = [];
    adding = next.length > 0 ? { kind: 'many', mode: 'links', inputs: next } : null;
  }

  async function takeTorrents(text: string, files: File[], dropped: boolean) {
    const found = intake(text, files, dropped);
    if (found.empty) { store.toast(found.empty); return; }
    const step = intakeStep(found.magnets.length + found.files.length, found.links.length, torrentsTaken);
    if (step.problem) { store.toast(step.problem); return; }
    const links: TorrentInput[] = step.links ? found.links.map((input) => ({ input })) : [];
    const inputs: TorrentInput[] = step.torrents ? found.magnets.map((input) => ({ input })) : [];
    if (step.torrents && found.files.length > 0) {
      if (!store.has('uploads')) store.toast(`${hereName(store.model.hello?.hostKind).upper} doesn't take uploaded files, so a .torrent file can't be sent to it from a browser.`, 'error');
      else {
        for (const file of found.files) {
          try {
            inputs.push(await uploadTorrent(file));
          } catch (error) {
            store.toast(`${file.name}: ${error instanceof Error ? error.message : String(error)}`, 'error');
          }
        }
      }
    }
    if (dialogOpen()) return;
    if (inputs.length === 0) {
      if (links.length > 0) adding = { kind: 'many', mode: 'links', inputs: links };
      return;
    }
    pendingLinks = links;
    if (torrentsOn) adding = { kind: 'torrent', mode: 'mixed', initial: inputs };
    else toProvider(inputs);
  }

  function pasted(event: ClipboardEvent) {
    if (!pageTakes(connected, dialogOpen(), isEditableTarget(event.target as HTMLElement | null))) return;
    event.preventDefault();
    void takeTorrents(event.clipboardData?.getData('text/plain') ?? '', [...(event.clipboardData?.files ?? [])], false);
  }

  function dragOver(event: DragEvent) {
    if (!pageTakes(connected, dialogOpen()) || !dragMayHoldTorrents(event.dataTransfer?.types)) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
    dropping = true;
  }

  function dragLeave(event: DragEvent) {
    // Only when it leaves the window, not each element under the pointer.
    if (event.relatedTarget === null) dropping = false;
  }

  function drop(event: DragEvent) {
    const was = dropping;
    dropping = false;
    if (!was || !pageTakes(connected, dialogOpen())) return;
    event.preventDefault();
    const data = event.dataTransfer;
    const text = [data?.getData('text/uri-list') ?? '', data?.getData('text/plain') ?? ''].join('\n');
    void takeTorrents(text, [...(data?.files ?? [])], true);
  }

  // Download the list as a file; the browser's own Save dialog says where it goes.
  async function exportList() {
    drawer = false;
    const jobs = Object.values(store.model.jobs);
    // A torrent is saved with its .torrent and file choice, which only the server has; without them, its magnet alone.
    let torrents: Record<string, TorrentListEntry> | undefined;
    if (jobs.some((job) => job.torrent)) {
      // Page by page, so a long list stays under the message size limit.
      const all: Record<string, TorrentListEntry> = {};
      let after: string | undefined;
      try {
        do {
          const page = await store.ask<{ entries: Record<string, TorrentListEntry>; next?: string | null }>('torrent.listEntries', after ? { after } : {});
          Object.assign(all, page.entries);
          after = page.next && page.next !== after ? page.next : undefined;
        } while (after);
      } catch {
        // What came before the failure is kept; the rest are saved with their magnets.
      }
      torrents = Object.keys(all).length ? all : undefined;
    }
    const text = serializeList(jobs, torrents);
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
    const changed = (event: HashChangeEvent) => {
      if (store.leaveGuard?.(event)) return;
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
  const connectionText: Record<string, string> = {
    connecting: 'Connecting…', open: 'Connected', reconnecting: 'Reconnecting…', signedOut: 'Signed out',
    incompatible: 'This page is older than the server. Reload it.', closed: 'Not connected',
  };
  const count = $derived(counts(store.model));
  const providers = $derived(providerEntries(store.model.accounts, Object.values(store.model.remoteJobs)));
  // The same figure the speed graph shows (U-WU-14); nothing while the server can't be reached (U-WU-3).
  const totalSpeed = $derived(liveSpeed(Object.values(store.model.jobs), store.connection === 'open', store.model.snapshots > 0));
  const offline = $derived(store.connection !== 'open' && store.connection !== 'connecting' && store.connection !== 'signedOut');
  const offlineText = $derived(
    store.connection === 'incompatible' ? connectionText.incompatible
      : `Can't reach the server${store.lostAt ? ` since ${clockTime(store.lostAt)}` : ''}. ${store.connection === 'reconnecting' ? 'Trying again… What the list shows is from before.' : 'Reload the page to try again.'}`,
  );
  const listCounts = $derived(countJobs(Object.values(store.model.jobs)));
  // A provider's page is marked by its account: `#/provider` alone stands for the first one that can browse.
  const current = $derived(
    screen.kind === 'list' ? screen.view
      : screen.kind === 'provider' ? `provider:${screen.provider || (providers.find((entry) => entry.showBrowse)?.provider ?? '')}`
      : screen.kind,
  );
  const categories = $derived(categoryList(store.model.settings?.customCategories ?? []));
  const statusText = $derived(offline && store.model.snapshots > 0 ? connectionText[store.connection] : screen.kind === 'list' && listState.sideNote ? listState.sideNote : statusBarText(listCounts, screen.kind === 'list' ? listState.selectedFiles : 0));
  // The Host answers archive.list, or has no extraction to show.
  const showExtractions = $derived(store.model.archivesAnswered);
  // The Torrents view, while torrents are on or any is listed; the chip while one runs (TorrentRules.ChipText).
  const showTorrents = $derived(sidebarShows(torrentsOn, count.torrents > 0));
  const torrentChip = $derived(store.connection === 'open' ? chipText(Object.values(store.model.jobs)) : null);
  // On a phone the chip says less, so the status words keep their room (WEB-T3).
  const torrentChipShort = $derived(store.connection === 'open' ? chipCompact(Object.values(store.model.jobs)) : null);
  // WEB-T12: the torrents whose file list is in and that wait for the user's choice of files.
  const choosing = $derived(waitingForChoice(Object.values(store.model.jobs)));


  const themes: { key: Theme; icon: string; label: string }[] = [
    { key: 'system', icon: 'monitor', label: 'Follow the system' },
    { key: 'light', icon: 'sun', label: 'Light' },
    { key: 'dark', icon: 'moon', label: 'Dark' },
  ];
  const nextTheme = $derived(themes[(themes.findIndex((t) => t.key === store.theme) + 1) % themes.length]);
  const themeNow = $derived(themes.find((t) => t.key === store.theme) ?? themes[0]);
</script>

{#snippet link(href: string, key: string, icon: string, label: string, n?: number, indent = 0, badge = 0, badgeTip = '')}
  <a {href} class="touch-row flex items-center gap-2 rounded px-3 py-1.5 text-[13px] {current === key ? 'bg-selected font-semibold' : 'hover:bg-hover'}" style={indent ? `padding-left: ${12 + indent * 16}px` : undefined} aria-current={current === key ? 'page' : undefined}>
    <Icon name={icon} size={16} class="shrink-0 text-icon-neutral" />
    <span class="min-w-0 flex-1 truncate">{label}</span>
    {#if badge > 0}<span class="tabular rounded-full bg-accent px-1.5 text-[11px] font-semibold text-accent-text" title={badgeTip}><span class="sr-only">{badgeTip}: </span>{badge}</span>{/if}
    {#if n}<span class="tabular text-[12px] text-muted">{n}</span>{/if}
  </a>
{/snippet}

<svelte:window onkeydown={keydown} onpaste={pasted} ondragover={dragOver} ondragleave={dragLeave} ondrop={drop} />

<div class="flex h-dvh flex-col bg-surface text-text">
  <header class="flex h-12 shrink-0 items-center gap-2 border-b border-divider bg-chrome px-3">
    <button class="btn-icon md:hidden" aria-label="Menu" aria-expanded={drawer} onclick={() => (drawer = !drawer)}><Icon name="menu" size={18} /></button>
    <a href="#/all" class="flex items-center gap-2 font-semibold"><img src="/favicon.svg" alt="" width="22" height="22" /> <span class="hidden sm:inline">Inbound</span></a>
    {#if screen.kind === 'list'}
      <label class="relative min-w-0 max-w-sm flex-1 md:ml-2">
        <span class="sr-only">Search the list</span>
        <Icon name="search" size={15} class="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-muted" />
        <input id="list-search" class="field h-8 pl-7" type="search" placeholder="Search" title="Search (Ctrl+F). F3 goes to the next row." bind:value={search} />
      </label>
    {:else}
      <span class="flex-1"></span>
    {/if}
    <span class="flex-1 {screen.kind === 'list' ? 'max-md:hidden' : ''}"></span>
    <span class="hidden items-center gap-1.5 text-[12px] text-muted sm:flex" role="status" aria-live="polite">
      <span class="inline-block size-2 rounded-full {store.connection === 'open' ? 'bg-status-active' : store.connection === 'reconnecting' || store.connection === 'connecting' ? 'bg-status-waiting' : 'bg-status-failed'}"></span>
      {connectionText[store.connection]}
    </span>
    <HostSwitcher />
    <!-- On a phone the theme button is in the drawer, so the search box and the host's name have room. -->
    <button class="btn-icon max-md:hidden" title="Theme: {themeNow.label}. Switch to {nextTheme.label.toLowerCase()}." aria-label="Theme: {themeNow.label}" onclick={() => store.setTheme(nextTheme.key)}><Icon name={themeNow.icon} size={18} /></button>
    {#if store.signInRequired}
      <button class="btn-icon" title="Sign out" aria-label="Sign out" onclick={() => store.signOut()}><Icon name="logout" size={18} /></button>
    {/if}
  </header>

  {#if offline}
    <!-- On every width, above the list (U-WU-3); the phone had it only. -->
    <div class="flex items-center gap-2 border-b border-divider bg-highlight px-4 py-1.5 text-[12.5px]" role="alert">
      <Icon name="alert" size={15} class="shrink-0 text-icon-danger" />
      <span class="min-w-0 flex-1">{offlineText}</span>
      {#if store.connection !== 'reconnecting'}<button class="btn h-7 py-0" onclick={() => location.reload()}>Reload</button>{/if}
    </div>
  {/if}

  <div class="relative flex min-h-0 flex-1">
    {#if drawer}<button class="fixed inset-0 z-20 bg-black/50 md:hidden" aria-label="Close the menu" tabindex="-1" onclick={() => (drawer = false)}></button>{/if}
    <nav aria-label="Views" class="z-30 w-60 shrink-0 overflow-y-auto border-r border-divider bg-chrome p-2 max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:shadow-xl max-md:transition-transform {drawer ? '' : 'max-md:-translate-x-full'}">
      <!-- The drawer covers the header on a phone, so it has its own close button (U-WU-22). -->
      <div class="mb-2 flex items-center justify-between md:hidden">
        <span class="px-1 text-[13px] font-semibold">Menu</span>
        <span class="flex items-center gap-1">
          <button class="btn-icon" title="Theme: {themeNow.label}. Switch to {nextTheme.label.toLowerCase()}." aria-label="Theme: {themeNow.label}" onclick={() => store.setTheme(nextTheme.key)}><Icon name={themeNow.icon} size={18} /></button>
          <button class="btn-icon" aria-label="Close the menu" onclick={() => (drawer = false)}><Icon name="close" size={18} /></button>
        </span>
      </div>
      <AddMenu onpick={openAdd} onexport={exportList} canProvider={store.has('remote')} canUpload={store.has('uploads')} canTorrents={torrentsOn} disabled={store.connection !== 'open'} />
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
      {#if showTorrents}
        {@render link(viewHash('torrents'), 'torrents', 'swarm', 'Torrents', count.torrents, 0, choosing, waitingForChoiceText(choosing))}
        {#each torrentFilters as filter (filter.key)}
          {@render link(viewHash(`torrents-${filter.key}`), `torrents-${filter.key}`, filter.icon, filter.label, count[`torrents-${filter.key}`], 1)}
        {/each}
        <!-- T7: only while they hold a torrent, or while shown, so the sidebar doesn't grow by six empty rows. -->
        {#each torrentExtraViews as extra (extra.key)}
          {#if extraViewShows(count[`torrents-${extra.key}`], `torrents-${extra.key}`, current)}
            {@render link(viewHash(`torrents-${extra.key}`), `torrents-${extra.key}`, extra.icon, extra.label, count[`torrents-${extra.key}`], 1)}
          {/if}
        {/each}
      {/if}
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
        <ListScreen view={screen.view} {search} onadd={() => openAdd('url')} onopentorrent={openAsTorrent} />
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
    <span class="tabular shrink-0">{totalSpeed === null ? '' : speed(totalSpeed)}</span>
    <span class="flex-1"></span>
    {#if torrentChip}<a href={viewHash('torrents')} class="inline-flex shrink-0 items-center gap-1 hover:text-text" title="Show the torrents"><Icon name="swarm" size={13} /><span class="hidden sm:inline">{torrentChip}</span><span class="sm:hidden">{torrentChipShort}</span></a>{/if}
    {#if store.model.hello}<span class="hidden items-center gap-1.5 sm:inline-flex"><img src="/favicon.svg" alt="" width="14" height="14" />{store.texts.versionPrefix} {versionWords(store.model.hello.hostVersion, store.model.status?.latestVersion)}</span>{/if}
  </footer>
</div>

{#if dropping}
  <div class="pointer-events-none fixed inset-0 z-50 flex items-center justify-center bg-black/30 p-4" aria-hidden="true">
    <div class="flex items-center gap-2 rounded-md border-2 border-dashed border-accent bg-surface px-6 py-4 text-[14px] font-semibold"><Icon name="swarm" size={18} /> Drop to add downloads</div>
  </div>
{/if}

{#if torrentsOff}
  <Confirm title="Open as torrent" message={torrentsOffText} primary="Open Options" onclose={() => (torrentsOff = false)}
    onconfirm={() => { torrentsOff = false; location.hash = '#/settings'; }} />
{/if}
{#if adding?.kind === 'url'}
  <AddUrl onclose={() => (adding = null)} onprovider={(input, result) => (adding = { kind: 'many', mode: 'links', seed: [{ input, result }] })}
    ontorrent={torrentsOn ? (magnets) => (adding = { kind: 'torrent', mode: 'magnet', initial: magnets.map((input) => ({ input })) }) : undefined} />
{:else if adding?.kind === 'many'}
  <AddLinks mode={adding.mode} seed={adding.seed} inputs={adding.inputs} onclose={closeAdding} />
{:else if adding?.kind === 'torrent'}
  <TorrentAdd mode={adding.mode} initial={adding.initial} onclose={closeAdding} />
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
