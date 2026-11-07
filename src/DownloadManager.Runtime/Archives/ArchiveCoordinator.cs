using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DownloadManager.Archives;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;

namespace DownloadManager.Runtime.Archives;

public sealed record ArchiveCoordinatorOptions
{
    /// <summary>One at a time is the default: extraction is heavy on the disk, and nothing else in the Host waits for it.</summary>
    public int MaxConcurrentExtractions { get; init; } = 1;

    /// <summary>Null extracts with UnRAR, found through the settings' path and then the machine.</summary>
    public Func<ArchiveSettings, IArchiveExtractor>? ExtractorFactory { get; init; }

    /// <summary>Null for the machine's own. Empty skips looking in Program Files.</summary>
    public IReadOnlyList<string>? ProgramFilesDirectories { get; init; }

    /// <summary>Null for this process's PATH. Empty skips looking on PATH.</summary>
    public string? PathVariable { get; init; }

    public ExtractionLimits Limits { get; init; } = new();

    /// <summary>Null asks the volume.</summary>
    public Func<string, long?>? AvailableBytes { get; init; }

    public TimeSpan ShutdownWait { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The durable life of archive sets: which exist, what each waits for, and running extraction one
/// set at a time. It sees local files and jobs and nothing about where they came from, so a
/// provider's download and an ordinary one are the same to it.
/// <para>
/// <see cref="_gate"/> orders state changes and their saves. Reading a folder and running the tool
/// both happen outside it. A set's state on disk is the claim: <see cref="ArchiveSetState.Extracting"/>
/// is saved before the tool starts, and only a recorded success counts as finished.
/// </para>
/// </summary>
public sealed partial class ArchiveCoordinator : IAsyncDisposable, IPackagePasswordKeeper
{
    private static readonly JsonSerializerOptions CompareOptions = new();

    private readonly DownloadManagerService _service;
    private readonly IArchiveStore _store;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly ArchiveCoordinatorOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    /// <summary>Guarded by <see cref="_sync"/>. Records are replaced, never changed in place.</summary>
    private readonly Dictionary<Guid, ArchiveSetRecord> _sets = [];

    /// <summary>Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, ArchivePackagePassword> _packagePasswords = [];

    /// <summary>Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, RunningExtraction> _running = [];

    /// <summary>Sets on disk whose documents couldn't be read at the start. Their ids stay out of use until a later start can read them.</summary>
    private readonly HashSet<Guid> _unreadable = [];
    private readonly ConcurrentDictionary<Guid, ExtractionProgress> _live = new();
    private readonly Channel<Trigger> _triggers = Channel.CreateUnbounded<Trigger>(new UnboundedChannelOptions { SingleReader = true });
    private ArchiveSettings _settings = new();
    private Task _worker = Task.CompletedTask;
    private volatile bool _stopping;
    private int _shutdownStarted;

    /// <summary>Attaches to the service at once, so this must be built before the service initializes.</summary>
    public ArchiveCoordinator(
        DownloadManagerService service,
        IArchiveStore store,
        Action<string>? log = null,
        ArchiveCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _service = service;
        _store = store;
        _log = log;
        _options = options ?? new ArchiveCoordinatorOptions();
        _time = timeProvider ?? TimeProvider.System;
        _service.JobChanged += OnJobChanged;
        _service.PackageCompleted += OnPackageCompleted;
    }

    /// <summary>A set's whole state changed. Handlers run on the caller's thread and must not block.</summary>
    public event EventHandler<ArchiveChangedDto>? Changed;

    /// <summary>
    /// A newly finished package's sets are up to date, so one that will extract now says so. Raised on the
    /// worker, after every earlier job's completion was counted. Handlers must not block. A package only
    /// looked at again by a start doesn't raise it.
    /// </summary>
    public event EventHandler<PackageCompletedEventArgs>? PackageExamined;

    private sealed record RunningExtraction(CancellationTokenSource Cancellation)
    {
        public Task Task { get; set; } = Task.CompletedTask;

        public void Stop()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It finished first.
            }
        }
    }

    private abstract record Trigger;

    private sealed record JobTrigger(JobDto Job, string Change) : Trigger;

    private sealed record PackageTrigger(PackageCompletedEventArgs Package, bool IsNews = false) : Trigger;

    private sealed record WakeTrigger : Trigger;

    private sealed record ResumeDeletesTrigger(Guid SetId) : Trigger;

    private sealed record TidyTrigger : Trigger;

    /// <summary>
    /// Recovers from whatever a crash left, then starts work. A set interrupted while extracting
    /// goes back to waiting and runs again: nothing it did before the crash counts.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            settings = new ArchiveSettings { TrackSince = _time.GetUtcNow() };
            await _store.SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        var sets = await _store.LoadSetsAsync(cancellationToken).ConfigureAwait(false);
        var passwords = await _store.LoadPackagePasswordsAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = _service.GetSnapshot();
        var packageIds = _service.GetDownloadGroups().Select(group => group.Id).ToHashSet();
        var jobIds = snapshot.Jobs.Select(job => job.Id).ToHashSet();
        lock (_sync) _unreadable.UnionWith(_store.UnreadableSets);

        var leftInPlace = await RecoverStagingAsync(sets).ConfigureAwait(false);

        foreach (var password in passwords.Where(password => !packageIds.Contains(password.PackageId)))
        {
            await _store.DeletePackagePasswordAsync(password.PackageId, cancellationToken).ConfigureAwait(false);
        }

        var resumeDeletes = new List<Guid>();
        lock (_sync)
        {
            _settings = settings;
            foreach (var password in passwords.Where(password => packageIds.Contains(password.PackageId))) _packagePasswords[password.PackageId] = password;
        }

        foreach (var set in sets)
        {
            // A removed set is remembered only while a download could list it again.
            if (set.Removed && !set.SeenJobs.Keys.Any(jobIds.Contains))
            {
                await _store.DeleteSetAsync(set.Id, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var current = set;

            // Keys used to be folded for padding, so a saved part01 could carry part1's key.
            var key = ArchiveSetIdentity.KeyFor(set.Directory, set.FirstVolumeName);
            if (set.State == ArchiveSetState.Extracting || !string.Equals(set.Key, key, StringComparison.Ordinal))
            {
                current = set.Clone();
                current.Key = key;
                if (set.State == ArchiveSetState.Extracting)
                {
                    current.State = ArchiveSetState.Waiting;
                    current.Note = "Extraction was interrupted, so it runs again.";
                }

                current.UpdatedAt = _time.GetUtcNow();
                await _store.SaveSetAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (leftInPlace.TryGetValue(current.Id, out var kept) && kept > 0)
            {
                current = current.Clone();
                current.Note = $"{current.Note} {kept} {(kept == 1 ? "file" : "files")} from the interrupted run changed since then and {(kept == 1 ? "was" : "were")} left in place.".TrimStart();
                await _store.SaveSetAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (current.State == ArchiveSetState.Completed && current.VolumesToDelete.Count > 0) resumeDeletes.Add(current.Id);
            lock (_sync) _sets[current.Id] = current;
        }

        _worker = Task.Run(RunWorkerAsync, CancellationToken.None);

        // First: a set left waiting by a download removed before this start must not run now.
        _triggers.Writer.TryWrite(new TidyTrigger());
        foreach (var id in resumeDeletes) _triggers.Writer.TryWrite(new ResumeDeletesTrigger(id));

        // Finishes a crash separated from their intent. Each is counted once, by job and time.
        foreach (var job in snapshot.Jobs.Where(job => job.State == "completed")) _triggers.Writer.TryWrite(new JobTrigger(job, "completed"));
        foreach (var package in _service.GetCompletedPackages()) _triggers.Writer.TryWrite(new PackageTrigger(package));
        Wake();
    }

    /// <summary>
    /// Stops any run and its process tree. A stopped run is not a failure of the archive: it goes
    /// back to waiting, on disk, and runs again at the next start.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        _service.JobChanged -= OnJobChanged;
        _service.PackageCompleted -= OnPackageCompleted;

        RunningExtraction[] running;
        lock (_sync)
        {
            _stopping = true;
            running = _running.Values.ToArray();
        }

        foreach (var run in running) run.Stop();
        _triggers.Writer.TryComplete();
        try
        {
            await Task.WhenAny(Task.WhenAll(running.Select(run => run.Task).Append(_worker)), Task.Delay(_options.ShutdownWait, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The next start recovers whatever this did not finish.
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        if (e.ChangeType == "progress") return;
        if (!ArchiveSetIdentity.LooksLikeVolume(Path.GetFileName(e.Job.FinalFilePath)))
        {
            // Not an archive now, but it may have been one under the name it had when listed.
            lock (_sync)
            {
                if (!_sets.Values.Any(set => set.JobIds.Contains(e.Job.Id))) return;
            }
        }

        _triggers.Writer.TryWrite(new JobTrigger(e.Job, e.ChangeType));
    }

    private void OnPackageCompleted(object? sender, PackageCompletedEventArgs e) => _triggers.Writer.TryWrite(new PackageTrigger(e, IsNews: true));

    private void Wake() => _triggers.Writer.TryWrite(new WakeTrigger());

    private async Task RunWorkerAsync()
    {
        await foreach (var trigger in _triggers.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_stopping) continue;
            try
            {
                switch (trigger)
                {
                    case JobTrigger job:
                        await HandleJobAsync(job.Job, job.Change).ConfigureAwait(false);
                        break;
                    case PackageTrigger package:
                        try
                        {
                            await HandlePackageAsync(package.Package).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (package.IsNews) RaisePackageExamined(package.Package);
                        }

                        break;
                    case ResumeDeletesTrigger resume:
                        await FinishDeletingAsync(resume.SetId).ConfigureAwait(false);
                        await DropSetsWithoutDownloadsAsync().ConfigureAwait(false);
                        break;
                    case TidyTrigger:
                        await DropSetsWithoutDownloadsAsync().ConfigureAwait(false);
                        break;
                }

                await StartReadySetsAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A save that failed here is counted again by the next trigger or start.
                _log?.Invoke($"Archive work failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private void RaisePackageExamined(PackageCompletedEventArgs package)
    {
        try
        {
            PackageExamined?.Invoke(this, package);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"A package handler failed: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// A package saying it finished adds nothing a job's own completion did not, and can say so more
    /// than once. Each job's completion is counted once, by job and time, whichever trigger brings it.
    /// </summary>
    private async Task HandlePackageAsync(PackageCompletedEventArgs package)
    {
        foreach (var id in package.JobIds)
        {
            if (_service.GetJob(id) is { State: "completed" } job && ArchiveSetIdentity.LooksLikeVolume(Path.GetFileName(job.FinalFilePath)))
            {
                await HandleJobAsync(job, "completed").ConfigureAwait(false);
            }
        }
    }

    private async Task HandleJobAsync(JobDto job, string change)
    {
        var jobs = _service.GetSnapshot().Jobs;
        Guid? target = null;
        var name = Path.GetFileName(job.FinalFilePath);
        if (change != "removed" && ArchiveSetIdentity.LooksLikeVolume(name) && Path.GetDirectoryName(job.FinalFilePath) is { Length: > 0 } directory)
        {
            var view = ArchiveDirectoryView.Read(directory, jobs);
            if (view.FindSet(name) is { } set) target = await TouchAsync(view, set, job, change).ConfigureAwait(false);
        }

        // A job that left a set, by being removed or by its name changing, changes what that set waits for.
        List<Guid> affected;
        lock (_sync) affected = _sets.Values.Where(set => set.Id != target && set.JobIds.Contains(job.Id)).Select(set => set.Id).ToList();
        foreach (var id in affected) await RefreshAsync(id, jobs).ConfigureAwait(false);
    }

    /// <summary>The record for a set in this view, by its key or by the name it had to guess. Null when there is none yet.</summary>
    private ArchiveSetRecord? RecordFor(ArchiveDirectoryView view, ArchiveVolumeSet set, string key)
    {
        lock (_sync)
        {
            return _sets.Values.FirstOrDefault(record => record.Key == key)
                ?? _sets.Values.FirstOrDefault(record =>
                    string.Equals(ArchiveSetIdentity.NormalizeDirectory(record.Directory), view.Directory, StringComparison.OrdinalIgnoreCase) &&
                    ReferenceEquals(view.FindFor(record), set));
        }
    }

    /// <summary>
    /// A new record's id. It comes from the key, so that a set whose document can't be read now is recognised,
    /// unless an older record that later learned its real name is still using that id.
    /// </summary>
    private Guid NewIdFor(string key)
    {
        lock (_sync)
        {
            var id = ArchiveSetIdentity.IdFor(key);
            for (var attempt = 2; _sets.TryGetValue(id, out var holder) && holder.Key != key; attempt++)
            {
                id = ArchiveSetIdentity.IdFor($"{key}#{attempt}");
            }

            return id;
        }
    }

    private bool IsUnreadable(Guid id)
    {
        lock (_sync) return _unreadable.Contains(id);
    }

    private async Task<Guid?> TouchAsync(ArchiveDirectoryView view, ArchiveVolumeSet set, JobDto job, string change)
    {
        var key = ArchiveSetIdentity.KeyFor(view.Directory, set);
        set = view.FindByKey(key) ?? set;
        var now = _time.GetUtcNow();
        var isCompletion = change == "completed" && job.State == "completed";
        var completedAt = job.CompletedAt ?? now;
        ArchiveSetDto? announce = null;
        Guid id;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ArchiveSettings settings;
            lock (_sync) settings = _settings;
            var existing = RecordFor(view, set, key);
            id = existing?.Id ?? NewIdFor(key);

            // Its record is there and can't be read right now, so this is not a new set. The next start counts this again.
            if (existing is null && IsUnreadable(id)) return null;
            if (existing is null && (isCompletion ? completedAt < settings.TrackSince : change != "added")) return id;

            var next = existing?.Clone() ?? NewRecord(id, key, view, set, now);
            if (isCompletion) CountCompletion(next, job.Id, completedAt, settings);
            if (next.Removed) return id;
            Apply(next, view, set);
            if (existing is not null && SameContent(existing, next)) return id;

            await SaveLockedAsync(next).ConfigureAwait(false);
            announce = Map(next);
        }
        finally
        {
            _gate.Release();
        }

        Announce(announce);
        return id;
    }

    /// <summary>Counts one finished download. This is the durable intent: once saved, a crash cannot lose it.</summary>
    private static void CountCompletion(ArchiveSetRecord next, Guid jobId, DateTimeOffset completedAt, ArchiveSettings settings)
    {
        if (next.SeenJobs.TryGetValue(jobId, out var seen) && seen >= completedAt) return;
        next.SeenJobs[jobId] = completedAt;

        if (next.Removed)
        {
            if (next.RemovedAt is { } removedAt && completedAt <= removedAt) return;
            next.Removed = false;
            next.RemovedAt = null;
            next.Wanted = false;
            next.State = ArchiveSetState.Downloaded;
            ClearFailure(next);
        }

        // Turning the setting on is not a request to go back over what finished before it.
        var auto = settings.AutoExtract && settings.AutoExtractSince is { } since && completedAt >= since;
        switch (next.State)
        {
            case ArchiveSetState.Downloading or ArchiveSetState.Downloaded or ArchiveSetState.Waiting:
                if (auto) next.Wanted = true;
                break;

            // A new copy of a volume is worth another look. Finished sets and those a user
            // stopped stay as they are; those take an explicit retry.
            case ArchiveSetState.Failed when next.Wanted && next.FailureKind is ExtractionFailureKind.MissingVolume or ExtractionFailureKind.Corrupted:
                next.State = ArchiveSetState.Waiting;
                ClearFailure(next);
                break;
        }
    }

    private static void ClearFailure(ArchiveSetRecord record)
    {
        record.FailureKind = null;
        record.FailureMessage = null;
    }

    private ArchiveSetRecord NewRecord(Guid id, string key, ArchiveDirectoryView view, ArchiveVolumeSet set, DateTimeOffset now) => new()
    {
        Id = id,
        Key = key,
        Directory = view.Directory,
        FirstVolumeName = ArchiveSetIdentity.FirstName(set),
        BaseName = set.BaseName,
        State = ArchiveSetState.Downloading,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Brings a record in line with its folder. What a run has claimed or finished is left alone.</summary>
    private void Apply(ArchiveSetRecord next, ArchiveDirectoryView view, ArchiveVolumeSet set)
    {
        if (next.State is ArchiveSetState.Completed or ArchiveSetState.Extracting) return;

        next.Directory = view.Directory;
        next.FirstVolumeName = ArchiveSetIdentity.FirstName(set);
        next.Key = ArchiveSetIdentity.KeyFor(view.Directory, set);
        next.BaseName = set.BaseName;

        var present = new List<string>();
        var downloading = new List<string>();
        var failed = new List<string>();
        var missing = new List<string>(set.MissingVolumes);
        var jobIds = new List<Guid>();
        foreach (var member in set.Members)
        {
            var jobs = view.JobsNamed(member);
            jobIds.AddRange(jobs.Select(job => job.Id));
            if (jobs.Any(job => job.State is not ("completed" or "failed" or "cancelled"))) downloading.Add(member);
            else if (view.IsOnDisk(member)) present.Add(member);
            else if (jobs.Any(job => job.State is "failed" or "cancelled")) failed.Add(member);
            else missing.Add(member);
        }

        foreach (var named in next.ToolMissingVolumes)
        {
            if (!view.IsOnDisk(named) && !downloading.Contains(named, StringComparer.OrdinalIgnoreCase) && !missing.Contains(named, StringComparer.OrdinalIgnoreCase)) missing.Add(named);
        }

        next.Volumes = present;
        next.DownloadingVolumes = downloading;
        next.FailedVolumes = failed;
        next.MissingVolumes = missing;
        next.JobIds = jobIds.Distinct().ToList();
        // Any size of package: a password can be kept for one that has only this download so far.
        if (next.JobIds.Select(_service.FindGroupOf).FirstOrDefault(package => package is not null) is { } packageId) next.PackageId = packageId;

        switch (next.State)
        {
            case ArchiveSetState.Failed:
                // The volume the tool said was absent has turned up, or is on its way.
                if (next.FailureKind == ExtractionFailureKind.MissingVolume && next.Wanted && next.ToolMissingVolumes.Count > 0 &&
                    next.ToolMissingVolumes.All(named => view.IsOnDisk(named) || downloading.Contains(named, StringComparer.OrdinalIgnoreCase)))
                {
                    next.State = ArchiveSetState.Waiting;
                    ClearFailure(next);
                }

                break;

            default:
                next.State = next.Wanted ? ArchiveSetState.Waiting
                    : downloading.Count > 0 || present.Count == 0 ? ArchiveSetState.Downloading
                    : ArchiveSetState.Downloaded;
                break;
        }
    }

    private async Task RefreshAsync(Guid id, IReadOnlyList<JobDto> jobs)
    {
        ArchiveSetRecord? record;
        lock (_sync) record = _sets.GetValueOrDefault(id);
        if (record is null || record.Removed) return;

        var view = ArchiveDirectoryView.Read(record.Directory, jobs);
        var set = view.FindFor(record);
        ArchiveSetDto? announce = null;
        Guid? gone = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync) record = _sets.GetValueOrDefault(id);
            if (record is null || record.Removed || record.State == ArchiveSetState.Extracting) return;

            if (LostItsDownloads(record))
            {
                await ForgetLockedAsync(id).ConfigureAwait(false);
                gone = id;
                return;
            }

            if (record.State == ArchiveSetState.Completed) return;

            var next = record.Clone();
            if (set is not null)
            {
                Apply(next, view, set);
            }
            else if (!next.Wanted && next.ProtectedPassword is null)
            {
                // Nothing here and nobody waiting for it: the download it was listed for has gone.
                await ForgetLockedAsync(id).ConfigureAwait(false);
                gone = id;
            }
            else
            {
                next.Volumes = [];
                next.DownloadingVolumes = [];
                next.FailedVolumes = [];
                next.JobIds = [];
                next.MissingVolumes = [next.FirstVolumeName];
            }

            if (gone is null && !SameContent(record, next))
            {
                await SaveLockedAsync(next).ConfigureAwait(false);
                announce = Map(next);
            }
        }
        finally
        {
            _gate.Release();
            if (gone is { } removed) AnnounceRemoved(removed);
        }

        Announce(announce);
    }

    /// <summary>
    /// Whether every download behind a set has been removed. The list holds a set for its downloads, so it goes with them,
    /// finished or not: its files stay wherever they are, and the same file can be extracted again. A set picked from the disk
    /// never had a download and is unaffected, because its record is the only place its outcome shows. A run still going, or
    /// archives still to be deleted, finishes first. The caller holds the gate, so nothing can list a new download meanwhile.
    /// </summary>
    private bool LostItsDownloads(ArchiveSetRecord set)
    {
        if (set.State == ArchiveSetState.Extracting || set.VolumesToDelete.Count > 0) return false;
        var had = set.JobIds.Concat(set.SeenJobs.Keys).ToList();
        return had.Count > 0 && had.All(job => _service.GetJob(job) is null);
    }

    /// <summary>Only the worker calls this. For what no job event can report: a run that was still going, or a removal before this start.</summary>
    private async Task DropSetsWithoutDownloadsAsync()
    {
        var gone = new List<Guid>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ArchiveSetRecord> sets;
            lock (_sync) sets = _sets.Values.Where(set => !set.Removed).ToList();
            foreach (var set in sets.Where(LostItsDownloads))
            {
                await ForgetLockedAsync(set.Id).ConfigureAwait(false);
                gone.Add(set.Id);
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var id in gone) AnnounceRemoved(id);
    }

    /// <summary>The whole record goes, with any password in it. Never touches a download's or an extraction's files. The caller holds the gate.</summary>
    private async Task ForgetLockedAsync(Guid id)
    {
        await _store.DeleteSetAsync(id).ConfigureAwait(false);
        lock (_sync) _sets.Remove(id);
    }

    private static bool SameContent(ArchiveSetRecord left, ArchiveSetRecord right) =>
        JsonSerializer.Serialize(left, CompareOptions) == JsonSerializer.Serialize(right, CompareOptions);

    /// <summary>Durable first: memory only ever shows what a restart would also find. The caller holds the gate.</summary>
    private async Task SaveLockedAsync(ArchiveSetRecord next)
    {
        next.UpdatedAt = _time.GetUtcNow();
        await _store.SaveSetAsync(next, CancellationToken.None).ConfigureAwait(false);
        lock (_sync) _sets[next.Id] = next;
    }

    private void Announce(ArchiveSetDto? set)
    {
        if (set is not null) Raise(new ArchiveChangedDto { Set = set });
    }

    private void AnnounceRemoved(Guid id) => Raise(new ArchiveChangedDto { RemovedId = id });

    private void Raise(ArchiveChangedDto change)
    {
        try
        {
            Changed?.Invoke(this, change);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"An archive change handler failed: {exception.GetType().Name}");
        }
    }
}
