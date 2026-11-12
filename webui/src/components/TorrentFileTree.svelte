<script lang="ts">
  import Icon from './Icon.svelte';
  import { providerBytes, visibleNodes, type FileNode, type FileTree } from '../lib/remote';
  import { nodePriority, nodeWanted, priorityChoices, priorityLabel, setAllWanted, setNodePriority, setNodeWanted, type FilePriority, type PriorityMap } from '../lib/torrentControls';

  // A torrent's files as folders (T3, mockups b and c): a tick box and a priority menu on each file and folder. A folder's
  // menu sets every file under it; "Mixed" shows when they differ. Without priorities (an older server) only the boxes show.
  let {
    tree, priorities, onchange, showPriority = true, progress, disabled = false, label = 'Files',
  }: {
    tree: FileTree;
    priorities: PriorityMap;
    onchange: (next: Map<string, FilePriority>, node: FileNode | null) => void;
    showPriority?: boolean;
    /** How far a file is ("71%", "Done"), when that is shown. */
    progress?: (node: FileNode) => string;
    disabled?: boolean;
    label?: string;
  } = $props();

  let collapsed = $state(new Set<string>());
  const nodes = $derived(visibleNodes(tree, collapsed));

  function toggleOpen(node: FileNode) {
    const next = new Set(collapsed);
    if (next.has(node.key)) next.delete(node.key);
    else next.add(node.key);
    collapsed = next;
  }

  function indeterminate(element: HTMLInputElement, value: boolean) {
    element.indeterminate = value;
    return { update: (next: boolean) => (element.indeterminate = next) };
  }

  // The box and the menu show what the parent holds: they go back at once and change when it takes the change (the
  // details send it to the server first, which may refuse it).
  function choose(node: FileNode, box: HTMLSelectElement) {
    const priority = priorityChoices.find(([key]) => key === box.value)?.[0];
    box.value = nodePriority(node, priorities);
    if (priority) onchange(setNodePriority(node, priorities, priority), node);
  }

  function tick(node: FileNode, box: HTMLInputElement) {
    const want = box.checked;
    const before = nodeWanted(node, priorities);
    box.checked = before === true;
    box.indeterminate = before === null;
    onchange(setNodeWanted(node, priorities, want), node);
  }
</script>

<div class="mb-1 flex flex-wrap items-center justify-end gap-3 text-[12px] text-muted">
  <button class="underline disabled:no-underline disabled:opacity-50" {disabled} onclick={() => onchange(setAllWanted(tree, priorities, true), null)}>All</button>
  <button class="underline disabled:no-underline disabled:opacity-50" {disabled} onclick={() => onchange(setAllWanted(tree, priorities, false), null)}>None</button>
</div>
<ul class="card max-h-72 divide-y divide-divider overflow-y-auto text-[12.5px]" aria-label={label}>
  {#each nodes as node (node.key)}
    {@const wanted = nodeWanted(node, priorities)}
    {@const priority = nodePriority(node, priorities)}
    {@const shown = progress && !node.isFolder ? progress(node) : ''}
    <li class="flex items-center gap-2 py-1 pr-2" style="padding-left: {6 + node.depth * 14}px">
      {#if node.isFolder}
        <button class="rounded p-0.5 text-muted hover:bg-hover" aria-label={collapsed.has(node.key) ? `Open ${node.name}` : `Close ${node.name}`} aria-expanded={!collapsed.has(node.key)} onclick={() => toggleOpen(node)}>
          <Icon name={collapsed.has(node.key) ? 'chevron-right' : 'chevron-down'} size={14} />
        </button>
      {:else}
        <span class="w-[22px] shrink-0"></span>
      {/if}
      <label class="flex min-w-0 flex-1 items-center gap-2">
        <input type="checkbox" {disabled} checked={wanted === true} use:indeterminate={wanted === null}
          onchange={(event) => tick(node, event.currentTarget)} />
        <span class="min-w-0 flex-1">
          <span class="block truncate {node.isFolder ? 'font-semibold' : ''} {wanted === false ? 'text-muted' : ''}" title={node.name}>{node.name}</span>
          <!-- On a phone the progress column has no room, so how far the file is shows under its name. -->
          {#if shown}<span class="tabular block text-[11.5px] sm:hidden {shown === 'Done' ? 'tone-done' : 'text-muted'}">{shown}</span>{/if}
        </span>
      </label>
      <span class="tabular shrink-0 text-muted">{providerBytes(node.size)}</span>
      {#if progress}
        <span class="tabular hidden w-12 shrink-0 text-right sm:inline {shown === 'Done' ? 'tone-done' : 'text-muted'}">{shown}</span>
      {/if}
      {#if showPriority}
        <select class="field w-[84px] shrink-0 px-1 py-0 text-[12px]" {disabled} aria-label="Priority of {node.name}" value={priority}
          onchange={(event) => choose(node, event.currentTarget)}>
          {#if priority === 'mixed'}<option value="mixed" disabled>{priorityLabel('mixed')}</option>{/if}
          {#each priorityChoices as [key, text] (key)}<option value={key}>{text}</option>{/each}
        </select>
      {/if}
    </li>
  {/each}
</ul>
