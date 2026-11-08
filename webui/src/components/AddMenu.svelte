<script lang="ts">
  import Icon from './Icon.svelte';

  // The Add button: Add URL on the button itself, and the other ways in under the arrow, in the desktop's order.
  export type AddKind = 'url' | 'links' | 'magnet' | 'torrent' | 'list';
  let { onpick, onexport, canTorrent = true }: { onpick: (kind: AddKind) => void; onexport: () => void; canTorrent?: boolean } = $props();

  let open = $state(false);

  function pick(kind: AddKind) {
    open = false;
    onpick(kind);
  }

  const items: { kind: AddKind; label: string; hint?: string }[] = [
    { kind: 'url', label: 'Add URL', hint: 'Alt+N' },
    { kind: 'links', label: 'Add links' },
    { kind: 'magnet', label: 'Add magnet' },
    { kind: 'torrent', label: 'Import torrent file' },
    { kind: 'list', label: 'Import download list' },
  ];
</script>

<svelte:window onclick={(event) => { if (open && !(event.target as HTMLElement).closest?.('[data-add-menu]')) open = false; }} onkeydown={(event) => { if (event.key === 'Escape') open = false; }} />

<div class="relative mb-3" data-add-menu>
  <div class="flex">
    <button class="btn-primary min-w-0 flex-1 justify-center rounded-r-none" onclick={() => pick('url')} title="Add URL (Alt+N)"><Icon name="add" size={16} /> Add URL</button>
    <button class="btn-primary rounded-l-none border-l border-white/30 px-2" aria-label="More ways to add" aria-haspopup="menu" aria-expanded={open} onclick={() => (open = !open)}><Icon name="up" size={14} class={open ? '' : 'rotate-180'} /></button>
  </div>
  {#if open}
    <ul class="card absolute left-0 right-0 z-40 mt-1 overflow-hidden py-1 shadow-lg" role="menu">
      {#each items as item (item.kind)}
        {#if item.kind !== 'torrent' || canTorrent}
          <li role="none"><button class="flex w-full items-center justify-between px-3 py-1.5 text-left text-[13px] hover:bg-hover" role="menuitem" onclick={() => pick(item.kind)}><span>{item.label}</span>{#if item.hint}<span class="text-[11px] text-muted">{item.hint}</span>{/if}</button></li>
        {/if}
      {/each}
      <li role="none"><button class="flex w-full px-3 py-1.5 text-left text-[13px] hover:bg-hover" role="menuitem" onclick={() => { open = false; onexport(); }}>Export download list</button></li>
    </ul>
  {/if}
</div>
