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
        public string Group => "inbound-transfer-" + Record.Id;
    }

    private sealed class Running
    {
        // Never disposed: it holds no timer, and a late Cancel on a disposed source would throw.
        public CancellationTokenSource Stop { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
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

    /// <summary>After any change, at most about twice a second; for <c>sites.transfer.changed</c>.</summary>
    public event Action<SiteTransfersDto>? Changed;

    // ---- Commands ------------------------------------------------------------------------------------------

    public SiteTransfersDto List()
    {
        lock (_sync) return SnapshotLocked();
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
                })],
            };
        }
    }

    public async Task<SiteTransferResultDto> StartAsync(SiteTransferStartRequest request, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(request.SiteId) || await _getSite(request.SiteId, cancellationToken).ConfigureAwait(false) is not { } site)
            return Fail("That site no longer exists.");
        if (request.Direction is not (SiteTransferDirections.Download or SiteTransferDirections.Upload)) return Fail("Choose download or upload.");
        var conflict = string.IsNullOrEmpty(request.Conflict) ? SiteTransferConflicts.Skip : request.Conflict;
        if (!SiteTransferConflicts.IsKnown(conflict)) return Fail("Choose Skip, Replace or Keep both for files that are already there.");
        if (SitePaths.Normalize(request.RemoteFolder) is not { } remoteFolder) return Fail("That is not a folder path on the server.");
        var localFolder = string.IsNullOrWhiteSpace(request.LocalFolder) ? site.LocalFolder : request.LocalFolder.Trim();
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

        if (site.HostKeys.Count == 0)
            return new SiteTransferResultDto { Ok = false, Problem = SiteProblems.HostKeyNotAccepted, Message = "The server's key has not been accepted yet." };

        var names = request.Names.Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();
        List<SiteTransferItem> items;
        try
        {
            items = request.Direction == SiteTransferDirections.Download
                ? await ListDownloadAsync(site, remoteFolder, names, cancellationToken).ConfigureAwait(false)
                : ListUpload(localFolder, names);
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

        if (items.Count == 0) return Fail("There are no files in that choice (empty folders are not copied yet).");
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
            return Fail("Sites are stopping.");
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
            var stoppedByProblem = batch.Record.State == SiteTransferStates.Failed && batch.Record.Problem is not null;
            if (batch.Record.State != SiteTransferStates.Paused && !stoppedByProblem)
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
    /// The failed files are tried again. A paused batch stays paused (they run at Resume); a canceled one is not brought back.
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

            if (!any && batch.Record.Problem is null) return Ok(batch);
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
                ["opt"] = new JsonObject { ["recurse"] = true, ["filesOnly"] = true, ["noMimeType"] = true },
            }, cancellationToken).ConfigureAwait(false);
            if (listing["list"] is not JsonArray files) continue;
            foreach (var node in files)
            {
                if (node is not JsonObject file || file["Path"]?.GetValue<string>() is not { } path) continue;
                if (!path.StartsWith(inside + "/", StringComparison.Ordinal)) continue;
                var relative = name + "/" + path[(inside.Length + 1)..];
                if (!SiteTransferPaths.IsRelative(relative)) continue;
                var size = file["Size"] is JsonValue s && s.TryGetValue<long>(out var bytes) && bytes >= 0 ? bytes : -1;
                items.Add(new SiteTransferItem { Path = relative, Size = size });
                if (items.Count > MaxItems) return items;
            }
        }

        return items;
    }

    private static List<SiteTransferItem> ListUpload(string localFolder, List<string> names)
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
            foreach (var file in folder.EnumerateFiles("*", options))
            {
                var relative = Path.GetRelativePath(localFolder, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (!SiteTransferPaths.IsRelative(relative)) continue;
                items.Add(new SiteTransferItem { Path = relative, Size = file.Length });
                if (items.Count > MaxItems) return items;
            }
        }

        return items;
    }

    // ---- Scheduling ---------------------------------------------------------------------------------------------

    /// <summary>Starts waiting files while there is room, oldest batch first, and sets the timer for the next retry.</summary>
    public void Pump()
    {
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

    /// <summary>
    /// The PC stays awake while a batch is running (moving, or waiting for a retry or a free place) and Sites is on, as
    /// downloads do; not for a paused one.
    /// </summary>
    private void UpdateSleep()
    {
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
            error = e is RcloneCallException call && IsGone(call.Message)
                ? (record.Direction == SiteTransferDirections.Download ? "The file is no longer on the server." : "The file is no longer on this PC.")
                : SiteErrorText.Friendly(e.Message);
            retry = e is not SiteStoreException && !(e is RcloneCallException gone && IsGone(gone.Message));
        }
        catch (Exception e)
        {
            state = SiteTransferItemStates.Failed;
            error = "The file could not be moved.";
            _log($"Site transfer {record.Id}: unexpected {e.GetType().Name}: {e.Message}");
        }

        lock (_sync)
        {
            batch.Active.Remove(item);
            _running--;
            if (connection && !token.IsCancellationRequested)
            {
                // rclone died mid-copy without tidying up: its partial files are looked for before going on.
                if (rcloneGone) record.SweepPending = true;
                batch.ConnectionFailures++;
                if (batch.ConnectionFailures >= MaxConnectionFailures)
                {
                    stopBatch = $"{error} The transfer stopped after {MaxConnectionFailures} tries; Retry goes on.";
                }
                else
                {
                    var delay = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, _firstRetryDelay.Ticks << (batch.ConnectionFailures - 1)));
                    var at = _time.GetUtcNow() + delay;
                    if (batch.RetryAt is null || at > batch.RetryAt) batch.RetryAt = at;
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
            else if (state is SiteTransferItemStates.Done or SiteTransferItemStates.Skipped)
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

        // Outside the lock; not waited for, since this is one of the batch's own files.
        foreach (var stop in others.Stops) stop.Cancel();
        Pump();
        RequestBroadcast();
    }

    private static bool SignInRefused(string message) =>
        message.Contains("couldn't connect SSH", StringComparison.OrdinalIgnoreCase)
        && message.Contains("unable to authenticate", StringComparison.OrdinalIgnoreCase);

    private static bool CantReach(string message) => message.Contains("couldn't connect SSH", StringComparison.OrdinalIgnoreCase);

    /// <summary>One file through rclone. Returns its new state (done or skipped); throws when it fails or is stopped.</summary>
    private async Task<string> MoveAsync(Batch batch, SiteTransferItem item, CancellationToken token)
    {
        var record = batch.Record;
        var download = record.Direction == SiteTransferDirections.Download;
        if (download && SiteTransferPaths.LocalPathProblem(item.Path) is { } nameProblem) throw new ItemFailure(nameProblem);
        if (download && IsInside(SiteTransferPaths.Local(record.LocalFolder, item.Target ?? item.Path), _stateDirectory))
            throw new ItemFailure("The file would land in Inbound's own data folder; it was not copied.");

        var site = await _getSite(record.SiteId, token).ConfigureAwait(false) ?? throw new ItemFailure("The site no longer exists.");
        using var lease = await _acquire(token).ConfigureAwait(false);
        var client = lease.Client;
        var fs = await SiteRemote.BuildAsync(site, client, token).ConfigureAwait(false);
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
                foreach (var item in record.Items.Where(NotFinished))
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
                    foreach (var batch in _batches) (batch.Speed, batch.InFlight) = (0, 0);
                    _progress = null;
                    break;
                }

                active = [.. _batches.Where(b => b.Active.Count > 0)];
            }

            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var lease = await _acquire(limit.Token).ConfigureAwait(false);
                foreach (var batch in active)
                {
                    var stats = await lease.Client.CallAsync("core/stats", new JsonObject { ["group"] = batch.Group }, limit.Token).ConfigureAwait(false);
                    long speed = 0, inFlight = 0;
                    if (stats["transferring"] is JsonArray moving)
                    {
                        foreach (var node in moving)
                        {
                            if (node is not JsonObject file) continue;
                            if (file["bytes"] is JsonValue b && b.TryGetValue<long>(out var bytes)) inFlight += bytes;
                            if (file["speed"] is JsonValue s && s.TryGetValue<double>(out var rate)) speed += (long)rate;
                        }
                    }

                    lock (_sync) (batch.Speed, batch.InFlight) = (speed, inFlight);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or RcloneCallException or RcloneUnavailableException)
            {
                // The next second tries again; the files' own jobs report real failures.
            }

            RequestBroadcast();
        }

        RequestBroadcast();
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

    private static SiteTransferDto ToDto(Batch batch)
    {
        var r = batch.Record;
        int done = 0, skipped = 0, failed = 0, running = 0;
        long bytes = 0, bytesDone = 0;
        foreach (var item in r.Items)
        {
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
                    break;
                case SiteTransferItemStates.Running:
                    running++;
                    break;
            }
        }

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
            FilesDone = done,
            FilesSkipped = skipped,
            FilesFailed = failed,
            FilesRunning = running,
            Bytes = bytes,
            BytesDone = Math.Min(bytes, bytesDone + (batch.Active.Count > 0 ? batch.InFlight : 0)),
            Speed = batch.Active.Count > 0 ? batch.Speed : 0,
            CreatedAt = r.CreatedAt,
            FinishedAt = r.FinishedAt,
        };
    }

    private static SiteTransferResultDto Ok(Batch batch) => new() { Ok = true, Transfer = ToDto(batch) };

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
