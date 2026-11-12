using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    private readonly SemaphoreSlim _scheduleEvaluationGate = new(1, 1);
    private bool _startupSchedulesEvaluated;
    private readonly HashSet<Guid> _startupSchedulesPending = new();

    public async Task<IReadOnlyList<ScheduleAction>> EvaluateSchedulesAsync(DateTimeOffset localNow, CancellationToken cancellationToken = default)
    {
        await _scheduleEvaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProcessQueueRetriesAsync(localNow, cancellationToken).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(localNow.DateTime);
            var time = TimeOnly.FromDateTime(localNow.DateTime);
            var due = new List<(Guid Id, int QueueId, string Action, DateOnly Window, bool Timed, bool Synchronize)>();
            // Stops that came due while their queue was idle: recorded as done, so a later start isn't stopped (M8).
            var passedStops = new List<(Guid Id, DateOnly StopDate)>();
            lock (_sync)
            {
                if (!_initialized || _shuttingDown) return Array.Empty<ScheduleAction>();
                if (!_startupSchedulesEvaluated)
                {
                    foreach (var schedule in _settings.Schedules.Where(schedule => schedule.Enabled && schedule.StartOnHostStartup))
                        _startupSchedulesPending.Add(schedule.Id);
                    _startupSchedulesEvaluated = true;
                }
                _startupSchedulesPending.RemoveWhere(id => !_settings.Schedules.Any(schedule => schedule.Id == id && schedule.Enabled && schedule.StartOnHostStartup));
                foreach (var schedule in _settings.Schedules.Where(item => item.Enabled))
                {
                    var queue = _settings.Queues.FirstOrDefault(item => item.Id == schedule.QueueId);
                    if (queue is null || !TryParseScheduleTime(schedule.StartTime, out var start)) continue;
                    var hasStop = TryParseScheduleTime(schedule.StopTime, out var stop);
                    var overnight = schedule.TimedStartEnabled && hasStop && stop <= start;
                    DateOnly? window = null;
                    if (time >= start && (!hasStop || overnight || time < stop) && Eligible(schedule, today)) window = today;
                    else if (overnight && time < stop && Eligible(schedule, today.AddDays(-1))) window = today.AddDays(-1);
                    // From the stop time until midnight (or the next start, overnight): a start at host startup waits.
                    var stopDue = hasStop && (overnight ? time >= stop && time < start && Eligible(schedule, today.AddDays(-1))
                        : time >= stop && Eligible(schedule, today));
                    // The stop that ends a window acts once, the first time it is seen after its time (audit 2 M8): on a
                    // queue still running it stops it, on an idle one it is only recorded, so a start by hand later that
                    // day is not stopped by it. LastStopDate is the date the stop time fell on.
                    DateOnly? stopDate = null;
                    if (stopDue) stopDate = today;
                    // Audit 2 L8: a PC asleep (or off) from before the stop time until after it, by any number of days,
                    // still stops the run this schedule started: the stop of its last timed start never acted. Not
                    // while a window of it is open now: that window's own stop ends the run.
                    else if (hasStop && window is null && schedule.LastStartDate is { } started)
                    {
                        var missedStop = overnight ? started.AddDays(1) : started;
                        if ((schedule.LastStopDate is null || schedule.LastStopDate < missedStop) &&
                            (missedStop < today || missedStop == today && time >= stop))
                            stopDate = missedStop;
                    }
                    if (stopDate is { } stopDay && schedule.LastStopDate != stopDay && (schedule.LastStopDate is null || schedule.LastStopDate < stopDay))
                    {
                        // A stopped queue may still have torrents seeding from before its stop time: the stop stops them
                        // too (T2). Seeding started after the stop time (by hand) is left alone.
                        if (queue.IsRunning || QueueSeededBeforeLocked(queue.Id, new DateTimeOffset(stopDay.ToDateTime(stop), localNow.Offset)))
                        {
                            due.Add((schedule.Id, queue.Id, "stop", stopDay, false, false));
                            continue;
                        }
                        passedStops.Add((schedule.Id, stopDay));
                    }
                    var timed = schedule.TimedStartEnabled && window is { } day &&
                        (schedule.LastStartDate != day || schedule.RepeatEveryMinutes is { } interval &&
                         (schedule.LastRunAt is null || localNow - schedule.LastRunAt >= TimeSpan.FromMinutes(interval)));
                    if (timed || _startupSchedulesPending.Contains(schedule.Id) && !stopDue)
                        due.Add((schedule.Id, queue.Id, "start", window ?? today, timed, schedule.RepeatEveryMinutes is not null));
                }
            }
            if (passedStops.Count > 0) await RecordPassedStopsAsync(passedStops, cancellationToken).ConfigureAwait(false);
            var results = new List<ScheduleAction>();
            foreach (var item in due)
            {
                lock (_sync)
                    if (_shuttingDown) break;
                var warning = item.Synchronize ? await SynchronizeQueueAsync(item.Id, item.QueueId, cancellationToken).ConfigureAwait(false) : null;
                await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ScheduleRule? schedule;
                    lock (_sync) schedule = _settings.Schedules.FirstOrDefault(candidate => candidate.Id == item.Id && candidate.Enabled && candidate.QueueId == item.QueueId);
                    if (schedule is null) continue;
                    // Audit 2 M10: a start arms this run's when-done actions inside the action, before anything starts.
                    var reply = await QueueActionCoreAsync(new QueueActionRequest { QueueId = item.QueueId, Action = item.Action }, cancellationToken,
                        armForSchedule: item.Action == "start" ? item.Id : null).ConfigureAwait(false);
                    if (reply.Ok)
                    {
                        var previousStart = schedule.LastStartDate;
                        var previousStop = schedule.LastStopDate;
                        var previousRun = schedule.LastRunAt;
                        var previousError = schedule.LastRunError;
                        lock (_sync)
                        {
                            if (item.Action == "start")
                            {
                                if (item.Timed) schedule.LastStartDate = item.Window;
                                schedule.LastRunAt = localNow;
                            }
                            else schedule.LastStopDate = item.Window;
                            schedule.LastRunError = warning;
                        }
                        try { await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false); }
                        catch
                        {
                            lock (_sync)
                            {
                                schedule.LastStartDate = previousStart; schedule.LastStopDate = previousStop;
                                schedule.LastRunAt = previousRun; schedule.LastRunError = previousError;
                            }
                            throw;
                        }
                        if (item.Action == "start") _startupSchedulesPending.Remove(item.Id);
                        SettingsChanged?.Invoke(this, _settings);
                    }
                    results.Add(new ScheduleAction(item.Id, item.QueueId, item.Action, today, reply.Ok, reply.Error ?? warning));
                }
                finally { _settingsUpdateGate.Release(); }
            }
            return results;
        }
        finally
        {
            _scheduleEvaluationGate.Release();
            ArmSchedulerTimer();
        }

        static bool Eligible(ScheduleRule schedule, DateOnly date) => schedule.Kind.Equals("oneTime", StringComparison.OrdinalIgnoreCase)
            ? schedule.StartDate == date : MatchesDay(schedule.Days, date.DayOfWeek);
    }

    /// <summary>Saves stops that came due on an idle queue as done (audit 2 M8). Nothing else changes.</summary>
    private async Task RecordPassedStopsAsync(List<(Guid Id, DateOnly StopDate)> stops, CancellationToken cancellationToken)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = new List<(ScheduleRule Schedule, DateOnly? Before)>();
            lock (_sync)
            {
                foreach (var (id, stopDate) in stops)
                {
                    if (_settings.Schedules.FirstOrDefault(schedule => schedule.Id == id && schedule.Enabled) is not { } schedule ||
                        schedule.LastStopDate is { } last && last >= stopDate) continue;
                    previous.Add((schedule, schedule.LastStopDate));
                    schedule.LastStopDate = stopDate;
                }
            }
            if (previous.Count == 0) return;
            try { await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not saved: seen again at the next evaluation.
                lock (_sync) foreach (var (schedule, before) in previous) schedule.LastStopDate = before;
                _log?.Invoke($"Could not record a passed schedule stop: {ex.Message}");
                return;
            }
            SettingsChanged?.Invoke(this, _settings);
        }
        finally { _settingsUpdateGate.Release(); }
    }

    // Audit 2 M18: no standing 30-second tick. One timer is armed for the next instant anything can come due (a
    // schedule's start or stop time, a synchronisation repeat, a queue retry, a startup start waiting for midnight),
    // and none at all when nothing is pending. It is armed again after every evaluation and every settings change.
    private ITimer? _schedulerTimer;
    private DateTimeOffset? _schedulerWakeAt;
    private int _schedulerTickRunning;

    /// <summary>
    /// Longest wait while anything is pending. A resume or a clock change the Host reports wakes it at once
    /// (<see cref="NotifyClockChanged"/>); this bounds one nobody reported.
    /// </summary>
    private static readonly TimeSpan SchedulerLongestWait = TimeSpan.FromHours(1);

    /// <summary>A retry already due that couldn't run yet is looked at again after this.</summary>
    private static readonly TimeSpan SchedulerRecheck = TimeSpan.FromSeconds(30);

    /// <summary>When the scheduler next wakes, or null when nothing is pending (then no timer is armed).</summary>
    public DateTimeOffset? NextSchedulerWake
    {
        get { lock (_sync) return _schedulerWakeAt; }
    }

    private void OnSchedulerSettingsChanged(object? sender, AppSettings settings) => ArmSchedulerTimer();

    private void ArmSchedulerTimer()
    {
        lock (_sync)
        {
            if (!_initialized || _shuttingDown) return;
            var now = _timeProvider.GetUtcNow();
            var next = NextSchedulerWakeLocked(_timeProvider.GetLocalNow());
            if (next is { } at && at - now > SchedulerLongestWait) next = now + SchedulerLongestWait;
            _schedulerWakeAt = next;
            if (_schedulerCts is not { IsCancellationRequested: false }) return;
            if (next is not { } wake)
            {
                _schedulerTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            var delay = wake - now;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            _schedulerTimer ??= _timeProvider.CreateTimer(_ => _ = SchedulerTickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _schedulerTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private int _schedulerTickAgain;

    /// <summary>
    /// The wall clock jumped: the PC resumed from sleep, or its time or time zone changed. A timer counts only time
    /// awake, so the wake armed before could now be up to <see cref="SchedulerLongestWait"/> late. This evaluates at
    /// once, and that evaluation arms the next wake from the new time (audit 2 M18). The Host calls it.
    /// </summary>
    public void NotifyClockChanged()
    {
        lock (_sync)
            if (!_initialized || _shuttingDown) return;
        _ = SchedulerTickAsync();
    }

    private async Task SchedulerTickAsync()
    {
        var cts = _schedulerCts;
        if (cts is null || cts.IsCancellationRequested) return;
        // One evaluation at a time; the one running arms the next wake when it ends, and runs once more for a wake that
        // came while it ran (it may have read the time before the clock moved).
        if (Interlocked.Exchange(ref _schedulerTickRunning, 1) == 1)
        {
            Interlocked.Exchange(ref _schedulerTickAgain, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _schedulerTickAgain, 0);
                try
                {
                    await EvaluateSchedulesAsync(_timeProvider.GetLocalNow(), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    _log?.Invoke($"Scheduler tick failed: {ex.Message}");
                    ArmSchedulerTimer();
                }
            }
            while (Volatile.Read(ref _schedulerTickAgain) == 1 && !cts.IsCancellationRequested);
        }
        finally { Interlocked.Exchange(ref _schedulerTickRunning, 0); }
        // A wake between the last check and the line above.
        if (Volatile.Read(ref _schedulerTickAgain) == 1 && !cts.IsCancellationRequested) _ = SchedulerTickAsync();
    }

    private void StopSchedulerTimer()
    {
        lock (_sync)
        {
            _schedulerTimer?.Dispose();
            _schedulerTimer = null;
            _schedulerWakeAt = null;
        }
        SettingsChanged -= OnSchedulerSettingsChanged;
    }

    /// <summary>The next instant something can come due, from <paramref name="localNow"/>; null when nothing is pending.</summary>
    private DateTimeOffset? NextSchedulerWakeLocked(DateTimeOffset localNow)
    {
        DateTimeOffset? best = null;
        void Consider(DateTimeOffset at)
        {
            if (best is null || at < best) best = at;
        }

        var zone = _timeProvider.LocalTimeZone;
        var today = DateOnly.FromDateTime(localNow.DateTime);
        DateTimeOffset Instant(DateOnly date, TimeOnly time)
        {
            var local = date.ToDateTime(time);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        // A margin, so the evaluation reads a time at or past the one it waits for.
        DateTimeOffset At(DateOnly date, TimeOnly time) => Instant(date, time).AddMilliseconds(500);
        // Once the time itself has come (the evaluation acts on it), the next is tomorrow's, even within the margin:
        // a timer can fire a little early by the wall clock.
        DateTimeOffset NextAt(TimeOnly time) => Instant(today, time) > localNow ? At(today, time) : At(today.AddDays(1), time);

        foreach (var schedule in _settings.Schedules.Where(schedule => schedule.Enabled))
        {
            if (!_settings.Queues.Any(queue => queue.Id == schedule.QueueId)) continue;
            if (schedule.TimedStartEnabled && TryParseScheduleTime(schedule.StartTime, out var start)) Consider(NextAt(start));
            if (TryParseScheduleTime(schedule.StopTime, out var stop)) Consider(NextAt(stop));
            if (schedule.RepeatEveryMinutes is { } interval && schedule.LastRunAt is { } lastRun &&
                lastRun + TimeSpan.FromMinutes(interval) is var repeat && repeat > localNow)
                Consider(repeat);
        }

        // A start at host startup that a stop time held back goes when that stop's day ends (as the evaluation reads it).
        if (_startupSchedulesPending.Count > 0) Consider(At(today.AddDays(1), TimeOnly.MinValue));

        foreach (var runtime in _jobs.Values)
        {
            if (runtime.Job.QueueRetryAt is not { } retryAt || !runtime.Job.IsQueued ||
                !_settings.Queues.Any(queue => queue.Id == runtime.Job.QueueId && queue.IsRunning)) continue;
            Consider(retryAt > localNow ? retryAt : localNow + SchedulerRecheck);
        }

        return best;
    }

    private async Task ProcessQueueRetriesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<Guid> ids;
        lock (_sync) ids = _jobs.Values.Where(runtime => runtime.Job.QueueRetryAt <= now).Select(runtime => runtime.Job.Id).ToList();
        foreach (var id in ids)
        {
            await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    JobRuntime? runtime;
                    DateTimeOffset? retryAt;
                    lock (_sync)
                    {
                        if (!_jobs.TryGetValue(id, out runtime) || runtime.Starting || runtime.Runner is { IsCompleted: false } ||
                            !runtime.Job.IsQueued || !runtime.Job.QueueProcessing || runtime.Job.State != DownloadState.Failed || runtime.Job.QueueRetryAt is null || runtime.Job.QueueRetryAt > now ||
                            !_settings.Queues.Any(queue => queue.Id == runtime.Job.QueueId && queue.IsRunning)) continue;
                        retryAt = runtime.Job.QueueRetryAt;
                        var limit = _settings.Queues.First(queue => queue.Id == runtime.Job.QueueId).RetryLimit;
                        if (limit is { } count && runtime.Job.QueueRetryCount > count)
                        {
                            runtime.Job.QueueRetryAt = null;
                            runtime.Job.Touch();
                        }
                        runtime.AdmissionSuspended = true;
                        if (runtime.Job.QueueRetryAt is not null) runtime.Job.State = DownloadState.Queued;
                        runtime.Job.QueueRetryAt = null;
                        runtime.Job.StartMode = StartMode.StartNow;
                        runtime.PauseRequested = false;
                        runtime.CancelRequested = false;
                        runtime.Job.Touch();
                    }
                    try { await PersistAsync(runtime, cancellationToken, requireSuccess: true, gateHeld: true).ConfigureAwait(false); }
                    catch
                    {
                        lock (_sync) { runtime.Job.State = DownloadState.Failed; runtime.Job.QueueRetryAt = retryAt; }
                        throw;
                    }
                    finally { lock (_sync) runtime.AdmissionSuspended = false; }
                    Raise(runtime, runtime.Job.State == DownloadState.Queued ? "queued" : "updated");
                }
                finally { _persistGate.Release(); }
            }
            finally { _settingsUpdateGate.Release(); }
        }
        Schedule();
    }

    private async Task<string?> SynchronizeQueueAsync(Guid scheduleId, int queueId, CancellationToken cancellationToken)
    {
        if (_engine is not IFileMetadataReader reader) return "The current engine cannot check synchronization metadata.";
        List<DownloadJob> completed;
        lock (_sync) completed = _jobs.Values.Where(runtime => runtime.Job.IsQueued && runtime.Job.QueueId == queueId && runtime.Job.State == DownloadState.Completed)
            .Select(runtime => runtime.Job.Clone()).ToList();
        var errors = 0;
        foreach (var original in completed)
        {
            // Their addresses are not this job's to probe or to store: a provider's expire, and
            // a pasted one is a secret that FinalUrl would write out in plain text.
            if (original.RemoteSource is not null || original.ProtectedUrl is not null) continue;
            if (!SupportsLiveSpeedLimit(original)) { errors++; continue; }
            FileMetadata metadata;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                metadata = await reader.ReadMetadataAsync(original, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { errors++; continue; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) { errors++; continue; }
            var comparable = false;
            var changed = !File.Exists(original.FinalFilePath) && !original.FileRemovedAfterExtraction;
            if (metadata.ETag is not null && original.ETag is not null)
            { comparable = true; changed |= metadata.ETag != original.ETag; }
            if (metadata.LastModified is not null && original.LastModified is not null)
            { comparable = true; changed |= metadata.LastModified != original.LastModified; }
            if (metadata.TotalBytes is { } length && original.TotalBytes is { } previousLength)
                changed |= length != previousLength;
            // Size alone cannot prove unchanged content. Without validators refresh conservatively.
            if (!changed && comparable) continue;
            await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    JobRuntime? runtime;
                    AppSettings staged;
                    lock (_sync)
                    {
                        if (!_settings.Schedules.Any(schedule => schedule.Id == scheduleId && schedule.Enabled && schedule.QueueId == queueId && schedule.RepeatEveryMinutes is not null)) continue;
                        if (!_jobs.TryGetValue(original.Id, out runtime) || runtime.Job.State != DownloadState.Completed ||
                            !runtime.Job.IsQueued || runtime.Job.QueueId != queueId || runtime.Job.CompletedAt != original.CompletedAt ||
                            runtime.Job.FinalFilePath != original.FinalFilePath) continue;
                        staged = _settings.WithDownloadGroups(CloneGroups(_settings.DownloadGroups));
                        staged.ArchivedDownloads = new(_settings.ArchivedDownloads);
                        if (!staged.ArchivedDownloads.Any(record => record.SourceJobId == original.Id && record.CompletedAt == original.CompletedAt))
                            staged.ArchivedDownloads.Add(new ArchivedDownloadRecord { Id = Guid.NewGuid(), SourceJobId = original.Id,
                                ReceivedBytes = original.ReceivedBytes, State = DownloadState.Completed,
                                CreatedAt = original.CreatedAt, CompletedAt = original.CompletedAt });
                        runtime.AdmissionSuspended = true;
                    }
                    try
                    {
                        await _settingsStore.SaveAsync(staged, cancellationToken).ConfigureAwait(false);
                        lock (_sync) _settings.ArchivedDownloads = staged.ArchivedDownloads;
                        await _checkpointStore.DeleteAsync(original.Id, cancellationToken).ConfigureAwait(false);
                        DeleteFileQuietly(original.PartFilePath);
                        lock (_sync)
                        {
                            runtime.Job.State = DownloadState.Queued;
                            runtime.Job.ReceivedBytes = 0;
                            runtime.Job.TotalBytes = metadata.TotalBytes;
                            runtime.Job.ETag = null;
                            runtime.Job.LastModified = null;
                            // An ordinary address can redirect to a provider's, whose key must not be
                            // stored. The next run follows the redirect again and needs nothing kept.
                            runtime.Job.FinalUrl = HoldsProviderSecret(metadata.FinalUrl) ? null : metadata.FinalUrl;
                            runtime.Job.CompletedAt = null;
                            runtime.Job.StartedAt = null;
                            runtime.Job.CompletionActionsStarted = false;
                            runtime.Job.ConflictPolicy = ConflictPolicy.Overwrite;
                            runtime.Job.QueueProcessing = true;
                            runtime.Job.QueueRetryCount = 0;
                            runtime.Job.QueueRetryAt = null;
                            runtime.Job.StartMode = StartMode.StartNow;
                            runtime.CancelRequested = false;
                            runtime.PauseRequested = false;
                            runtime.Job.Touch();
                        }
                        await PersistAsync(runtime, cancellationToken, requireSuccess: true, gateHeld: true).ConfigureAwait(false);
                        Raise(runtime, "queued");
                    }
                    catch
                    {
                        lock (_sync)
                        {
                            runtime.Job.State = original.State; runtime.Job.ReceivedBytes = original.ReceivedBytes;
                            runtime.Job.TotalBytes = original.TotalBytes; runtime.Job.ETag = original.ETag;
                            runtime.Job.LastModified = original.LastModified; runtime.Job.FinalUrl = original.FinalUrl;
                            runtime.Job.CompletedAt = original.CompletedAt; runtime.Job.StartedAt = original.StartedAt;
                            runtime.Job.CompletionActionsStarted = original.CompletionActionsStarted;
                            runtime.Job.ConflictPolicy = original.ConflictPolicy;
                            runtime.Job.QueueProcessing = original.QueueProcessing; runtime.Job.QueueRetryAt = original.QueueRetryAt;
                            runtime.Job.QueueRetryCount = original.QueueRetryCount; runtime.Job.StartMode = original.StartMode;
                        }
                        throw;
                    }
                    finally { lock (_sync) runtime.AdmissionSuspended = false; }
                }
                finally { _persistGate.Release(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { errors++; }
            finally { _settingsUpdateGate.Release(); }
        }
        return errors == 0 ? null : $"Synchronization could not refresh {errors} file(s). Existing completed files were kept. HTTP file downloads are supported.";
    }
}
