using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    private static List<DownloadGroup> CloneGroups(IEnumerable<DownloadGroup> groups) => groups.Select(group =>
        new DownloadGroup { Id = group.Id, Name = group.Name, Key = group.Key, JobIds = new(group.JobIds) }).ToList();

    private static List<DownloadGroupDto> MapGroups(IEnumerable<DownloadGroup> groups) => groups.Select(group =>
        new DownloadGroupDto { Id = group.Id, Name = group.Name, Key = group.Key, JobIds = new(group.JobIds) }).ToList();

    /// <summary>
    /// Puts a newly admitted job into a package: the batch named by the request, or the package of
    /// a split archive whose other parts are in the same folder. Runs under the settings gate; a
    /// failed save leaves the job ungrouped rather than failing the add.
    /// </summary>
    private async Task AssignPackageAfterAddAsync(Guid jobId, AddJobRequest request, CancellationToken cancellationToken)
    {
        List<DownloadGroup> next;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var record)) return;
            var job = record.Job;
            next = CloneGroups(_settings.DownloadGroups);
            if (next.Any(group => group.JobIds.Contains(jobId))) return;

            var batchKey = string.IsNullOrWhiteSpace(request.PackageKey) ? null : "batch:" + request.PackageKey.Trim();
            if (batchKey is not null)
            {
                var batch = next.FirstOrDefault(group => group.Key == batchKey);
                if (batch is null)
                {
                    var name = request.PackageName?.Trim();
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
                        name = MultipartArchiveName.TryGetBaseName(job.FileName, out var archive) ? archive : "Downloads";
                    // A batch package is created with its first job; it shows as a package from the second.
                    next.Add(new DownloadGroup { Name = name, Key = batchKey, JobIds = [jobId] });
                }
                else batch.JobIds.Add(jobId);
            }
            else
            {
                var key = MultipartArchiveName.KeyFor(job.FileName, job.DestinationDirectory)
                          ?? MultipartArchiveName.FirstPartKeyFor(job.FileName, job.DestinationDirectory);
                if (key is null) return;
                var existing = next.FirstOrDefault(group => group.Key == key);
                if (existing is not null) existing.JobIds.Add(jobId);
                else
                {
                    var grouped = next.SelectMany(group => group.JobIds).ToHashSet();
                    var siblings = _jobs.Values.Select(candidate => candidate.Job)
                        .Where(candidate => candidate.Id != jobId && !grouped.Contains(candidate.Id) &&
                            (MultipartArchiveName.KeyFor(candidate.FileName, candidate.DestinationDirectory) ??
                             MultipartArchiveName.FirstPartKeyFor(candidate.FileName, candidate.DestinationDirectory)) == key)
                        .OrderBy(candidate => candidate.CreatedAt).Select(candidate => candidate.Id).ToList();
                    // An unnumbered name.rar/name.zip only forms a package together with a numbered part.
                    var hasNumbered = MultipartArchiveName.KeyFor(job.FileName, job.DestinationDirectory) is not null ||
                        siblings.Any(id => MultipartArchiveName.KeyFor(_jobs[id].Job.FileName, _jobs[id].Job.DestinationDirectory) is not null);
                    if (siblings.Count == 0 || !hasNumbered) return;
                    MultipartArchiveName.TryGetBaseName(job.FileName, out var baseName);
                    if (string.IsNullOrEmpty(baseName)) baseName = Path.GetFileNameWithoutExtension(job.FileName);
                    next.Add(new DownloadGroup { Name = baseName, Key = key, JobIds = [.. siblings, jobId] });
                }
            }
            if (next.Count > 1000) return;
        }

        AppSettings staged;
        lock (_sync) staged = _settings.WithDownloadGroups(next);
        try { await _settingsStore.SaveAsync(staged, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        lock (_sync) _settings.DownloadGroups = next;
        SettingsChanged?.Invoke(this, _settings);
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

            if (action is "pause" or "resume" or "cancel")
            {
                // Controls operate on this membership snapshot; settling a paused child must
                // not hold up unrelated additions, settings edits or group management.
                _settingsUpdateGate.Release();
                gateHeld = false;
                var failures = new Dictionary<Guid, string>();
                foreach (var child in children)
                {
                    if (action == "pause" && !child.CanPause || action == "resume" && !child.CanResume ||
                        action == "cancel" && (child.IsTerminal || child.State == "cancelled")) continue;
                    try
                    {
                        var result = action switch
                        {
                            "pause" => await PauseAsync(child.Id, cancellationToken).ConfigureAwait(false),
                            "resume" => await ResumeAsync(child.Id, cancellationToken).ConfigureAwait(false),
                            _ => await CancelAsync(child.Id, deleteFile: false, cancellationToken: cancellationToken,
                                onlyIfUnfinished: true).ConfigureAwait(false),
                        };
                        if (!result.Ok) failures[child.Id] = result.Error ?? "Action rejected.";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { failures[child.Id] = ex.Message; }
                }
                return new GroupActionResult { Ok = failures.Count == 0, GroupId = group!.Id, FailedJobs = failures,
                    Error = failures.Count == 0 ? null : "Some child downloads could not be changed. Other children were processed.",
                    Groups = MapGroups(next) };
            }

            if (action is "create" or "rename")
            {
                var name = request.Name?.Trim() ?? string.Empty;
                if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) return Failure("Enter a group name of 1–100 characters.");
                if (action == "create")
                {
                    if (next.Count >= 1000) return Failure("At most 1000 groups are supported.");
                    group = new DownloadGroup { Name = name };
                    next.Add(group);
                }
                else group!.Name = name;
            }
            switch (action)
            {
                case "create":
                case "add":
                    var selected = request.JobIds!.ToHashSet();
                    foreach (var candidate in next) candidate.JobIds.RemoveAll(selected.Contains);
                    group!.JobIds.AddRange(request.JobIds!);
                    if (group.JobIds.Count > 1000) return Failure("A group can contain at most 1000 downloads.");
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
            AppSettings staged;
            lock (_sync) staged = _settings.WithDownloadGroups(next);
            try { await _settingsStore.SaveAsync(staged, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Failure("Group changes could not be saved. " + ex.Message); }
            lock (_sync) _settings.DownloadGroups = next;
            SettingsChanged?.Invoke(this, _settings);
            return new GroupActionResult { Ok = true, GroupId = group!.Id, Groups = MapGroups(next) };
        }
        finally { if (gateHeld) _settingsUpdateGate.Release(); }

        GroupActionResult Failure(string error)
        {
            lock (_sync) return new GroupActionResult { Error = error, GroupId = request.GroupId, Groups = MapGroups(_settings.DownloadGroups) };
        }
    }
}
