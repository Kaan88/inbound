using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// A package reached the point where every job in it has finished.
/// <paramref name="CompletedAt"/> with <paramref name="PackageId"/> identifies this completion:
/// it does not change across restarts, and a later completion of the same package has a later time.
/// </summary>
public sealed record PackageCompletedEventArgs(
    Guid PackageId,
    string Name,
    string? Key,
    IReadOnlyList<Guid> JobIds,
    DateTimeOffset CompletedAt);

public sealed partial class DownloadManagerService
{
    private const int MaxPackages = 1000;

    private sealed record PendingPackage(string? Key, string? Name);

    /// <summary>Jobs whose package could not be saved. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, PendingPackage> _pendingPackages = new();
    /// <summary>When a client last added a file to each package one at a time. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _packageLastJoinedAt = new();
    private bool _packageCheckRequested;
    private bool _packageCheckRunning;
    private bool _packageSettleCheckPending;
    private int _packageChecksHeld;
    private Task _packageCheckWorker = Task.CompletedTask;

    /// <summary>
    /// How long a package built one add at a time must go without a new file before it can be
    /// called finished. Those clients can't say when they are done, and a small first file can
    /// finish before the next add arrives.
    /// </summary>
    public TimeSpan PackageSettleDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Raised at most once per completion, after that completion is saved, so a restart cannot
    /// repeat it. A crash between the save and this event loses the event but not the record: a
    /// subscriber that has to act on every completion calls <see cref="GetCompletedPackages"/> when
    /// it starts and skips the ones it has already handled, by package id and time.
    /// <para>
    /// A batch added through <see cref="AddRemoteJobsAsync"/> completes once, with every file in
    /// it. A package built through <see cref="AddJobAsync"/> has no end marker, so a client adding
    /// its next file more than <see cref="PackageSettleDelay"/> after the last one can see a
    /// completion per stretch of adds. A subscriber that needs a whole set, such as every volume
    /// of an archive, has to check for it.
    /// </para>
    /// </summary>
    public event EventHandler<PackageCompletedEventArgs>? PackageCompleted;

    /// <summary>Packages whose last completion still stands: every job in them is finished.</summary>
    public IReadOnlyList<PackageCompletedEventArgs> GetCompletedPackages()
    {
        lock (_sync)
        {
            return _settings.DownloadGroups
                .Where(group => group.CompletedAt is not null && AllMembersCompletedLocked(group, out _))
                .Select(group => ToCompletedArgs(group, group.CompletedAt!.Value))
                .ToList();
        }
    }

    private static PackageCompletedEventArgs ToCompletedArgs(DownloadGroup group, DateTimeOffset completedAt)
        => new(group.Id, group.Name, group.Key, group.JobIds.ToList(), completedAt);

    /// <summary>
    /// Queues a completion check to run off the caller's path. A job's runner must not wait for the
    /// settings gate: holders of that gate can be waiting for the runner to finish.
    /// </summary>
    private void RequestPackageCheck()
    {
        lock (_sync)
        {
            _packageCheckRequested = true;
            if (_packageCheckRunning || _shuttingDown || !_initialized) return;
            _packageCheckRunning = true;
            _packageCheckWorker = Task.Run(RunPackageChecksAsync);
        }
    }

    private async Task RunPackageChecksAsync()
    {
        while (true)
        {
            lock (_sync)
            {
                if (!_packageCheckRequested || _shuttingDown)
                {
                    _packageCheckRunning = false;
                    return;
                }

                _packageCheckRequested = false;
            }

            try { await AnnouncePackageCompletionsAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke($"Checking finished packages failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Scans every package, so a save that failed earlier is retried by the next check. A package
    /// is due when all its jobs have finished and the newest finish is later than the one on record.
    /// </summary>
    private async Task AnnouncePackageCompletionsAsync(CancellationToken cancellationToken)
    {
        var due = new List<PackageCompletedEventArgs>();
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DownloadGroup> next;
            lock (_sync)
            {
                // Whoever holds these checks asks for one when it lets go.
                if (_packageChecksHeld > 0) return;
                SchedulePackageSettleCheckLocked();
                if (!_settings.DownloadGroups.Any(IsCompletionDueLocked)) return;
                next = CloneGroups(_settings.DownloadGroups);
                foreach (var group in next.Where(IsCompletionDueLocked).ToList())
                {
                    AllMembersCompletedLocked(group, out var completedAt);
                    group.CompletedAt = completedAt;
                    due.Add(ToCompletedArgs(group, completedAt));
                }
            }

            if (await TryCommitGroupsAsync(next, cancellationToken).ConfigureAwait(false) is not null) return;
        }
        finally { _settingsUpdateGate.Release(); }

        foreach (var completed in due)
        {
            try { PackageCompleted?.Invoke(this, completed); }
            catch (Exception ex) { _log?.Invoke($"A package completion handler failed: {ex.Message}"); }
        }
    }

    private bool IsCompletionDueLocked(DownloadGroup group)
        => HasNewCompletionLocked(group) && PackageSettleRemainingLocked(group) <= TimeSpan.Zero;

    private bool HasNewCompletionLocked(DownloadGroup group)
        => AllMembersCompletedLocked(group, out var completedAt) && (group.CompletedAt is not { } latched || completedAt > latched);

    private TimeSpan PackageSettleRemainingLocked(DownloadGroup group)
    {
        if (!_packageLastJoinedAt.TryGetValue(group.Id, out var joinedAt)) return TimeSpan.Zero;
        var remaining = joinedAt + PackageSettleDelay - _timeProvider.GetUtcNow();
        if (remaining > TimeSpan.Zero) return remaining;
        _packageLastJoinedAt.Remove(group.Id);
        return TimeSpan.Zero;
    }

    /// <summary>No job event follows a package's quiet period, so something has to look again when it ends.</summary>
    private void SchedulePackageSettleCheckLocked()
    {
        if (_packageSettleCheckPending) return;
        var wait = _settings.DownloadGroups.Where(HasNewCompletionLocked)
            .Select(PackageSettleRemainingLocked)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        if (wait <= TimeSpan.Zero) return;
        _packageSettleCheckPending = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(wait, _timeProvider).ConfigureAwait(false);
            lock (_sync) _packageSettleCheckPending = false;
            RequestPackageCheck();
        });
    }

    /// <summary>Marks packages from before completions were announced. Their finishes are history, not news.</summary>
    private async Task AdoptUntrackedPackagesAsync(CancellationToken cancellationToken)
    {
        List<DownloadGroup> next;
        lock (_sync)
        {
            if (_settings.DownloadGroups.All(group => group.CompletionTracked)) return;
            next = CloneGroups(_settings.DownloadGroups);
            foreach (var group in next.Where(group => !group.CompletionTracked))
            {
                group.CompletionTracked = true;
                if (AllMembersCompletedLocked(group, out var completedAt)) group.CompletedAt = completedAt;
            }
        }

        if (await TryCommitGroupsAsync(next, cancellationToken).ConfigureAwait(false) is not null)
        {
            // Still not news for this run; the next start adopts them on disk.
            lock (_sync) _settings.DownloadGroups = next;
        }
    }
}
