using System.Diagnostics;
using System.Runtime.InteropServices;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Extraction in the main window. A set's state has a column of its own beside the download's status,
/// so a failed extraction never reads as a failed download. Records with no file on the list get rows
/// of their own, which, like remote rows, live outside <c>_jobsById</c>. All of it hides when the Host
/// doesn't answer <c>archive.*</c>.
/// </summary>
public sealed partial class MainWindow
{
    private const string ArchiveSetsTag = "archive:sets";
    private const string ArchiveSetsEmptyText = "No archives yet.\nSelect a RAR download and choose Extract, or use Extract Archive… in the Downloads menu.";

    private static readonly DateTimeOffset WatchingSince = DateTimeOffset.Now;

    private readonly Dictionary<Guid, ArchiveSetDto> _setByJob = [];
    private readonly Dictionary<Guid, JobViewModel> _archiveRows = [];
    private readonly ArchiveNoticeBoard _archiveNotices = new();
    private ArchiveNotice? _archiveNotice;
    private ArchiveMenu? _contextArchiveMenu;
    private ArchiveMenu? _downloadsArchiveMenu;
    private bool _archiveSyncQueued;

    /// <summary>The same commands in the context menu and the Downloads menu.</summary>
    private sealed class ArchiveMenu
    {
        public required MenuFlyoutItem Extract { get; init; }
        public required MenuFlyoutItem ExtractFile { get; init; }
        public required MenuFlyoutItem Password { get; init; }
        public required MenuFlyoutItem Retry { get; init; }
        public required MenuFlyoutItem Cancel { get; init; }
        public required MenuFlyoutItem OpenFolder { get; init; }
        public required MenuFlyoutItem OpenArchiveFolder { get; init; }
        public required MenuFlyoutItem Remove { get; init; }
        public MenuFlyoutSeparator Separator { get; } = new();

        public MenuFlyoutItemBase[] All => [Extract, ExtractFile, Password, Retry, Cancel, OpenFolder, OpenArchiveFolder, Remove, Separator];

        /// <summary>Null hides everything. Only Extract stays when it can't run, to say why, unless another line already offers it.</summary>
        /// <param name="recordsOnly">Only records' own rows are selected. They have no Open Folder of a download's.</param>
        /// <param name="offerPicking">Whether to offer picking some other archive from the disk, which has nothing to do with the row a context menu opened on.</param>
        public void Apply(ArchivePlan? plan, bool connected, bool recordsOnly, bool offerPicking)
        {
            static void Show(MenuFlyoutItemBase item, bool on)
            {
                var wanted = on ? Visibility.Visible : Visibility.Collapsed;
                if (item.Visibility != wanted) item.Visibility = wanted;
            }

            Show(Separator, plan is not null);
            Show(Extract, plan?.ShowExtract == true);
            Show(ExtractFile, plan is not null && offerPicking);
            Show(Password, plan?.Password is not null);
            Show(Retry, plan?.Retry.Count > 0);
            Show(Cancel, plan?.Cancel.Count > 0);
            Show(OpenFolder, plan?.OpenFolders.Count > 0);
            Show(OpenArchiveFolder, recordsOnly && plan?.ArchiveFolders.Count > 0);
            Show(Remove, plan?.Remove.Count > 0);
            if (plan is null) return;

            if (Extract.Text != plan.ExtractText) Extract.Text = plan.ExtractText;
            if (Extract.IsEnabled != plan.CanExtract) Extract.IsEnabled = plan.CanExtract;
            if (ExtractFile.IsEnabled != connected) ExtractFile.IsEnabled = connected;

            var passwordText = plan.Password?.Needed == true ? "Enter Archive Password…" : "Archive Password…";
            if (Password.Text != passwordText) Password.Text = passwordText;

            var retryText = plan.Retry.Count > 0 && plan.Retry.All(set => set.State == "completed") ? "Extract Again…" : "Retry Extraction";
            if (Retry.Text != retryText) Retry.Text = retryText;
        }
    }

    private void InitializeArchiveUi()
    {
        _remote.State.ArchiveSets.CollectionChanged += (_, _) =>
        {
            SyncExtractionColumn();
            QueueArchiveSync();
        };

        // Right after the Open group, whose separator follows Move/Rename.
        if (JobsList.ContextFlyout is MenuFlyout flyout && _menuMoveRename is not null)
        {
            _contextArchiveMenu = CreateArchiveMenu();
            var at = flyout.Items.IndexOf(_menuMoveRename) + 2;
            foreach (var item in _contextArchiveMenu.All) flyout.Items.Insert(at++, item);
        }

        // Under Stop All and its separator.
        _downloadsArchiveMenu = CreateArchiveMenu();
        var position = 2;
        foreach (var item in _downloadsArchiveMenu.All) DownloadsMenu.Items.Insert(position++, item);
        _downloadsArchiveMenu.Extract.AccessKey = "E";
        _downloadsArchiveMenu.ExtractFile.AccessKey = "A";
        _downloadsArchiveMenu.Password.AccessKey = "P";
        _downloadsArchiveMenu.Retry.AccessKey = "R";
        _downloadsArchiveMenu.Cancel.AccessKey = "C";
        _downloadsArchiveMenu.OpenFolder.AccessKey = "O";
        _downloadsArchiveMenu.OpenArchiveFolder.AccessKey = "F";
        _downloadsArchiveMenu.Remove.AccessKey = "M";

        SyncArchiveEntryPoints();
    }

    private ArchiveMenu CreateArchiveMenu() => new()
    {
        Extract = CreateContextItem("Extract", async (_, _) => await ExtractSelectionAsync()),
        ExtractFile = CreateContextItem("Extract Archive…", async (_, _) => await ExtractFromDiskAsync()),
        Password = CreateContextItem("Archive Password…", async (_, _) =>
        {
            if (CurrentArchivePlan().Password is { } target) await EnterArchivePasswordAsync(target);
        }),
        Retry = CreateContextItem("Retry Extraction", async (_, _) => await RetryArchivesAsync(CurrentArchivePlan().Retry)),
        Cancel = CreateContextItem("Cancel Extraction", async (_, _) => await CancelArchivesAsync(CurrentArchivePlan().Cancel)),
        OpenFolder = CreateContextItem("Open Extracted Folder", async (_, _) => await OpenExtractedFoldersAsync(CurrentArchivePlan().OpenFolders)),
        OpenArchiveFolder = CreateContextItem("Open Archive Folder", async (_, _) => await OpenExtractedFoldersAsync(CurrentArchivePlan().ArchiveFolders)),
        Remove = CreateContextItem("Remove Extraction Record…", async (_, _) => await RemoveArchiveRecordsAsync(CurrentArchivePlan().Remove)),
    };

    // ---- Entry points ------------------------------------------------------------------------------

    /// <summary>
    /// Only there once there is an archive to talk about. It takes 140px, and without it the Description
    /// column, which holds a provider's own words and a remote failure's cause, stays on screen at the default width.
    /// </summary>
    private void SyncExtractionColumn()
    {
        var show = _remote.State.ArchivesSupported && _remote.State.ArchiveSets.Count > 0;
        _jobsHeader.SetColumnVisible("extraction", show);
        if (JobViewModel.ShowExtractionColumn == show) return;
        JobViewModel.ShowExtractionColumn = show;
        foreach (var row in _jobsById.Values.Concat(_packageRows.Values).Concat(_remoteRows.Values).Concat(_archiveRows.Values)) row.NotifyExtractionColumn();
    }

    private void SyncArchiveEntryPoints()
    {
        var state = _remote.State;
        var supported = state.ArchivesSupported;
        SyncExtractionColumn();
        SyncArchiveSidebar(supported);
        SyncArchiveMenus();
        QueueArchiveSync();

        // The view that was showing has gone with the capability.
        if (state.IsReady && !supported && _category == ArchiveSetsTag)
        {
            _category = "all";
            CategoryList.SelectedIndex = 0;
            RebuildVisible();
        }
    }

    private void SyncArchiveSidebar(bool supported)
    {
        var item = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(existing => existing.Tag as string == ArchiveSetsTag);
        if (supported == (item is not null)) return;
        _refreshingCategories = true;
        try
        {
            if (item is not null)
            {
                CategoryList.Items.Remove(item);
                return;
            }

            const string Text = "Extractions";
            var added = new ListBoxItem { Tag = ArchiveSetsTag, Padding = new Thickness(14, 2, 8, 2), Content = SidebarContent(Text, "&#xF012;") };
            AutomationProperties.SetName(added, Text);
            var at = CategoryList.Items.OfType<ListBoxItem>().ToList().FindIndex(existing => existing.Tag is "queues");
            CategoryList.Items.Insert(Math.Max(at, 0), added);
        }
        finally
        {
            _refreshingCategories = false;
        }
    }

    private void SyncArchiveMenus()
    {
        if (_downloadsArchiveMenu is null) return;
        _downloadsArchiveMenu.Apply(_remote.State.ArchivesSupported ? CurrentArchivePlan() : null, _remote.State.IsConnected, OnlyArchiveRowsSelected, offerPicking: true);
    }

    /// <summary>Runs last when the context menu opens. A selection of remote rows alone has nothing here to act on.</summary>
    private void ShowArchiveContextItems()
    {
        var remoteOnly = FirstLocalRow is null && SelectedRemoteRows.Count > 0 && SelectedArchiveRows.Count == 0;

        // Open Folder goes there already when the record's download has a file to show.
        var needsArchiveFolder = OnlyArchiveRowsSelected && RecordDownload is not { FileRemovedAfterExtraction: false };
        _contextArchiveMenu?.Apply(_remote.State.ArchivesSupported && !remoteOnly ? CurrentArchivePlan() : null, _remote.State.IsConnected, needsArchiveFolder, offerPicking: !OnlyArchiveRowsSelected);
    }

    private bool OnlyArchiveRowsSelected => FirstLocalRow is null && SelectedRemoteRows.Count == 0 && SelectedArchiveRows.Count > 0;

    private List<JobViewModel> SelectedArchiveRows => JobsList.SelectedItems.OfType<JobViewModel>().Where(row => row.IsArchiveRow).ToList();

    private List<ArchiveSetDto> OnlySelectedRecords => OnlyArchiveRowsSelected ? SelectedArchiveRows.Select(row => row.ArchiveSet).OfType<ArchiveSetDto>().ToList() : [];

    /// <summary>The download that a selected record's row can act on as its own, when the record has one on the list.</summary>
    private JobViewModel? RecordDownload => ArchiveRules.OnlyDownloadBehind(OnlySelectedRecords, _jobsById.ContainsKey) is { } id ? _jobsById[id] : null;

    /// <summary>The download that the context menu's file commands act on: the selected one, or the one behind a selected record.</summary>
    private JobViewModel? CommandJob => SelectedJob ?? RecordDownload;

    /// <summary>The Extractions view lists records, so a record's download lives on All Downloads, where it has every command.</summary>
    private void ShowDownloadsOfSelectedRecords()
    {
        var rows = ArchiveRules.DownloadsBehind(OnlySelectedRecords, _jobsById.ContainsKey).Select(id => _jobsById[id]).ToList();
        if (rows.Count == 0) return;

        // A file in a closed package has no row until the package opens.
        var closed = rows.Select(row => row.PackageId).OfType<Guid>().Distinct().Where(package => !_uiState.ExpandedPackages.Contains(package)).ToList();
        if (closed.Count > 0)
        {
            _uiState.ExpandedPackages.AddRange(closed);
            UiStateStore.Save(_uiState);
        }

        if (rows.Any(row => !MatchesSearch(row))) _search = string.Empty;
        _category = "all";
        CategoryList.SelectedIndex = 0;
        RebuildVisible();

        JobsList.SelectedItems.Clear();
        foreach (var row in rows.Where(_visibleJobs.Contains)) JobsList.SelectedItems.Add(row);
        if (JobsList.SelectedItems.Count > 0) JobsList.ScrollIntoView(JobsList.SelectedItems[0]);

        // The menu is still closing, and it gives the focus back when it has. The row it came from has gone.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (JobsList.SelectedItems.Count > 0 && JobsList.ContainerFromItem(JobsList.SelectedItems[0]) is Control row) row.Focus(FocusState.Keyboard);
            else JobsList.Focus(FocusState.Keyboard);
        });
    }

    private ArchivePlan CurrentArchivePlan()
    {
        // An archive that is gone and has no record left is nothing to extract or set a password for.
        var files = SelectedJobs
            .Where(job => !job.FileRemovedAfterExtraction || _setByJob.ContainsKey(job.Id))
            .Select(job => new ArchiveSelectedFile(job.Id, job.FileName, job.FinalFilePath, _setByJob.GetValueOrDefault(job.Id)))
            .ToList();
        var packages = SelectedPackageRows;
        var others = SelectedArchiveRows.Select(row => row.ArchiveSet).OfType<ArchiveSetDto>()
            .Concat(packages.SelectMany(ArchiveSetsOfPackageRow))
            .ToList();
        (Guid, string)? alone = packages is [var package] && JobsList.SelectedItems.Count == 1 ? (package.Id, package.FileName) : null;
        return ArchiveRules.Plan(files, others, alone, _remote.State.IsConnected);
    }

    private List<ArchiveSetDto> ArchiveSetsOfPackageRow(JobViewModel row)
    {
        var members = _groups.FirstOrDefault(group => group.Id == row.Id)?.JobIds;
        return _remote.State.ArchiveSets
            .Where(set => set.PackageId == row.Id || (members is not null && set.JobIds.Any(members.Contains)))
            .ToList();
    }

    // ---- Rows --------------------------------------------------------------------------------------

    private void QueueArchiveSync()
    {
        // A list of sets arrives one at a time, and a run keeps reporting. One pass covers them all.
        if (_archiveSyncQueued) return;
        _archiveSyncQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _archiveSyncQueued = false;
            SyncArchiveState();
        });
    }

    private void SyncArchiveState()
    {
        var sets = _remote.State.ArchiveSets.ToList();
        _setByJob.Clear();
        foreach (var set in sets.OrderBy(set => set.UpdatedAt))
        {
            foreach (var job in set.JobIds) _setByJob[job] = set;
        }

        foreach (var job in _jobsById.Values) ShowArchiveState(job);
        foreach (var row in _packageRows.Values) ShowPackageArchiveState(row);

        foreach (var set in sets)
        {
            if (!_archiveRows.TryGetValue(set.Id, out var row)) _archiveRows[set.Id] = row = new JobViewModel();
            row.UpdateArchiveRow(set, ArchiveText.Set(set));
        }

        foreach (var gone in _archiveRows.Keys.Where(id => sets.All(set => set.Id != id)).ToList()) _archiveRows.Remove(gone);

        if (ArchiveRowsInView().SequenceEqual(_visibleJobs.Where(row => row.IsArchiveRow))) UpdateButtons();
        else RebuildVisible();
        SyncArchiveNotice(sets);

        // A selected row of these is quoted on the status bar, and they change in place, as remote rows do.
        UpdateStatusText();
    }

    private void ShowArchiveState(JobViewModel job)
        => job.ShowArchive(_setByJob.TryGetValue(job.Id, out var set) ? ArchiveText.Set(set) : null);

    private void ShowPackageArchiveState(JobViewModel row) => row.ShowArchive(ArchiveText.Package(ArchiveSetsOfPackageRow(row)));

    /// <summary>A record whose files aren't on the list. Until the Host's list of downloads is here, every record would look like one.</summary>
    private bool HasOwnRow(ArchiveSetDto set) => _graphSnapshotReady && !set.JobIds.Any(_jobsById.ContainsKey);

    /// <summary>Every record in the Extractions view; on All Downloads, only those with no file there to show them.</summary>
    private List<JobViewModel> ArchiveRowsInView()
    {
        var showAll = _category == ArchiveSetsTag;
        if (!showAll && _category != "all") return [];
        var rows = _archiveRows.Values
            .Where(row => showAll || (row.ArchiveSet is { } set && HasOwnRow(set)))
            .Where(MatchesSearch)
            .OrderByDescending(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToList();
        for (var i = 0; i < rows.Count; i++) rows[i].MarkLastRemoteRow(i == rows.Count - 1 && !showAll);
        return rows;
    }

    private bool HandleArchiveRowKey(KeyRoutedEventArgs e)
    {
        var rows = SelectedArchiveRows;
        if (rows.Count == 0 || FirstLocalRow is not null || SelectedRemoteRows.Count > 0) return false;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Delete:
                e.Handled = true;
                _ = RemoveSideRowsAsync();
                return true;
            case Windows.System.VirtualKey.Enter when rows is [{ ArchiveSet: { } set }]:
                e.Handled = true;
                _ = RunArchivePrimaryAsync(set);
                return true;
            default:
                return false;
        }
    }

    private Task RunArchivePrimaryAsync(ArchiveSetDto set) => ArchiveRules.Primary(set) switch
    {
        ArchiveRowAction.EnterPassword => EnterArchivePasswordAsync(ArchiveRules.Target(set)),
        ArchiveRowAction.OpenFolder => OpenExtractedFoldersAsync([set.Destination!]),
        ArchiveRowAction.Retry => RetryArchivesAsync([set]),
        _ => Task.CompletedTask,
    };

    private async void ArchiveRowPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not JobViewModel row) return;
        ArchivePasswordTarget? target = null;
        if (row.IsArchiveRow && row.ArchiveSet is { } own) target = ArchiveRules.Target(own);
        else if (row.IsPackage) target = ArchiveRules.PackageTarget(row.Id, row.FileName, ArchiveSetsOfPackageRow(row));
        else if (_setByJob.TryGetValue(row.Id, out var set)) target = ArchiveRules.Target(set);
        if (target is not null) await EnterArchivePasswordAsync(target);
    }

    // ---- The notice above the list -----------------------------------------------------------------

    private void SyncArchiveNotice(IReadOnlyList<ArchiveSetDto> sets)
    {
        var notice = _archiveNotices.Update(sets, WatchingSince, _settings?.NotifyOnCompletion != false);
        if (notice == _archiveNotice) return;
        _archiveNotice = notice;
        ArchiveNoticeBar.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (notice is null)
        {
            // A button in the bar may have had the focus.
            if (ArchiveNoticeAction.FocusState != FocusState.Unfocused || ArchiveNoticeDismiss.FocusState != FocusState.Unfocused) JobsList.Focus(FocusState.Programmatic);
            return;
        }

        ArchiveNoticeText.Text = notice.Text;
        ToolTipService.SetToolTip(ArchiveNoticeText, notice.Text);
        AutomationProperties.SetName(ArchiveNoticeText, $"Extraction notice: {notice.Text}");
        ArchiveNoticeAction.Content = notice.ActionText;
        LiveRegion.Announce(ArchiveNoticeText);
    }

    private async void ArchiveNoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveNotice is not { } notice) return;
        var sets = _remote.State.ArchiveSets.ToList();
        var set = sets.FirstOrDefault(candidate => candidate.Id == notice.SetId);
        if (set is null) return;
        if (notice.Kind == ArchiveNoticeKind.Password)
        {
            await EnterArchivePasswordAsync(ArchiveRules.NoticeTarget(set, sets, _groups.FirstOrDefault(group => group.Id == set.PackageId)?.Name));
            return;
        }

        DismissArchiveNotice(notice, sets);
        if (!string.IsNullOrWhiteSpace(set.Destination)) await OpenExtractedFoldersAsync([set.Destination]);
    }

    private void ArchiveNoticeDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveNotice is { } notice) DismissArchiveNotice(notice, _remote.State.ArchiveSets.ToList());
    }

    private void DismissArchiveNotice(ArchiveNotice notice, IReadOnlyList<ArchiveSetDto> sets)
    {
        _archiveNotices.Dismiss(notice, sets);
        SyncArchiveNotice(sets);
    }

    // ---- Commands ----------------------------------------------------------------------------------

    private async Task ExtractSelectionAsync()
    {
        var plan = CurrentArchivePlan();
        if (!plan.CanExtract) return;
        var problems = new List<string>();
        var words = new List<string>();
        if (plan.ExtractJobIds.Count > 0)
        {
            var result = await _remote.ExtractAsync(new ArchiveExtractRequest { JobIds = plan.ExtractJobIds.ToList() });
            if (result.Ok) words.Add(ArchiveText.ExtractSummary(result));
            else problems.Add(RemoteText.Failure(result.Error));
        }

        foreach (var set in plan.ExtractSets)
        {
            var result = await _remote.RetryArchiveAsync(set.Id);
            if (result.Ok) words.Add($"Extraction asked for: {set.Name}.");
            else problems.Add($"{set.Name}: {RemoteText.Failure(result.Error)}");
        }

        if (problems.Count > 0) await ShowMessageAsync("Extract", string.Join(Environment.NewLine, problems.Take(5)));
        else if (words.Count > 0) ShowPassingStatus(string.Join(" ", words));
    }

    private async Task ExtractFromDiskAsync()
    {
        string? file;
        if (RemoteDataRules.UsesServerFolderBrowser(_connection.IsRemote))
        {
            // Another server extracts its own files: only its folders are offered, never this PC's.
            file = await AskServerArchivePathAsync();
        }
        else
        {
            try
            {
                file = (await Pickers.PickFilesAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), [".rar", ".7z", ".zip", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst", ".001"], multiple: false)).FirstOrDefault();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException)
            {
                await ShowMessageAsync("Extract archive", "The file picker couldn't open.");
                return;
            }
        }

        if (file is null) return;
        var result = await _remote.ExtractAsync(new ArchiveExtractRequest { FilePath = file });
        if (!result.Ok)
        {
            await ShowMessageAsync("Extract archive", RemoteText.Failure(result.Error));
            return;
        }

        // Asked for from outside the list, so it may have no row in the view showing now.
        if (_category != "all" && _category != ArchiveSetsTag && result.Sets.Any(set => !set.JobIds.Any(_jobsById.ContainsKey)))
        {
            CategoryList.SelectedItem = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag as string == ArchiveSetsTag);
        }

        var summary = ArchiveText.ExtractSummary(result);
        if (result.Ignored.Count > 0) await ShowMessageAsync("Extract archive", summary);
        else ShowPassingStatus(summary);
    }

    /// <summary>Only ever opened by the user: the row, the notice or the menu asks, and this answers.</summary>
    private async Task EnterArchivePasswordAsync(ArchivePasswordTarget target)
    {
        var box = new PasswordBox { Header = "Archive password", PasswordRevealMode = PasswordRevealMode.Hidden };
        AutomationProperties.SetName(box, "Archive password");
        var hint = new TextBlock { Text = ArchiveText.PasswordHint(target), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(hint, hint.Text);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = target.WasWrong ? "Wrong archive password" : "Archive password",
            Content = new StackPanel { Spacing = 10, MinWidth = 420, Children = { hint, box } },
            PrimaryButtonText = target.Needed ? "Extract with This Password" : "Save Password",
            SecondaryButtonText = target.HasPassword ? "Forget Saved Password" : string.Empty,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        // Null when another dialog is open over this window (UI.B8). The row and the notice are still there.
        var answer = await DialogGuard.ShowAsync(dialog);
        if (answer is null)
        {
            box.Password = string.Empty;
            return;
        }

        // Out of the box before anything is sent, whatever the answer turns out to be.
        var password = box.Password;
        box.Password = string.Empty;
        if (answer == ContentDialogResult.None || (answer == ContentDialogResult.Primary && password.Length == 0)) return;
        if (answer == ContentDialogResult.Secondary) password = string.Empty;

        var result = await _remote.SetArchivePasswordAsync(new ArchiveSetPasswordRequest
        {
            SetId = target.SetId,
            PackageId = target.PackageId,
            FilePath = target.FilePath,
            Password = password,
        });
        var cleared = password.Length == 0;
        password = string.Empty;
        if (!result.Ok) await ShowMessageAsync("Archive password", RemoteText.Failure(result.Error));
        else if (cleared) ShowPassingStatus($"The saved archive password for \"{target.Name}\" is gone.");
        else if (target.Needed) ShowPassingStatus($"Trying to extract \"{target.Name}\" with this password…");
        else ShowPassingStatus($"Archive password saved for \"{target.Name}\".");
    }

    private async Task RetryArchivesAsync(IReadOnlyList<ArchiveSetDto> sets)
    {
        if (sets.Count == 0) return;
        if (sets.Any(set => set.State == "completed") &&
            !await ConfirmAsync("Extract again?", "This extracts the archive again. The Extraction page in Options decides what happens to files that are already there.", "Extract Again"))
        {
            return;
        }

        await RunOnSetsAsync("Retry extraction", sets, _remote.RetryArchiveAsync);
    }

    private Task CancelArchivesAsync(IReadOnlyList<ArchiveSetDto> sets)
        => RunOnSetsAsync("Cancel extraction", sets, _remote.CancelArchiveAsync);

    private async Task RemoveArchiveRecordsAsync(IReadOnlyList<ArchiveSetDto> sets)
    {
        if (sets.Count == 0 || !await ConfirmAsync("Remove extraction record?", ArchiveText.RemoveQuestion(sets), "Remove Record")) return;
        await RemoveArchiveRecordsConfirmedAsync(sets);
    }

    private Task RemoveArchiveRecordsConfirmedAsync(IReadOnlyList<ArchiveSetDto> sets)
        => RunOnSetsAsync("Remove extraction record", sets, _remote.RemoveArchiveAsync);

    private async Task RunOnSetsAsync(string title, IReadOnlyList<ArchiveSetDto> sets, Func<Guid, CancellationToken, Task<ArchiveActionResult>> action)
    {
        var problems = new List<string>();
        foreach (var set in sets)
        {
            var result = await action(set.Id, CancellationToken.None);
            if (!result.Ok) problems.Add($"{set.Name}: {RemoteText.Failure(result.Error)}");
        }

        if (problems.Count > 0) await ShowMessageAsync(title, string.Join(Environment.NewLine, problems.Take(5)));
    }

    /// <summary>
    /// The path of an archive on the server the window shows: a folder of the server's, chosen with its folder browser,
    /// and the archive's name in it. Null when cancelled.
    /// </summary>
    private async Task<string?> AskServerArchivePathAsync()
    {
        var target = _connection.Target;
        var folder = new TextBox { Header = $"Folder on {target.Name}", IsReadOnly = true, PlaceholderText = "Choose a folder" };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var name = new TextBox { Header = "Archive's file name", PlaceholderText = "movie.part1.rar" };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(folder);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            if (await ServerFolderWindow.PickAsync(target, folder.Text) is { } chosen) folder.Text = chosen;
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        panel.Children.Add(new TextBlock { Text = $"The archive is extracted on {target.Name}, from one of its folders.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(row);
        panel.Children.Add(name);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Extract archive",
            Content = panel,
            PrimaryButtonText = "Extract",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        var chosenFolder = folder.Text.Trim();
        var fileName = name.Text.Trim();
        if (chosenFolder.Length == 0 || fileName.Length == 0 || fileName.IndexOfAny(['/', '\\']) >= 0) return null;
        // The server's own separator: a Linux server's paths use '/'.
        var separator = chosenFolder.Contains('\\') ? '\\' : '/';
        return chosenFolder.TrimEnd('/', '\\') + separator + fileName;
    }

    /// <summary>Asking whether a folder is there stays off the UI thread, because it can be a slow or missing network share.</summary>
    private async Task OpenExtractedFoldersAsync(IReadOnlyList<string> folders)
    {
        // Another server's folders are on that machine: this PC never asks about them.
        if (SayIfOnAnotherServer()) return;
        var there = await Task.Run(() => folders.Where(folder => Path.IsPathFullyQualified(folder) && Directory.Exists(folder)).ToList());
        var problems = folders.Except(there, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var folder in there.Take(5))
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                problems.Add(folder);
            }
        }

        if (problems.Count > 0) await ShowMessageAsync("Open folder", $"This folder could not be opened: {string.Join(", ", problems)}");
    }
}
