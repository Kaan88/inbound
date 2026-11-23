using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    private const string SitesOnlyHere = "Remote files can't be used from here.";
    private const string OwnRcloneHere = "Your own rclone can only be chosen in the app on the computer Inbound runs on.";
    private const string SitesUnavailable = "Remote files are not available here.";

    /// <summary>
    /// Answers <c>sites.*</c> (SITES.2). Null (a Host built without Sites) refuses them. The user at this PC and, since
    /// SITES.7, a web session (signed in with the password: the admin, as the owner asked on 2026-11-15) get answers. A
    /// web session's folders on the computer Inbound runs on are held to its own (<see cref="SessionRights.Scope"/>), and
    /// it can't choose a program to run (Use my own rclone).
    /// </summary>
    public ISitesCommands? Sites { get; init; }

    /// <summary>
    /// SITES.7, the server: the folders on this computer every caller's Sites requests are held to, as a web session's
    /// are to its own (<see cref="SessionRights.Scope"/>). Null on a desktop Host, whose own user may use any folder.
    /// </summary>
    public PathRoots? SitesRoots { get; init; }

    private async Task AnswerSitesAsync(IClientSession client, SessionRights rights, IpcMessage message, CancellationToken cancellationToken)
    {
        if (!SitesAnswered(rights))
        {
            client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = SitesOnlyHere }, message.Id));
            return;
        }

        if (Sites is not { } sites)
        {
            client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = SitesUnavailable }, message.Id));
            return;
        }

        if ((rights.Scope?.Roots ?? SitesRoots) is { } roots)
        {
            var (refusal, scoped) = await ScopedSitesAsync(roots, sites, message, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
            {
                client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = refusal }, message.Id));
                return;
            }

            message = scoped;
        }

        client.Enqueue(await sites.HandleAsync(message, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The sessions <c>sites.*</c> is answered for: this PC's user and web sessions (SITES.7).</summary>
    private static bool SitesAnswered(SessionRights rights) =>
        rights.Role is ClientRoles.Desktop or ClientRoles.Web;

    /// <summary>
    /// A web session's request, held to its folders: the folder a site saves into and a transfer's folder on this
    /// computer must be inside them, also when a batch made elsewhere (by the user at this PC) is resumed or retried, and
    /// Use my own rclone is not changed. The message is rewritten with the resolved folder. The reason, and the message to
    /// go on with, or the message as it was when it may go on unchanged.
    /// </summary>
    private static async Task<(string? Refusal, IpcMessage Message)> ScopedSitesAsync(PathRoots roots, ISitesCommands sites,
        IpcMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case SitesMessageTypes.SyncSchedule:
            {
                // SITES.13: a scheduled sync's folder here is held to the session's folders, as a sync's is.
                var schedule = message.GetPayload<SiteSyncScheduleRequest>();
                if (schedule is null || !schedule.Enabled) return (null, message);
                if (string.IsNullOrWhiteSpace(schedule.LocalFolder)) return ("Choose the folder on this computer to sync with.", message);
                if (roots.Allowed(schedule.LocalFolder) is not { } real) return (roots.OutsideTextFor(schedule.LocalFolder), message);
                schedule.LocalFolder = real;
                return (null, message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(schedule, IpcJson.Options) });
            }
            case SitesMessageTypes.TransferResume or SitesMessageTypes.TransferRetry or SitesMessageTypes.TransferStartFile:
            {
                // Pause, cancel, get, clear and stopFile move nothing; these start the batch's files moving again
                // (a file request has the same Id field).
                var id = message.GetPayload<SiteTransferIdRequest>()?.Id ?? "";
                if (sites.TransferLocalFolder(id) is not { } folder) return (null, message); // no such batch: Sites says so
                return (roots.Allowed(folder) is null ? roots.OutsideTextFor(folder) : null, message);
            }
            case SitesMessageTypes.TransferSchedule:
            {
                // Waiting for the schedule starts the batch at the schedule's start, so it is held as a resume is;
                // Wait = false only keeps it paused.
                var schedule = message.GetPayload<SiteTransferScheduleRequest>();
                if (schedule is not { Wait: true } || sites.TransferLocalFolder(schedule.Id) is not { } folder) return (null, message);
                return (roots.Allowed(folder) is null ? roots.OutsideTextFor(folder) : null, message);
            }
            case SitesMessageTypes.SetEnabled:
                return (message.GetPayload<SitesSetEnabledRequest>()?.OwnRclonePath is not null ? OwnRcloneHere : null, message);
            case SitesMessageTypes.Save:
            {
                var save = message.GetPayload<SiteSaveRequest>();
                if (save is null) return (null, message);
                // SITES.11: a web session's SFTP site never signs in with this PC's ssh-agent keys.
                save.OwnSignInOnly = true;
                if (!string.IsNullOrWhiteSpace(save.LocalFolder)
                    && !await KeptLinkedFolderAsync(sites, save.Id, save.LocalFolder, cancellationToken).ConfigureAwait(false))
                {
                    if (roots.Allowed(save.LocalFolder) is not { } real) return (roots.OutsideTextFor(save.LocalFolder), message);
                    save.LocalFolder = real;
                }

                return (null, message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(save, IpcJson.Options) });
            }
            case SitesMessageTypes.TestDraft:
            {
                // SITES.11 R4: a draft is checked as a save is, so it is held to the same rules.
                var draft = message.GetPayload<SiteTestDraftRequest>();
                if (draft is null) return (null, message);
                draft.Site ??= new SiteSaveRequest();
                draft.Site.OwnSignInOnly = true;
                if (!string.IsNullOrWhiteSpace(draft.Site.LocalFolder)
                    && !await KeptLinkedFolderAsync(sites, draft.Site.Id, draft.Site.LocalFolder, cancellationToken).ConfigureAwait(false))
                {
                    if (roots.Allowed(draft.Site.LocalFolder) is not { } real) return (roots.OutsideTextFor(draft.Site.LocalFolder), message);
                    draft.Site.LocalFolder = real;
                }

                return (null, message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(draft, IpcJson.Options) });
            }
            case SitesMessageTypes.SyncPlan or SitesMessageTypes.SyncRun:
            {
                // SITES.13: the folder here is named, so it can be checked; the remote's linked folder is not looked up.
                var sync = message.GetPayload<SiteSyncRequest>();
                if (sync is null) return (null, message);
                if (string.IsNullOrWhiteSpace(sync.LocalFolder)) return ("Choose the folder on this computer to sync with.", message);
                if (roots.Allowed(sync.LocalFolder) is not { } real) return (roots.OutsideTextFor(sync.LocalFolder), message);
                sync.LocalFolder = real;
                return (null, message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(sync, IpcJson.Options) });
            }
            case SitesMessageTypes.TransferStart or SitesMessageTypes.SafeDownloadStart:
            {
                var start = message.GetPayload<SiteTransferStartRequest>();
                if (start is null) return (null, message);
                // SITES.13: between remotes nothing is read or written on this computer ("copy" is Android 0.1.0 to 0.1.2's name).
                if (start.Direction is "between" or "copy" && message.Type == SitesMessageTypes.TransferStart) return (null, message);
                if (roots.Allowed(start.LocalFolder) is not { } real) return (roots.OutsideTextFor(start.LocalFolder), message);
                start.LocalFolder = real;
                return (null, message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(start, IpcJson.Options) });
            }
            default:
                return (null, message);
        }
    }

    /// <summary>
    /// SITES.10 (5): the save (or a test of an edit) keeps the linked folder the remote already has, so an edit is not
    /// refused for a folder the user did not change. A folder that is new, changed, or on a remote this PC doesn't know
    /// is held to the session's folders as before.
    /// </summary>
    private static async Task<bool> KeptLinkedFolderAsync(ISitesCommands sites, string? siteId, string folder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(siteId)) return false;
        var held = await sites.LinkedFolderAsync(siteId, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(held) && PathNames.Same(held.Trim(), folder.Trim());
    }

    /// <summary><c>sites.changed</c> to the sessions <c>sites.*</c> is answered for.</summary>
    public void BroadcastSitesChanged(SitesChangedDto change)
    {
        var message = IpcMessage.Create(SitesMessageTypes.Changed, change);
        foreach (var client in _sessions.Values)
        {
            if (RightsOf(client) is { } rights && SitesAnswered(rights)) client.Enqueue(message);
        }
    }

    /// <summary><c>sites.transfer.changed</c> to the same sessions as <see cref="BroadcastSitesChanged"/>.</summary>
    public void BroadcastSiteTransfersChanged(SiteTransfersDto change)
    {
        var message = IpcMessage.Create(SitesMessageTypes.TransferChanged, change);
        foreach (var client in _sessions.Values)
        {
            if (RightsOf(client) is { } rights && SitesAnswered(rights)) client.Enqueue(message);
        }
    }
}
