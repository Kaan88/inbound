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
internal sealed partial class SitesOptionsPage
{
    private readonly RemoteClient _client;
    private readonly SettingsWindow _window;
    private readonly CancellationTokenSource _closing = new();
    private readonly CheckBox _enable = new() { Content = "Use Remote files" };
    private readonly StatusLine _switchResult = new("Remote files switch result");
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
    private readonly HyperlinkButton _manage = new() { Content = "Manage remotes in All remotes", Padding = new Thickness(0) };
    private readonly StatusLine _loadStatus = new("Remote files settings");
    private readonly Button _tryAgain = new() { Content = "Try again", Visibility = Visibility.Collapsed };
    private readonly List<Control> _rcloneControls;

    private SitesStatusDto? _status;
    private bool _busy;
    private bool _rendering;

    /// <param name="client">A client of this PC's Host, not of a server the window may be showing.</param>
    /// <param name="serverName">The server the window shows, or null when it shows this PC.</param>
    public SitesOptionsPage(RemoteClient client, SettingsWindow window, string? serverName = null)
    {
        _client = client;
        _window = window;

        AutomationProperties.SetName(_enable, "Use Remote files");
        AutomationProperties.SetName(_download, "Download rclone");
        AutomationProperties.SetName(_check, "Check for a newer rclone");
        AutomationProperties.SetName(_update, "Update rclone");
        AutomationProperties.SetName(_auto, "Update rclone automatically");
        AutomationProperties.SetName(_ownPath, "Path of your own rclone");
        AutomationProperties.SetName(_browseOwn, "Browse for your own rclone");
        AutomationProperties.SetName(_useOwn, "Use this rclone path");
        AutomationProperties.SetName(_manage, "Manage remotes in All remotes");
        AutomationProperties.SetName(_tryAgain, "Try reading the Remote files settings again");
        AutomationProperties.SetName(_rcloneLine, "rclone in use");
        AutomationProperties.SetLiveSetting(_rcloneLine, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        _rcloneControls = [_download, _check, _update, _auto, _ownPath, _browseOwn, _useOwn];
        SetEnabledAll(false);
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
        _manage.Click += (_, _) => ManageRemotes?.Invoke();
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
            controls.Add(SettingsWindow.Hint($"Remote files is for this PC and is set up here, whichever server the window shows. It never changes {serverName}."));
        controls.AddRange(
        [
            _loadStatus.Text,
            _tryAgain,
            SettingsWindow.Section("Remote files"),
            _enable,
            SettingsWindow.Hint("Browse and move files on your own remotes (SFTP, FTP, WebDAV, S3 and cloud storage) through rclone. Off, nothing runs and rclone never starts."),
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
            SettingsWindow.Section("Remotes"),
            _manage,
        ]);
        Controls = controls.ToArray();
        // The switch, auto-update and the rclone path are the Host's state and take effect at once: OK has nothing to save.
        window.AppliesAtOnce(Controls);

        _client.SitesChanged += OnChanged;
        _ = LoadAsync();
    }

    public UIElement[] Controls { get; }

    /// <summary>
    /// Takes the user to the All remotes page, where the remotes are listed, added, edited and removed. Options closes
    /// (without saving) unless it has changes that are not saved; then it stays open, in front.
    /// </summary>
    public Action? ManageRemotes { get; set; }

    public void Close()
    {
        _client.SitesChanged -= OnChanged;
        _closing.Cancel();
    }

    // ---- Loading and showing ---------------------------------------------------------------

    private async Task LoadAsync()
    {
        _tryAgain.Visibility = Visibility.Collapsed;
        _loadStatus.Show("Reading the Remote files settings…");
        try
        {
            var status = await _client.GetSitesAsync(_closing.Token);
            if (status.Value is null)
            {
                _loadStatus.Show($"Couldn't read the Remote files settings. {RemoteText.Failure(status.Error)}", isProblem: true);
                _tryAgain.Visibility = Visibility.Visible;
                return;
            }

            _status = status.Value;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _loadStatus.Show(string.Empty);
        Render();
    }

    private void OnChanged(SitesChangedDto change)
    {
        if (_closing.IsCancellationRequested) return;
        _status = change.Status;
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
        }
        finally
        {
            _rendering = false;
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
        });
        if (on && _status is { Enabled: true } now && SitesRules.RcloneMissing(now) && string.IsNullOrWhiteSpace(now.OwnRclonePath) && !now.RcloneInstalling)
            await OfferDownloadAsync();
    }

    private async Task OfferDownloadAsync()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "Remote files needs rclone, which isn't on this PC. Download it now? It is about 32 MB, from downloads.rclone.org, and is checked before it is used.", TextWrapping = TextWrapping.Wrap });
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
            if (!_closing.IsCancellationRequested)
            {
                Render();
            }
        }
    }

    private async Task<ContentDialogResult?> AskAsync(string title, UIElement body, string? primary, string close)
    {
        if (_window.Content is not FrameworkElement root || root.XamlRoot is null) return null;
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = title,
            Content = DialogLayout.ScrollingContent(body, root.XamlRoot),
            PrimaryButtonText = primary ?? string.Empty,
            CloseButtonText = close,
            DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await DialogGuard.ShowAsync(dialog);
    }
}
