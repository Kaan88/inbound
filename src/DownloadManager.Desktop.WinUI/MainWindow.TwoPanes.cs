using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.5 / SITES.11 R2a (remote-sites.md, "Clients"; sites-rework.md `browse`, `remote-only`): the view switch row
/// ("Two panes | Server only", with the linked-folders line), and the panes under it: a folder of this PC on the left, the
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
    private string? _followedSiteId;
    private string? _followedServerPath;

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
            _panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            _panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _panesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_panesGrid, 1);
            SitesHost.Children.Add(_panesGrid);
        }

        Grid.SetColumn(panel, 2);
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
        _serverOnlyChoice = ViewChoice("Server only");
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
    private static ToggleButton ViewChoice(string text)
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

    private void SitesTwoPanesButton_Click(object sender, RoutedEventArgs e) => SetTwoPanes(!_uiState.SitesTwoPanes);

    /// <summary>The view switch: Two panes (true) or Server only (false). Remembered.</summary>
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
        SitesTwoPanesLabel.Text = _uiState.SitesTwoPanes ? "One pane" : "Two panes";
        AutomationProperties.SetName(SitesTwoPanesButton, _uiState.SitesTwoPanes ? "One pane" : "Two panes");
        SitesTwoPanesButton.IsEnabled = TwoPanesOffered;
        ToolTipService.SetToolTip(SitesTwoPanesButton, !TwoPanesOffered
            ? "Two panes shows this PC's folders, so it is off while the window shows another server"
            : _uiState.SitesTwoPanes ? "Show only the server's folder" : "Show a folder of this PC beside the server's");

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

        if (_localPane is { } local) local.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
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
        Grid.SetColumn(local, 0);
        grid.Children.Add(local);
        _localPane = local;

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
        ToolTipService.SetToolTip(_linkToggle, "On: this PC's folder follows the server's folder when the site has a linked folder");
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
        // The linked-folders line follows the site (also in Server only).
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
        var ready = TwoPanesShown && !panel.IsLoading && panel.SiteId is not null && local.Path.Length > 0;
        _uploadArrow.IsEnabled = ready && local.Selected.Count > 0;
        _downloadArrow.IsEnabled = ready && panel.SelectedNames.Count > 0;
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

    private async Task UploadFromPaneAsync()
    {
        if (_sitesPanel is not { Site: { } site } panel || _localPane is not { } local) return;
        var names = local.Selected.Select(entry => new SiteTransferNameDto { Name = entry.Name, IsFolder = entry.IsFolder }).ToList();
        if (names.Count == 0 || local.Path.Length == 0) return;
        await UploadAsync(site, panel.Path, local.Path, names);
    }

    private async Task DownloadToPaneAsync()
    {
        if (_sitesPanel is not { Site: { } site } panel || _localPane is not { } local) return;
        var names = panel.SelectedNames;
        if (names.Count == 0 || local.Path.Length == 0) return;
        var choice = await AskTransferAsync($"Download {SiteTransferRules.WhatText(names)} from {site.Name}", local.Path, folderEditable: false,
            $"From {panel.Path}", primary: "Download", header: "Save in this folder on this PC");
        if (choice is not { } chosen) return;
        await StartSiteTransferAsync(new SiteTransferStartRequest
        {
            SiteId = site.Id, Direction = "download", RemoteFolder = panel.Path, LocalFolder = local.Path, Names = names,
            Conflict = chosen.Conflict,
        });
    }

    /// <summary>Delete in the This PC pane: asks, then sends the items to the Recycle Bin (Windows only).</summary>
    private async Task DeleteLocalAsync()
    {
        if (!TwoPanesShown || _localPane is not { Visibility: Visibility.Visible } local || local.Selected is not { Count: > 0 } selected) return;
        if (!OperatingSystem.IsWindows())
        {
            await ShowSitesMessageAsync("Delete", "Deleting on this PC is offered on Windows only; use the file manager.");
            return;
        }

        var paths = selected.Select(entry => Path.Combine(local.Path, entry.Name)).ToList();
        if (paths.FirstOrDefault(path => SiteTransferRules.LocalInUse(_siteTransfers, path)) is { } used)
        {
            await ShowSitesMessageAsync("Delete", $"A site transfer still uses {Path.GetFileName(used)}. Cancel or finish the transfer first.");
            return;
        }

        var what = selected.Count == 1 ? $"“{selected[0].Name}”" : $"these {selected.Count} items";
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Move to the Recycle Bin?",
            Content = new TextBlock { Text = $"Move {what} from {local.Path} to the Recycle Bin?", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var failed = await Task.Run(() =>
        {
            var problems = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                    else if (File.Exists(path))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException)
                {
                    problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            return problems;
        });

        local.Reload();
        if (failed.Count > 0) await ShowSitesMessageAsync("Delete", "Not moved to the Recycle Bin:\n" + string.Join("\n", failed.Take(10)));
    }
}
