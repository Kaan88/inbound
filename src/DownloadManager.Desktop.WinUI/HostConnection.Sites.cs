using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>SITES.3: the Sites requests of the browse view. Each answers with a result object, never an exception, so the view can show the words.</summary>
public sealed partial class HostConnection
{
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

    private void DispatchSitesChanged(IpcMessage message)
    {
        try
        {
            if (message.GetPayload<SitesChangedDto>() is { } change) SitesChanged?.Invoke(change);
        }
        catch (System.Text.Json.JsonException) { /* a malformed event is dropped */ }
    }
}
