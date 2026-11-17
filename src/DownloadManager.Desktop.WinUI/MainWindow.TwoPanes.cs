using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.5 / SITES.11 R2a (remote-sites.md, "Clients"; sites-rework.md `browse`, `remote-only`): the view switch row
/// ("Two panes | Remote only", with the linked-folders line), and the panes under it: a folder of this PC on the left, the
/// server's on the right, and a gutter between them with Upload, Download and Linked. The arrows start the same Host
/// transfers as Download and Upload; the left pane only reads and (on Windows) sends to the Recycle Bin. Two panes is
/// offered for this PC's own Host only: another server's sites move files to and from that server's disks, not this PC's.
/// </summary>
public sealed partial class MainWindow
{
    private Grid? _switchRow;
    private Grid? _panesGrid;
    private ToggleButton? _twoPanesChoice;
    private ToggleButton? _serverOnlyChoice;
    private StackPanel? _linkedLine;
    private TextBlock? _linkedLineText;
    private LocalPane? _localPane;
    private StackPanel? _paneArrows;
    private Button? _uploadArrow;
    private Button? _downloadArrow;
    private ToggleButton? _linkToggle;
    private bool _syncingLinkToggle;
    // SITES.14: the left pane is This PC (a null id) or any remote, the same one as on the right included.
    private ComboBox? _leftSource;
    private SitesPanel? _leftRemote;
    private string? _leftSiteId;
    private bool _syncingLeftSource;
    private string? _leftSourceSignature;
    private bool _leftSourcesPending;
    private string? _followedSiteId;
    private string? _followedServerPath;

    // SITES.11 R2c: drag and drop (MainWindow.Drag.cs); built on Windows only.
    partial void WireServerDrag(SitesPanel panel);
    partial void WireLocalDrag(LocalPane local);

    private bool TwoPanesOffered => !_connection.IsRemote;

    private bool TwoPanesShown => _uiState.SitesTwoPanes && TwoPanesOffered;

    /// <summary>The panes (and the row above them) go into SitesHost, the server's pane in the right-hand column.</summary>
    private void AddSitesPanel(SitesPanel panel)
    {
        if (_panesGrid is null)
        {
            SitesHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SitesHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _switchRow = BuildSwitchRow();
            Grid.SetRow(_switchRow, 0);
            SitesHost.Children.Add(_switchRow);
            _panesGrid = new Grid { Margin = new Thickness(12, 0, 12, 12) };
            _panesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _panesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            _panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_panesGrid, 1);
            SitesHost.Children.Add(_panesGrid);
        }

        Grid.SetColumn(panel, 2);
        Grid.SetRow(panel, 1);
        _panesGrid.Children.Add(panel);
    }

    private Grid BuildSwitchRow()
    {
        var row = new Grid { Padding = new Thickness(12, 10, 12, 8), ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Toggle buttons, so UI Automation reads which view is on (Toggle state); the look stays the segmented one.
        _twoPanesChoice = ViewChoice("Two panes");
        _serverOnlyChoice = ViewChoice("Remote only");
        _twoPanesChoice.Click += (_, _) => SetTwoPanes(true);
        _serverOnlyChoice.Click += (_, _) => SetTwoPanes(false);
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        choices.Children.Add(_twoPanesChoice);
        choices.Children.Add(_serverOnlyChoice);
        var segmented = new Border
        {
            CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = choices,
        };
        InboundUi.Follow(segmented, "InboundDividerBrush", brush => segmented.BorderBrush = brush);
        InboundUi.Follow(segmented, "InboundChromeBrush", brush => segmented.Background = brush);
        AutomationProperties.SetName(segmented, "View");
        Grid.SetColumn(segmented, 0);
        row.Children.Add(segmented);

        var lineText = _linkedLineText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        InboundUi.Follow(lineText, "InboundSecondaryTextBrush", brush => lineText.Foreground = brush);
        _linkedLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right };
        var lineIcon = new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        InboundUi.Follow(lineIcon, "InboundSecondaryTextBrush", brush => lineIcon.Foreground = brush);
        _linkedLine.Children.Add(lineIcon);
        _linkedLine.Children.Add(lineText);
        Grid.SetColumn(_linkedLine, 2);
        row.Children.Add(_linkedLine);
        return row;
    }

    /// <summary>
    /// One half of the view switch: a toggle with the segmented look. The template's checked brushes are replaced by the
    /// app's tokens (the default is the accent fill); the brush objects stay and their colours follow the theme.
    /// </summary>
    internal static ToggleButton ViewChoice(string text)
    {
        var choice = new ToggleButton { Content = text, Padding = new Thickness(14, 4, 14, 4), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5) };
        AutomationProperties.SetName(choice, text);
        var selected = new SolidColorBrush();
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var text1 = new SolidColorBrush();
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            choice.Resources[$"ToggleButtonBackgroundChecked{state}"] = selected;
            choice.Resources[$"ToggleButtonBorderBrushChecked{state}"] = clear;
            choice.Resources[$"ToggleButtonForegroundChecked{state}"] = text1;
        }

        choice.Resources["ToggleButtonBackground"] = clear;
        choice.Resources["ToggleButtonBorderBrush"] = clear;
        InboundUi.Follow(choice, "InboundCategorySelectedBrush", brush =>
        {
            if (brush is SolidColorBrush solid) selected.Color = solid.Color;
        });
        // The checked half keeps the normal text colour (the default checked text is white for the accent fill).
        void Text() => text1.Color = choice.ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(0xE4, 0, 0, 0) : Microsoft.UI.Colors.White;
        Text();
        choice.ActualThemeChanged += (_, _) => Text();
        choice.Loaded += (_, _) => Text();
        return choice;
    }

    /// <summary>The view switch: Two panes (true) or Remote only (false). Remembered.</summary>
    private void SetTwoPanes(bool twoPanes)
    {
        if (twoPanes && !TwoPanesOffered) return;
        _uiState.SitesTwoPanes = twoPanes;
        SaveUiStateQuietly("the Two panes choice");
        ApplyTwoPanes();
        if (twoPanes) _localPane?.FocusList();
        else _sitesPanel?.FocusList();
    }

    private void SaveUiStateQuietly(string what)
    {
        try
        {
            UiStateStore.Save(_uiState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiTrace.Write($"Saving {what} failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Lays SitesHost out as one pane or two; safe to call again.</summary>
    private void ApplyTwoPanes()
    {
        if (_sitesPanel is not { } panel) return;
        var shown = TwoPanesShown;

        if (_twoPanesChoice is { } two && _serverOnlyChoice is { } only)
        {
            // A click on the half already on would turn it off; the state is always the view shown.
            two.IsChecked = shown;
            only.IsChecked = !shown;
            two.IsEnabled = TwoPanesOffered;
            ToolTipService.SetToolTip(two, TwoPanesOffered ? "A folder of this PC beside the server's"
                : "Two panes shows this PC's folders, so it is off while the window shows another server");
            ToolTipService.SetToolTip(only, "Only the server's folder");
        }

        if (shown) EnsureTwoPanes();
        if (_panesGrid is { ColumnDefinitions.Count: 3 } grid)
            grid.ColumnDefinitions[0].Width = shown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        if (_localPane is { } local) local.Visibility = shown && _leftSiteId is null ? Visibility.Visible : Visibility.Collapsed;
        if (_leftRemote is { } leftRemote) leftRemote.Visibility = shown && _leftSiteId is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_leftSource is { } leftSource) leftSource.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (_paneArrows is { } arrows) arrows.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        panel.ServerOnly = !shown;
        if (shown && panel.Site is { } site) FollowSite(site, panel.Path);
        UpdateTwoPanesArrows();
    }

    private void EnsureTwoPanes()
    {
        if (_localPane is not null || _sitesPanel is null || _panesGrid is not { } grid) return;
        var local = new LocalPane
        {
            GetStars = () => _uiState.LocalStarredFolders,
            SaveStars = stars =>
            {
                _uiState.LocalStarredFolders = stars;
                SaveUiStateQuietly("the starred folders of this PC");
            },
        };
        local.StateChanged += UpdateTwoPanesArrows;
        local.DeleteRequested += () => _ = DeleteLocalAsync();
        local.CommandRequested += FireLocalCommand;
        local.CanUpload = () => _sitesPanel is { IsLoading: false, SiteId: not null };
        WireLocalDrag(local);
        Grid.SetColumn(local, 0);
        Grid.SetRow(local, 1);
        grid.Children.Add(local);
        _localPane = local;

        _leftSource = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(_leftSource, "Left pane shows");
        ToolTipService.SetToolTip(_leftSource, "What the left pane shows: a folder of this PC or any remote");
        _leftSource.SelectionChanged += (_, _) => OnLeftSourceChanged();
        _leftSource.DropDownClosed += (_, _) =>
        {
            if (_leftSourcesPending) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefreshLeftSources);
        };
        Grid.SetColumn(_leftSource, 0);
        grid.Children.Add(_leftSource);
        RefreshLeftSources();

        _uploadArrow = GutterButton("", "Upload to the server", "Upload the items selected on this PC into the server folder on the right", "InboundUploadBrush");
        _uploadArrow.Click += (_, _) => _ = UploadFromPaneAsync();
        _downloadArrow = GutterButton("", "Download to this PC", "Download the items selected on the server into the folder on the left", "InboundIconInfoBrush");
        _downloadArrow.Click += (_, _) => _ = DownloadToPaneAsync();
        _linkToggle = new ToggleButton
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 }, Width = 30, Height = 30, Padding = new Thickness(0),
            IsChecked = _uiState.SitesLinked,
        };
        AutomationProperties.SetName(_linkToggle, "Linked folders");
        ToolTipService.SetToolTip(_linkToggle, "On: this PC's folder follows the server's folder when the remote has a linked folder");
        _linkToggle.Click += (_, _) =>
        {
            if (_syncingLinkToggle) return;
            _uiState.SitesLinked = _linkToggle.IsChecked == true;
            SaveUiStateQuietly("the Linked choice");
            // Turned on: this PC's pane goes to the folder that matches the server's now.
            _followedServerPath = null;
            if (_uiState.SitesLinked && _sitesPanel is { Site: { } site } panel) FollowSite(site, panel.Path);
            UpdateTwoPanesArrows();
        };
        // Beside the first rows, as in the mockup (below the cards' header, path row and column titles).
        _paneArrows = new StackPanel { Spacing = 6, Margin = new Thickness(8, 100, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        _paneArrows.Children.Add(_uploadArrow);
        _paneArrows.Children.Add(_downloadArrow);
        _paneArrows.Children.Add(_linkToggle);
        Grid.SetColumn(_paneArrows, 1);
        Grid.SetRow(_paneArrows, 1);
        grid.Children.Add(_paneArrows);
        local.SyncBar();
    }

    private static Button GutterButton(string glyph, string name, string tip, string brushKey)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 13 };
        InboundUi.Follow(icon, brushKey, brush => icon.Foreground = brush);
        var button = new Button
        {
            Content = icon,
            Width = 30, Height = 30, Padding = new Thickness(0), IsEnabled = false,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    private void UpdateTwoPanesArrows()
    {
        // The linked-folders line follows the site (also in Remote only).
        if (_linkedLine is not null && _linkedLineText is not null)
        {
            var line = _sitesPanel?.Site is { } shownSite ? SitesBrowseRules.LinkedLine(shownSite.LocalFolder, shownSite.RemoteFolder) : null;
            _linkedLine.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
            _linkedLineText.Text = line ?? string.Empty;
        }

        if (_linkToggle is { } toggle)
        {
            _syncingLinkToggle = true;
            toggle.IsChecked = _uiState.SitesLinked;
            toggle.IsEnabled = _sitesPanel?.Site is { } linkedSite && SitesBrowseRules.ShowsHere(linkedSite);
            _syncingLinkToggle = false;
        }

        if (_uploadArrow is null || _downloadArrow is null || _sitesPanel is not { } panel || _localPane is not { } local) return;
        if (_leftSiteId is not null && _leftRemote is { } leftRemote)
        {
            // Remote on both sides: a copy between them, either way.
            var both = TwoPanesShown && !panel.IsLoading && panel.SiteId is not null && !leftRemote.IsLoading && leftRemote.SiteId is not null;
            _uploadArrow.IsEnabled = both && leftRemote.SelectedNames.Count > 0;
            _downloadArrow.IsEnabled = both && panel.SelectedNames.Count > 0;
            ToolTipService.SetToolTip(_uploadArrow, "Copy what is selected on the left into the folder on the right");
            ToolTipService.SetToolTip(_downloadArrow, "Copy what is selected on the right into the folder on the left");
            SyncDeleteForGoodMenu();
            return;
        }

        ToolTipService.SetToolTip(_uploadArrow, "Upload the items selected on this PC into the server folder on the right");
        ToolTipService.SetToolTip(_downloadArrow, "Download the items selected on the server into the folder on the left");
        var ready = TwoPanesShown && !panel.IsLoading && panel.SiteId is not null && local.Path.Length > 0;
        _uploadArrow.IsEnabled = ready && local.Selected.Count > 0;
        _downloadArrow.IsEnabled = ready && panel.SelectedNames.Count > 0;
        SyncDeleteForGoodMenu();
    }

    /// <summary>The Remote menu's "Delete permanently on This PC…": shown with two panes, on while This PC has a selection (Windows only).</summary>
    private void SyncDeleteForGoodMenu()
    {
        var shown = TwoPanesShown && _localPane is { Visibility: Visibility.Visible };
        SiteMenuDeleteForGood.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        SiteMenuDeleteForGood.IsEnabled = shown && OperatingSystem.IsWindows() && _localPane is { Path.Length: > 0 } local && local.Selected.Count > 0;
    }

    /// <summary>The server pane moved: a linked site's This PC pane goes to the matching folder when it is there.</summary>
    private void FollowServerFolder()
    {
        if (!TwoPanesShown || _sitesPanel is not { Site: { } site } panel) return;
        FollowSite(site, panel.Path);
    }

    private int _followGeneration;

    private void FollowSite(SiteDto site, string serverPath) => _ = FollowSiteAsync(site, serverPath);

    private async Task FollowSiteAsync(SiteDto site, string serverPath)
    {
        if (_localPane is not { } local) return;
        var newSite = _followedSiteId != site.Id;
        if (!newSite && _followedServerPath == serverPath && local.TargetPath.Length > 0) return;
        _followedSiteId = site.Id;
        _followedServerPath = serverPath;
        var generation = ++_followGeneration;
        var linked = _uiState.SitesLinked && serverPath.Length > 0 ? LocalListing.LinkedFolder(site.LocalFolder, site.RemoteFolder, serverPath) : null;
        var candidates = new[] { site.LocalFolder, LocalSettings?.DefaultDownloadDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        // Directory.Exists can wait on a share or a sleeping disk: off the UI thread; a newer move of the server pane wins.
        var (linkedHere, start) = await Task.Run(() => (
            linked is not null && Directory.Exists(linked),
            candidates.FirstOrDefault(folder => !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && Directory.Exists(folder))));
        if (generation != _followGeneration || !ReferenceEquals(_localPane, local)) return;

        // The Linked toggle is off: the This PC pane stays where the user left it (it only gets a first folder).
        if (linkedHere && linked is not null)
        {
            if (!string.Equals(local.TargetPath, linked, StringComparison.OrdinalIgnoreCase)) local.Open(linked);
            return;
        }

        // Not linked, or no matching folder here: a new site opens at its linked folder or the download folder;
        // otherwise the pane stays where it is.
        if (!newSite && local.TargetPath.Length > 0) return;
        if (!_uiState.SitesLinked && local.TargetPath.Length > 0) return;
        if (start is not null && (local.TargetPath.Length == 0 || newSite && site.LocalFolder is { Length: > 0 })) local.Open(start);
    }

    // ---- The left pane's source (SITES.14) --------------------------------------------------------------

    /// <summary>
    /// "This PC" and every remote, with the choice kept; called when the remotes change. Only a change of the remotes
    /// (added, removed, renamed) touches the items, and then in place. The items used to be cleared and made again on the
    /// next sites broadcast after a choice (the choice was part of the signature), which comes within moments of opening
    /// the remote: the next layout (the right pane's path bar forces one) failed with E_UNEXPECTED and ended the window
    /// (stowed exception; SITES.14 owner report, 2026-11-17).
    /// </summary>
    private void RefreshLeftSources()
    {
        if (_leftSource is not { } box) return;
        var signature = string.Join(';', _sites.Select(site => site.Id + "|" + site.Name));
        if (signature == _leftSourceSignature && box.Items.Count > 0) return;
        // An open list is not changed under the pointer: it is brought up to date once it has closed.
        if (box.IsDropDownOpen)
        {
            _leftSourcesPending = true;
            return;
        }

        _leftSourcesPending = false;
        _leftSourceSignature = signature;
        var wanted = new List<(string Text, string? Id)> { ("This PC", null) };
        wanted.AddRange(_sites.Select(site => (site.Name, (string?)site.Id)));
        var selected = _leftSiteId is null ? 0 : Math.Max(0, wanted.FindIndex(entry => entry.Id == _leftSiteId));
        _syncingLeftSource = true;
        try
        {
            var selectedTextChanged = false;
            for (var index = 0; index < wanted.Count; index++)
            {
                var (text, id) = wanted[index];
                if (index < box.Items.Count && box.Items[index] is ComboBoxItem item)
                {
                    if (!Equals(item.Content, text))
                    {
                        item.Content = text;
                        selectedTextChanged |= index == box.SelectedIndex;
                    }

                    item.Tag = id;
                }
                else
                {
                    box.Items.Add(new ComboBoxItem { Content = text, Tag = id });
                }
            }

            while (box.Items.Count > wanted.Count) box.Items.RemoveAt(box.Items.Count - 1);

            // The remote on the left was removed: the pane goes back to This PC.
            if (_leftSiteId is not null && selected == 0)
            {
                _leftSiteId = null;
                _leftRemote?.Stop();
            }

            // The closed box keeps showing the words the item had when it was chosen: a renamed choice is chosen again.
            if (selectedTextChanged) box.SelectedIndex = -1;
            if (box.SelectedIndex != selected) box.SelectedIndex = selected;
        }
        finally
        {
            _syncingLeftSource = false;
        }

        ApplyTwoPanes();
    }

    private void OnLeftSourceChanged()
    {
        if (_syncingLeftSource || _leftSource?.SelectedItem is not ComboBoxItem item) return;
        var id = item.Tag as string;
        if (id == _leftSiteId) return;
        _leftSiteId = id;
        if (id is not null && FindSite(id) is { } site)
        {
            var panel = EnsureLeftRemote();
            panel.Open(site, site.LastFolder is { Length: > 0 } last ? last : null);
        }
        else _leftRemote?.Stop();

        ApplyTwoPanes();
        UpdateTwoPanesArrows();
    }

    /// <summary>The left pane as a remote: the same browser as the right one, wired for browsing and for the arrows.</summary>
    private SitesPanel EnsureLeftRemote()
    {
        if (_leftRemote is { } existing) return existing;
        var panel = new SitesPanel
        {
            Browse = BrowseAskingAsync,
            Star = request => _remote.StarSiteFolderAsync(request),
            HereLookup = (request, token) => _remote.SiteHereAsync(request, token),
            OpenSitesOptions = OpenSitesOptionsHook,
            FixSite = (site, problem) => _ = FixSiteAsync(site, problem),
            Visibility = Visibility.Collapsed,
        };
        panel.EditSiteRequested += site => _ = EnsureSiteFlows().SaveFormAsync(site);
        panel.StateChanged += UpdateTwoPanesArrows;
        panel.StateChanged += () => RememberPlace(panel);
        panel.SiteUpdated += updated =>
        {
            var at = _sites.FindIndex(site => site.Id == updated.Id);
            if (at >= 0) _sites[at] = updated;
        };
        panel.CommandRequested += command =>
        {
            switch (command)
            {
                case SiteCommand.Download when _sitesPanel is { } right:
                    _ = CopyBetweenAsync(panel, right);
                    break;
                case SiteCommand.Refresh:
                    panel.Refresh();
                    break;
                case SiteCommand.Preview:
                    _ = PreviewSelectedAsync(panel);
                    break;
                case SiteCommand.CopyPath:
                    CopyText(SitesCommandRules.Paths(panel.Path, [.. panel.SelectedNames.Select(name => name.Name)]), "Path copied");
                    break;
                default:
                    // F2 or Delete on the keyboard: the left pane's own menu offers no change.
                    SetSitesStatus("Rename, delete and the other changes are done in the right pane. Choose the remote there.");
                    break;
            }
        };
        // Its own menu (SitesCommandRules.LeftRemoteMenu), which the panel builds anew on each right click.
        panel.CopyPane = true;
        panel.ServerOnly = false;
        Grid.SetColumn(panel, 0);
        Grid.SetRow(panel, 1);
        _panesGrid?.Children.Add(panel);
        _leftRemote = panel;
        return panel;
    }

    private async Task UploadFromPaneAsync()
    {
        if (_leftSiteId is not null && _leftRemote is { } leftRemote && _sitesPanel is { } rightPanel)
        {
            await CopyBetweenAsync(leftRemote, rightPanel);
            return;
        }

        if (_sitesPanel is not { Site: { } site } panel || _localPane is not { } local) return;
        var names = local.Selected.Select(entry => new SiteTransferNameDto { Name = entry.Name, IsFolder = entry.IsFolder }).ToList();
        if (names.Count == 0 || local.Path.Length == 0) return;
        await UploadAsync(site, panel.Path, local.Path, names);
    }

    private Task DownloadToPaneAsync() => OnceAsync(DownloadToPaneCoreAsync);

    private async Task DownloadToPaneCoreAsync()
    {
        if (_leftSiteId is not null && _leftRemote is { } leftRemote && _sitesPanel is { } rightPanel)
        {
            await CopyBetweenCoreAsync(rightPanel, leftRemote);
            return;
        }

        if (_sitesPanel is not { Site: { } site } panel || _localPane is not { } local) return;
        var names = panel.SelectedNames;
        if (names.Count == 0 || local.Path.Length == 0) return;
        // The arrow aims at the folder of the This PC pane: no dialog, and the batch is asked about only when something is there.
        await DownloadNamesAsync(site, panel.Path, names, local.Path);
    }

    /// <summary>
    /// Delete in the This PC pane: asks, then sends the items to the Recycle Bin. With <paramref name="forGood"/> (Shift+Delete,
    /// "Delete permanently…"): asks with the count and the tick, then deletes them for good; an item in use is left and named.
    /// Windows only.
    /// </summary>
    private async Task DeleteLocalAsync(bool forGood = false)
    {
        if (!TwoPanesShown || _localPane is not { Visibility: Visibility.Visible } local || local.Path.Length == 0) return;
        if (local.Selected is not { Count: > 0 } selected)
        {
            if (forGood) local.Note("Select what to delete in This PC first.", reload: false);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            local.Note("Deleting on this PC is offered on Windows only; use the file manager.", reload: false);
            return;
        }

        var folder = local.Path;
        var paths = selected.Select(entry => Path.Combine(folder, entry.Name)).ToList();
        string? Busy() => paths.FirstOrDefault(path => SitesCommandRules.LocalItemBusy(_siteTransfers, folder, Path.GetFileName(path))) is { } used
            ? $"A remote transfer still uses {Path.GetFileName(used)}. Cancel or finish the transfer first."
            : null;
        if (Busy() is { } busy)
        {
            local.Note(busy, reload: false);
            return;
        }

        // SITES.11 R5: the same shape as Delete on server; the Recycle Bin, a network folder (Windows may delete outright, and
        // asks), or for good with the count of what goes.
        var place = forGood ? SiteChangeRules.DeletePlace.ForGood : SiteChangeRules.LocalDeletePlace(folder, OnNetworkDrive(folder));
        Func<CancellationToken, Task<DownloadManager.Contracts.SiteCountResultDto>>? count = forGood
            ? token => Task.Run(() => LocalRecycle.Count(paths, token), token)
            : null;
        var chosen = selected.Select(entry => new DownloadManager.Contracts.SiteTransferNameDto { Name = entry.Name, IsFolder = entry.IsFolder }).ToList();
        if (!await ConfirmDeleteAsync(chosen, place, remoteName: null, count)) return;

        // The dialog was up a while: a transfer may have started on one of them since.
        if (Busy() is { } busyNow)
        {
            local.Note(busyNow, reload: false);
            return;
        }

        string? note;
        try
        {
            if (forGood)
            {
                var (left, cutShort) = await Task.Run(() => LocalRecycle.DeleteForGood(folder, paths));
                note = SiteChangeRules.LocalDeleteNote(place, left, cutShort);
            }
            else
            {
                note = SiteChangeRules.LocalDeleteNote(place, await LocalRecycle.RecycleAsync(paths, WindowHandle));
            }
        }
        catch (Exception ex)
        {
            // Never lost: what happened is said, and the folder is read again to show what is still there.
            note = $"The delete stopped: {ex.Message}";
        }

        if (note is not null) local.Note(note, reload: true);
        else local.Reload();
    }

    /// <summary>Whether a folder is on a mapped network drive (a UNC path is told by its spelling).</summary>
    private static bool OnNetworkDrive(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            return root is { Length: > 0 } && !SiteChangeRules.IsUncPath(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
