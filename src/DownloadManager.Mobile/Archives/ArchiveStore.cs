using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Archives;

/// <summary>
/// The archive sets the Host reported, and which download row each belongs on: the row of the set's first volume. UI thread only.
/// </summary>
public sealed class ArchiveStore
{
    private readonly Func<Guid, JobDto?> _jobOf;
    private readonly Dictionary<Guid, ArchiveSetDto> _sets = new();
    private readonly Dictionary<Guid, List<ArchiveSetDto>> _byJob = new();

    /// <param name="jobOf">The download with this id as the Downloads list has it, or null.</param>
    public ArchiveStore(Func<Guid, JobDto?> jobOf) => _jobOf = jobOf ?? throw new ArgumentNullException(nameof(jobOf));

    /// <summary>Raised after the sets changed.</summary>
    public event Action? Changed;

    public int Count => _sets.Count;

    public void Load(ArchiveListResult list)
    {
        _sets.Clear();
        foreach (var set in list.Sets) _sets[set.Id] = set;
        Rebuild();
    }

    public void Clear()
    {
        if (_sets.Count == 0) return;
        _sets.Clear();
        Rebuild();
    }

    public void Apply(ArchiveChangedDto change)
    {
        if (change.Set is { } set) _sets[set.Id] = set;
        else if (change.RemovedId is { } gone) _sets.Remove(gone);
        else return;
        Rebuild();
    }

    private void Rebuild()
    {
        _byJob.Clear();
        foreach (var set in _sets.Values)
            foreach (var id in set.JobIds)
            {
                if (!_byJob.TryGetValue(id, out var list)) _byJob[id] = list = [];
                list.Add(set);
            }

        Changed?.Invoke();
    }

    /// <summary>The set shown on this download's row, or null when it is not the first volume of one.</summary>
    public ArchiveSetDto? SetFor(Guid jobId)
    {
        if (!_byJob.TryGetValue(jobId, out var candidates)) return null;
        ArchiveSetDto? best = null;
        foreach (var set in candidates)
            if (IsFirstVolumeJob(set, jobId) && (best is null || set.UpdatedAt > best.UpdatedAt)) best = set;
        return best;
    }

    /// <summary>What the row of this download shows of its set, or null to leave the row as it is.</summary>
    public ArchiveRowInfo? InfoFor(Guid jobId)
    {
        if (SetFor(jobId) is not { } set) return null;
        long total = 0;
        foreach (var id in set.JobIds) total += _jobOf(id)?.TotalBytes ?? 0;
        return ArchiveRowInfo.For(set, total);
    }

    /// <summary>
    /// The download whose file is the set's first volume. When no download of the set is known by that name (the list may not hold
    /// them yet) the set's first job stands in, so a set never lands on every one of its rows.
    /// </summary>
    private bool IsFirstVolumeJob(ArchiveSetDto set, Guid jobId)
    {
        var first = ArchiveRowInfo.FileNameOf(set.FirstVolume);
        if (first.Length > 0)
        {
            var named = false;
            foreach (var id in set.JobIds)
            {
                if (_jobOf(id) is not { } job || !string.Equals(job.FileName, first, StringComparison.OrdinalIgnoreCase)) continue;
                if (id == jobId) return true;
                named = true;
            }

            if (named) return false;
        }

        return set.JobIds.Count > 0 && set.JobIds[0] == jobId;
    }
}
