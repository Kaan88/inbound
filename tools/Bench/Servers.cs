using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestServer;

namespace Bench;

/// <summary>The bytes of the static server's file: one 1 MiB block of pseudo-random bytes, repeated. Offset i holds Block[i % length].</summary>
internal static class Pattern
{
    public const int BlockLength = 1 << 20;
    public static readonly byte[] Block = Make();

    private static byte[] Make()
    {
        var b = new byte[BlockLength];
        uint s = 0x9E3779B9;
        for (var i = 0; i < b.Length; i++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            b[i] = (byte)s;
        }

        return b;
    }

    /// <summary>Compares the file at a few offsets (start, middle, end) with the pattern.</summary>
    public static bool Verify(string path, long size)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        if (f.Length != size) return false;
        var buffer = new byte[4096];
        foreach (var offset in new[] { 0L, Math.Max(0, size / 2 - 17), Math.Max(0, size - buffer.Length) })
        {
            var n = (int)Math.Min(buffer.Length, size - offset);
            if (RandomAccess.Read(f.SafeFileHandle, buffer.AsSpan(0, n), offset) != n) return false;
            for (var i = 0; i < n; i++)
                if (buffer[i] != Block[(offset + i) % BlockLength]) return false;
        }

        return true;
    }
}

/// <summary>
/// A minimal static server: GET/HEAD /bench.bin?size=N, with single-range support. Nothing is generated or allocated per
/// request; every response is slices of the one shared block. Runs in its own process ("serve" sub-command).
/// </summary>
internal static class StaticServer
{
    public const string Path = "/bench.bin";

    public static async Task RunAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);
            o.Limits.MinResponseDataRate = null;
            o.AllowSynchronousIO = false;
        });
        var app = builder.Build();
        app.MapMethods(Path, ["GET", "HEAD"], Handle);
        await app.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"LISTENING {app.Urls.First().TrimEnd('/')}");
        await Servers.WaitForParentAsync().ConfigureAwait(false);
        await app.StopAsync().ConfigureAwait(false);
    }

    private static async Task Handle(HttpContext context)
    {
        var req = context.Request;
        var res = context.Response;
        if (!long.TryParse(req.Query["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
        {
            res.StatusCode = 400;
            return;
        }

        long start = 0, end = size - 1;
        var partial = false;
        if (req.Headers.Range.Count > 0)
        {
            if (!TryParseRange(req.Headers.Range[0]!, size, out start, out end))
            {
                res.StatusCode = 416;
                res.Headers.ContentRange = $"bytes */{size}";
                return;
            }

            partial = true;
        }

        res.StatusCode = partial ? 206 : 200;
        res.ContentType = "application/octet-stream";
        res.Headers.AcceptRanges = "bytes";
        res.Headers.ETag = $"\"bench-{size}\"";
        res.Headers.LastModified = "Thu, 01 Jan 2026 00:00:00 GMT";
        res.ContentLength = end - start + 1;
        if (partial) res.Headers.ContentRange = $"bytes {start}-{end}/{size}";
        if (HttpMethods.IsHead(req.Method)) return;

        var block = Pattern.Block;
        for (var pos = start; pos <= end;)
        {
            var offset = (int)(pos % Pattern.BlockLength);
            var n = (int)Math.Min(Pattern.BlockLength - offset, end - pos + 1);
            await res.Body.WriteAsync(block.AsMemory(offset, n), context.RequestAborted).ConfigureAwait(false);
            pos += n;
        }
    }

    private static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = end = 0;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',')) return false;
        var parts = header[6..].Split('-', 2);
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
            end = size - 1;
            return true;
        }

        if (!long.TryParse(parts[0], out start) || start >= size) return false;
        end = parts[1].Length == 0 ? size - 1 : long.TryParse(parts[1], out var e) ? Math.Min(e, size - 1) : -1;
        return end >= start;
    }
}

/// <summary>Starting the servers (each in its own process) and the raw-read ceilings.</summary>
internal static class Servers
{
    /// <summary>The "serve" sub-command: prints "LISTENING url" once ready, runs until stdin closes or it is killed.</summary>
    public static async Task<int> ServeAsync(Options o)
    {
        var kind = o.Get("kind", "static");
        var port = o.Int("port", 0);
        if (kind == "static")
        {
            await StaticServer.RunAsync(port).ConfigureAwait(false);
            return 0;
        }

        if (kind == "testserver")
        {
            await using var host = await TestServerHost.StartAsync(port).ConfigureAwait(false);
            Console.WriteLine($"LISTENING {host.BaseAddress}");
            await WaitForParentAsync().ConfigureAwait(false);
            return 0;
        }

        Console.Error.WriteLine($"Unknown server kind '{kind}'.");
        return 2;
    }

    public static async Task WaitForParentAsync()
    {
        // The parent holds our stdin open; when it ends (or kills us) the read returns.
        await Task.Run(() => { while (Console.In.ReadLine() is not null) { } }).ConfigureAwait(false);
    }

    public sealed class Running(Process process, string baseUrl, string kind) : IDisposable
    {
        public Process Process { get; } = process;
        public string BaseUrl { get; } = baseUrl;
        public string Kind { get; } = kind;

        public string Url(long size) => Kind == "static"
            ? $"{BaseUrl}{StaticServer.Path}?size={size}"
            : $"{BaseUrl}/file/bench.bin?size={size}&seed=1";

        public double CpuSeconds()
        {
            try { Process.Refresh(); return Process.TotalProcessorTime.TotalSeconds; } catch (Exception) { return double.NaN; }
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(5000); }
            }
            catch (Exception) { /* already gone */ }

            Process.Dispose();
        }
    }

    /// <summary>Starts "Bench serve --kind X" as a child of this process, so the server's CPU is not mixed with the engine's.</summary>
    public static async Task<Running> StartAsync(string kind)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var info = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Servers).Assembly.Location);
        info.ArgumentList.Add("serve");
        info.ArgumentList.Add("--kind");
        info.ArgumentList.Add(kind);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the server process.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                if (line.StartsWith("LISTENING ", StringComparison.Ordinal))
                    return new Running(process, line["LISTENING ".Length..].Trim(), kind);
        }
        catch (OperationCanceledException) { }

        try { process.Kill(true); } catch (Exception) { }
        throw new InvalidOperationException($"The {kind} server did not report that it was listening.");
    }

    public sealed record RawResult(double Seconds, long Bytes, double ClientCpuSeconds)
    {
        public double MbPerSecond => Bytes / 1e6 / Seconds;
    }

    /// <summary>Reads (and discards) <paramref name="size"/> bytes with <paramref name="connections"/> parallel range requests, 128 KiB reads as the engine does.</summary>
    public static async Task<RawResult> RawReadAsync(string url, long size, int connections)
    {
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = connections, AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using (var warm = new HttpRequestMessage(HttpMethod.Get, url))
        {
            warm.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(warm, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        var cpu0 = ProcessCpu();
        var sw = Stopwatch.StartNew();
        var chunk = size / connections;
        var tasks = Enumerable.Range(0, connections).Select(async i =>
        {
            var from = i * chunk;
            var to = i == connections - 1 ? size - 1 : from + chunk - 1;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (connections > 1) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0) total += n;
            return total;
        }).ToArray();
        var counts = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        return new RawResult(sw.Elapsed.TotalSeconds, counts.Sum(), ProcessCpu() - cpu0);
    }

    public static double ProcessCpu()
    {
        using var p = Process.GetCurrentProcess();
        return (p.UserProcessorTime + p.PrivilegedProcessorTime).TotalSeconds;
    }

    /// <summary>"curl -o NUL url" as a third opinion on the server ceiling (one connection). Null when curl is not there.</summary>
    public static async Task<RawResult?> CurlAsync(string url, long size)
    {
        try
        {
            var info = new ProcessStartInfo("curl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-s", "-o", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", "-w", "%{speed_download} %{size_download} %{time_total}", url })
                info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var text = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            var parts = text.Trim().Split(' ');
            if (p.ExitCode != 0 || parts.Length < 3) return null;
            var seconds = double.Parse(parts[2], CultureInfo.InvariantCulture);
            var bytes = (long)double.Parse(parts[1], CultureInfo.InvariantCulture);
            return new RawResult(seconds, bytes, double.NaN);
        }
        catch (Exception) { return null; }
    }
}
