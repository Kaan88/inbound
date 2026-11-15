using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.3 (remote-sites.md, "Clients"): the Sites section of the sidebar, the browse view that takes the list's place,
/// and the toolbar swap. Sites are this PC's: with another server shown (D9) the section is not offered.
/// </summary>
public sealed partial class MainWindow
{
    private const string SiteTagPrefix = "site:";

    private SitesStatusDto? _sitesStatus;
    private List<SiteDto> _sites = [];
    private SitesPanel? _sitesPanel;
    private bool _sitesShown;
    private string? _shownSiteId;
    private Visibility _scrollTrackBeforeSites = Visibility.Collapsed;

    /// <summary>Sites is on at the Host of this PC, and this PC is what the window shows.</summary>
    private bool SitesOffered => _connection == _local && _sitesStatus is { Enabled: true };

    private static string SiteTag(string id) => SiteTagPrefix + id;

    // ---- State from the Host ----------------------------------------------------------------------------

    private void WireSites(HostConnection connection)
    {
        connection.SitesChanged += change => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection == _local) ApplySitesState(change.Status, change.Sites);
        });
        connection.ConnectionChanged += (connected, error) =>
        {
            if (connected && connection == _local) DispatcherQueue.TryEnqueue(() => { _ = ReadSitesAsync(connection); });
        };
    }

    /// <summary>Asks this PC's Host for the switch and the saved sites, once per connection; later changes are pushed.</summary>
    private async Task ReadSitesAsync(HostConnection connection)
    {
        try
        {
            if (!connection.HostHasSites)
            {
                ApplySitesState(null, []);
                return;
            }

            var status = await connection.GetSitesStatusAsync();
            var sites = status is { Enabled: true } ? await connection.ListSitesAsync() ?? [] : [];
            if (connection == _local) ApplySitesState(status, sites);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            UiTrace.Write($"Reading the sites failed: {ex.GetType().Name}");
        }
    }

    private void ApplySitesState(SitesStatusDto? status, List<SiteDto> sites)
    {
        _sitesStatus = status;
        _sites = sites;
        SyncSiteEntryPoints();
        if (!_sitesShown) return;
        var shown = SitesOffered ? _sites.FirstOrDefault(site => site.Id == _shownSiteId) : null;
        if (shown is null) LeaveSites(selectCategory: true);
        else _sitesPanel?.UpdateSite(shown);
    }

    /// <summary>The window now shows this PC or another server: the section follows, and the view goes if it cannot stay.</summary>
    private void SyncSitesForConnection()
    {
        if (_sitesShown && !SitesOffered) LeaveSites(selectCategory: true);
        SyncSiteEntryPoints();
    }

    // ---- Sidebar ----------------------------------------------------------------------------------------

    /// <summary>One entry per saved site after My provider's, only while Sites is on. Inserted and removed by tag, as the remote entries are.</summary>
    private void SyncSiteEntryPoints()
    {
        var wanted = SitesOffered ? _sites.ToList() : [];
        _refreshingCategories = true;
        try
        {
            for (var index = CategoryList.Items.Count - 1; index >= 0; index--)
            {
                if (CategoryList.Items[index] is ListBoxItem { Tag: string tag } && tag.StartsWith(SiteTagPrefix, StringComparison.Ordinal) &&
                    wanted.All(site => SiteTag(site.Id) != tag))
                {
                    CategoryList.Items.RemoveAt(index);
                }
            }

            var items = CategoryList.Items.OfType<ListBoxItem>().ToList();
            var anchor = items.FindLastIndex(item => item.Tag is "queues" || (item.Tag is string tag && tag.StartsWith("remote:", StringComparison.Ordinal)));
            var at = anchor + 1;
            foreach (var site in wanted)
            {
                var tag = SiteTag(site.Id);
                var item = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(existing => existing.Tag as string == tag);
                if (item is null)
                {
                    item = new ListBoxItem { Tag = tag, Padding = new Thickness(14, 2, 8, 2) };
                    CategoryList.Items.Insert(Math.Min(at, CategoryList.Items.Count), item);
                }

                if ((item.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text != site.Name)
                {
                    item.Content = SidebarContent(site.Name, "&#xE839;");
                    AutomationProperties.SetName(item, $"Site {site.Name}");
                }

                at = CategoryList.Items.IndexOf(item) + 1;
            }

            if (_sitesShown && _shownSiteId is { } shown)
            {
                CategoryList.SelectedItem = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(existing => existing.Tag as string == SiteTag(shown));
            }
        }
        finally
        {
            _refreshingCategories = false;
        }
    }

    // ---- The view ---------------------------------------------------------------------------------------

    private SitesPanel EnsureSitesPanel()
    {
        if (_sitesPanel is { } existing) return existing;
        var panel = new SitesPanel
        {
            // On the provider client's connections of their own: a slow listing never holds up the window's main connection.
            Browse = (request, token) => _remote.BrowseSiteAsync(request, token),
            Star = request => _remote.StarSiteFolderAsync(request),
            OpenSitesOptions = OpenSitesOptionsHook,
        };
        panel.StateChanged += UpdateSitesToolbar;
        panel.SiteUpdated += updated =>
        {
            var at = _sites.FindIndex(site => site.Id == updated.Id);
            if (at >= 0) _sites[at] = updated;
            SyncSiteEntryPoints();
        };
        SitesHost.Children.Add(panel);
        _sitesPanel = panel;
        return panel;
    }

    /// <summary>
    /// Where a host-key or "Sites is off" message sends the user. Options > Sites (the switch, host-key trust, Add a site)
    /// is built apart from this view; when its dialogs can be opened directly, this is the one place to change.
    /// </summary>
    private void OpenSitesOptionsHook() => _ = OpenOptionsAsync();

    private void ShowSite(string siteId)
    {
        if (!SitesOffered || _sites.FirstOrDefault(site => site.Id == siteId) is not { } site)
        {
            SelectSidebarQuietly(_category);
            return;
        }

        LeaveProviderFull(selectCategory: false);
        var panel = EnsureSitesPanel();
        if (!_sitesShown) _scrollTrackBeforeSites = JobsScrollTrack.Visibility;
        // The same site again opens in the folder it was left in; another one at its start folder.
        var keepPlace = panel.SiteId == siteId;
        _sitesShown = true;
        _shownSiteId = siteId;
        SitesHost.Visibility = Visibility.Visible;
        DownloadListViewport.Visibility = Visibility.Collapsed;
        JobsScrollTrack.Visibility = Visibility.Collapsed;
        ProviderNoticeBar.IsOpen = false;
        DownloadToolSet.Visibility = Visibility.Collapsed;
        SitesToolSet.Visibility = Visibility.Visible;
        UpdateSpeedGraphVisibility();
        SyncTorrentInfo();
        panel.Open(site, keepPlace ? panel.Path : null);
        UpdateSitesToolbar();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, panel.FocusList);
    }

    /// <summary>Back to the list and the download buttons. The panel keeps its place for the next time.</summary>
    private void LeaveSites(bool selectCategory)
    {
        if (!_sitesShown) return;
        _sitesShown = false;
        _shownSiteId = null;
        _sitesPanel?.Stop();
        SitesHost.Visibility = Visibility.Collapsed;
        DownloadListViewport.Visibility = Visibility.Visible;
        JobsScrollTrack.Visibility = _scrollTrackBeforeSites;
        SitesToolSet.Visibility = Visibility.Collapsed;
        DownloadToolSet.Visibility = Visibility.Visible;
        UpdateSpeedGraphVisibility();
        SyncTorrentInfo();
        UpdateButtons();
        if (selectCategory) SelectSidebarQuietly(_category);
    }

    /// <summary>The speed graph follows its menu item, except in the Sites view, where it is hidden (the Transfers bar comes with SITES.5).</summary>
    private void UpdateSpeedGraphVisibility()
    {
        SpeedGraph.Visibility = ShowSpeedGraphMenuItem.IsChecked && !_sitesShown ? Visibility.Visible : Visibility.Collapsed;
        UpdateSpeedTimer();
    }

    // ---- Toolbar ----------------------------------------------------------------------------------------

    private void UpdateSitesToolbar()
    {
        if (_sitesPanel is not { } panel || !_sitesShown) return;
        SitesUpButton.IsEnabled = panel.CanGoUp;
        SitesRefreshButton.IsEnabled = !panel.IsLoading;
        SitesStarButton.IsEnabled = !panel.IsLoading && panel.SiteId is not null;
        var starred = panel.IsStarred;
        SitesStarIcon.Glyph = starred ? "" : "";
        SitesStarLabel.Text = starred ? "Unstar" : "Star";
        AutomationProperties.SetName(SitesStarButton, starred ? "Unstar folder" : "Star folder");
        ToolTipService.SetToolTip(SitesStarButton, starred
            ? "Remove this folder from this site's Starred list"
            : "Star this folder to find it again in this site's Starred list");
    }

    private void SitesUpButton_Click(object sender, RoutedEventArgs e) => _sitesPanel?.GoUp();

    private void SitesRefreshButton_Click(object sender, RoutedEventArgs e) => _sitesPanel?.Reload();

    private void SitesStarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesPanel is { } panel) _ = panel.ToggleStarAsync();
    }
}
