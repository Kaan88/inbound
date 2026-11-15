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
    internal SiteItemRow(SiteItemDto item, bool showsHere = false)
    {
        Name = item.Name;
        IsFolder = item.IsFolder;
        SizeText = SitesBrowseRules.SizeText(item.Size, item.IsFolder);
        ModifiedText = SitesBrowseRules.ModifiedText(item.Modified);
        HereText = SitesBrowseRules.HereText(item.Here);
        HereVisibility = showsHere ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>SITES.5: "On this PC", against the site's linked folder.</summary>
    public string HereText { get; }

    public Visibility HereVisibility { get; }

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
            if (HereText.Length > 0) parts.Add($"on this PC: {HereText}");
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

    // The Host's name for the listing the rows came from; a later page asks for that listing.
    private string? _listing;
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
        LostFocus += OnLostFocus;
        // The list takes Enter for itself (Uno marks it handled before it reaches the panel), so Enter is read on the way in.
        ItemsList.PreviewKeyDown += ItemsList_PreviewKeyDown;
        // SITES.4: Download follows what is selected.
        ItemsList.SelectionChanged += (_, _) => StateChanged?.Invoke();
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

    /// <summary>The site shown, as last saved.</summary>
    internal SiteDto? Site => _site;

    /// <summary>The names listed so far in the folder shown (for a free "New folder" name).</summary>
    internal IEnumerable<string> ShownNames => _rows.Select(row => row.Name);

    /// <summary>SITES.5: F2 and Delete on the list; the window asks and acts.</summary>
    internal event Action? RenameRequested;
    internal event Action? DeleteRequested;

    /// <summary>SITES.4: the selected files and folders of the folder shown, for Download.</summary>
    internal List<SiteTransferNameDto> SelectedNames =>
        [.. ItemsList.SelectedItems.OfType<SiteItemRow>().Select(row => new SiteTransferNameDto { Name = row.Name, IsFolder = row.IsFolder })];

    internal string Path => _path;

    // True from opening at the site's start folder until a folder has loaded: the start folder is the Host's to say
    // (null asks for it), so Try again and opening the site again ask for it again rather than for "/".
    private bool _atStart;

    /// <summary>Where opening this site again should go: the folder shown, or null while the start folder never loaded.</summary>
    internal string? ReopenPath => _atStart ? null : _path;

    // What the shown place depends on; when it changes the place is not kept.
    private string? _openedAs;

    private static string StartOf(SiteDto site) => $"{site.Host}\n{site.Port}\n{site.User}\n{site.RemoteFolder}";

    /// <summary>True when this site is shown and its server, user and start folder are the ones it was opened with.</summary>
    internal bool ShowsSameStart(SiteDto site) => _site?.Id == site.Id && StartOf(site) == _openedAs;

    internal bool CanGoUp => !SitesBrowseRules.IsRoot(_path);

    internal bool IsStarred => SitesBrowseRules.IsStarred(_site?.Stars, _path);

    internal bool IsLoading => _loading;

    /// <summary>Shows a site, at <paramref name="path"/> or, with null, at the folder the site starts in.</summary>
    internal void Open(SiteDto site, string? path = null)
    {
        _site = site;
        _openedAs = StartOf(site);
        SiteNameText.Text = site.Name;
        PlainChip.Visibility = site.Encrypted ? Visibility.Collapsed : Visibility.Visible;
        _path = path is null ? SitesBrowseRules.Root : SitesBrowseRules.Normalize(path);
        _atStart = path is null;
        _ = LoadAsync(path, refresh: false, append: false);
    }

    /// <summary>The site's saved data changed (renamed, stars): the name and the starred list follow, the folder stays.</summary>
    internal void UpdateSite(SiteDto site)
    {
        if (_site?.Id != site.Id) return;
        // Another server, user or start folder: the folder shown may not be there, so the view starts again.
        if (StartOf(site) != _openedAs)
        {
            Open(site);
            StateChanged?.Invoke();
            return;
        }

        _site = site;
        SiteNameText.Text = site.Name;
        PlainChip.Visibility = site.Encrypted ? Visibility.Collapsed : Visibility.Visible;
        SyncChrome();
        StateChanged?.Invoke();
    }

    internal void GoUp()
    {
        if (SitesBrowseRules.Parent(_path) is { } parent) OpenFolder(parent, selectName: SitesBrowseRules.LeafName(_path));
    }

    internal void Reload() => _ = LoadAsync(ReopenPath, refresh: true, append: false);

    /// <summary>Lists the folder again and selects <paramref name="selectName"/> in it (a new folder, a renamed item).</summary>
    internal void Reload(string? selectName)
    {
        _selectAfter = selectName;
        Reload();
    }

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
            // Still loading or empty: the panel itself holds the keyboard. Focusing it while it is not a tab stop would
            // give it to its first button (Starred, seen on WinUI); the load hands it on to the list or to Try again.
            IsTabStop = true;
            if (!Focus(FocusState.Programmatic)) IsTabStop = false;
        }
    }

    /// <summary>Stops what is loading; the window leaves the view.</summary>
    internal void Stop()
    {
        _cts?.Cancel();
        _generation++;
        _loading = false;
        // The view is left: the panel no longer holds the keyboard (a collapsed element's LostFocus is not relied on).
        IsTabStop = false;
    }

    private void OpenFolder(string path, string? selectName = null)
    {
        _atStart = false;
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
            // WinUI moves the keyboard off a button that is disabled (to the next tab stop, the status bar here); the
            // panel holds it while the page loads and Load more gets it back.
            if (XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), MoreButton))
            {
                IsTabStop = true;
                if (!Focus(FocusState.Programmatic)) IsTabStop = false;
            }

            MoreButton.IsEnabled = false;
        }
        else
        {
            HoldKeyboard();
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
                Listing = append ? _listing : null,
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

        // The Host no longer has the listing the shown rows came from: the folder is read again from the top rather than
        // mixing two listings.
        if (append && !result.Ok && result.Problem == SiteProblems.ListingChanged)
        {
            _ = LoadAsync(path, refresh: false, append: false);
            return;
        }

        if (!result.Ok)
        {
            _failed = true;
            MoreButton.IsEnabled = true;
            if (!append) _rows.Clear();
            var words = SitesBrowseRules.ProblemText(result.Problem, result.Message, result.OfferedFingerprint, result.AcceptedFingerprints);
            ShowMessage(words, busy: false, retry: true, options: SitesBrowseRules.PointsToOptions(result.Problem));
            PassHeldKeyboard();
            SyncChrome();
            StateChanged?.Invoke();
            return;
        }

        _failed = false;
        _atStart = false;
        _listing = result.Listing;
        _path = SitesBrowseRules.Normalize(string.IsNullOrEmpty(result.Path) ? path : result.Path);
        var showsHere = SitesBrowseRules.ShowsHere(_site);
        HereHeader.Visibility = showsHere ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in result.Items) _rows.Add(new SiteItemRow(item, showsHere));
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
        FocusListWhenReady(_generation, tries: 5);
    }

    /// <summary>
    /// Hiding the list while a folder loads would hand the keyboard to the menu bar, where the key that started the load
    /// (Enter, Backspace) lands when it is let go and opens a menu. The panel holds the keyboard meanwhile; it is a tab
    /// stop only while it does.
    /// </summary>
    private void HoldKeyboard()
    {
        // The list, or Try again and the message about to be replaced by "Loading…" (Try again starts a load).
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused
            || !(IsInside(focused, ItemsList) || IsInside(focused, MessagePanel))) return;
        IsTabStop = true;
        if (!Focus(FocusState.Programmatic)) IsTabStop = false;
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        // WinUI raises LostFocus later than the move: one from an earlier load may arrive after the panel took the
        // keyboard again, and must not make it stop being a tab stop while it holds it.
        if (ReferenceEquals(e.OriginalSource, this) && !(XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this)))
            IsTabStop = false;
    }

    /// <summary>A load that failed while the panel held the keyboard gives it to Try again.</summary>
    private void PassHeldKeyboard()
    {
        if (XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this) && RetryButton.Visibility == Visibility.Visible)
            RetryButton.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The list was hidden while the folder loaded, which took the keyboard away from it (Uno gives it to the menu bar).
    /// The selected row can take it back only once it is laid out again, and a focus moved by the hiding can land after a
    /// first try, so it is tried again on later turns until the keyboard is in the list, a few times at most.
    /// </summary>
    private void FocusListWhenReady(int generation, int tries, DependencyObject? seen = null)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusRowStep(generation, tries, seen));
    }

    private void FocusRowStep(int generation, int tries, DependencyObject? seen)
    {
        if (generation != _generation || ItemsList.Visibility != Visibility.Visible || XamlRoot is null) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        // A dialog or an open menu holds the keyboard: it is the user's.
        if (focused is not null && IsInOpenPopup(focused)) return;
        // The first step takes the keyboard back from wherever hiding the list or Load more sent it (Uno: the menu bar;
        // WinUI: the next tab stop, the sidebar or the status bar). A later step finding it outside the panel somewhere
        // the earlier step did not leave it means the user moved it: it stays there. Leaving the view or opening another
        // place ends the chain through the generation.
        if (seen is not null && focused is not null && !ReferenceEquals(focused, seen) && !IsInside(focused, this)) return;
        // The selected row itself: a row scrolled away is reused for another item and loses the keyboard with it.
        var row = ItemsList.SelectedIndex >= 0 ? ItemsList.ContainerFromIndex(ItemsList.SelectedIndex) as Control : null;
        if (row is null && ItemsList.SelectedIndex >= 0 && tries > 1)
        {
            // Not laid out yet (a list shown for the first time has no rows until its first layout pass, which can come
            // after several turns): try again after the next layout pass, or after a short wait if none comes.
            var done = false;
            var timer = DispatcherQueue.CreateTimer();
            void Next()
            {
                if (done) return;
                done = true;
                ItemsList.LayoutUpdated -= Laid;
                timer.Stop();
                FocusListWhenReady(generation, tries - 1, focused);
            }

            void Laid(object? sender, object args) => Next();
            ItemsList.LayoutUpdated += Laid;
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Next();
            timer.Start();
            return;
        }

        if (row is not null && focused is not null && IsInside(focused, row)) return;
        if (row?.Focus(FocusState.Programmatic) != true) ItemsList.Focus(FocusState.Programmatic);
        if (tries > 1) FocusListWhenReady(generation, tries - 1, XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container)) return true;
        }

        return false;
    }

    // Dialogs, menus and flyouts are shown in popups, each with its own tree.
    private bool IsInOpenPopup(DependencyObject element)
    {
        if (XamlRoot is null) return false;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is { } child && IsInside(element, child)) return true;
        }

        return false;
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
            var item = new MenuFlyoutItem { Text = SitesBrowseRules.StarLabel(star, stars), Tag = star };
            ToolTipService.SetToolTip(item, star);
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

    private void ItemsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.F2 when ItemsList.SelectedItems.Count == 1:
                RenameRequested?.Invoke();
                e.Handled = true;
                break;
            case VirtualKey.Delete when ItemsList.SelectedItems.Count > 0:
                DeleteRequested?.Invoke();
                e.Handled = true;
                break;
            case VirtualKey.Enter when ItemsList.SelectedItems.Count == 1 && ItemsList.SelectedItem is SiteItemRow { IsFolder: true }:
                OpenSelectedFolder();
                e.Handled = true;
                break;
            // Uno's list does not move on Home and End; WinUI's does the same thing.
            case VirtualKey.Home or VirtualKey.End when _rows.Count > 0:
                var index = e.Key == VirtualKey.Home ? 0 : _rows.Count - 1;
                ItemsList.SelectedIndex = index;
                ItemsList.ScrollIntoView(_rows[index]);
                FocusListWhenReady(_generation, tries: 5);
                e.Handled = true;
                break;
        }
    }

    private void OpenSelectedFolder()
    {
        if (ItemsList.SelectedItem is SiteItemRow { IsFolder: true } row) OpenFolder(SitesBrowseRules.Join(_path, row.Name));
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => Reload();

    private void OptionsButton_Click(object sender, RoutedEventArgs e) => OpenSitesOptions?.Invoke();

    private async void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = _rows.Count;
            var generation = _generation + 1;
            await LoadAsync(_path, refresh: false, append: true);
            // Another folder or site was opened meanwhile: its rows are not the ones this page added.
            if (generation != _generation) return;
            // More is left and the panel held the keyboard for the load: back to Load more, for the next page.
            if (MoreButton.Visibility == Visibility.Visible && MoreButton.IsEnabled && XamlRoot is not null
                && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this))
            {
                MoreButton.Focus(FocusState.Programmatic);
                return;
            }

            // The last page took the button (and the keyboard with it) away: the keyboard goes to the first new row.
            if (MoreButton.Visibility == Visibility.Collapsed && ItemsList.Visibility == Visibility.Visible && _rows.Count > start)
            {
                ItemsList.SelectedIndex = start;
                ItemsList.ScrollIntoView(_rows[start]);
                FocusListWhenReady(_generation, tries: 5);
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Loading more of a site folder failed: {ex.GetType().Name}");
        }
    }

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
        }
    }
}
