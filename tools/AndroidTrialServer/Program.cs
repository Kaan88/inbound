using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;

// Throw-away server for tools/AndroidTrial: GET/HEAD /bench.bin?size=N[&bps=B] with single-range support, and
// GET /sha256?size=N (the SHA-256 of that file). The bytes are the same 1 MiB pseudo-random block repeated, as in
// tools/Bench. bps throttles each connection (so 8 connections carry 8 x bps). Binds to 127.0.0.1 only; the
// emulator reaches it as 10.0.2.2.
const int BlockLength = 1 << 20;
var block = new byte[BlockLength];
uint s = 0x9E3779B9;
for (var i = 0; i < block.Length; i++)
{
    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
    block[i] = (byte)s;
}

var port = 0;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port") port = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
// --lan: listen on every address, so a phone on the same Wi-Fi can reach it (a real link, not adb reverse).
var lan = args.Contains("--lan");
// --log: one line per GET when it ends, "SENT start=<first byte> bytes=<bytes written>", so a script can tell a resume
// (requests after a restart add up to less than the file) from a download that started again from zero.
var logSent = args.Contains("--log");

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(o =>
{
    o.Listen(lan ? IPAddress.Any : IPAddress.Loopback, port);
    o.Limits.MinResponseDataRate = null;
});
var app = builder.Build();

app.MapGet("/sha256", (long size) =>
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    for (long pos = 0; pos < size;)
    {
        var n = (int)Math.Min(BlockLength - (int)(pos % BlockLength), size - pos);
        hash.AppendData(block, (int)(pos % BlockLength), n);
        pos += n;
    }

    return Results.Text(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
});

app.MapMethods("/bench.bin", ["GET", "HEAD"], async (HttpContext context) =>
{
    var req = context.Request;
    var res = context.Response;
    if (!long.TryParse(req.Query["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
    {
        res.StatusCode = 400;
        return;
    }

    long.TryParse(req.Query["bps"], NumberStyles.None, CultureInfo.InvariantCulture, out var bps);
    long start = 0, end = size - 1;
    var partial = false;
    if (req.Headers.Range.Count > 0)
    {
        var header = req.Headers.Range[0]!;
        var parts = header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) ? header[6..].Split('-', 2) : [];
        if (parts.Length != 2 || !long.TryParse(parts[0], out start) || start >= size)
        {
            res.StatusCode = 416;
            res.Headers.ContentRange = $"bytes */{size}";
            return;
        }

        end = parts[1].Length == 0 ? size - 1 : Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), size - 1);
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

    var sw = Stopwatch.StartNew();
    long sent = 0;
    try
    {
        for (var pos = start; pos <= end;)
        {
            var offset = (int)(pos % BlockLength);
            var n = (int)Math.Min(bps > 0 ? Math.Min(BlockLength - offset, 64 * 1024) : BlockLength - offset, end - pos + 1);
            await res.Body.WriteAsync(block.AsMemory(offset, n), context.RequestAborted).ConfigureAwait(false);
            pos += n;
            sent += n;
            if (bps > 0)
            {
                var delay = TimeSpan.FromSeconds(sent / (double)bps) - sw.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
            }
        }
    }
    finally
    {
        if (logSent) Console.WriteLine(FormattableString.Invariant($"SENT start={start} bytes={sent}"));
    }
});

await app.StartAsync().ConfigureAwait(false);
Console.WriteLine($"LISTENING {app.Urls.First().TrimEnd('/')}");
await app.WaitForShutdownAsync().ConfigureAwait(false);
