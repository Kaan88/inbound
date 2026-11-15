using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The Add a site / Edit a site dialog, drawn from <c>sites.kinds</c>. It saves by itself and stays open, with the reason,
/// when the Host or the checks refuse. The secrets live only in its boxes until they are sent and are cleared after. In
/// edit mode a secret box left empty keeps the stored secret.
/// </summary>
internal sealed class SiteDialog
{
    private static readonly SiteKindDto Sftp = new() { Kind = "sftp", Title = "SFTP", DefaultPort = 22, AuthMethods = ["password", "key"] };

    private readonly SettingsWindow _window;
    private readonly SiteDto? _existing;
    private readonly SiteKindDto _kind;
    private readonly Func<SiteSaveRequest, CancellationToken, Task<SiteResultDto>> _save;
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "My server" };
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
    private readonly string[] _methods;

    private SiteDialog(SettingsWindow window, SiteKindDto kind, SiteDto? existing, Func<SiteSaveRequest, CancellationToken, Task<SiteResultDto>> save)
    {
        _window = window;
        _kind = kind;
        _existing = existing;
        _save = save;
        _methods = kind.AuthMethods.Count > 0 ? kind.AuthMethods.Where(m => m is "password" or "key").ToArray() : ["password", "key"];
        if (_methods.Length == 0) _methods = ["password"];

        _name.Text = existing?.Name ?? string.Empty;
        _host.Text = existing?.Host ?? string.Empty;
        _port.Text = (existing?.Port is > 0 ? existing.Port : kind.DefaultPort > 0 ? kind.DefaultPort : 22).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _user.Text = existing?.User ?? string.Empty;
        _remoteFolder.Text = existing?.RemoteFolder ?? string.Empty;
        (_localFolderRow, _localFolder) = window.FolderField("Folder on this PC (optional)", existing?.LocalFolder, favorites: false);

        _auth.ItemsSource = _methods.Select(m => m == "key" ? "Private key" : "Password").ToArray();
        var method = existing is not null && _methods.Contains(existing.AuthMethod) ? existing.AuthMethod : _methods[0];
        _auth.SelectedIndex = Array.IndexOf(_methods, method);

        if (existing is not null)
        {
            _password.PlaceholderText = existing.HasPassword ? "Saved. Leave empty to keep it." : string.Empty;
            _privateKey.PlaceholderText = existing.HasPrivateKey ? "A key is saved. Leave empty to keep it." : "-----BEGIN OPENSSH PRIVATE KEY-----";
            _passphrase.PlaceholderText = existing.HasKeyPassphrase ? "Saved. Leave empty to keep it." : string.Empty;
        }
        else _privateKey.PlaceholderText = "-----BEGIN OPENSSH PRIVATE KEY-----";

        ScrollViewer.SetHorizontalScrollBarVisibility(_privateKey, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_name, "Site name");
        AutomationProperties.SetName(_host, "Server");
        AutomationProperties.SetName(_port, "Port");
        AutomationProperties.SetName(_user, "User name");
        AutomationProperties.SetName(_auth, "Sign in with");
        AutomationProperties.SetName(_password, "Password");
        AutomationProperties.SetName(_privateKey, "Private key");
        AutomationProperties.SetName(_chooseKey, "Choose a private key file");
        AutomationProperties.SetName(_passphrase, "Key passphrase");
        AutomationProperties.SetName(_removePassphrase, "Remove the saved passphrase");
        AutomationProperties.SetName(_remoteFolder, "Folder on the server");
        AutomationProperties.SetLiveSetting(_error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

        _auth.SelectionChanged += (_, _) => ShowAuth();
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
        var kind = (existing is null ? kinds.FirstOrDefault() : kinds.FirstOrDefault(k => k.Kind == existing.Kind)) ?? Sftp;
        var form = new SiteDialog(window, kind, existing, save);
        return await form.RunAsync(root);
    }

    private async Task<SiteDto?> RunAsync(FrameworkElement root)
    {
        var xamlRoot = root.XamlRoot!;
        // A fixed width: a long unwrapped key line would otherwise widen the whole dialog (it scrolls in its box instead).
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, MaxWidth = 440 };
        panel.Children.Add(_name);
        // Server and port on one line, as an address is read.
        var address = new Grid { ColumnSpacing = 8 };
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_port, 1);
        address.Children.Add(_host);
        address.Children.Add(_port);
        panel.Children.Add(address);
        if (_existing is not null)
            panel.Children.Add(SettingsWindow.Hint("Changing the server or the port asks about the server's key again."));
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
            Title = _existing is null ? $"New {_kind.Title} site" : $"Edit {_existing.Name}",
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
        dialog.Opened += (_, _) => (_existing is null ? _name : _password.Visibility == Visibility.Visible ? (Control)_password : _privateKey).Focus(FocusState.Programmatic);

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
            _remoteFolder.Text, _localFolder.Text);
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
