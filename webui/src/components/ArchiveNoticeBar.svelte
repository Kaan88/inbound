<script lang="ts">
  import { fade } from 'svelte/transition';
  import { noticeBoard as board, noticeWatchingSince as watchingSince, noticeTarget, type ArchiveNotice, type ArchivePasswordTarget } from '../lib/archives';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet } from '../lib/types';
  import Icon from './Icon.svelte';

  // One line above the list: an archive waiting for its password (oldest first), or a finished extraction. It
  // waits for the user, never takes the focus, and can be dismissed until the set has news.
  let { onpassword, onshow }: { onpassword: (target: ArchivePasswordTarget) => void; onshow: (set: ArchiveSet) => void } = $props();

  let dismissals = $state(0);

  const sets = $derived(Object.values(store.model.archives));
  const notice = $derived.by<ArchiveNotice | null>(() => {
    dismissals;
    return board.update(sets, watchingSince, store.model.settings?.notifyOnCompletion !== false);
  });

  function act() {
    if (!notice) return;
    const set = store.model.archives[notice.setId];
    if (!set) return;
    if (notice.kind === 'password') {
      const group = store.model.groups.find((candidate) => candidate.id === set.packageId);
      onpassword(noticeTarget(set, sets, group?.name));
      return;
    }
    board.dismiss(notice, sets);
    dismissals++;
    onshow(set);
  }

  function dismiss() {
    if (!notice) return;
    board.dismiss(notice, sets);
    dismissals++;
  }
</script>

{#if notice}
  <div class="flex flex-wrap items-center gap-2 border-b border-divider bg-highlight px-3 py-1.5 text-[13px]" role="status" transition:fade={{ duration: 150 }}>
    <Icon name={notice.kind === 'password' ? 'key' : 'archive'} size={16} class={notice.kind === 'password' ? 'text-icon-pause' : 'text-icon-media'} />
    <span class="min-w-0 flex-1">{notice.text}</span>
    <button class="btn py-0.5" onclick={act}>{notice.actionText}</button>
    <button class="rounded p-1 text-muted hover:bg-hover" aria-label="Dismiss" onclick={dismiss}><Icon name="close" size={14} /></button>
  </div>
{/if}
