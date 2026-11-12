using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T3 file priorities as the window shows them: High, Normal, Low and Skip (docs/plans/torrent-mockups b and c). Kept
/// free of WinUI so they are tested (tests/DownloadManager.Core.Tests links this file).
/// </summary>
internal static class TorrentPriorities
{
    public const string Skip = "skip";
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";

    /// <summary>The right-click menu's order, highest first.</summary>
    public static readonly IReadOnlyList<string> Menu = [High, Normal, Low, Skip];

    /// <summary>What a folder whose files differ shows.</summary>
    public const string MixedText = "mixed";

    public static bool IsKnown(string? priority) => priority is Skip or Low or Normal or High;

    /// <summary>A file's priority; an older Host leaves it out, which is Normal for a wanted file and Skip otherwise.</summary>
    public static string Of(TorrentFileDto file)
        => !file.Wanted ? Skip : IsKnown(file.Priority) && file.Priority != Skip ? file.Priority! : Normal;

    public static string Name(string? priority) => priority switch
    {
        High => "High",
        Low => "Low",
        Skip => "Skip",
        _ => "Normal",
    };

    /// <summary>A folder's word: the one priority all its files have, else "mixed"; empty with no files.</summary>
    public static string FolderText(IEnumerable<string> priorities)
    {
        string? first = null;
        foreach (var priority in priorities)
        {
            if (first is null) first = priority;
            else if (first != priority) return MixedText;
        }

        return first is null ? string.Empty : Name(first);
    }

    /// <summary>The priorities to send: the files that aren't Normal, by index (a skipped file is left out by the choice).</summary>
    public static List<TorrentFilePriorityDto>? ToSend(IReadOnlyDictionary<int, string>? priorities)
    {
        if (priorities is null) return null;
        var list = priorities.Where(pair => IsKnown(pair.Value) && pair.Value is not Normal and not Skip)
            .OrderBy(pair => pair.Key)
            .Select(pair => new TorrentFilePriorityDto { Index = pair.Key, Priority = pair.Value })
            .ToList();
        return list.Count == 0 ? null : list;
    }
}

/// <summary>
/// T3 controls of the desktop window: whether the Host has them, Force recheck and Announce now, a torrent's own
/// Options tab, the Overview's facts, the trackers' next announce, the status chip's DHT nodes, watching, Options ›
/// Torrents' peer settings and the Add dialog's free space. Kept free of WinUI so they are tested
/// (tests/DownloadManager.Core.Tests links this file).
/// </summary>
internal static class TorrentControlRules
{
    /// <summary>
    /// The Host answers T3's requests: it has torrents and answered the probe (<c>torrent.options</c> for no job) with an
    /// options reply. An older Host answers "Unknown message type"; until the probe is answered nothing T3 shows.
    /// </summary>
    public static bool Supported(bool torrentsOffered, bool? probe) => torrentsOffered && probe == true;

    /// <summary>
    /// What a reply to the probe says: true for the options reply, false when the Host doesn't know the request, null for
    /// anything else (asked again on the next connection).
    /// </summary>
    public static bool? ProbeAnswer(string? replyType, bool unknownRequest)
        => replyType == TorrentMessageTypes.OptionsResult ? true : unknownRequest ? false : null;

    public const string OlderHost = "This server doesn't have that yet. Update Inbound there to use it.";

    // ---- Force recheck and Announce now ----

    /// <summary>
    /// Force recheck applies: a torrent with its file list and something on disk, not cancelled, not being checked, and,
    /// once finished, only while it seeds (the Host checks a finished one only then).
    /// </summary>
    public static bool CanRecheck(JobDto? job)
    {
        if (job?.Torrent is not { HasMetadata: true } torrent || torrent.Checking) return false;
        if (job.State == "cancelled") return false;
        if (job.State == "completed") return torrent.Seeding;
        return job.ReceivedBytes > 0;
    }

    /// <summary>Announce now applies while the torrent runs: it downloads, connects, checks or seeds.</summary>
    public static bool CanAnnounce(JobDto? job)
        => job?.Torrent is not null && (TorrentRules.IsActive(job) || TorrentSeedRules.IsSeeding(job));

    /// <summary>The selected torrents a command acts on.</summary>
    public static List<Guid> Targets(IEnumerable<JobDto> selection, Func<JobDto, bool> applies)
        => selection.Where(applies).Select(job => job.Id).ToList();

    /// <summary>What the status bar says after Force recheck or Announce now.</summary>
    public static string Said(bool recheck, string? name, int done, int failed, string? firstError)
    {
        var what = recheck ? "check" : "announce";
        if (failed == 0)
        {
            if (recheck) return name is not null ? $"{name}: checking its files." : $"Checking the files of {done} torrents.";
            return name is not null ? $"{name}: asking the trackers for peers." : $"Asking the trackers of {done} torrents for peers.";
        }

        var reason = string.IsNullOrWhiteSpace(firstError) ? string.Empty : " " + firstError.Trim();
        if (done == 0) return name is not null ? $"{name}: the {what} didn't start.{reason}" : $"The {what} didn't start.{reason}";
        return $"{done} started, {failed} didn't.{reason}";
    }

    // ---- A torrent's own Options tab ----

    /// <summary>The largest limit the Host takes, 2,147,483,647 bytes per second, in whole KB/s.</summary>
    public const double MostLimitKilobytes = 2_097_151;

    /// <summary>The goal as the tab's choice: Global goal, Ratio, Time, Never.</summary>
    public static readonly IReadOnlyList<string> Goals = ["global", "ratio", "hours", "never"];

    /// <summary>What the tab shows: a limit box empty (NaN) when there is none.</summary>
    public sealed record OptionsForm(double DownloadKilobytes, double UploadKilobytes, string Goal, double GoalValue, bool Sequential);

    public static OptionsForm FormOf(TorrentOptionsDto? options)
    {
        options ??= new TorrentOptionsDto();
        var goal = options.SeedGoal is { } known && Goals.Contains(known) ? known : "global";
        return new OptionsForm(Kilobytes(options.DownloadLimit), Kilobytes(options.UploadLimit), goal,
            goal is "ratio" or "hours" && options.SeedGoalValue is { } value ? value : double.NaN, options.Sequential == true);
    }

    /// <summary>A limit in bytes/s as its box shows it: empty (NaN) for none.</summary>
    public static double Kilobytes(long? bytesPerSecond)
        => bytesPerSecond is > 0 ? Math.Round(bytesPerSecond.Value / 1024.0, 2) : double.NaN;

    /// <summary>A box's KB/s as bytes/s to send: 0 (none) for empty or 0.</summary>
    public static long LimitBytes(double kilobytes)
        => double.IsFinite(kilobytes) && kilobytes > 0 ? (long)Math.Min(int.MaxValue, Math.Round(kilobytes * 1024)) : 0;

    /// <summary>Why the tab can't be applied, or null: the Host's rules (Runtime's TorrentOptions), said before it refuses.</summary>
    public static string? Problem(OptionsForm form)
    {
        foreach (var limit in new[] { form.DownloadKilobytes, form.UploadKilobytes })
        {
            if (double.IsNaN(limit)) continue;
            // A ticked limit is 1 KB/s at least; none is an empty (unticked) box, never 0.
            if (!double.IsFinite(limit) || limit < 1 || limit > MostLimitKilobytes)
                return "Enter a limit from 1 to 2,097,151 KB/s, or leave it empty for none.";
        }

        return form.Goal switch
        {
            "ratio" when !(form.GoalValue is >= 0.01 and <= 100) => "Enter a seeding ratio from 0.01 to 100.",
            "hours" when !(form.GoalValue is >= 0.1 and <= 8760) => "Enter seeding hours from 0.1 to 8760.",
            _ => null,
        };
    }

    /// <summary>The tab as <c>torrent.setOptions</c>: every field set, so the Host keeps exactly what shows.</summary>
    public static TorrentOptionsDto ToDto(Guid jobId, OptionsForm form) => new()
    {
        JobId = jobId,
        DownloadLimit = LimitBytes(form.DownloadKilobytes),
        UploadLimit = LimitBytes(form.UploadKilobytes),
        SeedGoal = form.Goal,
        SeedGoalValue = form.Goal is "ratio" or "hours" ? form.GoalValue : null,
        Sequential = form.Sequential,
    };

    /// <summary>"Global goal (ratio 1.00 or 48 h)" with Options › Torrents' choice; "Global goal (don't seed)" when it is off.</summary>
    public static string GlobalGoalText(SettingsDto? settings)
    {
        if (settings is null) return "Global goal (Options › Torrents)";
        if (!settings.TorrentSeedAfterDownload) return "Global goal (don't seed)";
        var parts = new List<string>();
        if (settings.TorrentSeedRatio > 0) parts.Add($"ratio {TorrentSeedRules.Ratio(settings.TorrentSeedRatio)}");
        if (settings.TorrentSeedHours > 0) parts.Add($"{settings.TorrentSeedHours.ToString("0.#", CultureInfo.CurrentCulture)} h");
        return parts.Count == 0 ? "Global goal" : $"Global goal ({string.Join(" or ", parts)})";
    }

    /// <summary>"Set." or "Set. They apply when it starts." after the Host took the options.</summary>
    public static string Applied(bool running) => running ? "Saved. The torrent uses them now." : "Saved. They apply when the torrent starts.";

    /// <summary>
    /// After the Host saved the options: <see cref="Applied"/>, or "Saved. " and the Host's words when it added some
    /// (an older torrent program takes the limits but not the download order, and the Host says so).
    /// </summary>
    public static string SavedText(bool running, string? error)
        => string.IsNullOrWhiteSpace(error) ? Applied(running) : $"Saved. {RemoteText.Failure(error)}";

    /// <summary>
    /// The Options tab reads the torrent's options when it shows, the Host has the controls, and they aren't loaded or
    /// being read; a read already tried (and failed) waits until the tab opens again or the Host connects again.
    /// </summary>
    public static bool OptionsReadDue(bool shown, bool controls, bool connected, bool loaded, bool busy, bool tried)
        => shown && controls && connected && !loaded && !busy && !tried;

    /// <summary>
    /// Audit L5: a read after a reconnect fills the form only when it is still as last filled; edits not yet applied
    /// stay (Apply sends them). Nothing filled yet: the read fills it.
    /// </summary>
    public static bool ReadFillsForm(OptionsForm? lastFilled, OptionsForm now) => lastFilled is null || lastFilled == now;

    public const string EditsKept = "Your changes here aren't applied yet. Apply saves them.";

    // ---- Overview facts ----

    public static string SourceText(string? source) => source == "file" ? "a .torrent file" : "a magnet link";

    /// <summary>"Added 11 Nov 2026 14:02 · from a magnet link".</summary>
    public static string AddedText(DateTimeOffset addedAt, string? source)
        => $"{addedAt.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)} · from {SourceText(source)}";

    /// <summary>"Yes · DHT, PEX and LSD off", "No", or "—" while not known.</summary>
    public static string PrivateText(bool? isPrivate) => isPrivate switch
    {
        true => "Yes · DHT, PEX and local peers are off for it",
        false => "No",
        null => "—",
    };

    /// <summary>"12 MB · 3 hash failures"; "0 B · no hash failures"; "—" while not known.</summary>
    public static string WastedText(long? wasted, long? hashFailures)
    {
        if (wasted is null && hashFailures is null) return "—";
        var bytes = UiFormat.Bytes(Math.Max(0, wasted ?? 0));
        var failures = hashFailures ?? 0;
        return failures switch
        {
            <= 0 => $"{bytes} · no hash failures",
            1 => $"{bytes} · 1 hash failure",
            _ => $"{bytes} · {failures.ToString("N0", CultureInfo.CurrentCulture)} hash failures",
        };
    }

    /// <summary>The info hash line: "v1 3f9c…" and, for a hybrid or v2 torrent, " · v2 b07d…".</summary>
    public static string HashText(string infoHash, string? infoHashV2)
    {
        var v1 = infoHash.Length > 0 ? infoHash : "—";
        return infoHashV2 is { Length: 64 } v2 ? $"v1 {v1} · v2 {v2}" : v1;
    }

    /// <summary>The v2 info hash to show: the one just read, else the last one known (a read of a stopped torrent has none).</summary>
    public static string? KeptV2(string? known, string? read) => read is { Length: 64 } ? read : known;

    /// <summary>"15,820 × 256 KB"; empty while not known.</summary>
    public static string PiecesText(int count, long length)
        => count > 0 && length > 0 ? $"{count.ToString("N0", CultureInfo.CurrentCulture)} × {UiFormat.Bytes(length)}" : string.Empty;

    /// <summary>
    /// Seconds to an announce as the window says it: "now" for 0, "45 s", "24 min", "1 h 3 min"; "—" when none is planned
    /// or not known (-1 or null).
    /// </summary>
    public static string AnnounceIn(long? seconds)
    {
        if (seconds is not { } value || value < 0) return "—";
        if (value == 0) return "now";
        if (value < 60) return $"{value} s";
        var minutes = (value + 59) / 60;
        if (minutes < 60) return $"{minutes} min";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} h" : $"{hours} h {rest} min";
    }

    /// <summary>How often the Overview's facts are read again, at most, while the torrent runs and the Overview shows.</summary>
    public const long DetailsReadMs = 10_000;

    public static bool DetailsDue(bool overviewShown, bool mayRun, long sinceLastReadMs)
        => overviewShown && mayRun && sinceLastReadMs >= DetailsReadMs;

    // ---- The status chip ----

    /// <summary>"DHT 284 nodes", "DHT off"; null while the engine doesn't run or the Host didn't say.</summary>
    public static string? DhtText(long? nodes) => nodes switch
    {
        null => null,
        < 0 => "DHT off",
        1 => "DHT 1 node",
        { } count => $"DHT {count.ToString("N0", CultureInfo.CurrentCulture)} nodes",
    };

    /// <summary>The chip with the DHT words after it, when there are some.</summary>
    public static string? ChipText(string? chip, string? dht) => chip is null ? null : dht is null ? chip : $"{chip} · {dht}";

    // ---- Watching (torrent.watch) ----

    /// <summary>How long one watch lasts at the Host, and how often it is renewed while the window still shows torrents.</summary>
    public const int LeaseSeconds = 60;
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(45);

    /// <summary>
    /// A window watches (the Host then reports torrent progress every second, not every 5 seconds): it shows, isn't
    /// minimized, is connected to a Host with T3, and has a running torrent in view (one that downloads or seeds).
    /// </summary>
    public static bool Watches(bool shown, bool minimized, bool connected, bool supported, IEnumerable<JobDto?> inView)
        => shown && !minimized && connected && supported && inView.Any(job => job is not null && CanAnnounce(job));

    // ---- Options › Torrents: finding peers, privacy and limits ----

    public static readonly IReadOnlyList<(string Value, string Text)> Encryption =
    [
        ("prefer", "Prefer encrypted"),
        ("require", "Require encrypted"),
        ("off", "Don't encrypt"),
    ];

    public static int EncryptionIndex(string? value)
    {
        for (var index = 0; index < Encryption.Count; index++)
        {
            if (Encryption[index].Value == value) return index;
        }

        return 0;
    }

    /// <summary>A number box as a whole number; empty is 0.</summary>
    public static int WholeOrZero(double value) => double.IsFinite(value) ? (int)Math.Round(value) : 0;

    /// <summary>Why the limits can't be saved, or null: the Host's rules and words (Runtime's TorrentOptions).</summary>
    public static string? LimitsProblem(int seedsAtOnce, int perTorrent, int total)
    {
        if (seedsAtOnce is < 0 or > 1000) return "Enter torrents seeding at once from 0 to 1000 (0 is all of them).";
        if (perTorrent is < 0 or 1 or > 2000) return "Enter connections per torrent from 2 to 2000, or 0 for no limit of its own.";
        if (total is < 0 or (> 0 and < 10) or > 20000)
            return "Enter torrent connections in all from 10 to 20000, or 0 for the torrent program's own default.";
        return null;
    }

    /// <summary>
    /// Why torrents can't use the proxy as Options › Proxy has it (<paramref name="mode"/> <c>none</c>, <c>system</c> or
    /// <c>manual</c>; <paramref name="address"/> as <c>scheme://host:port</c>), or null. The Host's rules and words.
    /// </summary>
    public static string? ProxyProblem(string? mode, string? address)
    {
        const string turnOff = " or turn off \"Use the proxy\" in Options > Torrents.";
        if (string.Equals(mode, "system", StringComparison.OrdinalIgnoreCase))
            return "Torrents can't use the system proxy. Type the proxy in Options > Proxy," + turnOff;
        if (!string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(address))
            return "Torrents can use only a proxy typed in Options > Proxy. Set one there first," + turnOff;
        var (index, _) = ProxyAddress.Split(address);
        var scheme = ProxyAddress.Schemes[index];
        if (scheme is "socks4" or "socks4a")
            return $"Torrents can't use a {(scheme == "socks4" ? "SOCKS4" : "SOCKS4a")} proxy. Choose SOCKS5 or HTTP in Options > Proxy," + turnOff;
        return null;
    }

    /// <summary>The words under "Use the proxy": what it does with an HTTP proxy, or why it can't be used.</summary>
    public static (string Words, bool IsProblem) ProxyNote(bool useProxy, string? mode, string? address)
    {
        if (!useProxy) return ("Off: torrents connect directly, whatever Options › Proxy says.", false);
        if (ProxyProblem(mode, address) is { } problem) return (problem, true);
        var (index, _) = ProxyAddress.Split(address);
        return ProxyAddress.Schemes[index] == "http"
            ? ("An HTTP proxy carries no UDP, so DHT, local peers and uTP stay off while it is used.", false)
            : ("Peers, trackers and their names go through the proxy.", false);
    }

    // ---- The Add dialog's free space ----

    /// <summary>
    /// The free space line of a read torrent: "612 GB free in D:\Downloads"; a problem when the files to download don't
    /// fit. Null when the Host didn't measure it (an older Host, or a folder it couldn't measure).
    /// </summary>
    public static (string Words, bool IsProblem)? SpaceWords(long? freeBytes, long? wantedBytes, string? destination)
    {
        if (freeBytes is not { } free || free < 0) return null;
        var where = string.IsNullOrWhiteSpace(destination) ? string.Empty : $" in {destination.Trim()}";
        if (wantedBytes is { } wanted && wanted > free)
            return ($"Not enough space: it needs {UiFormat.Bytes(wanted)}, and {UiFormat.Bytes(free)} is free{where}.", true);
        return ($"{UiFormat.Bytes(free)} free{where}", false);
    }
}
