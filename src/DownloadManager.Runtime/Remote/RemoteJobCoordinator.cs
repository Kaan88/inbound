using DownloadManager.Core.Archives;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;

namespace DownloadManager.Runtime.Remote;

/// <summary>What the coordinator needs from the service, so it never reaches past this into local jobs.</summary>
public interface IRemoteJobHost
{
    Task<RemoteJobsResult> AddRemoteJobsAsync(
        IReadOnlyList<RemoteJobRequest> requests,
        string packageKey,
        string? packageName = null,
        CancellationToken cancellationToken = default);

    string DefaultDownloadDirectory { get; }

    /// <summary>What a package's local jobs are doing right now. Null when it has gone, or this host can't say.</summary>
    RemoteLocalSummary? GetLocalSummary(Guid packageId) => null;

    /// <summary>How many local jobs this remote job made are still on the list. Null when this host can't say.</summary>
    int? CountLocalJobs(Guid remoteJobId) => null;
}

/// <summary>A package's local jobs, counted by what they are doing.</summary>
public sealed record RemoteLocalSummary(int Total, int Completed, int Failed, int Paused, int Active);

public sealed record RemoteCoordinatorOptions
{
    /// <summary>Off only in tests, which step the coordinator themselves with <see cref="RemoteJobCoordinator.RunDueAsync"/>.</summary>
    public bool RunLoops { get; init; } = true;
    public int ListPageSize { get; init; } = 250;
    /// <summary>How far a search by hash goes. An item past this can't be called absent, so nothing is resubmitted.</summary>
    public int MostListPages { get; init; } = 40;
    public int MostResubmits { get; init; } = 3;
    /// <summary>0 to 1. Fixed in tests.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
    /// <summary>Takes a job's archive password once it has a package. Without one, the password stays on the job.</summary>
    public IPackagePasswordKeeper? PackagePasswords { get; init; }
}

/// <summary>What a user or a frontend asked to add. Holds secrets, so it prints as nothing.</summary>
public sealed record RemoteAddRequest
{
    /// <summary>A folder link, magnet, hoster link or the path of a local .torrent file.</summary>
    public string? Input { get; init; }
    /// <summary>A .torrent file already read, instead of <see cref="Input"/>.</summary>
    public byte[]? TorrentFile { get; init; }
    /// <summary>An item chosen from the provider's own list, instead of <see cref="Input"/>.</summary>
    public RemoteItemRef? ExistingItem { get; init; }
    /// <summary>A full path. Null uses the default download folder.</summary>
    public string? Destination { get; init; }
    public int QueueId { get; init; }
    public string StartMode { get; init; } = "startNow";
    /// <summary>
    /// Null decides by the input: a hoster link starts by itself when the provider is done, because there is
    /// nothing to choose among. A magnet or a .torrent file asks if it was cached, and otherwise takes all
    /// (<see cref="RemoteSelectionMode.AskIfCached"/>). A folder link asks.
    /// </summary>
    public RemoteSelectionMode? Selection { get; init; }
    public IReadOnlyList<string>? FileIds { get; init; }
    /// <summary>With <see cref="RemoteSelectionMode.Chosen"/>, instead of <see cref="FileIds"/>: files chosen from the input's own list, such as a .torrent file's.</summary>
    public IReadOnlyList<RemoteChosenPath>? FilePaths { get; init; }
    /// <summary>The user agreed to have the provider start work on this if it isn't cached.</summary>
    public bool ApprovedUncached { get; init; }
    /// <summary>Nobody asked for this yet, so it waits for the user's go-ahead before anything is sent or asked. See <see cref="RemoteJobRecord.HeldForApproval"/>.</summary>
    public bool HoldForApproval { get; init; }
    /// <summary>
    /// The folders this caller may have a .torrent file read from, when they are not the coordinator's own
    /// (<see cref="RemoteJobCoordinator.Roots"/>): a browser on another device (D8). Null uses the coordinator's.
    /// </summary>
    public PathRoots? SourceRoots { get; init; }
    /// <summary>As <see cref="SourceRoots"/>, for the destination (<see cref="RemoteJobCoordinator.DestinationRoots"/>).</summary>
    public PathRoots? DestinationRoots { get; init; }
    public string? LinkPassword { get; init; }
    /// <summary>For the archives this brings. Never sent to the provider, and not the same thing as <see cref="LinkPassword"/>.</summary>
    public string? ArchivePassword { get; init; }
    /// <summary>
    /// It came with the input and nobody typed it. Several jobs of one package can bring the same one at
    /// different times, and none of them may undo what the user has said in between.
    /// </summary>
    public bool ArchivePasswordOffered { get; init; }
    /// <summary>Set when this is one of several that belong together.</summary>
    public RemoteJobPlacement? Placement { get; init; }
    /// <summary>A name to show until the provider gives one. Already free of secrets.</summary>
    public string? NameHint { get; init; }

    public override string ToString() => "[remote add request]";
}

/// <summary>A file chosen from the input's own list, as that list gave it: its relative path and its size.</summary>
public sealed record RemoteChosenPath(string Path, long Size);

/// <summary>A remote job as a client may see it: no input, no link and no password, in any form.</summary>
public sealed record RemoteJobView
{
    public required Guid Id { get; init; }
    public required string Provider { get; init; }
    public required string ProviderName { get; init; }
    public required RemoteItemKind Kind { get; init; }
    public required RemoteJobState State { get; init; }
    /// <summary>What to show. Remote stages are words here, not local progress.</summary>
    public required string Status { get; init; }
    public RemoteStage? Stage { get; init; }
    public string? RawState { get; init; }
    public double Progress { get; init; }
    public long SpeedBytesPerSecond { get; init; }
    public long? EtaSeconds { get; init; }
    public string? ItemId { get; init; }
    public string? Name { get; init; }
    public string? DisplayHost { get; init; }
    public bool Cached { get; init; }
    public bool ApprovalNeeded { get; init; }
    public bool HeldForApproval { get; init; }
    public bool ApprovedUncached { get; init; }
    public bool HasLinkPassword { get; init; }
    public bool HasArchivePassword { get; init; }
    public RemoteFailureKind? FailureKind { get; init; }
    public string? FailureMessage { get; init; }
    public DateTimeOffset? NextCheckAt { get; init; }
    public string? Destination { get; init; }
    public int QueueId { get; init; }
    public string StartMode { get; init; } = "startNow";
    public RemoteSelectionMode SelectionMode { get; init; }
    public IReadOnlyList<string> SelectedFileIds { get; init; } = [];
    public Guid? PackageId { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RemoteAddResult
{
    public RemoteJobView? Job { get; init; }
    /// <summary>An active job already covers this input, and it is the one returned.</summary>
    public bool Existing { get; init; }
    public RemoteInputKind? Kind { get; init; }
    /// <summary>Set when nothing was created, in words for the user.</summary>
    public string? Error { get; init; }
    /// <summary>Something asked for that didn't happen, because an active job already covers the input.</summary>
    public string? Note { get; init; }
}

public sealed record RemoteActionResult(bool Ok, string? Error = null, RemoteJobView? Job = null);

public sealed record RemoteFileView
{
    public required string FileId { get; init; }
    /// <summary>The relative path it would have. Null when its path isn't safe, so it can't be chosen.</summary>
    public string? RelativePath { get; init; }
    public long Size { get; init; }
    public bool Infected { get; init; }
    /// <summary>Whether a download can be created for it.</summary>
    public bool Selectable { get; init; }
    public string? Note { get; init; }
}

public sealed record RemoteFilesResult
{
    public IReadOnlyList<RemoteFileView> Files { get; init; } = [];
    /// <summary>The provider doesn't know the files yet. Nothing is invented meanwhile.</summary>
    public bool Pending { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Durable remote jobs, from what the user asked for to local jobs starting. Everything goes through
/// one account loop, which is what spaces submissions and limits polling. Each change is saved before
/// it counts, and the network is never called under the gate. A failed status check changes nothing
/// about what is on the provider, and is never a reason to submit again: only a confirmed absence after
/// the grace period is. Nothing here deletes a remote item except the explicitly confirmed call.
/// </summary>
public sealed partial class RemoteJobCoordinator : IAsyncDisposable
{
    /// <summary>
    /// The most files an input lists to choose from before it is added, or has chosen by path. Every one of them is
    /// saved with the job, and one past this waits to be chosen from when the item is ready.
    /// </summary>
    public const int MostFilesToChooseFrom = 5000;

    private const int MostRoundsPerPass = 10;
    private const int LongestTorrentBytes = TorrentMetadata.MaxFileBytes;
    private const string NoChosenFiles = "None of the chosen files can be downloaded.";
    private const string NoFilesUsable = "None of its files can be downloaded.";
    private static readonly TimeSpan StoreTroublePause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortestLoopWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestLoopWait = TimeSpan.FromHours(1);

    private readonly RemoteProviderRegistry _providers;
    private readonly IRemoteJobStore _store;
    private readonly IRemoteJobHost _host;
    private readonly RemoteCoordinatorOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    // Lets one change at a time through, across its disk write and never across the network.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Taken after the gate, and only for the moment of reading or swapping a record.
    private readonly Lock _sync = new();
    // Across looking up and telling a listener, so no client hears an older state after a newer one. Before _sync.
    private readonly Lock _raising = new();
    private readonly Dictionary<Guid, RemoteJobRecord> _records = [];
    private readonly Dictionary<string, AccountWork> _accounts = new(StringComparer.Ordinal);

    private CancellationTokenSource? _stopping;
    private bool _started;

    public RemoteJobCoordinator(
        RemoteProviderRegistry providers,
        IRemoteJobStore store,
        IRemoteJobHost host,
        RemoteCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _providers = providers;
        _store = store;
        _host = host;
        _options = options ?? new RemoteCoordinatorOptions();
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        foreach (var entry in _providers.All)
        {
            _accounts[entry.Id] = new AccountWork(entry, entry.Hosters is { } directory ? new RemoteHosterCache(directory, _time, _log) : null);
        }
    }

    /// <summary>
    /// The first account's list of sites, from the hourly cache: a call only when it has gone stale. Null when no
    /// provider has one.
    /// </summary>
    public async Task<(RemoteProviderEntry Entry, IReadOnlyList<RemoteHoster> Hosters, string? Problem, DateTimeOffset? FetchedAt)?> GetHosterListAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var work in _accounts.Values)
        {
            if (work.Hosters is not { } cache) continue;
            if (work.Entry.AccountProblem() is { } problem) return (work.Entry, [], problem, null);
            var (matchers, refreshProblem) = await cache.GetAsync(cancellationToken).ConfigureAwait(false);
            return (work.Entry, matchers.Select(matcher => matcher.Hoster).ToList(), refreshProblem, cache.FetchedAt);
        }

        return null;
    }

    /// <summary>A job changed. Raised after the change is saved, and outside the gate.</summary>
    public event EventHandler<RemoteJobView>? Changed;

    /// <summary>A job left the list.</summary>
    public event EventHandler<Guid>? Removed;

    private sealed class AccountWork(RemoteProviderEntry entry, RemoteHosterCache? hosters)
    {
        public RemoteProviderEntry Entry { get; } = entry;
        public RemoteHosterCache? Hosters { get; } = hosters;
        public RemoteAccountPacing Pacing { get; set; } = new();
        public SemaphoreSlim Wake { get; } = new(0, 1);
        public Task? Loop { get; set; }
        public DateTimeOffset StoreTroubleUntil { get; set; }
        public int PassRunning;
        public EventHandler? KeyChanged { get; set; }

        public void Signal()
        {
            try { Wake.Release(); }
            catch (SemaphoreFullException) { /* One wake is already pending, and one is all it takes. */ }
        }
    }

    // ---- Lifecycle ----------------------------------------------------------------------------

    /// <summary>
    /// Loads what was saved and picks up where it stopped: a record still in Submitting can't say
    /// whether it reached the provider, so it becomes SubmissionUnknown and the provider's list decides.
    /// Once per instance: after <see cref="StopAsync"/>, a restart is a new coordinator.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            var now = _time.GetUtcNow();
            // A hold saved by an older build, or with a clock since changed, can't outlast what one is allowed to be.
            var latestHold = now + RemoteJobRules.LongestRateLimitHold;
            foreach (var work in _accounts.Values)
            {
                var pacing = await _store.LoadPacingAsync(work.Entry.Id, cancellationToken).ConfigureAwait(false);
                work.Pacing = pacing.RateLimitedUntil > latestHold ? pacing with { RateLimitedUntil = latestHold } : pacing;
            }

            foreach (var loaded in await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (loaded.State == RemoteJobState.Removed)
                {
                    // The delete after marking it never happened.
                    await DeleteQuietlyAsync(loaded.Id).ConfigureAwait(false);
                    continue;
                }

                var restored = loaded with { State = RemoteJobRules.AfterRestart(loaded.State) };
                if (restored.State != loaded.State)
                {
                    restored = restored with { SubmittedAt = loaded.SubmittedAt ?? now, NextCheckAt = now, UpdatedAt = now };
                    try
                    {
                        await _store.SaveAsync(restored, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Still treated as unknown here. A later save writes it, and a restart repeats this.
                        _log?.Invoke($"Remote job {loaded.Id:N}: could not save after a restart: {ex.GetType().Name}.");
                    }
                }

                if (RemoteJobRules.IsPolled(restored.State) && restored.NextCheckAt is null && !restored.ApprovalNeeded)
                {
                    restored = restored with { NextCheckAt = now };
                }
                else if (restored.NextCheckAt > latestHold)
                {
                    restored = restored with { NextCheckAt = latestHold };
                }

                lock (_sync) _records[restored.Id] = restored;
            }

            _started = true;
            if (_options.RunLoops)
            {
                _stopping = new CancellationTokenSource();
                var stopping = _stopping.Token;
                foreach (var account in _accounts.Values)
                {
                    if (account.Entry.Account is { } store)
                    {
                        account.KeyChanged = (_, _) => _ = OnKeyChangedAsync(account);
                        store.Changed += account.KeyChanged;
                    }

                    account.Loop = Task.Run(() => RunLoopAsync(account, stopping));
                }
            }
        }
        finally { _gate.Release(); }

        await HandOverWhatWasLeftAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? stopping;
        List<Task> loops;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            stopping = _stopping;
            _stopping = null;
            loops = _accounts.Values.Where(work => work.Loop is not null).Select(work => work.Loop!).ToList();
            foreach (var work in _accounts.Values)
            {
                work.Loop = null;
                if (work.KeyChanged is { } handler && work.Entry.Account is { } store)
                {
                    store.Changed -= handler;
                    work.KeyChanged = null;
                }
            }
        }
        finally { _gate.Release(); }

        if (stopping is null)
        {
            return;
        }

        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // How a loop ends.
        }
        catch (Exception ex)
        {
            // Too late to do anything about it, and it mustn't stop the rest of a shutdown.
            _log?.Invoke($"An account's remote job loop had stopped: {ex.GetType().Name}.");
        }
        finally
        {
            stopping.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task OnKeyChangedAsync(AccountWork work)
    {
        try
        {
            work.Hosters?.Invalidate();
            // What the provider said about the old key's account says nothing about this one's.
            await ChangePacingAsync(work, pacing => pacing with { CreationStoppedReason = null, RateLimitedUntil = null }).ConfigureAwait(false);
            await CheckNowAsync(work.Entry.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _log?.Invoke($"Could not wake {work.Entry.Id} after its key changed: {ex.GetType().Name}.");
        }
    }

    // ---- What clients can see -----------------------------------------------------------------

    public IReadOnlyList<RemoteJobView> List()
    {
        List<RemoteJobRecord> records;
        lock (_sync) records = _records.Values.OrderBy(record => record.CreatedAt).ToList();
        return records.Select(ToView).ToList();
    }

    public RemoteJobView? Find(Guid id)
    {
        RemoteJobRecord? record;
        lock (_sync) _records.TryGetValue(id, out record);
        return record is null ? null : ToView(record);
    }

    private RemoteJobView ToView(RemoteJobRecord record)
    {
        var name = _providers.Find(record.Provider)?.DisplayName ?? record.Provider;
        var local = record is { State: RemoteJobState.LocalStarted, PackageId: { } package } ? _host.GetLocalSummary(package) : null;
        return new RemoteJobView
        {
            Id = record.Id,
            Provider = record.Provider,
            ProviderName = name,
            Kind = record.Kind,
            State = record.State,
            Status = RemoteJobRules.StatusText(record, name, local),
            Stage = record.Stage,
            RawState = record.RawState,
            Progress = record.Progress,
            SpeedBytesPerSecond = record.SpeedBytesPerSecond,
            EtaSeconds = record.EtaSeconds,
            ItemId = record.ItemId,
            Name = record.Name,
            DisplayHost = record.DisplayHost,
            Cached = record.Cached,
            ApprovalNeeded = record.ApprovalNeeded,
            HeldForApproval = record.HeldForApproval,
            ApprovedUncached = record.ApprovedUncached,
            HasLinkPassword = record.ProtectedLinkPassword is not null,
            HasArchivePassword = record.ProtectedArchivePassword is not null || record.ArchivePasswordHandedOver,
            FailureKind = record.FailureKind,
            FailureMessage = record.FailureMessage,
            NextCheckAt = record.NextCheckAt,
            Destination = record.Destination,
            QueueId = record.QueueId,
            StartMode = record.StartMode,
            SelectionMode = record.SelectionMode,
            SelectedFileIds = record.SelectedFileIds,
            PackageId = record.PackageId,
            Notes = record.Notes,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };
    }

    // ---- Classifying and adding ---------------------------------------------------------------

    /// <summary>
    /// Sorts one pasted string. Only what would otherwise be an ordinary link needs the hoster list,
    /// which is fetched if there is none less than an hour old.
    /// </summary>
    public async Task<RemoteInputClassification> ClassifyAsync(string? text, CancellationToken cancellationToken = default)
    {
        text = Resolve(text);
        var first = RemoteInputClassifier.Classify(text, await ClassifierProvidersAsync(false, cancellationToken).ConfigureAwait(false));
        return first.Kind == RemoteInputKind.OrdinaryLink
            ? RemoteInputClassifier.Classify(text, await ClassifierProvidersAsync(true, cancellationToken).ConfigureAwait(false))
            : first;
    }

    /// <summary>Sorts many at once, fetching what they all need only once. Each answer is in the place of its input.</summary>
    public async Task<IReadOnlyList<RemoteInputClassification>> ClassifyAllAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        var plain = await ClassifierProvidersAsync(false, cancellationToken).ConfigureAwait(false);
        List<RemoteClassifierProvider>? withHosters = null;
        var answers = new List<RemoteInputClassification>(inputs.Count);
        foreach (var given in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = Resolve(given);
            var first = RemoteInputClassifier.Classify(input, plain);
            if (first.Kind == RemoteInputKind.OrdinaryLink)
            {
                withHosters ??= await ClassifierProvidersAsync(true, cancellationToken).ConfigureAwait(false);
                first = RemoteInputClassifier.Classify(input, withHosters);
            }

            answers.Add(first);
        }

        return answers;
    }

    /// <summary>
    /// As <see cref="ClassifyAsync"/>, and for a magnet or a .torrent file the files it holds, so they can be chosen
    /// before it is added. A .torrent file is read for its own list. A magnet's come from the provider's cache check,
    /// which also says whether it is cached: one that isn't has no files to show until the provider has fetched it.
    /// That check is all that is sent, and when it can't be made, nothing is said about the cache.
    /// </summary>
    /// <param name="sourceRoots">Where this caller may have a .torrent file read from, when not <see cref="Roots"/>.</param>
    public async Task<RemoteInputClassification> ClassifyWithFilesAsync(string? text, CancellationToken cancellationToken = default,
        PathRoots? sourceRoots = null)
    {
        text = Resolve(text);
        var classified = await ClassifyAsync(text, cancellationToken).ConfigureAwait(false);
        if (classified.Kind == RemoteInputKind.TorrentFile)
        {
            var read = await ReadTorrentFileAsync(text!.Trim(), sourceRoots ?? Roots, cancellationToken).ConfigureAwait(false);
            return read.Bytes is null
                ? new RemoteInputClassification { Kind = RemoteInputKind.Unsupported, Reason = read.Error }
                : RemoteInputClassifier.ClassifyTorrent(read.Bytes, await ClassifierProvidersAsync(false, cancellationToken).ConfigureAwait(false), withFiles: true);
        }

        if (classified is not { Kind: RemoteInputKind.Magnet, Hash: { } hash, Provider: { } provider } ||
            !_accounts.TryGetValue(provider, out var work) ||
            work.Entry.CacheChecker is not { } checker ||
            work.Entry.AccountProblem() is not null ||
            work.Pacing.RateLimitedUntil > _time.GetUtcNow())
        {
            return classified;
        }

        try
        {
            var hits = await checker.CheckCachedWithFilesAsync(classified.ItemKind ?? RemoteItemKind.Torrent, [hash], cancellationToken).ConfigureAwait(false);
            if (!hits.TryGetValue(hash, out var hit))
            {
                return classified with { Cached = false };
            }

            return classified with
            {
                Cached = true,
                Files = hit.Files is { Count: > 0 and <= MostFilesToChooseFrom } files ? files.Select(file => ToFileView(work.Entry, file)).ToList() : null,
            };
        }
        catch (Exception ex) when (ex is RemoteProviderException or HttpRequestException or IOException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _log?.Invoke($"A cache check before adding failed: {ex.GetType().Name}.");
            return classified;
        }
    }

    private async Task<List<RemoteClassifierProvider>> ClassifierProvidersAsync(bool withHosters, CancellationToken cancellationToken)
    {
        var result = new List<RemoteClassifierProvider>();
        foreach (var work in _accounts.Values)
        {
            if (withHosters && work.Hosters is { } hosters && work.Entry.AccountProblem() is null)
            {
                var (list, problem) = await hosters.GetAsync(cancellationToken).ConfigureAwait(false);
                result.Add(RemoteClassifierProvider.From(work.Entry, list, problem));
            }
            else
            {
                result.Add(RemoteClassifierProvider.From(work.Entry));
            }
        }

        return result;
    }

    /// <summary>
    /// Saves the intent and hands the rest to the account's loop, so nothing has been sent when this
    /// returns. An input an active job already covers returns that job. Ordinary links aren't this
    /// class's business and come back with an error: they belong to <c>job.add</c>.
    /// </summary>
    public async Task<RemoteAddResult> AddAsync(RemoteAddRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(request.Destination) && !Path.IsPathFullyQualified(request.Destination))
        {
            return new RemoteAddResult { Error = "The destination has to be a full path." };
        }

        if (!string.IsNullOrEmpty(request.Destination) && (request.DestinationRoots ?? DestinationRoots) is { } destinationRoots &&
            destinationRoots.Allowed(request.Destination) is null)
        {
            // Checked here, before any provider call or quota is spent, and again for each file when it is added.
            return new RemoteAddResult { Error = destinationRoots.OutsideTextFor(request.Destination) };
        }

        if (request.StartMode is not ("startNow" or "queue" or "later"))
        {
            return new RemoteAddResult { Error = "The start mode has to be startNow, queue or later." };
        }

        if (request.Selection == RemoteSelectionMode.Chosen && request.FileIds is not { Count: > 0 } && request.FilePaths is not { Count: > 0 })
        {
            return new RemoteAddResult { Error = "Choose at least one file." };
        }

        if (request.Selection == RemoteSelectionMode.Chosen && request.FilePaths is { Count: > 0 } paths)
        {
            if (request.FileIds is { Count: > 0 })
            {
                return new RemoteAddResult { Error = "Choose files by id or by path, not both." };
            }

            if (paths.Count > MostFilesToChooseFrom)
            {
                return new RemoteAddResult { Error = "That is too many files to choose before it is added. Choose them when it is ready." };
            }

            if (paths.Any(path => RemoteFileKey.Of(path.Path, path.Size) is null))
            {
                return new RemoteAddResult { Error = "A chosen file has a path that can't be used." };
            }
        }

        if (RemoteJobRules.DescribeBadArchivePassword(request.ArchivePassword) is { } badPassword)
        {
            return new RemoteAddResult { Error = badPassword };
        }

        var built = await BuildRecordAsync(request, cancellationToken).ConfigureAwait(false);
        if (built.Record is not { } record)
        {
            return new RemoteAddResult { Error = built.Error, Kind = built.Kind };
        }

        if (request.HoldForApproval)
        {
            record = record with { State = RemoteJobState.Intended, ApprovalNeeded = true, HeldForApproval = true, NextCheckAt = null };
        }

        RemoteJobRecord saved;
        var existing = false;
        string? note = null;
        IReadOnlyList<RemoteJobRecord> spent = [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (var found, spent) = FindTwinLocked(record);
            if (found is { } twin)
            {
                existing = true;
                saved = twin;
                if (twin.HeldForApproval && !request.HoldForApproval)
                {
                    // Someone asked for it now, so it is no longer only a copied link. Their approval, or lack of one, is what counts.
                    saved = ReleasedOnRequest(twin, record, request.ApprovedUncached);
                    await _store.SaveAsync(saved, CancellationToken.None).ConfigureAwait(false);
                    lock (_sync) _records[saved.Id] = saved;
                }
                else
                {
                    note = WhatATwinIgnores(request, twin);

                    // Also before anything has looked at it: an approval given now must still count then.
                    if (request.ApprovedUncached && twin.State == RemoteJobState.Intended && !twin.ApprovedUncached)
                    {
                        saved = twin with
                        {
                            ApprovedUncached = true,
                            ApprovalNeeded = false,
                            NextCheckAt = twin.ApprovalNeeded ? record.CreatedAt : twin.NextCheckAt,
                            UpdatedAt = record.CreatedAt,
                        };
                        await _store.SaveAsync(saved, CancellationToken.None).ConfigureAwait(false);
                        lock (_sync) _records[saved.Id] = saved;
                    }
                }
            }
            else
            {
                // The write comes first: if it fails, nothing has happened.
                await _store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
                lock (_sync) _records[record.Id] = record;
                saved = record;

                // Only now, so a crash in between leaves the new one, which a later add finds first.
                foreach (var old in spent) await ForgetAsync(old).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }

        if (!existing)
        {
            foreach (var old in spent) Removed?.Invoke(this, old.Id);
        }

        Raise(saved);
        WakeLoop(saved.Provider);

        // Whoever gives a password now wants it used, whichever job ends up bringing the files.
        var alreadyHasOne = saved.ProtectedArchivePassword is not null || saved.ArchivePasswordHandedOver;
        if (existing && !string.IsNullOrEmpty(request.ArchivePassword) && !(request.ArchivePasswordOffered && alreadyHasOne))
        {
            var given = await SetArchivePasswordAsync(saved.Id, request.ArchivePassword, request.ArchivePasswordOffered, cancellationToken).ConfigureAwait(false);
            if (!given.Ok)
            {
                note = note is null ? given.Error : $"{note} {given.Error}";
            }

            return new RemoteAddResult { Job = given.Job ?? Find(saved.Id) ?? ToView(saved), Existing = true, Kind = built.Kind, Note = note };
        }

        return new RemoteAddResult { Job = ToView(saved), Existing = existing, Kind = built.Kind, Note = note };
    }

    /// <summary>Adding what is already on the list returns that job as it is. Whoever asked for something different should hear so.</summary>
    private static string? WhatATwinIgnores(RemoteAddRequest request, RemoteJobRecord twin)
    {
        var folder = false;
        if (!string.IsNullOrWhiteSpace(request.Destination) &&
            !string.Equals(request.Destination.TrimEnd('\\', '/'), twin.Destination?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            folder = true;
        }

        var files = request.Selection is { } selection && (selection != twin.SelectionMode ||
            (selection == RemoteSelectionMode.Chosen && !(
                (request.FileIds ?? []).SequenceEqual(twin.SelectedFileIds) &&
                ChosenPaths(request).SequenceEqual(twin.SelectedPaths))));

        const string AnotherFolder = "To use another folder, remove it from the list and add it again.";
        const string ChooseFiles = "Its files can still be chosen from the list.";
        return (folder, files) switch
        {
            (true, true) => $"This is already on the list, so the folder and file choice given here weren't used. {ChooseFiles} {AnotherFolder}",
            (true, false) => $"This is already on the list, so the folder given here wasn't used. {AnotherFolder}",
            (false, true) => $"This is already on the list, so the file choice given here wasn't used. {ChooseFiles}",
            _ => null,
        };
    }

    /// <summary>
    /// The job already covering this input, if any. One whose downloads have all left the list since covers
    /// nothing any more: it is spent, and adding the input again starts it again instead of naming it.
    /// </summary>
    private (RemoteJobRecord? Twin, IReadOnlyList<RemoteJobRecord> Spent) FindTwinLocked(RemoteJobRecord record)
    {
        List<RemoteJobRecord> candidates;
        lock (_sync)
        {
            candidates = _records.Values.Where(other =>
                other.Provider == record.Provider &&
                other.Kind == record.Kind &&
                other.State is not (RemoteJobState.Failed or RemoteJobState.Removed) &&
                ((record.PredictedHash is not null && string.Equals(other.PredictedHash, record.PredictedHash, StringComparison.OrdinalIgnoreCase)) ||
                 (record.ItemId is not null && other.ItemId == record.ItemId))).ToList();
        }

        // Outside the lock: the count takes the service's own.
        var spent = candidates.Where(other => other.State == RemoteJobState.LocalStarted && _host.CountLocalJobs(other.Id) == 0).ToList();
        return (candidates.Except(spent).FirstOrDefault(), spent);
    }

    private async Task<(RemoteJobRecord? Record, string? Error, RemoteInputKind? Kind)> BuildRecordAsync(RemoteAddRequest request, CancellationToken cancellationToken)
    {
        if (Resolve(request.Input) is { } resolved && !ReferenceEquals(resolved, request.Input)) request = request with { Input = resolved };
        if (request.ExistingItem is { } item)
        {
            if (_providers.Find(item.Provider) is not { } entry)
            {
                return (null, $"This app has no provider called {item.Provider}.", null);
            }

            // What the provider answers about this item is matched against this, so only its own spelling will do.
            var itemId = entry.Catalog is { } catalog ? catalog.NormalizeItemId(item.ItemId) : item.ItemId;
            return itemId is null
                ? (null, $"That isn't an item id {entry.DisplayName} uses.", null)
                : (NewRecord(request, item.Provider, item.Kind) with { ItemId = itemId, State = RemoteJobState.Waiting }, null, null);
        }

        if (request.TorrentFile is { } bytes)
        {
            return BuildTorrentRecord(request, bytes, await ClassifierProvidersAsync(false, cancellationToken).ConfigureAwait(false));
        }

        var classification = await ClassifyAsync(request.Input, cancellationToken).ConfigureAwait(false);
        switch (classification.Kind)
        {
            case RemoteInputKind.FolderLink:
                // Nothing of the pasted link is kept: its id and type say all there is, and its name is anyone's guess.
                return (NewRecord(request, classification.Provider!, classification.ItemKind!.Value) with
                {
                    ItemId = classification.ItemId,
                    State = RemoteJobState.Waiting,
                }, null, classification.Kind);

            case RemoteInputKind.Magnet:
            case RemoteInputKind.HosterLink:
                if (classification.Hash is null)
                {
                    return (null, "This link can't be submitted, because it couldn't be found again if the answer got lost.", classification.Kind);
                }

                // Without a choice, a hoster link is one file to fetch, and so starts by itself. A magnet can hold anything.
                return (NewRecord(request, classification.Provider!, classification.ItemKind!.Value) with
                {
                    SelectionMode = request.Selection ?? (classification.Kind == RemoteInputKind.HosterLink ? RemoteSelectionMode.All : RemoteSelectionMode.AskIfCached),
                    ProtectedInput = SecretProtector.ProtectRemoteInput(request.Input!.Trim()),
                    ProtectedLinkPassword = string.IsNullOrEmpty(request.LinkPassword) ? null : SecretProtector.ProtectRemoteLinkPassword(request.LinkPassword),
                    PredictedHash = classification.Hash,
                    Name = classification.Name ?? request.NameHint,
                    DisplayHost = classification.DisplayHost,
                    HosterName = classification.HosterName,
                    State = RemoteJobState.Intended,
                }, null, classification.Kind);

            case RemoteInputKind.TorrentFile:
                var read = await ReadTorrentFileAsync(request.Input!.Trim(), request.SourceRoots ?? Roots, cancellationToken).ConfigureAwait(false);
                return read.Bytes is null
                    ? (null, read.Error, classification.Kind)
                    : BuildTorrentRecord(request, read.Bytes, await ClassifierProvidersAsync(false, cancellationToken).ConfigureAwait(false));

            case RemoteInputKind.Unsupported or RemoteInputKind.Undetermined:
                return (null, classification.Reason, classification.Kind);

            default:
                return (null, "This is an ordinary link, which downloads directly rather than through a provider.", classification.Kind);
        }
    }

    private (RemoteJobRecord? Record, string? Error, RemoteInputKind? Kind) BuildTorrentRecord(
        RemoteAddRequest request,
        byte[] bytes,
        IReadOnlyList<RemoteClassifierProvider> providers)
    {
        var classification = RemoteInputClassifier.ClassifyTorrent(bytes, providers);
        return classification.Kind != RemoteInputKind.TorrentFile
            ? (null, classification.Reason, classification.Kind)
            : (NewRecord(request, classification.Provider!, RemoteItemKind.Torrent) with
            {
                SelectionMode = request.Selection ?? RemoteSelectionMode.AskIfCached,
                ProtectedTorrent = SecretProtector.ProtectRemoteTorrent(bytes),
                PredictedHash = classification.Hash,
                Name = classification.Name,
                State = RemoteJobState.Intended,
            }, null, classification.Kind);
    }

    /// <summary>
    /// On a server, the folders whose files a client may have it read (a .torrent file given by its path). A client
    /// there is not the machine's user. Null on the desktop, where any file the user names may be read.
    /// </summary>
    public PathRoots? Roots { get; set; }

    /// <summary>
    /// On a server, the folders a client may have downloads saved in (<c>DOWNLOADMANAGER_ROOTS</c>, without the upload
    /// folder). A destination outside them is refused before the provider is asked for anything. Null on the desktop.
    /// </summary>
    public PathRoots? DestinationRoots { get; set; }

    /// <summary>
    /// On a server, turns a reference to a file a client uploaded (the server's own form, such as <c>upload:…</c>) into
    /// that file's path, or gives null for anything else. The client then never learns where the server keeps it.
    /// </summary>
    public Func<string, string?>? ResolveReference { get; set; }

    private string? Resolve(string? text) =>
        text is not null && ResolveReference is { } resolve && resolve(text.Trim()) is { } path ? path : text;

    private static async Task<(byte[]? Bytes, string? Error)> ReadTorrentFileAsync(string path, PathRoots? roots, CancellationToken cancellationToken)
    {
        if (roots is not null)
        {
            if (roots.Allowed(path) is not { } inside) return (null, roots.OutsideFileText(".torrent file"));
            path = inside;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return (null, "That .torrent file doesn't exist.");
            }

            return info.Length > LongestTorrentBytes
                ? (null, "This .torrent file is too large to be a real one.")
                : (await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, "That .torrent file can't be read.");
        }
    }

    private RemoteJobRecord NewRecord(RemoteAddRequest request, string provider, RemoteItemKind kind)
    {
        var now = _time.GetUtcNow();
        return new RemoteJobRecord
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            Kind = kind,
            Destination = string.IsNullOrWhiteSpace(request.Destination) ? null : request.Destination,
            QueueId = request.QueueId,
            StartMode = request.StartMode,
            SelectionMode = request.Selection ?? RemoteSelectionMode.AskWhenReady,
            SelectedFileIds = request.Selection == RemoteSelectionMode.Chosen ? request.FileIds?.ToList() ?? [] : [],
            SelectedPaths = ChosenPaths(request),
            ApprovedUncached = request.ApprovedUncached,
            ProtectedArchivePassword = string.IsNullOrEmpty(request.ArchivePassword) ? null : SecretProtector.ProtectRemoteArchivePassword(request.ArchivePassword),
            ArchivePasswordOffered = request.ArchivePasswordOffered,
            Placement = request.Placement,
            Name = request.NameHint,
            NextCheckAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>What finds each file chosen by path again, once <see cref="AddAsync"/> has checked that every one can.</summary>
    private static List<RemoteFileKey> ChosenPaths(RemoteAddRequest request)
        => request.Selection == RemoteSelectionMode.Chosen && request.FilePaths is { } paths
            ? paths.Select(path => RemoteFileKey.Of(path.Path, path.Size)).OfType<RemoteFileKey>().ToList()
            : [];

    // ---- User actions -------------------------------------------------------------------------

    /// <summary>The user agrees to have the provider start work on this if it isn't cached.</summary>
    public Task<RemoteActionResult> ApproveUncachedAsync(Guid id, CancellationToken cancellationToken = default)
        => ActAsync(id, record => record.State == RemoteJobState.Intended
            ? record.HeldForApproval
                ? ReleasedFromHold(record, approved: true, _time.GetUtcNow())
                : record with { ApprovedUncached = true, ApprovalNeeded = false, NextCheckAt = _time.GetUtcNow() }
            : null, "This item has already been sent.", cancellationToken);

    /// <summary>What a held job would have been from the start. A folder link has nothing to submit: its item is on the account already.</summary>
    /// <summary>
    /// A held job came from the clipboard with every option at its default and nothing has been sent, so what
    /// the user asks for now replaces those defaults. It stays the same job, on the same input. An archive
    /// password isn't taken here: <see cref="AddAsync"/> hands it over after this, as for any job already listed.
    /// </summary>
    private static RemoteJobRecord ReleasedOnRequest(RemoteJobRecord twin, RemoteJobRecord asked, bool approved)
        => ReleasedFromHold(twin, approved, asked.CreatedAt) with
        {
            Destination = asked.Destination,
            QueueId = asked.QueueId,
            StartMode = asked.StartMode,
            SelectionMode = asked.SelectionMode,
            SelectedFileIds = asked.SelectedFileIds,
            SelectedPaths = asked.SelectedPaths,
            ProtectedLinkPassword = asked.ProtectedLinkPassword,
            Placement = asked.Placement,
            Name = asked.Name ?? twin.Name,
        };

    private static RemoteJobRecord ReleasedFromHold(RemoteJobRecord record, bool approved, DateTimeOffset now) => record with
    {
        State = record is { PredictedHash: null, ItemId: not null } ? RemoteJobState.Waiting : RemoteJobState.Intended,
        HeldForApproval = false,
        ApprovalNeeded = false,
        ApprovedUncached = approved || record.ApprovedUncached,
        NextCheckAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Sets which files get local jobs, then checks the item once more before creating them. Files
    /// already downloading stay as they are, and choosing some again doesn't repeat them.
    /// </summary>
    public async Task<RemoteActionResult> ChooseFilesAsync(Guid id, bool all, IReadOnlyList<string>? fileIds, CancellationToken cancellationToken = default)
    {
        if (!all && fileIds is not { Count: > 0 })
        {
            return new RemoteActionResult(false, "Choose at least one file.");
        }

        // Refused in words now, rather than becoming a failure of a job that may be working fine.
        if (!all && await NoneOfTheseFilesExistAsync(id, fileIds!, cancellationToken).ConfigureAwait(false))
        {
            return new RemoteActionResult(false, "None of those files are in this item. List them with remote files.");
        }

        return await ActAsync(id, record =>
        {
            // A failure that came from the last choice is exactly what a new choice is for.
            var failedOnChoice = record is { State: RemoteJobState.Failed, FailureMessage: NoChosenFiles };
            if (record.State == RemoteJobState.Removed || (record.State == RemoteJobState.Failed && !failedOnChoice))
            {
                return null;
            }

            // This replaces any choice made before the provider listed the files, found or not.
            var chosen = record with
            {
                SelectionMode = all ? RemoteSelectionMode.All : RemoteSelectionMode.Chosen,
                SelectedFileIds = all ? [] : fileIds!.ToList(),
                SelectedPaths = [],
            };
            if (failedOnChoice)
            {
                chosen = chosen with { FailureKind = null, FailureMessage = null, ConsecutiveFailures = 0 };
            }
            else if (record.State == RemoteJobState.AwaitingSelection)
            {
                // What it said was about the choice this one replaces.
                chosen = chosen with { FailureMessage = null };
            }

            return failedOnChoice || record.State is RemoteJobState.AwaitingSelection or RemoteJobState.LocalStarted
                ? chosen with { State = RemoteJobState.Waiting, NextCheckAt = _time.GetUtcNow() }
                : chosen;
        }, "This item can't take a file choice now. Retry it first.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True only when the provider's own list of a finished item says so. If it can't say, for any
    /// reason, the choice is taken and the next look at the item decides.
    /// </summary>
    private async Task<bool> NoneOfTheseFilesExistAsync(Guid id, IReadOnlyList<string> fileIds, CancellationToken cancellationToken)
    {
        if (Find(id) is not { ItemId: { } itemId } view || _providers.Find(view.Provider) is not { Catalog: { } catalog } entry || entry.AccountProblem() is not null)
        {
            return false;
        }

        try
        {
            var item = await catalog.GetAsync(new RemoteItemRef(view.Provider, view.Kind, itemId), fresh: false, cancellationToken).ConfigureAwait(false);
            return item is { Stage: RemoteStage.Ready } && !item.Files.Any(file => fileIds.Contains(file.FileId, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is RemoteProviderException or HttpRequestException or IOException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>
    /// Ends a failure or a wait on the user. A known item is simply looked at again; the only thing
    /// that gets submitted again is an input that isn't on the account, after the same checks as the
    /// first time. Lifts the account's stop on creating items, if a quota error caused one, and any
    /// rate limit hold: the user can always ask for one more look, and hear the answer.
    /// </summary>
    public async Task<RemoteActionResult> RetryAsync(Guid id, string? linkPassword = null, bool? approveUncached = null, CancellationToken cancellationToken = default)
    {
        var password = string.IsNullOrEmpty(linkPassword) ? null : SecretProtector.ProtectRemoteLinkPassword(linkPassword);
        if (Find(id) is { State: RemoteJobState.Failed or RemoteJobState.Blocked } view && _accounts.TryGetValue(view.Provider, out var work))
        {
            await ChangePacingAsync(work, pacing => pacing with { CreationStoppedReason = null, RateLimitedUntil = null }).ConfigureAwait(false);
        }

        string? refusal = null;
        var retried = await ActAsync(id, record =>
        {
            if (record.State is not (RemoteJobState.Failed or RemoteJobState.Blocked))
            {
                return null;
            }

            var gone = record.FailureKind == RemoteFailureKind.NotFound;
            var canSubmit = record.ProtectedInput is not null || record.ProtectedTorrent is not null;
            var submitsAgain = canSubmit && (record.ItemId is null || gone);
            // The item failed on the provider, and a better one under the same hash may exist by now.
            // Something submitted can be looked for again by its hash, and absent then is absent.
            var looksByHash = canSubmit && !submitsAgain && record.PredictedHash is not null && record.FailureKind == RemoteFailureKind.Rejected;
            var keptItem = submitsAgain || looksByHash ? null : record.ItemId;
            // Its item may have been added again as another job since this one failed, and that one brings its files.
            if (keptItem is not null && OtherHolderLocked(record, keptItem) is not null)
            {
                refusal = AlreadyBroughtByAnother;
                return null;
            }

            return record with
            {
                State = submitsAgain ? RemoteJobState.Intended : RemoteJobState.Waiting,
                ItemId = keptItem,
                ProtectedLinkPassword = password ?? record.ProtectedLinkPassword,
                ApprovedUncached = approveUncached ?? record.ApprovedUncached,
                ApprovalNeeded = false,
                FailureKind = null,
                FailureMessage = null,
                ConsecutiveFailures = 0,
                Resubmits = 0,
                LastSubmitAnswer = null,
                MissingSince = null,
                NextCheckAt = _time.GetUtcNow(),
            };
        }, "Only a failed or blocked item can be retried.", cancellationToken).ConfigureAwait(false);
        return refusal is null ? retried : new RemoteActionResult(false, refusal);
    }

    /// <summary>
    /// Sets the password for this job's archives, whether or not its downloads exist yet. It is saved on
    /// the job first, so a crash can't lose it, and goes on to the archive side once there is a package.
    /// An empty one clears it, on both sides.
    /// </summary>
    public Task<RemoteActionResult> SetArchivePasswordAsync(Guid id, string? password, CancellationToken cancellationToken = default)
        => SetArchivePasswordAsync(id, password, offered: false, cancellationToken);

    /// <summary>
    /// TB.S2: offers a password to this job's own archives, with the user's consent given where it was typed. A job
    /// that has one already, given by the user or handed over, keeps it, and so does its package on the archive side.
    /// </summary>
    public async Task<RemoteActionResult> OfferArchivePasswordAsync(Guid id, string password, CancellationToken cancellationToken = default)
    {
        RemoteJobRecord? record;
        lock (_sync) _records.TryGetValue(id, out record);
        if (record is null)
        {
            return new RemoteActionResult(false, "That item is not on the list.");
        }

        if (record.ProtectedArchivePassword is not null || record.ArchivePasswordHandedOver)
        {
            return new RemoteActionResult(true, null, ToView(record));
        }

        return await SetArchivePasswordAsync(id, password, offered: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteActionResult> SetArchivePasswordAsync(Guid id, string? password, bool offered, CancellationToken cancellationToken)
    {
        if (RemoteJobRules.DescribeBadArchivePassword(password) is { } bad)
        {
            return new RemoteActionResult(false, bad);
        }

        if (Find(id) is null)
        {
            return new RemoteActionResult(false, "That item is not on the list.");
        }

        var clearing = string.IsNullOrEmpty(password);
        var sealedPassword = clearing ? null : SecretProtector.ProtectRemoteArchivePassword(password!);
        var updated = await UpdateAsync(id, record => record with
        {
            ProtectedArchivePassword = sealedPassword,
            ArchivePasswordHandedOver = false,
            ArchivePasswordOffered = offered,
        }, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return new RemoteActionResult(false, "That item is not on the list.");
        }

        string? problem = null;
        if (clearing)
        {
            // One password serves a whole package, so its other jobs must not hand it back later or go on saying they have one.
            List<Guid> siblings;
            lock (_sync)
            {
                siblings = _records.Values
                    .Where(other => other.Id != id && SharesPackage(other, updated) && (other.ProtectedArchivePassword is not null || other.ArchivePasswordHandedOver))
                    .Select(other => other.Id)
                    .ToList();
            }

            foreach (var sibling in siblings)
            {
                await UpdateAsync(sibling, record => record with
                {
                    ProtectedArchivePassword = null,
                    ArchivePasswordHandedOver = false,
                    ArchivePasswordOffered = false,
                }, cancellationToken).ConfigureAwait(false);
            }

            if (updated.PackageId is { } package && _options.PackagePasswords is { } keeper)
            {
                problem = await KeepPasswordAsync(keeper, package, null, keepExisting: false).ConfigureAwait(false);
            }
        }
        else
        {
            problem = await HandOverArchivePasswordAsync(id).ConfigureAwait(false);
        }

        return new RemoteActionResult(problem is null, problem, Find(id));
    }

    private static bool SharesPackage(RemoteJobRecord one, RemoteJobRecord other) =>
        (one.PackageId is { } package && package == other.PackageId) ||
        (one.Placement?.PackageKey is { } key && key == other.Placement?.PackageKey);

    /// <summary>
    /// Gives a job's archive password to the archive side and takes it off the job, so one copy is kept.
    /// Does nothing until there is a package. A crash before the job is saved means handing it over twice,
    /// which changes nothing. Returns why it couldn't be done.
    /// </summary>
    private async Task<string?> HandOverArchivePasswordAsync(Guid id)
    {
        RemoteJobRecord? record;
        lock (_sync) _records.TryGetValue(id, out record);
        if (record is not { PackageId: { } package, ProtectedArchivePassword: { } sealedPassword } || _options.PackagePasswords is not { } keeper)
        {
            return null;
        }

        var plain = SecretProtector.UnprotectRemoteArchivePassword(sealedPassword);
        if (plain is null)
        {
            await UpdateAsync(id, latest => latest.ProtectedArchivePassword == sealedPassword
                ? latest with { ProtectedArchivePassword = null, Notes = [.. latest.Notes, "The saved archive password can't be read on this Windows account. Enter it again if extraction asks."] }
                : null).ConfigureAwait(false);
            return null;
        }

        if (await KeepPasswordAsync(keeper, package, plain, record.ArchivePasswordOffered).ConfigureAwait(false) is { } problem)
        {
            return problem;
        }

        await UpdateAsync(id, latest => latest.ProtectedArchivePassword == sealedPassword
            ? latest with { ProtectedArchivePassword = null, ArchivePasswordHandedOver = true }
            : null).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> KeepPasswordAsync(IPackagePasswordKeeper keeper, Guid package, string? password, bool keepExisting)
    {
        try
        {
            return RemoteText.Clean(await keeper.SetPackagePasswordAsync(package, password, keepExisting, CancellationToken.None).ConfigureAwait(false), 300, hide: [password]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"An archive password could not be saved: {ex.GetType().Name}.");
            return "The archive password could not be saved, so it stays with this item. Try again in a moment.";
        }
    }

    /// <summary>A crash can fall between saving a password on a job that has its package and handing it over.</summary>
    private async Task HandOverWhatWasLeftAsync()
    {
        List<Guid> waiting;
        lock (_sync) waiting = _records.Values.Where(record => record is { PackageId: not null, ProtectedArchivePassword: not null }).Select(record => record.Id).ToList();
        foreach (var id in waiting)
        {
            try
            {
                if (await HandOverArchivePasswordAsync(id).ConfigureAwait(false) is { } problem)
                {
                    _log?.Invoke($"Remote job {id:N}: its archive password was not handed over: {problem}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // It stays on the job, and the next start tries again.
                _log?.Invoke($"Remote job {id:N}: could not save after handing over its archive password: {ex.GetType().Name}.");
            }
        }
    }

    /// <summary>Check Now: looks at these jobs on the next pass, without waiting for their timers. A 429's wait still holds.</summary>
    public async Task<RemoteActionResult> CheckNowAsync(Guid id, CancellationToken cancellationToken = default)
        => await ActAsync(id, record => CheckedNow(record), "This item has nothing to check.", cancellationToken).ConfigureAwait(false);

    /// <summary>Check Now for everything on one account, or everywhere.</summary>
    public async Task CheckNowAsync(string? providerId = null, CancellationToken cancellationToken = default)
    {
        foreach (var view in List().Where(view => providerId is null || view.Provider == providerId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ActAsync(view.Id, record => CheckedNow(record), string.Empty, cancellationToken).ConfigureAwait(false);
        }

        foreach (var work in _accounts.Values)
        {
            work.Signal();
        }
    }

    private RemoteJobRecord? CheckedNow(RemoteJobRecord record)
    {
        // A look at a held job could submit it if it is cached, and nobody has agreed to that.
        if (record.HeldForApproval)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        if (record.State == RemoteJobState.AwaitingSelection)
        {
            return record with { State = RemoteJobState.Waiting, NextCheckAt = now };
        }

        return RemoteJobRules.IsPolled(record.State) ? record with { NextCheckAt = now } : null;
    }

    /// <summary>
    /// Takes the job off this app's list. The provider's item and any local downloads stay exactly
    /// as they are: removing it from the provider is <see cref="RemoveRemoteItemAsync"/>, and local
    /// downloads have their own controls.
    /// </summary>
    public async Task<RemoteActionResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoteJobRecord? record;
            lock (_sync) _records.TryGetValue(id, out record);
            if (record is null)
            {
                return new RemoteActionResult(false, "That item is not on the list.");
            }

            await ForgetAsync(record).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        Removed?.Invoke(this, id);
        return new RemoteActionResult(true);
    }

    /// <summary>Takes a job off the list for good. Called under the gate.</summary>
    private async Task ForgetAsync(RemoteJobRecord record)
    {
        // Secrets go first, in a save of their own: a crash before the delete leaves nothing to find.
        await _store.SaveAsync(record with
        {
            State = RemoteJobState.Removed,
            ProtectedInput = null,
            ProtectedTorrent = null,
            ProtectedLinkPassword = null,
            ProtectedArchivePassword = null,
            NextCheckAt = null,
        }, CancellationToken.None).ConfigureAwait(false);
        lock (_sync) _records.Remove(record.Id);
        await DeleteQuietlyAsync(record.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the item from the provider. Only for an explicit, confirmed choice of this job's item
    /// by the user. Nothing else in this class calls it, and pausing or cancelling downloads never does.
    /// </summary>
    public async Task<RemoteActionResult> RemoveRemoteItemAsync(Guid id, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return new RemoteActionResult(false, "Removing an item from the provider has to be confirmed first.");
        }

        var view = Find(id);
        if (view is null)
        {
            return new RemoteActionResult(false, "That item is not on the list.");
        }

        var entry = _providers.Find(view.Provider);
        if (entry?.Remover is not { } remover)
        {
            return new RemoteActionResult(false, $"{view.ProviderName} can't remove items from here.");
        }

        if (view.ItemId is not { } itemId)
        {
            // TB.S1: waiting in the provider's queue, there is no item yet, only the queue's entry.
            if (view.Stage == RemoteStage.Queued && view.State == RemoteJobState.Waiting)
            {
                return await ControlQueuedAsync(id, RemoteQueueAction.Remove, cancellationToken).ConfigureAwait(false);
            }

            return new RemoteActionResult(false, $"This item isn't known to be on your {view.ProviderName} account.");
        }

        try
        {
            await remover.RemoveAsync(new RemoteItemRef(view.Provider, view.Kind, itemId), cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteProviderException ex) when (ex.Kind == RemoteFailureKind.NotFound)
        {
            // Already gone from the provider, such as deleted on its site: what was asked for is true.
        }
        catch (RemoteProviderException ex)
        {
            return new RemoteActionResult(false, Describe(entry, ex));
        }

        // The item is gone now, so a client leaving can't be allowed to keep the job on the list.
        return await RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// TB.S1: starts, or takes out, a job's entry in the provider's queue. Only from an explicit user action, and only
    /// for a job still waiting there with no item: its entry is found again by the job's hash in a fresh read of the
    /// queue, so nothing else in the queue can be touched. Start has the account looked at again at once, which
    /// finds the new item by that hash; Remove takes the job off the list too, since nothing is left to wait for.
    /// </summary>
    public async Task<RemoteActionResult> ControlQueuedAsync(Guid id, RemoteQueueAction action, CancellationToken cancellationToken = default)
    {
        RemoteJobRecord? record;
        lock (_sync) _records.TryGetValue(id, out record);
        if (record is null)
        {
            return new RemoteActionResult(false, "That item is not on the list.");
        }

        var entry = _providers.Find(record.Provider);
        var name = entry?.DisplayName ?? record.Provider;
        if (record is not { State: RemoteJobState.Waiting, Stage: RemoteStage.Queued, ItemId: null })
        {
            return new RemoteActionResult(false, $"This item isn't waiting in your {name} queue.");
        }

        if (entry?.Queue is not { } queue || entry.Provider is not IRemoteQueueControl control)
        {
            return new RemoteActionResult(false, $"{name}'s queue can't be changed from this app.");
        }

        if (entry.AccountProblem() is { } problem)
        {
            return new RemoteActionResult(false, problem);
        }

        try
        {
            var queueId = await FindQueueIdAsync(queue, record, cancellationToken).ConfigureAwait(false);
            if (queueId is null)
            {
                // Started or removed meanwhile, here or on the site: a look settles which.
                await ActAsync(id, CheckedNow, string.Empty, cancellationToken).ConfigureAwait(false);
                return new RemoteActionResult(false, $"This item is no longer in your {name} queue. It is being checked again.");
            }

            await control.ControlQueuedAsync(queueId, action, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteProviderException ex)
        {
            return new RemoteActionResult(false, Describe(entry, ex));
        }

        if (action == RemoteQueueAction.Remove)
        {
            return await RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
        }

        _log?.Invoke($"Remote job {id:N}: started from the {name} queue at the user's request.");
        return await ActAsync(id, CheckedNow, "This item has nothing to check.", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The queue's own id for this job's entry, matched by hash. Null when it isn't there, or no hash is known.</summary>
    private async Task<string?> FindQueueIdAsync(IRemoteQueue queue, RemoteJobRecord record, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.PredictedHash))
        {
            return null;
        }

        for (var page = 0; page < _options.MostListPages; page++)
        {
            var read = await queue.ListQueuedAsync(record.Kind, page * _options.ListPageSize, _options.ListPageSize, fresh: true, cancellationToken).ConfigureAwait(false);
            var hit = read.FirstOrDefault(queued => !string.IsNullOrWhiteSpace(queued.QueueId) &&
                string.Equals(queued.Hash, record.PredictedHash, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit.QueueId;
            }

            if (read.Count < _options.ListPageSize)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The item's files as they are now, with their safe paths. Nothing is invented while the provider doesn't know them.</summary>
    public async Task<RemoteFilesResult> GetFilesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var view = Find(id);
        if (view is null)
        {
            return new RemoteFilesResult { Error = "That item is not on the list." };
        }

        if (view.ItemId is not { } itemId)
        {
            return new RemoteFilesResult { Pending = true };
        }

        var entry = _providers.Find(view.Provider);
        if (entry?.Catalog is not { } catalog)
        {
            return new RemoteFilesResult { Error = $"{view.ProviderName} is not available in this app." };
        }

        RemoteItem? item;
        try
        {
            item = await catalog.GetAsync(new RemoteItemRef(view.Provider, view.Kind, itemId), fresh: true, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteProviderException ex)
        {
            return new RemoteFilesResult { Error = Describe(entry, ex) };
        }

        if (item is null)
        {
            return new RemoteFilesResult { Error = NotOnAccount(entry) };
        }

        return new RemoteFilesResult
        {
            Pending = item.Stage != RemoteStage.Ready,
            Files = item.Files.Select(file => ToFileView(entry, file)).ToList(),
        };
    }

    internal static RemoteFileView ToFileView(RemoteProviderEntry entry, RemoteFile file) => ToFileView(entry.DisplayName, file);

    internal static RemoteFileView ToFileView(string providerName, RemoteFile file)
    {
        var note = RemoteChildPlanner.Check(file, out var relative) switch
        {
            RemoteChildPlanner.Unusable.UnsafePath => "Its path isn't safe to use.",
            RemoteChildPlanner.Unusable.Infected => $"{providerName} marks it as infected.",
            RemoteChildPlanner.Unusable.Empty => "It is empty.",
            _ => null,
        };
        return new RemoteFileView
        {
            FileId = file.FileId,
            RelativePath = relative,
            Size = file.Size,
            Infected = file.Infected,
            Selectable = note is null,
            Note = note,
        };
    }

    private async Task<RemoteActionResult> ActAsync(Guid id, Func<RemoteJobRecord, RemoteJobRecord?> change, string refusal, CancellationToken cancellationToken)
    {
        if (Find(id) is null)
        {
            return new RemoteActionResult(false, "That item is not on the list.");
        }

        var updated = await UpdateAsync(id, change, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return new RemoteActionResult(false, refusal);
        }

        WakeLoop(updated.Provider);
        return new RemoteActionResult(true, null, ToView(updated));
    }

    // ---- Saved state ---------------------------------------------------------------------------

    /// <summary>
    /// The one way a record changes. The change sees the latest record, so a result that arrives after
    /// the user acted decides for itself whether it still applies. Returns null when it didn't. The
    /// write can't be cancelled, and the account's own steps don't pass a token at all, because what a
    /// provider just said must not be lost to a shutdown. A change to the timers alone isn't written:
    /// a restart simply looks a little sooner, and an idle item doesn't rewrite its file every poll.
    /// </summary>
    private async Task<RemoteJobRecord?> UpdateAsync(Guid id, Func<RemoteJobRecord, RemoteJobRecord?> change, CancellationToken cancellationToken = default)
    {
        RemoteJobRecord? next;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoteJobRecord? current;
            lock (_sync) _records.TryGetValue(id, out current);
            if (current is null)
            {
                return null;
            }

            next = change(current);
            if (next is null)
            {
                return null;
            }

            var timersOnly = next with { NextCheckAt = current.NextCheckAt, UnchangedPolls = current.UnchangedPolls } == current;
            if (!timersOnly)
            {
                next = next with { UpdatedAt = _time.GetUtcNow() };
                await _store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_sync) _records[id] = next;
        }
        finally { _gate.Release(); }

        Raise(next);
        return next;
    }

    private async Task ChangePacingAsync(AccountWork work, Func<RemoteAccountPacing, RemoteAccountPacing> change)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var next = change(work.Pacing);
            if (next != work.Pacing)
            {
                await _store.SavePacingAsync(work.Entry.Id, next, CancellationToken.None).ConfigureAwait(false);
                work.Pacing = next;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task DeleteQuietlyAsync(Guid id)
    {
        try
        {
            await _store.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A later start tries again; it holds no secrets by now.
            _log?.Invoke($"Remote job {id:N}: could not delete its file: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Sends the job as it is now, not as it was when this change was made. Changes come from more than
    /// one thread and this runs after the gate, so an older snapshot could otherwise arrive last and
    /// stay on a client's screen, with no later event to correct it. Listeners only queue what they get.
    /// </summary>
    private void Raise(RemoteJobRecord record)
    {
        lock (_raising)
        {
            RemoteJobRecord? current;
            lock (_sync) _records.TryGetValue(record.Id, out current);
            if (current is null)
            {
                return;
            }

            try
            {
                Changed?.Invoke(this, ToView(current));
            }
            catch (Exception ex)
            {
                // A listener's trouble is not the job's: the change is already saved.
                _log?.Invoke($"Remote job {record.Id:N}: a listener failed: {ex.GetType().Name}.");
            }
        }
    }

    private void WakeLoop(string providerId)
    {
        if (_accounts.TryGetValue(providerId, out var work))
        {
            work.Signal();
        }
    }
}
