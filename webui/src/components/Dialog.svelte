<script lang="ts" module>
  import { DialogStack, wrapTarget } from '../lib/dialogs';

  // One stack for every dialog on the page: Back closes the top one, and only the top one answers keys.
  const stack = new DialogStack(() => (typeof history === 'undefined' ? null : history), () => (typeof location === 'undefined' ? '' : location.href));
  let listening = false;
  function listen() {
    if (listening || typeof window === 'undefined') return;
    listening = true;
    window.addEventListener('popstate', () => stack.popped());
  }
</script>

<script lang="ts">
  import type { Snippet } from 'svelte';
  import { fade, scale } from 'svelte/transition';
  import Icon from './Icon.svelte';

  // A modal: full screen on a phone, a centred panel otherwise. Escape, the close button and the browser's Back call
  // onclose. The focus stays inside: when the control that had it goes away or is disabled (a step changes, a button
  // waits for a reply), the dialog takes it back, so Escape and Tab keep working (U-WU-6, review of D2d).
  let {
    title,
    onclose,
    wide = false,
    children,
    footer,
  }: { title: string; onclose: () => void; wide?: boolean; children: Snippet; footer?: Snippet } = $props();

  let panel: HTMLDivElement | undefined = $state();
  /** The control that last had the focus here: it gets it back once it is enabled again. */
  let lastFocused: HTMLElement | null = null;
  let myId = 0;

  function firstControl(): HTMLElement | null {
    const marked = panel?.querySelector<HTMLElement>('[data-autofocus]');
    if (marked && usable(marked)) return marked;
    return focusable().find((element) => !element.hasAttribute('data-close')) ?? null;
  }

  function usable(element: HTMLElement): boolean {
    return !(element as HTMLButtonElement).disabled && element.isConnected && element.getClientRects().length > 0;
  }

  $effect(() => {
    listen();
    const previous = document.activeElement as HTMLElement | null;
    const id = stack.opened(() => onclose());
    myId = id;
    firstControl()?.focus();
    // A first control that is still disabled (a reply not in yet) can't take the focus: the panel does, so Escape works.
    if (panel && !panel.contains(document.activeElement)) panel.focus();

    // Focus that drops to the page (the focused control was removed or disabled) comes back here.
    const observer = new MutationObserver(() => {
      if (!panel || !stack.isTop(id)) return;
      const at = document.activeElement;
      if (at && at !== document.body && at !== panel) return;
      if (lastFocused && panel.contains(lastFocused) && usable(lastFocused)) lastFocused.focus();
      else if (at !== panel) (firstControl() ?? panel).focus();
    });
    if (panel) observer.observe(panel, { subtree: true, childList: true, attributes: true, attributeFilter: ['disabled', 'hidden', 'class'] });

    return () => {
      observer.disconnect();
      stack.closed(id);
      previous?.focus?.();
    };
  });

  function focusable(): HTMLElement[] {
    // Only this dialog's own controls: one opened from inside it (the folder browser) keeps its own.
    return [...(panel?.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])') ?? [])]
      .filter((element) => !(element as HTMLButtonElement).disabled && element.tabIndex >= 0 && element.getClientRects().length > 0 && element.closest('[role="dialog"]') === panel);
  }

  function tab(event: KeyboardEvent) {
    // Tab stays inside the dialog, as it does in the desktop's dialogs. The panel itself counts as outside the list.
    const list = focusable();
    const to = wrapTarget(list, list.indexOf(document.activeElement as HTMLElement), event.shiftKey);
    if (to) {
      event.preventDefault();
      to.focus();
    } else if (list.length === 0) {
      event.preventDefault();
      panel?.focus();
    }
  }

  function keydown(event: KeyboardEvent) {
    if ((event.target as Element | null)?.closest?.('[role="dialog"]') !== panel) return;
    if (event.key === 'Escape') {
      event.stopPropagation();
      event.preventDefault();
      onclose();
    } else if (event.key === 'Tab') {
      tab(event);
    }
  }

  /** A key that reaches the page with nothing focused: the top dialog still answers Escape and Tab. */
  function pageKey(event: KeyboardEvent) {
    if (!panel || event.defaultPrevented) return;
    const target = event.target as Element | null;
    if (target && target !== document.body && target !== document.documentElement) return;
    if (!stack.isTop(myId)) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      onclose();
    } else if (event.key === 'Tab') {
      tab(event);
    }
  }

  function focusin(event: FocusEvent) {
    const target = event.target as HTMLElement;
    if (target !== panel && target.closest('[role="dialog"]') === panel) lastFocused = target;
  }

  /** A click on the dark backdrop leaves the focus where it was. */
  function backdrop(event: MouseEvent) {
    if (event.target === event.currentTarget) event.preventDefault();
  }
</script>

<svelte:window onkeydown={pageKey} />

<div class="fixed inset-0 z-40 flex items-stretch justify-center bg-black/45 sm:items-center sm:p-6" transition:fade={{ duration: 120 }} role="presentation" onkeydown={keydown} onmousedown={backdrop}>
  <div
    bind:this={panel}
    class="flex max-h-dvh w-full flex-col bg-surface shadow-2xl outline-none sm:max-h-[88vh] sm:rounded-lg sm:border sm:border-divider {wide ? 'sm:max-w-3xl' : 'sm:max-w-lg'}"
    role="dialog"
    aria-modal="true"
    aria-label={title}
    tabindex="-1"
    onfocusin={focusin}
    transition:scale={{ start: 0.97, duration: 140 }}
  >
    <header class="flex items-center gap-2 border-b border-divider bg-chrome px-4 py-2.5 sm:rounded-t-lg">
      <h2 class="flex-1 truncate text-[14px] font-semibold">{title}</h2>
      <button class="dialog-close rounded p-1 text-muted hover:bg-hover" data-close onclick={onclose} aria-label="Close"><Icon name="close" size={16} /></button>
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

<style>
  /* A phone: the close button is a full finger's width (U-WU-8). */
  @media (pointer: coarse) { .dialog-close { min-width: 40px; min-height: 40px; display: inline-flex; align-items: center; justify-content: center; } }
</style>
