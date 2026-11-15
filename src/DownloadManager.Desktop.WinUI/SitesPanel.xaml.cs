using System.Collections.ObjectModel;
using DownloadManager.Contracts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One line of the folder list. Public because the list's bindings read it by reflection.</summary>
public sealed class SiteItemRow
{
    internal SiteItemRow(SiteItemDto item)
    {
        Name = item.Name;
        IsFolder = item.IsFolder;
        SizeText = SitesBrowseRules.SizeText(item.Size, item.IsFolder);
        ModifiedText = SitesBrowseRules.ModifiedText(item.Modified);
    }

    public string Name { get; }

    public bool IsFolder { get; }

    public string SizeText { get; }

    public string ModifiedText { get; }

    /// <summary>Segoe MDL2 folder and file glyphs.</summary>
    public string Glyph => IsFolder ? "" : "";

    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { IsFolder ? $"Folder {Name}" : Name };
            if (SizeText.Length > 0) parts.Add(SizeText);
            if (ModifiedText.Length > 0) parts.Add($"modified {ModifiedText}");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// SITES.3: the read-only browse view of one saved site (remote-sites.md, "Clients"). Breadcrumbs, a folder list, paging,
/// stars, loading and error states. It asks the Host through <see cref="Browse"/> and <see cref="Star"/> and never talks to
/// rclone; the window hosts it in place of the download list and keeps its toolbar in step through <see cref="StateChanged"/>.
/// </summary>
public sealed partial class SitesPanel : UserControl
{
    private readonly ObservableCollection<SiteItemRow> _rows = [];
    private SiteDto? _site;
    private string _path = SitesBrowseRules.Root;
    private int _total;
    private int _generation;
    private bool _loading;
    private CancellationTokenSource? _cts;
    private string? _selectAfter;
    private bool _failed;

    public SitesPanel()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _rows;
        KeyDown += OnKeyDown;
        AutomationProperties.SetName(this, "Site browser");
        SyncChrome();
    }

    /// <summary>Reads one page of a folder from the Host.</summary>
    internal Func<SiteBrowseRequest, CancellationToken, Task<SiteBrowseResultDto>>? Browse { get; set; }

    /// <summary>Stars or unstars a folder at the Host.</summary>
    internal Func<SiteStarRequest, Task<SiteResultDto>>? Star { get; set; }

    /// <summary>
    /// A host-key problem or a switch that is off is fixed in Options > Sites. The window decides how that opens, so the
    /// dialogs built there (host-key trust, Add a site) can replace it later.
    /// </summary>
    internal Action? OpenSitesOptions { get; set; }

    /// <summary>The path, "can go up" or "is starred" changed, so the toolbar follows.</summary>
    internal event Action? StateChanged;

    /// <summary>A star changed the site; the window keeps its copy (and the sidebar) in step.</summary>
    internal event Action<SiteDto>? SiteUpdated;

    internal string? SiteId => _site?.Id;

    internal string Path => _path;

    internal bool CanGoUp => !SitesBrowseRules.IsRoot(_path);

    internal bool IsStarred => SitesBrowseRules.IsStarred(_site?.Stars, _path);

    internal bool IsLoading => _loading;

    /// <summary>Shows a site, at <paramref name="path"/> or, with null, at the folder the site starts in.</summary>
    internal void Open(SiteDto site, string? path = null)
    {
        _site = site;
        SiteNameText.Text = site.Name;
        _path = path is null ? SitesBrowseRules.Root : SitesBrowseRules.Normalize(path);
        _ = LoadAsync(path, refresh: false, append: false);
    }

    /// <summary>The site's saved data changed (renamed, stars): the name and the starred list follow, the folder stays.</summary>
    internal void UpdateSite(SiteDto site)
    {
        if (_site?.Id != site.Id) return;
        _site = site;
        SiteNameText.Text = site.Name;
        SyncChrome();
        StateChanged?.Invoke();
    }

    internal void GoUp()
    {
        if (SitesBrowseRules.Parent(_path) is { } parent) OpenFolder(parent, selectName: SitesBrowseRules.LeafName(_path));
    }

    internal void Reload() => _ = LoadAsync(_path, refresh: true, append: false);

    internal async Task ToggleStarAsync()
    {
        if (_site is not { } site || Star is null) return;
        var want = !IsStarred;
        var path = _path;
        SiteResultDto result;
        try
        {
            result = await Star(new SiteStarRequest { Id = site.Id, Path = path, Starred = want });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            result = new SiteResultDto { Message = ex.Message };
        }

        if (_site?.Id != site.Id) return;
        if (result.Ok && result.Site is { } updated)
        {
            _site = updated;
            SyncChrome();
            SiteUpdated?.Invoke(updated);
            StateChanged?.Invoke();
            Say(want ? $"Starred {SitesBrowseRules.LeafName(path)}" : $"Removed the star from {SitesBrowseRules.LeafName(path)}");
            return;
        }

        Say($"Could not {(want ? "star" : "unstar")} this folder: {(string.IsNullOrWhiteSpace(result.Message) ? "the background program did not say why." : result.Message)}");
    }

    /// <summary>Gives the keyboard to the list.</summary>
    internal void FocusList()
    {
        if (_rows.Count > 0)
        {
            if (ItemsList.SelectedIndex < 0) ItemsList.SelectedIndex = 0;
            ItemsList.Focus(FocusState.Programmatic);
        }
        else if (MessagePanel.Visibility == Visibility.Visible && RetryButton.Visibility == Visibility.Visible)
        {
            RetryButton.Focus(FocusState.Programmatic);
        }
        else
        {
            Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Stops what is loading; the window leaves the view.</summary>
    internal void Stop()
    {
        _cts?.Cancel();
        _generation++;
        _loading = false;
    }

    private void OpenFolder(string path, string? selectName = null)
    {
        _path = SitesBrowseRules.Normalize(path);
        _selectAfter = selectName;
        _ = LoadAsync(_path, refresh: false, append: false);
    }

    private async Task LoadAsync(string? path, bool refresh, bool append)
    {
        if (_site is not { } site || Browse is null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var generation = ++_generation;
        var offset = append ? _rows.Count : 0;
        _loading = true;
        if (append)
        {
            MoreButton.IsEnabled = false;
        }
        else
        {
            _rows.Clear();
            _total = 0;
            ShowMessage("Loading…", busy: true, retry: false, options: false);
        }

        SyncChrome();
        StateChanged?.Invoke();

        SiteBrowseResultDto result;
        try
        {
            result = await Browse(new SiteBrowseRequest
            {
                Id = site.Id,
                Path = path,
                Offset = offset,
                Limit = SitesBrowseRules.PageSize,
                Refresh = refresh,
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            result = new SiteBrowseResultDto { Message = ex.Message };
        }

        // A newer request, another site, or the view left: this answer is no longer wanted.
        if (generation != _generation || _site?.Id != site.Id) return;
        _loading = false;

        if (!result.Ok)
        {
            _failed = true;
            MoreButton.IsEnabled = true;
            if (!append) _rows.Clear();
            var words = SitesBrowseRules.ProblemText(result.Problem, result.Message, result.OfferedFingerprint, result.AcceptedFingerprints);
            ShowMessage(words, busy: false, retry: true, options: SitesBrowseRules.PointsToOptions(result.Problem));
            SyncChrome();
            StateChanged?.Invoke();
            return;
        }

        _failed = false;
        _path = SitesBrowseRules.Normalize(string.IsNullOrEmpty(result.Path) ? path : result.Path);
        foreach (var item in result.Items) _rows.Add(new SiteItemRow(item));
        _total = Math.Max(result.Total, _rows.Count);
        // A page with nothing in it ends paging, so a Host that miscounts cannot keep the button asking forever.
        if (result.Items.Count == 0) _total = _rows.Count;

        if (_rows.Count == 0)
        {
            ShowMessage("This folder is empty.", busy: false, retry: false, options: false);
        }
        else
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
            ItemsList.Visibility = Visibility.Visible;
        }

        SyncChrome();
        StateChanged?.Invoke();
        if (!append) RestoreSelection();
    }

    private void RestoreSelection()
    {
        if (_rows.Count == 0)
        {
            _selectAfter = null;
            return;
        }

        var index = _selectAfter is null ? 0 : Math.Max(0, _rows.ToList().FindIndex(row => string.Equals(row.Name, _selectAfter, StringComparison.Ordinal)));
        _selectAfter = null;
        ItemsList.SelectedIndex = index;
        ItemsList.ScrollIntoView(_rows[index]);
        ItemsList.Focus(FocusState.Programmatic);
    }

    private void ShowMessage(string text, bool busy, bool retry, bool options)
    {
        MessagePanel.Visibility = Visibility.Visible;
        ItemsList.Visibility = busy || _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = text;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        OptionsButton.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
        OptionsButton.Content = "Open Options > Sites";
        LiveRegion.Announce(MessageText);
    }

    /// <summary>The breadcrumbs, the starred menu, the count line and the load-more button for what is shown now.</summary>
    private void SyncChrome()
    {
        BuildCrumbs();
        BuildStarredMenu();
        var more = !_loading && _rows.Count > 0 && SitesBrowseRules.HasMore(0, _rows.Count, _total);
        MoreButton.Visibility = more || (_loading && _rows.Count > 0 && _total > _rows.Count) ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.IsEnabled = more;
        if (_total > _rows.Count)
        {
            var left = _total - _rows.Count;
            MoreButton.Content = $"Load more ({left:N0} left)";
        }

        if (_loading && _rows.Count == 0) CountText.Text = string.Empty;
        else if (_failed && _rows.Count == 0) CountText.Text = string.Empty;
        else Say(SitesBrowseRules.CountText(_rows.Count, _total), announce: false);
    }

    private void Say(string text, bool announce = true)
    {
        CountText.Text = text;
        if (announce) LiveRegion.Announce(CountText);
    }

    private void BuildCrumbs()
    {
        CrumbsPanel.Children.Clear();
        var crumbs = SitesBrowseRules.Crumbs(_path);
        for (var index = 0; index < crumbs.Count; index++)
        {
            var crumb = crumbs[index];
            var last = index == crumbs.Count - 1;
            if (index > 1)
            {
                CrumbsPanel.Children.Add(new FontIcon { Glyph = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"] });
            }

            var text = index == 0 ? "/" : crumb.Name;
            if (last)
            {
                var current = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0), FontSize = 13 };
                AutomationProperties.SetName(current, index == 0 ? "Current folder: root" : $"Current folder: {crumb.Name}");
                CrumbsPanel.Children.Add(current);
                continue;
            }

            var button = new HyperlinkButton
            {
                Content = text,
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 13,
                Tag = crumb.Path,
            };
            AutomationProperties.SetName(button, index == 0 ? "Open the root folder" : $"Open folder {crumb.Name}");
            button.Click += Crumb_Click;
            CrumbsPanel.Children.Add(button);
        }
    }

    private void BuildStarredMenu()
    {
        StarredMenu.Items.Clear();
        var stars = SitesBrowseRules.OrderedStars(_site?.Stars);
        if (stars.Count == 0)
        {
            StarredMenu.Items.Add(new MenuFlyoutItem { Text = "No starred folders yet", IsEnabled = false });
            return;
        }

        foreach (var star in stars)
        {
            var item = new MenuFlyoutItem { Text = star, Tag = star };
            AutomationProperties.SetName(item, $"Open starred folder {star}");
            item.Click += StarredItem_Click;
            StarredMenu.Items.Add(item);
        }
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenFolder(path);
    }

    private void StarredItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenFolder(path);
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenSelectedFolder();

    private void OpenSelectedFolder()
    {
        if (ItemsList.SelectedItem is SiteItemRow { IsFolder: true } row) OpenFolder(SitesBrowseRules.Join(_path, row.Name));
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => Reload();

    private void OptionsButton_Click(object sender, RoutedEventArgs e) => OpenSitesOptions?.Invoke();

    private void MoreButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_path, refresh: false, append: true);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Back when CanGoUp:
            case VirtualKey.Up when alt && CanGoUp:
                GoUp();
                e.Handled = true;
                break;
            case VirtualKey.F5:
                Reload();
                e.Handled = true;
                break;
            case VirtualKey.Enter when e.OriginalSource is ListViewItem or ListView:
                OpenSelectedFolder();
                e.Handled = true;
                break;
        }
    }
}
