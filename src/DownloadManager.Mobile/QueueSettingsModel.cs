using System.Text.Json;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>Queue and schedule commands against the selected Host. The screen owns presentation and applies LastResult on its UI thread.</summary>
public sealed class QueueSettingsModel(DownloadsModel downloads, IHostConnection connection, bool requireCapabilities = false)
{
    /// <summary>The selected Host's next reported timed window, while it is still in the future.</summary>
    public static DateTimeOffset? ReportedUpcomingStart(QueueDto queue, bool hasEnabledSchedule, DateTimeOffset now) =>
        !queue.IsRunning && hasEnabledSchedule && queue.NextTimedStartAt is { } start && start > now ? start : null;

    public SettingsDto Settings => downloads.Settings;
    public bool CanWriteSafely => !requireCapabilities || downloads.Hello?.Capabilities.Contains("settingsCompareAndSwap") == true;
    public bool CanEditQueuesSafely => CanWriteSafely && (!requireCapabilities || downloads.Hello?.Capabilities.Contains("queueIdentity") == true);
    private static CommandResult NeedsHostUpdate => CommandResult.Fail("Update this Host before editing its settings safely.");
    public async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken = default)
        => (await RequestAsync(IpcMessage.Create(MessageTypes.SettingsGet), cancellationToken).ConfigureAwait(false)).Result;
    /// <summary>The last authoritative Host result, including refusals. It is not automatically applied to the Downloads screen.</summary>
    public SettingsResult? LastResult { get; private set; }

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> TypeInfo<T>() => (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)IpcJsonContext.Default.GetTypeInfo(typeof(T))!;

    private static T Clone<T>(T value) => JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>()), TypeInfo<T>())!;

    private async Task<(CommandResult Result, SettingsResult? Response)> RequestAsync(IpcMessage request, CancellationToken ct)
    {
        LastResult = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var reply = await connection.RequestAsync(request, ct).ConfigureAwait(false);
            if (reply.Type != MessageTypes.SettingsResult) return (CommandResult.Fail("The Host answered with " + reply.Type + "."), null);
            var result = reply.GetPayload<SettingsResult>();
            LastResult = result;
            return (result.Ok ? CommandResult.Success : CommandResult.Fail(result.Error ?? "The Host refused."), result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return (CommandResult.Fail(e.Message), null); }
    }

    public async Task<CommandResult> QueueActionAsync(QueueActionRequest request, CancellationToken cancellationToken = default)
    {
        LastResult = null;
        return (await RequestAsync(IpcMessage.Create(MessageTypes.QueueAction, Clone(request)), cancellationToken).ConfigureAwait(false)).Result;
    }

    private async Task<(CommandResult Result, SettingsDto? Settings)> ReadAsync(CancellationToken ct)
    {
        var (result, response) = await RequestAsync(IpcMessage.Create(MessageTypes.SettingsGet), ct).ConfigureAwait(false);
        return (result, result.Ok ? Clone(response!.Settings) : null);
    }

    private async Task<CommandResult> WriteAsync(SettingsDto settings, CancellationToken ct) =>
        (await RequestAsync(IpcMessage.Create(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings, ExpectedRevision = settings.Revision }), ct).ConfigureAwait(false)).Result;

    public async Task<CommandResult> SaveQueueAsync(QueueDto edited, QueueDto? baseline, CancellationToken cancellationToken = default)
    {
        LastResult = null;
        if (!CanEditQueuesSafely) return NeedsHostUpdate;
        var desired = Clone(edited);
        var original = baseline is null ? null : Clone(baseline);
        if (requireCapabilities && original is not null && (original.Generation is null || original.Generation == Guid.Empty)) return NeedsHostUpdate;
        if (original is null)
            return await QueueActionAsync(new QueueActionRequest
            {
                Action = "create", Name = desired.Name, MaxConcurrentDownloads = desired.MaxConcurrentDownloads,
                QuotaEnabled = desired.QuotaEnabled, QuotaBytes = desired.QuotaBytes, QuotaWindowHours = desired.QuotaWindowHours,
                RequireUnmeteredNetwork = desired.RequireUnmeteredNetwork, RequireCharging = desired.RequireCharging,
                RequireBatterySaverOff = desired.RequireBatterySaverOff, SpeedLimitBytesPerSecond = desired.SpeedLimitBytesPerSecond,
                NotifyWhenFinished = desired.NotifyWhenFinished,
            }, cancellationToken).ConfigureAwait(false);
        if (desired.Id != original.Id) return CommandResult.Fail("The queue changed. Reopen it and try again.");
        var (result, latest) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Ok) return result;
        var current = latest!.Queues.FirstOrDefault(q => q.Id == original.Id);
        if (current is null || !SameQueue(current, original)) return CommandResult.Fail("The queue changed or was deleted. Reopen it and try again.");
        CopyQueue(desired, current);
        return await WriteAsync(latest, cancellationToken).ConfigureAwait(false);
    }

    private static void CopyQueue(QueueDto from, QueueDto to)
    {
        to.Name = from.Name; to.MaxConcurrentDownloads = from.MaxConcurrentDownloads; to.RetryLimit = from.RetryLimit;
        to.QuotaEnabled = from.QuotaEnabled; to.QuotaBytes = from.QuotaBytes; to.QuotaWindowHours = from.QuotaWindowHours;
        to.RequireUnmeteredNetwork = from.RequireUnmeteredNetwork; to.RequireCharging = from.RequireCharging;
        to.RequireBatterySaverOff = from.RequireBatterySaverOff; to.SpeedLimitBytesPerSecond = from.SpeedLimitBytesPerSecond;
        to.NotifyWhenFinished = from.NotifyWhenFinished;
    }

    private static bool SameQueue(QueueDto a, QueueDto b)
    {
        if (a.Generation != b.Generation) return false;
        var x = new QueueDto(); var y = new QueueDto();
        CopyQueue(a, x); CopyQueue(b, y);
        // Older Hosts omit these conditions; omission and false both mean no condition.
        x.SpeedLimitBytesPerSecond ??= 0; y.SpeedLimitBytesPerSecond ??= 0;
        x.NotifyWhenFinished ??= false; y.NotifyWhenFinished ??= false;
        x.RequireUnmeteredNetwork ??= false; y.RequireUnmeteredNetwork ??= false;
        x.RequireCharging ??= false; y.RequireCharging ??= false;
        x.RequireBatterySaverOff ??= false; y.RequireBatterySaverOff ??= false;
        return JsonSerializer.Serialize(x, TypeInfo<QueueDto>()) == JsonSerializer.Serialize(y, TypeInfo<QueueDto>());
    }

    private static void CopySchedule(ScheduleDto from, ScheduleDto to)
    {
        to.Name = from.Name; to.QueueId = from.QueueId; to.Enabled = from.Enabled; to.Kind = from.Kind;
        to.StartOnHostStartup = from.StartOnHostStartup; to.TimedStartEnabled = from.TimedStartEnabled;
        to.RepeatEveryMinutes = from.RepeatEveryMinutes; to.StartDate = from.StartDate; to.Days = from.Days;
        to.StartTime = from.StartTime; to.StopTime = from.StopTime; to.OpenFileWhenDone = from.OpenFileWhenDone;
        to.ExitWhenDone = from.ExitWhenDone; to.ShutdownWhenDone = from.ShutdownWhenDone;
        to.ShutdownMode = from.ShutdownMode; to.ForceShutdown = from.ForceShutdown; to.SiteTransfers = from.SiteTransfers;
    }

    private static bool SameSchedule(ScheduleDto a, ScheduleDto b)
    {
        var x = new ScheduleDto(); var y = new ScheduleDto();
        CopySchedule(a, x); CopySchedule(b, y);
        try { x.Days = ScheduleDays.Serialize(ScheduleDays.FromHost(x.Days)); y.Days = ScheduleDays.Serialize(ScheduleDays.FromHost(y.Days)); }
        catch (FormatException) { return false; } // Unknown Host formats are review-only, never rewritten.
        return JsonSerializer.Serialize(x, TypeInfo<ScheduleDto>()) == JsonSerializer.Serialize(y, TypeInfo<ScheduleDto>());
    }

    public async Task<CommandResult> SaveScheduleAsync(ScheduleDto edited, ScheduleDto? baseline, CancellationToken cancellationToken = default, Guid? expectedQueueGeneration = null)
    {
        LastResult = null;
        if (!CanEditQueuesSafely || requireCapabilities && (expectedQueueGeneration is null || expectedQueueGeneration == Guid.Empty)) return NeedsHostUpdate;
        var desired = Clone(edited); var original = baseline is null ? null : Clone(baseline);
        if (original is not null && desired.Id != original.Id) return CommandResult.Fail("The schedule changed. Reopen it and try again.");
        var (result, latest) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Ok) return result;
        if (expectedQueueGeneration is { } generation && latest!.Queues.FirstOrDefault(q => q.Id == desired.QueueId)?.Generation != generation)
            return CommandResult.Fail("The queue was deleted or replaced. Review it again.");
        if (original is null)
        {
            if (desired.Id == Guid.Empty) desired.Id = Guid.NewGuid();
            if (latest!.Schedules.Any(s => s.Id == desired.Id)) return CommandResult.Fail("A schedule with this ID already exists.");
            // Do not allow a draft to supply Host-owned run history.
            var added = new ScheduleDto { Id = desired.Id }; CopySchedule(desired, added); latest.Schedules.Add(added);
        }
        else
        {
            var current = latest!.Schedules.FirstOrDefault(s => s.Id == original.Id);
            if (current is null || !SameSchedule(current, original)) return CommandResult.Fail("The schedule changed or was deleted. Reopen it and try again.");
            CopySchedule(desired, current);
        }
        return await WriteAsync(latest!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandResult> DeleteScheduleAsync(ScheduleDto baseline, CancellationToken cancellationToken = default, Guid? expectedQueueGeneration = null)
    {
        LastResult = null;
        if (!CanEditQueuesSafely || requireCapabilities && (expectedQueueGeneration is null || expectedQueueGeneration == Guid.Empty)) return NeedsHostUpdate;
        var original = Clone(baseline);
        var (result, latest) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Ok) return result;
        if (expectedQueueGeneration is { } generation && latest!.Queues.FirstOrDefault(q => q.Id == original.QueueId)?.Generation != generation)
            return CommandResult.Fail("The queue was deleted or replaced. Review it again.");
        var current = latest!.Schedules.FirstOrDefault(s => s.Id == original.Id);
        if (current is null || !SameSchedule(current, original)) return CommandResult.Fail("The schedule changed or was deleted. Reopen it and try again.");
        latest.Schedules.Remove(current);
        return await WriteAsync(latest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures the requested changes before awaiting, then merges only changed fields into fresh Host settings.</summary>
    public async Task<CommandResult> UpdateSettingAsync(Action<SettingsDto> edit, CancellationToken cancellationToken = default)
    {
        LastResult = null;
        if (!CanWriteSafely) return NeedsHostUpdate;
        var before = Clone(Settings); var after = Clone(before); edit(after);
        var beforeNode = JsonSerializer.SerializeToNode(before, TypeInfo<SettingsDto>())!;
        var afterNode = JsonSerializer.SerializeToNode(after, TypeInfo<SettingsDto>())!;
        var (result, latest) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Ok) return result;
        var latestNode = JsonSerializer.SerializeToNode(latest!, TypeInfo<SettingsDto>())!;
        MergeChanges((JsonObject)beforeNode, (JsonObject)afterNode, (JsonObject)latestNode);
        return await WriteAsync(latestNode.Deserialize(TypeInfo<SettingsDto>())!, cancellationToken).ConfigureAwait(false);
    }

    private static void MergeChanges(JsonObject before, JsonObject after, JsonObject latest)
    {
        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key)))
        {
            before.TryGetPropertyValue(key, out var old);
            after.TryGetPropertyValue(key, out var desired);
            if (JsonNode.DeepEquals(old, desired)) continue;
            if (old is JsonObject oldObject && desired is JsonObject newObject && latest[key] is JsonObject latestObject)
                MergeChanges(oldObject, newObject, latestObject);
            else latest[key] = desired?.DeepClone();
        }
    }
}
