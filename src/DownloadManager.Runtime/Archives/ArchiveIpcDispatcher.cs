using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Archives;

/// <summary>
/// Turns <c>archive.*</c> requests into coordinator calls. Nothing here waits for an extraction, so
/// these are safe on a client's one command at a time. A bad payload is answered without echoing it,
/// because it can hold a password.
/// </summary>
/// <param name="roots">
/// On a server, the folders a client may have it read archives from and extract into: a client there is not the
/// machine's user. A path it gives is checked and replaced by the resolved one, so a link can't lead out. Null on the
/// desktop, where the user may name any file.
/// </param>
public sealed class ArchiveIpcDispatcher(ArchiveCoordinator coordinator, PathRoots? roots = null)
{
    private const string FileOutside = "That file is outside the folders this server may use.";

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
                    var update = request.GetPayload<ArchiveSettingsUpdate>();
                    if (roots is not null && !string.IsNullOrWhiteSpace(update.Folder))
                    {
                        if (roots.Allowed(update.Folder) is not { } folder)
                        {
                            var current = coordinator.GetSettings();
                            current.Ok = false;
                            current.Error = PathRoots.OutsideMessage;
                            return IpcMessage.Create(ArchiveMessageTypes.SettingsResult, current, request.Id);
                        }

                        update.Folder = folder;
                    }

                    return IpcMessage.Create(
                        ArchiveMessageTypes.SettingsResult,
                        await coordinator.UpdateSettingsAsync(update, cancellationToken).ConfigureAwait(false),
                        request.Id);

                case ArchiveMessageTypes.List:
                    return IpcMessage.Create(ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = coordinator.List(), PackagesWithPassword = coordinator.PackagesWithPassword() }, request.Id);

                case ArchiveMessageTypes.Extract:
                    var extract = request.GetPayload<ArchiveExtractRequest>();
                    if (Confine(extract.FilePath) is var (extractPath, extractOk) && !extractOk)
                        return IpcMessage.Create(ArchiveMessageTypes.ExtractResult, new ArchiveExtractResult { Ok = false, Error = FileOutside }, request.Id);
                    extract.FilePath = extractPath;
                    return IpcMessage.Create(
                        ArchiveMessageTypes.ExtractResult,
                        await coordinator.ExtractAsync(extract, cancellationToken).ConfigureAwait(false),
                        request.Id);

                case ArchiveMessageTypes.SetPassword:
                    var password = request.GetPayload<ArchiveSetPasswordRequest>();
                    if (Confine(password.FilePath) is var (passwordPath, passwordOk) && !passwordOk)
                        return Action(request, new ArchiveActionResult { Ok = false, Error = FileOutside });
                    password.FilePath = passwordPath;
                    return Action(request, await coordinator.SetPasswordAsync(password, cancellationToken).ConfigureAwait(false));

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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Every result type has ok and error, so whichever one the client expects reads this.
            return Action(request, new ArchiveActionResult { Ok = false, Error = $"The change could not be saved: {exception.GetType().Name}." });
        }
    }

    /// <summary>The path to use and whether it may be: unchanged without roots or without a path, else the resolved one inside them.</summary>
    private (string? Path, bool Ok) Confine(string? path)
    {
        if (roots is null || string.IsNullOrWhiteSpace(path)) return (path, true);
        return roots.Allowed(path) is { } inside ? (inside, true) : (path, false);
    }

    private static IpcMessage Action(IpcMessage request, ArchiveActionResult result) =>
        IpcMessage.Create(ArchiveMessageTypes.ActionResult, result, request.Id);
}
