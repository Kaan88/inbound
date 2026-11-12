namespace DownloadManager.Contracts;

/// <summary>Torrent IPC (T1, docs/plans/torrent-t1-design.md). Offered when the hello lists <c>torrents</c>.</summary>
public static class TorrentMessageTypes
{
    /// <summary><see cref="TorrentInspectRequest"/> → <see cref="TorrentInspectResult"/>.</summary>
    public const string Inspect = "torrent.inspect";
    public const string InspectResult = "torrent.inspectResult";
    /// <summary><see cref="TorrentAddRequest"/> → <see cref="TorrentAddResult"/>.</summary>
    public const string Add = "torrent.add";
    public const string AddResult = "torrent.addResult";
    /// <summary><see cref="TorrentSetFilesRequest"/> → <see cref="TorrentActionResult"/>.</summary>
    public const string SetFiles = "torrent.setFiles";
    /// <summary><see cref="TorrentFilesRequest"/> → <see cref="TorrentFilesResult"/>.</summary>
    public const string Files = "torrent.files";
    public const string FilesResult = "torrent.filesResult";
    public const string ActionResult = "torrent.actionResult";
    /// <summary>T2: <see cref="TorrentJobRequest"/> → <see cref="TorrentPeersResult"/>.</summary>
    public const string Peers = "torrent.peers";
    public const string PeersResult = "torrent.peersResult";
    /// <summary>T2: <see cref="TorrentJobRequest"/> → <see cref="TorrentTrackersResult"/>.</summary>
    public const string Trackers = "torrent.trackers";
    public const string TrackersResult = "torrent.trackersResult";
    /// <summary>T2: <see cref="TorrentJobRequest"/> → <see cref="TorrentPiecesResult"/>.</summary>
    public const string Pieces = "torrent.pieces";
    public const string PiecesResult = "torrent.piecesResult";
    /// <summary>T2: <see cref="TorrentSeedRequest"/> → <see cref="TorrentActionResult"/>.</summary>
    public const string Seed = "torrent.seed";

    /// <summary>T3: <see cref="TorrentJobRequest"/> → <see cref="TorrentOptionsResult"/>: the torrent's own limits, order and seeding goal.</summary>
    public const string Options = "torrent.options";
    public const string OptionsResult = "torrent.optionsResult";
    /// <summary>T3: <see cref="TorrentOptionsDto"/> → <see cref="TorrentOptionsResult"/>.</summary>
    public const string SetOptions = "torrent.setOptions";
    /// <summary>T3: <see cref="TorrentJobRequest"/> → <see cref="TorrentActionResult"/>: check the files on disk again.</summary>
    public const string Recheck = "torrent.recheck";
    /// <summary>T3: <see cref="TorrentJobRequest"/> → <see cref="TorrentActionResult"/>: ask the trackers (and DHT) for peers now.</summary>
    public const string Announce = "torrent.announce";
    /// <summary>T3: <see cref="TorrentJobRequest"/> → <see cref="TorrentDetailsResult"/>: the Overview's facts.</summary>
    public const string Details = "torrent.details";
    public const string DetailsResult = "torrent.detailsResult";
    /// <summary>T3: <see cref="TorrentWatchRequest"/> → <see cref="TorrentActionResult"/>: a client shows torrents now.</summary>
    public const string Watch = "torrent.watch";

    public static bool IsRequest(string? type) => type is Inspect or Add or SetFiles or Files or Peers or Trackers or Pieces or Seed or Network or ListEntries or Import
        or Options or SetOptions or Recheck or Announce or Details or Watch;
    /// <summary>T2: an empty request → <see cref="TorrentNetworkResult"/>, the port hint of Options › Torrents.</summary>
    public const string Network = "torrent.network";
    public const string NetworkResult = "torrent.networkResult";

    /// <summary><see cref="TorrentListEntriesRequest"/> → <see cref="TorrentListEntriesResult"/>: what Export list saves of each torrent.</summary>
    public const string ListEntries = "torrent.listEntries";
    public const string ListEntriesResult = "torrent.listEntriesResult";
    /// <summary><see cref="TorrentImportRequest"/> → <see cref="TorrentImportResult"/>: the torrents of an imported list.</summary>
    public const string Import = "torrent.import";
    public const string ImportResult = "torrent.importResult";

}

/// <summary>What every client is told about a torrent job.</summary>
public sealed class TorrentDto
{
    public string InfoHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool HasMetadata { get; set; }
    /// <summary>The file list is in and the download waits for a choice of files.</summary>
    public bool AwaitingFileChoice { get; set; }
    public int FileCount { get; set; }
    public int WantedCount { get; set; }
    public long TotalBytes { get; set; }
    public long WantedBytes { get; set; }
    public int Peers { get; set; }
    public int Seeds { get; set; }
    /// <summary>T2: the download is complete and the torrent is shared with other peers now.</summary>
    public bool Seeding { get; set; }
    public DateTimeOffset? SeedingSince { get; set; }
    /// <summary>Bytes sent to other peers over the torrent's life.</summary>
    public long Uploaded { get; set; }
    /// <summary>Uploaded against the downloaded (wanted) size.</summary>
    public double Ratio { get; set; }
    /// <summary>Bytes per second sent now, while it seeds.</summary>
    public double UploadRate { get; set; }
    /// <summary>T3: <c>magnet</c> or <c>file</c> (a .torrent file).</summary>
    public string Source { get; set; } = "magnet";
    /// <summary>T3: the worker is checking the files on disk now.</summary>
    public bool Checking { get; set; }
    /// <summary>T3: a check of the files was asked for and runs when the torrent starts.</summary>
    public bool RecheckPending { get; set; }
    /// <summary>T3: the torrent's own caps in bytes/s; null is none. The overall speed limit (Options › Connection) still
    /// covers them. A torrent's limits are changed with <c>torrent.setOptions</c>, never with the job's speed limit
    /// (<see cref="JobDto.CanChangeSpeedLimit"/> is false for a torrent and <c>job.update</c> refuses one).</summary>
    public long? DownloadLimit { get; set; }
    public long? UploadLimit { get; set; }
    /// <summary>T3: <c>global</c> (Options › Torrents), <c>ratio</c>, <c>hours</c> (<see cref="SeedGoalValue"/>) or <c>never</c>.</summary>
    public string SeedGoal { get; set; } = "global";
    public double? SeedGoalValue { get; set; }
    /// <summary>T3: downloads its pieces in order.</summary>
    public bool Sequential { get; set; }
}

public sealed class TorrentFileDto
{
    public int Index { get; set; }
    /// <summary>Relative to the job's destination, '/' separated: the name the file has once it is done.</summary>
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    /// <summary>False exactly when <see cref="Priority"/> is <c>skip</c>.</summary>
    public bool Wanted { get; set; }
    /// <summary>T3: <c>skip</c>, <c>low</c>, <c>normal</c> or <c>high</c>. An older Host leaves it out (read it as
    /// <c>normal</c> for a wanted file).</summary>
    public string? Priority { get; set; }
    /// <summary>Bytes of this file on disk so far; null when not known (the torrent is not running).</summary>
    public long? Done { get; set; }
}

/// <summary>T3: one file's priority, for <c>torrent.add</c> and <c>torrent.setFiles</c>.</summary>
public sealed class TorrentFilePriorityDto
{
    public int Index { get; set; }
    /// <summary><c>skip</c>, <c>low</c>, <c>normal</c> or <c>high</c>.</summary>
    public string Priority { get; set; } = "normal";
}

/// <summary>A magnet, or a .torrent file: a full path on the Host's machine, or an upload reference.</summary>
public sealed class TorrentInspectRequest
{
    public string Input { get; set; } = string.Empty;
    /// <summary>T3: the folder it would go to, for <see cref="TorrentInspectResult.FreeBytes"/>; null is the category
    /// folder, as an add without one.</summary>
    public string? Destination { get; set; }
    /// <summary>T10: in place of <see cref="Input"/>, the finished download whose file is a .torrent file
    /// (<see cref="JobDto.TorrentFileReady"/>). An older Host ignores it and reads <see cref="Input"/>.</summary>
    public Guid? FromJobId { get; set; }
}

public sealed class TorrentInspectResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary><c>magnet</c> or <c>torrentFile</c>.</summary>
    public string? Kind { get; set; }
    public string? InfoHash { get; set; }
    public string? Name { get; set; }
    /// <summary>Null for a magnet: its files are known only once peers send the metadata.</summary>
    public long? TotalBytes { get; set; }
    public List<TorrentFileDto>? Files { get; set; }
    /// <summary>A job with the same info hash is already in the list.</summary>
    public Guid? ExistingJobId { get; set; }
    /// <summary>T3: the folder <see cref="FreeBytes"/> was measured for.</summary>
    public string? Destination { get; set; }
    /// <summary>T3: free bytes at <see cref="Destination"/>; null when it couldn't be measured.</summary>
    public long? FreeBytes { get; set; }
    /// <summary>T3: the files don't fit in <see cref="FreeBytes"/> (all of them; an add with fewer may fit). In words.</summary>
    public string? SpaceProblem { get; set; }
}

public sealed class TorrentAddRequest
{
    public string Input { get; set; } = string.Empty;
    /// <summary>Null: the category folder (Save to), as for any download.</summary>
    public string? Destination { get; set; }
    public int? QueueId { get; set; }
    /// <summary><c>startNow</c>, <c>queue</c> or <c>later</c>, as <see cref="JobDto.StartMode"/>.</summary>
    public string StartMode { get; set; } = "startNow";
    /// <summary><c>all</c>, <c>chosen</c> (<see cref="FileIndexes"/>) or <c>askWhenReady</c> (a magnet: ask once the file list is in).</summary>
    public string Files { get; set; } = "all";
    /// <summary>The files for <c>chosen</c>. With <c>askWhenReady</c>, a choice to use by itself once the file list is in
    /// (asked for when the list does not have every file it names).</summary>
    public List<int>? FileIndexes { get; set; }
    public string? Description { get; set; }
    /// <summary>A .torrent file's bytes (base64), in place of <see cref="Input"/> (an imported list's torrent).</summary>
    public string? Data { get; set; }
    /// <summary>T3: priorities by file index, applied after <see cref="Files"/> (a file set to <c>skip</c> is left out, any
    /// other is wanted). Only for a torrent whose file list is known (a .torrent file). An older Host ignores it.</summary>
    public List<TorrentFilePriorityDto>? Priorities { get; set; }
    /// <summary>T3: the torrent's own options from the start; null fields are none / Options' choice.</summary>
    public TorrentOptionsDto? Options { get; set; }
    /// <summary>
    /// T10: in place of <see cref="Input"/> and <see cref="Data"/>, the finished download whose file is a .torrent file
    /// (<see cref="JobDto.TorrentFileReady"/>). The Host reads that file itself, and once the torrent is added, or was
    /// already in the list (<see cref="TorrentAddResult.Existing"/>), it removes that download and its file. On a failure
    /// both stay. An older Host ignores it and adds from <see cref="Input"/>, leaving the download in the list.
    /// </summary>
    public Guid? FromJobId { get; set; }
    /// <summary>
    /// T10: with <see cref="FromJobId"/>, only remove that download when its torrent is already in the list; nothing is
    /// ever added. When the torrent is not in the list (any more), the answer is an error and the download stays. Only
    /// sent with <see cref="FromJobId"/>, which only a Host that knows both offers (<see cref="JobDto.TorrentFileReady"/>).
    /// </summary>
    public bool OnlyIfListed { get; set; }
}

public sealed class TorrentAddResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>The same torrent was already in the list; <see cref="Job"/> is that one.</summary>
    public bool Existing { get; set; }
    public JobDto? Job { get; set; }
}

/// <summary>
/// The files to download. <see cref="FileIndexes"/> (when not empty) are the wanted ones and every other is left out, as
/// before T3; files that were wanted keep their priority. T3: <see cref="Priorities"/> then sets those files' priorities
/// (<c>skip</c> leaves one out) and leaves the rest as they are, so a request with only <see cref="Priorities"/> changes
/// just those files. A running torrent takes the change at once. At least one file must stay wanted.
/// </summary>
public sealed class TorrentSetFilesRequest
{
    public Guid JobId { get; set; }
    public List<int> FileIndexes { get; set; } = [];
    public List<TorrentFilePriorityDto>? Priorities { get; set; }
}

public sealed class TorrentFilesRequest
{
    public Guid JobId { get; set; }
}

public sealed class TorrentFilesResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<TorrentFileDto> Files { get; set; } = [];
}

public sealed class TorrentActionResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>T2: one torrent job, for <c>torrent.peers</c>, <c>torrent.trackers</c> and <c>torrent.pieces</c>.</summary>
public sealed class TorrentJobRequest
{
    public Guid JobId { get; set; }
}

/// <summary>T2: start (<see cref="On"/>) or stop seeding a completed torrent.</summary>
public sealed class TorrentSeedRequest
{
    public Guid JobId { get; set; }
    public bool On { get; set; }
}

public sealed class TorrentPeerDto
{
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Client { get; set; } = string.Empty;
    /// <summary>Any of <c>seed</c>, <c>incoming</c>, <c>outgoing</c>, <c>encrypted</c>, <c>utp</c>, <c>interested</c>,
    /// <c>peerInterested</c>, <c>choked</c>, <c>chokedByPeer</c>, <c>snubbed</c>.</summary>
    public List<string> Flags { get; set; } = [];
    public long DownloadRate { get; set; }
    public long UploadRate { get; set; }
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }
    /// <summary>How much of the torrent the peer has, 0 to 1.</summary>
    public double Progress { get; set; }
}

public sealed class TorrentTrackerDto
{
    public string Url { get; set; } = string.Empty;
    public int Tier { get; set; }
    /// <summary><c>notContacted</c>, <c>updating</c>, <c>working</c> or <c>error</c>.</summary>
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    /// <summary>-1 when the tracker did not say.</summary>
    public int Seeds { get; set; }
    public int Peers { get; set; }
    /// <summary>T3: seconds to the next announce (0: due now or under way); -1 when none is planned or not known.</summary>
    public long NextAnnounce { get; set; } = -1;
}

/// <summary>
/// T3: a torrent's own options. In <c>torrent.setOptions</c> a null field is left as it is; a limit of 0 removes it.
/// In a reply a null limit is none. The limits are bytes/s up to 2,147,483,647 and live within the overall speed limit.
/// </summary>
public sealed class TorrentOptionsDto
{
    public Guid JobId { get; set; }
    public long? DownloadLimit { get; set; }
    public long? UploadLimit { get; set; }
    /// <summary><c>global</c> (Options › Torrents), <c>ratio</c>, <c>hours</c> or <c>never</c>.</summary>
    public string? SeedGoal { get; set; }
    /// <summary>The ratio (0.01 to 100) or hours (0.1 to 8760) of <see cref="SeedGoal"/>.</summary>
    public double? SeedGoalValue { get; set; }
    /// <summary>Download the pieces in order (for watching while it downloads) instead of rarest first.</summary>
    public bool? Sequential { get; set; }
}

public sealed class TorrentOptionsResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public TorrentOptionsDto? Options { get; set; }
    /// <summary>The torrent is on the torrent engine now and took the options at once; otherwise they apply when it starts.</summary>
    public bool Running { get; set; }
}

/// <summary>
/// T3: the Overview tab's facts. What the Host keeps is always there; the rest (<see cref="Running"/> true) only while the
/// torrent is on the torrent engine. Byte counts are this run's (since the torrent was last added to the engine).
/// </summary>
public sealed class TorrentDetailsResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Running { get; set; }
    /// <summary><c>magnet</c> or <c>file</c>.</summary>
    public string Source { get; set; } = "magnet";
    public string InfoHash { get; set; } = string.Empty;
    /// <summary>The version 2 info hash (64 hex digits) of a hybrid or v2 torrent; null when it has none or isn't known.</summary>
    public string? InfoHashV2 { get; set; }
    /// <summary>A private torrent: DHT, PEX and local peers are always off for it. Null while not known.</summary>
    public bool? Private { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public string DestinationDirectory { get; set; } = string.Empty;
    public int PieceCount { get; set; }
    public long PieceLength { get; set; }
    /// <summary>Bytes downloaded twice or thrown away for a failed check.</summary>
    public long? Wasted { get; set; }
    /// <summary>Pieces that failed their check.</summary>
    public long? HashFailures { get; set; }
    public int? Connections { get; set; }
    public int? ConnectionsLimit { get; set; }
    /// <summary>How many full copies the connected peers have together.</summary>
    public double? DistributedCopies { get; set; }
    /// <summary>Seconds to the next announce to the trackers.</summary>
    public long? NextAnnounce { get; set; }
    /// <summary>Peer exchange is on for this torrent.</summary>
    public bool? Pex { get; set; }
    /// <summary>DHT nodes the torrent engine knows; -1 with DHT off.</summary>
    public long? DhtNodes { get; set; }
    public TorrentOptionsDto Options { get; set; } = new();
}

/// <summary>
/// T3: a client shows torrents now (<see cref="On"/>): a list with a torrent in view, or a torrent's details. Progress
/// comes every second while one does (or the overall speed limit needs fresh rates), else every 5 seconds. A watch
/// lasts <see cref="LeaseSeconds"/> (default 60, 10 to 300) unless renewed, and ends when the client disconnects or
/// sends <see cref="On"/> false.
/// </summary>
public sealed class TorrentWatchRequest
{
    public bool On { get; set; } = true;
    public int? LeaseSeconds { get; set; }
}

/// <summary>When <see cref="Running"/> is false the torrent is not on the torrent engine now (stopped, or finished and
/// not seeding) and the lists are empty.</summary>
public sealed class TorrentPeersResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Running { get; set; }
    public List<TorrentPeerDto> Peers { get; set; } = [];
}

public sealed class TorrentTrackersResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Running { get; set; }
    public List<TorrentTrackerDto> Trackers { get; set; } = [];
}

public sealed class TorrentPiecesResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Running { get; set; }
    public int Count { get; set; }
    public long PieceLength { get; set; }
    public int Have { get; set; }
    /// <summary>First and last piece (inclusive) of each run the torrent has; null when <see cref="Bitmap"/> is used.</summary>
    public List<int[]>? Ranges { get; set; }
    /// <summary>Base64, one bit per piece, piece 0 in the highest bit of the first byte; for a scattered torrent.</summary>
    public string? Bitmap { get; set; }
}

/// <summary>The ports a user may give torrents, and the one chosen when torrents are first turned on.</summary>
public static class TorrentListenPorts
{
    public const int Min = 1024;
    public const int Max = 65535;
    /// <summary>
    /// Where a port is picked: below Windows' dynamic range (49152 and up, where outgoing connections take their ports
    /// and Hyper-V, WSL and Docker reserve blocks) and above the ports well-known services use.
    /// </summary>
    public const int PickMin = 20000;
    public const int PickMax = 40000;

    public static bool IsValid(int port) => port is >= Min and <= Max;

    /// <summary>
    /// A random port in <see cref="PickMin"/>..<see cref="PickMax"/> that <paramref name="usable"/> accepts, tried a few
    /// times; the last one tried when none passes (the engine then listens on any free port for that run).
    /// </summary>
    public static int Pick(Func<int, bool>? usable = null, Random? random = null)
    {
        random ??= Random.Shared;
        var port = 0;
        for (var tries = 0; tries < 20; tries++)
        {
            port = random.Next(PickMin, PickMax + 1);
            if (usable is null || usable(port)) break;
        }

        return port;
    }
}

/// <summary>
/// What the Host knows of the torrent engine's listening since it started (it forgets on restart). Empty while no
/// torrent has run yet.
/// </summary>
public sealed class TorrentNetworkResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>The engine ran at least once since the Host started.</summary>
    public bool Ran { get; set; }
    /// <summary>The engine runs now.</summary>
    public bool Running { get; set; }
    /// <summary>The port it listened on; 0 when it never said.</summary>
    public int Port { get; set; }
    /// <summary>Why the port could not be opened, when no address could be used.</summary>
    public string? ListenError { get; set; }
    /// <summary>The port the router forwarded through UPnP or NAT-PMP, when it did.</summary>
    public int? MappedPort { get; set; }
    /// <summary>Another peer connected in: this PC was reachable from outside on its last run.</summary>
    public bool IncomingSeen { get; set; }
    /// <summary>The port from Options that couldn't be opened, so the engine listened on <see cref="Port"/> instead.</summary>
    public int? UnusablePort { get; set; }
    /// <summary>T3: DHT nodes the running engine knows (the status chip); null while it doesn't run, -1 with DHT off.</summary>
    public long? DhtNodes { get; set; }
}

public sealed class TorrentListEntriesRequest
{
    /// <summary>The page after this job id (<see cref="TorrentListEntriesResult.Next"/>); null for the first. An older Host
    /// ignores it and gives every torrent at once.</summary>
    public Guid? After { get; set; }
}

public sealed class TorrentListEntriesResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>By job id: the torrent jobs the caller may see, one page of them.</summary>
    public Dictionary<Guid, TorrentListEntry> Entries { get; set; } = [];
    /// <summary>The <see cref="TorrentListEntriesRequest.After"/> for the next page; null on the last one.</summary>
    public Guid? Next { get; set; }
}

public sealed class TorrentImportRequest
{
    /// <summary>The list's torrent entries (<see cref="JobListFile.IsTorrent"/>).</summary>
    public List<JobListEntry> Entries { get; set; } = [];
    /// <summary>Overrides each entry's start mode when set.</summary>
    public string? StartMode { get; set; }
}

public sealed class TorrentImportResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Added { get; set; }
    /// <summary>Not added, and nothing wrong: already in the list, or torrents are off.</summary>
    public int Skipped { get; set; }
    public string? SkipReason { get; set; }
    public int Failed { get; set; }
    public string? FirstError { get; set; }
}
