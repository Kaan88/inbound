using System.Text.Json.Serialization;

namespace DownloadManager.Core;

/// <summary>
/// What makes a job a torrent (T1, docs/plans/torrent-t1-design.md). Null on every other job. The file list itself is
/// kept apart (<see cref="ITorrentStore"/>), because a torrent can have 100,000 files and the job is saved often.
/// </summary>
public sealed class TorrentJobInfo
{
    /// <summary>Version 1 info hash, lower-case hex.</summary>
    public string InfoHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>The magnet as given; null when the job came from a .torrent file.</summary>
    public string? Magnet { get; set; }
    public bool HasMetadata { get; set; }
    public int FileCount { get; set; }
    public int WantedCount { get; set; }
    /// <summary>Every file's size together (pad files left out).</summary>
    public long TotalBytes { get; set; }
    public long WantedBytes { get; set; }
    /// <summary>Ask which files to download once the metadata is in (a multi-file magnet added with "choose files").</summary>
    public bool ChooseFilesWhenReady { get; set; }
    /// <summary>
    /// Files to choose by themselves when the file list comes (a magnet from an imported list, saved with its choice);
    /// null otherwise. Used when every index is in the list, else the user is asked.
    /// </summary>
    public List<int>? PendingFileIndexes { get; set; }
    /// <summary>The metadata is in and the transfer holds until the user chooses files (<c>torrent.setFiles</c>).</summary>
    public bool AwaitingFileChoice { get; set; }
    /// <summary>Added to every file name while it downloads: <c>.&lt;job id&gt;.part</c>.</summary>
    public string PartSuffix { get; set; } = string.Empty;
    /// <summary>Replaces the torrent's top-level name when that name was taken in the destination when it finished
    /// (chosen then, not on add); null otherwise.</summary>
    public string? RootName { get; set; }
    /// <summary>The worker gave the files their own names: a later add must not give them part names again.</summary>
    public bool Finalized { get; set; }

    /// <summary>
    /// Saved just before the worker is told to give the files their own names, cleared once <see cref="Finalized"/> is
    /// saved or the worker refused. Still set at the next start, the renames may have run (all, some or none), so the
    /// start finishes them instead of downloading the renamed files again under their part names.
    /// </summary>
    public bool Finalizing { get; set; }
    /// <summary>The top-level name the interrupted finalize gave (<see cref="RootName"/> to be); null keeps the torrent's own.</summary>
    public string? FinalizingRootName { get; set; }

    /// <summary>T2: the download finished and the torrent is shared with other peers now (the job itself is completed).
    /// Kept across restarts; cleared when the seeding policy or the user stops it.</summary>
    public bool Seeding { get; set; }
    /// <summary>When the current seeding began; the time limit counts from here.</summary>
    public DateTimeOffset? SeedingSince { get; set; }
    /// <summary>Bytes sent to other peers over the torrent's life, as last reported.</summary>
    public long Uploaded { get; set; }

    /// <summary>T3: this torrent's own download cap in bytes/s; null is none. The overall limit still covers it.</summary>
    public long? DownloadLimit { get; set; }
    /// <summary>T3: this torrent's own upload cap in bytes/s; null is none. The overall limit still covers it.</summary>
    public long? UploadLimit { get; set; }
    /// <summary>T3: when this torrent stops seeding (<see cref="TorrentSeedGoals"/>); null follows Options › Torrents.</summary>
    public string? SeedGoal { get; set; }
    /// <summary>The ratio or the hours of <see cref="SeedGoal"/>; unused for the others.</summary>
    public double SeedGoalValue { get; set; }
    /// <summary>T3: download the pieces in order (for watching while it downloads) instead of rarest first.</summary>
    public bool Sequential { get; set; }
    /// <summary>T3: a check of the files on disk was asked for while the torrent was not on the worker; done when it starts.</summary>
    public bool RecheckOnStart { get; set; }
    /// <summary>Why seeding last stopped by itself with a fault (a file check that found damage, say); cleared when it seeds again.</summary>
    public string? SeedingStoppedNote { get; set; }

    [JsonIgnore] public int Peers { get; set; }
    [JsonIgnore] public int Seeds { get; set; }
    [JsonIgnore] public double UploadRate { get; set; }
    /// <summary>T3: the state the worker reported last while the torrent was on it (<c>checking</c>, ...); null otherwise.</summary>
    [JsonIgnore] public string? WorkerState { get; set; }

    /// <summary>Uploaded against what was downloaded (the wanted size).</summary>
    [JsonIgnore] public double Ratio => WantedBytes > 0 ? (double)Uploaded / WantedBytes : 0;

    public TorrentJobInfo Clone()
    {
        var clone = (TorrentJobInfo)MemberwiseClone();
        clone.PendingFileIndexes = PendingFileIndexes is null ? null : [.. PendingFileIndexes];
        return clone;
    }

    public static string PartSuffixFor(Guid jobId) => $".{jobId:N}.part";
}

/// <summary>
/// One file of a torrent. <see cref="Path"/> is relative to the job's destination and is the file's final name.
/// <see cref="Wanted"/> is false exactly when <see cref="Priority"/> is <see cref="TorrentFilePriority.Skip"/>.
/// </summary>
public sealed record TorrentFile(int Index, string Path, long Size, bool Wanted)
{
    /// <summary>T3: a wanted file's priority when it is not Normal; null otherwise (and in every list saved before T3).</summary>
    public TorrentFilePriority? Level { get; init; }

    /// <summary>Skip for a file left out, else its level (Normal when none was given).</summary>
    [JsonIgnore]
    public TorrentFilePriority Priority => !Wanted ? TorrentFilePriority.Skip
        : Level is { } level && level != TorrentFilePriority.Skip ? level : TorrentFilePriority.Normal;

    /// <summary>The file with <paramref name="priority"/>: Skip leaves it out, any other wants it.</summary>
    public TorrentFile WithPriority(TorrentFilePriority priority)
        => this with
        {
            Wanted = priority != TorrentFilePriority.Skip,
            Level = priority is TorrentFilePriority.Skip or TorrentFilePriority.Normal ? null : priority,
        };
}

/// <summary>T3: Options › Torrents' encryption choice and the worker's word for it.</summary>
public static class TorrentEncryptionModes
{
    /// <summary>Encrypted when the peer can, plain otherwise.</summary>
    public const string Prefer = "prefer";
    /// <summary>Encrypted only: peers that can't encrypt are left out.</summary>
    public const string Require = "require";
    /// <summary>Plain connections only.</summary>
    public const string Off = "off";

    public static bool IsKnown(string? mode) => mode is Prefer or Require or Off;

    /// <summary>The worker's <c>encryption</c>: <c>enabled</c>, <c>forced</c> or <c>disabled</c>.</summary>
    public static string WorkerWord(string? mode) => mode switch
    {
        Require => "forced",
        Off => "disabled",
        _ => "enabled",
    };
}

/// <summary>T3: a file's priority, valued as libtorrent's (0, 1, 4 and 7).</summary>
public enum TorrentFilePriority
{
    Skip = 0,
    Low = 1,
    Normal = 4,
    High = 7,
}

public static class TorrentFilePriorities
{
    /// <summary><c>skip</c>, <c>low</c>, <c>normal</c> or <c>high</c>.</summary>
    public static string Text(TorrentFilePriority priority) => priority switch
    {
        TorrentFilePriority.Skip => "skip",
        TorrentFilePriority.Low => "low",
        TorrentFilePriority.High => "high",
        _ => "normal",
    };

    public static TorrentFilePriority? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "skip" => TorrentFilePriority.Skip,
        "low" => TorrentFilePriority.Low,
        "normal" => TorrentFilePriority.Normal,
        "high" => TorrentFilePriority.High,
        _ => null,
    };
}

/// <summary>T3: a torrent's own seeding goal (<see cref="TorrentJobInfo.SeedGoal"/>).</summary>
public static class TorrentSeedGoals
{
    public const string Global = "global";
    public const string Ratio = "ratio";
    public const string Hours = "hours";
    public const string Never = "never";

    public static bool IsKnown(string? goal) => goal is Global or Ratio or Hours or Never;

    /// <summary>Whether a finished torrent with this goal seeds: its own goal, or Options' choice when it has none.</summary>
    public static bool Seeds(TorrentJobInfo info, bool seedAfterDownload) => info.SeedGoal switch
    {
        Never => false,
        Ratio or Hours => true,
        _ => seedAfterDownload,
    };

    /// <summary>The seeding limits for this torrent: its own goal, or <paramref name="global"/>.</summary>
    public static TorrentSeedPolicy PolicyFor(TorrentJobInfo info, TorrentSeedPolicy global) => info.SeedGoal switch
    {
        Ratio => new TorrentSeedPolicy(Math.Max(0, info.SeedGoalValue), TimeSpan.Zero),
        Hours => new TorrentSeedPolicy(0, TimeSpan.FromHours(Math.Max(0, info.SeedGoalValue))),
        _ => global,
    };
}

/// <summary>Where a magnet or .torrent file goes when a provider account (TorBox) could take it too.</summary>
public static class TorrentRoutes
{
    public const string Ask = "ask";
    public const string ThisPc = "thisPc";
    public const string Provider = "provider";

    public static bool IsKnown(string? value) => value is Ask or ThisPc or Provider;
}

/// <summary>Durable per-torrent data beside the job: the metadata, the file list and the worker's resume data.</summary>
public interface ITorrentStore
{
    Task SaveMetadataAsync(Guid jobId, byte[] torrent, CancellationToken cancellationToken);
    Task<byte[]?> LoadMetadataAsync(Guid jobId, CancellationToken cancellationToken);
    Task SaveResumeAsync(Guid jobId, byte[] resume, CancellationToken cancellationToken);
    Task<byte[]?> LoadResumeAsync(Guid jobId, CancellationToken cancellationToken);
    Task SaveFilesAsync(Guid jobId, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken);
    Task<IReadOnlyList<TorrentFile>?> LoadFilesAsync(Guid jobId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken);
}

/// <summary>Whether this process may run torrents at all: the worker is there and the kill switch is not set.</summary>
public static class TorrentAvailability
{
    /// <summary><c>DOWNLOADMANAGER_TORRENTS=off</c> wins over the setting: no torrent command, no worker.</summary>
    public const string KillSwitchVariable = "DOWNLOADMANAGER_TORRENTS";

    public static bool KillSwitchSet(Func<string, string?>? environment = null)
        => string.Equals((environment ?? Environment.GetEnvironmentVariable)(KillSwitchVariable)?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a torrent run tells the service about its job. The engine never changes the job itself.</summary>
public interface ITorrentJobSink
{
    /// <summary>The metadata is known: the file list (already saved with <see cref="ITorrentStore.SaveFilesAsync"/>) and
    /// whether the transfer now waits for the user to choose files.</summary>
    Task MetadataArrivedAsync(Guid jobId, string name, IReadOnlyList<TorrentFile> files, bool awaitingFileChoice, CancellationToken cancellationToken);

    /// <summary>Last reported peers, seeds and upload rate (bytes/s) of a downloading torrent; shown with the next progress
    /// event, not raised on its own. The upload rate counts toward the overall speed limit (T2).</summary>
    void SwarmChanged(Guid jobId, int peers, int seeds, double uploadRate);

    /// <summary>The top-level name the finished torrent gets in <paramref name="destination"/>: <paramref name="topName"/>
    /// when nothing on disk or in the list has it, else a free one like it (its extension kept). For a multi-file torrent
    /// the folder (where its files download) is taken only when one of its <paramref name="files"/> is already there.</summary>
    string ChooseFinalTopName(Guid jobId, string destination, string topName, IReadOnlyList<TorrentFile> files);

    /// <summary>The files now carry their own names (<paramref name="files"/>, with <paramref name="rootName"/> applied).
    /// Clears <see cref="TorrentJobInfo.Finalizing"/>.</summary>
    Task FinalizedAsync(Guid jobId, string? rootName, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <see cref="TorrentJobInfo.Finalizing"/> (with <paramref name="rootName"/>) before the worker renames the files,
    /// or clears it when <paramref name="finalizing"/> is false (the worker refused). Must be on disk when it returns.
    /// </summary>
    Task FinalizingAsync(Guid jobId, string? rootName, bool finalizing, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Why <paramref name="neededBytes"/> more (the wanted files less what is already on disk) don't fit at
    /// <paramref name="destination"/>, in words, or null when they do or the room can't be measured. Asked once, when a
    /// torrent's file list first comes (a magnet's size is unknown when it is added).
    /// </summary>
    Task<string?> RoomProblemAsync(Guid jobId, string destination, long neededBytes, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>A seeding torrent's latest totals (T2).</summary>
    void SeedingProgress(Guid jobId, long uploaded, double uploadRate, int peers, int seeds);

    /// <summary>The torrent stopped seeding and left the worker: the policy's limit, the user, or an error (<paramref name="reason"/>).</summary>
    Task SeedingStoppedAsync(Guid jobId, TorrentSeedingEnd reason, string message);

    /// <summary>T3: the worker took the add with the file check <see cref="TorrentJobInfo.RecheckOnStart"/> asked for.</summary>
    void RecheckStarted(Guid jobId) { }

    /// <summary>T3: the state the worker reported last (<c>checking</c>, <c>downloading</c>, <c>seeding</c>, ...).</summary>
    void TorrentStateChanged(Guid jobId, string state) { }
}

public enum TorrentSeedingEnd
{
    /// <summary>The ratio or time limit was reached.</summary>
    LimitReached,
    /// <summary>The user (or the scheduler, Stop All, a quota or turning torrents off) stopped it.</summary>
    Stopped,
    Failed,
}

/// <summary>
/// When seeding stops (T2; owner default: ratio 1.0 or 48 hours, whichever comes first). A zero is no limit for that one;
/// both zero is refused by the settings, so seeding always ends by itself.
/// </summary>
public sealed record TorrentSeedPolicy(double Ratio, TimeSpan Duration)
{
    public static TorrentSeedPolicy Default { get; } = new(1.0, TimeSpan.FromHours(48));

    public static TorrentSeedPolicy From(double ratio, double hours)
        => new(Math.Max(0, ratio), TimeSpan.FromHours(Math.Max(0, hours)));

    public bool RatioReached(long uploaded, long wanted) => Ratio > 0 && wanted > 0 && uploaded >= Ratio * wanted;

    /// <summary>Time left before the time limit; null when there is none.</summary>
    public TimeSpan? TimeLeft(DateTimeOffset since, DateTimeOffset now)
        => Duration > TimeSpan.Zero ? since + Duration - now : null;
}

public sealed record TorrentPeerInfo(string Address, int Port, string Client, IReadOnlyList<string> Flags,
    long DownloadRate, long UploadRate, long Downloaded, long Uploaded, double Progress);

/// <summary><see cref="Status"/>: <c>notContacted</c>, <c>updating</c>, <c>working</c> or <c>error</c>. Seeds and peers are
/// -1 when the tracker did not say.</summary>
public sealed record TorrentTrackerInfo(string Url, int Tier, string Status, string Message, int Seeds, int Peers)
{
    /// <summary>T3: seconds to the next announce (0: due now or under way); -1 when none is planned or the worker is older.</summary>
    public long NextAnnounce { get; init; } = -1;
}

/// <summary>T3: what came of a command for one torrent.</summary>
public enum TorrentActionOutcome
{
    Applied,
    /// <summary>The torrent is not on the worker now.</summary>
    NotRunning,
    /// <summary>The worker is older than the command.</summary>
    OlderWorker,
    /// <summary>The worker refused it; <see cref="TorrentActionReply.Message"/> says why.</summary>
    Refused,
}

public sealed record TorrentActionReply(TorrentActionOutcome Outcome, string? Message = null)
{
    public static TorrentActionReply Applied { get; } = new(TorrentActionOutcome.Applied);
    public static TorrentActionReply NotRunning { get; } = new(TorrentActionOutcome.NotRunning);
    public static TorrentActionReply OlderWorker { get; } = new(TorrentActionOutcome.OlderWorker);
}

/// <summary>T3: what the worker knows of a torrent that it reports cheaply (the Overview tab). Counts are bytes unless
/// named otherwise; <see cref="DhtNodes"/> is -1 while DHT is off.</summary>
public sealed record TorrentDetails(string InfoHashV1, string InfoHashV2, bool Private, long Wasted, long FailedBytes,
    long HashFailures, int Pieces, long PieceLength, int Connections, int ConnectionsLimit, long DownloadLimit, long UploadLimit,
    bool Sequential, bool Pex, double DistributedCopies, long NextAnnounce, long AddedAt, long ActiveSeconds, long DhtNodes);

/// <summary>
/// T3: Options › Torrents' choices for finding peers and how many connections and seeds run (sent to the worker when it
/// starts and again when they change). <see cref="Encryption"/> is the worker's word: <c>enabled</c>, <c>forced</c> or
/// <c>disabled</c>. Zero is "no limit of our own" for the three counts.
/// </summary>
public sealed record TorrentPeerSettings(bool Dht, bool Pex, bool Lsd, string Encryption, int SeedsAtOnce,
    int ConnectionsPerTorrent, int MaxConnections, TorrentProxy? Proxy)
{
    public static TorrentPeerSettings Default { get; } = new(true, true, false, "enabled", 0, 0, 0, null);

    /// <summary>Torrents are set to use a proxy that can't be used (in words): the worker then does not start at all.</summary>
    public string? ProxyProblem { get; init; }
}

/// <summary>T3: the proxy torrents use. <see cref="Type"/>: <c>http</c> or <c>socks5</c> (the Host sends no <c>socks4</c>). The password is
/// never part of <see cref="ToString"/>.</summary>
public sealed record TorrentProxy(string Type, string Host, int Port, string? Username, string? Password)
{
    public override string ToString() => $"{Type} proxy";
}

/// <summary>Which pieces the torrent has: <see cref="Ranges"/> (first and last piece of each run, inclusive) or, for a
/// scattered torrent, <see cref="Bitmap"/> (base64, one bit per piece, piece 0 in the highest bit of the first byte).</summary>
public sealed record TorrentPieceSummary(int Count, long PieceLength, int Have, IReadOnlyList<int[]>? Ranges, string? Bitmap);

public enum TorrentFileChoiceOutcome
{
    Applied,
    /// <summary>The torrent is not on the worker now; the choice applies when it starts.</summary>
    NotRunning,
    /// <summary>The torrent is giving its files their names; the choice can't change now.</summary>
    Finishing,
}

/// <summary>The torrent engine as the service sees it beyond <see cref="IDownloadEngine"/>.</summary>
public interface ITorrentDownloadEngine : IDownloadEngine
{
    void SetSink(ITorrentJobSink sink);

    /// <summary>Whether the job's torrent is giving its finished files their names now.</summary>
    bool IsFinishing(Guid jobId);

    /// <summary>Sends a new choice of files to the job's running torrent.</summary>
    Task<TorrentFileChoiceOutcome> ApplyFileChoiceAsync(Guid jobId, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken);

    /// <summary>Bytes of each file on disk so far, by worker file index, while the job's torrent is on the worker; null
    /// when it is not, or when the worker can't say (an older worker).</summary>
    Task<IReadOnlyList<long>?> GetFileProgressAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Whether the worker process is running now (the status-bar chip).</summary>
    bool WorkerRunning { get; }

    event Action? WorkerRunningChanged;

    /// <summary>T2: puts a completed, finalized torrent on the worker to seed until <see cref="UpdateSeedPolicy"/>'s limits
    /// or <see cref="StopSeedingAsync"/>. Null when it started (or already seeds), else why not.</summary>
    Task<string?> StartSeedingAsync(DownloadJob job, CancellationToken cancellationToken);

    /// <summary>Stops the job's seeding (its resume data saved) and takes it off the worker; nothing when it does not seed.</summary>
    Task StopSeedingAsync(Guid jobId);

    bool IsSeeding(Guid jobId);

    void UpdateSeedPolicy(TorrentSeedPolicy policy);

    /// <summary>T2: the download and upload caps of every torrent together (null or 0: none), sent to the worker in one
    /// command when it runs, else when it starts. The service gives them their shares of the overall speed limit.</summary>
    void UpdateSpeedLimits(long? download, long? upload);

    /// <summary>The torrent's peers, trackers or pieces while it is on the worker (downloading or seeding); null when it
    /// is not, or when the worker is older than these commands.</summary>
    Task<IReadOnlyList<TorrentPeerInfo>?> GetPeersAsync(Guid jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TorrentTrackerInfo>?> GetTrackersAsync(Guid jobId, CancellationToken cancellationToken);
    Task<TorrentPieceSummary?> GetPiecesAsync(Guid jobId, CancellationToken cancellationToken);
    /// <summary>T2: the port to listen on and whether to ask the router for it; applied now when the worker runs.</summary>
    void UpdateListen(int port, bool upnp);

    /// <summary>T2: what the worker reported of its listening since the Host started.</summary>
    TorrentNetworkStatus Network { get; }

    /// <summary>T3: checks the pieces on disk of the job's torrent again, while it is on the worker.</summary>
    Task<TorrentActionReply> RecheckAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>T3: announces the job's torrent to its trackers (and the DHT when it is on) now.</summary>
    Task<TorrentActionReply> AnnounceAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>T3: the job's own limits, download order and seeding goal (from <paramref name="info"/>), applied now when
    /// it is on the worker; the next add carries them anyway.</summary>
    Task<TorrentActionReply> ApplyOptionsAsync(Guid jobId, TorrentJobInfo info, CancellationToken cancellationToken);

    /// <summary>T3: the Overview's facts while the torrent is on the worker; null when it is not, or the worker is older.</summary>
    Task<TorrentDetails?> GetDetailsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>T3: the DHT nodes the running worker knows; null while it does not run (or is older), -1 with DHT off.</summary>
    Task<long?> GetDhtNodesAsync(CancellationToken cancellationToken);

    /// <summary>T3: Options › Torrents' peer, connection and proxy choices; applied now when the worker runs.</summary>
    void UpdatePeerSettings(TorrentPeerSettings settings);

    /// <summary>T3: whether someone looks at torrents (or the speed budget needs fresh rates): progress comes every second
    /// then, else every 5 seconds. It returns at once (the Host calls it under a lock, so calls keep their order) and
    /// the last call wins.</summary>
    void SetProgressWatched(bool watched);
}

/// <summary>What the torrent worker reported of its listening (the port hint). <see cref="Ran"/> false: no run yet.</summary>
public sealed record TorrentNetworkStatus(bool Ran, int Port, string? ListenError, int? MappedPort, bool IncomingSeen)
{
    public static TorrentNetworkStatus None { get; } = new(false, 0, null, null, false);

    /// <summary>The port from Options that couldn't be opened on any address; <see cref="Port"/> is the one used instead.</summary>
    public int? UnusablePort { get; init; }
}

/// <summary>
/// Where a torrent's files end up. A file's <see cref="TorrentFile.Path"/> separates folders with '/'. A backslash counts
/// as one too: libtorrent joins a path with it on Windows (lists saved before the worker's paths were normalised hold it),
/// and libtorrent never leaves either character inside one name.
/// </summary>
public static class TorrentLayout
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>A path as the worker reports it, with '/' between its folders.</summary>
    public static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>A single file's own path, or the top folder of a multi-file torrent (named after the torrent).</summary>
    public static string FinalPathOf(string destination, IReadOnlyList<TorrentFile> files, string fallbackName)
        => Path.Combine(destination, TopNameOf(files, fallbackName));

    /// <summary>The top-level name: a single file's name, or a multi-file torrent's folder.</summary>
    public static string TopNameOf(IReadOnlyList<TorrentFile> files, string fallbackName)
    {
        if (files.Count == 0) return fallbackName;
        var first = files[0].Path;
        var slash = first.IndexOfAny(Separators);
        return slash > 0 ? first[..slash] : first;
    }

    /// <summary>The files with their top-level name replaced by <paramref name="top"/>.</summary>
    public static List<TorrentFile> WithTopName(IReadOnlyList<TorrentFile> files, string top)
        => files.Select(file =>
        {
            var path = NormalizePath(file.Path);
            var slash = path.IndexOf('/');
            return file with { Path = slash > 0 ? top + path[slash..] : top };
        }).ToList();

    /// <summary>libtorrent's priority per worker index: the file's <see cref="TorrentFile.Priority"/> (1, 4 or 7) when it
    /// is wanted, 0 otherwise (pad files included).</summary>
    public static int[] PrioritiesOf(IReadOnlyList<TorrentFile> files)
    {
        var length = files.Count == 0 ? 0 : files.Max(file => file.Index) + 1;
        var priorities = new int[length];
        foreach (var file in files) priorities[file.Index] = (int)file.Priority;
        return priorities;
    }

    /// <summary>The file on disk while it downloads (<paramref name="partSuffix"/> set) or once done.</summary>
    public static string DiskPathOf(string destination, TorrentFile file, string? partSuffix = null)
        => Path.Combine(destination, NormalizePath(file.Path).Replace('/', Path.DirectorySeparatorChar)) + (partSuffix ?? string.Empty);

    /// <summary>
    /// libtorrent's hidden store for the pieces a torrent shares with files left out: <c>.&lt;info hash&gt;.parts</c> in the
    /// save folder itself. Null unless <paramref name="infoHash"/> is a v1 hash (40 hex digits), so it never names anything else.
    /// </summary>
    public static string? PartsFileOf(string destination, string? infoHash)
    {
        if (string.IsNullOrEmpty(destination) || infoHash is not { Length: 40 } || !infoHash.All(char.IsAsciiHexDigit)) return null;
        return Path.Combine(destination, $".{infoHash.ToLowerInvariant()}.parts");
    }

    /// <summary>
    /// Deletes <see cref="PartsFileOf"/> when it is there, and nothing else. libtorrent lets go of it a moment after the
    /// torrent left the worker, so a file still in use is tried again for about two seconds. True when it is gone.
    /// </summary>
    public static async Task<bool> DeletePartsFileAsync(string destination, string? infoHash, CancellationToken cancellationToken = default,
        Func<bool>? stillOurs = null)
    {
        if (PartsFileOf(destination, infoHash) is not { } path) return true;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Another job of the same torrent into the same folder uses the same file: it is that job's now.
                if (stillOurs is not null && !stillOurs()) return true;
                if (!File.Exists(path)) return true;
                File.Delete(path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 20) return false;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
}
