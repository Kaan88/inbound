using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    private static List<DownloadGroup> CloneGroups(IEnumerable<DownloadGroup> groups) => groups.Select(group =>
        new DownloadGroup { Id = group.Id, Name = group.Name, Key = group.Key, AutoNamed = group.AutoNamed,
            CompletedAt = group.CompletedAt, CompletionTracked = group.CompletionTracked, JobIds = new(group.JobIds) }).ToList();

    private static List<DownloadGroupDto> MapGroups(IEnumerable<DownloadGroup> groups) => groups.Select(group =>
        new DownloadGroupDto { Id = group.Id, Name = group.Name, Key = group.Key, JobIds = new(group.JobIds) }).ToList();

    /// <summary>
    /// A name for a batch from its members' file names: the split archive's name, else the
    /// names' common beginning, else the first file's name.
    /// </summary>
    internal static string DerivePackageName(IReadOnlyList<string> fileNames)
    {
        var archive = fileNames.Select(name => MultipartArchiveName.TryGetBaseName(name, out var archiveBase) ? archiveBase : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
        if (archive is not null) return Limit(archive);
        var stems = fileNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name)).Where(stem => stem.Length > 0).ToList();
        if (stems.Count == 0) return "Downloads";
        // The largest set of names that begin alike (data1, data2 beside a Setup.exe) names the package.
        var family = stems.GroupBy(stem => stem.Length >= 3 ? stem[..3].ToUpperInvariant() : stem.ToUpperInvariant())
            .OrderByDescending(group => group.Count()).First().ToList();
        var prefix = family[0];
        foreach (var stem in family.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < stem.Length &&
                   char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(stem[length])) length++;
            prefix = prefix[..length];
        }
        prefix = prefix.TrimEnd(' ', '.', '_', '-', '(', '[');
        return Limit(family.Count > 1 && prefix.Length >= 3 ? prefix : stems[0]);

        static string Limit(string name)
        {
            name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return name.Length == 0 ? "Downloads" : name.Length > 100 ? name[..100] : name;
        }
    }

    /// <summary>The package a job belongs to when it shows as a package (two or more members).</summary>
    public Guid? FindPackageOf(Guid jobId)
    {
        lock (_sync)
        {
            var group = _settings.DownloadGroups.FirstOrDefault(candidate => candidate.JobIds.Contains(jobId));
            return group is { JobIds.Count: > 1 } ? group.Id : null;
        }
    }

    /// <summary>The package a job is in, however few members it has so far. Null when it is in none.</summary>
    public Guid? FindGroupOf(Guid jobId)
    {
        lock (_sync) return _settings.DownloadGroups.FirstOrDefault(candidate => candidate.JobIds.Contains(jobId))?.Id;
    }

    /// <summary>A removal has stopped this job and is on its way to taking it off the list.</summary>
    public bool IsBeingRemoved(Guid jobId)
    {
        lock (_sync) return _jobs.TryGetValue(jobId, out var runtime) && runtime.CancelledForRemoval;
    }

    /// <summary>
    /// True when the package has at least one job and all of them have finished. An id with no job
    /// says nothing about completion, so it neither counts as finished nor holds the package back.
    /// </summary>
    public bool IsPackageComplete(Guid packageId)
    {
        lock (_sync)
        {
            var group = _settings.DownloadGroups.FirstOrDefault(candidate => candidate.Id == packageId);
            return group is not null && AllMembersCompletedLocked(group, out _);
        }
    }

    public List<DownloadGroupDto> GetDownloadGroups()
    {
        lock (_sync) return MapGroups(_settings.DownloadGroups);
    }

    private bool AllMembersCompletedLocked(DownloadGroup group, out DateTimeOffset lastCompletedAt)
    {
        lastCompletedAt = DateTimeOffset.MinValue;
        var any = false;
        foreach (var id in group.JobIds)
        {
            if (!_jobs.TryGetValue(id, out var record)) continue;
            if (record.Job.State != DownloadState.Completed) return false;
            any = true;
            if (record.Job.CompletedAt is { } at && at > lastCompletedAt) lastCompletedAt = at;
        }
        return any;
    }

    private static string? BatchKeyFor(string? packageKey)
        => string.IsNullOrWhiteSpace(packageKey) ? null : "batch:" + packageKey.Trim();

    /// <summary>Refuses a new file for a full package before a job exists, so nothing is left half added.</summary>
    private AddJobResponse? RefuseIfPackageFull(AddJobRequest request)
    {
        var key = BatchKeyFor(request.PackageKey);
        if (key is null) return null;
        var owner = request.OwnerExtensionId?.Trim();
        var requestId = request.ClientRequestId?.Trim();
        lock (_sync)
        {
            var group = _settings.DownloadGroups.FirstOrDefault(candidate => candidate.Key == key);
            if (group is null || group.JobIds.Count < MaxPackageMembers) return null;
            // A retry of a file the package already holds adds nothing.
            var isRetry = !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(requestId) && _jobs.Values.Any(runtime =>
                runtime.Job.OwnerExtensionId == owner && runtime.Job.ClientRequestId == requestId && group.JobIds.Contains(runtime.Job.Id));
            return isRetry ? null : new AddJobResponse
            {
                Error = $"This package already has {MaxPackageMembers} downloads, the most a package can hold.",
                ErrorCode = "package_full",
            };
        }
    }

    /// <summary>
    /// Puts an admitted job into a package: the batch named by the request, or the package of a
    /// split archive whose other parts are in the same folder. Runs under the settings gate, for
    /// repeats too, because a crash can land between saving the job and saving its package. A
    /// failed save never fails the add: the job is remembered and the next add saves it as well.
    /// </summary>
    private async Task AssignPackageAfterAddAsync(Guid jobId, AddJobRequest request, CancellationToken cancellationToken)
    {
        List<DownloadGroup>? next;
        var refusals = new List<string>();
        lock (_sync)
        {
            next = CloneGroups(_settings.DownloadGroups);
            var changed = StagePendingPackagesLocked(next, refusals);
            if (StagePackageLocked(next, jobId, request.PackageKey, request.PackageName, refusals))
            {
                _pendingPackages[jobId] = new PendingPackage(request.PackageKey, request.PackageName);
                _packageLastJoinedAt[next.First(group => group.JobIds.Contains(jobId)).Id] = _timeProvider.GetUtcNow();
                changed = true;
            }

            if (!changed)
            {
                _pendingPackages.Clear();
                next = null;
            }
        }

        foreach (var refusal in refusals) _log?.Invoke(refusal);
        if (next is not null && await TryCommitGroupsAsync(next, cancellationToken).ConfigureAwait(false) is null)
        {
            lock (_sync) _pendingPackages.Clear();
        }
    }

    private bool StagePendingPackagesLocked(List<DownloadGroup> next, List<string> refusals)
    {
        var changed = false;
        foreach (var (id, pending) in _pendingPackages)
        {
            changed |= StagePackageLocked(next, id, pending.Key, pending.Name, refusals);
        }
        return changed;
    }

    /// <summary>Adds the job to its package in <paramref name="next"/>; false when there is nothing to save.</summary>
    private bool StagePackageLocked(List<DownloadGroup> next, Guid jobId, string? packageKey, string? packageName, List<string> refusals)
    {
        if (!_jobs.TryGetValue(jobId, out var record) || next.Any(group => group.JobIds.Contains(jobId))) return false;
        var job = record.Job;
        var batchKey = BatchKeyFor(packageKey);
        var groupKey = batchKey
            ?? MultipartArchiveName.KeyFor(job.FileName, job.DestinationDirectory)
            ?? MultipartArchiveName.FirstPartKeyFor(job.FileName, job.DestinationDirectory);
        if (groupKey is null) return false;

        var existing = next.FirstOrDefault(group => group.Key == groupKey);
        if (existing is { JobIds.Count: >= MaxPackageMembers } || (existing is null && next.Count >= MaxPackages))
        {
            refusals.Add($"Job {jobId} was left out of its package: {(existing is null ? "too many packages" : "the package is full")}.");
            return false;
        }

        if (batchKey is not null)
        {
            if (existing is null)
            {
                var name = packageName?.Trim();
                var auto = string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl);
                if (auto) name = DerivePackageName([job.FileName]);
                // A batch package is created with its first job; it shows as a package from the second.
                next.Add(new DownloadGroup { Name = name!, Key = batchKey, AutoNamed = auto, CompletionTracked = true, JobIds = [jobId] });
                return true;
            }

            existing.JobIds.Add(jobId);
            // A Host-chosen name follows the file names the servers report as parts arrive.
            if (existing.AutoNamed)
                existing.Name = DerivePackageName(existing.JobIds.Where(_jobs.ContainsKey).Select(id => _jobs[id].Job.FileName).ToList());
            return true;
        }

        if (existing is not null)
        {
            existing.JobIds.Add(jobId);
            return true;
        }

        var grouped = next.SelectMany(group => group.JobIds).ToHashSet();
        var siblings = _jobs.Values.Select(candidate => candidate.Job)
            .Where(candidate => candidate.Id != jobId && !grouped.Contains(candidate.Id) &&
                (MultipartArchiveName.KeyFor(candidate.FileName, candidate.DestinationDirectory) ??
                 MultipartArchiveName.FirstPartKeyFor(candidate.FileName, candidate.DestinationDirectory)) == groupKey)
            .OrderBy(candidate => candidate.CreatedAt).Select(candidate => candidate.Id).ToList();
        // An unnumbered name.rar/name.zip only forms a package together with a numbered part.
        var hasNumbered = MultipartArchiveName.KeyFor(job.FileName, job.DestinationDirectory) is not null ||
            siblings.Any(id => MultipartArchiveName.KeyFor(_jobs[id].Job.FileName, _jobs[id].Job.DestinationDirectory) is not null);
        if (siblings.Count == 0 || !hasNumbered) return false;
        MultipartArchiveName.TryGetBaseName(job.FileName, out var baseName);
        if (string.IsNullOrEmpty(baseName)) baseName = Path.GetFileNameWithoutExtension(job.FileName);
        next.Add(new DownloadGroup { Name = baseName, Key = groupKey, CompletionTracked = true, JobIds = [.. siblings, jobId] });
        return true;
    }

    /// <summary>Saves the packages and swaps them in. Returns why not when the save fails; the log has it too.</summary>
    private async Task<string?> TryCommitGroupsAsync(List<DownloadGroup> next, CancellationToken cancellationToken)
    {
        AppSettings staged;
        lock (_sync) staged = _settings.WithDownloadGroups(next);
        try { await _settingsStore.SaveAsync(staged, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not save packages: {ex.Message}");
            return ex.Message;
        }

        lock (_sync) _settings.DownloadGroups = next;
        SettingsChanged?.Invoke(this, _settings);
        return null;
    }

    public async Task<GroupActionResult> GroupActionAsync(GroupActionRequest request, CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateHeld = true;
        try
        {
            var action = request.Action?.ToLowerInvariant() ?? string.Empty;
            List<DownloadGroup> next;
            List<JobDto> children;
            DownloadGroup? group;
            lock (_sync)
            {
                next = CloneGroups(_settings.DownloadGroups);
                group = next.FirstOrDefault(candidate => candidate.Id == request.GroupId);
                if (action != "create" && group is null) return Failure("Group not found.");
                children = group is null ? new() : group.JobIds.Where(_jobs.ContainsKey).Select(id => Map(_jobs[id].Job)).ToList();
                if (action is "create" or "add" or "detach")
                {
                    if (request.JobIds is null || request.JobIds.Count is < 1 or > 1000 ||
                        request.JobIds.Distinct().Count() != request.JobIds.Count || request.JobIds.Any(id => !_jobs.ContainsKey(id)))
                        return Failure("Select between 1 and 1000 existing downloads without duplicates. Refresh if a download was removed.");
                }
            }

            if (action is "pause" or "resume" or "cancel" or "queue" or "unqueue" or "priority")
            {
                if (action == "queue" && !QueueExists(request.QueueId)) return Failure("Queue not found.");
                if (action == "priority" && (request.Priority is not { } priority || !JobPriorities.IsValid(priority)))
                    return Failure("Choose a priority of High, Normal or Low.");
                IReadOnlyList<JobDto> targets = action switch
                {
                    "pause" => PackageRules.PauseTargets(children),
                    "resume" => PackageRules.ResumeTargets(group!.Id, children),
                    "cancel" => PackageRules.CancelTargets(children),
                    "priority" => PackageRules.PriorityTargets(children),
                    _ => children,
                };
                // Controls operate on this membership snapshot; settling a paused child must
                // not hold up unrelated additions, settings edits or group management.
                _settingsUpdateGate.Release();
                gateHeld = false;
                var failures = await ApplyToChildrenAsync(action, group!.Id, targets, request, cancellationToken).ConfigureAwait(false);
                return new GroupActionResult { Ok = failures.Count == 0, GroupId = group.Id, FailedJobs = failures,
                    Error = failures.Count == 0 ? null : "Some child downloads could not be changed. Other children were processed.",
                    Groups = MapGroups(next), Changed = targets.Count - failures.Count,
                    LeftCancelled = action != "resume" ? 0 :
                        children.Count(child => child.State == "cancelled") - targets.Count(child => child.State == "cancelled") };
            }

            if (action is "create" or "rename")
            {
                var name = request.Name?.Trim() ?? string.Empty;
                if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) return Failure("Enter a group name of 1–100 characters.");
                if (action == "create")
                {
                    if (next.Count >= MaxPackages) return Failure($"At most {MaxPackages} groups are supported.");
                    group = new DownloadGroup { Name = name, CompletionTracked = true };
                    next.Add(group);
                }
                else { group!.Name = name; group.AutoNamed = false; }
            }
            switch (action)
            {
                case "create":
                case "add":
                    var selected = request.JobIds!.ToHashSet();
                    foreach (var candidate in next) candidate.JobIds.RemoveAll(selected.Contains);
                    group!.JobIds.AddRange(request.JobIds!);
                    if (group.JobIds.Count > MaxPackageMembers) return Failure($"A group can contain at most {MaxPackageMembers} downloads.");
                    // Grouping finished files by hand is not a package finishing.
                    lock (_sync)
                    {
                        if (AllMembersCompletedLocked(group, out var finishedAt) && (group.CompletedAt is not { } latched || finishedAt > latched))
                            group.CompletedAt = finishedAt;
                    }
                    break;
                case "detach":
                    var detached = request.JobIds!.ToHashSet();
                    if (detached.Any(id => !group!.JobIds.Contains(id))) return Failure("Group membership changed. Refresh and try again.");
                    group!.JobIds.RemoveAll(detached.Contains);
                    if (group.JobIds.Count < 2) next.Remove(group); // a package of one is just a download
                    break;
                case "delete":
                    next.Remove(group!); // Ungroup only: independent jobs and files are retained.
                    break;
                case "rename": break;
                default: return Failure("Unknown group action.");
            }

            // The definition and every membership change are committed in one atomic settings write.
            // Options cannot overwrite this Host-owned collection from an old snapshot.
            if (await TryCommitGroupsAsync(next, cancellationToken).ConfigureAwait(false) is { } saveError)
                return Failure("Group changes could not be saved. " + saveError);
            // Losing its last unfinished file can leave a package finished.
            if (action is "create" or "add" or "detach") RequestPackageCheck();
            return new GroupActionResult { Ok = true, GroupId = group!.Id, Groups = MapGroups(next) };
        }
        finally { if (gateHeld) _settingsUpdateGate.Release(); }

        GroupActionResult Failure(string error)
        {
            lock (_sync) return new GroupActionResult { Error = error, GroupId = request.GroupId, Groups = MapGroups(_settings.DownloadGroups) };
        }

        bool QueueExists(int? queueId)
        {
            lock (_sync) return queueId is { } id && _settings.Queues.Any(queue => queue.Id == id);
        }
    }

    private async Task<Dictionary<Guid, string>> ApplyToChildrenAsync(
        string action,
        Guid packageId,
        IReadOnlyList<JobDto> targets,
        GroupActionRequest request,
        CancellationToken cancellationToken)
    {
        // Stopping a file frees a slot, and the scheduler would otherwise start the next file only to stop it.
        var holdStarts = action is "pause" or "cancel";
        if (holdStarts) SetAdmissionSuspended(targets, true);
        var failures = new Dictionary<Guid, string>();
        try
        {
            foreach (var child in targets)
            {
                try
                {
                    var result = action switch
                    {
                        "pause" => await PauseAsync(child.Id, cancellationToken).ConfigureAwait(false),
                        "resume" => await ResumeAsync(child.Id, cancellationToken).ConfigureAwait(false),
                        // Partial data stays, which is what lets a Resume undo this.
                        "cancel" => await CancelAsync(child.Id, deleteFile: false, cancellationToken: cancellationToken,
                            onlyIfUnfinished: true, cancelledByPackage: packageId).ConfigureAwait(false),
                        "queue" => await MoveToQueueAsync(child.Id, request.QueueId!.Value, cancellationToken).ConfigureAwait(false),
                        "unqueue" => await DetachFromQueueAsync(child.Id, cancellationToken).ConfigureAwait(false),
                        _ => await UpdateJobAsync(new JobUpdateRequest { JobId = child.Id, Priority = request.Priority }, cancellationToken).ConfigureAwait(false),
                    };
                    if (!result.Ok) failures[child.Id] = result.Error ?? "Action rejected.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { failures[child.Id] = ex.Message; }
            }
        }
        finally
        {
            if (holdStarts)
            {
                SetAdmissionSuspended(targets, false);
                Schedule();
            }
        }
        return failures;
    }

    private void SetAdmissionSuspended(IEnumerable<JobDto> jobs, bool suspended)
    {
        lock (_sync)
        {
            foreach (var job in jobs)
            {
                if (_jobs.TryGetValue(job.Id, out var runtime)) runtime.AdmissionSuspended = suspended;
            }
        }
    }
}
