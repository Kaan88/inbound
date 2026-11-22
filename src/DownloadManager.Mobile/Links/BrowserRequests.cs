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
            _requests[normalized] = new BrowserRequestObservation(page, observed, nonGet, ambiguous, _document) { MainFrame = mainFrame };
        }
    }
    public BrowserRequestObservation? Take(string url)
    {
        lock (_gate)
        {
            if (_requests.Remove(url, out var request)) return request.Document == _document ? request : null;
            return RedirectedLocked(url);
        }
    }

    /// <summary>
    /// WebView reports neither redirect hops nor the request a download came from, and only the first address of a chain
    /// reaches <see cref="Observe"/> (review A3). A download at an address never observed is taken as the end of a redirect
    /// only when exactly one top-level request of this document is still open (a download never commits a page) and it was
    /// a plain GET from this page: a redirect from a GET stays a GET. Anything else stays unknown and is refused.
    /// </summary>
    private BrowserRequestObservation? RedirectedLocked(string url)
    {
        if (LinkRules.Normalize(url) is not { } final) return null;
        string? key = null;
        foreach (var (address, observation) in _requests)
        {
            if (!observation.MainFrame || observation.Document != _document) continue;
            if (key is not null) return null;
            key = address;
        }
        if (key is null || !_requests.TryGetValue(key, out var origin) || origin.Ambiguous || origin.KnownNonGet) return null;
        _requests.Remove(key);
        // The browser sends a cross-origin hop only the referrer's origin (the default policy, strict-origin-when-cross-origin).
        var referrer = origin.Referrer;
        if (referrer is not null && Uri.TryCreate(referrer, UriKind.Absolute, out var from) && Uri.TryCreate(final, UriKind.Absolute, out var to)
            && from.GetLeftPart(UriPartial.Authority) != to.GetLeftPart(UriPartial.Authority))
            referrer = to.Scheme == Uri.UriSchemeHttp && from.Scheme == Uri.UriSchemeHttps ? null : from.GetLeftPart(UriPartial.Authority) + "/";
        return new BrowserRequestObservation(origin.PageUrl, referrer, false, false, origin.Document) { MainFrame = true, Redirected = true };
    }
}

public sealed class BrowserRequestObservation(string? page, string? referrer, bool knownNonGet, bool ambiguous, long document)
{
    public string? PageUrl { get; } = page;
    public string? Referrer { get; } = referrer;
    public bool KnownNonGet { get; } = knownNonGet;
    public bool Ambiguous { get; } = ambiguous;
    internal long Document { get; } = document;
    internal bool MainFrame { get; init; }
    /// <summary>The download's address was reached through a redirect from the observed request.</summary>
    public bool Redirected { get; init; }
    public override string ToString() => "Browser request evidence (details hidden)";
}
