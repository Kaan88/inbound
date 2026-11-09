<script lang="ts">
  import Confirm from './Confirm.svelte';
  import Dialog from './Dialog.svelte';
  import Icon from './Icon.svelte';
  import { checkHost, cleartextNote, HostList, maxHostName, normalizeHostAddress, type SavedHost } from '../lib/hosts';

  // The header's host switcher: this host first, then the hosts saved in this browser. Choosing one opens that host's own
  // web UI in this tab (you sign in there once); this page never talks to another host.
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

  function remove() {
    if (!removing) return;
    hostList.remove(removing.id);
    hosts = hostList.all;
    removing = null;
  }
</script>

<svelte:window onclick={(event) => { if (open && !(event.target as HTMLElement).closest?.('[data-host-switcher]')) open = false; }} onkeydown={(event) => { if (event.key === 'Escape' && open) { open = false; toggle?.focus(); } }} />

<div class="relative" data-host-switcher>
  <button bind:this={toggle} class="btn h-8 gap-1.5 px-2" aria-expanded={open} title="Switch to another host" onclick={() => (open = !open)}>
    <Icon name="monitor" size={15} class="text-icon-neutral" />
    <span class="hidden max-w-40 truncate sm:inline">{location.host}</span>
    <Icon name="up" size={12} class={open ? '' : 'rotate-180'} />
  </button>
  {#if open}
    <div class="card absolute right-0 z-40 mt-1 w-72 max-w-[calc(100vw-1.5rem)] overflow-hidden py-1 shadow-lg">
      <div class="flex items-center gap-2 bg-selected px-3 py-1.5 text-[13px]" aria-current="true">
        <div class="min-w-0 flex-1">
          <div class="font-semibold">This host</div>
          <div class="truncate text-[12px] text-muted">{location.host}</div>
        </div>
        <Icon name="check" size={14} class="shrink-0" />
      </div>
      {#each hosts as host (host.id)}
        <div class="flex items-center hover:bg-hover">
          <a class="min-w-0 flex-1 px-3 py-1.5 text-[13px]" href="{host.origin}/" title="Open {host.name}'s own web UI in this tab">
            <div class="truncate">{host.name}</div>
            <div class="truncate text-[12px] text-muted">{host.origin.replace(/^https?:\/\//, '')}</div>
          </a>
          <button class="px-2 py-1 text-[12px] text-accent underline" aria-label="Edit {host.name}" onclick={() => startEdit(host)}>Edit</button>
          <button class="btn-icon shrink-0" aria-label="Remove {host.name}" onclick={() => { open = false; removing = host; }}><Icon name="trash" size={14} /></button>
        </div>
      {/each}
      <div class="my-1 border-t border-divider"></div>
      <button class="flex w-full items-center gap-2 px-3 py-1.5 text-left text-[13px] hover:bg-hover" onclick={startAdd}><Icon name="add" size={14} /> Add a host…</button>
      <p class="hint px-3 pb-1">This list is kept in this browser for this web UI address. Another host's web UI keeps its own list.</p>
    </div>
  {/if}
</div>

{#if editing}
  <Dialog title={editing.id ? 'Edit host' : 'Add a host'} onclose={() => (editing = null)}>
    <form id="host-form" class="grid gap-3" onsubmit={(event) => { event.preventDefault(); save(); }}>
      <div>
        <label class="label" for="host-name">Name</label>
        <input id="host-name" class="field" maxlength={maxHostName} bind:value={editing.name} placeholder="Home server" autocomplete="off" />
      </div>
      <div>
        <label class="label" for="host-address">Address</label>
        <input id="host-address" class="field font-mono" bind:value={editing.address} placeholder="http://192.168.1.20:8080" autocomplete="off" spellcheck="false" data-autofocus />
        <p class="hint mt-1">This opens that host's own web UI in this tab, and you sign in there. Nothing is sent to it from here.</p>
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
  <Confirm title="Remove host" message="Remove {removing.name} ({removing.origin.replace(/^https?:\/\//, '')}) from this list? Nothing changes on that host." primary="Remove" danger onconfirm={remove} onclose={() => (removing = null)} />
{/if}
