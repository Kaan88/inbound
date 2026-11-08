<script lang="ts">
  import { checkedState, choosableIds, setChecked, treeSummary, visibleNodes, type FileNode, type FileTree } from '../lib/remote';
  import { providerBytes } from '../lib/remote';
  import Icon from './Icon.svelte';

  // A provider item's files as folders, with a checkbox on each. A file that can't be chosen shows its note.
  let { tree, chosen = $bindable(new Set<string>()), disabled = false, label = 'Files' }: { tree: FileTree; chosen?: Set<string>; disabled?: boolean; label?: string } = $props();

  let collapsed = $state(new Set<string>());
  const nodes = $derived(visibleNodes(tree, collapsed));
  const summary = $derived(treeSummary(tree, chosen));

  function toggleOpen(node: FileNode) {
    const next = new Set(collapsed);
    if (next.has(node.key)) next.delete(node.key);
    else next.add(node.key);
    collapsed = next;
  }

  function toggleChecked(node: FileNode) {
    chosen = setChecked(node, chosen, checkedState(node, chosen) !== true);
  }

  function indeterminate(element: HTMLInputElement, value: boolean) {
    element.indeterminate = value;
    return { update: (next: boolean) => (element.indeterminate = next) };
  }
</script>

<div class="mb-1 flex flex-wrap items-center justify-between gap-2 text-[12px] text-muted">
  <span aria-live="polite">{summary}</span>
  <span class="flex gap-3">
    <button class="underline disabled:no-underline disabled:opacity-50" {disabled} onclick={() => (chosen = new Set(choosableIds(tree)))}>All</button>
    <button class="underline disabled:no-underline disabled:opacity-50" {disabled} onclick={() => (chosen = new Set())}>None</button>
  </span>
</div>
<ul class="card max-h-72 divide-y divide-divider overflow-y-auto text-[12.5px]" aria-label={label}>
  {#each nodes as node (node.key)}
    {@const state = checkedState(node, chosen)}
    <li class="flex items-center gap-2 py-1 pr-3 {node.choosable === 0 ? 'opacity-60' : ''}" style="padding-left: {8 + node.depth * 16}px">
      {#if node.isFolder}
        <button class="rounded p-0.5 text-muted hover:bg-hover" aria-label={collapsed.has(node.key) ? `Open ${node.name}` : `Close ${node.name}`} aria-expanded={!collapsed.has(node.key)} onclick={() => toggleOpen(node)}>
          <Icon name={collapsed.has(node.key) ? 'chevron-right' : 'chevron-down'} size={14} />
        </button>
      {:else}
        <span class="w-[22px] shrink-0"></span>
      {/if}
      <label class="flex min-w-0 flex-1 items-center gap-2">
        <input type="checkbox" disabled={disabled || node.choosable === 0} checked={state === true} use:indeterminate={state === null} onchange={() => toggleChecked(node)} />
        <span class="min-w-0 flex-1 truncate {node.isFolder ? 'font-semibold' : ''}" title={node.name}>{node.name}</span>
      </label>
      {#if node.note}<span class="max-w-[40%] truncate text-[11.5px] text-muted" title={node.note}>{node.note}</span>{/if}
      <span class="tabular shrink-0 text-muted">{providerBytes(node.size)}</span>
    </li>
  {/each}
</ul>
