<script lang="ts">
  import { untrack } from 'svelte';
  import AskDialog from '../components/AskDialog.svelte';
  import Dialog from '../components/Dialog.svelte';
  import ProgressBar from '../components/ProgressBar.svelte';
  import SegmentMap from '../components/SegmentMap.svelte';
  import { cantTell, routeOf } from '../lib/addlines';
  import {
    addressLabel, barValue, completedSize, detailRows, failureLabel, hasOwnAddress, heading, mergeSegments, noFileText, noticeText,
    pollsSegments, redownloadRefusal, segmentRows, shownAddress, speedLimitFrom, speedLimitHelp, speedLimitText, speedLimitUnavailable, splitPath,
  } from '../lib/details';
  import { archiveShort, archiveTone, dateTime, percent, speed, tone } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import type { ArchiveSet, ClassifyResult, ConnectionInfo, Job, Segment } from '../lib/types';
  import MoveRename from './MoveRename.svelte';
  import Properties from './Properties.svelte';
  import RefreshAddress from './RefreshAddress.svelte';

  let { jobId, set, onclose, onpassword }: { jobId: string; set?: ArchiveSet; onclose: () => void; onpassword: (set: ArchiveSet) => void } = $props();

  // The download as the server has it. When it leaves the list the dialog stays, saying so, until it is closed (D21).
  let last = $state<Job | null>(untrack(() => store.model.jobs[jobId] ?? null));
  const live = $derived(store.model.jobs[jobId]);
  $effect(() => {
    if (live) last = live;
  });
  const removed = $derived(!live);
  const job = $derived(live ?? last);

  const connected = $derived(store.connection === 'open');
  const complete = $derived(job?.state === 'completed');
  const desktopApp = store.has('tray');
  const providerName = $derived(store.model.accounts.find((a) => a.provider === job?.remoteProvider)?.providerName ?? job?.remoteProvider ?? 'your provider');

  // Opening a download is a click, which is when a browser lets the page ask for system notices.
  store.askForNotices();

  type Tab = 'status' | 'speed' | 'completion';
  let tab = $state<Tab>('status');
  let notice = $state('');
  let working = $state(false);
  let dialog = $state<'cancel' | 'delete' | 'move' | 'properties' | 'refresh' | null>(null);
  let question = $state<{ text: string; primary: string; secondary?: string; onprimary: () => void; onsecondary?: () => void } | null>(null);

  // ---- Segments (D2) ----
  let segmentsOpen = $state(false);
  let segments = $state<Segment[]>([]);
  let connections = $state<ConnectionInfo[]>([]);
  let segmentProblem = $state('');
  const highWater = new Map<number, number>();
  const rates = new Map<number, number>();
  let loadingSegments = false;

  async function loadSegments() {
    const current = job;
    if (loadingSegments || !connected || removed || !current || !pollsSegments(current)) return;
    loadingSegments = true;
    try {
      const saved = await store.ask<Segment[]>('job.segments', { jobId });
      const live = await store.ask<ConnectionInfo[]>('job.connections', { jobId });
      segmentProblem = '';
      connections = Array.isArray(live) ? live : [];
      segments = mergeSegments(Array.isArray(saved) ? saved : [], connections, job ?? current, highWater);
    } catch (error) {
      segmentProblem = `Saved segment details unavailable: ${error instanceof Error ? error.message : String(error)}`;
    } finally {
      loadingSegments = false;
    }
  }

  $effect(() => {
    const timer = setInterval(() => void loadSegments(), 1000);
    void loadSegments();
    return () => clearInterval(timer);
  });

  // A download that ended has no live segments left to show.
  $effect(() => {
    if (job && !pollsSegments(job) && segments.length && !complete) {
      segments = [];
      connections = [];
    }
  });

  const segmentView = $derived(segmentRows(segments, connections, rates));

  // ---- Speed limiter (D1) ----
  let useLimit = $state(false);
  let limitText = $state('');
  let shownLimit: number | null | undefined = undefined;
  let limitNotice = $state('');
  $effect(() => {
    const value = job?.speedLimitBytesPerSecond ?? null;
    if (shownLimit !== value) {
      shownLimit = value;
      useLimit = !!value && value > 0;
      limitText = speedLimitText(value);
    }
  });
  const canLimit = $derived(!!job?.canChangeSpeedLimit);
  const limiterNotice = $derived(limitNotice || (canLimit ? speedLimitHelp : speedLimitUnavailable));

  async function applyLimit() {
    if (!canLimit) {
      limitNotice = 'Speed limiting is unavailable for this transfer.';
      return;
    }
    const parsed = speedLimitFrom(useLimit, limitText);
    if (!parsed.ok) {
      limitNotice = parsed.error;
      return;
    }
    working = true;
    try {
      await store.ask('job.update', { jobId, speedLimitBytesPerSecond: parsed.bytesPerSecond });
      limitNotice = parsed.bytesPerSecond === 0 ? 'Speed limiter is off for this download.' : 'Speed limit applied to this download.';
    } catch (error) {
      limitNotice = error instanceof Error ? error.message : String(error);
    } finally {
      working = false;
    }
  }

  // ---- Completion options (D9) ----
  const globalShow = $derived(store.model.settings?.showCompletionDialog !== false);
  const showForJob = $derived(job?.showCompletionDialog ?? globalShow);

  async function setShowForJob(value: boolean) {
    try {
      await store.ask('job.update', { jobId, showCompletionDialog: value });
    } catch (error) {
      notice = error instanceof Error ? error.message : String(error);
    }
  }

  async function setDontShowAgain(checked: boolean) {
    const ok = await store.saveSettings((settings) => { settings.showCompletionDialog = !checked; }, '');
    if (ok) notice = checked ? 'The default completion window is disabled. Individual download choices still apply.' : 'Completion windows will continue to appear.';
  }

  // ---- Actions ----
  async function run(type: string, payload: Record<string, unknown> = {}): Promise<boolean> {
    working = true;
    notice = '';
    try {
      await store.ask(type, { jobId, ...payload });
      return true;
    } catch (error) {
      notice = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      working = false;
    }
  }

  async function cancelDownload() {
    dialog = null;
    if (job?.canCancel) await run('job.cancel', { deleteFile: true, onlyIfUnfinished: true });
  }

  async function deleteDownload() {
    dialog = null;
    if (await run('job.remove', { deleteFile: !job?.fileRemovedAfterExtraction })) onclose();
  }

  function nothingToDelete(): string {
    return 'Its archive was removed after extraction, so no file is deleted.';
  }

  const isWebLink = (value: string) => /^https?:\/\//i.test(value);

  async function redownload() {
    const current = job;
    if (!current) return;
    notice = '';
    const refusal = redownloadRefusal(current);
    if (refusal) {
      notice = refusal;
      return;
    }
    // Only a web link can be a provider's. The job may be a direct choice made on purpose, so that stays on offer.
    if (isWebLink(current.url) && store.has('remote')) {
      working = true;
      try {
        const result = await store.ask<ClassifyResult>('remote.classify', { text: current.url });
        const name = result.providerName || providerName;
        const route = routeOf({ input: current.url, position: 1, result, error: null, direct: false });
        if (route === 'provider') {
          question = {
            text: `${name} handles this site, so this link can go through your account. It can also download directly from the site instead.`,
            primary: `Add through ${name}`,
            secondary: 'Download directly',
            onprimary: () => void addThroughProvider(current),
            onsecondary: () => void addDirect(current),
          };
          return;
        }
        if (cantTell({ input: current.url, position: 1, result, error: null, direct: false }) || result.checkIncomplete) {
          question = {
            text: result.note
              ? `${result.note} Download it directly only if you know it isn't one of those sites.`
              : "This app couldn't tell what this address is. Try the check again, or download directly.",
            primary: 'Try again',
            secondary: 'Download directly',
            onprimary: () => void redownload(),
            onsecondary: () => void addDirect(current),
          };
          return;
        }
      } catch (error) {
        notice = error instanceof Error ? error.message : String(error);
        return;
      } finally {
        working = false;
      }
    }
    await addDirect(current);
  }

  async function addDirect(current: Job) {
    question = null;
    working = true;
    try {
      const result = await store.ask<{ accepted: boolean; error?: string }>('job.add', {
        url: current.url, destinationDirectory: current.destinationDirectory, fileName: current.fileName, categoryId: current.categoryId,
        conflictPolicy: 'rename', rememberDestination: false, source: 'web',
      });
      if (!result.accepted) notice = result.error ?? 'The server rejected the redownload.';
      else store.toast('The download was added again.');
    } catch (error) {
      notice = error instanceof Error ? error.message : String(error);
    } finally {
      working = false;
    }
  }

  async function addThroughProvider(current: Job) {
    question = null;
    working = true;
    try {
      await store.ask('remote.add', { input: current.url, destination: current.destinationDirectory });
      store.toast(`The link was sent through ${providerName}.`);
    } catch (error) {
      notice = error instanceof Error ? error.message : String(error);
    } finally {
      working = false;
    }
  }

  async function copy(text: string, done: string) {
    try {
      await navigator.clipboard.writeText(text);
      store.toast(done);
    } catch {
      notice = 'The browser did not allow copying. Select the text and copy it by hand.';
    }
  }

  const rows = $derived(job ? detailRows(job) : []);
  const bar = $derived(job ? barValue(job) : 0);
  const title = $derived(job ? (complete ? `Download complete — ${job.fileName || 'Download'}` : `${(percent(job) ?? 0).toFixed(1)}% — ${job.fileName || 'Download'}`) : 'Download removed');
  const noticeLine = $derived(removed ? 'This download is no longer in the list.' : notice || (job ? noticeText(job) : ''));
  const failLabel = $derived(job ? failureLabel(job) : null);
  const ownAddress = $derived(!!job && hasOwnAddress(job));
  const folderOf = $derived(job ? splitPath(job.finalFilePath).directory : '');
</script>

<Dialog {title} {onclose} wide>
  <div class="mb-3 text-[15px] font-semibold">{removed ? 'Download removed' : job ? heading(job) : ''}</div>

  {#if job}
    <div class="mb-3 truncate text-[13px] font-semibold" title={job.fileName}>{job.fileName || 'Download'}</div>
  {/if}

  {#if job && !complete}
    <div role="tablist" aria-label="Download details" class="mb-3 flex gap-1 border-b border-divider">
      {#each [['status', 'Download status'], ['speed', 'Speed limiter'], ['completion', 'Options on completion']] as [key, label] (key)}
        <button
          role="tab"
          class="-mb-px border-b-2 px-3 py-1.5 text-[13px] {tab === key ? 'border-accent font-semibold' : 'border-transparent text-muted hover:text-text'}"
          aria-selected={tab === key}
          onclick={() => (tab = key as Tab)}
        >{label}</button>
      {/each}
    </div>

    {#if tab === 'status'}
      <div class="grid gap-2">
        {#if ownAddress}
          <div class="text-[13px] font-semibold">{addressLabel(job)}</div>
          <div class="min-w-0 select-text break-all font-mono text-[12px]">{shownAddress(job)}</div>
        {:else}
          <div class="text-[13px]"><span class="text-muted">Source</span> <span class="ml-2">{providerName}</span></div>
        {/if}
      </div>

      <dl class="mt-3 grid grid-cols-[minmax(110px,auto)_1fr] gap-x-4 gap-y-1.5 text-[13px]">
        {#each rows as row (row.label)}
          <dt class="text-muted">{row.label}</dt><dd class="tabular">{row.value}</dd>
        {/each}
        {#if job.requestedConnections}
          <dt class="text-muted">Connections</dt>
          <dd class="tabular">{job.activeConnections ?? 0} of {job.requestedConnections}{job.waitingForHost ? `, waiting for ${job.waitingForHost}` : ''}</dd>
        {/if}
        <dt class="text-muted">Queue</dt><dd>{job.queueName ?? job.queueId}</dd>
        <dt class="text-muted">Added</dt><dd>{dateTime(job.createdAt)}</dd>
        {#if job.statusNote}<dt class="text-muted">Note</dt><dd>{job.statusNote}</dd>{/if}
        {#if failLabel && job.failure}<dt class="text-muted">{failLabel}</dt><dd class="text-error">{job.failure.message}</dd>{/if}
        {#if set}
          <dt class="text-muted">Extraction</dt>
          <dd>
            <span class="tone-{archiveTone(set)}">{archiveShort(set)}</span>
            <span class="block text-[12px] text-muted">{set.stateText}</span>
          </dd>
        {/if}
      </dl>

      <details class="mt-4 rounded border border-divider" bind:open={segmentsOpen}>
        <summary class="cursor-pointer px-3 py-1.5 text-[13px] font-semibold">Segment details</summary>
        <div class="overflow-x-auto px-3 pb-3 pt-1">
          {#if segmentProblem}<p class="mb-2 text-[12px] text-error">{segmentProblem}</p>{/if}
          {#if segmentView.length === 0}
            <p class="text-[12px] text-muted">No segment information yet.</p>
          {:else}
            <table class="w-full min-w-[420px] text-[12px]">
              <thead>
                <tr class="text-left text-muted">
                  <th class="w-8 py-1 font-semibold">N.</th><th class="font-semibold">Progress</th>
                  <th class="w-36 text-right font-semibold">Downloaded</th><th class="w-20 text-right font-semibold">Speed</th><th class="w-32 pl-3 font-semibold">Info</th>
                </tr>
              </thead>
              <tbody>
                {#each segmentView as row (row.segment.start)}
                  <tr class="border-t border-divider/60">
                    <td class="py-1 text-muted">{row.number}</td>
                    <td class="pr-3"><ProgressBar value={row.percent} label="Segment {row.number}" /></td>
                    <td class="tabular text-right">{row.downloaded}</td>
                    <td class="tabular text-right {row.speed === '—' ? 'text-muted' : ''}">{row.speed}</td>
                    <td class="pl-3 {row.info === 'Waiting' ? 'text-muted' : ''}">{row.info}</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          {/if}
        </div>
      </details>
    {:else if tab === 'speed'}
      <div class="grid gap-3">
        <div class="text-[13px]">Current transfer rate: <span class="tabular inline-block min-w-20 text-right" aria-label="Current transfer rate">{speed(job.bytesPerSecond) || '—'}</span></div>
        <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={useLimit} disabled={!canLimit} /> Use speed limiter</label>
        <div>
          <label class="label" for="limit-input">Maximum download speed (KB/s)</label>
          <input id="limit-input" class="field" inputmode="decimal" bind:value={limitText} disabled={!canLimit || !useLimit} placeholder="Enter a positive number" autocomplete="off" />
        </div>
        <p class="hint" role="status">{limiterNotice}</p>
        <div><button class="btn" disabled={!connected || !canLimit || working} onclick={applyLimit}>Apply speed limit</button></div>
      </div>
    {:else}
      <div class="grid gap-3">
        <div class="text-[13px] font-semibold">Save to</div>
        <div class="select-text break-all text-[13px]">{job.finalFilePath}</div>
        {#if desktopApp}
          <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" checked={showForJob} disabled={!connected} onchange={(event) => void setShowForJob(event.currentTarget.checked)} /> Show the completion window for this download</label>
          <p class="hint">The global completion preference can be changed from the completion window.</p>
        {/if}
      </div>
    {/if}

    <div class="mt-4">
      {#if segments.length > 0 && job.totalBytes}
        <SegmentMap {segments} total={job.totalBytes} />
      {:else if bar === null}
        <ProgressBar value={null} height={14} />
      {:else}
        <ProgressBar value={bar} height={14} />
      {/if}
    </div>
  {:else if job}
    <div class="grid gap-2 text-[13px]">
      <div>{completedSize(job)}</div>
      {#if ownAddress}
        <label class="mt-1.5 font-semibold" for="done-address">{addressLabel(job)}</label>
        <input id="done-address" class="field font-mono" readonly value={shownAddress(job)} onfocus={(event) => event.currentTarget.select()} />
      {:else}
        <div><span class="text-muted">Source</span> <span class="ml-2">{providerName}</span></div>
      {/if}
      <label class="font-semibold" for="done-path">The file saved as</label>
      <input id="done-path" class="field font-mono" readonly value={job.finalFilePath} onfocus={(event) => event.currentTarget.select()} />
      {#if job.fileRemovedAfterExtraction}<p class="hint">{noFileText(job.statusNote)}</p>{/if}
      {#if set}
        <div><span class="text-muted">Extraction</span> <span class="tone-{archiveTone(set)} ml-2">{archiveShort(set)}</span></div>
      {/if}
      {#if desktopApp}
        <label class="mt-2 flex items-center gap-2"><input type="checkbox" checked={showForJob} disabled={!connected} onchange={(event) => void setShowForJob(event.currentTarget.checked)} /> Show the completion window for this download</label>
        <label class="flex items-center gap-2"><input type="checkbox" checked={!globalShow} disabled={!connected} onchange={(event) => void setDontShowAgain(event.currentTarget.checked)} /> Don't show this dialog again</label>
      {/if}
    </div>
  {/if}

  <p class="mt-3 min-h-5 text-[12.5px] {notice || (job?.failure && !removed) ? 'text-error' : 'text-muted'}" role="status">{noticeLine}</p>

  {#snippet footer()}
    {#if job}
      <button class="btn" disabled={!connected || !job.canPause || working} style:display={job.isTerminal ? 'none' : undefined} onclick={() => run('job.pause')}>Pause</button>
      <button class="btn" disabled={!connected || !job.canResume || working} style:display={job.isTerminal && !job.canResume ? 'none' : undefined} onclick={() => run('job.resume')}>Resume</button>
      {#if job.canCancel}<button class="btn" disabled={!connected || working} onclick={() => (dialog = 'cancel')}>Cancel</button>{/if}
      <button class="btn" disabled={!connected || working} title="Remove this download and delete its file" onclick={() => (dialog = 'delete')}>Delete…</button>
      {#if !removed}
        <button class="btn" disabled={!connected || working} onclick={() => void redownload()}>Redownload</button>
        {#if complete && !job.fileRemovedAfterExtraction}
          <button class="btn" disabled={!connected || working} onclick={() => (dialog = 'move')}>Move/Rename…</button>
          <button class="btn" onclick={() => copy(job.finalFilePath, 'The path was copied.')}>Copy path</button>
          <button class="btn" onclick={() => copy(folderOf, 'The folder path was copied.')}>Copy folder path</button>
        {/if}
        {#if !job.isRemote && !job.url.toLowerCase().startsWith('remote://')}
          <button class="btn" disabled={!connected || working} onclick={() => (dialog = 'refresh')}>Refresh download address…</button>
        {/if}
        <button class="btn" disabled={!connected} onclick={() => (dialog = 'properties')}>Properties…</button>
        {#if set?.needsPassword}<button class="btn" onclick={() => onpassword(set)}>Archive password…</button>{/if}
        {#if set && (set.state === 'downloaded' || set.state === 'failed')}
          <button class="btn" onclick={() => store.attempt('archive.extract', { jobIds: [job.id] }, 'Extraction asked for.')}>Extract</button>
        {/if}
      {/if}
    {/if}
    <button class="btn" onclick={onclose}>{job?.isTerminal || removed ? 'Close' : 'Hide'}</button>
  {/snippet}
</Dialog>

{#if dialog === 'cancel'}
  <AskDialog title="Cancel download?" text="Cancel this transfer and delete its partial file?" confirm="Cancel download" keep="Keep downloading" onconfirm={cancelDownload} onclose={() => (dialog = null)} />
{:else if dialog === 'delete' && job}
  <AskDialog
    title="Delete download?"
    text={job.fileRemovedAfterExtraction
      ? `Remove ${job.fileName ? `“${job.fileName}”` : 'this download'} from the list? ${nothingToDelete()}`
      : `Remove ${job.fileName ? `“${job.fileName}”` : 'this download'} from the list and delete its downloaded file? This cannot be undone.`}
    confirm="Delete"
    danger
    onconfirm={deleteDownload}
    onclose={() => (dialog = null)}
  />
{:else if dialog === 'move' && job}
  <MoveRename {job} onclose={() => (dialog = null)} />
{:else if dialog === 'properties' && job}
  <Properties {job} onclose={() => (dialog = null)} />
{:else if dialog === 'refresh' && job}
  <RefreshAddress {job} onclose={() => (dialog = null)} />
{/if}

{#if question}
  <Dialog title="Redownload" onclose={() => (question = null)}>
    <p class="text-[13px]">{question.text}</p>
    {#snippet footer()}
      <button class="btn" onclick={() => (question = null)}>Cancel</button>
      {#if question?.secondary}<button class="btn" onclick={() => { const q = question; question = null; q?.onsecondary?.(); }}>{question.secondary}</button>{/if}
      <button class="btn-primary" onclick={() => { const q = question; question = null; q?.onprimary(); }}>{question?.primary}</button>
    {/snippet}
  </Dialog>
{/if}
