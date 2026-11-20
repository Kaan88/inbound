using System.Text;
using System.Text.RegularExpressions;

namespace DownloadManager.Mobile.Links;

public enum PageLinkKind { All, Archives, Audio, Video, Images, Documents, Other }
public sealed record PageLink(string Url, string Name, long? SizeBytes = null);

internal static class LinkRules
{
    internal const int MaxUrl = 8192;
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxUrl || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        // Remove only the fragment, keeping the escaped path and query as supplied by Uri.
        var result = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return result.Length <= MaxUrl ? result : null;
    }
    internal static string Name(string? value)
    {
        var b = new StringBuilder(200);
        foreach (var c in value ?? "")
        {
            if (!char.IsControl(c)) b.Append(c);
            if (b.Length == 200) break;
        }
        var name = b.ToString().Trim();
        return name.Length <= 200 ? name : name[..200];
    }
}

public static partial class SharedLinks
{
    private const int MaxInput = 64 * 1024;
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        // A browser may share just one signed URL. Its final punctuation can be
        // part of a token/path; only prose extraction uses punctuation heuristics.
        var direct = LinkRules.Normalize(text.Trim());
        if (direct is not null) return [direct];
        var truncated = text.Length > MaxInput;
        if (truncated) text = text[..MaxInput];
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UrlPattern().Matches(text))
        {
            var candidate = match.Value;
            // Never accept a truncated URL at the input boundary.
            if (match.Index + match.Length == text.Length && truncated) continue;
            candidate = TrimPunctuation(candidate);
            var url = LinkRules.Normalize(candidate);
            if (url is not null && seen.Add(url)) { result.Add(url); if (result.Count == 100) break; }
        }
        return result;
    }
    private static string TrimPunctuation(string value)
    {
        var end = value.Length;
        while (end > 0)
        {
            var c = value[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or ']' or '}') { end--; continue; }
            if (c == ')' && value.AsSpan(0, end).Count(')') > value.AsSpan(0, end).Count('(')) { end--; continue; }
            break;
        }
        return value[..end];
    }
    [GeneratedRegex(@"(?i)https?://[^\s<>\x00-\x1f\x7f]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}

public sealed class PageLinks
{
    private readonly List<PageLink> _links = [];
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    public PageLinks(IEnumerable<PageLink> links)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inspected = 0;
        foreach (var link in links)
        {
            if (++inspected > 10000) break;
            if (link is null) continue;
            var url = LinkRules.Normalize(link.Url);
            if (url is null || !seen.Add(url)) continue;
            _links.Add(new PageLink(url, LinkRules.Name(link.Name), link.SizeBytes is >= 0 ? link.SizeBytes : null));
            if (_links.Count == 2000) break;
        }
    }
    public string Query { get; set; } = "";
    public PageLinkKind Kind { get; set; } = PageLinkKind.All;
    public IReadOnlyList<PageLink> Visible => _links.Where(Matches).ToArray();
    public IReadOnlyList<PageLink> DisplayRows => Kind == PageLinkKind.All || !string.IsNullOrWhiteSpace(Query)
        ? Visible : _links.OrderByDescending(link => Classify(new Uri(link.Url).AbsolutePath) == Kind).ToArray();

    public IReadOnlyList<PageLink> Selected => _links.Where(link => _selected.Contains(link.Url)).ToArray();
    public long? SelectedBytes => TotalBytes(_links.Where(link => _selected.Contains(link.Url)));
    public long? VisibleBytes => TotalBytes(Visible);
    private static long? TotalBytes(IEnumerable<PageLink> links)
    {
        long? total = null;
        foreach (var link in links)
        {
            if (link.SizeBytes is not long size || size > long.MaxValue - total.GetValueOrDefault()) return null;
            total = total.GetValueOrDefault() + size;
        }
        return total;
    }
    public int SelectedCount => _selected.Count;
    public int TotalCount => _links.Count;
    public int Count(PageLinkKind kind) => kind == PageLinkKind.All ? _links.Count : _links.Count(link => Classify(new Uri(link.Url).AbsolutePath) == kind);
    public void Toggle(string url)
    {
        var normalized = LinkRules.Normalize(url);
        if (normalized is null || !_links.Any(link => link.Url == normalized)) return;
        if (!_selected.Add(normalized)) _selected.Remove(normalized);
    }
    public void SetVisibleSelected(bool selected)
    {
        foreach (var link in Visible) if (selected) _selected.Add(link.Url); else _selected.Remove(link.Url);
    }
    private bool Matches(PageLink link)
    {
        var uri = new Uri(link.Url);
        return (Kind == PageLinkKind.All || Classify(uri.AbsolutePath) == Kind) &&
            (string.IsNullOrWhiteSpace(Query) || link.Name.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase));
    }
    private static PageLinkKind Classify(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => PageLinkKind.Archives,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" => PageLinkKind.Audio,
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => PageLinkKind.Video,
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".svg" => PageLinkKind.Images,
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" => PageLinkKind.Documents,
        _ => PageLinkKind.Other,
    };
}
