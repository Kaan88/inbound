<script lang="ts">
  import Dialog from './Dialog.svelte';
  import { displayDate, elsewhereText, releaseNotesUrl, type WhatsNewView } from '../lib/whatsNew';

  // UPD.N1: What's new in this server's version. Full screen on a phone (the Dialog does that), a panel otherwise.
  // The notes are text from the server and are shown only as text.
  let { version, subtitle, view, onclose }: { version: string; subtitle: string; view: WhatsNewView; onclose: () => void } = $props();

  const several = $derived(view.versions.length > 1);
  const folded = $derived(elsewhereText(view.elsewhere));
</script>

<Dialog title="What's new in {version}" {onclose}>
  {#if subtitle}<p class="mb-3 text-[12.5px] text-muted">{subtitle}</p>{/if}
  {#each view.versions as shown, index (shown.version)}
    {#if several}
      <h3 class="text-[15px] font-semibold {index > 0 ? 'mt-5' : ''}">{shown.version}{#if displayDate(shown.date)}<span class="font-normal text-muted"> · {displayDate(shown.date)}</span>{/if}</h3>
    {/if}
    {#each shown.sections as section, sectionIndex (section.title)}
      <h4 class="text-[13px] font-semibold {sectionIndex > 0 || several ? 'mt-3' : ''}">{section.title}</h4>
      <ul class="mt-1 list-disc space-y-1 pl-5 text-[13px]">
        {#each section.lines as line, lineIndex (lineIndex)}<li>{line}</li>{/each}
      </ul>
    {/each}
  {/each}
  {#if view.elsewhere.length}
    <details class="mt-4 rounded border border-divider px-3 py-2 text-[13px]">
      <summary class="cursor-pointer select-none">{folded}</summary>
      <ul class="mt-2 space-y-1">
        {#each view.elsewhere as line, lineIndex (lineIndex)}<li><span class="text-muted">{line.place} · </span>{line.text}</li>{/each}
      </ul>
    </details>
  {/if}
  {#snippet footer()}
    <a class="mr-auto text-[13px] text-accent hover:underline" href={releaseNotesUrl(version)} target="_blank" rel="noopener noreferrer">Full release notes</a>
    <button class="btn-primary max-sm:h-[48px] max-sm:w-full" data-autofocus onclick={onclose}>Got it</button>
  {/snippet}
</Dialog>
