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
    const first = panel?.querySelector<HTMLElement>('[data-autofocus]') ?? panel?.querySelector<HTMLElement>('input, select, textarea, button:not([data-close])');
    first?.focus();
    // A first control that is still disabled (a reply not in yet) can't take the focus: the panel does, so Escape works.
    if (panel && !panel.contains(document.activeElement)) panel.focus();
    return () => previous?.focus?.();
  });

  function focusable(): HTMLElement[] {
    // Only this dialog's own controls: one opened from inside it (the folder browser) keeps its own.
    return [...(panel?.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])') ?? [])]
      .filter((element) => !(element as HTMLButtonElement).disabled && element.tabIndex >= 0 && element.getClientRects().length > 0 && element.closest('[role="dialog"]') === panel);
  }

  function keydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.stopPropagation();
      onclose();
    } else if (event.key === 'Tab' && panel && (event.target as Element | null)?.closest?.('[role="dialog"]') === panel) {
      // Tab stays inside the dialog, as it does in the desktop's dialogs.
      const list = focusable();
      if (list.length === 0) return;
      const first = list[0];
      const last = list[list.length - 1];
      const at = document.activeElement;
      if (event.shiftKey && (at === first || !panel.contains(at))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (at === last || !panel.contains(at))) {
        event.preventDefault();
        first.focus();
      }
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
