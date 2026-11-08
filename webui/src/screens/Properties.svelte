<script lang="ts">
  import { untrack } from 'svelte';
  import Dialog from '../components/Dialog.svelte';
  import { bytes, dateTime, stateWord } from '../lib/format';
  import {
    addressLabel, canForce, categoryChoices, categoryOf, completionFrom, hasOwnAddress, programModeOf, shownAddress, systemActions,
    type ProgramMode,
  } from '../lib/details';
  import { store } from '../lib/store.svelte';
  import type { Job } from '../lib/types';

  // Download properties (D7, D8): what the download is, its description and category, and what happens when it completes.
  let { job, onclose }: { job: Job; onclose: () => void } = $props();

  const settings = $derived(store.model.settings);
  const choices = $derived(categoryChoices(settings?.customCategories as { id: string; name: string }[] | undefined));
  // The boxes start from the download as it is when the dialog opens.
  const first = untrack(() => job);
  const startCategory = categoryOf(first);
  // The program path is a path on the server, and only the Windows app can run one (the server refuses it otherwise).
  const programAllowed = store.has('tray');
  const finished = first.state === 'completed';
  const packageName = $derived(store.model.groups.find((group) => group.jobIds.includes(job.id))?.name);
  const queue = $derived(job.queueName || 'None');

  let description = $state(first.description ?? '');
  let category = $state(startCategory);
  let mode = $state<ProgramMode>(programModeOf(first.completionOptions));
  let programPath = $state(first.completionOptions?.programPath ?? '');
  let openFile = $state(first.completionOptions?.openFile ?? false);
  let systemAction = $state(first.completionOptions?.systemAction || 'none');
  let force = $state(first.completionOptions?.forceSystemAction ?? false);
  let error = $state('');
  let busy = $state(false);

  const info = $derived(
    [
      ['File', job.fileName],
      ['Status', stateWord(job.state)],
      ['Note', job.statusNote],
      ['Size', bytes(job.totalBytes ?? job.receivedBytes)],
      ['Queue', queue],
      ['Source', job.isRemote ? job.remoteProvider : job.source],
      ['Package', job.isRemote ? packageName : undefined],
      [addressLabel(job), hasOwnAddress(job) ? shownAddress(job) : undefined],
      ['Saved to', job.finalFilePath],
      ['Added', dateTime(job.createdAt)],
    ].filter(([, value]) => value && String(value).trim()) as [string, string][],
  );

  async function save() {
    error = '';
    const payload: Record<string, unknown> = { jobId: job.id, description };
    if (category !== startCategory) payload.categoryId = category;
    if (!finished) {
      const built = completionFrom(mode, programPath, openFile, systemAction, force, !!settings?.allowSystemCompletionActions);
      if (!built.ok) {
        error = built.error;
        return;
      }
      payload.completionOptions = built.options;
    }
    busy = true;
    try {
      await store.ask('job.update', payload);
      store.toast('The properties were saved.');
      onclose();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog title="Download properties" {onclose}>
  <form id="properties-form" class="grid gap-4" onsubmit={(event) => { event.preventDefault(); void save(); }}>
    <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-[13px]">
      {#each info as [label, value] (label)}
        <dt class="text-muted">{label}</dt><dd class="min-w-0 select-text break-all">{value}</dd>
      {/each}
    </dl>

    <div>
      <label class="label" for="prop-description">Description</label>
      <textarea id="prop-description" class="field h-20" bind:value={description}></textarea>
    </div>

    <div>
      <label class="label" for="prop-category">Category</label>
      <select id="prop-category" class="field" bind:value={category}>
        {#each choices as choice (choice.id)}<option value={choice.id}>{choice.label}</option>{/each}
      </select>
    </div>

    <fieldset class="grid gap-3" disabled={finished}>
      <legend class="label">When this download completes</legend>
      {#if programAllowed}
        <select class="field" aria-label="When this download completes" bind:value={mode}>
          <option value="global">Use global program</option>
          <option value="none">No completion program</option>
          <option value="run">Run a program</option>
        </select>
        <input class="field font-mono" aria-label="Program" bind:value={programPath} disabled={mode !== 'run' || finished} placeholder="Path of an .exe on the server" autocomplete="off" spellcheck="false" />
        <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={openFile} /> Open the downloaded file when complete</label>
      {/if}
      <div>
        <label class="label" for="prop-system">System action</label>
        <select id="prop-system" class="field" bind:value={systemAction}>
          {#each systemActions as action (action.id)}<option value={action.id}>{action.label}</option>{/each}
        </select>
        <p class="hint mt-1">{settings?.allowSystemCompletionActions ? 'A system action runs only when no other downloads are unfinished.' : 'Enable system completion actions in Scheduler settings to use this option.'}</p>
      </div>
      <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={force} disabled={!canForce(systemAction) || finished} /> Force the system action</label>
    </fieldset>
    {#if finished}<p class="hint -mt-2">This download has finished, so what happens on completion can't change.</p>{/if}
    {#if error}<p class="text-[12px] text-error" role="alert">{error}</p>{/if}
  </form>
  {#snippet footer()}
    <button class="btn" type="button" onclick={onclose}>Cancel</button>
    <button class="btn-primary" type="submit" form="properties-form" disabled={busy}>Save</button>
  {/snippet}
</Dialog>
