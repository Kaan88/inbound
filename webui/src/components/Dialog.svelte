<script lang="ts">
  import type { Snippet } from 'svelte';
  import { fade, scale } from 'svelte/transition';
  import Icon from './Icon.svelte';

  // A modal: full screen on a phone, a centred panel otherwise. Escape and the close button call onclose.
  let {
    title,
    onclose,
    wide = false,
    children,
    footer,
  }: { title: string; onclose: () => void; wide?: boolean; children: Snippet; footer?: Snippet } = $props();

  let panel: HTMLDivElement | undefined = $state();

  $effect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const first = panel?.querySelector<HTMLElement>('input, select, textarea, button:not([data-close])');
    first?.focus();
    return () => previous?.focus?.();
  });

  function keydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.stopPropagation();
      onclose();
    }
  }
</script>

<div class="fixed inset-0 z-40 flex items-stretch justify-center bg-black/45 sm:items-center sm:p-6" transition:fade={{ duration: 120 }} role="presentation" onkeydown={keydown}>
  <div
    bind:this={panel}
    class="flex max-h-dvh w-full flex-col bg-surface shadow-2xl sm:max-h-[88vh] sm:rounded-lg sm:border sm:border-divider {wide ? 'sm:max-w-3xl' : 'sm:max-w-lg'}"
    role="dialog"
    aria-modal="true"
    aria-label={title}
    tabindex="-1"
    transition:scale={{ start: 0.97, duration: 140 }}
  >
    <header class="flex items-center gap-2 border-b border-divider bg-chrome px-4 py-2.5 sm:rounded-t-lg">
      <h2 class="flex-1 truncate text-[14px] font-semibold">{title}</h2>
      <button class="rounded p-1 text-muted hover:bg-hover" data-close onclick={onclose} aria-label="Close"><Icon name="close" size={16} /></button>
    </header>
    <div class="min-h-0 flex-1 overflow-y-auto px-4 py-3">
      {@render children()}
    </div>
    {#if footer}
      <footer class="flex flex-wrap items-center justify-end gap-2 border-t border-divider bg-chrome px-4 py-2.5 sm:rounded-b-lg">
        {@render footer()}
      </footer>
    {/if}
  </div>
</div>
