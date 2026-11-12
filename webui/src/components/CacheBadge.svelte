<script lang="ts">
  import type { CacheBadge } from '../lib/torrentIntake';

  // T9 W3: the provider's cache answer as a badge (the desktop's CacheBadgeView): cached is a filled accent badge with a
  // short line under it, not cached and checking a neutral outline, couldn't check a muted one with the reason as its tip.
  let { badge, align = 'start' }: { badge: CacheBadge; align?: 'start' | 'end' } = $props();

  const look = $derived(
    badge.kind === 'cached' ? 'border-accent bg-accent text-accent-text font-semibold'
    : badge.kind === 'unknown' ? 'border-divider text-muted'
    : 'border-status-neutral text-status-neutral',
  );
</script>

<div class="flex flex-col gap-0.5 {align === 'end' ? 'items-end text-right' : 'items-start'}" role="status" aria-live="polite"
  aria-label={[badge.text, badge.note, badge.tip ?? ''].filter((part) => part).join('. ')}>
  <span class="inline-block whitespace-nowrap rounded-[10px] border px-2 text-[12px] leading-[18px] {look}" title={badge.tip ?? undefined}>{badge.text}</span>
  {#if badge.note}<span class="max-w-56 text-[12px] text-muted">{badge.note}</span>{/if}
</div>
