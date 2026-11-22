namespace DownloadManager.Mobile.Links;

/// <summary>Bounded, one-shot evidence from main-frame requests belonging to a committed document.</summary>
public sealed class BrowserRequests
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BrowserRequestObservation> _requests = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;
    private string? _page;
    private long _document;
    // The table was full and a request went unrecorded since this document committed: a redirect can't be traced.
    private bool _dropped;

    /// <summary>How long a top-level request stays a candidate for a redirected download (audit A3-1).</summary>
    public const long RedirectWindowMs = 30_000;

    public BrowserRequests(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    public void CommitPage(string? url)
    {
        lock (_gate) { _document++; _page = LinkRules.Normalize(url); _requests.Clear(); _dropped = false; }
    }
    public void Observe(string? url, string? method, bool mainFrame, string? referrer)
    {
        if (LinkRules.Normalize(url) is not { } normalized) return;
        lock (_gate)
        {
            if (_requests.Count >= 128 && !_requests.ContainsKey(normalized)) { _dropped = true; return; }
            var nonGet = !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
            var observed = LinkRules.Normalize(referrer);
            // A full referrer from a different document is not evidence of the committed page.
            var page = _page;
            var sourceMatches = observed is null || page is not null && (observed == page ||
                observed == new Uri(page).GetLeftPart(UriPartial.Authority) + "/");
            var ambiguous = !mainFrame || _requests.ContainsKey(normalized) || !sourceMatches || page is null;
            if (_requests.TryGetValue(normalized, out var previous)) nonGet |= previous.KnownNonGet;
            _requests[normalized] = new BrowserRequestObservation(page, observed, nonGet, ambiguous, _document) { MainFrame = mainFrame, ObservedAtMs = _nowMs() };
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
    /// Audit A3-1/A3-2 (2026-11-22): only a request from the last <see cref="RedirectWindowMs"/> counts, so a stale one that
    /// never ended can neither be matched later nor block a newer one; one that is refused as a source is dropped; and
    /// nothing is traced once the table overflowed. Sub-frame requests after the candidate don't refuse it: pages keep
    /// loading resources after a click.
    /// </summary>
    private BrowserRequestObservation? RedirectedLocked(string url)
    {
        if (_dropped || LinkRules.Normalize(url) is not { } final) return null;
        var now = _nowMs();
        string? key = null;
        foreach (var (address, observation) in _requests)
        {
            if (!observation.MainFrame || observation.Document != _document || now - observation.ObservedAtMs > RedirectWindowMs) continue;
            if (key is not null) return null;
            key = address;
        }
        if (key is null || !_requests.TryGetValue(key, out var origin)) return null;
        _requests.Remove(key);
        if (origin.Ambiguous || origin.KnownNonGet) return null;
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
    internal long ObservedAtMs { get; init; }
    /// <summary>The download's address was reached through a redirect from the observed request.</summary>
    public bool Redirected { get; init; }
    public override string ToString() => "Browser request evidence (details hidden)";
}
