using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The Sites page of Options (remote-sites.md, "The switch"): the switch, the rclone Sites needs, and the saved sites with
/// their host-key trust. Everything here takes effect straight away, apart from OK and Cancel, because it is the Host's
/// state (<c>sites.*</c>) and not a window setting. The Host answers Sites to this PC's own window only, so the page is
/// given a client of this PC's Host, also while the window shows another server. Secrets are written in the site dialog
/// and are never shown here.
/// </summary>
internal sealed class SitesOptionsPage
{
    private readonly RemoteClient _client;
    private readonly SettingsWindow _window;
    private readonly CancellationTokenSource _closing = new();
    private readonly CheckBox _enable = new() { Content = "Use Sites" };
    private readonly StatusLine _switchResult = new("Sites switch result");
    private readonly TextBlock _rcloneLine = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _offerLine = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _checkedLine = SettingsWindow.Hint(string.Empty);
    private readonly Button _download = new() { Content = "Download rclone", MinWidth = 92 };
    private readonly Button _check = new() { Content = "Check for updates", MinWidth = 92 };
    private readonly Button _update = new() { Content = "Update", MinWidth = 92, Visibility = Visibility.Collapsed };
    private readonly StatusLine _rcloneResult = new("rclone result");
    private readonly CheckBox _auto = new() { Content = "Update rclone automatically" };
    private readonly TextBox _ownPath = new() { Header = "Use my own rclone", PlaceholderText = "Automatic: the rclone Inbound downloads", MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _browseOwn = new() { Content = "Browse…", MinWidth = 92, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _useOwn = new() { Content = "Use this path", MinWidth = 92, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel _siteList = new() { Spacing = 6 };
    private readonly TextBlock _noSites = SettingsWindow.Hint("No sites yet.");
    private readonly Button _add = new() { Content = "Add a site…", MinWidth = 92 };
    private readonly StatusLine _siteResult = new("Sites result");
    private readonly StatusLine _loadStatus = new("Sites settings");
    private readonly Button _tryAgain = new() { Content = "Try again", Visibility = Visibility.Collapsed };
    private readonly List<Control> _rcloneControls;

    private SitesStatusDto? _status;
    private List<SiteDto> _sites = [];
    private IReadOnlyList<SiteKindDto> _kinds = [];
    private bool _busy;
    private bool _rendering;

    /// <param name="client">A client of this PC's Host, not of a server the window may be showing.</param>
    /// <param name="serverName">The server the window shows, or null when it shows this PC.</param>
    public SitesOptionsPage(RemoteClient client, SettingsWindow window, string? serverName = null)
    {
        _client = client;
        _window = window;

        AutomationProperties.SetName(_enable, "Use Sites");
        AutomationProperties.SetName(_download, "Download rclone");
        AutomationProperties.SetName(_check, "Check for a newer rclone");
        AutomationProperties.SetName(_update, "Update rclone");
        AutomationProperties.SetName(_auto, "Update rclone automatically");
        AutomationProperties.SetName(_ownPath, "Path of your own rclone");
        AutomationProperties.SetName(_browseOwn, "Browse for your own rclone");
        AutomationProperties.SetName(_useOwn, "Use this rclone path");
        AutomationProperties.SetName(_add, "Add a site");
        AutomationProperties.SetName(_tryAgain, "Try reading the Sites settings again");
        AutomationProperties.SetName(_rcloneLine, "rclone in use");
        AutomationProperties.SetLiveSetting(_rcloneLine, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        _rcloneControls = [_download, _check, _update, _auto, _ownPath, _browseOwn, _useOwn];
        SetEnabledAll(false);
        _add.IsEnabled = false;
        // Until the Host has said whether Sites is on, the switch has nothing to change.
        _enable.IsEnabled = false;

        _enable.Checked += (_, _) => OnSwitch(true);
        _enable.Unchecked += (_, _) => OnSwitch(false);
        _download.Click += (_, _) => _ = InstallAsync(null);
        _update.Click += (_, _) => _ = InstallAsync(_status is null ? null : SitesRules.Rclone(_status, DateTimeOffset.Now).UpdateVersion);
        _check.Click += (_, _) => _ = CheckAsync();
        _auto.Checked += (_, _) => OnAuto();
        _auto.Unchecked += (_, _) => OnAuto();
        _browseOwn.Click += (_, _) => _ = BrowseOwnAsync();
        _useOwn.Click += (_, _) => _ = UseOwnAsync(_ownPath.Text.Trim());
        _ownPath.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter || !_useOwn.IsEnabled) return;
            args.Handled = true;
            _ = UseOwnAsync(_ownPath.Text.Trim());
        };
        _add.Click += (_, _) => _ = AddAsync();
        _tryAgain.Click += (_, _) => _ = LoadAsync();

        var ownRow = new Grid { ColumnSpacing = 8 };
        ownRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ownRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ownRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_ownPath, 0);
        Grid.SetColumn(_browseOwn, 1);
        Grid.SetColumn(_useOwn, 2);
        ownRow.Children.Add(_ownPath);
        ownRow.Children.Add(_browseOwn);
        ownRow.Children.Add(_useOwn);

        var rcloneButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rcloneButtons.Children.Add(_download);
        rcloneButtons.Children.Add(_check);
        rcloneButtons.Children.Add(_update);

        var controls = new List<UIElement>();
        if (serverName is not null)
            controls.Add(SettingsWindow.Hint($"Sites is for this PC and is set up here, whichever server the window shows. It never changes {serverName}."));
        controls.AddRange(
        [
            _loadStatus.Text,
            _tryAgain,
            SettingsWindow.Section("Sites"),
            _enable,
            SettingsWindow.Hint("Browse and move files on your own servers (SFTP today) through rclone. Off, nothing runs and rclone never starts."),
            _switchResult.Text,
            SettingsWindow.Section("rclone"),
            _rcloneLine,
            _offerLine,
            rcloneButtons,
            _rcloneResult.Text,
            _checkedLine,
            _auto,
            SettingsWindow.Hint("rclone is free software under the MIT licence (rclone.org). Inbound downloads it from downloads.rclone.org, about 32 MB, and checks it before using it. A new release is installed a week after it comes out."),
            ownRow,
            SettingsWindow.Hint("Empty uses the rclone Inbound downloads. Inbound never updates your own."),
            SettingsWindow.Section("Saved sites"),
            _noSites,
            _siteList,
            _add,
            _siteResult.Text,
        ]);
        Controls = controls.ToArray();

        _client.SitesChanged += OnChanged;
        _ = LoadAsync();
    }

    public UIElement[] Controls { get; }

    public void Close()
    {
        _client.SitesChanged -= OnChanged;
        _closing.Cancel();
    }

    // ---- Loading and showing ---------------------------------------------------------------

    private async Task LoadAsync()
    {
        _tryAgain.Visibility = Visibility.Collapsed;
        _loadStatus.Show("Reading the Sites settings…");
        try
        {
            var status = await _client.GetSitesAsync(_closing.Token);
            if (status.Value is null)
            {
                _loadStatus.Show($"Couldn't read the Sites settings. {RemoteText.Failure(status.Error)}", isProblem: true);
                _tryAgain.Visibility = Visibility.Visible;
                return;
            }

            _status = status.Value;
            var kinds = await _client.GetSiteKindsAsync(_closing.Token);
            if (kinds.Value is { } known) _kinds = known.Kinds;
            await LoadSitesAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _loadStatus.Show(string.Empty);
        Render();
    }

    private async Task LoadSitesAsync()
    {
        var list = await _client.ListSitesAsync(_closing.Token);
        if (list.Value is { } sites) _sites = sites.Sites;
    }

    private void OnChanged(SitesChangedDto change)
    {
        if (_closing.IsCancellationRequested) return;
        _status = change.Status;
        _sites = change.Sites;
        Render();
    }

    private void SetEnabledAll(bool enabled)
    {
        foreach (var control in _rcloneControls) control.IsEnabled = enabled;
    }

    private void Render()
    {
        if (_status is not { } status) return;
        _rendering = true;
        try
        {
            _enable.IsEnabled = !_busy;
            _enable.IsChecked = status.Enabled;
            _auto.IsChecked = status.AutoUpdateRclone;
            if (_ownPath.FocusState == FocusState.Unfocused) _ownPath.Text = status.OwnRclonePath ?? string.Empty;

            var view = SitesRules.Rclone(status, DateTimeOffset.Now, busy: _busy && _installing);
            _rcloneLine.Text = view.Line;
            if (view.LineIsProblem) _rcloneLine.Foreground = InboundTheme.Brush("InboundErrorTextBrush", _rcloneLine);
            else _rcloneLine.ClearValue(TextBlock.ForegroundProperty);
            _offerLine.Text = view.OfferLine ?? string.Empty;
            _offerLine.Visibility = view.OfferLine is null ? Visibility.Collapsed : Visibility.Visible;
            _checkedLine.Text = view.LastCheckLine ?? string.Empty;
            _checkedLine.Visibility = view.LastCheckLine is null ? Visibility.Collapsed : Visibility.Visible;
            _download.Visibility = view.ShowDownload ? Visibility.Visible : Visibility.Collapsed;
            _download.IsEnabled = view.EnableDownload && !_busy;
            _check.IsEnabled = view.EnableCheck && !_busy;
            _update.Visibility = view.ShowUpdate ? Visibility.Visible : Visibility.Collapsed;
            _update.IsEnabled = view.EnableUpdate && !_busy;
            _update.Content = view.UpdateVersion is null ? "Update" : $"Update to {view.UpdateVersion}";
            _auto.IsEnabled = view.EnableAutoUpdate && !_busy;
            _ownPath.IsEnabled = _browseOwn.IsEnabled = _useOwn.IsEnabled = status.Enabled && !_busy && !status.RcloneInstalling;

            _add.IsEnabled = status.Enabled && !_busy;
            RenderSites(status.Enabled);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderSites(bool enabled)
    {
        _siteList.Children.Clear();
        // With Sites off the Host lists none, though the saved sites stay.
        _noSites.Text = enabled ? "No sites yet." : "Turn Sites on to see your saved sites.";
        _noSites.Visibility = _sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var site in _sites)
        {
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = site.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(SettingsWindow.Hint(SitesRules.Summary(site)));
            if (SitesRules.NeedsTrust(site)) text.Children.Add(SettingsWindow.Hint("The server's key isn't trusted yet. Test the site to check it."));

            var test = new Button { Content = "Test", MinWidth = 64, IsEnabled = enabled && !_busy };
            var edit = new Button { Content = "Edit…", MinWidth = 64, IsEnabled = enabled && !_busy };
            var remove = new Button { Content = "Remove…", MinWidth = 64, IsEnabled = enabled && !_busy };
            AutomationProperties.SetName(test, $"Test {site.Name}");
            AutomationProperties.SetName(edit, $"Edit {site.Name}");
            AutomationProperties.SetName(remove, $"Remove {site.Name}");
            var captured = site;
            test.Click += (_, _) => _ = TestSiteAsync(captured);
            edit.Click += (_, _) => _ = EditAsync(captured);
            remove.Click += (_, _) => _ = RemoveAsync(captured);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(test);
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(buttons, 1);
            row.Children.Add(text);
            row.Children.Add(buttons);
            _siteList.Children.Add(InboundUi.Card(row, new Thickness(12, 8, 12, 8)));
        }
    }

    // ---- The switch and rclone ---------------------------------------------------------------

    private bool _installing;

    private void OnSwitch(bool on)
    {
        if (_rendering || _status is null) return;
        _ = SwitchAsync(on);
    }

    private async Task SwitchAsync(bool on)
    {
        if (_status is null) return;
        await RunAsync(async () =>
        {
            var reply = await _client.SetSitesAsync(new SitesSetEnabledRequest { Enabled = on }, _closing.Token);
            if (reply.Value is null)
            {
                _switchResult.Say(RemoteText.Failure(reply.Error), isProblem: true);
                return;
            }

            _status = reply.Value;
            _switchResult.Say(string.IsNullOrWhiteSpace(reply.Value.Error) ? string.Empty : reply.Value.Error.Trim(), isProblem: !string.IsNullOrWhiteSpace(reply.Value.Error));
            if (on) await LoadSitesAsync();
        });
        if (on && _status is { Enabled: true } now && SitesRules.RcloneMissing(now) && string.IsNullOrWhiteSpace(now.OwnRclonePath) && !now.RcloneInstalling)
            await OfferDownloadAsync();
    }

    private async Task OfferDownloadAsync()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "Sites needs rclone, which isn't on this PC. Download it now? It is about 32 MB, from downloads.rclone.org, and is checked before it is used.", TextWrapping = TextWrapping.Wrap });
        if (await AskAsync("Download rclone?", body, "Download", "Not now") == ContentDialogResult.Primary) await InstallAsync(null);
    }

    private async Task InstallAsync(string? version)
    {
        if (_status is null) return;
        _installing = true;
        _rcloneResult.Say(string.Empty);
        await RunAsync(async () =>
        {
            var reply = await _client.InstallRcloneAsync(version, _closing.Token);
            if (reply.Value is null)
            {
                _rcloneResult.Say(RemoteText.Failure(reply.Error), isProblem: true);
                return;
            }

            _status = reply.Value;
            if (!string.IsNullOrWhiteSpace(reply.Value.Error)) _rcloneResult.Say(reply.Value.Error.Trim(), isProblem: true);
            else _rcloneResult.Say(reply.Value.RcloneVersion is { } v ? $"rclone {v} is installed." : "rclone is installed.");
        });
        _installing = false;
        Render();
    }

    private async Task CheckAsync()
    {
        await RunAsync(async () =>
        {
            _rcloneResult.Say("Checking for a newer rclone…");
            var reply = await _client.CheckRcloneAsync(_closing.Token);
            if (reply.Value is null)
            {
                _rcloneResult.Say(RemoteText.Failure(reply.Error), isProblem: true);
                return;
            }

            _status = reply.Value;
            if (!string.IsNullOrWhiteSpace(reply.Value.RcloneUpdateError)) _rcloneResult.Say(string.Empty);
            else if (!string.IsNullOrWhiteSpace(reply.Value.Error)) _rcloneResult.Say(reply.Value.Error.Trim(), isProblem: true);
            else _rcloneResult.Say(reply.Value.RcloneUpdate is null ? "rclone is up to date." : string.Empty);
        });
    }

    private void OnAuto()
    {
        if (_rendering || _status is null) return;
        var on = _auto.IsChecked == true;
        _ = RunAsync(async () =>
        {
            var reply = await _client.SetSitesAsync(new SitesSetEnabledRequest { Enabled = _status.Enabled, AutoUpdateRclone = on }, _closing.Token);
            if (reply.Value is null) _rcloneResult.Say(RemoteText.Failure(reply.Error), isProblem: true);
            else _status = reply.Value;
        });
    }

    private async Task BrowseOwnAsync()
    {
        try
        {
            IReadOnlyList<string> files = await Pickers.PickFilesAsync(WinRT.Interop.WindowNative.GetWindowHandle(_window),
                OperatingSystem.IsWindows() ? [".exe"] : ["*"], multiple: false, PickerLocationId.ComputerFolder);
            if (files is not [var file]) return;
            _ownPath.Text = file;
            await UseOwnAsync(file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            _rcloneResult.Say("The file picker couldn't open. Type the path instead.", isProblem: true);
        }
    }

    private async Task UseOwnAsync(string path)
    {
        if (_status is null) return;
        await RunAsync(async () =>
        {
            var reply = await _client.SetSitesAsync(new SitesSetEnabledRequest { Enabled = _status.Enabled, OwnRclonePath = path }, _closing.Token);
            if (reply.Value is null)
            {
                _rcloneResult.Say(RemoteText.Failure(reply.Error), isProblem: true);
                return;
            }

            _status = reply.Value;
            _ownPath.Text = reply.Value.OwnRclonePath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(reply.Value.Error)) _rcloneResult.Say(reply.Value.Error.Trim(), isProblem: true);
            else _rcloneResult.Say(path.Length == 0 ? "Using the rclone Inbound downloads." : "Using your own rclone.");
        });
    }

    // ---- Sites ----------------------------------------------------------------------------------

    private async Task AddAsync() => await SaveFormAsync(null);

    private async Task EditAsync(SiteDto site) => await SaveFormAsync(site);

    private async Task SaveFormAsync(SiteDto? existing)
    {
        if (_busy || _status is not { Enabled: true }) return;
        SiteDto? saved;
        try
        {
            saved = await SiteDialog.ShowAsync(_window, _kinds, existing, async (request, cancel) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, cancel);
                return await _client.SaveSiteAsync(request, linked.Token);
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (saved is null) return;
        var index = _sites.FindIndex(s => s.Id == saved.Id);
        if (index >= 0) _sites[index] = saved;
        else _sites.Add(saved);
        Render();
        _siteResult.Say($"{saved.Name} is saved.");

        // A new server, or a changed host or port, has no trusted key yet: ask now, then try the site.
        if (SitesRules.NeedsTrust(saved)) await TestSiteAsync(saved);
    }

    private async Task RemoveAsync(SiteDto site)
    {
        var body = new TextBlock
        {
            Text = $"Remove {site.Name}? Its saved password or key and the key you trusted are deleted from this PC. Nothing on the server is touched.",
            TextWrapping = TextWrapping.Wrap,
        };
        if (await AskAsync($"Remove {site.Name}?", body, "Remove", "Cancel", danger: true, defaultClose: true) != ContentDialogResult.Primary) return;
        await RunAsync(async () =>
        {
            var result = await _client.RemoveSiteAsync(site.Id, _closing.Token);
            if (!result.Ok)
            {
                _siteResult.Say(string.IsNullOrWhiteSpace(result.Message) ? $"{site.Name} couldn't be removed." : result.Message, isProblem: true);
                return;
            }

            _sites.RemoveAll(s => s.Id == site.Id);
            _siteResult.Say($"{site.Name} is removed.");
        });
    }

    private async Task TestSiteAsync(SiteDto site)
    {
        // Twice at most: once, and again after the key was trusted or replaced.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            SiteResultDto? result = null;
            await RunAsync(async () =>
            {
                _siteResult.Say($"Testing {site.Name}…");
                result = await _client.TestSiteAsync(site.Id, _closing.Token);
            });
            if (result is null) return;

            if (result.Problem == SiteProblems.HostKeyNotAccepted)
            {
                if (attempt == 0 && await TrustAsync(site)) continue;
                SayTestResult($"{site.Name} wasn't tested, because its server's key isn't trusted.", isProblem: true);
                return;
            }

            if (result.Problem == SiteProblems.HostKeyChanged)
            {
                if (attempt == 0 && await ReplaceKeyAsync(site, result)) continue;
                SayTestResult($"{site.Name} wasn't used: its server's key is not the one you trusted.", isProblem: true);
                return;
            }

            if (result.Ok) SayTestResult($"{site.Name}: connected, and its start folder was listed.");
            else SayTestResult($"{site.Name}: {(string.IsNullOrWhiteSpace(result.Message) ? "the test failed." : result.Message)}", isProblem: true);
            return;
        }
    }

    /// <summary>
    /// A test's outcome, scrolled into view: the line sits under the sites list, which can be below the fold of the page
    /// when the Test button was pressed.
    /// </summary>
    private void SayTestResult(string text, bool isProblem = false)
    {
        _siteResult.Say(text, isProblem);
        _siteResult.Text.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_siteResult.Text.XamlRoot is not null) _siteResult.Text.StartBringIntoView();
        });
    }

    /// <summary>First use: reads the server's keys without signing in, shows them, and records them if the user trusts them.</summary>
    private async Task<bool> TrustAsync(SiteDto site)
    {
        SiteProbeResultDto? probe = null;
        await RunAsync(async () =>
        {
            _siteResult.Say($"Reading the key of {site.Host}…");
            probe = await _client.ProbeSiteAsync(site.Host, site.Port, _closing.Token);
        });
        if (probe is null) return false;
        if (!probe.Ok || probe.Keys.Count == 0)
        {
            _siteResult.Say($"Couldn't read the key of {site.Host}. {(string.IsNullOrWhiteSpace(probe.Message) ? "Check the server name and the port." : probe.Message)}", isProblem: true);
            return false;
        }

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"{site.Host}{(site.Port == 22 ? "" : ":" + site.Port)} identified itself with the key below. Inbound will connect to this site only if the server shows this key again.",
            TextWrapping = TextWrapping.Wrap,
        });
        AddKeys(body, probe.Keys);
        body.Children.Add(SettingsWindow.Hint("Compare it with the fingerprint your server's administrator gave you, or with the output of ssh-keygen -lf on the server's host key file."));
        if (await AskAsync("Trust this key?", body, "Trust", "Cancel", defaultClose: true) != ContentDialogResult.Primary) return false;
        return await SendTrustAsync(site, probe.Keys);
    }

    /// <summary>A key that is not the trusted one: a hard warning, and a second question before the old key is replaced.</summary>
    private async Task<bool> ReplaceKeyAsync(SiteDto site, SiteResultDto result)
    {
        var warning = new StackPanel { Spacing = 8 };
        warning.Children.Add(new TextBlock
        {
            Text = $"The server {site.Host} now shows a different key from the one you trusted. Someone may be intercepting the connection, or the server may have been reinstalled. Inbound won't sign in with your password or key.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        warning.Children.Add(Mono("Shown now", result.OfferedFingerprint ?? "unknown"));
        foreach (var accepted in result.AcceptedFingerprints) warning.Children.Add(Mono("Trusted", accepted));
        warning.Children.Add(SettingsWindow.Hint("Replace the trusted key only if you or the server's administrator changed the server's keys."));
        if (await AskAsync("The server's key has changed", warning, "Replace the key…", "Cancel", danger: true, defaultClose: true) != ContentDialogResult.Primary)
            return false;

        SiteProbeResultDto? probe = null;
        await RunAsync(async () => probe = await _client.ProbeSiteAsync(site.Host, site.Port, _closing.Token));
        if (probe is null) return false;
        if (!probe.Ok || !SitesRules.OffersFingerprint(probe.Keys, result.OfferedFingerprint))
        {
            _siteResult.Say(probe.Ok ? "The server showed another key just now. Nothing was changed. Test the site again." : $"Couldn't read the key of {site.Host}. {probe.Message}", isProblem: true);
            return false;
        }

        var confirm = new StackPanel { Spacing = 8 };
        confirm.Children.Add(new TextBlock
        {
            Text = "Are you sure? The key you trusted before is forgotten, and these keys are trusted instead.",
            TextWrapping = TextWrapping.Wrap,
        });
        AddKeys(confirm, probe.Keys);
        if (await AskAsync("Replace the trusted key?", confirm, "Replace the key", "Keep the old key", danger: true, defaultClose: true) != ContentDialogResult.Primary)
            return false;
        return await SendTrustAsync(site, probe.Keys);
    }

    private async Task<bool> SendTrustAsync(SiteDto site, IEnumerable<SiteHostKeyDto> keys)
    {
        SiteResultDto? trusted = null;
        await RunAsync(async () => trusted = await _client.TrustSiteAsync(site.Id, keys.Select(key => key.Entry), _closing.Token));
        if (trusted is null) return false;
        if (!trusted.Ok)
        {
            _siteResult.Say(string.IsNullOrWhiteSpace(trusted.Message) ? "The key couldn't be saved." : trusted.Message, isProblem: true);
            return false;
        }

        if (trusted.Site is { } updated)
        {
            var index = _sites.FindIndex(s => s.Id == updated.Id);
            if (index >= 0) _sites[index] = updated;
            Render();
        }

        return true;
    }

    private static void AddKeys(Panel panel, IEnumerable<SiteHostKeyDto> keys)
    {
        foreach (var key in keys) panel.Children.Add(Mono(key.Type, key.Fingerprint));
    }

    private static TextBlock Mono(string label, string value) => new()
    {
        Text = $"{label}\n{value}",
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Menlo, monospace"),
    };

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>Runs one call with the page's buttons off, and shows the page again after. A closing window ends it quietly.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        Render();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _busy = false;
            if (!_closing.IsCancellationRequested) Render();
        }
    }

    private async Task<ContentDialogResult?> AskAsync(string title, UIElement body, string primary, string close, bool danger = false, bool defaultClose = false)
    {
        if (_window.Content is not FrameworkElement root || root.XamlRoot is null) return null;
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = title,
            Content = DialogLayout.ScrollingContent(body, root.XamlRoot),
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = defaultClose ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (danger) dialog.PrimaryButtonStyle = InboundUi.DangerDialogButton;
        return await DialogGuard.ShowAsync(dialog);
    }
}
