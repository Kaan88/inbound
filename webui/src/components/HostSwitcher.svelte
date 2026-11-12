<script lang="ts">
  import Confirm from './Confirm.svelte';
  import Dialog from './Dialog.svelte';
  import Icon from './Icon.svelte';
  import { arrival, checkHost, cleartextNote, defaultName, handoffUrl, HostList, maxHostName, normalizeHostAddress, tabHandoff, type SavedHost } from '../lib/hosts';

  // The header's host switcher: this host first, then the hosts saved in this browser. Choosing one opens that host's own
  // web UI in this tab (you sign in there once); this page never talks to another host. The jump carries this host and
  // the list in the link's fragment, so the other host's page can offer the way back and the hosts it lacks (U-WU-1).
  const hostList = new HostList();
  let hosts = $state<SavedHost[]>(hostList.all);
  let open = $state(false);
  let toggle = $state<HTMLButtonElement>();
  type Editing = { id?: string; name: string; address: string };
  let editing = $state<Editing | null>(null);
  let removing = $state<SavedHost | null>(null);
  let problem = $state('');

  const here = location.origin;
  const typed = $derived(editing ? normalizeHostAddress(editing.address) : null);
  const warnClear = $derived(typed?.ok === true && typed.cleartext);
  const came = $derived(arrival(tabHandoff.value, here, hosts));
  const hereName = $derived(came.selfName || defaultName(here));
  /** The link to another host: its address, with this host and this list in the fragment. */
  const jump = (origin: string) => handoffUrl(origin, here, hosts);

  function startAdd() {
    open = false;
    problem = '';
    editing = { name: '', address: '' };
  }

  function startEdit(host: SavedHost) {
    open = false;
    problem = '';
    editing = { id: host.id, name: host.name, address: host.origin };
  }

  function save() {
    if (!editing) return;
    const checked = checkHost(hosts, editing.name, editing.address, here, editing.id);
    if (!checked.ok) {
      problem = checked.error;
      return;
    }
    const { name, origin } = checked.host;
    if (editing.id) hostList.update(editing.id, { name, origin });
    else hostList.add({ name, origin });
    hosts = hostList.all;
    editing = null;
  }

  function keep(list: { name: string; origin: string }[]) {
    for (const host of list) if (!hosts.some((known) => known.origin === host.origin) && host.origin !== here) hostList.add(host);
    hosts = hostList.all;
  }

  function remove() {
    if (!removing) return;
    hostList.remove(removing.id);
    hosts = hostList.all;
    removing = null;
  }
</script>

<!-- The event's path, not the target: Add takes its own button out of the list before this runs, and the menu must stay. -->
<svelte:window onclick={(event) => { if (open && !event.composedPath().some((node) => node instanceof HTMLElement && node.hasAttribute('data-host-switcher'))) open = false; }} onkeydown={(event) => { if (event.key === 'Escape' && open) { open = false; toggle?.focus(); } }} />

<div class="relative min-w-0" data-host-switcher>
  <!-- A server glyph, not the theme's monitor (U-WU-19); the host's name shows on a phone too. -->
  <button bind:this={toggle} class="btn h-8 min-w-0 gap-1.5 px-2" aria-expanded={open} aria-haspopup="true" aria-controls="host-list" aria-label="Showing downloads of {hereName}. Switch to another server" title="Switch to another server" onclick={() => (open = !open)}>
    <Icon name="server" size={15} class="shrink-0 text-icon-neutral" />
    <span class="max-w-20 truncate sm:max-w-40">{hereName}</span>
    <Icon name="up" size={12} class="shrink-0 {open ? '' : 'rotate-180'}" />
  </button>
  {#if open}
    <div id="host-list" role="group" aria-label="Servers" class="card absolute right-0 z-40 mt-1 w-80 max-w-[calc(100vw-1.5rem)] overflow-hidden py-1 shadow-lg">
      {#if came.back}
        <a class="host-entry flex items-center gap-2 px-3 py-1.5 text-[13px] hover:bg-hover" href={jump(came.back.origin)} title="Go back to {came.back.name}'s own web UI">
          <Icon name="chevron-left" size={14} class="shrink-0" />
          <span class="min-w-0 flex-1">
            <span class="block truncate font-semibold">Back to {came.back.name}</span>
            <span class="block truncate text-[12px] text-muted">{came.back.origin.replace(/^https?:\/\//, '')}</span>
          </span>
        </a>
        <div class="my-1 border-t border-divider"></div>
      {/if}
      <div class="flex items-center gap-2 bg-selected px-3 py-1.5 text-[13px]" aria-current="true">
        <div class="min-w-0 flex-1">
          <div class="font-semibold">This server{came.selfName ? ` (${came.selfName})` : ''}</div>
          <div class="truncate text-[12px] text-muted">{location.host}</div>
        </div>
        <Icon name="check" size={14} class="shrink-0" />
      </div>
      {#each hosts as host (host.id)}
        <div class="flex items-center hover:bg-hover">
          <a class="host-entry min-w-0 flex-1 px-3 py-1.5 text-[13px]" href={jump(host.origin)} title="Open {host.name}'s own web UI in this tab">
            <div class="truncate">{host.name}</div>
            <div class="truncate text-[12px] text-muted">{host.origin.replace(/^https?:\/\//, '')}</div>
          </a>
          <button class="host-action px-2 py-1 text-[12px] text-accent underline" aria-label="Edit {host.name}" onclick={() => startEdit(host)}>Edit</button>
          <button class="host-action btn-icon shrink-0" aria-label="Remove {host.name}" onclick={() => { open = false; removing = host; }}><Icon name="trash" size={14} /></button>
        </div>
      {/each}
      {#if came.offered.length > 0 && came.from}
        <div class="my-1 border-t border-divider"></div>
        <p class="hint px-3 pb-1">{came.back ? `${came.from.name} also knows these. They are not in this list yet:` : `You came from ${came.from.name}. Add it to get a Back link here. Not in this list yet:`}</p>
        {#each came.offered as host (host.origin)}
          <div class="flex items-center hover:bg-hover">
            <a class="host-entry min-w-0 flex-1 px-3 py-1.5 text-[13px]" href={jump(host.origin)} title="Open {host.name}'s own web UI in this tab">
              <div class="truncate">{host.name}</div>
              <div class="truncate text-[12px] text-muted">{host.origin.replace(/^https?:\/\//, '')}</div>
            </a>
            <button class="host-action px-2 py-1 text-[12px] text-accent underline" aria-label="Add {host.name} to this list" onclick={() => keep([host])}>Add</button>
          </div>
        {/each}
        {#if came.offered.length > 1}<button class="flex w-full items-center gap-2 px-3 py-1.5 text-left text-[13px] hover:bg-hover" onclick={() => keep(came.offered)}><Icon name="add" size={14} /> Add all {came.offered.length} to this list</button>{/if}
      {/if}
      <div class="my-1 border-t border-divider"></div>
      <button class="flex w-full items-center gap-2 px-3 py-1.5 text-left text-[13px] hover:bg-hover" onclick={startAdd}><Icon name="add" size={14} /> Add a server…</button>
      <p class="hint px-3 pb-1">This list is kept in this browser for this address. Going to another server takes its names and addresses along, never a password, so that server can offer the way back.</p>
    </div>
  {/if}
</div>

{#if editing}
  <Dialog title={editing.id ? 'Edit server' : 'Add a server'} onclose={() => (editing = null)}>
    <form id="host-form" class="grid gap-3" onsubmit={(event) => { event.preventDefault(); save(); }}>
      <div>
        <label class="label" for="host-name">Name</label>
        <input id="host-name" class="field" maxlength={maxHostName} bind:value={editing.name} placeholder="For example: Home server" autocomplete="off" />
      </div>
      <div>
        <label class="label" for="host-address">Address</label>
        <input id="host-address" class="field font-mono" bind:value={editing.address} placeholder="http://192.168.1.20:8080" autocomplete="off" spellcheck="false" data-autofocus />
        <p class="hint mt-1">This opens that server's own web UI in this tab, and you sign in there. Only the names and addresses in this list go with it.</p>
      </div>
      {#if warnClear}<p class="hint" role="note">{cleartextNote}</p>{/if}
      {#if problem}<p class="text-[13px] text-error" role="alert">{problem}</p>{/if}
    </form>
    {#snippet footer()}
      <button class="btn" type="button" onclick={() => (editing = null)}>Cancel</button>
      <button class="btn-primary" type="submit" form="host-form">{editing?.id ? 'Save' : 'Add'}</button>
    {/snippet}
  </Dialog>
{/if}

{#if removing}
  <Confirm title="Remove server" message="Remove {removing.name} ({removing.origin.replace(/^https?:\/\//, '')}) from this list? Nothing changes on that server." primary="Remove" danger onconfirm={remove} onclose={() => (removing = null)} />
{/if}

<style>
  /* A phone: rows a finger can hit (U-WU-8). */
  @media (pointer: coarse) {
    .host-entry { min-height: 44px; }
    .host-action { min-width: 40px; min-height: 40px; }
  }
</style>
