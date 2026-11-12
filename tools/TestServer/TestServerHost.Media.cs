using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TestServer;

/// <summary>
/// Generated HLS and DASH streams with fault switches, for the engine's media path. Everything is computed from the
/// query, so a test states its whole scenario in the playlist address:
/// <list type="bullet">
/// <item><c>n</c>, <c>size</c>: fragment count and size (fragment i holds <see cref="MediaFragment"/>(i, size)).</item>
/// <item><c>cut</c>, <c>cutAt</c>: the first request of fragment <c>cut</c> sends <c>cutAt</c> bytes of a declared
/// whole and then drops the connection.</item>
/// <item><c>redirect=1</c>: every fragment and key request answers 302 once, to the same address with <c>hop=1</c>.</item>
/// <item><c>keys=query</c>: AES-128, fragment i under <c>key?id={i+1}</c> (addresses that differ only in the query).
/// <c>ivattr=1</c> also names an IV and puts <c>div=3</c> in the key's query.</item>
/// <item><c>chunk</c>, <c>delayMs</c>: fragments are sent <c>chunk</c> bytes at a time, <c>delayMs</c> apart.</item>
/// <item><c>keysize</c>: the key response's length (16 is a real key).</item>
/// </list>
/// </summary>
public sealed partial class TestServerHost
{
    private readonly ConcurrentDictionary<string, int> _mediaRequests = new(StringComparer.Ordinal);
    private long _mediaBytesServed;

    /// <summary>Requests of a media name since the last reset: <c>seg0</c>, <c>key1</c>, <c>redirect</c>, <c>v1</c>, <c>a2</c>, <c>chunk3</c>.</summary>
    public int MediaRequests(string name) => _mediaRequests.GetValueOrDefault(name);

    /// <summary>Bytes the oversized-playlist route wrote before the client went away.</summary>
    public long MediaBytesServed => Interlocked.Read(ref _mediaBytesServed);

    public void ResetMediaStats()
    {
        _mediaRequests.Clear();
        Interlocked.Exchange(ref _mediaBytesServed, 0);
    }

    /// <summary>The clear bytes of generated fragment <paramref name="index"/>.</summary>
    public static byte[] MediaFragment(int index, int size) => TestContent.Generate(1000 + index, size);

    /// <summary>The AES-128 key served as <c>key?id=<paramref name="id"/></c>.</summary>
    public static byte[] MediaKey(int id) => SHA256.HashData(Encoding.UTF8.GetBytes($"media-key-{id}"))[..16];

    /// <summary>The IV a playlist with <c>ivattr=1</c> names for fragment <paramref name="index"/>; not the sequence IV.</summary>
    public static byte[] MediaExplicitIv(int index)
    {
        var iv = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        iv[15] = (byte)index;
        return iv;
    }

    private static byte[] SequenceIv(int index)
    {
        var iv = new byte[16];
        BitConverter.GetBytes(index).Reverse().ToArray().CopyTo(iv, 12);
        return iv;
    }

    private void MapMediaEndpoints()
    {
        _app.MapGet("/media/hls/{file}", async (HttpContext context, string file) =>
        {
            var query = context.Request.QueryString.Value ?? string.Empty;
            var n = QueryInt(context, "n", 2);
            var size = QueryInt(context, "size", 3000);
            var encrypted = string.Equals(context.Request.Query["keys"], "query", StringComparison.Ordinal);
            var explicitIv = QueryInt(context, "ivattr", 0) == 1;

            if (file is "playlist.m3u8" or "playlist")
            {
                var text = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n");
                var rest = query.TrimStart('?');
                for (var i = 0; i < n; i++)
                {
                    if (encrypted)
                    {
                        var keyUri = $"key?id={i + 1}" + (explicitIv ? "&div=3" : string.Empty) + $"&{rest}";
                        text.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"{keyUri}\"");
                        if (explicitIv) text.Append(",IV=0x").Append(Convert.ToHexString(MediaExplicitIv(i)));
                        text.Append('\n');
                    }

                    text.Append("#EXTINF:2.000000,\n").Append($"seg{i}.ts?{rest}\n");
                }

                text.Append("#EXT-X-ENDLIST\n");
                context.Response.ContentType = "application/vnd.apple.mpegurl";
                await context.Response.WriteAsync(text.ToString()).ConfigureAwait(false);
                return;
            }

            if (QueryInt(context, "redirect", 0) == 1 && QueryInt(context, "hop", 0) == 0)
            {
                _mediaRequests.AddOrUpdate("redirect", 1, (_, existing) => existing + 1);
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = $"{file}{query}&hop=1";
                return;
            }

            if (file == "key")
            {
                var id = QueryInt(context, "id", 1);
                _mediaRequests.AddOrUpdate($"key{id}", 1, (_, existing) => existing + 1);
                var keySize = QueryInt(context, "keysize", 16);
                var key = keySize == 16 ? MediaKey(id) : new byte[keySize];
                context.Response.ContentType = "application/octet-stream";
                await context.Response.Body.WriteAsync(key).ConfigureAwait(false);
                return;
            }

            if (!file.StartsWith("seg", StringComparison.Ordinal) || !file.EndsWith(".ts", StringComparison.Ordinal) ||
                !int.TryParse(file[3..^3], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= n)
            {
                context.Response.StatusCode = 404;
                return;
            }

            var count = _mediaRequests.AddOrUpdate($"seg{index}", 1, (_, existing) => existing + 1);
            var bytes = MediaFragment(index, size);
            if (encrypted)
            {
                using var aes = Aes.Create();
                aes.Key = MediaKey(index + 1);
                bytes = aes.EncryptCbc(bytes, explicitIv ? MediaExplicitIv(index) : SequenceIv(index), PaddingMode.PKCS7);
            }

            context.Response.ContentType = "video/mp2t";
            context.Response.ContentLength = bytes.Length;
            if (QueryInt(context, "cut", -1) == index && count == 1)
            {
                var cutAt = Math.Min(QueryInt(context, "cutAt", 1000), bytes.Length - 1);
                await context.Response.Body.WriteAsync(bytes.AsMemory(0, cutAt)).ConfigureAwait(false);
                await context.Response.Body.FlushAsync().ConfigureAwait(false);
                context.Abort();
                return;
            }

            await WriteChunkedAsync(context, bytes).ConfigureAwait(false);
        });

        // A playlist address that keeps sending: the client must stop reading at its limit, not hold it all.
        _app.MapGet("/media/big.m3u8", async (HttpContext context) =>
        {
            var megabytes = QueryInt(context, "mb", 64);
            context.Response.ContentType = "application/vnd.apple.mpegurl";
            var line = Encoding.ASCII.GetBytes("#" + new string('x', 1022) + "\n");
            try
            {
                for (var i = 0; i < megabytes * 1024; i++)
                {
                    await context.Response.Body.WriteAsync(line, context.RequestAborted).ConfigureAwait(false);
                    Interlocked.Add(ref _mediaBytesServed, line.Length);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // the client went away
            }
        });

        _app.MapGet("/media/dash/{file}", async (HttpContext context, string file) =>
        {
            var size = QueryInt(context, "size", 3000);
            if (file.EndsWith(".mpd", StringComparison.Ordinal))
            {
                var manifest = DashManifest(file[..^4]);
                if (manifest is null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.ContentType = "application/dash+xml";
                await context.Response.WriteAsync(manifest).ConfigureAwait(false);
                return;
            }

            if (file == "sidx.bin")
            {
                // A SIDX box of version 1 cut after 24 bytes: the 64-bit times it announces are missing.
                var sidx = new byte[24];
                sidx[3] = 24;
                Encoding.ASCII.GetBytes("sidx").CopyTo(sidx, 4);
                sidx[8] = 1;
                await WriteRangeAsync(context, sidx).ConfigureAwait(false);
                return;
            }

            // v{n}.m4s, a{n}.m4s and chunk{n}.m4s: video, audio and single-track fragments, numbered from 1.
            var stem = file.EndsWith(".m4s", StringComparison.Ordinal) ? file[..^4] : string.Empty;
            var prefix = stem.StartsWith("chunk", StringComparison.Ordinal) ? "chunk" : stem.Length > 0 ? stem[..1] : string.Empty;
            if (prefix is not ("chunk" or "v" or "a") ||
                !int.TryParse(stem[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                context.Response.StatusCode = 404;
                return;
            }

            _mediaRequests.AddOrUpdate(stem, 1, (_, existing) => existing + 1);

            context.Response.ContentType = "video/mp4";
            var bytes = DashFragment(prefix, number, size);
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
        });
    }

    /// <summary>The bytes of DASH fragment <paramref name="number"/> of a track: <c>v</c>, <c>a</c> or <c>chunk</c>.</summary>
    public static byte[] DashFragment(string track, int number, int size)
        => MediaFragment(track switch { "v" => 0, "a" => 100, _ => 200 } + number, size);

    private static async Task WriteChunkedAsync(HttpContext context, byte[] bytes)
    {
        var chunk = QueryInt(context, "chunk", 0);
        var delay = QueryInt(context, "delayMs", 0);
        if (chunk <= 0)
        {
            await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
            return;
        }

        try
        {
            for (var offset = 0; offset < bytes.Length; offset += chunk)
            {
                var count = Math.Min(chunk, bytes.Length - offset);
                await context.Response.Body.WriteAsync(bytes.AsMemory(offset, count), context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                if (offset + count < bytes.Length && delay > 0)
                {
                    await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // the client went away
        }
    }

    private static async Task WriteRangeAsync(HttpContext context, byte[] bytes)
    {
        var range = context.Request.Headers.Range.ToString();
        if (range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var parts = range["bytes=".Length..].Split('-');
            var from = long.Parse(parts[0], CultureInfo.InvariantCulture);
            var to = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : bytes.Length - 1;
            to = Math.Min(to, bytes.Length - 1);
            context.Response.StatusCode = 206;
            context.Response.Headers.ContentRange = $"bytes {from}-{to}/{bytes.Length}";
            context.Response.ContentLength = to - from + 1;
            await context.Response.Body.WriteAsync(bytes.AsMemory((int)from, (int)(to - from + 1))).ConfigureAwait(false);
            return;
        }

        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string? DashManifest(string name)
    {
        const string head = "<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" ";
        return name switch
        {
            // r="-1" to the end of a 6-second presentation of 2-second fragments: three of them.
            "repeat" => head + "mediaPresentationDuration=\"PT6S\"><Period>" +
                        "<AdaptationSet mimeType=\"video/mp4\"><Representation id=\"r\" bandwidth=\"1\">" +
                        "<SegmentTemplate timescale=\"1\" media=\"chunk$Number$.m4s\" startNumber=\"1\">" +
                        "<SegmentTimeline><S t=\"0\" d=\"2\" r=\"-1\"/></SegmentTimeline>" +
                        "</SegmentTemplate></Representation></AdaptationSet></Period></MPD>",
            "huge-repeat" => head + "mediaPresentationDuration=\"PT6S\"><Period>" +
                             "<AdaptationSet mimeType=\"video/mp4\"><Representation id=\"r\" bandwidth=\"1\">" +
                             "<SegmentTemplate timescale=\"1\" media=\"chunk$Number$.m4s\" startNumber=\"1\">" +
                             "<SegmentTimeline><S t=\"0\" d=\"1\" r=\"2000000000\"/></SegmentTimeline>" +
                             "</SegmentTemplate></Representation></AdaptationSet></Period></MPD>",
            "huge-duration" => head + "mediaPresentationDuration=\"P100000D\"><Period>" +
                               "<AdaptationSet mimeType=\"video/mp4\"><Representation id=\"r\" bandwidth=\"1\">" +
                               "<SegmentTemplate timescale=\"1\" duration=\"1\" media=\"chunk$Number$.m4s\" startNumber=\"1\"/>" +
                               "</Representation></AdaptationSet></Period></MPD>",
            "multiperiod" => head + "mediaPresentationDuration=\"PT4S\">" +
                             string.Concat(Enumerable.Repeat(
                                 "<Period duration=\"PT2S\"><AdaptationSet mimeType=\"video/mp4\"><Representation id=\"r\" bandwidth=\"1\">" +
                                 "<SegmentTemplate timescale=\"1\" duration=\"2\" media=\"chunk$Number$.m4s\" startNumber=\"1\"/>" +
                                 "</Representation></AdaptationSet></Period>", 2)) + "</MPD>",
            "sidx-short" => head + "mediaPresentationDuration=\"PT4S\"><Period>" +
                            "<AdaptationSet mimeType=\"video/mp4\"><Representation id=\"r\" bandwidth=\"1\">" +
                            "<BaseURL>sidx.bin</BaseURL><SegmentBase indexRange=\"0-23\"/>" +
                            "</Representation></AdaptationSet></Period></MPD>",
            // Separate video and audio, two 2-second fragments each: muxed into one file.
            "av" => head + "mediaPresentationDuration=\"PT4S\"><Period>" +
                    "<AdaptationSet mimeType=\"video/mp4\"><Representation id=\"v\" bandwidth=\"2\">" +
                    "<SegmentTemplate timescale=\"1\" duration=\"2\" media=\"v$Number$.m4s\" startNumber=\"1\"/>" +
                    "</Representation></AdaptationSet>" +
                    "<AdaptationSet mimeType=\"audio/mp4\"><Representation id=\"a\" bandwidth=\"1\">" +
                    "<SegmentTemplate timescale=\"1\" duration=\"2\" media=\"a$Number$.m4s\" startNumber=\"1\"/>" +
                    "</Representation></AdaptationSet></Period></MPD>",
            _ => null,
        };
    }
}
