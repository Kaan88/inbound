using DownloadManager.Contracts;
using DownloadManager.Core;

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
                    var stopDue = hasStop && (overnight ? time >= stop && time < start && Eligible(schedule, today.AddDays(-1))
                        : time >= stop && Eligible(schedule, today));
                    if (stopDue && queue.IsRunning && schedule.LastStopDate != today)
                    {
                        due.Add((schedule.Id, queue.Id, "stop", today, false, false));
                        continue;
                    }
                    var timed = schedule.TimedStartEnabled && window is { } day &&
                        (schedule.LastStartDate != day || schedule.RepeatEveryMinutes is { } interval &&
                         (schedule.LastRunAt is null || localNow - schedule.LastRunAt >= TimeSpan.FromMinutes(interval)));
                    if (timed || _startupSchedulesPending.Contains(schedule.Id) && !stopDue)
                        due.Add((schedule.Id, queue.Id, "start", window ?? today, timed, schedule.RepeatEveryMinutes is not null));
                }
            }
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
                    var reply = await QueueActionCoreAsync(new QueueActionRequest { QueueId = item.QueueId, Action = item.Action }, cancellationToken).ConfigureAwait(false);
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
                            else schedule.LastStopDate = today;
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
        finally { _scheduleEvaluationGate.Release(); }

        static bool Eligible(ScheduleRule schedule, DateOnly date) => schedule.Kind.Equals("oneTime", StringComparison.OrdinalIgnoreCase)
            ? schedule.StartDate == date : MatchesDay(schedule.Days, date.DayOfWeek);
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
            var changed = !File.Exists(original.FinalFilePath);
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
                            runtime.Job.FinalUrl = metadata.FinalUrl;
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
