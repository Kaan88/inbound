namespace DownloadManager.Core.Tests;

/// <summary>
/// Review 5 (the user's view, 2026-11-12): desktop fixes that live in WinUI code the tests can't run, checked in its source
/// as the other window findings are. The rules behind them are tested where they live.
/// </summary>
public sealed class UserViewReviewDesktopTests
{
    private static string Source(string name) => File.ReadAllText(WindowLocalFindingsTests.WinUiFile(name));

    /// <summary>W2a: Options › Save to has several favorites buttons in a row; each names its folder box.</summary>
    [Fact]
    public void Each_favorites_button_names_its_box()
    {
        var source = Source("FolderChooser.cs");
        Assert.Contains("AutomationProperties.SetName(favorites, favoritesName)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.SetName(favorites, \"Favorite folders\")", source, StringComparison.Ordinal);
    }

    /// <summary>A3: a long path shows its end (the folder the files go to) while the box isn't typed in, and the whole path as the tooltip.</summary>
    [Fact]
    public void A_folder_box_shows_the_end_of_its_path()
    {
        var source = Source("FolderChooser.cs");
        Assert.Contains("viewer.ChangeView(viewer.ScrollableWidth", source, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(box,", source, StringComparison.Ordinal);
        Assert.Contains("box.LostFocus += (_, _) => ShowEnd();", source, StringComparison.Ordinal);
    }

    /// <summary>L1: a column shown or hidden reaches every realized row, not only the rows still tracked.</summary>
    [Fact]
    public void Column_changes_reach_every_realized_row()
    {
        var source = Source("ColumnHeaderRow.cs");
        var applyToAll = source[source.IndexOf("public void ApplyToAll()", StringComparison.Ordinal)..];
        Assert.Contains("TakeRealized();", applyToAll[..applyToAll.IndexOf('}')], StringComparison.Ordinal);
        Assert.Contains("_list.ItemsPanelRoot", source, StringComparison.Ordinal);
    }

    /// <summary>T1a: a lone torrent's file list no longer opens by itself and pushes Save to, Start and Queue out of view.</summary>
    [Fact]
    public void A_lone_torrents_files_stay_closed_until_asked()
    {
        var source = Source("TorrentAddDialog.cs");
        Assert.DoesNotContain("_openedOnce", source, StringComparison.Ordinal);
        Assert.Contains("_treeOpen = !_treeOpen;", source, StringComparison.Ordinal);
    }

    /// <summary>S1: Close and the title bar ask before unsaved Scheduler edits are dropped.</summary>
    [Fact]
    public void Scheduler_asks_before_dropping_unsaved_edits()
    {
        var source = Source("SchedulerWindow.cs");
        Assert.Contains("_close.Click += async (_, _) => await CloseAskingAsync();", source, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Closing += (sender, args) =>", source, StringComparison.Ordinal);
        var unsaved = source[source.IndexOf("private bool HasUnsavedEdits()", StringComparison.Ordinal)..];
        unsaved = unsaved[..unsaved.IndexOf("private async Task CloseAskingAsync()", StringComparison.Ordinal)];
        Assert.Contains("_dirtySchedules.Count > 0", unsaved, StringComparison.Ordinal);
        // A New schedule form (no loaded key) is something Apply saves, so it counts as an unsaved edit too.
        Assert.Contains("if (_scheduleEditorEnabled && ScheduleFormKey() != _scheduleLoadedKey) return true;", unsaved, StringComparison.Ordinal);
        Assert.DoesNotContain("_scheduleLoadedKey.Length > 0", unsaved, StringComparison.Ordinal);
    }

    /// <summary>Review 5:E1: a rebuild of a stale list (a category switch, a sort, a package opened) keeps rows not current.</summary>
    [Fact]
    public void A_rebuild_of_a_stale_list_keeps_rows_not_current()
    {
        var source = Source("MainWindow.xaml.cs");
        var rebuild = source[source.IndexOf("private void RebuildVisible(", StringComparison.Ordinal)..];
        rebuild = rebuild[..rebuild.IndexOf("private void UpdateFindBar()", StringComparison.Ordinal)];
        Assert.Contains("if (_banner.Stale) foreach (var row in _visibleJobs) row.ShowNotCurrent();", rebuild, StringComparison.Ordinal);
        var packageUpdate = source.IndexOf("row.UpdatePackage(package, PackageMembers(package), row.IsExpanded);", StringComparison.Ordinal);
        Assert.True(packageUpdate > 0);
        Assert.Contains("if (_banner.Stale) row.ShowNotCurrent();", source.Substring(packageUpdate, 200), StringComparison.Ordinal);
    }
}
