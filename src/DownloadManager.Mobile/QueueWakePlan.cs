using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

public sealed record DeferredQueueWake(DateTimeOffset At, bool Charging, bool Unmetered, DateTimeOffset? OverrideConstraintsAt = null);

/// <summary>Plans ordinary OS jobs; Runtime remains the authority for queue windows and individual conditions.</summary>
public static class QueueWakePlan
{
    public static DeferredQueueWake? Next(SnapshotDto snapshot, DateTimeOffset? schedulerWake, DateTimeOffset now,
        DateTimeOffset retryNotBefore, bool unattended, bool guardTripped)
    {
        if (guardTripped) return null;
        var queues = snapshot.Settings.Queues.ToDictionary(q => q.Id);
        var pending = snapshot.Jobs.Where(j => j.State == "queued" && j.StartMode != StartModes.Later &&
            (!j.IsQueued || queues.GetValueOrDefault(j.QueueId)?.IsRunning == true)).ToArray();
        var pendingAt = unattended && pending.Length > 0 ? (retryNotBefore > now ? retryNotBefore : now) : (DateTimeOffset?)null;
        var at = schedulerWake is { } timed && (pendingAt is null || timed < pendingAt) ? timed : pendingAt;
        if (at is null) return null;
        // Future timer wakes may start a different queue with different conditions. Only constrain a wake serving current work.
        bool currentWork = pendingAt is not null && at == pendingAt;
        bool charging = currentWork && pending.All(j => j.IsQueued && queues.GetValueOrDefault(j.QueueId)?.RequireCharging == true);
        bool unmetered = snapshot.Settings.DownloadOnlyOnUnmeteredNetwork || currentWork && pending.All(j => j.IsQueued && queues.GetValueOrDefault(j.QueueId)?.RequireUnmeteredNetwork == true);
        // A pending constrained queue must not suppress a later timer for another queue.
        // The OS deadline only grants evaluation; Runtime still enforces each queue policy.
        var deadline = schedulerWake is { } timer ? (timer < now ? now : timer) : (DateTimeOffset?)null;
        return new DeferredQueueWake(at.Value < now ? now : at.Value, charging, unmetered, deadline);
    }
}
