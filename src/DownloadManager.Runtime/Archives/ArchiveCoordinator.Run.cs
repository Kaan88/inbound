using DownloadManager.Archives;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Persistence;
using DownloadManager.Core;

namespace DownloadManager.Runtime.Archives;

public sealed partial class ArchiveCoordinator
{
    /// <summary>
    /// Whatever a crash left in staging. An attempt a set has on record as finished is that set's
    /// result, so only its staging goes. Any other attempt is undone, because nothing it did counts.
    /// Only what the saved sets name is touched: a staging folder sits among the user's files, so one no
    /// set can account for says nothing, and is left alone however it looks inside. Nothing here can
    /// stop the Host from starting. Returns how many files each set had to leave where they were.
    /// </summary>
    private async Task<Dictionary<Guid, int>> RecoverStagingAsync(IReadOnlyList<ArchiveSetRecord> sets)
    {
        var leftInPlace = new Dictionary<Guid, int>();
        var accounted = new HashSet<string>(PathNames.Comparer);
        foreach (var set in sets)
        {
            if (set is not { Destination: { } destination, StagingRoot: { } root }) continue;
            try
            {
                var folder = StagingArea.FolderFor(root, FirstVolumePath(set), destination);
                accounted.Add(folder);
                if (!Directory.Exists(folder)) continue;

                if (set.State == ArchiveSetState.Completed)
                {
                    if (!await StagingArea.DiscardFinishedAsync(root, FirstVolumePath(set), destination).ConfigureAwait(false)) LogStillThere();
                    continue;
                }

                var recovery = await StagingArea.RecoverAndRemoveAsync(root, FirstVolumePath(set), destination).ConfigureAwait(false);
                if (recovery.LeftInPlace > 0) leftInPlace[set.Id] = recovery.LeftInPlace;

                // The set's next run tries again and reports it if the folder is still held.
                if (!recovery.Removed) LogStillThere();
            }
            catch (Exception exception)
            {
                _log?.Invoke($"Could not clear up after an interrupted extraction: {exception.GetType().Name}");
            }
        }

        foreach (var root in sets.Select(set => set.StagingRoot).OfType<string>().Distinct(PathNames.Comparer))
        {
            try
            {
                foreach (var stranger in StagingArea.FindLeftovers(root).Where(folder => !accounted.Contains(folder)))
                {
                    _log?.Invoke($"Left a folder alone because no extraction on record made it: {ExtractionSafety.SafeForDisplay(Path.GetFileName(stranger))}");
                }
            }
            catch (Exception exception)
            {
                _log?.Invoke($"Could not look for leftover extraction files: {exception.GetType().Name}");
            }
        }

        return leftInPlace;

        void LogStillThere() => _log?.Invoke("A leftover extraction folder could not be removed yet.");
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
        var set = view.FindFor(record);
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
                next.PausedVolumes = [];
                next.QueuedVolumes = [];
                next.HeldVolumes = [];
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

            List<string> takenFolders;
            lock (_sync)
            {
                takenFolders = _sets.Values
                    .Where(other => other.Id != id && other.Destination is not null && PathNames.Same(other.Directory, next.Directory))
                    .Select(other => other.Destination!)
                    .ToList();
            }

            var earlierDestination = next.Destination;
            var earlierRoot = next.StagingRoot;
            var (destination, stagingRoot) = ArchiveDestinations.Resolve(next, settings, takenFolders);
            next.State = ArchiveSetState.Extracting;
            next.Attempts++;
            next.Destination = destination;
            next.StagingRoot = stagingRoot;
            next.Note = null;
            ClearFailure(next);

            // The claim. From here a restart knows a run was under way and where its staging is.
            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);

            // Listed before it can start or finish, and only if a shutdown has not looked already.
            // One that has counts as interrupted: the next start finds the saved claim.
            var cancellation = new CancellationTokenSource();
            var listed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = new RunningExtraction(cancellation)
            {
                Task = Task.Run(
                    async () =>
                    {
                        if (await listed.Task.ConfigureAwait(false)) await RunAsync(next, earlierRoot, earlierDestination, settings, cancellation).ConfigureAwait(false);
                    },
                    CancellationToken.None),
            };

            lock (_sync)
            {
                claimed = !_stopping;
                if (claimed) _running[id] = run;
            }

            listed.SetResult(claimed);
            if (!claimed) cancellation.Dispose();
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

    private async Task RunAsync(ArchiveSetRecord claimed, string? earlierRoot, string? earlierDestination, ArchiveSettings settings, CancellationTokenSource cancellation)
    {
        var id = claimed.Id;
        var committed = false;
        var (password, ownPassword, packageStamp) = ResolvePassword(claimed);
        var volumes = claimed.Volumes.Select(name => Path.Combine(claimed.Directory, name)).ToList();

        async Task CommitOnceAsync(ExtractionResult result)
        {
            await CommitAsync(id, result, settings.DeleteArchives, WhatTheRunReallyUsed(claimed, result)).ConfigureAwait(false);
            committed = true;
        }

        try
        {
            // An earlier attempt may have left an original in staging under the folder it used. This run's folder is another one if the destination has changed since.
            if (earlierRoot is not null && earlierDestination is not null &&
                !PathNames.Same(earlierDestination, claimed.Destination))
            {
                await StagingArea.RecoverAndRemoveAsync(earlierRoot, FirstVolumePath(claimed), earlierDestination).ConfigureAwait(false);
            }

            var request = new ExtractionRequest
            {
                FirstVolume = FirstVolumePath(claimed),
                Destination = claimed.Destination!,
                StagingRoot = claimed.StagingRoot!,
                Password = password,
                ConflictPolicy = settings.ConflictPolicy,
                PreserveFolders = settings.PreserveFolders,
                KeepFiles = volumes,
                MakeDurable = settings.DeleteArchives,
                Committed = CommitOnceAsync,
            };

            var result = await CreateExtractor(settings).ExtractAsync(request, new ProgressSink(this, id), cancellation.Token).ConfigureAwait(false);
            if (!committed) await CommitOnceAsync(result).ConfigureAwait(false);
            await FinishDeletingAsync(id).ConfigureAwait(false);
        }
        catch (ExtractionException failure) when (!committed)
        {
            await RecordFailureAsync(id, failure.Kind, failure.Message, failure.MissingVolumes, password is not null && ownPassword, packageStamp, claimed).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!committed)
        {
            await RecordFailureAsync(id, ExtractionFailureKind.Cancelled, "Extraction was cancelled.", [], false, null, claimed).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Only the type: this is the one path a message could carry something unexpected.
            _log?.Invoke($"Extraction stopped unexpectedly: {exception.GetType().Name}");
            if (!committed)
            {
                await RecordFailureAsync(id, ExtractionFailureKind.Unknown, $"Extraction stopped unexpectedly: {exception.GetType().Name}.", [], false, null, claimed).ConfigureAwait(false);
            }
        }
        finally
        {
            _live.TryRemove(id, out _);
            lock (_sync) _running.Remove(id);
            cancellation.Dispose();

            // Its downloads may have been removed while it ran. Wakes the worker as well.
            _triggers.Writer.TryWrite(new TidyTrigger());
        }
    }

    /// <summary>What a finished run really used, from the tool and not from the names.</summary>
    /// <param name="Volumes">Volumes the tool read. Null when the extractor can't say.</param>
    /// <param name="Unread">Files the names grouped with this set that the tool never read, so they were not extracted.</param>
    private sealed record RunUse(IReadOnlyList<string>? Volumes, IReadOnlyList<string> Unread, IReadOnlyList<Guid> Jobs);

    /// <summary>
    /// A name only suggests a set. Only what the tool read has been extracted and can ever go, so a file that merely
    /// has the right sort of name is kept, and is never counted as done. That covers a volume that came from another
    /// download between the claim and the tool, which belongs to the result only if the tool did read it.
    /// </summary>
    private RunUse WhatTheRunReallyUsed(ArchiveSetRecord claimed, ExtractionResult result)
    {
        var view = ArchiveDirectoryView.Read(claimed.Directory, _service.GetSnapshot().Jobs);

        // What a download brought or the claim saw. Anything else with such a name here could be what this very run extracted.
        var extracted = result.Files.Select(file => Path.GetFileName(file.FinalPath)).ToHashSet(PathNames.Comparer);
        var named = (view.FindByKey(claimed.Key)?.Members ?? [])
            .Where(member => view.JobsNamed(member).Any(job => job.State == "completed"))
            .Union(claimed.Volumes, PathNames.Comparer)
            .Where(member => view.IsOnDisk(member) && !extracted.Contains(member))
            .ToList();
        if (result.VolumesRead is not { } paths)
        {
            return new RunUse(null, [], claimed.JobIds);
        }

        var read = paths
            .Where(path => PathNames.Same(Path.GetDirectoryName(path), view.Directory))
            .Select(path => Path.GetFileName(path))
            .Where(name => name.Length > 0)
            .Distinct(PathNames.Comparer)
            .ToList();
        var jobs = claimed.JobIds.Union(read.SelectMany(view.JobsNamed).Where(job => job.State == "completed").Select(job => job.Id)).ToList();
        return new RunUse(read, named.Except(read, PathNames.Comparer).ToList(), jobs);
    }

    private (string? Password, bool Own, DateTimeOffset? PackageStamp) ResolvePassword(ArchiveSetRecord set)
    {
        if (set.ProtectedPassword is { } own && SecretProtector.UnprotectArchivePassword(own) is { } ownPlain)
        {
            return (ownPlain, true, null);
        }

        if (UsablePackagePassword(set) is { } package && SecretProtector.UnprotectArchivePassword(package.ProtectedPassword) is { } packagePlain)
        {
            return (packagePlain, false, package.Stamp);
        }

        return (null, false, null);
    }

    /// <summary>The package's password, unless it has failed on this set. A set can run before its record has heard of its package, so its downloads say too.</summary>
    private ArchivePackagePassword? UsablePackagePassword(ArchiveSetRecord set)
    {
        var packageId = set.PackageId ?? set.JobIds.Select(_service.FindGroupOf).FirstOrDefault(package => package is not null);
        if (packageId is not { } id) return null;
        lock (_sync)
        {
            return _packagePasswords.TryGetValue(id, out var password) && password.Stamp != set.RejectedPackagePasswordStamp ? password : null;
        }
    }

    private IArchiveExtractor CreateExtractor(ArchiveSettings settings) =>
        _options.ExtractorFactory?.Invoke(settings) ?? new SevenZipExtractor(new SevenZipExtractorOptions
        {
            ToolPath = _options.SevenZipPath,
            Limits = _options.Limits,
            AvailableBytes = _options.AvailableBytes,
        });

    /// <summary>
    /// The one place a set becomes finished. It runs after every check and move, and before staging
    /// goes. If this save fails the extractor undoes the placed files, so success on disk and files
    /// in the destination cannot disagree.
    /// </summary>
    private async Task CommitAsync(Guid id, ExtractionResult result, bool deleteArchives, RunUse use)
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
            next.Volumes = [.. use.Volumes ?? next.Volumes];
            next.JobIds = [.. use.Jobs];
            ClearFailure(next);
            next.ToolMissingVolumes = [];
            next.DownloadingVolumes = [];
            next.PausedVolumes = [];
            next.QueuedVolumes = [];
            next.HeldVolumes = [];
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
            next.VolumesToDelete = [];
            if (deleteArchives)
            {
                if (next.FilesSkipped > 0)
                {
                    // A skipped entry exists only in the archive, so deleting that would lose it.
                    next.Note = $"The archives were kept because {next.FilesSkipped} files were skipped.";
                }
                else if (use.Volumes is not { } read)
                {
                    next.Note = "The archives were kept because the extraction didn't report which ones it read.";
                }
                else if (!result.FlushedToDisk)
                {
                    next.Note = "The archives were kept because the extracted files could not be confirmed as written to the disk.";
                }
                else
                {
                    // TB.S3: the set's recovery volumes rebuild what it holds, so they go with it. Only names RAR
                    // gives this set's .rev files, and none another set in this folder could also claim.
                    next.VolumesToDelete = [.. read, .. RecoveryVolumesLocked(next, read)];
                }
            }

            if (use.Unread.Count > 0)
            {
                var names = Names(use.Unread);
                var were = use.Unread.Count == 1 ? "is" : "are";
                next.Note = $"{next.Note} 7-Zip did not read {names}: not part of this archive, so not extracted, and {were} still there.".TrimStart();
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

    /// <summary>The <c>.rev</c> files on disk beside this set that are its own. Called under the gate.</summary>
    private List<string> RecoveryVolumesLocked(ArchiveSetRecord set, IReadOnlyCollection<string> read)
    {
        // Only RAR writes .rev files: a 7z, zip or split set called "name" must not take a user's own name.rev.
        var names = set.Volumes.Append(set.FirstVolumeName).Concat(read).Distinct(PathNames.Comparer).ToList();
        if (ArchiveVolumes.FindSet(names, set.FirstVolumeName) is not { } own || !ArchiveVolumes.HasRarRecoveryVolumes(own)) return [];

        string[] present;
        try
        {
            present = System.IO.Directory.Exists(set.Directory)
                ? System.IO.Directory.EnumerateFiles(set.Directory, "*.rev").Select(Path.GetFileName).OfType<string>().ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"The recovery volumes beside a set could not be listed, so they are kept: {exception.GetType().Name}");
            return [];
        }

        List<string> others;
        lock (_sync)
        {
            others = _sets.Values
                .Where(other => other.Id != set.Id && PathNames.Same(other.Directory, set.Directory))
                .Select(other => other.BaseName)
                .ToList();
        }

        return ArchiveVolumes.RecoveryVolumes(set.BaseName, present, others)
            .Where(name => !read.Contains(name, PathNames.Comparer))
            .ToList();
    }

    /// <summary>Runs after success is on disk, so a restart can finish it and nothing here can undo that success.</summary>
    private async Task FinishDeletingAsync(Guid id)
    {
        ArchiveSetRecord? record;
        lock (_sync) record = _sets.GetValueOrDefault(id);
        if (record is not { State: ArchiveSetState.Completed } || record.VolumesToDelete.Count == 0) return;

        string PathOf(string name) => Path.Combine(record.Directory, Path.GetFileName(name));

        // Before any delete: a restart that finds a completed download's file gone has to find this already saved,
        // or it reports a failure that never happened. Staying undeleted until then costs nothing, and the next start comes back here.
        try
        {
            await _service.MarkFilesRemovedAfterExtractionAsync(record.VolumesToDelete.Select(PathOf).ToList(), removed: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Kept the archives for now, because their downloads could not be marked: {exception.GetType().Name}");
            return;
        }

        var kept = new List<string>();
        foreach (var name in record.VolumesToDelete)
        {
            try
            {
                var path = PathOf(name);
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

        if (kept.Count > 0)
        {
            try
            {
                await _service.MarkFilesRemovedAfterExtractionAsync(kept.Select(PathOf).ToList(), removed: false).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Could not take the mark off a download whose archive is still here: {exception.GetType().Name}");
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

    private async Task RecordFailureAsync(Guid id, ExtractionFailureKind kind, string message, IReadOnlyList<string> missing, bool usedOwnPassword, DateTimeOffset? packageStamp, ArchiveSetRecord claimed)
    {
        ArchiveSetDto? announce = null;

        // A volume's download can finish while a run is failing, and that finish was no use to a set that was still extracting.
        // Read here, outside the gate, because a folder can be on a slow share.
        var arrived = kind == ExtractionFailureKind.MissingVolume && missing.Count > 0 && AllOnTheirWay(claimed, missing);

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

                        // A package's password arrived while a run that started without one was going on. No need to ask for what is here.
                        if (!usedOwnPassword && packageStamp is null && UsablePackagePassword(next) is not null)
                        {
                            next.State = ArchiveSetState.Waiting;
                            ClearFailure(next);
                        }

                        break;

                    case ExtractionFailureKind.MissingVolume:
                        next.ToolMissingVolumes = [.. missing];
                        next.MissingVolumes = next.MissingVolumes.Union(missing, PathNames.Comparer).ToList();
                        if (arrived && next.Wanted)
                        {
                            // Back in line. The next look at the folder tidies up what it is still waiting for.
                            next.State = ArchiveSetState.Waiting;
                            next.MissingVolumes = next.MissingVolumes.Except(missing, PathNames.Comparer).ToList();
                            ClearFailure(next);
                        }

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

    /// <summary>
    /// Whether what the tool said was absent has since turned up or is being downloaded. A volume that was already here when
    /// the run was claimed doesn't count: the tool looked at it and it didn't help, and trying again would never end.
    /// </summary>
    private bool AllOnTheirWay(ArchiveSetRecord claimed, IReadOnlyList<string> missing)
    {
        var view = ArchiveDirectoryView.Read(claimed.Directory, _service.GetSnapshot().Jobs);
        return missing.All(name =>
            !claimed.Volumes.Contains(name, PathNames.Comparer) &&
            (view.IsOnDisk(name) || view.JobsNamed(name).Any(job => job.State is not ("completed" or "failed" or "cancelled"))));
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
    /// <param name="taken">Destinations other sets of this folder have used. Sets can share a base name, as name.rar and name.part1.rar do, and a subfolder is for one set's files.</param>
    public static (string Destination, string StagingRoot) Resolve(ArchiveSetRecord set, ArchiveSettings settings, IReadOnlyCollection<string> taken)
    {
        switch (settings.DestinationMode)
        {
            case ArchiveDestinationMode.NamedSubfolder:
                return (SubfolderFor(set, taken), set.Directory);

            case ArchiveDestinationMode.ChosenFolder when !string.IsNullOrWhiteSpace(settings.ChosenFolder):
                var chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.ChosenFolder));
                return (chosen, chosen);

            default:
                return (set.Directory, set.Directory);
        }
    }

    /// <summary>A retry keeps the folder its set already has. Otherwise the first name no other set is using.</summary>
    private static string SubfolderFor(ArchiveSetRecord set, IReadOnlyCollection<string> taken)
    {
        bool Free(string folder) => !taken.Contains(folder, PathNames.Comparer);

        if (set.Destination is { } earlier && Free(earlier) &&
            PathNames.Same(Path.GetDirectoryName(earlier), set.Directory))
        {
            return earlier;
        }

        var preferred = FolderNameFor(set.BaseName);
        var stem = FolderNameFor(Path.GetFileNameWithoutExtension(set.FirstVolumeName));
        foreach (var name in new[] { preferred, stem })
        {
            var folder = Path.Combine(set.Directory, name);
            if (Free(folder)) return folder;
        }

        for (var number = 2; ; number++)
        {
            var folder = Path.Combine(set.Directory, $"{stem} ({number})");
            if (Free(folder)) return folder;
        }
    }

    /// <summary>A folder made from a downloaded file's name, so it gets the same care as a name from inside an archive.</summary>
    private static string FolderNameFor(string baseName)
    {
        var name = baseName.TrimEnd('.', ' ');
        if (ArchiveWorkingFolders.IsReserved(name)) name = "_" + name;
        return name.Length == 0 || ExtractionSafety.DescribeUnsafePath(name) is not null || name.Contains('\\') || name.Contains('/') ? "extracted" : name;
    }
}
