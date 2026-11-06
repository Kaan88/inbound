using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Archives;

/// <summary>
/// Turns <c>archive.*</c> requests into coordinator calls. Nothing here waits for an extraction, so
/// these are safe on a client's one command at a time. A bad payload is answered without echoing it,
/// because it can hold a password.
/// </summary>
public sealed class ArchiveIpcDispatcher(ArchiveCoordinator coordinator)
{
    public static bool Handles(string? messageType) =>
        messageType?.StartsWith(ArchiveMessageTypes.Prefix, StringComparison.Ordinal) == true;

    public async Task<IpcMessage> DispatchAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Type)
            {
                case ArchiveMessageTypes.SettingsGet:
                    return IpcMessage.Create(ArchiveMessageTypes.SettingsResult, coordinator.GetSettings(), request.Id);

                case ArchiveMessageTypes.SettingsSet:
                    return IpcMessage.Create(
                        ArchiveMessageTypes.SettingsResult,
                        await coordinator.UpdateSettingsAsync(request.GetPayload<ArchiveSettingsUpdate>(), cancellationToken).ConfigureAwait(false),
                        request.Id);

                case ArchiveMessageTypes.List:
                    return IpcMessage.Create(ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = coordinator.List() }, request.Id);

                case ArchiveMessageTypes.Extract:
                    return IpcMessage.Create(
                        ArchiveMessageTypes.ExtractResult,
                        await coordinator.ExtractAsync(request.GetPayload<ArchiveExtractRequest>(), cancellationToken).ConfigureAwait(false),
                        request.Id);

                case ArchiveMessageTypes.SetPassword:
                    return Action(request, await coordinator.SetPasswordAsync(request.GetPayload<ArchiveSetPasswordRequest>(), cancellationToken).ConfigureAwait(false));

                case ArchiveMessageTypes.Retry:
                    return Action(request, await coordinator.RetryAsync(request.GetPayload<ArchiveActionRequest>().SetId, cancellationToken).ConfigureAwait(false));

                case ArchiveMessageTypes.Cancel:
                    return Action(request, await coordinator.CancelAsync(request.GetPayload<ArchiveActionRequest>().SetId, cancellationToken).ConfigureAwait(false));

                case ArchiveMessageTypes.Remove:
                    return Action(request, await coordinator.RemoveAsync(request.GetPayload<ArchiveActionRequest>().SetId, cancellationToken).ConfigureAwait(false));

                default:
                    return Action(request, new ArchiveActionResult { Ok = false, Error = $"Unknown message type '{request.Type}'." });
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return Action(request, new ArchiveActionResult { Ok = false, Error = "The request could not be read." });
        }
    }

    private static IpcMessage Action(IpcMessage request, ArchiveActionResult result) =>
        IpcMessage.Create(ArchiveMessageTypes.ActionResult, result, request.Id);
}
