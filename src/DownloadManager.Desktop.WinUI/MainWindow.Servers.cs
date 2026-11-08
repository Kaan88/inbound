using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The window showing another server (design D9, section 5): the Servers list (File > Servers), the switcher at the
/// left of the status bar, and what changes while a server is shown. This PC's Host stays connected throughout
/// (<see cref="_local"/>): its settings drive the theme and the window's own choices, its updates are the ones offered,
/// and its Exit quits the app.
/// </summary>
public sealed partial class MainWindow
{
    private readonly HostConnection _local;
    private readonly ServerStore _servers = ServerStore.ForThisUser();
    private SettingsDto? _localSettings;
    private HostStatusDto? _localStatus;
    private int _switches;

    /// <summary>This PC's settings, whichever server is shown.</summary>
    private SettingsDto? LocalSettings => _connection.IsRemote ? _localSettings : _settings ?? _localSettings;

    private void KeepLocalSettings(SettingsDto settings)
    {
        _localSettings = settings;
        if (!_connection.IsRemote) _settings = settings;
    }

    /// <summary>Every connection's events reach the window; only the shown one's change the list.</summary>
    private void WireConnection(HostConnection connection)
    {
        connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection == _local && _connection != _local) ApplyLocalSnapshot(snapshot);
            if (connection == _connection) ApplySnapshot(snapshot);
        });
        connection.JobChanged += (job, type) => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection == _connection) ApplyJobEvent(job, type);
        });
        connection.GroupsChanged += groups => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection != _connection) return;
            _groups = groups;
            RebuildVisible();
        });
        connection.ConnectionChanged += (connected, error) => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection == _connection) ApplyConnection(connected, error);
        });
        // Only this PC's Host raises it (HostConnection never does for a server).
        connection.HostExiting += () => DispatcherQueue.TryEnqueue(() =>
        {
            UiTrace.Write("Host announced application exit");
            Application.Current.Exit();
        });
    }

    /// <summary>This PC's snapshot while a server is shown: only what is this PC's is taken from it.</summary>
    private void ApplyLocalSnapshot(SnapshotDto snapshot)
    {
        _localSettings = snapshot.Settings;
        _localStatus = snapshot.Status;
        FollowThemeSetting(snapshot.Settings);
    }

    private void InitializeServers()
    {
        ServerSwitcherMenu.Opening += (_, _) => FillServerMenu();
        AppWindow.Changed += (sender, args) =>
        {
            if (!args.DidVisibilityChange) return;
            // While the window is hidden a server is asked for nothing (design D9, section 5).
            _connection.SetEventsWanted(sender.IsVisible);
            _ = _remote.SetEventsWantedAsync(sender.IsVisible);
        };

        if (_servers.LastShown is { } last)
        {
            if (_servers.Target(last) is { } target) _ = ShowServerAsync(target);
            else ShowPassingStatus("The server shown last can't be opened on this Windows account. Pair it again in File > Servers.");
        }
    }

    private void FillServerMenu()
    {
        ServerSwitcherMenu.Items.Clear();
        var shown = _connection.Target;
        ServerSwitcherMenu.Items.Add(ServerItem("This PC", shown.IsLocal, () => _ = ShowServerAsync(ServerTarget.Local)));
        foreach (var server in _servers.Load())
        {
            var id = server.Id;
            ServerSwitcherMenu.Items.Add(ServerItem(server.Name, shown.Id == id, () => _ = ShowSavedServerAsync(id)));
        }

        ServerSwitcherMenu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Servers…" };
        manage.Click += (_, _) => _ = ShowServersDialogAsync();
        ServerSwitcherMenu.Items.Add(manage);

        static MenuFlyoutItem ServerItem(string text, bool current, Action choose)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = current };
            item.Click += (_, _) => choose();
            return item;
        }
    }

    private async Task ShowSavedServerAsync(string id)
    {
        if (_servers.Target(id) is { } target)
        {
            await ShowServerAsync(target);
            return;
        }

        await ShowMessageAsync("Servers", "This server's pairing can't be opened on this Windows account. Select it in File > Servers and pair it again.");
    }

    /// <summary>
    /// Shows another Host's downloads. The list empties and fills again from that Host; the windows tied to the one shown
    /// before (Options, Scheduler, Statistics) close.
    /// </summary>
    private async Task ShowServerAsync(ServerTarget target)
    {
        var old = _connection;
        if (old.Target.Id == target.Id && (target.IsLocal || old.Target.Token == target.Token)) return;
        var switchNumber = ++_switches;
        _settingsWindow?.Close();
        _schedulerWindow?.Close();
        _statisticsWindow?.Close();

        ShownServer.Target = target;
        if (target.IsLocal)
        {
            _connection = _local;
        }
        else
        {
            var connection = new HostConnection(target);
            WireConnection(connection);
            connection.SetEventsWanted(AppWindow.IsVisible);
            _connection = connection;
            connection.Start();
        }

        if (old != _local) _ = DisposeQuietlyAsync(old);

        _jobsById.Clear();
        _packageRows.Clear();
        _groups = new();
        _status = target.IsLocal ? _localStatus : null;
        _settings = target.IsLocal ? _localSettings : null;
        _graphConnected = _connection.IsConnected;
        _graphSnapshotReady = false;
        ServerSwitcher.Content = target.Name;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(ServerSwitcher, $"Showing {target.Name}");
        ConnectionText.Text = target.IsLocal ? "Connecting…" : $"Connecting to {target.Name}…";
        RebuildVisible();
        UpdateStatusText();
        UpdateButtons();
        try
        {
            _servers.SetLastShown(target.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiTrace.Write($"Saving the shown server failed: {ex.GetType().Name}");
        }

        await _remote.RetargetAsync(target);
        if (switchNumber != _switches || !target.IsLocal || !_local.IsConnected) return;
        try
        {
            ApplyConnection(true, null);
            ApplySnapshot(await _local.RefreshAsync());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException)
        {
            UiTrace.Write($"Reading this PC's list again failed: {ex.GetType().Name}");
        }
    }

    private static async Task DisposeQuietlyAsync(HostConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Closing a server's connection failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Says why a file can't be opened from this PC, when the window shows another server. True when it said so.</summary>
    private bool SayIfOnAnotherServer()
    {
        if (RemoteDataRules.MayUseFiles(_connection.IsRemote)) return false;
        ShowPassingStatus($"This download is on {_connection.Target.Name}, so its file can't be opened from this PC.");
        return true;
    }

    /// <summary>A finished download on another server, while the window is open: this PC's choice of a notification.</summary>
    private void NoteRemoteCompletion(JobDto job)
    {
        if (!_connection.IsRemote || LocalSettings?.NotifyOnCompletion != true) return;
        ShowPassingStatus($"Finished on {_connection.Target.Name}: {job.FileName}");
    }

    private void MenuServers_Click(object sender, RoutedEventArgs e) => _ = ShowServersDialogAsync();

    // ---- File > Servers ---------------------------------------------------------------------------------------------

    private async Task ShowServersDialogAsync()
    {
        while (true)
        {
            var servers = _servers.Load();
            string? action = null;
            SavedServer? chosen = null;
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MinHeight = 160,
                MaxHeight = 320,
                ItemsSource = servers.Select(server => $"{server.Name}  ·  {server.Address}").ToList(),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Saved servers");
            var add = new Button { Content = "Add…" };
            var edit = new Button { Content = "Edit…", IsEnabled = false };
            var remove = new Button { Content = "Remove…", IsEnabled = false };
            var show = new Button { Content = "Show", IsEnabled = false };
            list.SelectionChanged += (_, _) => edit.IsEnabled = remove.IsEnabled = show.IsEnabled = list.SelectedIndex >= 0;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(add);
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);
            buttons.Children.Add(show);
            var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
            panel.Children.Add(new TextBlock
            {
                Text = "Another computer running Inbound (the Windows app with its web UI turned on, or the server) can be shown in this window. Choose which one with the button at the left of the status bar.",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(servers.Count == 0 ? new TextBlock { Text = "No servers yet.", Opacity = 0.72 } : list);
            panel.Children.Add(buttons);
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Servers",
                Content = panel,
                CloseButtonText = "Close",
            };
            void Pick(string what)
            {
                action = what;
                chosen = list.SelectedIndex >= 0 && list.SelectedIndex < servers.Count ? servers[list.SelectedIndex] : null;
                dialog.Hide();
            }

            add.Click += (_, _) => Pick("add");
            edit.Click += (_, _) => Pick("edit");
            remove.Click += (_, _) => Pick("remove");
            show.Click += (_, _) => Pick("show");
            await dialog.ShowAsync();

            switch (action)
            {
                case "add":
                    await PairServerAsync(null);
                    continue;
                case "edit" when chosen is not null:
                    await PairServerAsync(chosen);
                    continue;
                case "remove" when chosen is not null:
                    await RemoveServerAsync(chosen);
                    continue;
                case "show" when chosen is not null:
                    await ShowSavedServerAsync(chosen.Id);
                    return;
                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Adds a server, or edits a saved one. The server's password is asked for once and sent only in the pairing request;
    /// only the token that comes back is kept. Editing keeps the pairing unless a password is typed (to pair again, or
    /// for another address).
    /// </summary>
    private async Task PairServerAsync(SavedServer? existing)
    {
        var name = new TextBox { Header = "Name", Text = existing?.Name ?? string.Empty, PlaceholderText = "Home server", MaxLength = 80 };
        var address = new TextBox { Header = "Address", Text = existing?.Address ?? string.Empty, PlaceholderText = "homeserver:8080 or http://192.168.1.5:8080" };
        var password = new PasswordBox
        {
            Header = existing is null ? "The server's password" : "The server's password (only to pair again)",
            PlaceholderText = existing is null ? null : "Leave empty to keep the pairing",
        };
        var plainHttp = new CheckBox
        {
            Content = "Allow this server's sign-in to cross the network unencrypted (http://)",
            IsChecked = existing?.PlainHttpConfirmed == true,
            Visibility = Visibility.Collapsed,
        };
        var plainHint = SettingsWindow.Hint("Anyone on the same network could read what this app and the server send each other, including the sign-in that lets this PC control it. Use this only on a network you trust, or use an https:// address.");
        plainHint.Visibility = Visibility.Collapsed;
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void FollowAddress()
        {
            var needs = ServerAddress.TryNormalize(address.Text, out var normal, out _) && ServerAddress.NeedsPlainHttpConfirmation(normal);
            plainHttp.Visibility = plainHint.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        }

        address.TextChanged += (_, _) => FollowAddress();
        FollowAddress();
        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        panel.Children.Add(name);
        panel.Children.Add(address);
        panel.Children.Add(password);
        panel.Children.Add(SettingsWindow.Hint("The password is used once, to pair this PC with the server, and isn't kept. The server lists this PC under Settings, Sign-in and browsers, where the pairing can be removed."));
        panel.Children.Add(plainHttp);
        panel.Children.Add(plainHint);
        panel.Children.Add(message);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = existing is null ? "Add a server" : $"Edit {existing.Name}",
            Content = panel,
            PrimaryButtonText = existing is null ? "Pair" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var paired = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var error = await SaveServerAsync(existing, name.Text, address.Text, password.Password, plainHttp.IsChecked == true);
                password.Password = string.Empty;
                if (error is not null)
                {
                    message.Text = error;
                    message.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
                else paired = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        password.Password = string.Empty;
        if (!paired || existing is null || _connection.Target.Id != existing.Id) return;
        // The shown server changed its name, its http choice or its pairing: connect with what is saved now.
        if (_servers.Target(existing.Id) is { } updated)
        {
            ShownServer.Target = updated;
            ServerSwitcher.Content = updated.Name;
            _connection.Retarget(updated);
            await _remote.RetargetAsync(updated);
        }
    }

    /// <summary>The error in words, or null once saved.</summary>
    private async Task<string?> SaveServerAsync(SavedServer? existing, string name, string typedAddress, string password, bool plainHttpConfirmed)
    {
        if (!ServerAddress.TryNormalize(typedAddress, out var address, out var addressError)) return addressError;
        if (!ServerAddress.NeedsPlainHttpConfirmation(address)) plainHttpConfirmed = false;
        var sameAddress = existing is not null && string.Equals(existing.Address, address.ToString(), StringComparison.OrdinalIgnoreCase);
        if (existing is not null && password.Length == 0)
        {
            if (!sameAddress) return "Type the server's password to pair this PC with the new address.";
            _servers.Update(existing.Id, name, plainHttpConfirmed);
            return null;
        }

        if (password.Length == 0) return "Type the server's password.";
        if (!ServerAddress.MaySend(address, plainHttpConfirmed))
            return "This address starts with http://. Tick the box to allow its sign-in unencrypted, or use an https:// address.";
        var result = await RemoteServerHttp.PairAsync(address, password, $"Inbound on {Environment.MachineName}", plainHttpConfirmed);
        if (!result.Ok) return result.Error;
        try
        {
            if (existing is null) _servers.Add(name, address, result.Token!, result.TokenId, plainHttpConfirmed, result.HostKind);
            else
            {
                // The old token is the server's to forget; a new one replaces it here.
                if (_servers.Target(existing.Id) is { } old) _ = RemoteServerHttp.UnpairAsync(old);
                _servers.Repair(existing.Id, address, result.Token!, result.TokenId, plainHttpConfirmed, result.HostKind);
                _servers.Update(existing.Id, name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return "The pairing worked, but this PC couldn't save it. Check that the app's data folder can be written to.";
        }

        return null;
    }

    private async Task RemoveServerAsync(SavedServer server)
    {
        if (!await ConfirmAsync($"Remove {server.Name}?",
                "This PC forgets the server and asks the server to forget this PC's pairing. Its downloads stay on the server.", "Remove"))
            return;

        var told = _servers.Target(server.Id) is { } target && await RemoteServerHttp.UnpairAsync(target);
        if (_connection.Target.Id == server.Id) await ShowServerAsync(ServerTarget.Local);
        try
        {
            _servers.Remove(server.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Servers", "The server couldn't be removed from this PC's list. Check that the app's data folder can be written to.");
            return;
        }

        if (!told)
            await ShowMessageAsync("Servers",
                $"{server.Name} couldn't be reached, so it still lists this PC as paired. Remove it there in the web UI (Settings, Sign-in and browsers).");
    }

    // ---- Options while a server is shown ------------------------------------------------------------------------------

    private Task OpenOptionsAsync() => _connection.IsRemote ? ShowServerOptionsAsync() : ShowSettingsDialogAsync();

    /// <summary>
    /// Options while a server is shown (design D9, section 5): this PC's own choices (theme, start at sign-in, clipboard,
    /// browser, updates, what a finished download does here) are read from and saved to this PC's Host; the server's
    /// pages show only what its hello allows, and a save there never changes those choices on the server.
    /// </summary>
    private async Task ShowServerOptionsAsync()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var server = _connection;
        var serverName = server.Target.Name;
        SnapshotDto local, remote;
        try
        {
            local = await _local.RefreshAsync();
            remote = await server.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException)
        {
            await ShowMessageAsync("Options", ex is InvalidOperationException
                ? $"{serverName} isn't connected yet."
                : $"The options couldn't be read: {(ex is RemoteServerException ? ex.Message : "the connection was lost.")}");
            return;
        }

        var pages = RemoteSettingsRules.PagesFor(server.Hello?.Capabilities, server.Hello?.HostKind);
        var mine = local.Settings;
        var theirs = remote.Settings;
        var window = new SettingsWindow { Title = $"Options — {serverName} — Inbound Download Manager" };
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;

        // This PC.
        string[] themeValues = [ThemeChoices.System, ThemeChoices.Light, ThemeChoices.Dark];
        var themeBox = new ComboBox
        {
            Header = "Theme",
            MinWidth = 200,
            ItemsSource = new[] { "Same as Windows", "Light", "Dark" },
            SelectedIndex = Math.Max(0, Array.IndexOf(themeValues, ThemeChoices.InUse(_uiState.DarkMode, mine.Theme))),
        };
        var launch = new CheckBox { Content = "Start Inbound when I sign in to Windows", IsChecked = mine.LaunchAtStartup };
        var closeCompletely = new CheckBox { Content = "Closing the window ends it, to use less memory and power while closed", IsChecked = mine.CloseWindowCompletely };
        var integration = new CheckBox { Content = "Capture downloads from your browser", IsChecked = mine.BrowserIntegrationEnabled };
        var clipboard = new CheckBox { Content = "Watch the clipboard for download links", IsChecked = mine.ClipboardMonitoring };
        var completionDialog = new CheckBox { Content = "Show the download-complete window", IsChecked = mine.ShowCompletionDialog };
        var notify = new CheckBox { Content = "Show a notification", IsChecked = mine.NotifyOnCompletion };
        var autoUpdate = new CheckBox { Content = "Check for and install updates automatically while no downloads are running", IsChecked = mine.AutoCheckUpdates };
        var updateInterval = SettingsWindow.Number("Check every (hours)", mine.UpdateCheckIntervalHours, 1, 168);
        window.AddPage("This PC", "",
            SettingsWindow.Hint($"This PC's own choices. They are saved on this PC, whichever server the window shows, and never change {serverName}."),
            SettingsWindow.Section("Startup"), launch,
            SettingsWindow.Section("Window"), themeBox, closeCompletely,
            SettingsWindow.Section("Capturing downloads"), integration, clipboard,
            SettingsWindow.Section($"When a download on {serverName} finishes, while this window is open"), completionDialog, notify,
            SettingsWindow.Section("Updates"), autoUpdate, updateInterval);
        const int ThisPcPage = 0;

        // The server.
        var serverControls = new List<UIElement>
        {
            SettingsWindow.Hint($"{serverName}'s own options, saved on {serverName}. Anything not shown here is changed in its web UI."),
        };
        TextBox? directory = null;
        CheckBox? useCategoryFolders = null;
        if (pages.Folders)
        {
            var (row, box) = window.FolderField("Default download folder", theirs.DefaultDownloadDirectory);
            directory = box;
            useCategoryFolders = new CheckBox { Content = "Save downloads to a folder for their category", IsChecked = theirs.UseCategoryFolders };
            serverControls.Add(SettingsWindow.Section("Save to"));
            serverControls.Add(row);
            serverControls.Add(useCategoryFolders);
        }

        var maxConcurrent = SettingsWindow.Number("Maximum simultaneous downloads", theirs.MaxConcurrentDownloads, 1, 16);
        var connections = SettingsWindow.Number("Connections per download", theirs.DefaultConnections, 1, 32);
        var speedLimit = SettingsWindow.Number("Total speed limit, all downloads (KB/s)",
            theirs.GlobalSpeedLimitBytesPerSecond is > 0 ? theirs.GlobalSpeedLimitBytesPerSecond.Value / 1024 : null, 1, 10_000_000, "Unlimited");
        speedLimit.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
        serverControls.Add(SettingsWindow.Section("Connection"));
        serverControls.Add(maxConcurrent);
        serverControls.Add(connections);
        serverControls.Add(speedLimit);
        serverControls.Add(SettingsWindow.Hint("Leave the speed limit empty for unlimited."));
        CheckBox? autoResume = null;
        if (pages.AutoResume)
        {
            autoResume = new CheckBox { Content = $"Resume unfinished downloads when {serverName} starts", IsChecked = theirs.AutoResumeUnfinishedOnStartup };
            serverControls.Add(autoResume);
        }

        TextBox? postDownload = null;
        if (pages.ProgramSettings)
        {
            postDownload = new TextBox { Header = $"Run this program on {serverName} after each download (optional)", Text = theirs.PostDownloadCommand ?? string.Empty };
            serverControls.Add(SettingsWindow.Section("When a download finishes"));
            serverControls.Add(postDownload);
        }

        window.AddPage(serverName, "", serverControls.ToArray());
        const int ServerPage = 1;
        var nextPage = ServerPage + 1;

        // The server's provider and extraction pages, through the provider client, which follows the shown server.
        var accountPages = new List<(ProviderAccountPage Page, int Index)>();
        if (!_remote.State.IsReady) await _remote.WaitUntilReadyAsync(TimeSpan.FromSeconds(1.5));
        if (_remote.State.RemoteSupported)
        {
            foreach (var account in _remote.State.Accounts.Where(account => account.Capabilities.Contains("account", StringComparer.OrdinalIgnoreCase)))
            {
                var page = new ProviderAccountPage(_remote, account);
                page.KeyBoxEmptied += window.ClearMessage;
                window.AddPage(page.Title, "", page.Controls);
                accountPages.Add((page, nextPage++));
            }
        }

        ExtractionOptionsPage? extraction = null;
        var extractionPageIndex = nextPage;
        if (_remote.State.ArchivesSupported)
        {
            extraction = new ExtractionOptionsPage(_remote, window);
            window.AddPage("Extraction", "", extraction.Controls);
        }

        window.Closed += (_, _) =>
        {
            foreach (var (page, _) in accountPages) page.Close();
            extraction?.Close();
        };

        static int? WholeNumber(NumberBox box) => double.IsNaN(box.Value) ? null : (int)Math.Round(box.Value);
        window.Save = async () =>
        {
            foreach (var (page, index) in accountPages)
            {
                if (page.HasUnsavedKey) return ($"The {page.ProviderName} API key you typed isn't saved. Select Save key, or empty the box.", index);
            }

            if (extraction?.Validate() is { } extractionProblem) return (extractionProblem, extractionPageIndex);
            if (WholeNumber(updateInterval) is not { } checkInterval || checkInterval is < 1 or > 168) return ("Update interval must be 1-168 hours.", ThisPcPage);
            if (WholeNumber(maxConcurrent) is not { } max || max is < 1 or > 16) return ("Maximum simultaneous downloads must be between 1 and 16.", ServerPage);
            if (WholeNumber(connections) is not { } connectionsValue || connectionsValue is < 1 or > 32) return ("Connections per download must be between 1 and 32.", ServerPage);
            long? limit = WholeNumber(speedLimit) is { } kb ? kb * 1024L : null;
            if (limit is <= 0) return ("Speed limit must be a positive number of KB/s.", ServerPage);

            // This PC: from its latest settings, so nothing changed elsewhere meanwhile is undone.
            var localLatest = CloneSettings((await _local.RefreshAsync()).Settings);
            localLatest.Theme = themeValues[Math.Max(0, themeBox.SelectedIndex)];
            localLatest.LaunchAtStartup = launch.IsChecked == true;
            localLatest.CloseWindowCompletely = closeCompletely.IsChecked == true;
            localLatest.BrowserIntegrationEnabled = integration.IsChecked == true;
            localLatest.ClipboardMonitoring = clipboard.IsChecked == true;
            localLatest.ShowCompletionDialog = completionDialog.IsChecked == true;
            localLatest.NotifyOnCompletion = notify.IsChecked == true;
            localLatest.AutoCheckUpdates = autoUpdate.IsChecked == true;
            localLatest.UpdateCheckIntervalHours = checkInterval;
            var localResult = (await _local.UpdateSettingsAsync(localLatest)).GetPayload<SettingsResult>();
            if (!localResult.Ok) return (localResult.Error ?? "This PC's Host didn't take the settings.", ThisPcPage);
            KeepLocalSettings(localResult.Settings);
            if (_uiState.DarkMode is not null)
            {
                _uiState.DarkMode = null;
                SaveUiState();
            }

            AppTheme.Use(localResult.Settings.Theme);

            // The server: only its own fields; this PC's choices go back to it as it has them.
            var serverAtSave = (await server.RefreshAsync()).Settings;
            var edited = CloneSettings(serverAtSave);
            if (directory is not null) edited.DefaultDownloadDirectory = directory.Text.Trim();
            if (useCategoryFolders is not null) edited.UseCategoryFolders = useCategoryFolders.IsChecked == true;
            edited.MaxConcurrentDownloads = max;
            edited.DefaultConnections = connectionsValue;
            edited.GlobalSpeedLimitBytesPerSecond = limit;
            if (autoResume is not null) edited.AutoResumeUnfinishedOnStartup = autoResume.IsChecked == true;
            if (postDownload is not null) edited.PostDownloadCommand = string.IsNullOrWhiteSpace(postDownload.Text) ? null : postDownload.Text.Trim();
            RemoteSettingsRules.KeepServerValues(edited, serverAtSave);
            var serverResult = (await server.UpdateSettingsAsync(edited)).GetPayload<SettingsResult>();
            if (!serverResult.Ok) return (serverResult.Error ?? $"{serverName} didn't take the settings.", ServerPage);
            if (server == _connection)
            {
                _settings = serverResult.Settings;
                UpdateLimitButtons();
            }

            var extractionSaveProblem = extraction is null ? null : await extraction.SaveAsync();
            return extractionSaveProblem is null ? (null, 0) : (extractionSaveProblem, extractionPageIndex);
        };

        window.Activate();
        await window.WaitClosedAsync();
    }
}
