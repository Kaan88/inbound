<script lang="ts">
  import { extensionLinks, extensionLinksHint } from '../lib/extensionLinks';

  // UI.R41: the desktop's Options > General > Browser extension. No per-browser status (see extensionLinks.ts); paired
  // is the one line about the paired extensions, or null.
  let { hostKind, paired = null }: { hostKind: string | null | undefined; paired?: string | null } = $props();
</script>

<div class="grid gap-2">
  <p class="hint">{extensionLinksHint(hostKind)}</p>
  {#if paired}<p class="text-[13px]" role="status">{paired}</p>{/if}
  <ul class="card divide-y divide-divider" aria-label="Get the browser extension">
    {#each extensionLinks as link (link.browser)}
      <li class="flex items-center gap-3 px-3 py-2 text-[13px]">
        <span class="min-w-0 flex-1"><span class="block font-semibold">{link.name}</span>
          <span class="block text-[12px] text-muted">{link.from}</span></span>
        <a class="btn shrink-0" href={link.url} target="_blank" rel="noopener noreferrer" title={link.url}
          aria-label="Get the extension for {link.name}">Get it</a>
      </li>
    {/each}
  </ul>
</div>
