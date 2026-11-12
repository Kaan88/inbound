using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// T3: a torrent's own options (<c>torrent.options</c>, <c>torrent.setOptions</c>), a check of its files
/// (<c>torrent.recheck</c>), an announce (<c>torrent.announce</c>), the Overview's facts (<c>torrent.details</c>), who
/// watches torrents (<c>torrent.watch</c>), and Options › Torrents' peer, connection and proxy settings.
/// </summary>
public sealed partial class DownloadManagerService
{
    /// <summary>The highest per-torrent limit, bytes/s (what the torrent engine can take).</summary>
    public const long MostTorrentSpeedLimit = int.MaxValue;

    public const string TorrentNotListedText = "That torrent is not in the list.";
    /// <summary><c>job.update</c>'s speed limit on a torrent: torrents have their own (TorrentDto.DownloadLimit, UploadLimit).</summary>
    public const string TorrentSpeedLimitRefused = "A torrent has its own download and upload limits. Set them in its Options tab.";
    public const string TorrentNotRunningAnnounceText = "The torrent isn't running, so it can't ask for peers now.";
    public const string TorrentOlderWorkerText = "The torrent program in this installation is too old for that. Update the app.";

    /// <summary>Tests: measures the free bytes at a folder instead of the drive (null: not measured).</summary>
    public Func<string, CancellationToken, Task<long?>>? MeasureFreeBytes { get; set; }

    private async Task<long?> FreeBytesAtAsync(string folder, CancellationToken cancellationToken)
        => MeasureFreeBytes is { } measure
            ? await measure(folder, cancellationToken).ConfigureAwait(false)
            : (await FolderSpaceMeter.MeasureAsync(folder, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false)).FreeBytes;

    // ---- a torrent's own options ----

    /// <summary>Why these options can't be used, or null.</summary>
    internal static string? TorrentOptionsError(TorrentOptionsDto options)
    {
        if (options.DownloadLimit is < 0 or > MostTorrentSpeedLimit || options.UploadLimit is < 0 or > MostTorrentSpeedLimit)
            return "A torrent's speed limit must be 0 (none) up to 2,147,483,647 bytes per second.";
        if (options.SeedGoal is { } goal && !TorrentSeedGoals.IsKnown(goal))
            return "The seeding goal must be global, ratio, hours or never.";
        if (options.SeedGoal == TorrentSeedGoals.Ratio && options.SeedGoalValue is not (>= 0.01 and <= 100))
            return "Enter a seeding ratio from 0.01 to 100.";
        if (options.SeedGoal == TorrentSeedGoals.Hours && options.SeedGoalValue is not (>= 0.1 and <= 8760))
            return "Enter seeding hours from 0.1 to 8760.";
        if (options.SeedGoal is null && options.SeedGoalValue is not null)
            return "Give the seeding goal together with its value.";
        return null;
    }

    /// <summary>Applies the fields that are set (a limit of 0 removes it).</summary>
    internal static void ApplyOptionsTo(TorrentJobInfo info, TorrentOptionsDto options)
    {
        if (options.DownloadLimit is { } download) info.DownloadLimit = download > 0 ? download : null;
        if (options.UploadLimit is { } upload) info.UploadLimit = upload > 0 ? upload : null;
        if (options.SeedGoal is { } goal)
        {
            info.SeedGoal = goal == TorrentSeedGoals.Global ? null : goal;
            info.SeedGoalValue = goal is TorrentSeedGoals.Ratio or TorrentSeedGoals.Hours ? options.SeedGoalValue!.Value : 0;
        }

        if (options.Sequential is { } sequential) info.Sequential = sequential;
    }

    internal static TorrentOptionsDto OptionsOf(Guid jobId, TorrentJobInfo info) => new()
    {
        JobId = jobId,
        DownloadLimit = info.DownloadLimit,
        UploadLimit = info.UploadLimit,
        SeedGoal = info.SeedGoal ?? TorrentSeedGoals.Global,
        SeedGoalValue = info.SeedGoal is TorrentSeedGoals.Ratio or TorrentSeedGoals.Hours ? info.SeedGoalValue : null,
        Sequential = info.Sequential,
    };

    /// <summary>A torrent job the caller may see, or why not.</summary>
    private (JobRuntime? Runtime, string? Error) TorrentJobFor(Guid jobId, CallerScope? caller)
    {
        JobRuntime? runtime;
        lock (_sync) _jobs.TryGetValue(jobId, out runtime);
        if (runtime?.Job.Torrent is null || _torrentEngine is null) return (null, TorrentNotListedText);
        if (caller is not null && HeldBy(jobId, caller) == false) return (null, CallerScope.OutsideJobRefused);
        return (runtime, null);
    }

    public Task<TorrentOptionsResult> GetTorrentOptionsAsync(TorrentJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        var (runtime, error) = TorrentJobFor(request.JobId, caller);
        if (runtime is null) return Task.FromResult(new TorrentOptionsResult { Error = error });
        TorrentOptionsDto options;
        lock (_sync) options = OptionsOf(request.JobId, runtime.Job.Torrent!);
        return Task.FromResult(new TorrentOptionsResult { Ok = true, Options = options, Running = IsOnTorrentEngine(request.JobId) });
    }

    public async Task<TorrentOptionsResult> SetTorrentOptionsAsync(TorrentOptionsDto request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        var (runtime, error) = TorrentJobFor(request.JobId, caller);
        if (runtime is null) return new TorrentOptionsResult { Error = error };
        if (TorrentOptionsError(request) is { } refused) return new TorrentOptionsResult { Error = refused };
        TorrentJobInfo info;
        TorrentOptionsDto options;
        bool stopSeeding;
        lock (_sync)
        {
            if (runtime.Job.State == DownloadState.Cancelled) return new TorrentOptionsResult { Error = TorrentNotListedText };
            info = runtime.Job.Torrent!;
            ApplyOptionsTo(info, request);
            options = OptionsOf(request.JobId, info);
            // "Never" ends seeding that runs now (and one about to start).
            stopSeeding = info.SeedGoal == TorrentSeedGoals.Never && (info.Seeding || _seedPending.Contains(request.JobId));
            runtime.Job.Touch();
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");
        var applied = await _torrentEngine!.ApplyOptionsAsync(request.JobId, info, cancellationToken).ConfigureAwait(false);
        if (stopSeeding) await StopTorrentSeedingAsync(runtime).ConfigureAwait(false);
        return new TorrentOptionsResult
        {
            Ok = true,
            Options = options,
            Running = applied.Outcome == TorrentActionOutcome.Applied,
            // Saved either way; an older torrent program takes the limits but not the download order.
            Error = applied.Outcome == TorrentActionOutcome.OlderWorker ? TorrentOlderWorkerText : null,
        };
    }

    private bool IsOnTorrentEngine(Guid jobId)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var runtime)) return false;
            return runtime.Starting || runtime.Runner is { IsCompleted: false } || runtime.Job.Torrent is { Seeding: true };
        }
    }

    // ---- check and announce ----

    /// <summary>
    /// <c>torrent.recheck</c>: a torrent on the worker (downloading or seeding) is checked now; an unfinished one that is
    /// paused, queued or failed is checked when it starts (<see cref="TorrentDto.RecheckPending"/>). A finished torrent
    /// that does not seed, one without its file list, and one with nothing on disk are refused.
    /// </summary>
    public async Task<TorrentActionResult> RecheckTorrentAsync(TorrentJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        var (runtime, error) = TorrentJobFor(request.JobId, caller);
        if (runtime is null) return new TorrentActionResult { Error = error };
        DownloadState state;
        TorrentJobInfo info;
        lock (_sync)
        {
            state = runtime.Job.State;
            info = runtime.Job.Torrent!;
        }

        if (state == DownloadState.Cancelled) return new TorrentActionResult { Error = TorrentNotListedText };
        if (!info.HasMetadata) return new TorrentActionResult { Error = "The file list is not in yet, so there is nothing to check." };
        if (_torrentEngine!.IsFinishing(request.JobId)) return new TorrentActionResult { Error = "The torrent is giving its files their names; check it once it is done." };
        if (!await TorrentHasDataOnDiskAsync(runtime.Job).ConfigureAwait(false))
            return new TorrentActionResult { Error = "Nothing of this torrent is on disk, so there is nothing to check." };

        var reply = await _torrentEngine.RecheckAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        switch (reply.Outcome)
        {
            case TorrentActionOutcome.Applied:
                return new TorrentActionResult { Ok = true };
            case TorrentActionOutcome.Refused:
                return new TorrentActionResult { Error = $"The torrent program did not check it: {reply.Message}" };
            case TorrentActionOutcome.OlderWorker:
                return new TorrentActionResult { Error = TorrentOlderWorkerText };
        }

        // Not on the worker: a finished torrent is checked while it seeds; an unfinished one when it starts.
        lock (_sync)
        {
            if (runtime.Job.State == DownloadState.Completed)
                return new TorrentActionResult { Error = "A finished torrent's files are checked while it seeds. Start seeding it, then check it." };
            info.RecheckOnStart = true;
            runtime.Job.Touch();
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");
        return new TorrentActionResult { Ok = true };
    }

    /// <summary>Whether any file of the torrent is on disk, under its part name or its own.</summary>
    private async Task<bool> TorrentHasDataOnDiskAsync(DownloadJob job)
    {
        if (job.Torrent is not { } info) return false;
        IReadOnlyList<TorrentFile> files;
        try
        {
            files = await TorrentStore.LoadFilesAsync(job.Id, CancellationToken.None).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var file in files)
        {
            var path = TorrentLayout.DiskPathOf(job.DestinationDirectory, file);
            if (!IsInside(job.DestinationDirectory, path)) continue;
            if (File.Exists(path + info.PartSuffix) || info.Finalized && File.Exists(path)) return true;
        }

        return false;
    }

    void ITorrentJobSink.RecheckStarted(Guid jobId)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.Torrent is not { RecheckOnStart: true } info) return;
            info.RecheckOnStart = false;
            runtime.Job.Touch();
        }

        _ = PersistAsync(runtime, CancellationToken.None);
        Raise(runtime, "updated");
    }

    void ITorrentJobSink.TorrentStateChanged(Guid jobId, string state)
    {
        JobRuntime? runtime;
        bool checkingChanged;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.Torrent is not { } info) return;
            checkingChanged = (info.WorkerState == "checking") != (state == "checking");
            info.WorkerState = state;
        }

        if (checkingChanged) Raise(runtime, "updated");
    }

    /// <summary><c>torrent.announce</c>: asks the trackers (and the DHT when it is on) for peers now.</summary>
    public async Task<TorrentActionResult> AnnounceTorrentAsync(TorrentJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        var (runtime, error) = TorrentJobFor(request.JobId, caller);
        if (runtime is null) return new TorrentActionResult { Error = error };
        // A paused torrent may still sit on the worker, but it does not announce: say so rather than pretend.
        bool stopped;
        lock (_sync)
            stopped = runtime.Job.State is DownloadState.Paused or DownloadState.Queued or DownloadState.Failed or DownloadState.Cancelled
                || runtime.Job.State == DownloadState.Completed && !runtime.Job.Torrent!.Seeding;
        if (stopped) return new TorrentActionResult { Error = TorrentNotRunningAnnounceText };
        var reply = await _torrentEngine!.AnnounceAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return reply.Outcome switch
        {
            TorrentActionOutcome.Applied => new TorrentActionResult { Ok = true },
            TorrentActionOutcome.NotRunning => new TorrentActionResult { Error = TorrentNotRunningAnnounceText },
            TorrentActionOutcome.OlderWorker => new TorrentActionResult { Error = TorrentOlderWorkerText },
            _ => new TorrentActionResult { Error = $"The torrent program did not ask for peers: {reply.Message}" },
        };
    }

    // ---- details ----

    /// <summary><c>torrent.details</c>: what the Host keeps, and while the torrent runs what the worker reports.</summary>
    public async Task<TorrentDetailsResult> GetTorrentDetailsAsync(TorrentJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        var (runtime, error) = TorrentJobFor(request.JobId, caller);
        if (runtime is null) return new TorrentDetailsResult { Error = error };
        TorrentDetailsResult result;
        lock (_sync)
        {
            var job = runtime.Job;
            var info = job.Torrent!;
            result = new TorrentDetailsResult
            {
                Ok = true,
                Source = info.Magnet is null ? "file" : "magnet",
                InfoHash = info.InfoHash,
                AddedAt = job.CreatedAt,
                DestinationDirectory = job.DestinationDirectory,
                Options = OptionsOf(job.Id, info),
            };
        }

        if (await _torrentEngine!.GetDetailsAsync(request.JobId, cancellationToken).ConfigureAwait(false) is not { } details) return result;
        result.Running = true;
        result.InfoHashV2 = details.InfoHashV2.Length == 64 ? details.InfoHashV2 : null;
        result.Private = details.Private;
        result.PieceCount = details.Pieces;
        result.PieceLength = details.PieceLength;
        result.Wasted = details.Wasted;
        result.HashFailures = details.HashFailures;
        result.Connections = details.Connections;
        result.ConnectionsLimit = details.ConnectionsLimit;
        result.DistributedCopies = Math.Round(details.DistributedCopies, 3);
        result.NextAnnounce = Math.Max(0, details.NextAnnounce);
        result.Pex = details.Pex;
        result.DhtNodes = details.DhtNodes;
        return result;
    }

    /// <summary>T2's <c>torrent.network</c> with T3's DHT node count, asked of the worker only while it runs.</summary>
    public async Task<TorrentNetworkResult> GetTorrentNetworkAsync(CancellationToken cancellationToken = default)
    {
        var result = GetTorrentNetwork();
        if (result.Ok && result.Running && _torrentEngine is { } engine)
            result.DhtNodes = await engine.GetDhtNodesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ---- who watches torrents (the progress rate) ----

    private readonly object _watchSync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _torrentWatchers = [];
    private ITimer? _watchTimer;
    private bool? _progressWatched;

    /// <summary>Clients that show torrents now (tests).</summary>
    public int TorrentWatcherCount
    {
        get
        {
            lock (_watchSync) return _torrentWatchers.Count(watch => watch.Value > _timeProvider.GetUtcNow());
        }
    }

    /// <summary><c>torrent.watch</c>: <paramref name="client"/> shows torrents now (or no longer).</summary>
    public TorrentActionResult WatchTorrents(Guid client, TorrentWatchRequest request)
    {
        var lease = TimeSpan.FromSeconds(Math.Clamp(request.LeaseSeconds ?? 60, 10, 300));
        lock (_watchSync)
        {
            if (request.On) _torrentWatchers[client] = _timeProvider.GetUtcNow() + lease;
            else _torrentWatchers.Remove(client);
        }

        UpdateProgressRate();
        return new TorrentActionResult { Ok = true };
    }

    /// <summary>The client went away: its watch ends.</summary>
    public void ForgetTorrentWatcher(Guid client)
    {
        bool removed;
        lock (_watchSync) removed = _torrentWatchers.Remove(client);
        if (removed) UpdateProgressRate();
    }

    /// <summary>
    /// Progress comes every second while a client watches torrents or the overall speed limit is split (it needs fresh
    /// rates), else every 5 seconds. A timer runs only while a watch is open, to end it when its lease runs out.
    /// </summary>
    private void UpdateProgressRate()
    {
        bool watched;
        lock (_watchSync)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var expired in _torrentWatchers.Where(watch => watch.Value <= now).Select(watch => watch.Key).ToList())
                _torrentWatchers.Remove(expired);
            watched = _torrentWatchers.Count > 0;
            if (watched)
            {
                var next = _torrentWatchers.Values.Min() - now;
                _watchTimer ??= _timeProvider.CreateTimer(_ => UpdateProgressRate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _watchTimer.Change(next < TimeSpan.Zero ? TimeSpan.Zero : next, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _watchTimer?.Dispose();
                _watchTimer = null;
            }

            // Decided and told under the one lock, so two calls that cross (a watch ending while another starts, or a
            // limit change) can't leave the worker at an older rate: the engine call returns at once and its last value
            // wins. _budgetSync is only ever taken inside _watchSync, never the other way round.
            bool limited;
            lock (_budgetSync) limited = _budgetSettings.Limit is not null;
            var fast = watched || limited;
            if (_progressWatched == fast) return;
            _progressWatched = fast;
            _torrentEngine?.SetProgressWatched(fast);
        }
    }

    // ---- Options › Torrents: peers, connections and the proxy ----

    /// <summary>Why the T3 torrent settings can't be saved with <paramref name="proxy"/> (the proxy being saved), or null.</summary>
    private static string? TorrentPeerSettingsError(SettingsDto settings, ProxySettings proxy)
    {
        if (!TorrentEncryptionModes.IsKnown(settings.TorrentEncryption))
            return "Torrent encryption must be prefer, require or off.";
        if (settings.TorrentSeedsAtOnce is < 0 or > 1000)
            return "Enter torrents seeding at once from 0 to 1000 (0 is all of them).";
        if (settings.TorrentConnectionsPerTorrent is < 0 or 1 or > 2000)
            return "Enter connections per torrent from 2 to 2000, or 0 for no limit of its own.";
        if (settings.TorrentMaxConnections is < 0 or (> 0 and < 10) or > 20000)
            return "Enter torrent connections in all from 10 to 20000, or 0 for the torrent program's own default.";
        if (settings.TorrentUseProxy && TorrentProxyOf(proxy, out var problem) is null) return problem;
        return null;
    }

    /// <summary>
    /// The proxy torrents use, from Options › Proxy: one typed in there (HTTP or SOCKS5). Null with
    /// <paramref name="problem"/> in words when there is none torrents can use.
    /// </summary>
    internal static TorrentProxy? TorrentProxyOf(ProxySettings proxy, out string? problem)
    {
        const string turnOff = " or turn off \"Use the proxy\" in Options > Torrents.";
        problem = null;
        if (string.Equals(proxy.Mode, "system", StringComparison.OrdinalIgnoreCase))
        {
            problem = "Torrents can't use the system proxy. Type the proxy in Options > Proxy," + turnOff;
            return null;
        }

        if (!string.Equals(proxy.Mode, "manual", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(proxy.Address))
        {
            problem = "Torrents can use only a proxy typed in Options > Proxy. Set one there first," + turnOff;
            return null;
        }

        var (index, hostPort) = ProxyAddress.Split(proxy.Address);
        var scheme = ProxyAddress.Schemes[index];
        // The torrent engine sends host names only through SOCKS5 and HTTP proxies; with SOCKS4 it would look tracker
        // names up on this PC and drop the user ID, and it has no SOCKS4a.
        if (scheme is "socks4" or "socks4a")
        {
            problem = $"Torrents can't use a {(scheme == "socks4" ? "SOCKS4" : "SOCKS4a")} proxy. Choose SOCKS5 or HTTP in Options > Proxy," + turnOff;
            return null;
        }

        if (!Uri.TryCreate("http://" + hostPort, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.IsDefaultPort && !hostPort.EndsWith(":80", StringComparison.Ordinal))
        {
            problem = "The proxy address in Options > Proxy needs a host and a port for torrents," + turnOff;
            return null;
        }

        return new TorrentProxy(scheme, uri.IdnHost.TrimStart('[').TrimEnd(']'), uri.Port, string.IsNullOrEmpty(proxy.Username) ? null : proxy.Username, proxy.Password);
    }

    /// <summary>What the worker is told of Options › Torrents (with the proxy only when torrents use it).</summary>
    /// <remarks>Torrents set to use a proxy that can't be used get none and <see cref="TorrentPeerSettings.ProxyProblem"/>,
    /// so the worker never starts and nothing connects around the proxy.</remarks>
    internal static TorrentPeerSettings TorrentPeerSettingsOf(AppSettings settings)
    {
        string? problem = null;
        var proxy = settings.TorrentUseProxy ? TorrentProxyOf(settings.Proxy, out problem) : null;
        return new(settings.TorrentDht, settings.TorrentPex, settings.TorrentLsd, TorrentEncryptionModes.WorkerWord(settings.TorrentEncryption),
            Math.Max(0, settings.TorrentSeedsAtOnce), Math.Max(0, settings.TorrentConnectionsPerTorrent), Math.Max(0, settings.TorrentMaxConnections),
            proxy) { ProxyProblem = settings.TorrentUseProxy && proxy is null ? problem : null };
    }

    private void ApplyTorrentPeerSettings(AppSettings settings) => _torrentEngine?.UpdatePeerSettings(TorrentPeerSettingsOf(settings));
}
