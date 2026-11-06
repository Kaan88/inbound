using DownloadManager.Archives;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Persistence;

namespace DownloadManager.Runtime.Archives;

public sealed partial class ArchiveCoordinator
{
    /// <summary>
    /// Whatever a crash left in staging. An attempt a set has on record as finished is that set's
    /// result, so only its staging goes. Any other attempt is undone, because nothing it did counts.
    /// </summary>
    private async Task RecoverStagingAsync(IReadOnlyList<ArchiveSetRecord> sets)
    {
        var roots = sets.Select(set => set.StagingRoot).Where(root => root is not null).Select(root => root!).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            IReadOnlyList<StagingLeftover> leftovers;
            try
            {
                leftovers = StagingArea.FindLeftovers(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Could not look for leftover extraction files: {exception.GetType().Name}");
                continue;
            }

            foreach (var leftover in leftovers)
            {
                var finished = sets.Any(set => set.State == ArchiveSetState.Completed &&
                    string.Equals(FirstVolumePath(set), leftover.FirstVolume, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(set.Destination, leftover.Destination, StringComparison.OrdinalIgnoreCase));
                var removed = finished
                    ? await StagingArea.DiscardFinishedAsync(leftover.Folder).ConfigureAwait(false)
                    : await StagingArea.RecoverAndRemoveAsync(leftover.Folder).ConfigureAwait(false);

                // The set's next run tries again and reports it if the folder is still held.
                if (!removed) _log?.Invoke("A leftover extraction folder could not be removed yet.");
            }
        }
    }

    private static string FirstVolumePath(ArchiveSetRecord set) => Path.Combine(set.Directory, set.FirstVolumeName);

    /// <summary>Starts every set that is ready, up to the limit. Only the worker calls this, so claims never race each other.</summary>
    private async Task StartReadySetsAsync()
    {
        while (true)
        {
            ArchiveSetRecord[] candidates;
            lock (_sync)
            {
                if (_stopping || _running.Count >= Math.Max(1, _options.MaxConcurrentExtractions)) return;
                candidates = _sets.Values
                    .Where(set => set is { Wanted: true, Removed: false, State: ArchiveSetState.Waiting } && !_running.ContainsKey(set.Id))
                    .OrderBy(set => set.UpdatedAt)
                    .ToArray();
            }

            if (candidates.Length == 0) return;

            var jobs = _service.GetSnapshot().Jobs;
            var started = false;
            foreach (var candidate in candidates)
            {
                if (await TryClaimAsync(candidate.Id, jobs).ConfigureAwait(false))
                {
                    started = true;
                    break;
                }
            }

            if (!started) return;
        }
    }

    private async Task<bool> TryClaimAsync(Guid id, IReadOnlyList<JobDto> jobs)
    {
        ArchiveSetRecord? record;
        lock (_sync) record = _sets.GetValueOrDefault(id);
        if (record is null) return false;

        // The folder is read outside the gate: it can be a slow disk or a network share.
        var view = ArchiveDirectoryView.Read(record.Directory, jobs);
        var set = view.FindByKey(record.Key);
        ArchiveSetDto? announce = null;
        var claimed = false;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ArchiveSettings settings;
            lock (_sync)
            {
                record = _sets.GetValueOrDefault(id);
                settings = _settings;
            }

            if (_stopping || record is not { Wanted: true, Removed: false, State: ArchiveSetState.Waiting }) return false;

            var next = record.Clone();
            if (set is not null)
            {
                Apply(next, view, set);
            }
            else
            {
                next.Volumes = [];
                next.DownloadingVolumes = [];
                next.FailedVolumes = [];
                next.MissingVolumes = [next.FirstVolumeName];
            }

            if (next.State != ArchiveSetState.Waiting || !IsReady(next, view))
            {
                if (!SameContent(record, next))
                {
                    await SaveLockedAsync(next).ConfigureAwait(false);
                    announce = Map(next);
                }

                return false;
            }

            var (destination, stagingRoot) = ArchiveDestinations.Resolve(next, settings);
            next.State = ArchiveSetState.Extracting;
            next.Attempts++;
            next.Destination = destination;
            next.StagingRoot = stagingRoot;
            next.Note = null;
            ClearFailure(next);

            // The claim. From here a restart knows a run was under way and where its staging is.
            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);

            var cancellation = new CancellationTokenSource();
            var run = new RunningExtraction(cancellation);
            lock (_sync) _running[id] = run;
            run.Task = Task.Run(() => RunAsync(next, settings, cancellation), CancellationToken.None);
            claimed = true;
        }
        finally
        {
            _gate.Release();
            Announce(announce);
        }

        return claimed;
    }

    private static bool IsReady(ArchiveSetRecord set, ArchiveDirectoryView view) =>
        set.DownloadingVolumes.Count == 0 &&
        set.FailedVolumes.Count == 0 &&
        set.MissingVolumes.Count == 0 &&
        view.IsOnDisk(set.FirstVolumeName) &&
        set.ToolMissingVolumes.All(view.IsOnDisk);

    private async Task RunAsync(ArchiveSetRecord claimed, ArchiveSettings settings, CancellationTokenSource cancellation)
    {
        var id = claimed.Id;
        var committed = false;
        var (password, ownPassword, packageStamp) = ResolvePassword(claimed);
        var volumes = claimed.Volumes.Select(name => Path.Combine(claimed.Directory, name)).ToList();

        async Task CommitOnceAsync(ExtractionResult result)
        {
            await CommitAsync(id, result, settings.DeleteArchives, claimed.Volumes).ConfigureAwait(false);
            committed = true;
        }

        try
        {
            var request = new ExtractionRequest
            {
                FirstVolume = FirstVolumePath(claimed),
                Destination = claimed.Destination!,
                StagingRoot = claimed.StagingRoot!,
                Password = password,
                ConflictPolicy = settings.ConflictPolicy,
                PreserveFolders = settings.PreserveFolders,
                KeepFiles = volumes,
                Committed = CommitOnceAsync,
            };

            var result = await CreateExtractor(settings).ExtractAsync(request, new ProgressSink(this, id), cancellation.Token).ConfigureAwait(false);
            if (!committed) await CommitOnceAsync(result).ConfigureAwait(false);
            await FinishDeletingAsync(id).ConfigureAwait(false);
        }
        catch (ExtractionException failure) when (!committed)
        {
            await RecordFailureAsync(id, failure.Kind, failure.Message, failure.MissingVolumes, password is not null && ownPassword, packageStamp).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!committed)
        {
            await RecordFailureAsync(id, ExtractionFailureKind.Cancelled, "Extraction was cancelled.", [], false, null).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Only the type: this is the one path a message could carry something unexpected.
            _log?.Invoke($"Extraction stopped unexpectedly: {exception.GetType().Name}");
            if (!committed)
            {
                await RecordFailureAsync(id, ExtractionFailureKind.Unknown, $"Extraction stopped unexpectedly: {exception.GetType().Name}.", [], false, null).ConfigureAwait(false);
            }
        }
        finally
        {
            _live.TryRemove(id, out _);
            lock (_sync) _running.Remove(id);
            cancellation.Dispose();
            Wake();
        }
    }

    private (string? Password, bool Own, DateTimeOffset? PackageStamp) ResolvePassword(ArchiveSetRecord set)
    {
        if (set.ProtectedPassword is { } own && SecretProtector.UnprotectArchivePassword(own) is { } ownPlain)
        {
            return (ownPlain, true, null);
        }

        ArchivePackagePassword? package = null;
        lock (_sync)
        {
            if (set.PackageId is { } packageId) package = _packagePasswords.GetValueOrDefault(packageId);
        }

        if (package is not null && package.Stamp != set.RejectedPackagePasswordStamp &&
            SecretProtector.UnprotectArchivePassword(package.ProtectedPassword) is { } packagePlain)
        {
            return (packagePlain, false, package.Stamp);
        }

        return (null, false, null);
    }

    private IArchiveExtractor CreateExtractor(ArchiveSettings settings) =>
        _options.ExtractorFactory?.Invoke(settings) ?? new UnrarExtractor(new UnrarExtractorOptions
        {
            ToolPath = settings.UnrarPath,
            ProgramFilesDirectories = _options.ProgramFilesDirectories,
            PathVariable = _options.PathVariable,
            Limits = _options.Limits,
            AvailableBytes = _options.AvailableBytes,
        });

    /// <summary>
    /// The one place a set becomes finished. It runs after every check and move, and before staging
    /// goes. If this save fails the extractor undoes the placed files, so success on disk and files
    /// in the destination cannot disagree.
    /// </summary>
    private async Task CommitAsync(Guid id, ExtractionResult result, bool deleteArchives, IReadOnlyList<string> volumes)
    {
        ArchiveSetDto announce;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? record;
            lock (_sync) record = _sets.GetValueOrDefault(id);
            var next = (record ?? throw new InvalidOperationException("The set is gone.")).Clone();
            next.State = ArchiveSetState.Completed;
            next.CompletedAt = _time.GetUtcNow();
            next.Destination = result.Destination;
            ClearFailure(next);
            next.ToolMissingVolumes = [];
            next.DownloadingVolumes = [];
            next.FailedVolumes = [];
            next.MissingVolumes = [];
            next.FilesWritten = result.Files.Count(file => file.Outcome == ExtractedFileOutcome.Written);
            next.FilesOverwritten = result.Files.Count(file => file.Outcome == ExtractedFileOutcome.Overwrote);
            next.FilesRenamed = result.Files.Count(file => file.Outcome == ExtractedFileOutcome.Renamed);
            next.FilesSkipped = result.Files.Count(file => file.Outcome == ExtractedFileOutcome.Skipped);

            // It has done its job, and a password is kept only while it is needed.
            next.ProtectedPassword = null;
            next.ArchivesDeleted = false;
            next.Note = null;
            if (deleteArchives && next.FilesSkipped > 0)
            {
                // A skipped entry exists only in the archive, so deleting that would lose it.
                next.VolumesToDelete = [];
                next.Note = $"The archives were kept because {next.FilesSkipped} files were skipped.";
            }
            else
            {
                next.VolumesToDelete = deleteArchives ? [.. volumes] : [];
            }

            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(announce);
    }

    /// <summary>Runs after success is on disk, so a restart can finish it and nothing here can undo that success.</summary>
    private async Task FinishDeletingAsync(Guid id)
    {
        ArchiveSetRecord? record;
        lock (_sync) record = _sets.GetValueOrDefault(id);
        if (record is not { State: ArchiveSetState.Completed } || record.VolumesToDelete.Count == 0) return;

        var kept = new List<string>();
        foreach (var name in record.VolumesToDelete)
        {
            try
            {
                var path = Path.Combine(record.Directory, Path.GetFileName(name));
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                kept.Add(name);
            }
        }

        ArchiveSetDto announce;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_sync) record = _sets.GetValueOrDefault(id);
            if (record is null) return;
            var next = record.Clone();
            next.VolumesToDelete = [];
            next.ArchivesDeleted = kept.Count == 0;
            next.Volumes = kept;
            if (kept.Count > 0) next.Note = "Could not delete: " + string.Join(", ", kept.Select(ExtractionSafety.SafeForDisplay));
            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(announce);
    }

    private async Task RecordFailureAsync(Guid id, ExtractionFailureKind kind, string message, IReadOnlyList<string> missing, bool usedOwnPassword, DateTimeOffset? packageStamp)
    {
        ArchiveSetDto? announce = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ArchiveSetRecord? record;
            lock (_sync) record = _sets.GetValueOrDefault(id);
            if (record is null) return;

            var next = record.Clone();
            if (_stopping && kind == ExtractionFailureKind.Cancelled)
            {
                // The Host is going away, which says nothing about this archive.
                next.State = ArchiveSetState.Waiting;
                next.Note = "Extraction was interrupted, so it runs again.";
                ClearFailure(next);
            }
            else
            {
                next.State = ArchiveSetState.Failed;
                next.FailureKind = kind;
                next.FailureMessage = message;
                switch (kind)
                {
                    case ExtractionFailureKind.WrongPassword:
                        // Never tried again by itself. The set's own is forgotten. A package's
                        // can be right for its other sets, so this set just won't use that one again.
                        if (usedOwnPassword) next.ProtectedPassword = null;
                        else if (packageStamp is not null) next.RejectedPackagePasswordStamp = packageStamp;
                        break;

                    case ExtractionFailureKind.PasswordRequired:
                        // Nothing readable was sent, so whatever is on record cannot be used.
                        next.ProtectedPassword = null;
                        break;

                    case ExtractionFailureKind.MissingVolume:
                        next.ToolMissingVolumes = [.. missing];
                        next.MissingVolumes = next.MissingVolumes.Union(missing, StringComparer.OrdinalIgnoreCase).ToList();
                        break;

                    case ExtractionFailureKind.Cancelled:
                        next.Wanted = false;
                        break;
                }
            }

            try
            {
                await SaveLockedAsync(next).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // On disk it still reads as extracting, which a restart turns back into waiting.
                _log?.Invoke($"Could not save an extraction's outcome: {exception.GetType().Name}");
                lock (_sync) _sets[id] = next;
            }

            announce = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(announce);
    }

    private sealed class ProgressSink(ArchiveCoordinator owner, Guid id) : IProgress<ExtractionProgress>
    {
        private static readonly long MinimumGap = TimeSpan.FromMilliseconds(200).Ticks;
        private ExtractionPhase? _phase;
        private long _lastAnnounced;

        public void Report(ExtractionProgress value)
        {
            owner._live[id] = value;
            var now = owner._time.GetUtcNow().UtcTicks;
            if (_phase == value.Phase && now - _lastAnnounced < MinimumGap) return;
            _phase = value.Phase;
            _lastAnnounced = now;

            ArchiveSetRecord? record;
            lock (owner._sync) record = owner._sets.GetValueOrDefault(id);
            if (record is not null) owner.Announce(owner.Map(record));
        }
    }
}

internal static class ArchiveDestinations
{
    /// <summary>
    /// Where a set's files go, and where its staging lives. Staging sits beside the files on the
    /// same volume, because finishing an extraction is a rename and never a copy.
    /// </summary>
    public static (string Destination, string StagingRoot) Resolve(ArchiveSetRecord set, ArchiveSettings settings)
    {
        switch (settings.DestinationMode)
        {
            case ArchiveDestinationMode.NamedSubfolder:
                return (Path.Combine(set.Directory, FolderNameFor(set.BaseName)), set.Directory);

            case ArchiveDestinationMode.ChosenFolder when !string.IsNullOrWhiteSpace(settings.ChosenFolder):
                var chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.ChosenFolder));
                return (chosen, chosen);

            default:
                return (set.Directory, set.Directory);
        }
    }

    /// <summary>A folder made from a downloaded file's name, so it gets the same care as a name from inside an archive.</summary>
    private static string FolderNameFor(string baseName)
    {
        var name = baseName.TrimEnd('.', ' ');
        return name.Length == 0 || ExtractionSafety.DescribeUnsafePath(name) is not null || name.Contains('\\') || name.Contains('/') ? "extracted" : name;
    }
}
