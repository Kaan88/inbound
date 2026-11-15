using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.4 (remote-sites.md, "Clients"): Download and Upload on the Sites toolbar, the Transfers bar in the speed graph's
/// place, the status chip and the sidebar marks. The batches come from this PC's Host (pushed on change); the buttons
/// act through the provider client, as browsing does.
/// </summary>
public sealed partial class MainWindow
{
    private List<SiteTransferDto> _siteTransfers = [];
    private bool _transfersBarWired;

    private void WireSiteTransfers(HostConnection connection)
    {
        connection.SiteTransfersChanged += change => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection == _local) ApplySiteTransfers(change.Transfers);
        });

        if (_transfersBarWired) return;
        _transfersBarWired = true;
        TransfersBar.Act = (type, id) => _remote.SiteTransferActionAsync(type, id);
        TransfersBar.Details = id => _remote.GetSiteTransferAsync(id);
    }

    /// <summary>Asks for the batches once per connection; later changes are pushed.</summary>
    private async Task ReadSiteTransfersAsync(HostConnection connection)
    {
        try
        {
            var transfers = connection.HostHasSites ? await connection.ListSiteTransfersAsync() ?? [] : [];
            if (connection == _local) ApplySiteTransfers(transfers);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            UiTrace.Write($"Reading the site transfers failed: {ex.GetType().Name}");
        }
    }

    private void ApplySiteTransfers(List<SiteTransferDto> transfers)
    {
        var before = _siteTransfers;
        _siteTransfers = transfers;
        // An upload into the folder on show finished, or a download that changes its "On this PC" column: list it again.
        if (_sitesPanel is { Site: { } shown } panel && panel.Visibility == Visibility.Visible
            && (SiteTransferRules.UploadLanded(before, transfers, shown.Id, panel.Path)
                || SitesBrowseRules.ShowsHere(shown) && SiteTransferRules.DownloadLandedFrom(before, transfers, shown.Id)))
        {
            panel.Reload();
        }

        // A download into the folder the This PC pane shows finished: read it again.
        if (_localPane is { } local && TwoPanesShown && _sitesShown && local.Path.Length > 0
            && SiteTransferRules.DownloadLanded(before, transfers, local.Path))
        {
            local.Reload();
        }

        TransfersBar.Show(transfers);
        UpdateSitesChip();
        SyncSiteMarks();
        UpdatePauseAllButton();
    }

    /// <summary>Pause transfers, or Resume transfers when every batch is paused; off with nothing to act on.</summary>
    private void UpdatePauseAllButton()
    {
        var bulk = SiteTransferRules.BulkPause(_siteTransfers);
        var label = bulk?.Label ?? "Pause transfers";
        SitesPauseAllButton.IsEnabled = bulk is not null;
        if (SitesPauseAllLabel.Text == label) return;
        SitesPauseAllLabel.Text = label;
        SitesPauseAllIcon.Glyph = bulk?.Type == SitesMessageTypes.TransferResume ? "\uE768" : "\uE769";
        AutomationProperties.SetName(SitesPauseAllButton, label);
    }

    private async void SitesPauseAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (SiteTransferRules.BulkPause(_siteTransfers) is not { } bulk) return;
        SitesPauseAllButton.IsEnabled = false;
        try
        {
            string? problem = null;
            foreach (var id in bulk.Ids)
            {
                var reply = await _remote.SiteTransferActionAsync(bulk.Type, id);
                if (!reply.Ok) problem ??= reply.Message;
            }

            if (problem is not null) await ShowSitesMessageAsync(bulk.Label, problem);
        }
        finally
        {
            UpdatePauseAllButton();
        }
    }

    /// <summary>The status chip: shown while a batch runs, waits, is paused or failed, and Sites is offered here.</summary>
    private void UpdateSitesChip()
    {
        var text = SitesOffered ? SiteTransferRules.Summary(_siteTransfers) : null;
        var visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (SitesChip.Visibility != visibility) SitesChip.Visibility = visibility;
        if (text is null || SitesChipText.Text == text) return;
        SitesChipText.Text = text;
        AutomationProperties.SetName(SitesChip, text);
    }

    /// <summary>Each sidebar site shows arrows with counts while files move there, and a clock while a batch waits.</summary>
    private void SyncSiteMarks()
    {
        foreach (var item in CategoryList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is not string tag || !tag.StartsWith(SiteTagPrefix, StringComparison.Ordinal) || item.Content is not StackPanel content) continue;
            var siteId = tag[SiteTagPrefix.Length..];
            var site = _sites.FirstOrDefault(s => s.Id == siteId);
            var mark = SiteTransferRules.SiteMark(_siteTransfers, siteId);
            var markBlock = content.Children.OfType<TextBlock>().FirstOrDefault(block => block.Tag as string == "mark");
            if (markBlock is null)
            {
                markBlock = new TextBlock { Tag = "mark", FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(markBlock);
            }

            if (markBlock.Text != (mark ?? "")) markBlock.Text = mark ?? "";
            var progress = SiteTransferRules.SiteProgress(_siteTransfers, siteId);
            var line = content.Children.OfType<ProgressBar>().FirstOrDefault(bar => bar.Tag as string == "progress");
            if (line is null && progress is not null)
            {
                line = new ProgressBar
                {
                    Tag = "progress", Width = 36, MinHeight = 0, Height = 3, Maximum = 100, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0), IsTabStop = false,
                };
                AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                content.Children.Add(line);
            }

            if (line is not null)
            {
                var visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
                if (line.Visibility != visibility) line.Visibility = visibility;
                if (progress is { } value) line.Value = value;
            }
            var words = SiteTransferRules.SiteMarkName(_siteTransfers, siteId);
            AutomationProperties.SetName(item, words is null ? $"Site {site?.Name}" : $"Site {site?.Name}, {words}");
        }
    }

    // ---- Download and Upload ----------------------------------------------------------------------------

    private async void SitesDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesPanel is not { Site: { } site } panel) return;
        var names = panel.SelectedNames;
        if (names.Count == 0) return;
        var folder = site.LocalFolder is { Length: > 0 } linked ? linked : LocalSettings?.DefaultDownloadDirectory ?? "";
        var choice = await AskTransferAsync($"Download {SiteTransferRules.WhatText(names)} from {site.Name}", folder, folderEditable: true,
            $"From {panel.Path}");
        if (choice is not { } chosen) return;
        await StartSiteTransferAsync(new SiteTransferStartRequest
        {
            SiteId = site.Id, Direction = "download", RemoteFolder = panel.Path, LocalFolder = chosen.Folder, Names = names,
            Conflict = chosen.Conflict,
        });
    }

    private async void SitesUploadFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesPanel is not { Site: { } site } panel) return;
        IReadOnlyList<string> files;
        try
        {
            files = await Pickers.PickFilesAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), ["*"], multiple: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            await ShowSitesMessageAsync("Upload", "The file picker could not open: " + ex.Message);
            return;
        }

        var (localFolder, picked, problem) = SiteTransferRules.UploadNames(files);
        if (files.Count == 0) return;
        if (problem is not null || localFolder is null)
        {
            await ShowSitesMessageAsync("Upload", problem ?? "Choose files from one folder at a time.");
            return;
        }

        await UploadAsync(site, panel.Path, localFolder, [.. picked.Select(name => new SiteTransferNameDto { Name = name })]);
    }

    private async void SitesUploadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesPanel is not { Site: { } site } panel) return;
        string? folder;
        try
        {
            folder = await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            await ShowSitesMessageAsync("Upload", "The folder picker could not open: " + ex.Message);
            return;
        }

        if (folder is null) return;
        var trimmed = folder.TrimEnd('\\', '/');
        var parent = System.IO.Path.GetDirectoryName(trimmed);
        var name = System.IO.Path.GetFileName(trimmed);
        if (parent is null || name.Length == 0)
        {
            await ShowSitesMessageAsync("Upload", "A whole drive can't be uploaded; choose a folder on it.");
            return;
        }

        await UploadAsync(site, panel.Path, parent, [new SiteTransferNameDto { Name = name, IsFolder = true }]);
    }

    private async Task UploadAsync(SiteDto site, string remoteFolder, string localFolder, List<SiteTransferNameDto> names)
    {
        var choice = await AskTransferAsync($"Upload {SiteTransferRules.WhatText(names)} to {site.Name}", remoteFolder, folderEditable: false,
            $"From {localFolder}", primary: "Upload");
        if (choice is not { } chosen) return;
        await StartSiteTransferAsync(new SiteTransferStartRequest
        {
            SiteId = site.Id, Direction = "upload", RemoteFolder = remoteFolder, LocalFolder = localFolder, Names = names,
            Conflict = chosen.Conflict,
        });
    }

    private async Task StartSiteTransferAsync(SiteTransferStartRequest request)
    {
        SitesDownloadButton.IsEnabled = false;
        SitesUploadButton.IsEnabled = false;
        try
        {
            var reply = await _remote.StartSiteTransferAsync(request);
            if (!reply.Ok)
            {
                await ShowSitesMessageAsync(request.Direction == "download" ? "Download" : "Upload",
                    SitesBrowseRules.PointsToOptions(reply.Problem)
                        ? (reply.Message ?? "The site needs attention.") + " Check it in Options > Sites."
                        : reply.Message ?? "The transfer could not start.");
                return;
            }

            if (reply.Transfer is { } added && _siteTransfers.All(t => t.Id != added.Id)) ApplySiteTransfers([.. _siteTransfers, added]);
            TransfersBar.OpenAtBest();
        }
        finally
        {
            UpdateSitesToolbar();
        }
    }

    /// <summary>The target folder (editable for a download) and "If a file is already there"; null when cancelled.</summary>
    private async Task<(string Folder, string Conflict)?> AskTransferAsync(string title, string folder, bool folderEditable, string from,
        string primary = "Download", string? header = null)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = from, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        var folderBox = new TextBox
        {
            Header = header ?? (folderEditable ? "Save in this folder on this PC" : "Into this folder on the site"),
            Text = folder, IsReadOnly = !folderEditable,
        };
        if (folderEditable)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
            browse.Click += async (_, _) =>
            {
                try
                {
                    if (await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), start: folderBox.Text) is { } picked) folderBox.Text = picked;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // The box still takes a typed folder.
                    folderBox.Header = "Folder on this PC (the folder picker could not open; type the folder)";
                }
            };
            Grid.SetColumn(browse, 1);
            row.Children.Add(folderBox);
            row.Children.Add(browse);
            panel.Children.Add(row);
        }
        else
        {
            panel.Children.Add(folderBox);
        }

        var conflict = new ComboBox { Header = "If a file is already there", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in SiteTransferRules.Conflicts) conflict.Items.Add(label);
        conflict.SelectedIndex = 0;
        panel.Children.Add(conflict);
        panel.Children.Add(new TextBlock
        {
            Text = "A file that was stopped part way starts again from the beginning.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        folderBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !folderEditable || System.IO.Path.IsPathFullyQualified(folderBox.Text.Trim());
        dialog.IsPrimaryButtonEnabled = !folderEditable || System.IO.Path.IsPathFullyQualified(folderBox.Text.Trim());
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        var index = Math.Max(0, conflict.SelectedIndex);
        return (folderEditable ? folderBox.Text.Trim() : folder, SiteTransferRules.Conflicts[index].Value);
    }

    private async Task ShowSitesMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        await DialogGuard.ShowAsync(dialog);
    }

    /// <summary>The chip opens the Transfers bar, in the Sites view (the shown site, or the one the newest batch is for).</summary>
    private void SitesChip_Click(object sender, RoutedEventArgs e)
    {
        if (!_sitesShown)
        {
            var siteId = _siteTransfers.OrderByDescending(t => t.CreatedAt).Select(t => t.SiteId)
                .FirstOrDefault(id => _sites.Any(site => site.Id == id)) ?? _sites.FirstOrDefault()?.Id;
            if (siteId is null) return;
            ShowSite(siteId);
            SyncSiteEntryPoints();
        }

        TransfersBar.OpenAtBest();
    }
}
