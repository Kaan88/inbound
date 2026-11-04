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
    private readonly ConcurrentDictionary<string, int> _dashRangeRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _slowFirstRanges = new();
    private byte[]? _updateManifest;
    private byte[]? _updatePackage;
    private readonly object _authLastSync = new();
    private int _robotsTxtRequests;
    private int _roboDisallowedHits;
    private int _activeRangeRequests;
    private int _peakRangeRequests;
    private int _activeFileRequests;
    private int _peakFileRequests;
    private long _authAuthorized;
    private long _authDenied;
    private long _expiringExpired;
    private long _expiringServed;
    private long _expiringRangeRequests;
    private long _siteAuthorized;
    private long _siteDenied;
    private string _siteUser = "siteuser";
    private string _sitePass = "sitepass";
    private int _hlsRotatingToken = 1;
    private bool _hlsRotateAfterSegment = true;
    private int _dashRotatingToken = 1;
    private bool _dashRotateAfterSegment = true;
    private bool _lastAuthCookiePresent;
    private string _lastAuthReferer = string.Empty;
    private string _lastAuthUserAgent = string.Empty;
    private string _lastAuthRange = string.Empty;
    private string? _lastAuthFailure;

    private const string AuthCookie = "dmsession=valid-auth-token";

    public int PeakConcurrentRangeRequests => Volatile.Read(ref _peakRangeRequests);

    public int PeakConcurrentFileRequests => Volatile.Read(ref _peakFileRequests);
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
        Interlocked.Exchange(ref _activeFileRequests, 0);
        Interlocked.Exchange(ref _peakFileRequests, 0);
    }

    public void ResetFileConcurrency()
    {
        Interlocked.Exchange(ref _activeFileRequests, 0);
        Interlocked.Exchange(ref _peakFileRequests, 0);
    }

    public int HlsSegmentRequests(string name) => _hlsSegmentRequests.GetValueOrDefault(name);

    public int DashSegmentRequests(string name) => _dashSegmentRequests.GetValueOrDefault(name);

    public int DashRangeRequests(string name) => _dashRangeRequests.GetValueOrDefault(name);

    public int FlakyRequests(string name) => _requestCounts.GetValueOrDefault($"flaky-{name}");

    public void ResetHlsStats()
    {
        _hlsSegmentRequests.Clear();
        _dashSegmentRequests.Clear();
        _dashRangeRequests.Clear();
    }

    public IReadOnlyList<string> SlowFirstRanges()
        => _slowFirstRanges.ToArray();

    public void ResetSlowFirstStats()
    {
        while (_slowFirstRanges.TryDequeue(out _))
        {
        }
    }

    public void SetUpdatePayload(byte[] manifest, byte[] package)
    {
        _updateManifest = manifest;
        _updatePackage = package;
    }

    public void ResetRotatingMedia()
    {
        _hlsRotatingToken = 1;
        _hlsRotateAfterSegment = true;
        _dashRotatingToken = 1;
        _dashRotateAfterSegment = true;
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
    public FtpServer Ftp { get; private set; } = null!;

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
        host.Ftp = await FtpServer.StartAsync(cancellationToken).ConfigureAwait(false);
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

        _app.MapGet("/update/manifest", () =>
            _updateManifest is null
                ? Results.NotFound()
                : Results.Bytes(_updateManifest, "application/json"));

        _app.MapGet("/update/package", () =>
            _updatePackage is null
                ? Results.NotFound()
                : Results.Bytes(_updatePackage, "application/zip"));

        _app.MapGet("/ftp/info", () => Results.Json(new
        {
            port = Ftp.Port,
            user = "ftpuser",
        }));

        _app.MapGet("/ftp/stats", () => Results.Json(new
        {
            slowBytesServed = Ftp.BytesServed("slow.bin"),
            slowRetr = Ftp.RetrCount("slow.bin"),
            rest = Ftp.RestCount,
        }));

        _app.MapGet("/file/{name}", async (HttpContext context, string name) =>
        {
            BeginFileRequest();
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
                    EndRangeRequest();
                }

                EndFileRequest();
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

        _app.MapGet("/large/{name}", async (HttpContext context, string name) =>
        {
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var size = Math.Clamp(
                QueryInt(context, "size", 200 * 1024 * 1024), 1, 512 * 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            var throttle = QueryInt(context, "bps", 0);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = size;
            context.Response.Headers.AcceptRanges = "none";
            var buffer = new byte[64 * 1024];
            var state = (uint)(seed == 0 ? 1 : seed);
            var remaining = (long)size;
            var written = 0L;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                TestContent.Fill(ref state, buffer.AsSpan(0, count));
                await context.Response.Body.WriteAsync(
                    buffer.AsMemory(0, count), context.RequestAborted).ConfigureAwait(false);
                written += count;
                remaining -= count;
                if (throttle > 0)
                {
                    var expected = written / (double)throttle;
                    var delay = TimeSpan.FromSeconds(expected) - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
                    }
                }
            }
        });

        _app.MapGet("/large/expected", (int? size, int? seed) =>
        {
            var length = Math.Clamp(size ?? 200 * 1024 * 1024, 1, 512 * 1024 * 1024);
            var state = (uint)((seed ?? 1) == 0 ? 1 : (seed ?? 1));
            var buffer = new byte[64 * 1024];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remaining = (long)length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                TestContent.Fill(ref state, buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }

            return Results.Json(new
            {
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                bytes = length,
            });
        });

        _app.MapGet("/slow/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 512 * 1024);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                throttleBytesPerSecond: QueryInt(context, "bps", 64 * 1024),
                unknownLength: false).ConfigureAwait(false);
        });

        _app.MapGet("/slowfirst/{name}", async (HttpContext context, string name) =>
        {
            var size = QueryInt(context, "size", 4 * 1024 * 1024);
            var seed = QueryInt(context, "seed", 1);
            var range = context.Request.Headers.Range.ToString();
            if (!string.IsNullOrWhiteSpace(range))
            {
                _slowFirstRanges.Enqueue(range);
            }

            await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                throttleBytesPerSecond: QueryInt(context, "bps", 256 * 1024),
                unknownLength: false,
                throttleWhenStartIsZero: true).ConfigureAwait(false);
        });

        _app.MapGet("/slowfirst/stats", () => Results.Json(new
        {
            ranges = _slowFirstRanges.ToArray(),
        }));

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

        _app.MapGet("/hls-rotating/playlist.m3u8", () =>
        {
            var token = _hlsRotatingToken;
            return Results.Text(
                "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n" +
                "#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" +
                $"#EXTINF:2.000000,\nseg0.ts?token={token}\n" +
                $"#EXTINF:2.000000,\nseg1.ts?token={token}\n" +
                $"#EXTINF:2.000000,\nseg2.ts?token={token}\n" +
                "#EXT-X-ENDLIST\n",
                "application/vnd.apple.mpegurl");
        });

        _app.MapGet("/hls-rotating/{file}", async (HttpContext context, string file) =>
        {
            if (!string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal) ||
                !file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (QueryInt(context, "token", 0) != _hlsRotatingToken)
            {
                context.Response.StatusCode = 403;
                context.Response.Headers["X-Url-Expired"] = "true";
                await context.Response.WriteAsync("expired media token").ConfigureAwait(false);
                return;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "hls", file);
            if (!File.Exists(path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            _hlsSegmentRequests.AddOrUpdate($"rotating-{file}", 1, (_, existing) => existing + 1);
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            context.Response.ContentType = "video/mp2t";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
            if (_hlsRotateAfterSegment)
            {
                _hlsRotateAfterSegment = false;
                _hlsRotatingToken++;
            }
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

        _app.MapGet("/hls/av-expected", () =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory, "hls", "av-muxed.mp4"));
            return Results.Json(new
            {
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes = bytes.Length,
            });
        });

        _app.MapGet("/hls/stats", () => Results.Json(new
        {
            segments = _hlsSegmentRequests.ToDictionary(pair => pair.Key, pair => pair.Value),
        }));

        _app.MapGet("/dash-rotating/manifest.mpd", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "dash", "manifest.mpd"));
            var tokenized = source.Replace(
                "chunk-$RepresentationID$-$Number%05d$.m4s",
                $"chunk-$RepresentationID$-$Number%05d$.m4s?token={_dashRotatingToken}",
                StringComparison.Ordinal);
            return Results.Text(tokenized, "application/dash+xml");
        });

        _app.MapGet("/dash-rotating/{file}", async (HttpContext context, string file) =>
        {
            if (!string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var isChunk = file.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) &&
                          file.StartsWith("chunk-", StringComparison.OrdinalIgnoreCase);
            if (isChunk && QueryInt(context, "token", 0) != _dashRotatingToken)
            {
                context.Response.StatusCode = 403;
                context.Response.Headers["X-Url-Expired"] = "true";
                await context.Response.WriteAsync("expired media token").ConfigureAwait(false);
                return;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "dash", file);
            if (!File.Exists(path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (isChunk)
            {
                _dashSegmentRequests.AddOrUpdate($"rotating-{file}", 1, (_, existing) => existing + 1);
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            context.Response.ContentType = isChunk ? "video/iso.segment" : "video/mp4";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
            if (isChunk && _dashRotateAfterSegment)
            {
                _dashRotateAfterSegment = false;
                _dashRotatingToken++;
            }
        });

        _app.MapGet("/dash-range/{file}", async (HttpContext context, string file) =>
        {
            if (!string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "dash-range", file);
            if (!File.Exists(path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(context.Request.Headers.Range))
            {
                _dashRangeRequests.AddOrUpdate(file, 1, (_, existing) => existing + 1);
            }

            await ServeAsync(context, bytes, file, allowRanges: true,
                throttleBytesPerSecond: 0, unknownLength: false).ConfigureAwait(false);
        });

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

        _app.MapGet("/grab/{**path}", async (HttpContext context, string? path) =>
        {
            var relative = (path ?? string.Empty).Replace('\\', '/').Trim('/');
            if (relative.Length == 0)
            {
                relative = "index.html";
            }

            var html = relative switch
            {
                "index.html" =>
                    "<html><body><h3>Grab root</h3>" +
                    "<a href=\"/grab/page1.html\">Page 1</a>" +
                    "<a href=\"/grab/files/a.bin?size=65536&seed=290\">File A</a>" +
                    "<img src=\"/grab/img/pic.png?size=32768&seed=291\">" +
                    "<a href=\"https://example.com/external\">External</a>" +
                    "</body></html>",
                "page1.html" =>
                    "<html><body><h3>Page 1</h3>" +
                    "<a href=\"/grab/page2.html\">Page 2</a>" +
                    "<a href=\"/grab/files/b.bin?size=65536&seed=292\">File B</a>" +
                    "<a href=\"/grab/files/a.bin?size=65536&seed=290\">A again</a>" +
                    "</body></html>",
                "page2.html" =>
                    "<html><body><h3>Page 2</h3>" +
                    "<a href=\"/grab/page3.html\">Page 3</a>" +
                    "<a href=\"/grab/files/c.bin?size=65536&seed=293\">File C</a>" +
                    "</body></html>",
                "page3.html" =>
                    "<html><body><h3>Page 3</h3>" +
                    "<a href=\"/grab/files/d.bin?size=65536&seed=294\">File D</a>" +
                    "</body></html>",
                _ => null,
            };

            if (html is not null)
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(html).ConfigureAwait(false);
                return;
            }

            if (relative.StartsWith("files/", StringComparison.Ordinal) || relative == "img/pic.png")
            {
                var name = Path.GetFileName(relative);
                var size = QueryInt(context, "size", 65_536);
                var seed = QueryInt(context, "seed", 1);
                await ServeAsync(context, ContentFor(name, size, seed), name, allowRanges: true,
                    throttleBytesPerSecond: 0, unknownLength: false).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
        });

        _app.MapGet("/robots.txt", () =>
        {
            Interlocked.Increment(ref _robotsTxtRequests);
            return Results.Text(
                "# Test fixture: only /robo/ is restricted.\n" +
                "User-agent: OtherBot\nDisallow: /\n\n" +
                "User-agent: *\n" +
                "Disallow: /robo/private/\n" +
                "Allow: /robo/private/open.html\n" +
                "Disallow: /robo/files/blocked*\n",
                "text/plain");
        });

        _app.MapGet("/robo/stats", () => Results.Json(new
        {
            robotsTxtRequests = Interlocked.CompareExchange(ref _robotsTxtRequests, 0, 0),
            disallowedHits = Interlocked.CompareExchange(ref _roboDisallowedHits, 0, 0),
        }));

        _app.MapGet("/robo/{**path}", async (HttpContext context, string? path) =>
        {
            var relative = (path ?? string.Empty).Replace('\\', '/').Trim('/');
            if (relative.Length == 0)
            {
                relative = "index.html";
            }

            if ((relative.StartsWith("private/", StringComparison.Ordinal) && relative != "private/open.html") ||
                relative.StartsWith("files/blocked", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _roboDisallowedHits);
            }

            if (relative == "redirect.html")
            {
                // Allowed URL that redirects into a disallowed one.
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = "/robo/private/secret.html";
                return;
            }

            var html = relative switch
            {
                "index.html" =>
                    "<html><body>" +
                    "<a href=\"/robo/redirect.html\">Redirect</a>" +
                    "<a href=\"/robo/private/secret.html\">Secret</a>" +
                    "<a href=\"/robo/private/open.html\">Open</a>" +
                    "<a href=\"/robo/files/ok.bin?size=65536&seed=320\">OK</a>" +
                    "<a href=\"/robo/files/blocked.bin?size=65536&seed=321\">Blocked</a>" +
                    "</body></html>",
                "private/secret.html" =>
                    "<html><body><a href=\"/robo/files/secret.bin?size=65536&seed=322\">S</a></body></html>",
                "private/open.html" =>
                    "<html><body><a href=\"/robo/files/open.bin?size=65536&seed=323\">O</a></body></html>",
                _ => null,
            };

            if (html is not null)
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(html).ConfigureAwait(false);
                return;
            }

            if (relative.StartsWith("files/", StringComparison.Ordinal))
            {
                var name = Path.GetFileName(relative);
                await ServeAsync(context, ContentFor(name, QueryInt(context, "size", 65_536), QueryInt(context, "seed", 1)),
                    name, allowRanges: true, throttleBytesPerSecond: 0, unknownLength: false).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
        });

        _app.MapGet("/live/playlist.m3u8", () => Results.Text(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n" +
            "#EXT-X-MEDIA-SEQUENCE:10\n#EXTINF:2.000000,\nseg0.ts\n" +
            "#EXTINF:2.000000,\nseg1.ts\n",
            "application/vnd.apple.mpegurl"));

        _app.MapGet("/live/manifest.mpd", () => Results.Text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"dynamic\" " +
            "mediaPresentationDuration=\"PT6S\"><Period><AdaptationSet contentType=\"video\">" +
            "<Representation id=\"0\" bandwidth=\"100000\"><SegmentTemplate " +
            "media=\"chunk-$Number$.m4s\" duration=\"2\"/></Representation>" +
            "</AdaptationSet></Period></MPD>",
            "application/dash+xml"));

        _app.MapGet("/drm/hls.m3u8", () => Results.Text(
            "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:2\n" +
            "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\",KEYFORMATVERSIONS=\"1\"\n" +
            "#EXTINF:2.000000,\nseg0.ts\n#EXT-X-ENDLIST\n",
            "application/vnd.apple.mpegurl"));

        _app.MapGet("/drm/manifest.mpd", () => Results.Text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" " +
            "mediaPresentationDuration=\"PT6S\"><Period><AdaptationSet contentType=\"video\">" +
            "<ContentProtection schemeIdUri=\"urn:mpeg:dash:mp4protection:2011\" value=\"cenc\"/>" +
            "<ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/>" +
            "<Representation id=\"0\" bandwidth=\"100000\"><SegmentTemplate " +
            "media=\"chunk-$Number$.m4s\" duration=\"2\"/></Representation>" +
            "</AdaptationSet></Period></MPD>",
            "application/dash+xml"));

        _app.MapGet("/download-all-page", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(
                "<html><body style=\"font:16px sans-serif;padding:24px\">" +
                "<h3>Download All test</h3>" +
                "<a href=\"/file/all-a.bin?size=262144&seed=210\">Report A</a><br>" +
                "<a href=\"/file/all-b.bin?size=131072&seed=211\">Report B</a><br>" +
                "<a href=\"/file/all-a.bin?size=262144&seed=210\">Duplicate A</a><br>" +
                "<a href=\"mailto:test@example.com\">Mail</a><br>" +
                "<a href=\"javascript:void(0)\">JavaScript</a><br>" +
                "<a href=\"#section\">Anchor</a><br>" +
                "<img alt=\"Image asset\" src=\"/file/all-image.png?size=65536&seed=212\"><br>" +
                "<a href=\"/hls/master.m3u8\">HLS stream</a>" +
                "</body></html>").ConfigureAwait(false);
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
                "<p><a href=\"/hls/multi.m3u8\">HLS stream link</a></p>" +
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

        _app.MapGet("/flaky/{name}", async (HttpContext context, string name) =>
        {
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var count = _requestCounts.AddOrUpdate(
                $"flaky-{name}", 1, (_, existing) => existing + 1);
            var failures = QueryInt(context, "failures", 1);
            if (count <= failures)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("temporary failure").ConfigureAwait(false);
                return;
            }

            var size = QueryInt(context, "size", 65536);
            var seed = QueryInt(context, "seed", 1);
            await ServeAsync(context, ContentFor(name, size, seed), name,
                allowRanges: true, throttleBytesPerSecond: 0, unknownLength: false)
                .ConfigureAwait(false);
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
        bool forcePartial = false,
        bool throttleWhenStartIsZero = false)
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

        var effectiveThrottle = throttleWhenStartIsZero && start != 0
            ? 0
            : throttleBytesPerSecond;
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

                if (effectiveThrottle > 0)
                {
                    var expected = written / (double)effectiveThrottle;
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

    private void BeginFileRequest()
    {
        var active = Interlocked.Increment(ref _activeFileRequests);
        var currentPeak = Volatile.Read(ref _peakFileRequests);
        while (active > currentPeak)
        {
            var observed = Interlocked.CompareExchange(ref _peakFileRequests, active, currentPeak);
            if (observed == currentPeak)
            {
                break;
            }

            currentPeak = observed;
        }
    }

    private void EndFileRequest() => Interlocked.Decrement(ref _activeFileRequests);

    public async ValueTask DisposeAsync()
    {
        if (Ftp is not null)
        {
            await Ftp.DisposeAsync().ConfigureAwait(false);
        }

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
        Fill(ref state, bytes);
        return bytes;
    }

    public static void Fill(ref uint state, Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[i] = (byte)(state & 0xFF);
        }
    }
}
