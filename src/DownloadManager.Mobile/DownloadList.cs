using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DownloadManager.Mobile;

/// <summary>The state views of the Downloads screen (its filter chips). Paused downloads are in <see cref="All"/> only, as in the mockup.</summary>
public enum DownloadFilter { All, Active, Queued, Finished, Failed }

/// <summary>What the summary card at the top of Downloads shows.</summary>
public sealed record DownloadSummary(double Rate, int Active, int Queued, double? SecondsLeft, IReadOnlyList<float> History)
{
    public static readonly DownloadSummary Empty = new(0, 0, 0, null, Array.Empty<float>());

    /// <summary>The big figure: the rate while something transfers, otherwise a word.</summary>
    public string Headline => Active > 0 ? Fmt.Rate(Rate) : "Idle";

    /// <summary>"2 active · 1 queued · about 7 min left"; counts of zero are left out.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Active > 0) parts.Add($"{Active} active");
            if (Queued > 0) parts.Add($"{Queued} queued");
            if (Active > 0 && SecondsLeft is { } s) parts.Add(Fmt.About(s) + " left");
            return parts.Count == 0 ? "Nothing is downloading" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// What the Downloads screen shows of a <see cref="DownloadsModel"/>: the rows of the chosen state view that match the search,
/// the chips' counts, the summary card, and the multiselect. <see cref="Visible"/> follows <see cref="DownloadsModel.Rows"/>' order
/// (newest first) and changes by single inserts and removes, never by moving a row, so the list can animate and a row never
/// slips from under a finger. Everything runs on the UI thread. No timer: the summary is recomputed only when a row changes, and
/// the speed history gets a sample at most once per <see cref="SampleInterval"/> while something transfers.
/// </summary>
public sealed class DownloadList : IDisposable
{
    public const int MaxHistory = 60;
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly DownloadsModel _model;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<Guid> _selected = new();
    private readonly List<float> _history = new();
    private readonly HashSet<JobRow> _watched = new();
    private DownloadFilter _filter;
    private string _search = "";
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private bool _recomputeQueued;
    private bool _disposed;

    /// <param name="post">Runs an action later on the UI thread (coalesces the summary of one batch of events).</param>
    public DownloadList(DownloadsModel model, Action<Action> post, Func<DateTimeOffset>? clock = null)
    {
        _model = model;
        _post = post;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        model.Rows.CollectionChanged += OnRows;
        foreach (var r in model.Rows) Watch(r);
        Rebuild();
    }

    /// <summary>The rows to show, in order. Changed by inserts and removes only.</summary>
    public ObservableCollection<JobRow> Visible { get; } = new();

    public DownloadSummary Summary { get; private set; } = DownloadSummary.Empty;

    /// <summary>The summary or a chip's count changed.</summary>
    public event Action? SummaryChanged;

    /// <summary>The selection changed (also when it became empty).</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// A selected row changed state, so what the selection bar offers (<see cref="CanResumeSelection"/>,
    /// <see cref="CanPauseSelection"/>) may have changed; the selection itself did not.
    /// </summary>
    public event Action? SelectedRowsChanged;

    public DownloadFilter Filter
    {
        get => _filter;
        set { if (_filter == value) return; _filter = value; Rebuild(); }
    }

    public string Search
    {
        get => _search;
        set { value = (value ?? "").Trim(); if (_search == value) return; _search = value; Rebuild(); }
    }

    public int Count(DownloadFilter f) => _model.Rows.Count(r => InFilter(r, f));

    /// <summary>The label of a chip: "All 12", "Active 2". A count of zero is still written (the chips do not move about).</summary>
    public string ChipLabel(DownloadFilter f) => $"{f} {Count(f)}";

    /// <summary>True when the Downloads list is empty because there is nothing at all (the empty screen), not because a filter hides it.</summary>
    public bool NothingAtAll => _model.Rows.Count == 0;

    private static bool InFilter(JobRow r, DownloadFilter f) => f switch
    {
        DownloadFilter.All => true,
        DownloadFilter.Active => r.Group == RowGroup.Active,
        DownloadFilter.Queued => r.Group == RowGroup.Queued,
        DownloadFilter.Finished => r.Group == RowGroup.Finished,
        DownloadFilter.Failed => r.Group == RowGroup.Failed,
        _ => true,
    };

    private bool Matches(JobRow r) =>
        InFilter(r, _filter) && (_search.Length == 0 || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || r.Job.Url.Contains(_search, StringComparison.OrdinalIgnoreCase));

    private void Watch(JobRow r)
    {
        if (_watched.Add(r)) r.PropertyChanged += OnRowChanged;
    }

    private void Unwatch(JobRow r)
    {
        if (_watched.Remove(r)) r.PropertyChanged -= OnRowChanged;
    }

    private void OnRows(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (JobRow r in e.OldItems) { Unwatch(r); _selected.Remove(r.Id); }
        if (e.NewItems is not null) foreach (JobRow r in e.NewItems) Watch(r);
        if (e.Action == NotifyCollectionChangedAction.Reset) { foreach (var r in _watched.ToArray()) Unwatch(r); foreach (var r in _model.Rows) Watch(r); _selected.Clear(); }
        Rebuild();
        if (e.OldItems is not null) SelectionChanged?.Invoke();
    }

    private void OnRowChanged(object? s, PropertyChangedEventArgs e)
    {
        // Membership depends on the state view and the name; the summary on the rate, state and progress.
        if (e.PropertyName is nameof(JobRow.Group) or nameof(JobRow.Name))
        {
            if (SyncVisible()) SelectionChanged?.Invoke();
            else if (e.PropertyName == nameof(JobRow.Group) && s is JobRow moved && _selected.Contains(moved.Id)) SelectedRowsChanged?.Invoke();
        }
        else if (e.PropertyName == nameof(JobRow.Action) && s is JobRow r && _selected.Contains(r.Id)) SelectedRowsChanged?.Invoke();

        QueueRecompute();
    }

    /// <summary>
    /// Visible becomes the matching rows by removing the rows that no longer match, then inserting the ones that now do. A
    /// selected row that is no longer shown leaves the selection, so a bulk action never reaches a row the user can't see
    /// (finding 10). True when that changed the selection.
    /// </summary>
    private bool SyncVisible()
    {
        var target = _model.Rows.Where(Matches).ToList();
        var keep = new HashSet<JobRow>(target);
        var unselected = false;
        for (var i = Visible.Count - 1; i >= 0; i--)
        {
            if (keep.Contains(Visible[i])) continue;
            unselected |= _selected.Remove(Visible[i].Id);
            Visible.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Visible.Count && ReferenceEquals(Visible[i], target[i])) continue;
            Visible.Insert(i, target[i]);
        }

        // Selected ids that are not on screen (a row the list never showed) go too.
        return (_selected.RemoveWhere(id => !Visible.Any(r => r.Id == id)) > 0) | unselected;
    }

    private void Rebuild()
    {
        if (SyncVisible()) SelectionChanged?.Invoke();
        Recompute();
    }

    private void QueueRecompute()
    {
        if (_recomputeQueued || _disposed) return;
        _recomputeQueued = true;
        _post(() => { _recomputeQueued = false; if (!_disposed) Recompute(); });
    }

    private void Recompute()
    {
        double rate = 0, remaining = 0;
        int active = 0, queued = 0;
        var allKnown = true;
        foreach (var r in _model.Rows)
        {
            if (r.Group == RowGroup.Active)
            {
                active++;
                var j = r.Job;
                rate += j.BytesPerSecond ?? 0;
                if (j.TotalBytes is > 0 && j.TotalBytes > j.ReceivedBytes) remaining += j.TotalBytes.Value - j.ReceivedBytes;
                else if (j.TotalBytes is null or 0) allKnown = false;
            }
            else if (r.Group == RowGroup.Queued) queued++;
        }

        var now = _clock();
        if (active > 0 && now - _lastSample >= SampleInterval)
        {
            _lastSample = now;
            _history.Add((float)rate);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }

        // Nothing is sampled while idle, and the graph starts again from empty after a pause of everything.
        if (active == 0 && _history.Count > 0) { _history.Clear(); _lastSample = DateTimeOffset.MinValue; }
        var left = active > 0 && allKnown && rate > 0 ? remaining / rate : (double?)null;
        var next = new DownloadSummary(rate, active, queued, left, _history.ToArray());
        Summary = next;
        SummaryChanged?.Invoke();
    }

    // ---- multiselect ----

    public bool Selecting => _selected.Count > 0;

    public int SelectedCount => _selected.Count;

    public bool IsSelected(JobRow r) => _selected.Contains(r.Id);

    public IReadOnlyList<JobRow> SelectedRows => Visible.Where(r => _selected.Contains(r.Id)).ToList();

    /// <summary>Long press: starts the selection with this row. Does nothing for a row that is already selected.</summary>
    public void StartSelecting(JobRow r)
    {
        if (_selected.Add(r.Id)) SelectionChanged?.Invoke();
    }

    /// <summary>A tap while selecting: adds or removes the row; the selection ends when the last row is removed.</summary>
    public void Toggle(JobRow r)
    {
        if (!_selected.Remove(r.Id)) _selected.Add(r.Id);
        SelectionChanged?.Invoke();
    }

    public void SelectAll()
    {
        foreach (var r in Visible) _selected.Add(r.Id);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>"3 selected".</summary>
    public string SelectionTitle => $"{_selected.Count} selected";

    /// <summary>The play button of the selection bar: something selected is stopped, waiting or failed.</summary>
    public bool CanResumeSelection => SelectedRows.Any(r => r.Group is RowGroup.Paused or RowGroup.Queued or RowGroup.Failed);

    /// <summary>The pause button of the selection bar: something selected is transferring (or waiting to retry).</summary>
    public bool CanPauseSelection => SelectedRows.Any(r => r.Action == RowAction.Pause);

    /// <summary>
    /// What a delete asks. Files exist for a finished download (its file) and for an unfinished one (its part file); a failed or
    /// cancelled one has nothing left worth a question when it never received a byte.
    /// </summary>
    public static DeletePlan PlanDelete(IReadOnlyCollection<JobRow> rows)
    {
        var withFiles = rows.Count(r => r.Group == RowGroup.Finished ? !r.Job.FileRemovedAfterExtraction : r.Job.ReceivedBytes > 0);
        var noun = rows.Count == 1 ? "download" : "downloads";
        var title = rows.Count == 1 ? $"Delete \"{Shorten(rows.First().Name)}\"?" : $"Delete {rows.Count} {noun}?";
        return new DeletePlan(rows.Count, withFiles, title);
    }

    /// <summary>The rows' groups when a delete was asked, to tell later whether they changed.</summary>
    public static RowGroup[] Groups(IReadOnlyList<JobRow> rows) => rows.Select(r => r.Group).ToArray();

    /// <summary>
    /// True when a delete question asked with <paramref name="plan"/> and <paramref name="groups"/> still fits the rows: none
    /// changed its group (a download that finished meanwhile) and the files to delete are the same count.
    /// </summary>
    public static bool SamePlan(IReadOnlyList<JobRow> rows, DeletePlan plan, IReadOnlyList<RowGroup> groups) =>
        rows.Count == groups.Count && rows.Select(r => r.Group).SequenceEqual(groups) && PlanDelete(rows) == plan;

    private static string Shorten(string name) => name.Length <= 36 ? name : name[..33] + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Rows.CollectionChanged -= OnRows;
        foreach (var r in _watched.ToArray()) Unwatch(r);
    }
}

/// <summary>The question asked before a delete: <paramref name="WithFiles"/> of <paramref name="Count"/> downloads have a file on the phone.</summary>
public sealed record DeletePlan(int Count, int WithFiles, string Title)
{
    public bool HasFiles => WithFiles > 0;

    public const string KeepFiles = "Remove from the list only";

    /// <summary>"Also delete the file" / "Also delete 2 files"; says nothing of files when there are none.</summary>
    public string DeleteFilesLabel => WithFiles == 1 ? "Remove and delete the file" : $"Remove and delete {WithFiles} files";

    /// <summary>One line under the title: what each choice does.</summary>
    public string Message => HasFiles
        ? "The files stay on the phone unless you choose to delete them too."
        : "Nothing has been saved for " + (Count == 1 ? "it" : "them") + " yet, so only the list entry goes.";
}
