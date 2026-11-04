using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TestServer;

public sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte[]> _content = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _hlsSegmentRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _dashSegmentRequests = new(StringComparer.Ordinal);
    private readonly object _authLastSync = new();
    private int _activeRangeRequests;
    private int _peakRangeRequests;
    private long _authAuthorized;
    private long _authDenied;
    private long _expiringExpired;
    private long _expiringServed;
    private long _expiringRangeRequests;
    private long _siteAuthorized;
    private long _siteDenied;
    private string _siteUser = "siteuser";
    private string _sitePass = "sitepass";
    private bool _lastAuthCookiePresent;
    private string _lastAuthReferer = string.Empty;
    private string _lastAuthUserAgent = string.Empty;
    private string _lastAuthRange = string.Empty;
    private string? _lastAuthFailure;

    private const string AuthCookie = "dmsession=valid-auth-token";

    public int PeakConcurrentRangeRequests => Volatile.Read(ref _peakRangeRequests);
    public long AuthAuthorized => Interlocked.Read(ref _authAuthorized);
    public long AuthDenied => Interlocked.Read(ref _authDenied);
    public long ExpiringExpired => Interlocked.Read(ref _expiringExpired);
    public long ExpiringServed => Interlocked.Read(ref _expiringServed);
    public long ExpiringRangeRequests => Interlocked.Read(ref _expiringRangeRequests);
    public long SiteAuthAuthorized => Interlocked.Read(ref _siteAuthorized);
    public long SiteAuthDenied => Interlocked.Read(ref _siteDenied);

    public void SetSiteCredentials(string username, string password)
    {
        _siteUser = username;
        _sitePass = password;
    }

    public void ResetSiteAuthStats()
    {
        Interlocked.Exchange(ref _siteAuthorized, 0);
        Interlocked.Exchange(ref _siteDenied, 0);
    }

    public void ResetConcurrency()
    {
        Interlocked.Exchange(ref _activeRangeRequests, 0);
        Interlocked.Exchange(ref _peakRangeRequests, 0);
    }

    public int HlsSegmentRequests(string name) => _hlsSegmentRequests.GetValueOrDefault(name);

    public int DashSegmentRequests(string name) => _dashSegmentRequests.GetValueOrDefault(name);

    public void ResetHlsStats()
    {
        _hlsSegmentRequests.Clear();
        _dashSegmentRequests.Clear();
    }

    public void ResetExpiringStats()
    {
        Interlocked.Exchange(ref _expiringExpired, 0);
        Interlocked.Exchange(ref _expiringServed, 0);
        Interlocked.Exchange(ref _expiringRangeRequests, 0);
    }

    public void ResetAuthStats()
    {
        Interlocked.Exchange(ref _authAuthorized, 0);
        Interlocked.Exchange(ref _authDenied, 0);
        lock (_authLastSync)
        {
            _lastAuthCookiePresent = false;
            _lastAuthReferer = string.Empty;
            _lastAuthUserAgent = string.Empty;
            _lastAuthRange = string.Empty;
            _lastAuthFailure = null;
        }
    }

    private TestServerHost(WebApplication app)
    {
        _app = app;
    }

    public string BaseAddress { get; private set; } = string.Empty;

    public static async Task<TestServerHost> StartAsync(
        int port = 0,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        var app = builder.Build();
        var host = new TestServerHost(app);
        host.MapEndpoints();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        host.BaseAddress = app.Urls.First().TrimEnd('/');
        return host;
    }

    public byte[] ContentFor(string name, int size, int seed = 1)
    {
        var key = $"{name}:{size}:{seed}";
        return _content.GetOrAdd(key, _ => TestContent.Generate(seed, size));
    }

    public void SetVersion(string name, int version)
        => _versions[name] = version;

    public int GetVersion(string name)
        => _versions.GetValueOrDefault(name, 1);

    private void MapEndpoints()
    {
        _app.MapGet("/health", () => Results.Text("ok"));

        _app.MapGet("/file/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            var isRange = !string.IsNullOrWhiteSpace(context.Request.Headers.Range);
            if (isRange)
            {
                BeginRangeRequest();
            }

            try
            {
                await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                    throttleBytesPerSecond: QueryInt(context, "bps", 0),
                    unknownLength: false).ConfigureAwait(false);
            }
            finally
            {
                if (isRange)
                {
                    Interlocked.Decrement(ref _activeRangeRequests);
                }
            }
        });

        _app.MapGet("/norange/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: false,
                throttleBytesPerSecond: QueryInt(context, "bps", 0),
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/badrange/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                badRange: true,
                forcePartial: true,
                throttleBytesPerSecond: 0,
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/unknown/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: false,
                throttleBytesPerSecond: QueryInt(context, "bps", 0),
                unknownLength: true).ConfigureAwait(false);
        });

        _app.MapGet("/slow/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 512 * 1024);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                throttleBytesPerSecond: QueryInt(context, "bps", 64 * 1024),
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/truncate/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            var cut = QueryInt(context, "cut", size / 2);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                throttleBytesPerSecond: 0,
                unknownLength: false,
                cutAt: Math.Clamp(cut, 1, size))
                .ConfigureAwait(false);
        });

        _app.MapGet("/changed/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 1024 * 1024);
            var version = _versions.GetValueOrDefault(name, 1);
            var seed = QueryInt(context, "seed", version);
            await ServeAsync(context, ContentFor(name, size, seed), $"{name}-v{version}",
                allowRanges: true,
                throttleBytesPerSecond: QueryInt(context, "bps", 0),
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/expiring/{name}", async (HttpContext context, string name) =>
        {
            var token = QueryInt(context, "token", 1);
            var expectedToken = _versions.GetValueOrDefault(name, 1);
            if (token != expectedToken)
            {
                Interlocked.Increment(ref _expiringExpired);
                context.Response.StatusCode = 403;
                context.Response.Headers["X-Url-Expired"] = "true";
                await context.Response.WriteAsync("download URL expired").ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref _expiringServed);
            if (!string.IsNullOrWhiteSpace(context.Request.Headers.Range))
            {
                Interlocked.Increment(ref _expiringRangeRequests);
            }

            var size = QueryInt(context, "size", 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                throttleBytesPerSecond: QueryInt(context, "bps", 0),
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/expiring/rotate/{name}", (string name, int? token) =>
        {
            var next = token ?? GetVersion(name) + 1;
            SetVersion(name, next);
            return Results.Json(new { name, token = next });
        });

        _app.MapGet("/expiring/stats", () => Results.Json(new
        {
            expired = ExpiringExpired,
            served = ExpiringServed,
            rangeRequests = ExpiringRangeRequests,
        }));

        _app.MapGet("/flaky-range/{name}", async (HttpContext context, string name) =>
        {
            var count = _requestCounts.AddOrUpdate(name, 1, (_, existing) => existing + 1);
            var size = QueryInt(context, "size", 1024 * 1024);
            await ServeAsync(context, ContentFor(name, size, 1), name,
                allowRanges: count > 1, throttleBytesPerSecond: 0, unknownLength: false,
                forceFullForRange: count == 1).ConfigureAwait(false);
        });

        _app.MapGet("/redirect", async (HttpContext context) =>
        {
            var target = context.Request.Query["to"].ToString();
            var count = QueryInt(context, "count", 1);
            if (string.IsNullOrWhiteSpace(target))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("missing to").ConfigureAwait(false);
                return;
            }

            if (count > 1)
            {
                context.Response.Redirect($"/redirect?count={count - 1}&to={Uri.EscapeDataString(target)}");
            }
            else
            {
                context.Response.Redirect(target);
            }
        });

        _app.MapGet("/redirect-file/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 1024 * 1024);
            var count = QueryInt(context, "count", 1);
            var target = $"/file/{name}?size={size}";
            if (count > 1)
            {
                context.Response.Redirect($"/redirect-file/{name}?size={size}&count={count - 1}");
            }
            else
            {
                context.Response.Redirect(target);
            }
        });

        _app.MapGet("/auth/basic", async (HttpContext context) =>
        {
            var user = context.Request.Query["user"].ToString();
            var pass = context.Request.Query["pass"].ToString();
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            if (context.Request.Headers.Authorization != expected)
            {
                context.Response.StatusCode = 401;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"test\"";
                await context.Response.WriteAsync("unauthorized").ConfigureAwait(false);
                return;
            }

            await context.Response.WriteAsync("authenticated").ConfigureAwait(false);
        });

        _app.MapGet("/auth/login", async (HttpContext context) =>
        {
            var size = QueryInt(context, "size", 2 * 1024 * 1024);
            var seed = QueryInt(context, "seed", 123);
            context.Response.Cookies.Append("dmsession", "valid-auth-token", new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
            });
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<html><body style=\"font:16px sans-serif;padding:24px\">" +
                "<h3>Authenticated download test</h3>" +
                $"<a id=\"dl\" href=\"/auth/file/secure.bin?size={size}&amp;seed={seed}\">Download secure.bin</a>" +
                "</body></html>").ConfigureAwait(false);
        });

        _app.MapGet("/auth/file/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 2 * 1024 * 1024);
            var seed = QueryInt(context, "seed", 123);
            var cookie = context.Request.Headers.Cookie.ToString();
            var referer = context.Request.Headers.Referer.ToString();
            var userAgent = context.Request.Headers.UserAgent.ToString();
            var range = context.Request.Headers.Range.ToString();

            var cookieOk = cookie
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(part => part.Equals(AuthCookie, StringComparison.Ordinal));
            var expectedReferrer = $"{context.Request.Scheme}://{context.Request.Host}/auth/login";
            var refererOk = referer.StartsWith(expectedReferrer, StringComparison.OrdinalIgnoreCase);
            var userAgentOk = userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase);

            RecordAuth(cookieOk, referer, userAgent, range, cookieOk && refererOk && userAgentOk
                ? null
                : !cookieOk ? "missing or wrong dmsession cookie"
                : !refererOk ? "missing or wrong Referer"
                : "missing Chrome User-Agent");

            if (!cookieOk || !refererOk || !userAgentOk)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("authenticated handoff rejected").ConfigureAwait(false);
                return;
            }

            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                throttleBytesPerSecond: QueryInt(context, "bps", 0),
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/auth/stats", () => Results.Json(new
        {
            authorized = AuthAuthorized,
            denied = AuthDenied,
            lastCookiePresent = _lastAuthCookiePresent,
            lastReferer = _lastAuthReferer,
            lastUserAgent = _lastAuthUserAgent,
            lastRange = _lastAuthRange,
            lastFailure = _lastAuthFailure,
        }));

        _app.MapGet("/site/secure/{name}", async (HttpContext context, string name) =>
        {
            var expected = "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_siteUser}:{_sitePass}"));
            if (!string.Equals(context.Request.Headers.Authorization.ToString(), expected,
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _siteDenied);
                context.Response.StatusCode = 401;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"site-login\"";
                await context.Response.WriteAsync("site login required").ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref _siteAuthorized);
            var size = QueryInt(context, "size", 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                throttleBytesPerSecond: QueryInt(context, "bps", 0),
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/site/stats", () => Results.Json(new
        {
            authorized = SiteAuthAuthorized,
            denied = SiteAuthDenied,
        }));

        _app.MapGet("/cookie/required", async (HttpContext context) =>
        {
            if (!context.Request.Headers.Cookie.ToString().Contains("session=ok", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("forbidden").ConfigureAwait(false);
                return;
            }

            await context.Response.WriteAsync("cookie ok").ConfigureAwait(false);
        });

        _app.MapGet("/header/required", async (HttpContext context) =>
        {
            if (context.Request.Headers["X-Test-Token"] != "expected")
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("missing header").ConfigureAwait(false);
                return;
            }

            await context.Response.WriteAsync("header ok").ConfigureAwait(false);
        });

        _app.MapGet("/sensitive/echo", (HttpContext context) => Results.Json(new
        {
            cookie = !string.IsNullOrWhiteSpace(context.Request.Headers.Cookie.ToString()),
            authorization = !string.IsNullOrWhiteSpace(context.Request.Headers.Authorization.ToString()),
            referer = !string.IsNullOrWhiteSpace(context.Request.Headers.Referer.ToString()),
            userAgent = context.Request.Headers.UserAgent.ToString(),
        }));

        _app.MapGet("/filename/{name}", async (HttpContext context, string name) =>
        {
            var disposition = context.Request.Query["disposition"].ToString();
            if (!string.IsNullOrWhiteSpace(disposition))
            {
                context.Response.Headers.ContentDisposition = disposition;
            }

            await ServeAsync(context, ContentFor(name, 1024, 1), name, allowRanges: true,
                throttleBytesPerSecond: 0, unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/hls/{file}", async (HttpContext context, string file) =>
        {
            if (!string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "hls", file);
            if (!File.Exists(path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            context.Response.ContentType = file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.apple.mpegurl"
                : "video/mp2t";
            context.Response.ContentLength = bytes.Length;
            if (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                _hlsSegmentRequests.AddOrUpdate(file, 1, (_, existing) => existing + 1);
            }

            var throttle = QueryInt(context, "bps", 0);
            if (throttle <= 0)
            {
                await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
                return;
            }

            var buffer = new byte[16 * 1024];
            var written = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (written < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - written);
                Array.Copy(bytes, written, buffer, 0, count);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted)
                    .ConfigureAwait(false);
                written += count;
                var expected = written / (double)throttle;
                var delay = TimeSpan.FromSeconds(expected) - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
                }
            }
        });

        _app.MapGet("/hls/expected", () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "hls");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            foreach (var name in new[] { "seg0.ts", "seg1.ts", "seg2.ts" })
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, name));
                hash.AppendData(bytes);
                length += bytes.Length;
            }

            return Results.Json(new
            {
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                bytes = length,
            });
        });

        _app.MapGet("/hls/stats", () => Results.Json(new
        {
            segments = _hlsSegmentRequests.ToDictionary(pair => pair.Key, pair => pair.Value),
        }));

        _app.MapGet("/dash/{file}", async (HttpContext context, string file) =>
        {
            if (!string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "dash", file);
            if (!File.Exists(path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            context.Response.ContentType = file.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
                ? "application/dash+xml"
                : "video/mp4";
            context.Response.ContentLength = bytes.Length;
            if (file.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase))
            {
                _dashSegmentRequests.AddOrUpdate(file, 1, (_, existing) => existing + 1);
            }

            var throttle = QueryInt(context, "bps", 0);
            if (throttle <= 0)
            {
                await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
                return;
            }

            var buffer = new byte[16 * 1024];
            var written = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (written < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - written);
                Array.Copy(bytes, written, buffer, 0, count);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted)
                    .ConfigureAwait(false);
                written += count;
                var expected = written / (double)throttle;
                var delay = TimeSpan.FromSeconds(expected) - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
                }
            }
        });

        _app.MapGet("/dash/stats", () => Results.Json(new
        {
            segments = _dashSegmentRequests.ToDictionary(pair => pair.Key, pair => pair.Value),
        }));

        _app.MapGet("/dash/expected", () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "dash");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            var files = new[] { "init-0.m4s", "chunk-0-00001.m4s", "chunk-0-00002.m4s", "chunk-0-00003.m4s" };
            foreach (var name in files)
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, name));
                hash.AppendData(bytes);
                length += bytes.Length;
            }

            return Results.Json(new
            {
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                bytes = length,
            });
        });

        _app.MapGet("/dash-av/{file}", async (HttpContext context, string file) =>
        {
            if (!string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "dash-av", file);
            if (!File.Exists(path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            context.Response.ContentType = file.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
                ? "application/dash+xml"
                : file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? "video/mp4"
                    : "video/iso.segment";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
        });

        _app.MapGet("/dash-av/expected", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "dash-av", "muxed.mp4");
            var bytes = File.ReadAllBytes(path);
            return Results.Json(new
            {
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes = bytes.Length,
            });
        });

        _app.MapGet("/media-page", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<html><body style=\"font:16px sans-serif;padding:24px\">" +
                "<h3>Media detection test</h3>" +
                "<video id=\"video\" controls width=\"320\" " +
                "src=\"/file/media-video.mp4?size=524288&seed=201\"></video>" +
                "<audio id=\"audio\" controls " +
                "src=\"/file/media-audio.mp3?size=262144&seed=202\"></audio>" +
                "<p><a href=\"/hls/demo.m3u8\">HLS stream link</a></p>" +
                "</body></html>").ConfigureAwait(false);
        });

        _app.MapGet("/html", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body>downloadable html</body></html>").ConfigureAwait(false);
        });

        _app.MapGet("/empty", async (HttpContext context) =>
        {
            context.Response.ContentLength = 0;
            await context.Response.Body.FlushAsync().ConfigureAwait(false);
        });

        _app.MapGet("/error/{code:int}", async (HttpContext context, int code) =>
        {
            context.Response.StatusCode = code;
            await context.Response.WriteAsync($"error {code}").ConfigureAwait(false);
        });

        _app.MapPost("/admin/reset", () =>
        {
            _content.Clear();
            _versions.Clear();
            _requestCounts.Clear();
            _hlsSegmentRequests.Clear();
            _dashSegmentRequests.Clear();
            ResetAuthStats();
            ResetExpiringStats();
            ResetSiteAuthStats();
            return Results.Ok();
        });
    }

    private void RecordAuth(bool cookiePresent, string referer, string userAgent, string range, string? failure)
    {
        if (failure is null)
        {
            Interlocked.Increment(ref _authAuthorized);
        }
        else
        {
            Interlocked.Increment(ref _authDenied);
        }

        lock (_authLastSync)
        {
            _lastAuthCookiePresent = cookiePresent;
            _lastAuthReferer = Truncate(referer, 512);
            _lastAuthUserAgent = Truncate(userAgent, 512);
            _lastAuthRange = Truncate(range, 128);
            _lastAuthFailure = failure;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private async Task ServeAsync(
        HttpContext context,
        byte[] content,
        string etagName,
        bool allowRanges,
        int throttleBytesPerSecond,
        bool unknownLength,
        bool badRange = false,
        int? cutAt = null,
        bool forceFullForRange = false,
        bool forcePartial = false)
    {
        var etag = $"\"{etagName}-{content.Length}-{Convert.ToHexString(SHA256.HashData(content))[..12]}\"";
        var lastModified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(content.Length % 1000);
        context.Response.Headers.ETag = etag;
        context.Response.Headers.LastModified = lastModified.ToString("R");
        context.Response.Headers.AcceptRanges = allowRanges ? "bytes" : "none";

        var rangeHeader = context.Request.Headers.Range.ToString();
        var ifRange = context.Request.Headers.IfRange.ToString();
        if (!string.IsNullOrWhiteSpace(ifRange) && ifRange != etag)
        {
            forceFullForRange = true;
        }

        var hasRange = allowRanges && !forceFullForRange && (forcePartial || !string.IsNullOrWhiteSpace(rangeHeader));
        long start = 0;
        long end = content.Length - 1;
        if (hasRange && string.IsNullOrWhiteSpace(rangeHeader))
        {
            // Deliberately malformed endpoint: send a 206 even without a Range request.
            start = 0;
            end = content.Length - 1;
        }
        else if (hasRange && TryParseRange(rangeHeader, content.Length, out var rangeStart, out var rangeEnd))
        {
            start = rangeStart;
            end = rangeEnd;
        }
        else if (hasRange)
        {
            context.Response.StatusCode = 416;
            context.Response.Headers.ContentRange = $"bytes */{content.Length}";
            await context.Response.WriteAsync("range not satisfiable").ConfigureAwait(false);
            return;
        }

        var isPartial = hasRange;
        context.Response.StatusCode = isPartial ? 206 : 200;
        context.Response.ContentType = "application/octet-stream";
        if (isPartial)
        {
            var reportedStart = badRange ? Math.Min(start + 1, content.Length - 1) : start;
            context.Response.Headers.ContentRange = $"bytes {reportedStart}-{end}/{content.Length}";
        }

        if (!unknownLength)
        {
            context.Response.ContentLength = end - start + 1;
        }

        var totalToWrite = end - start + 1;
        if (cutAt is not null)
        {
            totalToWrite = Math.Min(totalToWrite, cutAt.Value);
        }

        var buffer = new byte[64 * 1024];
        var written = 0L;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (written < totalToWrite)
            {
                var count = (int)Math.Min(buffer.Length, totalToWrite - written);
                Array.Copy(content, start + written, buffer, 0, count);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted)
                    .ConfigureAwait(false);
                written += count;

                if (throttleBytesPerSecond > 0)
                {
                    var expected = written / (double)throttleBytesPerSecond;
                    var delay = TimeSpan.FromSeconds(expected) - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
                    }
                }
            }

            if (cutAt is not null)
            {
                await context.Response.Body.FlushAsync().ConfigureAwait(false);
                context.Abort();
            }
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // client disconnected
        }
    }

    private static bool TryParseRange(string header, int length, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = header[6..].Split(',')[0].Trim();
        var dash = value.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startText = value[..dash];
        var endText = value[(dash + 1)..];
        if (string.IsNullOrWhiteSpace(startText))
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffix);
            end = length - 1;
            return true;
        }

        if (!long.TryParse(startText, out start) || start < 0 || start >= length)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(endText))
        {
            end = length - 1;
            return true;
        }

        if (!long.TryParse(endText, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, length - 1);
        return true;
    }

    private static int QueryInt(HttpContext context, string name, int fallback)
        => int.TryParse(context.Request.Query[name], out var value) ? value : fallback;

    private void BeginRangeRequest()
    {
        var active = Interlocked.Increment(ref _activeRangeRequests);
        var currentPeak = Volatile.Read(ref _peakRangeRequests);
        while (active > currentPeak)
        {
            var observed = Interlocked.CompareExchange(ref _peakRangeRequests, active, currentPeak);
            if (observed == currentPeak)
            {
                break;
            }

            currentPeak = observed;
        }
    }

    private void EndRangeRequest() => Interlocked.Decrement(ref _activeRangeRequests);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

public static class TestContent
{
    public static byte[] Generate(int seed, int size)
    {
        var bytes = new byte[size];
        var state = (uint)(seed == 0 ? 1 : seed);
        for (var i = 0; i < bytes.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)(state & 0xFF);
        }

        return bytes;
    }
}
