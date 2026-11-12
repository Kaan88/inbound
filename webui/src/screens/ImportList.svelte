<script lang="ts">
  import Dialog from '../components/Dialog.svelte';
  import { chunkTorrentEntries, importSummary, isTorrentEntry, parseList, type ListEntry } from '../lib/addflow';
  import { routeOf, type AddLine } from '../lib/addlines';
  import { lineWords, newLine } from '../lib/addview';
  import { store } from '../lib/store.svelte';
  import type { ClassifyResult } from '../lib/types';

  // Import download list: a file saved by Export list (or by the desktop), the "Import downloads?" question, then
  // what became of each entry. Entries a provider could take, or that need a decision, go on to the review.
  let { onclose, onreview }: { onclose: () => void; onreview: (seed: { input: string; result: ClassifyResult }[]) => void } = $props();

  let step = $state<'pick' | 'confirm' | 'working' | 'done'>('pick');
  let entries = $state<ListEntry[]>([]);
  let problem = $state('');
  let summary = $state('');
  let toReview = $state<{ input: string; result: ClassifyResult }[]>([]);

  const providerAdd = $derived(store.has('remote') && store.model.accounts.some((a) => a.hasKey && !a.keyUnreadable));
  const providerName = $derived(store.model.accounts[0]?.providerName ?? 'the provider');
  const keyNotice = $derived.by(() => {
    const unreadable = store.model.accounts.find((a) => a.keyUnreadable);
    return unreadable ? `The saved ${unreadable.providerName} API key can't be read on this server, so links here aren't going through ${unreadable.providerName}. Enter the key again in Options to use it.` : '';
  });

  async function picked(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    problem = '';
    if (!file) return;
    try {
      entries = parseList(await file.text());
    } catch (error) {
      problem = error instanceof Error ? error.message : String(error);
      return;
    }

    if (entries.length === 0) { problem = 'The file has no downloads.'; return; }
    step = 'confirm';
  }

  async function run() {
    step = 'working';
    const answers = new Map<string, ClassifyResult>();
    if (providerAdd) {
      summary = `Checking ${entries.length} addresses…`;
      for (const entry of entries) {
        if (isTorrentEntry(entry) || !/^https?:\/\//i.test(entry.url) || answers.has(entry.url)) continue;
        try {
          answers.set(entry.url, await store.ask<ClassifyResult>('remote.classify', { text: entry.url }));
        } catch (error) {
          answers.set(entry.url, { ok: false, kind: 'undetermined', checkIncomplete: false, error: error instanceof Error ? error.message : String(error) } as ClassifyResult);
        }
      }
    }

    let accepted = 0;
    let failed = 0;
    let firstError: string | undefined;
    const review: { input: string; result: ClassifyResult }[] = [];
    // Torrents go to the server's torrent import: one already in the list, or every one while torrents are off, is skipped.
    const torrents = entries.filter(isTorrentEntry);
    let skipped = 0;
    let skipReason: string | undefined;
    // In requests that each fit one message.
    let sent = 0;
    for (const chunk of chunkTorrentEntries(torrents)) {
      try {
        const result = await store.ask<{ added: number; skipped: number; skipReason?: string; failed: number; firstError?: string }>('torrent.import', { entries: chunk });
        accepted += result.added;
        failed += result.failed;
        firstError ??= result.firstError;
        skipped += result.skipped;
        skipReason ??= result.skipReason;
        sent += chunk.length;
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        // A server without torrents doesn't know the request: nothing went wrong with the entries.
        if (/unknown message type/i.test(message)) { skipped += torrents.length - sent; skipReason ??= 'This server does not download torrents.'; }
        else { failed += torrents.length - sent; firstError ??= message; }
        break;
      }
    }

    for (const entry of entries.filter((item) => !isTorrentEntry(item))) {
      if (!/^https?:\/\//i.test(entry.url)) {
        failed++;
        firstError ??= `Invalid URL: ${entry.url}`;
        continue;
      }

      const answer = answers.get(entry.url);
      if (answer) {
        const line: AddLine = { ...newLine(entry.url, 0), result: answer };
        const route = routeOf(line);
        const cantTell = answer.ok === false || answer.kind === 'undetermined';
        if (route === 'none' && !cantTell) {
          failed++;
          firstError ??= lineWords(line, providerName, false).detail;
          continue;
        }

        if (route !== 'direct') {
          if (!review.some((item) => item.input === entry.url)) review.push({ input: entry.url, result: answer });
          continue;
        }
      }

      try {
        const result = await store.ask<{ accepted: boolean; error?: string }>('job.add', {
          url: entry.url, fileName: entry.fileName, categoryId: entry.categoryId, destinationDirectory: entry.destinationDirectory,
          rememberDestination: false, description: entry.description, queueId: entry.queueId,
          connections: entry.requestedConnections > 0 ? entry.requestedConnections : undefined,
          startMode: entry.startMode, conflictPolicy: entry.conflictPolicy, expectedSha256: entry.expectedSha256, source: 'import',
        });
        if (result.accepted) accepted++;
        else { failed++; firstError ??= result.error; }
      } catch (error) {
        failed++;
        firstError ??= error instanceof Error ? error.message : String(error);
      }
    }

    toReview = review;
    summary = importSummary(accepted, entries.length, failed, firstError, review.length, skipped, skipReason);
    step = 'done';
  }
</script>

<Dialog title={step === 'confirm' || step === 'working' ? 'Import downloads?' : 'Import download list'} {onclose}>
  {#if step === 'pick'}
    <label class="label" for="list-file">A download list saved with Export list</label>
    <input id="list-file" class="field" type="file" accept=".json,application/json" onchange={picked} />
    {#if problem}<p class="mt-2 text-[12.5px] text-error" role="alert">{problem}</p>{/if}
  {:else if step === 'confirm'}
    <p class="text-[13px]">Add {entries.length} downloads from this file?{keyNotice ? ` ${keyNotice}` : ''}</p>
  {:else if step === 'working'}
    <p class="text-[13px]" role="status">{summary || 'Adding…'}</p>
  {:else}
    <p class="whitespace-pre-line text-[13px]" role="status">{summary}</p>
  {/if}

  {#snippet footer()}
    {#if step === 'confirm'}
      <button class="btn-primary" onclick={run}>Add</button>
      <button class="btn" onclick={onclose}>Cancel</button>
    {:else if step === 'done'}
      {#if toReview.length}<button class="btn-primary" onclick={() => onreview(toReview)}>Review {toReview.length}</button>{/if}
      <button class={toReview.length ? 'btn' : 'btn-primary'} onclick={onclose}>{toReview.length ? 'Not now' : 'Close'}</button>
    {:else if step === 'pick'}
      <button class="btn" onclick={onclose}>Cancel</button>
    {/if}
  {/snippet}
</Dialog>
