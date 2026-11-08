using DownloadManager.Contracts;
using Microsoft.AspNetCore.Http;

namespace DownloadManager.Server;

/// <summary>
/// The browser extension calling the server (D2 pairing; the extension side is stream C's transport). Its requests
/// carry its own origin, so the <see cref="OriginCheck"/> lets them through only on the two paths where it brings a
/// credential of its own, and this answers the CORS questions for them. Credentials (cookies) are never allowed
/// across: the extension proves itself with a bearer token, never with the user's sign-in.
/// <list type="bullet">
/// <item><c>chrome-extension://</c> with one of our own ids (the store build and the unpacked build).</item>
/// <item><c>moz-extension://</c> with any id: Firefox gives each install a random one, so it can't be listed. It is
/// let in on <c>/api/ipc</c> only with a bearer token, which the sign-in gate then checks, and on <c>/api/pair</c>,
/// where the one-time code is the credential and wrong codes make the address wait.</item>
/// </list>
/// </summary>
public static class ExtensionCors
{
    private static bool IsExtensionPath(PathString path) =>
        path.Equals("/api/ipc", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase);

    public static bool IsExtensionOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        if (BrowserBridge.IsTrustedOrigin(origin)) return true;
        return origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) &&
               Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.AbsolutePath is "" or "/";
    }

    /// <summary>Whether the origin check should let this extension request through to the sign-in gate.</summary>
    public static bool Admits(HttpRequest request)
    {
        if (!IsExtensionPath(request.Path) || !IsExtensionOrigin(request.Headers.Origin.ToString())) return false;
        if (HttpMethods.IsOptions(request.Method)) return true;
        if (!HttpMethods.IsPost(request.Method)) return false;
        return request.Path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase) ||
               request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    public static Func<HttpContext, Func<Task>, Task> Middleware => async (context, next) =>
    {
        var request = context.Request;
        var origin = request.Headers.Origin.ToString();
        if (!IsExtensionPath(request.Path) || !IsExtensionOrigin(origin))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var headers = context.Response.Headers;
        headers.AccessControlAllowOrigin = origin;
        headers.Vary = "Origin";
        if (HttpMethods.IsOptions(request.Method))
        {
            headers.AccessControlAllowMethods = "POST";
            headers.AccessControlAllowHeaders = "Authorization, Content-Type";
            headers.AccessControlMaxAge = "600";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await next().ConfigureAwait(false);
    };
}

/// <summary>
/// Headers on every answer: the pages load scripts and styles from this server only and can't be framed, nothing is
/// sniffed, no address leaks in a referrer, and no API answer is cached.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static Func<HttpContext, Func<Task>, Task> Middleware => (context, next) =>
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/health"))
            headers.CacheControl = "no-store";
        return next();
    };
}
