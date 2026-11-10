<script lang="ts">
  import { onMount } from 'svelte';
  import { packagePasswordHint, showsArchiveSection } from '../lib/details';
  import { archiveLabel, fullWithNote } from '../lib/archives';
  import { store } from '../lib/store.svelte';
  import type { Job } from '../lib/types';

  // The archive password for what a package brings (D16): for extracting its archives, not for the download. It can be
  // set before the files have downloaded. Shown by the PackageArchiveSection rule.
  let { groupId, members }: { groupId: string; members: Job[] } = $props();

  let password = $state('');
  let onRecord = $state(false);
  let savedHere = $state(false);
  let actedHere = $state(false);
  let status = $state('');
  let problem = $state(false);
  let showingSaved = $state(false);

  onMount(() => {
    void (async () => {
      try {
        const reply = await store.ask<{ packagesWithPassword?: string[] }>('archive.list', {});
        onRecord = (reply.packagesWithPassword ?? []).includes(groupId);
      } catch {
        // The next change of the package asks again.
      }
    })();
  });

  const sets = $derived(
    Object.values(store.model.archives)
      .filter((set) => set.packageId === groupId || set.jobIds.some((id) => members.some((m) => m.id === id)))
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' })),
  );
  const visible = $derived(showsArchiveSection(members.map((m) => m.fileName), sets.length, savedHere || onRecord || sets.some((s) => s.hasPassword)) || actedHere);
  const canClear = $derived(savedHere || onRecord || sets.length === 0 || sets.some((s) => s.hasPassword));
  const asking = $derived(sets.filter((s) => s.needsPassword));
  const wrong = $derived(asking.some((s) => s.failureKind === 'wrongPassword'));
  const lines = $derived.by(() => {
    const out = sets.slice(0, 6).map((set) => `${set.name}: ${fullWithNote(set) || archiveLabel(set).text}`);
    if (sets.length > 6) out.push(`and ${sets.length - 6} more`);
    if (asking.length > 0) {
      out.unshift(wrong
        ? 'That password was wrong. The downloads are fine. Enter the right one above, and extraction runs again.'
        : 'An archive here needs its password. The downloads are fine. Enter it above, and extraction runs.');
    }
    return out;
  });

  // What was just saved has been tried and has failed, so it no longer gets the last word.
  $effect(() => {
    if (asking.length > 0 && showingSaved) {
      showingSaved = false;
      status = '';
    }
  });

  async function send(value: string): Promise<string | null> {
    try {
      await store.ask('archive.setPassword', { packageId: groupId, password: value });
      return null;
    } catch (error) {
      return error instanceof Error ? error.message : String(error);
    }
  }

  async function save() {
    const value = password;
    // Out of the box before anything is sent, whatever the answer turns out to be.
    password = '';
    if (!value) return;
    showingSaved = false;
    actedHere = true;
    problem = false;
    status = 'Saving the archive password…';
    const error = await send(value);
    if (error === null) {
      savedHere = true;
      status = "Archive password saved. It is used when this package's archives are extracted.";
      showingSaved = true;
    } else {
      problem = true;
      status = `The archive password wasn't saved. ${error}`;
    }
  }

  async function clear() {
    showingSaved = false;
    actedHere = true;
    problem = false;
    status = 'Clearing the archive password…';
    const error = await send('');
    if (error === null) {
      savedHere = false;
      onRecord = false;
      status = 'Any archive password saved for this package is gone.';
    } else {
      problem = true;
      status = `The archive password wasn't cleared. ${error}`;
    }
  }
</script>

{#if visible}
  <form class="grid gap-2" onsubmit={(event) => { event.preventDefault(); void save(); }}>
    <label class="label" for="package-password">Archive password</label>
    <div class="flex flex-wrap gap-2">
      <input id="package-password" class="field min-w-0 flex-1" type="password" bind:value={password} autocomplete="off" spellcheck="false" aria-label="Archive password for this package. It isn't for the download." />
      <button class="btn shrink-0" type="submit" disabled={!password}>Save password</button>
      <button class="btn shrink-0" type="button" disabled={!canClear} onclick={clear}>Clear password</button>
    </div>
    <p class="hint">{packagePasswordHint}</p>
    {#if status}<p class="text-[12.5px] {problem ? 'text-error' : 'text-muted'}" role="status">{status}</p>{/if}
    {#if lines.length}
      <p class="whitespace-pre-line text-[12.5px] {wrong ? 'text-error' : 'text-muted'}" role="status">{lines.join('\n')}</p>
    {/if}
  </form>
{/if}
