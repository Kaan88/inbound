using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a web UI call came to: the status the Host sent back, or words for the user.</summary>
internal sealed record WebUiResult(WebUiStatusDto? Status, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>The sign-in link of <c>webui.openTicket</c>, or words for the user.</summary>
internal sealed record WebUiTicketResult(string? Url, string? Error)
{
    public bool Ok => Error is null && !string.IsNullOrEmpty(Url);
}

// The web UI commands of the Host (webui.*), which only a session on the pipe may send.
internal sealed partial class RemoteClient
{
    /// <summary>The web UI changed on the Host: its state, address, password or firewall. Raised on the UI thread.</summary>
    public event Action<WebUiStatusDto>? WebUiChanged;

    public Task<WebUiResult> GetWebUiAsync(CancellationToken cancellationToken = default)
        => WebUiCallAsync(WebUiMessageTypes.Get, new { }, cancellationToken);

    public Task<WebUiResult> SetWebUiAsync(WebUiSetRequest request, CancellationToken cancellationToken = default)
        => WebUiCallAsync(WebUiMessageTypes.Set, request, cancellationToken);

    /// <summary>The password goes only into this one message. Nothing here keeps it.</summary>
    public Task<WebUiResult> SetWebUiPasswordAsync(string password, CancellationToken cancellationToken = default)
        => WebUiCallAsync(WebUiMessageTypes.SetPassword, new WebUiSetPasswordRequest { Password = password }, cancellationToken);

    /// <summary>Reads the firewall rule again, for example after this window added it.</summary>
    public Task<WebUiResult> RecheckWebUiAsync(CancellationToken cancellationToken = default)
        => WebUiCallAsync(WebUiMessageTypes.Recheck, new { }, cancellationToken);

    /// <summary>A link on this PC that signs in once and expires after a minute.</summary>
    public async Task<WebUiTicketResult> OpenWebUiTicketAsync(CancellationToken cancellationToken = default)
    {
        var result = await WebUiCoreAsync(WebUiMessageTypes.OpenTicket, new { }, cancellationToken, readTicket: true).ConfigureAwait(false);
        return result.Error is not null ? new WebUiTicketResult(null, result.Error) : new WebUiTicketResult(result.Ticket, null);
    }

    private async Task<WebUiResult> WebUiCallAsync<T>(string type, T request, CancellationToken cancellationToken)
        => await WebUiCoreAsync(type, request, cancellationToken, readTicket: false).ConfigureAwait(false);

    private async Task<WebUiCallResult> WebUiCoreAsync<T>(string type, T request, CancellationToken cancellationToken, bool readTicket)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        deadline.CancelAfter(_quickTimeout);
        try
        {
            Start();
            var reply = await RequestOnControlAsync(type, request, deadline.Token).ConfigureAwait(false);
            return ReadWebUiReply(reply, readTicket);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsCommunicationFailure(ex))
        {
            return new WebUiCallResult(null, null, DescribeFailure(ex, reachedHost: ex is IOException || ControlSource().Task.IsCompletedSuccessfully));
        }
    }

    /// <summary>
    /// A Host that doesn't know the command answers "ok: false" in a host action result, like any unknown message.
    /// A refusal carries the Host's own words. Otherwise the reply is the status (or, for a ticket, the link).
    /// </summary>
    internal static WebUiCallResult ReadWebUiReply(IpcMessage reply, bool readTicket = false)
    {
        try
        {
            if (reply.Type == MessageTypes.ServerIncompatible) return new WebUiCallResult(null, null, RemoteText.HostIncompatible);
            if (reply.Type == MessageTypes.HostActionResult)
            {
                var action = reply.GetPayload<HostActionResult>();
                if (action.Ok && !readTicket) return new WebUiCallResult(null, null, null);
                return new WebUiCallResult(null, null, IsUnknownMessage(action.Message)
                    ? RemoteText.HostDoesNotOffer
                    : RemoteText.Failure(action.Message));
            }

            if (!reply.Type.StartsWith("webui.", StringComparison.Ordinal))
                return new WebUiCallResult(null, null, RemoteText.HostDoesNotOffer);

            if (readTicket)
            {
                var ticket = reply.GetPayload<WebUiTicketDto>();
                return string.IsNullOrWhiteSpace(ticket.Url)
                    ? new WebUiCallResult(null, null, RemoteText.HostUnreadable)
                    : new WebUiCallResult(null, ticket.Url, null);
            }

            return new WebUiCallResult(reply.GetPayload<WebUiStatusDto>(), null, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        {
            return new WebUiCallResult(null, null, RemoteText.HostUnreadable);
        }
    }

    private static bool IsUnknownMessage(string? message)
        => message is not null && message.StartsWith("Unknown message type", StringComparison.OrdinalIgnoreCase);

    private void RaiseWebUiChanged(IpcMessage message)
    {
        var status = message.GetPayload<WebUiStatusDto>();
        _post(() => WebUiChanged?.Invoke(status));
    }

    /// <summary>What <see cref="ReadWebUiReply"/> found.</summary>
    internal readonly record struct WebUiCallResult(WebUiStatusDto? Status, string? Ticket, string? Error)
    {
        public static implicit operator WebUiResult(WebUiCallResult result) => new(result.Status, result.Error);
    }
}
