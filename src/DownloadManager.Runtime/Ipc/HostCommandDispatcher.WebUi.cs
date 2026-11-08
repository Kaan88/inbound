using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    private const string WebUiOnlyHere = "The web UI can only be set up in the app on this PC.";
    private const string WebUiUnavailable = "The web UI is not available here.";

    /// <summary>
    /// Answers <c>webui.*</c> (D8): the desktop Host's web UI. Null (the headless server) refuses them. Only a session
    /// with the desktop role gets an answer; a web session, a browser through the bridge with a cookie or a paired
    /// token, is refused, so nothing on another device can turn the web UI on or off, change its port or password, or get
    /// a sign-in ticket.
    /// </summary>
    public IWebUiCommands? WebUi { get; init; }

    private async Task AnswerWebUiAsync(IClientSession client, SessionRights rights, IpcMessage message, CancellationToken cancellationToken)
    {
        if (!string.Equals(rights.Role, ClientRoles.Desktop, StringComparison.Ordinal) || rights.Scope is not null)
        {
            client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = WebUiOnlyHere }, message.Id));
            return;
        }

        if (WebUi is not { } webUi)
        {
            client.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = WebUiUnavailable }, message.Id));
            return;
        }

        client.Enqueue(await webUi.HandleAsync(message, cancellationToken).ConfigureAwait(false));
    }

    /// <summary><c>webui.changed</c> to the sessions of the user at this PC that take events, never to a web session.</summary>
    public void BroadcastWebUiChanged(WebUiStatusDto status)
    {
        var message = IpcMessage.Create(WebUiMessageTypes.Changed, status);
        foreach (var client in _sessions.Values)
        {
            if (RightsOf(client) is { Role: ClientRoles.Desktop, Scope: null }) client.Enqueue(message);
        }
    }
}
