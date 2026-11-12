using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime;

/// <summary><c>torrent.inspect</c>, <c>torrent.add</c>, <c>torrent.setFiles</c> and <c>torrent.files</c> (T1, design section 5).</summary>
public sealed partial class DownloadManagerService
{
    private const string TorrentsOffText = "Torrents are turned off. Turn them on in Options > Torrents.";
    private const string TorrentsUnavailableText = "Torrents are not available in this installation.";
    private const string ChosenFileMissingText = "A chosen file is not in this torrent.";

    /// <summary>The refusal for a .torrent path on another computer or a Windows device.</summary>
    public const string TorrentNetworkPathText = "Only .torrent files on a drive of this computer can be added.";

    /// <summary>Turns an upload's reference (<c>upload:…</c>) into its file here; null for any other text. Set by the Host where uploads exist.</summary>
    public Func<string, string?>? ResolveTorrentUpload { get; set; }

    /// <summary>A magnet or a .torrent file, read and checked: what an add would make of it.</summary>
    private sealed record TorrentInput(string Kind, string InfoHash, string Name, string Magnet, byte[]? Metadata, TorrentMetadata? Parsed);

    private string? TorrentsRefusal()
        => !TorrentsAvailable ? TorrentsUnavailableText : !TorrentsOn ? TorrentsOffText : null;

    /// <summary>The refusal for a torrent from another device that names an address of this PC or its network.</summary>
    public const string TorrentLocalAddressText =
        "This torrent names a tracker, web seed or peer on the computer Inbound runs on or on its local network, so it can " +
        "only be added in the app on that computer.";

    /// <summary>The refusal for a torrent from another device that names more hosts than are checked.</summary>
    public const string TorrentTooManyHostsText =
        "This torrent names too many trackers and web seeds to check, so it can only be added in the app on the computer " +
        "Inbound runs on.";

    // Magnet fields that name somewhere to connect to: trackers, web seeds, other sources, peers and DHT nodes.
    private static readonly string[] MagnetAddressFields = ["tr", "ws", "as", "xs", "x.pe", "dht"];

    // The most host names one torrent from another device may name, and how long their lookup may take in all.
    private const int MostTorrentHostNames = 64;
    private static readonly TimeSpan TorrentHostLookupTime = TimeSpan.FromSeconds(5);

    /// <summary>Looks up a host name's addresses for the torrent check below. Tests set one that makes no network call.</summary>
    public Func<string, CancellationToken, Task<System.Net.IPAddress[]>> TorrentHostResolver { get; set; } =
        (host, cancellationToken) => System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// M3: for a caller from another device, the torrent may make this PC connect only to public addresses, as a link it
    /// adds may: no tracker, web seed, source, peer or DHT node in the magnet, and no tracker, web seed or DHT node in the
    /// .torrent file, on this PC or its local network. First by the text (<see cref="IsLocalTorrentAddress"/>), then by
    /// what each host name resolves to now: the torrent worker does not check the addresses it connects to, as the HTTP
    /// engine does, so a name such as <c>fritz.box</c> with a private address is refused here. A name that does not
    /// resolve (or not in time) can't be shown to be local and is let through.
    /// </summary>
    private async Task<string?> DescribeTorrentAddressesAsync(TorrentInput input, CancellationToken cancellationToken)
    {
        var addresses = new List<string>();
        var query = input.Magnet.IndexOf('?');
        if (input.Kind == "magnet" && query >= 0)
        {
            foreach (var pair in input.Magnet[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                var name = Uri.UnescapeDataString(pair[..equals].Replace('+', ' ')).Trim();
                var field = name.Split('.') is [var head, var tail, ..] && tail.All(char.IsAsciiDigit) && head != "x" ? head : name;
                if (!MagnetAddressFields.Contains(field, StringComparer.OrdinalIgnoreCase)) continue;
                addresses.Add(Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')));
            }
        }

        if (input.Metadata is { } file) TorrentMetadata.FirstAddress(file, address => { addresses.Add(address); return false; });
        if (addresses.Any(IsLocalTorrentAddress)) return TorrentLocalAddressText;

        var names = new List<string>();
        foreach (var host in addresses.Select(TorrentAddressHost))
        {
            if (Uri.CheckHostName(host) != UriHostNameType.Dns || !Uri.TryCreate($"http://{host}/", UriKind.Absolute, out var uri)) continue;
            var name = uri.IdnHost.TrimEnd('.');
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        if (names.Count == 0) return null;
        if (names.Count > MostTorrentHostNames) return TorrentTooManyHostsText;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TorrentHostLookupTime);
        var resolved = await Task.WhenAll(names.Select(async host =>
        {
            try
            {
                return await TorrentHostResolver(host, limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return [];
            }
        })).ConfigureAwait(false);
        return resolved.Any(found => found.Any(NetworkAddresses.IsLocal)) ? TorrentLocalAddressText : null;
    }

    /// <summary>
    /// Whether an address a torrent names points at this PC or its local network, by its text alone: what
    /// <see cref="NetworkAddresses.IsLocalTarget"/> says, and a name of one label (<c>nas</c>), which only the local
    /// network resolves. A host that can't be read counts as one: it can't be shown to be public.
    /// </summary>
    public static bool IsLocalTorrentAddress(string address)
    {
        if (address.Trim().Length == 0) return false;
        var host = TorrentAddressHost(address);
        if (host.Length == 0) return true;
        if (!Uri.TryCreate($"http://{host}/", UriKind.Absolute, out var uri) || NetworkAddresses.IsLocalTarget(uri)) return true;
        return uri.HostNameType == UriHostNameType.Dns && !uri.IdnHost.TrimEnd('.').Contains('.');
    }

    /// <summary>
    /// The host of an address a torrent names (an IPv6 one in brackets), or "" when it has none. Read by hand, not by
    /// <see cref="Uri"/>, so a port it would refuse doesn't hide the host.
    /// </summary>
    private static string TorrentAddressHost(string address)
    {
        var value = address.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        var rest = scheme >= 0 ? value[(scheme + 3)..] : value;
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end >= 0 ? rest[..end] : rest;
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            return close > 0 ? authority[..(close + 1)] : authority;
        }

        // A bare IPv6 address (several colons) has no port to strip.
        var colon = authority.IndexOf(':');
        var host = colon >= 0 && colon == authority.LastIndexOf(':') ? authority[..colon] : authority;
        return host.Contains(':') ? $"[{host}]" : host;
    }

    public async Task<TorrentInspectResult> InspectTorrentAsync(TorrentInspectRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        if (TorrentsRefusal() is { } refused) return new TorrentInspectResult { Error = refused };
        var (input, error) = await ReadTorrentInputAsync(request.Input, caller, cancellationToken).ConfigureAwait(false);
        if (input is null) return new TorrentInspectResult { Error = error };
        if (caller is not null && await DescribeTorrentAddressesAsync(input, cancellationToken).ConfigureAwait(false) is { } local)
            return new TorrentInspectResult { Error = local };
        Guid? existing;
        lock (_sync) existing = FindTorrentJobLocked(input.InfoHash)?.Job.Id;
        var totalBytes = input.Parsed?.Files.Sum(file => file.Length);
        // T3: the room at the folder it would go to, measured where the files would be written.
        var name = FileNameResolver.Sanitize(input.Name);
        var destination = ResolveDestination(request.Destination, name, useLastFolder: caller is null, useCategoryFolder: true,
            categoryId: FileCategories.For(input.Name));
        long? free = null;
        if ((caller?.Roots ?? Roots) is not { } roots || roots.Allowed(destination) is not null)
            free = await FreeBytesAtAsync(destination, cancellationToken).ConfigureAwait(false);
        return new TorrentInspectResult
        {
            Ok = true,
            Kind = input.Kind,
            InfoHash = input.InfoHash,
            Name = input.Name,
            TotalBytes = totalBytes,
            Files = input.Parsed?.Files.Select(file => new TorrentFileDto
            {
                Index = file.Index, Path = file.Path, Size = file.Length, Wanted = true,
                Priority = TorrentFilePriorities.Text(TorrentFilePriority.Normal),
            }).ToList(),
            ExistingJobId = existing,
            Destination = destination,
            FreeBytes = free,
            SpaceProblem = totalBytes is { } total && free is { } room && total > room ? NoRoomText(total, room) : null,
        };
    }

    /// <summary>T3: why the chosen files don't fit, in words.</summary>
    internal static string NoRoomText(long wanted, long free)
        => $"Not enough free space: the chosen files need {ByteText(wanted)} and the folder has {ByteText(free)} free.";

    private static string ByteText(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} bytes" : value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>T3: <paramref name="files"/> with <paramref name="priorities"/> applied, or why they can't be.</summary>
    private static (List<TorrentFile>? Files, string? Error) ApplyPriorities(List<TorrentFile> files, IReadOnlyList<TorrentFilePriorityDto>? priorities)
    {
        if (priorities is not { Count: > 0 }) return (files, null);
        var byIndex = new Dictionary<int, TorrentFilePriority>();
        foreach (var entry in priorities)
        {
            if (entry is null || TorrentFilePriorities.Parse(entry.Priority) is not { } priority)
                return (null, "A file priority must be skip, low, normal or high.");
            byIndex[entry.Index] = priority;
        }

        if (!byIndex.Keys.ToHashSet().IsSubsetOf(files.Select(file => file.Index))) return (null, ChosenFileMissingText);
        var updated = files.Select(file => byIndex.TryGetValue(file.Index, out var priority) ? file.WithPriority(priority) : file).ToList();
        return updated.Any(file => file.Wanted) ? (updated, null) : (null, "Choose at least one file.");
    }

    public async Task<TorrentAddResult> AddTorrentAsync(TorrentAddRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        if (TorrentsRefusal() is { } refused) return new TorrentAddResult { Error = refused };
        var (input, error) = string.IsNullOrWhiteSpace(request.Data)
            ? await ReadTorrentInputAsync(request.Input, caller, cancellationToken).ConfigureAwait(false)
            : ReadTorrentData(request.Data);
        if (input is null) return new TorrentAddResult { Error = error };
        if (caller is not null && await DescribeTorrentAddressesAsync(input, cancellationToken).ConfigureAwait(false) is { } local)
            return new TorrentAddResult { Error = local };
        lock (_sync)
        {
            if (FindTorrentJobLocked(input.InfoHash) is { } existing)
                return new TorrentAddResult { Ok = true, Existing = true, Job = Map(existing.Job) };
        }

        var filesMode = (request.Files ?? "all").Trim();
        if (filesMode is not ("all" or "chosen" or "askWhenReady"))
            return new TorrentAddResult { Error = "Files must be all, chosen or askWhenReady." };

        // The file list with each file's choice; a magnet's comes with its metadata.
        List<TorrentFile>? files = input.Parsed?.Files.Select(file => new TorrentFile(file.Index, file.Path, file.Length, true)).ToList();
        if (filesMode == "chosen")
        {
            if (files is null) return new TorrentAddResult { Error = "A magnet's files are known only once it has found peers. Add it with askWhenReady to choose them then." };
            var chosen = (request.FileIndexes ?? []).ToHashSet();
            if (chosen.Count == 0) return new TorrentAddResult { Error = "Choose at least one file." };
            if (!chosen.IsSubsetOf(files.Select(file => file.Index))) return new TorrentAddResult { Error = ChosenFileMissingText };
            files = files.Select(file => file with { Wanted = chosen.Contains(file.Index) }).ToList();
        }

        // T3: priorities by file, after the choice above.
        if (request.Priorities is { Count: > 0 })
        {
            if (files is null) return new TorrentAddResult { Error = "A magnet's files are known only once it has found peers. Set their priorities then." };
            var (prioritized, priorityError) = ApplyPriorities(files, request.Priorities);
            if (prioritized is null) return new TorrentAddResult { Error = priorityError };
            files = prioritized;
        }

        var options = request.Options;
        if (options is not null && TorrentOptionsError(options) is { } optionsError) return new TorrentAddResult { Error = optionsError };

        var askWhenReady = filesMode == "askWhenReady" && files is null;
        var startMode = ParseEnum(request.StartMode, StartMode.StartNow);
        var categoryId = FileCategories.For(input.Name);
        var name = FileNameResolver.Sanitize(input.Name);
        var destination = ResolveDestination(request.Destination, name, useLastFolder: caller is null, useCategoryFolder: true, categoryId: categoryId);
        if ((caller?.Roots ?? Roots) is { } roots)
        {
            if (roots.Allowed(destination) is not { } inside) return new TorrentAddResult { Error = roots.OutsideTextFor(destination) };
            destination = inside;
        }

        // T3: the wanted files must fit in the room left at the destination (a magnet's are checked when its file list comes).
        if (files is not null)
        {
            var needed = files.Where(file => file.Wanted).Sum(file => file.Size);
            var room = await FreeBytesAtAsync(destination, cancellationToken).ConfigureAwait(false);
            if (room is { } free && needed > free) return new TorrentAddResult { Error = NoRoomText(needed, free) };
        }

        List<string> madeFolders;
        // The final top-level name is picked when the torrent finishes (ITorrentJobSink.ChooseFinalTopName), when it is
        // known which names are taken then.
        var finalPath = Path.Combine(destination, name);
        try
        {
            madeFolders = CreateFolderRecordingMade(destination, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new TorrentAddResult { Error = $"Cannot use destination '{destination}': {ex.Message}" };
        }

        var rootName = name;
        int queueId;
        string queueName;
        bool autoResume;
        lock (_sync)
        {
            queueId = _settings.Queues.Any(q => q.Id == request.QueueId) ? request.QueueId!.Value : _settings.DefaultQueueId;
            queueName = _settings.Queues.First(q => q.Id == queueId).Name;
            autoResume = _settings.AutoResumeUnfinishedOnStartup;
        }

        var id = Guid.NewGuid();
        var wantedBytes = files?.Where(file => file.Wanted).Sum(file => file.Size) ?? 0;
        var job = new DownloadJob
        {
            Id = id,
            Url = input.Magnet,
            QueueId = queueId,
            QueueName = queueName,
            Priority = JobPriorities.Normal,
            FileName = rootName,
            CategoryId = categoryId,
            CategoryIsAutomatic = true,
            DestinationDirectory = destination,
            MadeFolders = madeFolders,
            FinalFilePath = finalPath,
            PartFilePath = string.Empty,
            StartMode = startMode,
            QueueProcessing = startMode == StartMode.Queue,
            Source = DownloadSource.Api,
            AddedFromAnotherDevice = caller is not null,
            PublicNetworkOnly = caller is not null,
            RequestedConnections = 1,
            Description = request.Description,
            ConflictPolicy = ConflictPolicy.Rename,
            State = DownloadState.Queued,
            AutoResumeAfterRestart = autoResume,
            TotalBytes = files is null ? null : wantedBytes,
            Torrent = new TorrentJobInfo
            {
                InfoHash = input.InfoHash,
                Name = input.Name,
                Magnet = input.Kind == "magnet" ? input.Magnet : null,
                HasMetadata = files is not null,
                FileCount = files?.Count ?? 0,
                WantedCount = files?.Count(file => file.Wanted) ?? 0,
                TotalBytes = files?.Sum(file => file.Size) ?? 0,
                WantedBytes = wantedBytes,
                ChooseFilesWhenReady = askWhenReady,
                PendingFileIndexes = askWhenReady && request.FileIndexes is { Count: > 0 } pending ? [.. pending.Distinct()] : null,
                PartSuffix = TorrentJobInfo.PartSuffixFor(id),
            },
        };
        if (options is not null) ApplyOptionsTo(job.Torrent, options);

        try
        {
            // The torrent's own data first: a job is never saved without what it needs to run.
            if (input.Metadata is not null) await TorrentStore.SaveMetadataAsync(id, input.Metadata, cancellationToken).ConfigureAwait(false);
            if (files is not null)
            {
                await TorrentStore.SaveFilesAsync(id, files, cancellationToken).ConfigureAwait(false);
            }

            await _jobStore.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DeleteTorrentDataQuietlyAsync(id).ConfigureAwait(false);
            return new TorrentAddResult { Error = $"Could not save the download: {ex.Message}" };
        }

        var runtime = new JobRuntime(job);
        lock (_sync)
        {
            // Two adds of one torrent at once: the first one saved wins.
            if (FindTorrentJobLocked(input.InfoHash) is { } raced)
            {
                _ = _jobStore.DeleteAsync(id, CancellationToken.None);
                _ = DeleteTorrentDataQuietlyAsync(id);
                return new TorrentAddResult { Ok = true, Existing = true, Job = Map(raced.Job) };
            }

            _jobs[id] = runtime;
            _completionFiredQueues.Remove(queueId);
        }

        Raise(runtime, "added");
        Schedule();
        return new TorrentAddResult { Ok = true, Job = Map(job) };
    }

    public async Task<TorrentActionResult> SetTorrentFilesAsync(TorrentSetFilesRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        JobRuntime? runtime;
        lock (_sync) _jobs.TryGetValue(request.JobId, out runtime);
        if (runtime?.Job.Torrent is not { } info) return new TorrentActionResult { Error = "That torrent is not in the list." };
        if (caller is not null && HeldBy(request.JobId, caller) == false) return new TorrentActionResult { Error = CallerScope.OutsideJobRefused };
        DownloadState state;
        lock (_sync) state = runtime.Job.State;
        if (state is DownloadState.Completed or DownloadState.Cancelled || info.Finalized || _torrentEngine?.IsFinishing(request.JobId) == true)
            return new TorrentActionResult { Error = "This torrent has finished; its files can't change now." };
        var files = await TorrentStore.LoadFilesAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (files is null || !info.HasMetadata) return new TorrentActionResult { Error = "The file list is not in yet." };
        var chosen = (request.FileIndexes ?? []).ToHashSet();
        if (chosen.Count == 0 && request.Priorities is not { Count: > 0 }) return new TorrentActionResult { Error = "Choose at least one file." };
        if (!chosen.IsSubsetOf(files.Select(file => file.Index))) return new TorrentActionResult { Error = ChosenFileMissingText };
        // Without FileIndexes (T3: priorities alone) every file keeps its choice.
        var updated = chosen.Count == 0 ? files.ToList() : files.Select(file => file with { Wanted = chosen.Contains(file.Index) }).ToList();
        var (prioritized, priorityError) = ApplyPriorities(updated, request.Priorities);
        if (prioritized is null) return new TorrentActionResult { Error = priorityError };
        updated = prioritized;
        // L4: a magnet that waited for this choice has had no room check (its size was unknown when it was added, and
        // its first file list stopped for the choice): the chosen files must fit, as a .torrent's must when it is added.
        bool awaiting;
        lock (_sync) awaiting = info.AwaitingFileChoice;
        if (awaiting)
        {
            var needed = updated.Where(file => file.Wanted).Sum(file => file.Size);
            var room = await FreeBytesAtAsync(runtime.Job.DestinationDirectory, cancellationToken).ConfigureAwait(false);
            if (room is { } free && needed > free) return new TorrentActionResult { Error = NoRoomText(needed, free) };
        }

        await TorrentStore.SaveFilesAsync(request.JobId, updated, cancellationToken).ConfigureAwait(false);

        bool wasWaiting;
        lock (_sync)
        {
            wasWaiting = info.AwaitingFileChoice;
            info.AwaitingFileChoice = false;
            info.ChooseFilesWhenReady = false;
            info.PendingFileIndexes = null;
            info.WantedCount = updated.Count(file => file.Wanted);
            info.WantedBytes = updated.Where(file => file.Wanted).Sum(file => file.Size);
            runtime.Job.TotalBytes = info.WantedBytes;
            // A torrent that waited for this choice goes on now, as Resume would.
            if (wasWaiting && runtime.Job.State == DownloadState.Paused && runtime.Runner is not { IsCompleted: false })
            {
                runtime.Job.State = DownloadState.Queued;
                runtime.Job.StartMode = StartMode.StartNow;
                runtime.Job.PausedByUser = false;
            }

            runtime.Job.Touch();
        }

        // A running torrent takes the new choice at once. One that began finishing meanwhile keeps what it had.
        if (_torrentEngine is { } engine &&
            await engine.ApplyFileChoiceAsync(request.JobId, updated, cancellationToken).ConfigureAwait(false) == TorrentFileChoiceOutcome.Finishing)
        {
            await TorrentStore.SaveFilesAsync(request.JobId, files, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                info.WantedCount = files.Count(file => file.Wanted);
                info.WantedBytes = files.Where(file => file.Wanted).Sum(file => file.Size);
                runtime.Job.TotalBytes = info.WantedBytes;
            }

            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            Raise(runtime, "updated");
            return new TorrentActionResult { Error = "This torrent has finished; its files can't change now." };
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");
        Schedule();
        return new TorrentActionResult { Ok = true };
    }

    public async Task<TorrentFilesResult> GetTorrentFilesAsync(TorrentFilesRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        bool known;
        var finished = false;
        lock (_sync)
        {
            known = _jobs.TryGetValue(request.JobId, out var runtime) && runtime.Job.Torrent is not null;
            if (known) finished = runtime!.Job.State == DownloadState.Completed || runtime.Job.Torrent!.Finalized;
        }

        if (!known) return new TorrentFilesResult { Error = "That torrent is not in the list." };
        if (caller is not null && HeldBy(request.JobId, caller) == false) return new TorrentFilesResult { Error = CallerScope.OutsideJobRefused };
        var files = await TorrentStore.LoadFilesAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        // While an unfinished torrent is on the worker, the worker says how far each file is (whole checked pieces).
        var progress = !finished && _torrentEngine is { } engine
            ? await engine.GetFileProgressAsync(request.JobId, cancellationToken).ConfigureAwait(false)
            : null;
        return new TorrentFilesResult
        {
            Ok = true,
            Files = (files ?? []).Select(file => new TorrentFileDto
            {
                Index = file.Index,
                Path = file.Path,
                Size = file.Size,
                Wanted = file.Wanted,
                Priority = TorrentFilePriorities.Text(file.Priority),
                Done = TorrentFileDone(file, progress, finished),
            }).ToList(),
        };
    }

    /// <summary>
    /// A file's done bytes: for a finished torrent its full size when wanted and 0 when left out; else the worker's count
    /// while the torrent is attached, and not known (null) while it is off the worker.
    /// </summary>
    internal static long? TorrentFileDone(TorrentFile file, IReadOnlyList<long>? progress, bool finished)
    {
        if (finished) return file.Wanted ? file.Size : 0;
        if (progress is not null && file.Index >= 0 && file.Index < progress.Count) return Math.Clamp(progress[file.Index], 0, file.Size);
        return null;
    }

    private JobRuntime? FindTorrentJobLocked(string infoHash)
        => _jobs.Values.FirstOrDefault(runtime => !runtime.CancelledForRemoval &&
            string.Equals(runtime.Job.Torrent?.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));

    /// <summary>A magnet (v1 hash), a .torrent file's full path on this machine, or an upload's reference.</summary>
    private async Task<(TorrentInput? Input, string? Error)> ReadTorrentInputAsync(string? text, CallerScope? caller, CancellationToken cancellationToken)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0) return (null, "Enter a magnet link or choose a .torrent file.");
        if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var hash = RemoteInputClassifier.InfoHashOfMagnet(value, out var displayName, out var problem);
            if (hash is null) return (null, problem);
            var name = string.IsNullOrWhiteSpace(displayName) ? hash : displayName;
            return (new TorrentInput("magnet", hash, name, value, null, null), null);
        }

        // A network or device path is never opened: even asking whether it exists sends this Windows account's
        // credentials to that computer. This comes before anything touches the path.
        if (RemoteInputClassifier.IsNetworkOrDevicePath(value)) return (null, TorrentNetworkPathText);
        var path = ResolveTorrentUpload?.Invoke(value);
        if (path is null && ResolveTorrentUpload is not null && UploadFolder.IsReference(value)) return (null, UploadFolder.GoneText);
        if (path is null)
        {
            if (!Path.IsPathFullyQualified(value)) return (null, "Enter a magnet link or the full path of a .torrent file.");
            if ((caller?.Roots ?? Roots) is { } roots)
            {
                if (roots.Allowed(value) is not { } inside) return (null, roots.OutsideFileText(".torrent file"));
                value = inside;
            }

            path = value;
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, "That .torrent file doesn't exist.");
            if (info.Length > TorrentMetadata.MaxFileBytes) return (null, "This .torrent file is too large to be a real one.");
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, "That .torrent file can't be read.");
        }

        return TorrentFromBytes(bytes);
    }

    /// <summary>A .torrent file's bytes in base64, as a saved list carries them.</summary>
    private static (TorrentInput? Input, string? Error) ReadTorrentData(string data)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data.Trim());
        }
        catch (FormatException)
        {
            return (null, "The saved .torrent file is damaged.");
        }

        if (bytes.Length > TorrentMetadata.MaxFileBytes) return (null, "This .torrent file is too large to be a real one.");
        return TorrentFromBytes(bytes);
    }

    private static (TorrentInput? Input, string? Error) TorrentFromBytes(byte[] bytes)
    {
        if (!TorrentMetadata.TryParse(bytes, out var parsed, out var parseProblem) || parsed is null) return (null, parseProblem);
        if (parsed.Files.Count == 0) return (null, "This torrent describes its files only in version 2 form, which can't be downloaded here yet.");
        if (parsed.FilesCut) return (null, $"This torrent lists more files than can be downloaded here (at most {TorrentMetadata.MaxListedFiles.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}).");
        var magnet = $"magnet:?xt=urn:btih:{parsed.InfoHash}&dn={Uri.EscapeDataString(parsed.Name)}";
        return (new TorrentInput("torrentFile", parsed.InfoHash, parsed.Name, magnet, bytes, parsed), null);
    }
}
