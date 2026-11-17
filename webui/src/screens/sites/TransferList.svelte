<script lang="ts">
  import ContextMenu, { type MenuItem } from '../../components/ContextMenu.svelte';
  import Dialog from '../../components/Dialog.svelte';
  import { LongPress } from '../../lib/longpress';
  import Icon from '../../components/Icon.svelte';
  import ProgressBar from '../../components/ProgressBar.svelte';
  import { store } from '../../lib/store.svelte';
  import type { SiteTransfer } from '../../lib/sites';
  import {
    canClear, clearDoneTargets, commandLabel, commandType, description, emptyMessage, fullRoute, inTab, isDownload, menu as commandMenu,
    pauseAllAction, phoneButton, phoneHeader, phoneRows, phoneSheet, phoneSheetLabel, rateText, routeLine, retryFailedTargets, sizeText, spaceCommand, status, tabLabel, tabOf, timeLeft, title, valid,
    type TransferCommand, type TransferTab,
  } from '../../lib/sitesTransfers';

  // The transfers between remote files and this computer (SITES.11 R6b): the main download list's look (its columns, its
  // two-line rows on a phone, its status colours and bar), with a view model of its own. A row offers only what a transfer
  // supports (lib/sitesTransfers: menu), so none of the download list's commands leak in.
  let { transfers, onshow, tab = $bindable('active') }: { transfers: SiteTransfer[]; onshow: (siteId: string) => void; tab?: TransferTab } = $props();

  let selected = $state<Set<string>>(new Set());
  let menuAt = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);

  // A phone (R7, the mockup): one list without tabs or tick boxes, a header that counts, one button per card.
  let phone = $state(typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(max-width: 767px)').matches);
  $effect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const query = window.matchMedia('(max-width: 767px)');
    const changed = () => (phone = query.matches);
    query.addEventListener('change', changed);
    return () => query.removeEventListener('change', changed);
  });

  const rows = $derived(phone ? phoneRows(transfers) : inTab(transfers, tab));
  const chosen = $derived(rows.filter((row) => selected.has(row.id)));
  const clearable = $derived(clearDoneTargets(transfers));
  const retryable = $derived(retryFailedTargets(transfers));
  const pauseAll = $derived(pauseAllAction(transfers));

  // A row that left the tab leaves the selection.
  $effect(() => {
    const present = new Set(rows.map((row) => row.id));
    if ([...selected].some((id) => !present.has(id))) selected = new Set([...selected].filter((id) => present.has(id)));
  });

  async function send(type: string, id: string) {
    const result = await store.attempt<{ ok: boolean; message?: string }>(type, { id });
    if (result && !result.ok) store.toast(result.message ?? 'That did not work.', 'error');
  }

  async function run(command: TransferCommand, list: SiteTransfer[]) {
    if (command === 'showOnRemote') {
      if (list[0]) onshow(list[0].siteId);
      return;
    }
    const type = commandType(command);
    if (!type) return;
    for (const row of list.filter((item) => valid(command, item))) await send(type, row.id);
  }

  async function runAll(type: string, ids: string[]) {
    for (const id of ids) await send(type, id);
  }

  function toggle(row: SiteTransfer) {
    const next = new Set(selected);
    if (next.has(row.id)) next.delete(row.id);
    else next.add(row.id);
    selected = next;
  }

  function open(row: SiteTransfer | null, x: number, y: number) {
    const target = row && selected.has(row.id) ? chosen : row ? [row] : chosen;
    if (row && !selected.has(row.id)) selected = new Set([row.id]);
    const groups = commandMenu(target);
    if (!groups.length) return;
    const items: MenuItem[] = [];
    for (const group of groups) {
      if (items.length) items.push({ separator: true });
      for (const command of group) items.push({ label: commandLabel(command), onselect: () => void run(command, target), hint: command === 'stop' || command === 'resume' ? 'Space' : command === 'retry' ? 'F5' : command === 'remove' ? 'Delete' : undefined });
    }
    menuAt = { items, x, y };
  }

  // A phone: a long press on a card opens its actions as a bottom sheet (lib/sitesTransfers: phoneSheet).
  const longPress = new LongPress();
  let sheetFor = $state<SiteTransfer | null>(null);
  const sheetRow = $derived(sheetFor ? (transfers.find((row) => row.id === sheetFor!.id) ?? null) : null);

  function pointerdown(row: SiteTransfer, event: PointerEvent) {
    if (!phone) return;
    longPress.down(event.pointerType, event.clientX, event.clientY, () => { if (phoneSheet(row).length) sheetFor = row; });
  }

  function rowClick(row: SiteTransfer) {
    if (longPress.takeClick()) return;
    selected = new Set([row.id]);
  }

  function keydown(event: KeyboardEvent) {
    if (event.target instanceof HTMLInputElement || event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.key === ' ') {
      const command = spaceCommand(chosen);
      if (command) { event.preventDefault(); void run(command, chosen); }
    } else if (event.key === 'F5') {
      if (chosen.some((row) => valid('retry', row))) { event.preventDefault(); void run('retry', chosen); }
    } else if (event.key === 'Delete') {
      if (chosen.some((row) => valid('remove', row))) { event.preventDefault(); void run('remove', chosen); }
    } else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
      open(null, box.left + 40, box.top + 40);
    }
  }
</script>

<div class="flex min-h-0 flex-1 flex-col">
  <div class="flex flex-wrap items-center gap-2 px-3 py-2">
    <span class="text-[13px] text-muted md:hidden">{phoneHeader(transfers)}</span>
    <div class="segmented max-md:hidden" role="group" aria-label="Transfers">
      {#each ['active', 'failed', 'done'] as const as name (name)}
        <button aria-pressed={tab === name} onclick={() => (tab = name)}>{tabLabel(transfers, name)}</button>
      {/each}
    </div>
    <span class="flex-1"></span>
    {#if pauseAll}<button class="btn max-md:hidden" onclick={() => runAll(pauseAll.type, pauseAll.ids)}><Icon name={pauseAll.type.endsWith('pause') ? 'pause' : 'play'} size={15} /> {pauseAll.label}</button>{/if}
    {#if retryable.length}<button class="btn max-md:hidden" onclick={() => runAll('sites.transfer.retry', retryable)}><Icon name="retry" size={15} /> Retry failed</button>{/if}
    <button class="btn max-md:hidden" disabled={!clearable.length} onclick={() => runAll('sites.transfer.clear', clearable)}><Icon name="sweep" size={15} /> Clear done</button>
  </div>

  <div class="tl-grid hidden border-y border-divider bg-chrome text-[12px] font-semibold md:grid" role="row">
    <span></span><span>File Name</span><span class="text-right">Size</span><span>Status</span><span class="text-right">Time left</span><span class="text-right">Transfer rate</span><span>Description</span>
  </div>
  <div class="min-h-0 flex-1 overflow-y-auto outline-none" role="grid" tabindex="0" aria-label="Transfers" aria-multiselectable="true" onkeydown={keydown}>
    {#if rows.length === 0}
      <p class="px-6 py-8 text-center text-[13px] text-muted">{emptyMessage(tab)}</p>
    {/if}
    {#each rows as row (row.id)}
      {@const st = status(row)}
      <!-- A row is one transfer batch: a click selects it, a long press or the "…" opens its menu (on a phone, its one button). -->
      <!-- svelte-ignore a11y_click_events_have_key_events, a11y_interactive_supports_focus -->
      <div class="tl-row border-b border-divider/60 text-[12.5px] {selected.has(row.id) ? 'bg-selected' : 'hover:bg-hover'}" role="row" aria-selected={selected.has(row.id)}
        onclick={() => rowClick(row)}
        oncontextmenu={(event) => { event.preventDefault(); if (phone) { if (phoneSheet(row).length) sheetFor = row; } else open(row, event.clientX, event.clientY); }}
        onpointerdown={(event) => pointerdown(row, event)}
        onpointermove={(event) => longPress.move(event.clientX, event.clientY)}
        onpointerup={() => longPress.up()}
        onpointercancel={() => longPress.cancelled()}>
        <!-- svelte-ignore a11y_no_noninteractive_element_interactions, a11y_click_events_have_key_events -->
        <label class="tl-check flex items-center justify-center self-stretch" onclick={(event) => event.stopPropagation()}>
          <input type="checkbox" aria-label="Select {title(row)}" checked={selected.has(row.id)} onchange={() => toggle(row)} />
        </label>
        <span class="tl-name flex min-w-0 items-center gap-1.5" title={fullRoute(row)}>
          <Icon name={isDownload(row) ? 'download' : 'upload'} size={15} class="shrink-0 {isDownload(row) ? 'text-icon-info' : 'text-icon-neutral'}" />
          <span class="min-w-0 flex-1 truncate">{title(row)}</span>
          <button class="tl-menu" aria-label="Actions for {title(row)}" aria-haspopup="menu" onclick={(event) => { event.stopPropagation(); const box = (event.currentTarget as HTMLElement).getBoundingClientRect(); open(row, box.left, box.bottom + 2); }}><Icon name="more" size={16} /></button>
        </span>
        {#if phone}
          {@const command = phoneButton(row)}
          {#if command}
            <button class="tl-phone-btn" aria-label="{command === 'stop' ? 'Pause' : command === 'cancel' ? 'Cancel' : 'Remove'} {title(row)}"
              onclick={(event) => { event.stopPropagation(); void run(command, [row]); }}><Icon name={command === 'stop' ? 'pause' : 'close'} size={16} /></button>
          {/if}
        {/if}
        <span class="tl-size tabular text-right">{sizeText(row)}</span>
        <span class="tl-status min-w-0">
          <span class="block truncate tone-{st.kind}">{st.text}<span class="md:hidden">{row.state === 'failed' && description(row) ? `: ${description(row)}` : ''}{rateText(row) ? ` · ${rateText(row)}` : ''}{timeLeft(row) ? ` · ${timeLeft(row)}` : ''}</span></span>
          {#if st.percent !== null}<span class="mt-0.5 block"><ProgressBar value={st.percent} label="Progress" /></span>{/if}
        </span>
        <span class="tl-eta tabular text-right">{timeLeft(row)}</span>
        <span class="tl-rate tabular text-right">{rateText(row)}</span>
        <span class="tl-route truncate text-muted md:hidden">{routeLine(row, store.texts.hostName)}</span>
        <span class="tl-desc truncate {row.state === 'failed' ? 'tone-failed' : 'text-muted'}" title={description(row)}>{description(row)}</span>
      </div>
    {/each}
  </div>
  <!-- A phone: Pause transfers, Retry failed and Clear done at the bottom, when they apply (`phone`). -->
  {#if pauseAll || retryable.length || clearable.length}
    <div class="flex flex-wrap gap-2 border-t border-divider px-3 py-2.5 md:hidden">
      {#if pauseAll}<button class="btn touch-row" onclick={() => runAll(pauseAll.type, pauseAll.ids)}><Icon name={pauseAll.type.endsWith('pause') ? 'pause' : 'play'} size={15} /> {pauseAll.label}</button>{/if}
      {#if retryable.length}<button class="btn touch-row" onclick={() => runAll('sites.transfer.retry', retryable)}><Icon name="retry" size={15} /> Retry failed</button>{/if}
      {#if clearable.length}<button class="btn touch-row" onclick={() => runAll('sites.transfer.clear', clearable)}><Icon name="sweep" size={15} /> Clear done</button>{/if}
    </div>
  {/if}
  {#if sheetRow && phone}
    {@const row = sheetRow}
    <Dialog title={title(row)} sheet onclose={() => (sheetFor = null)}>
      <div class="flex flex-col py-1" role="menu" aria-label="Actions for {title(row)}">
        {#each phoneSheet(row) as command (command)}
          <button class="touch-row px-4 py-3 text-left text-[15px] hover:bg-hover {command === 'cancel' ? 'tone-failed' : ''}" role="menuitem"
            onclick={() => { sheetFor = null; void run(command, [row]); }}>{phoneSheetLabel(command)}</button>
        {/each}
      </div>
    </Dialog>
  {/if}
  {#if menuAt}<ContextMenu items={menuAt.items} x={menuAt.x} y={menuAt.y} label="Transfer actions" onclose={() => (menuAt = null)} />{/if}
</div>

<style>
  .tl-grid, .tl-row { display: grid; align-items: center; column-gap: 10px; padding: 0 10px 0 4px; }
  /* The main list's tracks at desktop width: box, name, size, status with its bar, time left, rate, description. */
  .tl-grid, .tl-row { grid-template-columns: 28px minmax(180px, 2fr) 84px minmax(140px, 1.3fr) 84px 100px minmax(160px, 2fr); }
  .tl-grid { height: 30px; }
  .tl-row { min-height: 30px; padding-top: 3px; padding-bottom: 3px; cursor: default; user-select: none; }
  .tl-menu { display: inline-flex; border-radius: 4px; padding: 2px; color: var(--muted); opacity: 0; }
  .tl-menu:hover { background: var(--hover); color: var(--text); }
  .tl-row:hover .tl-menu, .tl-row:focus-within .tl-menu, .tl-menu:focus-visible { opacity: 1; }
  /* A phone: each transfer is lines, as in the mockup: name and size, then status with rate and time, then where. */
  @media (max-width: 767px) {
    .tl-grid { display: none; }
    .tl-row { grid-template-columns: 1fr 44px; grid-template-areas: 'name act' 'status act' 'route act'; padding-left: 12px; row-gap: 2px; padding-top: 8px; padding-bottom: 8px; }
    .tl-check { display: none; }
    .tl-phone-btn { grid-area: act; display: inline-flex; align-items: center; justify-content: center; min-width: 40px; min-height: 40px; border-radius: 6px; color: var(--text); }
    .tl-phone-btn:hover { background: var(--hover); }
    .tl-name { grid-area: name; font-size: 14px; }
    .tl-status { grid-area: status; }
    .tl-route { grid-area: route; font-size: 11.5px; }
    .tl-eta, .tl-rate, .tl-size, .tl-desc { display: none; }
    .tl-menu { display: none; }
  }
  @media (pointer: coarse) and (min-width: 768px) { .tl-menu { opacity: 1; } }
</style>
