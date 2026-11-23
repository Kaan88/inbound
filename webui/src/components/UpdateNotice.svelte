<script lang="ts">
  import Icon from './Icon.svelte';

  // UPD.D1: the dismissible "update available" notice above the list. A rounded panel on desktop, a stacked one on a
  // phone (the WebPhone board): the bold lead and the sentence after it, then "What's new and how", "Not now" and ×.
  let {
    title,
    body,
    shortBody,
    onopen,
    onnotnow,
    ondismiss,
  }: { title: string; body: string; shortBody: string; onopen: () => void; onnotnow: () => void; ondismiss: () => void } = $props();
</script>

<section aria-label="Update available" class="mx-3 mb-1.5 mt-2.5 flex items-center gap-3 rounded-md border border-notice-border bg-notice px-3.5 py-2.5 max-sm:flex-col max-sm:items-stretch max-sm:gap-2.5 max-sm:px-3 max-sm:py-3">
  <span class="flex min-w-0 flex-1 items-start gap-2.5">
    <Icon name="download" size={18} class="mt-0.5 shrink-0 text-icon-info" />
    <p class="min-w-0 text-[13px] leading-snug">
      <strong>{title}</strong> <span class="text-muted"><span class="hidden sm:inline">{body}</span><span class="sm:hidden">{shortBody}</span></span>
    </p>
    <button class="btn-icon -mr-2 -mt-2 size-10 shrink-0 self-start text-muted sm:hidden" aria-label="Dismiss" onclick={ondismiss}><Icon name="close" size={14} /></button>
  </span>
  <span class="flex shrink-0 items-center gap-2 max-sm:pl-0">
    <button class="btn-primary h-8 px-3 max-sm:h-[44px] max-sm:flex-1" onclick={onopen}>What's new and how</button>
    <button class="btn h-8 border-notice-button-border bg-transparent px-3 max-sm:h-[44px]" onclick={onnotnow}>Not now</button>
    <button class="btn-icon size-8 shrink-0 text-muted max-sm:hidden" aria-label="Dismiss" onclick={ondismiss}><Icon name="close" size={14} /></button>
  </span>
</section>
