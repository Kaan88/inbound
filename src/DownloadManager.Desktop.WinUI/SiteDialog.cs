using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The Add a site / Edit a site dialog, drawn from <c>sites.kinds</c>: a new site starts with a choice of kind, and each kind
/// shows only its own fields (SITES.6). It saves by itself and stays open, with the reason, when the Host or the checks
/// refuse. The secrets live only in its boxes until they are sent and are cleared after. In edit mode a secret box left
/// empty keeps the stored secret.
/// </summary>
internal sealed class SiteDialog
{
    private static readonly SiteKindDto Sftp = new() { Kind = "sftp", Title = "SFTP", DefaultPort = 22, AuthMethods = ["password", "key"] };

    private readonly SettingsWindow _window;
    private readonly SiteDto? _existing;
    private readonly IReadOnlyList<SiteKindDto> _kinds;
    private SiteKindDto _kind;
    private readonly Func<SiteSaveRequest, CancellationToken, Task<SiteResultDto>> _save;
    private readonly ComboBox _kindBox = new() { Header = "Kind", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "My server" };
    private readonly Grid _address = new() { ColumnSpacing = 8 };
    private readonly ComboBox _tls = new() { Header = "Encryption", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _url = new() { Header = "Address", PlaceholderText = "https://files.example.com/remote.php/dav/files/me" };
    private readonly ComboBox _vendor = new() { Header = "Server type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _provider = new() { Header = "Service", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _endpoint = new() { Header = "Service address (endpoint)", PlaceholderText = "https://s3.example.com" };
    private readonly TextBox _region = new() { Header = "Region (optional)", PlaceholderText = "eu-central-1" };
    private readonly TextBlock _plainWarning = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _note = SettingsWindow.Hint(string.Empty);
    private readonly TextBlock _keyHint = SettingsWindow.Hint("Changing the server or the port asks about the server's key again.");
    private readonly TextBox _host = new() { Header = "Server", PlaceholderText = "files.example.com" };
    private readonly TextBox _port = new() { Header = "Port", Width = 96 };
    private readonly TextBox _user = new() { Header = "User name" };
    private readonly RadioButtons _auth = new() { Header = "Sign in with" };
    private readonly PasswordBox _password = new() { Header = "Password", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly TextBox _privateKey = new()
    {
        Header = "Private key", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 96, MaxHeight = 140,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Menlo, monospace"),
    };
    private readonly Button _chooseKey = new() { Content = "Choose key file…" };
    private readonly PasswordBox _passphrase = new() { Header = "Key passphrase (if it has one)", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly CheckBox _removePassphrase = new() { Content = "Remove the saved passphrase" };
    private readonly TextBox _remoteFolder = new() { Header = "Folder on the server (optional)", PlaceholderText = "/" };
    private readonly TextBox _localFolder;
    private readonly UIElement _localFolderRow;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, IsTextSelectionEnabled = true };
    private string[] _methods = ["password"];
    private bool _applying;

    private SiteDialog(SettingsWindow window, IReadOnlyList<SiteKindDto> kinds, SiteKindDto kind, SiteDto? existing, Func<SiteSaveRequest, CancellationToken, Task<SiteResultDto>> save)
    {
        _window = window;
        _kinds = kinds;
        _kind = kind;
        _existing = existing;
        _save = save;

        _name.Text = existing?.Name ?? string.Empty;
        _host.Text = existing?.Host ?? string.Empty;
        _port.Text = (existing?.Port is > 0 ? existing.Port : kind.DefaultPort > 0 ? kind.DefaultPort : 22).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _user.Text = existing?.User ?? string.Empty;
        _url.Text = existing?.Url ?? string.Empty;
        _endpoint.Text = existing?.Endpoint ?? string.Empty;
        _region.Text = existing?.Region ?? string.Empty;
        _remoteFolder.Text = existing?.RemoteFolder ?? string.Empty;
        (_localFolderRow, _localFolder) = window.FolderField("Folder on this PC (optional)", existing?.LocalFolder, favorites: false);

        _kindBox.ItemsSource = kinds.Select(k => k.Title).ToArray();
        _kindBox.SelectedIndex = Math.Max(0, kinds.ToList().FindIndex(k => k.Kind == kind.Kind));
        _kindBox.Visibility = existing is null && kinds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ApplyKind(existing);

        if (existing is not null)
        {
            _password.PlaceholderText = existing.HasPassword ? "Saved. Leave empty to keep it." : string.Empty;
            _privateKey.PlaceholderText = existing.HasPrivateKey ? "A key is saved. Leave empty to keep it." : "-----BEGIN OPENSSH PRIVATE KEY-----";
            _passphrase.PlaceholderText = existing.HasKeyPassphrase ? "Saved. Leave empty to keep it." : string.Empty;
        }
        else _privateKey.PlaceholderText = "-----BEGIN OPENSSH PRIVATE KEY-----";

        ScrollViewer.SetHorizontalScrollBarVisibility(_privateKey, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_kindBox, "Kind of site");
        AutomationProperties.SetName(_tls, "Encryption");
        AutomationProperties.SetName(_url, "Address");
        AutomationProperties.SetName(_vendor, "Server type");
        AutomationProperties.SetName(_provider, "Service");
        AutomationProperties.SetName(_endpoint, "Service address");
        AutomationProperties.SetName(_region, "Region");
        AutomationProperties.SetName(_name, "Site name");
        AutomationProperties.SetName(_host, "Server");
        AutomationProperties.SetName(_port, "Port");
        AutomationProperties.SetName(_auth, "Sign in with");
        AutomationProperties.SetName(_privateKey, "Private key");
        AutomationProperties.SetName(_chooseKey, "Choose a private key file");
        AutomationProperties.SetName(_passphrase, "Key passphrase");
        AutomationProperties.SetName(_removePassphrase, "Remove the saved passphrase");
        AutomationProperties.SetName(_remoteFolder, "Folder on the server");
        AutomationProperties.SetLiveSetting(_error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

        _auth.SelectionChanged += (_, _) => ShowAuth();
        _kindBox.SelectionChanged += (_, _) =>
        {
            if (_kindBox.SelectedIndex >= 0 && _kindBox.SelectedIndex < _kinds.Count && _kinds[_kindBox.SelectedIndex].Kind != _kind.Kind)
            {
                _kind = _kinds[_kindBox.SelectedIndex];
                // A port still at a kind's usual one (or empty) follows the new kind's, through WebDAV and S3 too, which have none.
                if (_kind.DefaultPort > 0 && _port.Text.Trim() is "" or "21" or "22" or "990")
                    _port.Text = _kind.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ApplyKind(null);
            }
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
        ShowAuth();
    }

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
        _auth.ItemsSource = _methods.Select(m => m == "key" ? "Private key" : "Password").ToArray();
        var method = existing is not null && _methods.Contains(existing.AuthMethod) ? existing.AuthMethod : _methods[0];
        _auth.SelectedIndex = Array.IndexOf(_methods, method);

        SetChoices(_tls, SiteFields.Tls, existing?.Tls);
        SetChoices(_vendor, SiteFields.Vendor, existing?.Vendor);
        SetChoices(_provider, SiteFields.Provider, existing?.Provider);

        _address.Visibility = Visible(Shows(SiteFields.Host));
        _tls.Visibility = Visible(Shows(SiteFields.Tls));
        _url.Visibility = Visible(Shows(SiteFields.Url));
        _vendor.Visibility = Visible(Shows(SiteFields.Vendor));
        _provider.Visibility = Visible(Shows(SiteFields.Provider));
        _region.Visibility = Visible(Shows(SiteFields.Region));
        _keyHint.Visibility = Visible(_existing is not null && _kind.Capabilities.Contains(SiteCapabilities.HostKey));

        _user.Header = _kind.UserLabel ?? (_kind.Kind == SiteKindCatalog.WebDav ? "User name (if the server asks for one)" : "User name");
        AutomationProperties.SetName(_user, _kind.UserLabel ?? "User name");
        var passwordLabel = _kind.PasswordLabel ?? "Password";
        _password.Header = passwordLabel;
        AutomationProperties.SetName(_password, passwordLabel);
        _note.Text = _kind.Note ?? string.Empty;
        _note.Visibility = Visible(!string.IsNullOrEmpty(_kind.Note));
        ShowAuth();
        ShowEndpoint();
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

    /// <summary>SITES.6: a plain FTP or http:// site says so while it is filled in, not only on the list.</summary>
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
        _password.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        _privateKey.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        _chooseKey.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        _passphrase.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        _removePassphrase.Visibility = key && _existing?.HasKeyPassphrase == true ? Visibility.Visible : Visibility.Collapsed;
        _auth.Visibility = _methods.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the dialog. Returns the saved site, or null when cancelled. <paramref name="save"/> sends the request; a
    /// refusal keeps the dialog open with its message.
    /// </summary>
    public static async Task<SiteDto?> ShowAsync(SettingsWindow window, IReadOnlyList<SiteKindDto> kinds, SiteDto? existing,
        Func<SiteSaveRequest, CancellationToken, Task<SiteResultDto>> save)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null) return null;
        if (kinds.Count == 0) kinds = [Sftp];
        var kind = (existing is null ? kinds[0] : kinds.FirstOrDefault(k => k.Kind == existing.Kind)) ?? Sftp;
        var form = new SiteDialog(window, kinds, kind, existing, save);
        return await form.RunAsync(root);
    }

    private async Task<SiteDto?> RunAsync(FrameworkElement root)
    {
        var xamlRoot = root.XamlRoot!;
        // A fixed width: a long unwrapped key line would otherwise widen the whole dialog (it scrolls in its box instead).
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, MaxWidth = 440 };
        panel.Children.Add(_kindBox);
        panel.Children.Add(_name);
        // Server and port on one line, as an address is read.
        _address.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _address.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_port, 1);
        _address.Children.Add(_host);
        _address.Children.Add(_port);
        panel.Children.Add(_address);
        panel.Children.Add(_keyHint);
        panel.Children.Add(_tls);
        panel.Children.Add(_url);
        panel.Children.Add(_vendor);
        panel.Children.Add(_provider);
        panel.Children.Add(_endpoint);
        panel.Children.Add(_region);
        panel.Children.Add(_plainWarning);
        panel.Children.Add(_note);
        panel.Children.Add(_user);
        panel.Children.Add(_auth);
        panel.Children.Add(_password);
        panel.Children.Add(_privateKey);
        panel.Children.Add(_chooseKey);
        panel.Children.Add(_passphrase);
        panel.Children.Add(_removePassphrase);
        panel.Children.Add(SettingsWindow.Hint("Passwords and keys are stored sealed on this PC and never shown again."));
        panel.Children.Add(_remoteFolder);
        panel.Children.Add(_localFolderRow);
        panel.Children.Add(_error);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = root.ActualTheme,
            // "New SFTP site": no "a"/"an" to get wrong for a kind's name.
            Title = _existing is null ? "New site" : $"Edit {_existing.Name}",
            Content = DialogLayout.ScrollingContent(panel, xamlRoot),
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Enter in the key box is a line break, not Save (the walk found Uno's dialog saving on it, after the key's first line).
        _privateKey.GotFocus += (_, _) => dialog.DefaultButton = ContentDialogButton.None;
        _privateKey.LostFocus += (_, _) => dialog.DefaultButton = ContentDialogButton.Primary;

        SiteDto? saved = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                SiteDto? site;
                try
                {
                    site = await TrySaveAsync();
                }
                catch (Exception)
                {
                    // An event handler's exception would end the app; no exception words are shown, a secret may be in them.
                    ShowError("The site could not be saved.");
                    site = null;
                }

                if (site is not null)
                {
                    saved = site;
                    args.Cancel = false;
                }
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        dialog.Opened += (_, _) => (_existing is null ? _kindBox.Visibility == Visibility.Visible ? (Control)_kindBox : _name : _password.Visibility == Visibility.Visible ? (Control)_password : _privateKey).Focus(FocusState.Programmatic);

        try
        {
            await DialogGuard.ShowAsync(dialog);
        }
        finally
        {
            Clear();
        }

        return saved;
    }

    private async Task<SiteDto?> TrySaveAsync()
    {
        var values = new SiteFormValues(_kind.Kind, _name.Text, _host.Text, _port.Text, _user.Text, Method, _password.Password,
            _privateKey.Text, _passphrase.Password, _removePassphrase.IsChecked == true && _removePassphrase.Visibility == Visibility.Visible,
            _remoteFolder.Text, _localFolder.Text, Choice(_tls, SiteFields.Tls), Shows(SiteFields.Url) ? _url.Text : string.Empty,
            Choice(_vendor, SiteFields.Vendor), Choice(_provider, SiteFields.Provider), Shows(SiteFields.Endpoint) ? _endpoint.Text : string.Empty,
            Shows(SiteFields.Region) ? _region.Text : string.Empty);
        var request = SitesRules.Request(values, _existing, out var problem);
        if (request is null)
        {
            ShowError(problem!);
            return null;
        }

        ShowError(string.Empty);
        SiteResultDto result;
        try
        {
            result = await _save(request, CancellationToken.None);
        }
        finally
        {
            // Out of the request before anything else happens.
            request.Password = null;
            request.PrivateKey = null;
            request.KeyPassphrase = null;
        }

        if (!result.Ok || result.Site is null)
        {
            ShowError(string.IsNullOrWhiteSpace(result.Message) ? "The site couldn't be saved." : result.Message);
            return null;
        }

        return result.Site;
    }

    private async Task ChooseKeyAsync()
    {
        _chooseKey.IsEnabled = false;
        try
        {
            var files = await Pickers.PickFilesAsync(WinRT.Interop.WindowNative.GetWindowHandle(_window), ["*"], multiple: false, Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder);
            if (files is not [var file]) return;
            var info = new FileInfo(file);
            if (info.Length > SitesRules.MostKeyBytes)
            {
                ShowError("That file is too big to be a private key.");
                return;
            }

            var text = await File.ReadAllTextAsync(file);
            if (SiteKeyRules.Problem(text) is { } keyProblem)
            {
                ShowError(keyProblem);
                return;
            }

            _privateKey.Text = text;
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
        _privateKey.Text = string.Empty;
    }
}
