using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// Audit 4 H1 (Host side): every client saves a whole copy of the settings, and a copy can be older than the Host's
/// own state. Two rules keep such a save from undoing what happened since:
/// <list type="bullet">
/// <item>A queue's run state and quota use (<c>IsRunning</c>, <c>QuotaWindowStart</c>, <c>QuotaBytesUsed</c>) are the
/// Host's. <c>settings.update</c> never takes them for an existing queue; <c>queue.action</c> starts and stops a queue,
/// and <see cref="QueueDto.ResetQuota"/> starts a fresh quota window.</item>
/// <item>A queue or schedule missing from the request is removed only when the client's copy had it. The Host gives
/// <see cref="AppSettings.Revision"/> with every copy, and remembers (in memory) the revision at which each queue and
/// schedule was added or removed. Missing and added after the copy's revision: kept. In the request but removed after
/// that revision: not brought back. In the request with an id the Host gave a newer item since: the Host's item is
/// kept. A request without a revision (0, an older client) removes what it lacks, as before.</item>
/// </list>
/// The revision starts from the clock at each start, so a copy from before a restart is older than anything added
/// after it. What was added or removed before the restart counts as seen by every copy.
/// </summary>
public sealed partial class DownloadManagerService
{
    private readonly Dictionary<int, long> _queueAddedAt = new();
    private readonly Dictionary<int, long> _queueRemovedAt = new();
    private readonly Dictionary<Guid, long> _scheduleAddedAt = new();
    private readonly Dictionary<Guid, long> _scheduleRemovedAt = new();

    /// <summary>The first revision of this run: later than any a copy from an earlier run can carry.</summary>
    private static long StartingRevision(TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds() * 1000;

    /// <summary>A request's revision as far as this Host can trust it: none is 0, and one from the future is now.</summary>
    private long BaseRevisionLocked(long requested) => requested <= 0 ? 0 : Math.Min(requested, _settings.Revision);

    /// <summary>
    /// Records which queues and schedules <paramref name="next"/> adds or removes against <paramref name="previous"/>,
    /// and gives <paramref name="next"/> its revision. Call under <c>_sync</c>, as the settings are swapped.
    /// </summary>
    private void NoteSettingsRevisionLocked(AppSettings previous, AppSettings next)
    {
        var revision = Math.Max(previous.Revision, next.Revision);
        var queuesBefore = previous.Queues.Select(queue => queue.Id).ToHashSet();
        var queuesAfter = next.Queues.Select(queue => queue.Id).ToHashSet();
        foreach (var id in queuesAfter.Where(id => !queuesBefore.Contains(id)))
        {
            _queueAddedAt[id] = ++revision;
            _queueRemovedAt.Remove(id);
        }
        foreach (var id in queuesBefore.Where(id => !queuesAfter.Contains(id)))
        {
            _queueRemovedAt[id] = ++revision;
            _queueAddedAt.Remove(id);
        }

        var schedulesBefore = previous.Schedules.Select(schedule => schedule.Id).ToHashSet();
        var schedulesAfter = next.Schedules.Select(schedule => schedule.Id).ToHashSet();
        foreach (var id in schedulesAfter.Where(id => !schedulesBefore.Contains(id)))
        {
            _scheduleAddedAt[id] = ++revision;
            _scheduleRemovedAt.Remove(id);
        }
        foreach (var id in schedulesBefore.Where(id => !schedulesAfter.Contains(id)))
        {
            _scheduleRemovedAt[id] = ++revision;
            _scheduleAddedAt.Remove(id);
        }

        next.Revision = revision;
    }

    /// <summary>Queue <c>create</c> and <c>delete</c> change the live list; this records them as a save would.</summary>
    private void NoteQueueAddedLocked(int id)
    {
        _queueAddedAt[id] = ++_settings.Revision;
        _queueRemovedAt.Remove(id);
    }

    private void NoteQueueRemovedLocked(int id)
    {
        _queueRemovedAt[id] = ++_settings.Revision;
        _queueAddedAt.Remove(id);
    }

    private List<DownloadQueue> MergeQueues(List<QueueDto>? requested, long requestedRevision, int defaultMax)
    {
        List<DownloadQueue> existing;
        long baseRevision;
        Dictionary<int, long> addedAt, removedAt;
        lock (_sync)
        {
            existing = _settings.Queues.ToList();
            baseRevision = BaseRevisionLocked(requestedRevision);
            addedAt = new(_queueAddedAt);
            removedAt = new(_queueRemovedAt);
        }

        var now = _timeProvider.GetUtcNow();
        bool Seen(int id) => baseRevision == 0 || addedAt.GetValueOrDefault(id) <= baseRevision;
        static DownloadQueue Copy(DownloadQueue queue) => new()
        {
            Id = queue.Id,
            Name = queue.Name,
            MaxConcurrentDownloads = queue.MaxConcurrentDownloads,
            IsRunning = queue.IsRunning,
            Order = queue.Order,
            RetryLimit = queue.RetryLimit,
            QuotaEnabled = queue.QuotaEnabled,
            QuotaBytes = queue.QuotaBytes,
            QuotaWindowHours = queue.QuotaWindowHours,
            QuotaWindowStart = queue.QuotaWindowStart,
            QuotaBytesUsed = queue.QuotaBytesUsed,
            JobOrder = queue.JobOrder?.ToList() ?? new(),
        };

        List<DownloadQueue> source;
        var keptIds = new HashSet<int>();
        if (requested is not { Count: > 0 })
        {
            source = existing.Select(Copy).ToList();
            keptIds.UnionWith(source.Select(queue => queue.Id));
        }
        else
        {
            source = new List<DownloadQueue>();
            var listed = new HashSet<int>();
            foreach (var (q, index) in requested.Where(q => q is not null).Select((q, index) => (q, index)))
            {
                var current = existing.FirstOrDefault(queue => queue.Id == q.Id);
                if (current is not null && !Seen(current.Id))
                {
                    // The client's entry is an older queue that had this number; the Host's newer one stays as it is.
                    if (listed.Add(current.Id)) { source.Add(Copy(current)); keptIds.Add(current.Id); }
                    continue;
                }
                if (current is null && baseRevision > 0 && removedAt.GetValueOrDefault(q.Id) > baseRevision)
                    continue; // removed after this copy was taken: not brought back
                var queue = new DownloadQueue
                {
                    Id = q.Id,
                    Name = string.IsNullOrWhiteSpace(q.Name) ? $"Queue {index + 1}" : q.Name.Trim(),
                    MaxConcurrentDownloads = Math.Clamp(q.MaxConcurrentDownloads, 1, 16),
                    // Run state and quota use are the Host's; a new queue starts as the client asks, with no use.
                    IsRunning = current?.IsRunning ?? q.IsRunning,
                    RetryLimit = q.RetryLimit,
                    QuotaEnabled = q.QuotaEnabled,
                    QuotaBytes = Math.Clamp(q.QuotaBytes, 1, 10L * 1024 * 1024 * 1024 * 1024),
                    QuotaWindowHours = Math.Clamp(q.QuotaWindowHours, 1, 24 * 365),
                    QuotaWindowStart = q.ResetQuota ? now : current?.QuotaWindowStart,
                    QuotaBytesUsed = q.ResetQuota ? 0 : current?.QuotaBytesUsed ?? 0,
                    JobOrder = current?.JobOrder?.ToList() ?? new(),
                };
                if (current is not null)
                {
                    if (!listed.Add(current.Id)) continue;
                    keptIds.Add(current.Id);
                }
                source.Add(queue);
            }

            // Added after this copy was taken, so the copy can't have meant to remove it.
            foreach (var current in existing.Where(queue => !listed.Contains(queue.Id) && !Seen(queue.Id)))
            {
                listed.Add(current.Id);
                keptIds.Add(current.Id);
                source.Add(Copy(current));
            }
        }

        if (source.Count == 0)
        {
            source.Add(new DownloadQueue
            {
                Id = 0,
                Name = "Main",
                MaxConcurrentDownloads = Math.Clamp(defaultMax, 1, 16),
                IsRunning = true,
            });
        }

        // An existing queue keeps its number; a new one that takes a number already used gets the next free one.
        var seen = new HashSet<int>(keptIds);
        for (var i = 0; i < source.Count; i++)
        {
            if (!keptIds.Contains(source[i].Id))
            {
                if (!seen.Add(source[i].Id))
                {
                    source[i].Id = seen.Max() + 1;
                    seen.Add(source[i].Id);
                }
            }

            source[i].Order = i;
        }

        return source;
    }

    private List<ScheduleRule> MergeSchedules(List<ScheduleDto>? requested, long requestedRevision)
    {
        List<ScheduleRule> existing;
        long baseRevision;
        Dictionary<Guid, long> addedAt, removedAt;
        lock (_sync)
        {
            existing = _settings.Schedules.ToList();
            baseRevision = BaseRevisionLocked(requestedRevision);
            addedAt = new(_scheduleAddedAt);
            removedAt = new(_scheduleRemovedAt);
        }

        // A null list means "not supplied"; an empty list means the caller removed every schedule it had.
        if (requested is null)
        {
            return existing.ToList();
        }

        bool Seen(Guid id) => baseRevision == 0 || addedAt.GetValueOrDefault(id) <= baseRevision;
        var existingById = existing.GroupBy(s => s.Id).ToDictionary(group => group.Key, group => group.First());
        var listed = new HashSet<Guid>();
        var result = new List<ScheduleRule>();
        foreach (var s in requested.Where(s => s is not null))
        {
            var id = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id;
            existingById.TryGetValue(id, out var previous);
            if (previous is null && baseRevision > 0 && removedAt.GetValueOrDefault(id) > baseRevision) continue;
            if (!listed.Add(id)) continue;
            var triggerChanged = previous is not null && (previous.QueueId != s.QueueId || previous.Kind != s.Kind ||
                previous.StartDate != s.StartDate || previous.Days != s.Days || previous.StartTime != s.StartTime ||
                previous.StopTime != s.StopTime || previous.StartOnHostStartup != s.StartOnHostStartup ||
                previous.TimedStartEnabled != s.TimedStartEnabled || previous.RepeatEveryMinutes != s.RepeatEveryMinutes ||
                !previous.Enabled && s.Enabled);
            result.Add(new ScheduleRule
            {
                Id = id,
                Name = s.Name,
                QueueId = s.QueueId,
                Enabled = s.Enabled,
                Kind = string.Equals(s.Kind, "oneTime", StringComparison.OrdinalIgnoreCase)
                    ? "oneTime"
                    : "daily",
                StartDate = s.StartDate,
                Days = s.Days,
                StartTime = s.StartTime,
                StopTime = s.StopTime,
                OpenFileWhenDone = s.OpenFileWhenDone,
                ExitWhenDone = s.ExitWhenDone,
                ShutdownWhenDone = s.ShutdownWhenDone,
                ShutdownMode = s.ShutdownMode,
                ForceShutdown = s.ForceShutdown,
                StartOnHostStartup = s.StartOnHostStartup, TimedStartEnabled = s.TimedStartEnabled,
                RepeatEveryMinutes = s.RepeatEveryMinutes,
                // Run state is owned by the host scheduler, never by a UI payload.
                LastRunAt = triggerChanged ? null : previous?.LastRunAt, LastRunError = triggerChanged ? null : previous?.LastRunError,
                LastStartDate = triggerChanged ? null : previous?.LastStartDate,
                LastStopDate = triggerChanged ? null : previous?.LastStopDate,
            });
        }

        // Added after this copy was taken, so the copy can't have meant to remove it.
        result.AddRange(existing.Where(schedule => !listed.Contains(schedule.Id) && !Seen(schedule.Id)));
        return result;
    }
}
