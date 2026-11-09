<script lang="ts" module>
  // The glyphs live in lib/icons.ts, shared with the browser extension.
  import { iconPaths, type IconName } from '../lib/icons';
  const paths = iconPaths;
  export type { IconName };
</script>

<script lang="ts">
  let { name, size = 18, class: className = '', title }: { name: string; size?: number; class?: string; title?: string } = $props();
</script>

<svg
  class={className}
  width={size}
  height={size}
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.75"
  stroke-linecap="round"
  stroke-linejoin="round"
  aria-hidden={title ? undefined : 'true'}
  role={title ? 'img' : undefined}
>
  {#if title}<title>{title}</title>{/if}
  <path d={paths[name] ?? paths.info} />
</svg>
