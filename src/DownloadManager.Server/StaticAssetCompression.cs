using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace DownloadManager.Server;

/// <summary>
/// PERF.20: the web UI's built files under <c>/assets/</c> carry a hash of their contents in their names, so a browser may
/// keep them for good, and the publish step leaves a Brotli (<c>.br</c>) and a gzip (<c>.gz</c>) copy of each beside it.
/// This sends the compressed copy the browser accepts and marks the file immutable (privately: the files are behind the sign-in). Only <c>/assets/</c>: those files
/// hold no secrets, so compressing them gives no side channel (the API and the WebSocket stay uncompressed). Without a
/// compressed copy (a build that made none) the plain file is sent as before.
/// </summary>
internal static class StaticAssetCompression
{
    private const string Prefix = "/assets/";

    /// <summary>Runs before <c>UseStaticFiles</c>, which then serves the path this leaves.</summary>
    public static void UseCompressedAssets(this IApplicationBuilder app, IFileProvider files)
    {
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var path = request.Path.Value;
            if (path is not null && path.StartsWith(Prefix, StringComparison.Ordinal) && IsCompressedCopy(path))
            {
                // A compressed copy is only ever sent in place of its file, with its encoding said; asked for by its own
                // name it is not there, as before.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
                path is not null && path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                var response = context.Response;
                // private: the files are behind the sign-in, so a shared cache on the way must not keep them. Only a file
                // that was found is kept for good (audit 2026-11-15): a miss while an update swaps the files must not stay
                // in the browser for a year.
                response.OnStarting(() =>
                {
                    if (response.StatusCode == StatusCodes.Status200OK)
                        response.Headers.CacheControl = "private, max-age=31536000, immutable";
                    return Task.CompletedTask;
                });
                response.Headers.Vary = HeaderNames.AcceptEncoding;
                var chosen = Choose(request.Headers.AcceptEncoding.ToString(), path, files);
                if (chosen is { } pick)
                {
                    request.Path = path + pick.Extension;
                    response.Headers.ContentEncoding = pick.Encoding;
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool IsCompressedCopy(string path)
        => path.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>The compressed copy to send: Brotli before gzip, each only when the browser takes it and the copy exists.</summary>
    internal static (string Encoding, string Extension)? Choose(string acceptEncoding, string path, IFileProvider files)
    {
        foreach (var (encoding, extension) in new[] { ("br", ".br"), ("gzip", ".gz") })
        {
            if (Accepts(acceptEncoding, encoding) && files.GetFileInfo(path + extension) is { Exists: true, IsDirectory: false })
                return (encoding, extension);
        }

        return null;
    }

    /// <summary>Whether an Accept-Encoding header takes <paramref name="encoding"/>: named (or <c>*</c>) and not with q=0.</summary>
    internal static bool Accepts(string acceptEncoding, string encoding)
    {
        foreach (var part in acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(';', StringSplitOptions.TrimEntries);
            if (!pieces[0].Equals(encoding, StringComparison.OrdinalIgnoreCase) && pieces[0] != "*") continue;
            var refused = pieces.Skip(1).Any(p => p.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(p[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q) && q <= 0);
            if (!refused) return true;
        }

        return false;
    }

    /// <summary>The content type of a compressed copy under /assets/ is that of the file it was made from (x.js.br is JavaScript).</summary>
    internal sealed class ContentTypes : IContentTypeProvider
    {
        private readonly FileExtensionContentTypeProvider _inner = new();

        public bool TryGetContentType(string subpath, out string contentType)
        {
            // Only under /assets/, where the middleware above put the path; anywhere else a .br or .gz stays unknown.
            var original = subpath.StartsWith(Prefix, StringComparison.Ordinal) && IsCompressedCopy(subpath) ? subpath[..^3] : subpath;
            return _inner.TryGetContentType(original, out contentType!);
        }
    }
}
