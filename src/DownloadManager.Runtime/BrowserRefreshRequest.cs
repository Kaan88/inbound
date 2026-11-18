using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>Copies and validates optional browser context before refresh can await or mutate a job.</summary>
internal static class BrowserRefreshRequest
{
    internal static BrowserRequestContext Copy(BrowserRequestContext value, string target)
    {
        var page = Address(value.PageUrl);
        var referrer = Address(value.Referrer);
        if (value.Headers is null || value.Headers.Count > 1) throw new ArgumentException();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in value.Headers)
        {
            // This narrow extension is browser-cookie refresh, not arbitrary header replacement.
            if (!pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || !Header(pair.Value, 32768)
                || string.IsNullOrWhiteSpace(pair.Value) || !SameOrigin(page, target)) throw new ArgumentException();
            headers.Add("Cookie", pair.Value);
        }
        if (value.PageUrl is not null && page is null || value.Referrer is not null && referrer is null
            || !Header(value.UserAgent, 4096)) throw new ArgumentException();
        if (referrer is not null && new Uri(referrer).Scheme == "https" && new Uri(target).Scheme == "http") referrer = null;
        return new BrowserRequestContext { Headers = headers, PageUrl = page, Referrer = referrer, UserAgent = value.UserAgent };
    }
    private static bool Header(string? value, int max) => value is null || value.Length <= max && !value.Any(char.IsControl);
    private static bool SameOrigin(string? page, string target) => page is not null &&
        new Uri(page).GetLeftPart(UriPartial.Authority).Equals(new Uri(target).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    private static string? Address(string? value)
    {
        if (value is null || value.Length > 8192 || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return null;
        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
    }
}
