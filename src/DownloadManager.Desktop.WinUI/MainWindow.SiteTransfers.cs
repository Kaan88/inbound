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
            // What is on this PC changed: the "On this PC" answers are asked for again, not reused.
            panel.Refresh();
        }

        // A download into the folder the This PC pane shows finished: read it again.
        if (_localPane is { } local && TwoPanesShown && _sitesShown && local.Path.Length > 0
            && SiteTransferRules.DownloadLanded(before, transfers, local.Path))
        {
            local.Reload();
        }

        ResolveOpenWaits(transfers);
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
        SiteMenuPause.IsEnabled = bulk is not null;
        SiteMenuPause.Text = label;
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

    // SITES.11 R2b: no dialog; to the linked folder, or by the Host's category rules (SiteDownloadAsync).
    private void SitesDownloadButton_Click(object sender, RoutedEventArgs e) => _ = SiteDownloadAsync(askFolder: false);

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

    /// <summary>
    /// Uploads <paramref name="names"/> into <paramref name="remoteFolder"/>. The names are checked against the server listing
    /// shown (what is loaded so far); when some are there the batch is asked about once.
    /// </summary>
    private Task UploadAsync(SiteDto site, string remoteFolder, string localFolder, List<SiteTransferNameDto> names) =>
        OnceAsync(() => UploadCoreAsync(site, remoteFolder, localFolder, names));

    private async Task UploadCoreAsync(SiteDto site, string remoteFolder, string localFolder, List<SiteTransferNameDto> names)
    {
        // Compared without case (a server may not tell "A.iso" from "a.iso"); a folder whose pages are not all loaded is
        // asked about anyway, since what isn't listed may be there.
        var shown = _sitesPanel is { } panel && panel.SiteId == site.Id && SitesBrowseRules.Normalize(panel.Path) == SitesBrowseRules.Normalize(remoteFolder);
        var there = SitesCommandRules.AlreadyThere(names, shown ? _sitesPanel!.ShownNames : [], ignoreCase: true);
        var complete = shown && _sitesPanel!.ListingComplete;
        var conflict = SitesCommandRules.Skip;
        if (SitesCommandRules.ConflictQuestion(there, $"{SitesBrowseRules.Normalize(remoteFolder)} on {site.Name}", names.Any(name => name.IsFolder), complete) is { } ask)
        {
            if (await AskConflictAsync(ask) is not { } chosen) return;
            conflict = chosen;
        }

        await StartSiteTransferAsync(new SiteTransferStartRequest
        {
            SiteId = site.Id, Direction = "upload", RemoteFolder = remoteFolder, LocalFolder = localFolder, Names = names,
            Conflict = conflict,
        });
    }

    /// <summary>Starts a batch; the batch as the Host reports it, or null when it did not start (the reason is shown).</summary>
    private async Task<SiteTransferDto?> StartSiteTransferAsync(SiteTransferStartRequest request)
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
                        ? (reply.Message ?? "The remote needs attention.") + " Check it in Options > Sites."
                        : reply.Message ?? "The transfer could not start.");
                return null;
            }

            if (reply.Transfer is { } added && _siteTransfers.All(t => t.Id != added.Id)) ApplySiteTransfers([.. _siteTransfers, added]);
            TransfersBar.OpenAtBest();
            return reply.Transfer;
        }
        finally
        {
            UpdateSitesToolbar();
        }
    }


    private async Task ShowSitesMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content?.XamlRoot,
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
