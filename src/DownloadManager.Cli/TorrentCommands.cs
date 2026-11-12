using System.Globalization;
using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Cli;

/// <summary><c>dm torrent ...</c>: adding torrents, their files, options and details (T1 to T3), as the IPC offers them.</summary>
internal static class TorrentCommands
{
    public const string Usage = """
        usage: torrent inspect <magnet|file.torrent> [--dest DIR]
               torrent add <magnet|file.torrent> [--dest DIR] [--later] [--files 0,2,...]
                           [--priority INDEX=skip|low|normal|high,...] [--down BYTES/S] [--up BYTES/S] [--sequential]
               torrent files <job-id>
               torrent priority <job-id> INDEX=skip|low|normal|high ...
               torrent options <job-id> [--down BYTES/S] [--up BYTES/S] [--seed-goal global|never|ratio:N|hours:N]
                               [--sequential on|off]      (0 removes a limit; no flags shows the options)
               torrent recheck | announce | details <job-id>
               torrent seed <job-id> on|off
               torrent peers | trackers | pieces <job-id>
               torrent network
        """;

    private const string NotOffered = "Torrents aren't available here.";
    private const string NotOfferedOrOlder = "Inbound can't do that here: torrents aren't available, or the app is older than this command.";
    private const string NotRunning = "not running: the torrent is stopped, or finished and not seeding";

    /// <summary>The flags that take a value (their value is not a positional argument).</summary>
    internal static readonly string[] ValueFlags = ["--dest", "--files", "--priority", "--down", "--up", "--seed-goal", "--sequential"];

    private static readonly JsonSerializerOptions PrettyJson = new(IpcJson.Options) { WriteIndented = true };

    public static async Task<int> RunAsync(CliClient client, string[] args, bool json)
    {
        var positional = Positional(args);
        var sub = positional.FirstOrDefault()?.ToLowerInvariant();
        if (sub == "network") return await NetworkAsync(client, json).ConfigureAwait(false);
        if (sub is "inspect" or "add")
        {
            if (positional.Length < 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            return sub == "inspect"
                ? await InspectAsync(client, positional[1], Flag(args, "--dest"), json).ConfigureAwait(false)
                : await AddAsync(client, positional[1], args, json).ConfigureAwait(false);
        }

        if (sub is "files" or "priority" or "options" or "recheck" or "announce" or "details")
        {
            if (positional.Length < 2 || !Guid.TryParse(positional[1], out var id))
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            return sub switch
            {
                "files" => await FilesAsync(client, id, json).ConfigureAwait(false),
                "priority" => await PriorityAsync(client, id, positional.Skip(2).ToArray(), json).ConfigureAwait(false),
                "options" => await OptionsAsync(client, id, args, json).ConfigureAwait(false),
                "details" => await DetailsAsync(client, id, json).ConfigureAwait(false),
                _ => await ActionAsync(client, sub == "recheck" ? TorrentMessageTypes.Recheck : TorrentMessageTypes.Announce, id,
                    sub == "recheck" ? "checking the files" : "asked the trackers for peers", json).ConfigureAwait(false),
            };
        }

        if (sub is not ("seed" or "peers" or "trackers" or "pieces"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (positional.Length < 2 || !Guid.TryParse(positional[1], out var jobId))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        switch (sub)
        {
            case "seed":
            {
                var on = positional.Length > 2 ? positional[2].ToLowerInvariant() : null;
                if (on is not ("on" or "off"))
                {
                    Console.Error.WriteLine("usage: torrent seed <job-id> on|off");
                    return 2;
                }

                var reply = await client.RequestAsync(TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = jobId, On = on == "on" }).ConfigureAwait(false);
                if (reply.Type != TorrentMessageTypes.ActionResult) return Unavailable(reply);
                var result = reply.GetPayload<TorrentActionResult>();
                if (json) Print(result);
                else if (result.Ok) Console.WriteLine(on == "on" ? "seeding" : "seeding stopped");
                else Console.Error.WriteLine($"failed: {result.Error}");
                return result.Ok ? 0 : 1;
            }
            case "peers":
            {
                var reply = await client.RequestAsync(TorrentMessageTypes.Peers, new TorrentJobRequest { JobId = jobId }).ConfigureAwait(false);
                if (reply.Type != TorrentMessageTypes.PeersResult) return Unavailable(reply);
                var result = reply.GetPayload<TorrentPeersResult>();
                foreach (var peer in result.Peers)
                {
                    // A peer names its own client and flags: shown, not acted on by the terminal.
                    peer.Client = Printable(peer.Client);
                    peer.Flags = peer.Flags.Select(Printable).ToList();
                }

                if (json) Print(result);
                else if (!result.Ok) Console.Error.WriteLine($"failed: {result.Error}");
                else if (!result.Running) Console.WriteLine(NotRunning);
                else
                {
                    Console.WriteLine($"peers: {result.Peers.Count}");
                    foreach (var peer in result.Peers)
                    {
                        var name = string.IsNullOrWhiteSpace(peer.Client) ? "unknown client" : peer.Client;
                        Console.WriteLine($"  {Endpoint(peer.Address, peer.Port)}  {name}  down {Rate(peer.DownloadRate)}  up {Rate(peer.UploadRate)}" +
                                          $"  has {peer.Progress.ToString("P0", CultureInfo.InvariantCulture)}" +
                                          (peer.Flags.Count > 0 ? "  " + string.Join(",", peer.Flags) : string.Empty));
                    }
                }

                return result.Ok ? 0 : 1;
            }
            case "trackers":
            {
                var reply = await client.RequestAsync(TorrentMessageTypes.Trackers, new TorrentJobRequest { JobId = jobId }).ConfigureAwait(false);
                if (reply.Type != TorrentMessageTypes.TrackersResult) return Unavailable(reply);
                var result = reply.GetPayload<TorrentTrackersResult>();
                foreach (var tracker in result.Trackers)
                {
                    // An announce address often carries the user's passkey; a tracker's message is its own text.
                    tracker.Url = TrackerShown(tracker.Url);
                    tracker.Message = Printable(tracker.Message);
                    tracker.Status = Printable(tracker.Status);
                }

                if (json) Print(result);
                else if (!result.Ok) Console.Error.WriteLine($"failed: {result.Error}");
                else if (!result.Running) Console.WriteLine(NotRunning);
                else
                {
                    Console.WriteLine($"trackers: {result.Trackers.Count}");
                    foreach (var tracker in result.Trackers)
                    {
                        var counts = tracker.Seeds < 0 && tracker.Peers < 0 ? string.Empty
                            : $"  seeds {Count(tracker.Seeds)}  peers {Count(tracker.Peers)}";
                        var message = string.IsNullOrWhiteSpace(tracker.Message) ? string.Empty : $"  {tracker.Message}";
                        Console.WriteLine($"  tier {tracker.Tier}  {tracker.Url}  {tracker.Status}{counts}{message}");
                    }
                }

                return result.Ok ? 0 : 1;
            }
            default:
            {
                var reply = await client.RequestAsync(TorrentMessageTypes.Pieces, new TorrentJobRequest { JobId = jobId }).ConfigureAwait(false);
                if (reply.Type != TorrentMessageTypes.PiecesResult) return Unavailable(reply);
                var result = reply.GetPayload<TorrentPiecesResult>();
                if (json) Print(result);
                else if (!result.Ok) Console.Error.WriteLine($"failed: {result.Error}");
                else if (!result.Running) Console.WriteLine(NotRunning);
                else
                {
                    Console.WriteLine($"pieces: {result.Have} of {result.Count} ({Size(result.PieceLength)} each)");
                    if (result.Ranges is { Count: > 0 } ranges)
                        Console.WriteLine("  have: " + string.Join(", ", ranges.Select(range => range[0] == range[1] ? $"{range[0]}" : $"{range[0]}-{range[1]}")));
                    else if (result.Bitmap is not null)
                        Console.WriteLine("  scattered: use --json for the bitmap");
                }

                return result.Ok ? 0 : 1;
            }
        }
    }

    // ---- T3 ----

    private static string[] Positional(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) positional.Add(args[i]);
            else if (TakesValue(args, i)) i++;
        }

        return positional.ToArray();
    }

    /// <summary>
    /// Whether the flag at <paramref name="i"/> has its value next. <c>--sequential</c> takes on|off with options and
    /// nothing with add, so <c>torrent add --sequential film.torrent</c> keeps the file as the torrent to add.
    /// </summary>
    private static bool TakesValue(string[] args, int i)
    {
        if (!ValueFlags.Contains(args[i], StringComparer.OrdinalIgnoreCase) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            return false;
        return !string.Equals(args[i], "--sequential", StringComparison.OrdinalIgnoreCase) ||
               args[i + 1].ToLowerInvariant() is "on" or "off";
    }

    /// <summary>Text from a peer or a tracker, with what a terminal would act on (escape sequences, bidi marks) made spaces.</summary>
    internal static string Printable(string? text) => CliText.Printable(text);

    /// <summary>
    /// A tracker's address without what could be a passkey, as the app shows it: the query is left out, and a path
    /// other than a plain announce is cut after the host ("https://tracker.example/…").
    /// </summary>
    internal static string TrackerShown(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return "Tracker";
        var origin = $"{uri.Scheme}://{uri.Authority}";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) return Printable(origin);
        return Printable(path is "/announce" or "/announce.php" ? origin + path : origin + "/…");
    }

    private static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    private static bool Has(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A magnet as it is; a file by its full path (the Host reads it).</summary>
    private static string InputOf(string input)
        => input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? input
            : Path.GetFullPath(input);

    /// <summary>INDEX=word pairs, split by commas or given one per argument.</summary>
    private static List<TorrentFilePriorityDto>? PrioritiesOf(IEnumerable<string> parts, out string? error)
    {
        error = null;
        var priorities = new List<TorrentFilePriorityDto>();
        foreach (var part in parts.SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var pair = part.Split('=', 2);
            var word = pair.Length == 2 ? pair[1].ToLowerInvariant() : string.Empty;
            if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || word is not ("skip" or "low" or "normal" or "high"))
            {
                error = $"'{part}' is not INDEX=skip|low|normal|high";
                return null;
            }

            priorities.Add(new TorrentFilePriorityDto { Index = index, Priority = word });
        }

        return priorities;
    }

    private static bool TryLimit(string[] args, string name, out long? limit)
    {
        limit = null;
        if (Flag(args, name) is not { } text) return true;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            Console.Error.WriteLine($"{name} takes bytes per second (0 is no limit)");
            return false;
        }

        limit = value;
        return true;
    }

    private static async Task<int> InspectAsync(CliClient client, string input, string? destination, bool json)
    {
        var reply = await client.RequestAsync(TorrentMessageTypes.Inspect, new TorrentInspectRequest
        {
            Input = InputOf(input), Destination = destination is null ? null : Path.GetFullPath(destination),
        }).ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.InspectResult) return Unavailable(reply);
        var result = reply.GetPayload<TorrentInspectResult>();
        if (json) Print(result);
        else if (!result.Ok) Console.Error.WriteLine($"failed: {result.Error}");
        else
        {
            // The name, paths and folder come from the torrent as its author wrote them (audit 4 L15).
            Console.WriteLine($"{Printable(result.Name)}  {result.InfoHash}  ({result.Kind})");
            if (result.TotalBytes is { } total) Console.WriteLine($"size: {Size(total)}");
            if (result.ExistingJobId is { } existing) Console.WriteLine($"already in the list: {existing}");
            if (result.Destination is { } folder)
                Console.WriteLine($"to: {Printable(folder)}" + (result.FreeBytes is { } free ? $"  ({Size(free)} free)" : string.Empty));
            if (!string.IsNullOrWhiteSpace(result.SpaceProblem)) Console.WriteLine(result.SpaceProblem);
            if (result.Files is { } files) PrintFiles(files);
            else Console.WriteLine("files: known once the magnet's file list is in");
        }

        return result.Ok ? 0 : 1;
    }

    private static async Task<int> AddAsync(CliClient client, string input, string[] args, bool json)
    {
        var request = new TorrentAddRequest
        {
            Input = InputOf(input),
            Destination = Flag(args, "--dest") is { } dest ? Path.GetFullPath(dest) : null,
            StartMode = Has(args, "--later") ? "later" : "startNow",
        };
        if (Flag(args, "--files") is { } chosen)
        {
            var indexes = new List<int>();
            foreach (var part in chosen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    Console.Error.WriteLine("--files takes file numbers such as 0,2,5 (see torrent inspect)");
                    return 2;
                }

                indexes.Add(index);
            }

            request.Files = "chosen";
            request.FileIndexes = indexes;
        }

        if (Flag(args, "--priority") is { } priority)
        {
            request.Priorities = PrioritiesOf([priority], out var error);
            if (error is not null)
            {
                Console.Error.WriteLine(error);
                return 2;
            }
        }

        if (!TryLimit(args, "--down", out var down) || !TryLimit(args, "--up", out var up)) return 2;
        if (down is not null || up is not null || Has(args, "--sequential"))
            request.Options = new TorrentOptionsDto { DownloadLimit = down, UploadLimit = up, Sequential = Has(args, "--sequential") ? true : null };

        var reply = await client.RequestAsync(TorrentMessageTypes.Add, request).ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.AddResult) return Unavailable(reply);
        var result = reply.GetPayload<TorrentAddResult>();
        if (json) Print(result);
        else if (!result.Ok) Console.Error.WriteLine($"failed: {result.Error}");
        else Console.WriteLine($"{(result.Existing ? "already in the list" : "added")}: {result.Job!.Id}  {Printable(result.Job.FileName)}");
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> FilesAsync(CliClient client, Guid jobId, bool json)
    {
        var reply = await client.RequestAsync(TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = jobId }).ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.FilesResult) return Unavailable(reply);
        var result = reply.GetPayload<TorrentFilesResult>();
        if (json) Print(result);
        else if (!result.Ok) Console.Error.WriteLine($"failed: {result.Error}");
        else PrintFiles(result.Files);
        return result.Ok ? 0 : 1;
    }

    private static void PrintFiles(IReadOnlyList<TorrentFileDto> files)
    {
        Console.WriteLine($"files: {files.Count}");
        foreach (var file in files) Console.WriteLine(FileLine(file));
    }

    /// <summary>One file of a torrent, its path as the torrent's author wrote it made printable.</summary>
    internal static string FileLine(TorrentFileDto file)
    {
        var priority = Printable(file.Priority ?? (file.Wanted ? "normal" : "skip"));
        var done = file.Done is { } bytes ? $"  {Size(bytes)} done" : string.Empty;
        return $"  {file.Index,4}  {priority,-6}  {Size(file.Size),9}  {Printable(file.Path)}{done}";
    }

    private static async Task<int> PriorityAsync(CliClient client, Guid jobId, string[] pairs, bool json)
    {
        var priorities = PrioritiesOf(pairs, out var error);
        if (error is not null || priorities is not { Count: > 0 })
        {
            Console.Error.WriteLine(error ?? "usage: torrent priority <job-id> INDEX=skip|low|normal|high ...");
            return 2;
        }

        var reply = await client.RequestAsync(TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = jobId, Priorities = priorities })
            .ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.ActionResult) return Unavailable(reply);
        var result = reply.GetPayload<TorrentActionResult>();
        if (json) Print(result);
        else if (result.Ok) Console.WriteLine($"priorities set for {priorities.Count} file{(priorities.Count == 1 ? string.Empty : "s")}");
        else Console.Error.WriteLine($"failed: {result.Error}");
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> OptionsAsync(CliClient client, Guid jobId, string[] args, bool json)
    {
        if (!TryLimit(args, "--down", out var down) || !TryLimit(args, "--up", out var up)) return 2;
        string? goal = null;
        double? goalValue = null;
        if (Flag(args, "--seed-goal") is { } goalText)
        {
            var parts = goalText.ToLowerInvariant().Split(':', 2);
            goal = parts[0];
            if (parts.Length == 2)
            {
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    Console.Error.WriteLine("--seed-goal takes global, never, ratio:N or hours:N");
                    return 2;
                }

                goalValue = value;
            }
        }

        bool? sequential = null;
        if (Flag(args, "--sequential") is { } sequentialText)
        {
            if (sequentialText.ToLowerInvariant() is not ("on" or "off"))
            {
                Console.Error.WriteLine("--sequential takes on or off");
                return 2;
            }

            sequential = sequentialText.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        var change = down is not null || up is not null || goal is not null || sequential is not null;
        var reply = change
            ? await client.RequestAsync(TorrentMessageTypes.SetOptions, new TorrentOptionsDto
            {
                JobId = jobId, DownloadLimit = down, UploadLimit = up, SeedGoal = goal, SeedGoalValue = goalValue, Sequential = sequential,
            }).ConfigureAwait(false)
            : await client.RequestAsync(TorrentMessageTypes.Options, new TorrentJobRequest { JobId = jobId }).ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.OptionsResult) return OlderHost(reply);
        var result = reply.GetPayload<TorrentOptionsResult>();
        if (json) Print(result);
        else if (!result.Ok) Console.Error.WriteLine($"failed: {result.Error}");
        else
        {
            var options = result.Options!;
            Console.WriteLine($"download limit: {Limit(options.DownloadLimit)}");
            Console.WriteLine($"upload limit: {Limit(options.UploadLimit)}");
            Console.WriteLine($"seeding goal: {options.SeedGoal}" +
                              (options.SeedGoalValue is { } value ? " " + value.ToString(CultureInfo.InvariantCulture) : string.Empty));
            Console.WriteLine($"in order: {(options.Sequential == true ? "on" : "off")}");
        }

        return result.Ok ? 0 : 1;
    }

    private static string Limit(long? bytesPerSecond) => bytesPerSecond is > 0 ? Rate(bytesPerSecond.Value) : "none";

    private static async Task<int> ActionAsync(CliClient client, string type, Guid jobId, string done, bool json)
    {
        var reply = await client.RequestAsync(type, new TorrentJobRequest { JobId = jobId }).ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.ActionResult) return OlderHost(reply);
        var result = reply.GetPayload<TorrentActionResult>();
        if (json) Print(result);
        else if (result.Ok) Console.WriteLine(done);
        else Console.Error.WriteLine($"failed: {result.Error}");
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> DetailsAsync(CliClient client, Guid jobId, bool json)
    {
        var reply = await client.RequestAsync(TorrentMessageTypes.Details, new TorrentJobRequest { JobId = jobId }).ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.DetailsResult) return OlderHost(reply);
        var result = reply.GetPayload<TorrentDetailsResult>();
        if (json)
        {
            Print(result);
            return result.Ok ? 0 : 1;
        }

        if (!result.Ok)
        {
            Console.Error.WriteLine($"failed: {result.Error}");
            return 1;
        }

        Console.WriteLine($"info hash: {result.InfoHash}" + (result.InfoHashV2 is { } v2 ? $"  v2 {v2}" : string.Empty));
        Console.WriteLine($"from: {Printable(result.Source)}  added {result.AddedAt.ToLocalTime():g}");
        Console.WriteLine($"folder: {Printable(result.DestinationDirectory)}");
        if (!result.Running)
        {
            Console.WriteLine(NotRunning);
            return 0;
        }

        Console.WriteLine($"pieces: {result.PieceCount} of {Size(result.PieceLength)}");
        if (result.Private is { } isPrivate) Console.WriteLine($"private: {(isPrivate ? "yes" : "no")}");
        Console.WriteLine($"wasted: {Size(result.Wasted ?? 0)}  failed checks: {result.HashFailures ?? 0}");
        var limit = result.ConnectionsLimit is { } most ? most > 0 ? $" of {most.ToString(CultureInfo.InvariantCulture)}" : " (no limit)" : string.Empty;
        Console.WriteLine($"connections: {result.Connections ?? 0}{limit}");
        if (result.DistributedCopies is { } copies) Console.WriteLine($"copies among peers: {copies.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (result.NextAnnounce is { } next) Console.WriteLine($"next announce: in {next} s");
        if (result.Pex is { } pex) Console.WriteLine($"peer exchange: {(pex ? "on" : "off")}");
        if (result.DhtNodes is { } nodes) Console.WriteLine($"DHT nodes: {(nodes < 0 ? "DHT off" : nodes.ToString(CultureInfo.InvariantCulture))}");
        return 0;
    }

    /// <summary>A reply that is not the T3 result: a Host without torrents, or one older than these commands.</summary>
    private static int OlderHost(IpcMessage reply)
    {
        Console.Error.WriteLine(NotDone(reply, NotOfferedOrOlder));
        return 1;
    }

    private static async Task<int> NetworkAsync(CliClient client, bool json)
    {
        var reply = await client.RequestAsync(TorrentMessageTypes.Network, new { }).ConfigureAwait(false);
        if (reply.Type != TorrentMessageTypes.NetworkResult) return Unavailable(reply);
        var result = reply.GetPayload<TorrentNetworkResult>();
        if (json)
        {
            Print(result);
            return result.Ok ? 0 : 1;
        }

        if (!result.Ok)
        {
            Console.Error.WriteLine($"failed: {result.Error}");
            return 1;
        }

        Console.WriteLine($"torrent engine: {(result.Running ? "running" : "not running")}");
        if (!result.Ran)
        {
            Console.WriteLine("no torrent has run since the app started, so the port hasn't been tried yet");
            return 0;
        }

        Console.WriteLine($"port: {(result.Port > 0 ? result.Port.ToString(CultureInfo.InvariantCulture) : "not reported")}");
        if (result.UnusablePort is { } unusable) Console.WriteLine($"port {unusable} from Options couldn't be opened; this run used the one above");
        if (!string.IsNullOrWhiteSpace(result.ListenError)) Console.WriteLine($"listen error: {result.ListenError}");
        Console.WriteLine($"router forwarding: {(result.MappedPort is { } mapped ? $"port {mapped}" : "none")}");
        Console.WriteLine($"incoming connections: {(result.IncomingSeen ? "seen, so other peers can reach this PC" : "none seen yet")}");
        if (result.DhtNodes is { } dht) Console.WriteLine($"DHT nodes: {(dht < 0 ? "DHT off" : dht.ToString(CultureInfo.InvariantCulture))}");
        return 0;
    }

    private static int Unavailable(IpcMessage reply)
    {
        Console.Error.WriteLine(NotDone(reply, NotOffered));
        return 1;
    }

    /// <summary>
    /// What to say for a reply that is not the expected result (audit 4 M12). A Host that doesn't know the command
    /// answers "Unknown message type", which is <paramref name="fallback"/>'s case; any other host.actionResult is the
    /// Host's own error (a refusal, or the command failed there) and is shown in its words.
    /// </summary>
    internal static string NotDone(IpcMessage reply, string fallback)
        => HostError(reply) is { } error ? "failed: " + error : fallback;

    /// <summary>The Host's own error in a reply that is not the expected result: see <see cref="CliText.HostError"/>.</summary>
    internal static string? HostError(IpcMessage reply) => CliText.HostError(reply);

    private static string Endpoint(string address, int port) => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";

    private static string Count(int value) => value < 0 ? "?" : value.ToString(CultureInfo.InvariantCulture);

    private static string Rate(long bytesPerSecond) => Size(bytesPerSecond) + "/s";

    private static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, PrettyJson));
}
