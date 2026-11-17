using System.Globalization;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// Site transfers (SITES.4; remote-sites.md, "Transfers"): download and upload batches, moved file by file by rclone's
/// <c>operations/copyfile</c> as async rc jobs.
/// <list type="bullet">
/// <item>A batch is written to <c>sites/transfers/{id}.json</c> before anything moves. Progress is saved at most every
/// <see cref="FlushDelay"/>; a change of state is saved at once. A batch found running at start may have been cut by a
/// kill: before its files run again, rclone's <c>.partial</c> files for its unfinished files are removed
/// (<see cref="SiteTransferRecord.SweepPending"/>), and the files that were moving start again from zero.</item>
/// <item>At most "transfers at once" files move at a time across all sites, oldest batch first. A failed file does not
/// stop its batch: it is tried again after 2, 4, 8 and 16 seconds, then fails with a plain reason. A changed host key or
/// a refused sign-in stops the whole batch. When the server can't be reached the batch waits (2 s doubling to a minute)
/// without spending its files' tries, and stops after five such waits in a row.</item>
/// <item>Pause stops the files in flight (rclone removes their <c>.partial</c>); Resume starts them again from zero.</item>
/// <item>Nothing runs, and no timer is set, while no batch is running.</item>
/// </list>
/// Bytes go straight to the target folder (the payload rule): rclone writes <c>name.&lt;token&gt;.partial</c> next to the
/// target and renames it in place.
/// </summary>
public sealed class SiteTransfers : IAsyncDisposable
{
    /// <summary>The most files one batch may hold.</summary>
    public const int MaxItems = 20_000;

    /// <summary>A file is tried this many times before it fails.</summary>
    public const int MaxTries = 5;

    public static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BroadcastDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    private sealed class Batch(SiteTransferRecord record)
    {
        public SiteTransferRecord Record { get; } = record;
        public Dictionary<SiteTransferItem, Running> Active { get; } = new(ReferenceEqualityComparer.Instance);
        public long Speed { get; set; }
        public long InFlight { get; set; }
        public bool Dirty { get; set; }
        public bool Removed { get; set; }

        /// <summary>The sweep of <see cref="SiteTransferRecord.SweepPending"/> is running.</summary>
        public bool Sweeping { get; set; }

        /// <summary>Connection failures in a row; the batch waits until <see cref="RetryAt"/> before its next file.</summary>
        public int ConnectionFailures { get; set; }

        public DateTimeOffset? RetryAt { get; set; }

        /// <summary>SITES.11: the connection error the batch waits out until <see cref="RetryAt"/>.</summary>
        public string? RetryReason { get; set; }

        /// <summary>SITES.11: the picked names, worked out once (an item's path never changes).</summary>
        public (List<string> First, int Count)? Picked { get; set; }

        public string Group => "inbound-transfer-" + Record.Id;
    }

    private sealed class Running
    {
        // Never disposed: it holds no timer, and a late Cancel on a disposed source would throw.
        public CancellationTokenSource Stop { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>The <c>srcRemote</c> its copy was given: rclone's stats name a moving file by it (checked with rclone 1.76).</summary>
        public string? Source { get; set; }

        /// <summary>SITES.11: the user stopped this file on its own; when its copy ends cut, it is left stopped, not waiting.</summary>
        public bool StopFile { get; set; }

        /// <summary>SITES.11: as rclone last reported this file; null until it has been seen moving.</summary>
        public (long BytesDone, long Speed)? Progress { get; set; }
    }

    /// <summary>A file that can't be moved however often it is tried.</summary>
    private sealed class ItemFailure(string message) : Exception(message);

    /// <summary>Connection failures in a row after which a batch stops instead of waiting again.</summary>
    public const int MaxConnectionFailures = 5;

    private readonly SiteTransferStore _store;
    private readonly string _stateDirectory;
    private readonly Func<string, CancellationToken, Task<SiteRecord?>> _getSite;
    private readonly Func<CancellationToken, Task<RcloneLease>> _acquire;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _limit;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _firstRetryDelay;
    private readonly ISleepPrevention _sleep;
    private readonly Func<bool> _preventSleep;
    // Taken before _sync, never inside it: decided and applied in one go, so two callers can't apply answers out of order.
    private readonly Lock _sleepGate = new();
    private bool _sleepHeld;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly List<Batch> _batches = [];
    private int _running;
    private ITimer? _retryTimer;
    private ITimer? _flushTimer;
    private ITimer? _broadcastTimer;
    private Task? _progress;
    private bool _disposed;
    /// <summary>A batch is running and Sites is on, as last reported through <see cref="ActivityChanged"/>.</summary>
    private bool _active;
    /// <summary>The share of the overall speed limit (bytes/s; null for none), and what rclone was last told, by process.</summary>
    private long? _speedLimit;
    private (int ProcessId, long? Limit)? _speedApplied;
    private readonly SemaphoreSlim _speedGate = new(1, 1);

    /// <summary>A batch started running, or none runs any more (Host: the overall speed limit is split again).</summary>
    public event Action? ActivityChanged;

    /// <summary>The speeds were read from rclone (about once a second while files move).</summary>
    public event Action? Measured;

    /// <summary>
    /// SITES.11: a file reached the site (true), or the site could not be reached or refused the sign-in (false); for the
    /// site's reach. One file's own failure says nothing about the site.
    /// </summary>
    public event Action<string, bool>? Reached;

    /// <param name="getSite">The saved site by id, or null when it is gone.</param>
    /// <param name="acquire">A lease on rclone (starting it when needed).</param>
    /// <param name="enabled">Sites is on: with it off nothing starts.</param>
    /// <param name="limit">"Transfers at once".</param>
    /// <param name="firstRetryDelay">The wait before a failed file's second try; it doubles after each (2 s by default).</param>
    /// <param name="sleep">Keeps the PC awake while a batch runs (a share of the Host's power request).</param>
    /// <param name="preventSleep">Options › "Prevent sleep while downloading", which covers site transfers too.</param>
    public SiteTransfers(string stateDirectory, Func<string, CancellationToken, Task<SiteRecord?>> getSite,
        Func<CancellationToken, Task<RcloneLease>> acquire, Func<bool> enabled, Func<int> limit, Action<string> log,
        TimeProvider? time = null, TimeSpan? firstRetryDelay = null, ISleepPrevention? sleep = null, Func<bool>? preventSleep = null)
    {
        _sleep = sleep ?? NullSleepPrevention.Instance;
        _preventSleep = preventSleep ?? (() => true);
        _stateDirectory = stateDirectory;
        _store = new SiteTransferStore(stateDirectory, log);
        _getSite = getSite;
        _acquire = acquire;
        _enabled = enabled;
        _limit = limit;
        _log = log;
        _time = time ?? TimeProvider.System;
        _firstRetryDelay = firstRetryDelay ?? TimeSpan.FromSeconds(2);

        foreach (var record in _store.LoadAll())
        {
            var batch = new Batch(record);
            foreach (var item in record.Items)
            {
                if (item.State != SiteTransferItemStates.Running) continue;
                item.State = SiteTransferItemStates.Waiting;
                batch.Dirty = true;
            }

            // Running when the Host stopped: a clean stop leaves it so too, but a kill may have left partial files, and
            // which file was moving is not saved. So its unfinished files' folders are looked at once before it goes on.
            if (record.State == SiteTransferStates.Running && !record.SweepPending && record.Items.Any(NotFinished))
            {
                record.SweepPending = true;
                batch.Dirty = true;
            }

            _batches.Add(batch);
        }

        if (_batches.Any(b => b.Dirty)) ScheduleFlushLocked();
    }

    /// <summary>
    /// SITES.11 (decisions 1 and 2): where a download goes when the request names no folder and the site has no linked
    /// folder, by the picked names (the Host's category rules); null when there is no such rule here.
    /// </summary>
    public Func<IReadOnlyList<SiteTransferNameDto>, string?>? DownloadFolder { get; set; }

    /// <summary>After any change, at most about twice a second; for <c>sites.transfer.changed</c>.</summary>
    public event Action<SiteTransfersDto>? Changed;

    // ---- Commands ------------------------------------------------------------------------------------------

    public SiteTransfersDto List()
    {
        lock (_sync) return SnapshotLocked();
    }

    /// <summary>The folder on this computer a batch reads or writes; null when there is no such batch.</summary>
    public string? LocalFolderOf(string id)
    {
        lock (_sync) return _batches.FirstOrDefault(b => b.Record.Id == id)?.Record.LocalFolder;
    }

    /// <summary>
    /// SITES.5: whether a batch that may still move files (running, paused, or stopped with files waiting) reads or writes
    /// <paramref name="path"/> on that site, or anything inside it, or lands inside it: such a path is not renamed or
    /// deleted under the batch.
    /// </summary>
    public bool Uses(string siteId, string path)
    {
        static bool Within(string candidate, string root) =>
            root == "/" || candidate == root || candidate.StartsWith(root + "/", StringComparison.Ordinal);

        lock (_sync)
        {
            foreach (var batch in _batches)
            {
                var record = batch.Record;
                if (record.SiteId != siteId) continue;
                if (record.State is not (SiteTransferStates.Running or SiteTransferStates.Paused) && !StoppedLocked(batch)) continue;
                if (Within(record.RemoteFolder, path)) return true;
                foreach (var item in record.Items)
                {
                    if (!NotFinished(item)) continue;
                    if (Within(SiteTransferPaths.Remote(record.RemoteFolder, item.Path), path)) return true;
                    // An upload kept both under a new name writes that name.
                    if (record.Direction == SiteTransferDirections.Upload && item.Target is { } target
                        && Within(SiteTransferPaths.Remote(record.RemoteFolder, target), path)) return true;
                }
            }
        }

        return false;
    }

    /// <summary>Batches running now (moving or waiting their turn), for the tray.</summary>
    public int RunningCount()
    {
        lock (_sync) return _batches.Count(b => b.Record.State == SiteTransferStates.Running);
    }

    public SiteTransferResultDto Get(string id)
    {
        lock (_sync)
        {
            if (Find(id) is not { } batch) return Fail("That transfer is no longer listed.");
            return new SiteTransferResultDto
            {
                Ok = true,
                Transfer = ToDto(batch),
                Items = [.. batch.Record.Items.Select(i => new SiteTransferItemDto
                {
                    Path = i.Path,
                    Target = i.Target,
                    Size = i.Size,
                    State = i.State,
                    Tries = i.Tries,
                    Error = i.Error,
                    IsFolder = i.Folder,
                })],
            };
        }
    }

    public async Task<SiteTransferResultDto> StartAsync(SiteTransferStartRequest request, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(request.SiteId) || await _getSite(request.SiteId, cancellationToken).ConfigureAwait(false) is not { } site)
            return Fail("That remote no longer exists.");
        if (request.Direction is not (SiteTransferDirections.Download or SiteTransferDirections.Upload)) return Fail("Choose download or upload.");
        var conflict = string.IsNullOrEmpty(request.Conflict) ? SiteTransferConflicts.Skip : request.Conflict;
        if (!SiteTransferConflicts.IsKnown(conflict)) return Fail("Choose Skip, Replace or Keep both for files that are already there.");
        if (SitePaths.Normalize(request.RemoteFolder) is not { } remoteFolder) return Fail("That is not a folder path on the server.");
        var localFolder = !string.IsNullOrWhiteSpace(request.LocalFolder) ? request.LocalFolder.Trim()
            : site.LocalFolder
            // The same guards follow as for a folder given: a full path, outside Inbound's data; made when missing.
            ?? (request.Direction == SiteTransferDirections.Download && request.Names is { Count: > 0 } picked && picked.All(n => n?.Name is not null) ? DownloadFolder?.Invoke(picked) : null);
        if (localFolder is null || !Path.IsPathFullyQualified(localFolder)) return Fail("Choose a folder on this PC.");
        localFolder = Path.GetFullPath(localFolder);
        if (IsInside(localFolder, _stateDirectory)) return Fail("Files can't be placed in Inbound's own data folder; choose another folder.");
        if (request.Names is null || request.Names.Count == 0 || request.Names.Any(n => n is null)) return Fail("Choose the files to move.");
        if (request.Names.Count > MaxItems) return Fail($"A transfer can hold at most {MaxItems:N0} files.");
        foreach (var name in request.Names)
        {
            if (!SiteTransferPaths.IsRelative(name.Name) || name.Name.Contains('/', StringComparison.Ordinal)
                || (request.Direction == SiteTransferDirections.Upload && name.Name != Path.GetFileName(name.Name)))
                return Fail("A chosen name is not a file or folder in that folder.");
        }

        if (site.NeedsHostKey)
            return new SiteTransferResultDto { Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet." };

        var names = request.Names.Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();
        List<SiteTransferItem> items;
        try
        {
            items = request.Direction == SiteTransferDirections.Download
                ? await ListDownloadAsync(site, remoteFolder, names, cancellationToken).ConfigureAwait(false)
                : ListUpload(localFolder, names, SiteKindCatalog.Has(site.Kind, SiteCapabilities.EmptyFolders));
        }
        catch (ItemFailure e)
        {
            return Fail(e.Message);
        }
        catch (RcloneCallException e) when (SshHostKeyProblem.Parse(e.Message) is not null)
        {
            return new SiteTransferResultDto { Ok = false, Problem = SiteProblems.HostKeyChanged, Message = HostKeyMessage(SshHostKeyProblem.Parse(e.Message)!) };
        }
        catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or SiteStoreException)
        {
            return Fail(SiteErrorText.Friendly(e.Message));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"The folder on this PC could not be read: {e.Message}");
        }

        if (items.Count == 0) return Fail("Nothing was chosen.");
        if (items.Count > MaxItems) return Fail($"That is {items.Count:N0} files; a transfer can hold at most {MaxItems:N0}.");

        if (request.Direction == SiteTransferDirections.Download)
        {
            // Windows (and macOS) can't hold two names that differ only in case: the second would write over the first.
            var seen = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var item in items)
            {
                var problem = SiteTransferPaths.LocalPathProblem(item.Path)
                    ?? (seen.Add(item.Path) ? null : "Another file in this transfer has the same name apart from upper and lower case; this PC can't hold both.");
                if (problem is null) continue;
                item.State = SiteTransferItemStates.Failed;
                item.Error = problem;
            }
        }

        var record = new SiteTransferRecord
        {
            Id = SiteTransferStore.NewId(),
            SiteId = site.Id,
            SiteName = site.Name,
            Direction = request.Direction,
            RemoteFolder = remoteFolder,
            LocalFolder = localFolder,
            Conflict = conflict,
            State = SiteTransferStates.Running,
            Items = items,
            CreatedAt = _time.GetUtcNow(),
        };
        var batch = new Batch(record);
        // Stored before anything moves: a Host that stops now finds the batch at its next start.
        try
        {
            await SaveAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"The transfer could not be saved: {e.Message}");
        }

        SiteTransferDto dto;
        bool stopping;
        lock (_sync)
        {
            stopping = _disposed;
            if (!stopping) _batches.Add(batch);
        }

        if (stopping)
        {
            // Not kept: the user was told it did not start, so it must not start by itself at the next Host start.
            await DeleteQuietlyAsync(batch).ConfigureAwait(false);
            return Fail("Remote files are stopping.");
        }

        lock (_sync)
        {
            FinishIfDoneLocked(batch);
            dto = ToDto(batch);
        }

        _log($"Site transfer {record.Id}: {record.Direction} of {items.Count} file(s) started.");
        Pump();
        RequestBroadcast();
        return new SiteTransferResultDto { Ok = true, Transfer = dto };
    }

    public Task<SiteTransferResultDto> PauseAsync(string id) => StopAsync(id, SiteTransferStates.Paused, null);

    public Task<SiteTransferResultDto> CancelAsync(string id) => StopAsync(id, SiteTransferStates.Canceled, null);

    public async Task<SiteTransferResultDto> ResumeAsync(string id)
    {
        Batch batch;
        lock (_sync)
        {
            if (Find(id) is not { } found) return Fail("That transfer is no longer listed.");
            batch = found;
            if (batch.Record.State != SiteTransferStates.Paused && !StoppedLocked(batch))
                return batch.Record.State == SiteTransferStates.Running ? Ok(batch) : Fail("Only a paused transfer can be resumed.");
            batch.Record.State = SiteTransferStates.Running;
            batch.Record.Problem = null;
            batch.Record.Message = null;
            batch.Record.FinishedAt = null;
            (batch.ConnectionFailures, batch.RetryAt) = (0, null);
            FinishIfDoneLocked(batch);
        }

        await SaveQuietlyAsync(batch).ConfigureAwait(false);
        Pump();
        RequestBroadcast();
        lock (_sync) return Ok(batch);
    }

    /// <summary>
    /// The failed files are tried again; files the user stopped stay stopped (<see cref="StartFileAsync"/>). A paused batch stays paused (they run at Resume); a canceled one is not brought back.
    /// </summary>
    public async Task<SiteTransferResultDto> RetryAsync(string id)
    {
        Batch batch;
        lock (_sync)
        {
            if (Find(id) is not { } found) return Fail("That transfer is no longer listed.");
            batch = found;
            if (batch.Record.State == SiteTransferStates.Canceled) return Fail("A canceled transfer can't be retried; start it again.");
            var any = false;
            foreach (var item in batch.Record.Items.Where(i => i.State == SiteTransferItemStates.Failed))
            {
                item.State = SiteTransferItemStates.Waiting;
                item.Tries = 0;
                item.Error = null;
                item.NotBefore = null;
                any = true;
            }

            if (!any && !StoppedLocked(batch)) return Ok(batch);
            if (batch.Record.State != SiteTransferStates.Paused)
            {
                batch.Record.State = SiteTransferStates.Running;
                batch.Record.Problem = null;
                batch.Record.Message = null;
                batch.Record.FinishedAt = null;
                (batch.ConnectionFailures, batch.RetryAt) = (0, null);
                FinishIfDoneLocked(batch);
            }
        }

        await SaveQuietlyAsync(batch).ConfigureAwait(false);
        Pump();
        RequestBroadcast();
        lock (_sync) return Ok(batch);
    }

    /// <summary>
    /// SITES.11: one file is stopped and left <c>stopped</c>; the batch goes on with the others (and finishes without it).
    /// A moving file is cut and waited for; rclone removes its partial file as for a pause. A finished file is left as it is.
    /// </summary>
    public async Task<SiteTransferResultDto> StopFileAsync(string id, string path)
    {
        Batch batch;
        Running? run;
        lock (_sync)
        {
            if (Find(id) is not { } found) return Fail("That transfer is no longer listed.");
            batch = found;
            if (batch.Record.State == SiteTransferStates.Canceled) return Ok(batch);
            if (batch.Record.Items.FirstOrDefault(i => i.Path == path) is not { } item) return Fail("That file is not in this transfer.");
            if (!NotFinished(item)) return Ok(batch);
            if (batch.Active.TryGetValue(item, out run))
            {
                run.StopFile = true;
            }
            else
            {
                item.State = SiteTransferItemStates.Stopped;
                item.Error = null;
                item.NotBefore = null;
                FinishIfDoneLocked(batch);
            }
        }

        if (run is not null)
        {
            // Outside the lock: the cancel may run the file's ending inline, which takes the lock. The ending saves.
            run.Stop.Cancel();
            await WaitQuietlyAsync([run.Task]).ConfigureAwait(false);
        }
        else
        {
            await SaveQuietlyAsync(batch).ConfigureAwait(false);
        }

        RequestBroadcast();
        lock (_sync) return Ok(batch);
    }

    /// <summary>
    /// SITES.11: a file the user stopped (or one that failed) goes back in line. A finished batch runs again for it; a
    /// paused one stays paused (it runs at Resume); a canceled one is not brought back.
    /// </summary>
    public async Task<SiteTransferResultDto> StartFileAsync(string id, string path)
    {
        Batch batch;
        lock (_sync)
        {
            if (Find(id) is not { } found) return Fail("That transfer is no longer listed.");
            batch = found;
            if (batch.Record.State == SiteTransferStates.Canceled) return Fail("A canceled transfer can't be started again; start it anew.");
            if (batch.Record.Items.FirstOrDefault(i => i.Path == path) is not { } item) return Fail("That file is not in this transfer.");
            if (item.State is not (SiteTransferItemStates.Stopped or SiteTransferItemStates.Failed)) return Ok(batch);
            item.State = SiteTransferItemStates.Waiting;
            item.Tries = 0;
            item.Error = null;
            item.NotBefore = null;
            if (batch.Record.State is SiteTransferStates.Done or SiteTransferStates.Failed)
            {
                batch.Record.State = SiteTransferStates.Running;
                batch.Record.Problem = null;
                batch.Record.Message = null;
                batch.Record.FinishedAt = null;
                (batch.ConnectionFailures, batch.RetryAt) = (0, null);
            }
        }

        await SaveQuietlyAsync(batch).ConfigureAwait(false);
        Pump();
        RequestBroadcast();
        lock (_sync) return Ok(batch);
    }

    /// <summary>
    /// Failed because something stopped the whole batch (a host key, a refused sign-in, a server out of reach), not
    /// because its files ran out of tries: files are still waiting, or a problem is set.
    /// </summary>
    private static bool StoppedLocked(Batch batch) =>
        batch.Record.State == SiteTransferStates.Failed
        && (batch.Record.Problem is not null || batch.Record.Items.Any(i => i.State == SiteTransferItemStates.Waiting));

    /// <summary>Removes one finished batch, or every finished batch for an empty id. The files moved stay where they are.</summary>
    public async Task<SiteTransferResultDto> ClearAsync(string id)
    {
        List<Batch> gone;
        lock (_sync)
        {
            if (string.IsNullOrEmpty(id))
            {
                gone = [.. _batches.Where(b => SiteTransferStates.IsFinished(b.Record.State) && b.Active.Count == 0)];
            }
            else
            {
                if (Find(id) is not { } batch) return Fail("That transfer is no longer listed.");
                if (!SiteTransferStates.IsFinished(batch.Record.State) || batch.Active.Count > 0) return Fail("Stop the transfer before removing it from the list.");
                gone = [batch];
            }

            foreach (var batch in gone)
            {
                batch.Removed = true;
                _batches.Remove(batch);
            }
        }

        foreach (var batch in gone) await DeleteQuietlyAsync(batch).ConfigureAwait(false);
        RequestBroadcast();
        return new SiteTransferResultDto { Ok = true };
    }

    private async Task DeleteQuietlyAsync(Batch batch)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync) batch.Removed = true;
            _store.Delete(batch.Record.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"Site transfer {batch.Record.Id} could not be removed: {e.Message}");
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Sites was turned off: every running batch pauses, and says why.</summary>
    public async Task PauseAllAsync(string message)
    {
        List<string> ids;
        lock (_sync) ids = [.. _batches.Where(b => b.Record.State == SiteTransferStates.Running).Select(b => b.Record.Id)];
        foreach (var id in ids) await StopAsync(id, SiteTransferStates.Paused, message).ConfigureAwait(false);
    }

    /// <summary>The message of a batch a schedule paused; a schedule's start resumes only those.</summary>
    public const string ScheduledPauseMessage = "Paused by a schedule.";

    /// <summary>A schedule's stop (SITES.4): every running batch is paused.</summary>
    public Task PauseForScheduleAsync() => PauseAllAsync(ScheduledPauseMessage);

    /// <summary>A schedule's start: the batches a schedule paused go on; ones paused by hand, or when Sites was turned off,
    /// stay paused. Nothing while Sites is off.</summary>
    public async Task ResumeForScheduleAsync()
    {
        List<string> ids;
        lock (_sync)
        {
            if (_disposed || !_enabled()) return;
            ids = [.. _batches.Where(b => b.Record.State == SiteTransferStates.Paused && b.Record.Message == ScheduledPauseMessage)
                .Select(b => b.Record.Id)];
        }

        foreach (var id in ids) await ResumeAsync(id).ConfigureAwait(false);
    }

    /// <summary>The site was removed: its batches that are not finished are canceled.</summary>
    public async Task CancelSiteAsync(string siteId, string message)
    {
        List<string> ids;
        lock (_sync)
        {
            ids = [.. _batches.Where(b => b.Record.SiteId == siteId && !SiteTransferStates.IsFinished(b.Record.State)).Select(b => b.Record.Id)];
        }

        foreach (var id in ids) await StopAsync(id, SiteTransferStates.Canceled, message).ConfigureAwait(false);
    }

    private async Task<SiteTransferResultDto> StopAsync(string id, string state, string? message)
    {
        Batch batch;
        (CancellationTokenSource[] Stops, Task[] Tasks) stopping;
        lock (_sync)
        {
            if (Find(id) is not { } found) return Fail("That transfer is no longer listed.");
            batch = found;
            var current = batch.Record.State;
            if (state == SiteTransferStates.Paused && current != SiteTransferStates.Running)
                return current == SiteTransferStates.Paused ? Ok(batch) : Fail("Only a running transfer can be paused.");
            if (state == SiteTransferStates.Canceled && SiteTransferStates.IsFinished(current) && current != SiteTransferStates.Failed)
                return Ok(batch);
            batch.Record.State = state;
            batch.Record.Message = message;
            if (state == SiteTransferStates.Canceled) batch.Record.FinishedAt = _time.GetUtcNow();
            stopping = ActiveLocked(batch);
        }

        await StopAndWaitAsync(stopping).ConfigureAwait(false);
        if (stopping.Tasks.Length > 0) await StopGroupAsync(batch).ConfigureAwait(false);
        await SaveQuietlyAsync(batch).ConfigureAwait(false);
        UpdateSleep();
        RequestBroadcast();
        lock (_sync) return Ok(batch);
    }

    /// <summary>
    /// The batch's files in flight. They are cancelled outside the lock (<see cref="StopAndWaitAsync"/>): a cancel can run
    /// a file's ending inline, and that takes the lock and changes <see cref="Batch.Active"/>.
    /// </summary>
    private static (CancellationTokenSource[] Stops, Task[] Tasks) ActiveLocked(Batch batch) =>
        ([.. batch.Active.Values.Select(r => r.Stop)], [.. batch.Active.Values.Select(r => r.Task)]);

    private async Task StopAndWaitAsync((CancellationTokenSource[] Stops, Task[] Tasks) active)
    {
        foreach (var stop in active.Stops) stop.Cancel();
        await WaitQuietlyAsync(active.Tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// After a batch's files were stopped: anything rclone still runs for its group is stopped too (a job whose id was
    /// never read, or one whose stop was not confirmed). Only while rclone runs anyway.
    /// </summary>
    private async Task StopGroupAsync(Batch batch)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var lease = await _acquire(limit.Token).ConfigureAwait(false);
            await lease.Client.CallAsync("job/stopgroup", new JsonObject { ["group"] = batch.Group }, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or RcloneCallException or RcloneUnavailableException)
        {
            // Nothing of the group is running, or rclone is gone, which stopped it too.
        }
    }

    private async Task WaitQuietlyAsync(Task[] tasks)
    {
        if (tasks.Length == 0) return;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(StopWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log("A site transfer did not stop in time; it stops in the background.");
        }
        catch (Exception)
        {
            // Each file's task handles its own errors; nothing here is new.
        }
    }

    // ---- Listing a batch's files ------------------------------------------------------------------------------

    private async Task<List<SiteTransferItem>> ListDownloadAsync(SiteRecord site, string remoteFolder, List<string> names, CancellationToken cancellationToken)
    {
        using var lease = await _acquire(cancellationToken).ConfigureAwait(false);
        var fs = await SiteRemote.BuildAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
        var folder = await lease.Client.CallAsync("operations/list", new JsonObject
        {
            ["fs"] = fs.DeepClone(),
            ["remote"] = SitePaths.ToRemote(remoteFolder),
            ["opt"] = new JsonObject { ["noMimeType"] = true },
        }, cancellationToken).ConfigureAwait(false);
        var here = new Dictionary<string, SiteItemDto>(StringComparer.Ordinal);
        foreach (var entry in SiteListing.Read(folder)) here.TryAdd(entry.Name, entry);

        var items = new List<SiteTransferItem>();
        foreach (var name in names)
        {
            if (!here.TryGetValue(name, out var entry)) throw new ItemFailure($"\"{name}\" is no longer in that folder. Refresh and choose again.");
            if (!entry.IsFolder)
            {
                items.Add(new SiteTransferItem { Path = name, Size = entry.Size });
                continue;
            }

            var inside = SitePaths.ToRemote(SiteTransferPaths.Remote(remoteFolder, name));
            var listing = await lease.Client.CallAsync("operations/list", new JsonObject
            {
                ["fs"] = fs.DeepClone(),
                ["remote"] = inside,
                ["opt"] = new JsonObject { ["recurse"] = true, ["noMimeType"] = true },
            }, cancellationToken).ConfigureAwait(false);
            var entries = new List<string>();
            var folders = new List<string>();
            if (listing["list"] is JsonArray files)
            {
                foreach (var node in files)
                {
                    if (node is not JsonObject file || file["Path"]?.GetValue<string>() is not { } path) continue;
                    if (!path.StartsWith(inside + "/", StringComparison.Ordinal)) continue;
                    var relative = name + "/" + path[(inside.Length + 1)..];
                    if (!SiteTransferPaths.IsRelative(relative)) continue;
                    entries.Add(relative);
                    if (file["IsDir"] is JsonValue dir && dir.TryGetValue<bool>(out var isDir) && isDir)
                    {
                        folders.Add(relative);
                        continue;
                    }

                    var size = file["Size"] is JsonValue s && s.TryGetValue<long>(out var bytes) && bytes >= 0 ? bytes : -1;
                    items.Add(new SiteTransferItem { Path = relative, Size = size });
                    if (items.Count > MaxItems) return items;
                }
            }

            items.AddRange(EmptyFolders(name, folders, entries));
            if (items.Count > MaxItems) return items;
        }

        return items;
    }

    /// <summary>
    /// The folders with nothing inside, as items that are made at the target: only the deepest ones, since making those
    /// makes the ones above. <paramref name="top"/> itself when it holds nothing at all.
    /// </summary>
    private static IEnumerable<SiteTransferItem> EmptyFolders(string top, List<string> folders, List<string> entries)
    {
        if (entries.Count == 0)
        {
            yield return new SiteTransferItem { Path = top, Size = 0, Folder = true };
            yield break;
        }

        // Every folder that holds something: each entry's parents.
        var holding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            for (var cut = entry.LastIndexOf('/'); cut > 0; cut = entry.LastIndexOf('/', cut - 1))
            {
                if (!holding.Add(entry[..cut])) break;
            }
        }

        foreach (var folder in folders)
        {
            if (!holding.Contains(folder)) yield return new SiteTransferItem { Path = folder, Size = 0, Folder = true };
        }
    }

    /// <summary>
    /// The files to upload. <paramref name="emptyFolders"/> false (an object store, SITES.6): an empty folder can't exist
    /// there, so it is not an item.
    /// </summary>
    private static List<SiteTransferItem> ListUpload(string localFolder, List<string> names, bool emptyFolders)
    {
        var items = new List<SiteTransferItem>();
        // Links and junctions are not followed: a loop or a folder elsewhere on the PC is never uploaded by surprise.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var name in names)
        {
            var full = Path.Combine(localFolder, name);
            var info = new FileInfo(full);
            if (info.Exists)
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ItemFailure($"\"{name}\" is a link; choose the file it points to.");
                items.Add(new SiteTransferItem { Path = name, Size = info.Length });
                continue;
            }

            var folder = new DirectoryInfo(full);
            if (!folder.Exists) throw new ItemFailure($"\"{name}\" is no longer in that folder.");
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ItemFailure($"\"{name}\" is a link; choose the folder it points to.");
            var entries = new List<string>();
            var folders = new List<string>();
            foreach (var entry in folder.EnumerateFileSystemInfos("*", options))
            {
                var relative = Path.GetRelativePath(localFolder, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (!SiteTransferPaths.IsRelative(relative)) continue;
                entries.Add(relative);
                if (entry is DirectoryInfo)
                {
                    folders.Add(relative);
                    continue;
                }

                items.Add(new SiteTransferItem { Path = relative, Size = ((FileInfo)entry).Length });
                if (items.Count > MaxItems) return items;
            }

            if (emptyFolders) items.AddRange(EmptyFolders(name, folders, entries));
            if (items.Count > MaxItems) return items;
        }

        return items;
    }

    // ---- Scheduling ---------------------------------------------------------------------------------------------

    /// <summary>Starts waiting files while there is room, oldest batch first, and sets the timer for the next retry.</summary>
    public void Pump()
    {
        // Before any file starts, so the speed share for a batch that just started is in place before its first copy.
        UpdateActivity();
        var started = false;
        lock (_sync)
        {
            if (_disposed || !_enabled()) return;
            var now = _time.GetUtcNow();
            var limit = _limit();
            DateTimeOffset? nextRetry = null;
            // A sweep runs alone: nothing new starts while one waits or runs, so it never takes a partial file that a
            // copy of this run is writing (two batches may share a target folder).
            var sweep = _batches.FirstOrDefault(b => b.Record.State == SiteTransferStates.Running && b.Record.SweepPending);
            if (sweep is not null)
            {
                if (_running == 0 && !_batches.Any(b => b.Sweeping))
                {
                    sweep.Sweeping = true;
                    _running++;
                    var swept = sweep;
                    _ = Task.Run(() => SweepAsync(swept));
                }

                return;
            }

            foreach (var batch in _batches)
            {
                if (batch.Record.State != SiteTransferStates.Running) continue;
                if (batch.RetryAt is { } wait && wait > now)
                {
                    if (nextRetry is null || wait < nextRetry) nextRetry = wait;
                    continue;
                }

                foreach (var item in batch.Record.Items)
                {
                    if (item.State != SiteTransferItemStates.Waiting) continue;
                    if (item.NotBefore is { } notBefore && notBefore > now)
                    {
                        if (nextRetry is null || notBefore < nextRetry) nextRetry = notBefore;
                        continue;
                    }

                    if (_running >= limit) continue;
                    item.State = SiteTransferItemStates.Running;
                    _running++;
                    var run = new Running();
                    batch.Active[item] = run;
                    var (b, i) = (batch, item);
                    run.Task = Task.Run(() => RunItemAsync(b, i, run));
                    started = true;
                }

                FinishIfDoneLocked(batch);
            }

            _retryTimer?.Dispose();
            _retryTimer = null;
            if (nextRetry is { } at)
            {
                var wait = at - now;
                _retryTimer = _time.CreateTimer(_ => Pump(), null, wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }

            if (started && _progress is null) _progress = Task.Run(ProgressLoopAsync);
        }

        if (started) RequestBroadcast();
        UpdateSleep();
    }

    /// <summary>Whether a batch runs and how fast all of them go together: their demand in the overall speed limit's split.</summary>
    public SpeedDemand Demand()
    {
        lock (_sync)
        {
            var active = !_disposed && _enabled() && _batches.Any(b => b.Record.State == SiteTransferStates.Running);
            return new SpeedDemand(active, _batches.Where(b => b.Active.Count > 0).Sum(b => (double)b.Speed));
        }
    }

    /// <summary>Site transfers' share of the overall speed limit (bytes/s), or null for none. rclone is told before the next
    /// file starts, or within a second while files move.</summary>
    public void SetSpeedLimit(long? bytesPerSecond)
    {
        lock (_sync) _speedLimit = bytesPerSecond is > 0 ? bytesPerSecond : null;
    }

    /// <summary>Tells this rclone process the speed share when it differs from what it was last told.</summary>
    private async Task ApplySpeedLimitAsync(RcloneLease lease, CancellationToken token)
    {
        await _speedGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long? limit;
            lock (_sync) limit = _speedLimit;
            if (_speedApplied == (lease.ProcessId, limit)) return;
            // A plain number would be read as KiB; the B suffix makes it bytes. "off" lifts the limit.
            var rate = limit is { } bytes ? bytes.ToString(CultureInfo.InvariantCulture) + "B" : "off";
            await lease.Client.CallAsync("core/bwlimit", new JsonObject { ["rate"] = rate }, token).ConfigureAwait(false);
            _speedApplied = (lease.ProcessId, limit);
        }
        catch (RcloneCallException e)
        {
            // The copy still runs; the next file or second tries again.
            _log($"Site transfers: the speed limit could not be set: {e.Message}");
        }
        finally
        {
            _speedGate.Release();
        }
    }

    /// <summary>Raises <see cref="ActivityChanged"/> when a batch starts running or none runs any more.</summary>
    private void UpdateActivity()
    {
        bool changed;
        lock (_sync)
        {
            var active = !_disposed && _enabled() && _batches.Any(b => b.Record.State == SiteTransferStates.Running);
            changed = active != _active;
            _active = active;
        }

        if (!changed) return;
        try
        {
            ActivityChanged?.Invoke();
        }
        catch (Exception e)
        {
            _log($"Site transfers: a listener failed: {e.Message}");
        }
    }

    /// <summary>
    /// The PC stays awake while a batch is running (moving, or waiting for a retry or a free place) and Sites is on, as
    /// downloads do; not for a paused one.
    /// </summary>
    private void UpdateSleep()
    {
        UpdateActivity();
        lock (_sleepGate)
        {
            bool hold;
            lock (_sync) hold = !_disposed && _enabled() && _batches.Any(b => b.Record.State == SiteTransferStates.Running);
            try
            {
                hold = hold && _preventSleep();
                if (hold == _sleepHeld) return;
                _sleepHeld = hold;
                _sleep.SetActive(hold);
            }
            catch (Exception e)
            {
                _log($"Site transfers: sleep prevention could not be changed: {e.Message}");
            }
        }
    }

    /// <summary>A running batch with nothing left to move is done, or failed when a file failed.</summary>
    private void FinishIfDoneLocked(Batch batch)
    {
        if (batch.Record.State != SiteTransferStates.Running || batch.Active.Count > 0) return;
        if (batch.Record.Items.Any(i => i.State is SiteTransferItemStates.Waiting or SiteTransferItemStates.Running)) return;
        var failed = batch.Record.Items.Count(i => i.State == SiteTransferItemStates.Failed);
        batch.Record.State = failed > 0 ? SiteTransferStates.Failed : SiteTransferStates.Done;
        batch.Record.FinishedAt = _time.GetUtcNow();
        batch.Speed = 0;
        batch.InFlight = 0;
        _log($"Site transfer {batch.Record.Id} finished: {batch.Record.State}{(failed > 0 ? $", {failed} file(s) failed" : "")}.");
        var finished = batch;
        _ = SaveQuietlyAsync(finished);
    }

    private async Task RunItemAsync(Batch batch, SiteTransferItem item, Running run)
    {
        var record = batch.Record;
        var token = run.Stop.Token;
        (CancellationTokenSource[] Stops, Task[] Tasks) others = ([], []);
        string state;
        string? error = null;
        SshHostKeyProblem? keyProblem = null;
        string? stopBatch = null;
        var connection = false;
        var rcloneGone = false;
        var retry = false;
        try
        {
            state = await MoveAsync(batch, item, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A stop of this file alone is read in the lock below, with every other ending.
            state = SiteTransferItemStates.Waiting;
        }
        catch (RcloneCallException e) when (SshHostKeyProblem.Parse(e.Message) is { } problem)
        {
            state = SiteTransferItemStates.Waiting;
            keyProblem = problem;
        }
        catch (ItemFailure e)
        {
            state = SiteTransferItemStates.Failed;
            error = e.Message;
        }
        catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException && SignInRefused(e.Message))
        {
            // Every file would be refused the same way, and a server may lock the account after many tries.
            state = SiteTransferItemStates.Waiting;
            stopBatch = SiteErrorText.Friendly(e.Message);
        }
        catch (Exception e) when (e is RcloneUnavailableException || (e is RcloneCallException && CantReach(e.Message)))
        {
            // Not this file's fault: the batch waits, and the file keeps its tries.
            state = SiteTransferItemStates.Waiting;
            connection = true;
            rcloneGone = e is RcloneUnavailableException;
            error = SiteErrorText.Friendly(e.Message);
        }
        catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or SiteStoreException or IOException or UnauthorizedAccessException)
        {
            state = SiteTransferItemStates.Failed;
            (error, retry) = FileFailure(e, record, item);
        }
        catch (Exception e)
        {
            state = SiteTransferItemStates.Failed;
            error = "The file could not be moved.";
            _log($"Site transfer {record.Id}: unexpected {e.GetType().Name}: {e.Message}");
        }

        var saveNow = false;
        lock (_sync)
        {
            batch.Active.Remove(item);
            _running--;
            // Review 2026-11-16: the user stopped this file (the flag may have been set after the copy ended some other
            // way, while the ending waited for this lock). Unless it got there, it is stopped: no try spent, no batch wait,
            // and not started again by a pause's Resume or a retry.
            if (run.StopFile && state is not (SiteTransferItemStates.Done or SiteTransferItemStates.Skipped))
            {
                state = SiteTransferItemStates.Stopped;
                (connection, rcloneGone, retry, error) = (false, false, false, null);
                saveNow = true;
            }

            if (connection && !token.IsCancellationRequested)
            {
                // rclone died mid-copy without tidying up: its partial files are looked for before going on.
                if (rcloneGone) record.SweepPending = true;
                // Audit 2026-11-15: the files in flight all fail in the same outage. Only the first counts and sets the
                // wait; the others fail while the batch already waits for that wait, so the five waits (doubling to a
                // minute) are five outages, not one outage seen by five files.
                var now = _time.GetUtcNow();
                if (batch.RetryAt is null || now >= batch.RetryAt)
                {
                    batch.ConnectionFailures++;
                    if (batch.ConnectionFailures >= MaxConnectionFailures)
                    {
                        stopBatch = $"{error} The transfer stopped after {MaxConnectionFailures} failed connections in a row; Retry goes on.";
                    }
                    else
                    {
                        var delay = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, _firstRetryDelay.Ticks << (batch.ConnectionFailures - 1)));
                        batch.RetryAt = now + delay;
                        batch.RetryReason = error;
                    }
                }
            }
            else if (state is SiteTransferItemStates.Done or SiteTransferItemStates.Skipped)
            {
                (batch.ConnectionFailures, batch.RetryAt) = (0, null);
            }

            if (state == SiteTransferItemStates.Failed)
            {
                item.Tries++;
                item.Error = error;
                // Also while paused: the file then waits for Resume like the others, its count of tries kept.
                if (retry && item.Tries < MaxTries)
                {
                    state = SiteTransferItemStates.Waiting;
                    item.NotBefore = _time.GetUtcNow() + TimeSpan.FromTicks(_firstRetryDelay.Ticks << (item.Tries - 1));
                }
            }
            else if (state is SiteTransferItemStates.Done or SiteTransferItemStates.Skipped or SiteTransferItemStates.Stopped)
            {
                item.Error = null;
                item.NotBefore = null;
            }

            item.State = state;
            if (keyProblem is not null && record.State == SiteTransferStates.Running)
            {
                // The server is not the one the user accepted: nothing more goes to it until the key is dealt with.
                record.State = SiteTransferStates.Failed;
                record.Problem = SiteProblems.HostKeyChanged;
                record.Message = HostKeyMessage(keyProblem);
                record.FinishedAt = _time.GetUtcNow();
                others = ActiveLocked(batch);
                _ = SaveQuietlyAsync(batch);
            }
            else if (stopBatch is not null && record.State == SiteTransferStates.Running)
            {
                record.State = SiteTransferStates.Failed;
                record.Message = stopBatch;
                record.FinishedAt = _time.GetUtcNow();
                others = ActiveLocked(batch);
                _ = SaveQuietlyAsync(batch);
            }

            batch.Dirty = true;
            ScheduleFlushLocked();
            FinishIfDoneLocked(batch);
        }

        // A stop is kept at once: after a Host kill before the flush, the file would come back waiting and run.
        if (saveNow) await SaveQuietlyAsync(batch).ConfigureAwait(false);

        bool? reached = connection || keyProblem is not null || (stopBatch is not null && !connection) ? false
            : state is SiteTransferItemStates.Done or SiteTransferItemStates.Skipped && !(item.Folder && record.Direction == SiteTransferDirections.Download) ? true
            : null;
        if (reached is { } ok)
        {
            try
            {
                Reached?.Invoke(record.SiteId, ok);
            }
            catch (Exception e)
            {
                _log($"Site transfers: a listener failed: {e.Message}");
            }
        }

        // Outside the lock; not waited for, since this is one of the batch's own files.
        foreach (var stop in others.Stops) stop.Cancel();
        Pump();
        RequestBroadcast();
    }

    // Audit 2026-11-15: every kind of site, not only SFTP (SiteErrorText reads rclone's and Go's fixed phrases).
    private static bool SignInRefused(string message) => SiteErrorText.RefusesTheSite(message);

    private static bool CantReach(string message) => SiteErrorText.CantReach(message);

    /// <summary>One file through rclone. Returns its new state (done or skipped); throws when it fails or is stopped.</summary>
    private async Task<string> MoveAsync(Batch batch, SiteTransferItem item, CancellationToken token)
    {
        var record = batch.Record;
        var download = record.Direction == SiteTransferDirections.Download;
        if (download && SiteTransferPaths.LocalPathProblem(item.Path) is { } nameProblem) throw new ItemFailure(nameProblem);
        if (download && IsInside(SiteTransferPaths.Local(record.LocalFolder, item.Target ?? item.Path), _stateDirectory))
            throw new ItemFailure("The file would land in Inbound's own data folder; it was not copied.");

        if (item.Folder && download)
        {
            // An empty folder: made here, nothing to copy. A file of that name stays and the folder fails.
            var path = SiteTransferPaths.Local(record.LocalFolder, item.Path);
            if (File.Exists(path)) throw new ItemFailure("A file of that name is already here, so the folder was not made.");
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ItemFailure($"The folder could not be made: {e.Message}");
            }

            return SiteTransferItemStates.Done;
        }

        var site = await _getSite(record.SiteId, token).ConfigureAwait(false) ?? throw new ItemFailure("The remote no longer exists.");
        using var lease = await _acquire(token).ConfigureAwait(false);
        var client = lease.Client;
        var fs = await SiteRemote.BuildAsync(site, client, token).ConfigureAwait(false);
        if (item.Folder)
        {
            // An empty folder made on the server; one that is there already is fine.
            await client.CallAsync("operations/mkdir", new JsonObject
            {
                ["fs"] = fs.DeepClone(),
                ["remote"] = SitePaths.ToRemote(SiteTransferPaths.Remote(record.RemoteFolder, item.Path)),
            }, token).ConfigureAwait(false);
            return SiteTransferItemStates.Done;
        }

        await ApplySpeedLimitAsync(lease, token).ConfigureAwait(false);
        var local = new JsonObject { ["type"] = "local", ["_root"] = record.LocalFolder };

        string? target;
        lock (_sync) target = item.Target;
        if (target is null)
        {
            if (await ExistsAsync(record, client, fs, item.Path, token).ConfigureAwait(false))
            {
                switch (record.Conflict)
                {
                    case SiteTransferConflicts.Skip:
                        return SiteTransferItemStates.Skipped;
                    case SiteTransferConflicts.KeepBoth:
                        target = await FreeNameAsync(record, client, fs, item.Path, token).ConfigureAwait(false);
                        if (download && IsInside(SiteTransferPaths.Local(record.LocalFolder, target), _stateDirectory))
                            throw new ItemFailure("The file would land in Inbound's own data folder; it was not copied.");
                        // Kept before the copy, so a restart writes to the same new name, not a third one.
                        lock (_sync) item.Target = target;
                        await SaveAsync(batch, token).ConfigureAwait(false);
                        break;
                }
            }
        }

        var to = target ?? item.Path;
        var remote = SitePaths.ToRemote(SiteTransferPaths.Remote(record.RemoteFolder, download ? item.Path : to));
        lock (_sync)
        {
            if (batch.Active.TryGetValue(item, out var run)) run.Source = download ? remote : item.Path;
        }

        token.ThrowIfCancellationRequested();
        // Not on the stop token: a stop that cut this call after rclone had made the job would leave the job running with
        // no id to stop it by. The answer is read in full, and a stop that came meanwhile stops the job in WaitForJobAsync.
        JsonObject job;
        using (var answer = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            job = await client.CallAsync("operations/copyfile", new JsonObject
            {
                ["srcFs"] = download ? fs : local,
                ["srcRemote"] = download ? remote : item.Path,
                ["dstFs"] = download ? local.DeepClone() : fs.DeepClone(),
                ["dstRemote"] = download ? to : remote,
                ["_async"] = true,
                ["_group"] = batch.Group,
            }, answer.Token).ConfigureAwait(false);
        }

        var jobId = job["jobid"] is JsonValue id && id.TryGetValue<long>(out var value) ? value
            : throw new RcloneCallException("operations/copyfile", 200, "rclone gave no job id.");
        await WaitForJobAsync(client, jobId, token).ConfigureAwait(false);
        return SiteTransferItemStates.Done;
    }

    /// <summary>Waits for an rclone job; a stop asks rclone to stop it (which removes its partial file) and waits for that.</summary>
    private async Task WaitForJobAsync(RcloneClient client, long jobId, CancellationToken token)
    {
        var delay = TimeSpan.FromMilliseconds(50);
        try
        {
            while (true)
            {
                await Task.Delay(delay, _time, token).ConfigureAwait(false);
                if (delay < TimeSpan.FromSeconds(1)) delay *= 2;
                var status = await client.CallAsync("job/status", new JsonObject { ["jobid"] = jobId }, token).ConfigureAwait(false);
                if (status["finished"] is not JsonValue finished || !finished.TryGetValue<bool>(out var done) || !done) continue;
                if (status["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && ok) return;
                var text = status["error"] is JsonValue e && e.TryGetValue<string>(out var message) && message.Length > 0 ? message : "the copy failed";
                // A job's error is not an rc error answer, so the client has not redacted it.
                throw new RcloneCallException("operations/copyfile", 500, client.Redact(text));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or RcloneUnavailableException
                                      || (e is RcloneCallException call && call.Command != "operations/copyfile"))
        {
            // Stopped, or the job could not be followed: it must not go on unseen and write the file later.
            await StopJobAsync(client, jobId).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task StopJobAsync(RcloneClient client, long jobId)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.CallAsync("job/stop", new JsonObject { ["jobid"] = jobId }, limit.Token).ConfigureAwait(false);
            while (!limit.IsCancellationRequested)
            {
                var status = await client.CallAsync("job/status", new JsonObject { ["jobid"] = jobId }, limit.Token).ConfigureAwait(false);
                if (status["finished"] is JsonValue finished && finished.TryGetValue<bool>(out var done) && done) return;
                await Task.Delay(100, limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or RcloneCallException or RcloneUnavailableException)
        {
            // rclone already ended the job, forgot it, or is gone: either way nothing is moving.
        }
    }

    private static async Task<bool> ExistsAsync(SiteTransferRecord record, RcloneClient client, JsonObject fs, string relative, CancellationToken token)
    {
        if (record.Direction == SiteTransferDirections.Download)
        {
            var path = SiteTransferPaths.Local(record.LocalFolder, relative);
            return File.Exists(path) || Directory.Exists(path);
        }

        var stat = await client.CallAsync("operations/stat", new JsonObject
        {
            ["fs"] = fs.DeepClone(),
            ["remote"] = SitePaths.ToRemote(SiteTransferPaths.Remote(record.RemoteFolder, relative)),
        }, token).ConfigureAwait(false);
        return stat["item"] is JsonObject;
    }

    private static async Task<string> FreeNameAsync(SiteTransferRecord record, RcloneClient client, JsonObject fs, string relative, CancellationToken token)
    {
        for (var number = 2; number < 1000; number++)
        {
            var candidate = SiteTransferPaths.Numbered(relative, number);
            if (record.Direction == SiteTransferDirections.Download && SiteTransferPaths.LocalPathProblem(candidate) is not null) break;
            if (!await ExistsAsync(record, client, fs, candidate, token).ConfigureAwait(false)) return candidate;
        }

        throw new ItemFailure("No free name was found for a second copy.");
    }

    /// <summary>
    /// A batch that may have been cut without rclone tidying up: in each folder that holds one of its unfinished files,
    /// rclone's partial files for those files' targets are removed, on this PC for a download, on the server for an
    /// upload. One look per folder. Runs while nothing else moves (<see cref="Pump"/>).
    /// </summary>
    private async Task SweepAsync(Batch batch)
    {
        var record = batch.Record;
        try
        {
            Dictionary<string, HashSet<string>> folders = new(StringComparer.Ordinal);
            lock (_sync)
            {
                foreach (var item in record.Items.Where(i => NotFinished(i) && !i.Folder))
                {
                    foreach (var relative in new[] { item.Path, item.Target })
                    {
                        if (relative is null) continue;
                        var slash = relative.LastIndexOf('/');
                        var folder = slash < 0 ? "" : relative[..slash];
                        if (!folders.TryGetValue(folder, out var names)) folders[folder] = names = new HashSet<string>(StringComparer.Ordinal);
                        names.Add(relative[(slash + 1)..]);
                    }
                }
            }

            var removed = 0;
            if (record.Direction == SiteTransferDirections.Download)
            {
                foreach (var (folder, names) in folders)
                {
                    var path = folder.Length == 0 ? record.LocalFolder : SiteTransferPaths.Local(record.LocalFolder, folder);
                    if (!Directory.Exists(path)) continue;
                    foreach (var file in Directory.EnumerateFiles(path, "*.partial"))
                    {
                        var name = Path.GetFileName(file);
                        if (!names.Any(n => SiteTransferPaths.IsPartialOf(name, n))) continue;
                        File.Delete(file);
                        removed++;
                    }
                }
            }
            else if (await _getSite(record.SiteId, CancellationToken.None).ConfigureAwait(false) is { } site)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var lease = await _acquire(limit.Token).ConfigureAwait(false);
                var fs = await SiteRemote.BuildAsync(site, lease.Client, limit.Token).ConfigureAwait(false);
                foreach (var (folder, names) in folders)
                {
                    var parent = SitePaths.ToRemote(folder.Length == 0 ? record.RemoteFolder : SiteTransferPaths.Remote(record.RemoteFolder, folder));
                    JsonObject listing;
                    try
                    {
                        listing = await lease.Client.CallAsync("operations/list", new JsonObject
                        {
                            ["fs"] = fs.DeepClone(),
                            ["remote"] = parent,
                            ["opt"] = new JsonObject { ["filesOnly"] = true, ["noMimeType"] = true, ["noModTime"] = true },
                        }, limit.Token).ConfigureAwait(false);
                    }
                    catch (RcloneCallException e) when (e.Message.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var entry in SiteListing.Read(listing))
                    {
                        if (!names.Any(n => SiteTransferPaths.IsPartialOf(entry.Name, n))) continue;
                        await lease.Client.CallAsync("operations/deletefile", new JsonObject
                        {
                            ["fs"] = fs.DeepClone(),
                            ["remote"] = parent.Length == 0 ? entry.Name : parent + "/" + entry.Name,
                        }, limit.Token).ConfigureAwait(false);
                        removed++;
                    }
                }
            }

            if (removed > 0) _log($"Site transfer {record.Id}: removed {removed} partial file(s) left by a stop.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException
                                      or RcloneCallException or RcloneUnavailableException or SiteStoreException)
        {
            // Not tried again: the files themselves say what is wrong, and a stray partial file is only clutter.
            _log($"Site transfer {record.Id}: partial files could not be looked for ({e.GetType().Name}).");
        }
        finally
        {
            lock (_sync)
            {
                batch.Sweeping = false;
                record.SweepPending = false;
                _running--;
                batch.Dirty = true;
                ScheduleFlushLocked();
            }

            Pump();
        }
    }

    private static bool NotFinished(SiteTransferItem item) => item.State is SiteTransferItemStates.Waiting or SiteTransferItemStates.Running;

    /// <summary>
    /// A file's failure (not the connection's, not the sign-in's): its words and whether its tries go on. An upload this PC
    /// can't read, or that the server refuses to write, is said so and not tried again; an FTP server's 4xx keeps the tries.
    /// "No longer there" is not read from an FTP server's reply ("stor: 450 … file does not exist"): those are its words.
    /// </summary>
    internal static (string Error, bool Retry) FileFailure(Exception e, SiteTransferRecord record, SiteTransferItem item)
    {
        var upload = record.Direction != SiteTransferDirections.Download;
        var problem = upload && e is RcloneCallException write
            ? SiteErrorText.UploadFailure(write.Message, record.LocalFolder, item.Path, item.Target)
            : UploadProblem.None;
        var gone = e is RcloneCallException call && IsGone(call.Message)
            && !(upload && call.Message.Contains("stor: ", StringComparison.OrdinalIgnoreCase));
        var error = problem != UploadProblem.None
            ? SiteErrorText.UploadText(problem, e.Message, record.SiteName, record.RemoteFolder, item.Path)
            : gone
                ? (upload ? "The file is no longer on this PC." : "The file is no longer on the server.")
                : SiteErrorText.Friendly(e.Message);
        var retry = (problem is UploadProblem.None or UploadProblem.ServerDeclined) && e is not SiteStoreException && !gone;
        return (error, retry);
    }

    private static bool IsGone(string message) =>
        message.Contains("object not found", StringComparison.OrdinalIgnoreCase)
        || message.Contains("file does not exist", StringComparison.OrdinalIgnoreCase);

    private static string HostKeyMessage(SshHostKeyProblem problem) => problem.Kind == SshHostKeyProblemKind.Changed
        ? "The server's key has changed. This can mean the connection is being intercepted. Do not continue unless the server's owner confirms the new key."
        : "The server no longer offers the kind of key that was accepted. Do not continue unless the server's owner confirms the change.";

    // ---- Progress, saving and notices ---------------------------------------------------------------------------

    /// <summary>While files move: once a second, each batch's bytes in flight and speed from rclone's stats for its group.</summary>
    private async Task ProgressLoopAsync()
    {
        while (true)
        {
            await Task.Delay(ProgressInterval, _time).ConfigureAwait(false);
            List<Batch> active;
            lock (_sync)
            {
                if (_running == 0 || _disposed)
                {
                    foreach (var batch in _batches)
                    {
                        (batch.Speed, batch.InFlight) = (0, 0);
                        foreach (var run in batch.Active.Values) run.Progress = null;
                    }

                    _progress = null;
                    break;
                }

                active = [.. _batches.Where(b => b.Active.Count > 0)];
            }

            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var lease = await _acquire(limit.Token).ConfigureAwait(false);
                await ApplySpeedLimitAsync(lease, limit.Token).ConfigureAwait(false);
                foreach (var batch in active)
                {
                    var stats = await lease.Client.CallAsync("core/stats", new JsonObject { ["group"] = batch.Group }, limit.Token).ConfigureAwait(false);
                    var files = ReadTransferring(stats);
                    lock (_sync)
                    {
                        (batch.Speed, batch.InFlight) = (files.Sum(f => f.Speed), files.Sum(f => f.BytesDone));
                        MatchProgress(batch.Active.Values, files);
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or RcloneCallException or RcloneUnavailableException)
            {
                // The next second tries again; the files' own jobs report real failures.
            }

            RequestBroadcast();
            try
            {
                Measured?.Invoke();
            }
            catch (Exception e)
            {
                _log($"Site transfers: a listener failed: {e.Message}");
            }
        }

        RequestBroadcast();
    }

    /// <summary>The files in rclone's <c>core/stats</c> <c>transferring</c> list: name, bytes so far and speed.</summary>
    internal static List<(string Name, long BytesDone, long Speed)> ReadTransferring(JsonObject stats)
    {
        var files = new List<(string, long, long)>();
        if (stats["transferring"] is not JsonArray moving) return files;
        foreach (var node in moving)
        {
            if (node is not JsonObject file) continue;
            var name = file["name"] is JsonValue n && n.TryGetValue<string>(out var text) ? text : "";
            var bytes = file["bytes"] is JsonValue b && b.TryGetValue<long>(out var done) ? Math.Max(0, done) : 0;
            var speed = file["speed"] is JsonValue s && s.TryGetValue<double>(out var rate) && rate > 0 ? (long)rate : 0;
            files.Add((name, bytes, speed));
        }

        return files;
    }

    /// <summary>
    /// For each moving file (by the <c>srcRemote</c> its copy was given), the index of its entry in rclone's list, or -1.
    /// rclone names a file in its stats by that source, whatever the target is called; a file not found shows no progress
    /// of its own (it still counts in the batch's bytes and speed). No guessing: a wrong pair would show one file's bar on another.
    /// </summary>
    internal static int[] MatchTransferring(IReadOnlyList<string?> sources, IReadOnlyList<string> reported)
    {
        var match = new int[sources.Count];
        Array.Fill(match, -1);
        var taken = new bool[reported.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is not { } source) continue;
            for (var j = 0; j < reported.Count; j++)
            {
                if (taken[j] || reported[j] != source) continue;
                (match[i], taken[j]) = (j, true);
                break;
            }
        }

        return match;
    }

    private static void MatchProgress(IEnumerable<Running> running, List<(string Name, long BytesDone, long Speed)> files)
    {
        var list = running.ToList();
        var match = MatchTransferring([.. list.Select(r => r.Source)], [.. files.Select(f => f.Name)]);
        for (var i = 0; i < list.Count; i++)
            list[i].Progress = match[i] < 0 ? null : (files[match[i]].BytesDone, files[match[i]].Speed);
    }

    private void ScheduleFlushLocked()
    {
        if (_flushTimer is not null || _disposed) return;
        _flushTimer = _time.CreateTimer(_ => _ = FlushAsync(), null, FlushDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushAsync()
    {
        List<Batch> dirty;
        lock (_sync)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            dirty = [.. _batches.Where(b => b.Dirty)];
        }

        foreach (var batch in dirty) await SaveQuietlyAsync(batch).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the batch. The bytes are made under the write gate, so a later state is never overwritten by an earlier one.
    /// </summary>
    private async Task SaveAsync(Batch batch, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes;
            lock (_sync)
            {
                if (batch.Removed) return;
                batch.Dirty = false;
                bytes = SiteTransferStore.Serialize(batch.Record);
            }

            try
            {
                await _store.WriteAsync(batch.Record.Id, bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Not written: the next flush tries again.
                lock (_sync) batch.Dirty = true;
                throw;
            }
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task SaveQuietlyAsync(Batch batch)
    {
        try
        {
            await SaveAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"Site transfer {batch.Record.Id} could not be saved: {e.Message}");
            lock (_sync) ScheduleFlushLocked();
        }
    }

    private void RequestBroadcast()
    {
        lock (_sync)
        {
            if (_broadcastTimer is not null || _disposed) return;
            _broadcastTimer = _time.CreateTimer(_ => Broadcast(), null, BroadcastDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Broadcast()
    {
        SiteTransfersDto dto;
        lock (_sync)
        {
            _broadcastTimer?.Dispose();
            _broadcastTimer = null;
            dto = SnapshotLocked();
        }

        try
        {
            Changed?.Invoke(dto);
        }
        catch (Exception e)
        {
            _log($"Site transfer notice failed: {e.Message}");
        }
    }

    // ---- Shape ----------------------------------------------------------------------------------------------------

    private Batch? Find(string id) => _batches.FirstOrDefault(b => b.Record.Id == id);

    private SiteTransfersDto SnapshotLocked() => new() { Transfers = [.. _batches.Select(ToDto)] };

    private SiteTransferDto ToDto(Batch batch)
    {
        var r = batch.Record;
        int done = 0, skipped = 0, failed = 0, running = 0, stopped = 0;
        SiteTransferItem? firstFailed = null;
        long bytes = 0, bytesDone = 0, setAside = 0;
        var unknownLeft = false;
        foreach (var item in r.Items)
        {
            if (item.Size < 0 && !item.Folder && NotFinished(item)) unknownLeft = true;
            var size = Math.Max(0, item.Size);
            bytes += size;
            switch (item.State)
            {
                case SiteTransferItemStates.Done:
                    done++;
                    bytesDone += size;
                    break;
                case SiteTransferItemStates.Skipped:
                    skipped++;
                    bytesDone += size;
                    break;
                case SiteTransferItemStates.Failed:
                    failed++;
                    setAside += size;
                    firstFailed ??= item;
                    break;
                case SiteTransferItemStates.Running:
                    running++;
                    break;
                case SiteTransferItemStates.Stopped:
                    stopped++;
                    setAside += size;
                    break;
            }
        }

        batch.Picked ??= PickedNames(r.Items);
        // Only while the wait lasts: after it the files are tried again, and the batch is not waiting any more.
        var waiting = r.State == SiteTransferStates.Running && batch.RetryAt is { } at && at > _time.GetUtcNow();

        var moving = batch.Active.Count > 0;
        var speed = moving ? batch.Speed : 0;
        var shownDone = Math.Min(bytes, bytesDone + (moving ? batch.InFlight : 0));
        return new SiteTransferDto
        {
            Id = r.Id,
            SiteId = r.SiteId,
            SiteName = r.SiteName,
            Direction = r.Direction,
            RemoteFolder = r.RemoteFolder,
            LocalFolder = r.LocalFolder,
            Conflict = r.Conflict,
            State = r.State,
            Problem = r.Problem,
            Message = r.Message,
            Files = r.Items.Count,
            Folders = r.Items.Count(i => i.Folder),
            FilesDone = done,
            FilesSkipped = skipped,
            FilesFailed = failed,
            FilesRunning = running,
            FilesStopped = stopped,
            Picked = [.. batch.Picked.Value.First],
            PickedCount = batch.Picked.Value.Count,
            FirstFailedPath = firstFailed?.Path,
            FirstFailedError = firstFailed?.Error,
            RetryAt = waiting ? batch.RetryAt : null,
            RetryReason = waiting ? batch.RetryReason : null,
            Bytes = bytes,
            BytesDone = shownDone,
            Speed = speed,
            SecondsLeft = r.State == SiteTransferStates.Running && moving && !unknownLeft ? SiteTransferTime.SecondsLeft(bytes - setAside, shownDone, speed) : null,
            BytesSetAside = setAside,
            Moving = [.. batch.Active
                .Where(a => a.Value.Progress is not null)
                .OrderBy(a => a.Key.Path, StringComparer.Ordinal)
                .Select(a => new SiteTransferMovingDto
                {
                    Path = a.Key.Path,
                    Size = a.Key.Size,
                    BytesDone = a.Value.Progress!.Value.BytesDone,
                    Speed = a.Value.Progress!.Value.Speed,
                    SecondsLeft = SiteTransferTime.SecondsLeft(a.Key.Size, a.Value.Progress!.Value.BytesDone, a.Value.Progress!.Value.Speed),
                })],
            CreatedAt = r.CreatedAt,
            FinishedAt = r.FinishedAt,
        };
    }

    /// <summary>How many picked names <see cref="SiteTransferDto.Picked"/> holds at most.</summary>
    internal const int PickedShown = 3;

    /// <summary>
    /// SITES.11: the names picked, from the files' first path segment (a picked folder brings files under its name), in
    /// the order of the batch's files; the first <see cref="PickedShown"/> and how many there are.
    /// </summary>
    internal static (List<string> First, int Count) PickedNames(IEnumerable<SiteTransferItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = new List<string>(PickedShown);
        foreach (var item in items)
        {
            var slash = item.Path.IndexOf('/', StringComparison.Ordinal);
            var name = slash < 0 ? item.Path : item.Path[..slash];
            if (seen.Add(name) && first.Count < PickedShown) first.Add(name);
        }

        return (first, seen.Count);
    }

    private SiteTransferResultDto Ok(Batch batch) => new() { Ok = true, Transfer = ToDto(batch) };

    private static SiteTransferResultDto Fail(string message) => new() { Ok = false, Message = message };

    private static bool IsInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, comparison);
    }

    /// <summary>On the Host's exit: files in flight stop and stay waiting, so their batches go on at the next start.</summary>
    public async ValueTask DisposeAsync()
    {
        (CancellationTokenSource[] Stops, Task[] Tasks) stopping;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            var active = _batches.Select(ActiveLocked).ToList();
            stopping = ([.. active.SelectMany(a => a.Stops)], [.. active.SelectMany(a => a.Tasks)]);
            _retryTimer?.Dispose();
            _retryTimer = null;
            _broadcastTimer?.Dispose();
            _broadcastTimer = null;
        }

        await StopAndWaitAsync(stopping).ConfigureAwait(false);
        lock (_sync)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        List<Batch> dirty;
        lock (_sync) dirty = [.. _batches.Where(b => b.Dirty)];
        foreach (var batch in dirty) await SaveQuietlyAsync(batch).ConfigureAwait(false);
        UpdateSleep();
    }
}
