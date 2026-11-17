using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R4: Add a remote and Edit remote. A new remote starts with tiles (the kinds the Host offers), then the short form
/// of that kind; an edit opens the same form, filled in. Each kind shows only its own fields (SITES.6); port, starting folder
/// and name sit in Advanced, closed. Test tries the form as typed without saving it (<c>sites.testDraft</c>), so Cancel leaves
/// nothing behind; a server key or certificate it meets is shown in the form with a Trust button (no second dialog), and is
/// trusted when the remote is saved. With an older Host, which tests saved remotes only, Test saves first, and Back or Cancel
/// remove the remote that save made. The dialog stays open, with the reason, when the Host or the checks refuse. The secrets
/// live only in its boxes until they are sent and are cleared after. In edit mode a secret box left empty keeps the stored secret.
/// </summary>
internal sealed class SiteDialog
{
    private static readonly SiteKindDto Sftp = new() { Kind = "sftp", Title = "SFTP", DefaultPort = 22, AuthMethods = ["password", "key"] };

    private readonly Window _window;
    private SiteDto? _existing;
    // The dialog was opened on a saved remote (Edit remote). _existing can also be a remote an older Host's Test saved.
    private readonly bool _editing;
    private readonly SiteDialogCalls _calls;
    // An older Host's Test saved this new remote; Back and Cancel remove it.
    private string? _createdByTest;
    // Accepted in the form for this test and the save; dropped when the form points elsewhere.
    private List<SiteHostKeyDto> _acceptedKeys = [];
    private string? _certificateToTrust;
    private (string Host, string Port, string Url, string Endpoint, string? Tls) _trustedFor;
    private List<SiteHostKeyDto> _offeredKeys = [];
    private SiteCertificateDto? _offeredCertificate;
    private readonly Border _trustPanel = new() { Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1), Padding = new Thickness(12), CornerRadius = new CornerRadius(4) };
    private readonly StackPanel _trustBody = new() { Spacing = 10 };
    private readonly StackPanel _trustLines = new() { Spacing = 10 };
    private readonly Button _trustButton = new() { Content = "Trust", MinWidth = 92 };
    private readonly IReadOnlyList<SiteKindDto> _allKinds;
    private IReadOnlyList<SiteKindDto> _kinds;
    private SiteKindDto _kind;
    private readonly ComboBox _kindBox = new() { Header = "Kind", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "The server's name" };
    private readonly Grid _hostUser = new() { ColumnSpacing = 12 };
    private readonly StackPanel _userSolo = new();
    private readonly ComboBox _tls = new() { Header = "Encryption", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _url = new() { Header = "Address", PlaceholderText = "https://files.example.com/remote.php/dav/files/me" };
    private readonly ComboBox _vendor = new() { Header = "Server type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _provider = new() { Header = "Service", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _endpoint = new() { Header = "Service address (endpoint)", PlaceholderText = "https://s3.example.com" };
    private readonly TextBox _region = new() { Header = "Region (optional)", PlaceholderText = "eu-central-1" };
    // SITES.6c: an own app registration, for those who don't want rclone's shared one.
    private readonly TextBox _clientId = new() { Header = "Your own client ID (optional)" };
    private readonly PasswordBox _clientSecret = new() { Header = "Its client secret (optional)", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBlock _plainWarning = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _note = SettingsWindow.Hint(string.Empty);
    private readonly TextBlock _keyHint = SettingsWindow.Hint("Changing the server or the port asks about the server's key again.");
    private readonly TextBox _host = new() { Header = "Server", PlaceholderText = "files.example.com" };
    private readonly TextBox _port = new() { Header = "Port", Width = 96, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _user = new() { Header = "User" };
    // SITES.13: a speed limit per remote, in KB/s; 0 is no limit. Under Advanced.
    private readonly NumberBox _downLimit = new() { Header = "Download limit (KB/s, 0 = none)", Minimum = 0, Maximum = 10_000_000, SmallChange = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 190 };
    private readonly NumberBox _upLimit = new() { Header = "Upload limit (KB/s, 0 = none)", Minimum = 0, Maximum = 10_000_000, SmallChange = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 190 };
    private readonly SegmentedChoice _auth = new("Sign in with");
    private readonly PasswordBox _password = new() { Header = "Password", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBox _keyPath = new() { Header = "Key file (OpenSSH or PuTTY .ppk)", PlaceholderText = @"C:\Users\you\.ssh\id_ed25519" };
    private readonly Button _chooseKey = new() { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom, MinWidth = 92 };
    private readonly Grid _keyRow = new() { ColumnSpacing = 8 };
    private readonly PasswordBox _passphrase = new() { Header = "Key passphrase (if it has one)", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly CheckBox _removePassphrase = new() { Content = "Remove the saved passphrase" };
    private readonly TextBox _remoteFolder = new() { Header = "Starting folder on the server (optional)", PlaceholderText = "/" };
    private readonly TextBox _localFolder = new() { Header = "Folder on this PC that goes with it (optional)" };
    private readonly Button _browseLocal = new() { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom, MinWidth = 92 };
    private readonly Grid _localRow = new() { ColumnSpacing = 8 };
    private readonly Expander _advanced = new() { IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _advancedBody = new() { Spacing = 10 };
    private readonly Button _testButton = new() { Content = "Test", MinWidth = 92 };
    private readonly TextBlock _testText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _testIcon = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _testLine = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(4) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, IsTextSelectionEnabled = true };
    private readonly StackPanel _form = new() { Spacing = 10, MinWidth = 480 };
    private readonly Grid _tileGrid = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly StackPanel _tiles = new() { Spacing = 10 };
    private readonly List<(SiteTile Tile, Button Button)> _tileButtons = [];
    private SiteTile? _chosen;
    private ContentDialog? _dialog;
    private string[] _methods = ["password"];
    private bool _applying;
    private bool _testing;

    private SiteDialog(Window window, IReadOnlyList<SiteKindDto> kinds, SiteKindDto kind, SiteDto? existing, SiteDialogCalls calls)
    {
        _window = window;
        _allKinds = kinds;
        _kinds = kinds;
        _kind = kind;
        _existing = existing;
        _editing = existing is not null;
        _calls = calls;

        _name.Text = existing?.Name ?? string.Empty;
        _host.Text = existing?.Host ?? string.Empty;
        _port.Text = (existing?.Port is > 0 ? existing.Port : kind.DefaultPort > 0 ? kind.DefaultPort : 22).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _user.Text = existing?.User ?? string.Empty;
        _downLimit.Value = existing?.DownLimit ?? 0;
        _upLimit.Value = existing?.UpLimit ?? 0;
        _clientId.Text = existing?.ClientId ?? string.Empty;
        if (existing?.HasClientSecret == true) _clientSecret.PlaceholderText = "Saved; type to replace";
        _url.Text = existing?.Url ?? string.Empty;
        _endpoint.Text = existing?.Endpoint ?? string.Empty;
        _region.Text = existing?.Region ?? string.Empty;
        _remoteFolder.Text = existing?.RemoteFolder ?? string.Empty;
        _localFolder.Text = existing?.LocalFolder ?? string.Empty;

        BuildForm();
        BindKinds(kinds);
        _kindBox.SelectedIndex = Math.Max(0, kinds.ToList().FindIndex(k => k.Kind == kind.Kind));
        ApplyKind(existing);

        if (existing is not null)
        {
            _password.PlaceholderText = existing.HasPassword ? "Saved. Leave empty to keep it." : string.Empty;
            _keyPath.PlaceholderText = existing.HasPrivateKey ? "A key is saved. Browse to replace it." : _keyPath.PlaceholderText;
            _passphrase.PlaceholderText = existing.HasKeyPassphrase ? "Saved. Leave empty to keep it." : string.Empty;
        }

        AutomationProperties.SetName(_kindBox, "Kind of remote");
        AutomationProperties.SetName(_tls, "Encryption");
        AutomationProperties.SetName(_url, "Address");
        AutomationProperties.SetName(_vendor, "Server type");
        AutomationProperties.SetName(_provider, "Service");
        AutomationProperties.SetName(_endpoint, "Service address");
        AutomationProperties.SetName(_region, "Region");
        AutomationProperties.SetName(_name, "Remote name");
        AutomationProperties.SetName(_host, "Server");
        AutomationProperties.SetName(_port, "Port");
        AutomationProperties.SetName(_user, "User");
        AutomationProperties.SetName(_auth, "Sign in with");
        AutomationProperties.SetName(_keyPath, "Key file");
        AutomationProperties.SetName(_chooseKey, "Browse for a key file");
        AutomationProperties.SetName(_passphrase, "Key passphrase");
        AutomationProperties.SetName(_removePassphrase, "Remove the saved passphrase");
        AutomationProperties.SetName(_remoteFolder, "Starting folder on the server");
        AutomationProperties.SetName(_localFolder, "Folder on this PC that goes with it");
        AutomationProperties.SetName(_browseLocal, "Browse for the folder on this PC");
        AutomationProperties.SetName(_advanced, "Advanced");
        AutomationProperties.SetName(_testButton, "Test the remote");
        AutomationProperties.SetName(_trustButton, "Trust what the server showed");
        AutomationProperties.SetName(_testText, "Test result");
        AutomationProperties.SetLiveSetting(_testText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

        _auth.SelectionChanged += (_, _) => ShowAuth();
        _kindBox.SelectionChanged += (_, _) =>
        {
            if (_applying || _kindBox.SelectedIndex < 0 || _kindBox.SelectedIndex >= _kinds.Count || _kinds[_kindBox.SelectedIndex].Kind == _kind.Kind) return;
            _kind = _kinds[_kindBox.SelectedIndex];
            // A port still at a kind's usual one (or empty) follows the new kind's, through WebDAV and S3 too, which have none.
            if (_kind.DefaultPort > 0 && _port.Text.Trim() is "" or "21" or "22" or "990")
                _port.Text = _kind.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ApplyKind(null);
        };
        _tls.SelectionChanged += (_, _) =>
        {
            // Filling the box for a kind is not a choice: the port stays as the kind switch left it.
            if (_applying) return;
            // Implicit TLS lives on 990; plain and explicit FTP on 21.
            var tls = Choice(_tls, SiteFields.Tls);
            if (tls == SiteTlsModes.Implicit && _port.Text.Trim() == "21") _port.Text = "990";
            else if (tls != SiteTlsModes.Implicit && _port.Text.Trim() == "990") _port.Text = "21";
            ShowWarning();
        };
        _provider.SelectionChanged += (_, _) => ShowEndpoint();
        _url.TextChanged += (_, _) => ShowWarning();
        _endpoint.TextChanged += (_, _) => ShowWarning();
        _chooseKey.Click += async (_, _) =>
        {
            try
            {
                await ChooseKeyAsync();
            }
            catch (Exception)
            {
                // An event handler's exception would end the app; the words stay in the dialog instead.
                ShowError("The key file could not be read.");
            }
        };
        _browseLocal.Click += async (_, _) =>
        {
            try
            {
                if (await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(_window), startInDownloads: false, start: _localFolder.Text) is { } folder) _localFolder.Text = folder;
            }
            catch (Exception)
            {
                ShowError("The folder picker couldn't open. Type the folder instead.");
            }
        };
        _trustButton.Click += async (_, _) =>
        {
            try
            {
                await TrustShownAsync();
            }
            catch (Exception)
            {
                ShowTest("The test could not run.", ok: false);
            }
            finally
            {
                _testing = false;
                _testButton.IsEnabled = !_testOff;
            }
        };
        _testButton.Click += async (_, _) => await RunTestSafelyAsync();
        HookChanges();
        ShowAuth();
    }

    private async Task RunTestSafelyAsync()
    {
        try
        {
            await TestAsync();
        }
        catch (Exception)
        {
            // No exception words are shown, a secret may be in them.
            ShowTest("The test could not run.", ok: false);
        }
        finally
        {
            _testing = false;
            _testButton.IsEnabled = !_testOff;
        }
    }

    // SITES.13 #5: Save tests first when nothing has been tested since the last change; a failed test is shown and the
    // next Save says "Save anyway".
    private bool _testedSinceChange;
    private bool _lastTestOk;
    private bool _saveAnyway;
    private Action<string>? _setPrimaryText;

    private void HookChanges()
    {
        foreach (var box in new[] { _name, _url, _endpoint, _region, _clientId, _host, _port, _user, _keyPath, _remoteFolder, _localFolder }) box.TextChanged += (_, _) => FormChanged();
        foreach (var box in new[] { _password, _passphrase, _clientSecret }) box.PasswordChanged += (_, _) => FormChanged();
        foreach (var box in new[] { _kindBox, _tls, _vendor, _provider }) box.SelectionChanged += (_, _) => FormChanged();
        _auth.SelectionChanged += (_, _) => FormChanged();
        _removePassphrase.Click += (_, _) => FormChanged();
    }

    private void FormChanged()
    {
        _testedSinceChange = false;
        if (!_saveAnyway) return;
        _saveAnyway = false;
        _setPrimaryText?.Invoke("Save");
    }

    /// <summary>Runs the test a Save asks for; true when the Save goes on (it passed, could not run, or is not needed).</summary>
    private async Task<bool> TestBeforeSaveAsync()
    {
        if (!SitesHomeRules.NeedsTestBeforeSave(_testedSinceChange, !_testOff && !_testing, SiteKindCatalog.SignsIn(_kind.Kind), _saveAnyway)) return true;
        await RunTestSafelyAsync();
        // A test that could not run (a form problem, an older Host) leaves the save to say why.
        if (!_testedSinceChange || _lastTestOk) return true;
        _saveAnyway = true;
        _setPrimaryText?.Invoke(SitesHomeRules.SaveAnyway);
        return false;
    }

    /// <summary>The tiles' kinds only, so "S3 or B2" offers just those two; with an edit, the one kind.</summary>
    private void BindKinds(IReadOnlyList<SiteKindDto> kinds)
    {
        _applying = true;
        try
        {
            _kinds = kinds;
            _kindBox.ItemsSource = kinds.Select(k => k.Title).ToArray();
            _kindBox.SelectedIndex = Math.Max(0, kinds.ToList().FindIndex(k => k.Kind == _kind.Kind));
            _kindBox.Visibility = !_editing && kinds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _applying = false;
        }
    }

    private void BuildForm()
    {
        _hostUser.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hostUser.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hostUser.Children.Add(_host);
        _userSolo.Children.Add(_user);

        _keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_chooseKey, 1);
        _keyRow.Children.Add(_keyPath);
        _keyRow.Children.Add(_chooseKey);

        _localRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _localRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_browseLocal, 1);
        _localRow.Children.Add(_localFolder);
        _localRow.Children.Add(_browseLocal);

        _advancedBody.Children.Add(_name);
        _advancedBody.Children.Add(_port);
        _advancedBody.Children.Add(_remoteFolder);
        var limits = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        limits.Children.Add(_downLimit);
        limits.Children.Add(_upLimit);
        _advancedBody.Children.Add(limits);
        _advancedBody.Children.Add(_keyHint);
        _advanced.Content = _advancedBody;

        // One block under the Test button: the icon at the first line, the whole message wrapped beside it.
        var testRow = new Grid { ColumnSpacing = 10 };
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _testIcon.VerticalAlignment = VerticalAlignment.Top;
        _testIcon.Margin = new Thickness(0, 2, 0, 0);
        _testText.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_testText, 1);
        testRow.Children.Add(_testIcon);
        testRow.Children.Add(_testText);
        _testLine.Child = testRow;

        _form.Children.Add(_kindBox);
        _form.Children.Add(_url);
        _form.Children.Add(_vendor);
        _form.Children.Add(_provider);
        _form.Children.Add(_endpoint);
        _form.Children.Add(_region);
        _form.Children.Add(_hostUser);
        _form.Children.Add(_tls);
        _form.Children.Add(_userSolo);
        _form.Children.Add(_plainWarning);
        _form.Children.Add(_note);
        _form.Children.Add(_clientId);
        _form.Children.Add(_clientSecret);
        _form.Children.Add(_auth);
        _form.Children.Add(_password);
        _form.Children.Add(_keyRow);
        _form.Children.Add(_passphrase);
        _form.Children.Add(_removePassphrase);
        _form.Children.Add(SettingsWindow.Hint("Passwords and keys are stored sealed on this PC and never shown again."));
        _form.Children.Add(_localRow);
        _form.Children.Add(_advanced);
        _trustBody.Children.Add(_trustLines);
        _trustBody.Children.Add(_trustButton);
        _trustPanel.Child = _trustBody;
    }

    /// <summary>
    /// The form in its scroller, with a footer the scroller never hides: Test at the left, as the mockup's button row draws
    /// it, the test line beside it, a server key or certificate to trust and the error under them, right above Back and Save.
    /// </summary>
    private Grid BuildFormHost(XamlRoot xamlRoot)
    {
        var host = new Grid { RowSpacing = 8 };
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.Children.Add(DialogLayout.ScrollingContent(_form, xamlRoot));
        var footer = new Grid { RowSpacing = 10, Margin = new Thickness(0, 4, 0, 10) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _testButton.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(_testButton);
        Grid.SetRow(_testLine, 1);
        footer.Children.Add(_testLine);
        Grid.SetRow(_trustPanel, 2);
        footer.Children.Add(_trustPanel);
        Grid.SetRow(_error, 3);
        footer.Children.Add(_error);
        Grid.SetRow(footer, 1);
        host.Children.Add(footer);
        return host;
    }

    private Grid? _formHost;

    private string Method => _auth.SelectedIndex >= 0 && _auth.SelectedIndex < _methods.Length ? _methods[_auth.SelectedIndex] : _methods[0];

    private bool Shows(string field) => _kind.Fields.Count == 0
        ? field is SiteFields.Host or SiteFields.Port or SiteFields.User or SiteFields.Password or SiteFields.Key
        : _kind.Fields.Contains(field);

    /// <summary>Shows the current kind's fields, labels and choices; <paramref name="existing"/> picks the saved choices.</summary>
    private void ApplyKind(SiteDto? existing)
    {
        _applying = true;
        try
        {
            ApplyKindFields(existing);
        }
        finally
        {
            _applying = false;
        }

        ShowWarning();
    }

    private void ApplyKindFields(SiteDto? existing)
    {
        _methods = _kind.AuthMethods.Where(m => m == "password" || (m == "key" && Shows(SiteFields.Key))).ToArray();
        if (_methods.Length == 0) _methods = ["password"];
        _auth.SetItems(_methods.Select(SitesHomeRules.MethodLabel).ToArray());
        var method = existing is not null && _methods.Contains(existing.AuthMethod) ? existing.AuthMethod : _methods[0];
        _auth.SelectedIndex = Array.IndexOf(_methods, method);

        SetChoices(_tls, SiteFields.Tls, existing?.Tls);
        SetChoices(_vendor, SiteFields.Vendor, existing?.Vendor);
        SetChoices(_provider, SiteFields.Provider, existing?.Provider);

        // Server and user side by side as an address is read; kinds with no server show the user alone.
        var hasHost = Shows(SiteFields.Host);
        _hostUser.Children.Remove(_user);
        _userSolo.Children.Remove(_user);
        if (hasHost)
        {
            Grid.SetColumn(_user, 1);
            _hostUser.Children.Add(_user);
        }
        else _userSolo.Children.Add(_user);
        _hostUser.Visibility = Visible(hasHost);
        _userSolo.Visibility = Visible(!hasHost);
        _tls.Visibility = Visible(Shows(SiteFields.Tls));
        _url.Visibility = Visible(Shows(SiteFields.Url));
        _vendor.Visibility = Visible(Shows(SiteFields.Vendor));
        _provider.Visibility = Visible(Shows(SiteFields.Provider));
        _region.Visibility = Visible(Shows(SiteFields.Region));
        var signIn = Shows(SiteFields.SignIn);
        // Own app registration: advanced by nature, so it lives under Advanced.
        MoveToAdvanced(_clientId, signIn);
        MoveToAdvanced(_clientSecret, signIn);
        _port.Visibility = Visible(Shows(SiteFields.Port));
        _keyHint.Visibility = Visible(_existing is not null && _kind.Capabilities.Contains(SiteCapabilities.HostKey));
        _advanced.Header = SitesHomeRules.AdvancedLabel(Shows(SiteFields.Port));

        _user.Header = _kind.UserLabel ?? (_kind.Kind == SiteKindCatalog.WebDav ? "User (if the server asks for one)" : "User");
        AutomationProperties.SetName(_user, _kind.UserLabel ?? "User");
        var passwordLabel = _kind.PasswordLabel ?? "Password";
        _password.Header = passwordLabel;
        AutomationProperties.SetName(_password, passwordLabel);
        _note.Text = _kind.Note ?? string.Empty;
        _note.Visibility = Visible(!string.IsNullOrEmpty(_kind.Note));
        ShowAuth();
        ShowEndpoint();
    }

    /// <summary>The control sits in the form while <paramref name="advanced"/> is false and under Advanced otherwise; only shown kinds use it.</summary>
    private void MoveToAdvanced(FrameworkElement control, bool shown)
    {
        control.Visibility = Visible(shown);
        if (shown && !_advancedBody.Children.Contains(control)) _advancedBody.Children.Add(control);
        else if (!shown) _advancedBody.Children.Remove(control);
    }

    private static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void SetChoices(ComboBox box, string field, string? saved)
    {
        var choices = _kind.Choices.GetValueOrDefault(field) ?? [];
        box.ItemsSource = choices.Select(c => c.Title).ToArray();
        var index = choices.FindIndex(c => c.Value == saved);
        box.SelectedIndex = choices.Count == 0 ? -1 : Math.Max(0, index);
    }

    private string? Choice(ComboBox box, string field)
    {
        var choices = _kind.Choices.GetValueOrDefault(field);
        return choices is not null && Shows(field) && box.SelectedIndex >= 0 && box.SelectedIndex < choices.Count ? choices[box.SelectedIndex].Value : null;
    }

    /// <summary>Amazon S3 knows its own address; every other service needs one typed.</summary>
    private void ShowEndpoint()
    {
        _endpoint.Visibility = Visible(Shows(SiteFields.Endpoint));
        _endpoint.Header = Choice(_provider, SiteFields.Provider) == SiteKindCatalog.AwsProvider
            ? "Service address (optional for Amazon S3)"
            : "Service address (endpoint)";
        ShowWarning();
    }

    /// <summary>SITES.6: a plain FTP or http:// remote says so while it is filled in, not only on the list.</summary>
    private void ShowWarning()
    {
        string? warning = null;
        if (_kind.Kind == SiteKindCatalog.Ftp)
            warning = SitesRules.PlainWarning(_kind.Kind, Choice(_tls, SiteFields.Tls), null, null);
        else if (Shows(SiteFields.Url) || Shows(SiteFields.Endpoint))
        {
            // Only a finished http:// address warns, not one still being typed.
            var typed = (Shows(SiteFields.Url) ? _url.Text : _endpoint.Text).Trim();
            if (typed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && typed.Length > "http://".Length)
                warning = SitesRules.PlainWarning(_kind.Kind, null, Shows(SiteFields.Url) ? typed : null, Shows(SiteFields.Endpoint) ? typed : null);
        }

        _plainWarning.Text = warning ?? string.Empty;
        _plainWarning.Visibility = Visible(warning is not null);
        if (warning is not null) _plainWarning.Foreground = InboundTheme.Brush("InboundErrorTextBrush", _plainWarning);
    }

    private void ShowAuth()
    {
        var key = Method == "key";
        var signIn = Shows(SiteFields.SignIn);
        _password.Visibility = key || signIn ? Visibility.Collapsed : Visibility.Visible;
        _keyRow.Visibility = key && !signIn ? Visibility.Visible : Visibility.Collapsed;
        _passphrase.Visibility = key && !signIn ? Visibility.Visible : Visibility.Collapsed;
        _removePassphrase.Visibility = key && _existing?.HasKeyPassphrase == true ? Visibility.Visible : Visibility.Collapsed;
        _auth.Visibility = _methods.Length > 1 && !signIn ? Visibility.Visible : Visibility.Collapsed;
        // SITES.6c: a browser sign-in has no user name or password; the sign-in follows the save.
        _user.Visibility = signIn ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- The two steps ---------------------------------------------------------------------------------

    /// <summary>
    /// Shows the dialog. Returns the saved remote, or null when cancelled. <see cref="SiteDialogCalls.Save"/> sends the
    /// request; a refusal keeps the dialog open with its message.
    /// </summary>
    public static async Task<SiteDto?> ShowAsync(Window window, IReadOnlyList<SiteKindDto> kinds, SiteDto? existing, SiteDialogCalls calls)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null) return null;
        if (kinds.Count == 0) kinds = [Sftp];
        var tiles = SitesHomeRules.TilesFor(kinds);
        var kind = (existing is null ? kinds.FirstOrDefault(k => tiles.Count > 0 && tiles[0].Kinds.Contains(k.Kind)) : kinds.FirstOrDefault(k => k.Kind == existing.Kind)) ?? Sftp;
        var form = new SiteDialog(window, kinds, kind, existing, calls);
        return await form.RunAsync(root, tiles);
    }

    private async Task<SiteDto?> RunAsync(FrameworkElement root, IReadOnlyList<SiteTile> tiles)
    {
        var xamlRoot = root.XamlRoot!;
        _xamlRoot = xamlRoot;
        BuildTiles(tiles, root.ActualTheme);
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = root.ActualTheme,
            DefaultButton = ContentDialogButton.Primary,
        };
        // Wide enough for four tiles in a row.
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        _dialog = dialog;
        if (!_editing) ShowTilesStep();
        else ShowFormStep();

        SiteDto? saved = null;
        dialog.SecondaryButtonClick += (_, args) =>
        {
            // Back: the form returns to the tiles; the dialog stays. A remote an older Host's Test saved goes, so the next
            // tile's Save adds its own instead of changing that one.
            args.Cancel = true;
            ForgetTestedRemote();
            ForgetTrust();
            _testLine.Visibility = Visibility.Collapsed;
            ShowError(string.Empty);
            ShowTilesStep();
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Cancel = true;
                if (_chosen is not null && !_formShown)
                {
                    // Next: the tile's kind, and its form.
                    ChooseKind(_chosen);
                    ShowFormStep();
                    return;
                }

                dialog.IsPrimaryButtonEnabled = false;
                _setPrimaryText = text => dialog.PrimaryButtonText = text;
                SiteDto? site;
                try
                {
                    if (!await TestBeforeSaveAsync()) return;
                    site = await TrySaveAsync();
                }
                catch (Exception)
                {
                    // An event handler's exception would end the app; no exception words are shown, a secret may be in them.
                    ShowError("The remote could not be saved.");
                    site = null;
                }

                if (site is not null)
                {
                    saved = site;
                    _createdByTest = null;
                    args.Cancel = false;
                }
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        dialog.Opened += (_, _) => FocusFirst();

        try
        {
            await DialogGuard.ShowAsync(dialog);
        }
        finally
        {
            Clear();
            // Cancelled after an older Host's Test saved a new remote: it goes again.
            if (saved is null) ForgetTestedRemote();
        }

        return saved;
    }

    private bool _formShown;
    private XamlRoot? _xamlRoot;

    // Each step's content is built once and only ever set as the dialog's content, so nothing is moved to a new parent
    // while it still has one (a new scroller around the tiles on each Back did that: "already the child of another element").
    private ScrollViewer? _tilesHost;

    private void ShowTilesStep() => SwitchStep(form: false);

    private void ShowFormStep() => SwitchStep(form: true);

    /// <summary>
    /// Switches between the tiles and the form. The content goes first; if it can't be shown nothing else changes, so the
    /// dialog is never half-switched (a title of one step over the content of the other).
    /// </summary>
    private void SwitchStep(bool form)
    {
        if (_dialog is not { } dialog || _xamlRoot is not { } xamlRoot) return;
        try
        {
            UIElement content = form ? _formHost ??= BuildFormHost(xamlRoot) : _tilesHost ??= DialogLayout.ScrollingContent(_tiles, xamlRoot);
            if (!ReferenceEquals(dialog.Content, content)) dialog.Content = content;
        }
        catch (Exception ex)
        {
            UiTrace.Write($"The remote dialog could not switch steps: {ex.GetType().Name}");
            return;
        }

        _formShown = form;
        dialog.Title = !form ? SitesHomeRules.AddRemote
            : _editing ? "Edit remote" : _chosen?.FormTitle ?? SitesHomeRules.TileOf(_kind.Kind)?.FormTitle ?? "Add a remote";
        _saveAnyway = false;
        dialog.PrimaryButtonText = form ? "Save" : "Next";
        // Drawn as Test | Back, Save; Esc cancels. An edit has no step to go back to, so it keeps Cancel.
        dialog.SecondaryButtonText = form && !_editing ? "Back" : string.Empty;
        dialog.CloseButtonText = form && !_editing ? string.Empty : "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        FocusFirst();
    }

    private void FocusFirst()
    {
        Control? target;
        if (!_formShown) target = _tileButtons.Count == 0 ? null : (_tileButtons.FirstOrDefault(t => ReferenceEquals(t.Tile, _chosen)).Button ?? _tileButtons[0].Button);
        else if (!_editing && _kindBox.Visibility == Visibility.Visible) target = _kindBox;
        else if (_hostUser.Visibility == Visibility.Visible) target = _host;
        else if (_url.Visibility == Visibility.Visible) target = _url;
        else if (_provider.Visibility == Visibility.Visible) target = _provider;
        else target = _testButton;
        target?.Focus(FocusState.Programmatic);
    }

    /// <summary>A tile was chosen: the form takes its kind (and, for "S3 or B2", offers just those two).</summary>
    private void ChooseKind(SiteTile tile)
    {
        var offered = _allKinds.Where(k => tile.Kinds.Contains(k.Kind)).ToList();
        if (offered.Count == 0) offered = [_allKinds[0]];
        _kind = offered[0];
        BindKinds(offered);
        if (_kind.DefaultPort > 0) _port.Text = _kind.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyKind(null);
    }

    // ---- Tiles -----------------------------------------------------------------------------------------

    private void BuildTiles(IReadOnlyList<SiteTile> tiles, ElementTheme theme)
    {
        // The tiles are not in the tree yet, so their own ActualTheme is unset: take the window's (R7, light theme).
        _tiles.Children.Add(new TextBlock { Text = "What kind of place is it?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        const int columns = 4;
        for (var c = 0; c < columns; c++) _tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < (tiles.Count + columns - 1) / columns; r++) _tileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new FontIcon { Glyph = tile.Glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left, Foreground = InboundTheme.Brush("InboundIconInfoBrush", theme) });
            body.Children.Add(new TextBlock { Text = tile.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = tile.Line, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", theme) });
            var button = new Button
            {
                Content = body,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(12, 10, 12, 10),
                MinHeight = 92,
            };
            AutomationProperties.SetName(button, $"{tile.Name}. {tile.Line}");
            button.Click += (_, _) => Choose(tile);
            // A double click is a quicker Next.
            button.DoubleTapped += (_, _) =>
            {
                Choose(tile);
                ChooseKind(tile);
                ShowFormStep();
            };
            Grid.SetRow(button, i / columns);
            Grid.SetColumn(button, i % columns);
            _tileGrid.Children.Add(button);
            _tileButtons.Add((tile, button));
        }

        _tiles.Children.Add(_tileGrid);
        if (tiles.Count > 0) Choose(tiles[0]);
    }

    private void Choose(SiteTile tile)
    {
        _chosen = tile;
        foreach (var (other, button) in _tileButtons)
        {
            var picked = ReferenceEquals(other, tile);
            button.BorderThickness = new Thickness(picked ? 2 : 1);
            if (picked) button.BorderBrush = InboundTheme.Brush("InboundProgressFillBrush", button);
            else button.ClearValue(Control.BorderBrushProperty);
            AutomationProperties.SetItemStatus(button, picked ? "Chosen" : string.Empty);
        }
    }

    // ---- Save and Test ---------------------------------------------------------------------------------

    private static int Limit(NumberBox box) => double.IsNaN(box.Value) ? 0 : (int)Math.Clamp(Math.Round(box.Value), 0, 10_000_000);

    private async Task<SiteSaveRequest?> BuildRequestAsync()
    {
        var keyText = string.Empty;
        if (Method == "key" && Shows(SiteFields.Key) && _keyPath.Text.Trim().Length > 0)
        {
            try
            {
                var path = SitesHomeRules.KeyFilePath(_keyPath.Text);
                var info = new FileInfo(path);
                if (SitesHomeRules.KeyFileProblem(info.Exists, info.Exists ? info.Length : 0) is { } fileProblem) { ShowError(fileProblem); return null; }
                keyText = await File.ReadAllTextAsync(path);
                if (SiteKeyRules.Problem(keyText) is { } keyProblem) { ShowError(keyProblem); return null; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowError("The key file couldn't be read.");
                return null;
            }
        }

        var url = Shows(SiteFields.Url) ? _url.Text : string.Empty;
        var endpoint = Shows(SiteFields.Endpoint) ? _endpoint.Text : string.Empty;
        var name = SitesHomeRules.FormName(_name.Text, _kind.Kind, _host.Text, url, endpoint);
        var values = new SiteFormValues(_kind.Kind, name, _host.Text, _port.Text, _user.Text, Method, _password.Password,
            keyText, _passphrase.Password, _removePassphrase.IsChecked == true && _removePassphrase.Visibility == Visibility.Visible,
            _remoteFolder.Text, _localFolder.Text, Choice(_tls, SiteFields.Tls), url,
            Choice(_vendor, SiteFields.Vendor), Choice(_provider, SiteFields.Provider), endpoint,
            Shows(SiteFields.Region) ? _region.Text : string.Empty,
            Shows(SiteFields.SignIn) ? _clientId.Text : string.Empty, Shows(SiteFields.SignIn) ? _clientSecret.Password : string.Empty);
        var request = SitesRules.Request(values, _existing, out var problem);
        if (request is null) ShowError(problem!);
        else
        {
            request.DownLimit = Limit(_downLimit);
            request.UpLimit = Limit(_upLimit);
        }

        return request;
    }

    private async Task<SiteDto?> TrySaveAsync()
    {
        var request = await BuildRequestAsync();
        if (request is null) return null;

        ShowError(string.Empty);
        DropTrustIfMoved();
        SiteResultDto result;
        try
        {
            result = await _calls.Save(request, CancellationToken.None);
        }
        finally
        {
            // Out of the request before anything else happens.
            ClearSecrets(request);
        }

        if (!result.Ok || result.Site is null)
        {
            ShowError(string.IsNullOrWhiteSpace(result.Message) ? "The remote couldn't be saved." : result.Message);
            return null;
        }

        // What was trusted in the form is trusted now that the remote exists: the Host reads a certificate again and
        // takes it only if it is the one shown. A trust that fails is asked again by the test after the save.
        var site = result.Site;
        if (_acceptedKeys.Count > 0 && SitesRules.NeedsTrust(site))
        {
            var trusted = await _calls.TrustKeys(site.Id, [.. _acceptedKeys.Select(key => key.Entry)], CancellationToken.None);
            if (trusted is { Ok: true, Site: { } withKeys }) site = withKeys;
        }

        if (_certificateToTrust is { } fingerprint)
        {
            var trusted = await _calls.TrustCertificate(site.Id, fingerprint, CancellationToken.None);
            if (trusted is { Ok: true, Site: { } withCertificate }) site = withCertificate;
        }

        return site;
    }

    private static void ClearSecrets(SiteSaveRequest request)
    {
        request.Password = null;
        request.PrivateKey = null;
        request.KeyPassphrase = null;
        request.ClientSecret = null;
    }

    private (string Host, string Port, string Url, string Endpoint, string? Tls) Where()
        => (_host.Text, _port.Text, Shows(SiteFields.Url) ? _url.Text : "", Shows(SiteFields.Endpoint) ? _endpoint.Text : "", Choice(_tls, SiteFields.Tls));

    /// <summary>A key or certificate accepted for one server is never kept for another the form now points at.</summary>
    private void DropTrustIfMoved()
    {
        if ((_acceptedKeys.Count > 0 || _certificateToTrust is not null) && !SitesHomeRules.SameServer(_trustedFor, Where())) ForgetTrust();
    }

    private void ForgetTrust()
    {
        _acceptedKeys = [];
        _certificateToTrust = null;
        HideTrust();
    }

    private void ShowTrustCard()
    {
        _trustPanel.BorderBrush = InboundTheme.Brush("InboundDividerBrush", _trustPanel);
        _trustPanel.Background = InboundTheme.Brush("InboundCardBrush", _trustPanel);
        _trustPanel.Visibility = Visibility.Visible;
    }

    private void HideTrust()
    {
        _offeredKeys = [];
        _offeredCertificate = null;
        _trustLines.Children.Clear();
        _trustPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>An older Host's Test saved a new remote and the dialog is left without saving: it is removed.</summary>
    private void ForgetTestedRemote()
    {
        if (_createdByTest is not { } id) return;
        _createdByTest = null;
        _existing = null;
        _ = RemoveQuietlyAsync(id);
    }

    private async Task RemoveQuietlyAsync(string id)
    {
        try
        {
            await _calls.Remove(id, CancellationToken.None);
        }
        catch (Exception)
        {
            // The remote stays in the list, where Remove takes it; nothing here may end the app.
        }
    }

    /// <summary>
    /// Tries the form as typed without saving it. A server key the Host read (with no login) is shown under the line with
    /// Trust, which tries again with it; a certificate is shown the same way and trusted at the save. A browser sign-in
    /// follows the save.
    /// </summary>
    private async Task TestAsync()
    {
        if (_testing) return;
        _testing = true;
        _testButton.IsEnabled = false;
        DropTrustIfMoved();
        HideTrust();
        ShowTest("Testing…", ok: null);
        var request = await BuildRequestAsync();
        if (request is null)
        {
            _testLine.Visibility = Visibility.Collapsed;
            return;
        }

        ShowError(string.Empty);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SiteResultDto? result;
        try
        {
            result = await _calls.TestDraft(request, [.. _acceptedKeys.Select(key => key.Entry)], CancellationToken.None);
        }
        finally
        {
            ClearSecrets(request);
        }

        clock.Stop();
        if (result is null)
        {
            // An older Host tests saved remotes only. A new remote is saved first (Back and Cancel remove it again); an edit
            // is not, since that would change the saved remote before Save: Test is turned off there instead.
            if (_editing)
            {
                _testLine.Visibility = Visibility.Collapsed;
                DisableTestForOlderHost();
                return;
            }

            await TestBySavingAsync();
            return;
        }

        ShowTest(SitesHomeRules.DraftTestText(result, SiteKindCatalog.SignsIn(_kind.Kind), clock.Elapsed), ok: result.Ok);
        _testedSinceChange = true;
        _lastTestOk = result.Ok;
        _trustedFor = Where();
        if (result.Problem == SiteProblems.HostKeyNotAccepted && result.OfferedKeys is { Count: > 0 } keys) ShowKeys(keys);
        else if (result.Problem == SiteProblems.CertificateNotTrusted && result.Certificate is { CannotTrust: null } certificate) ShowCertificate(certificate);
        else if (result.Problem == SiteProblems.CertificateNotTrusted && result.Certificate is { } unusable)
        {
            // Trusting it would not help (expired, not for this name…): shown, with nothing offered.
            _trustLines.Children.Clear();
            foreach (var (label, value) in SitesRules.CertificateLines(unusable)) _trustLines.Children.Add(Mono(label, value));
            _trustButton.Visibility = Visibility.Collapsed;
            ShowTrustCard();
        }
    }

    private bool _testOff;

    private void DisableTestForOlderHost()
    {
        _testOff = true;
        _testButton.IsEnabled = false;
        const string why = "Inbound's background program is older and can only test a saved remote. Save, then Test the remote from its menu.";
        ToolTipService.SetToolTip(_testButton, why);
        AutomationProperties.SetHelpText(_testButton, why);
        ShowError(why);
    }

    private void ShowKeys(IReadOnlyList<SiteHostKeyDto> keys)
    {
        _offeredKeys = [.. keys];
        _offeredCertificate = null;
        _trustLines.Children.Clear();
        foreach (var key in keys) _trustLines.Children.Add(Mono(key.Type, key.Fingerprint));
        _trustLines.Children.Add(SettingsWindow.Hint("Inbound connects to this remote only while the server shows this key. Compare it with ssh-keygen -lf on the server's host key file."));
        _trustButton.Visibility = Visibility.Visible;
        ShowTrustCard();
    }

    private void ShowCertificate(SiteCertificateDto certificate)
    {
        _offeredKeys = [];
        _offeredCertificate = certificate;
        _trustLines.Children.Clear();
        foreach (var (label, value) in SitesRules.CertificateLines(certificate)) _trustLines.Children.Add(Mono(label, value));
        // rclone takes a trusted certificate as an authority for all of Inbound's remotes (no per-remote trust exists).
        _trustLines.Children.Add(SettingsWindow.Hint("Whoever holds this certificate's key is trusted by all your Inbound remotes, not only this one. Trust only a certificate of your own server."));
        _trustButton.Visibility = Visibility.Visible;
        ShowTrustCard();
    }

    /// <summary>Trust: keys are used for a second test at once; a certificate is trusted when the remote is saved.</summary>
    private async Task TrustShownAsync()
    {
        if (_offeredKeys.Count > 0)
        {
            _acceptedKeys = [.. _offeredKeys];
            _trustedFor = Where();
            HideTrust();
            await TestAsync();
            return;
        }

        if (_offeredCertificate is { } certificate)
        {
            _certificateToTrust = certificate.Fingerprint;
            _trustedFor = Where();
            _trustButton.Visibility = Visibility.Collapsed;
            ShowTest("The certificate is trusted when you save, and the remote is tried then.", ok: null);
            _testedSinceChange = true;
            _lastTestOk = true;
        }
    }

    /// <summary>A key type (or certificate line) in the default font over its value in monospace, wrapped.</summary>
    private static TextBlock Mono(string label, string value)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Menlo, monospace") });
        return block;
    }

    /// <summary>
    /// An older Host tests saved remotes only, so Test saves what is typed and then tries it. What needs a question of its own
    /// (a server's key, a certificate, a browser sign-in) is not asked here: Save closes the form and the questions follow.
    /// A new remote saved here is removed again by Back or Cancel.
    /// </summary>
    private async Task TestBySavingAsync()
    {
        var wasNew = _existing is null;
        ShowTest("Testing…", ok: null);
        var site = await TrySaveAsync();
        if (site is null)
        {
            _testLine.Visibility = Visibility.Collapsed;
            return;
        }

        // From here the form edits the remote it just saved, so Save updates it instead of adding another.
        if (wasNew) _createdByTest = site.Id;
        _existing = site;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await _calls.Test(site.Id, CancellationToken.None);
        clock.Stop();
        _testedSinceChange = true;
        _lastTestOk = result.Ok;
        if (result.Ok) ShowTest(SitesHomeRules.TestWorks(SiteKindCatalog.SignsIn(site.Kind), clock.Elapsed), ok: true);
        else if (result.Problem == SiteProblems.HostKeyNotAccepted) ShowTest("Saved. The server's key isn't trusted yet: press Save, and Inbound shows it to you.", ok: false);
        else if (result.Problem == SiteProblems.HostKeyChanged) ShowTest("The server shows a different key from the one you trusted. Press Save to see it.", ok: false);
        else if (result.Problem == SiteProblems.CertificateNotTrusted) ShowTest("The server's certificate isn't trusted yet: press Save, and Inbound shows it to you.", ok: false);
        else if (result.Problem == SiteProblems.SignInNeeded) ShowTest("Not signed in yet: press Save, and your browser opens to sign in.", ok: false);
        else ShowTest(string.IsNullOrWhiteSpace(result.Message) ? "The test failed." : result.Message, ok: false);
    }

    private void ShowTest(string text, bool? ok)
    {
        _testText.Text = text;
        _testLine.Visibility = Visibility.Visible;
        _testLine.Background = InboundTheme.Brush("InboundCardBrush", _testLine);
        _testIcon.Glyph = ok == true ? "\uE73E" : ok == false ? "\uE783" : "\uE895";
        _testIcon.Foreground = InboundTheme.Brush(ok == true ? "InboundStatusDoneBrush" : ok == false ? "InboundErrorTextBrush" : "InboundSecondaryTextBrush", _testLine);
    }

    private async Task ChooseKeyAsync()
    {
        _chooseKey.IsEnabled = false;
        try
        {
            var files = await Pickers.PickFilesAsync(WinRT.Interop.WindowNative.GetWindowHandle(_window), ["*"], multiple: false, Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder);
            if (files is not [var file]) return;
            _keyPath.Text = file;
            ShowError(string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ShowError("The key file couldn't be read.");
        }
        finally
        {
            _chooseKey.IsEnabled = true;
        }
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (text.Length > 0) _error.Foreground = InboundTheme.Brush("InboundErrorTextBrush", _error);
    }

    private void Clear()
    {
        _password.Password = string.Empty;
        _passphrase.Password = string.Empty;
        _clientSecret.Password = string.Empty;
    }
}

/// <summary>What the Add a remote / Edit remote dialog asks the Host (SITES.11 R4).</summary>
/// <param name="TestDraft">Tries the form without saving it with the server keys accepted in it; null when the Host is older and lacks it.</param>
internal sealed record SiteDialogCalls(
    Func<SiteSaveRequest, CancellationToken, Task<SiteResultDto>> Save,
    Func<string, CancellationToken, Task<SiteResultDto>> Test,
    Func<SiteSaveRequest, IReadOnlyList<string>, CancellationToken, Task<SiteResultDto?>> TestDraft,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<SiteResultDto>> TrustKeys,
    Func<string, string, CancellationToken, Task<SiteResultDto>> TrustCertificate,
    Func<string, CancellationToken, Task<SiteResultDto>> Remove);

/// <summary>
/// A segmented control as the mockup draws "Sign in with": one framed row of toggle buttons, one of them on. Built in code
/// (Windows App SDK has none), so it is the same on Linux. Left and Right move the choice.
/// </summary>
internal sealed class SegmentedChoice : StackPanel
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> _buttons = [];
    private int _selected = -1;

    public event EventHandler? SelectionChanged;

    public SegmentedChoice(string header)
    {
        Spacing = 4;
        Children.Add(new TextBlock { Text = header });
        var frame = new Border
        {
            Child = _row,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        InboundUi.Follow(frame, "InboundDividerBrush", brush => frame.BorderBrush = brush);
        Children.Add(frame);
        _row.KeyDown += (_, e) =>
        {
            var step = e.Key == Windows.System.VirtualKey.Right ? 1 : e.Key == Windows.System.VirtualKey.Left ? -1 : 0;
            if (step == 0 || _buttons.Count == 0) return;
            e.Handled = true;
            var next = Math.Clamp(_selected + step, 0, _buttons.Count - 1);
            Select(next);
            _buttons[next].Focus(FocusState.Keyboard);
        };
    }

    /// <summary>The choices, in order; none is on until <see cref="SelectedIndex"/> is set.</summary>
    public void SetItems(IReadOnlyList<string> items)
    {
        _row.Children.Clear();
        _buttons.Clear();
        _selected = -1;
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = items[i], MinWidth = 96, CornerRadius = new CornerRadius(4) };
            AutomationProperties.SetName(button, items[i]);
            AutomationProperties.SetPositionInSet(button, i + 1);
            AutomationProperties.SetSizeOfSet(button, items.Count);
            // A click on the chosen one keeps it on: one is always chosen.
            button.Click += (_, _) => Select(index);
            _buttons.Add(button);
            _row.Children.Add(button);
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value);
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _buttons.Count) index = -1;
        var changed = index != _selected;
        _selected = index;
        for (var i = 0; i < _buttons.Count; i++) _buttons[i].IsChecked = i == index;
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
