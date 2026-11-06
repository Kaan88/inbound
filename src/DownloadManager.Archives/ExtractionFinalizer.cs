using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

/// <summary>
/// Moves verified files from staging into the destination, and undoes its own work on failure so
/// earlier output stays as it was. Moves stay on one volume, so each file is a rename.
/// </summary>
public static class ExtractionFinalizer
{
    public static IReadOnlyList<ExtractedFile> MoveIntoDestination(
        StagingArea staging,
        StagedContent content,
        string destination,
        ExtractionConflictPolicy policy,
        bool preserveFolders,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? keepFiles = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        using var state = new State(staging, root, keepFiles);
        var results = new List<ExtractedFile>(content.Files.Count);

        try
        {
            state.EnsureDirectory(root);
            if (preserveFolders)
            {
                foreach (var folder in content.Directories) state.EnsureDirectory(Path.Combine(root, folder));
            }

            foreach (var relative in content.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(state.Place(relative, preserveFolders ? relative : Path.GetFileName(relative), policy));
                progress?.Report(new ExtractionProgress(ExtractionPhase.Finalizing, null, results.Count, content.Files.Count));
            }

            return results;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state.RollBack();
            throw ExtractionSafety.MapIoFailure(exception, "moving files into the destination");
        }
        catch
        {
            state.RollBack();
            throw;
        }
    }

    private sealed class State(StagingArea staging, string root, IReadOnlyCollection<string>? keepFiles) : IDisposable
    {
        private readonly List<Step> _steps = [];
        private readonly Stack<string> _createdDirectories = new();
        private readonly HashSet<string> _placedThisAttempt = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _keep = new((keepFiles ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        private readonly PlacementLog _log = staging.OpenPlacementLog();

        public void Dispose() => _log.Dispose();

        public ExtractedFile Place(string relativeSource, string relativeTarget, ExtractionConflictPolicy policy)
        {
            var source = Path.Combine(staging.PayloadFolder, relativeSource);
            var target = Path.GetFullPath(Path.Combine(root, relativeTarget));
            if (!target.StartsWith(ExtractionSafety.WithSeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"A file would land outside the destination: {ExtractionSafety.SafeForDisplay(relativeTarget)}");
            }

            EnsureDirectory(Path.GetDirectoryName(target)!);

            // Without folders two entries can share a name. Neither Overwrite nor Skip should
            // silently lose a file that came from this same archive.
            var effective = _placedThisAttempt.Contains(target) ? ExtractionConflictPolicy.Rename : policy;

            // Next to its archive, an entry can carry the name of one of the volumes.
            if (effective == ExtractionConflictPolicy.Overwrite && _keep.Contains(target)) effective = ExtractionConflictPolicy.Rename;
            var exists = File.Exists(target) || Directory.Exists(target);

            if (!exists)
            {
                MoveNew(relativeSource, source, target);
                return new ExtractedFile(relativeSource, target, ExtractedFileOutcome.Written);
            }

            switch (effective)
            {
                case ExtractionConflictPolicy.Skip:
                    return new ExtractedFile(relativeSource, target, ExtractedFileOutcome.Skipped);

                case ExtractionConflictPolicy.Rename:
                    var renamed = ExtractionSafety.FreeName(target);
                    MoveNew(relativeSource, source, renamed);
                    return new ExtractedFile(relativeSource, renamed, ExtractedFileOutcome.Renamed);

                default:
                    if (Directory.Exists(target))
                    {
                        throw new ExtractionException(ExtractionFailureKind.Permission, $"A folder already has this file's name: {ExtractionSafety.SafeForDisplay(relativeTarget)}");
                    }

                    Displace(target, relativeTarget);
                    _log.Record(PlacementEntry.For(relativeSource, target, relativeTarget, new FileInfo(source)));
                    File.Move(source, target);
                    _steps[^1] = _steps[^1] with { Placed = true };
                    _placedThisAttempt.Add(target);
                    return new ExtractedFile(relativeSource, target, ExtractedFileOutcome.Overwrote);
            }
        }

        public void EnsureDirectory(string directory)
        {
            var missing = new Stack<string>();
            var current = directory;
            while (!Directory.Exists(current))
            {
                missing.Push(current);
                current = Path.GetDirectoryName(current) ?? throw new DirectoryNotFoundException();
            }

            // The destination itself may be a link the user chose. A link inside it would carry
            // this archive's files somewhere else.
            for (var check = current; check.Length > root.Length && check.StartsWith(root, StringComparison.OrdinalIgnoreCase); check = Path.GetDirectoryName(check)!)
            {
                if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"A folder in the destination is a link: {ExtractionSafety.SafeForDisplay(check)}");
                }
            }

            foreach (var create in missing)
            {
                Directory.CreateDirectory(create);
                _createdDirectories.Push(create);
            }
        }

        public void RollBack()
        {
            for (var index = _steps.Count - 1; index >= 0; index--)
            {
                var step = _steps[index];
                try
                {
                    if (step.Placed)
                    {
                        // UnRAR restores the read-only attribute, and a delete refuses such a file.
                        File.SetAttributes(step.Target, FileAttributes.Normal);
                        File.Delete(step.Target);
                    }

                    if (step.Displaced is not null) File.Move(step.Displaced, step.Target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Staging holds the record of what was placed, so the next attempt can finish this.
                    staging.KeepForRecovery = true;
                }
            }

            while (_createdDirectories.Count > 0)
            {
                var directory = _createdDirectories.Pop();
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // An empty folder left behind is harmless.
                }
            }
        }

        private void MoveNew(string relativeSource, string source, string target)
        {
            _log.Record(PlacementEntry.For(relativeSource, target, null, new FileInfo(source)));
            File.Move(source, target, overwrite: false);
            _steps.Add(new Step(target, null, Placed: true));
            _placedThisAttempt.Add(target);
        }

        /// <summary>Keeping the relative path lets the next attempt put the original back after a crash.</summary>
        private void Displace(string target, string relativeTarget)
        {
            var backup = Path.Combine(staging.DisplacedFolder, relativeTarget);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(target, backup);
            _steps.Add(new Step(target, backup, Placed: false));
        }

        private sealed record Step(string Target, string? Displaced, bool Placed);
    }
}
