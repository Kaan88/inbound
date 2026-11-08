<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import FolderField from '../components/FolderField.svelte';
  import { bytes } from '../lib/format';
  import { containerAddSummary, containerChoice, containerEntryName } from '../lib/remote';
  import { store } from '../lib/store.svelte';
  import type { ContainerAddResult, ContainerPreview } from '../lib/types';

  // Import container file: an .rsdf, .ccf or .dlc file from this device is uploaded to the server
  // (/api/upload/container), which reads it and shows what it holds without its links (remote.container.preview).
  // The chosen entries go to the provider like pasted links (remote.container.add).
  let { onclose }: { onclose: () => void } = $props();

  let step = $state<'pick' | 'reading' | 'choose' | 'adding'>('pick');
  let problem = $state('');
  let preview = $state<ContainerPreview | null>(null);
  let chosen = $state<number[]>([]);
  let destination = $state(store.model.settings?.defaultDownloadDirectory ?? '');
  let problems = $state<string[]>([]);
  const providerName = $derived(store.model.accounts[0]?.providerName ?? 'the provider');

  async function picked(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    problem = '';
    if (!/\.(rsdf|ccf|dlc)$/i.test(file.name)) { problem = 'Only .rsdf, .ccf and .dlc files can be imported.'; return; }
    step = 'reading';
    try {
      const response = await fetch(`/api/upload/container?name=${encodeURIComponent(file.name)}`, {
        method: 'POST', body: file, credentials: 'same-origin', headers: { 'Content-Type': 'application/octet-stream' },
      });
      // A reference to the uploaded file (upload:…), not its path on the server.
      const body = (await response.json()) as { ok: boolean; upload?: string; error?: string };
      if (!body.ok || !body.upload) throw new Error(body.error ?? 'The upload failed.');
      const result = await store.raw<ContainerPreview>('remote.container.preview', { path: body.upload, fileName: file.name });
      const read = result.payload as ContainerPreview;
      if (!read.previewId) {
        problem = read.error || read.accountProblem || 'Nothing in this file can be added.';
        problems = read.problems ?? [];
        step = 'pick';
        return;
      }
      preview = read;
      chosen = containerChoice(read);
      problems = read.problems ?? [];
      step = 'choose';
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
      step = 'pick';
    }
  }

  function toggle(entryId: number, on: boolean) {
    chosen = on ? [...chosen, entryId] : chosen.filter((id) => id !== entryId);
  }

  async function add() {
    if (!preview?.previewId || chosen.length === 0) return;
    step = 'adding';
    problem = '';
    try {
      const reply = await store.raw<ContainerAddResult>('remote.container.add', {
        previewId: preview.previewId, entryIds: chosen, destination: destination.trim() || null, startMode: 'startNow',
      });
      const result = reply.payload as ContainerAddResult;
      const summary = containerAddSummary(result, preview, providerName);
      if (result.ok && summary.problems.length === 0) {
        store.toast(summary.text);
        onclose();
        return;
      }
      problem = summary.text;
      problems = summary.problems;
      // What was added is done; what wasn't stays chosen, to try again or leave.
      chosen = result.outcomes.filter((outcome) => !outcome.ok).map((outcome) => outcome.entryId);
      step = 'choose';
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
      step = 'choose';
    }
  }

  const packageName = (index: number | undefined) => (index === undefined || index === null ? null : preview?.packages.find((p) => p.index === index)?.name ?? null);
</script>

<Dialog title="Import container file" wide {onclose}>
  {#if step === 'pick' || step === 'reading'}
    <label class="label" for="container-file">Container file (.rsdf, .ccf or .dlc)</label>
    <input id="container-file" type="file" accept=".rsdf,.ccf,.dlc" class="field" disabled={step === 'reading'} onchange={picked} />
    <p class="hint mt-1">The file is sent to the server, which reads it. Its links are never shown, and they go to {providerName}.</p>
    {#if step === 'reading'}<p class="mt-2 text-[13px]" role="status">Reading the file…</p>{/if}
  {:else if preview}
    {#if preview.accountProblem}<p class="mb-2 text-[12.5px] text-error" role="alert">{preview.accountProblem}</p>{/if}
    <p class="label" id="container-entries">Choose what to add ({chosen.length} of {preview.entries.length})</p>
    <ul class="mb-3 max-h-72 overflow-y-auto rounded border border-divider" aria-labelledby="container-entries">
      {#each preview.entries as entry (entry.entryId)}
        <li class="flex items-start gap-2 border-b border-divider px-2 py-1.5 text-[13px] last:border-b-0">
          <input type="checkbox" class="mt-0.5" id={`entry-${entry.entryId}`} checked={chosen.includes(entry.entryId)} disabled={!entry.canAdd || step === 'adding'}
            onchange={(event) => toggle(entry.entryId, event.currentTarget.checked)} />
          <label for={`entry-${entry.entryId}`} class="min-w-0 flex-1">
            <span class="block truncate">{containerEntryName(entry)}{#if entry.size}<span class="tabular text-muted"> · {bytes(entry.size)}</span>{/if}</span>
            {#if packageName(entry.packageIndex)}<span class="block truncate text-[12px] text-muted">{packageName(entry.packageIndex)}</span>{/if}
            {#if entry.reason}<span class="block text-[12px] {entry.canAdd ? 'text-muted' : 'text-error'}">{entry.reason}</span>{/if}
          </label>
        </li>
      {/each}
    </ul>
    <FolderField id="container-destination" label="Save to" bind:value={destination} disabled={step === 'adding'} />
  {/if}
  {#if problem}<p class="mt-2 text-[12.5px] text-error" role="alert">{problem}</p>{/if}
  {#each problems as line}<p class="mt-1 text-[12px] text-muted">{line}</p>{/each}
  {#snippet footer()}
    <button class="btn" onclick={onclose}>{step === 'choose' || step === 'adding' ? 'Cancel' : 'Close'}</button>
    {#if preview && (step === 'choose' || step === 'adding')}
      <button class="btn-primary" disabled={step === 'adding' || chosen.length === 0} onclick={add}>Add {chosen.length === 1 ? '1 link' : `${chosen.length} links`}</button>
    {/if}
  {/snippet}
</Dialog>
