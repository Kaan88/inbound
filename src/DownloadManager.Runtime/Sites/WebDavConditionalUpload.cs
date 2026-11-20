using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>Conditional publication for standard WebDAV servers. Never retries or follows redirects.</summary>
internal static class WebDavConditionalUpload
{
    private const string StagePrefix = ".inbound-upload-";
    internal sealed class PublicationException(string message) : IOException(message);

    internal static bool TryTarget(SiteRecord site, string folder, string name, out Uri? target)
    {
        target = null;
        if (site.Kind != SiteKindCatalog.WebDav || site.Vendor is not (null or "" or "other")
            || site.TrustedCertificates.Count != 0
            || SiteKindCatalog.AddressProblem(site.Url, "WebDAV address") is not null
            || !Uri.TryCreate(site.Url, UriKind.Absolute, out var root)
            || (root.Scheme != Uri.UriSchemeHttps &&
                !(root.Scheme == Uri.UriSchemeHttp && root.IsLoopback && site.User.Length == 0 && site.SealedPassword is null))
            || SitePaths.Normalize(folder) != folder || SitePaths.NameProblem(name) is not null
            || name.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = string.Join('/', folder.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Append(name).Select(Uri.EscapeDataString));
        target = new Uri(root.AbsoluteUri.TrimEnd('/') + "/" + relative);
        return true;
    }

    internal static async Task SendAsync(SiteRecord site, Uri target, Stream source, long length, CancellationToken cancellationToken)
    {
        // Use the same managed transport on Android and desktop; platform URLConnection
        // handlers need not support WebDAV's MOVE method.
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        using var client = new HttpClient(handler);
        AuthenticationHeaderValue? auth = null;
        if (site.User.Length > 0 || site.SealedPassword is not null)
        {
            string password = site.SealedPassword is { } sealedPassword
                ? SecretProtector.UnprotectSiteSecret(sealedPassword) ?? throw new IOException("The saved WebDAV password could not be opened.")
                : "";
            auth = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{site.User}:{password}")));
        }

        // MOVE's Overwrite:F is the WebDAV no-replace operation. A preflight stat, or conditional
        // PUT to the final name on servers that ignore HTTP preconditions for PUT, is not enough.
        var stage = new Uri(target, StagePrefix + Guid.NewGuid().ToString("N"));
        using var put = new HttpRequestMessage(HttpMethod.Put, stage) { Content = new StreamContent(source) };
        put.Headers.Authorization = auth;
        put.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        put.Content.Headers.ContentLength = length;
        using var prepared = await client.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (prepared.StatusCode != HttpStatusCode.Created)
            throw new IOException("The WebDAV server did not confirm the staging file.");
        bool published = false;
        try
        {
            using var move = new HttpRequestMessage(new HttpMethod("MOVE"), stage);
            move.Headers.Authorization = auth;
            move.Headers.Add("Destination", target.AbsoluteUri);
            move.Headers.Add("Overwrite", "F");
            using var response = await client.SendAsync(move, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new PublicationException("A file already exists at the destination. It was not replaced.");
            if (response.StatusCode != HttpStatusCode.Created)
                throw new IOException("The WebDAV server did not confirm that it created a new file.");
            published = true;
        }
        finally
        {
            if (!published)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    using var delete = new HttpRequestMessage(HttpMethod.Delete, stage);
                    delete.Headers.Authorization = auth;
                    using var ignored = await client.SendAsync(delete, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
            }
        }
    }
}
