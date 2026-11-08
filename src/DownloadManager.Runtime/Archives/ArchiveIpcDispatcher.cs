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
    /// <summary>The coordinator it answers for.</summary>
    public ArchiveCoordinator Coordinator => coordinator;

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
                            current.Error = roots.OutsideText;
                            return IpcMessage.Create(ArchiveMessageTypes.SettingsResult, current, request.Id);
                        }

                        update.Folder = folder;
                    }

                    return IpcMessage.Create(
                        ArchiveMessageTypes.SettingsResult,
                        await coordinator.UpdateSettingsAsync(update, cancellationToken).ConfigureAwait(false),
                        request.Id);

                case ArchiveMessageTypes.List:
                    var sets = coordinator.List();
                    if (roots is not null) sets = sets.Where(Inside).ToList();
                    return IpcMessage.Create(ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = sets, PackagesWithPassword = coordinator.PackagesWithPassword() }, request.Id);

                case ArchiveMessageTypes.Extract:
                    var extract = request.GetPayload<ArchiveExtractRequest>();
                    if (Confine(extract.FilePath) is var (extractPath, extractOk) && !extractOk)
                        return IpcMessage.Create(ArchiveMessageTypes.ExtractResult, new ArchiveExtractResult { Ok = false, Error = roots!.OutsideFileText() }, request.Id);
                    extract.FilePath = extractPath;
                    return IpcMessage.Create(
                        ArchiveMessageTypes.ExtractResult,
                        await coordinator.ExtractAsync(extract, cancellationToken).ConfigureAwait(false),
                        request.Id);

                case ArchiveMessageTypes.SetPassword:
                    var password = request.GetPayload<ArchiveSetPasswordRequest>();
                    if (Confine(password.FilePath) is var (passwordPath, passwordOk) && !passwordOk)
                        return Action(request, new ArchiveActionResult { Ok = false, Error = roots!.OutsideFileText() });
                    if (password.SetId is { } passwordSet && RefuseOutside(passwordSet) is { } passwordRefused) return Action(request, passwordRefused);
                    password.FilePath = passwordPath;
                    return Action(request, await coordinator.SetPasswordAsync(password, cancellationToken).ConfigureAwait(false));

                case ArchiveMessageTypes.Retry:
                    var retry = request.GetPayload<ArchiveActionRequest>().SetId;
                    return Action(request, RefuseOutside(retry) ?? await coordinator.RetryAsync(retry, cancellationToken).ConfigureAwait(false));

                case ArchiveMessageTypes.Cancel:
                    var cancel = request.GetPayload<ArchiveActionRequest>().SetId;
                    return Action(request, RefuseOutside(cancel) ?? await coordinator.CancelAsync(cancel, cancellationToken).ConfigureAwait(false));

                case ArchiveMessageTypes.Remove:
                    var remove = request.GetPayload<ArchiveActionRequest>().SetId;
                    return Action(request, RefuseOutside(remove) ?? await coordinator.RemoveAsync(remove, cancellationToken).ConfigureAwait(false));

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

    /// <summary>
    /// With roots, a set whose archives or output are not all inside them is left alone: running it again could write
    /// next to the archives, and with the user's settings overwrite or delete there. A set that isn't there is left to
    /// the coordinator to say so.
    /// </summary>
    private ArchiveActionResult? RefuseOutside(Guid setId)
        => roots is not null && coordinator.Find(setId) is { } set && !Inside(set)
            ? new ArchiveActionResult { Ok = false, Error = OutsideSetText(roots) }
            : null;

    /// <summary>Whether the set's folder (where all its volumes are) and its output folder, once it has one, are inside the roots.</summary>
    private bool Inside(ArchiveSetDto set)
        => roots!.Allowed(set.Directory) is not null &&
           (string.IsNullOrWhiteSpace(set.Destination) || roots.Allowed(set.Destination) is not null);

    public static string OutsideSetText(PathRoots roots) => $"These archives are outside {roots.Called}, so they can't be changed from another device.";

    private static IpcMessage Action(IpcMessage request, ArchiveActionResult result) =>
        IpcMessage.Create(ArchiveMessageTypes.ActionResult, result, request.Id);
}
