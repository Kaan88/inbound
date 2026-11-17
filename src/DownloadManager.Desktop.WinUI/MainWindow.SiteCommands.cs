using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R2b (sites-rework.md decisions 1 to 4): the Remote menu and its keys, Download with no dialog (and "Download to…"),
/// the one conflict question for a batch, Open, Copy and Paste address, and the This PC pane's own commands. The words and
/// choices are <see cref="SitesCommandRules"/>; this file only carries them out.
/// </summary>
public sealed partial class MainWindow
{
    private bool _siteMenuReady;
    private string _starredShown = "\u0001";

    // "Open" downloads a file into a folder of its own under %TEMP% and opens it when the transfer is done: by transfer id.
    private readonly Dictionary<string, OpenWait> _openWaits = [];

    // Opens being started (before the Host answers with a transfer id), by OpenKey, so a second Open of the same file waits.
    private readonly HashSet<string> _openStarting = [];

    private sealed record OpenWait(string Path, string Key, string SiteName)
    {
        /// <summary>Its batch was in a list once (a batch that is then missing twice in a row was cleared).</summary>
        public bool Seen { get; init; }

        /// <summary>
        /// Lists in a row without its batch. One is not enough: a list read before the start can be applied after it.
        /// </summary>
        public int Misses { get; init; }

        public DateTimeOffset Added { get; init; } = DateTimeOffset.UtcNow;
    }

    // Download, Download to…, Upload and Open are starting (a dialog, a picker or the Host's answer is awaited): Enter, the
    // menu and a double click while that runs don't start a second batch.
    private bool _transferStarting;

    private nint WindowHandle => OperatingSystem.IsWindows() ? WinRT.Interop.WindowNative.GetWindowHandle(this) : 0;

    // ---- The Site menu ----------------------------------------------------------------------------------

    /// <summary>The keys the menu shows (the Sites view reads them itself: <see cref="SitesHost_PreviewKeyDown"/>).</summary>
    private void InitSiteMenu()
    {
        if (_siteMenuReady) return;
        _siteMenuReady = true;
        void Key(MenuFlyoutItem item, string key) => item.KeyboardAcceleratorTextOverride = CommandKey.Text(key);
        Key(SiteMenuDownload, SitesCommandRules.KeyText(SiteCommand.Download));
        Key(SiteMenuDownloadTo, SitesCommandRules.KeyText(SiteCommand.DownloadTo));
        Key(SiteMenuOpen, SitesCommandRules.KeyText(SiteCommand.Open));
        Key(SiteMenuUpload, SitesCommandRules.KeyText(SiteCommand.Upload));
        Key(SiteMenuNewFolder, SitesCommandRules.KeyText(SiteCommand.NewFolder));
        Key(SiteMenuRename, SitesCommandRules.KeyText(SiteCommand.Rename));
        Key(SiteMenuDelete, SitesCommandRules.KeyText(SiteCommand.Delete));
        Key(SiteMenuDeleteForGood, "Shift+Delete");
        Key(SiteMenuRefresh, SitesCommandRules.KeyText(SiteCommand.Refresh));
        Key(SiteMenuStar, "Ctrl+D");
        Key(SiteMenuTwoPanes, "Ctrl+T");
        Key(SiteMenuCopyAddress, SitesCommandRules.KeyText(SiteCommand.CopyAddress));
        SitesHost.PreviewKeyDown += SitesHost_PreviewKeyDown;
    }

    /// <summary>The menu follows the selection, the site and what is running (called with the toolbar).</summary>
    private void SyncSiteMenu()
    {
        if (_sitesPanel is not { } panel) return;
        var site = panel.Site;
        var ready = site is not null && !panel.IsLoading;
        var picked = panel.SelectedNames.Count;
        var one = picked == 1;
        SiteMenuDownload.IsEnabled = ready && picked > 0;
        // Managing another Inbound server: a download lands on its disks, so Open and Download to… (this PC) are off.
        var thisPc = !_connection.IsRemote;
        panel.OtherServer = !thisPc;
        SiteMenuDownloadTo.IsEnabled = ready && picked > 0 && thisPc;
        SiteMenuOpen.IsEnabled = ready && one && thisPc;
        SiteMenuUpload.IsEnabled = ready && thisPc;
        SiteMenuUploadFolder.IsEnabled = ready;
        var canNewFolder = site?.Capabilities.Contains(SiteCapabilities.NewFolder) != false;
        SiteMenuNewFolder.Visibility = canNewFolder ? Visibility.Visible : Visibility.Collapsed;
        SiteMenuNewFolder.IsEnabled = ready && !_siteChangeRunning;
        SiteMenuRename.IsEnabled = ready && one && !_siteChangeRunning;
        SiteMenuDelete.IsEnabled = ready && picked > 0 && !_siteChangeRunning;
        SyncDeleteForGoodMenu();
        SiteMenuRefresh.IsEnabled = site is not null && !panel.IsLoading;
        // The Host refuses a star on the root.
        SiteMenuStar.IsEnabled = ready && panel.CanGoUp;
        SiteMenuStar.Text = panel.IsStarred ? "Unstar this folder" : "Star this folder";
        SiteMenuTwoPanes.Text = TwoPanesShown ? "Remote only" : "Two panes";
        SiteMenuTwoPanes.IsEnabled = TwoPanesOffered;
        SiteMenuCopyAddress.IsEnabled = site is not null;
        // An address opened once is not edited; it can be remembered from All remotes.
        SiteMenuEdit.IsEnabled = site is not null && SitesHomeRules.CanEdit(site);
        SitesEditSiteButton.IsEnabled = site is not null && SitesHomeRules.CanEdit(site);
        SyncStarredMenu(panel, site);
    }

    private void SyncStarredMenu(SitesPanel panel, SiteDto? site)
    {
        var stars = SitesBrowseRules.OrderedStars(site?.Stars);
        var key = string.Join('\n', stars);
        if (key == _starredShown) return;
        _starredShown = key;
        SiteMenuStarred.Items.Clear();
        SiteMenuStarred.IsEnabled = stars.Count > 0;
        foreach (var star in stars)
        {
            var item = new MenuFlyoutItem { Text = SitesBrowseRules.StarLabel(star, stars) };
            ToolTipService.SetToolTip(item, star);
            var path = star;
            item.Click += (_, _) => panel.GoTo(path);
            SiteMenuStarred.Items.Add(item);
        }
    }

    private void SiteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "TwoPanes":
                SetTwoPanes(!TwoPanesShown);
                break;
            case "Pause":
                SitesPauseAllButton_Click(sender, e);
                break;
            case "Edit":
                SitesEditSiteButton_Click(sender, e);
                break;
            case "Options":
                OpenSitesOptionsHook();
                break;
            case "DeleteForGood":
                FireLocalCommand(LocalCommand.DeleteForGood);
                break;
            case { } name when Enum.TryParse<SiteCommand>(name, out var command):
                FireSiteCommand(command);
                break;
        }
    }

    private void SitesEditSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesPanel?.Site is not { } site || !SitesHomeRules.CanEdit(site)) return;
        // The same Edit remote form as the pane's button and the All remotes page.
        _ = EnsureSiteFlows().SaveFormAsync(site);
    }

    private void SitesOptionsButton_Click(object sender, RoutedEventArgs e) => OpenSitesOptionsHook();

    /// <summary>One entry for the toolbar, the Site menu, the context menu and the keys.</summary>
    private async Task RunSiteCommandAsync(SiteCommand command)
    {
        if (!_sitesShown || _sitesPanel is not { Site: { } site } panel) return;
        if (_connection.IsRemote && SitesCommandRules.OffForOtherServer(command))
        {
            SetSitesStatus($"{SitesCommandRules.Label(command).TrimEnd('…')} works for this PC's own remotes only.");
            return;
        }

        var none = new RoutedEventArgs();
        switch (command)
        {
            case SiteCommand.Download:
                await SiteDownloadAsync(askFolder: false);
                break;
            case SiteCommand.DownloadTo:
                await SiteDownloadAsync(askFolder: true);
                break;
            case SiteCommand.Open:
                await SiteOpenAsync();
                break;
            case SiteCommand.Rename:
                SitesRenameButton_Click(this, none);
                break;
            case SiteCommand.Delete:
                SitesDeleteButton_Click(this, none);
                break;
            case SiteCommand.NewFolder when site.Capabilities.Contains(SiteCapabilities.NewFolder):
                SitesNewFolderButton_Click(this, none);
                break;
            case SiteCommand.Upload:
                SitesUploadFiles_Click(this, none);
                break;
            case SiteCommand.UploadFolder:
                SitesUploadFolder_Click(this, none);
                break;
            case SiteCommand.Refresh:
                panel.Refresh();
                break;
            case SiteCommand.PasteAddress:
                await PasteSiteAddressAsync();
                break;
            case SiteCommand.CopyAddress:
                CopySiteAddress();
                break;
            case SiteCommand.CopyPath:
                CopyText(SitesCommandRules.Paths(panel.Path, [.. panel.SelectedNames.Select(name => name.Name)]), panel.SelectedNames.Count > 1 ? "Paths copied" : "Path copied");
                break;
            case SiteCommand.StarFolder:
                await panel.ToggleStarAsync();
                break;
        }
    }

    /// <summary>Runs a command from a click or a key; a failure (the Host gone, an IPC error) is said in the status line.</summary>
    private void FireSiteCommand(SiteCommand command) => _ = ReportedAsync(RunSiteCommandAsync(command), SitesCommandRules.Label(command));

    private void FireLocalCommand(LocalCommand command) => _ = ReportedAsync(RunLocalCommandAsync(command), SitesCommandRules.Label(command));

    private async Task ReportedAsync(Task work, string what)
    {
        try
        {
            await work;
        }
        catch (Exception ex)
        {
            UiTrace.Write($"The remote command {what} failed: {ex.GetType().Name}");
            SetSitesStatus($"{what.TrimEnd('…')} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts a transfer flow once: while one is starting, another Enter, menu click or double click does nothing. False when
    /// it was ignored for that (a drop says so).
    /// </summary>
    private async Task<bool> OnceAsync(Func<Task> work)
    {
        if (_transferStarting) return false;
        _transferStarting = true;
        try
        {
            await work();
            return true;
        }
        finally
        {
            _transferStarting = false;
        }
    }

    // ---- Keys -------------------------------------------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private bool FocusIsIn(DependencyObject? container)
    {
        if (container is null || Content?.XamlRoot is not { } root) return false;
        for (var node = FocusManager.GetFocusedElement(root) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container)) return true;
        }

        return false;
    }

    private bool FocusInLocalPane => _localPane is { Visibility: Visibility.Visible } local && FocusIsIn(local);

    /// <summary>
    /// The Remote menu's keys that belong to a pane: they act on the pane that has the keyboard (This PC or the server) and
    /// never in a text box (the Filter box, the path box). Enter, F2, Delete and F5 are the panes' own. Ctrl+Shift+S and
    /// Ctrl+T are window accelerators (<see cref="SitesKeyDownloadTo"/>, <see cref="SitesKeyTwoPanes"/>), so they work
    /// after a click on the toolbar or the sidebar too, and on a Mac with Cmd.
    /// </summary>
    private void SitesHost_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_sitesShown || e.OriginalSource is TextBox or PasswordBox or RichEditBox || !IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)) return;
        var shift = IsDown(VirtualKey.Shift);
        var inLocal = FocusInLocalPane;
        switch (e.Key)
        {
            case VirtualKey.Enter when !shift:
                if (inLocal) FireLocalCommand(LocalCommand.Open);
                else FireSiteCommand(SiteCommand.Open);
                break;
            case VirtualKey.U when !shift:
                // In This PC: the local selection goes up to the server folder; in the server pane: pick files to upload.
                if (inLocal) FireLocalCommand(LocalCommand.Upload);
                else FireSiteCommand(SiteCommand.Upload);
                break;
            case VirtualKey.N when shift:
                if (inLocal) FireLocalCommand(LocalCommand.NewFolder);
                else FireSiteCommand(SiteCommand.NewFolder);
                break;
            case VirtualKey.D when !shift:
                if (inLocal) _localPane?.ToggleStar();
                else FireSiteCommand(SiteCommand.StarFolder);
                break;
            case VirtualKey.C when shift:
                if (inLocal) CopyLocalPaths();
                else FireSiteCommand(SiteCommand.CopyAddress);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Ctrl+Shift+S, a window accelerator: Download to… while Sites is shown (wherever the keyboard is).</summary>
    private void SitesKeyDownloadTo() => FireSiteCommand(SiteCommand.DownloadTo);

    /// <summary>Ctrl+T, a window accelerator: the view switch while Sites is shown.</summary>
    private void SitesKeyTwoPanes()
    {
        if (TwoPanesOffered) SetTwoPanes(!TwoPanesShown);
    }

    // ---- Download with no dialog ------------------------------------------------------------------------

    /// <summary>
    /// Download, Enter, double click and the menus (decisions 1 and 2): at once, to the site's linked folder, or with none
    /// to the folder the Host's category rules pick. "Download to…" asks for a folder first.
    /// </summary>
    private Task SiteDownloadAsync(bool askFolder) => OnceAsync(() => SiteDownloadCoreAsync(askFolder));

    private async Task SiteDownloadCoreAsync(bool askFolder)
    {
        if (_sitesPanel is not { Site: { } site } panel || panel.IsLoading) return;
        var names = panel.SelectedNames;
        if (names.Count == 0) return;
        if (askFolder && _connection.IsRemote)
        {
            SetSitesStatus($"{SitesCommandRules.Label(SiteCommand.DownloadTo).TrimEnd('…')} works for this PC's own remotes only.");
            return;
        }
        var folder = site.LocalFolder is { Length: > 0 } linked && Path.IsPathFullyQualified(linked) ? linked : null;
        if (askFolder)
        {
            try
            {
                folder = await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), start: folder ?? LocalSettings?.DefaultDownloadDirectory);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                await ShowSitesMessageAsync("Download to…", "The folder picker could not open: " + ex.Message);
                return;
            }

            if (folder is null) return;
        }

        await DownloadNamesAsync(site, panel.Path, names, folder);
    }

    /// <summary>
    /// Starts a download of <paramref name="names"/>. With a folder (the linked one, a picked one, the This PC pane's) the folder is
    /// read off the UI thread and each name already there is asked about, one file at a time (SITES.11 R5); the choices go out as
    /// one batch each. With no folder the Host picks one by the category rules, which the window does not know, so it can't
    /// look: Keep both then, so nothing is overwritten or skipped without being asked.
    /// </summary>
    private async Task<SiteTransferDto?> DownloadNamesAsync(SiteDto site, string remoteFolder, List<SiteTransferNameDto> names, string? folder)
    {
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        var other = SitesCommandRules.KeepBoth;
        // The linked folder of a site of another Inbound server is on that server's disks: this PC can't look in it.
        if (folder is not null && !_connection.IsRemote)
        {
            var targets = await Task.Run(() => LocalTargets(folder));
            var ignoreCase = OperatingSystem.IsWindows();
            var sources = new Dictionary<string, SitesCommandRules.ConflictSide>(StringComparer.Ordinal);
            // What the server says of the picked files is known when their folder is the one the pane shows.
            if (_sitesPanel is { } panel && panel.SiteId == site.Id && SitesBrowseRules.Normalize(panel.Path) == SitesBrowseRules.Normalize(remoteFolder))
                foreach (var item in panel.ShownItems) sources[item.Name] = new SitesCommandRules.ConflictSide(item.Size, item.Modified);
            var pairs = SitesCommandRules.ConflictPairs(names, sources, targets, ignoreCase);
            if (await AskConflictsAsync(pairs, upload: false, folder, names.Any(name => name.IsFolder), complete: true, [.. targets.Select(t => t.Key)],
                    SitesCommandRules.TimesArePrecise(site.Kind)) is not { } answer)
                return null;
            choices = answer.Choices;
            other = answer.Other;
        }

        return await StartConflictBatchesAsync(names, choices, other, (conflict, batch) => new SiteTransferStartRequest
        {
            SiteId = site.Id, Direction = "download", RemoteFolder = remoteFolder, LocalFolder = folder ?? "", Names = batch, Conflict = conflict,
        });
    }

    // ---- Open -------------------------------------------------------------------------------------------

    /// <summary>
    /// A folder opens in the pane; a file downloads into a folder of its own, <c>%TEMP%\Inbound\open\&lt;siteId&gt;\&lt;guid&gt;</c>
    /// (never the app's state folder), gets the mark of the web, and opens with its Windows program when the transfer is done
    /// (<see cref="ResolveOpenWaits"/>); a program or script is asked about first. Off while the window manages another server.
    /// </summary>
    private Task SiteOpenAsync() => OnceAsync(SiteOpenCoreAsync);

    private async Task SiteOpenCoreAsync()
    {
        if (_connection.IsRemote || _sitesPanel is not { Site: { } site } panel) return;
        if (panel.SelectedFolderPath is { } folderPath)
        {
            panel.GoTo(folderPath);
            return;
        }

        if (panel.SelectedNames is not [{ IsFolder: false } file]) return;
        var key = SitesCommandRules.OpenKey(site.Id, panel.Path, file.Name);
        if (_openStarting.Contains(key) || _openWaits.Values.Any(wait => wait.Key == key))
        {
            SetSitesStatus($"{file.Name} is already being opened.");
            return;
        }

        var temp = Path.GetTempPath();
        var folder = SitesCommandRules.OpenFolder(temp, site.Id, Guid.NewGuid());
        var keep = _openWaits.Values.Select(wait => Path.GetDirectoryName(wait.Path) ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        _openStarting.Add(key);
        try
        {
            try
            {
                await Task.Run(() =>
                {
                    RemoveStaleOpenFolders(SitesCommandRules.OpenRoot(temp), keep);
                    Directory.CreateDirectory(folder);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ShowSitesMessageAsync("Open", "The temporary folder could not be made: " + ex.Message);
                return;
            }

            // A folder of its own: nothing is there, so nothing is replaced.
            var started = await StartSiteTransferAsync(new SiteTransferStartRequest
            {
                SiteId = site.Id, Direction = "download", RemoteFolder = panel.Path, LocalFolder = folder, Names = [file], Conflict = SitesCommandRules.Skip,
            });
            if (started is null) return;
            _openWaits[started.Id] = new OpenWait(Path.Combine(folder, file.Name), key, site.Name);
        }
        finally
        {
            _openStarting.Remove(key);
        }

        ResolveOpenWaits(_siteTransfers);
    }

    /// <summary>Open folders (one per Open, under each site's folder) last written more than a day ago go; ones still waited on stay.</summary>
    private static void RemoveStaleOpenFolders(string root, IReadOnlySet<string> keep)
    {
        if (!Directory.Exists(root)) return;
        var now = DateTime.UtcNow;
        foreach (var siteFolder in Directory.EnumerateDirectories(root))
        {
            foreach (var openFolder in Directory.EnumerateDirectories(siteFolder))
            {
                try
                {
                    if (keep.Contains(openFolder) || !SitesCommandRules.IsStaleOpenFolder(Directory.GetLastWriteTimeUtc(openFolder), now)) continue;
                    Directory.Delete(openFolder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file still open in its program: the folder goes another time.
                }
            }
        }
    }

    /// <summary>Called with every batch change: a finished Open batch opens its file; a failed one says so.</summary>
    private void ResolveOpenWaits(IReadOnlyList<SiteTransferDto> transfers)
    {
        foreach (var (id, wait) in _openWaits.ToList())
        {
            var transfer = transfers.FirstOrDefault(t => t.Id == id);
            if (transfer is null)
            {
                // A just-started batch may not be in a list yet, and a list read before the start can arrive after it: a wait
                // goes once its batch was seen and then missed from two lists in a row (cleared), or after 30 s unseen, and the
                // status line says so.
                var misses = wait.Misses + 1;
                var gone = wait.Seen ? misses >= 2 : DateTimeOffset.UtcNow - wait.Added >= TimeSpan.FromSeconds(30);
                if (!gone)
                {
                    _openWaits[id] = wait with { Misses = misses };
                    continue;
                }

                _openWaits.Remove(id);
                SetSitesStatus($"{Path.GetFileName(wait.Path)} won't open: its transfer is no longer listed.");
                continue;
            }

            if (!wait.Seen || wait.Misses > 0) _openWaits[id] = wait with { Seen = true, Misses = 0 };

            if (transfer.State is not ("done" or "failed" or "canceled")) continue;
            _openWaits.Remove(id);
            if (transfer.State == "done" && File.Exists(wait.Path))
            {
                _ = OpenDownloadedAsync(wait);
                continue;
            }

            _ = ShowSitesMessageAsync("Open", $"{Path.GetFileName(wait.Path)} was not downloaded" + (string.IsNullOrWhiteSpace(transfer.Message) ? "." : ": " + transfer.Message));
        }
    }

    /// <summary>
    /// Marks the file as from the internet (Zone.Identifier, zone 3), so Windows and Office warn as they do for a browser's
    /// download; a program, script or shortcut is asked about first, with Show in folder as the default.
    /// </summary>
    private async Task OpenDownloadedAsync(OpenWait wait)
    {
        var name = Path.GetFileName(wait.Path);
        // Off Windows there is no mark to write, and nothing that would read it.
        var marked = !OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                await Task.Run(() => File.WriteAllText(wait.Path + ":Zone.Identifier", SitesCommandRules.ZoneIdentifierText));
                marked = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                UiTrace.Write($"Marking an opened remote file failed: {ex.GetType().Name}");
            }
        }

        // A program, or a file Windows won't warn about because the mark could not be written, is asked about first.
        var runnable = SitesCommandRules.IsRunnable(name);
        if (runnable || !marked)
        {
            var answer = await AskBeforeOpeningAsync(runnable ? "Run this file?" : "Open this file?",
                runnable ? SitesCommandRules.RunQuestion(name, wait.SiteName) : SitesCommandRules.UnmarkedQuestion(name, wait.SiteName),
                runnable ? "Run" : "Open");
            if (answer is null)
            {
                SetSitesStatus($"{name} is downloaded but not opened: another dialog is open.");
                return;
            }

            if (answer == ContentDialogResult.Primary)
            {
                StartShell(ShellActions.RevealFile(wait.Path), name);
                return;
            }

            if (answer != ContentDialogResult.Secondary) return;
        }

        StartShell(ShellActions.OpenFile(wait.Path, WindowHandle), name);
    }

    /// <summary>Show in folder (the default), <paramref name="go"/> (Run or Open) or Cancel; null when another dialog is open.</summary>
    private Task<ContentDialogResult?> AskBeforeOpeningAsync(string title, string question, string go)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content?.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Show in folder",
            SecondaryButtonText = go,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return DialogGuard.ShowAsync(dialog);
    }

    private void StartShell(System.Diagnostics.ProcessStartInfo start, string name)
    {
        try
        {
            ShellActions.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _ = ShowSitesMessageAsync("Open", $"{name} could not be opened: {ex.Message}");
        }
    }

    // ---- Addresses --------------------------------------------------------------------------------------

    private void CopySiteAddress()
    {
        if (_sitesPanel is not { Site: { } site } panel) return;
        var text = SitesCommandRules.Addresses(site, panel.Path, [.. panel.SelectedNames.Select(name => name.Name)]);
        CopyText(text, panel.SelectedNames.Count > 1 ? "Addresses copied" : "Address copied");
    }

    /// <summary>Ctrl+Shift+C in This PC: the selected items' full paths, or the folder's.</summary>
    private void CopyLocalPaths()
    {
        if (_localPane is not { } local || local.Path.Length == 0) return;
        var selected = local.Selected;
        var text = selected.Count == 0 ? local.Path : string.Join(Environment.NewLine, selected.Select(entry => Path.Combine(local.Path, entry.Name)));
        CopyText(text, selected.Count > 1 ? "Paths copied" : "Path copied");
    }

    private void CopyText(string text, string done)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            SetSitesStatus(done);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetSitesStatus("It could not be copied: " + ex.Message);
        }
    }

    private async Task PasteSiteAddressAsync()
    {
        if (_sitesPanel is not { Site: { } site } panel) return;
        string? text = null;
        try
        {
            var clip = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (clip.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) text = await clip.GetTextAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            text = null;
        }

        if (SitesCommandRules.PathFromText(site, text) is { } path) panel.GoTo(path);
        else await ShowSitesMessageAsync("Paste address", $"The clipboard doesn't hold a folder of {site.Name}: a path such as /media/isos or an address of this remote.");
    }

    // ---- This PC pane -----------------------------------------------------------------------------------

    private async Task RunLocalCommandAsync(LocalCommand command)
    {
        if (_localPane is not { Visibility: Visibility.Visible } local || local.Path.Length == 0) return;
        var selected = local.Selected;
        switch (command)
        {
            case LocalCommand.Upload:
                await UploadFromPaneAsync();
                break;
            case LocalCommand.Open when selected is [var item]:
                var full = Path.Combine(local.Path, item.Name);
                if (item.IsFolder) local.Open(full);
                else if (SitesCommandRules.IsRunnable(item.Name))
                {
                    // A program or script is asked about first, as a remote file is; Show in folder is the default.
                    var answer = await AskBeforeOpeningAsync("Run this file?", SitesCommandRules.LocalRunQuestion(item.Name), "Run");
                    if (answer is null) local.Note($"{item.Name} was not opened: another dialog is open.", reload: false);
                    else if (answer == ContentDialogResult.Primary) StartShell(ShellActions.RevealFile(full), item.Name);
                    else if (answer == ContentDialogResult.Secondary) StartShell(ShellActions.OpenFile(full, WindowHandle), item.Name);
                }
                else
                {
                    try
                    {
                        ShellActions.Start(ShellActions.OpenFile(full, WindowHandle))?.Dispose();
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                    {
                        local.Note($"{item.Name} could not be opened: {ex.Message}", reload: false);
                    }
                }

                break;
            case LocalCommand.ShowInExplorer when selected is [var shown]:
                try
                {
                    ShellActions.Start(ShellActions.RevealFile(Path.Combine(local.Path, shown.Name)))?.Dispose();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    local.Note($"Explorer could not be opened: {ex.Message}", reload: false);
                }

                break;
            case LocalCommand.Rename when selected is [var renamed]:
                await RenameLocalAsync(local, renamed);
                break;
            case LocalCommand.Delete:
                await DeleteLocalAsync();
                break;
            case LocalCommand.DeleteForGood:
                await DeleteLocalAsync(forGood: true);
                break;
            case LocalCommand.NewFolder:
                await NewLocalFolderAsync(local);
                break;
            case LocalCommand.Refresh:
                local.Reload();
                break;
            case LocalCommand.CopyPath:
                CopyLocalPaths();
                break;
        }
    }

    private async Task RenameLocalAsync(LocalPane local, LocalEntry item)
    {
        var typed = await AskNameAsync(item.IsFolder ? "Rename folder" : "Rename", $"In {local.Path} on this PC", item.Name, "Rename",
            SiteChangeRules.RenameSelectionLength(item.Name, item.IsFolder));
        if (typed is null) return;
        var (name, bad) = SitesCommandRules.LocalNewName(typed, OperatingSystem.IsWindows());
        if (name == item.Name) return;
        if (bad is not null)
        {
            local.Note($"Not renamed: {bad}", reload: false);
            return;
        }

        var from = Path.Combine(local.Path, item.Name);
        var to = Path.Combine(local.Path, name);
        // A batch writing or reading it (or a folder inside it): renaming would pull it from under the transfer.
        if (SitesCommandRules.LocalItemBusy(_siteTransfers, local.Path, item.Name))
        {
            local.Note($"A remote transfer still uses {item.Name}. Cancel or finish the transfer first.", reload: false);
            return;
        }

        var problem = await Task.Run(() =>
        {
            try
            {
                if (item.IsFolder) Directory.Move(from, to);
                else File.Move(from, to);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return ex.Message;
            }
        });
        if (problem is null) local.Reload(name);
        else local.Note($"Not renamed: {problem}", reload: true);
    }

    private async Task NewLocalFolderAsync(LocalPane local)
    {
        var typed = await AskNameAsync("New folder", $"In {local.Path} on this PC", SiteChangeRules.NewFolderName(local.ShownNames), "Make", selectLength: null);
        if (typed is null) return;
        var (name, bad) = SitesCommandRules.LocalNewName(typed, OperatingSystem.IsWindows());
        if (bad is not null)
        {
            local.Note($"The folder was not made: {bad}", reload: false);
            return;
        }

        var path = Path.Combine(local.Path, name);
        var problem = await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(path) || File.Exists(path)) return "something with that name is already there.";
                Directory.CreateDirectory(path);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return ex.Message;
            }
        });
        if (problem is null) local.Reload(name);
        else local.Note($"The folder was not made: {problem}", reload: false);
    }
}
