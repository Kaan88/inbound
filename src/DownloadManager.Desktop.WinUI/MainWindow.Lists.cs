using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace DownloadManager.Desktop.WinUI;

/// <summary>The download list's header and its selection box.</summary>
public sealed partial class MainWindow
{
    private readonly ListColumnLayout _jobColumns = ListColumnSets.Downloads();
    private ColumnHeaderRow _jobsHeader = null!;
    private RegionDrag? _jobsRegion;

    /// <summary>The rows a box began by keeping, while one is being dragged. Null otherwise.</summary>
    private HashSet<JobViewModel>? _regionKeep;

    private void InitializeListHeader()
    {
        _jobColumns.Import(_uiState.ColumnWidths);

        // It stays away until there is an archive to talk about.
        _jobColumns.SetAvailable("extraction", false);
        // DT-6, UI.R42: the torrent views have choices of their own (SyncTorrentColumns puts the view's in).
        _torrentColumnsShown = TorrentListRules.IsTorrentView(_uiState.Category);
        ListColumnSets.UseDownloadsChoices(_jobColumns, _torrentColumnsShown, ColumnChoices(_torrentColumnsShown));
        _jobsHeader = new ColumnHeaderRow(_jobColumns, JobsHeader, JobsList, RootGrid, HeaderSort_Clicked, SaveColumnWidths, ColumnsChosen);
        _jobsHeader.MinimumWidthChanged += width =>
        {
            DownloadListGrid.MinWidth = width;
            // T7: Torrent Info narrows so the columns still fit, or stays away when they would not.
            SyncTorrentInfo();
        };
        _jobsHeader.ApplyToAll();
        _jobsRegion = new RegionDrag(JobsList, JobsRegionLayer, () => false, BeginRegion, ChangeRegion, EndRegion);
#if HAS_UNO
        // WinUI's list clears the selection on a click below the rows; Uno's doesn't, so this does.
        JobsList.AddHandler(UIElement.TappedEvent, new Microsoft.UI.Xaml.Input.TappedEventHandler((_, e) =>
        {
            for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, JobsList);
                 element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            {
                if (element is ListViewItem or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) return;
            }

            JobsList.SelectedItems.Clear();
        }), true);
#endif
    }

    private void HeaderSort_Clicked(string key)
    {
        var (next, ascending) = ListSort.Next(_sortKey, _sortAscending, key);
        ApplySort(next, ascending);
    }

    /// <summary>The one way the order changes: a header, the View menu or the size shortcut.</summary>
    private void ApplySort(string key, bool ascending)
    {
        _sortKey = key;
        _sortAscending = ascending;
        _jobsHeader.ShowSort(key, ascending);
        RebuildVisible(sorting: true);
        SaveUiState();

        // The list keeps its place on whichever row was at the top, which would hide the new first rows.
        if (JobsList.SelectedItem is { } selected) JobsList.ScrollIntoView(selected);
        else if (JobsList.Items.Count > 0) JobsList.ScrollIntoView(JobsList.Items[0]);
    }

    private void SaveColumnWidths()
    {
        _uiState.ColumnWidths = _jobColumns.Export();
        SaveUiState();
    }

    /// <summary>Whether the layout holds the torrent views' choices now (SyncTorrentColumns), not the other views'.</summary>
    private bool _torrentColumnsShown;

    /// <summary>UI.R42: the saved choices of the torrent views, or of every other view.</summary>
    private Dictionary<string, bool> ColumnChoices(bool torrentViews) => torrentViews ? _uiState.TorrentViewColumns : _uiState.ShownColumns;

    /// <summary>UI.R42: the header's menu showed or hid a column, or reset them: kept for the views of its kind, and the list sorted again.</summary>
    private void ColumnsChosen()
    {
        if (_torrentColumnsShown) _uiState.TorrentViewColumns = _jobColumns.ExportChoices();
        else _uiState.ShownColumns = _jobColumns.ExportChoices();
        _uiState.ColumnWidths = _jobColumns.Export();
        SaveUiState();
        UpdateSeedClock();
        // A sort by a column that went waits for it; one by a column that came back holds again.
        _jobsHeader.ShowSort(_sortKey, _sortAscending);
        RebuildVisible(sorting: true);
    }

    /// <summary>
    /// UI.R42: reads the seeding rows' Seeding time again while the column shows (TorrentListRules.SeedClockInterval). An
    /// idle seeding torrent gets no updates from the Host, so the cell would stand still. No timer runs otherwise.
    /// </summary>
    private DispatcherTimer? _seedClock;

    private void UpdateSeedClock()
    {
        var interval = TorrentListRules.SeedClockInterval(WindowShown, _jobColumns.IsVisible("seedTime"), _jobsById.Values.Select(job => job.SeedingTime));
        if (interval is not { } every)
        {
            _seedClock?.Stop();
            return;
        }

        if (_seedClock is null)
        {
            _seedClock = new DispatcherTimer();
            _seedClock.Tick += (_, _) =>
            {
                foreach (var row in _jobsById.Values) row.RefreshSeedTime();
                UpdateSeedClock();
            };
        }
        if (_seedClock.Interval != every) _seedClock.Interval = every;
        if (!_seedClock.IsEnabled) _seedClock.Start();
    }

    /// <summary>The order the list is put in; a column's sort holds only where that column shows.</summary>
    private (string Key, bool Ascending) ShownSort => ListColumnSets.ShownSort(_jobColumns, _sortKey, _sortAscending);

    private IEnumerable<JobViewModel> Sorted(IEnumerable<JobViewModel> rows)
    {
        var (key, ascending) = ShownSort;
        return Sorted(rows, key, ascending);
    }

    /// <summary>Blank cells stay at the end whichever way the list runs.</summary>
    private static IEnumerable<JobViewModel> Sorted(IEnumerable<JobViewModel> rows, string key, bool ascending) => key switch
    {
        "name" => ListSort.Order(rows, job => job.FileName, NaturalStringComparer.Instance, ascending),
        "queue" => ListSort.Order(rows, job => job.QueueText, NaturalStringComparer.Instance, ascending, job => job.QueueText.Length == 0),
        "size" => ListSort.Order(rows, job => job.TotalSize, null, ascending, job => job.TotalSize <= 0),
        "status" => ListSort.Order(rows, job => job.StateText, NaturalStringComparer.Instance, ascending),
        "extraction" => ListSort.Order(rows, job => job.ArchiveText, NaturalStringComparer.Instance, ascending, job => job.ArchiveText.Length == 0),
        "seeds" => ListSort.Order(rows, job => job.SeedCount, null, ascending, job => job.SeedCount is null),
        "peers" => ListSort.Order(rows, job => job.PeerCount, null, ascending, job => job.PeerCount is null),
        "eta" => ListSort.Order(rows, job => job.Remaining, null, ascending, job => job.Remaining is null),
        "up" => ListSort.Order(rows, job => job.UploadBytesPerSecond, null, ascending, job => job.UploadBytesPerSecond <= 0),
        "ratio" => ListSort.Order(rows, job => job.RatioValue, null, ascending, job => job.RatioValue is null),
        "uploaded" => ListSort.Order(rows, job => job.UploadedBytes, null, ascending, job => job.UploadedBytes is null),
        "seedTime" => ListSort.Order(rows, job => job.SeedingTime, null, ascending, job => job.SeedingTime is null),
        "seedGoal" => ListSort.Order(rows, job => job.SeedGoalText, NaturalStringComparer.Instance, ascending, job => job.SeedGoalText.Length == 0),
        "rate" => ListSort.Order(rows, job => job.BytesPerSecond, null, ascending, job => job.BytesPerSecond <= 0),
        "lastTry" => ListSort.Order(rows, job => job.LastUpdated, null, ascending),
        "description" => ListSort.Order(rows, job => job.Description, NaturalStringComparer.Instance, ascending, job => job.Description.Length == 0),
        _ => ListSort.Order(rows, job => job.CreatedAt, null, ascending),
    };

    private void BeginRegion(bool add)
    {
        _regionKeep = add ? JobsList.SelectedItems.OfType<JobViewModel>().ToHashSet() : [];
        if (!add) JobsList.SelectedItems.Clear();
    }

    private void ChangeRegion(RowRange? before, RowRange? now, bool rowsMoved)
    {
        if (_regionKeep is not { } keep) return;
        if (rowsMoved)
        {
            // Positions from before mean nothing now, so this starts again from what was kept.
            JobsList.SelectedItems.Clear();
            foreach (var row in keep.Where(_visibleJobs.Contains)) JobsList.SelectedItems.Add(row);
            before = null;
        }

        var (add, remove) = RegionSelection.Change(before, now);
        foreach (var at in remove)
        {
            if (at < _visibleJobs.Count && !keep.Contains(_visibleJobs[at]))
            {
#if HAS_UNO
                // Uno has no SelectRange/DeselectRange.
                JobsList.SelectedItems.Remove(_visibleJobs[at]);
#else
                JobsList.DeselectRange(new ItemIndexRange(at, 1));
#endif
            }
        }

        foreach (var at in add)
        {
            if (at < _visibleJobs.Count && !JobsList.SelectedItems.Contains(_visibleJobs[at]))
            {
#if HAS_UNO
                JobsList.SelectedItems.Add(_visibleJobs[at]);
#else
                JobsList.SelectRange(new ItemIndexRange(at, 1));
#endif
            }
        }

        UpdateStatusText();
    }

    private void EndRegion()
    {
        _regionKeep = null;
        UpdateButtons();
        UpdateStatusText();
    }
}
