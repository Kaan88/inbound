using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Links;

/// <summary>One transient browser capture. Cookie material is never part of its display or string representation.</summary>
public sealed class CapturedDownload
{
    private readonly string? _cookie;
    public string Url { get; }
    public string FileName { get; }
    public string? ContentType { get; }
    public long? SizeHint { get; }
    public string? PageUrl { get; }
    public string? Referrer { get; }
    public string? UserAgent { get; }
    public bool HasCookies => _cookie is not null;
    public string RequestId { get; } = "android-capture-" + Guid.NewGuid().ToString("N");

    public CapturedDownload(string url, string? fileName = null, string? contentType = null, long? sizeHint = null,
        string? pageUrl = null, string? userAgent = null, string? cookie = null, string? observedReferrer = null)
    {
        Url = LinkRules.Normalize(url) ?? throw new ArgumentException("Only HTTP and HTTPS download addresses are supported.");
        PageUrl = LinkRules.Normalize(pageUrl);
        FileName = SafeFileName(fileName);
        ContentType = Header(contentType, 512);
        SizeHint = sizeHint is >= 0 ? sizeHint : null;
        UserAgent = Header(userAgent, 4096);
        Referrer = SafeReferrer(null, Url, observedReferrer); // an omitted browser Referer stays omitted
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            if (!SameOrigin(Url, PageUrl)) throw new ArgumentException("Cookie capture requires the matching page origin.");
            _cookie = Header(cookie, 32768);
        }
    }

    public FileMetadataRequest Probe(bool remote, bool cookieConsent) => new()
    {
        Url = Url, Headers = Headers(remote, cookieConsent), UserAgent = UserAgent, Referrer = Referrer,
    };

    public AddJobRequest Add(bool remote, bool cookieConsent, string? destination, int connections, bool later) => new()
    {
        Url = Url, FileName = FileName.Length == 0 ? null : FileName, PageUrl = PageUrl,
        Headers = Headers(remote, cookieConsent), UserAgent = UserAgent, Referrer = Referrer, ContentType = ContentType,
        DestinationDirectory = destination, Connections = Math.Max(1, connections),
        StartMode = later ? StartModes.Later : StartModes.StartNow, Source = "app", ClientRequestId = RequestId, OwnerExtensionId = "inbound-android-capture",
    };

    public JobRefreshUrlRequest Refresh(Guid jobId, bool remote, bool cookieConsent) => new()
    {
        JobId = jobId, Url = Url,
        BrowserContext = new BrowserRequestContext { Headers = Headers(remote, cookieConsent), UserAgent = UserAgent, Referrer = Referrer, PageUrl = PageUrl },
    };

    private Dictionary<string, string> Headers(bool remote, bool cookieConsent)
    {
        if (remote && HasCookies && !cookieConsent) throw new InvalidOperationException("Ask before sending sign-in cookies to this server.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_cookie is not null) headers["Cookie"] = _cookie;
        return headers;
    }

    public static bool SameOrigin(string? first, string? second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a) && Uri.TryCreate(second, UriKind.Absolute, out var b) &&
        a.Scheme is "http" or "https" && a.Scheme == b.Scheme && a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    public static string? SafeReferrer(string? page, string target, string? observed = null)
    {
        var source = LinkRules.Normalize(observed) ?? LinkRules.Normalize(page);
        if (source is null) return null;
        var from = new Uri(source); var to = new Uri(target);
        if (from.Scheme == "https" && to.Scheme == "http") return null;
        // An observed request header is the WebView's policy decision. Otherwise
        // use strict-origin-when-cross-origin, without fragments or userinfo.
        return LinkRules.Normalize(observed) is not null || SameOrigin(source, target)
            ? source : from.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static string SafeFileName(string? name)
    {
        var leaf = (name ?? "").Replace('\\', '/').Split('/').Last();
        return LinkRules.Name(leaf).TrimEnd(' ', '.');
    }

    private static string? Header(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("Browser request data is invalid or too large.");
        return value;
    }

    public override string ToString() => "Captured download (request details hidden)";
}
