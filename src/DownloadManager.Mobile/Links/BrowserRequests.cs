namespace DownloadManager.Mobile.Links;

/// <summary>Bounded, one-shot evidence from main-frame requests belonging to a committed document.</summary>
public sealed class BrowserRequests
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BrowserRequestObservation> _requests = new(StringComparer.Ordinal);
    private string? _page;
    private long _document;

    public void CommitPage(string? url)
    {
        lock (_gate) { _document++; _page = LinkRules.Normalize(url); _requests.Clear(); }
    }
    public void Observe(string? url, string? method, bool mainFrame, string? referrer)
    {
        if (LinkRules.Normalize(url) is not { } normalized) return;
        lock (_gate)
        {
            if (_requests.Count >= 128 && !_requests.ContainsKey(normalized)) return;
            var nonGet = !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
            var observed = LinkRules.Normalize(referrer);
            // A full referrer from a different document is not evidence of the committed page.
            var page = _page;
            var sourceMatches = observed is null || page is not null && (observed == page ||
                observed == new Uri(page).GetLeftPart(UriPartial.Authority) + "/");
            var ambiguous = !mainFrame || _requests.ContainsKey(normalized) || !sourceMatches || page is null;
            if (_requests.TryGetValue(normalized, out var previous)) nonGet |= previous.KnownNonGet;
            _requests[normalized] = new BrowserRequestObservation(page, observed, nonGet, ambiguous, _document);
        }
    }
    public BrowserRequestObservation? Take(string url)
    {
        lock (_gate)
        {
            if (!_requests.Remove(url, out var request) || request.Document != _document) return null;
            return request;
        }
    }
}

public sealed class BrowserRequestObservation(string? page, string? referrer, bool knownNonGet, bool ambiguous, long document)
{
    public string? PageUrl { get; } = page;
    public string? Referrer { get; } = referrer;
    public bool KnownNonGet { get; } = knownNonGet;
    public bool Ambiguous { get; } = ambiguous;
    internal long Document { get; } = document;
    public override string ToString() => "Browser request evidence (details hidden)";
}
