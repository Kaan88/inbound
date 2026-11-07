using System.Text.Json;
using DownloadManager.Archives;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Persistence;

namespace DownloadManager.Runtime.Archives;

public sealed partial class ArchiveCoordinator
{
    private const int MaxNamesInText = 5;
    private const string UnreadableRecord = "The saved state of this archive couldn't be read when the app started, so it is left alone for now. Restart the app to try again.";

    public List<ArchiveSetDto> List()
    {
        lock (_sync)
        {
            return _sets.Values.Where(set => !set.Removed).OrderByDescending(set => set.CreatedAt).Select(Map).ToList();
        }
    }

    public ArchiveSetDto? Find(Guid id)
    {
        lock (_sync)
        {
            return _sets.TryGetValue(id, out var set) && !set.Removed ? Map(set) : null;
        }
    }

    public ArchiveSettingsResult GetSettings()
    {
        ArchiveSettings settings;
        lock (_sync) settings = _settings;
        return new ArchiveSettingsResult { Ok = true, Settings = MapSettings(settings), Tool = ToolStatus(settings) };
    }

    public async Task<ArchiveSettingsResult> UpdateSettingsAsync(ArchiveSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        ArchiveSettings settings;
        string? error;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) settings = _settings;
            var next = settings.Clone();
            error = ApplyUpdate(next, update);
            if (error is null)
            {
                await _store.SaveSettingsAsync(next, cancellationToken).ConfigureAwait(false);
                lock (_sync) _settings = next;
                settings = next;
            }
        }
        finally
        {
            _gate.Release();
        }

        // The tool's status reads the disk, which can be a share, so it waits for the gate to be free.
        return new ArchiveSettingsResult { Ok = error is null, Error = error, Settings = MapSettings(settings), Tool = ToolStatus(settings) };
    }

    /// <summary>Returns what is wrong with the update, or null once it is applied.</summary>
    private string? ApplyUpdate(ArchiveSettings next, ArchiveSettingsUpdate update)
    {
        if (update.AutoExtract is { } auto)
        {
            if (auto && !next.AutoExtract) next.AutoExtractSince = _time.GetUtcNow();
            next.AutoExtract = auto;
        }

        if (update.ClearUnrarPath)
        {
            next.UnrarPath = null;
        }
        else if (update.UnrarPath is { } tool)
        {
            if (!Path.IsPathFullyQualified(tool) || !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return "The UnRAR path must be the full path of an .exe file.";
            }

            next.UnrarPath = tool;
        }

        if (update.Folder is { } folder)
        {
            if (!Path.IsPathFullyQualified(folder)) return "The destination folder must be a full path.";
            next.ChosenFolder = folder;
        }

        if (update.Destination is { } destination)
        {
            if (!TryParseDestination(destination, out var mode)) return "The destination must be nextToArchive, subfolder or folder.";
            next.DestinationMode = mode;
        }

        if (next.DestinationMode == ArchiveDestinationMode.ChosenFolder && string.IsNullOrWhiteSpace(next.ChosenFolder))
        {
            return "Choose a destination folder first.";
        }

        if (update.ConflictPolicy is { } policy)
        {
            if (!TryParsePolicy(policy, out var parsed)) return "The conflict policy must be rename, overwrite or skip.";
            next.ConflictPolicy = parsed;
        }

        if (update.PreserveFolders is { } preserve) next.PreserveFolders = preserve;
        if (update.DeleteArchives is { } delete) next.DeleteArchives = delete;
        return null;
    }

    /// <summary>Asks for extraction. Files already on their way are waited for; this never runs the tool itself.</summary>
    public async Task<ArchiveExtractResult> ExtractAsync(ArchiveExtractRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DescribeBadPassword(request.Password) is { } badPassword) return new ArchiveExtractResult { Ok = false, Error = badPassword };

        var jobs = _service.GetSnapshot().Jobs;
        var targets = new List<string>();
        var ignored = new List<ArchiveExtractIgnored>();

        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            var path = request.FilePath;
            if (!Path.IsPathFullyQualified(path)) return new ArchiveExtractResult { Ok = false, Error = "The file must be given as a full path." };
            if (!File.Exists(path)) ignored.Add(new ArchiveExtractIgnored { Item = path, Reason = "The file is not there." });
            else if (!ArchiveSetIdentity.LooksLikeVolume(Path.GetFileName(path))) ignored.Add(new ArchiveExtractIgnored { Item = path, Reason = "This is not a RAR archive. Only RAR is supported." });
            else targets.Add(Path.GetFullPath(path));
        }

        foreach (var jobId in request.JobIds ?? [])
        {
            var job = jobs.FirstOrDefault(candidate => candidate.Id == jobId);
            if (job is null) ignored.Add(new ArchiveExtractIgnored { Item = jobId.ToString(), Reason = "There is no such download." });
            else if (!ArchiveSetIdentity.LooksLikeVolume(job.FileName)) ignored.Add(new ArchiveExtractIgnored { Item = job.FileName, Reason = "This is not a RAR archive. Only RAR is supported." });
            else targets.Add(job.FinalFilePath);
        }

        if (request.PackageId is { } packageId)
        {
            var group = _service.GetDownloadGroups().FirstOrDefault(candidate => candidate.Id == packageId);
            if (group is null) return new ArchiveExtractResult { Ok = false, Error = "There is no such package." };
            foreach (var job in jobs.Where(job => group.JobIds.Contains(job.Id)))
            {
                if (ArchiveSetIdentity.LooksLikeVolume(job.FileName)) targets.Add(job.FinalFilePath);
                else ignored.Add(new ArchiveExtractIgnored { Item = job.FileName, Reason = "This is not a RAR archive. Only RAR is supported." });
            }

            if (!string.IsNullOrEmpty(request.Password))
            {
                await SavePackagePasswordAsync(packageId, request.Password, cancellationToken).ConfigureAwait(false);
            }
        }

        var setPassword = request.PackageId is null ? request.Password : null;
        var sets = new Dictionary<Guid, ArchiveSetDto>();
        foreach (var inFolder in targets.Where(target => Path.GetDirectoryName(target) is not null).GroupBy(target => Path.GetDirectoryName(target)!, StringComparer.OrdinalIgnoreCase))
        {
            var view = ArchiveDirectoryView.Read(inFolder.Key, jobs);
            foreach (var target in inFolder)
            {
                var name = Path.GetFileName(target);
                if (view.FindSet(name) is not { } set)
                {
                    ignored.Add(new ArchiveExtractIgnored { Item = target, Reason = "This is not part of a RAR set." });
                    continue;
                }

                var key = ArchiveSetIdentity.KeyFor(view.Directory, set);
                var chosen = view.FindByKey(key) ?? set;
                var id = RecordFor(view, chosen, key)?.Id ?? NewIdFor(key);
                if (sets.ContainsKey(id)) continue;
                if (RecordFor(view, chosen, key) is null && IsUnreadable(id))
                {
                    ignored.Add(new ArchiveExtractIgnored { Item = target, Reason = UnreadableRecord });
                    continue;
                }

                sets[id] = await RequestExtractionAsync(id, view, chosen, jobs, setPassword, ignored, target, cancellationToken).ConfigureAwait(false);
            }
        }

        Wake();
        return new ArchiveExtractResult
        {
            Ok = sets.Count > 0,
            Error = sets.Count > 0 ? null : ignored.FirstOrDefault()?.Reason ?? "Nothing to extract was given.",
            Sets = [.. sets.Values],
            Ignored = ignored,
        };
    }

    private async Task<ArchiveSetDto> RequestExtractionAsync(
        Guid id,
        ArchiveDirectoryView view,
        ArchiveVolumeSet set,
        IReadOnlyList<JobDto> jobs,
        string? password,
        List<ArchiveExtractIgnored> ignored,
        string target,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        ArchiveSetDto result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? existing;
            lock (_sync) existing = _sets.GetValueOrDefault(id);
            if (existing is not null && !existing.Removed)
            {
                switch (existing.State)
                {
                    case ArchiveSetState.Extracting:
                        return Map(existing);
                    case ArchiveSetState.Completed:
                        ignored.Add(new ArchiveExtractIgnored
                        {
                            Item = target,
                            Reason = $"Already extracted to {existing.Destination}. Use retry to extract it again.",
                        });
                        return Map(existing);
                }
            }

            var key = ArchiveSetIdentity.KeyFor(view.Directory, set);
            var next = existing?.Clone() ?? NewRecord(id, key, view, set, now);
            next.Removed = false;
            next.RemovedAt = null;
            next.Wanted = true;
            next.State = ArchiveSetState.Waiting;
            next.ToolMissingVolumes = [];
            ClearFailure(next);
            if (!string.IsNullOrEmpty(password)) next.ProtectedPassword = SecretProtector.ProtectArchivePassword(password);

            foreach (var job in jobs.Where(job => job.State == "completed" && job.CompletedAt is not null && set.Members.Contains(Path.GetFileName(job.FinalFilePath), StringComparer.OrdinalIgnoreCase)))
            {
                next.SeenJobs[job.Id] = job.CompletedAt!.Value;
            }

            Apply(next, view, set);
            await SaveLockedAsync(next).ConfigureAwait(false);
            result = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(result);
        return result;
    }

    public async Task<ArchiveActionResult> SetPasswordAsync(ArchiveSetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DescribeBadPassword(request.Password) is { } bad) return new ArchiveActionResult { Ok = false, Error = bad };

        if (request.PackageId is { } packageId)
        {
            var group = _service.GetDownloadGroups().FirstOrDefault(candidate => candidate.Id == packageId);
            if (group is null) return new ArchiveActionResult { Ok = false, Error = "There is no such package." };
            await SavePackagePasswordAsync(packageId, request.Password, cancellationToken).ConfigureAwait(false);

            // By its downloads too: a set may have been listed before its package was saved.
            var members = group.JobIds.ToHashSet();
            List<Guid> asking;
            lock (_sync) asking = _sets.Values.Where(set => AsksForPassword(set) && (set.PackageId == packageId || set.JobIds.Any(members.Contains))).Select(set => set.Id).ToList();
            foreach (var id in asking) await ResumeAskingSetAsync(id, cancellationToken).ConfigureAwait(false);
            Wake();
            return new ArchiveActionResult { Ok = true };
        }

        Guid setId;
        if (request.SetId is { } requested)
        {
            setId = requested;
        }
        else if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            var created = await RecordInAdvanceAsync(request.FilePath, cancellationToken).ConfigureAwait(false);
            if (created.Error is { } error) return new ArchiveActionResult { Ok = false, Error = error };
            setId = created.Id;
        }
        else
        {
            return new ArchiveActionResult { Ok = false, Error = "Say which set or package the password is for." };
        }

        ArchiveSetDto announce;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? record;
            lock (_sync) record = _sets.GetValueOrDefault(setId);
            if (record is null || record.Removed) return new ArchiveActionResult { Ok = false, Error = "There is no such archive set." };

            var next = record.Clone();
            next.ProtectedPassword = string.IsNullOrEmpty(request.Password) ? null : SecretProtector.ProtectArchivePassword(request.Password);
            if (next.ProtectedPassword is not null && AsksForPassword(next))
            {
                next.Wanted = true;
                next.State = ArchiveSetState.Waiting;
                ClearFailure(next);
            }

            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(announce);
        Wake();
        return new ArchiveActionResult { Ok = true, Set = announce };
    }

    public async Task<string?> SetPackagePasswordAsync(Guid packageId, string? password, bool keepExisting = false, CancellationToken cancellationToken = default)
    {
        if (keepExisting)
        {
            lock (_sync)
            {
                if (_packagePasswords.ContainsKey(packageId)) return null;
            }
        }

        return (await SetPasswordAsync(new ArchiveSetPasswordRequest { PackageId = packageId, Password = password }, cancellationToken).ConfigureAwait(false)).Error;
    }

    private async Task<(Guid Id, string? Error)> RecordInAdvanceAsync(string path, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path)) return (Guid.Empty, "The file must be given as a full path.");
        var name = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !ArchiveSetIdentity.LooksLikeVolume(name)) return (Guid.Empty, "This is not a RAR archive. Only RAR is supported.");

        var jobs = _service.GetSnapshot().Jobs;
        var view = ArchiveDirectoryView.Read(directory, jobs, [name]);
        if (view.FindSet(name) is not { } set) return (Guid.Empty, "This is not part of a RAR set.");

        var key = ArchiveSetIdentity.KeyFor(view.Directory, set);
        set = view.FindByKey(key) ?? set;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = RecordFor(view, set, key);
            var id = existing?.Id ?? NewIdFor(key);
            if (existing is null && IsUnreadable(id)) return (Guid.Empty, UnreadableRecord);
            if (existing is not null && !existing.Removed) return (id, null);

            var next = existing?.Clone() ?? NewRecord(id, key, view, set, _time.GetUtcNow());
            next.Removed = false;
            next.RemovedAt = null;
            Apply(next, view, set);
            await SaveLockedAsync(next).ConfigureAwait(false);
            return (id, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SavePackagePasswordAsync(Guid packageId, string? password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(password))
            {
                await _store.DeletePackagePasswordAsync(packageId, cancellationToken).ConfigureAwait(false);
                lock (_sync) _packagePasswords.Remove(packageId);
                return;
            }

            var record = new ArchivePackagePassword
            {
                PackageId = packageId,
                ProtectedPassword = SecretProtector.ProtectArchivePassword(password),
                Stamp = _time.GetUtcNow(),
            };
            await _store.SavePackagePasswordAsync(record, cancellationToken).ConfigureAwait(false);
            lock (_sync) _packagePasswords[packageId] = record;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResumeAskingSetAsync(Guid id, CancellationToken cancellationToken)
    {
        ArchiveSetDto? announce = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? record;
            lock (_sync) record = _sets.GetValueOrDefault(id);
            if (record is null || !AsksForPassword(record)) return;
            var next = record.Clone();
            next.Wanted = true;
            next.State = ArchiveSetState.Waiting;
            ClearFailure(next);
            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(announce);
    }

    /// <summary>Runs a stopped or finished set again. The conflict policy decides what a second run does to earlier output.</summary>
    public async Task<ArchiveActionResult> RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArchiveSetDto announce;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? record;
            lock (_sync) record = _sets.GetValueOrDefault(id);
            if (record is null || record.Removed) return new ArchiveActionResult { Ok = false, Error = "There is no such archive set." };
            if (record.State == ArchiveSetState.Extracting) return new ArchiveActionResult { Ok = false, Error = "This set is already extracting.", Set = Map(record) };

            var next = record.Clone();
            next.Wanted = true;
            next.State = ArchiveSetState.Waiting;
            next.ToolMissingVolumes = [];
            next.Note = null;
            ClearFailure(next);
            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(announce);
        Wake();
        return new ArchiveActionResult { Ok = true, Set = announce };
    }

    /// <summary>Stops a running set, killing its process tree, or takes a waiting one out of line. The archives stay.</summary>
    public async Task<ArchiveActionResult> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArchiveSetDto announce;
        RunningExtraction? stopping = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? record;
            lock (_sync)
            {
                record = _sets.GetValueOrDefault(id);
                if (record is { Removed: false, State: ArchiveSetState.Extracting }) stopping = _running.GetValueOrDefault(id);
            }

            if (stopping is not null) return new ArchiveActionResult { Ok = true, Set = Map(record!) };
            if (record is null || record.Removed) return new ArchiveActionResult { Ok = false, Error = "There is no such archive set." };
            if (record.State != ArchiveSetState.Waiting) return new ArchiveActionResult { Ok = false, Error = "This set is not waiting or extracting.", Set = Map(record) };

            var next = record.Clone();
            next.Wanted = false;
            next.State = ArchiveSetState.Failed;
            next.FailureKind = ExtractionFailureKind.Cancelled;
            next.FailureMessage = "Extraction was cancelled.";
            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);
        }
        finally
        {
            _gate.Release();

            // Outside the gate: cancelling runs whatever waits on the token, right here on this thread.
            stopping?.Stop();
        }

        Announce(announce);
        return new ArchiveActionResult { Ok = true, Set = announce };
    }

    /// <summary>Takes a set off the list and forgets its password. Never touches a file.</summary>
    public async Task<ArchiveActionResult> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? record;
            lock (_sync) record = _sets.GetValueOrDefault(id);
            if (record is null || record.Removed) return new ArchiveActionResult { Ok = false, Error = "There is no such archive set." };
            if (record.State == ArchiveSetState.Extracting) return new ArchiveActionResult { Ok = false, Error = "This set is extracting. Cancel it first.", Set = Map(record) };

            var next = record.Clone();
            next.Removed = true;
            next.RemovedAt = _time.GetUtcNow();
            next.Wanted = false;
            next.ProtectedPassword = null;
            await SaveLockedAsync(next).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        AnnounceRemoved(id);
        return new ArchiveActionResult { Ok = true };
    }

    private static bool AsksForPassword(ArchiveSetRecord set) =>
        set is { State: ArchiveSetState.Failed, FailureKind: ExtractionFailureKind.PasswordRequired or ExtractionFailureKind.WrongPassword };

    /// <summary>Says what is wrong with a password without repeating any of it.</summary>
    private static string? DescribeBadPassword(string? password) =>
        password is not null && password.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0 ? "A RAR password cannot contain a line break." : null;

    private ArchiveToolStatusDto ToolStatus(ArchiveSettings settings)
    {
        var found = UnrarToolLocator.Find(settings.UnrarPath, _options.ProgramFilesDirectories, _options.PathVariable);
        var configuredFound = !string.IsNullOrWhiteSpace(settings.UnrarPath) && File.Exists(settings.UnrarPath);
        string message;
        if (found is null)
        {
            message = string.IsNullOrWhiteSpace(settings.UnrarPath)
                ? "UnRAR was not found. Install WinRAR or set the path to UnRAR.exe."
                : "The chosen UnRAR path does not exist, and no other UnRAR was found.";
        }
        else if (configuredFound)
        {
            message = $"Using the chosen UnRAR at {found}.";
        }
        else if (!string.IsNullOrWhiteSpace(settings.UnrarPath))
        {
            message = $"The chosen UnRAR path does not exist, so the UnRAR at {found} is used instead.";
        }
        else
        {
            message = $"Using the UnRAR found at {found}.";
        }

        return new ArchiveToolStatusDto
        {
            Found = found is not null,
            Path = found,
            ConfiguredPath = settings.UnrarPath,
            ConfiguredPathFound = configuredFound,
            Message = message,
        };
    }

    private static ArchiveSettingsDto MapSettings(ArchiveSettings settings) => new()
    {
        AutoExtract = settings.AutoExtract,
        UnrarPath = settings.UnrarPath,
        Destination = settings.DestinationMode switch
        {
            ArchiveDestinationMode.NamedSubfolder => "subfolder",
            ArchiveDestinationMode.ChosenFolder => "folder",
            _ => "nextToArchive",
        },
        Folder = settings.ChosenFolder,
        PreserveFolders = settings.PreserveFolders,
        ConflictPolicy = settings.ConflictPolicy switch
        {
            ExtractionConflictPolicy.Overwrite => "overwrite",
            ExtractionConflictPolicy.Skip => "skip",
            _ => "rename",
        },
        DeleteArchives = settings.DeleteArchives,
    };

    private static bool TryParseDestination(string text, out ArchiveDestinationMode mode)
    {
        switch (text.ToLowerInvariant())
        {
            case "nexttoarchive":
                mode = ArchiveDestinationMode.NextToArchive;
                return true;
            case "subfolder":
                mode = ArchiveDestinationMode.NamedSubfolder;
                return true;
            case "folder":
                mode = ArchiveDestinationMode.ChosenFolder;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static bool TryParsePolicy(string text, out ExtractionConflictPolicy policy)
    {
        switch (text.ToLowerInvariant())
        {
            case "rename":
                policy = ExtractionConflictPolicy.Rename;
                return true;
            case "overwrite":
                policy = ExtractionConflictPolicy.Overwrite;
                return true;
            case "skip":
                policy = ExtractionConflictPolicy.Skip;
                return true;
            default:
                policy = default;
                return false;
        }
    }

    private ArchiveSetDto Map(ArchiveSetRecord set)
    {
        var live = set.State == ArchiveSetState.Extracting ? _live.GetValueOrDefault(set.Id) : null;
        bool packageHasPassword;
        lock (_sync)
        {
            packageHasPassword = set.PackageId is { } packageId && _packagePasswords.TryGetValue(packageId, out var password) && password.Stamp != set.RejectedPackagePasswordStamp;
        }

        return new ArchiveSetDto
        {
            Id = set.Id,
            Name = ExtractionSafety.SafeForDisplay(set.BaseName),
            Directory = set.Directory,
            FirstVolume = FirstVolumePath(set),
            State = JsonNamingPolicy.CamelCase.ConvertName(set.State.ToString()),
            StateText = DescribeState(set, live),
            FailureKind = set.State == ArchiveSetState.Failed && set.FailureKind is { } kind ? JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()) : null,
            FailureMessage = set.State == ArchiveSetState.Failed ? set.FailureMessage : null,
            NeedsPassword = AsksForPassword(set),
            HasPassword = set.ProtectedPassword is not null || packageHasPassword,
            Volumes = [.. set.Volumes.Select(ExtractionSafety.SafeForDisplay)],
            MissingVolumes = [.. set.MissingVolumes.Select(ExtractionSafety.SafeForDisplay)],
            DownloadingVolumes = [.. set.DownloadingVolumes.Select(ExtractionSafety.SafeForDisplay)],
            FailedVolumes = [.. set.FailedVolumes.Select(ExtractionSafety.SafeForDisplay)],
            Phase = live is null ? null : JsonNamingPolicy.CamelCase.ConvertName(live.Phase.ToString()),
            Percent = live?.Percent,
            FilesFinalized = live?.FilesFinalized ?? 0,
            FilesTotal = live?.FilesTotal ?? 0,
            Destination = set.Destination,
            Attempts = set.Attempts,
            FilesWritten = set.FilesWritten,
            FilesOverwritten = set.FilesOverwritten,
            FilesRenamed = set.FilesRenamed,
            FilesSkipped = set.FilesSkipped,
            ArchivesDeleted = set.ArchivesDeleted,
            Note = set.Note,
            PackageId = set.PackageId,
            JobIds = [.. set.JobIds],
            CreatedAt = set.CreatedAt,
            UpdatedAt = set.UpdatedAt,
            CompletedAt = set.CompletedAt,
        };
    }

    private static string DescribeState(ArchiveSetRecord set, ExtractionProgress? live)
    {
        switch (set.State)
        {
            case ArchiveSetState.Downloading:
                if (set.DownloadingVolumes.Count > 0) return $"Downloading: {Names(set.DownloadingVolumes)}";
                return set.FailedVolumes.Count > 0 ? $"Download failed: {Names(set.FailedVolumes)}" : "Not downloaded yet";

            case ArchiveSetState.Downloaded:
                var gaps = set.MissingVolumes.Concat(set.FailedVolumes).ToList();
                return gaps.Count > 0 ? $"Downloaded, but missing: {Names(gaps)}" : "Downloaded";

            case ArchiveSetState.Waiting:
                if (set.DownloadingVolumes.Count > 0) return $"Waiting for extraction: still downloading {Names(set.DownloadingVolumes)}";
                if (set.FailedVolumes.Count > 0) return $"Waiting for extraction: the download failed for {Names(set.FailedVolumes)}";
                if (set.MissingVolumes.Count > 0) return $"Waiting for extraction: missing {Names(set.MissingVolumes)}";
                return "Waiting for extraction: next in line";

            case ArchiveSetState.Extracting:
                return live switch
                {
                    { Phase: ExtractionPhase.Extracting, Percent: { } percent } => $"Extracting {percent}%",
                    { Phase: ExtractionPhase.Verifying } => "Extracting: checking the files",
                    { Phase: ExtractionPhase.Finalizing } => $"Extracting: moving files, {live.FilesFinalized} of {live.FilesTotal}",
                    _ => "Extracting",
                };

            case ArchiveSetState.Completed:
                var files = set.FilesWritten + set.FilesOverwritten + set.FilesRenamed;
                var text = $"Extraction completed: {files} {(files == 1 ? "file" : "files")} in {set.Destination}";
                if (set.FilesRenamed > 0) text += $", {set.FilesRenamed} renamed";
                if (set.FilesSkipped > 0) text += $", {set.FilesSkipped} skipped";
                if (set.ArchivesDeleted) text += ", archive removed";
                return text;

            default:
                var label = set.FailureKind switch
                {
                    ExtractionFailureKind.PasswordRequired => "Password required",
                    ExtractionFailureKind.WrongPassword => "Wrong password",
                    ExtractionFailureKind.MissingVolume => "Missing volume",
                    ExtractionFailureKind.Corrupted => "Corrupted archive",
                    ExtractionFailureKind.UnsupportedVersion => "Unsupported archive version",
                    ExtractionFailureKind.ToolUnavailable => "UnRAR unavailable",
                    ExtractionFailureKind.DiskSpace => "Not enough disk space",
                    ExtractionFailureKind.Permission => "Permission refused",
                    ExtractionFailureKind.UnsafePath => "Unsafe archive",
                    ExtractionFailureKind.Cancelled => "Extraction cancelled",
                    _ => "Extraction failed",
                };
                if (string.IsNullOrEmpty(set.FailureMessage)) return label;

                // The tool's reason can start with the same label. Once is enough.
                return set.FailureMessage.StartsWith($"{label}: ", StringComparison.OrdinalIgnoreCase) ? set.FailureMessage : $"{label}: {set.FailureMessage}";
        }
    }

    private static string Names(IReadOnlyList<string> names)
    {
        var shown = names.Take(MaxNamesInText).Select(ExtractionSafety.SafeForDisplay);
        var text = string.Join(", ", shown);
        return names.Count > MaxNamesInText ? $"{text} and {names.Count - MaxNamesInText} more" : text;
    }
}
