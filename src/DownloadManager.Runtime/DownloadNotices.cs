using DownloadManager.Contracts;
using DownloadManager.Runtime.Archives;

namespace DownloadManager.Runtime;

public enum DownloadNoticeKind
{
    /// <summary>Downloads finished. Follows the notification and sound settings for completions.</summary>
    Completed,

    /// <summary>A download failed. Always shown, with the failure sound if one is set.</summary>
    Failed,

    /// <summary>A lone set's outcome, or a set in a package that stopped. Always shown, with no sound.</summary>
    Extraction,

    /// <summary>A package's archives extracted, some time after its downloads. Follows the notification setting, with no sound.</summary>
    PackageExtractionCompleted,
}

public sealed record DownloadNotice(DownloadNoticeKind Kind, string Title, string Message);

/// <summary>
/// Decides what the user is told when downloads and extractions end. A download on its own, or a package
/// of one file, is told about file by file. A package of two or more gets one notice once nothing in it
/// is still working, covering its files and its archive sets together. A file stopped or cancelled counts
/// as no longer working, so the rest of the package isn't left waiting for it, and the notice says how many
/// of its files arrived. Resuming and finishing a stopped file later ends in a notice of its own.
/// <para>
/// A failure in a package is told at once, because the user may want to act, but only the first one. The
/// rest are counted into the closing notice, so a failing package says at most two things however many
/// files it has. Only memory is kept: after a restart a package starts again from nothing told.
/// </para>
/// </summary>
public sealed class DownloadNotices : IDisposable
{
    private readonly DownloadManagerService _service;
    private readonly ArchiveCoordinator? _archives;
    private readonly Action<DownloadNotice> _tell;
    private readonly Action<string>? _log;
    private readonly object _sync = new();

    /// <summary>Packages with something still to say. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, PackageState> _packages = [];

    /// <summary>The latest completion of each package that the archive side has looked at. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _examined = [];

    /// <summary>Each outcome of each attempt once: progress and later saves repeat the same state. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, string> _toldSets = [];

    /// <summary>What each package last said when it closed with files stopped. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, string> _toldEarly = [];

    private sealed class PackageState
    {
        public bool DownloadsFinished { get; set; }

        public bool SawFailure { get; set; }

        public bool FailureTold { get; set; }

        /// <summary>How many files the package had at its last failure. Fewer now means someone is removing them.</summary>
        public int JobsAtFailure { get; set; }

        /// <summary>A file finished or was cancelled, so the package can close without every file having finished.</summary>
        public bool MayCloseEarly { get; set; }

        /// <summary>How many files the package had then. Fewer now means someone is removing them.</summary>
        public int JobsWhenMarked { get; set; }

        public Dictionary<Guid, SetOutcome> Sets { get; } = [];
    }

    private sealed record SetOutcome(string Name, bool Completed, string Words);

    private sealed record PackageLook(string Name, IReadOnlyList<JobDto> Jobs, bool Busy);

    /// <param name="archives">Null when extraction didn't start: nothing then holds a package's notice back.</param>
    /// <param name="tell">Must not block. Called on whichever thread brought the news.</param>
    public DownloadNotices(DownloadManagerService service, ArchiveCoordinator? archives, Action<DownloadNotice> tell, Action<string>? log = null)
    {
        _service = service;
        _archives = archives;
        _tell = tell;
        _log = log;

        // What finished before now is history, so it holds nothing back.
        foreach (var finished in service.GetCompletedPackages()) _examined[finished.PackageId] = finished.CompletedAt;

        service.JobChanged += OnJobChanged;
        if (archives is null)
        {
            service.PackageCompleted += OnPackageFinished;
        }
        else
        {
            archives.PackageExamined += OnPackageFinished;
            archives.Changed += OnArchiveChanged;
        }
    }

    public void Dispose()
    {
        _service.JobChanged -= OnJobChanged;
        _service.PackageCompleted -= OnPackageFinished;
        if (_archives is not null)
        {
            _archives.PackageExamined -= OnPackageFinished;
            _archives.Changed -= OnArchiveChanged;
        }
    }

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        if (e.ChangeType == "progress") return;
        Guarded(() =>
        {
            var job = e.Job;

            // In a package of any size, the package's own completion speaks for it: a package built one add
            // at a time can have only this file so far.
            if (e.ChangeType == "completed" && _service.FindGroupOf(job.Id) is null) Tell(DownloadNoticeKind.Completed, "Download complete", job.FileName);

            // A stop isn't marked: it can be undone, and stopping again would say the same thing twice.
            if (e.ChangeType is "completed" or "cancelled" && _service.FindPackageOf(job.Id) is { } member) MarkMayCloseEarly(member);
            if (e.ChangeType == "failed")
            {
                if (_service.FindPackageOf(job.Id) is { } package) MemberFailed(package, job);
                else Tell(DownloadNoticeKind.Failed, "Download failed", FailureWords(job));
            }

            SettleAll();
        });
    }

    private void MarkMayCloseEarly(Guid packageId)
    {
        var count = Look(packageId)?.Jobs.Count ?? 0;
        lock (_sync)
        {
            var state = StateOf(packageId);
            state.MayCloseEarly = true;
            state.JobsWhenMarked = count;
        }
    }

    private void MemberFailed(Guid packageId, JobDto job)
    {
        var look = Look(packageId);
        bool tellNow;
        lock (_sync)
        {
            var state = StateOf(packageId);
            state.SawFailure = true;
            state.JobsAtFailure = look?.Jobs.Count ?? 0;

            // When this was the last thing going, the closing notice below is the only one.
            tellNow = look is { Busy: true } && !state.FailureTold;
            if (tellNow) state.FailureTold = true;
        }

        if (tellNow) Tell(DownloadNoticeKind.Failed, "Download failed", FailureWords(job));
    }

    private void OnPackageFinished(object? sender, PackageCompletedEventArgs e) => Guarded(() =>
    {
        lock (_sync)
        {
            _examined[e.PackageId] = e.CompletedAt;
            _toldEarly.Remove(e.PackageId);
        }

        var jobs = e.JobIds.Select(_service.GetJob).OfType<JobDto>().ToList();
        if (jobs is [var only])
        {
            Tell(DownloadNoticeKind.Completed, "Download complete", only.FileName);
            return;
        }

        if (jobs.Count == 0) return;
        lock (_sync) StateOf(e.PackageId).DownloadsFinished = true;
        Settle(e.PackageId);
    });

    private void OnArchiveChanged(object? sender, ArchiveChangedDto change) => Guarded(() =>
    {
        if (change.RemovedId is { } gone)
        {
            lock (_sync)
            {
                _toldSets.Remove(gone);
                foreach (var state in _packages.Values) state.Sets.Remove(gone);
            }
        }

        if (change.Set is { State: "completed" or "failed" } set && IsNewOutcome(set))
        {
            if (set.PackageId is { } package && _service.GetDownloadGroups().Any(group => group.Id == package && group.JobIds.Count > 1))
            {
                lock (_sync) StateOf(package).Sets[set.Id] = new SetOutcome(set.Name, set.State == "completed", set.StateText);
            }
            else
            {
                Tell(DownloadNoticeKind.Extraction, set.State == "completed" ? "Extraction complete" : "Extraction stopped", $"{set.Name}: {set.StateText}");
            }
        }

        SettleAll();
    });

    private bool IsNewOutcome(ArchiveSetDto set)
    {
        var outcome = $"{set.Attempts}:{set.State}:{set.FailureKind}";
        lock (_sync)
        {
            if (_toldSets.TryGetValue(set.Id, out var told) && told == outcome) return false;
            _toldSets[set.Id] = outcome;
            return true;
        }
    }

    /// <summary>The caller holds <see cref="_sync"/>.</summary>
    private PackageState StateOf(Guid packageId)
    {
        if (!_packages.TryGetValue(packageId, out var state)) _packages[packageId] = state = new PackageState();
        return state;
    }

    private void SettleAll()
    {
        List<Guid> waiting;
        lock (_sync)
        {
            if (_packages.Count == 0) return;
            waiting = _packages.Keys.ToList();
        }

        foreach (var package in waiting) Settle(package);
    }

    /// <summary>Says the package's closing notice if nothing in it is working any more. Taking the state out is what makes it once.</summary>
    private void Settle(Guid packageId)
    {
        // Read before the lock: the service and the coordinator have locks of their own.
        var look = Look(packageId);
        PackageState? state;
        lock (_sync)
        {
            if (!_packages.TryGetValue(packageId, out state) || look is { Busy: true }) return;
            _packages.Remove(packageId);
        }

        // Null is a package deleted meanwhile, which has nothing to say.
        if (look is null)
        {
            lock (_sync) _toldEarly.Remove(packageId);
            return;
        }

        if (Closing(look, state) is { } notice && IsNewsIfEarly(packageId, look, state, notice)) Tell(notice);
    }

    /// <summary>A file's completion can reach here just after a stop has already closed the package with the same count.</summary>
    private bool IsNewsIfEarly(Guid packageId, PackageLook look, PackageState state, DownloadNotice notice)
    {
        if (notice.Kind != DownloadNoticeKind.Completed || !ClosesEarly(look, state)) return true;
        lock (_sync)
        {
            if (_toldEarly.TryGetValue(packageId, out var told) && told == notice.Message) return false;
            _toldEarly[packageId] = notice.Message;
            return true;
        }
    }

    /// <summary>Some files arrived and the rest were stopped. Cancelling the whole package, or removing files, is something the user knows about.</summary>
    private static bool ClosesEarly(PackageLook look, PackageState state)
    {
        var arrived = look.Jobs.Count(job => job.State == "completed");
        return !state.DownloadsFinished && state.MayCloseEarly && arrived > 0 && arrived < look.Jobs.Count && look.Jobs.Count >= state.JobsWhenMarked &&
               look.Jobs.All(job => job.CancelledByPackageId is null);
    }

    /// <summary>What the package is doing now. Null when it, or every file in it, has gone.</summary>
    private PackageLook? Look(Guid packageId)
    {
        var group = _service.GetDownloadGroups().FirstOrDefault(candidate => candidate.Id == packageId);
        if (group is null) return null;
        var jobs = group.JobIds.Select(_service.GetJob).OfType<JobDto>().ToList();
        if (jobs.Count == 0) return null;

        // A file being removed reads as cancelled first, and is gone a moment later.
        var busy = jobs.Any(job => job.QueueRetryAt is not null || job.State is not ("completed" or "failed" or "cancelled" or "paused") || _service.IsBeingRemoved(job.Id)) ||
                   (_archives?.List().Any(set => set.PackageId == packageId && set.IsStillWorking()) ?? false) ||
                   (jobs.All(job => job.State == "completed") && !CompletionWasExamined(packageId));
        return new PackageLook(group.Name, jobs, busy);
    }

    /// <summary>
    /// A package whose files are all done still has its completion to come, up to a settling delay later. Until
    /// the archive side has seen it, an extraction can't tell whether more will follow.
    /// </summary>
    private bool CompletionWasExamined(Guid packageId)
    {
        var latest = _service.GetCompletedPackages().FirstOrDefault(package => package.PackageId == packageId);
        if (latest is null) return false;
        lock (_sync) return _examined.TryGetValue(packageId, out var examined) && examined >= latest.CompletedAt;
    }

    private static DownloadNotice? Closing(PackageLook look, PackageState state)
    {
        var total = look.Jobs.Count;

        // A user removing or cancelling files knows, and a count taken partway through would be wrong anyway.
        var stoppedByUser = total < state.JobsAtFailure || look.Jobs.Any(job => job.State == "cancelled");
        var failed = state.SawFailure && !stoppedByUser ? look.Jobs.Where(job => job.State == "failed").ToList() : [];
        var extracted = state.Sets.Values.Count(set => set.Completed);
        var stopped = state.Sets.Count - extracted;
        var stoppedWords = $"{stopped} of {state.Sets.Count} archives {(stopped == 1 ? "wasn't" : "weren't")} extracted";
        var closesEarly = ClosesEarly(look, state);
        var files = closesEarly ? $"{look.Jobs.Count(job => job.State == "completed")} of {total} files" : $"{total} files";

        if (failed.Count > 0)
        {
            var words = $"{look.Name}: {failed.Count} of {total} downloads failed";
            if (failed is [var only] && !state.FailureTold) words += $". {FailureWords(only)}";
            if (stopped > 0) words += $". {stoppedWords}";
            return new DownloadNotice(DownloadNoticeKind.Failed, "Download failed", words);
        }

        if (stopped > 0)
        {
            var words = state.DownloadsFinished || closesEarly ? $"{look.Name}: {files} downloaded, but {stoppedWords}"
                : state.Sets.Count == 1 ? $"{state.Sets.Values.First().Name}: {state.Sets.Values.First().Words}"
                : $"{look.Name}: {stoppedWords}";
            return new DownloadNotice(DownloadNoticeKind.Extraction, "Extraction stopped", words);
        }

        if (state.DownloadsFinished || closesEarly)
        {
            var words = extracted > 0 ? $"{look.Name}: {files}, {Archives(extracted)} extracted" : $"{look.Name}: {files}";
            return new DownloadNotice(DownloadNoticeKind.Completed, "Download complete", words);
        }

        if (extracted == 0) return null;
        return new DownloadNotice(DownloadNoticeKind.PackageExtractionCompleted, "Extraction complete", extracted == 1
            ? $"{state.Sets.Values.First().Name}: {state.Sets.Values.First().Words}"
            : $"{look.Name}: {Archives(extracted)} extracted");
    }

    private static string Archives(int count) => count == 1 ? "1 archive" : $"{count} archives";

    private static string FailureWords(JobDto job) => $"{job.FileName}: {job.Failure?.Message}";

    private void Tell(DownloadNoticeKind kind, string title, string message) => Tell(new DownloadNotice(kind, title, message));

    private void Tell(DownloadNotice notice) => Guarded(() => _tell(notice));

    /// <summary>Telling is never worth failing what brought the news.</summary>
    private void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _log?.Invoke($"A download notice failed: {exception.GetType().Name}");
        }
    }
}
