using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    private static bool ChangesQueueFinishNotice(List<QueueDto>? requested, List<QueueDto> current)
        => (requested ?? []).Any(next => next.NotifyWhenFinished is { } value &&
            value != (current.FirstOrDefault(q => q.Id == next.Id)?.NotifyWhenFinished ?? false));

    private static bool ChangesQueueConditions(List<QueueDto>? requested, List<QueueDto> current)
    {
        foreach (var next in requested ?? [])
        {
            var before = current.FirstOrDefault(q => q.Id == next.Id);
            if (next.RequireCharging is { } charging && charging != (before?.RequireCharging ?? false) ||
                next.RequireBatterySaverOff is { } saver && saver != (before?.RequireBatterySaverOff ?? false) ||
                next.RequireUnmeteredNetwork is { } network && network != (before?.RequireUnmeteredNetwork ?? false)) return true;
        }
        return false;
    }

    private bool QueueActionChangesConditions(QueueActionRequest request)
    {
        if (!string.Equals(request.Action, "create", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Action, "policy", StringComparison.OrdinalIgnoreCase)) return false;
        var before = string.Equals(request.Action, "create", StringComparison.OrdinalIgnoreCase) ? null : _service.GetSettings().Queues.FirstOrDefault(q => q.Id == request.QueueId);
        return request.RequireCharging is { } charging && charging != (before?.RequireCharging ?? false) ||
            request.RequireBatterySaverOff is { } saver && saver != (before?.RequireBatterySaverOff ?? false) ||
            request.RequireUnmeteredNetwork is { } network && network != (before?.RequireUnmeteredNetwork ?? false);
    }
}
