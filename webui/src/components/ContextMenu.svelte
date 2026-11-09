<script lang="ts" module>
  export interface MenuItem {
    label?: string;
    onselect?: () => void;
    /** Dimmed and not clickable. The label can say why, as the desktop's menus do. */
    disabled?: boolean;
    /** A tick: a choice among several, or a switch. */
    checked?: boolean;
    separator?: boolean;
    hidden?: boolean;
    /** The key that does the same, written the way the desktop writes it. */
    hint?: string;
    /** A submenu, opened in place under its entry. */
    items?: MenuItem[];
  }
</script>

<script lang="ts">
  import { tick } from 'svelte';
  import Icon from './Icon.svelte';

  // A menu at a point (a right-click, a long press) or under a button. Arrow keys move, Enter picks, Escape closes.
  let { items, x, y, onclose, label = 'Actions' }: { items: MenuItem[]; x: number; y: number; onclose: () => void; label?: string } = $props();

  let menu: HTMLDivElement | undefined = $state();
  let openSub = $state<string | null>(null);
  let left = $state(0);
  let top = $state(0);

  const visible = $derived(prune(items));

  function prune(list: MenuItem[]): MenuItem[] {
    const kept = list.filter((item) => !item.hidden);
    // No separator first, last or twice in a row.
    return kept.filter((item, index) => !item.separator || (index > 0 && index < kept.length - 1 && !kept[index - 1].separator));
  }

  $effect(() => {
    // Place it, then keep it on the screen.
    left = x;
    top = y;
    void tick().then(() => {
      if (!menu) return;
      const box = menu.getBoundingClientRect();
      left = Math.max(4, Math.min(x, window.innerWidth - box.width - 4));
      top = Math.max(4, Math.min(y, window.innerHeight - box.height - 4));
      menu.querySelector<HTMLElement>('[role^="menuitem"]:not(:disabled)')?.focus();
    });
  });

  function enabled(): HTMLElement[] {
    return [...(menu?.querySelectorAll<HTMLElement>('[role^="menuitem"]:not(:disabled)') ?? [])];
  }

  function choose(item: MenuItem, path: string) {
    if (item.items) {
      openSub = openSub === path ? null : path;
      return;
    }
    onclose();
    item.onselect?.();
  }

  function keydown(event: KeyboardEvent) {
    const list = enabled();
    const at = list.indexOf(document.activeElement as HTMLElement);
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      list[(at + 1) % list.length]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      list[(at <= 0 ? list.length : at) - 1]?.focus();
    } else if (event.key === 'Home') {
      event.preventDefault();
      list[0]?.focus();
    } else if (event.key === 'End') {
      event.preventDefault();
      list[list.length - 1]?.focus();
    } else if (event.key === 'ArrowRight') {
      const active = document.activeElement as HTMLElement | null;
      if (active?.getAttribute('aria-haspopup') === 'menu' && active.getAttribute('aria-expanded') !== 'true') {
        event.preventDefault();
        active.click();
      }
    } else if (event.key === 'ArrowLeft' && openSub !== null) {
      event.preventDefault();
      const parent = menu?.querySelector<HTMLElement>(`[data-path="${openSub}"]`);
      openSub = null;
      parent?.focus();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      onclose();
    } else if (event.key === 'Tab') {
      event.preventDefault();
      onclose();
    }
  }

  function outside(event: Event) {
    if (menu && !menu.contains(event.target as Node)) onclose();
  }

  function scrolled(event: Event) {
    if (menu && !menu.contains(event.target as Node)) onclose();
  }
</script>

<svelte:window onpointerdown={outside} onresize={onclose} onblur={onclose} onscrollcapture={scrolled} />

<div
  bind:this={menu}
  class="fixed z-50 max-h-[80dvh] min-w-52 max-w-[min(20rem,calc(100vw-8px))] overflow-y-auto rounded-md border border-divider bg-surface py-1 text-[13px] shadow-lg"
  style="left: {left}px; top: {top}px; box-shadow: 0 6px 24px var(--shadow)"
  role="menu"
  aria-label={label}
  tabindex="-1"
  onkeydown={keydown}
  oncontextmenu={(event) => event.preventDefault()}
>
  {#snippet entries(list: MenuItem[], depth: number, prefix: string)}
    {#each depth === 0 ? visible : prune(list) as item, index (prefix + index)}
      {#if item.separator}
        <div class="my-1 h-px bg-divider" role="separator"></div>
      {:else}
        {@const path = `${prefix}${index}`}
        <button
          type="button"
          class="menu-entry flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-hover focus:bg-hover focus:outline-none disabled:cursor-default disabled:opacity-45 disabled:hover:bg-transparent"
          style="padding-left: {12 + depth * 16}px"
          role={item.checked !== undefined ? 'menuitemradio' : 'menuitem'}
          aria-checked={item.checked}
          aria-haspopup={item.items ? 'menu' : undefined}
          aria-expanded={item.items ? openSub === path || (openSub?.startsWith(path + '.') ?? false) : undefined}
          data-path={path}
          disabled={item.disabled}
          onclick={() => choose(item, path)}
        >
          <span class="inline-flex w-4 shrink-0 justify-center">{#if item.checked}<Icon name="check" size={14} />{/if}</span>
          <span class="min-w-0 flex-1">{item.label}</span>
          {#if item.hint}<span class="shrink-0 text-[12px] text-muted">{item.hint}</span>{/if}
          {#if item.items}<Icon name={openSub === path || (openSub?.startsWith(path + '.') ?? false) ? 'chevron-down' : 'chevron-right'} size={14} class="shrink-0 text-muted" />{/if}
        </button>
        {#if item.items && (openSub === path || (openSub?.startsWith(path + '.') ?? false))}
          {@render entries(item.items, depth + 1, path + '.')}
        {/if}
      {/if}
    {/each}
  {/snippet}
  {@render entries(visible, 0, '')}
</div>

<style>
  /* The key-moved entry is marked as clearly as any other focused control (U-WU-18). */
  .menu-entry:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
  @media (pointer: coarse) { .menu-entry { min-height: 44px; } }
</style>
