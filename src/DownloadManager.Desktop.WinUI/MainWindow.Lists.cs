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
        _jobColumns.SetVisible("extraction", false);
        _jobsHeader = new ColumnHeaderRow(_jobColumns, JobsHeader, JobsList, RootGrid, HeaderSort_Clicked, SaveColumnWidths);
        _jobsHeader.MinimumWidthChanged += width => DownloadListGrid.MinWidth = width;
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

    /// <summary>Blank cells stay at the end whichever way the list runs.</summary>
    private IEnumerable<JobViewModel> Sorted(IEnumerable<JobViewModel> rows) => _sortKey switch
    {
        "name" => ListSort.Order(rows, job => job.FileName, NaturalStringComparer.Instance, _sortAscending),
        "queue" => ListSort.Order(rows, job => job.QueueText, NaturalStringComparer.Instance, _sortAscending, job => job.QueueText.Length == 0),
        "size" => ListSort.Order(rows, job => job.TotalSize, null, _sortAscending, job => job.TotalSize <= 0),
        "status" => ListSort.Order(rows, job => job.StateText, NaturalStringComparer.Instance, _sortAscending),
        "extraction" => ListSort.Order(rows, job => job.ArchiveText, NaturalStringComparer.Instance, _sortAscending, job => job.ArchiveText.Length == 0),
        "eta" => ListSort.Order(rows, job => job.Remaining, null, _sortAscending, job => job.Remaining is null),
        "rate" => ListSort.Order(rows, job => job.BytesPerSecond, null, _sortAscending, job => job.BytesPerSecond <= 0),
        "lastTry" => ListSort.Order(rows, job => job.LastUpdated, null, _sortAscending),
        "description" => ListSort.Order(rows, job => job.Description, NaturalStringComparer.Instance, _sortAscending, job => job.Description.Length == 0),
        _ => ListSort.Order(rows, job => job.CreatedAt, null, _sortAscending),
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
