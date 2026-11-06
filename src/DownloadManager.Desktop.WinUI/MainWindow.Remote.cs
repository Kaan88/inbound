using System.ComponentModel;
using System.Runtime.InteropServices;
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
    private MenuFlyoutItem? _menuRemoteApprove;
    private MenuFlyoutItem? _menuRemoteLinkPassword;
    private MenuFlyoutItem? _menuRemoteRetry;
    private MenuFlyoutItem? _menuRemoteCheckNow;
    private MenuFlyoutItem? _menuRemoteRemove;
    private MenuFlyoutSeparator? _remoteContextSeparator;
    private string? _pendingRemoteCategory;
    private string _remoteTasksOffers = string.Empty;

    private void InitializeRemoteUi()
    {
        var state = _remote.State;
        state.Jobs.CollectionChanged += (_, _) => SyncRemoteRows();
        state.Accounts.CollectionChanged += (_, _) => SyncRemoteEntryPoints();
        state.PropertyChanged += OnRemoteStateChanged;
        SyncRemoteEntryPoints();
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
                DownloadedLocationOf,
                LocalStatusOf,
                OpenPackageWindow,
                RememberFolder));
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

    /// <summary>A downloaded file to show in its folder, or the folder given when there is none yet. Null when neither is on disk.</summary>
    private string? DownloadedLocationOf(RemoteJobDto job)
    {
        var files = LocalFilesOf(job);
        var done = files.Select(file => file.FinalFilePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (done is not null) return done;
        var folder = files.Select(file => file.Destination).Append(job.Destination)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
        return folder;
    }

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
        _menuRemoteSelectFiles = CreateContextItem("Select Files…", async (_, _) => { if (SelectedRemoteJobs is [var job]) await SelectRemoteFilesAsync(job); });
        _menuRemoteApprove = CreateContextItem("Approve…", async (_, _) => { if (SelectedRemoteJobs is [var job]) await ApproveRemoteAsync(job); });
        _menuRemoteLinkPassword = CreateContextItem("Enter Link Password…", async (_, _) => { if (SelectedRemoteJobs is [var job]) await EnterLinkPasswordAsync(job); });
        _menuRemoteRetry = CreateContextItem("Retry", async (_, _) => await RetryRemoteAsync(SelectedRemoteJobs));
        _menuRemoteCheckNow = CreateContextItem("Check Now", async (_, _) => await CheckRemoteNowAsync(SelectedRemoteJobs));
        _menuRemoteCheckNow.KeyboardAcceleratorTextOverride = "F5";
        _menuRemoteRemove = CreateContextItem("Remove from List…", async (_, _) => await RemoveRemoteAsync(SelectedRemoteJobs));
        _menuRemoteRemove.KeyboardAcceleratorTextOverride = "Del";
        _remoteContextSeparator = new MenuFlyoutSeparator();
        MenuFlyoutItemBase[] items = [_menuRemoteSelectFiles, _menuRemoteApprove, _menuRemoteLinkPassword, _menuRemoteRetry, _menuRemoteCheckNow, _menuRemoteRemove, _remoteContextSeparator];
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
        if (_menuRemoteSelectFiles is not null) _menuRemoteSelectFiles.Visibility = Show(actions is [{ SelectFiles: true }]);
        if (_menuRemoteApprove is not null) _menuRemoteApprove.Visibility = Show(actions is [{ Approve: true }]);
        if (_menuRemoteLinkPassword is not null) _menuRemoteLinkPassword.Visibility = Show(actions is [{ EnterLinkPassword: true }]);
        if (_menuRemoteRetry is not null) _menuRemoteRetry.Visibility = Show(actions.Any(action => action.Retry));
        if (_menuRemoteCheckNow is not null) _menuRemoteCheckNow.Visibility = Show(actions.Any(action => action.CheckNow));
        if (_menuRemoteRemove is not null) _menuRemoteRemove.Visibility = Show(jobs.Count > 0);
        if (_remoteContextSeparator is not null) _remoteContextSeparator.Visibility = Show(jobs.Count > 0 && FirstLocalRow is not null);
    }

    /// <summary>Runs last. A selection of remote rows alone has nothing for the local items to act on.</summary>
    private void HideLocalContextItemsIfRemoteOnly()
    {
        if (FirstLocalRow is not null || (SelectedRemoteRows.Count == 0 && SelectedArchiveRows.Count == 0)) return;
        foreach (var item in _localContextItems) item.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The list's keys for remote rows. Enter does the next thing a row is waiting for, F5 checks, Delete
    /// takes it off the list. Before the list sees them: it keeps Enter for itself.
    /// </summary>
    private void JobsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
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
                _ = RemoveRemoteAsync(jobs);
                break;
            case Windows.System.VirtualKey.Enter when FirstLocalRow is null && jobs.Count == 1:
                e.Handled = true;
                _ = RunRemotePrimaryAsync(jobs[0]);
                break;
        }
    }

    private Task RunRemotePrimaryAsync(RemoteJobDto job) => RemoteRules.Actions(job).Primary switch
    {
        RemoteRowAction.SelectFiles => SelectRemoteFilesAsync(job),
        RemoteRowAction.Approve => ApproveRemoteAsync(job),
        RemoteRowAction.EnterLinkPassword => EnterLinkPasswordAsync(job),
        RemoteRowAction.Retry => RetryRemoteAsync([job]),
        RemoteRowAction.CheckNow => CheckRemoteNowAsync([job]),
        _ => Task.CompletedTask,
    };

    private async Task SelectRemoteFilesAsync(RemoteJobDto job)
    {
        var provider = RemoteText.ProviderName(job);
        var choice = await RemoteFileSelectionDialog.ShowAsync(
            Content.XamlRoot,
            RootGrid.ActualTheme,
            $"Choose files: {RemoteText.JobRow(job, DateTimeOffset.Now).Name}",
            provider,
            async token =>
            {
                var files = await _remote.GetFilesAsync(job.Id, token);
                return files.Ok
                    ? new RemoteFilesToShow(files.Files, files.Pending)
                    : new RemoteFilesToShow([], Error: files.Error ?? string.Empty);
            });
        if (choice is null) return;
        var result = await _remote.SelectAsync(new RemoteSelectRequest { JobId = job.Id, All = choice.All, FileIds = choice.All ? null : choice.FileIds.ToList() });
        if (!result.Ok) await ShowMessageAsync("Choose files", RemoteText.Failure(result.Error, null, provider));
    }

    private async Task ApproveRemoteAsync(RemoteJobDto job)
    {
        var provider = RemoteText.ProviderName(job);
        if (!await ConfirmAsync($"Start this on {provider}?", RemoteText.ApproveQuestion(job), "Approve")) return;
        var result = await _remote.RetryAsync(new RemoteRetryRequest { JobId = job.Id, ApproveUncached = true });
        if (!result.Ok) await ShowMessageAsync("Approve", RemoteText.Failure(result.Error, null, provider));
    }

    private async Task EnterLinkPasswordAsync(RemoteJobDto job)
    {
        var provider = RemoteText.ProviderName(job);
        var box = new PasswordBox { Header = "Link password", PasswordRevealMode = PasswordRevealMode.Hidden };
        AutomationProperties.SetName(box, "Link password");
        var panel = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = RemoteText.LinkPasswordHint(job), TextWrapping = TextWrapping.Wrap }, box } };
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
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return;
        }

        // Out of the box before anything is sent, whatever the answer turns out to be.
        var password = box.Password;
        box.Password = string.Empty;
        if (answer != ContentDialogResult.Primary || password.Length == 0) return;
        var result = await _remote.RetryAsync(new RemoteRetryRequest { JobId = job.Id, LinkPassword = password });
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

        if (problems.Count > 0) await ShowMessageAsync("Retry", string.Join(Environment.NewLine, problems.Take(5)));
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

        if (problems.Count > 0) await ShowMessageAsync("Check Now", string.Join(Environment.NewLine, problems.Take(5)));
        else if (asked > 0) ShowPassingStatus(asked == 1 ? "Checking now…" : $"Checking {asked} items now…");
    }

    private async Task RemoveRemoteAsync(IReadOnlyList<RemoteJobDto> jobs)
    {
        if (jobs.Count == 0 || !await ConfirmAsync("Remove from list?", RemoteText.RemoveQuestion(jobs), "Remove from list")) return;
        var problems = new List<string>();
        foreach (var job in jobs)
        {
            var result = await _remote.RemoveAsync(job.Id);
            if (!result.Ok) problems.Add($"{RemoteText.JobRow(job, DateTimeOffset.Now).Name}: {RemoteText.Failure(result.Error, null, RemoteText.ProviderName(job))}");
        }

        if (problems.Count > 0) await ShowMessageAsync("Remove from list", string.Join(Environment.NewLine, problems.Take(5)));
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
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return false;
        }
    }
}
