using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.5 (remote-sites.md, "Clients"): the Two panes view. A folder of this PC on the left, the server's on the right,
/// arrows between them. The arrows start the same Host transfers as Download and Upload; the left pane only reads and
/// (on Windows) sends to the Recycle Bin. It is offered for this PC's own Host only: another server's sites move files
/// to and from that server's disks, not this PC's.
/// </summary>
public sealed partial class MainWindow
{
    private LocalPane? _localPane;
    private StackPanel? _paneArrows;
    private Button? _uploadArrow;
    private Button? _downloadArrow;
    private string? _followedSiteId;
    private string? _followedServerPath;

    private bool TwoPanesOffered => !_connection.IsRemote;

    private bool TwoPanesShown => _uiState.SitesTwoPanes && TwoPanesOffered;

    private void SitesTwoPanesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TwoPanesOffered) return;
        _uiState.SitesTwoPanes = !_uiState.SitesTwoPanes;
        try
        {
            UiStateStore.Save(_uiState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiTrace.Write($"Saving the Two panes choice failed: {ex.GetType().Name}");
        }

        ApplyTwoPanes();
        if (_uiState.SitesTwoPanes) _localPane?.FocusList();
        else _sitesPanel?.FocusList();
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

        if (shown) EnsureTwoPanes();
        if (SitesHost.ColumnDefinitions.Count == 3)
        {
            SitesHost.ColumnDefinitions[0].Width = shown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SitesHost.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }

        if (_localPane is { } local) local.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (_paneArrows is { } arrows) arrows.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown && panel.Site is { } site) FollowSite(site, panel.Path);
        UpdateTwoPanesArrows();
    }

    private void EnsureTwoPanes()
    {
        if (_localPane is not null || _sitesPanel is not { } panel) return;
        SitesHost.ColumnDefinitions.Clear();
        SitesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SitesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SitesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(panel, 2);

        var local = new LocalPane { BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InboundDividerBrush"], BorderThickness = new Thickness(0, 0, 1, 0) };
        local.StateChanged += UpdateTwoPanesArrows;
        local.DeleteRequested += () => _ = DeleteLocalAsync();
        Grid.SetColumn(local, 0);
        SitesHost.Children.Add(local);
        _localPane = local;

        _uploadArrow = ArrowButton("", "Upload to the server", "Upload the items selected on this PC into the server folder on the right");
        _uploadArrow.Click += (_, _) => _ = UploadFromPaneAsync();
        _downloadArrow = ArrowButton("", "Download to this PC", "Download the items selected on the server into the folder on the left");
        _downloadArrow.Click += (_, _) => _ = DownloadToPaneAsync();
        _paneArrows = new StackPanel
        {
            Spacing = 8, Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = local.BorderBrush, BorderThickness = new Thickness(0, 0, 1, 0),
        };
        _paneArrows.Children.Add(_uploadArrow);
        _paneArrows.Children.Add(_downloadArrow);
        Grid.SetColumn(_paneArrows, 1);
        SitesHost.Children.Add(_paneArrows);
    }

    private static Button ArrowButton(string glyph, string name, string tip)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Padding = new Thickness(8, 6, 8, 6), IsEnabled = false };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    private void UpdateTwoPanesArrows()
    {
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

    private void FollowSite(SiteDto site, string serverPath)
    {
        if (_localPane is not { } local) return;
        var newSite = _followedSiteId != site.Id;
        if (!newSite && _followedServerPath == serverPath && local.Path.Length > 0) return;
        _followedSiteId = site.Id;
        _followedServerPath = serverPath;
        if (serverPath.Length > 0 && LocalListing.LinkedFolder(site.LocalFolder, site.RemoteFolder, serverPath) is { } linked && Directory.Exists(linked))
        {
            if (!string.Equals(local.Path, linked, StringComparison.OrdinalIgnoreCase)) local.Open(linked);
            return;
        }

        // Not linked, or no matching folder here: a new site opens at its linked folder or the download folder;
        // otherwise the pane stays where it is.
        if (!newSite && local.Path.Length > 0) return;
        var start = new[] { site.LocalFolder, LocalSettings?.DefaultDownloadDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
            .FirstOrDefault(folder => !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && Directory.Exists(folder));
        if (start is not null && (local.Path.Length == 0 || newSite && site.LocalFolder is { Length: > 0 })) local.Open(start);
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
