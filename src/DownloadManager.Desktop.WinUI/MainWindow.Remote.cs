using System.ComponentModel;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Remote work in the main window: the entry points, which exist only while the Host offers them, and
/// the rows for jobs with no local download yet. Those rows live in <see cref="_remoteRows"/> and never
/// in <c>_jobsById</c>, so counts, bulk actions and the speed graph stay about local files.
/// </summary>
public sealed partial class MainWindow
{
    private const string RemoteWaitingTag = "remote:waiting:";
    private const string RemoteBrowseTag = "remote:browse:";
    private const string DefaultEmptyText = "No downloads in this view.\nUse Add URL or the browser extension to start one.";

    private readonly Dictionary<Guid, JobViewModel> _remoteRows = [];
    private readonly Dictionary<string, MyProviderWindow> _providerWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MenuFlyoutItemBase> _localContextItems = [];
    private readonly List<MenuFlyoutItemBase> _remoteContextItems = [];
    private readonly List<MenuFlyoutItemBase> _remoteTasksItems = [];
    private MenuFlyoutItem? _menuRemoteSelectFiles;
    private MenuFlyoutItem? _menuRemoteDownloadAll;
    private MenuFlyoutItem? _menuRemoteApprove;
    private MenuFlyoutItem? _menuRemoteLinkPassword;
    private MenuFlyoutItem? _menuRemoteRetry;
    private MenuFlyoutItem? _menuRemoteCheckNow;
    private MenuFlyoutItem? _menuRemoteRemove;
    private MenuFlyoutItem? _menuRemoteStartQueued;
    private MenuFlyoutItem? _menuRemoteDequeue;
    private MenuFlyoutSeparator? _remoteContextSeparator;
    private string? _pendingRemoteCategory;
    private string _remoteTasksOffers = string.Empty;

    private void InitializeRemoteUi()
    {
        var state = _remote.State;
        state.Jobs.CollectionChanged += (_, _) => SyncRemoteRows();
        state.Accounts.CollectionChanged += (_, _) =>
        {
            SyncRemoteEntryPoints();
            ShowProviderSources();
        };
        state.PropertyChanged += OnRemoteStateChanged;
        SyncRemoteEntryPoints();
        InitializeAutoOpen();
    }

    private void OnRemoteStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteState.ArchivesSupported) or nameof(RemoteState.IsConnected) or nameof(RemoteState.IsReady)) SyncArchiveEntryPoints();
        if (e.PropertyName is nameof(RemoteState.RemoteSupported) or nameof(RemoteState.IsReady))
        {
            SyncRemoteRows();
            SyncRemoteEntryPoints();
        }
    }

    private string ProviderDisplayName(string? provider)
        => _remote.State.FindAccount(provider) is { } account ? RemoteText.ProviderName(account) : provider ?? string.Empty;

    // ---- Rows --------------------------------------------------------------------------------------

    /// <summary>Follows the state in place. A change to one job touches only its row, so nothing blinks.</summary>
    private void SyncRemoteRows()
    {
        var jobs = _remote.State.Jobs.Where(RemoteRules.IsRow).ToList();
        var changed = false;
        var now = DateTimeOffset.Now;
        foreach (var job in jobs)
        {
            if (!_remoteRows.TryGetValue(job.Id, out var row))
            {
                _remoteRows[job.Id] = row = new JobViewModel();
                changed = true;
            }

            row.UpdateRemote(job, RemoteText.JobRow(job, now));
        }

        foreach (var gone in _remoteRows.Keys.Except(jobs.Select(job => job.Id)).ToList())
        {
            _remoteRows.Remove(gone);
            changed = true;
        }

        if (changed || _search.Length > 0) RebuildVisible();
        SyncRemoteEntryPoints();

        // The status bar quotes a selected row like these, and they change in place: otherwise it kept the words
        // the row had when it was chosen, such as a "Next check" long past under a row that says Choose files.
        UpdateStatusText();
    }

    /// <summary>All of them on All Downloads, one provider's in its own view, nothing anywhere else.</summary>
    private List<JobViewModel> RemoteRowsInView()
    {
        var provider = _category.StartsWith(RemoteWaitingTag, StringComparison.Ordinal) ? _category[RemoteWaitingTag.Length..] : null;
        if (provider is null && _category != "all") return [];
        var rows = _remoteRows.Values
            .Where(row => provider is null || RemoteRules.SameProvider(row.RemoteJob?.Provider, provider))
            .Where(MatchesSearch)
            .OrderByDescending(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToList();
        for (var i = 0; i < rows.Count; i++) rows[i].MarkLastRemoteRow(i == rows.Count - 1 && provider is null);
        return rows;
    }

    private bool MatchesSearch(JobViewModel row) => _search.Length == 0 ||
        row.FileName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        row.StateText.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        row.ArchiveText.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        row.Description.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private void UpdateEmptyText()
    {
        EmptyText.Text = _category.StartsWith(RemoteWaitingTag, StringComparison.Ordinal)
            ? $"Nothing is waiting for {ProviderDisplayName(_category[RemoteWaitingTag.Length..])}.\nItems show here until their files start downloading."
            : _category == ArchiveSetsTag ? ArchiveSetsEmptyText
            : DefaultEmptyText;
    }

    /// <summary>The rows are drawn before the accounts arrive, when all there is to call a provider is its id.</summary>
    private void ShowProviderSources()
    {
        foreach (var job in _jobsById.Values) ShowProviderSource(job);
    }

    /// <summary>Says which account a local file came from, and its package, because one file alone gets no package row.</summary>
    private void ShowProviderSource(JobViewModel job)
    {
        if (!job.IsProviderFile) return;
        var provider = ProviderDisplayName(job.Dto?.RemoteProvider);
        var package = _groups.FirstOrDefault(group => group.JobIds.Contains(job.Id))?.Name;
        job.ShowProviderSource(string.IsNullOrWhiteSpace(package) ? $"From {provider}" : $"From {provider}: {package}");
    }

    private List<JobViewModel> SelectedRemoteRows => JobsList.SelectedItems.OfType<JobViewModel>().Where(row => row.IsRemoteRow).ToList();

    private List<RemoteJobDto> SelectedRemoteJobs => SelectedRemoteRows.Select(row => row.RemoteJob).OfType<RemoteJobDto>().ToList();

    // ---- Entry points -----------------------------------------------------------------------------

    /// <summary>Hidden when the Host has no remote capability. My provider needs a usable key; a waiting view also shows for jobs left from before a key was cleared.</summary>
    private void SyncRemoteEntryPoints()
    {
        var state = _remote.State;
        var accounts = state.RemoteSupported ? state.Accounts.ToList() : [];
        var wanted = new List<(string Tag, string Text, string Glyph)>();
        foreach (var account in accounts)
        {
            var name = RemoteText.ProviderName(account);
            var waiting = state.Jobs.Count(job => RemoteRules.IsRow(job) && RemoteRules.SameProvider(job.Provider, account.Provider));
            if (RemoteRules.HasUsableKey(account) || waiting > 0)
            {
                wanted.Add((RemoteWaitingTag + account.Provider, waiting > 0 ? $"Waiting for {name} ({waiting})" : $"Waiting for {name}", "&#xE753;"));
            }

            if (RemoteRules.CanBrowse(account)) wanted.Add((RemoteBrowseTag + account.Provider, $"My {name}", "&#xEC50;"));
        }

        _refreshingCategories = true;
        try
        {
            for (var index = CategoryList.Items.Count - 1; index >= 0; index--)
            {
                if (CategoryList.Items[index] is ListBoxItem { Tag: string tag } && tag.StartsWith("remote:", StringComparison.Ordinal) && wanted.All(entry => entry.Tag != tag))
                {
                    CategoryList.Items.RemoveAt(index);
                }
            }

            var at = CategoryList.Items.OfType<ListBoxItem>().ToList().FindIndex(item => item.Tag is "queues") + 1;
            foreach (var (tag, text, glyph) in wanted)
            {
                var item = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(existing => existing.Tag as string == tag);
                if (item is null)
                {
                    item = new ListBoxItem { Tag = tag, Padding = new Thickness(14, 2, 8, 2) };
                    CategoryList.Items.Insert(at, item);
                }

                if ((item.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text != text)
                {
                    item.Content = SidebarContent(text, glyph);
                    AutomationProperties.SetName(item, text);
                }

                at++;
            }
        }
        finally
        {
            _refreshingCategories = false;
        }

        if (_pendingRemoteCategory is { } pending)
        {
            var item = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(existing => existing.Tag as string == pending);
            if (item is not null || state.IsReady) _pendingRemoteCategory = null;
            if (item is not null) CategoryList.SelectedItem = item;
        }

        // The view that was showing has gone: its provider or its capability did.
        if (state.IsReady && _category.StartsWith(RemoteWaitingTag, StringComparison.Ordinal) && wanted.All(entry => entry.Tag != _category))
        {
            _category = "all";
            CategoryList.SelectedIndex = 0;
            RebuildVisible();
        }

        SyncRemoteTasksMenu(accounts);
    }

    /// <summary>The one glyph brush that follows the theme by itself is one set from XAML, so this piece is built that way.</summary>
    private static StackPanel SidebarContent(string text, string glyph)
    {
        var icon = (FontIcon)XamlReader.Load(
            "<FontIcon xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            $"Glyph=\"{glyph}\" FontSize=\"14\" Foreground=\"{{ThemeResource InboundIconInfoBrush}}\" />");
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, new TextBlock { Text = text } } };
    }

    private void RestoreCategorySelection()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshingCategories = true;
            try
            {
                CategoryList.SelectedItem = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag as string == _category);
            }
            finally
            {
                _refreshingCategories = false;
            }
        });
    }

    private void SyncRemoteTasksMenu(List<RemoteAccountDto> accounts)
    {
        var usable = accounts.Where(RemoteRules.HasUsableKey).ToList();
        var browsable = usable.Where(RemoteRules.CanBrowse).ToList();
        var torrents = usable.Any(account => account.Capabilities.Contains("torrents", StringComparer.OrdinalIgnoreCase));
        var canCheck = _remote.State.Jobs.Any(job => RemoteRules.Actions(job).CheckNow);

        // This runs on every job change, and the menu may be open. It only changes when what it offers does.
        var offers = $"{torrents}|{usable.Count}|{canCheck}|{string.Join('|', browsable.Select(account => RemoteText.ProviderName(account)))}";
        if (offers == _remoteTasksOffers) return;
        _remoteTasksOffers = offers;
        foreach (var old in _remoteTasksItems) TasksMenu.Items.Remove(old);
        _remoteTasksItems.Clear();
        if (usable.Count == 0) return;

        // After Add Links, before the export group.
        var at = 2;
        void Add(string text, string accessKey, RoutedEventHandler click, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, AccessKey = accessKey, IsEnabled = enabled };
            item.Click += click;
            TasksMenu.Items.Insert(at++, item);
            _remoteTasksItems.Add(item);
        }

        if (torrents)
        {
            Add("Add Magnet…", "M", async (_, _) => await ShowProviderAddAsync(AddFocus.Magnet));
            Add("Import Torrent File…", "T", async (_, _) => await ImportTorrentFilesAsync());
        }

        foreach (var account in browsable)
        {
            Add($"Browse My {RemoteText.ProviderName(account)}…", "B", (_, _) => OpenProviderWindow(account.Provider));
        }

        if (browsable.Count > 0) Add($"{RemoteText.ProviderName(browsable[0])} Hosters…", "H", (_, _) => HosterWindow.Show(_remote));

        Add("Import JD Container…", "J", async (_, _) => await ImportJdContainerAsync());

        // Dimmed with nothing waiting, because then there is nothing for it to do.
        Add("Check Remote Status Now", "C", async (_, _) => await CheckRemoteStatusNowAsync(), canCheck);
    }

    private async Task CheckRemoteStatusNowAsync()
    {
        var result = await _remote.CheckNowAsync();
        if (!result.Ok) await ShowMessageAsync("Check remote status now", RemoteText.Failure(result.Error));
        else ShowPassingStatus("Checking remote status now…");
    }

    private void OpenProviderWindow(string provider)
    {
        if (_remote.State.FindAccount(provider) is not { } account || !RemoteRules.CanBrowse(account)) return;
        if (!_providerWindows.TryGetValue(provider, out var window))
        {
            window = new MyProviderWindow(_remote, account, new MyProviderContext(
                () => _settings,
                LocalStatusOf,
                RememberFolder,
                _uiState,
                () => SaveUiState()));
            _providerWindows[provider] = window;
            window.Closed += (_, _) => _providerWindows.Remove(provider);
        }

        window.ShowWindow();
    }

    private List<JobViewModel> LocalFilesOf(RemoteJobDto job)
    {
        if (job.PackageId is not { } package || _groups.FirstOrDefault(group => group.Id == package) is not { } found) return [];
        return found.JobIds.Where(_jobsById.ContainsKey).Select(id => _jobsById[id]).ToList();
    }

    private string? LocalStatusOf(RemoteJobDto job)
        => RemoteText.LocalWords(LocalFilesOf(job).Select(file => file.Dto).OfType<JobDto>().ToList());


    /// <summary>The same memory Add URL fills: <c>remote.add</c> leaves it alone, so a folder chosen for a provider's item is saved here.</summary>
    private async void RememberFolder(string folder)
    {
        if (string.Equals(_settings?.LastDestinationDirectory, folder, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var current = await _connection.RefreshAsync();
            var updated = CloneSettings(current.Settings);
            updated.LastDestinationDirectory = folder;
            var result = (await _connection.UpdateSettingsAsync(updated)).GetPayload<SettingsResult>();
            if (result.Ok) _settings = result.Settings;
        }
        catch (Exception ex)
        {
            // Only a convenience for next time. The download itself has already been added.
            UiTrace.Write($"Remembering the folder failed: {ex.GetType().Name}");
        }
    }

    // ---- Commands ----------------------------------------------------------------------------------

    private void AddRemoteContextItems(MenuFlyout flyout)
    {
        // Everything already there is for local rows.
        _localContextItems.AddRange(flyout.Items);
        // For several rows, one dialog after another.
        _menuRemoteSelectFiles = CreateContextItem("Select Files…", async (_, _) => await WalkFileChoicesAsync(SelectedRemoteJobs));
        _menuRemoteDownloadAll = CreateContextItem(RemoteText.DownloadAllFiles, async (_, _) => await DownloadAllFilesAsync(SelectedRemoteJobs));
        // Named for the selected rows' provider when the menu opens.
        _menuRemoteApprove = CreateContextItem("Add", async (_, _) => await ApproveRemoteAsync(SelectedRemoteJobs));
        _menuRemoteLinkPassword = CreateContextItem("Enter Link Password…", async (_, _) => { if (SelectedRemoteJobs is [var job]) await EnterLinkPasswordAsync(job); });
        _menuRemoteRetry = CreateContextItem("Retry", async (_, _) => await RetryRemoteAsync(SelectedRemoteJobs));
        _menuRemoteCheckNow = CreateContextItem("Check Now", async (_, _) => await CheckRemoteNowAsync(SelectedRemoteJobs));
        _menuRemoteCheckNow.KeyboardAcceleratorTextOverride = "F5";
        // TB.S1: named for the provider when the menu opens.
        _menuRemoteStartQueued = CreateContextItem("Start Now", async (_, _) => await StartQueuedAsync(SelectedRemoteJobs));
        _menuRemoteDequeue = CreateContextItem("Remove from Queue…", async (_, _) => await RemoveFromProviderQueueAsync(SelectedRemoteJobs));
        _menuRemoteRemove = CreateContextItem("Remove from List…", async (_, _) => await RemoveRemoteAsync(SelectedRemoteJobs));
        _menuRemoteRemove.KeyboardAcceleratorTextOverride = "Del";
        _remoteContextSeparator = new MenuFlyoutSeparator();
        MenuFlyoutItemBase[] items = [_menuRemoteSelectFiles, _menuRemoteDownloadAll, _menuRemoteApprove, _menuRemoteLinkPassword, _menuRemoteRetry, _menuRemoteCheckNow, _menuRemoteStartQueued, _menuRemoteDequeue, _menuRemoteRemove, _remoteContextSeparator];
        for (var i = 0; i < items.Length; i++) flyout.Items.Insert(i, items[i]);
        _remoteContextItems.AddRange(items);
    }

    /// <summary>Runs first, so the local menu's own rules below it decide for themselves after this.</summary>
    private void ShowContextItemsForSelection()
    {
        foreach (var item in _localContextItems) item.Visibility = Visibility.Visible;

        var jobs = SelectedRemoteJobs;
        var actions = jobs.Select(RemoteRules.Actions).ToList();
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        if (_menuRemoteSelectFiles is not null) _menuRemoteSelectFiles.Visibility = Show(RemoteRules.AllCan(jobs, action => action.SelectFiles));
        if (_menuRemoteDownloadAll is not null) _menuRemoteDownloadAll.Visibility = Show(RemoteRules.OffersDownloadAll(jobs));
        if (_menuRemoteApprove is not null)
        {
            _menuRemoteApprove.Visibility = Show(RemoteRules.AllCan(jobs, action => action.Approve));
            _menuRemoteApprove.Text = RemoteText.AddTo(jobs);
        }

        if (_menuRemoteLinkPassword is not null) _menuRemoteLinkPassword.Visibility = Show(actions is [{ EnterLinkPassword: true }]);
        if (_menuRemoteRetry is not null) _menuRemoteRetry.Visibility = Show(actions.Any(action => action.Retry));
        if (_menuRemoteCheckNow is not null) _menuRemoteCheckNow.Visibility = Show(actions.Any(action => action.CheckNow));
        var queued = jobs.Count > 0 && jobs.All(RemoteRules.IsInProviderQueue);
        var queueProvider = queued ? RemoteText.ProviderName(jobs[0]) : string.Empty;
        if (_menuRemoteStartQueued is not null)
        {
            _menuRemoteStartQueued.Visibility = Show(queued);
            _menuRemoteStartQueued.Text = $"Start Now on {queueProvider}";
        }

        if (_menuRemoteDequeue is not null)
        {
            _menuRemoteDequeue.Visibility = Show(queued);
            _menuRemoteDequeue.Text = $"Remove from {queueProvider}'s Queue…";
        }

        if (_menuRemoteRemove is not null) _menuRemoteRemove.Visibility = Show(jobs.Count > 0);
        if (_remoteContextSeparator is not null) _remoteContextSeparator.Visibility = Show(jobs.Count > 0 && FirstLocalRow is not null);
    }

    /// <summary>
    /// Runs last. A selection of remote rows alone has nothing for the local items to act on. A record's row
    /// keeps the file commands for the download behind it, and leads to that download for the rest.
    /// </summary>
    private void HideLocalContextItemsIfRemoteOnly()
    {
        var behind = ArchiveRules.DownloadsBehind(OnlySelectedRecords, _jobsById.ContainsKey).Count;
        if (FirstLocalRow is null && (SelectedRemoteRows.Count > 0 || SelectedArchiveRows.Count > 0))
        {
            foreach (var item in _localContextItems) item.Visibility = Visibility.Collapsed;
            if (RecordDownload is not null)
            {
                foreach (var item in _recordFileItems) item.Visibility = Visibility.Visible;
            }
        }

        if (_menuShowDownload is not null)
        {
            _menuShowDownload.Text = behind == 1 ? "Show Download" : "Show Downloads";
            _menuShowDownload.Visibility = behind > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_separatorAfterOpen is not null && behind > 0) _separatorAfterOpen.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The list's keys for remote rows. Enter does the next thing a row is waiting for, F5 checks, Delete
    /// takes it off the list. Before the list sees them: it keeps Enter for itself.
    /// </summary>
    private void JobsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A button on the row, such as the archive password key, has the focus and answers Enter itself.
        if (e.Key == Windows.System.VirtualKey.Enter && e.OriginalSource is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
        if (HandleArchiveRowKey(e)) return;
        var jobs = SelectedRemoteJobs;
        if (jobs.Count == 0)
        {
            // The list takes Enter for itself before KeyDown, so it is caught here for local rows too.
            if (e.Key == Windows.System.VirtualKey.Enter && JobsList.SelectedItems.Count == 1)
            {
                e.Handled = true;
                _ = RunRowPrimaryAsync();
            }

            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.F5:
                e.Handled = true;
                _ = CheckRemoteNowAsync(jobs);
                break;
            case Windows.System.VirtualKey.Delete when FirstLocalRow is null:
                e.Handled = true;
                _ = RemoveSideRowsAsync();
                break;
            case Windows.System.VirtualKey.Enter when FirstLocalRow is null:
                e.Handled = true;
                _ = RunRemoteEnterAsync(jobs);
                break;
        }
    }

    /// <summary>Enter on remote rows: one row's next step, or, for several, adding them all or choosing their files in turn.</summary>
    private Task RunRemoteEnterAsync(IReadOnlyList<RemoteJobDto> jobs) => RemoteRules.EnterFor(jobs) switch
    {
        RemoteRowAction.SelectFiles => WalkFileChoicesAsync(jobs),
        RemoteRowAction.Approve => ApproveRemoteAsync(jobs),
        RemoteRowAction.EnterLinkPassword => EnterLinkPasswordAsync(jobs[0]),
        RemoteRowAction.Retry => RetryRemoteAsync(jobs),
        RemoteRowAction.CheckNow => CheckRemoteNowAsync(jobs),
        _ => Task.CompletedTask,
    };

    private Task RunRemotePrimaryAsync(RemoteJobDto job) => RunRemoteEnterAsync([job]);

    /// <summary>Several rows' files, one dialog after another down the list, each row as it is by its turn. Cancel ends it.</summary>
    private Task WalkFileChoicesAsync(IReadOnlyList<RemoteJobDto> jobs)
        => RemoteRules.WalkFileChoicesAsync(
            RemoteRules.InListOrder(jobs, _visibleJobs.Select(row => row.Id).ToList()),
            id => _remote.State.Jobs.FirstOrDefault(job => job.Id == id),
            SelectRemoteFilesAsync);

    /// <summary>Every selected row that waits for a file choice takes all of its files, with nothing asked.</summary>
    private async Task DownloadAllFilesAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        var problems = new List<string>();
        var taken = 0;
        foreach (var job in RemoteRules.TakingAllFiles(jobs))
        {
            var result = await _remote.SelectAsync(new RemoteSelectRequest { JobId = job.Id, All = true });
            if (result.Ok) taken++;
            else problems.Add(RemoteText.Refused(RemoteText.JobRow(job, DateTimeOffset.Now).Name, result.Error, RemoteText.ProviderName(job)));
        }

        if (problems.Count > 0) await ShowMessageAsync(RemoteText.DownloadAllFiles, string.Join(Environment.NewLine, problems));
        else if (taken > 0) ShowPassingStatus(RemoteText.AllFilesTaken(taken));
    }

    /// <summary>
    /// The provider's files for a row that asks. Before it is done, the choice can be made early, or left as all of
    /// them, and the row goes on as it was. What was found of an earlier choice starts ticked. False when nothing was
    /// answered: Cancel, or a dialog that couldn't open.
    /// </summary>
    private async Task<bool> SelectRemoteFilesAsync(RemoteJobDto job)
    {
        var provider = RemoteText.ProviderName(job);
        var early = job.State != "awaitingSelection";
        var answer = await RemoteFileSelectionDialog.AskAsync(
            Content.XamlRoot,
            RootGrid.ActualTheme,
            $"Choose files: {RemoteText.JobRow(job, DateTimeOffset.Now).Name}",
            provider,
            async token =>
            {
                var files = await _remote.GetFilesAsync(job.Id, token);
                return files.Ok
                    ? new RemoteFilesToShow(files.Files, files.Pending, Chosen: early ? null : job.SelectedFileIds)
                    : new RemoteFilesToShow([], Error: files.Error ?? string.Empty);
            },
            laterText: early ? RemoteText.DownloadAllWhenReady : null,
            laterWords: early ? "You can download all of them when it does." : null);
        var choice = answer.Choice;
        if (choice is null && !answer.Later) return false;

        var all = answer.Later || choice!.All;
        var result = await _remote.SelectAsync(new RemoteSelectRequest { JobId = job.Id, All = all, FileIds = all ? null : choice!.FileIds.ToList() });
        if (!result.Ok) await ShowMessageAsync("Choose files", RemoteText.Failure(result.Error, null, provider));
        else if (early) ShowPassingStatus(RemoteText.EarlyChoice(all));
        return true;
    }

    /// <summary>
    /// Rows nothing has been sent for yet, such as copied links, sent together. Enter or the menu is the request, so
    /// nothing asks again, and a file choice may open by itself as it would after an add: when one of them can ask.
    /// </summary>
    private async Task ApproveRemoteAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        var released = new List<RemoteJobDto>();
        var problems = new List<string>();
        foreach (var job in jobs.Where(job => RemoteRules.Actions(job).Approve))
        {
            var result = await _remote.RetryAsync(new RemoteRetryRequest { JobId = job.Id, ApproveUncached = true });
            if (!result.Ok) problems.Add(RemoteText.Refused(RemoteText.JobRow(job, DateTimeOffset.Now).Name, result.Error, RemoteText.ProviderName(job)));
            else if (result.Job is { } sent) released.Add(sent);
        }

        // As after an add: whatever has to be said first, then the clock for opening by itself starts.
        if (problems.Count > 0) await ShowMessageAsync(RemoteText.AddTo(jobs), string.Join(Environment.NewLine, problems));
        if (released.Count > 0) ArmAutoOpen(released);
    }

    private async Task EnterLinkPasswordAsync(RemoteJobDto job)
    {
        var provider = RemoteText.ProviderName(job);
        var box = new PasswordBox { Header = "Site password", PasswordRevealMode = PasswordRevealMode.Hidden };
        AutomationProperties.SetName(box, "Site password");
        // TB.S2: the user's own choice, for this item's archives only, and only while they have no password of their own.
        var forArchives = new CheckBox { Content = "Use it as the archive password too" };
        var panel = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = RemoteText.LinkPasswordHint(job), TextWrapping = TextWrapping.Wrap }, box } };
        if (!job.HasArchivePassword) panel.Children.Add(forArchives);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Enter the link's password",
            Content = panel,
            PrimaryButtonText = "Retry with this password",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        // Null when another dialog is open over this window (UI.B8); the check below treats that as cancel.
        var answer = await DialogGuard.ShowAsync(dialog);

        // Out of the box before anything is sent, whatever the answer turns out to be.
        var password = box.Password;
        box.Password = string.Empty;
        if (answer != ContentDialogResult.Primary || password.Length == 0) return;
        var result = await _remote.RetryAsync(new RemoteRetryRequest { JobId = job.Id, LinkPassword = password, LinkPasswordForArchives = forArchives.IsChecked == true && !job.HasArchivePassword });
        password = string.Empty;
        if (!result.Ok) await ShowMessageAsync("Retry", RemoteText.Failure(result.Error, null, provider));
    }

    private async Task RetryRemoteAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        var problems = new List<string>();
        foreach (var job in jobs.Where(job => RemoteRules.Actions(job).Retry))
        {
            var result = await _remote.RetryAsync(new RemoteRetryRequest { JobId = job.Id });
            if (!result.Ok) problems.Add($"{RemoteText.JobRow(job, DateTimeOffset.Now).Name}: {RemoteText.Failure(result.Error, null, RemoteText.ProviderName(job))}");
        }

        if (problems.Count > 0) await ShowMessageAsync("Retry", string.Join(Environment.NewLine, problems));
    }

    private async Task CheckRemoteNowAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        var problems = new List<string>();
        var asked = 0;
        foreach (var job in jobs.Where(job => RemoteRules.Actions(job).CheckNow))
        {
            var result = await _remote.CheckNowAsync(job.Id);
            if (result.Ok) asked++;
            else problems.Add($"{RemoteText.JobRow(job, DateTimeOffset.Now).Name}: {RemoteText.Failure(result.Error, null, RemoteText.ProviderName(job))}");
        }

        if (problems.Count > 0) await ShowMessageAsync("Check Now", string.Join(Environment.NewLine, problems));
        else if (asked > 0) ShowPassingStatus(asked == 1 ? "Checking now…" : $"Checking {asked} items now…");
    }

    /// <summary>TB.S1: asks the provider to start these queued entries now. Each is found again by its own hash on the Host.</summary>
    private async Task StartQueuedAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        var problems = new List<string>();
        var started = 0;
        foreach (var job in jobs.Where(RemoteRules.IsInProviderQueue))
        {
            var result = await _remote.StartQueuedAsync(job.Id);
            if (result.Ok) started++;
            else problems.Add($"{RemoteText.JobRow(job, DateTimeOffset.Now).Name}: {RemoteText.Failure(result.Error, null, RemoteText.ProviderName(job))}");
        }

        if (problems.Count > 0) await ShowMessageAsync("Start Now", string.Join(Environment.NewLine, problems));
        else if (started > 0) ShowPassingStatus(started == 1 ? "Started. Checking now…" : $"Started {started} items. Checking now…");
    }

    /// <summary>TB.S1: takes these entries out of the provider's queue, and the rows off the list. Nothing else on the account is touched.</summary>
    private async Task RemoveFromProviderQueueAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        var queued = jobs.Where(RemoteRules.IsInProviderQueue).ToList();
        if (queued.Count == 0) return;
        var provider = RemoteText.ProviderName(queued[0]);
        var what = queued.Count == 1 ? $"'{RemoteText.JobRow(queued[0], DateTimeOffset.Now).Name}'" : $"these {queued.Count} items";
        if (!await ConfirmAsync($"Remove from {provider}'s queue?",
                $"Take {what} out of your {provider} queue and off this list? {provider} hasn't started them, so nothing else is deleted.",
                "Remove from queue")) return;

        var problems = new List<string>();
        foreach (var job in queued)
        {
            var result = await _remote.DeleteRemoteAsync(job.Id, confirmed: true);
            if (!result.Ok) problems.Add($"{RemoteText.JobRow(job, DateTimeOffset.Now).Name}: {RemoteText.Failure(result.Error, null, provider)}");
        }

        if (problems.Count > 0) await ShowMessageAsync("Remove from queue", string.Join(Environment.NewLine, problems));
    }

    private async Task RemoveRemoteAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        if (jobs.Count == 0 || !await ConfirmAsync("Remove from list?", RemoteText.RemoveQuestion(jobs), "Remove from list")) return;
        await RemoveRemoteConfirmedAsync(jobs);
    }

    /// <summary>Del, File > Remove and the toolbar's Delete when no local row is selected. Rows it can't remove are named, not skipped in silence.</summary>
    private async Task RemoveSideRowsAsync()
    {
        var jobs = SelectedRemoteJobs;
        var archiveRows = SelectedArchiveRows.Count;
        var sets = archiveRows > 0 ? CurrentArchivePlan().Remove : [];
        var left = ArchiveText.LeftExtracting(archiveRows - sets.Count);

        if (jobs.Count > 0 && sets.Count > 0)
        {
            if (await ConfirmAsync("Remove from list?", $"{RemoteText.RemoveQuestion(jobs)} {ArchiveText.RemoveQuestion(sets)}", "Remove from list"))
            {
                await RemoveRemoteConfirmedAsync(jobs);
                await RemoveArchiveRecordsConfirmedAsync(sets);
            }
        }
        else if (jobs.Count > 0)
        {
            await RemoveRemoteAsync(jobs);
        }
        else
        {
            await RemoveArchiveRecordsAsync(sets);
        }

        if (left is not null) ShowPassingStatus(left);
    }

    private async Task RemoveRemoteConfirmedAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        var problems = new List<string>();
        foreach (var job in jobs)
        {
            var result = await _remote.RemoveAsync(job.Id);
            if (!result.Ok) problems.Add($"{RemoteText.JobRow(job, DateTimeOffset.Now).Name}: {RemoteText.Failure(result.Error, null, RemoteText.ProviderName(job))}");
        }

        if (problems.Count > 0) await ShowMessageAsync("Remove from list", string.Join(Environment.NewLine, problems));
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        // Refused while another dialog is open, which counts as Cancel. DialogGuard never throws.
        return await DialogGuard.ConfirmAsync(dialog);
    }
}
