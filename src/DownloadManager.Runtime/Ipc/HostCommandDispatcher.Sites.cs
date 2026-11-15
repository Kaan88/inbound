using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    private const string SitesOnlyHere = "Sites can only be used from the app on this PC for now.";
    private const string SitesUnavailable = "Sites are not available here.";

    /// <summary>
    /// Answers <c>sites.*</c> (SITES.2). Null (a Host built without Sites) refuses them. Only the user at this PC gets an
    /// answer for now: a web session will be answered as admin only (owner, 2026-11-15), which comes with the web UI
    /// (SITES.7).
    /// </summary>
    public ISitesCommands? Sites { get; init; }

    private async Task AnswerSitesAsync(IClientSession client, SessionRights rights, IpcMessage message, CancellationToken cancellationToken)
    {
        if (!string.Equals(rights.Role, ClientRoles.Desktop, StringComparison.Ordinal) || rights.Scope is not null)
        {
            client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = SitesOnlyHere }, message.Id));
            return;
        }

        if (Sites is not { } sites)
        {
            client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = SitesUnavailable }, message.Id));
            return;
        }

        client.Enqueue(await sites.HandleAsync(message, cancellationToken).ConfigureAwait(false));
    }

    /// <summary><c>sites.changed</c> to the sessions of the user at this PC, never to a web session.</summary>
    public void BroadcastSitesChanged(SitesChangedDto change)
    {
        var message = IpcMessage.Create(SitesMessageTypes.Changed, change);
        foreach (var client in _sessions.Values)
        {
            if (RightsOf(client) is { Role: ClientRoles.Desktop, Scope: null }) client.Enqueue(message);
        }
    }

    /// <summary><c>sites.transfer.changed</c> to the same sessions as <see cref="BroadcastSitesChanged"/>.</summary>
    public void BroadcastSiteTransfersChanged(SiteTransfersDto change)
    {
        var message = IpcMessage.Create(SitesMessageTypes.TransferChanged, change);
        foreach (var client in _sessions.Values)
        {
            if (RightsOf(client) is { Role: ClientRoles.Desktop, Scope: null }) client.Enqueue(message);
        }
    }
}
