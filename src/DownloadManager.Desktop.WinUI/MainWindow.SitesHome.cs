using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R4: the "Remote files" part of the sidebar (All remotes and one entry per remote, as drawn) and the All remotes
/// page, with Add a remote, Edit remote, Test and Remove through the flows Options > Sites used to hold (<see cref="SiteFlows"/>).
/// </summary>
public sealed partial class MainWindow
{
    private const string SitesHomeTag = "sites-home";

    private SitesHomePanel? _homePanel;
    private bool _homeShown;
    private SiteFlows? _siteFlows;

    /// <summary>Addresses opened once: the Host holds them in memory and lists none, so the window remembers them while they are open.</summary>
    private readonly List<SiteDto> _onceSites = [];

    private SiteDto? FindSite(string? id)
        => id is null ? null : _sites.FirstOrDefault(site => site.Id == id) ?? _onceSites.FirstOrDefault(site => site.Id == id);

    private SiteFlows EnsureSiteFlows()
    {
        var flows = _siteFlows ??= new SiteFlows(_remote, this, CancellationToken.None,
            SayFlowResult,
            async action =>
            {
                try
                {
                    await action();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    // Every failure is shown: a flow started from a menu has no one else to tell. No exception words, a
                    // secret may be in them.
                    UiTrace.Write($"A remote's flow failed: {ex.GetType().Name}");
                    SayFlowResult("That didn't finish: Inbound's background program didn't answer as expected. Try again.", true);
                }
            },
            () =>
            {
                SyncSiteEntryPoints();
                RefreshHome();
            });
        // The flows keep this list current after a save or a trust, as the window's own list would.
        flows.Sites = _sites;
        return flows;
    }

    /// <summary>
    /// A flow's result: on the All remotes page its own line (the status bar there is the page's count, rewritten on each
    /// refresh); elsewhere the status bar.
    /// </summary>
    private void SayFlowResult(string text, bool isProblem)
    {
        if (_homeShown && _homePanel is { } panel) panel.SayMessage(string.IsNullOrEmpty(text) ? null : text, isProblem);
        else SetSitesStatus(string.IsNullOrEmpty(text) ? null : text);
    }

    // ---- The page -----------------------------------------------------------------------------------------

    private SitesHomePanel EnsureHomePanel()
    {
        if (_homePanel is { } existing) return existing;
        var panel = new SitesHomePanel();
        panel.OpenRequested += site => OpenSiteFromHome(site.Id);
        panel.OpenAddressRequested += address => _ = OpenAddressOnceAsync(address);
        panel.AddRequested += () => _ = EnsureSiteFlows().SaveFormAsync(null);
        panel.EditRequested += site => _ = EnsureSiteFlows().SaveFormAsync(site);
        panel.TestRequested += site => _ = EnsureSiteFlows().TestSiteAsync(site);
        panel.RemoveRequested += site => _ = EnsureSiteFlows().RemoveAsync(site);
        panel.RememberRequested += site => _ = EnsureSiteFlows().RememberAsync(site);
        panel.ForgetCertificateRequested += site => _ = EnsureSiteFlows().ForgetCertificateAsync(site);
        SitesHomeHost.Children.Add(panel);
        _homePanel = panel;
        return panel;
    }

    private void ShowHome()
    {
        if (!SitesOffered)
        {
            SelectSidebarQuietly(_category);
            return;
        }

        LeaveProviderFull(selectCategory: false);
        if (_sitesShown) LeaveSites(selectCategory: false);
        var panel = EnsureHomePanel();
        if (!_homeShown) _scrollTrackBeforeSites = JobsScrollTrack.Visibility;
        _homeShown = true;
        panel.SetShown(true);
        SitesHomeHost.Visibility = Visibility.Visible;
        DownloadListViewport.Visibility = Visibility.Collapsed;
        JobsScrollTrack.Visibility = Visibility.Collapsed;
        ProviderNoticeBar.IsOpen = false;
        UpdateSpeedGraphVisibility();
        SyncTorrentInfo();
        RefreshHome();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, panel.FocusAddress);
    }

    /// <summary>Back to the list; a remote opened from the page, or another category, takes the view next.</summary>
    private void HideHome()
    {
        if (!_homeShown) return;
        _homeShown = false;
        _homePanel?.SetShown(false);
        SitesHomeHost.Visibility = Visibility.Collapsed;
        DownloadListViewport.Visibility = Visibility.Visible;
        JobsScrollTrack.Visibility = _scrollTrackBeforeSites;
        UpdateSpeedGraphVisibility();
        SyncTorrentInfo();
        UpdateButtons();
        UpdateStatusText();
    }

    private void RefreshHome()
    {
        if (!_homeShown || _homePanel is not { } panel) return;
        panel.AddEnabled = SitesOffered;
        panel.Update(_sites, _siteTransfers);
        StatusText.Text = SitesHomeRules.FooterText(_sites.Count);
    }

    /// <summary>The page is asked for from Options: the sidebar entry takes the view.</summary>
    private void ShowRemotesPage()
    {
        // Options asked for it and stays in front: the main window comes up behind it, showing the page.
        Activate();
        if (SitesOffered)
            CategoryList.SelectedItem = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag as string == SitesHomeTag);
        _settingsWindow?.Activate();
    }

    private void OpenSiteFromHome(string siteId)
    {
        var item = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(entry => entry.Tag as string == SiteTag(siteId));
        if (item is not null) CategoryList.SelectedItem = item;
        else ShowSite(siteId);
    }

    /// <summary>
    /// "Open an address once": the Host holds it as a remote that is not saved; the pane shows its folders. It is sent with
    /// no password; the browse asks for one when the server wants it (<see cref="BrowseAskingAsync"/>). The Host holds at most
    /// eight: the oldest the window does not show is forgotten first.
    /// </summary>
    private async Task OpenAddressOnceAsync(string address)
    {
        if (_homePanel is not { } panel) return;
        try
        {
            if (!await ForgetOldestOnceAsync())
            {
                panel.SayMessage(SitesHomeRules.CloseOneFirst);
                return;
            }

            var result = await _remote.OpenSiteOnceAsync(new SiteOpenOnceRequest { Address = address });
            if (!result.Ok || result.Site is null)
            {
                panel.SayMessage(string.IsNullOrWhiteSpace(result.Message) ? "That address couldn't be opened." : result.Message);
                return;
            }

            panel.SayMessage(null);
            panel.ClearAddress();
            var at = _onceSites.FindIndex(site => site.Id == result.Site.Id);
            if (at >= 0) _onceSites[at] = result.Site;
            else _onceSites.Add(result.Site);
            ShowSite(result.Site.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            panel.SayMessage("That address couldn't be opened.");
        }
    }

    /// <summary>
    /// Forgets (<c>sites.close</c>) the oldest addresses opened once, so another fits under the Host's limit. One with
    /// batches that are not finished is never forgotten (that would cancel them). False when no room can be made.
    /// </summary>
    private async Task<bool> ForgetOldestOnceAsync()
    {
        // Oldest first: those opened elsewhere (the web UI) before the window's own, which are in the order opened.
        var open = _sites.Where(site => site.Unsaved && _onceSites.All(own => own.Id != site.Id)).Concat(_onceSites).ToList();
        if (SitesHomeRules.ForgetBeforeOpening(open, _sitesShown ? _shownSiteId : null, SitesHomeRules.BusyRemotes(_siteTransfers)) is not { } forget)
            return false;
        var names = new List<string>();
        foreach (var id in forget)
        {
            var closed = await _remote.CloseSiteAsync(id);
            if (!closed.Ok) continue; // remembered meanwhile: it is a saved remote now
            if (open.FirstOrDefault(site => site.Id == id) is { } gone) names.Add(gone.Name);
            _onceSites.RemoveAll(site => site.Id == id);
            _sites.RemoveAll(site => site.Id == id);
        }

        SyncSiteEntryPoints();
        // Said, not silent: the oldest addresses opened once were closed to make room.
        if (names.Count > 0) SetSitesStatus(SitesHomeRules.ClosedToMakeRoom(names));
        return true;
    }

    /// <summary>
    /// A browse of the pane. For a load the user started (<paramref name="byUser"/>: open, a folder, Retry, Refresh), an
    /// address opened once whose server wants a password asks for it, sends it with the remote's id and browses again, as
    /// often as the server refuses it. A load the window started itself never asks: the pane's trouble state has the button,
    /// and Cancel leaves it there too.
    /// </summary>
    private async Task<SiteBrowseResultDto> BrowseAskingAsync(SiteBrowseRequest request, bool byUser, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _remote.BrowseSiteAsync(request, cancellationToken);
            var site = FindSite(request.Id);
            if (!byUser || site is null || SitesHomeRules.AfterBrowse(result.Problem, site.Unsaved) != OnceStep.AskPassword) return result;
            if (!await AskOncePasswordAsync(site, result.Message)) return result;
        }
    }

    /// <summary>Asks for an address's password and sends it with its id (<c>sites.openOnce</c>). False when not sent.</summary>
    private async Task<bool> AskOncePasswordAsync(SiteDto site, string? why)
    {
        var who = string.IsNullOrEmpty(site.User) ? site.Host : $"{site.User}@{site.Host}";
        var password = await AskPasswordAsync(who, why);
        if (password is null) return false;
        var result = await _remote.OpenSiteOnceAsync(new SiteOpenOnceRequest { Id = site.Id, Password = password.Length == 0 ? null : password });
        password = null;
        if (!result.Ok || result.Site is null)
        {
            SetSitesStatus(string.IsNullOrWhiteSpace(result.Message) ? "The password couldn't be sent." : result.Message);
            return false;
        }

        var at = _onceSites.FindIndex(once => once.Id == result.Site.Id);
        if (at >= 0) _onceSites[at] = result.Site;
        var listed = _sites.FindIndex(known => known.Id == result.Site.Id);
        if (listed >= 0) _sites[listed] = result.Site;
        return true;
    }

    /// <summary>
    /// SITES.11 R4: the button beside a problem in the pane, and a transfer the Host refused for one. Sites being off opens
    /// Options; a password is asked for an address opened once; the rest test the remote, which asks about its key,
    /// certificate or sign-in. The pane then reads the folder again.
    /// </summary>
    private async Task FixSiteAsync(SiteDto site, string? problem)
    {
        try
        {
            if (problem == SiteProblems.Off)
            {
                OpenSitesOptionsHook();
                return;
            }

            if (SitesHomeRules.AfterBrowse(problem, site.Unsaved) == OnceStep.AskPassword)
            {
                if (!await AskOncePasswordAsync(site, null)) return;
            }
            else await EnsureSiteFlows().TestSiteAsync(FindSite(site.Id) ?? site);

            if (_sitesShown && _shownSiteId == site.Id) _sitesPanel?.Reload();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        {
            SetSitesStatus($"{site.Name} couldn't be checked. Try again.");
        }
    }

    private async Task<string?> AskPasswordAsync(string who, string? why = null)
    {
        if (Content is not FrameworkElement { XamlRoot: { } xamlRoot } root) return null;
        var box = new PasswordBox { Header = "Password", PasswordRevealMode = PasswordRevealMode.Hidden };
        AutomationProperties.SetName(box, $"Password for {who}");
        var body = new StackPanel { Spacing = 8 };
        // The server's reason, after a refusal ("wrong password").
        if (!string.IsNullOrWhiteSpace(why)) body.Children.Add(new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        body.Children.Add(SettingsWindow.Hint("It goes only to Inbound's background program, which keeps it in memory while the address is open."));
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = $"Password for {who}",
            Content = DialogLayout.ScrollingContent(body, xamlRoot),
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var answer = await DialogGuard.ShowAsync(dialog);
        var typed = box.Password;
        box.Password = string.Empty;
        return answer == ContentDialogResult.Primary ? typed : null;
    }

    // ---- Sidebar entries ----------------------------------------------------------------------------------

    private const string SiteEntryXaml =
        "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ColumnSpacing=\"8\" RowSpacing=\"2\">" +
        "<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"Auto\" /></Grid.ColumnDefinitions>" +
        "<Grid.RowDefinitions><RowDefinition Height=\"Auto\" /><RowDefinition Height=\"Auto\" /></Grid.RowDefinitions>" +
        "<Grid Width=\"8\" Height=\"8\" VerticalAlignment=\"Center\">" +
        "<Ellipse x:Name=\"DotOk\" Width=\"8\" Height=\"8\" Fill=\"{ThemeResource InboundStatusDoneBrush}\" />" +
        "<Ellipse x:Name=\"DotWarn\" Width=\"8\" Height=\"8\" Fill=\"{ThemeResource InboundStatusWaitingBrush}\" Visibility=\"Collapsed\" /></Grid>" +
        "<TextBlock x:Name=\"NameText\" Grid.Column=\"1\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\" />" +
        "<StackPanel Grid.Column=\"2\" Orientation=\"Horizontal\" Spacing=\"6\" VerticalAlignment=\"Center\">" +
        "<TextBlock x:Name=\"DownText\" FontSize=\"11\" Foreground=\"{ThemeResource InboundProgressFillBrush}\" />" +
        "<TextBlock x:Name=\"UpText\" FontSize=\"11\" Foreground=\"{ThemeResource InboundUploadBrush}\" />" +
        // The clock is a Segoe Fluent glyph in the waiting colour, not the colour emoji the font fallback draws.
        "<StackPanel x:Name=\"Wait\" Orientation=\"Horizontal\" Spacing=\"2\" VerticalAlignment=\"Center\" Visibility=\"Collapsed\">" +
        "<FontIcon Glyph=\"&#xE823;\" FontSize=\"11\" Foreground=\"{ThemeResource InboundStatusWaitingBrush}\" VerticalAlignment=\"Center\" />" +
        "<TextBlock x:Name=\"WaitText\" FontSize=\"11\" Foreground=\"{ThemeResource InboundStatusWaitingBrush}\" VerticalAlignment=\"Center\" /></StackPanel>" +
        "<TextBlock x:Name=\"KindText\" FontSize=\"11\" Foreground=\"{ThemeResource InboundSecondaryTextBrush}\" /></StackPanel>" +
        // A plain two-part line rather than a ProgressBar, whose template keeps a minimum height and drew nothing at 2 px:
        // the fill's share of the track is the progress.
        "<Grid x:Name=\"Line\" Grid.Row=\"1\" Grid.ColumnSpan=\"3\" Height=\"2\" Margin=\"0,2,0,0\" CornerRadius=\"1\" Visibility=\"Collapsed\" " +
        "Background=\"{ThemeResource InboundDividerBrush}\">" +
        "<Grid.ColumnDefinitions><ColumnDefinition Width=\"0*\" /><ColumnDefinition Width=\"100*\" /></Grid.ColumnDefinitions>" +
        "<Border Background=\"{ThemeResource InboundProgressFillBrush}\" CornerRadius=\"1\" /></Grid></Grid>";

    private static Grid BuildSiteEntry() => (Grid)XamlReader.Load(SiteEntryXaml);

    /// <summary>
    /// The entry as drawn: a dot (a warning colour in trouble), the name, arrows with counts while files move, a clock with a
    /// count while batches wait, the kind in small grey text when idle, and a thin progress line under it while they run.
    /// </summary>
    private void UpdateSiteEntry(ListBoxItem item, SiteDto site)
    {
        if (item.Content is not Grid grid)
        {
            grid = BuildSiteEntry();
            item.Content = grid;
        }

        var badge = SitesHomeRules.Badge(_siteTransfers, site.Id);
        var waiting = SitesHomeRules.WaitingCount(badge);
        var busy = badge.Down + badge.Up > 0 || waiting.Length > 0;
        var trouble = SitesHomeRules.InTrouble(site);
        void Set(string name, string text)
        {
            if (grid.FindName(name) is TextBlock block && block.Text != text) block.Text = text;
        }

        Set("NameText", site.Name);
        // Down and up are two blocks so each has its colour.
        Set("DownText", badge.Down > 0 ? $"↓{badge.Down}" : "");
        Set("UpText", badge.Up > 0 ? $"↑{badge.Up}" : "");
        Set("WaitText", waiting);
        if (grid.FindName("Wait") is UIElement wait) wait.Visibility = waiting.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Set("KindText", busy ? "" : SitesHomeRules.KindText(site.Kind));
        if (grid.FindName("DotOk") is UIElement ok) ok.Visibility = trouble ? Visibility.Collapsed : Visibility.Visible;
        if (grid.FindName("DotWarn") is UIElement warn) warn.Visibility = trouble ? Visibility.Visible : Visibility.Collapsed;
        if (grid.FindName("Line") is Grid line && line.ColumnDefinitions.Count == 2)
        {
            var visibility = badge.Progress is null ? Visibility.Collapsed : Visibility.Visible;
            if (line.Visibility != visibility) line.Visibility = visibility;
            if (badge.Progress is { } value)
            {
                line.ColumnDefinitions[0].Width = new GridLength(value, GridUnitType.Star);
                line.ColumnDefinitions[1].Width = new GridLength(100 - value, GridUnitType.Star);
            }
        }

        var words = SitesHomeRules.ActivityWords(badge);
        var label = $"Remote {site.Name}, {SitesHomeRules.KindText(site.Kind)}, {SitesHomeRules.DotWords(site)}{(words is null ? "" : ", " + words)}";
        if (AutomationProperties.GetName(item) != label) AutomationProperties.SetName(item, label);
    }
}
