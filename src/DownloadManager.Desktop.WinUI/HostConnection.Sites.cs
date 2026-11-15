using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>SITES.3: the Sites requests of the browse view. Each answers with a result object, never an exception, so the view can show the words.</summary>
public sealed partial class HostConnection
{
    // The browse and star types are written here until SiteDtos.cs names them (the Host side of SITES.3).
    private const string SitesBrowseType = "sites.browse";
    private const string SitesBrowseResultType = "sites.browseResult";
    private const string SitesStarType = "sites.star";

    /// <summary>Sites on this PC's Host push their state to the window as <see cref="SitesMessageTypes.Changed"/>.</summary>
    public event Action<SitesChangedDto>? SitesChanged;

    /// <summary>True when the Host says it has Sites (its hello capability <c>sites</c>); an older Host would answer "unknown message".</summary>
    public bool HostHasSites => _hello?.Capabilities.Contains("sites", StringComparer.Ordinal) == true;

    public async Task<SitesStatusDto?> GetSitesStatusAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(SitesMessageTypes.Get, new EmptyPayload(), TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        return reply.Type == SitesMessageTypes.Status ? reply.GetPayload<SitesStatusDto>() : null;
    }

    public async Task<List<SiteDto>?> ListSitesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(SitesMessageTypes.ListRequest, new EmptyPayload(), TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        return reply.Type == SitesMessageTypes.List ? reply.GetPayload<SitesListDto>()?.Sites : null;
    }

    /// <summary>One page of a folder. A listing can be slow (a far server, a big folder), so the wait is long.</summary>
    public async Task<SiteBrowseResultDto> BrowseSiteAsync(SiteBrowseRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await RequestAsync(SitesBrowseType, request, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            if (reply.Type == SitesBrowseResultType && reply.GetPayload<SiteBrowseResultDto>() is { } result) return result;
            return new SiteBrowseResultDto { Message = SitesReplyError(reply) };
        }
        catch (TimeoutException)
        {
            return new SiteBrowseResultDto { Message = "The site did not answer in time." };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new SiteBrowseResultDto { Message = "Inbound's background program is not connected." };
        }
    }

    /// <summary>Stars or unstars a folder; the answer's <c>Site</c> has the new list of stars.</summary>
    public async Task<SiteResultDto> StarSiteFolderAsync(SiteStarRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await RequestAsync(SitesStarType, request, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (reply.Type == SitesMessageTypes.Result && reply.GetPayload<SiteResultDto>() is { } result) return result;
            return new SiteResultDto { Message = SitesReplyError(reply) };
        }
        catch (TimeoutException)
        {
            return new SiteResultDto { Message = "Inbound's background program did not answer in time." };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new SiteResultDto { Message = "Inbound's background program is not connected." };
        }
    }

    private static string SitesReplyError(IpcMessage reply)
    {
        if (reply.Type == MessageTypes.HostActionResult)
        {
            try
            {
                if (IsUnknownRequest(reply)) return "This version of Inbound's background program does not browse sites. Update it.";
                var message = reply.GetPayload<HostActionResult>().Message?.Trim();
                if (!string.IsNullOrEmpty(message)) return message;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
            {
                // Falls through to the general words.
            }
        }

        return "Inbound's background program gave an answer this window could not read.";
    }

    private void DispatchSitesChanged(IpcMessage message)
    {
        try
        {
            if (message.GetPayload<SitesChangedDto>() is { } change) SitesChanged?.Invoke(change);
        }
        catch { /* ignore malformed events */ }
    }
}
