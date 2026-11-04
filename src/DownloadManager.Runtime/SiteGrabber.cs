using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

internal sealed record GrabberFile(Uri Url, string RelativePath);

internal sealed class SiteGrabber
{
    private static readonly Regex LinkPattern = new(
        @"(?<attr>href|src)\s*=\s*(?<quote>[""'])(?<url>.*?)\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly string _destination;
    private readonly Action<string>? _log;

    public SiteGrabber(string destination, Action<string>? log)
    {
        _destination = destination;
        _log = log;
    }

    public async Task<SiteGrabberResult> RunAsync(
        SiteGrabberRequest request,
        Func<GrabberFile, Task<AddJobResponse>> enqueue,
        CancellationToken cancellationToken,
        Action<int, int, int>? onProgress = null)
    {
        var result = new SiteGrabberResult();
        if (!Uri.TryCreate(request.StartUrl, UriKind.Absolute, out var start) ||
            start.Scheme is not ("http" or "https"))
        {
            result.Error = "Enter an absolute http or https start URL.";
            return result;
        }

        var startAuthority = start.GetLeftPart(UriPartial.Authority);
        var maxDepth = Math.Clamp(request.MaxDepth, 0, 5);
        var maxPages = Math.Clamp(request.MaxPages, 1, 500);
        var maxFiles = Math.Max(100, maxPages * 20);
        var extensions = request.Extensions
            .Select(extension => extension.Trim().TrimStart('.').ToLowerInvariant())
            .Where(extension => extension.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<GrabberPage>();
        var files = new List<GrabberFile>();
        var fileUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(Uri Url, int Depth)>();
        queue.Enqueue((start, 0));
        visited.Add(Normalize(start));

        using var http = CreateClient();
        var robotsCache = new Dictionary<string, RobotsRules>(StringComparer.OrdinalIgnoreCase);
        var robotsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<bool> AllowedAsync(Uri target)
        {
            if (!request.RespectRobotsTxt)
            {
                return true;
            }

            var authority = target.GetLeftPart(UriPartial.Authority);
            if (!robotsCache.TryGetValue(authority, out var rules))
            {
                rules = await FetchRobotsAsync(http, authority, cancellationToken).ConfigureAwait(false);
                robotsCache[authority] = rules;
            }

            if (rules.IsAllowed(target.PathAndQuery))
            {
                return true;
            }

            if (robotsSeen.Add(Normalize(target)))
            {
                result.RobotsBlocked++;
                _log?.Invoke($"Grabber skipped {target} (disallowed by robots.txt).");
            }

            return false;
        }

        if (!await AllowedAsync(start).ConfigureAwait(false))
        {
            result.Error = "The start URL is disallowed by the site's robots.txt.";
            return result;
        }

        while (queue.Count > 0 && pages.Count < maxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (url, depth) = queue.Dequeue();
            if (!InScope(url, startAuthority, request) || !await AllowedAsync(url).ConfigureAwait(false))
            {
                continue;
            }

            if (!IsPageCandidate(url))
            {
                AddFile(url, request, extensions, files, fileUrls, localOwners, result);
                if (files.Count >= maxFiles)
                {
                    break;
                }

                continue;
            }

            try
            {
                using var response = await http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AddError(result, $"{url}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var bytes = await ReadLimitedAsync(response.Content, 5 * 1024 * 1024, cancellationToken)
                    .ConfigureAwait(false);
                if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    AddFile(url, request, extensions, files, fileUrls, localOwners, result);
                    continue;
                }

                var html = Encoding.UTF8.GetString(bytes);
                var page = new GrabberPage(
                    url,
                    html,
                    AssignPath(url, true, localOwners));
                pages.Add(page);
                onProgress?.Invoke(pages.Count, result.FilesQueued, result.FilesSkipped);
                _log?.Invoke($"Grabber crawled {url} (depth {depth}, {html.Length} chars).");

                foreach (var link in ExtractLinks(html, url))
                {
                    if (!InScope(link, startAuthority, request) || !await AllowedAsync(link).ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (IsPageCandidate(link))
                    {
                        if (depth < maxDepth && visited.Add(Normalize(link)))
                        {
                            queue.Enqueue((link, depth + 1));
                        }
                    }
                    else
                    {
                        AddFile(link, request, extensions, files, fileUrls, localOwners, result);
                        if (files.Count >= maxFiles)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AddError(result, $"{url}: {ex.Message}");
            }
        }

        result.PagesCrawled = pages.Count;
        if (pages.Count == 0 && files.Count == 0)
        {
            result.Ok = false;
            result.Error = result.Errors.FirstOrDefault() ??
                           "The start URL could not be crawled.";
            return result;
        }

        var pageMap = pages.ToDictionary(page => Normalize(page.Url), page => page.RelativePath, StringComparer.OrdinalIgnoreCase);

        if (request.SavePages)
        {
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var html = request.RewriteLinks
                    ? RewriteLinks(page, pageMap, files)
                    : page.Html;
                var fullPath = Path.Combine(_destination, ToLocal(page.RelativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, html, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(_destination, ToLocal(file.RelativePath));
            if (File.Exists(fullPath))
            {
                result.FilesSkipped++;
                onProgress?.Invoke(pages.Count, result.FilesQueued, result.FilesSkipped);
                continue;
            }

            try
            {
                var add = await enqueue(file).ConfigureAwait(false);
                if (add.Accepted)
                {
                    result.FilesQueued++;
                }
                else
                {
                    AddError(result, $"{file.Url}: {add.Error}");
                }
            }
            catch (Exception ex)
            {
                AddError(result, $"{file.Url}: {ex.Message}");
            }

            onProgress?.Invoke(pages.Count, result.FilesQueued, result.FilesSkipped);
        }

        result.Ok = true;
        return result;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(RobotsUserAgent);
        return client;
    }

    private const string RobotsUserAgent = "DownloadManagerGrabber/0.1";

    /// <summary>
    /// RFC 9309: 4xx means no restrictions; 5xx or an unreachable host means full disallow.
    /// </summary>
    private async Task<RobotsRules> FetchRobotsAsync(HttpClient http, string authority, CancellationToken cancellationToken)
    {
        var robotsUrl = new Uri(authority + "/robots.txt");
        try
        {
            using var response = await http.GetAsync(robotsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 400 and < 500)
            {
                return RobotsRules.AllowAll;
            }

            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"robots.txt at {robotsUrl} returned HTTP {status}; treating the host as disallowed.");
                return RobotsRules.Parse("User-agent: *\nDisallow: /\n", RobotsUserAgent);
            }

            var bytes = await ReadLimitedAsync(response.Content, 500 * 1024, cancellationToken).ConfigureAwait(false);
            var rules = RobotsRules.Parse(Encoding.UTF8.GetString(bytes), RobotsUserAgent);
            _log?.Invoke($"Loaded robots.txt for {authority} ({rules.RuleCount} rule(s)).");
            return rules;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            _log?.Invoke($"robots.txt at {robotsUrl} could not be read ({ex.Message}); treating the host as disallowed.");
            return RobotsRules.Parse("User-agent: *\nDisallow: /\n", RobotsUserAgent);
        }
    }

    private static bool InScope(Uri url, string startAuthority, SiteGrabberRequest request)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (request.SameHostOnly &&
            !string.Equals(url.GetLeftPart(UriPartial.Authority), startAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = url.AbsoluteUri;
        if (request.ExcludePatterns.Any(pattern => GlobMatch(value, pattern)))
        {
            return false;
        }

        if (IsPageCandidate(url))
        {
            return true;
        }

        return request.IncludePatterns.Count == 0 ||
               request.IncludePatterns.Any(pattern => GlobMatch(value, pattern));
    }

    private static bool IsPageCandidate(Uri url)
    {
        var path = url.AbsolutePath;
        if (path.EndsWith('/'))
        {
            return true;
        }

        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension is "" or "html" or "htm" or "xhtml" or "php" or "asp" or "aspx" or "jsp";
    }

    private void AddFile(
        Uri url,
        SiteGrabberRequest request,
        HashSet<string> extensions,
        List<GrabberFile> files,
        HashSet<string> fileUrls,
        Dictionary<string, string> localOwners,
        SiteGrabberResult result)
    {
        var extension = Path.GetExtension(url.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (extensions.Count > 0 && !extensions.Contains(extension))
        {
            result.FilesSkipped++;
            return;
        }

        var key = Normalize(url);
        if (!fileUrls.Add(key))
        {
            return;
        }

        files.Add(new GrabberFile(url, AssignPath(url, false, localOwners)));
    }

    private static string AssignPath(Uri url, bool page, Dictionary<string, string> owners)
    {
        var path = Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/');
        if (string.IsNullOrWhiteSpace(path) || url.AbsolutePath.EndsWith('/'))
        {
            path += "index.html";
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not ("." or ".."))
            .Select(SanitizeSegment)
            .ToList();
        if (segments.Count == 0)
        {
            segments.Add("index.html");
        }

        var fileName = segments[^1];
        if (page)
        {
            var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (extension is not ("html" or "htm"))
            {
                fileName = Path.ChangeExtension(fileName, ".html");
            }
        }

        segments[^1] = fileName;
        var relative = string.Join('/', segments);
        var candidate = relative;
        for (var i = 1; owners.TryGetValue(candidate, out var owner) &&
                        !string.Equals(owner, url.AbsoluteUri, StringComparison.OrdinalIgnoreCase); i++)
        {
            var extension = Path.GetExtension(relative);
            var stem = Path.GetFileNameWithoutExtension(relative);
            candidate = $"{Path.GetDirectoryName(relative)?.Replace('\\', '/')}/{stem}-{i}{extension}"
                .TrimStart('/');
        }

        owners[candidate] = url.AbsoluteUri;
        return candidate;
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) || ch == '?' || ch == '#' ? '_' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "_" : result;
    }

    private static IEnumerable<Uri> ExtractLinks(string html, Uri baseUri)
    {
        foreach (Match match in LinkPattern.Matches(html))
        {
            var value = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (value.Length == 0 || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith('#'))
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, value, out var resolved) &&
                resolved.Scheme is "http" or "https")
            {
                yield return resolved;
            }
        }
    }

    private static string RewriteLinks(GrabberPage page, Dictionary<string, string> pages, List<GrabberFile> files)
    {
        var map = new Dictionary<string, string>(pages, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            map[Normalize(file.Url)] = file.RelativePath;
        }

        var pageDirectory = Path.GetDirectoryName(page.RelativePath)?.Replace('\\', '/') ?? string.Empty;
        return LinkPattern.Replace(page.Html, match =>
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (!Uri.TryCreate(page.Url, raw, out var resolved))
            {
                return match.Value;
            }

            var key = Normalize(resolved);
            if (!map.TryGetValue(key, out var target))
            {
                return match.Value;
            }

            var relative = Path.GetRelativePath(
                    string.IsNullOrEmpty(pageDirectory) ? "." : pageDirectory,
                    target.Replace('/', Path.DirectorySeparatorChar))
                .Replace('\\', '/');
            if (relative.Length == 0)
            {
                relative = ".";
            }

            return $"{match.Groups["attr"].Value}={match.Groups["quote"].Value}{relative}{match.Groups["quote"].Value}";
        });
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int limit,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException("The page is too large to crawl.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81_920];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("The page is too large to crawl.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static string Normalize(Uri url) => url.GetLeftPart(UriPartial.Query);

    private static string ToLocal(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static bool GlobMatch(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static void AddError(SiteGrabberResult result, string error)
    {
        if (result.Errors.Count < 20)
        {
            result.Errors.Add(error);
        }
    }

    private sealed record GrabberPage(Uri Url, string Html, string RelativePath);
}
