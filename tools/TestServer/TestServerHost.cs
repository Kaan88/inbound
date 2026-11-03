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
    private int _activeRangeRequests;
    private int _peakRangeRequests;

    public int PeakConcurrentRangeRequests => Volatile.Read(ref _peakRangeRequests);

    public void ResetConcurrency()
    {
        Interlocked.Exchange(ref _activeRangeRequests, 0);
        Interlocked.Exchange(ref _peakRangeRequests, 0);
    }

    private TestServerHost(WebApplication app)
    {
        _app = app;
    }

    public string BaseAddress { get; private set; } = string.Empty;

    public static async Task<TestServerHost> StartAsync(int port = 0, CancellationToken cancellationToken = default)
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
            return Results.Ok();
        });
    }

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
