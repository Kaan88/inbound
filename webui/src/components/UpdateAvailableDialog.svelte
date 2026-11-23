<script lang="ts">
  import Dialog from './Dialog.svelte';
  import { copyText } from '../lib/clipboard';
  import { displayDate } from '../lib/whatsNew';
  import { composeCommand, runCommand, dialogSubtitle, tagNote, windowsHowTo, type UpdateKind } from '../lib/updateNotice';

  // UPD.D1: the update dialog the notice bar opens. Docker: the changelog lines for this server, then the compose and
  // docker run commands with a Copy each and a Docker guide link. A normal install (the Windows bridge): the same notes,
  // but "how to update" points at the app's own Check for Updates. "Skip this version" hides the notice until a newer one.
  let {
    version,
    current,
    kind,
    here,
    elsewhere,
    releaseDate,
    onskip,
    onclose,
  }: {
    version: string;
    current: string;
    kind: UpdateKind;
    here: string[];
    elsewhere: string;
    releaseDate: string | null;
    onskip: () => void;
    onclose: () => void;
  } = $props();

  const subtitle = $derived(`${dialogSubtitle(kind, current)}${displayDate(releaseDate) ? ` · released ${displayDate(releaseDate)}` : ''}`);
  const changesHeading = $derived(kind === 'windows' ? 'What changes for this PC' : 'What changes for this server');

  let copied = $state<'' | 'compose' | 'run'>('');
  async function copy(command: string, which: 'compose' | 'run') {
    if (await copyText(command)) {
      copied = which;
      setTimeout(() => (copied = ''), 1600);
    }
  }
</script>

<Dialog title="Inbound {version} is out" wide {onclose}>
  <p class="mb-3 text-[12.5px] text-muted">{subtitle}</p>

  {#if here.length}
    <h3 class="text-[13px] font-semibold text-text">{changesHeading}</h3>
    <ul class="mt-1 list-disc space-y-1 pl-5 text-[13px]">
      {#each here as line, index (index)}<li>{line}</li>{/each}
    </ul>
    {#if elsewhere}<p class="mt-1.5 text-[12px] text-muted">{elsewhere}</p>{/if}
  {/if}

  <h3 class="mt-4 text-[13px] font-semibold text-text">How to update</h3>
  {#if kind === 'windows'}
    <p class="mt-1 text-[13px] leading-snug">{windowsHowTo()}</p>
  {:else}
    <p class="mt-1 text-[13px] leading-snug">Your downloads and settings stay in the data volume. Running downloads stop and resume after the restart.</p>
    <div class="mt-2.5 flex flex-col gap-1.5">
      <div class="flex items-center gap-2 rounded border border-divider bg-chrome py-2 pl-3 pr-2">
        <span class="w-[92px] shrink-0 text-[12px] text-muted">Compose</span>
        <code class="min-w-0 flex-1 break-words font-mono text-[12.5px]">{composeCommand}</code>
        <button class="btn h-7 px-2.5 py-0 text-[12px]" onclick={() => copy(composeCommand, 'compose')}>{copied === 'compose' ? 'Copied' : 'Copy'}</button>
      </div>
      <div class="flex items-center gap-2 rounded border border-divider bg-chrome py-2 pl-3 pr-2">
        <span class="w-[92px] shrink-0 text-[12px] text-muted">docker run</span>
        <code class="min-w-0 flex-1 break-words font-mono text-[12.5px]">{runCommand}</code>
        <button class="btn h-7 px-2.5 py-0 text-[12px]" onclick={() => copy(runCommand, 'run')}>{copied === 'run' ? 'Copied' : 'Copy'}</button>
      </div>
    </div>
    <p class="mt-1.5 text-[12px] leading-snug text-muted">Then start the container again with the same options. {tagNote(current)} <a class="text-accent hover:underline" href="https://github.com/Kaan88/inbound/blob/main/docs/docker.md" target="_blank" rel="noopener noreferrer">Docker guide</a></p>
  {/if}

  {#snippet footer()}
    <!-- WebDialog board: Skip this version on the left, Close on the right. -->
    <button class="btn sm:mr-auto max-sm:h-[48px] max-sm:w-full" onclick={onskip}>Skip this version</button>
    <button class="btn-primary max-sm:h-[48px] max-sm:w-full" data-autofocus onclick={onclose}>Close</button>
  {/snippet}
</Dialog>
