using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime;

/// <summary>One file a provider holds that should become a local download.</summary>
public sealed record RemoteJobRequest
{
    public required RemoteSource Source { get; init; }
    public required string FileName { get; init; }
    /// <summary>The folder the package goes in. <see cref="RemoteSource.RelativePath"/> decides where under it.</summary>
    public required string DestinationRoot { get; init; }
    public required int MaxConnections { get; init; }
    public long? ExpectedTotalBytes { get; init; }
    public string? ExpectedMd5 { get; init; }
    public int QueueId { get; init; }
    public string StartMode { get; init; } = "startNow";
}

public sealed record RemoteJobsResult
{
    /// <summary>This one file's path can't be made under a folder that is otherwise fine. Worth skipping, not retrying.</summary>
    public const string UnusablePathCode = "unusable_path";

    /// <summary>One per request, in order. A retried file comes back as a duplicate of its job.</summary>
    public IReadOnlyList<AddJobResponse> Items { get; init; } = [];
    public Guid? PackageId { get; init; }
    /// <summary>Set when the package could not be saved. The jobs exist; adding the same batch again repairs it.</summary>
    public string? Error { get; init; }
    /// <summary>What the user should hear about existing jobs in this batch.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed partial class DownloadManagerService : IRemoteJobHost
{
    private const string RemoteOwnerPrefix = "provider:";
    private const string RemotePackagePrefix = "remote:";
    private const int MaxLinkRefreshesPerRun = 2;
    private const int MaxPackageMembers = 1000;

    private sealed record RemoteAddContext(RemoteSource Source, long? ExpectedTotalBytes, string? ExpectedMd5, int MaxConnections);

    /// <summary>Where a provider's files go when the user named no folder.</summary>
    public string DefaultDownloadDirectory
    {
        get
        {
            lock (_sync) return _settings.DefaultDownloadDirectory;
        }
    }

    /// <summary>
    /// Creates local jobs for a provider's files as one batch: one package, one settings commit.
    /// In-process only, so no client can forge a job that spends API calls. Safe to repeat: each
    /// file is claimed by its remote job and file id, and duplicates still get their package.
    /// </summary>
    public async Task<RemoteJobsResult> AddRemoteJobsAsync(
        IReadOnlyList<RemoteJobRequest> requests,
        string packageKey,
        string? packageName = null,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count > MaxPackageMembers)
        {
            return new RemoteJobsResult { Error = $"A package can contain at most {MaxPackageMembers} downloads." };
        }

        if (string.IsNullOrWhiteSpace(packageKey))
        {
            return new RemoteJobsResult { Error = "A package key is required." };
        }

        var notes = new List<string>();
        var revived = new List<Guid>();
        RemoteJobsResult result;
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (RefuseOversizedBatch(requests, packageKey) is { } refused)
            {
                return new RemoteJobsResult { Error = refused };
            }

            var items = new List<AddJobResponse>(requests.Count);
            foreach (var request in requests)
            {
                var added = await AddRemoteJobAsync(request, cancellationToken).ConfigureAwait(false);
                items.Add(added.Duplicate && added.Job is { } job
                    ? await RepointIfMovedAsync(job.Id, request, added, notes, revived, cancellationToken).ConfigureAwait(false)
                    : added);
            }

            var jobIds = items.Where(item => item is { Accepted: true, Job: not null }).Select(item => item.Job!.Id).ToList();
            var (packageId, error) = await AssignBatchToPackageAsync(jobIds, packageKey, packageName, cancellationToken)
                .ConfigureAwait(false);
            // A small file can finish before its package is saved, which no job event then reports.
            RequestPackageCheck();
            result = new RemoteJobsResult { Items = items, PackageId = packageId, Error = error, Notes = notes };
        }
        finally { _settingsUpdateGate.Release(); }

        // Stopped only because their item had gone, and it is back under a new id. The rest are the user's to resume.
        foreach (var id in revived)
        {
            await ResumeAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// A job is claimed by its remote job and file id, which stay the same when the provider's item
    /// changes: a retry can resubmit something gone, and a new item takes its place. The job must
    /// follow, or it would go on asking for an item that no longer exists. Its bytes stay: the next
    /// run compares size and md5 with the file on the new item, and starts over only if they differ. A
    /// finished file that is no longer what the item holds stays finished, and the user hears of it.
    /// </summary>
    private async Task<AddJobResponse> RepointIfMovedAsync(
        Guid jobId,
        RemoteJobRequest request,
        AddJobResponse added,
        List<string> notes,
        List<Guid> revived,
        CancellationToken cancellationToken)
    {
        JobRuntime? runtime;
        RemoteSource previous;
        string previousUrl;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.RemoteSource is not { } current ||
                (current.ItemId == request.Source.ItemId && current.Kind == request.Source.Kind))
            {
                return added;
            }

            var job = runtime.Job;
            var sameFile =
                (job.ExpectedTotalBytes is null || request.ExpectedTotalBytes is null || job.ExpectedTotalBytes == request.ExpectedTotalBytes) &&
                (string.IsNullOrWhiteSpace(job.ExpectedMd5) || string.IsNullOrWhiteSpace(request.ExpectedMd5) ||
                 job.ExpectedMd5.Equals(request.ExpectedMd5, StringComparison.OrdinalIgnoreCase));
            if (job.State == DownloadState.Completed && !sameFile)
            {
                notes.Add($"{RemoteText.Clean(job.FileName, 200)} is finished, but the provider now holds a different file under this name. Yours was kept.");
                return added;
            }

            previous = current;
            previousUrl = job.Url;
            job.RemoteSource = request.Source;
            job.Url = request.Source.ToReferenceUrl();
            job.Touch();
            if (job is { State: DownloadState.Failed, Failure.Kind: FailureKind.NotFound })
            {
                revived.Add(jobId);
            }
        }

        try
        {
            await PersistAsync(runtime, cancellationToken, requireSuccess: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing counts until it is saved. The job's next look is still at the old item, and repeating this batch repeats this.
            lock (_sync)
            {
                runtime.Job.RemoteSource = previous;
                runtime.Job.Url = previousUrl;
            }

            revived.Remove(jobId);
            return new AddJobResponse
            {
                Accepted = false,
                Error = "Could not save a download's new reference.",
                ErrorCode = "persistence_failed",
            };
        }

        Raise(runtime, "updated");
        return added;
    }

    /// <summary>The package's local jobs right now, for the words on its remote job.</summary>
    public RemoteLocalSummary? GetLocalSummary(Guid packageId)
    {
        lock (_sync)
        {
            var group = _settings.DownloadGroups.FirstOrDefault(candidate => candidate.Id == packageId);
            if (group is null)
            {
                return null;
            }

            int total = 0, completed = 0, failed = 0, paused = 0, active = 0;
            foreach (var id in group.JobIds)
            {
                if (!_jobs.TryGetValue(id, out var runtime))
                {
                    continue;
                }

                total++;
                switch (runtime.Job.State)
                {
                    case DownloadState.Completed: completed++; break;
                    case DownloadState.Failed: failed++; break;
                    case DownloadState.Paused or DownloadState.Cancelled: paused++; break;
                    case DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting or DownloadState.Verifying: active++; break;
                }
            }

            return new RemoteLocalSummary(total, completed, failed, paused, active);
        }
    }

    /// <summary>
    /// Refuses a batch the package can't hold before any job exists. Spilling into a second package
    /// would be worse: one provider item would then have two sets of parent controls and report
    /// finished once per part, while a clear refusal leaves nothing behind and is safe to repeat.
    /// </summary>
    private string? RefuseOversizedBatch(IReadOnlyList<RemoteJobRequest> requests, string packageKey)
    {
        var key = BatchKeyFor(packageKey);
        lock (_sync)
        {
            var group = _settings.DownloadGroups.FirstOrDefault(candidate => candidate.Key == key);
            if (group is null)
            {
                return _settings.DownloadGroups.Count >= MaxPackages
                    ? $"At most {MaxPackages} packages are supported. Remove some and try again."
                    : null;
            }

            var members = group.JobIds.ToHashSet();
            var held = _jobs.Values.Where(runtime => members.Contains(runtime.Job.Id))
                .Select(runtime => (runtime.Job.OwnerExtensionId, runtime.Job.ClientRequestId))
                .ToHashSet();
            var incoming = requests.Select(request => (
                    (string?)(RemoteOwnerPrefix + request.Source.Provider),
                    (string?)$"{request.Source.RemoteJobId}/{request.Source.FileId}"))
                .Distinct()
                .Count(id => !held.Contains(id));
            return group.JobIds.Count + incoming > MaxPackageMembers
                ? $"A package can contain at most {MaxPackageMembers} downloads."
                : null;
        }
    }

    private async Task<AddJobResponse> AddRemoteJobAsync(RemoteJobRequest request, CancellationToken cancellationToken)
    {
        var source = request.Source;
        if (!IsReferenceWord(source.Provider) || !IsReferenceWord(source.Kind) ||
            string.IsNullOrWhiteSpace(source.ItemId) || string.IsNullOrWhiteSpace(source.FileId) ||
            string.IsNullOrWhiteSpace(source.RemoteJobId))
        {
            return new AddJobResponse { Error = "The provider's file reference is incomplete.", ErrorCode = "invalid_reference" };
        }

        if (!TryResolveRemoteDestination(request, out var destination))
        {
            return new AddJobResponse { Error = "The file's path leaves the download folder.", ErrorCode = "invalid_path" };
        }

        var added = await AddJobCoreAsync(new AddJobRequest
        {
            Url = source.ToReferenceUrl(),
            FileName = request.FileName,
            DestinationDirectory = destination,
            RememberDestination = false,
            Source = nameof(DownloadSource.Remote),
            StartMode = request.StartMode,
            QueueId = request.QueueId,
            OwnerExtensionId = RemoteOwnerPrefix + source.Provider,
            ClientRequestId = $"{source.RemoteJobId}/{source.FileId}",
        }, cancellationToken, new RemoteAddContext(source, request.ExpectedTotalBytes, request.ExpectedMd5, request.MaxConnections))
            .ConfigureAwait(false);

        if (added.ErrorCode == "invalid_destination" && FolderCanBeUsed(request.DestinationRoot))
        {
            added.ErrorCode = RemoteJobsResult.UnusablePathCode;
        }

        return added;
    }

    /// <summary>A folder that can't be used is worth waiting for, such as a drive plugged in later. One path under a good folder isn't.</summary>
    private static bool FolderCanBeUsed(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsReferenceWord(string? value)
        => !string.IsNullOrEmpty(value) &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The provider's path is untrusted, so the result is checked as a real path, not as text.</summary>
    private static bool TryResolveRemoteDestination(RemoteJobRequest request, out string destination)
    {
        destination = string.Empty;
        if (!Path.IsPathFullyQualified(request.DestinationRoot))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.DestinationRoot));
            var folder = root;
            if (request.Source.RelativePath is { } relativePath)
            {
                var safe = RemotePaths.ToSafeRelativePath(relativePath);
                if (safe is null)
                {
                    return false;
                }

                folder = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(safe) ?? string.Empty));
            }

            var final = Path.GetFullPath(Path.Combine(folder, FileNameResolver.Sanitize(request.FileName)));
            // A drive root keeps its separator when trimmed.
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!final.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            destination = Path.GetDirectoryName(final)!;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Puts jobs in the package under this key in one save, leaving out any already in a package.</summary>
    private async Task<(Guid? PackageId, string? Error)> AssignBatchToPackageAsync(
        IReadOnlyList<Guid> jobIds,
        string packageKey,
        string? packageName,
        CancellationToken cancellationToken)
    {
        var key = BatchKeyFor(packageKey);
        List<DownloadGroup> next;
        DownloadGroup? group;
        var refusals = new List<string>();
        lock (_sync)
        {
            next = CloneGroups(_settings.DownloadGroups);
            var changed = StagePendingPackagesLocked(next, refusals);
            group = next.FirstOrDefault(candidate => candidate.Key == key);
            var grouped = next.SelectMany(candidate => candidate.JobIds).ToHashSet();
            var missing = jobIds.Where(id => _jobs.ContainsKey(id) && !grouped.Contains(id)).Distinct().ToList();
            if (missing.Count == 0 && !changed)
            {
                _pendingPackages.Clear();
                return (group?.Id, null);
            }

            if ((group?.JobIds.Count ?? 0) + missing.Count > MaxPackageMembers)
            {
                return (group?.Id, $"A package can contain at most {MaxPackageMembers} downloads.");
            }

            if (group is null && missing.Count > 0)
            {
                if (next.Count >= MaxPackages)
                {
                    return (null, $"At most {MaxPackages} packages are supported.");
                }

                var name = packageName?.Trim();
                var auto = string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl);
                group = new DownloadGroup { Name = name!, Key = key, AutoNamed = auto, CompletionTracked = true, JobIds = [] };
                next.Add(group);
            }

            if (group is not null && missing.Count > 0)
            {
                group.JobIds.AddRange(missing);
                if (group.AutoNamed)
                {
                    group.Name = DerivePackageName(group.JobIds.Where(_jobs.ContainsKey).Select(id => _jobs[id].Job.FileName).ToList());
                }
            }

            foreach (var id in missing)
            {
                _pendingPackages[id] = new PendingPackage(packageKey, packageName);
            }
        }

        foreach (var refusal in refusals) _log?.Invoke(refusal);
        if (await TryCommitGroupsAsync(next, cancellationToken).ConfigureAwait(false) is { } saveError)
        {
            return (null, "The package could not be saved. " + saveError);
        }

        lock (_sync) _pendingPackages.Clear();
        return (group?.Id, null);
    }

    /// <summary>On a provider's own host with a token, or anywhere at all with a stored account key in it.</summary>
    private bool HoldsProviderSecret(string? url)
        => ProviderLinks.IsProviderLink(url) || _linkSecrets?.Holds(url) == true;

    private string MaskProviderSecret(string url)
        => ProviderLinks.MaskToken(_linkSecrets?.Mask(url) ?? url);

    /// <summary>
    /// A pasted provider link carries the account key, so the job keeps a masked copy and the
    /// real address goes to the field the store encrypts.
    /// </summary>
    private (string Url, string? FinalUrl, string? ProtectedUrl) SplitOffProviderLink(string url, string? finalUrl)
    {
        var urlIsLink = HoldsProviderSecret(url);
        var finalIsLink = HoldsProviderSecret(finalUrl);
        if (!urlIsLink && !finalIsLink)
        {
            return (url, finalUrl, null);
        }

        return (urlIsLink ? MaskProviderSecret(url) : url, null, finalIsLink ? finalUrl : url);
    }

    private string? MaskIfProviderLink(string? url)
        => HoldsProviderSecret(url) ? MaskProviderSecret(url!) : url;

    /// <summary>
    /// Takes the account key out of a job saved before its address was recognised as a provider
    /// link, wherever the job and its checkpoint kept it. Every later save would otherwise write
    /// the key out again, and every client would be sent it.
    /// </summary>
    private async Task<bool> ProtectStoredProviderLinksAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var changed = ProtectJobLinks(job);
        await ProtectCheckpointLinksAsync(job, cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private bool ProtectJobLinks(DownloadJob job)
    {
        var changed = false;
        if (HoldsProviderSecret(job.Url) || HoldsProviderSecret(job.FinalUrl))
        {
            var (url, finalUrl, protectedUrl) = SplitOffProviderLink(job.Url, job.FinalUrl);
            job.Url = url;
            job.FinalUrl = finalUrl;
            job.ProtectedUrl ??= protectedUrl;
            changed = true;
        }

        if (HoldsProviderSecret(job.PageUrl))
        {
            job.PageUrl = MaskIfProviderLink(job.PageUrl);
            changed = true;
        }

        if (HoldsProviderSecret(job.Referrer))
        {
            job.Referrer = MaskIfProviderLink(job.Referrer);
            changed = true;
        }

        return changed;
    }

    private async Task ProtectCheckpointLinksAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpointStore.LoadAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (checkpoint is not null && (HoldsProviderSecret(checkpoint.Url) || HoldsProviderSecret(checkpoint.FinalUrl)))
        {
            checkpoint.Url = job.Url;
            checkpoint.FinalUrl = job.FinalUrl;
            try { await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Could not rewrite the checkpoint of job {job.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// For when an account key has just been stored: addresses saved earlier may turn out to hold
    /// it. A startup does the same for jobs through reconciling them.
    /// </summary>
    public async Task ProtectStoredSecretsAsync(CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<JobRuntime> runtimes;
            lock (_sync) runtimes = _jobs.Values.ToList();
            foreach (var runtime in runtimes)
            {
                bool running;
                bool changed;
                lock (_sync)
                {
                    running = runtime.Starting || runtime.Runner is { IsCompleted: false };
                    changed = ProtectJobLinks(runtime.Job);
                    // The transfer goes on with the address it had, for as long as this run lasts.
                    if (changed && running) runtime.Job.ResolvedUrl ??= runtime.Job.ProtectedUrl;
                }

                // A running engine writes its own checkpoints, and takes the masked address from the job.
                if (!running) await ProtectCheckpointLinksAsync(runtime.Job, cancellationToken).ConfigureAwait(false);
                if (changed)
                {
                    await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
                    Raise(runtime, "updated");
                }
            }

            await ScrubRecentUrlsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task ScrubRecentUrlsAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_settings.RecentUrls is null || _settings.RecentUrls.RemoveAll(HoldsProviderSecret) == 0)
            {
                return;
            }
        }

        try
        {
            await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            SettingsChanged?.Invoke(this, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not remove a recent address that holds an account key: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the engine, getting the job's real address first. A provider's links die with time,
    /// and the engine's own retries can't help with that, so a dead link earns a limited number
    /// of fresh tries here. Checkpoints stay, so a new link continues where the last one stopped.
    /// </summary>
    private async Task<DownloadResult> ExecuteWithLinksAsync(
        JobRuntime runtime,
        IDownloadEngine engine,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var job = runtime.Job;
        for (var refreshes = 0; ; refreshes++)
        {
            var linkFailure = await PrepareRunLinkAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (linkFailure is not null)
            {
                return DownloadResult.Fail(DownloadState.Failed, linkFailure, job.ReceivedBytes, job.TotalBytes);
            }

            var checkpoint = await _checkpointStore.LoadAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            var result = await engine.ExecuteAsync(job, checkpoint, progress, cancellationToken).ConfigureAwait(false);
            if (result.Success || result.Failure is not { } failure ||
                job.RemoteSource is null || _linkBroker is null ||
                failure.Kind is not (FailureKind.ExpiredUrl or FailureKind.Unauthorized or FailureKind.Forbidden or FailureKind.NotFound))
            {
                return result;
            }

            if (refreshes >= MaxLinkRefreshesPerRun)
            {
                // The engine's own wording points at Refresh Download Address, which these jobs don't have.
                var status = failure.HttpStatus is { } code ? $" (HTTP {code})" : string.Empty;
                return result with
                {
                    Failure = failure with
                    {
                        Message = $"Fresh download links keep failing{status}. Anything already downloaded was kept, so try again later.",
                        IsResumable = true,
                    },
                };
            }

            if (!await ShouldRefreshLinkAsync(job, failure, cancellationToken).ConfigureAwait(false))
            {
                return result;
            }
        }
    }

    private async Task<bool> ShouldRefreshLinkAsync(DownloadJob job, JobFailure failure, CancellationToken cancellationToken)
    {
        DownloadJob snapshot;
        lock (_sync) snapshot = job.Clone();
        try
        {
            return await _linkBroker!.ShouldRefreshAfterFailureAsync(snapshot, failure, cancellationToken).ConfigureAwait(false);
        }
        // An API timeout also surfaces as a cancellation; only the run's own cancel is one.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The transfer's own failure is the one worth showing.
            _log?.Invoke($"Job {job.Id}: asking for a fresh link failed: {UrlRedactor.RedactText(ex.Message)}");
            return false;
        }
    }

    /// <summary>Sets the address for this run, in memory only. Returns why there is none, if so.</summary>
    private async Task<JobFailure?> PrepareRunLinkAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        var job = runtime.Job;
        if (job.RemoteSource is { } source)
        {
            return await PrepareRemoteLinkAsync(runtime, source, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(job.ProtectedUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(job.ProtectedUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return new JobFailure(FailureKind.Unknown,
                "The saved address for this download could not be read on this Windows account. " +
                "Use Refresh Download Address with the original link.",
                false, true, DateTimeOffset.UtcNow);
        }

        lock (_sync)
        {
            job.ResolvedUrl = job.ProtectedUrl;
            ClampConnectionsLocked(job, uri, ProviderLinks.MaxConnections);
        }

        return null;
    }

    private async Task<JobFailure?> PrepareRemoteLinkAsync(JobRuntime runtime, RemoteSource source, CancellationToken cancellationToken)
    {
        var job = runtime.Job;
        if (_linkBroker is null)
        {
            return new JobFailure(FailureKind.Unknown,
                $"This download comes from {source.Provider}, and that account is not available in this app.",
                false, true, DateTimeOffset.UtcNow);
        }

        DownloadJob snapshot;
        lock (_sync) snapshot = job.Clone();
        RemoteLinkResolution resolution;
        try
        {
            resolution = await _linkBroker.PrepareRunAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        // An API timeout also surfaces as a cancellation; only the run's own cancel is one.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = ex is OperationCanceledException ? "The request timed out." : UrlRedactor.RedactText(ex.Message);
            _log?.Invoke($"Job {job.Id}: preparing a link failed: {reason}");
            return new JobFailure(FailureKind.Unknown, $"Could not get a download link from {source.Provider}. {reason}",
                false, true, DateTimeOffset.UtcNow);
        }

        if (resolution.ResetLocalData)
        {
            await DeletePayloadAsync(job, includeFinal: false, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                job.ReceivedBytes = 0;
                job.ETag = null;
                job.LastModified = null;
                job.ContentType = null;
                job.ResumeSupported = null;
                job.ExpectedTotalBytes = resolution.ExpectedTotalBytes;
                job.ExpectedMd5 = resolution.ExpectedMd5;
                job.TotalBytes = resolution.ExpectedTotalBytes;
                job.Touch();
            }

            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            Raise(runtime, "updated");
        }

        if (resolution.Failure is { } failure)
        {
            // Saved in plain job JSON and sent to every client, and a provider can quote what was submitted.
            return failure with { Message = RemoteText.Clean(failure.Message, 1000) ?? "The provider gave no reason." };
        }

        if (!Uri.TryCreate(resolution.Url, UriKind.Absolute, out var uri))
        {
            return new JobFailure(FailureKind.Unknown, $"{source.Provider} did not supply a usable download link.",
                false, true, DateTimeOffset.UtcNow);
        }

        lock (_sync)
        {
            job.ExpectedTotalBytes = resolution.ExpectedTotalBytes ?? job.ExpectedTotalBytes;
            job.ExpectedMd5 = resolution.ExpectedMd5 ?? job.ExpectedMd5;
            job.ResolvedUrl = resolution.Url;
            ClampConnectionsLocked(job, uri, resolution.MaxConnections ?? int.MaxValue);
        }

        return null;
    }

    /// <summary>Limits can drop after a job was saved, and only the run knows the real host.</summary>
    private void ClampConnectionsLocked(DownloadJob job, Uri address, int cap)
        => job.RequestedConnections = Math.Clamp(
            Math.Min(Math.Min(job.RequestedConnections, EffectiveConnectionLimit(address)), cap), 1, 32);
}
