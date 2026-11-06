using DownloadManager.Core.Providers;
using DownloadManager.Persistence;

namespace DownloadManager.Runtime.Remote;

public sealed partial class RemoteJobCoordinator
{
    private enum FindKind
    {
        Found,
        InQueue,
        /// <summary>Everything was read and it isn't there.</summary>
        Absent,
        /// <summary>The account has more than could be read, so nothing can be said.</summary>
        Inconclusive,
    }

    private readonly record struct FindAnswer(FindKind Kind, RemoteItem? Item = null);

    /// <summary>What was read during one pass, so ten waiting items don't each read the whole list.</summary>
    private sealed class PassScope(RemoteCoordinatorOptions options)
    {
        private readonly Dictionary<RemoteItemKind, (List<RemoteItem> Items, bool Complete)> _lists = [];
        private readonly Dictionary<RemoteItemKind, (List<RemoteQueuedItem> Items, bool Complete)> _queues = [];

        public Dictionary<RemoteItemKind, int> DuePolls { get; } = [];
        public bool RateLimited { get; set; }

        public async Task<(List<RemoteItem> Items, bool Complete)> ListAsync(IRemoteCatalog catalog, RemoteItemKind kind, CancellationToken cancellationToken)
        {
            if (_lists.TryGetValue(kind, out var cached))
            {
                return cached;
            }

            var items = new List<RemoteItem>();
            var complete = false;
            for (var page = 0; page < options.MostListPages; page++)
            {
                var read = await catalog.ListAsync(kind, page * options.ListPageSize, options.ListPageSize, fresh: true, cancellationToken).ConfigureAwait(false);
                items.AddRange(read);
                if (read.Count < options.ListPageSize)
                {
                    complete = true;
                    break;
                }
            }

            return _lists[kind] = (items, complete);
        }

        public async Task<(List<RemoteQueuedItem> Items, bool Complete)> QueueAsync(IRemoteQueue queue, RemoteItemKind kind, CancellationToken cancellationToken)
        {
            if (_queues.TryGetValue(kind, out var cached))
            {
                return cached;
            }

            var items = new List<RemoteQueuedItem>();
            var complete = false;
            for (var page = 0; page < options.MostListPages; page++)
            {
                var read = await queue.ListQueuedAsync(kind, page * options.ListPageSize, options.ListPageSize, fresh: true, cancellationToken).ConfigureAwait(false);
                items.AddRange(read);
                if (read.Count < options.ListPageSize)
                {
                    complete = true;
                    break;
                }
            }

            return _queues[kind] = (items, complete);
        }

        /// <summary>A submission just changed what the provider holds.</summary>
        public void Forget(RemoteItemKind kind)
        {
            _lists.Remove(kind);
            _queues.Remove(kind);
        }
    }

    // ---- The loop ------------------------------------------------------------------------------

    private async Task RunLoopAsync(AccountWork work, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset? next;
            try
            {
                next = await RunAccountAsync(work, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Everything expected is handled per job. What's left must not end the loop for good.
                _log?.Invoke($"Remote jobs of {work.Entry.Id}: a pass failed: {ex.GetType().Name}.");
                next = _time.GetUtcNow() + StoreTroublePause;
            }

            // Kept to what a timer accepts, whatever a saved time says: a wait out of range would end this loop for good.
            var delay = next is { } due
                ? TimeSpan.FromTicks(Math.Clamp((due - _time.GetUtcNow()).Ticks, ShortestLoopWait.Ticks, LongestLoopWait.Ticks))
                : Timeout.InfiniteTimeSpan;

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var timer = Task.Delay(delay, _time, waiting.Token);
                var woken = work.Wake.WaitAsync(waiting.Token);
                await Task.WhenAny(timer, woken).ConfigureAwait(false);
            }
            finally
            {
                await waiting.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Does everything that is due now, on every account, and returns when that is done.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken = default)
    {
        foreach (var work in _accounts.Values)
        {
            await RunAccountAsync(work, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <returns>When something is next due. Null when nothing on this account waits for a timer.</returns>
    private async Task<DateTimeOffset?> RunAccountAsync(AccountWork work, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref work.PassRunning, 1, 0) != 0)
        {
            return _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        }

        try
        {
            for (var round = 0; round < MostRoundsPerPass; round++)
            {
                var now = _time.GetUtcNow();
                if (now < work.StoreTroubleUntil || work.Pacing.RateLimitedUntil is { } limited && now < limited)
                {
                    break;
                }

                var due = DueRecords(work.Entry.Id, now);
                if (due.Count == 0)
                {
                    break;
                }

                var pass = new PassScope(_options);
                foreach (var group in due.Where(record => record.State == RemoteJobState.Waiting).GroupBy(record => record.Kind))
                {
                    pass.DuePolls[group.Key] = group.Count();
                }

                foreach (var record in due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await StepAsync(work, record.Id, now, pass, cancellationToken).ConfigureAwait(false);
                    if (pass.RateLimited || _time.GetUtcNow() < work.StoreTroubleUntil)
                    {
                        break;
                    }
                }
            }

            return NextDue(work);
        }
        finally
        {
            Volatile.Write(ref work.PassRunning, 0);
        }
    }

    private List<RemoteJobRecord> DueRecords(string providerId, DateTimeOffset now)
    {
        lock (_sync)
        {
            return _records.Values
                .Where(record => record.Provider == providerId && RemoteJobRules.IsPolled(record.State) && record.NextCheckAt is { } at && at <= now)
                .OrderBy(record => record.CreatedAt)
                .ToList();
        }
    }

    private DateTimeOffset? NextDue(AccountWork work)
    {
        DateTimeOffset? next = null;
        lock (_sync)
        {
            foreach (var record in _records.Values)
            {
                if (record.Provider == work.Entry.Id && RemoteJobRules.IsPolled(record.State) && record.NextCheckAt is { } at && (next is null || at < next))
                {
                    next = at;
                }
            }
        }

        if (next is null)
        {
            return null;
        }

        var held = work.Pacing.RateLimitedUntil is { } limited && limited > work.StoreTroubleUntil ? limited : work.StoreTroubleUntil;
        return held > next ? held : next;
    }

    private async Task StepAsync(AccountWork work, Guid id, DateTimeOffset dueBy, PassScope pass, CancellationToken cancellationToken)
    {
        // An earlier step or the user may have moved it since the pass began.
        RemoteJobRecord? record;
        lock (_sync) _records.TryGetValue(id, out record);
        if (record is null || !RemoteJobRules.IsPolled(record.State) || record.NextCheckAt is not { } at || at > dueBy)
        {
            return;
        }

        try
        {
            try
            {
                switch (record.State)
                {
                    case RemoteJobState.Intended or RemoteJobState.Blocked:
                        await SubmissionStepAsync(work, record, pass, cancellationToken).ConfigureAwait(false);
                        break;
                    case RemoteJobState.SubmissionUnknown:
                        await UnknownStepAsync(work, record, pass, cancellationToken).ConfigureAwait(false);
                        break;
                    case RemoteJobState.Waiting:
                        await PollStepAsync(work, record, pass, cancellationToken).ConfigureAwait(false);
                        break;
                }

                await MoveOnIfStuckAsync(id, record, dueBy).ConfigureAwait(false);
            }
            catch (RemoteProviderException ex)
            {
                await CheckFailedAsync(work, id, pass, ex).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
            {
                // Such as the client's timeout, which surfaces as a cancellation nobody asked for.
                _log?.Invoke($"Remote job {id:N}: a check failed: {ex.GetType().Name}.");
                await CheckFailedAsync(work, id, pass, new RemoteProviderException(
                    RemoteFailureKind.Network, null, $"{work.Entry.DisplayName} could not be reached.")).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The disk refused a save, so the step's result didn't count. The record is as it was, and every step can be repeated.
            _log?.Invoke($"Remote job {id:N}: could not save its state: {ex.GetType().Name}.");
            work.StoreTroubleUntil = _time.GetUtcNow() + StoreTroublePause;
        }
    }

    /// <summary>
    /// A step that changed nothing and left its record due would run again every round of every pass,
    /// which is about ten requests a second, forever, with nothing shown. Every step means to move
    /// its record on, so this only catches one that didn't.
    /// </summary>
    private async Task MoveOnIfStuckAsync(Guid id, RemoteJobRecord before, DateTimeOffset dueBy)
    {
        RemoteJobRecord? after;
        lock (_sync) _records.TryGetValue(id, out after);
        if (after is null || after != before || after.NextCheckAt is not { } at || at > dueBy)
        {
            return;
        }

        _log?.Invoke($"Remote job {id:N}: a step changed nothing, so it waits before trying again.");
        var next = _time.GetUtcNow() + RemoteJobRules.Backoff(before.ConsecutiveFailures + 1, _options.Jitter());
        await UpdateAsync(id, latest => latest == before ? latest with { NextCheckAt = next } : null).ConfigureAwait(false);
    }

    /// <summary>
    /// A status check failed. The record's state doesn't move, so this can't lead to a submit: it only
    /// waits longer. A provider's rate limit holds the whole account, however it was said. Its asked-for
    /// wait counts only as far as the provider's own code passes one on, which is a 429's Retry-After
    /// and nothing else, because the service sends one on every response.
    /// </summary>
    private async Task CheckFailedAsync(AccountWork work, Guid id, PassScope pass, RemoteProviderException failure)
    {
        var now = _time.GetUtcNow();
        var rateLimited = failure.Kind == RemoteFailureKind.RateLimited;
        var hold = RemoteJobRules.RateLimitHold(failure.RetryAfter);
        if (rateLimited)
        {
            pass.RateLimited = true;
            await ChangePacingAsync(work, pacing => pacing with { RateLimitedUntil = now + hold }).ConfigureAwait(false);
        }

        await UpdateAsync(id, record =>
        {
            if (!RemoteJobRules.IsPolled(record.State))
            {
                return null;
            }

            var failures = record.ConsecutiveFailures + 1;
            var wait = rateLimited ? hold : RemoteJobRules.Backoff(failures, _options.Jitter());

            return record with
            {
                ConsecutiveFailures = failures,
                FailureKind = failure.Kind,
                FailureMessage = Describe(work.Entry, failure),
                NextCheckAt = now + wait,
            };
        }).ConfigureAwait(false);
    }

    // ---- Submitting ----------------------------------------------------------------------------

    /// <summary>
    /// The intent is on disk by now. In order: is it on the account already, is it cached, may it be
    /// created, and only then the slot, the Submitting record and the POST. Anything failing before
    /// that point leaves nothing to reconcile.
    /// </summary>
    private async Task SubmissionStepAsync(AccountWork work, RemoteJobRecord record, PassScope pass, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        if (entry.AccountProblem() is { } accountProblem)
        {
            // As for a job already on the provider: nothing about the job is wrong, and setting a key
            // wakes everything. One waiting for the user's approval goes on waiting for it.
            var later = _time.GetUtcNow() + RemoteJobRules.LongestIdlePoll;
            await UpdateAsync(record.Id, latest => latest.State is RemoteJobState.Intended or RemoteJobState.Blocked
                ? latest with
                {
                    FailureKind = RemoteFailureKind.Authentication,
                    FailureMessage = accountProblem,
                    NextCheckAt = latest.ApprovalNeeded ? null : later,
                }
                : null).ConfigureAwait(false);
            return;
        }

        if (entry.Submitter is not { } submitter || record.PredictedHash is not { } hash)
        {
            await FailAsync(record.Id, RemoteFailureKind.Rejected, $"{entry.DisplayName} can't accept this input from here.").ConfigureAwait(false);
            return;
        }

        var found = await FindByHashAsync(entry, record.Kind, hash, pass, cancellationToken).ConfigureAwait(false);
        if (await AdoptIfFoundAsync(work, record, found, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (found.Kind == FindKind.Inconclusive)
        {
            await TooManyItemsAsync(entry, record.Id).ConfigureAwait(false);
            return;
        }

        var cached = false;
        if (entry.CacheChecker is { } checker)
        {
            var hits = await checker.CheckCachedAsync(record.Kind, [hash], cancellationToken).ConfigureAwait(false);
            cached = hits.ContainsKey(hash);
        }

        if (!cached && !record.ApprovedUncached)
        {
            var askedAt = _time.GetUtcNow();
            await UpdateAsync(record.Id, latest =>
            {
                if (latest.State is not (RemoteJobState.Intended or RemoteJobState.Blocked))
                {
                    return null;
                }

                // The user agreed while the cache was being checked, and that answer is the newer one.
                return latest.ApprovedUncached
                    ? latest with { ApprovalNeeded = false, NextCheckAt = askedAt }
                    : latest with
                    {
                        State = RemoteJobState.Intended,
                        ApprovalNeeded = true,
                        Cached = false,
                        FailureKind = null,
                        FailureMessage = null,
                        ConsecutiveFailures = 0,
                        NextCheckAt = null,
                    };
            }).ConfigureAwait(false);
            return;
        }

        if (!cached && record.HosterName is { } hosterName && work.Hosters is { } hosters &&
            await hosters.FindAsync(hosterName, cancellationToken).ConfigureAwait(false) is { } hoster)
        {
            var site = RemoteText.Clean(hoster.Name, 100) ?? entry.DisplayName;
            if (!hoster.Available)
            {
                var note = RemoteText.Clean(hoster.Note, 200, entry.Account is { } account ? account.Mask : null);
                await FailAsync(record.Id, RemoteFailureKind.Rejected,
                    $"{entry.DisplayName} reports that {site} is unavailable right now{(note is null ? "." : $": {note}")}").ConfigureAwait(false);
                return;
            }

            // It clears by itself. Sending it anyway would only spend a creation on a refusal.
            if (RemoteJobRules.DailyLimitUsedUp(hoster))
            {
                await UpdateAsync(record.Id, latest =>
                {
                    var failures = latest.ConsecutiveFailures + 1;
                    return latest.State is RemoteJobState.Intended or RemoteJobState.Blocked
                        ? latest with
                        {
                            State = RemoteJobState.Blocked,
                            ConsecutiveFailures = failures,
                            FailureKind = RemoteFailureKind.AccountBusy,
                            FailureMessage = $"{entry.DisplayName} has used up today's allowance for {site}. This will be tried again by itself.",
                            NextCheckAt = _time.GetUtcNow() + RemoteJobRules.Backoff(failures, _options.Jitter()),
                        }
                        : null;
                }).ConfigureAwait(false);
                return;
            }
        }

        // Only what would make the provider start new work. What is cached may still be refused, and that refusal is then the real answer.
        if (!cached && work.Pacing.CreationStoppedReason is { } stopped)
        {
            await FailAsync(record.Id, RemoteFailureKind.AccountLimit,
                $"Not sent, because {entry.DisplayName} refused an earlier item. {stopped}").ConfigureAwait(false);
            return;
        }

        var now = _time.GetUtcNow();
        if (record.Kind == RemoteItemKind.WebDownload && work.Pacing.NextWebCreateAt is { } nextSlot && nextSlot > now)
        {
            await UpdateAsync(record.Id, latest => latest.State is RemoteJobState.Intended or RemoteJobState.Blocked
                ? WithoutAccountProblem(latest) with { Cached = cached, NextCheckAt = nextSlot }
                : null).ConfigureAwait(false);
            return;
        }

        // Read last, and only in this method's locals.
        string? link = null;
        byte[]? torrent = null;
        if (record.ProtectedTorrent is not null)
        {
            torrent = SecretProtector.UnprotectRemoteTorrent(record.ProtectedTorrent);
        }
        else
        {
            link = SecretProtector.UnprotectRemoteInput(record.ProtectedInput);
        }

        if (link is null && torrent is null)
        {
            await FailAsync(record.Id, RemoteFailureKind.Rejected,
                "The saved input can't be read on this Windows account. Add the link or file again.").ConfigureAwait(false);
            return;
        }

        var password = SecretProtector.UnprotectRemoteLinkPassword(record.ProtectedLinkPassword);
        if (record.Kind == RemoteItemKind.WebDownload && entry.TransferPolicy.CreationSpacing > TimeSpan.Zero)
        {
            // Reserved before the request, and not given back: a request that fails may still count against the limit.
            await ChangePacingAsync(work, pacing => pacing with { NextWebCreateAt = now + entry.TransferPolicy.CreationSpacing }).ConfigureAwait(false);
        }

        var submitting = await UpdateAsync(record.Id, latest => latest.State is RemoteJobState.Intended or RemoteJobState.Blocked
            ? WithoutAccountProblem(latest) with { State = RemoteJobState.Submitting, SubmittedAt = now, Cached = cached, ApprovalNeeded = false, NextCheckAt = null }
            : null).ConfigureAwait(false);
        if (submitting is null)
        {
            return;
        }

        await SendAsync(work, record.Id, submitter, new RemoteSubmission
        {
            Kind = record.Kind,
            Link = link,
            TorrentFile = torrent,
            LinkPassword = password,
            OnlyIfCached = !record.ApprovedUncached,
        }, hash, pass, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(
        AccountWork work,
        Guid id,
        IRemoteSubmitter submitter,
        RemoteSubmission submission,
        string hash,
        PassScope pass,
        CancellationToken cancellationToken)
    {
        RemoteSubmissionResult? result = null;
        RemoteProviderException? failure = null;
        try
        {
            result = await submitter.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteProviderException ex)
        {
            failure = ex;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down with the answer unknown. The record stays in Submitting, which a restart reconciles.
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Remote job {id:N}: the submission failed: {ex.GetType().Name}.");
            failure = new RemoteProviderException(RemoteFailureKind.Network, null, $"{work.Entry.DisplayName} could not be reached, and may have received the request.");
        }

        pass.Forget(submission.Kind);
        try
        {
            // The slot was reserved before the request, which can have waited for a long time on the
            // provider's request budget. What counts against the limit is when the request went out.
            var spacing = work.Entry.TransferPolicy.CreationSpacing;
            if (submission.Kind == RemoteItemKind.WebDownload && spacing > TimeSpan.Zero)
            {
                var earliest = _time.GetUtcNow() + spacing;
                await ChangePacingAsync(work, pacing => pacing.NextWebCreateAt is { } slot && slot >= earliest
                    ? pacing
                    : pacing with { NextWebCreateAt = earliest }).ConfigureAwait(false);
            }

            if (failure is not null)
            {
                await SubmissionFailedAsync(work, id, submission, failure, pass).ConfigureAwait(false);
            }
            else
            {
                await SubmissionAnsweredAsync(work.Entry, id, result!, hash).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What the provider said couldn't be saved, and Submitting isn't polled. In memory only, the
            // list decides instead; a restart reaches the same place from the Submitting record on disk.
            _log?.Invoke($"Remote job {id:N}: could not save the answer: {ex.GetType().Name}.");
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_records.TryGetValue(id, out var current) && current.State == RemoteJobState.Submitting)
                    {
                        _records[id] = current with { State = RemoteJobState.SubmissionUnknown, NextCheckAt = _time.GetUtcNow() + StoreTroublePause };
                    }
                }
            }
            finally { _gate.Release(); }

            work.StoreTroubleUntil = _time.GetUtcNow() + StoreTroublePause;
        }
    }

    private Task SubmissionAnsweredAsync(RemoteProviderEntry entry, Guid id, RemoteSubmissionResult result, string hash)
    {
        var now = _time.GetUtcNow();
        return UpdateAsync(id, record =>
        {
            if (record.State != RemoteJobState.Submitting)
            {
                return null;
            }

            if (result.Ref is { } reference)
            {
                return Settled(record) with
                {
                    State = RemoteJobState.Waiting,
                    ItemId = reference.ItemId,
                    PredictedHash = result.Hash ?? hash,
                    NextCheckAt = now,
                };
            }

            if (result.Queued)
            {
                return Settled(record) with
                {
                    State = RemoteJobState.Waiting,
                    Stage = RemoteStage.Queued,
                    PredictedHash = result.Hash ?? hash,
                    NextCheckAt = now + RemoteJobRules.NextPoll(null, policy: entry.TransferPolicy),
                };
            }

            // Accepted, it seems, but it didn't say what as. The provider has a minute from now to list it.
            return record with { State = RemoteJobState.SubmissionUnknown, SubmittedAt = now, NextCheckAt = now };
        });
    }

    private async Task SubmissionFailedAsync(AccountWork work, Guid id, RemoteSubmission submission, RemoteProviderException failure, PassScope pass)
    {
        var entry = work.Entry;
        var now = _time.GetUtcNow();
        var message = Describe(entry, failure, submission.LinkPassword, submission.Link);
        var outcome = RemoteJobRules.AfterSubmitFailure(failure.Kind);
        var rateLimited = failure.Kind == RemoteFailureKind.RateLimited;
        var hold = RemoteJobRules.RateLimitHold(failure.RetryAfter);
        if (rateLimited)
        {
            pass.RateLimited = true;
            await ChangePacingAsync(work, pacing => pacing with { RateLimitedUntil = now + hold }).ConfigureAwait(false);
        }

        if (outcome == RemoteSubmitOutcome.FailedAndStopCreating)
        {
            await ChangePacingAsync(work, pacing => pacing with { CreationStoppedReason = message }).ConfigureAwait(false);
        }

        await UpdateAsync(id, record =>
        {
            if (record.State != RemoteJobState.Submitting)
            {
                return null;
            }

            var failures = record.ConsecutiveFailures + 1;
            return outcome switch
            {
                RemoteSubmitOutcome.SubmissionUnknown => record with
                {
                    State = RemoteJobState.SubmissionUnknown,
                    // The grace counts from when the answer was lost, not from before a wait to send it.
                    SubmittedAt = now,
                    FailureKind = failure.Kind,
                    FailureMessage = message,
                    NextCheckAt = now,
                },
                RemoteSubmitOutcome.TryAgainLater => record with
                {
                    State = RemoteJobState.Intended,
                    ConsecutiveFailures = failures,
                    FailureKind = failure.Kind,
                    FailureMessage = message,
                    NextCheckAt = now + (rateLimited ? hold : RemoteJobRules.Backoff(failures, _options.Jitter())),
                },
                RemoteSubmitOutcome.Blocked => record with
                {
                    State = RemoteJobState.Blocked,
                    ConsecutiveFailures = failures,
                    FailureKind = failure.Kind,
                    FailureMessage = message,
                    NextCheckAt = now + RemoteJobRules.Backoff(failures, _options.Jitter()),
                },
                RemoteSubmitOutcome.NeedsApproval => record with
                {
                    State = RemoteJobState.Intended,
                    ApprovalNeeded = true,
                    Cached = false,
                    FailureKind = null,
                    FailureMessage = null,
                    NextCheckAt = null,
                },
                _ => record with
                {
                    State = RemoteJobState.Failed,
                    FailureKind = failure.Kind,
                    FailureMessage = message,
                    NextCheckAt = null,
                    // It wasn't the right one, and it won't become so.
                    ProtectedLinkPassword = failure.Kind == RemoteFailureKind.LinkPasswordRequired ? null : record.ProtectedLinkPassword,
                },
            };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The answer to a submission never came. The provider's list decides: found means it was
    /// accepted, and only absent after the grace period goes back to being submitted, through the
    /// same checks as the first time. A failed look at the list changes nothing.
    /// </summary>
    private async Task UnknownStepAsync(AccountWork work, RemoteJobRecord record, PassScope pass, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        if (entry.AccountProblem() is { } accountProblem)
        {
            await UpdateAsync(record.Id, latest => latest.State == RemoteJobState.SubmissionUnknown
                ? latest with
                {
                    FailureKind = RemoteFailureKind.Authentication,
                    FailureMessage = accountProblem,
                    NextCheckAt = _time.GetUtcNow() + RemoteJobRules.LongestIdlePoll,
                }
                : null).ConfigureAwait(false);
            return;
        }

        if (record.PredictedHash is not { } hash)
        {
            await FailAsync(record.Id, RemoteFailureKind.Rejected,
                $"This may or may not have reached {entry.DisplayName}, and it can't be looked up. Check your account.").ConfigureAwait(false);
            return;
        }

        var found = await FindByHashAsync(entry, record.Kind, hash, pass, cancellationToken).ConfigureAwait(false);
        if (await AdoptIfFoundAsync(work, record, found, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (found.Kind == FindKind.Inconclusive)
        {
            await TooManyItemsAsync(entry, record.Id).ConfigureAwait(false);
            return;
        }

        var now = _time.GetUtcNow();
        var graceEnds = (record.SubmittedAt ?? now) + RemoteJobRules.RegistrationGrace;
        if (now < graceEnds)
        {
            await UpdateAsync(record.Id, latest => latest.State == RemoteJobState.SubmissionUnknown
                ? latest with { NextCheckAt = graceEnds }
                : null).ConfigureAwait(false);
            return;
        }

        if (record.Resubmits >= _options.MostResubmits)
        {
            await FailAsync(record.Id, RemoteFailureKind.ServiceError,
                $"{entry.DisplayName} didn't list this after {record.Resubmits + 1} tries. It won't be sent again until you retry.").ConfigureAwait(false);
            return;
        }

        await UpdateAsync(record.Id, latest => latest.State == RemoteJobState.SubmissionUnknown
            ? latest with
            {
                State = RemoteJobState.Intended,
                Resubmits = latest.Resubmits + 1,
                FailureKind = null,
                FailureMessage = null,
                NextCheckAt = now,
            }
            : null).ConfigureAwait(false);
    }

    /// <summary>The account's own list, then its queue, by hash. Failures are left to the caller and never mean absent.</summary>
    private static async Task<FindAnswer> FindByHashAsync(RemoteProviderEntry entry, RemoteItemKind kind, string hash, PassScope pass, CancellationToken cancellationToken)
    {
        if (entry.Catalog is not { } catalog)
        {
            return new FindAnswer(FindKind.Inconclusive);
        }

        var (items, listComplete) = await pass.ListAsync(catalog, kind, cancellationToken).ConfigureAwait(false);
        // One hash can have several items, such as an old failure and a newer success. What can be
        // downloaded comes first, then what is still working, and the latest of those wins.
        var matches = items.Where(item => RemoteJobRules.SameHash(item, hash)).ToList();
        var match = matches.Find(item => item.Stage == RemoteStage.Ready)
            ?? matches.FindLast(item => item.Stage != RemoteStage.Failed)
            ?? matches.LastOrDefault();
        if (match is not null)
        {
            return new FindAnswer(FindKind.Found, match);
        }

        var queueComplete = true;
        if (entry.Queue is { } queue)
        {
            var (waiting, complete) = await pass.QueueAsync(queue, kind, cancellationToken).ConfigureAwait(false);
            queueComplete = complete;
            if (waiting.Any(queued => string.Equals(queued.Hash, hash, StringComparison.OrdinalIgnoreCase)))
            {
                return new FindAnswer(FindKind.InQueue);
            }
        }

        return listComplete && queueComplete ? new FindAnswer(FindKind.Absent) : new FindAnswer(FindKind.Inconclusive);
    }

    private async Task<bool> AdoptIfFoundAsync(AccountWork work, RemoteJobRecord record, FindAnswer found, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        switch (found.Kind)
        {
            case FindKind.Found:
                var item = found.Item!;
                var adopted = await UpdateAsync(record.Id, latest => RemoteJobRules.IsPolled(latest.State)
                    ? Settled(latest) with
                    {
                        State = RemoteJobState.Waiting,
                        ItemId = item.Ref.ItemId,
                        PredictedHash = item.Hash ?? latest.PredictedHash,
                        NextCheckAt = now,
                    }
                    : null).ConfigureAwait(false);
                if (adopted is not null)
                {
                    await ApplyItemAsync(work, record.Id, item, cancellationToken).ConfigureAwait(false);
                }

                return true;

            case FindKind.InQueue:
                await UpdateAsync(record.Id, latest => RemoteJobRules.IsPolled(latest.State)
                    ? Settled(latest) with
                    {
                        State = RemoteJobState.Waiting,
                        Stage = RemoteStage.Queued,
                        NextCheckAt = now + RemoteJobRules.NextPoll(null, policy: work.Entry.TransferPolicy),
                    }
                    : null).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    /// <summary>More items than can be read. Nothing can be called absent, so nothing is sent; the user sees why.</summary>
    private Task TooManyItemsAsync(RemoteProviderEntry entry, Guid id)
        => UpdateAsync(id, record =>
        {
            var failures = record.ConsecutiveFailures + 1;
            return RemoteJobRules.IsPolled(record.State)
                ? record with
                {
                    ConsecutiveFailures = failures,
                    FailureKind = RemoteFailureKind.ServiceError,
                    FailureMessage = $"Your {entry.DisplayName} account lists too many items to check whether this one is there, so it wasn't sent.",
                    NextCheckAt = _time.GetUtcNow() + RemoteJobRules.Backoff(failures, _options.Jitter()),
                }
                : null;
        });

    private static RemoteJobRecord Settled(RemoteJobRecord record) => record with
    {
        ApprovalNeeded = false,
        FailureKind = null,
        FailureMessage = null,
        ConsecutiveFailures = 0,
        MissingSince = null,
    };

    // ---- Waiting -------------------------------------------------------------------------------

    private async Task PollStepAsync(AccountWork work, RemoteJobRecord record, PassScope pass, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        if (entry.AccountProblem() is { } accountProblem)
        {
            await UpdateAsync(record.Id, latest => latest.State == RemoteJobState.Waiting
                ? latest with
                {
                    FailureKind = RemoteFailureKind.Authentication,
                    FailureMessage = accountProblem,
                    NextCheckAt = _time.GetUtcNow() + RemoteJobRules.LongestIdlePoll,
                }
                : null).ConfigureAwait(false);
            return;
        }

        if (entry.Catalog is not { } catalog)
        {
            await FailAsync(record.Id, RemoteFailureKind.Rejected, $"{entry.DisplayName} can't list items from here.").ConfigureAwait(false);
            return;
        }

        if (record.ItemId is null)
        {
            // Accepted into the provider's queue, so it has no item id until it leaves.
            await LookForSubmittedAsync(work, record, pass, cancellationToken).ConfigureAwait(false);
            return;
        }

        var reference = new RemoteItemRef(record.Provider, record.Kind, record.ItemId);
        RemoteItem? item = null;
        if (pass.DuePolls.GetValueOrDefault(record.Kind) >= 3)
        {
            var (items, _) = await pass.ListAsync(catalog, record.Kind, cancellationToken).ConfigureAwait(false);
            item = items.FirstOrDefault(candidate => candidate.Ref.ItemId == record.ItemId);
        }

        item ??= await catalog.GetAsync(reference, fresh: true, cancellationToken).ConfigureAwait(false);
        if (item is not null)
        {
            if (item.Ref.ItemId != record.ItemId)
            {
                // Says nothing about this item, so it counts as a failed look, and the next one waits.
                throw new RemoteProviderException(RemoteFailureKind.BadResponse, null, $"{entry.DisplayName} answered about a different item.");
            }

            await ApplyItemAsync(work, record.Id, item, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (record.PredictedHash is null)
        {
            // It has never been seen on this account, and nothing was submitted. A link from someone else's account is the usual reason.
            await FailAsync(record.Id, RemoteFailureKind.NotFound, NotOnAccount(entry)).ConfigureAwait(false);
            return;
        }

        await LookForSubmittedAsync(work, record, pass, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An item that should exist and wasn't where expected: its list, then its queue, then a minute's grace.</summary>
    private async Task LookForSubmittedAsync(AccountWork work, RemoteJobRecord record, PassScope pass, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var found = await FindByHashAsync(entry, record.Kind, record.PredictedHash!, pass, cancellationToken).ConfigureAwait(false);
        if (await AdoptIfFoundAsync(work, record, found, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (found.Kind == FindKind.Inconclusive)
        {
            await TooManyItemsAsync(entry, record.Id).ConfigureAwait(false);
            return;
        }

        var now = _time.GetUtcNow();
        var missingSince = record.MissingSince ?? now;
        var graceEnds = missingSince + RemoteJobRules.RegistrationGrace;
        if (now >= graceEnds)
        {
            await FailAsync(record.Id, RemoteFailureKind.NotFound, NotOnAccount(entry)).ConfigureAwait(false);
            return;
        }

        await UpdateAsync(record.Id, latest => latest.State == RemoteJobState.Waiting
            ? latest with { MissingSince = missingSince, NextCheckAt = graceEnds }
            : null).ConfigureAwait(false);
    }

    /// <summary>Saves what the provider says about an item, and starts local jobs if it says they can start.</summary>
    private async Task ApplyItemAsync(AccountWork work, Guid id, RemoteItem item, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var now = _time.GetUtcNow();
        var outcome = RemoteJobRules.AfterItemRead(item.Stage);
        var rawState = Words(entry, item.RawState, 100);
        var name = Words(entry, item.Name, 300);
        var updated = await UpdateAsync(id, record =>
        {
            if (record.State != RemoteJobState.Waiting || record.ItemId != item.Ref.ItemId)
            {
                return null;
            }

            var unchanged = record.Stage == item.Stage && record.RawState == rawState && record.Progress.Equals(item.Progress)
                ? record.UnchangedPolls + 1
                : 0;
            var next = Settled(record) with
            {
                Stage = item.Stage,
                RawState = rawState,
                Progress = item.Progress,
                SpeedBytesPerSecond = item.SpeedBytesPerSecond,
                EtaSeconds = item.EtaSeconds,
                Name = name ?? record.Name,
                Cached = record.Cached || item.Cached,
                PredictedHash = record.PredictedHash ?? item.Hash,
                UnchangedPolls = unchanged,
            };
            return outcome switch
            {
                RemoteItemOutcome.Failed => next with
                {
                    State = RemoteJobState.Failed,
                    FailureKind = RemoteFailureKind.Rejected,
                    FailureMessage = $"{entry.DisplayName} reports that this item failed: {Words(entry, item.Error ?? item.RawState, 500, SecretsOf(record)) ?? "no reason given"}.",
                    NextCheckAt = null,
                },
                RemoteItemOutcome.Ready when record.SelectionMode == RemoteSelectionMode.AskWhenReady => next with
                {
                    State = RemoteJobState.AwaitingSelection,
                    NextCheckAt = null,
                },
                // Still Waiting until the jobs exist, so a crash anywhere before that comes back here.
                RemoteItemOutcome.Ready => next with { ConsecutiveFailures = record.ConsecutiveFailures, NextCheckAt = now + ReadyRetry },
                _ => next with { NextCheckAt = now + RemoteJobRules.NextPoll(item.EtaSeconds, unchanged, entry.TransferPolicy) },
            };
        }).ConfigureAwait(false);

        if (outcome == RemoteItemOutcome.Ready && updated?.State == RemoteJobState.Waiting)
        {
            await CreateChildrenAsync(work, updated, item, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- Local jobs ----------------------------------------------------------------------------

    /// <summary>
    /// Can run any number of times for one item: each file is claimed by this record's id and the
    /// file's id, so a repeat finds its job, and the package is keyed on this record. LocalStarted is
    /// only saved when all of it holds.
    /// </summary>
    private async Task CreateChildrenAsync(AccountWork work, RemoteJobRecord record, RemoteItem item, CancellationToken cancellationToken)
    {
        var entry = work.Entry;
        var destination = record.Destination ?? _host.DefaultDownloadDirectory;
        var plan = RemoteChildPlanner.Build(record, item, entry.DisplayName, entry.TransferPolicy.MaxConnectionsPerFile, destination);
        if (plan.Requests.Count == 0)
        {
            await UpdateAsync(record.Id, latest => latest.State == RemoteJobState.Waiting ? NothingToCreate(latest, record, plan.Notes) : null).ConfigureAwait(false);
            return;
        }

        RemoteJobsResult result;
        try
        {
            result = await _host.AddRemoteJobsAsync(
                plan.Requests,
                record.Placement?.PackageKey ?? $"remote:{record.Id:N}",
                record.Placement is { } placement ? placement.PackageName : record.Name,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Remote job {record.Id:N}: could not create its downloads: {ex.GetType().Name}.");
            await ChildrenNotReadyAsync(record.Id, "Could not create the downloads. This will be tried again.").ConfigureAwait(false);
            return;
        }

        if (result.Items.Count == 0)
        {
            // The batch was refused whole, and repeating it won't change why.
            await UpdateAsync(record.Id, latest => latest.State == RemoteJobState.Waiting
                ? latest with
                {
                    State = RemoteJobState.Failed,
                    FailureKind = RemoteFailureKind.Rejected,
                    FailureMessage = RemoteText.Clean(result.Error, 500) ?? "The downloads could not be created.",
                    Notes = plan.Notes,
                    NextCheckAt = null,
                }
                : null).ConfigureAwait(false);
            return;
        }

        var notes = plan.Notes.ToList();
        notes.AddRange(result.Notes);
        var leftover = Words(entry, item.Error, 300);
        if (leftover is not null)
        {
            notes.Add($"{entry.DisplayName} also reports: {leftover}");
        }

        var accepted = 0;
        string? trouble = result.Error;
        for (var i = 0; i < result.Items.Count; i++)
        {
            var answer = result.Items[i];
            if (answer.Accepted)
            {
                accepted++;
            }
            else if (answer.ErrorCode == "invalid_path")
            {
                notes.Add($"Skipped {RemoteText.Clean(plan.Requests[i].Source.RelativePath, 300)}: its path leaves the download folder.");
            }
            else if (answer.ErrorCode == RemoteJobsResult.UnusablePathCode)
            {
                notes.Add($"Skipped {RemoteText.Clean(plan.Requests[i].Source.RelativePath, 300)}: this path can't be created in the download folder.");
            }
            else
            {
                trouble ??= answer.Error ?? "A download could not be created.";
            }
        }

        if (trouble is not null)
        {
            await ChildrenNotReadyAsync(record.Id, RemoteText.Clean(trouble, 500)!).ConfigureAwait(false);
            return;
        }

        var packageId = result.PackageId;
        if (packageId is { } package && !await PassPasswordToPackageAsync(record.Id, package).ConfigureAwait(false))
        {
            return;
        }

        var at = _time.GetUtcNow();
        await UpdateAsync(record.Id, latest =>
        {
            if (latest.State != RemoteJobState.Waiting)
            {
                return null;
            }

            // The user chose again while these were being made. The next pass plans from that choice.
            if (!SameChoice(latest, record))
            {
                return latest with { PackageId = packageId ?? latest.PackageId, NextCheckAt = at };
            }

            return accepted > 0
                ? Settled(latest) with
                {
                    State = RemoteJobState.LocalStarted,
                    PackageId = packageId ?? latest.PackageId,
                    Notes = notes,
                    NextCheckAt = null,
                }
                : NothingToCreate(latest, record, notes);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The package id is saved first, so everything after this can be repeated. The files may already be
    /// downloading. The archive side copes with a password that arrives after a run has started without one.
    /// </summary>
    /// <returns>False when it couldn't be done, and the whole step is tried again later.</returns>
    private async Task<bool> PassPasswordToPackageAsync(Guid id, Guid package)
    {
        RemoteJobRecord? current;
        lock (_sync) _records.TryGetValue(id, out current);
        if (current?.ProtectedArchivePassword is null || _options.PackagePasswords is null)
        {
            return true;
        }

        await UpdateAsync(id, latest => latest.State == RemoteJobState.Waiting && latest.PackageId != package
            ? latest with { PackageId = package }
            : null).ConfigureAwait(false);
        if (await HandOverArchivePasswordAsync(id).ConfigureAwait(false) is not { } problem)
        {
            return true;
        }

        await ChildrenNotReadyAsync(id, problem).ConfigureAwait(false);
        return false;
    }

    private static bool SameChoice(RemoteJobRecord one, RemoteJobRecord other)
        => one.SelectionMode == other.SelectionMode && one.SelectedFileIds.SequenceEqual(other.SelectedFileIds);

    /// <summary>
    /// No file of this choice can be downloaded. That ends a job with nothing downloading, in a way a
    /// new choice can undo. A job whose earlier files have local jobs goes on, and only hears about it.
    /// </summary>
    private RemoteJobRecord NothingToCreate(RemoteJobRecord latest, RemoteJobRecord planned, IReadOnlyList<string> notes)
    {
        if (!SameChoice(latest, planned))
        {
            return latest with { NextCheckAt = _time.GetUtcNow() };
        }

        return latest.PackageId is not null
            ? latest with
            {
                State = RemoteJobState.LocalStarted,
                Notes = [.. notes, $"{NoChosenFiles} The downloads already created go on as they were."],
                NextCheckAt = null,
            }
            : latest with
            {
                State = RemoteJobState.Failed,
                FailureKind = RemoteFailureKind.Rejected,
                FailureMessage = NoChosenFiles,
                Notes = notes,
                NextCheckAt = null,
            };
    }

    private Task ChildrenNotReadyAsync(Guid id, string message)
        => UpdateAsync(id, record =>
        {
            var failures = record.ConsecutiveFailures + 1;
            return record.State == RemoteJobState.Waiting
                ? record with
                {
                    ConsecutiveFailures = failures,
                    FailureMessage = message,
                    NextCheckAt = _time.GetUtcNow() + RemoteJobRules.Backoff(failures, _options.Jitter()),
                }
                : null;
        });

    // ---- Words ---------------------------------------------------------------------------------

    private Task FailAsync(Guid id, RemoteFailureKind kind, string message)
        => UpdateAsync(id, record => RemoteJobRules.IsTerminal(record.State)
            ? null
            : record with { State = RemoteJobState.Failed, FailureKind = kind, FailureMessage = message, ApprovalNeeded = false, NextCheckAt = null });

    internal static string NotOnAccount(RemoteProviderEntry entry)
        => $"This item is not on your {entry.DisplayName} account. A folder link only works for the account it came from, and the item may have been removed.";

    /// <summary>A key that was missing when this was last looked at, and isn't now, is no longer what is wrong.</summary>
    private static RemoteJobRecord WithoutAccountProblem(RemoteJobRecord record)
        => record.FailureKind == RemoteFailureKind.Authentication ? record with { FailureKind = null, FailureMessage = null } : record;

    /// <summary>A provider's words, with the key and these secrets out of them, whatever form they come back in.</summary>
    private static string? Words(RemoteProviderEntry entry, string? words, int longest, IEnumerable<string?>? hide = null)
        => RemoteText.Clean(words, longest, entry.Account is { } account ? account.Mask : null, hide);

    /// <summary>What was submitted for this job. Only for taking out of a provider's words; these go nowhere else.</summary>
    private static string?[] SecretsOf(RemoteJobRecord record) =>
    [
        SecretProtector.UnprotectRemoteInput(record.ProtectedInput),
        SecretProtector.UnprotectRemoteLinkPassword(record.ProtectedLinkPassword),
        SecretProtector.UnprotectRemoteArchivePassword(record.ProtectedArchivePassword),
    ];

    /// <summary>The real cause in plain words, safe for a saved record and for every client.</summary>
    internal static string Describe(RemoteProviderEntry entry, RemoteProviderException failure, string? password = null, string? link = null)
    {
        var said = Words(entry, failure.Message, 500, [password, link]) ?? "no reason given";
        var name = entry.DisplayName;
        return failure.Kind switch
        {
            RemoteFailureKind.Authentication => $"{name} refused the API key. Check it in Options, then try again. ({said})",
            RemoteFailureKind.AccountLimit => $"Your {name} account doesn't allow this: {said}. Nothing new will be added until you retry.",
            RemoteFailureKind.AccountBusy => $"Your {name} account is at its limit for now, so this will be tried again by itself: {said}",
            RemoteFailureKind.LinkPasswordRequired => string.IsNullOrEmpty(password)
                ? "This link needs a password. Enter it, then retry."
                : "That password wasn't accepted. Enter it again, then retry.",
            RemoteFailureKind.RateLimited => $"{name} is asking for fewer requests, so this will be tried again by itself. ({said})",
            RemoteFailureKind.NotFound => $"{name} has no such item: {said}",
            RemoteFailureKind.Rejected => $"{name} refused this: {said}",
            RemoteFailureKind.ServiceError => $"{name} is having problems, so this will be tried again by itself: {said}",
            RemoteFailureKind.Network => $"{name} could not be reached, so this will be tried again by itself.",
            RemoteFailureKind.NotCached => $"This is not cached on {name}.",
            RemoteFailureKind.AlreadyExists => $"{name} says this is already on your account.",
            _ => $"{name} sent an answer this app can't read. This will be tried again by itself.",
        };
    }
}
