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
        TransfersBar.ActFile = (type, id, path) => _remote.SiteTransferFileActionAsync(type, id, path);
        TransfersBar.Details = id => _remote.GetSiteTransferAsync(id);
        TransfersBar.LocalCommandRequested += (command, row) => OnTransferLocalCommand(command, row);
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

        // A copy between two remotes landed in the folder a remote pane shows (either side): list it again.
        if (_sitesPanel is { Site: { } rightSite } right && right.Visibility == Visibility.Visible
            && SiteTransferRules.CopyLanded(before, transfers, rightSite.Id, right.Path))
            right.Refresh();
        if (_leftRemote is { Site: { } leftSite } leftPanel && leftPanel.Visibility == Visibility.Visible && TwoPanesShown && _sitesShown
            && SiteTransferRules.CopyLanded(before, transfers, leftSite.Id, leftPanel.Path))
            leftPanel.Refresh();

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

    /// <summary>The status chip ("Remote files · 2 running · 156 MB/s"): shown while a batch runs, waits, is paused or failed, and remote files are offered here.</summary>
    private void UpdateSitesChip()
    {
        var text = SitesOffered ? SiteTransferRules.ChipText(_siteTransfers) : null;
        var visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (SitesChip.Visibility != visibility) SitesChip.Visibility = visibility;
        if (text is null || SitesChipText.Text == text) return;
        SitesChipText.Text = text;
        AutomationProperties.SetName(SitesChip, text);
    }

    /// <summary>Each remote's sidebar entry follows its state and what runs on it (arrows, clock, progress line); so does the All remotes page.</summary>
    private void SyncSiteMarks()
    {
        foreach (var item in CategoryList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is not string tag || !tag.StartsWith(SiteTagPrefix, StringComparison.Ordinal)) continue;
            if (_sites.FirstOrDefault(s => s.Id == tag[SiteTagPrefix.Length..]) is { } site) UpdateSiteEntry(item, site);
        }

        RefreshHome();
    }

    // ---- Download and Upload ----------------------------------------------------------------------------

    // SITES.11 R2b: no dialog; to the linked folder, or by the Host's category rules (SiteDownloadAsync).
    private void SitesDownloadButton_Click(object sender, RoutedEventArgs e) => _ = SiteDownloadAsync(askFolder: false);

    private async void SitesUploadFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesPanel is not { Site: { } site } panel) return;
        if (_connection.IsRemote)
        {
            SetSitesStatus($"{SitesCommandRules.Label(SiteCommand.Upload).TrimEnd('…')} works for this PC's own remotes only.");
            return;
        }

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

        // Managing another server, the folder was picked in that server's folder browser: its own path.
        await UploadAsync(site, panel.Path, parent, [new SiteTransferNameDto { Name = name, IsFolder = true }], serverPaths: _connection.IsRemote);
    }

    /// <summary>
    /// Uploads <paramref name="names"/> into <paramref name="remoteFolder"/>. The names are checked against the server listing
    /// shown (what is loaded so far); when some are there the batch is asked about once.
    /// </summary>
    private Task UploadAsync(SiteDto site, string remoteFolder, string localFolder, List<SiteTransferNameDto> names, bool serverPaths = false) =>
        OnceAsync(() => UploadCoreAsync(site, remoteFolder, localFolder, names, serverPaths));

    private async Task UploadCoreAsync(SiteDto site, string remoteFolder, string localFolder, List<SiteTransferNameDto> names, bool serverPaths)
    {
        // Paths picked on this PC (a picker, a pane, a drop) mean nothing to another server: it would read its own disks.
        if (_connection.IsRemote && !serverPaths)
        {
            SetSitesStatus("Uploading files from this PC works for this PC's own remotes only.");
            return;
        }

        // Compared without case (a server may not tell "A.iso" from "a.iso"); a folder whose pages are not all loaded is
        // asked about anyway, since what isn't listed may be there.
        var target = SitesBrowseRules.Normalize(remoteFolder);
        var shown = _sitesPanel is { } panel && panel.SiteId == site.Id && SitesBrowseRules.Normalize(panel.Path) == target;
        List<SiteItemDto>? existing;
        bool complete;
        if (shown)
        {
            existing = [.. _sitesPanel!.ShownItems];
            complete = _sitesPanel.ListingComplete;
        }
        else
        {
            // Another folder (a drop on a folder row): its first page, as the pane asks for it, is read to look.
            (existing, complete) = await ReadServerItemsAsync(site, target);
        }

        // SITES.11 R5: each name already there is asked about on its own, with both copies' size and date; the choices go
        // out as one batch each (the Host takes one choice per batch).
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        var other = SitesCommandRules.Skip;
        string? unread = null;
        if (existing is null)
        {
            // Not readable: nothing to ask about; Skip keeps what is there.
            unread = SitesCommandRules.UnreadTargetText(target);
        }
        else
        {
            List<KeyValuePair<string, SitesCommandRules.ConflictSide>> targets =
                [.. existing.Select(item => new KeyValuePair<string, SitesCommandRules.ConflictSide>(item.Name, new SitesCommandRules.ConflictSide(item.Size, item.Modified)))];
            // The other server's disks are not this PC's: nothing to look at there.
            var sources = _connection.IsRemote ? [] : await Task.Run(() => LocalSides(localFolder, names));
            var pairs = SitesCommandRules.ConflictPairs(names, sources, targets, ignoreCase: true);
            if (await AskConflictsAsync(pairs, upload: true, $"{target} on {site.Name}", names.Any(name => name.IsFolder), complete,
                    [.. targets.Select(t => t.Key)], SitesCommandRules.TimesArePrecise(site.Kind)) is not { } answer) return;
            choices = answer.Choices;
            other = answer.Other;
        }

        var started = await StartConflictBatchesAsync(names, choices, other, (conflict, batch) => new SiteTransferStartRequest
        {
            SiteId = site.Id, Direction = "upload", RemoteFolder = remoteFolder, LocalFolder = localFolder, Names = batch, Conflict = conflict,
        });
        if (started is not null && unread is not null) SetSitesStatus(unread);
    }

    /// <summary>
    /// The names in a server folder that is not the one shown: one sites.browse of its first page (the request the pane
    /// makes). Complete when that page holds the whole folder; null names when it could not be read.
    /// </summary>
    private async Task<(List<SiteItemDto>? Items, bool Complete)> ReadServerItemsAsync(SiteDto site, string folder)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await _remote.BrowseSiteAsync(new SiteBrowseRequest
            {
                Id = site.Id, Path = folder, Offset = 0, Limit = SitesBrowseRules.PageSize,
            }, limit.Token);
            if (!result.Ok) return (null, false);
            return ([.. result.Items], result.Items.Count >= result.Total);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or TimeoutException)
        {
            return (null, false);
        }
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
                // SITES.11 R4: a key, certificate, sign-in or password to see to is asked here, by testing the remote, rather
                // than sending the user to Options; the transfer is started again by the user once it works.
                if (SitesBrowseRules.PointsToOptions(reply.Problem) && reply.Problem != SiteProblems.Off && FindSite(request.SiteId) is { } site)
                {
                    SetSitesStatus($"{site.Name} needs attention before the {(request.Direction == "download" ? "download" : "upload")} can start. {reply.Message}".TrimEnd());
                    await FixSiteAsync(site, reply.Problem);
                    return null;
                }

                await ShowSitesMessageAsync(request.Direction == "download" ? "Download" : "Upload", reply.Message ?? "The transfer could not start.");
                return null;
            }

            if (reply.Transfer is { } added && _siteTransfers.All(t => t.Id != added.Id)) ApplySiteTransfers([.. _siteTransfers, added]);
            // Just started: it is on Running, whatever failed before.
            TransfersBar.OpenOnRunning();
            return reply.Transfer;
        }
        finally
        {
            UpdateSitesToolbar();
        }
    }


    // ---- Open, Open folder, Show on the remote (R3, decision 4) -----------------------------------------

    private void OnTransferLocalCommand(SiteTransferRules.TransferCommand command, SiteTransferRules.TransferRef row)
    {
        switch (command)
        {
            case SiteTransferRules.TransferCommand.Open:
                _ = OpenTransferAsync(row, reveal: false);
                break;
            case SiteTransferRules.TransferCommand.OpenFolder:
                _ = OpenTransferAsync(row, reveal: true);
                break;
            case SiteTransferRules.TransferCommand.ShowOnRemote:
                _ = ShowTransferOnRemoteAsync(row);
                break;
        }
    }

    /// <summary>
    /// Open and Open folder act on this PC: a download's file, an upload's source. A folder opens in Explorer. A downloaded
    /// file opens as an opened remote file does (<see cref="OpenDownloadedAsync"/>: marked as from the internet, a program
    /// asked about first); an upload's source is the user's own file and just opens.
    /// </summary>
    private async Task OpenTransferAsync(SiteTransferRules.TransferRef row, bool reveal)
    {
        // The bar lists this PC's Host's batches; a window managing another server never opens them here.
        if (_connection.IsRemote || !SitesOffered) return;
        var path = SiteTransferRules.LocalTarget(row);
        var batch = row.Batch;
        if (row.Item is null && SiteTransferRules.IsDownload(batch) && batch.PickedCount == 1 && batch.Picked.Count == 1)
        {
            // One picked name: what landed may have another name (Keep both). Its files as read by the bar, or read now; when
            // neither tells, the batch's folder opens.
            var items = TransfersBar.FilesOf(batch.Id);
            if (items is null)
            {
                try
                {
                    var reply = await _remote.GetSiteTransferAsync(batch.Id);
                    items = reply.Ok ? reply.Items : null;
                }
                catch (Exception ex)
                {
                    // The batch's folder opens instead; the status line says why.
                    UiTrace.Write($"Reading a transfer's files for Open failed: {ex.GetType().Name}");
                    SetSitesStatus($"The files of that transfer could not be read ({ex.Message}); its folder opens instead.");
                }
            }

            var (landed, file) = items is null ? (null, null) : SiteTransferRules.PickedLocal(batch, items);
            path = landed ?? batch.LocalFolder;
            if (file is not null) row = new SiteTransferRules.TransferRef(batch, file);
        }

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0) name = path;
        var folder = batch.LocalFolder;
        // The checks touch the disk (a slow or sleeping drive): off the UI thread.
        var (isFolder, isFile, folderThere) = await Task.Run(() =>
            (Directory.Exists(path), File.Exists(path), folder.Length > 0 && Directory.Exists(folder)));
        if (isFolder)
        {
            StartShell(ShellActions.OpenFolder(path), name);
            return;
        }

        if (!isFile)
        {
            // The file was moved or deleted: its folder is still worth showing.
            if (reveal && folderThere)
            {
                StartShell(ShellActions.OpenFolder(folder), name);
                return;
            }

            await ShowSitesMessageAsync(reveal ? "Open folder" : "Open", $"{name} is no longer on this PC at {path}.");
            return;
        }

        if (reveal)
        {
            StartShell(ShellActions.RevealFile(path), name);
            return;
        }

        // A copied download opens as an opened remote file does; a skipped one (already the user's) and an upload's source plainly.
        if (SiteTransferRules.OpensAsDownloaded(row))
        {
            await OpenDownloadedAsync(new OpenWait(path, batch.Id, batch.SiteName));
            return;
        }

        StartShell(ShellActions.OpenFile(path, WindowHandle), name);
    }

    /// <summary>Goes to the remote's folder of this transfer in the Remote files view and selects the file or folder there.</summary>
    private async Task ShowTransferOnRemoteAsync(SiteTransferRules.TransferRef row)
    {
        if (_connection.IsRemote) return;
        if (_sites.FirstOrDefault(site => site.Id == row.Batch.SiteId) is not { } site || !SitesOffered)
        {
            await ShowSitesMessageAsync("Show on the remote", $"{row.Batch.SiteName} is not among your remotes any more.");
            return;
        }

        var (folder, select) = SiteTransferRules.RemoteTarget(row);
        if (!_sitesShown || _shownSiteId != site.Id)
        {
            ShowSite(site.Id);
            SyncSiteEntryPoints();
        }

        _sitesPanel?.Open(site, folder, select, sayIfMissing: true);
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
