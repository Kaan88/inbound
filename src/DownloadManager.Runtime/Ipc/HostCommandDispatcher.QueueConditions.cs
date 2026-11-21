using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    private static bool ChangesQueueFinishNotice(List<QueueDto>? requested, List<QueueDto> current)
        => (requested ?? []).Any(next => next.NotifyWhenFinished is { } value &&
            value != (current.FirstOrDefault(q => q.Id == next.Id)?.NotifyWhenFinished ?? false));

    /// <summary>Turning a condition on where the platform can't check it. Turning one off is always allowed (review S1).</summary>
    private static bool ChangesQueueConditions(List<QueueDto>? requested, List<QueueDto> current)
    {
        foreach (var next in requested ?? [])
        {
            var before = current.FirstOrDefault(q => q.Id == next.Id);
            if (next.RequireCharging == true && before?.RequireCharging != true ||
                next.RequireBatterySaverOff == true && before?.RequireBatterySaverOff != true ||
                next.RequireUnmeteredNetwork == true && before?.RequireUnmeteredNetwork != true) return true;
        }
        return false;
    }

    private bool QueueActionChangesConditions(QueueActionRequest request)
    {
        if (!string.Equals(request.Action, "create", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Action, "policy", StringComparison.OrdinalIgnoreCase)) return false;
        var before = string.Equals(request.Action, "create", StringComparison.OrdinalIgnoreCase) ? null : _service.GetSettings().Queues.FirstOrDefault(q => q.Id == request.QueueId);
        return request.RequireCharging == true && before?.RequireCharging != true ||
            request.RequireBatterySaverOff == true && before?.RequireBatterySaverOff != true ||
            request.RequireUnmeteredNetwork == true && before?.RequireUnmeteredNetwork != true;
    }
}
