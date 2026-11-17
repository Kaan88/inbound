using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    private const string SitesOnlyHere = "Sites can't be used from here.";
    private const string OwnRcloneHere = "Your own rclone can only be chosen in the app on the computer Inbound runs on.";
    private const string SitesUnavailable = "Sites are not available here.";

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

        if ((rights.Scope?.Roots ?? SitesRoots) is { } roots && ScopedSites(roots, sites, ref message) is { } refusal)
        {
            client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = refusal }, message.Id));
            return;
        }

        client.Enqueue(await sites.HandleAsync(message, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The sessions <c>sites.*</c> is answered for: this PC's user and web sessions (SITES.7).</summary>
    private static bool SitesAnswered(SessionRights rights) =>
        rights.Role is ClientRoles.Desktop or ClientRoles.Web;

    /// <summary>
    /// A web session's request, held to its folders: the folder a site saves into and a transfer's folder on this
    /// computer must be inside them, also when a batch made elsewhere (by the user at this PC) is resumed or retried, and
    /// Use my own rclone is not changed. The message is rewritten with the resolved folder. The reason, or null when it
    /// may go on.
    /// </summary>
    private static string? ScopedSites(PathRoots roots, ISitesCommands sites, ref IpcMessage message)
    {
        switch (message.Type)
        {
            case SitesMessageTypes.TransferResume or SitesMessageTypes.TransferRetry or SitesMessageTypes.TransferStartFile:
            {
                // Pause, cancel, get, clear and stopFile move nothing; these start the batch's files moving again
                // (a file request has the same Id field).
                var id = message.GetPayload<SiteTransferIdRequest>()?.Id ?? "";
                if (sites.TransferLocalFolder(id) is not { } folder) return null; // no such batch: Sites says so
                return roots.Allowed(folder) is null ? roots.OutsideTextFor(folder) : null;
            }
            case SitesMessageTypes.SetEnabled:
                return message.GetPayload<SitesSetEnabledRequest>()?.OwnRclonePath is not null ? OwnRcloneHere : null;
            case SitesMessageTypes.Save:
            {
                var save = message.GetPayload<SiteSaveRequest>();
                if (save is null) return null;
                // SITES.11: a web session's SFTP site never signs in with this PC's ssh-agent keys.
                save.OwnSignInOnly = true;
                if (!string.IsNullOrWhiteSpace(save.LocalFolder))
                {
                    if (roots.Allowed(save.LocalFolder) is not { } real) return roots.OutsideTextFor(save.LocalFolder);
                    save.LocalFolder = real;
                }

                message = message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(save, IpcJson.Options) };
                return null;
            }
            case SitesMessageTypes.TestDraft:
            {
                // SITES.11 R4: a draft is checked as a save is, so it is held to the same rules.
                var draft = message.GetPayload<SiteTestDraftRequest>();
                if (draft is null) return null;
                draft.Site ??= new SiteSaveRequest();
                draft.Site.OwnSignInOnly = true;
                if (!string.IsNullOrWhiteSpace(draft.Site.LocalFolder))
                {
                    if (roots.Allowed(draft.Site.LocalFolder) is not { } real) return roots.OutsideTextFor(draft.Site.LocalFolder);
                    draft.Site.LocalFolder = real;
                }

                message = message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(draft, IpcJson.Options) };
                return null;
            }
            case SitesMessageTypes.TransferStart:
            {
                var start = message.GetPayload<SiteTransferStartRequest>();
                if (start is null) return null;
                if (roots.Allowed(start.LocalFolder) is not { } real) return roots.OutsideTextFor(start.LocalFolder);
                start.LocalFolder = real;
                message = message with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(start, IpcJson.Options) };
                return null;
            }
            default:
                return null;
        }
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
