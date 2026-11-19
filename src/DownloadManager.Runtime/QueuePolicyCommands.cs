using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    // Called with the settings mutation gate held. Reuse the private candidate/save/publish path: failed creation
    // must not leak a live queue that only exists in memory, or allow it to start before durable admission.
    private Task<SettingsResult> CreateQueueDurablyAsync(QueueActionRequest request, CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (!ValidQueuePolicy(request)) return Task.FromResult(new SettingsResult { Error = "Invalid queue name, concurrency or quota policy.", Settings = settings });
        var used = settings.Queues.Select(q => q.Id).ToHashSet();
        int id = 0;
        while (used.Contains(id) && id < int.MaxValue) id++;
        if (used.Contains(id)) return Task.FromResult(new SettingsResult { Error = "No queue identifiers are available.", Settings = settings });
        settings.Queues.Add(new QueueDto
        {
            Id = id, Name = request.Name!.Trim(), Order = settings.Queues.Count, IsRunning = false,
            MaxConcurrentDownloads = request.MaxConcurrentDownloads,
            QuotaEnabled = request.QuotaEnabled ?? false, QuotaBytes = request.QuotaBytes ?? 200L * 1024 * 1024,
            QuotaWindowHours = request.QuotaWindowHours ?? 5,
            RequireUnmeteredNetwork = request.RequireUnmeteredNetwork ?? false,
            RequireCharging = request.RequireCharging ?? false, RequireBatterySaverOff = request.RequireBatterySaverOff ?? false,
            SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond ?? 0,
            NotifyWhenFinished = request.NotifyWhenFinished ?? false,
        });
        return UpdateSettingsCoreAsync(settings, cancellationToken);
    }

    // Settings gate held throughout: admissions, destination removal and policy edits cannot interleave.
    private async Task<SettingsResult> DeleteQueueDurablyAsync(QueueActionRequest request, CancellationToken ct, CallerScope? caller)
    {
        var settings = GetSettings();
        if (request.QueueId == settings.DefaultQueueId || !settings.Queues.Any(q => q.Id == request.QueueId))
            return new SettingsResult { Error = "The queue is missing or is the default queue.", Settings = settings };
        List<Guid> members;
        lock (_sync)
        {
            var liveMembers = _jobs.Values.Where(r => r.Job.QueueId == request.QueueId).ToArray();
            // Authorize the exact member set under the same admission/membership gate, before the first move.
            if (caller is not null && liveMembers.Any(r => !caller.Holds(r.Job.DestinationDirectory, r.Job.FinalFilePath, r.Job.PartFilePath)))
                return new SettingsResult { Error = CallerScope.OutsideJobRefused, Settings = settings };
            members = liveMembers.Select(r => r.Job.Id).ToList();
        }
        if (request.MoveContentsToQueueId is not { } destination)
        {
            if (members.Count > 0 || settings.Schedules.Any(s => s.QueueId == request.QueueId) && !request.RemoveSchedules)
                return new SettingsResult { Error = "Move this queue's downloads and schedules before deleting it. No downloads were removed.", Settings = settings };
        }
        else
        {
            if (destination == request.QueueId || !settings.Queues.Any(q => q.Id == destination &&
                (request.ExpectedDestinationQueueGeneration is null || q.Generation == request.ExpectedDestinationQueueGeneration)))
                return new SettingsResult { Error = "Choose another existing queue.", Settings = settings };
            try
            {
                foreach (var id in members)
                {
                    ct.ThrowIfCancellationRequested();
                    var moved = await MoveToQueueCoreAsync(id, destination, ct).ConfigureAwait(false);
                    if (!moved.Ok) return new SettingsResult { Error = "Queue retained; some downloads may already have moved. " + moved.Error, Settings = GetSettings() };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SettingsResult { Error = "Queue retained; some downloads may already have moved. " + ex.Message, Settings = GetSettings() };
            }
        }
        // Save removal and schedule changes in a private candidate. Failed final save leaves the live queue
        // and its schedules intact, even when earlier per-job moves already committed successfully.
        settings = GetSettings();
        settings.Queues.RemoveAll(q => q.Id == request.QueueId);
        if (request.RemoveSchedules) settings.Schedules.RemoveAll(s => s.QueueId == request.QueueId);
        else if (request.MoveContentsToQueueId is { } target)
            foreach (var schedule in settings.Schedules.Where(s => s.QueueId == request.QueueId)) schedule.QueueId = target;
        try { return await UpdateSettingsCoreAsync(settings, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsResult { Error = "Queue retained because deletion could not be saved. Some downloads may already have moved. " + ex.Message, Settings = GetSettings() };
        }
    }
}
