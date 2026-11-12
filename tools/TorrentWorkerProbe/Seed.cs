using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DownloadManager.Torrents;

/// <summary>
/// Third mode, for walking the app's torrent UI: <c>TorrentWorkerProbe seed &lt;worker exe&gt; &lt;work dir&gt; &lt;app port&gt;
/// &lt;minutes&gt;</c>. Makes three torrents (a multi-file pack, a single file, 250 small files), writes their .torrent files
/// and magnets to <c>&lt;work dir&gt;\out</c>, seeds them on 127.0.0.1 only, and keeps offering itself to the app's worker
/// at 127.0.0.1:&lt;app port&gt; (a Host started with INBOUND_TORRENT_TEST_LISTEN=&lt;app port&gt;), since loopback has no tracker
/// or DHT. The pack uploads at 3 MB/s so its progress can be watched.
/// </summary>
internal static class Seed
{
    private const int PieceLength = 256 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        var (exe, dir, appPort, minutes) = (Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), int.Parse(args[3]),
            double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        var many = Enumerable.Range(1, 250).Select(i => ($"page-{i:000}.txt", 4096L + i)).ToArray();
        var torrents = new[]
        {
            Make(dir, "Inbound T1 test pack",
            [
                ("video/film.mkv", 120L << 20), ("video/extras/making-of.mkv", 40L << 20), ("subs/en.srt", 20_000),
                ("subs/tr.srt", 21_000), ("cover.jpg", 300_000), ("readme.txt", 1_000),
            ]),
            Make(dir, "single-file.bin", null, 30L << 20),
            Make(dir, "Inbound many files", many),
        };

        var worker = TorrentWorkerProcess.Start(exe, log: line => Console.Error.WriteLine(line));
        var seeding = new HashSet<string>();
        var drain = Task.Run(async () =>
        {
            await foreach (var e in worker.Events.ReadAllAsync())
            {
                if (e["event"]?.GetValue<string>() != "progress") continue;
                foreach (var r in e["torrents"]!.AsArray())
                    if (r!["state"]!.GetValue<string>() == "seeding") lock (seeding) seeding.Add(r["id"]!.GetValue<string>());
            }
        });
        await worker.SendAsync("setListen", new JsonObject { ["port"] = 0, ["interfaces"] = new JsonArray("127.0.0.1"), ["dht"] = false, ["lsd"] = false, ["upnp"] = false, ["natpmp"] = false });
        await worker.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 1000 });
        var magnets = new StringBuilder();
        for (var i = 0; i < torrents.Length; i++)
        {
            var (name, bytes, hash) = torrents[i];
            var file = Path.Combine(outDir, Safe(name) + ".torrent");
            await File.WriteAllBytesAsync(file, bytes);
            magnets.AppendLine($"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}");
            await worker.SendAsync("add", new JsonObject { ["id"] = $"s{i}", ["savePath"] = dir, ["torrent"] = Convert.ToBase64String(bytes), ["seed"] = true });
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "magnets.txt"), magnets.ToString());
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (seeding) if (seeding.Count == torrents.Length) break;
            await Task.Delay(200);
        }

        await worker.SendAsync("setTorrentLimits", new JsonObject { ["id"] = "s0", ["upload"] = 3_000_000 });
        lock (seeding) Console.WriteLine($"{seeding.Count} of {torrents.Length} seeding; files in {outDir}");
        var end = DateTime.UtcNow.AddMinutes(minutes);
        while (DateTime.UtcNow < end)
        {
            for (var i = 0; i < torrents.Length; i++)
            {
                try
                {
                    await worker.SendAsync("addPeer", new JsonObject { ["id"] = $"s{i}", ["address"] = "127.0.0.1", ["port"] = appPort });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"addPeer s{i}: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        await worker.CloseAsync(TimeSpan.FromSeconds(15));
        await drain;
        return 0;
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-'));

    /// <summary>Writes the payload under <paramref name="dir"/> (kept when already there) and returns the metainfo.</summary>
    private static (string Name, byte[] Torrent, string Hash) Make(string dir, string name, (string Path, long Length)[]? files, long single = 0)
    {
        var list = files ?? [(name, single)];
        var root = files is null ? dir : Path.Combine(dir, name);
        using var pieces = new MemoryStream();
        var buffer = new byte[PieceLength];
        var filled = 0;
        var seed = 1;
        foreach (var (path, length) in list)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full) || new FileInfo(full).Length != length)
            {
                var random = new Random(seed);
                using var write = File.Create(full);
                var chunk = new byte[1 << 20];
                for (long done = 0; done < length;)
                {
                    var n = (int)Math.Min(chunk.Length, length - done);
                    random.NextBytes(chunk.AsSpan(0, n));
                    write.Write(chunk, 0, n);
                    done += n;
                }
            }

            seed++;
            using var read = File.OpenRead(full);
            int got;
            while ((got = read.Read(buffer, filled, PieceLength - filled)) > 0)
            {
                filled += got;
                if (filled == PieceLength)
                {
                    pieces.Write(SHA1.HashData(buffer));
                    filled = 0;
                }
            }
        }

        if (filled > 0) pieces.Write(SHA1.HashData(buffer.AsSpan(0, filled)));

        using var info = new MemoryStream();
        Bytes(info, "d");
        if (files is not null)
        {
            Str(info, "files");
            Bytes(info, "l");
            foreach (var (path, length) in files)
            {
                Bytes(info, "d");
                Str(info, "length"); Int(info, length);
                Str(info, "path");
                Bytes(info, "l");
                foreach (var part in path.Split('/')) Str(info, part);
                Bytes(info, "e");
                Bytes(info, "e");
            }

            Bytes(info, "e");
        }
        else
        {
            Str(info, "length"); Int(info, single);
        }

        Str(info, "name"); Str(info, name);
        Str(info, "piece length"); Int(info, PieceLength);
        Str(info, "pieces"); Raw(info, pieces.ToArray());
        Bytes(info, "e");
        var hash = Convert.ToHexStringLower(SHA1.HashData(info.ToArray()));
        using var torrent = new MemoryStream();
        Bytes(torrent, "d");
        Str(torrent, "info");
        torrent.Write(info.ToArray());
        Bytes(torrent, "e");
        return (name, torrent.ToArray(), hash);
    }

    private static void Bytes(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
    private static void Int(Stream s, long n) => Bytes(s, $"i{n}e");
    private static void Str(Stream s, string text) => Raw(s, Encoding.UTF8.GetBytes(text));
    private static void Raw(Stream s, byte[] data)
    {
        Bytes(s, $"{data.Length}:");
        s.Write(data);
    }
}
