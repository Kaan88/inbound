using System.Net;
using System.Net.Sockets;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Runtime;

/// <summary>Torrent jobs (T1, docs/plans/torrent-t1-design.md): the service side of <see cref="ITorrentDownloadEngine"/>.</summary>
public sealed partial class DownloadManagerService : ITorrentJobSink
{
    private ITorrentDownloadEngine? _torrentEngine;
    private ITorrentStore? _torrentStoreField;

    private ITorrentStore TorrentStore => _torrentStoreField ??= new TorrentStore(_paths);

    /// <summary>The Host has the worker and the kill switch is not set: torrent commands are offered (capability <c>torrents</c>).</summary>
    public bool TorrentsAvailable => _torrentEngine is not null && !TorrentAvailability.KillSwitchSet();

    /// <summary>Torrents can be added and run now: available and turned on in Options.</summary>
    public bool TorrentsOn
    {
        get
        {
            lock (_sync) return TorrentsAvailable && _settings.TorrentsEnabled;
        }
    }

    /// <summary>Whether the worker process runs now, for the status-bar chip.</summary>
    public bool TorrentWorkerRunning => _torrentEngine?.WorkerRunning == true;

    public event EventHandler? TorrentWorkerRunningChanged;

    /// <summary>
    /// T2: the port to save. A typed port wins; 0 keeps the saved one, and when none was chosen yet and torrents are
    /// being turned on, one is picked at random from the dynamic range and kept from then on.
    /// </summary>
    private int NextTorrentPort(Contracts.SettingsDto requested)
    {
        if (requested.TorrentListenPort != 0) return requested.TorrentListenPort;
        int saved;
        lock (_sync) saved = _settings.TorrentListenPort;
        if (saved != 0) return saved;
        return requested.TorrentsEnabled ? Contracts.TorrentListenPorts.Pick(CanListenOn) : 0;
    }

    /// <summary>Settings saved before T2 with torrents on have no port yet: one is chosen and saved at start.</summary>
    private async Task ChooseTorrentPortAsync(CancellationToken cancellationToken)
    {
        if (_torrentEngine is null || !_settings.TorrentsEnabled || _settings.TorrentListenPort != 0) return;
        _settings.TorrentListenPort = Contracts.TorrentListenPorts.Pick(CanListenOn);
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this machine lets a program open <paramref name="port"/> on TCP and UDP now. Run once, when a port is
    /// picked: it turns away ports Windows reserves (Hyper-V, WSL and Docker exclude blocks) and ports already in use.
    /// It tries 127.0.0.1, which those reservations cover too: opening the port on every address would make Windows ask
    /// to allow the Host through the firewall.
    /// </summary>
    public static bool CanListenOn(int port)
    {
        try
        {
            using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>T2: the port hint of Options › Torrents.</summary>
    public Contracts.TorrentNetworkResult GetTorrentNetwork()
    {
        if (_torrentEngine is not { } engine) return new Contracts.TorrentNetworkResult { Error = "Torrents aren't available on this Host." };
        var network = engine.Network;
        return new Contracts.TorrentNetworkResult
        {
            Ok = true,
            Ran = network.Ran,
            Running = engine.WorkerRunning,
            Port = network.Port,
            ListenError = network.ListenError,
            MappedPort = network.MappedPort,
            IncomingSeen = network.IncomingSeen,
            UnusablePort = network.UnusablePort,
        };
    }

    private void AttachTorrentEngine(ITorrentDownloadEngine? engine)
    {
        _torrentEngine = engine;
        if (engine is null) return;
        engine.SetSink(this);
        engine.WorkerRunningChanged += () =>
        {
            OnTorrentWorkerRunningChanged();
            TorrentWorkerRunningChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    async Task ITorrentJobSink.MetadataArrivedAsync(Guid jobId, string name, IReadOnlyList<TorrentFile> files,
        bool awaitingFileChoice, CancellationToken cancellationToken)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.Torrent is not { } info) return;
            var job = runtime.Job;
            info.HasMetadata = true;
            if (!string.IsNullOrWhiteSpace(name)) info.Name = name;
            info.FileCount = files.Count;
            info.WantedCount = files.Count(file => file.Wanted);
            info.TotalBytes = files.Sum(file => file.Size);
            info.WantedBytes = files.Where(file => file.Wanted).Sum(file => file.Size);
            info.AwaitingFileChoice = awaitingFileChoice;
            // A saved choice is used once, when the file list comes; one that did not fit is asked for instead.
            info.PendingFileIndexes = null;
            // A torrent that did not stop for a choice (one file: nothing to choose) has no choice left to ask for.
            if (!awaitingFileChoice) info.ChooseFilesWhenReady = false;
            job.TotalBytes = info.WantedBytes;
            // A single file's own name, or the torrent's folder (its name), never the first file of several.
            var finalPath = TorrentLayout.FinalPathOf(job.DestinationDirectory, files, info.Name);
            job.FinalFilePath = finalPath;
            job.FileName = Path.GetFileName(finalPath);
            job.Touch();
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");
    }

    /// <summary>L4: a magnet's files are measured against the room left once its file list comes, as a .torrent's are when added.</summary>
    async Task<string?> ITorrentJobSink.RoomProblemAsync(Guid jobId, string destination, long neededBytes, CancellationToken cancellationToken)
    {
        try
        {
            var free = await FreeBytesAtAsync(destination, cancellationToken).ConfigureAwait(false);
            return free is { } room && neededBytes > room ? NoRoomText(neededBytes, room) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // not measured: the disk itself says when it is full
        }
    }

    string ITorrentJobSink.ChooseFinalTopName(Guid jobId, string destination, string topName, IReadOnlyList<TorrentFile> files)
    {
        // A multi-file torrent downloads into its folder, so the folder being there says nothing; one of its files
        // already there under its own name does (the worker would refuse to rename over it).
        var inFolder = files.Count > 0 && files[0].Path.IndexOfAny(['/', '\\']) > 0;
        HashSet<string> taken;
        lock (_sync)
        {
            // Other downloads' names in the list count as taken, finished or not, as a plain download's would.
            taken = _jobs.Values
                .Where(runtime => runtime.Job.Id != jobId && !string.IsNullOrWhiteSpace(runtime.Job.FinalFilePath))
                .Select(runtime => Path.GetFullPath(runtime.Job.FinalFilePath))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        bool Free(string name)
        {
            var path = Path.Combine(destination, name);
            if (taken.Contains(Path.GetFullPath(path)) || File.Exists(path)) return false;
            if (!Directory.Exists(path)) return true;
            if (!inFolder) return false;
            return !TorrentLayout.WithTopName(files, name)
                .Where(file => file.Wanted)
                .Select(file => TorrentLayout.DiskPathOf(destination, file))
                .Any(target => File.Exists(target) || Directory.Exists(target));
        }

        if (Free(topName)) return topName;
        var extension = Path.GetExtension(topName);
        // A folder name with a dot in it is not a file name with an extension.
        if (extension.Length is 0 or > 10 || extension.Contains(' ')) extension = string.Empty;
        var stem = topName[..^extension.Length];
        for (var number = 2; number < 10_000; number++)
        {
            var candidate = $"{stem} ({number}){extension}";
            if (Free(candidate)) return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    async Task ITorrentJobSink.FinalizedAsync(Guid jobId, string? rootName, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken)
    {
        // The names first: a restart that reads the job as finalized must find them.
        await TorrentStore.SaveFilesAsync(jobId, files, cancellationToken).ConfigureAwait(false);
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.Torrent is not { } info) return;
            var job = runtime.Job;
            info.Finalized = true;
            info.Finalizing = false;
            info.FinalizingRootName = null;
            info.RootName = rootName;
            var finalPath = TorrentLayout.FinalPathOf(job.DestinationDirectory, files, rootName ?? info.Name);
            job.FinalFilePath = finalPath;
            job.FileName = Path.GetFileName(finalPath);
            job.Touch();
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");
    }

    async Task ITorrentJobSink.FinalizingAsync(Guid jobId, string? rootName, bool finalizing, CancellationToken cancellationToken)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.Torrent is not { } info) return;
            info.Finalizing = finalizing;
            info.FinalizingRootName = finalizing ? rootName : null;
            runtime.Job.Touch();
        }

        // M16: on disk before the worker renames anything.
        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>REV.4 for a torrent: every file it wrote is marked as from the internet.</summary>
    private async Task MarkTorrentFilesAsync(DownloadJob job)
    {
        IReadOnlyList<TorrentFile>? files;
        try
        {
            files = await TorrentStore.LoadFilesAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Torrent job {job.Id}: its files were not marked as from the internet: {ex.Message}");
            return;
        }

        foreach (var file in files ?? [])
        {
            if (!file.Wanted) continue;
            var path = TorrentLayout.DiskPathOf(job.DestinationDirectory, file);
            if (IsInside(job.DestinationDirectory, path)) MarkOfTheWeb.TryWrite(path, job.Torrent?.Magnet ?? job.EffectiveUrl, _log);
        }
    }

    void ITorrentJobSink.SwarmChanged(Guid jobId, int peers, int seeds, double uploadRate)
    {
        lock (_sync)
        {
            if (_jobs.TryGetValue(jobId, out var runtime) && runtime.Job.Torrent is { } info)
            {
                info.Peers = peers;
                info.Seeds = seeds;
                runtime.TorrentUploadRate = uploadRate;
            }
        }
    }

    /// <summary>The suffix every torrent job's files carry while they download, so the orphan scan leaves them alone.</summary>
    private List<string> TorrentPartSuffixesLocked()
        => _jobs.Values
            .Select(runtime => runtime.Job.Torrent?.PartSuffix)
            .Where(suffix => !string.IsNullOrEmpty(suffix))
            .Select(suffix => suffix!)
            .ToList();

    private bool IsTorrentPartLocked(string path)
    {
        foreach (var suffix in TorrentPartSuffixesLocked())
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// A torrent's files on disk: every file under its part name, and with <paramref name="includeFinal"/> under its own
    /// name too (only a completed job's, which this download wrote). Folders left empty go as well, the destination
    /// itself never.
    /// </summary>
    private async Task DeleteTorrentPayloadAsync(DownloadJob job, bool includeFinal)
    {
        if (job.Torrent is not { } info) return;
        IReadOnlyList<TorrentFile> files;
        try
        {
            files = await TorrentStore.LoadFilesAsync(job.Id, CancellationToken.None).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Torrent job {job.Id}: its file list could not be read, so its files stay: {ex.Message}");
            return;
        }

        // Files under their own names are this download's only once it gave them those names, and only the wanted ones.
        var ownNames = includeFinal && info.Finalized;
        var destination = job.DestinationDirectory;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var final = TorrentLayout.DiskPathOf(destination, file);
            if (!IsInside(destination, final)) continue;
            DeleteFileQuietly(final + info.PartSuffix);
            if (ownNames && file.Wanted) DeleteFileQuietly(final);
            if (Path.GetDirectoryName(final) is { } folder) folders.Add(folder);
        }

        // libtorrent's store for pieces that overlap files left out (it lets go of it a moment after the torrent left the worker).
        // Not while another job of this torrent saves into the same folder (deleted and added again at once): libtorrent
        // names the file after the torrent, so it is that job's.
        bool StillOurs()
        {
            lock (_sync)
                return !_jobs.Values.Any(other => other.Job.Id != job.Id && !other.CancelledForRemoval &&
                    string.Equals(other.Job.Torrent?.InfoHash, info.InfoHash, StringComparison.OrdinalIgnoreCase) &&
                    PathNames.Same(other.Job.DestinationDirectory, destination));
        }

        if (!await TorrentLayout.DeletePartsFileAsync(destination, info.InfoHash, CancellationToken.None, StillOurs).ConfigureAwait(false))
            _log?.Invoke($"Torrent job {job.Id}: its hidden parts file is still in use and stays.");
        foreach (var folder in folders.OrderByDescending(folder => folder.Length))
        {
            for (var current = folder; IsInside(destination, current) && !PathNames.Same(current, destination);
                 current = Path.GetDirectoryName(current)!)
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) break;
                    Directory.Delete(current, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
        }
    }

    private static bool IsInside(string root, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return full.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task DeleteTorrentDataQuietlyAsync(Guid jobId)
    {
        try
        {
            await TorrentStore.DeleteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not delete the torrent data of job {jobId}: {ex.Message}");
        }
    }

    private DownloadResult TorrentUnavailableResult(DownloadJob job)
        => DownloadResult.Fail(DownloadState.Failed,
            new JobFailure(FailureKind.Unknown,
                _torrentEngine is null || TorrentAvailability.KillSwitchSet()
                    ? "Torrents are not available in this installation."
                    : "Torrents are turned off. Turn them on in Options > Torrents to resume this download.",
                false, true, DateTimeOffset.UtcNow),
            job.ReceivedBytes, job.TotalBytes);

    private async Task<bool> ReconcileTorrentAsync(DownloadJob job)
    {
        var changed = false;
        if (job.State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting or DownloadState.Verifying)
        {
            job.State = job.AutoResumeAfterRestart && _settings.AutoResumeUnfinishedOnStartup
                ? DownloadState.Queued
                : DownloadState.Paused;
            changed = true;
        }

        if (job.State == DownloadState.Completed && !File.Exists(job.FinalFilePath) && !Directory.Exists(job.FinalFilePath))
        {
            job.State = DownloadState.Failed;
            job.Failure = new JobFailure(FailureKind.DestinationUnavailable,
                "The completed files are missing from their destination.", false, false, DateTimeOffset.UtcNow);
            changed = true;
        }

        // Only a completed torrent seeds (T2).
        if (job.State != DownloadState.Completed && job.Torrent is { Seeding: true } seeding)
        {
            seeding.Seeding = false;
            seeding.SeedingSince = null;
            changed = true;
        }

        if (job.State == DownloadState.Cancelled && job.DeleteFileOnCancel)
        {
            await DeleteTorrentPayloadAsync(job, includeFinal: false).ConfigureAwait(false);
        }

        return changed;
    }
}
