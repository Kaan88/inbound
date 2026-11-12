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

    // Which of the shown server's downloads may open a completion window here.
    private readonly RemoteCompletionGate _remoteCompletions = new();

    // Downloads that just finished, waiting a moment so a package gets one window (U-WL-4, U-WR-16).
    private readonly List<Guid> _finishedBurst = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _burstTimer;

    private void QueueCompletionWindow(Guid jobId)
    {
        if (!_progressWindows.OpensCompletionWindows) return;
        _finishedBurst.Add(jobId);
        if (_burstTimer is null)
        {
            _burstTimer = DispatcherQueue.CreateTimer();
            _burstTimer.IsRepeating = false;
            _burstTimer.Interval = CompletionBurst.Gather;
            _burstTimer.Tick += (_, _) => OpenCompletionWindows();
        }

        // From the first finish, not the last: a steady stream still gets its windows.
        if (!_burstTimer.IsRunning) _burstTimer.Start();
    }

    private void OpenCompletionWindows()
    {
        var finished = _finishedBurst.ToList();
        _finishedBurst.Clear();
        if (!_progressWindows.OpensCompletionWindows) return;
        var plan = CompletionBurst.Plan(finished, id => PackageOf(id) is { } package
            ? (package.Id, CompletionBurst.Settled(PackageMembers(package).Select(member => member.State).ToList()))
            : null);
        var opens = new List<(ProgressWindowKind, Guid, ServerTarget)>();
        foreach (var window in plan)
        {
            if (_connection.IsRemote && !_remoteCompletions.MayOpenWindow(DateTimeOffset.UtcNow)) break;
            // Decided now, for the server shown now: the windows open in later turns (T11). A package of one is an
            // ordinary row on the list, so it gets an ordinary file's window.
            var only = window.IsPackage ? ProgressDetailRules.OnlyFile(_groups.FirstOrDefault(group => group.Id == window.Id), _jobsById.ContainsKey) : null;
            opens.Add(window.IsPackage && only is null
                ? (ProgressWindowKind.Package, window.Id, _connection.Target)
                : (ProgressWindowKind.Download, only ?? window.Id, _connection.Target));
        }

        OpenWindowsInTurns(opens);
    }

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
        // UI.R37: an update check on this PC or the shown server found another latest version.
        connection.LatestVersionChanged += latest => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection == _local && _localStatus is { } local) local.LatestVersion = latest;
            if (connection == _connection && _status is { } shown) shown.LatestVersion = latest;
            // This PC's latest is also what a shown server without a check of its own is held to.
            if (connection == _local || connection == _connection) UpdateStatusText();
        });
        // UI.R41, review: an extension reached this PC's Host for the first time, so the hint goes now.
        connection.ExtensionsSeenChanged += seen => DispatcherQueue.TryEnqueue(() =>
        {
            if (connection != _local) return;
            if (_localStatus is { } local) local.ExtensionsSeen = seen;
            if (connection == _connection && _status is { } shown) shown.ExtensionsSeen = seen;
            UiTrace.Write($"Extensions seen pushed: {string.Join(",", seen.Keys.Order(StringComparer.Ordinal))}");
            UpdateExtensionHint(false);
            // UI.B30: an open Get the Browser Extension dialog or Options shows it too.
            RefreshExtensionLinkPanels();
        });
        // Only this PC's Host raises it (HostConnection never does for a server).
        connection.HostExiting += () => DispatcherQueue.TryEnqueue(() =>
        {
            UiTrace.Write("Host announced application exit");
            Application.Current.Exit();
#if HAS_UNO
            // Uno's X11 head leaves the process running after Exit.
            Environment.Exit(0);
#endif
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
        ServerSwitcherMenu.Opening += (_, _) => FillServerMenu(ServerSwitcherMenu.Items);
        ToolbarServerMenu.Opening += (_, _) => FillServerMenu(ToolbarServerMenu.Items);
        ToolbarServerMenu.Opened += (_, _) => FocusServerMenuOnOpen(ToolbarServerMenu);
        ToolbarServerMenu.Closed += (_, _) => _serverMenuFromKeyboard = false;
        UpdateServerSwitchers();
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
            else ShowPassingStatus($"The server shown last can't be opened on {PlatformText.ThisAccount}. Pair it again in File > Servers.");
        }
    }

    // The shown server's connection since it was shown: up at least once, and its last words when it went down.
    private bool _shownEverConnected;
    private string? _shownConnectionError;
    private ServerBanner _banner = ServerBannerRules.For(ServerTarget.Local, false, false, false, false, null);

    /// <summary>The connection to the shown Host went up or down: the bar above the list, the list and the counts follow.</summary>
    private void NoteShownConnection(bool connected, string? error)
    {
        if (connected)
        {
            _shownEverConnected = true;
            _shownConnectionError = null;
        }
        else if (error is not null) _shownConnectionError = error;
        UpdateServerBanner();
    }

    /// <summary>
    /// The bar above the list (UI.B18, U-WR-1, U-WR-3): which server is shown, with Back to This PC always one click away;
    /// while it can't be reached, why, what to do, and a dimmed list so rows from before don't look live.
    /// </summary>
    private void UpdateServerBanner()
    {
        var banner = ServerBannerRules.For(_connection.Target, _connection.IsConnected, _shownEverConnected,
            _connection.PairingEnded, _connection.StoppedTrying, _shownConnectionError);
        _banner = banner;
        ServerBannerBar.Visibility = banner.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (ServerBannerText.Text != banner.Text)
        {
            ServerBannerText.Text = banner.Text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ServerBannerText, banner.Text.Length == 0 ? "Server shown" : banner.Text);
        }

        ServerBannerIcon.Glyph = banner.IsProblem ? "" : "";
        ServerBannerIcon.Foreground = InboundTheme.Brush(banner.IsProblem ? "InboundStatusFailedBrush" : "InboundIconInfoBrush", RootGrid);
        ServerBannerButton.Visibility = banner.ActionText is null ? Visibility.Collapsed : Visibility.Visible;
        ServerBannerButton.Content = banner.ActionText ?? string.Empty;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ServerBannerButton, banner.ActionText is null ? string.Empty : $"{banner.ActionText.TrimEnd('…')} {_connection.Target.Name}");
        ServerBannerBack.Visibility = _connection.Target.IsLocal ? Visibility.Collapsed : Visibility.Visible;
        JobsList.Opacity = banner.Stale ? 0.5 : 1;
        // Review 5:E1: rows from before the connection went say nothing of a speed or time left that isn't known now.
        if (banner.Stale)
        {
            foreach (var row in _visibleJobs.Concat(_jobsById.Values).Distinct()) row.ShowNotCurrent();
            UpdateStatusText();
        }
        UpdateEmptyText();
    }

    private void ServerBannerBack_Click(object sender, RoutedEventArgs e) => _ = ShowServerAsync(ServerTarget.Local);

    private void ServerBannerAction_Click(object sender, RoutedEventArgs e)
    {
        var target = _connection.Target;
        switch (_banner.Action)
        {
            case ServerBannerAction.Retry:
                // The saved pairing as it is now, in case it changed in another window.
                var now = target.Id is { } id ? _servers.Target(id) ?? target : target;
                _shownConnectionError = null;
                _connection.Retarget(now);
                ShowConnectionWords($"Connecting to {now.Name}…");
                UpdateServerBanner();
                break;
            case ServerBannerAction.PairAgain:
            case ServerBannerAction.EditServer:
                if (_servers.Load().FirstOrDefault(server => string.Equals(server.Id, target.Id, StringComparison.OrdinalIgnoreCase)) is { } saved)
                    _ = PairServerAsync(saved);
                else _ = ShowServersDialogAsync();
                break;
        }
    }

    private IReadOnlyList<ServerChoice> ServerChoices() => ServerSwitch.Choices(_servers.Load(), _connection.Target.Id);

    /// <summary>
    /// UI.R37: the shown server's name in the switchers' menus, with "0.6.3 (not latest)" after it when it is known to be
    /// older than the latest. Only the shown one's version is known.
    /// </summary>
    private string ServerMenuText(ServerChoice choice)
        => choice.IsCurrent && _connection.IsRemote && _graphConnected && VersionStatus.ServerNote(_status?.HostVersion, ShownServerLatest) is { } note
            ? $"{choice.Name} · {note}"
            : choice.Name;

    // UI.R37: the shown server's note the Tasks > Show Downloads Of submenu was last filled with. That submenu has no
    // Opening event of its own, so it is filled again when the note changes (snapshot, connection, latest version).
    private string? _tasksServerNote;

    private void RefreshTasksServerNote()
    {
        var note = _connection.IsRemote && _graphConnected ? VersionStatus.ServerNote(_status?.HostVersion, ShownServerLatest) : null;
        if (note == _tasksServerNote) return;
        _tasksServerNote = note;
        FillServerMenu(TasksServersItem.Items);
    }

    /// <summary>UI.R37: the latest version the shown server is held to (<see cref="VersionStatus.LatestFor"/>).</summary>
    private string? ShownServerLatest
        => VersionStatus.LatestFor(_status?.LatestVersion, _connection.Hello?.HostKind, _localStatus?.LatestVersion);

    /// <summary>
    /// The connection words at the right of the status bar (UI.R37): <paramref name="text"/>, then a link to the update
    /// when there is one, then <paramref name="tail"/>.
    /// </summary>
    private void ShowConnectionWords(string text, string? updateLink = null, string? tail = null)
    {
        string words;
        string tailWords;
        if (updateLink is null)
        {
            words = tail is null ? text : $"{text} · {tail}";
            tailWords = string.Empty;
        }
        else
        {
            words = $"{text} · ";
            tailWords = tail is null ? string.Empty : $" · {tail}";
            // UPD.N1: an update waiting is the coloured link; the latest version is a neutral link to What's new
            // (owner, 2026-11-13: neutral like the rest of the status bar unless it is out of date).
            var shown = _versionLinkUpdates ? UpdateLink : WhatsNewLink;
            if (!Equals(shown.Content, updateLink))
            {
                shown.Content = updateLink;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(shown, _versionLinkUpdates ? "Update available" : $"Version {updateLink}, what's new");
            }
        }

        if (ConnectionText.Text != words) ConnectionText.Text = words;
        if (ConnectionTail.Text != tailWords) ConnectionTail.Text = tailWords;
        var update = updateLink is not null && _versionLinkUpdates ? Visibility.Visible : Visibility.Collapsed;
        if (UpdateLink.Visibility != update) UpdateLink.Visibility = update;
        var whatsNew = updateLink is not null && !_versionLinkUpdates ? Visibility.Visible : Visibility.Collapsed;
        if (WhatsNewLink.Visibility != whatsNew) WhatsNewLink.Visibility = whatsNew;
        var tailShown = tailWords.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (ConnectionTail.Visibility != tailShown) ConnectionTail.Visibility = tailShown;
    }

    /// <summary>
    /// UI.R37: the status bar's "update available" link runs Help > Check for updates; UPD.N1: with no update waiting,
    /// the version itself is the link, to What's new.
    /// </summary>
    private void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (_versionLinkUpdates) _ = OpenUpdateDialogAsync();
        else _ = ShowInstalledWhatsNewAsync();
    }

    /// <summary>The switchers' menus (toolbar, status bar, Tasks > Show Downloads Of): This PC, the saved servers, Servers….</summary>
    private void FillServerMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        foreach (var choice in ServerChoices())
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = ServerMenuText(choice),
                IsChecked = choice.IsCurrent,
                KeyboardAcceleratorTextOverride = choice.ShortcutText ?? string.Empty,
            };
            var id = choice.Id;
            item.PreviewKeyDown += (_, args) => SwitchKeysInMenu(items, args);
            item.Click += (_, _) =>
            {
                // A ToggleMenuFlyoutItem unticks itself when the shown one is chosen again; it stays the shown one.
                item.IsChecked = true;
                _ = id is null ? ShowServerAsync(ServerTarget.Local) : ShowSavedServerAsync(id);
            };
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "Add a Server…" };
        add.Click += (_, _) => _ = AddServerFromMenuAsync();
        items.Add(add);
        var manage = new MenuFlyoutItem { Text = "Servers…" };
        manage.Click += (_, _) => _ = ShowServersDialogAsync();
        items.Add(manage);
    }

    // Ctrl+Shift+O opened the menu: its first item takes the focus, so the arrow keys and Enter work straight away.
    private bool _serverMenuFromKeyboard;

    /// <summary>Ctrl+Shift+O: opens the toolbar switcher's menu, or closes it when it is open.</summary>
    private void OpenServerMenuFromKeyboard()
    {
        if (ToolbarServerMenu.IsOpen)
        {
            ToolbarServerMenu.Hide();
            return;
        }

        _serverMenuFromKeyboard = true;
        ToolbarServerMenu.ShowAt(ToolbarServerSwitcher);
    }

    private void FocusServerMenuOnOpen(MenuFlyout menu)
    {
        if (!_serverMenuFromKeyboard) return;
        _serverMenuFromKeyboard = false;
        // The current one, or the first: where the arrow keys start from.
        var items = menu.Items.OfType<MenuFlyoutItem>().ToList();
        var start = items.OfType<ToggleMenuFlyoutItem>().FirstOrDefault(item => item.IsChecked) ?? items.FirstOrDefault();
        start?.Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Ctrl+0 to Ctrl+9 inside an open switcher menu. The window's own keys don't reach a menu, which is a popup of its own,
    /// yet each item shows its key, so the menu handles them too.
    /// </summary>
    private void SwitchKeysInMenu(IList<MenuFlyoutItemBase> items, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        var number = args.Key - Windows.System.VirtualKey.Number0;
        if (number is < 0 or > ServerSwitch.MostNumbered) return;
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (!control.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        args.Handled = true;
        // Whichever of the three menus holds these items.
        foreach (var menu in new[] { ToolbarServerMenu, ServerSwitcherMenu })
            if (ReferenceEquals(menu.Items, items)) menu.Hide();
        SwitchToNumber(number);
    }

    private async Task AddServerFromMenuAsync()
    {
        try
        {
            await PairServerAsync(null);
        }
        finally
        {
            UpdateServerSwitchers();
        }
    }

    /// <summary>Ctrl+0 shows this PC; Ctrl+1 to Ctrl+9 the saved servers in their order.</summary>
    private void SwitchToNumber(int number)
    {
        if (ServerSwitch.ForNumber(ServerChoices(), number) is not { } choice)
        {
            ShowPassingStatus($"There is no server {number}. Add one in File > Servers.");
            return;
        }

        if (choice.IsCurrent)
        {
            ShowPassingStatus($"{choice.Name} is already shown.");
            return;
        }

        _ = choice.Id is null ? ShowServerAsync(ServerTarget.Local) : ShowSavedServerAsync(choice.Id);
    }

    /// <summary>
    /// Says everywhere which Host the window shows (UI.R26): the toolbar switcher's name and icon, the status-bar
    /// switcher, the window title, and the Tasks menu's list. Called on every switch and whenever the list changes.
    /// </summary>
    private void UpdateServerSwitchers()
    {
        var target = _connection.Target;
        var help = ServerSwitch.SwitcherHelp(target);
        ToolbarServerLabel.Text = target.Name;
        ToolbarServerIcon.Glyph = target.IsLocal ? "" : "";
        var name = ServerNames.SwitcherName(target);
        ToolTipService.SetToolTip(ToolbarServerSwitcher, help);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ToolbarServerSwitcher, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(ToolbarServerSwitcher, help);
        ServerSwitcher.Content = target.Name;
        ToolTipService.SetToolTip(ServerSwitcher, help);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ServerSwitcher, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(ServerSwitcher, help);
        Title = ServerSwitch.TitleFor(target);
        FillServerMenu(TasksServersItem.Items);
        _tasksServerNote = _connection.IsRemote && _graphConnected ? VersionStatus.ServerNote(_status?.HostVersion, ShownServerLatest) : null;
        UpdateServerBanner();
    }

    private async Task ShowSavedServerAsync(string id)
    {
        if (_servers.Target(id) is { } target)
        {
            await ShowServerAsync(target);
            return;
        }

        await ShowMessageAsync("Servers", $"This server's pairing can't be opened on {PlatformText.ThisAccount}. Select it in File > Servers and pair it again.");
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
        EndTorrentWatchFor(old);
        _settingsWindow?.Close();
        _schedulerWindow?.Close();
        _statisticsWindow?.Close();
        // UI.R32: My provider shows the account of the Host shown, so the old one's catalog goes.
        ResetProviderViews(closeWindows: true);

        ShownServer.Target = target;
        // What it said when it was paired, until its own hello and settings say more.
        if (!target.IsLocal) ShownServer.Describe(_servers.Find(target.Id!)?.HostKind, null);
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
        _remoteCompletions.Clear();
        _finishedBurst.Clear();
        _groups = new();
        _status = target.IsLocal ? _localStatus : null;
        _settings = target.IsLocal ? _localSettings : null;
        _graphConnected = _connection.IsConnected;
        _graphSnapshotReady = false;
        _shownEverConnected = _connection.IsConnected;
        _shownConnectionError = null;
        UpdateServerSwitchers();
        ShowConnectionWords(target.IsLocal ? "Connecting…" : $"Connecting to {target.Name}…");
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
            var snapshot = await _local.RefreshAsync();
            // Another switch while this PC's list was read: that one fills the window.
            if (switchNumber != _switches || _connection != _local) return;
            ApplySnapshot(snapshot);
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
        ShowPassingStatus($"Completed on {_connection.Target.Name}: {job.FileName}");
    }

    private void MenuServers_Click(object sender, RoutedEventArgs e) => _ = ShowServersDialogAsync();

    // ---- File > Servers ---------------------------------------------------------------------------------------------

    private async Task ShowServersDialogAsync()
    {
        try
        {
            await ShowServersDialogLoopAsync();
        }
        finally
        {
            UpdateServerSwitchers();
        }
    }

    private async Task ShowServersDialogLoopAsync()
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
            // UI.R31: with no server, Add is the accent button and the buttons for a chosen server stay away.
            var add = new Button { Content = "Add…" };
            if (servers.Count == 0 && InboundUi.Style("AccentButtonStyle") is { } accent) add.Style = accent;
            var none = servers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var edit = new Button { Content = "Edit…", IsEnabled = false, Visibility = none };
            var remove = new Button { Content = "Remove…", IsEnabled = false, Visibility = none };
            var show = new Button { Content = "Show", IsEnabled = false, Visibility = none };
            list.SelectionChanged += (_, _) => edit.IsEnabled = remove.IsEnabled = show.IsEnabled = list.SelectedIndex >= 0;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(add);
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);
            buttons.Children.Add(show);
            var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
            panel.Children.Add(InboundUi.Secondary("Another computer running Inbound (the Windows app with its web UI turned on, or the server) can be shown in this window. Choose which one with the switcher at the right of the toolbar or the left of the status bar, Tasks > Show Downloads Of, or Ctrl+0 to Ctrl+9."));
            panel.Children.Add(servers.Count == 0
                ? InboundUi.Card(InboundUi.EmptyState("\uE968", "No servers yet."))
                : InboundUi.ListCard(list));
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
            DialogArrowKeys.Add(dialog);
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
        panel.Children.Add(SettingsWindow.Hint("The password is used once, to pair this PC with the server, and isn't kept. The server lists this PC under Options, Sign-in and browsers, where the pairing can be removed."));
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
        DialogArrowKeys.Add(dialog);
        await dialog.ShowAsync();
        password.Password = string.Empty;
        if (!paired || existing is null || _connection.Target.Id != existing.Id) return;
        // The shown server changed its name, its http choice or its pairing: connect with what is saved now.
        if (_servers.Target(existing.Id) is { } updated)
        {
            ShownServer.Target = updated;
            ShownServer.Describe(_servers.Find(existing.Id)?.HostKind ?? _connection.Hello?.HostKind, _settings?.DefaultDownloadDirectory);
            _connection.Retarget(updated);
            UpdateServerSwitchers();
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
            try
            {
                _servers.Update(existing.Id, name, plainHttpConfirmed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return ex is ServerListUnreadableException ? ex.Message : "This PC couldn't save the change. Check that the app's data folder can be written to.";
            }

            return null;
        }

        if (password.Length == 0) return "Type the server's password.";
        if (!ServerAddress.MaySend(address, plainHttpConfirmed))
            return "This address starts with http://. Tick the box to allow its sign-in unencrypted, or use an https:// address.";
        var result = await RemoteServerHttp.PairAsync(address, password, $"Inbound on {Environment.MachineName}", plainHttpConfirmed);
        if (!result.Ok) return result.Error;

        // The old pairing, read before the new one replaces it here.
        var old = existing is null ? null : _servers.Target(existing.Id);
        try
        {
            if (existing is null) _servers.Add(name, address, result.Token!, result.TokenId, plainHttpConfirmed, result.HostKind);
            else
            {
                _servers.Repair(existing.Id, address, result.Token!, result.TokenId, plainHttpConfirmed, result.HostKind);
                _servers.Update(existing.Id, name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Not kept here, so the server forgets it too; the old pairing, if any, stays as it was.
            _ = RemoteServerHttp.ForgetTokenAsync(address, result.Token, plainHttpConfirmed);
            return ex is ServerListUnreadableException
                ? ex.Message
                : "The pairing worked, but this PC couldn't save it, so it was undone. Check that the app's data folder can be written to.";
        }

        // Saved: only now is the old token the server's to forget.
        if (old is not null && old.Token != result.Token) _ = RemoteServerHttp.UnpairAsync(old);
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
            await ShowMessageAsync("Servers", ex is ServerListUnreadableException
                ? ex.Message
                : "The server couldn't be removed from this PC's list. Check that the app's data folder can be written to.");
            return;
        }

        if (!told)
            await ShowMessageAsync("Servers",
                $"{server.Name} couldn't be reached, so it still lists this PC as paired. Remove it there in the web UI (Options, Sign-in and browsers).");
    }

    // ---- Options while a server is shown ------------------------------------------------------------------------------

    // Options is being read and built, before its window exists: a second request waits for that one rather than
    // opening a second window (review of UI.R27). Web UI on This PC… meanwhile asks for its page when it is there.
    private bool _optionsOpening;
    private bool _webUiPageWanted;
    private int _optionsRequests;

    // The Web UI page's place in the Options window that is open, if it has one, for Web UI on This PC… to go to.
    private int? _settingsWebUiPage;

    private void MenuWebUi_Click(object sender, RoutedEventArgs e) => _ = OpenOptionsAsync(showWebUi: true);

    /// <summary>
    /// Options, or Web UI on This PC… (UI.R27): Options on its Web UI page, whichever server the window shows. While a
    /// server is shown that page talks to this PC's Host directly, so turning the web UI on never needs a switch back,
    /// and it opens even when the server can't be reached.
    /// </summary>
    private async Task OpenOptionsAsync(bool showWebUi = false)
    {
        if (_settingsWindow is not null)
        {
            if (showWebUi)
            {
                // Still being built: it goes to the page once the page is there.
                if (_settingsWebUiPage is { } page) _settingsWindow.ShowPage(page);
                else _webUiPageWanted = true;
            }

            _settingsWindow.Activate();
            // UI.B30: what Options says about the extensions is this PC's Host's word now, not when it opened.
            _ = RefreshExtensionLinksFromHostAsync();
            return;
        }

        if (_optionsOpening)
        {
            _webUiPageWanted |= showWebUi;
            return;
        }

        _optionsOpening = true;
        _webUiPageWanted = showWebUi;
        var request = ++_optionsRequests;
        try
        {
            if (_connection.IsRemote) await ShowServerOptionsAsync();
            else await ShowSettingsDialogAsync();
        }
        finally
        {
            // Only this request's: a later one may be opening its own window by now.
            if (request == _optionsRequests)
            {
                _optionsOpening = false;
                _webUiPageWanted = false;
            }
        }
    }

    /// <summary>The Options window is there now: other requests activate it instead of opening another.</summary>
    private void OptionsWindowCreated(SettingsWindow window)
    {
        _settingsWindow = window;
        _optionsOpening = false;
        window.Closed += (_, _) =>
        {
            if (_settingsWindow == window) _settingsWindow = null;
            _settingsWebUiPage = null;
        };
    }

    /// <summary>The Web UI page is built: Options goes to it if Web UI on This PC… asked for it meanwhile.</summary>
    private bool WebUiPageReady(SettingsWindow window, int page)
    {
        _settingsWebUiPage = page;
        if (!_webUiPageWanted) return false;
        _webUiPageWanted = false;
        window.ShowPage(page);
        return true;
    }

    /// <summary>"All of This PC's options…": shows this PC, whose Options has every page, and opens it.</summary>
    private async Task OpenThisPcOptionsAsync()
    {
        _settingsWindow?.Close();
        await ShowServerAsync(ServerTarget.Local);
        if (_connection.IsRemote) return;
        if (_settings is null) await RefreshSettingsAsync();
        await OpenOptionsAsync();
    }

    /// <summary>
    /// Options while a server is shown (design D9, section 5): this PC's own choices (theme, start at sign-in, clipboard,
    /// browser, updates, what a finished download does here) are read from and saved to this PC's Host; the server's
    /// pages show only what its hello allows, and a save there never changes those choices on the server.
    /// </summary>
    /// <summary>Opened through <see cref="OpenOptionsAsync"/>, which keeps it to one window.</summary>
    private async Task ShowServerOptionsAsync()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var server = _connection;
        var serverName = server.Target.Name;
        var hostKind = server.Hello?.HostKind;
        SnapshotDto local;
        try
        {
            local = await _local.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException)
        {
            await ShowMessageAsync("Options", RemoteOptionsText.ThisPcUnreachable);
            return;
        }

        // U-WR-4, review of UI.R27: a server that can't be reached still leaves this PC's own pages, Web UI on this PC
        // among them. Its own pages are left out and say why.
        SettingsDto? theirs = null;
        string? unreachable = null;
        try
        {
            theirs = (await server.RefreshAsync()).Settings;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException)
        {
            unreachable = RemoteOptionsText.ServerUnreachable(serverName);
        }

        var pages = RemoteSettingsRules.PagesFor(server.Hello?.Capabilities, hostKind);
        var mine = local.Settings;
        // Review of UI.R41: this PC's status as just fetched; the copy kept from its last snapshot can be old while a server is shown.
        if (local.Status is not null) _localStatus = local.Status;
        var window = new SettingsWindow { Title = $"Options — {serverName} — Inbound Download Manager" };
        OptionsWindowCreated(window);

        // This PC.
        string[] themeValues = [ThemeChoices.System, ThemeChoices.Light, ThemeChoices.Dark];
        var themeBox = new ComboBox
        {
            Header = "Theme",
            MinWidth = 200,
            ItemsSource = new[] { PlatformText.FollowSystemTheme, "Light", "Dark" },
            SelectedIndex = Math.Max(0, Array.IndexOf(themeValues, ThemeChoices.InUse(_uiState.DarkMode, mine.Theme))),
        };
        var launch = new CheckBox { Content = PlatformText.StartAtSignIn, IsChecked = mine.LaunchAtStartup };
        var closeCompletely = new CheckBox { Content = "Close the window completely (it opens a moment slower next time)", IsChecked = mine.CloseWindowCompletely };
        var providerOpens = ProviderOpensBox();
        var integration = new CheckBox { Content = "Accept downloads sent from your browser", IsChecked = mine.BrowserIntegrationEnabled };
        var clipboard = new CheckBox { Content = "Add links I copy to the download list, without asking", IsChecked = mine.ClipboardMonitoring };
        var completionDialog = new CheckBox { Content = "Show the download-complete window", IsChecked = mine.ShowCompletionDialog };
        var notify = new CheckBox { Content = "Show a notification", IsChecked = mine.NotifyOnCompletion };
        var autoUpdate = new CheckBox { Content = "Check for and install updates automatically while no downloads are running", IsChecked = mine.AutoCheckUpdates };
        var updateInterval = SettingsWindow.Number("Check every (hours)", mine.UpdateCheckIntervalHours, 1, 168);
        var sound = new CheckBox { Content = "Play a sound when a download on this PC finishes", IsChecked = mine.PlayCompletionSound };
        var soundEditor = new SoundOptionsEditor(mine.EventSoundFiles, window);
        // The rest of this PC's options (folders, connection, proxy, its own provider account) are in its own Options.
        var allThisPc = new Button { Content = "All of This PC's options…" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(allThisPc, "Shows this PC's downloads and opens its Options");
        allThisPc.Click += (_, _) => _ = OpenThisPcOptionsAsync();
        window.AddPage("This PC", "",
            SettingsWindow.Hint($"This PC's own choices. They are saved on this PC, whichever server the window shows, and never change {serverName}."),
            allThisPc,
            SettingsWindow.Hint("Shows this PC's downloads in the window and opens this PC's Options, with its folders, connection and the rest."),
            SettingsWindow.Section("Startup"), launch,
            SettingsWindow.Section("Window"), themeBox, closeCompletely, providerOpens,
            SettingsWindow.Section("Browser downloads"), integration, clipboard,
            // UI.R41: this PC's extensions; the server's pair with it from the extension's Options.
            SettingsWindow.Section("Browser extension"), SettingsWindow.Hint(ExtensionPromoRules.SectionHint), ExtensionLinksPanel(_localStatus),
            SettingsWindow.Section($"When a download on {serverName} finishes, while this window is open"), completionDialog, notify,
            SettingsWindow.Hint($"For {serverName}, the notification is a line in this window's status bar, and no sound plays."),
            SettingsWindow.Section("Updates"), autoUpdate, updateInterval);
        const int ThisPcPage = 0;
        window.AddPage("Sounds on this PC", "", sound, soundEditor.Content);
        const int SoundsPage = 1;

        // The server.
        var serverControls = new List<UIElement>
        {
            SettingsWindow.Hint(unreachable ?? RemoteOptionsText.ServerPageHint(serverName, hostKind)),
        };
        // U-WR-13: the rest is in the server's web UI, one click away.
        if (server.Target.Address is { } webAddress)
        {
            var openWeb = new HyperlinkButton { Content = $"Open {serverName}'s web UI", NavigateUri = webAddress, Padding = new Thickness(0, 2, 0, 2) };
            ToolTipService.SetToolTip(openWeb, webAddress.ToString());
            serverControls.Add(openWeb);
        }

        TextBox? directory = null;
        CheckBox? useCategoryFolders = null, useTorrentCategoryFolders = null;
        NumberBox? maxConcurrent = null, connections = null, speedLimit = null;
        CheckBox? autoResume = null;
        TextBox? postDownload = null;
        if (theirs is not null)
        {
            if (pages.Folders)
            {
                var (row, box) = window.FolderField("Default download folder", theirs.DefaultDownloadDirectory);
                directory = box;
                useCategoryFolders = new CheckBox { Content = "Save downloads to a folder for their category", IsChecked = theirs.UseCategoryFolders };
                serverControls.Add(SettingsWindow.Section("Save to"));
                serverControls.Add(row);
                serverControls.Add(useCategoryFolders);
                // T14: a server from before the torrent folders sends no switch; the torrent folder stays in its web UI.
                if (theirs.UseTorrentCategoryFolders is { } torrentOn)
                {
                    useTorrentCategoryFolders = new CheckBox { Content = "Save torrents to a folder for their category", IsChecked = torrentOn };
                    serverControls.Add(useTorrentCategoryFolders);
                }
            }

            maxConcurrent = SettingsWindow.Number("Maximum simultaneous downloads", theirs.MaxConcurrentDownloads, 1, 16);
            connections = SettingsWindow.Number("Connections per download", theirs.DefaultConnections, 1, 32);
            speedLimit = SettingsWindow.Number("Total speed limit, all downloads (KB/s)",
                theirs.GlobalSpeedLimitBytesPerSecond is > 0 ? theirs.GlobalSpeedLimitBytesPerSecond.Value / 1024 : null, 1, 10_000_000, "Unlimited");
            speedLimit.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
            serverControls.Add(SettingsWindow.Section("Connection"));
            serverControls.Add(maxConcurrent);
            serverControls.Add(connections);
            serverControls.Add(speedLimit);
            serverControls.Add(SettingsWindow.Hint("Leave the speed limit empty for unlimited."));
            if (pages.AutoResume)
            {
                autoResume = new CheckBox { Content = $"Resume unfinished downloads when {serverName} starts", IsChecked = theirs.AutoResumeUnfinishedOnStartup };
                serverControls.Add(autoResume);
            }

            if (pages.ProgramSettings)
            {
                postDownload = new TextBox { Header = $"Run this program on {serverName} after each download (optional)", Text = theirs.PostDownloadCommand ?? string.Empty };
                serverControls.Add(SettingsWindow.Section("When a download finishes"));
                serverControls.Add(postDownload);
                serverControls.Add(SettingsWindow.Hint(ShellWords.PostDownloadProgramGets));
                // Review 2:L5: a program that server keeps but refuses to start (its own rule, so its own words).
                if (theirs.PostDownloadCommandProblem is { } problem)
                {
                    var note = SettingsWindow.Hint(problem);
                    note.Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid);
                    serverControls.Add(note);
                }
            }
        }

        window.AddPage(serverName, "", serverControls.ToArray());
        const int ServerPage = SoundsPage + 1;
        var nextPage = ServerPage + 1;

        // The server's provider and extraction pages, through the provider client, which follows the shown server.
        var accountPages = new List<(ProviderAccountPage Page, int Index)>();
        ExtractionOptionsPage? extraction = null;
        var extractionPageIndex = nextPage;
        if (theirs is not null)
        {
            if (!_remote.State.IsReady) await _remote.WaitUntilReadyAsync(TimeSpan.FromSeconds(1.5));
            if (_remote.State.RemoteSupported)
            {
                foreach (var account in _remote.State.Accounts.Where(account => account.Capabilities.Contains("account", StringComparer.OrdinalIgnoreCase)))
                {
                    var page = new ProviderAccountPage(_remote, account, serverName, hostKind);
                    page.KeyBoxEmptied += window.ClearMessage;
                    window.AddPage(page.Title, "", page.Controls);
                    accountPages.Add((page, nextPage++));
                }
            }

            extractionPageIndex = nextPage;
            if (_remote.State.ArchivesSupported)
            {
                extraction = new ExtractionOptionsPage(_remote, window);
                window.AddPage("Extraction", "", extraction.Controls);
                nextPage++;
            }
        }

        // UI.R27: this PC's web UI, through a client of this PC's Host of its own (the window's follows the server, and
        // a server never takes webui.* from a paired app). It lets other devices reach this PC, not the server.
        var localClient = new RemoteClient(action => DispatcherQueue.TryEnqueue(() => action()));
        var webUi = new WebUiOptionsPage(localClient) { PageIndex = nextPage };
        window.AddPage("Web UI on this PC", "",
            [SettingsWindow.Hint($"This PC's own web UI: a browser on another device can control this PC's downloads through it. It doesn't change {serverName}; {serverName}'s web UI is set on {serverName}."),
             .. webUi.Controls]);

        window.Closed += (_, _) =>
        {
            foreach (var (page, _) in accountPages) page.Close();
            extraction?.Close();
            webUi.Close();
            _ = localClient.DisposeAsync().AsTask();
        };

        static int? WholeNumber(NumberBox box) => double.IsNaN(box.Value) ? null : (int)Math.Round(box.Value);
        window.Save = async () =>
        {
            foreach (var (page, index) in accountPages)
            {
                if (page.HasUnsavedKey) return ($"The {page.ProviderName} API key you typed isn't saved. Select Save key, or empty the box.", index);
            }

            if (extraction?.Validate() is { } extractionProblem) return (extractionProblem, extractionPageIndex);
            if (webUi.Validate() is { } webUiProblem) return (webUiProblem, webUi.PageIndex);
            if (WholeNumber(updateInterval) is not { } checkInterval || checkInterval is < 1 or > 168) return ("Update interval must be 1-168 hours.", ThisPcPage);
            var sounds = new Dictionary<string, string>(mine.EventSoundFiles, StringComparer.OrdinalIgnoreCase);
            if (!soundEditor.TryApplyTo(sounds, out var soundError)) return (soundError ?? "Invalid sound choice.", SoundsPage);
            if (maxConcurrent is not null && connections is not null && speedLimit is not null)
            {
                if (WholeNumber(maxConcurrent) is not { } max || max is < 1 or > 16) return ("Maximum simultaneous downloads must be between 1 and 16.", ServerPage);
                if (WholeNumber(connections) is not { } connectionsValue || connectionsValue is < 1 or > 32) return ("Connections per download must be between 1 and 32.", ServerPage);
                long? limit = WholeNumber(speedLimit) is { } kb ? kb * 1024L : null;
                if (limit is <= 0) return ("Speed limit must be a positive number of KB/s.", ServerPage);
                if (await SaveServerSideAsync(max, connectionsValue, limit) is { } serverProblem) return (serverProblem, ServerPage);
            }

            return await SaveThisPcSideAsync(checkInterval, sounds);
        };

        // The server first: if it doesn't take its settings, nothing is changed anywhere. Only its own fields; this
        // PC's choices go back to it as it has them. Null once saved.
        async Task<string?> SaveServerSideAsync(int max, int connectionsValue, long? limit)
        {
            SettingsDto serverAtSave;
            try
            {
                serverAtSave = (await server.RefreshAsync()).Settings;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException)
            {
                return $"Can't reach {serverName} right now, so nothing was saved, on {serverName} or on this PC.";
            }

            var edited = CloneSettings(serverAtSave);
            if (directory is not null) edited.DefaultDownloadDirectory = directory.Text.Trim();
            if (useCategoryFolders is not null) edited.UseCategoryFolders = useCategoryFolders.IsChecked == true;
            if (useTorrentCategoryFolders is not null) edited.UseTorrentCategoryFolders = useTorrentCategoryFolders.IsChecked == true;
            edited.MaxConcurrentDownloads = max;
            edited.DefaultConnections = connectionsValue;
            edited.GlobalSpeedLimitBytesPerSecond = limit;
            if (autoResume is not null) edited.AutoResumeUnfinishedOnStartup = autoResume.IsChecked == true;
            if (postDownload is not null) edited.PostDownloadCommand = string.IsNullOrWhiteSpace(postDownload.Text) ? null : postDownload.Text.Trim();
            RemoteSettingsRules.KeepServerValues(edited, serverAtSave);
            SettingsResult serverResult;
            try
            {
                serverResult = (await server.UpdateSettingsAsync(edited)).GetPayload<SettingsResult>();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException)
            {
                return $"The connection to {serverName} was lost while saving, so its options may not have been saved. Nothing was saved on this PC.";
            }

            if (!serverResult.Ok) return $"{serverResult.Error ?? $"{serverName} didn't take the settings."} Nothing was saved, on {serverName} or on this PC.";
            if (server == _connection)
            {
                _settings = serverResult.Settings;
                UpdateLimitButtons();
            }

            return null;
        }

        // Then this PC: from its latest settings, so nothing changed elsewhere meanwhile is undone.
        async Task<(string?, int)> SaveThisPcSideAsync(int checkInterval, Dictionary<string, string> sounds)
        {
            string? thisPcProblem = null;
            try
            {
                var localLatest = CloneSettings((await _local.RefreshAsync()).Settings);
                localLatest.Theme = themeValues[Math.Max(0, themeBox.SelectedIndex)];
                localLatest.LaunchAtStartup = launch.IsChecked == true;
                localLatest.CloseWindowCompletely = closeCompletely.IsChecked == true;
                SaveProviderOpens(providerOpens);
                localLatest.BrowserIntegrationEnabled = integration.IsChecked == true;
                localLatest.ClipboardMonitoring = clipboard.IsChecked == true;
                localLatest.ShowCompletionDialog = completionDialog.IsChecked == true;
                localLatest.NotifyOnCompletion = notify.IsChecked == true;
                localLatest.AutoCheckUpdates = autoUpdate.IsChecked == true;
                localLatest.UpdateCheckIntervalHours = checkInterval;
                localLatest.PlayCompletionSound = sound.IsChecked == true;
                localLatest.EventSoundFiles = sounds;
                var localResult = (await _local.UpdateSettingsAsync(localLatest)).GetPayload<SettingsResult>();
                if (!localResult.Ok) thisPcProblem = localResult.Error ?? "This PC didn't take them.";
                else
                {
                    KeepLocalSettings(localResult.Settings);
                    if (_uiState.DarkMode is not null)
                    {
                        _uiState.DarkMode = null;
                        SaveUiState();
                    }

                    AppTheme.Use(localResult.Settings.Theme);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or InvalidDataException)
            {
                thisPcProblem = "This PC isn't connected.";
            }

            if (thisPcProblem is not null)
                return (theirs is null ? $"This PC's options weren't saved: {thisPcProblem}" : $"{serverName}'s options were saved, but this PC's weren't: {thisPcProblem}", ThisPcPage);

            var extractionSaveProblem = extraction is null ? null : await extraction.SaveAsync();
            var webUiSaveProblem = await webUi.SaveAsync();
            // Neither problem is dropped: both are said, and the window shows the page of the first.
            if (webUiSaveProblem is not null && extractionSaveProblem is not null)
                return ($"{extractionSaveProblem} {webUiSaveProblem}", extractionPageIndex);
            if (webUiSaveProblem is not null) return (webUiSaveProblem, webUi.PageIndex);
            return extractionSaveProblem is null ? (null, 0) : (extractionSaveProblem, extractionPageIndex);
        }

        // Unreachable: the server's page, which says so, unless Web UI on This PC… asked for its page.
        if (!WebUiPageReady(window, webUi.PageIndex) && unreachable is not null) window.ShowPage(ServerPage);
        window.Activate();
        await window.WaitClosedAsync();
    }
}
