<script lang="ts">
  import ContextMenu, { type MenuItem } from '../../components/ContextMenu.svelte';
  import Icon from '../../components/Icon.svelte';
  import { store } from '../../lib/store.svelte';
  import type { Site, SiteResult, SiteTransfer } from '../../lib/sites';
  import { kindChip } from '../../lib/sitesBrowse';
  import {
    activityText, activityWords, addRemote, arrowsText, badge, busyRemotes, canEdit, closedToMakeRoom, closeOneFirst, dotWords, footerText, forgetBeforeOpening, inTrouble,
    kindLine, lastUsedText, listed, openHint,
  } from '../../lib/sitesHome';

  // All remotes (SITES.11 R6b, `home`): the address box, "Add a remote", and the remotes with a dot, their kind, the folder
  // on this PC, when they were last used and what runs on them. On a phone the same list is two lines a remote, as in `phone`.
  let { sites, transfers, testing, onopen, onadd, onedit, ontest, onremove, onsignin, onforget, onkeep }: {
    sites: Site[];
    transfers: SiteTransfer[];
    testing: string | null;
    onopen: (site: Site) => void;
    onadd: () => void;
    onedit: (site: Site) => void;
    ontest: (site: Site) => void;
    onremove: (site: Site) => void;
    onsignin: (site: Site) => void;
    onforget: (site: Site) => void;
    onkeep: (site: Site) => void;
  } = $props();

  let address = $state('');
  let opening = $state(false);
  let problem = $state('');
  let menuAt = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);
  let focused = $state<string | null>(null);

  const rows = $derived(listed(sites));
  const lastUsed = (site: Site) => (site.unsaved ? 'Opened once' : lastUsedText(site.lastUsedAt));

  /** Opens an address as an unsaved remote (held by the Host in memory), after forgetting the oldest idle one when eight are open. */
  async function openOnce() {
    const text = address.trim();
    if (!text || opening) return;
    opening = true;
    problem = '';
    try {
      const forget = forgetBeforeOpening(sites, null, busyRemotes(transfers));
      if (forget === null) {
        problem = closeOneFirst;
        return;
      }
      const names = new Map(sites.map((site) => [site.id, site.name]));
      const closed: string[] = [];
      for (const id of forget) {
        const result = await store.ask<SiteResult>('sites.close', { id });
        if (result.ok) closed.push(names.get(id) ?? id);
      }
      // Said, not silent: the oldest idle address opened once was closed to make room.
      if (closed.length) store.toast(closedToMakeRoom(closed));
      const result = await store.ask<SiteResult>('sites.openOnce', { address: text });
      if (!result.ok || !result.site) {
        problem = result.message ?? 'That address could not be opened.';
        return;
      }
      address = '';
      onopen(result.site);
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
    } finally {
      opening = false;
    }
  }

  function items(site: Site): MenuItem[] {
    const list: MenuItem[] = [{ label: 'Open', onselect: () => onopen(site), hint: 'Enter' }];
    if (canEdit(site)) list.push({ label: 'Edit remote', onselect: () => onedit(site), hint: 'F2' });
    list.push({ label: testing === site.id ? 'Testing…' : 'Test', disabled: testing === site.id, onselect: () => ontest(site) });
    if (site.capabilities.includes('signIn') && !site.signedIn) list.push({ label: 'Sign in…', onselect: () => onsignin(site) });
    if (site.unsaved) list.push({ label: 'Remember', onselect: () => onkeep(site) });
    if (site.trustedCertificates.length) list.push({ label: 'Forget the certificate', onselect: () => onforget(site) });
    list.push({ separator: true }, { label: site.unsaved ? 'Close' : 'Remove', onselect: () => onremove(site), hint: 'Delete' });
    return list;
  }

  function menu(site: Site, x: number, y: number) {
    focused = site.id;
    menuAt = { items: items(site), x, y };
  }

  function key(event: KeyboardEvent, site: Site) {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLButtonElement) return;
    if (event.key === 'Enter') { event.preventDefault(); onopen(site); }
    else if (event.key === 'F2' && canEdit(site)) { event.preventDefault(); onedit(site); }
    else if (event.key === 'Delete') { event.preventDefault(); onremove(site); }
    else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
      menu(site, box.left + 40, box.bottom);
    }
  }
</script>

{#snippet dot(site: Site)}
  <span class="inline-block size-2.5 shrink-0 rounded-full {inTrouble(site) ? 'bg-[var(--status-waiting)]' : 'bg-[var(--status-done)]'}" role="img" aria-label={dotWords(site)} title={dotWords(site)}></span>
{/snippet}

<div class="flex flex-wrap items-center gap-2">
  <form class="flex min-w-0 flex-1 gap-2 max-sm:basis-full" onsubmit={(event) => { event.preventDefault(); void openOnce(); }}>
    <input class="field min-w-0 flex-1" type="text" placeholder={openHint} aria-label="Open an address once" bind:value={address} autocomplete="off" spellcheck="false" />
    <button class="btn" type="submit" disabled={opening || !address.trim()}>{opening ? 'Opening…' : 'Open'}</button>
  </form>
  <button class="btn-primary max-md:hidden" onclick={onadd}><Icon name="add" size={15} /> {addRemote}</button>
</div>
{#if problem}<p class="mt-2 text-[12.5px] text-error" role="alert">{problem}</p>{/if}

{#if rows.length === 0}
  <p class="mt-4 text-[13px] text-muted">No remotes yet. Add one to see its files here.</p>
{:else}
  <div class="card mt-3 hidden overflow-hidden md:block" role="grid" aria-label="All remotes">
    <div class="rh-grid border-b border-divider bg-chrome text-[12px] font-semibold" role="row">
      <span>Remote</span><span>Kind</span><span>Folder on {store.texts.hostName}</span><span>Last used</span><span></span>
    </div>
    {#each rows as site (site.id)}
      {@const b = badge(transfers, site.id)}
      <!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_click_events_have_key_events -->
      <div class="rh-grid group border-b border-divider/60 text-[13px] last:border-b-0 {focused === site.id ? 'bg-selected' : 'hover:bg-hover'}" role="row" tabindex="0"
        onfocus={() => (focused = site.id)} onclick={() => (focused = site.id)} ondblclick={() => onopen(site)}
        onkeydown={(event) => key(event, site)}
        oncontextmenu={(event) => { event.preventDefault(); menu(site, event.clientX, event.clientY); }}>
        <span class="flex min-w-0 items-center gap-2">{@render dot(site)}<span class="truncate font-medium">{site.name}</span></span>
        <span>{#if kindChip(site.kind)}<span class="rounded-full border border-divider px-2 text-[11.5px] text-muted">{kindChip(site.kind)}</span>{/if}</span>
        <span class="truncate text-muted" title={site.localFolder ?? ''}>{site.localFolder ?? ''}</span>
        <span class="text-muted">{lastUsed(site)}</span>
        <span class="flex items-center justify-end gap-1">
          {#if activityText(b)}<span class="text-[12.5px] {b.running ? 'text-icon-info' : 'tone-waiting'}">{activityText(b)}</span>{/if}
          <button class="rh-menu" aria-label="Actions for {site.name}" aria-haspopup="menu" onclick={(event) => { event.stopPropagation(); const box = (event.currentTarget as HTMLElement).getBoundingClientRect(); menu(site, box.left, box.bottom + 2); }}><Icon name="more" size={16} /></button>
        </span>
      </div>
    {/each}
  </div>

  <!-- A phone: the remotes as lines, the progress as a thin line under the name (the mockup's first screen). -->
  <ul class="card mt-3 overflow-hidden md:hidden" aria-label="All remotes">
    {#each rows as site (site.id)}
      {@const b = badge(transfers, site.id)}
      <li class="flex items-center border-b border-divider last:border-b-0">
        <button class="min-w-0 flex-1 px-3 py-2.5 text-left" onclick={() => onopen(site)}>
          <span class="flex items-center gap-2">{@render dot(site)}<span class="min-w-0 flex-1 truncate text-[14px] font-medium">{site.name}</span>
            {#if arrowsText(b)}<span class="tabular text-[12.5px] text-icon-info" aria-label={activityWords(b) ?? undefined}>{arrowsText(b)}</span>
            {:else if b.waiting}<span class="tone-waiting text-[12.5px]">{b.waiting} waiting</span>{/if}
          </span>
          <span class="ml-[18px] block text-[12px] text-muted">{kindLine(site, kindChip(site.kind))}</span>
          {#if b.progress !== null}<span class="ml-[18px] mt-1.5 block h-[3px] overflow-hidden rounded-full bg-track"><span class="block h-full rounded-full bg-progress" style="width: {Math.round(b.progress)}%"></span></span>{/if}
        </button>
        <button class="rh-menu !opacity-100" aria-label="Actions for {site.name}" aria-haspopup="menu" onclick={(event) => { const box = (event.currentTarget as HTMLElement).getBoundingClientRect(); menu(site, box.left, box.bottom + 2); }}><Icon name="more" size={18} /></button>
      </li>
    {/each}
  </ul>
{/if}
<!-- A phone: "Add a remote" under the list (`phone`). -->
<button class="btn-primary mt-3 md:hidden" onclick={onadd}><Icon name="add" size={15} /> {addRemote}</button>
<p class="mt-2 text-[12px] text-muted max-md:hidden">{footerText(rows.filter((site) => !site.unsaved).length)}{rows.some((site) => site.unsaved) ? ` · ${rows.filter((site) => site.unsaved).length} opened once` : ''}</p>

{#if menuAt}<ContextMenu items={menuAt.items} x={menuAt.x} y={menuAt.y} label="Remote actions" onclose={() => (menuAt = null)} />{/if}

<style>
  .rh-grid { display: grid; align-items: center; column-gap: 12px; padding: 7px 10px 7px 14px; grid-template-columns: minmax(160px, 1.4fr) 100px minmax(160px, 2fr) 120px minmax(150px, 1fr); }
  .rh-grid:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
  .rh-menu { display: inline-flex; border-radius: 4px; padding: 4px; color: var(--muted); opacity: 0; min-width: 32px; min-height: 32px; align-items: center; justify-content: center; }
  .rh-menu:hover { background: var(--hover); color: var(--text); }
  .rh-grid:hover .rh-menu, .rh-grid:focus-within .rh-menu, .rh-menu:focus-visible { opacity: 1; }
  @media (pointer: coarse) { .rh-menu { opacity: 1; min-width: 44px; min-height: 44px; } }
</style>
