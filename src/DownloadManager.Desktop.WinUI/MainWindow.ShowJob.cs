using DownloadManager.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>EXT.R12: the extension's "Show in app" opens this window at one download, selected and briefly highlighted.</summary>
public sealed partial class MainWindow
{
    /// <summary>How long the pointed-out row keeps its highlight.</summary>
    private static readonly TimeSpan ShownRowHighlight = TimeSpan.FromSeconds(2);

    private Guid? _pendingShowJob;
    private DispatcherQueueTimer? _highlightTimer;
    private JobViewModel? _highlightedRow;

    /// <summary>
    /// Shows the window at <paramref name="jobId"/>; before the Host's first answer, once the list is in. The download is
    /// on this PC, so a window showing another server switches to This PC first and shows it once this PC's list is in.
    /// </summary>
    public void RequestShowJob(Guid jobId)
    {
        ShowFromTray();
        switch (ShowJobRequests.ShowWhen(_connection.IsRemote, _settings is not null))
        {
            case ShowJobRequests.When.Now:
                ShowJobRow(jobId);
                break;
            case ShowJobRequests.When.AfterList:
                _pendingShowJob = jobId;
                break;
            case ShowJobRequests.When.AfterSwitch:
                _pendingShowJob = jobId;
                _ = ShowServerAsync(ServerTarget.Local);
                break;
        }
    }

    /// <summary>After a list came in: this PC's only, since the download asked for is on this PC.</summary>
    private void ShowPendingJob()
    {
        if (_pendingShowJob is not { } id || !ShowJobRequests.ListBringsIt(_connection.IsRemote)) return;
        _pendingShowJob = null;
        ShowJobRow(id);
    }

    /// <summary>
    /// The download's row: on All Downloads if this view or the search hides it, its package opened when it is in one,
    /// then selected, scrolled to, focused and highlighted for <see cref="ShownRowHighlight"/>.
    /// </summary>
    private void ShowJobRow(Guid id)
    {
        if (!_jobsById.TryGetValue(id, out var row))
        {
            ShowPassingStatus("That download is no longer in the list.");
            return;
        }

        if (!_visibleJobs.Contains(row))
        {
            if (!MatchesSearch(row)) _search = string.Empty;
            _category = "all";
            CategoryList.SelectedIndex = 0;
        }

        if (PackageOf(id) is { } package && !_uiState.ExpandedPackages.Contains(package.Id))
        {
            _uiState.ExpandedPackages.Add(package.Id);
            UiStateStore.Save(_uiState);
        }

        RebuildVisible(selectedId: id);
        if (!_visibleJobs.Contains(row)) return;
        JobsList.ScrollIntoView(row);
        JobsList.UpdateLayout();
        if (JobsList.ContainerFromItem(row) is Control item) item.Focus(FocusState.Keyboard);
        HighlightBriefly(row);
    }

    /// <summary>One short-lived timer, stopped when the highlight ends: nothing runs while idle.</summary>
    private void HighlightBriefly(JobViewModel row)
    {
        _highlightedRow?.Highlight(null);
        _highlightedRow = row;
        var accent = InboundTheme.Brush("InboundBadgeAccentFillBrush", JobsList) is SolidColorBrush fill
            ? new SolidColorBrush(fill.Color) { Opacity = 0.35 }
            : null;
        row.Highlight(accent);
        if (_highlightTimer is null)
        {
            _highlightTimer = DispatcherQueue.CreateTimer();
            _highlightTimer.IsRepeating = false;
            _highlightTimer.Tick += (_, _) =>
            {
                _highlightTimer?.Stop();
                _highlightedRow?.Highlight(null);
                _highlightedRow = null;
            };
        }

        _highlightTimer.Stop();
        _highlightTimer.Interval = ShownRowHighlight;
        _highlightTimer.Start();
    }
}
