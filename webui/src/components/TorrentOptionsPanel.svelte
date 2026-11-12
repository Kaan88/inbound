<script lang="ts">
  import { untrack } from 'svelte';
  import { store } from '../lib/store.svelte';
  import { draftFrom, globalGoalText, optionsRequest, optionsSaid, type OptionsDraft, type TorrentOptions } from '../lib/torrentControls';
  import type { Job } from '../lib/types';

  // A torrent's own Options tab (T3, mockup c): download and upload limits, when seeding stops, and download order. Read
  // with torrent.options when the tab opens and saved with torrent.setOptions on Apply; a running torrent takes them at once.
  let { job }: { job: Job } = $props();

  let draft = $state<OptionsDraft | null>(null);
  let problem = $state('');
  let field = $state('');
  let notice = $state('');
  let saving = $state(false);
  const connected = $derived(store.connection === 'open');
  const globalGoal = $derived(globalGoalText(store.model.settings));

  async function read() {
    problem = '';
    try {
      const result = await store.ask<{ options?: TorrentOptions }>('torrent.options', { jobId: job.id });
      draft = draftFrom(result.options);
    } catch (error) {
      problem = `The options could not be read. ${error instanceof Error ? error.message : String(error)}`;
    }
  }

  $effect(() => {
    if (connected) untrack(() => { if (!draft) void read(); });
  });

  async function apply() {
    if (!draft) return;
    const checked = optionsRequest(job.id, draft);
    notice = '';
    if (!checked.ok) {
      problem = checked.error;
      field = checked.field;
      document.getElementById(checked.field)?.focus();
      return;
    }
    problem = '';
    field = '';
    saving = true;
    try {
      const result = await store.ask<{ ok: boolean; error?: string; options?: TorrentOptions; running?: boolean }>('torrent.setOptions', checked.request);
      draft = draftFrom(result.options ?? checked.request);
      notice = optionsSaid(result.running === true, result.error);
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
    } finally {
      saving = false;
    }
  }
</script>

{#if draft}
  <form class="grid gap-3 text-[13px]" onsubmit={(event) => { event.preventDefault(); void apply(); }}>
    <fieldset class="grid gap-2" disabled={saving || !connected}>
      <legend class="mb-1 font-semibold">Speed limits for this torrent</legend>
      <div class="flex flex-wrap items-center gap-2">
        <label class="flex w-32 items-center gap-2"><input type="checkbox" bind:checked={draft.limitDown} /> Download</label>
        <input id="opt-down" class="field w-28" type="number" min="1" step="1" aria-label="Download limit in KB/s" disabled={!draft.limitDown} bind:value={draft.downKb} aria-invalid={field === 'opt-down'} />
        <span class="text-muted">KB/s</span>
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <label class="flex w-32 items-center gap-2"><input type="checkbox" bind:checked={draft.limitUp} /> Upload</label>
        <input id="opt-up" class="field w-28" type="number" min="1" step="1" aria-label="Upload limit in KB/s" disabled={!draft.limitUp} bind:value={draft.upKb} aria-invalid={field === 'opt-up'} />
        <span class="text-muted">KB/s</span>
      </div>
      <p class="hint">The torrent limits in Options › Torrents and the total speed limit in Options › Connection still apply.</p>
    </fieldset>

    <fieldset class="grid gap-1.5" disabled={saving || !connected}>
      <legend class="mb-1 font-semibold">Stop seeding at</legend>
      <label class="flex items-start gap-2"><input type="radio" class="mt-0.5" name="opt-goal" value="global" bind:group={draft.goal} /> <span>The global goal <span class="text-muted">({globalGoal})</span></span></label>
      <div class="flex flex-wrap items-center gap-2">
        <label class="flex items-center gap-2"><input type="radio" name="opt-goal" value="ratio" bind:group={draft.goal} /> Ratio</label>
        <input id="opt-ratio" class="field w-24" type="number" min="0.01" max="100" step="0.01" aria-label="Seeding ratio" disabled={draft.goal !== 'ratio'} bind:value={draft.ratio} aria-invalid={field === 'opt-ratio'} />
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <label class="flex items-center gap-2"><input type="radio" name="opt-goal" value="hours" bind:group={draft.goal} /> Time</label>
        <input id="opt-hours" class="field w-24" type="number" min="0.1" max="8760" step="0.1" aria-label="Seeding hours" disabled={draft.goal !== 'hours'} bind:value={draft.hours} aria-invalid={field === 'opt-hours'} />
        <span class="text-muted">hours</span>
      </div>
      <label class="flex items-center gap-2"><input type="radio" name="opt-goal" value="never" bind:group={draft.goal} /> Never seed (stop as soon as the download finishes)</label>
    </fieldset>

    <fieldset class="grid gap-1" disabled={saving || !connected}>
      <legend class="mb-1 font-semibold">Download order</legend>
      <label class="flex items-start gap-2"><input type="checkbox" class="mt-0.5" bind:checked={draft.sequential} /> <span>Download in order <span class="text-muted">(for watching while it downloads, instead of the rarest pieces first)</span></span></label>
    </fieldset>

    <div class="flex flex-wrap items-center gap-3">
      <button class="btn-primary" type="submit" disabled={saving || !connected}>{saving ? 'Applying…' : 'Apply'}</button>
      {#if notice}<span class="text-muted" role="status">{notice}</span>{/if}
    </div>
  </form>
{:else if !problem}
  <p class="text-[13px] text-muted">Reading…</p>
{/if}
{#if problem}<p class="mt-2 text-[12.5px] text-error" role="alert">{problem}</p>{/if}
