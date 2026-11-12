using DownloadManager.Contracts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DownloadManager.Desktop.WinUI;

/// <summary>The strip on the drawer's left edge that is dragged to change its width.</summary>
public sealed partial class ProviderDrawerGrip : Grid
{
    public ProviderDrawerGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}

/// <summary>
/// UI.R32: My provider in the main window. One <see cref="ProviderPanel"/> moves between the sidebar view in the list's
/// place (A, the default way in) and a drawer over the list's right side (C, the toolbar button and Ctrl+T), keeping its
/// tab, chosen item, scroll and folder. "Open in its own window" moves it to a <see cref="ProviderWindow"/>. It reads
/// only while one of those shows it. After a Download a notice in the list says what was added (B's notice).
/// </summary>
public sealed partial class MainWindow
{
    private ProviderPanel? _providerPanel;
    private bool _providerFull;
    private bool _closingDrawer;
    private ProviderDownloadNotice? _providerNotice;
    private DispatcherOneShot? _providerNoticeHide;

    /// <summary>How long the "Added N files" notice stays before it closes by itself (UI.R33).</summary>
    internal static readonly TimeSpan ProviderNoticeTime = TimeSpan.FromSeconds(3);
    private MenuFlyoutItem? _menuShowInProvider;
    private double _gripStartX;
    private double _gripStartWidth;
    private bool _gripDragging;
    private DateTime _drawerDismissedAt = DateTime.MinValue;
    private Visibility _scrollTrackBeforeFull = Visibility.Collapsed;

    private void InitializeProviderViews()
    {
        ProviderDrawerButton.IsEnabled = true;
        ProviderNoticeBar.Closed += (_, _) => { _providerNotice = null; _providerNoticeHide?.Stop(); };
        _providerNoticeHide = new DispatcherOneShot(DispatcherQueue, ProviderNoticeTime, () =>
        {
            // Focus on one of its buttons would be lost with it, so it goes to the list first.
            if (ProviderNoticeBar.FocusState != FocusState.Unfocused || ProviderNoticeBack.FocusState != FocusState.Unfocused)
                JobsList.Focus(FocusState.Programmatic);
            ProviderNoticeBar.IsOpen = false;
        });
        AppWindow.Changed += (sender, args) =>
        {
            // Hidden to the tray, nothing is read or ticks; shown again, the view it had reads again.
            // Audit M11: minimised too.
            if (args.DidVisibilityChange || args.DidPresenterChange) _providerPanel?.SetShown(WindowShown && ProviderPanelOnShow);
        };
    }

    /// <summary>Ctrl+T opens and closes the drawer; Esc closes one that isn't pinned, wherever the focus is, and is otherwise left alone.</summary>
    private IEnumerable<KeyboardAccelerator> ProviderAccelerators()
    {
        yield return CreateAccelerator(Windows.System.VirtualKey.T, Windows.System.VirtualKeyModifiers.Control, ToggleProviderDrawer);
        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => args.Handled = CloseUnpinnedDrawer();
        yield return escape;
    }

    private bool ProviderPanelOnShow => _providerFull || ProviderSplit.IsPaneOpen;

    private IReadOnlyCollection<JobDto> LocalFilesOfJob(RemoteJobDto job)
        => LocalFilesOf(job).Select(file => file.Dto).OfType<JobDto>().ToList();

    private ProviderPanelContext ProviderContext(Func<IntPtr> handle)
        => new(() => _settings, LocalFilesOfJob, RememberFolder, _uiState, () => SaveUiState(), handle);

    /// <summary>The first account My provider can browse; the drawer opens on it.</summary>
    private RemoteAccountDto? BrowsableAccount(string? provider = null)
        => _remote.State.RemoteSupported
            ? _remote.State.Accounts.FirstOrDefault(account => RemoteRules.CanBrowse(account) && (provider is null || RemoteRules.SameProvider(account.Provider, provider)))
            : null;

    private ProviderPanel? EnsureProviderPanel(string? provider)
    {
        if (BrowsableAccount(provider ?? _providerPanel?.Provider) is not { } account) return null;
        if (_providerPanel is { } existing && RemoteRules.SameProvider(existing.Provider, account.Provider)) return existing;
        ResetProviderViews(closeWindows: false);

        var panel = new ProviderPanel(_remote, account, ProviderContext(() => WinRT.Interop.WindowNative.GetWindowHandle(this)), inMainWindow: true);
        panel.Pinned = _uiState.ProviderDrawerPinned;
        panel.Downloaded += OnProviderDownloaded;
        panel.ShowInListRequested += ShowProviderJobOnList;
        panel.OwnWindowRequested += () => MoveProviderToOwnWindow(panel);
        panel.FullViewRequested += () => ShowProviderFull(panel.Provider);
        panel.DrawerRequested += () => ShowProviderDrawer(panel.Provider);
        panel.CloseRequested += CloseProviderDrawer;
        panel.PinChanged += pinned =>
        {
            _uiState.ProviderDrawerPinned = pinned;
            SaveUiState();
            UpdateDrawerLayout();
        };
        panel.EscapePressed += CloseUnpinnedDrawer;
        _providerPanel = panel;
        return panel;
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel parent) parent.Children.Remove(element);
    }

    // ---- A: the sidebar view (the default) -------------------------------------------------------------

    /// <summary>
    /// Every way into My provider that isn't a view's own button: the sidebar, Tasks, Show in My provider. It opens the
    /// full view (A) unless Options says the drawer (C) comes first; either leads on to the other.
    /// </summary>
    private void OpenProviderWindow(string provider)
    {
        if (!ProviderOpening.OpensDrawer(_uiState.ProviderOpensAs))
        {
            ShowProviderFull(provider);
            return;
        }

        ShowProviderDrawer(provider);
        // From the sidebar, its entry stays on the category the list shows.
        if (!_providerFull) SelectSidebarQuietly(_category);
    }

    private void ShowProviderFull(string provider)
    {
        // From the drawer, a new panel takes up the drawer's place (tab, item, scroll, folder). The same panel moved out
        // of the SplitView's pane was left with almost no controls in UI Automation, so a screen reader found nothing.
        if (_providerPanel is { Mode: ProviderPanelMode.Drawer } drawer && BrowsableAccount(provider) is { } account &&
            RemoteRules.SameProvider(drawer.Provider, account.Provider))
        {
            var state = drawer.Capture();
            if (ProviderSplit.IsPaneOpen)
            {
                _closingDrawer = true;
                ProviderSplit.IsPaneOpen = false;
            }

            drawer.Close();
            Detach(drawer.Element);
            _providerPanel = null;
            if (EnsureProviderPanel(provider) is { } fresh) fresh.Restore(state);
        }

        if (EnsureProviderPanel(provider) is not { } panel) return;
        panel.SetMode(ProviderPanelMode.Full);
        if (ProviderSplit.IsPaneOpen)
        {
            _closingDrawer = true;
            ProviderSplit.IsPaneOpen = false;
        }

        AttachFull(panel);
        if (!_providerFull) _scrollTrackBeforeFull = JobsScrollTrack.Visibility;
        _providerFull = true;
        ProviderFullHost.Visibility = Visibility.Visible;
        DownloadListViewport.Visibility = Visibility.Collapsed;
        JobsScrollTrack.Visibility = Visibility.Collapsed;
        ProviderNoticeBar.IsOpen = false;
        SelectSidebarQuietly(RemoteBrowseTag + panel.Provider);
        UpdateProviderButton();
        SyncTorrentInfo();
        panel.SetShown(AppWindow.IsVisible);
    }

    private void AttachFull(ProviderPanel panel)
    {
        if (ReferenceEquals(panel.Element.Parent, ProviderFullHost)) return;
        Detach(panel.Element);
        ProviderFullHost.Children.Add(panel.Element);
    }

    /// <summary>Back to the list as it was. The panel keeps its place for the next time.</summary>
    private void LeaveProviderFull(bool selectCategory)
    {
        if (!_providerFull) return;
        _providerFull = false;
        ProviderFullHost.Visibility = Visibility.Collapsed;
        DownloadListViewport.Visibility = Visibility.Visible;
        JobsScrollTrack.Visibility = _scrollTrackBeforeFull;
        if (_providerPanel is { } panel && !ProviderSplit.IsPaneOpen) panel.SetShown(false);
        if (selectCategory) SelectSidebarQuietly(_category);
        UpdateProviderButton();
        SyncTorrentInfo();
    }

    private void SelectSidebarQuietly(string tag)
    {
        _refreshingCategories = true;
        try
        {
            CategoryList.SelectedItem = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag as string == tag);
        }
        finally
        {
            _refreshingCategories = false;
        }
    }

    // ---- C: the drawer -------------------------------------------------------------------------------

    private void ProviderDrawerButton_Click(object sender, RoutedEventArgs e) => ToggleProviderDrawer();

    private void ToggleProviderDrawer()
    {
        // A click on the button that closed the drawer by light dismiss must not open it again at once.
        if ((DateTime.UtcNow - _drawerDismissedAt).TotalMilliseconds < 300) return;
        if (ProviderSplit.IsPaneOpen) CloseProviderDrawer();
        else ShowProviderDrawer(null);
    }

    private void ShowProviderDrawer(string? provider)
    {
        if (EnsureProviderPanel(provider) is not { } panel) return;
        panel.SetMode(ProviderPanelMode.Drawer);
        LeaveProviderFull(selectCategory: true);
        Detach(panel.Element);
        ProviderDrawerHost.Children.Add(panel.Element);
        UpdateDrawerLayout();
        ProviderSplit.IsPaneOpen = true;
        UpdateProviderButton();
        panel.SetShown(AppWindow.IsVisible);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => panel.Element.Focus(FocusState.Programmatic));
    }

    private void CloseProviderDrawer()
    {
        if (!ProviderSplit.IsPaneOpen) return;
        _closingDrawer = true;
        ProviderSplit.IsPaneOpen = false;
    }

    private bool CloseUnpinnedDrawer()
    {
        if (!ProviderSplit.IsPaneOpen || _providerPanel is { Pinned: true }) return false;
        CloseProviderDrawer();
        JobsList.Focus(FocusState.Keyboard);
        return true;
    }

    /// <summary>A click outside closes an unpinned drawer that covers the list. A pinned one stays, even where it has to cover it.</summary>
    private void ProviderSplit_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
    {
        if (!_closingDrawer && _providerPanel is { Pinned: true }) args.Cancel = true;
    }

    private void ProviderSplit_PaneClosed(SplitView sender, object args)
    {
        if (!_closingDrawer) _drawerDismissedAt = DateTime.UtcNow;
        _closingDrawer = false;

        if (_providerPanel is { } panel && !_providerFull) panel.SetShown(false);
        UpdateProviderButton();
    }

    private void ProviderSplit_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDrawerLayout();

    /// <summary>The drawer's width within its limits, and docked or covering by the room there is.</summary>
    private void UpdateDrawerLayout()
    {
        var available = ProviderSplit.ActualWidth;
        if (available <= 0) return;
        var width = ProviderDrawerLayout.Width(_uiState.ProviderDrawerWidth, available);
        ProviderSplit.OpenPaneLength = width;
        var inline = ProviderDrawerLayout.Inline(_providerPanel?.Pinned == true, available, width);
        var mode = inline ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        if (ProviderSplit.DisplayMode != mode) ProviderSplit.DisplayMode = mode;
    }

    private void ProviderDrawerGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement grip) return;
        _gripDragging = grip.CapturePointer(e.Pointer);
        _gripStartX = e.GetCurrentPoint(ProviderSplit).Position.X;
        _gripStartWidth = ProviderSplit.OpenPaneLength;
        e.Handled = true;
    }

    private void ProviderDrawerGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_gripDragging) return;
        var x = e.GetCurrentPoint(ProviderSplit).Position.X;
        _uiState.ProviderDrawerWidth = ProviderDrawerLayout.Width(_gripStartWidth + (_gripStartX - x), ProviderSplit.ActualWidth);
        UpdateDrawerLayout();
        e.Handled = true;
    }

    private void ProviderDrawerGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_gripDragging) return;
        _gripDragging = false;
        (sender as UIElement)?.ReleasePointerCaptures();
        SaveUiState();
    }

    private void ProviderDrawerGrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _uiState.ProviderDrawerWidth = null;
        UpdateDrawerLayout();
        SaveUiState();
    }

    // ---- Its own window ------------------------------------------------------------------------------

    /// <summary>"Open in its own window": the same place, in a window for a second screen. The main window's view closes.</summary>
    private void MoveProviderToOwnWindow(ProviderPanel panel)
    {
        var state = panel.Capture();
        var provider = panel.Provider;
        if (_providerFull) LeaveProviderFull(selectCategory: true);
        CloseProviderDrawer();
        if (BrowsableAccount(provider) is not { } account) return;
        if (!_providerWindows.TryGetValue(provider, out var window))
        {
            window = new ProviderWindow(_remote, account, own => ProviderContext(() => WinRT.Interop.WindowNative.GetWindowHandle(own)));
            window.Panel.Downloaded += notice =>
            {
                OnProviderDownloaded(notice with { FromDrawer = false });
                window.Close();
            };
            window.Panel.ShowInListRequested += ShowProviderJobOnList;
            _providerWindows[provider] = window;
            window.Closed += (_, _) => _providerWindows.Remove(provider);
        }

        window.Panel.Restore(state);
        window.ShowWindow();
    }

    // ---- The notice and the way between the list and the catalog ---------------------------------------

    /// <summary>After a Download: the list shows, with a notice of what was added, a way to it and a way back.</summary>
    private void OnProviderDownloaded(ProviderDownloadNotice notice)
    {
        if (_providerFull) LeaveProviderFull(selectCategory: true);
        if (ProviderSplit.IsPaneOpen && _providerPanel is { Pinned: false }) CloseProviderDrawer();
        _providerNotice = notice;
        var name = ProviderDisplayName(notice.Provider);
        ProviderNoticeBar.Title = notice.Text;
        ProviderNoticeBar.Message = string.Empty;
        ProviderNoticeBack.Content = $"Back to My {name}";
        AutomationProperties.SetName(ProviderNoticeBack, $"Back to My {name}");
        ProviderNoticeBar.IsOpen = true;
        _providerNoticeHide?.Restart();
        ShowPassingStatus(notice.Text + ".");
    }

    private void ProviderNoticeShow_Click(object sender, RoutedEventArgs e)
    {
        if (_providerNotice is not { } notice) return;
        if (_remote.State.Jobs.FirstOrDefault(job => job.Id == notice.RemoteJobId) is { } job) ShowProviderJobOnList(job);
        else ShowPassingStatus("Those downloads are no longer on the list.");
    }

    private void ProviderNoticeBack_Click(object sender, RoutedEventArgs e)
    {
        if (_providerNotice is not { } notice) return;
        ProviderNoticeBar.IsOpen = false;
        if (notice.FromDrawer) ShowProviderDrawer(notice.Provider);
        else ShowProviderFull(notice.Provider);
    }

    /// <summary>The downloads a job brought to the list, selected on All Downloads; a job with none yet, its own row.</summary>
    private void ShowProviderJobOnList(RemoteJobDto job)
    {
        var current = _remote.State.Jobs.FirstOrDefault(candidate => candidate.Id == job.Id) ?? job;
        var rows = LocalFilesOf(current);
        if (rows.Count == 0 && _remoteRows.TryGetValue(current.Id, out var remoteRow)) rows = [remoteRow];
        if (rows.Count == 0)
        {
            ShowPassingStatus("Its downloads aren't on the list yet.");
            return;
        }

        if (_providerFull) LeaveProviderFull(selectCategory: false);
        if (ProviderSplit.IsPaneOpen && _providerPanel is { Pinned: false }) CloseProviderDrawer();
        ShowRowsOnList(rows);
    }

    /// <summary>The catalog item behind the selected rows, when they all come from one: a file, its package, or a waiting row.</summary>
    private RemoteJobDto? ProviderJobOfSelection()
    {
        var rows = JobsList.SelectedItems.OfType<JobViewModel>().ToList();
        if (rows.Count == 0) return null;
        RemoteJobDto? found = null;
        foreach (var row in rows)
        {
            RemoteJobDto? job;
            if (row.IsRemoteRow)
            {
                job = row.RemoteJob;
            }
            else
            {
                var package = row.IsPackage ? row.Id : row.PackageId ?? _groups.FirstOrDefault(group => group.JobIds.Contains(row.Id))?.Id;
                job = package is { } id ? _remote.State.Jobs.FirstOrDefault(candidate => candidate.PackageId == id && !string.IsNullOrEmpty(candidate.ItemId)) : null;
            }

            if (job is not { ItemId.Length: > 0 } || BrowsableAccount(job.Provider) is null) return null;
            if (found is not null && found.Id != job.Id) return null;
            found = job;
        }

        return found;
    }

    private void AddShowInProviderItem(MenuFlyout flyout)
    {
        _menuShowInProvider = CreateContextItem("Show in My provider", (_, _) =>
        {
            if (ProviderJobOfSelection() is not { ItemId: { } item } job) return;
            OpenProviderWindow(job.Provider);
            _providerPanel?.Reveal(job.Kind, item);
        });
        var at = _menuShowDownload is { } after ? flyout.Items.IndexOf(after) + 1 : 0;
        flyout.Items.Insert(at, _menuShowInProvider);
    }

    private void ShowProviderContextItem()
    {
        if (_menuShowInProvider is null) return;
        var job = ProviderJobOfSelection();
        _menuShowInProvider.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
        if (job is not null) _menuShowInProvider.Text = $"Show in My {ProviderDisplayName(job.Provider)}";
    }

    // ---- The option: which view opens first ----------------------------------------------------------

    /// <summary>Options > Window: "Open My provider as". Shown only while an account can be browsed.</summary>
    private ComboBox ProviderOpensBox()
    {
        var name = BrowsableAccount() is { } account ? RemoteText.ProviderName(account) : "provider";
        var box = new ComboBox
        {
            Header = $"Open My {name} as",
            MinWidth = 260,
            ItemsSource = new[] { "Full view, in place of the list", "Drawer beside the list" },
            SelectedIndex = ProviderOpening.OpensDrawer(_uiState.ProviderOpensAs) ? 1 : 0,
            Visibility = BrowsableAccount() is null ? Visibility.Collapsed : Visibility.Visible,
        };
        AutomationProperties.SetAutomationId(box, "ProviderOpensBox");
        ToolTipService.SetToolTip(box, $"Where the sidebar's My {name}, the Tasks menu and Show in My {name} open it. The other view is one click on from there.");
        return box;
    }

    private void SaveProviderOpens(ComboBox box)
    {
        _uiState.ProviderOpensAs = ProviderOpening.Choices[Math.Clamp(box.SelectedIndex, 0, ProviderOpening.Choices.Length - 1)];
        SaveUiState();
    }

    // ---- Following the accounts and the server shown ---------------------------------------------------

    /// <summary>The toolbar button shows while an account can be browsed. A view whose account can't be any more closes.</summary>
    private void SyncProviderViews()
    {
        var account = BrowsableAccount();
        ProviderToolGroup.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        if (account is not null)
        {
            var name = RemoteText.ProviderName(account);
            ProviderDrawerLabel.Text = $"My {name}";
            AutomationProperties.SetName(ProviderDrawerButton, $"My {name}");
            ToolTipService.SetToolTip(ProviderDrawerButton, $"Show My {name} as a drawer over the list (Ctrl+T)");
        }

        if (_providerPanel is { } panel && BrowsableAccount(panel.Provider) is null && _remote.State.IsReady) ResetProviderViews(closeWindows: true);
    }

    private void UpdateProviderButton()
    {
        var open = ProviderSplit.IsPaneOpen;
        if (open)
        {
            ProviderDrawerButton.Background = InboundTheme.Brush("InboundCardBrush", RootGrid);
            ProviderDrawerButton.BorderBrush = InboundTheme.Brush("InboundCardStrokeBrush", RootGrid);
            ProviderDrawerButton.BorderThickness = new Thickness(1);
        }
        else
        {
            ProviderDrawerButton.ClearValue(Control.BackgroundProperty);
            ProviderDrawerButton.ClearValue(Control.BorderBrushProperty);
            ProviderDrawerButton.ClearValue(Control.BorderThicknessProperty);
        }

        AutomationProperties.SetItemStatus(ProviderDrawerButton, open ? "Open" : string.Empty);
    }

    /// <summary>Another server is shown, or the account went: its catalog goes with every view of it.</summary>
    private void ResetProviderViews(bool closeWindows)
    {
        ProviderNoticeBar.IsOpen = false;
        LeaveProviderFull(selectCategory: true);
        if (ProviderSplit.IsPaneOpen)
        {
            _closingDrawer = true;
            ProviderSplit.IsPaneOpen = false;
        }

        if (_providerPanel is { } panel)
        {
            panel.Close();
            Detach(panel.Element);
            _providerPanel = null;
        }

        if (closeWindows)
        {
            foreach (var window in _providerWindows.Values.ToList()) window.Close();
        }

        UpdateProviderButton();
    }
}
